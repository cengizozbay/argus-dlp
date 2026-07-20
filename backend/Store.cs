// Depolama sözleşmesi (IStore). Uygulamalar: SqliteStore (varsayılan), PostgresStore (üretim).

namespace Argus.Server;

public interface IStore
{
    Tenant SeedTenant(string name, string key);
    Tenant? GetTenantByKey(string key);
    Tenant? GetTenantById(string id);
    IReadOnlyList<Tenant> ListTenants();
    bool RenameTenant(string id, string name);
    bool DeleteTenant(string id);   // firmanın TÜM verisini siler (agent/olay/uyarı/kullanıcı…)
    bool SetTenantLicense(string id, int seatLimit, DateTime? expiresAt);
    int CountAgents(string tenantId);
    Agent EnrollAgent(string tenantId, string machine, string user, string host);
    Agent? GetAgentByToken(string token);
    bool SetAgentCommand(string tenantId, string agentId, string command);   // active | disabled | remove
    bool SetAgentDepartment(string tenantId, string agentId, string department);

    void SaveSummary(string tenantId, string agentId, SummaryDto summary);
    void AddAlerts(string tenantId, string agentId, IEnumerable<AlertDto> alerts);
    void AddEvents(string tenantId, string agentId, IEnumerable<EventDto> events);

    SummaryDto? LatestSummary(string tenantId, string agentId);
    IReadOnlyList<AgentView> AgentsView(string tenantId);
    IReadOnlyList<TenantAlert> AlertsForTenant(string tenantId, int limit);
    IReadOnlyList<TenantEvent> RecentEvents(string tenantId, int limit);
    IReadOnlyList<WebActivityRow> WebActivity(string tenantId);
    IReadOnlyList<AppUsageRow> FleetApps(string tenantId, string? department = null);
    IReadOnlyList<AppLogRow> AppLog(string tenantId);

    void AddWebUsage(string tenantId, string agentId, string machine, string user, IEnumerable<WebUsageDto> usage);
    IReadOnlyList<WebReportRow> WebReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null);

    void AddAppUsage(string tenantId, string agentId, string machine, string user, IEnumerable<AppUsageDto> usage);
    IReadOnlyList<AppReportRow> AppReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null);

    void AddDocUsage(string tenantId, string agentId, string machine, string user, IEnumerable<DocUsageDto> usage);
    IReadOnlyList<DocReportRow> DocReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null);
    IReadOnlyList<TenantEvent> EventsInRange(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, int limit);

    TenantSettings GetSettings(string tenantId);
    void SaveSettings(string tenantId, TenantSettings settings);

    // --- Panel kullanıcıları + oturumlar (RBAC) ---
    int CountUsers(string tenantId);
    PanelUser? GetUser(string tenantId, string username);
    PanelUser? GetUserByUsernameGlobal(string username);   // multi-tenant giriş: tüm firmalarda ara
    PanelUser? GetUserById(string tenantId, string id);
    IReadOnlyList<UserView> ListUsers(string tenantId);
    PanelUser CreateUser(string tenantId, string username, string passwordHash, string role, string displayName, string department = "");
    bool UpdateUser(string tenantId, string id, string? role, string? displayName, bool? active, string? passwordHash, string? department = null);
    bool DeleteUser(string tenantId, string id);
    void UpdateLastLogin(string tenantId, string userId);
    void SetPassword(string tenantId, string userId, string passwordHash);   // parola + must_change=0

    string CreateSession(string tenantId, string userId, DateTime expiresUtc);
    (string userId, string tenantId)? GetSession(string token);   // süresi dolmuşsa null
    void DeleteSession(string token);

    // --- Patron/yönetici özeti ---
    Overview BuildOverview(string tenantId, string? department = null);
}
