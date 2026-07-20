// Tarayıcı uzantısı köprüsünden (web-current.json) aktif sekmeyi okur.
// Uzantı yüklüyse UIA'ya göre çok daha doğru: tam URL, SPA navigasyonları, gizli mod.
// Dosya bayatsa (tarayıcı kapalı/uzantı yok) null döner — agent UIA'ya düşer.

using System.Text.Json;

namespace Argus.Agent;

public sealed class ExtensionWebSource
{
    private readonly string _curPath;

    public ExtensionWebSource(string dataDir)
        => _curPath = Path.Combine(dataDir, "web-current.json");

    public WebTab? Latest(TimeSpan freshness)
    {
        try
        {
            if (!File.Exists(_curPath)) return null;
            if (DateTime.Now - File.GetLastWriteTime(_curPath) > freshness) return null;  // bayat
            return Parse(File.ReadAllText(_curPath));
        }
        catch { return null; }
    }

    private static WebTab? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            var url = r.TryGetProperty("url", out var u) ? u.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) return null;
            var title = r.TryGetProperty("title", out var t) ? (t.GetString() ?? "") : "";
            var incog = r.TryGetProperty("incognito", out var i) && i.ValueKind == JsonValueKind.True;
            return new WebTab(DomainOf(url) ?? "", url, title, incog);
        }
        catch { return null; }
    }

    private static string? DomainOf(string url)
    {
        var u = url.Trim();
        if (!u.Contains("://")) u = "http://" + u;
        try
        {
            var host = new Uri(u).Host;
            if (string.IsNullOrEmpty(host) || !host.Contains('.')) return null;
            return host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..] : host;
        }
        catch { return null; }
    }
}
