// Argus Server — multi-tenant telemetri API'si (ASP.NET Core, .NET 8, minimal API).
// Kimlik: enroll/okuma için X-Tenant-Key, telemetri için X-Agent-Token.

using Argus.Server;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory   // wwwroot'u çalışma dizininden bağımsız bul
});
// HTTP ve/veya HTTPS — ARGUS_URLS ile seçilir. Örnekler:
//   http://0.0.0.0:5099                                (yalnız HTTP)
//   https://0.0.0.0:5443                               (yalnız HTTPS)
//   http://0.0.0.0:5099;https://0.0.0.0:5443           (ikisi birden — isteyen HTTP isteyen HTTPS)
// HTTPS için sertifika: ARGUS_CERT_PATH (.pfx) + ARGUS_CERT_PASS. (Yoksa .NET dev sertifikası denenir.)
builder.WebHost.UseUrls(Environment.GetEnvironmentVariable("ARGUS_URLS") ?? "http://localhost:5099");
var certPath = Environment.GetEnvironmentVariable("ARGUS_CERT_PATH");
var certPass = Environment.GetEnvironmentVariable("ARGUS_CERT_PASS") ?? "";
builder.WebHost.ConfigureKestrel(k =>
{
    if (!string.IsNullOrWhiteSpace(certPath) && File.Exists(certPath))
        k.ConfigureHttpsDefaults(h =>
            h.ServerCertificate = new System.Security.Cryptography.X509Certificates.X509Certificate2(certPath, certPass));
});

var dataDir = Environment.GetEnvironmentVariable("ARGUS_DATA")
    ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ArgusServer");
var pgConn = Environment.GetEnvironmentVariable("ARGUS_PG") ?? builder.Configuration.GetConnectionString("Postgres");
Directory.CreateDirectory(dataDir);
FileLog.Init(dataDir);
builder.Services.AddSingleton<IStore>(_ => string.IsNullOrWhiteSpace(pgConn)
    ? new SqliteStore(Path.Combine(dataDir, "argus.db"))   // varsayılan: SQLite (kurulumsuz, tek dosya)
    : new PostgresStore(pgConn));                          // ARGUS_PG verilince: PostgreSQL (üretim)
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNameCaseInsensitive = true;
});
// CORS: varsayılan olarak çapraz-köken KAPALI (panel sunucuyla aynı kökenden çalışır, CORS'a gerek yok).
// Gerekirse ARGUS_CORS ile izinli köken listesi verilir (virgülle ayrık).
var corsOrigins = (Environment.GetEnvironmentVariable("ARGUS_CORS") ?? "")
    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
{
    if (corsOrigins.Length > 0) p.WithOrigins(corsOrigins).AllowAnyHeader().AllowAnyMethod();
}));

var app = builder.Build();
app.UseCors();
// Sunucu hatalarını dosyaya yaz (akışı bozmadan).
app.Use(async (ctx, next) =>
{
    try { await next(); }
    catch (Exception ex) { FileLog.Error(ctx.Request.Path, ex.Message); throw; }
});
app.UseDefaultFiles();
app.UseStaticFiles();

// Demo tenant (üretimde admin panelinden açılacak).
var store = app.Services.GetRequiredService<IStore>();
var demo = store.SeedTenant("Demo A.Ş.", "demo-tenant-key-001");

// SÜPER ADMIN (patron) hesabı — ilk açılışta yoksa oluşturulur. Kimlik env ile ezilebilir.
var adminUser = Environment.GetEnvironmentVariable("ARGUS_ADMIN_USER") ?? "admin";
var adminPass = Environment.GetEnvironmentVariable("ARGUS_ADMIN_PASS") ?? "admin";
if (store.CountUsers(demo.Id) == 0)
{
    store.CreateUser(demo.Id, adminUser, Passwords.Hash(adminPass), Roles.Superadmin, "Platform Yöneticisi");
    app.Logger.LogWarning("SÜPER ADMIN oluşturuldu → kullanıcı: {U}  parola: {P}  (ilk girişte değiştirilecek!)", adminUser, adminPass);
}

FileLog.Info("Sunucu başladı.");

// Otomatik yedekleme (yalnız SQLite) — başlangıçta + her 6 saatte bir, son 14 yedeği tut.
System.Threading.Timer? backupTimer = null;
if (store is SqliteStore sqlite)
{
    var backupDir = Path.Combine(dataDir, "backups");
    Directory.CreateDirectory(backupDir);
    void DoBackup()
    {
        try
        {
            var dest = Path.Combine(backupDir, $"argus-{DateTime.Now:yyyy-MM-dd-HHmm}.db");
            sqlite.Backup(dest);
            FileLog.Info($"Yedek alındı: {Path.GetFileName(dest)}");
            foreach (var f in new DirectoryInfo(backupDir).GetFiles("argus-*.db").OrderByDescending(f => f.Name).Skip(14))
                try { f.Delete(); } catch { }
        }
        catch (Exception ex) { FileLog.Error("backup", ex.Message); }
    }
    DoBackup();
    backupTimer = new System.Threading.Timer(_ => DoBackup(), null, TimeSpan.FromHours(6), TimeSpan.FromHours(6));
    app.Lifetime.ApplicationStopping.Register(() => backupTimer?.Dispose());
}

