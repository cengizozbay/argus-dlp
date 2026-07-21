// Ön plandaki belge uygulamasının AÇIK TUTTUĞU dosyanın TAM YOLUNU bulur (konum için).
// Office (Word/Excel/PPT), Adobe/Foxit PDF, editörler dosyayı açık tutar → yolu alınır.
// Tarayıcıda açılan (Chrome/Edge PDF) dosyayı öyle tutmaz → yol gelmez (null).
//
// Yöntem: sürecin açık handle'larını NtQuerySystemInformation ile listeler, hedef sürecin dosya
// handle'larını kendi sürecimize DuplicateHandle eder, GetFinalPathNameByHandle ile yolu okur.
// (NtQueryObject bazı handle'larda asılabildiği için ONU KULLANMIYORUZ; GetFinalPathNameByHandle güvenli.)

using System.Runtime.InteropServices;
using System.Text;

namespace Argus.Agent;

internal static class DocPathFinder
{
    public static string? Find(int pid, string docName)
    {
        if (pid <= 0 || string.IsNullOrWhiteSpace(docName)) return null;
        IntPtr proc = IntPtr.Zero;
        try
        {
            proc = OpenProcess(PROCESS_DUP_HANDLE, false, pid);
            if (proc == IntPtr.Zero) return null;
            var self = GetCurrentProcess();

            foreach (var h in EnumHandlesFor(pid))
            {
                if (!DuplicateHandle(proc, h, self, out var dup, 0, false, DUPLICATE_SAME_ACCESS)) continue;
                try
                {
                    if (GetFileType(dup) != FILE_TYPE_DISK) continue;   // sadece disk dosyaları
                    var sb = new StringBuilder(1024);
                    if (GetFinalPathNameByHandle(dup, sb, sb.Capacity, 0) <= 0) continue;
                    var p = Normalize(sb.ToString());
                    if (!string.IsNullOrEmpty(p) &&
                        string.Equals(Path.GetFileName(p), docName, StringComparison.OrdinalIgnoreCase))
                        return p;
                }
                finally { CloseHandle(dup); }
            }
        }
        catch { }
        finally { if (proc != IntPtr.Zero) CloseHandle(proc); }
        return null;
    }

    private static string Normalize(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + p.Substring(8);
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal)) return p.Substring(4);
        return p;
    }

    // Verilen PID'e ait handle değerlerini döndürür (sistem geneli tablodan süzülür).
    private static List<IntPtr> EnumHandlesFor(int pid)
    {
        var result = new List<IntPtr>();
        int len = 0x100000;
        IntPtr buf = Marshal.AllocHGlobal(len);
        try
        {
            int status, tries = 0;
            while ((status = NtQuerySystemInformation(SystemExtendedHandleInformation, buf, len, out int need)) == STATUS_INFO_LENGTH_MISMATCH)
            {
                Marshal.FreeHGlobal(buf);
                len = Math.Max(need, len * 2);
                buf = Marshal.AllocHGlobal(len);
                if (++tries > 6) return result;
            }
            if (status != 0) return result;

            // SYSTEM_HANDLE_INFORMATION_EX: [IntPtr Count][IntPtr Reserved][entries...]
            long count = Marshal.ReadIntPtr(buf).ToInt64();
            int header = IntPtr.Size * 2;
            int entry = IntPtr.Size == 8 ? 40 : 28;   // 64-bit entry = 40 byte
            long baseAddr = buf.ToInt64() + header;
            for (long i = 0; i < count; i++)
            {
                long e = baseAddr + i * entry;
                int owner = (int)Marshal.ReadIntPtr((IntPtr)(e + IntPtr.Size)).ToInt64();   // UniqueProcessId
                if (owner != pid) continue;
                result.Add(Marshal.ReadIntPtr((IntPtr)(e + IntPtr.Size * 2)));              // HandleValue
            }
        }
        catch { }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    private const int SystemExtendedHandleInformation = 0x40;
    private const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    private const uint PROCESS_DUP_HANDLE = 0x0040;
    private const uint DUPLICATE_SAME_ACCESS = 0x2;
    private const uint FILE_TYPE_DISK = 0x0001;

    [DllImport("ntdll.dll")] private static extern int NtQuerySystemInformation(int cls, IntPtr buf, int len, out int ret);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool DuplicateHandle(IntPtr src, IntPtr h, IntPtr dst, out IntPtr dup, uint access, bool inherit, uint opts);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr h);
    [DllImport("kernel32.dll")] private static extern uint GetFileType(IntPtr h);
    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern int GetFinalPathNameByHandle(IntPtr h, StringBuilder path, int len, int flags);
}
