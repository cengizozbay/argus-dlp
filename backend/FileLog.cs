// Basit dosya günlüğü — bağımlılıksız. Günlük olarak ayrı dosyalar (rotasyon):
//   logs/audit-YYYY-MM-DD.log  → denetim (kim ne yaptı: giriş, kill switch, ayar, firma/kullanıcı)
//   logs/error-YYYY-MM-DD.log  → sunucu hataları (istisna)
//   logs/app-YYYY-MM-DD.log    → genel bilgi
// Üretimde merkezi log (Seq/ELK) eklenebilir; bu, tek sunucu için yeterli + KVKK denetim kaydı sağlar.

namespace Argus.Server;

public static class FileLog
{
    private static string _dir = ".";
    private static readonly object _gate = new();

    public static void Init(string dataDir)
    {
        _dir = Path.Combine(dataDir, "logs");
        try { Directory.CreateDirectory(_dir); } catch { }
    }

    private static void Write(string kind, string line)
    {
        try
        {
            var path = Path.Combine(_dir, $"{kind}-{DateTime.Now:yyyy-MM-dd}.log");
            lock (_gate) File.AppendAllText(path, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch { /* log yazımı asla akışı bozmasın */ }
    }

    // Denetim kaydı: güvenlik/yönetim eylemleri.
    public static void Audit(string tenant, string user, string action, string? detail = null)
        => Write("audit", $"[{tenant}] {user} · {action}{(string.IsNullOrEmpty(detail) ? "" : " · " + detail)}");

    public static void Error(string where, string message) => Write("error", $"{where} :: {message}");
    public static void Info(string message) => Write("app", message);

    // Bir günlük türünün son N satırını oku (panel görüntüleme için).
    public static IReadOnlyList<string> Tail(string kind, int lines)
    {
        try
        {
            var path = Path.Combine(_dir, $"{kind}-{DateTime.Now:yyyy-MM-dd}.log");
            if (!File.Exists(path)) return Array.Empty<string>();
            lock (_gate)
            {
                var all = File.ReadAllLines(path);
                return all.Reverse().Take(lines).ToList();   // en yeni üstte
            }
        }
        catch { return Array.Empty<string>(); }
    }
}
