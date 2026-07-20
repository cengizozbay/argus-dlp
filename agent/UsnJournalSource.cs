// NTFS USN Change Journal tabanlı dosya olayı kaynağı (üretim güvenilirliği).
//
// FileSystemWatcher yoğun anlarda iç tampon taşınca olay düşürür (DLP için ölümcül).
// USN Journal ise NTFS'in kalıcı değişiklik günlüğüdür: hiçbir olay düşmez, agent kapalıyken
// olan değişiklikler bile açılışta okunur. Bu kaynak IFileEventSource arayüzünü uygular →
// AlertEngine/Program hiç değişmeden aynı FileEvent akışını alır.
//
// GEREKSİNİM: yönetici (admin) hakları — birim tanıtıcısını (\\.\C:) GENERIC_READ ile açmak için.
//   Admin değilse ya da birim NTFS değilse Start() hata fırlatır; çağıran FileSystemWatcher'a düşer.
// KAPSAM: watch kökleri hangi sürücü harflerindeyse yalnız o NTFS birimleri okunur; olaylar
//   köklerin altına düşenlerle sınırlanır (FileSystemWatcher davranışıyla aynı).
// NOT: ETW (Kernel-File) gerçek zamanlı + süreç ilişkilendirme sağlar ama sıfır-bağımlılıkla
//   gerçek zamanlı ETW çözümlemesi ayrı ve büyük bir iştir; USN Journal güvenilirlik kazancının
//   çekirdeğini tek başına verir. ETW bir sonraki adım.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Argus.Agent;

public sealed class UsnJournalSource : IFileEventSource
{
    private readonly string[] _roots;                 // izlenecek klasörler (mutlak yol)
    private readonly List<string> _volumes = new();   // ör. "C:"
    private readonly List<Thread> _threads = new();
    private readonly List<SafeFileHandle> _handles = new();
    private readonly ConcurrentDictionary<ulong, string?> _dirPathCache = new();     // parent FRN -> klasör yolu
    private readonly ConcurrentDictionary<string, string> _nameIndex = new(StringComparer.OrdinalIgnoreCase); // ad -> ilk yol
    private volatile bool _running;

    public event Action<FileEvent>? OnEvent;

    public UsnJournalSource(IEnumerable<string> roots)
    {
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                      .Select(Path.GetFullPath)
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray();

        // Köklerin bulunduğu farklı sürücü harflerini çıkar (ör. C:, D:).
        foreach (var root in _roots)
        {
            var vol = Path.GetPathRoot(root)?.TrimEnd('\\');   // "C:\" -> "C:"
            if (!string.IsNullOrEmpty(vol) && vol.Length == 2 && vol[1] == ':' &&
                !_volumes.Contains(vol, StringComparer.OrdinalIgnoreCase))
                _volumes.Add(vol);
        }
    }

    public void Start()
    {
        _running = true;
        var opened = 0;
        foreach (var vol in _volumes)
        {
            SafeFileHandle h;
            USN_JOURNAL_DATA_V0 jd;
            try
            {
                h = OpenVolume(vol);
                if (h.IsInvalid) { h.Dispose(); continue; }
                if (!QueryJournal(h, out jd)) { h.Dispose(); continue; }
            }
            catch { continue; }

            _handles.Add(h);
            var t = new Thread(() => ReadLoop(vol, h, jd.UsnJournalID, jd.NextUsn))
                { IsBackground = true, Name = $"argus-usn-{vol}" };
            _threads.Add(t);
            t.Start();
            opened++;
        }

        if (opened == 0)
        {
            _running = false;
            throw new InvalidOperationException(
                "USN Journal açılamadı (yönetici hakkı ve NTFS birim gerekir).");
        }
    }

