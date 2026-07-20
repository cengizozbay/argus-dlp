// USB / harici disk izleme (DLP çekirdeği: veri harici medyaya sızabilir).
// Çıkarılabilir sürücüleri yoklar; takılma/çıkarılma olayı üretir ve takılıyken
// sürücü köküne FileSystemWatcher takarak medyaya YAZILAN dosyaları "usb_copy" olarak işaretler.
// FileEvent akışını yeniden kullanır → panelde Dosya Olayları'nda otomatik görünür.
// Not: klasik USB bellek = DriveType.Removable. Bazı harici HDD'ler Fixed görünür; onları
// C:'yi izlememek için kapsam dışı tuttuk (üretimde WMI cihaz olayı ile ayırt edilecek).

namespace Argus.Agent;

public sealed class UsbMonitor : IDisposable
{
    private readonly Dictionary<string, FileSystemWatcher> _mounted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _labels = new(StringComparer.OrdinalIgnoreCase); // kök -> açıklama
    private readonly HashSet<string> _dead = new(StringComparer.OrdinalIgnoreCase); // watcher'ı ölen (buffer taşması) kökler
    private readonly object _gate = new();
    private Thread? _thread;
    private volatile bool _running;

    public event Action<FileEvent>? OnEvent;

    public void Start()
    {
        _running = true;
        Scan();                                   // ilk durum (halihazırda takılı sürücüler)
        _thread = new Thread(Loop) { IsBackground = true, Name = "argus-usb" };
        _thread.Start();
    }

    private void Loop()
    {
        while (_running)
        {
            Thread.Sleep(2000);
            try { Scan(); } catch { /* sürücü geçişi sırasında yarış — sonraki tur toparlar */ }
        }
    }

    private void Scan()
    {
        lock (_gate)
        {
            var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Removable) continue;
                if (!d.IsReady) continue;
                var root = d.RootDirectory.FullName;   // ör. "E:\"
                current.Add(root);
                if (!_mounted.ContainsKey(root))
                    Attach(root, d, announce: true);           // yeni takıldı
                else if (_dead.Remove(root))
                    Attach(root, d, announce: false);          // watcher ölmüştü (buffer taşması) → sessizce yeniden kur
            }

            // Artık görülmeyen (çıkarılan) sürücüler
            foreach (var root in _mounted.Keys.ToList())
                if (!current.Contains(root)) Detach(root);
        }
    }

    private void Attach(string root, DriveInfo d, bool announce)
    {
        string label;
        try
        {
            var name = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Adsız Birim" : d.VolumeLabel;
            var gb = d.TotalSize / (1024.0 * 1024 * 1024);
            label = $"{root} ({name}, {gb:0.#} GB)";
        }
        catch { label = root; }
        _labels[root] = label;

        // Yeniden kurulumsa (buffer taşması sonrası) eski ölü watcher'ı bırak.
        if (_mounted.TryGetValue(root, out var old)) { try { old?.Dispose(); } catch { } }

        try
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                InternalBufferSize = 64 * 1024
            };
            w.Created += (_, e) => Emit("usb_copy", e.FullPath);
            w.Renamed += (_, e) => Emit("usb_copy", e.FullPath);   // taşıma/yeniden adlandırma da medyaya varıştır
            // Buffer taşması / geçici erişim hatası: watcher'ı ölü işaretle. Sürücü hâlâ takılıysa
            // sonraki tarama sessizce yeniden kurar (yoksa olaylar kalıcı olarak kaybolurdu).
            w.Error += (_, __) => { lock (_gate) _dead.Add(root); };
            w.EnableRaisingEvents = true;
            _mounted[root] = w;
        }
        catch
        {
            _mounted[root] = null!;   // izlenemese de "takıldı" olayını yine de bildirelim
        }

        if (announce) Emit("usb_insert", label);
    }

    private void Detach(string root)
    {
        if (_mounted.TryGetValue(root, out var w))
        {
            try { w?.Dispose(); } catch { }
        }
        _mounted.Remove(root);
        _dead.Remove(root);
        Emit("usb_remove", _labels.TryGetValue(root, out var lbl) ? lbl : root);
        _labels.Remove(root);
    }

    private void Emit(string op, string path)
        => OnEvent?.Invoke(new FileEvent(
            DateTime.Now.ToString("o"), op, path, null,
            Environment.MachineName, Environment.UserName));

    public string Describe()
    {
        lock (_gate)
            return _mounted.Count == 0 ? "(takılı çıkarılabilir sürücü yok)" : string.Join("  |  ", _labels.Values);
    }

    public void Dispose()
    {
        _running = false;
        lock (_gate)
        {
            foreach (var w in _mounted.Values) { try { w?.Dispose(); } catch { } }
            _mounted.Clear();
            _dead.Clear();
        }
    }
}
