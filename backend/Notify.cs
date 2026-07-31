// Uyarı bildirimi — e-posta · webhook · SIEM (syslog). SIFIR dış bağımlılık.
//
// NEDEN: Uyarıyı görmenin tek yolu paneli açık tutmaktı. Kurumsal kullanımda kimse
// paneli 24 saat izlemez; USB'ye hassas veri kopyalanmasını ertesi gün öğrenmek geç.
// Bu sınıf kritik uyarıları güvenlik ekibine ITER (push) ve SIEM'e akıtır.
//
// Tasarım:
//  - Telemetri isteği ASLA beklemez: uyarılar kuyruğa atılır, ayrı iplik gönderir.
//  - Toplu gönderim: 60 sn'lik pencerede biriken uyarılar firma başına TEK e-postada özetlenir
//    (toplu kopyalamada 200 ayrı e-posta atmak bildirimi işe yaramaz hale getirirdi).
//  - SMTP kimliği SUNUCU düzeyinde ortam değişkeniyle verilir; parola tenant kaydında tutulmaz.
//  - Gönderim hatası akışı bozmaz, yalnız hata günlüğüne yazılır.
//
// Ortam değişkenleri:
//   ARGUS_SMTP_HOST / ARGUS_SMTP_PORT (varsayılan 587) / ARGUS_SMTP_USER / ARGUS_SMTP_PASS
//   ARGUS_SMTP_FROM (varsayılan kullanıcı adı) / ARGUS_SMTP_TLS (varsayılan true)
//   ARGUS_SYSLOG  → "host:port" (UDP). Verilirse her uyarı CEF biçiminde SIEM'e akar.
//   ARGUS_PANEL_URL → e-postadaki panel bağlantısı için taban adres.

using System.Collections.Concurrent;
using System.Net;
using System.Net.Mail;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Argus.Server;

public sealed class Notifier : IDisposable
{
    private readonly record struct Item(string TenantId, string TenantName, string Machine, AlertDto Alert);

    private readonly ConcurrentQueue<Item> _queue = new();
    private readonly IStore _store;
    private readonly System.Threading.Timer _timer;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private volatile bool _disposed;

    // Aynı firma+tip için üst üste bildirim yağmuru olmasın (toplu olayda tek özet yeter).
    private readonly ConcurrentDictionary<string, DateTime> _lastSent = new();
    private static readonly TimeSpan TypeCooldown = TimeSpan.FromMinutes(10);

    private readonly string? _smtpHost, _smtpUser, _smtpPass, _smtpFrom;
    private readonly int _smtpPort;
    private readonly bool _smtpTls;
    private readonly string? _syslogHost;
    private readonly int _syslogPort;
    private readonly string _panelUrl;

    public Notifier(IStore store)
    {
        _store = store;
        _smtpHost = Env("ARGUS_SMTP_HOST");
        _smtpPort = int.TryParse(Env("ARGUS_SMTP_PORT"), out var p) ? p : 587;
        _smtpUser = Env("ARGUS_SMTP_USER");
        _smtpPass = Env("ARGUS_SMTP_PASS");
        _smtpFrom = Env("ARGUS_SMTP_FROM") ?? _smtpUser;
        _smtpTls = (Env("ARGUS_SMTP_TLS") ?? "true").Equals("true", StringComparison.OrdinalIgnoreCase);
        _panelUrl = (Env("ARGUS_PANEL_URL") ?? "").TrimEnd('/');

        var sys = Env("ARGUS_SYSLOG");
        if (!string.IsNullOrWhiteSpace(sys))
        {
            var bits = sys.Split(':', 2);
            _syslogHost = bits[0];
            _syslogPort = bits.Length > 1 && int.TryParse(bits[1], out var sp) ? sp : 514;
        }

        _timer = new System.Threading.Timer(_ => Drain(), null, TimeSpan.FromSeconds(20), TimeSpan.FromSeconds(60));
    }