    // --- Birim başına okuma döngüsü ---
    private void ReadLoop(string vol, SafeFileHandle h, ulong journalId, long startUsn)
    {
        var outBuf = Marshal.AllocHGlobal(OutBufferSize);
        var inBuf = Marshal.AllocHGlobal(Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>());
        var managed = new byte[OutBufferSize];
        try
        {
            var next = startUsn;
            while (_running)
            {
                var req = new READ_USN_JOURNAL_DATA_V0
                {
                    StartUsn = next,
                    ReasonMask = 0xFFFFFFFF,
                    ReturnOnlyOnClose = 0,
                    Timeout = 0,
                    BytesToWaitFor = 0,
                    UsnJournalID = journalId
                };
                Marshal.StructureToPtr(req, inBuf, false);

                if (!DeviceIoControl(h, FSCTL_READ_USN_JOURNAL, inBuf, Marshal.SizeOf<READ_USN_JOURNAL_DATA_V0>(),
                        outBuf, OutBufferSize, out var returned, IntPtr.Zero))
                {
                    Thread.Sleep(1000);   // birim geçici erişilemez — tekrar dene
                    continue;
                }

                if (returned <= sizeof(long)) { Thread.Sleep(700); continue; }   // yeni kayıt yok, biraz bekle

                Marshal.Copy(outBuf, managed, 0, returned);
                next = BitConverter.ToInt64(managed, 0);                         // sonraki tur için USN imleci
                ParseRecords(managed, sizeof(long), returned, h);
            }
        }
        catch { /* birim çıkarıldı / handle kapandı — döngü biter */ }
        finally
        {
            Marshal.FreeHGlobal(outBuf);
            Marshal.FreeHGlobal(inBuf);
        }
    }

    // Tamponu USN_RECORD_V2 kayıtlarına ayrıştır. Reason "kapanışa kadar birikir"; bu yüzden yalnız
    // CLOSE bayrağı olan kayıtları işleyip biriken nedenden tek mantıksal olay üretiyoruz (gürültüsüz).
    private void ParseRecords(byte[] buf, int offset, int end, SafeFileHandle vol)
    {
        while (offset + 60 <= end)
        {
            var recLen = (int)BitConverter.ToUInt32(buf, offset);
            if (recLen <= 0 || offset + recLen > end) break;

            var major = BitConverter.ToUInt16(buf, offset + 4);
            if (major == 2)
            {
                var parentFrn = BitConverter.ToUInt64(buf, offset + 16);
                var reason = BitConverter.ToUInt32(buf, offset + 40);
                var attrs = BitConverter.ToUInt32(buf, offset + 52);
                var nameLen = BitConverter.ToUInt16(buf, offset + 56);
                var nameOff = BitConverter.ToUInt16(buf, offset + 58);

                if ((reason & USN_REASON_CLOSE) != 0 && nameLen > 0 && offset + nameOff + nameLen <= end)
                {
                    var name = Encoding.Unicode.GetString(buf, offset + nameOff, nameLen);
                    HandleRecord(vol, parentFrn, reason, attrs, name);
                }
            }
            offset += recLen;
        }
    }

    private void HandleRecord(SafeFileHandle vol, ulong parentFrn, uint reason, uint attrs, string name)
    {
        var op = OpFor(reason);
        if (op is null) return;

        var dir = ResolveDir(vol, parentFrn);
        if (dir is null) return;                       // yol çözülemedi → atla (gürültü yerine kayıp)
        var full = Path.Combine(dir, name);

        if (!UnderRoot(full)) return;                  // watch köklerinin dışı → yok say

        // Kopyalama sezgiseli (FileSystemWatcher ile aynı): aynı ad başka mevcut yolda görülmüşse KOPYA.
        string? source = null;
        if (op == "create")
        {
            if (_nameIndex.TryGetValue(name, out var other) &&
                !string.Equals(other, full, StringComparison.OrdinalIgnoreCase) && File.Exists(other))
            { op = "copy"; source = other; }   // kaynak = aynı adlı mevcut dosya (nereden)
            else
                _nameIndex[name] = full;
        }

        OnEvent?.Invoke(new FileEvent(
            DateTime.Now.ToString("o"), op, full, source, Environment.MachineName, Environment.UserName));
    }

    private static string? OpFor(uint reason)
    {
        if ((reason & USN_REASON_FILE_DELETE) != 0) return "delete";
        if ((reason & USN_REASON_RENAME_NEW_NAME) != 0) return "rename";
        if ((reason & USN_REASON_FILE_CREATE) != 0) return "create";
        if ((reason & (USN_REASON_DATA_OVERWRITE | USN_REASON_DATA_EXTEND | USN_REASON_DATA_TRUNCATION)) != 0) return "modify";
        return null;
    }

