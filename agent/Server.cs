// Agent yapılandırması + sunucu istemcisi (enroll + telemetri gönderimi).
// Senkron HttpClient kullanır; sunucu erişilemezse sessizce başarısız olur (offline dayanıklılık).

using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Argus.Agent;

public sealed class AgentConfig
{
    public string ServerUrl { get; set; } = "";
    public string TenantKey { get; set; } = "";
    public int SendSeconds { get; set; } = 15;
    public List<string>? WatchFolders { get; set; }
    public string FileMonitor { get; set; } = "auto";   // auto | usn | fsw (sunucu ayarı bunu ezebilir)
    public bool ContentScan { get; set; } = true;        // dosya içeriği hassas veri taraması
    public List<string>? SensitiveKeywords { get; set; } // içerikte aranacak ek anahtar kelimeler
    public bool AllowInsecureTls { get; set; } = false;  // HTTPS'te self-signed sertifikayı kabul et (yalnız test/dev)

    public bool ServerEnabled => !string.IsNullOrWhiteSpace(ServerUrl) && !string.IsNullOrWhiteSpace(TenantKey);

    public static AgentConfig Load(string dataDir)
    {
        // Aday config yolları: önce verilen dataDir (kullanıcı), sonra makine-geneli ProgramData.
        // Servis GÖZCÜ modunda ajanı kullanıcı oturumunda başlatır; config'i ProgramData'dan bulur.
        var candidates = new[]
        {
            Path.Combine(dataDir, "config.json"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Argus", "config.json")
        };
        AgentConfig cfg = new();
        foreach (var path in candidates)
        {
            try
            {
                if (File.Exists(path))
                {
                    cfg = JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(path)) ?? new AgentConfig();
                    break;
                }
            }
            catch { /* sonraki adaya geç */ }
        }

        // Ortam değişkenleri config'i geçersiz kılar (GPO/dağıtım kolaylığı).
        var envServer = Environment.GetEnvironmentVariable("ARGUS_SERVER");
        var envKey = Environment.GetEnvironmentVariable("ARGUS_TENANT_KEY");
        if (!string.IsNullOrWhiteSpace(envServer)) cfg.ServerUrl = envServer;
        if (!string.IsNullOrWhiteSpace(envKey)) cfg.TenantKey = envKey;
        if (cfg.SendSeconds < 5) cfg.SendSeconds = 5;
        return cfg;
    }
}

public sealed record OutEvent(string Ts, string? Type, string? Op, string? Exe, string? App, string? Title, string? Path, string? Sensitivity = null, string? Source = null, string? User = null);
public sealed record WebUsageOut(string Site, string Domain, string? Url, string? Title, long Seconds, bool Incognito, string Ts);
public sealed record AppUsageOut(string App, string Exe, long Seconds, string Ts);
public sealed record DocUsageOut(string Name, string App, long Seconds, string Ts);

// Sunucudan enroll/telemetri yanıtında gelen tenant politikası (backend TenantSettings ile aynı alanlar).
public sealed class AgentSettings
{
    public int DeleteThreshold { get; set; } = 10;
    public int CopyThreshold { get; set; } = 15;
    public int UsbThreshold { get; set; } = 3;
    public int SensitiveThreshold { get; set; } = 1;   // hassas dosya sızıntısı için eşik (1 = ilk dosyada uyar)
    public int WindowSeconds { get; set; } = 30;
    public int CooldownSeconds { get; set; } = 60;
    public int SendSeconds { get; set; } = 15;
    public int IdleThresholdSeconds { get; set; } = 180;
    public string FileMonitor { get; set; } = "auto";
    public bool UsbMonitoring { get; set; } = true;
    public bool UsbBlocked { get; set; } = false;   // USB depolama engelle (SYSTEM servisi registry ile uygular)
    public bool ContentScan { get; set; } = true;
    public List<string>? WatchFolders { get; set; }
    public List<string>? SensitiveKeywords { get; set; }
    public string UpdatedAt { get; set; } = "";
}

public sealed class ServerClient
{
    private readonly HttpClient _http;
    private readonly string _base;
    private readonly string _tenantKey;
    private string? _token;

    public bool Enrolled => _token is not null;
    public string? AgentId { get; private set; }
    public AgentSettings? LatestSettings { get; private set; }   // sunucudan gelen son politika
    public string LatestCommand { get; private set; } = "active"; // active | disabled | remove (uzaktan kill switch)

    public ServerClient(string baseUrl, string tenantKey, bool allowInsecureTls = false)
    {
        _base = baseUrl.TrimEnd('/');
        _tenantKey = tenantKey;
        // HTTPS + self-signed sertifika (yalnız test): sertifika doğrulamasını atla. Üretimde GERÇEK sertifika kullan.
        if (allowInsecureTls)
        {
            var handler = new HttpClientHandler
            {
                ServerCertificateCustomValidationCallback = (_, _, _, _) => true
            };
            _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) };
        }
        else
        {
            _http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        }
    }

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private void CaptureSettings(JsonElement root)
    {
        if (root.TryGetProperty("settings", out var st) && st.ValueKind == JsonValueKind.Object)
        {
            try { LatestSettings = st.Deserialize<AgentSettings>(Opts); } catch { /* bozuk ayar → eski geçerli */ }
        }
        if (root.TryGetProperty("command", out var cm) && cm.ValueKind == JsonValueKind.String)
        {
            var c = cm.GetString();
            if (!string.IsNullOrWhiteSpace(c)) LatestCommand = c!;
        }
    }

    public bool Enroll(string machine, string user, string host)
    {
        try
        {
            var body = JsonSerializer.Serialize(new { Machine = machine, User = user, Host = host }, Opts);
            using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/api/v1/enroll");
            req.Headers.Add("X-Tenant-Key", _tenantKey);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = _http.Send(req);
            if (!resp.IsSuccessStatusCode) return false;

            using var doc = JsonDocument.Parse(new StreamReader(resp.Content.ReadAsStream()).ReadToEnd());
            _token = doc.RootElement.GetProperty("agentToken").GetString();
            AgentId = doc.RootElement.TryGetProperty("agentId", out var id) ? id.GetString() : null;
            CaptureSettings(doc.RootElement);
            return _token is not null;
        }
        catch { return false; }
    }

    public bool SendTelemetry(object summary, IReadOnlyList<Alert> alerts, IReadOnlyList<OutEvent> events, IReadOnlyList<WebUsageOut> webUsage, IReadOnlyList<AppUsageOut> appUsage, IReadOnlyList<DocUsageOut> docUsage)
    {
        if (_token is null) return false;
        try
        {
            var body = JsonSerializer.Serialize(new { Summary = summary, Alerts = alerts, Events = events, WebUsage = webUsage, AppUsage = appUsage, DocUsage = docUsage }, Opts);
            using var req = new HttpRequestMessage(HttpMethod.Post, _base + "/api/v1/telemetry");
            req.Headers.Add("X-Agent-Token", _token);
            req.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var resp = _http.Send(req);
            if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized) _token = null; // yeniden enroll gerekir
            if (resp.IsSuccessStatusCode)
            {
                try
                {
                    using var doc = JsonDocument.Parse(new StreamReader(resp.Content.ReadAsStream()).ReadToEnd());
                    CaptureSettings(doc.RootElement);   // canlı politika güncellemesi
                }
                catch { /* yanıt gövdesi okunamazsa gönderim yine başarılı */ }
                return true;
            }
            return false;
        }
        catch { return false; }
    }
}
