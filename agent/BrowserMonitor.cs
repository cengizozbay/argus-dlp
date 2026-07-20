// Tarayıcı adres çubuğunu Windows UI Automation (COM, late-bound) ile okur — gizli/normal fark etmez.
// URL yakalanamazsa BAŞLIK adres olarak KULLANILMAZ (aksi halde bozuk link olur); sadece
// gerçek URL varsa tıklanabilir olur, yoksa sekme başlığı düz metin olarak saklanır.
// COM aynı iş parçacığında çağrılır (apartman sorunlarını önlemek için). Her şey try/catch korumalı.

namespace Argus.Agent;

public sealed class BrowserMonitor
{
    private static readonly Guid CUIAutomationClsid = new("ff48dba4-60ef-4201-aa87-54103eef594e");
    private const int UIA_ValueValuePropertyId = 30045;
    private const int UIA_ControlTypePropertyId = 30003;
    private const int UIA_EditControlTypeId = 50004;
    private const int TreeScope_Descendants = 4;

    private static readonly HashSet<string> Browsers = new(StringComparer.OrdinalIgnoreCase)
    { "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "chromium", "iexplore" };

    private static readonly string[] BrowserSuffixes =
    { "Google Chrome", "Microsoft Edge", "Mozilla Firefox", "Firefox", "Brave", "Opera", "Vivaldi", "Chromium", "Internet Explorer" };

    private object? _uia;
    private IntPtr _lastHwnd;
    private string _lastTitle = "";
    private WebTab? _cached;

    public static bool IsBrowser(string exe) => Browsers.Contains(exe);

    public WebTab? GetActiveTab(IntPtr hwnd, string exe, string windowTitle)
    {
        if (!IsBrowser(exe) || hwnd == IntPtr.Zero) return null;
        if (hwnd == _lastHwnd && windowTitle == _lastTitle && _cached is not null) return _cached;

        var title = CleanTitle(windowTitle);
        var incog = IsIncognito(windowTitle);
        var url = TryReadUrl(hwnd);               // gerçek URL (yoksa null)
        var domain = DomainOf(url);               // geçerli host (yoksa null)

        // Domain'e ASLA başlık koyma; yoksa boş bırak (frontend linkleştirmez).
        var tab = new WebTab(domain ?? "", url, title, incog);
        _lastHwnd = hwnd; _lastTitle = windowTitle; _cached = tab;
        return tab;
    }

    private string? TryReadUrl(IntPtr hwnd)
    {
        try
        {
            if (_uia is null)   // ilk denemede oluşmazsa (hibrit/COM zamanlaması) sonraki çağrıda tekrar dene
            {
                var t = Type.GetTypeFromCLSID(CUIAutomationClsid);
                if (t is not null) _uia = Activator.CreateInstance(t);
            }
            if (_uia is null) return null;

            dynamic uia = _uia;
            dynamic root = uia.ElementFromHandle(hwnd);
            if (root is null) return null;

            dynamic cond = uia.CreatePropertyCondition(UIA_ControlTypePropertyId, UIA_EditControlTypeId);
            dynamic edits = root.FindAll(TreeScope_Descendants, cond);
            int n = edits.Length;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    dynamic el = edits.GetElement(i);
                    var v = el.GetCurrentPropertyValue(UIA_ValueValuePropertyId) as string;
                    if (LooksLikeUrl(v)) return v!.Trim();
                }
                catch { /* bu edit okunamadı */ }
            }
        }
        catch { /* UIA erişilemedi */ }
        return null;
    }

    private static bool LooksLikeUrl(string? v)
    {
        if (string.IsNullOrWhiteSpace(v)) return false;
        v = v.Trim();
        if (v.Contains(' ')) return false;               // "adsız" veya arama metni değil
        if (v.StartsWith("http", StringComparison.OrdinalIgnoreCase)) return true;
        // "github.com/x" gibi şemasız ama gerçek host: nokta içermeli, boşluk olmamalı
        var hostPart = v.Split('/')[0];
        return hostPart.Contains('.') && !hostPart.EndsWith(".");
    }

    private static string? DomainOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
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

    private static string CleanTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.Trim();
        foreach (var b in BrowserSuffixes)
        {
            foreach (var sep in new[] { " - ", " — " })
            {
                var suffix = sep + b;
                if (t.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    return t[..^suffix.Length].Trim();
            }
        }
        return t;
    }

    private static bool IsIncognito(string title)
    {
        foreach (var k in new[] { "InPrivate", "Incognito", "Gizli", "Private Browsing", "Gizli Gezinti", "Özel" })
            if (title.Contains(k, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public sealed record WebTab(string Domain, string? Url, string Title, bool Incognito);