// Bildirim motoru (e-posta / webhook / SIEM). Yapılandırılmamışsa sessizce devre dışı kalır.
var notifier = new Notifier(store);
app.Lifetime.ApplicationStopping.Register(() => notifier.Dispose());
app.Logger.LogInformation("Bildirim: e-posta={Mail}  SIEM/syslog={Syslog}  (webhook firma ayarından)",
    notifier.EmailConfigured ? "açık" : "yapılandırılmadı", notifier.SyslogConfigured ? "açık" : "yapılandırılmadı");

app.Logger.LogInformation("Argus Server hazır. Panel: http://localhost:5099   API: /api/v1");
app.Logger.LogInformation("Depolama: {Store}", string.IsNullOrWhiteSpace(pgConn) ? $"SQLite ({Path.Combine(dataDir, "argus.db")})" : "PostgreSQL");
app.Logger.LogInformation("Demo tenant: {Name}  (X-Tenant-Key: {Key})", demo.Name, demo.Key);

static Tenant? ResolveTenant(HttpContext c, IStore s)
    => c.Request.Headers.TryGetValue("X-Tenant-Key", out var k) ? s.GetTenantByKey(k.ToString()) : null;

static Agent? ResolveAgent(HttpContext c, IStore s)
    => c.Request.Headers.TryGetValue("X-Agent-Token", out var t) ? s.GetAgentByToken(t.ToString()) : null;

// Panel oturumu: X-Session başlığından (giriş jetonu) kullanıcı + tenant çözer.
// Panel veri endpoint'leri artık Tenant Key değil, GİRİŞ ister.
static (PanelUser user, Tenant tenant)? PanelAuth(HttpContext c, IStore s)
{
    if (!c.Request.Headers.TryGetValue("X-Session", out var tok)) return null;
    var sess = s.GetSession(tok.ToString());
    if (sess is null) return null;
    var user = s.GetUserById(sess.Value.tenantId, sess.Value.userId);
    if (user is null || !user.Active) return null;
    var tenant = s.GetTenantById(sess.Value.tenantId);
    return tenant is null ? null : (user, tenant);
}

// Departman-bazlı izleyici kapsamı: izleyici + departman doluysa o departmanın adı, yoksa null (kısıt yok).
static string? ViewerDept(PanelUser u)
    => (u.Role == Roles.Viewer && !string.IsNullOrWhiteSpace(u.Department)) ? u.Department : null;

// İzleyicinin görebileceği uç nokta id/makine kümesi (departmanına göre). null = kısıt yok.
static (HashSet<string> ids, HashSet<string> machines)? DeptScope(PanelUser u, IStore s)
{
    var dep = ViewerDept(u);
    if (dep is null) return null;
    var av = s.AgentsView(u.TenantId).Where(a => string.Equals(a.Department, dep, StringComparison.OrdinalIgnoreCase)).ToList();
    return (av.Select(a => a.AgentId).ToHashSet(StringComparer.OrdinalIgnoreCase),
            av.Select(a => a.Machine).ToHashSet(StringComparer.OrdinalIgnoreCase));
}

// Saha saat dilimi. Uç nokta olayları AGENT'ın yerel saatiyle (Türkiye) damgalanır; sunucu ise
// Ubuntu'da genelde UTC çalışır. Sınırları sunucunun yerel saatiyle kurarsak "bugün" filtresi kayar.
// ARGUS_TZ ile değiştirilebilir (IANA adı, ör. "Europe/Istanbul").
var fieldTz = ResolveFieldTz(Environment.GetEnvironmentVariable("ARGUS_TZ"));
app.Logger.LogInformation("Saha saat dilimi (rapor sınırları): {Tz}", fieldTz.Id);

