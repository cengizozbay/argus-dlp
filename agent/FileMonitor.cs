// Dosya olayı kaynağı — soyutlama + FileSystemWatcher uygulaması.
// Üretimde bu arayüzün arkasına ETW (Kernel-File) + USN Journal kaynağı gelecek;
// motorun geri kalanı değişmeyecek.

using System.Collections.Concurrent;

namespace Argus.Agent;

public sealed record FileEvent(
    string Ts, string Op, string Path, string? OldPath, string Machine, string User)
{
    // Hassas içerik sınıflandırması (ContentClassifier tarafından doldurulur).
    // null = taranmadı ya da temiz. Ör. "TC Kimlik, IBAN".
    public string? Sensitivity { get; init; }
    public int SensitiveHits { get; init; }   // dosyada bulunan hassas veri adedi
}

public interface IFileEventSource : IDisposable
{
    event Action<FileEvent>? OnEvent;
    void Start();
    string Describe();
}

public sealed class FileSystemWatcherSource : IFileEventSource
{
    private readonly List<FileSystemWatcher> _watchers = new();
    private readonly string[] _roots;
    private readonly ConcurrentDictionary<string, DateTime> _lastChange = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _nameIndex = new(StringComparer.OrdinalIgnoreCase); // ad -> ilk yol

    public event Action<FileEvent>? OnEvent;

    public FileSystemWatcherSource(IEnumerable<string> roots)
    {
        _roots = roots.Where(r => !string.IsNullOrWhiteSpace(r) && Directory.Exists(r))
                      .Distinct(StringComparer.OrdinalIgnoreCase)
                      .ToArray();
    }

    public void Start()
    {
        foreach (var root in _roots)
        {
            var w = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024  // yoğun anlarda taşmayı azaltır
            };
            w.Created += (_, e) => EmitCreate(e.FullPath);
            w.Deleted += (_, e) => Emit("delete", e.FullPath, null);
            w.Renamed += (_, e) => Emit("rename", e.FullPath, e.OldFullPath);
            w.Changed += (_, e) => EmitChange(e.FullPath);
            w.Error += (_, __) => { /* buffer overflow: üretimde ETW/USN'e düşülecek */ };
            w.EnableRaisingEvents = true;
            _watchers.Add(w);
        }
    }

    // Aynı adlı bir dosya başka bir konumda zaten görülmüşse, bu "create" muhtemelen bir KOPYA'dır.
    // (Sezgisel — ad tabanlı. Kesin kopya/kaynak tespiti için üretimde ETW + USN kullanılacak.)
    private void EmitCreate(string path)
    {
        var op = "create";
        string? source = null;
        try
        {
            var name = Path.GetFileName(path);
            if (_nameIndex.TryGetValue(name, out var other) &&
                !string.Equals(other, path, StringComparison.OrdinalIgnoreCase) && File.Exists(other))
            { op = "copy"; source = other; }   // kaynak = aynı adlı mevcut dosya (nereden)
            else
                _nameIndex[name] = path;
        }
        catch { /* yol okunamadı */ }
        Emit(op, path, source);
    }

    // FSW tek kaydetmede birden çok "Changed" üretir; aynı yol için 1 sn'lik gürültüyü ele.
    private void EmitChange(string path)
    {
        var now = DateTime.Now;
        if (_lastChange.TryGetValue(path, out var last) && (now - last).TotalMilliseconds < 1000) return;
        _lastChange[path] = now;
        Emit("modify", path, null);
    }

    private void Emit(string op, string path, string? oldPath)
        => OnEvent?.Invoke(new FileEvent(
            DateTime.Now.ToString("o"), op, path, oldPath,
            Environment.MachineName, Environment.UserName));

    public string Describe() => _roots.Length == 0 ? "(izlenecek klasör bulunamadı)" : string.Join("  |  ", _roots);

    public void Dispose()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}
