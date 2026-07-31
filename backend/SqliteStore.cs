// SQLite depolama — IStore'un tek dosyalık uygulaması. Kurulum/servis gerektirmez.
// Varsayılan depolama budur; PostgreSQL'e geçiş için ARGUS_PG verilir.
// Tek bağlantı + kilit ile eşzamanlı erişim güvenli; ağır alanlar (summary/alert) JSON metni.

using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Argus.Server;

public sealed class SqliteStore : IStore, IDisposable
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);
    private readonly SqliteConnection _conn;
    private readonly object _gate = new();

    public SqliteStore(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath}");
        _conn.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;");
        InitSchema();
        Migrate();
    }

    // Var olan veritabanlarını yeni sütunlarla uyumlu tut (yeni kurulumda InitSchema zaten ekler).
    private void Migrate()
    {
        if (!ColumnExists("events", "sensitivity"))
            Exec("ALTER TABLE events ADD COLUMN sensitivity TEXT");
        if (!ColumnExists("agents", "command"))
            Exec("ALTER TABLE agents ADD COLUMN command TEXT DEFAULT 'active'");
        if (!ColumnExists("events", "src"))
            Exec("ALTER TABLE events ADD COLUMN src TEXT");
        if (!ColumnExists("events", "user_name"))
            Exec("ALTER TABLE events ADD COLUMN user_name TEXT");   // fileserver denetimi: olayı YAPAN kullanıcı
        if (!ColumnExists("doc_usage", "path"))
            Exec("ALTER TABLE doc_usage ADD COLUMN path TEXT");     // belgenin tam yolu (konum)
        if (!ColumnExists("web_usage", "title"))
            Exec("ALTER TABLE web_usage ADD COLUMN title TEXT");
        if (!ColumnExists("panel_users", "must_change"))
            Exec("ALTER TABLE panel_users ADD COLUMN must_change INTEGER DEFAULT 0");
        if (!ColumnExists("agents", "department"))
            Exec("ALTER TABLE agents ADD COLUMN department TEXT DEFAULT ''");
        if (!ColumnExists("panel_users", "department"))
            Exec("ALTER TABLE panel_users ADD COLUMN department TEXT DEFAULT ''");
        if (!ColumnExists("tenants", "seat_limit"))
            Exec("ALTER TABLE tenants ADD COLUMN seat_limit INTEGER DEFAULT 0");
        if (!ColumnExists("tenants", "expires_at"))
            Exec("ALTER TABLE tenants ADD COLUMN expires_at TEXT");
    }

    private static Tenant ReadTenant(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0), Name = r.GetString(1), Key = r.GetString(2), CreatedAt = ParseUtc(r.GetString(3)),
        SeatLimit = r.FieldCount > 4 && !r.IsDBNull(4) ? (int)r.GetInt64(4) : 0,
        ExpiresAt = r.FieldCount > 5 && !r.IsDBNull(5) ? ParseUtc(r.GetString(5)) : null
    };
    private const string TenantCols = "id,name,key,created_at,seat_limit,expires_at";

    private bool ColumnExists(string table, string column)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"PRAGMA table_info({table})";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            if (string.Equals(r.GetString(1), column, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private void InitSchema() => Exec(@"
CREATE TABLE IF NOT EXISTS tenants(id TEXT PRIMARY KEY, name TEXT, key TEXT UNIQUE, created_at TEXT);
CREATE TABLE IF NOT EXISTS agents(id TEXT PRIMARY KEY, tenant_id TEXT, token TEXT UNIQUE,
  machine TEXT, user_name TEXT, host TEXT, enrolled_at TEXT, last_seen TEXT, command TEXT DEFAULT 'active');
CREATE INDEX IF NOT EXISTS ix_agents_tenant ON agents(tenant_id);
CREATE TABLE IF NOT EXISTS summaries(tenant_id TEXT, agent_id TEXT, data TEXT, updated_at TEXT,
  PRIMARY KEY(tenant_id, agent_id));
CREATE TABLE IF NOT EXISTS alerts(id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT, agent_id TEXT,
  machine TEXT, received_at TEXT, data TEXT);
CREATE INDEX IF NOT EXISTS ix_alerts_tenant ON alerts(tenant_id, received_at);
CREATE TABLE IF NOT EXISTS events(id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT, agent_id TEXT,
  machine TEXT, ts TEXT, op TEXT, path TEXT, sensitivity TEXT);
CREATE INDEX IF NOT EXISTS ix_events_tenant ON events(tenant_id, id);
-- Geriye dönük (tarih aralıklı) olay sorgusu tam tarama yapmasın.
CREATE INDEX IF NOT EXISTS ix_events_ts ON events(tenant_id, ts);
CREATE TABLE IF NOT EXISTS web_usage(id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT, agent_id TEXT,
  machine TEXT, user_name TEXT, site TEXT, domain TEXT, url TEXT, seconds INTEGER, incognito INTEGER, ts TEXT);
CREATE INDEX IF NOT EXISTS ix_webusage ON web_usage(tenant_id, agent_id, ts);
CREATE TABLE IF NOT EXISTS app_usage(id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT, agent_id TEXT,
  machine TEXT, user_name TEXT, app TEXT, exe TEXT, seconds INTEGER, ts TEXT);
CREATE INDEX IF NOT EXISTS ix_appusage ON app_usage(tenant_id, agent_id, ts);
CREATE TABLE IF NOT EXISTS doc_usage(id INTEGER PRIMARY KEY AUTOINCREMENT, tenant_id TEXT, agent_id TEXT,
  machine TEXT, user_name TEXT, name TEXT, app TEXT, seconds INTEGER, ts TEXT);
CREATE INDEX IF NOT EXISTS ix_docusage ON doc_usage(tenant_id, agent_id, ts);
CREATE TABLE IF NOT EXISTS settings(tenant_id TEXT PRIMARY KEY, data TEXT, updated_at TEXT);
CREATE TABLE IF NOT EXISTS panel_users(id TEXT PRIMARY KEY, tenant_id TEXT, username TEXT, password_hash TEXT,
  role TEXT, display_name TEXT, active INTEGER, created_at TEXT, last_login TEXT);
CREATE UNIQUE INDEX IF NOT EXISTS ix_users_uniq ON panel_users(tenant_id, username);
CREATE TABLE IF NOT EXISTS sessions(token TEXT PRIMARY KEY, tenant_id TEXT, user_id TEXT, created_at TEXT, expires_at TEXT);
CREATE INDEX IF NOT EXISTS ix_sessions_exp ON sessions(expires_at);");

    // --- yardımcılar ---
    private void Exec(string sql, params (string, object?)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private static string Iso(DateTime d) => d.ToUniversalTime().ToString("o");
    private static DateTime ParseUtc(string s) => DateTime.Parse(s, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind).ToUniversalTime();

    public Tenant SeedTenant(string name, string key)
    {
        lock (_gate)
        {
            var existing = GetTenantByKeyNoLock(key);
            if (existing is not null) return existing;
            var t = new Tenant { Id = "ten_" + Guid.NewGuid().ToString("N")[..10], Name = name, Key = key, CreatedAt = DateTime.UtcNow };
            Exec("INSERT OR IGNORE INTO tenants(id,name,key,created_at) VALUES(@id,@n,@k,@c)",
                ("@id", t.Id), ("@n", t.Name), ("@k", t.Key), ("@c", Iso(t.CreatedAt)));
            return GetTenantByKeyNoLock(key) ?? t;
        }
    }

    public Tenant? GetTenantByKey(string key) { lock (_gate) return GetTenantByKeyNoLock(key); }

    public Tenant? GetTenantById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {TenantCols} FROM tenants WHERE id=@id";
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return ReadTenant(r);
        }
    }

    private Tenant? GetTenantByKeyNoLock(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT {TenantCols} FROM tenants WHERE key=@k";
        cmd.Parameters.AddWithValue("@k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadTenant(r);
    }

    public IReadOnlyList<Tenant> ListTenants()
    {
        lock (_gate)
        {
            var list = new List<Tenant>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {TenantCols} FROM tenants ORDER BY created_at";
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(ReadTenant(r));
            return list;
        }
    }

    public bool RenameTenant(string id, string name)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE tenants SET name=@n WHERE id=@id";
            cmd.Parameters.AddWithValue("@n", name);
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool SetTenantLicense(string id, int seatLimit, DateTime? expiresAt)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE tenants SET seat_limit=@s, expires_at=@e WHERE id=@id";
            cmd.Parameters.AddWithValue("@s", seatLimit);
            cmd.Parameters.AddWithValue("@e", expiresAt is null ? DBNull.Value : Iso(expiresAt.Value));
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public int CountAgents(string tenantId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM agents WHERE tenant_id=@t";
            cmd.Parameters.AddWithValue("@t", tenantId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    public bool DeleteTenant(string id)
    {
        lock (_gate)
        {
            var tables = new[] { "agents", "summaries", "alerts", "events", "web_usage", "app_usage", "settings", "panel_users" };
            using var tx = _conn.BeginTransaction();
            foreach (var t in tables)
                using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = $"DELETE FROM {t} WHERE tenant_id=@t"; c.Parameters.AddWithValue("@t", id); c.ExecuteNonQuery(); }
            int n;
            using (var c = _conn.CreateCommand()) { c.Transaction = tx; c.CommandText = "DELETE FROM tenants WHERE id=@t"; c.Parameters.AddWithValue("@t", id); n = c.ExecuteNonQuery(); }
            tx.Commit();
            return n > 0;
        }
    }

    public Agent EnrollAgent(string tenantId, string machine, string user, string host)
    {
        lock (_gate)
        {
            using (var find = _conn.CreateCommand())
            {
                find.CommandText = "SELECT id,token,enrolled_at,command FROM agents WHERE tenant_id=@t AND lower(machine)=lower(@m)";
                find.Parameters.AddWithValue("@t", tenantId);
                find.Parameters.AddWithValue("@m", machine);
                using var r = find.ExecuteReader();
                if (r.Read())
                {
                    var cmd = r.IsDBNull(3) ? "active" : r.GetString(3);
                    if (cmd == "remove") cmd = "active";   // yeniden kurulum: bekleyen 'kaldır'ı miras alma
                    var a = new Agent
                    {
                        Id = r.GetString(0), TenantId = tenantId, Token = r.GetString(1),
                        Machine = machine, User = user, Host = host,
                        EnrolledAt = ParseUtc(r.GetString(2)), LastSeen = DateTime.UtcNow,
                        Command = cmd
                    };
                    r.Close();
                    Exec("UPDATE agents SET user_name=@u,host=@h,last_seen=@ls,command=@c WHERE id=@id",
                        ("@u", user), ("@h", host), ("@ls", Iso(a.LastSeen)), ("@c", cmd), ("@id", a.Id));
                    return a;
                }
            }

            var agent = new Agent
            {
                Id = "agt_" + Guid.NewGuid().ToString("N")[..10],
                TenantId = tenantId, Token = "tok_" + Guid.NewGuid().ToString("N"),
                Machine = machine, User = user, Host = host,
                EnrolledAt = DateTime.UtcNow, LastSeen = DateTime.UtcNow
            };
            Exec("INSERT INTO agents(id,tenant_id,token,machine,user_name,host,enrolled_at,last_seen) " +
                 "VALUES(@id,@t,@tok,@m,@u,@h,@e,@ls)",
                ("@id", agent.Id), ("@t", agent.TenantId), ("@tok", agent.Token), ("@m", agent.Machine),
                ("@u", agent.User), ("@h", agent.Host), ("@e", Iso(agent.EnrolledAt)), ("@ls", Iso(agent.LastSeen)));
            return agent;
        }
    }

    public Agent? GetAgentByToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT id,tenant_id,token,machine,user_name,host,enrolled_at,last_seen,command FROM agents WHERE token=@tok";
            cmd.Parameters.AddWithValue("@tok", token);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            return new Agent
            {
                Id = r.GetString(0), TenantId = r.GetString(1), Token = r.GetString(2),
                Machine = r.GetString(3), User = r.IsDBNull(4) ? "" : r.GetString(4),
                Host = r.IsDBNull(5) ? "" : r.GetString(5),
                EnrolledAt = ParseUtc(r.GetString(6)), LastSeen = ParseUtc(r.GetString(7)),
                Command = r.IsDBNull(8) ? "active" : r.GetString(8)
            };
        }
    }

    public void SaveSummary(string tenantId, string agentId, SummaryDto summary)
    {
        lock (_gate)
        {
            Exec("INSERT INTO summaries(tenant_id,agent_id,data,updated_at) VALUES(@t,@a,@d,@u) " +
                 "ON CONFLICT(tenant_id,agent_id) DO UPDATE SET data=@d, updated_at=@u",
                ("@t", tenantId), ("@a", agentId), ("@d", JsonSerializer.Serialize(summary)), ("@u", Iso(DateTime.UtcNow)));
            TouchNoLock(agentId);
        }
    }

    public void AddAlerts(string tenantId, string agentId, IEnumerable<AlertDto> alerts)
    {
        lock (_gate)
        {
            var machine = MachineOfNoLock(agentId);
            foreach (var a in alerts)
                Exec("INSERT INTO alerts(tenant_id,agent_id,machine,received_at,data) VALUES(@t,@a,@m,@r,@d)",
                    ("@t", tenantId), ("@a", agentId), ("@m", machine), ("@r", Iso(DateTime.UtcNow)), ("@d", JsonSerializer.Serialize(a)));
            TouchNoLock(agentId);
        }
    }

    private static readonly HashSet<string> FileOps = new(StringComparer.OrdinalIgnoreCase)
        { "create", "modify", "delete", "rename", "copy", "usb_copy", "usb_insert", "usb_remove" };

    public void AddEvents(string tenantId, string agentId, IEnumerable<EventDto> events)
    {
        lock (_gate)
        {
            var machine = MachineOfNoLock(agentId);
            foreach (var e in events)
            {
                if (e.Op is null || !FileOps.Contains(e.Op)) continue;   // sadece dosya olayları
                Exec("INSERT INTO events(tenant_id,agent_id,machine,ts,op,path,sensitivity,src,user_name) VALUES(@t,@a,@m,@ts,@op,@p,@s,@src,@u)",
                    ("@t", tenantId), ("@a", agentId), ("@m", machine), ("@ts", e.Ts), ("@op", e.Op.ToLowerInvariant()),
                    ("@p", e.Path), ("@s", e.Sensitivity), ("@src", e.Source), ("@u", e.User));
            }
            TouchNoLock(agentId);
        }
    }

    public IReadOnlyList<TenantEvent> RecentEvents(string tenantId, int limit)
    {
        lock (_gate)
        {
            var list = new List<TenantEvent>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT agent_id,machine,ts,op,path,sensitivity,src,user_name FROM events WHERE tenant_id=@t ORDER BY id DESC LIMIT @l";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TenantEvent(
                    r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
            return list;
        }
    }

    public IReadOnlyList<WebActivityRow> WebActivity(string tenantId)
    {
        lock (_gate)
        {
            var list = new List<WebActivityRow>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT a.machine,a.user_name,s.data FROM agents a " +
                              "JOIN summaries s ON s.tenant_id=a.tenant_id AND s.agent_id=a.id WHERE a.tenant_id=@t";
            cmd.Parameters.AddWithValue("@t", tenantId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var machine = r.GetString(0);
                var user = r.IsDBNull(1) ? "" : r.GetString(1);
                var s = JsonSerializer.Deserialize<SummaryDto>(r.GetString(2));
                if (s?.Web is null) continue;
                foreach (var w in s.Web)
                    list.Add(new WebActivityRow(machine, user, w.Domain, w.Url, w.Title, w.Seconds, w.Incognito));
            }
            return list.OrderByDescending(x => x.Seconds).ToList();
        }
    }

    public SummaryDto? LatestSummary(string tenantId, string agentId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT data FROM summaries WHERE tenant_id=@t AND agent_id=@a";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@a", agentId);
            return cmd.ExecuteScalar() is string s ? JsonSerializer.Deserialize<SummaryDto>(s) : null;
        }
    }

    public IReadOnlyList<AgentView> AgentsView(string tenantId)
    {
        lock (_gate)
        {
            var list = new List<AgentView>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT a.id,a.machine,a.user_name,a.last_seen,s.data,a.command,a.department FROM agents a " +
                "LEFT JOIN summaries s ON s.tenant_id=a.tenant_id AND s.agent_id=a.id WHERE a.tenant_id=@t";
            cmd.Parameters.AddWithValue("@t", tenantId);
            var now = DateTime.UtcNow;
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var s = r.IsDBNull(4) ? null : JsonSerializer.Deserialize<SummaryDto>(r.GetString(4));
                var last = ParseUtc(r.GetString(3));
                list.Add(new AgentView(
                    r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                    last, now - last < OnlineWindow,
                    s?.TotalActiveSeconds ?? 0, s?.TotalIdleSeconds ?? 0, s?.FileDeletes ?? 0, s?.FileCopies ?? 0, s?.AlertCount ?? 0,
                    r.IsDBNull(5) ? "active" : r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6),
                    s?.UsbPolicy));
            }
            return list.OrderByDescending(v => v.Online).ThenBy(v => v.Machine).ToList();
        }
    }

    public IReadOnlyList<TenantAlert> AlertsForTenant(string tenantId, int limit)
    {
        lock (_gate)
        {
            var list = new List<TenantAlert>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT agent_id,machine,received_at,data FROM alerts WHERE tenant_id=@t ORDER BY received_at DESC LIMIT @l";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@l", limit);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TenantAlert(
                    r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                    ParseUtc(r.GetString(2)), JsonSerializer.Deserialize<AlertDto>(r.GetString(3))!));
            return list;
        }
    }

    public IReadOnlyList<AppUsageRow> FleetApps(string tenantId, string? department = null)
    {
        lock (_gate)
        {
            var agg = new Dictionary<string, (string app, long sec)>(StringComparer.OrdinalIgnoreCase);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT s.data FROM summaries s LEFT JOIN agents a ON a.id=s.agent_id " +
                "WHERE s.tenant_id=@t AND (@dep IS NULL OR a.department=@dep)";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@dep", (object?)department ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var s = JsonSerializer.Deserialize<SummaryDto>(r.GetString(0));
                if (s?.Apps is null) continue;
                foreach (var a in s.Apps)
                {
                    var key = string.IsNullOrEmpty(a.Exe) ? a.App : a.Exe;
                    var prev = agg.TryGetValue(key, out var v) ? v.sec : 0;
                    agg[key] = (a.App, prev + a.ActiveSeconds);
                }
            }
            return agg.Select(kv => new AppUsageRow(kv.Value.app, kv.Key, kv.Value.sec))
                      .OrderByDescending(x => x.Seconds).Take(12).ToList();
        }
    }

    public IReadOnlyList<AppLogRow> AppLog(string tenantId)
    {
        lock (_gate)
        {
            var list = new List<AppLogRow>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT a.id,a.machine,a.user_name,s.data FROM agents a " +
                              "JOIN summaries s ON s.tenant_id=a.tenant_id AND s.agent_id=a.id WHERE a.tenant_id=@t";
            cmd.Parameters.AddWithValue("@t", tenantId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var s = JsonSerializer.Deserialize<SummaryDto>(r.GetString(3));
                if (s?.Apps is null) continue;
                foreach (var a in s.Apps)
                    list.Add(new AppLogRow(r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2), a.App, a.Exe, a.ActiveSeconds));
            }
            return list.OrderByDescending(x => x.Seconds).ToList();
        }
    }

    public void AddWebUsage(string tenantId, string agentId, string machine, string user, IEnumerable<WebUsageDto> usage)
    {
        lock (_gate)
        {
            foreach (var u in usage)
                Exec("INSERT INTO web_usage(tenant_id,agent_id,machine,user_name,site,domain,url,seconds,incognito,ts,title) " +
                     "VALUES(@t,@a,@m,@u,@s,@d,@url,@sec,@i,@ts,@title)",
                    ("@t", tenantId), ("@a", agentId), ("@m", machine), ("@u", user), ("@s", u.Site),
                    ("@d", u.Domain), ("@url", u.Url), ("@sec", u.Seconds), ("@i", u.Incognito ? 1 : 0), ("@ts", u.Ts), ("@title", u.Title));
        }
    }

    public IReadOnlyList<WebReportRow> WebReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null)
    {
        lock (_gate)
        {
            var list = new List<WebReportRow>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT w.site, MAX(w.domain), MAX(w.url), SUM(w.seconds), MAX(w.incognito), MAX(w.ts), MAX(w.title) " +
                "FROM web_usage w LEFT JOIN agents a ON a.id=w.agent_id " +
                "WHERE w.tenant_id=@t AND (@a IS NULL OR w.agent_id=@a) AND w.ts>=@f AND w.ts<=@to AND (@dep IS NULL OR a.department=@dep) " +
                "GROUP BY w.site ORDER BY SUM(w.seconds) DESC LIMIT 300";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@a", (object?)agentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f", fromUtcIso);
            cmd.Parameters.AddWithValue("@to", toUtcIso);
            cmd.Parameters.AddWithValue("@dep", (object?)department ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new WebReportRow(
                    r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                    r.IsDBNull(6) ? null : r.GetString(6),
                    r.IsDBNull(3) ? 0 : r.GetInt64(3), !r.IsDBNull(4) && r.GetInt64(4) > 0, r.IsDBNull(5) ? null : r.GetString(5)));
            return list;
        }
    }

    public void AddAppUsage(string tenantId, string agentId, string machine, string user, IEnumerable<AppUsageDto> usage)
    {
        lock (_gate)
            foreach (var u in usage)
                Exec("INSERT INTO app_usage(tenant_id,agent_id,machine,user_name,app,exe,seconds,ts) " +
                     "VALUES(@t,@a,@m,@u,@app,@exe,@sec,@ts)",
                    ("@t", tenantId), ("@a", agentId), ("@m", machine), ("@u", user),
                    ("@app", u.App), ("@exe", u.Exe), ("@sec", u.Seconds), ("@ts", u.Ts));
    }

    public IReadOnlyList<AppReportRow> AppReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null)
    {
        lock (_gate)
        {
            var list = new List<AppReportRow>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT MAX(p.app), p.exe, SUM(p.seconds), MAX(p.ts) FROM app_usage p " +
                "LEFT JOIN agents a ON a.id=p.agent_id " +
                "WHERE p.tenant_id=@t AND (@a IS NULL OR p.agent_id=@a) AND p.ts>=@f AND p.ts<=@to AND (@dep IS NULL OR a.department=@dep) " +
                "GROUP BY p.exe ORDER BY SUM(p.seconds) DESC LIMIT 300";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@a", (object?)agentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f", fromUtcIso);
            cmd.Parameters.AddWithValue("@to", toUtcIso);
            cmd.Parameters.AddWithValue("@dep", (object?)department ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new AppReportRow(
                    r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)));
            return list;
        }
    }

    public void AddDocUsage(string tenantId, string agentId, string machine, string user, IEnumerable<DocUsageDto> usage)
    {
        lock (_gate)
            foreach (var u in usage)
                Exec("INSERT INTO doc_usage(tenant_id,agent_id,machine,user_name,name,app,seconds,ts,path) " +
                     "VALUES(@t,@a,@m,@u,@n,@app,@sec,@ts,@p)",
                    ("@t", tenantId), ("@a", agentId), ("@m", machine), ("@u", user),
                    ("@n", u.Name), ("@app", u.App), ("@sec", u.Seconds), ("@ts", u.Ts), ("@p", u.Path));
    }

    public IReadOnlyList<DocReportRow> DocReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null)
    {
        lock (_gate)
        {
            var list = new List<DocReportRow>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText =
                "SELECT d.name, MAX(d.app), SUM(d.seconds), MAX(d.ts), MAX(d.path) FROM doc_usage d " +
                "LEFT JOIN agents a ON a.id=d.agent_id " +
                "WHERE d.tenant_id=@t AND (@a IS NULL OR d.agent_id=@a) AND d.ts>=@f AND d.ts<=@to AND (@dep IS NULL OR a.department=@dep) " +
                "GROUP BY d.name ORDER BY SUM(d.seconds) DESC LIMIT 300";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@a", (object?)agentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f", fromUtcIso);
            cmd.Parameters.AddWithValue("@to", toUtcIso);
            cmd.Parameters.AddWithValue("@dep", (object?)department ?? DBNull.Value);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new DocReportRow(
                    r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4)));
            return list;
        }
    }

    public IReadOnlyList<TenantEvent> EventsInRange(string tenantId, string? agentId, string fromUtcIso, string toUtcIso,
        int limit, string? op = null, bool sensitiveOnly = false, int offset = 0)
    {
        lock (_gate)
        {
            var list = new List<TenantEvent>();
            using var cmd = _conn.CreateCommand();
            // op="usb" → usb_insert/usb_remove/usb_copy hepsi; başka değer → tam eşleşme.
            var opSql = op switch
            {
                null or "" => "",
                "usb" => " AND op LIKE 'usb%'",
                _ => " AND op=@op"
            };
            cmd.CommandText = "SELECT agent_id,machine,ts,op,path,sensitivity,src,user_name FROM events " +
                "WHERE tenant_id=@t AND (@a IS NULL OR agent_id=@a) AND ts>=@f AND ts<=@to" + opSql +
                (sensitiveOnly ? " AND sensitivity IS NOT NULL AND sensitivity<>''" : "") +
                " ORDER BY id DESC LIMIT @l OFFSET @o";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@a", (object?)agentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f", fromUtcIso);
            cmd.Parameters.AddWithValue("@to", toUtcIso);
            cmd.Parameters.AddWithValue("@l", limit);
            cmd.Parameters.AddWithValue("@o", Math.Max(0, offset));
            if (opSql.Contains("@op")) cmd.Parameters.AddWithValue("@op", op!);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TenantEvent(
                    r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                    r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
            return list;
        }
    }

    // Uyarı geçmişi: received_at (UTC) aralığı + isteğe bağlı kişi/tip/önem + sayfalama.
    // Tip ve önem uyarının JSON gövdesinde → json_extract ile süzülür (SQLite JSON1 yerleşik).
    public IReadOnlyList<TenantAlert> AlertsInRange(string tenantId, string? agentId, DateTime fromUtc, DateTime toUtc,
        string? type, string? severity, int limit, int offset)
    {
        lock (_gate)
        {
            var list = new List<TenantAlert>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT agent_id,machine,received_at,data FROM alerts " +
                "WHERE tenant_id=@t AND (@a IS NULL OR agent_id=@a) AND received_at>=@f AND received_at<=@to" +
                (string.IsNullOrWhiteSpace(type) ? "" : " AND json_extract(data,'$.Type')=@ty") +
                (string.IsNullOrWhiteSpace(severity) ? "" : " AND json_extract(data,'$.Severity')=@sv") +
                " ORDER BY received_at DESC, id DESC LIMIT @l OFFSET @o";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@a", (object?)agentId ?? DBNull.Value);
            cmd.Parameters.AddWithValue("@f", Iso(fromUtc));
            cmd.Parameters.AddWithValue("@to", Iso(toUtc));
            cmd.Parameters.AddWithValue("@l", limit);
            cmd.Parameters.AddWithValue("@o", Math.Max(0, offset));
            if (!string.IsNullOrWhiteSpace(type)) cmd.Parameters.AddWithValue("@ty", type);
            if (!string.IsNullOrWhiteSpace(severity)) cmd.Parameters.AddWithValue("@sv", severity);
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add(new TenantAlert(
                    r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                    ParseUtc(r.GetString(2)), JsonSerializer.Deserialize<AlertDto>(r.GetString(3))!));
            return list;
        }
    }

    public TenantSettings GetSettings(string tenantId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT data FROM settings WHERE tenant_id=@t";
            cmd.Parameters.AddWithValue("@t", tenantId);
            return (cmd.ExecuteScalar() is string s
                ? JsonSerializer.Deserialize<TenantSettings>(s) ?? new TenantSettings()
                : new TenantSettings()).NormalizeUsb();
        }
    }

    public long PurgeOlderThan(string tenantId, DateTime cutoffUtc, string cutoffLocalIso)
    {
        lock (_gate)
        {
            long n = 0;
            n += ExecCount("DELETE FROM alerts WHERE tenant_id=@t AND received_at<@c", ("@t", tenantId), ("@c", Iso(cutoffUtc)));
            foreach (var table in new[] { "events", "web_usage", "app_usage", "doc_usage" })
                n += ExecCount($"DELETE FROM {table} WHERE tenant_id=@t AND ts<@c", ("@t", tenantId), ("@c", cutoffLocalIso));
            return n;
        }
    }

    private int ExecCount(string sql, params (string, object)[] ps)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v);
        return cmd.ExecuteNonQuery();
    }

    public void SaveSettings(string tenantId, TenantSettings settings)
    {
        settings.NormalizeUsb();
        settings.UpdatedAt = Iso(DateTime.UtcNow);
        lock (_gate)
            Exec("INSERT INTO settings(tenant_id,data,updated_at) VALUES(@t,@d,@u) " +
                 "ON CONFLICT(tenant_id) DO UPDATE SET data=@d, updated_at=@u",
                ("@t", tenantId), ("@d", JsonSerializer.Serialize(settings)), ("@u", settings.UpdatedAt));
    }

    // --- Panel kullanıcıları + oturumlar ---
    public int CountUsers(string tenantId)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM panel_users WHERE tenant_id=@t";
            cmd.Parameters.AddWithValue("@t", tenantId);
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private static PanelUser ReadUser(Microsoft.Data.Sqlite.SqliteDataReader r) => new()
    {
        Id = r.GetString(0), TenantId = r.GetString(1), Username = r.GetString(2), PasswordHash = r.GetString(3),
        Role = r.GetString(4), DisplayName = r.IsDBNull(5) ? "" : r.GetString(5), Active = r.GetInt64(6) != 0,
        CreatedAt = ParseUtc(r.GetString(7)), LastLogin = r.IsDBNull(8) ? null : ParseUtc(r.GetString(8)),
        MustChangePassword = !r.IsDBNull(9) && r.GetInt64(9) != 0,
        Department = r.IsDBNull(10) ? "" : r.GetString(10)
    };
    private const string UserCols = "id,tenant_id,username,password_hash,role,display_name,active,created_at,last_login,must_change,department";

    public PanelUser? GetUser(string tenantId, string username)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {UserCols} FROM panel_users WHERE tenant_id=@t AND lower(username)=lower(@u)";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@u", username);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadUser(r) : null;
        }
    }

    public PanelUser? GetUserByUsernameGlobal(string username)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {UserCols} FROM panel_users WHERE lower(username)=lower(@u) LIMIT 1";
            cmd.Parameters.AddWithValue("@u", username);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadUser(r) : null;
        }
    }

    public PanelUser? GetUserById(string tenantId, string id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {UserCols} FROM panel_users WHERE tenant_id=@t AND id=@id";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@id", id);
            using var r = cmd.ExecuteReader();
            return r.Read() ? ReadUser(r) : null;
        }
    }

    public IReadOnlyList<UserView> ListUsers(string tenantId)
    {
        lock (_gate)
        {
            var list = new List<UserView>();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT {UserCols} FROM panel_users WHERE tenant_id=@t ORDER BY created_at";
            cmd.Parameters.AddWithValue("@t", tenantId);
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var u = ReadUser(r);
                list.Add(new UserView(u.Id, u.Username, u.Role, u.DisplayName, u.Active, u.Department, u.CreatedAt, u.LastLogin));
            }
            return list;
        }
    }

    public PanelUser CreateUser(string tenantId, string username, string passwordHash, string role, string displayName, string department = "")
    {
        lock (_gate)
        {
            var u = new PanelUser
            {
                Id = "usr_" + Guid.NewGuid().ToString("N")[..10], TenantId = tenantId, Username = username,
                PasswordHash = passwordHash, Role = role, DisplayName = displayName, Active = true,
                Department = department ?? "", CreatedAt = DateTime.UtcNow
            };
            Exec("INSERT INTO panel_users(id,tenant_id,username,password_hash,role,display_name,active,created_at,last_login,must_change,department) " +
                 "VALUES(@id,@t,@u,@ph,@r,@dn,1,@c,NULL,1,@dep)",
                ("@id", u.Id), ("@t", tenantId), ("@u", username), ("@ph", passwordHash),
                ("@r", role), ("@dn", displayName), ("@c", Iso(u.CreatedAt)), ("@dep", u.Department));
            return u;
        }
    }

    public bool UpdateUser(string tenantId, string id, string? role, string? displayName, bool? active, string? passwordHash, string? department = null)
    {
        lock (_gate)
        {
            var sets = new List<string>();
            var ps = new List<(string, object?)> { ("@t", tenantId), ("@id", id) };
            if (role is not null) { sets.Add("role=@r"); ps.Add(("@r", role)); }
            if (displayName is not null) { sets.Add("display_name=@dn"); ps.Add(("@dn", displayName)); }
            if (active is not null) { sets.Add("active=@a"); ps.Add(("@a", active.Value ? 1 : 0)); }
            if (passwordHash is not null) { sets.Add("password_hash=@ph"); ps.Add(("@ph", passwordHash)); }
            if (department is not null) { sets.Add("department=@dep"); ps.Add(("@dep", department)); }
            if (sets.Count == 0) return true;
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"UPDATE panel_users SET {string.Join(",", sets)} WHERE tenant_id=@t AND id=@id";
            foreach (var (k, v) in ps) cmd.Parameters.AddWithValue(k, v ?? DBNull.Value);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool DeleteUser(string tenantId, string id)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM panel_users WHERE tenant_id=@t AND id=@id";
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@id", id);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool SetAgentCommand(string tenantId, string agentId, string command)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE agents SET command=@c WHERE tenant_id=@t AND id=@id";
            cmd.Parameters.AddWithValue("@c", command);
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@id", agentId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public bool SetAgentDepartment(string tenantId, string agentId, string department)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "UPDATE agents SET department=@d WHERE tenant_id=@t AND id=@id";
            cmd.Parameters.AddWithValue("@d", department ?? "");
            cmd.Parameters.AddWithValue("@t", tenantId);
            cmd.Parameters.AddWithValue("@id", agentId);
            return cmd.ExecuteNonQuery() > 0;
        }
    }

    public void UpdateLastLogin(string tenantId, string userId)
        => Exec("UPDATE panel_users SET last_login=@ls WHERE tenant_id=@t AND id=@id",
            ("@ls", Iso(DateTime.UtcNow)), ("@t", tenantId), ("@id", userId));

    public void SetPassword(string tenantId, string userId, string passwordHash)
        => Exec("UPDATE panel_users SET password_hash=@ph, must_change=0 WHERE tenant_id=@t AND id=@id",
            ("@ph", passwordHash), ("@t", tenantId), ("@id", userId));

    public string CreateSession(string tenantId, string userId, DateTime expiresUtc)
    {
        var token = Passwords.NewToken();
        lock (_gate)
            Exec("INSERT INTO sessions(token,tenant_id,user_id,created_at,expires_at) VALUES(@tk,@t,@u,@c,@e)",
                ("@tk", token), ("@t", tenantId), ("@u", userId), ("@c", Iso(DateTime.UtcNow)), ("@e", Iso(expiresUtc)));
        return token;
    }

    public (string userId, string tenantId)? GetSession(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT user_id,tenant_id,expires_at FROM sessions WHERE token=@tk";
            cmd.Parameters.AddWithValue("@tk", token);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var exp = ParseUtc(r.GetString(2));
            if (exp < DateTime.UtcNow) return null;
            return (r.GetString(0), r.GetString(1));
        }
    }

    public void DeleteSession(string token)
    {
        lock (_gate) Exec("DELETE FROM sessions WHERE token=@tk", ("@tk", token));
    }

    public Overview BuildOverview(string tenantId, string? department = null) => OverviewBuilder.Build(this, tenantId, department);

    private void TouchNoLock(string agentId)
        => Exec("UPDATE agents SET last_seen=@ls WHERE id=@id", ("@ls", Iso(DateTime.UtcNow)), ("@id", agentId));

    private string? MachineOfNoLock(string agentId)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT machine FROM agents WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", agentId);
        return cmd.ExecuteScalar() as string;
    }

    // Tutarlı yedek: VACUUM INTO ile temiz, tek dosyalık kopya (WAL dahil birleşik).
    public void Backup(string destPath)
    {
        lock (_gate)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"VACUUM INTO '{destPath.Replace("'", "''")}'";
            cmd.ExecuteNonQuery();
        }
    }

    public void Dispose() => _conn.Dispose();
}