// --- Veri saklama (retention) ---
// 300 makine × 15 sn telemetri → events tablosu sınırsız büyür; zamanla panel ve raporlar
// yavaşlar, disk dolar. Günde bir kez, firma politikasındaki süreden eski TELEMETRİYİ siler
// (firma/kullanıcı/lisans/ayar kayıtlarına DOKUNMAZ).
// Firma ayarı 0 ise ARGUS_RETENTION_DAYS (sunucu varsayılanı) uygulanır; o da yoksa silme yapılmaz.
var defaultRetention = int.TryParse(Environment.GetEnvironmentVariable("ARGUS_RETENTION_DAYS"), out var rd) ? rd : 0;
void RunRetention()
{
    try
    {
        foreach (var t in store.ListTenants())
        {
            var days = store.GetSettings(t.Id).RetentionDays;
            if (days <= 0) days = defaultRetention;
            if (days <= 0) continue;

            // Kesim noktası saha duvar saatiyle kurulur (olay damgaları da öyle tutuluyor).
            var cutoffWall = DateTime.UtcNow.Date.AddDays(-days);
            var cutoff = new DateTimeOffset(cutoffWall, fieldTz.GetUtcOffset(cutoffWall));
            var removed = store.PurgeOlderThan(t.Id, cutoff.UtcDateTime, cutoff.ToString("o"));
            if (removed > 0)
            {
                FileLog.Info($"Saklama: {t.Name} → {days} günden eski {removed} kayıt silindi.");
                app.Logger.LogInformation("Saklama temizliği: {Tenant} {Days} gün, {N} kayıt silindi", t.Name, days, removed);
            }
        }
    }
    catch (Exception ex) { FileLog.Error("retention", ex.Message); }
}
var retentionTimer = new System.Threading.Timer(_ => RunRetention(), null, TimeSpan.FromMinutes(5), TimeSpan.FromHours(24));
app.Lifetime.ApplicationStopping.Register(() => retentionTimer.Dispose());

static TimeZoneInfo ResolveFieldTz(string? id)
{
    foreach (var candidate in new[] { id, "Europe/Istanbul", "Turkey Standard Time" })
    {
        if (string.IsNullOrWhiteSpace(candidate)) continue;
        try { return TimeZoneInfo.FindSystemTimeZoneById(candidate); } catch { }
    }
    return TimeZoneInfo.Local;
}

// "2026-06-01" gibi tarihleri saha gün sınırlarına çevirir.
//  from/to  : olay tablolarının metin damgalarıyla karşılaştırmak için ISO metin (sözlüksel karşılaştırma)
//  fromUtc/toUtc : gerçek timestamp sütunları (alerts.received_at) için UTC
static (string from, string to, DateTime fromUtc, DateTime toUtc) ParseRange(string? from, string? to, TimeZoneInfo tz)
{
    var inv = System.Globalization.CultureInfo.InvariantCulture;
    var f = string.IsNullOrWhiteSpace(from) ? DateTime.UtcNow.Date.AddDays(-7) : DateTime.Parse(from, inv).Date;
    var t = string.IsNullOrWhiteSpace(to) ? DateTime.UtcNow.Date : DateTime.Parse(to, inv).Date;
    var fWall = f;                              // gün başı (saha duvar saati)
    var tWall = t.AddDays(1).AddTicks(-1);      // gün sonu
    var fOff = new DateTimeOffset(fWall, tz.GetUtcOffset(fWall));
    var tOff = new DateTimeOffset(tWall, tz.GetUtcOffset(tWall));
    return (fOff.ToString("o"), tOff.ToString("o"), fOff.UtcDateTime, tOff.UtcDateTime);
}

var v1 = app.MapGroup("/api/v1");

// Agent kaydı: tenant anahtarıyla gelir, agent token'ı alır.
v1.MapPost("/enroll", (HttpContext ctx, IStore s, EnrollRequest req) =>
{
    var t = ResolveTenant(ctx, s);
    if (t is null) return Results.Unauthorized();
    // Lisans: süre dolduysa yeni enroll'u reddet.
    if (t.ExpiresAt is not null && t.ExpiresAt < DateTime.UtcNow)
        return Results.Json(new { error = "lisans süresi doldu" }, statusCode: 403);
    // Lisans: koltuk limiti — yeni bir makine ise ve limit dolduysa reddet (mevcut makineler serbest).
    if (t.SeatLimit > 0)
    {
        var known = s.AgentsView(t.Id).Any(x => string.Equals(x.Machine, req.Machine, StringComparison.OrdinalIgnoreCase));
        if (!known && s.CountAgents(t.Id) >= t.SeatLimit)
            return Results.Json(new { error = "lisans koltuk limiti dolu" }, statusCode: 403);
    }
    var a = s.EnrollAgent(t.Id, req.Machine, req.User, req.Host);
    return Results.Ok(new { agentId = a.Id, agentToken = a.Token, tenantId = t.Id, tenantName = t.Name, settings = s.GetSettings(t.Id), command = a.Command });
});

