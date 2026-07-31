// Session 0 izolasyonunu aşan başlatıcı.
// SYSTEM servisi ön plan penceresi / tarayıcı / boşta süresini GÖREMEZ (bunlar kullanıcı oturumu API'leri).
// Bu sınıf izleme ajanını AKTİF KULLANICI OTURUMUNDA (winsta0\default) başlatır, ölürse/oturum değişirse
// yeniden başlatır. Böylece servis gizli+tamper kalırken app/web/boşta düzgün toplanır.

using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

namespace Argus.Agent;

internal sealed class SessionLauncher
{
    private volatile bool _run = true;
    private Thread? _thread;

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "argus-launcher" };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        try { _thread?.Join(4000); } catch { }
    }

    private static readonly string SignalDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Argus", "signal");
    private static readonly string UninstallFlag = Path.Combine(SignalDir, "uninstall");
    private static readonly string UsbFlag = Path.Combine(SignalDir, "usb");

    private void Loop()
    {
        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule!.FileName;
        uint currentSession = INVALID_SESSION;
        Process? child = null;

        while (_run)
        {
            try
            {
                // Kaldırma sinyali: kullanıcı-ajanı "remove" komutu alınca bırakır. SYSTEM olan servis
                // gerçek kaldırmayı yapar (çocuğu öldür, servisi durdur+sil, dosyaları kaldır) ve çıkar.
                if (File.Exists(UninstallFlag)) { PerformUninstall(child); return; }

                ApplyUsbState();   // panelden gelen USB engelle/izin ver isteğini registry'ye uygula (SYSTEM)

                var session = WTSGetActiveConsoleSessionId();
                bool childAlive = child is { HasExited: false };

                // Kullanıcı değişti / oturum kilitlendi-açıldı → eski çocuğu bırak, yenisini başlat.
                if (childAlive && session != currentSession)
                {
                    try { child!.Kill(true); } catch { }
                    childAlive = false;
                    child = null;
                }

                if (!childAlive && session != INVALID_SESSION && session != 0)
                {
                    if (LaunchInSession(session, exe, out child))
                        currentSession = session;
                }
            }
            catch { /* döngü sağlam kalsın */ }

            // ~3 sn'de bir kontrol → kullanıcı ajanı öldürürse hızlı geri gelir.
            for (int i = 0; i < 30 && _run; i++) Thread.Sleep(100);
        }

        try { if (child is { HasExited: false }) child.Kill(true); } catch { }
    }

    // Gerçek kaldırma — SYSTEM (LocalSystem) yetkisiyle. Servisi durdurup siler ve dosyaları kaldırır.
    // Çalışan servis exe'si ancak servis durunca silinebilir → kopuk bir cmd sırayla yapar.
    private static void PerformUninstall(Process? child)
    {
        try { if (child is { HasExited: false }) child.Kill(true); } catch { }
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Argus");
        var pd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Argus");
        var args = "/c sc stop ArgusAgent >nul 2>&1 & timeout /t 2 >nul & sc delete ArgusAgent >nul 2>&1 & " +
                   "timeout /t 1 >nul & rmdir /s /q \"" + pf + "\" & rmdir /s /q \"" + pd + "\"";
        try
        {
            Process.Start(new ProcessStartInfo("cmd.exe", args)
            { CreateNoWindow = true, UseShellExecute = false, WindowStyle = ProcessWindowStyle.Hidden });
        }
        catch { }
    }

    // --- USB depolama politikası (panelden: allow | readonly | block) ---
    //
    // NEDEN TEK BAŞINA USBSTOR YETMEZ: USBSTOR\Start=4 yalnız sürücünün YÜKLENMESİNİ engeller.
    // Zaten takılı olan bellek çalışmaya devam eder, sürücü o oturumda yüklüyse yeni takılan da
    // çoğu zaman açılır — yani "engelle"yi işaretleyip belleği takınca hâlâ açılıyordu.
    // ÇÖZÜM: asıl uygulayıcı "Removable Storage Access" politikası (RemovableStorageDevices).
    // Bu politika birime HANDLE AÇILIRKEN denetlenir → takılı aygıt da anında erişilemez olur,
    // yeniden başlatma gerekmez. USBSTOR/StorageDevicePolicies destekleyici katman olarak kalır.
    //
    // Sınıf GUID'leri (Grup İlkesi ile aynı):
    private const string RsdPolicy = @"SOFTWARE\Policies\Microsoft\Windows\RemovableStorageDevices";
    private const string ClsRemovableDisk = "{53f5630d-b6bf-11d0-94f2-00a0c91efb8b}";   // USB bellek / harici disk
    private const string ClsCdRom = "{53f56308-b6bf-11d0-94f2-00a0c91efb8b}";           // CD/DVD (yazma = sızıntı)
    private const string ClsWpd1 = "{6AC27878-A6FA-4155-BA85-F98F491D4F33}";            // WPD — telefon/MTP
    private const string ClsWpd2 = "{F33FDC04-D1AC-4E8E-9A30-19BBD4B108AE}";            // WPD — diğer taşınabilir
    private const string ClsFloppy = "{53f56311-b6bf-11d0-94f2-00a0c91efb8b}";
    private const string ClsTape = "{53f5630b-b6bf-11d0-94f2-00a0c91efb8b}";

    // Uygulanan son durum (tekrar tekrar yazmamak için). Enforcement: her döngüde kontrol edilir,
    // biri elle geri açarsa yeniden uygulanır.
    private static string _usbApplied = "";

    private static void ApplyUsbState()
    {
        try
        {
            if (!File.Exists(UsbFlag)) return;
            var want = File.ReadAllText(UsbFlag).Trim().ToLowerInvariant();
            if (want is not ("block" or "readonly" or "allow"))
                want = want == "block" ? "block" : "allow";   // eski sürüm "block"/"allow" yazıyordu

            bool block = want == "block";
            bool noWrite = want is "block" or "readonly";

            // 1) ASIL KATMAN — Removable Storage Access. Takılı aygıtta da geçerli, reboot istemez.
            using (var root = Registry.LocalMachine.CreateSubKey(RsdPolicy, writable: true))
            {
                if (root is not null)
                {
                    // "Tüm çıkarılabilir depolama sınıfları: tüm erişimi reddet"
                    SetDword(root, "Deny_All", block ? 1 : 0);

                    SetClass(root, ClsRemovableDisk, denyRead: block, denyWrite: noWrite);
                    SetClass(root, ClsWpd1, denyRead: block, denyWrite: noWrite);
                    SetClass(root, ClsWpd2, denyRead: block, denyWrite: noWrite);
                    SetClass(root, ClsFloppy, denyRead: block, denyWrite: noWrite);
                    SetClass(root, ClsTape, denyRead: block, denyWrite: noWrite);
                    // CD/DVD: okumayı hiç kesmiyoruz (kurulum medyası), yalnız YAZMAYI kapatıyoruz.
                    SetClass(root, ClsCdRom, denyRead: false, denyWrite: noWrite);
                }
            }

            // 2) Destek katmanı — salt-okunur için klasik genel yazma koruması.
            using (var sdp = Registry.LocalMachine.CreateSubKey(@"SYSTEM\CurrentControlSet\Control\StorageDevicePolicies", writable: true))
                if (sdp is not null) SetDword(sdp, "WriteProtect", noWrite && !block ? 1 : 0);

            // 3) Destek katmanı — sürücü hiç yüklenmesin (yalnız tam engelde; yeni takılanlar için).
            foreach (var svc in new[] { "USBSTOR", "UASPStor" })
            {
                using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{svc}", writable: true);
                if (key is null) continue;   // UASPStor her sistemde olmayabilir
                var desired = block ? 4 : 3;
                if ((key.GetValue("Start") as int? ?? 3) != desired) key.SetValue("Start", desired, RegistryValueKind.DWord);
            }

            // Uygulanan gerçek durumu bırak: kullanıcı-ajanı okuyup sunucuya bildirir → panelde
            // "politika şu makinede uygulandı mı" görülür (yoksa engelin tuttuğunu doğrulamak imkânsızdı).
            if (_usbApplied != want)
            {
                _usbApplied = want;
                try { File.WriteAllText(Path.Combine(SignalDir, "usb-applied"), want); } catch { }
            }
        }
        catch { /* registry erişilemedi */ }
    }

    private static void SetDword(RegistryKey k, string name, int value)
    {
        if ((k.GetValue(name) as int?) != value) k.SetValue(name, value, RegistryValueKind.DWord);
    }

    private static void SetClass(RegistryKey root, string classGuid, bool denyRead, bool denyWrite)
    {
        using var k = root.CreateSubKey(classGuid, writable: true);
        if (k is null) return;
        SetDword(k, "Deny_Read", denyRead ? 1 : 0);
        SetDword(k, "Deny_Write", denyWrite ? 1 : 0);
    }

    private static bool LaunchInSession(uint sessionId, string exePath, out Process? child)
    {
        child = null;
        IntPtr userToken = IntPtr.Zero, dupToken = IntPtr.Zero, env = IntPtr.Zero;
        try
        {
            if (!WTSQueryUserToken(sessionId, out userToken)) return false;   // oturumda oturum-açmış kullanıcı yok

            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>() };
            if (!DuplicateTokenEx(userToken, MAXIMUM_ALLOWED, ref sa, SecurityImpersonation, TokenPrimary, out dupToken))
                return false;

            CreateEnvironmentBlock(out env, dupToken, false);

            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = @"winsta0\default" };
            uint flags = CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW;
            var cmd = new StringBuilder("\"" + exePath + "\" --useragent");

            if (!CreateProcessAsUser(dupToken, exePath, cmd, ref sa, ref sa, false, flags, env, null, ref si, out var pi))
                return false;

            CloseHandle(pi.hThread);
            try { child = Process.GetProcessById(pi.dwProcessId); } catch { child = null; }
            CloseHandle(pi.hProcess);
            return true;
        }
        catch { return false; }
        finally
        {
            if (env != IntPtr.Zero) DestroyEnvironmentBlock(env);
            if (dupToken != IntPtr.Zero) CloseHandle(dupToken);
            if (userToken != IntPtr.Zero) CloseHandle(userToken);
        }
    }

    // ---- P/Invoke ----
    private const uint INVALID_SESSION = 0xFFFFFFFF;
    private const uint MAXIMUM_ALLOWED = 0x02000000;
    private const int SecurityImpersonation = 2;
    private const int TokenPrimary = 1;
    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(IntPtr hToken, uint access, ref SECURITY_ATTRIBUTES attrs, int impLevel, int tokenType, out IntPtr newToken);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr env, IntPtr token, bool inherit);
    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr env);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(IntPtr hToken, string? appName, StringBuilder cmdLine,
        ref SECURITY_ATTRIBUTES procAttrs, ref SECURITY_ATTRIBUTES threadAttrs, bool inherit, uint flags,
        IntPtr env, string? curDir, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public bool bInheritHandle; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }
}
