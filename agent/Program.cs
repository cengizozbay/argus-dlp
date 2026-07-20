// Argus Agent — Faz 0/1/2 + sunucu bağlantısı
// Önplan uygulaması + aktif/boşta + dosya olayları + toplu-silme uyarısı,
// ve enroll + periyodik telemetri gönderimi (offline dayanıklı kuyruk).

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Argus.Agent;

internal static class Program
{
    private const int SampleSeconds = 2;
    private static int _idleThreshold = 60;         // sunucu politikasıyla güncellenebilir
    private const int FlushSeconds = 10;
    private const int MaxPending = 5000;

    private static readonly Dictionary<string, AppStat> Stats = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> FriendlyCache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, WebStat> WebStats = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, DocStat> DocStats = new(StringComparer.OrdinalIgnoreCase);
    private static readonly BrowserMonitor Browser = new();
    private static ExtensionWebSource? _extWeb;
    private static readonly object IoLock = new();

    private static readonly ConcurrentQueue<OutEvent> PendingEvents = new();
    private static readonly ConcurrentQueue<Alert> PendingAlerts = new();

    private static long _activeSeconds, _idleSeconds;
    private static long _fileCreates, _fileModifies, _fileDeletes, _fileRenames, _fileCopies, _alertCount;
    private static long _usbCopies, _usbInserts, _sensitiveFiles;
    private static long _sentBatches;

    private static string _lastExe = "";
    private static bool _wasIdle;
    private static string _dataDir = "", _eventsPath = "", _summaryPath = "", _alertsPath = "";
    private static string _watchDesc = "";
    private static DateTime _sessionStart;
    private static volatile bool _running = true;
    private static volatile bool _dormant;    // sunucudan "disabled" gelince toplama durur, yoklama sürer

    private static AgentConfig _cfg = new();
    private static AlertEngine _engine = null!;
    private static readonly ContentClassifier _classifier = new();
    private static IFileEventSource? _fileSource;
    private static IFileEventSource? _netFileSource;   // ağ paylaşımları (UNC \\sunucu\...) için ek FSW
    private static UsbMonitor? _usbSource;
    private static ServerClient? _server;
    private static string _serverStatus = "yerel mod";

    private static void Main(string[] args)
    {
        // "--service" (SCM): servis GÖZCÜ olarak çalışır → izleme ajanını kullanıcı oturumunda başlatır.
        // "--useragent": gözcünün kullanıcı oturumunda başlattığı izleme süreci (Session 0 sorununu aşar).
        // Argümansız/konsol/Zamanlanmış Görev → doğrudan izleme döngüsü.
        if (OperatingSystem.IsWindows() && args.Contains("--service"))
        {
            System.ServiceProcess.ServiceBase.Run(new ArgusService());
            return;
        }
        // Bayrakları ayıkla (kalan konumsal argümanlar = izlenecek klasörler).
        RunAgent(args.Where(a => !a.StartsWith("--")).ToArray());
    }

    internal static void RunAgent(string[] args)
    {
        _sessionStart = DateTime.Now;
        _dataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Argus");
        Directory.CreateDirectory(_dataDir);
        _eventsPath = Path.Combine(_dataDir, "events.jsonl");
        _summaryPath = Path.Combine(_dataDir, "summary.json");
        _alertsPath = Path.Combine(_dataDir, "alerts.jsonl");
        _extWeb = new ExtensionWebSource(_dataDir);

        _cfg = AgentConfig.Load(_dataDir);
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; _running = false; };

        var roots = args.Length > 0
            ? args.ToList()
            : _cfg.WatchFolders is { Count: > 0 }
                ? _cfg.WatchFolders
                : new List<string>
                {
                    Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                };

        _engine = new AlertEngine();
        _engine.OnAlert += OnAlert;
        _classifier.Configure(_cfg.ContentScan, _cfg.SensitiveKeywords);   // sunucu politikası varsa ApplySettings ezer

        // Sunucu bağlıysa ÖNCE enroll et → tenant politikasını al. Dosya motoru (USN/FSW) ve
        // eşikler bu politikayla belirlenir. Sunucuya ulaşılamazsa yerel varsayılanlarla devam.
        if (_cfg.ServerEnabled)
        {
            _server = new ServerClient(_cfg.ServerUrl, _cfg.TenantKey, _cfg.AllowInsecureTls);
            _serverStatus = "bağlanıyor…";
            try { _server.Enroll(Environment.MachineName, Environment.UserName, "domain-lan"); } catch { }
        }