// Telemetri: agent token'ı ile özet + uyarı + olay gönderir.
v1.MapPost("/telemetry", (HttpContext ctx, IStore s, TelemetryBatch batch) =>
{
    var a = ResolveAgent(ctx, s);
    if (a is null) return Results.Unauthorized();

    var sum = false; var al = 0; var ev = 0;
    if (batch.Summary is not null) { s.SaveSummary(a.TenantId, a.Id, batch.Summary); sum = true; }
    if (batch.Alerts is { Count: > 0 })
    {
        s.AddAlerts(a.TenantId, a.Id, batch.Alerts);
        al = batch.Alerts.Count;
        // Bildirim: kuyruğa atar, gönderimi ayrı iplik yapar → agent'ın telemetri isteği beklemez.
        notifier.Enqueue(a.TenantId, s.GetTenantById(a.TenantId)?.Name ?? a.TenantId, a.Machine, batch.Alerts);
    }
    if (batch.Events is { Count: > 0 }) { s.AddEvents(a.TenantId, a.Id, batch.Events); ev = batch.Events.Count; }
    if (batch.WebUsage is { Count: > 0 }) { s.AddWebUsage(a.TenantId, a.Id, a.Machine, a.User, batch.WebUsage); }
    if (batch.AppUsage is { Count: > 0 }) { s.AddAppUsage(a.TenantId, a.Id, a.Machine, a.User, batch.AppUsage); }
    if (batch.DocUsage is { Count: > 0 }) { s.AddDocUsage(a.TenantId, a.Id, a.Machine, a.User, batch.DocUsage); }

    return Results.Ok(new { accepted = new { summary = sum, alerts = al, events = ev }, settings = s.GetSettings(a.TenantId), command = a.Command });
});

// --- Kimlik doğrulama (panel girişi) ---
v1.MapPost("/auth/login", (HttpContext ctx, IStore s, LoginRequest req) =>
{
    // Kaba kuvvet koruması: kullanıcı+IP başına deneme sınırı.
    var key = (req.Username ?? "") + "|" + (ctx.Connection.RemoteIpAddress?.ToString() ?? "");
    var wait = LoginThrottle.RetryAfter(key);
    if (wait > 0) return Results.Json(new { error = $"Çok fazla hatalı deneme. {wait} sn sonra tekrar deneyin." }, statusCode: 429);

    // Multi-tenant: kullanıcı adından firma çözülür (kullanıcı adları global benzersiz).
    var user = s.GetUserByUsernameGlobal(req.Username ?? "");
    if (user is null || !user.Active || !Passwords.Verify(req.Password ?? "", user.PasswordHash))
    {
        LoginThrottle.OnFailure(key);
        FileLog.Audit("-", req.Username ?? "?", "giriş BAŞARISIZ", ctx.Connection.RemoteIpAddress?.ToString());
        return Results.Json(new { error = "Kullanıcı adı veya parola hatalı" }, statusCode: 401);
    }
    LoginThrottle.OnSuccess(key);
    var expires = req.Remember ? DateTime.UtcNow.AddDays(30) : DateTime.UtcNow.AddHours(12);
    var token = s.CreateSession(user.TenantId, user.Id, expires);
    s.UpdateLastLogin(user.TenantId, user.Id);
    var tenant = s.GetTenantById(user.TenantId);
    FileLog.Audit(tenant?.Name ?? user.TenantId, user.Username, "giriş yaptı");
    var tk = Roles.AtLeast(user.Role, Roles.Admin) ? tenant?.Key : null;
    return Results.Ok(new { token, role = user.Role, displayName = user.DisplayName, username = user.Username, mustChangePassword = user.MustChangePassword, tenantKey = tk });
});

// Parola değiştir (oturumlu). İlk giriş zorunlu değişimi ve normal değişim burada.
v1.MapPost("/auth/change-password", (HttpContext ctx, IStore s, ChangePasswordRequest req) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (!Passwords.Verify(req.CurrentPassword ?? "", a.Value.user.PasswordHash))
        return Results.Json(new { error = "Mevcut parola hatalı" }, statusCode: 400);
    var np = req.NewPassword ?? "";
    if (np.Length < 6)
        return Results.Json(new { error = "Yeni parola en az 6 karakter olmalı" }, statusCode: 400);
    s.SetPassword(a.Value.tenant.Id, a.Value.user.Id, Passwords.Hash(np));
    return Results.Ok(new { ok = true });
});

v1.MapPost("/auth/logout", (HttpContext ctx, IStore s) =>
{
    if (ctx.Request.Headers.TryGetValue("X-Session", out var tok)) s.DeleteSession(tok.ToString());
    return Results.Ok(new { ok = true });
});