    private static string? Env(string k)
    {
        var v = Environment.GetEnvironmentVariable(k);
        return string.IsNullOrWhiteSpace(v) ? null : v;
    }

    public bool EmailConfigured => !string.IsNullOrWhiteSpace(_smtpHost);
    public bool SyslogConfigured => !string.IsNullOrWhiteSpace(_syslogHost);

    // Telemetri yolundan çağrılır. HIÇBIR ŞEKİLDE bloklamaz.
    public void Enqueue(string tenantId, string tenantName, string machine, IEnumerable<AlertDto> alerts)
    {
        if (_disposed) return;
        foreach (var a in alerts)
        {
            // Syslog/SIEM anlık akmalı (korelasyon için gecikme istenmez), e-posta toplu gider.
            if (SyslogConfigured) TrySyslog(tenantName, machine, a);
            if (_queue.Count < 5000) _queue.Enqueue(new Item(tenantId, tenantName, machine, a));
        }
    }

    private static int SeverityRank(string? s) => s switch
    {
        "critical" => 3,
        "warning" => 2,
        _ => 1
    };

    private void Drain()
    {
        if (_disposed) return;
        try
        {
            // Kuyruğu boşalt ve firma bazında grupla.
            var batch = new List<Item>();
            while (_queue.TryDequeue(out var it)) batch.Add(it);
            if (batch.Count == 0) return;

            foreach (var g in batch.GroupBy(x => x.TenantId))
            {
                TenantSettings s;
                try { s = _store.GetSettings(g.Key); } catch { continue; }

                var min = SeverityRank(string.IsNullOrWhiteSpace(s.NotifyMinSeverity) ? "critical" : s.NotifyMinSeverity);
                var eligible = g.Where(x => SeverityRank(x.Alert.Severity) >= min).ToList();
                if (eligible.Count == 0) continue;

                // Tip başına bekleme: aynı tipte 10 dk içinde ikinci bildirim gitmesin.
                var fresh = eligible.Where(x =>
                {
                    var key = g.Key + "|" + (x.Alert.Type ?? "");
                    var now = DateTime.UtcNow;
                    if (_lastSent.TryGetValue(key, out var last) && now - last < TypeCooldown) return false;
                    _lastSent[key] = now;
                    return true;
                }).ToList();
                if (fresh.Count == 0) continue;

                var tenantName = fresh[0].TenantName;
                if (!string.IsNullOrWhiteSpace(s.NotifyWebhookUrl)) TryWebhook(s.NotifyWebhookUrl!, tenantName, fresh);
                if (EmailConfigured && !string.IsNullOrWhiteSpace(s.NotifyEmailTo)) TryEmail(s.NotifyEmailTo!, tenantName, fresh);
            }
        }
        catch (Exception ex) { FileLog.Error("notify", ex.Message); }
    }

    // --- Webhook (Slack/Teams/kendi sisteminiz) ---
    private void TryWebhook(string url, string tenantName, List<Item> items)
    {
        try
        {
            var payload = new
            {
                source = "argus-dlp",
                tenant = tenantName,
                at = DateTime.UtcNow.ToString("o"),
                count = items.Count,
                // Slack/Teams gibi "text" bekleyen uçlar da anlamlı bir şey görsün.
                text = Summary(tenantName, items),
                alerts = items.Select(x => new
                {
                    machine = x.Machine,
                    user = x.Alert.User,
                    severity = x.Alert.Severity,
                    type = x.Alert.Type,
                    title = x.Alert.Title,
                    detail = x.Alert.Detail,
                    ts = x.Alert.Ts
                })
            };
            var body = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");
            var resp = _http.PostAsync(url, body).GetAwaiter().GetResult();
            if (!resp.IsSuccessStatusCode) FileLog.Error("notify-webhook", $"{(int)resp.StatusCode} {url}");
        }
        catch (Exception ex) { FileLog.Error("notify-webhook", ex.Message); }
    }