    // parent FRN -> klasör yolu. OpenFileById ile çözer, önbelleğe alır (yeniden sorgu maliyetli).
    private string? ResolveDir(SafeFileHandle vol, ulong parentFrn)
    {
        if (_dirPathCache.TryGetValue(parentFrn, out var cached)) return cached;

        string? path = null;
        try
        {
            var desc = new FILE_ID_DESCRIPTOR
            {
                dwSize = (uint)Marshal.SizeOf<FILE_ID_DESCRIPTOR>(),
                Type = 0,                       // FileIdType
                FileId = unchecked((long)parentFrn)
            };
            using var fh = OpenFileById(vol, ref desc, FILE_READ_ATTRIBUTES,
                FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, FILE_FLAG_BACKUP_SEMANTICS);
            if (!fh.IsInvalid)
            {
                var sb = new StringBuilder(1024);
                var len = GetFinalPathNameByHandle(fh, sb, sb.Capacity, 0);
                if (len > 0)
                {
                    var p = sb.ToString();
                    if (p.StartsWith(@"\\?\")) p = p.Substring(4);   // "\\?\C:\..." -> "C:\..."
                    path = p;
                }
            }
        }
        catch { path = null; }

        _dirPathCache[parentFrn] = path;                 // null da önbelleğe alınır (tekrar denemeyi önler)
        if (_dirPathCache.Count > 20000) _dirPathCache.Clear();  // sınırsız büyümeyi engelle
        return path;
    }

    private bool UnderRoot(string full)
    {
        foreach (var root in _roots)
            if (full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    public string Describe()
        => _volumes.Count == 0
            ? "(USN: birim yok)"
            : $"USN Journal · {string.Join(", ", _volumes)} · kökler: {string.Join("  |  ", _roots)}";

    public void Dispose()
    {
        _running = false;
        foreach (var h in _handles) { try { h.Dispose(); } catch { } }
        _handles.Clear();
    }

    // --- Win32 / NTFS USN Journal birlikte-çalışma ---
    private const int OutBufferSize = 64 * 1024;

    private const uint GENERIC_READ = 0x80000000;
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, FILE_SHARE_DELETE = 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_READ_ATTRIBUTES = 0x80;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900f4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900bb;

    private const uint USN_REASON_DATA_OVERWRITE = 0x00000001;
    private const uint USN_REASON_DATA_EXTEND = 0x00000002;
    private const uint USN_REASON_DATA_TRUNCATION = 0x00000004;
    private const uint USN_REASON_FILE_CREATE = 0x00000100;
    private const uint USN_REASON_FILE_DELETE = 0x00000200;
    private const uint USN_REASON_RENAME_NEW_NAME = 0x00002000;
    private const uint USN_REASON_CLOSE = 0x80000000;

    private static SafeFileHandle OpenVolume(string vol)
        => CreateFileW($@"\\.\{vol}", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);

    private static bool QueryJournal(SafeFileHandle h, out USN_JOURNAL_DATA_V0 data)
    {
        var size = Marshal.SizeOf<USN_JOURNAL_DATA_V0>();
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(h, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, buf, size, out _, IntPtr.Zero))
            {
                data = default;
                return false;
            }
            data = Marshal.PtrToStructure<USN_JOURNAL_DATA_V0>(buf);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct USN_JOURNAL_DATA_V0
    {
        public ulong UsnJournalID;
        public long FirstUsn;
        public long NextUsn;
        public long LowestValidUsn;
        public long MaxUsn;
        public ulong MaximumSize;
        public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct READ_USN_JOURNAL_DATA_V0
    {
        public long StartUsn;
        public uint ReasonMask;
        public uint ReturnOnlyOnClose;
        public ulong Timeout;
        public ulong BytesToWaitFor;
        public ulong UsnJournalID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FILE_ID_DESCRIPTOR
    {
        public uint dwSize;
        public int Type;
        public long FileId;    // LARGE_INTEGER birleşim üyesi
        public long Reserved;  // FILE_ID_128 (16 bayt) birleşimini kaplamak için dolgu
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize,
        out int lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle OpenFileById(SafeFileHandle hVolumeHint, ref FILE_ID_DESCRIPTOR lpFileId,
        uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwFlagsAndAttributes);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder lpszFilePath,
        int cchFilePath, int dwFlags);
}