v1.MapGet("/auth/me", (HttpContext ctx, IStore s) =>
{
    var auth = PanelAuth(ctx, s);
    if (auth is null) return Results.Unauthorized();
    // Tenant anahtarı yalnız yönetici+ (agent kurulumu için); izleyiciye verilmez.
    var key = Roles.AtLeast(auth.Value.user.Role, Roles.Admin) ? auth.Value.tenant.Key : null;
    return Results.Ok(new MeView(auth.Value.user.Username, auth.Value.user.Role, auth.Value.user.DisplayName, auth.Value.tenant.Name, key));
});

// --- Panel veri endpoint'leri (GİRİŞ ister) ---
v1.MapGet("/settings", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    return a is null ? Results.Unauthorized() : Results.Ok(s.GetSettings(a.Value.tenant.Id));
});

// Ayar kaydı: yönetici+ (viewer değiştiremez).
v1.MapPut("/settings", (HttpContext ctx, IStore s, TenantSettings body) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (!Roles.AtLeast(a.Value.user.Role, Roles.Admin)) return Results.Json(new { error = "yetkiniz yok" }, statusCode: 403);
    s.SaveSettings(a.Value.tenant.Id, body);
    FileLog.Audit(a.Value.tenant.Name, a.Value.user.Username, "politika/ayar değiştirildi");
    return Results.Ok(s.GetSettings(a.Value.tenant.Id));
});

v1.MapGet("/agents", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var list = s.AgentsView(a.Value.tenant.Id);
    var sc = DeptScope(a.Value.user, s);
    if (sc is not null) list = list.Where(x => sc.Value.ids.Contains(x.AgentId)).ToList();
    return Results.Ok(list);
});

// Uç noktaya departman ata (departman-bazlı izleyici için). Yönetici+ gerekir.
v1.MapPut("/agents/{id}/department", (HttpContext ctx, IStore s, string id, AgentDepartmentRequest req) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (!Roles.AtLeast(a.Value.user.Role, Roles.Admin)) return Results.Json(new { error = "yetkiniz yok" }, statusCode: 403);
    return s.SetAgentDepartment(a.Value.tenant.Id, id, req.Department ?? "")
        ? Results.Ok(new { id, department = req.Department ?? "" }) : Results.NotFound();
});

// Uzaktan komut (kill switch): agent'ı durdur/kaldır/aktif et. Yönetici+ gerekir.
v1.MapPut("/agents/{id}/command", (HttpContext ctx, IStore s, string id, AgentCommandRequest req) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (!Roles.AtLeast(a.Value.user.Role, Roles.Admin)) return Results.Json(new { error = "yetkiniz yok" }, statusCode: 403);
    var cmd = (req.Command ?? "").ToLowerInvariant();
    if (cmd is not ("active" or "disabled" or "remove")) return Results.BadRequest(new { error = "geçersiz komut" });
    if (!s.SetAgentCommand(a.Value.tenant.Id, id, cmd)) return Results.NotFound();
    FileLog.Audit(a.Value.tenant.Name, a.Value.user.Username, "agent komutu: " + cmd, id);
    return Results.Ok(new { id, command = cmd });
});

v1.MapGet("/agents/{id}/activity", (HttpContext ctx, IStore s, string id) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var sc = DeptScope(a.Value.user, s);
    if (sc is not null && !sc.Value.ids.Contains(id)) return Results.NotFound();   // izleyici başka departmanı açamaz
    var sum = s.LatestSummary(a.Value.tenant.Id, id);
    return sum is null ? Results.NotFound() : Results.Ok(sum);
});

v1.MapGet("/alerts", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var list = s.AlertsForTenant(a.Value.tenant.Id, 200);
    var sc = DeptScope(a.Value.user, s);
    if (sc is not null) list = list.Where(x => sc.Value.ids.Contains(x.AgentId)).Take(100).ToList();
    return Results.Ok(list);
});

v1.MapGet("/events", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var list = s.RecentEvents(a.Value.tenant.Id, 200);
    var sc = DeptScope(a.Value.user, s);
    if (sc is not null) list = list.Where(x => sc.Value.ids.Contains(x.AgentId)).ToList();
    return Results.Ok(list);
});

v1.MapGet("/web", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var list = s.WebActivity(a.Value.tenant.Id);
    var sc = DeptScope(a.Value.user, s);
    if (sc is not null) list = list.Where(x => sc.Value.machines.Contains(x.Machine)).ToList();
    return Results.Ok(list);
});

v1.MapGet("/apps", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    return a is null ? Results.Unauthorized() : Results.Ok(s.FleetApps(a.Value.tenant.Id, ViewerDept(a.Value.user)));
});

v1.MapGet("/appusage", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var list = s.AppLog(a.Value.tenant.Id);
    var sc = DeptScope(a.Value.user, s);
    if (sc is not null) list = list.Where(x => sc.Value.ids.Contains(x.AgentId)).ToList();
    return Results.Ok(list);
});

