// Uyarı (kural) motoru. Şimdilik tek kural: kayan pencerede N+ silme → kritik uyarı.
// Kurallar ilerde sunucudaki politikadan gelecek; motor kaynaktan bağımsız çalışır.

namespace Argus.Agent;

public sealed record Alert(
    string Ts, string Severity, string Rule, string Type,
    string Title, string Detail, int Count, int WindowSeconds,
    List<string> Samples, string Machine, string User);

public sealed class AlertEngine
{
    // Sunucu politikasıyla canlı güncellenir → readonly değil, _gate altında değişir.
    private int _deleteThreshold;
    private int _copyThreshold;
    private int _usbThreshold;
    private int _sensitiveThreshold;
    private int _windowSeconds;
    private int _cooldownSeconds;

    private readonly LinkedList<(DateTime t, string path)> _recentDeletes = new();
    private readonly LinkedList<(DateTime t, string path)> _recentCopies = new();
    private readonly LinkedList<(DateTime t, string path)> _recentUsb = new();
    private readonly LinkedList<(DateTime t, string path)> _recentSensitive = new();
    private DateTime _lastDeleteAlert = DateTime.MinValue;
    private DateTime _lastCopyAlert = DateTime.MinValue;
    private DateTime _lastUsbAlert = DateTime.MinValue;
    private DateTime _lastSensitiveAlert = DateTime.MinValue;
    private readonly object _gate = new();

    public event Action<Alert>? OnAlert;

    // Demo için düşük eşik. Üretimde politika ör. 50 silme / 5 dk.
    // USB'ye kopyalama daha şüpheli (veri sızıntısı) → eşik daha düşük.
    public AlertEngine(int deleteThreshold = 10, int copyThreshold = 15, int usbThreshold = 3,
                       int sensitiveThreshold = 1, int windowSeconds = 30, int cooldownSeconds = 60)
    {
        _deleteThreshold = deleteThreshold;
        _copyThreshold = copyThreshold;
        _usbThreshold = usbThreshold;
        _sensitiveThreshold = sensitiveThreshold;
        _windowSeconds = windowSeconds;
        _cooldownSeconds = cooldownSeconds;
    }

    // Sunucudan gelen politikayı uygula (panelde eşikler değişince agent yeniden başlamadan geçerli olur).
    public void Configure(int deleteThreshold, int copyThreshold, int usbThreshold, int sensitiveThreshold,
                          int windowSeconds, int cooldownSeconds)
    {
        lock (_gate)
        {
            _deleteThreshold = Math.Max(1, deleteThreshold);
            _copyThreshold = Math.Max(1, copyThreshold);
            _usbThreshold = Math.Max(1, usbThreshold);
            _sensitiveThreshold = Math.Max(1, sensitiveThreshold);
            _windowSeconds = Math.Max(1, windowSeconds);
            _cooldownSeconds = Math.Max(0, cooldownSeconds);
        }
    }

    public void Observe(FileEvent e)
    {
        var op = e.Op?.ToLowerInvariant();

        // Hassas içerik dışarı taşınıyorsa (kopya/USB), veri türüyle birlikte yüksek öncelikli uyarı.
        // Adet-tabanlı kurallardan bağımsız: tek bir hassas dosya bile eşiği (varsayılan 1) tetikler.
        if (e.Sensitivity is not null && (op == "copy" || op == "usb_copy"))
        {
            var dest = op == "usb_copy" ? "USB'ye" : "başka konuma";
            var s = EvaluateSensitive(e.Path, e.Sensitivity, dest);
            if (s is not null) OnAlert?.Invoke(s);
        }

        Alert? fire = op switch
        {
            "delete" => Evaluate(_recentDeletes, e.Path, _deleteThreshold, ref _lastDeleteAlert,
                                  "toplu-silme", "mass_delete", "Toplu dosya silme tespit edildi", "silindi"),
            "copy"   => Evaluate(_recentCopies, e.Path, _copyThreshold, ref _lastCopyAlert,
                                  "toplu-kopyalama", "mass_copy", "Toplu dosya kopyalama tespit edildi", "kopyalandı"),
            "usb_copy" => Evaluate(_recentUsb, e.Path, _usbThreshold, ref _lastUsbAlert,
                                  "usb-sizinti", "usb_exfil", "USB'ye toplu dosya kopyalama tespit edildi", "USB'ye kopyalandı"),
            _ => null
        };
        if (fire is not null) OnAlert?.Invoke(fire);
    }

    // Hassas veri sızıntısı kuralı: kayan pencerede N hassas dosya taşınırsa uyar; detayda veri türü.
    private Alert? EvaluateSensitive(string path, string labels, string dest)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            _recentSensitive.AddLast((now, path));
            var cutoff = now.AddSeconds(-_windowSeconds);
            while (_recentSensitive.First is not null && _recentSensitive.First.Value.t < cutoff)
                _recentSensitive.RemoveFirst();

            if (_recentSensitive.Count < _sensitiveThreshold ||
                (now - _lastSensitiveAlert).TotalSeconds < _cooldownSeconds) return null;
            _lastSensitiveAlert = now;
            var samples = _recentSensitive.Reverse().Select(x => x.path).Take(6).ToList();
            return new Alert(
                now.ToString("o"), "critical", "hassas-veri-sizinti", "sensitive_exfil",
                "Hassas veri dışarı taşınıyor",
                $"{_recentSensitive.Count} hassas dosya {dest} taşındı ({labels}).",
                _recentSensitive.Count, _windowSeconds, samples, Environment.MachineName, Environment.UserName);
        }
    }

    private Alert? Evaluate(LinkedList<(DateTime t, string path)> window, string path, int threshold,
                            ref DateTime lastAlert, string rule, string type, string title, string verb)
    {
        lock (_gate)
        {
            var now = DateTime.Now;
            window.AddLast((now, path));
            var cutoff = now.AddSeconds(-_windowSeconds);
            while (window.First is not null && window.First.Value.t < cutoff) window.RemoveFirst();

            if (window.Count < threshold || (now - lastAlert).TotalSeconds < _cooldownSeconds) return null;
            lastAlert = now;
            var samples = window.Reverse().Select(x => x.path).Take(6).ToList();
            return new Alert(
                now.ToString("o"), "critical", rule, type, title,
                $"{window.Count} dosya {_windowSeconds} sn içinde {verb}.",
                window.Count, _windowSeconds, samples, Environment.MachineName, Environment.UserName);
        }
    }
}