        var settings = _server?.LatestSettings;
        if (settings is not null) ApplySettings(settings);            // eşikler + gönderim aralığı + idle
        var mode = settings?.FileMonitor ?? _cfg.FileMonitor;
        var effectiveRoots = settings?.WatchFolders is { Count: > 0 } ? settings.WatchFolders : roots;
        var usbOn = settings?.UsbMonitoring ?? true;

        // Dosya olayı kaynağı: USN Journal (güvenilir, admin ister) ya da FileSystemWatcher (yedek).
        _fileSource = SelectFileSource(mode, effectiveRoots.ToList(), out var chosenMode);
        _watchDesc = $"[{chosenMode}] " + _fileSource.Describe();

        // Ağ paylaşımları (UNC \\sunucu\paylaşım) USN ile GÖRÜLEMEZ (uzaktaki disk). USN modundaysak
        // bu yollara ayrıca FileSystemWatcher koy → paylaşımda çalışılan dosya olayları da düşer.
        if (chosenMode != "fsw")
        {
            var netRoots = effectiveRoots.Where(r => r.StartsWith(@"\\")).ToList();
            if (netRoots.Count > 0)
            {
                try
                {
                    var netFsw = new FileSystemWatcherSource(netRoots);
                    netFsw.OnEvent += OnFileEvent;
                    netFsw.Start();
                    _netFileSource = netFsw;
                    _watchDesc += " + ağ: " + string.Join(", ", netRoots);
                }
                catch (Exception ex) { Console.WriteLine($"  Ağ paylaşımı izlenemedi: {ex.Message}"); }
            }
        }

        // USB / harici disk izleme — takılma/çıkarılma + medyaya yazılan dosyalar (usb_copy).
        if (usbOn)
        {
            var usb = new UsbMonitor();
            usb.OnEvent += OnFileEvent;
            usb.Start();
            _usbSource = usb;
        }

        LogEvent(new AgentEvent("session_start", null, null, Environment.MachineName, Environment.UserName));
        Console.WriteLine($"Argus agent başladı. İzlenen: {_watchDesc}");
        Console.WriteLine(_cfg.ServerEnabled ? $"Sunucu: {_cfg.ServerUrl}" : "Sunucu: (yerel mod — config.json ile bağlanır)");

        var lastFlush = DateTime.Now;
        var lastSend = DateTime.MinValue;
        var lastRender = DateTime.MinValue;

        while (_running)
        {
            Sample();

            if ((DateTime.Now - lastFlush).TotalSeconds >= FlushSeconds) { Flush(); lastFlush = DateTime.Now; }

            if (_server is not null && (DateTime.Now - lastSend).TotalSeconds >= _cfg.SendSeconds)
            {
                TrySend();
                ApplyLiveSettings();   // panelde değişen politikayı yeniden başlatmadan uygula
                ApplyCommand();        // uzaktan kill switch (durdur/kaldır)
                lastSend = DateTime.Now;
            }

            if (!Console.IsOutputRedirected && (DateTime.Now - lastRender).TotalSeconds >= 1)
            {
                Render();
                lastRender = DateTime.Now;
            }

            Thread.Sleep(SampleSeconds * 1000);
        }