v1.MapGet("/report/web", (HttpContext ctx, IStore s, string? agent, string? from, string? to) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var (f, to2, _, _) = ParseRange(from, to, fieldTz);
    return Results.Ok(s.WebReport(a.Value.tenant.Id, string.IsNullOrWhiteSpace(agent) ? null : agent, f, to2, ViewerDept(a.Value.user)));
});

// Rapor: tarih aralığında (ve isteğe bağlı kişi bazında) uygulama başına toplam süre.
v1.MapGet("/report/app", (HttpContext ctx, IStore s, string? agent, string? from, string? to) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var (f, to2, _, _) = ParseRange(from, to, fieldTz);
    return Results.Ok(s.AppReport(a.Value.tenant.Id, string.IsNullOrWhiteSpace(agent) ? null : agent, f, to2, ViewerDept(a.Value.user)));
});

// Rapor: tarih aralığında (ve isteğe bağlı kişi bazında) belge başına süre (dosya-içi süre).
v1.MapGet("/report/doc", (HttpContext ctx, IStore s, string? agent, string? from, string? to) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var (f, to2, _, _) = ParseRange(from, to, fieldTz);
    return Results.Ok(s.DocReport(a.Value.tenant.Id, string.IsNullOrWhiteSpace(agent) ? null : agent, f, to2, ViewerDept(a.Value.user)));
});

// Rapor: tarih aralığında (ve isteğe bağlı kişi bazında) dosya olayları.
//   op        : tek işlem (delete/copy/usb_copy…) ya da "usb" (tüm USB olayları)
//   sensitive : yalnız hassas içerik tespit edilen olaylar
//   limit/offset : sayfalama — panel "daha fazla yükle" ile geçmişe iner (eski 500 tavanı kalktı)
v1.MapGet("/report/events", (HttpContext ctx, IStore s, string? agent, string? from, string? to,
                             string? op, bool? sensitive, int? limit, int? offset) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var (f, to2, _, _) = ParseRange(from, to, fieldTz);
    var take = Math.Clamp(limit ?? 500, 1, 5000);
    var list = s.EventsInRange(a.Value.tenant.Id, string.IsNullOrWhiteSpace(agent) ? null : agent, f, to2,
        take, string.IsNullOrWhiteSpace(op) ? null : op, sensitive == true, Math.Max(0, offset ?? 0));
    var sc = DeptScope(a.Value.user, s);
    if (sc is not null) list = list.Where(x => sc.Value.ids.Contains(x.AgentId)).ToList();
    return Results.Ok(list);
});

// Rapor: tarih aralığında (ve isteğe bağlı kişi/tip/önem) UYARI GEÇMİŞİ.
// Canlı /alerts yalnız son 200 kaydı verir — geriye dönük bakış bu uçtan yapılır.
v1.MapGet("/report/alerts", (HttpContext ctx, IStore s, string? agent, string? from, string? to,
                             string? type, string? severity, int? limit, int? offset) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    var (_, _, fUtc, tUtc) = ParseRange(from, to, fieldTz);
    var take = Math.Clamp(limit ?? 300, 1, 2000);
    var list = s.AlertsInRange(a.Value.tenant.Id, string.IsNullOrWhiteSpace(agent) ? null : agent, fUtc, tUtc,
        string.IsNullOrWhiteSpace(type) ? null : type,
        string.IsNullOrWhiteSpace(severity) ? null : severity,
        take, Math.Max(0, offset ?? 0));
    var sc = DeptScope(a.Value.user, s);
    if (sc is not null) list = list.Where(x => sc.Value.ids.Contains(x.AgentId)).ToList();
    return Results.Ok(list);
});

// Patron / yönetici özeti (executive overview).
v1.MapGet("/overview", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    return a is null ? Results.Unauthorized() : Results.Ok(s.BuildOverview(a.Value.tenant.Id, ViewerDept(a.Value.user)));
});

// --- Kullanıcı yönetimi (yalnız Patron/owner) ---
v1.MapGet("/users", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (!Roles.AtLeast(a.Value.user.Role, Roles.Owner)) return Results.Json(new { error = "yetkiniz yok" }, statusCode: 403);
    return Results.Ok(s.ListUsers(a.Value.tenant.Id));
});