    // --- E-posta ---
    private void TryEmail(string to, string tenantName, List<Item> items)
    {
        try
        {
            using var client = new SmtpClient(_smtpHost, _smtpPort) { EnableSsl = _smtpTls };
            if (!string.IsNullOrWhiteSpace(_smtpUser))
                client.Credentials = new NetworkCredential(_smtpUser, _smtpPass ?? "");

            var crit = items.Count(x => x.Alert.Severity == "critical");
            var subject = crit > 0
                ? $"[Argus] {tenantName} — {crit} KRİTİK veri güvenliği uyarısı"
                : $"[Argus] {tenantName} — {items.Count} güvenlik uyarısı";

            using var msg = new MailMessage
            {
                From = new MailAddress(_smtpFrom!, "Argus DLP"),
                Subject = subject,
                Body = Summary(tenantName, items),
                IsBodyHtml = false
            };
            foreach (var addr in to.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                msg.To.Add(addr);
            if (msg.To.Count == 0) return;

            client.Send(msg);
        }
        catch (Exception ex) { FileLog.Error("notify-mail", ex.Message); }
    }

    private string Summary(string tenantName, List<Item> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Argus DLP — {tenantName}");
        sb.AppendLine($"{items.Count} uyarı ({DateTime.Now:dd.MM.yyyy HH:mm})");
        sb.AppendLine(new string('-', 52));
        foreach (var x in items.OrderByDescending(x => SeverityRank(x.Alert.Severity)).Take(30))
        {
            var sev = x.Alert.Severity switch { "critical" => "KRİTİK", "warning" => "UYARI", _ => "BİLGİ" };
            sb.AppendLine($"[{sev}] {x.Alert.Title}");
            sb.AppendLine($"        Makine: {x.Machine}   Kullanıcı: {x.Alert.User}");
            sb.AppendLine($"        {x.Alert.Detail}");
            sb.AppendLine();
        }
        if (items.Count > 30) sb.AppendLine($"… ve {items.Count - 30} uyarı daha.");
        if (_panelUrl.Length > 0) sb.AppendLine($"Panel: {_panelUrl}");
        return sb.ToString();
    }

    // --- SIEM: CEF biçiminde syslog (UDP) ---
    // ArcSight/Splunk/QRadar/Wazuh bu biçimi doğrudan ayrıştırır.
    private void TrySyslog(string tenantName, string machine, AlertDto a)
    {
        try
        {
            var sev = a.Severity switch { "critical" => 9, "warning" => 6, _ => 3 };
            var cef = $"CEF:0|Argus|ArgusDLP|1.0|{Esc(a.Type)}|{Esc(a.Title)}|{sev}|" +
                      $"dvchost={Esc(machine)} suser={Esc(a.User)} cs1Label=Tenant cs1={Esc(tenantName)} " +
                      $"cs2Label=Rule cs2={Esc(a.Rule)} cnt={a.Count} msg={Esc(a.Detail)} rt={Esc(a.Ts)}";
            // RFC3164 öncelik: facility 13 (log audit) * 8 + severity
            var pri = 13 * 8 + (a.Severity == "critical" ? 2 : a.Severity == "warning" ? 4 : 6);
            var packet = Encoding.UTF8.GetBytes($"<{pri}>{DateTime.Now:MMM dd HH:mm:ss} argus {cef}");
            using var udp = new UdpClient();
            udp.Send(packet, packet.Length, _syslogHost!, _syslogPort);
        }
        catch (Exception ex) { FileLog.Error("notify-syslog", ex.Message); }
    }

    // CEF kaçışı: | ve = işaretleri ile satır sonu.
    private static string Esc(string? s) =>
        (s ?? "").Replace("\\", "\\\\").Replace("|", "\\|").Replace("=", "\\=")
                 .Replace("\r", " ").Replace("\n", " ");

    public void Dispose()
    {
        _disposed = true;
        try { _timer.Dispose(); } catch { }
        try { _http.Dispose(); } catch { }
    }
}