        LogEvent(new AgentEvent("session_end", null, null, Environment.MachineName, Environment.UserName));
        Flush();
        if (_server is not null) TrySend();
        _fileSource?.Dispose();
        _netFileSource?.Dispose();
        _usbSource?.Dispose();
        Console.WriteLine($"\nDurduruldu. Veriler: {_dataDir}");
    }

    // --- Politika (tenant ayarları) uygulaması ---
    private static string _appliedSettingsStamp = "";

    private static void ApplySettings(AgentSettings s)
    {
        _engine.Configure(s.DeleteThreshold, s.CopyThreshold, s.UsbThreshold, s.SensitiveThreshold, s.WindowSeconds, s.CooldownSeconds);
        _classifier.Configure(s.ContentScan, s.SensitiveKeywords);
        if (s.SendSeconds >= 5) _cfg.SendSeconds = s.SendSeconds;
        if (s.IdleThresholdSeconds >= 5) _idleThreshold = s.IdleThresholdSeconds;
        _appliedSettingsStamp = s.UpdatedAt;
    }

    // Panelde ayar değişince (yeni UpdatedAt) canlı uygula. Eşikler/aralık/idle ve USB aç-kapa
    // anında geçerli; dosya motoru (usn/fsw) ve izlenen klasör değişikliği agent yeniden başlatılınca.
    private static void ApplyLiveSettings()
    {
        var s = _server?.LatestSettings;
        if (s is null || s.UpdatedAt == _appliedSettingsStamp) return;
        ApplySettings(s);

        if (!s.UsbMonitoring && _usbSource is not null) { _usbSource.Dispose(); _usbSource = null; }
        else if (s.UsbMonitoring && _usbSource is null)
        {
            var usb = new UsbMonitor(); usb.OnEvent += OnFileEvent; usb.Start(); _usbSource = usb;
        }
        Console.WriteLine("  Politika sunucudan güncellendi.");
    }

    // Dosya olayı kaynağını moda göre seç ve başlat. "auto"/"usn" USN Journal dener (admin+NTFS),
    // olmazsa FileSystemWatcher'a düşer. "fsw" doğrudan FileSystemWatcher.
    private static IFileEventSource SelectFileSource(string mode, List<string> roots, out string chosen)
    {
        mode = (mode ?? "auto").ToLowerInvariant();
        if (mode is "usn" or "auto")
        {
            try
            {
                var usn = new UsnJournalSource(roots);
                usn.OnEvent += OnFileEvent;
                usn.Start();                 // admin değilse / NTFS değilse fırlatır
                chosen = "usn";
                return usn;
            }
            catch (Exception ex)
            {
                if (mode == "usn")
                    Console.WriteLine($"  USN Journal açılamadı ({ex.Message}) — FileSystemWatcher'a düşülüyor.");
            }
        }
        var fsw = new FileSystemWatcherSource(roots);
        fsw.OnEvent += OnFileEvent;
        fsw.Start();
        chosen = "fsw";
        return fsw;
    }

    // --- Uzaktan komut (panel kill switch) ---
    // active: normal · disabled: toplamayı durdur ama yoklamaya devam (yeniden açılabilir) · remove: kendini kaldır.
    private static void ApplyCommand()
    {
        var cmd = _server?.LatestCommand ?? "active";
        switch (cmd)
        {
            case "remove":
                Console.WriteLine("  Sunucudan KALDIRMA komutu alındı — kaldırma başlatılıyor.");
                LogEvent(new AgentEvent("uninstall", null, null, Environment.MachineName, Environment.UserName));
                Flush();
                TrySend();               // son telemetriyi gönder (uninstall olayı dahil)
                WriteUninstallSignal();  // hibrit: SYSTEM servisi bu sinyali görüp servisi+dosyaları admin yetkisiyle siler
                SelfUninstall();         // görev (task) modu için eski temizlik — servis modunda zararsız
                _running = false;        // döngüden çık → Main temizliği çalışır
                break;
            case "disabled":
                if (!_dormant) { _dormant = true; _serverStatus = "PANELDEN DURDURULDU (uyuyor)"; Console.WriteLine("  Panelden durduruldu — toplama duraklatıldı."); }
                break;
            default:
                if (_dormant) { _dormant = false; Console.WriteLine("  Panelden yeniden etkinleştirildi — toplama sürüyor."); }
                break;
        }
    }

    // Kendini kaldır: gizli Zamanlanmış Görevi sil, sonra kurulum klasörünü (bin + veri) ayrı bir
    // süreçle (bu exe çıktıktan sonra) sil. Admin yoksa görev silme sessizce başarısız olabilir.
    private static void SelfUninstall()
    {
        try { RunHidden("schtasks", "/delete /tn ArgusAgent /f"); } catch { }
        try
        {
            var home = _dataDir;   // %LOCALAPPDATA%\Argus (bin + config + veriler burada)
            // Süreç çıkınca 3 sn bekleyip tüm klasörü sil (çalışan exe ancak çıkınca silinebilir).
            var psi = new ProcessStartInfo("cmd.exe", $"/c timeout /t 3 >nul & rmdir /s /q \"{home}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Windows)
            };
            Process.Start(psi);
        }
        catch { /* silme başarısızsa görev zaten kaldırıldı → bir daha başlamaz */ }
    }

    private static void RunHidden(string file, string args)
    {
        var psi = new ProcessStartInfo(file, args) { CreateNoWindow = true, UseShellExecute = false };
        Process.Start(psi)?.WaitForExit(5000);
    }

    // Hibrit kaldırma: kullanıcı-oturumu ajanı yetkisiz (servisi silemez). ProgramData\Argus\signal
    // klasörüne bir "uninstall" işareti bırakır; SYSTEM servisi (SessionLauncher) bunu görüp gerçek
    // kaldırmayı (servis + Program Files + ProgramData) admin yetkisiyle yapar. Klasör kuruluşta
    // Users'a yazılabilir verilir (yalnız signal alt klasörü; config admin-only kalır).
    private static void WriteUninstallSignal()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Argus", "signal");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "uninstall"), DateTime.Now.ToString("o"));
        }
        catch { /* servis yoksa (görev modu) SelfUninstall zaten devrede */ }
    }

    // --- Sunucuya gönderim (enroll + telemetri, offline dayanıklı) ---
    private static void TrySend()
    {
        if (_server is null) return;
        if (!_server.Enrolled)
        {
            if (!_server.Enroll(Environment.MachineName, Environment.UserName, "domain-lan"))
            {
                _serverStatus = "sunucuya ulaşılamıyor (kuyruğa alınıyor)";
                return;
            }
        }

        var alerts = Drain(PendingAlerts, 500);
        var events = Drain(PendingEvents, 2000);
        var usage = BuildWebUsage();
        var appUsage = BuildAppUsage();
        var docUsage = BuildDocUsage();

        if (_server.SendTelemetry(BuildSummary(), alerts, events, usage, appUsage, docUsage))
        {
            Interlocked.Increment(ref _sentBatches);
            _serverStatus = $"bağlı · {Interlocked.Read(ref _sentBatches)} paket gönderildi";
        }
        else
        {
            foreach (var a in alerts) Enqueue(PendingAlerts, a);   // başarısız → geri kuyruğa
            foreach (var e in events) Enqueue(PendingEvents, e);
            foreach (var u in usage) if (WebStats.TryGetValue(u.Site, out var ws)) ws.IntervalSeconds += u.Seconds;
            foreach (var u in appUsage) if (Stats.TryGetValue(u.Exe, out var st)) st.IntervalSeconds += u.Seconds;
            foreach (var u in docUsage) if (DocStats.TryGetValue(u.Name, out var ds)) ds.IntervalSeconds += u.Seconds;
            _serverStatus = "gönderim başarısız (yeniden denenecek)";
        }
    }

    // Son gönderimden bu yana site başına biriken süreyi topla (zaman serisi), aralığı sıfırla.
    private static List<WebUsageOut> BuildWebUsage()
    {
        var list = new List<WebUsageOut>();
        var nowIso = DateTime.Now.ToString("o");   // yerel saat (dosya olaylarıyla tutarlı, tarih filtresi doğru)
        foreach (var kv in WebStats)
        {
            var ws = kv.Value;
            if (ws.IntervalSeconds <= 0) continue;
            list.Add(new WebUsageOut(kv.Key, ws.Domain, ws.Url, ws.Title, ws.IntervalSeconds, ws.Incognito, nowIso));
            ws.IntervalSeconds = 0;
        }
        return list;
    }

    // Son gönderimden bu yana uygulama başına biriken süre (zaman serisi), aralığı sıfırla.
    private static List<AppUsageOut> BuildAppUsage()
    {
        var list = new List<AppUsageOut>();
        var nowIso = DateTime.Now.ToString("o");   // yerel saat (dosya olaylarıyla tutarlı, tarih filtresi doğru)
        foreach (var stat in Stats.Values)
        {
            if (stat.IntervalSeconds <= 0) continue;
            list.Add(new AppUsageOut(stat.App, stat.Exe, stat.IntervalSeconds, nowIso));
            stat.IntervalSeconds = 0;
        }
        return list;
    }

    // Son gönderimden bu yana belge başına biriken süre (dosya-içi süre, zaman serisi).
    private static List<DocUsageOut> BuildDocUsage()
    {
        var list = new List<DocUsageOut>();
        var nowIso = DateTime.Now.ToString("o");
        foreach (var kv in DocStats)
        {
            var ds = kv.Value;
            if (ds.IntervalSeconds <= 0) continue;
            list.Add(new DocUsageOut(kv.Key, ds.App, ds.IntervalSeconds, nowIso));
            ds.IntervalSeconds = 0;
        }
        return list;
    }

    private static List<T> Drain<T>(ConcurrentQueue<T> q, int max)
    {
        var list = new List<T>();
        while (list.Count < max && q.TryDequeue(out var item)) list.Add(item);
        return list;
    }

    private static void Enqueue<T>(ConcurrentQueue<T> q, T item)
    {
        q.Enqueue(item);
        while (q.Count > MaxPending) q.TryDequeue(out _);  // en eskiyi düşür
    }

    // --- Aktivite örneği ---
    private static void Sample()
    {
        if (_dormant) return;   // panelden durdurulduysa toplama yapma
        var idleMs = GetIdleMilliseconds();
        if (idleMs >= _idleThreshold * 1000)
        {
            _idleSeconds += SampleSeconds;
            if (!_wasIdle) { LogEvent(new AgentEvent("idle_start", null, null, null, null)); _wasIdle = true; }
            return;
        }
        if (_wasIdle) { LogEvent(new AgentEvent("idle_end", null, null, null, null)); _wasIdle = false; }

        var (hwnd, exe, friendly, title) = GetForegroundApp();
        _activeSeconds += SampleSeconds;

        if (!Stats.TryGetValue(exe, out var stat))
        {
            stat = new AppStat { Exe = exe, App = friendly, FirstSeen = DateTime.Now };
            Stats[exe] = stat;
        }
        stat.ActiveSeconds += SampleSeconds;
        stat.IntervalSeconds += SampleSeconds;
        stat.LastSeen = DateTime.Now;
        stat.Samples++;

        if (!string.Equals(exe, _lastExe, StringComparison.OrdinalIgnoreCase))
        {
            LogEvent(new AgentEvent("focus", exe, title, null, null) { App = friendly });
            _lastExe = exe;
        }

        // Belge uygulaması (Word/Excel/PDF/editör) → açık belgeyi başlıktan çıkar, süreyi belgeye yaz.
        if (DocumentMonitor.IsDocApp(exe))
        {
            var doc = DocumentMonitor.DocName(title);
            if (doc is not null)
            {
                if (!DocStats.TryGetValue(doc, out var ds)) { ds = new DocStat(); DocStats[doc] = ds; }
                ds.App = DocumentMonitor.AppName(exe);
                ds.Seconds += SampleSeconds; ds.IntervalSeconds += SampleSeconds; ds.LastSeen = DateTime.Now;
            }
        }

        // Tarayıcıysa aktif sekmenin URL'sini yakala (gizli mod dahil), süreyi domaine yaz.
        if (BrowserMonitor.IsBrowser(exe))
        {
            // Uzantı köprüsü varsa onu kullan (tam URL, SPA, gizli mod); yoksa UIA yedeği.
            // Uzantı her sekme/odak/başlık değişiminde son aktif sekmeyi yazar; kullanıcı sayfada
            // uzun süre kalsa da bu "son bilinen" sekme geçerlidir → pencereyi geniş tut (5 dk).
            var tab = _extWeb?.Latest(TimeSpan.FromSeconds(300)) ?? Browser.GetActiveTab(hwnd, exe, title);
            if (tab is not null)
            {
                // Anahtar: SAYFA bazında topla → tam URL varsa URL (her short/sayfa ayrı görünür),
                // yoksa domain, o da yoksa başlık. Böylece "youtube.com" altındaki alt sayfalar kaybolmaz.
                var key = !string.IsNullOrWhiteSpace(tab.Url) ? tab.Url
                        : !string.IsNullOrWhiteSpace(tab.Domain) ? tab.Domain : tab.Title;
                if (!string.IsNullOrWhiteSpace(key))
                {
                    if (!WebStats.TryGetValue(key, out var ws)) { ws = new WebStat(); WebStats[key] = ws; }
                    ws.Domain = tab.Domain;   // gerçek host ya da "" (başlık asla domain'e yazılmaz)
                    ws.Url = tab.Url; ws.Title = tab.Title; ws.Incognito = tab.Incognito;
                    ws.Seconds += SampleSeconds; ws.IntervalSeconds += SampleSeconds; ws.LastSeen = DateTime.Now;

                    // Kategori riski: porno/illegal (kritik) ise UYARI üret (kim + hangi site).
                    CheckWebPolicy(!string.IsNullOrWhiteSpace(tab.Domain) ? tab.Domain! : tab.Title ?? key, ws.Incognito);
                }
            }
        }
    }

    // Riskli/yasaklı site ziyaretinde uyarı üret. Aynı site için spam olmasın diye site başına bekleme.
    private static readonly Dictionary<string, DateTime> _webAlertCooldown = new(StringComparer.OrdinalIgnoreCase);
    private const int WebAlertCooldownSeconds = 300;

    private static void CheckWebPolicy(string siteText, bool incognito)
    {
        var res = WebCategory.Classify(siteText);
        if (res.Risk != WebRisk.Critical) return;

        var now = DateTime.Now;
        if (_webAlertCooldown.TryGetValue(siteText, out var last) && (now - last).TotalSeconds < WebAlertCooldownSeconds)
            return;
        _webAlertCooldown[siteText] = now;
        if (_webAlertCooldown.Count > 500) _webAlertCooldown.Clear();

        var shortSite = siteText.Length > 80 ? siteText[..79] + "…" : siteText;
        var alert = new Alert(
            now.ToString("o"), "critical", "web-politika", "web_policy",
            $"Yasaklı/riskli site ziyareti — {res.Category}",
            $"{Environment.UserName} ({res.Category}{(incognito ? ", gizli mod" : "")}): {shortSite}",
            1, 0, new List<string> { shortSite }, Environment.MachineName, Environment.UserName);
        OnAlert(alert);
    }

    private static void OnFileEvent(FileEvent e)
    {
        if (_dormant) return;   // panelden durdurulduysa dosya olaylarını yok say
        if (NoiseFilter.IsNoise(e.Op, e.Path)) return;   // sistem/geçici/önbellek gürültüsünü kaynağında ele
        switch (e.Op)
        {
            case "create": Interlocked.Increment(ref _fileCreates); break;
            case "modify": Interlocked.Increment(ref _fileModifies); break;
            case "delete": Interlocked.Increment(ref _fileDeletes); break;
            case "rename": Interlocked.Increment(ref _fileRenames); break;
            case "copy":   Interlocked.Increment(ref _fileCopies); break;
            case "usb_copy":   Interlocked.Increment(ref _usbCopies); break;
            case "usb_insert": Interlocked.Increment(ref _usbInserts); break;
        }

        // Hassas içerik taraması — oluşturma/değiştirme/kopyalama olaylarında (silmede dosya yok, taranmaz).
        // "create" olayında dosya çoğu kez henüz boştur (içerik sonradan yazılır); "modify" bu boşluğu kapatır.
        // Uyarı yalnız kopya/USB'de tetiklenir; modify yalnız panelde "hassas" etiketi düşürür.
        if (_classifier.Enabled && e.Op is "create" or "modify" or "copy" or "usb_copy")
        {
            var (label, hits) = _classifier.Classify(e.Path);
            if (label is not null)
            {
                e = e with { Sensitivity = label, SensitiveHits = hits };
                Interlocked.Increment(ref _sensitiveFiles);
            }
        }

        LogLine(_eventsPath, e);
        if (_server is not null) Enqueue(PendingEvents, new OutEvent(e.Ts, null, e.Op, null, null, null, e.Path, e.Sensitivity, e.OldPath));
        _engine.Observe(e);
    }

    private static void OnAlert(Alert a)
    {
        Interlocked.Increment(ref _alertCount);
        LogLine(_alertsPath, a);
        if (_server is not null) Enqueue(PendingAlerts, a);

        var prev = Console.ForegroundColor;
        if (!Console.IsOutputRedirected) Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"\n  ⚠ UYARI [{a.Severity}] {a.Title} — {a.Detail}");
        if (!Console.IsOutputRedirected) Console.ForegroundColor = prev;
    }

    // --- Özet üretimi (disk + sunucu ortak) ---
    private static SessionSummary BuildSummary()
    {
        var apps = Stats.Values
            .OrderByDescending(s => s.ActiveSeconds)
            .Select(s => new AppSummary(
                s.App, s.Exe, s.ActiveSeconds,
                _activeSeconds > 0 ? Math.Round(100.0 * s.ActiveSeconds / _activeSeconds, 1) : 0,
                s.FirstSeen, s.LastSeen))
            .ToList();

        var web = WebStats.Values
            .OrderByDescending(w => w.Seconds)
            .Select(w => new WebSummary(w.Domain, w.Url, w.Title ?? "", w.Seconds, w.Incognito))
            .ToList();

        return new SessionSummary(
            DateTime.Now, Environment.MachineName, Environment.UserName, _sessionStart,
            _activeSeconds, _idleSeconds,
            Interlocked.Read(ref _fileCreates), Interlocked.Read(ref _fileModifies),
            Interlocked.Read(ref _fileDeletes), Interlocked.Read(ref _fileRenames),
            Interlocked.Read(ref _fileCopies), Interlocked.Read(ref _alertCount), apps, web);
    }

    private static void Flush()
    {
        lock (IoLock) File.WriteAllText(_summaryPath, JsonSerializer.Serialize(BuildSummary(), JsonOpts));
    }

    private static void LogEvent(AgentEvent ev)
    {
        LogLine(_eventsPath, ev);
        if (_server is not null) Enqueue(PendingEvents, new OutEvent(ev.Ts, ev.Type, null, ev.Exe, ev.App, ev.Title, null));
    }

    private static void LogLine(string path, object obj)
    {
        try { lock (IoLock) File.AppendAllText(path, JsonSerializer.Serialize(obj, JsonOpts) + Environment.NewLine); }
        catch { /* dosya kilidi — tek örnek kaybı kritik değil */ }
    }

    private static void Render()
    {
        Console.Clear();
        var total = _activeSeconds + _idleSeconds;
        var activePct = total > 0 ? 100.0 * _activeSeconds / total : 0;

        Console.WriteLine("  ARGUS AGENT  ·  canlı izleme (Ctrl+C ile durdur)");
        Console.WriteLine("  " + new string('-', 60));
        Console.WriteLine($"  Makine : {Environment.MachineName}    Kullanıcı: {Environment.UserName}");
        Console.WriteLine($"  Oturum : {Fmt(total)}   Aktif: {Fmt(_activeSeconds)} (%{activePct:0})   Boşta: {Fmt(_idleSeconds)}");
        Console.WriteLine($"  Durum  : {(_wasIdle ? "BOŞTA" : "AKTİF")}");
        Console.WriteLine($"  Dosya  : +{_fileCreates} ~{_fileModifies} -{_fileDeletes} ⇄{_fileRenames} ⧉{_fileCopies}   |   Uyarı: {_alertCount}");
        Console.WriteLine($"  USB    : {_usbInserts} takılma · {_usbCopies} dosya USB'ye kopyalandı   |   {_usbSource?.Describe()}");
        Console.WriteLine($"  Hassas : {_sensitiveFiles} dosyada hassas veri (TC/IBAN/kart/anahtar kelime)   |   İçerik taraması: {(_classifier.Enabled ? "açık" : "kapalı")}");
        var topWeb = WebStats.Values.OrderByDescending(w => w.Seconds).FirstOrDefault();
        if (topWeb is not null)
            Console.WriteLine($"  Web    : {WebStats.Count} site · en çok {topWeb.Domain} ({Fmt(topWeb.Seconds)}){(topWeb.Incognito ? " [gizli]" : "")}");
        Console.WriteLine($"  Sunucu : {_serverStatus}   (kuyruk: {PendingEvents.Count} olay / {PendingAlerts.Count} uyarı)");
        Console.WriteLine($"  İzleme : {_watchDesc}");
        Console.WriteLine();
        Console.WriteLine("  Uygulama                          Aktif Süre     Pay");
        Console.WriteLine("  " + new string('-', 60));
        foreach (var s in Stats.Values.OrderByDescending(s => s.ActiveSeconds).Take(10))
        {
            var pct = _activeSeconds > 0 ? 100.0 * s.ActiveSeconds / _activeSeconds : 0;
            var name = s.App.Length > 32 ? s.App[..31] + "…" : s.App;
            Console.WriteLine($"  {name,-32}  {Fmt(s.ActiveSeconds),10}   %{pct,3:0}");
        }
    }

    private static string Fmt(long seconds)
    {
        var t = TimeSpan.FromSeconds(seconds);
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}s {t.Minutes}d {t.Seconds}sn"
             : t.TotalMinutes >= 1 ? $"{t.Minutes}d {t.Seconds}sn"
             : $"{t.Seconds}sn";
    }

    // --- Win32 ---
    private static (IntPtr hwnd, string exe, string friendly, string title) GetForegroundApp()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return (IntPtr.Zero, "(bilinmiyor)", "(bilinmiyor)", "");
        var title = GetWindowTitle(hwnd);
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0) return (hwnd, "(bilinmiyor)", "(bilinmiyor)", title);
        try
        {
            using var p = Process.GetProcessById((int)pid);
            var exe = p.ProcessName;
            return (hwnd, exe, ResolveFriendlyName(p, exe), title);
        }
        catch { return (hwnd, "(erişilemedi)", "(erişilemedi)", title); }
    }

    private static string ResolveFriendlyName(Process p, string exe)
    {
        if (FriendlyCache.TryGetValue(exe, out var cached)) return cached;
        var friendly = exe;
        try
        {
            var desc = p.MainModule?.FileVersionInfo.FileDescription;
            if (!string.IsNullOrWhiteSpace(desc)) friendly = desc!;
        }
        catch { /* korumalı süreçler */ }
        FriendlyCache[exe] = friendly;
        return friendly;
    }

    private static string GetWindowTitle(IntPtr hwnd)
    {
        var len = GetWindowTextLength(hwnd);
        if (len <= 0) return "";
        var sb = new System.Text.StringBuilder(len + 1);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static uint GetIdleMilliseconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        return (uint)Environment.TickCount - info.dwTime;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] private static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, System.Text.StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

    internal static void StopAgentLoop() => _running = false;
}