v1.MapPost("/users", (HttpContext ctx, IStore s, CreateUserRequest req) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (!Roles.AtLeast(a.Value.user.Role, Roles.Owner)) return Results.Json(new { error = "yetkiniz yok" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.Username) || string.IsNullOrWhiteSpace(req.Password))
        return Results.BadRequest(new { error = "kullanıcı adı ve parola gerekli" });
    if (!Roles.IsValid(req.Role)) return Results.BadRequest(new { error = "geçersiz rol" });
    if (s.GetUserByUsernameGlobal(req.Username) is not null)
        return Results.BadRequest(new { error = "bu kullanıcı adı zaten var" });
    var u = s.CreateUser(a.Value.tenant.Id, req.Username, Passwords.Hash(req.Password), req.Role, req.DisplayName ?? req.Username, req.Department ?? "");
    FileLog.Audit(a.Value.tenant.Name, a.Value.user.Username, "kullanıcı ekledi: " + u.Username + " (" + u.Role + ")");
    return Results.Ok(new UserView(u.Id, u.Username, u.Role, u.DisplayName, u.Active, u.Department, u.CreatedAt, u.LastLogin));
});

v1.MapPut("/users/{id}", (HttpContext ctx, IStore s, string id, UpdateUserRequest req) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (!Roles.AtLeast(a.Value.user.Role, Roles.Owner)) return Results.Json(new { error = "yetkiniz yok" }, statusCode: 403);
    if (req.Role is not null && !Roles.IsValid(req.Role)) return Results.BadRequest(new { error = "geçersiz rol" });
    // Patron kendini kilitlemesin: kendi rolünü/aktifliğini düşüremez.
    if (id == a.Value.user.Id && (req.Active == false || (req.Role is not null && req.Role != Roles.Owner)))
        return Results.BadRequest(new { error = "kendi patron yetkinizi kaldıramazsınız" });
    var ph = string.IsNullOrWhiteSpace(req.Password) ? null : Passwords.Hash(req.Password);
    return s.UpdateUser(a.Value.tenant.Id, id, req.Role, req.DisplayName, req.Active, ph, req.Department)
        ? Results.Ok(new { ok = true }) : Results.NotFound();
});

v1.MapDelete("/users/{id}", (HttpContext ctx, IStore s, string id) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (!Roles.AtLeast(a.Value.user.Role, Roles.Owner)) return Results.Json(new { error = "yetkiniz yok" }, statusCode: 403);
    if (id == a.Value.user.Id) return Results.BadRequest(new { error = "kendinizi silemezsiniz" });
    if (!s.DeleteUser(a.Value.tenant.Id, id)) return Results.NotFound();
    FileLog.Audit(a.Value.tenant.Name, a.Value.user.Username, "kullanıcı sildi", id);
    return Results.Ok(new { ok = true });
});

// --- Firma (tenant) yönetimi — yalnız süper-admin (satıcı) ---
v1.MapGet("/tenants", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (a.Value.user.Role != Roles.Superadmin) return Results.Json(new { error = "yalnız platform yöneticisi" }, statusCode: 403);
    var list = s.ListTenants()
        .Select(t => new TenantView(t.Id, t.Name, t.Key, t.CreatedAt, s.CountAgents(t.Id), s.CountUsers(t.Id),
            t.SeatLimit, t.ExpiresAt, t.ExpiresAt is not null && t.ExpiresAt < DateTime.UtcNow))
        .ToList();
    return Results.Ok(list);
});

v1.MapPost("/tenants", (HttpContext ctx, IStore s, CreateTenantRequest req) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (a.Value.user.Role != Roles.Superadmin) return Results.Json(new { error = "yalnız platform yöneticisi" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.Name) || string.IsNullOrWhiteSpace(req.OwnerUsername) || string.IsNullOrWhiteSpace(req.OwnerPassword))
        return Results.BadRequest(new { error = "firma adı, yönetici kullanıcı adı ve parola gerekli" });
    if (s.GetUserByUsernameGlobal(req.OwnerUsername) is not null)
        return Results.BadRequest(new { error = "bu kullanıcı adı zaten var" });
    var key = "tk_" + Passwords.NewToken();                 // rastgele, tahmin edilemez firma anahtarı
    var t = s.SeedTenant(req.Name, key);
    s.CreateUser(t.Id, req.OwnerUsername, Passwords.Hash(req.OwnerPassword), Roles.Owner, req.OwnerDisplayName ?? req.OwnerUsername);
    FileLog.Audit("PLATFORM", a.Value.user.Username, "firma oluşturdu: " + t.Name, "owner=" + req.OwnerUsername);
    app.Logger.LogInformation("Yeni firma: {Name}  (anahtar: {Key})", t.Name, t.Key);
    return Results.Ok(new { tenantId = t.Id, name = t.Name, key = t.Key, ownerUsername = req.OwnerUsername });
});

v1.MapPut("/tenants/{id}", (HttpContext ctx, IStore s, string id, CreateTenantRequest req) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (a.Value.user.Role != Roles.Superadmin) return Results.Json(new { error = "yalnız platform yöneticisi" }, statusCode: 403);
    if (string.IsNullOrWhiteSpace(req.Name)) return Results.BadRequest(new { error = "firma adı gerekli" });
    return s.RenameTenant(id, req.Name) ? Results.Ok(new { ok = true }) : Results.NotFound();
});

