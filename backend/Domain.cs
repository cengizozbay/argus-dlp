// Argus Server — alan modelleri ve DTO'lar.
// Agent'ın diske yazdığı JSON şekilleriyle (SummaryDto/AlertDto/EventDto) birebir uyumlu.

namespace Argus.Server;

// --- Çekirdek varlıklar ---
public sealed class Tenant
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Key { get; set; } = "";
    public DateTime CreatedAt { get; set; }
    public int SeatLimit { get; set; }              // 0 = sınırsız; aksi halde en fazla bu kadar agent
    public DateTime? ExpiresAt { get; set; }         // null = süresiz; aksi halde lisans bitiş tarihi
}

public record LicenseRequest(int SeatLimit, string? ExpiresAt);   // ExpiresAt "yyyy-MM-dd" ya da boş

public sealed class Agent
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Token { get; set; } = "";
    public string Machine { get; set; } = "";
    public string User { get; set; } = "";
    public string Host { get; set; } = "";
    public DateTime EnrolledAt { get; set; }
    public DateTime LastSeen { get; set; }
    public string Command { get; set; } = "active";   // active | disabled | remove (panelden uzaktan)
    public string Department { get; set; } = "";       // panelden atanır; departman-bazlı izleyici için
}

// --- Panel kullanıcıları (admin paneline giren kişiler; izlenen personelden AYRI) ---
public sealed class PanelUser
{
    public string Id { get; set; } = "";
    public string TenantId { get; set; } = "";
    public string Username { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Role { get; set; } = Roles.Viewer;   // owner | admin | viewer
    public string DisplayName { get; set; } = "";
    public bool Active { get; set; } = true;
    public bool MustChangePassword { get; set; } = true;   // ilk girişte parola değiştirme zorunlu
    public string Department { get; set; } = "";           // izleyici için: boş = tüm firma, dolu = sadece o departman
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLogin { get; set; }
}

public record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public record LoginRequest(string Username, string Password, bool Remember = false);
// Firma (tenant) yönetimi — yalnız süper-admin
public record TenantView(string Id, string Name, string Key, DateTime CreatedAt, int Agents, int Users,
    int SeatLimit, DateTime? ExpiresAt, bool Expired);
public record CreateTenantRequest(string Name, string OwnerUsername, string OwnerPassword, string? OwnerDisplayName);
public record CreateUserRequest(string Username, string Password, string Role, string DisplayName, string? Department);
public record UpdateUserRequest(string? Role, string? DisplayName, bool? Active, string? Password, string? Department);
// Parola/hash panele ASLA dönmez.
public record UserView(string Id, string Username, string Role, string DisplayName, bool Active, string Department, DateTime CreatedAt, DateTime? LastLogin);
public record MeView(string Username, string Role, string DisplayName, string TenantName, string? TenantKey);

// --- İstek gövdeleri ---
public record EnrollRequest(string Machine, string User, string Host);

// --- Tenant politika/config ayarları (panelden düzenlenir, agent'a enroll/telemetri yanıtında iner) ---
public sealed class TenantSettings
{
    // Uyarı eşikleri (agent AlertEngine'ine geçer)
    public int DeleteThreshold { get; set; } = 10;   // kayan pencerede N silme → uyarı
    public int CopyThreshold { get; set; } = 15;     // N kopyalama → uyarı
    public int UsbThreshold { get; set; } = 3;       // USB'ye N kopyalama → sızıntı uyarısı
    public int SensitiveThreshold { get; set; } = 1; // N hassas dosya sızıntısı → uyarı (1 = ilk dosyada)
    public int WindowSeconds { get; set; } = 30;     // kayan pencere
    public int CooldownSeconds { get; set; } = 60;   // aynı uyarı için bekleme

    // Agent davranışı
    public int SendSeconds { get; set; } = 15;           // telemetri gönderim aralığı
    public int IdleThresholdSeconds { get; set; } = 60;  // bu kadar giriş yoksa "boşta"
    public string FileMonitor { get; set; } = "auto";    // auto | usn | fsw (dosya izleme motoru)
    public bool UsbMonitoring { get; set; } = true;      // USB/harici disk izleme açık mı
    public bool ContentScan { get; set; } = true;        // dosya içeriği hassas veri taraması (TC/IBAN/kart)
    public List<string> WatchFolders { get; set; } = new(); // boşsa agent varsayılanı (Masaüstü+Belgeler)
    public List<string> SensitiveKeywords { get; set; } = new(); // içerikte aranacak ek anahtar kelimeler