// Windows Service sarmalayıcısı — GÖZCÜ modu.
// Servis Session 0'da izleme YAPMAZ (ön plan penceresini göremez); bunun yerine izleme ajanını
// aktif kullanıcı oturumunda başlatır ve canlı tutar (SessionLauncher). Tamper/kalıcılık servis tarafında.
internal sealed class ArgusService : System.ServiceProcess.ServiceBase
{
    private readonly SessionLauncher _launcher = new();
    public ArgusService() { ServiceName = "ArgusAgent"; }
    protected override void OnStart(string[] args) => _launcher.Start();
    protected override void OnStop() => _launcher.Stop();
}

internal sealed class AppStat
{
    public string Exe = "", App = "";
    public long ActiveSeconds;
    public long IntervalSeconds;   // son gönderimden bu yana (zaman serisi için)
    public int Samples;
    public DateTime FirstSeen, LastSeen;
}

internal sealed class DocStat
{
    public string App = "";
    public long Seconds;           // oturum toplamı
    public long IntervalSeconds;   // son gönderimden bu yana (zaman serisi için)
    public DateTime LastSeen;
}

internal sealed record SessionSummary(
    DateTime GeneratedAt, string Machine, string User, DateTime SessionStart,
    long TotalActiveSeconds, long TotalIdleSeconds,
    long FileCreates, long FileModifies, long FileDeletes, long FileRenames, long FileCopies, long AlertCount,
    List<AppSummary> Apps, List<WebSummary> Web);

internal sealed record WebSummary(string Domain, string? Url, string Title, long Seconds, bool Incognito);

internal sealed class WebStat
{
    public string Domain = "";
    public string? Url;
    public string? Title;
    public long Seconds;           // oturum toplamı
    public long IntervalSeconds;   // son gönderimden bu yana (zaman serisi için)
    public bool Incognito;
    public DateTime LastSeen;
}

internal sealed record AppSummary(
    string App, string Exe, long ActiveSeconds, double SharePercent,
    DateTime FirstSeen, DateTime LastSeen);

internal sealed record AgentEvent(
    string Type, string? Exe, string? Title, string? Machine, string? User)
{
    public string Ts { get; init; } = DateTime.Now.ToString("o");
    public string? App { get; set; }
}