// Lisans ata/güncelle (yalnız süper-admin): koltuk limiti + bitiş tarihi.
v1.MapPut("/tenants/{id}/license", (HttpContext ctx, IStore s, string id, LicenseRequest req) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (a.Value.user.Role != Roles.Superadmin) return Results.Json(new { error = "yalnız platform yöneticisi" }, statusCode: 403);
    DateTime? exp = null;
    if (!string.IsNullOrWhiteSpace(req.ExpiresAt) &&
        DateTime.TryParse(req.ExpiresAt, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
        exp = DateTime.SpecifyKind(d.Date.AddDays(1).AddTicks(-1), DateTimeKind.Utc);   // gün sonuna kadar geçerli
    if (!s.SetTenantLicense(id, Math.Max(0, req.SeatLimit), exp)) return Results.NotFound();
    FileLog.Audit("PLATFORM", a.Value.user.Username, "lisans güncelledi", $"{id} koltuk={req.SeatLimit} bitiş={req.ExpiresAt}");
    return Results.Ok(new { ok = true });
});

v1.MapDelete("/tenants/{id}", (HttpContext ctx, IStore s, string id) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (a.Value.user.Role != Roles.Superadmin) return Results.Json(new { error = "yalnız platform yöneticisi" }, statusCode: 403);
    if (id == a.Value.tenant.Id) return Results.BadRequest(new { error = "kendi firmanızı silemezsiniz" });
    if (!s.DeleteTenant(id)) return Results.NotFound();
    FileLog.Audit("PLATFORM", a.Value.user.Username, "firma SİLDİ", id);
    return Results.Ok(new { ok = true });
});

// Sistem günlüğü: denetim + hata + yedek durumu (yalnız süper-admin).
v1.MapGet("/audit", (HttpContext ctx, IStore s) =>
{
    var a = PanelAuth(ctx, s);
    if (a is null) return Results.Unauthorized();
    if (a.Value.user.Role != Roles.Superadmin) return Results.Json(new { error = "yalnız platform yöneticisi" }, statusCode: 403);
    var backupDir = Path.Combine(dataDir, "backups");
    var backups = Directory.Exists(backupDir)
        ? new DirectoryInfo(backupDir).GetFiles("argus-*.db").OrderByDescending(f => f.Name).Take(20)
            .Select(f => new { name = f.Name, sizeKb = f.Length / 1024, at = f.LastWriteTime }).ToList<object>()
        : new List<object>();
    return Results.Ok(new { audit = FileLog.Tail("audit", 200), errors = FileLog.Tail("error", 100), backups });
});

// --- Tarayıcı uzantısı barındırma (self-hosted force-install) ---
// Chrome/Edge GPO policy'si /ext/update.xml'i yoklar, oradaki CRX'i zorunlu kurar. Web Store gerekmez.
var extHostDir = Path.Combine(AppContext.BaseDirectory, "wwwroot", "ext");
app.MapGet("/ext/update.xml", (HttpContext ctx) =>
{
    var idFile = Path.Combine(extHostDir, "extid.txt");
    var crxFile = Path.Combine(extHostDir, "argus-ext.crx");
    if (!File.Exists(idFile) || !File.Exists(crxFile)) return Results.NotFound();
    var id = File.ReadAllText(idFile).Trim();
    var verFile = Path.Combine(extHostDir, "extver.txt");
    var ver = File.Exists(verFile) ? File.ReadAllText(verFile).Trim() : "1.0.0";
    var codebase = $"{ctx.Request.Scheme}://{ctx.Request.Host}/ext/argus-ext.crx";
    var xml = "<?xml version='1.0' encoding='UTF-8'?>\n" +
              "<gupdate xmlns='http://www.google.com/update2/response' protocol='2.0'>\n" +
              $"  <app appid='{id}'>\n    <updatecheck codebase='{codebase}' version='{ver}' />\n  </app>\n</gupdate>";
    return Results.Content(xml, "application/xml");
});
app.MapGet("/ext/argus-ext.crx", () =>
{
    var crx = Path.Combine(extHostDir, "argus-ext.crx");
    return File.Exists(crx) ? Results.File(crx, "application/x-chrome-extension", "argus-ext.crx") : Results.NotFound();
});
app.MapGet("/ext/argus-ext.xpi", () =>
{
    var xpi = Path.Combine(extHostDir, "argus-ext.xpi");
    return File.Exists(xpi) ? Results.File(xpi, "application/x-xpinstall", "argus-ext.xpi") : Results.NotFound();
});

app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "argus-server" }));

app.Run();