    public string UpdatedAt { get; set; } = "";
}

// --- Agent telemetrisi (agent'ın JSON çıktısıyla aynı alanlar) ---
public record AppDto(string App, string Exe, long ActiveSeconds, double SharePercent);

public record SummaryDto(
    string Machine, string User, DateTime SessionStart, DateTime GeneratedAt,
    long TotalActiveSeconds, long TotalIdleSeconds,
    long FileCreates, long FileModifies, long FileDeletes, long FileRenames, long FileCopies, long AlertCount,
    List<AppDto> Apps, List<WebDto>? Web);

public record WebDto(string Domain, string? Url, string Title, long Seconds, bool Incognito);

public record AlertDto(
    string Ts, string Severity, string Rule, string Type, string Title, string Detail,
    int Count, int WindowSeconds, List<string> Samples, string Machine, string User);

public record EventDto(
    string Ts, string? Type, string? Op, string? Exe, string? App, string? Title, string? Path, string? Sensitivity = null, string? Source = null);

public record WebUsageDto(string Site, string? Domain, string? Url, string? Title, long Seconds, bool Incognito, string Ts);

// Zaman-serisi uygulama kullanımı (web_usage gibi; agent aralık deltası + zaman damgası gönderir)
public record AppUsageDto(string App, string Exe, long Seconds, string Ts);
public record AppReportRow(string App, string Exe, long Seconds, string? LastTs);

// Dosya-içi süre (belge başına)
public record DocUsageDto(string Name, string App, long Seconds, string Ts);
public record DocReportRow(string Name, string App, long Seconds, string? LastTs);

public record TelemetryBatch(SummaryDto? Summary, List<AlertDto>? Alerts, List<EventDto>? Events, List<WebUsageDto>? WebUsage, List<AppUsageDto>? AppUsage, List<DocUsageDto>? DocUsage);

// Raporlama: tarih aralığında site başına toplam süre + son ziyaret zamanı
public record WebReportRow(string Site, string? Domain, string? Url, string? Title, long Seconds, bool Incognito, string? LastTs);

// --- Panele dönen görünümler ---
public record AgentView(
    string AgentId, string Machine, string User, DateTime LastSeen, bool Online,
    long ActiveSeconds, long IdleSeconds, long FileDeletes, long FileCopies, long AlertCount,
    string Command, string Department);

public record AgentCommandRequest(string Command);   // active | disabled | remove
public record AgentDepartmentRequest(string Department);

public record TenantAlert(string AgentId, string Machine, DateTime ReceivedAt, AlertDto Alert);

// Dosya olayı akışı (Dosya Olayları ekranı) ve web aktivitesi (Web Aktivitesi ekranı)
public record TenantEvent(string AgentId, string Machine, string Ts, string Op, string? Path, string? Sensitivity = null, string? Source = null);
public record WebActivityRow(string Machine, string User, string Domain, string? Url, string Title, long Seconds, bool Incognito);
public record AppUsageRow(string App, string Exe, long Seconds);
public record AppLogRow(string AgentId, string Machine, string User, string App, string Exe, long Seconds);

// --- Patron / yönetici özeti (executive overview) ---
public record OverviewRisk(string AgentId, string Machine, string User, int Score, long SensitiveFiles, long UsbCopies, long Alerts);
public record OverviewPerson(string AgentId, string Machine, string User, long ActiveSeconds, long IdleSeconds, int ActivePercent);
public record Overview(
    int Endpoints, int Online, int Offline,
    long TotalActiveSeconds, long TotalIdleSeconds, int WorkforceActivePercent,
    int SensitiveExfil, int UsbExfil, int MassDelete, int MassCopy, int OpenAlerts,
    long SensitiveFiles, long UsbCopies,
    List<OverviewRisk> TopRisk, List<OverviewPerson> MostActive, List<OverviewPerson> LeastActive);
