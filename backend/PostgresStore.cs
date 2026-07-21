// PostgreSQL depolama — IStore'un Postgres uygulaması.
// Bağlantı dizesi verilince (ARGUS_PG env ya da config) devreye girer; şemayı otomatik oluşturur.
// Ağır alanlar (summary/alert) jsonb olarak saklanır.

using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

namespace Argus.Server;

public sealed class PostgresStore : IStore
{
    private static readonly TimeSpan OnlineWindow = TimeSpan.FromMinutes(2);
    private readonly NpgsqlDataSource _ds;

    public PostgresStore(string connectionString)
    {
        _ds = NpgsqlDataSource.Create(connectionString);
        InitSchema();
    }

    private void InitSchema()
    {
        const string sql = @"
CREATE TABLE IF NOT EXISTS tenants (
  id text PRIMARY KEY, name text NOT NULL, key text UNIQUE NOT NULL, created_at timestamptz NOT NULL);
ALTER TABLE tenants ADD COLUMN IF NOT EXISTS seat_limit integer NOT NULL DEFAULT 0;
ALTER TABLE tenants ADD COLUMN IF NOT EXISTS expires_at timestamptz;
CREATE TABLE IF NOT EXISTS agents (
  id text PRIMARY KEY, tenant_id text NOT NULL, token text UNIQUE NOT NULL,
  machine text NOT NULL, user_name text, host text,
  enrolled_at timestamptz NOT NULL, last_seen timestamptz NOT NULL);
ALTER TABLE agents ADD COLUMN IF NOT EXISTS command text NOT NULL DEFAULT 'active';
ALTER TABLE agents ADD COLUMN IF NOT EXISTS department text NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS ix_agents_tenant ON agents(tenant_id);
CREATE TABLE IF NOT EXISTS panel_users (
  id text PRIMARY KEY, tenant_id text NOT NULL, username text NOT NULL, password_hash text NOT NULL,
  role text NOT NULL, display_name text, active boolean NOT NULL DEFAULT true,
  created_at timestamptz NOT NULL, last_login timestamptz);
ALTER TABLE panel_users ADD COLUMN IF NOT EXISTS must_change boolean NOT NULL DEFAULT false;
ALTER TABLE panel_users ADD COLUMN IF NOT EXISTS department text NOT NULL DEFAULT '';
CREATE UNIQUE INDEX IF NOT EXISTS ix_users_uniq ON panel_users(tenant_id, lower(username));
CREATE TABLE IF NOT EXISTS sessions (
  token text PRIMARY KEY, tenant_id text NOT NULL, user_id text NOT NULL,
  created_at timestamptz NOT NULL, expires_at timestamptz NOT NULL);
CREATE INDEX IF NOT EXISTS ix_sessions_exp ON sessions(expires_at);
CREATE TABLE IF NOT EXISTS summaries (
  tenant_id text NOT NULL, agent_id text NOT NULL, data jsonb NOT NULL, updated_at timestamptz NOT NULL,
  PRIMARY KEY (tenant_id, agent_id));
CREATE TABLE IF NOT EXISTS alerts (
  id bigserial PRIMARY KEY, tenant_id text NOT NULL, agent_id text, machine text,
  received_at timestamptz NOT NULL, data jsonb NOT NULL);
CREATE INDEX IF NOT EXISTS ix_alerts_tenant ON alerts(tenant_id, received_at DESC);
CREATE TABLE IF NOT EXISTS events (
  id bigserial PRIMARY KEY, tenant_id text NOT NULL, agent_id text, machine text, ts text, op text, path text);
ALTER TABLE events ADD COLUMN IF NOT EXISTS sensitivity text;
ALTER TABLE events ADD COLUMN IF NOT EXISTS src text;
ALTER TABLE events ADD COLUMN IF NOT EXISTS user_name text;
CREATE INDEX IF NOT EXISTS ix_events_tenant ON events(tenant_id, id DESC);
CREATE TABLE IF NOT EXISTS web_usage (
  id bigserial PRIMARY KEY, tenant_id text, agent_id text, machine text, user_name text,
  site text, domain text, url text, seconds bigint, incognito boolean, ts text);
ALTER TABLE web_usage ADD COLUMN IF NOT EXISTS title text;
CREATE INDEX IF NOT EXISTS ix_webusage ON web_usage(tenant_id, agent_id, ts);
CREATE TABLE IF NOT EXISTS app_usage (
  id bigserial PRIMARY KEY, tenant_id text, agent_id text, machine text, user_name text,
  app text, exe text, seconds bigint, ts text);
CREATE INDEX IF NOT EXISTS ix_appusage ON app_usage(tenant_id, agent_id, ts);
CREATE TABLE IF NOT EXISTS doc_usage (
  id bigserial PRIMARY KEY, tenant_id text, agent_id text, machine text, user_name text,
  name text, app text, seconds bigint, ts text);
CREATE INDEX IF NOT EXISTS ix_docusage ON doc_usage(tenant_id, agent_id, ts);
CREATE TABLE IF NOT EXISTS settings (
  tenant_id text PRIMARY KEY, data jsonb NOT NULL, updated_at timestamptz NOT NULL);";
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(sql, conn);
        cmd.ExecuteNonQuery();
    }

    public Tenant SeedTenant(string name, string key)
    {
        var existing = GetTenantByKey(key);
        if (existing is not null) return existing;

        var t = new Tenant { Id = "ten_" + Guid.NewGuid().ToString("N")[..10], Name = name, Key = key, CreatedAt = DateTime.UtcNow };
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO tenants(id,name,key,created_at) VALUES(@id,@n,@k,@c) ON CONFLICT (key) DO NOTHING", conn);
        cmd.Parameters.AddWithValue("id", t.Id);
        cmd.Parameters.AddWithValue("n", t.Name);
        cmd.Parameters.AddWithValue("k", t.Key);
        cmd.Parameters.AddWithValue("c", t.CreatedAt);
        cmd.ExecuteNonQuery();
        return GetTenantByKey(key) ?? t;
    }

    public Tenant? GetTenantByKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand($"SELECT {TenantCols} FROM tenants WHERE key=@k", conn);
        cmd.Parameters.AddWithValue("k", key);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadTenant(r);
    }

    public Tenant? GetTenantById(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand($"SELECT {TenantCols} FROM tenants WHERE id=@id", conn);
        cmd.Parameters.AddWithValue("id", id);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return ReadTenant(r);
    }

    public IReadOnlyList<Tenant> ListTenants()
    {
        var list = new List<Tenant>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand($"SELECT {TenantCols} FROM tenants ORDER BY created_at", conn);
        using var r = cmd.ExecuteReader();
        while (r.Read()) list.Add(ReadTenant(r));
        return list;
    }

    private const string TenantCols = "id,name,key,created_at,seat_limit,expires_at";
    private static Tenant ReadTenant(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), Name = r.GetString(1), Key = r.GetString(2), CreatedAt = r.GetDateTime(3),
        SeatLimit = r.IsDBNull(4) ? 0 : r.GetInt32(4),
        ExpiresAt = r.IsDBNull(5) ? null : r.GetDateTime(5)
    };

    public bool SetTenantLicense(string id, int seatLimit, DateTime? expiresAt)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("UPDATE tenants SET seat_limit=@s, expires_at=@e WHERE id=@id", conn);
        cmd.Parameters.AddWithValue("s", seatLimit);
        cmd.Parameters.AddWithValue("e", (object?)expiresAt ?? DBNull.Value);
        cmd.Parameters.AddWithValue("id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public int CountAgents(string tenantId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM agents WHERE tenant_id=@t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public bool RenameTenant(string id, string name)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("UPDATE tenants SET name=@n WHERE id=@id", conn);
        cmd.Parameters.AddWithValue("n", name);
        cmd.Parameters.AddWithValue("id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteTenant(string id)
    {
        using var conn = _ds.OpenConnection();
        using var tx = conn.BeginTransaction();
        foreach (var t in new[] { "agents", "summaries", "alerts", "events", "web_usage", "app_usage", "settings", "panel_users" })
            using (var c = new NpgsqlCommand($"DELETE FROM {t} WHERE tenant_id=@t", conn, tx)) { c.Parameters.AddWithValue("t", id); c.ExecuteNonQuery(); }
        int n;
        using (var c = new NpgsqlCommand("DELETE FROM tenants WHERE id=@t", conn, tx)) { c.Parameters.AddWithValue("t", id); n = c.ExecuteNonQuery(); }
        tx.Commit();
        return n > 0;
    }

    public Agent EnrollAgent(string tenantId, string machine, string user, string host)
    {
        using var conn = _ds.OpenConnection();

        using (var find = new NpgsqlCommand(
            "SELECT id,token,enrolled_at,command FROM agents WHERE tenant_id=@t AND lower(machine)=lower(@m)", conn))
        {
            find.Parameters.AddWithValue("t", tenantId);
            find.Parameters.AddWithValue("m", machine);
            using var r = find.ExecuteReader();
            if (r.Read())
            {
                var cmd = r.IsDBNull(3) ? "active" : r.GetString(3);
                if (cmd == "remove") cmd = "active";   // yeniden kurulum: bekleyen 'kaldır'ı miras alma
                var a = new Agent
                {
                    Id = r.GetString(0), TenantId = tenantId, Token = r.GetString(1),
                    Machine = machine, User = user, Host = host,
                    EnrolledAt = r.GetDateTime(2), LastSeen = DateTime.UtcNow,
                    Command = cmd
                };
                r.Close();
                using var upd = new NpgsqlCommand(
                    "UPDATE agents SET user_name=@u,host=@h,last_seen=@ls,command=@c WHERE id=@id", conn);
                upd.Parameters.AddWithValue("u", user);
                upd.Parameters.AddWithValue("h", host);
                upd.Parameters.AddWithValue("ls", a.LastSeen);
                upd.Parameters.AddWithValue("c", cmd);
                upd.Parameters.AddWithValue("id", a.Id);
                upd.ExecuteNonQuery();
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
        using var ins = new NpgsqlCommand(
            "INSERT INTO agents(id,tenant_id,token,machine,user_name,host,enrolled_at,last_seen) " +
            "VALUES(@id,@t,@tok,@m,@u,@h,@e,@ls)", conn);
        ins.Parameters.AddWithValue("id", agent.Id);
        ins.Parameters.AddWithValue("t", agent.TenantId);
        ins.Parameters.AddWithValue("tok", agent.Token);
        ins.Parameters.AddWithValue("m", agent.Machine);
        ins.Parameters.AddWithValue("u", agent.User);
        ins.Parameters.AddWithValue("h", agent.Host);
        ins.Parameters.AddWithValue("e", agent.EnrolledAt);
        ins.Parameters.AddWithValue("ls", agent.LastSeen);
        ins.ExecuteNonQuery();
        return agent;
    }

    public Agent? GetAgentByToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT id,tenant_id,token,machine,user_name,host,enrolled_at,last_seen,command FROM agents WHERE token=@tok", conn);
        cmd.Parameters.AddWithValue("tok", token);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        return new Agent
        {
            Id = r.GetString(0), TenantId = r.GetString(1), Token = r.GetString(2),
            Machine = r.GetString(3), User = r.IsDBNull(4) ? "" : r.GetString(4),
            Host = r.IsDBNull(5) ? "" : r.GetString(5),
            EnrolledAt = r.GetDateTime(6), LastSeen = r.GetDateTime(7),
            Command = r.IsDBNull(8) ? "active" : r.GetString(8)
        };
    }

    public bool SetAgentCommand(string tenantId, string agentId, string command)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("UPDATE agents SET command=@c WHERE tenant_id=@t AND id=@id", conn);
        cmd.Parameters.AddWithValue("c", command);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", agentId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool SetAgentDepartment(string tenantId, string agentId, string department)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("UPDATE agents SET department=@d WHERE tenant_id=@t AND id=@id", conn);
        cmd.Parameters.AddWithValue("d", department ?? "");
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", agentId);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void SaveSummary(string tenantId, string agentId, SummaryDto summary)
    {
        using var conn = _ds.OpenConnection();
        using (var cmd = new NpgsqlCommand(
            "INSERT INTO summaries(tenant_id,agent_id,data,updated_at) VALUES(@t,@a,@d,@u) " +
            "ON CONFLICT (tenant_id,agent_id) DO UPDATE SET data=@d, updated_at=@u", conn))
        {
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("a", agentId);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(summary) });
            cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
            cmd.ExecuteNonQuery();
        }
        Touch(conn, agentId);
    }

    public void AddAlerts(string tenantId, string agentId, IEnumerable<AlertDto> alerts)
    {
        using var conn = _ds.OpenConnection();
        var machine = MachineOf(conn, agentId);
        foreach (var a in alerts)
        {
            using var cmd = new NpgsqlCommand(
                "INSERT INTO alerts(tenant_id,agent_id,machine,received_at,data) VALUES(@t,@a,@m,@r,@d)", conn);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("a", agentId);
            cmd.Parameters.AddWithValue("m", (object?)machine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("r", DateTime.UtcNow);
            cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(a) });
            cmd.ExecuteNonQuery();
        }
        Touch(conn, agentId);
    }

    private static readonly HashSet<string> FileOps = new(StringComparer.OrdinalIgnoreCase)
        { "create", "modify", "delete", "rename", "copy", "usb_copy", "usb_insert", "usb_remove" };

    public void AddEvents(string tenantId, string agentId, IEnumerable<EventDto> events)
    {
        using var conn = _ds.OpenConnection();
        var machine = MachineOf(conn, agentId);
        foreach (var e in events)
        {
            if (e.Op is null || !FileOps.Contains(e.Op)) continue;   // sadece dosya olayları
            using var cmd = new NpgsqlCommand(
                "INSERT INTO events(tenant_id,agent_id,machine,ts,op,path,sensitivity,src,user_name) VALUES(@t,@a,@m,@ts,@op,@p,@s,@src,@u)", conn);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("a", agentId);
            cmd.Parameters.AddWithValue("m", (object?)machine ?? DBNull.Value);
            cmd.Parameters.AddWithValue("ts", (object?)e.Ts ?? DBNull.Value);
            cmd.Parameters.AddWithValue("op", e.Op.ToLowerInvariant());
            cmd.Parameters.AddWithValue("p", (object?)e.Path ?? DBNull.Value);
            cmd.Parameters.AddWithValue("s", (object?)e.Sensitivity ?? DBNull.Value);
            cmd.Parameters.AddWithValue("src", (object?)e.Source ?? DBNull.Value);
            cmd.Parameters.AddWithValue("u", (object?)e.User ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
        Touch(conn, agentId);
    }

    public IReadOnlyList<TenantEvent> RecentEvents(string tenantId, int limit)
    {
        var list = new List<TenantEvent>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT agent_id,machine,ts,op,path,sensitivity,src,user_name FROM events WHERE tenant_id=@t ORDER BY id DESC LIMIT @l", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TenantEvent(
                r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    public IReadOnlyList<WebActivityRow> WebActivity(string tenantId)
    {
        var list = new List<WebActivityRow>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT a.machine,a.user_name,s.data FROM agents a " +
            "JOIN summaries s ON s.tenant_id=a.tenant_id AND s.agent_id=a.id WHERE a.tenant_id=@t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
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

    public SummaryDto? LatestSummary(string tenantId, string agentId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT data FROM summaries WHERE tenant_id=@t AND agent_id=@a", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", agentId);
        var data = cmd.ExecuteScalar() as string;
        return data is null ? null : JsonSerializer.Deserialize<SummaryDto>(data);
    }

    public IReadOnlyList<AgentView> AgentsView(string tenantId)
    {
        var list = new List<AgentView>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT a.id,a.machine,a.user_name,a.last_seen,s.data,a.command,a.department FROM agents a " +
            "LEFT JOIN summaries s ON s.tenant_id=a.tenant_id AND s.agent_id=a.id WHERE a.tenant_id=@t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        using var r = cmd.ExecuteReader();
        var now = DateTime.UtcNow;
        while (r.Read())
        {
            var s = r.IsDBNull(4) ? null : JsonSerializer.Deserialize<SummaryDto>(r.GetString(4));
            var last = r.GetDateTime(3);
            list.Add(new AgentView(
                r.GetString(0), r.GetString(1), r.IsDBNull(2) ? "" : r.GetString(2),
                last, now - last < OnlineWindow,
                s?.TotalActiveSeconds ?? 0, s?.TotalIdleSeconds ?? 0, s?.FileDeletes ?? 0, s?.FileCopies ?? 0, s?.AlertCount ?? 0,
                r.IsDBNull(5) ? "active" : r.GetString(5), r.IsDBNull(6) ? "" : r.GetString(6)));
        }
        return list.OrderByDescending(v => v.Online).ThenBy(v => v.Machine).ToList();
    }

    public IReadOnlyList<TenantAlert> AlertsForTenant(string tenantId, int limit)
    {
        var list = new List<TenantAlert>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT agent_id,machine,received_at,data FROM alerts WHERE tenant_id=@t ORDER BY received_at DESC LIMIT @l", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var alert = JsonSerializer.Deserialize<AlertDto>(r.GetString(3))!;
            list.Add(new TenantAlert(
                r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.GetDateTime(2), alert));
        }
        return list;
    }

    public IReadOnlyList<AppUsageRow> FleetApps(string tenantId, string? department = null)
    {
        var agg = new Dictionary<string, (string app, long sec)>(StringComparer.OrdinalIgnoreCase);
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT s.data FROM summaries s LEFT JOIN agents a ON a.id=s.agent_id " +
            "WHERE s.tenant_id=@t AND (@dep::text IS NULL OR a.department=@dep)", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("dep", (object?)department ?? DBNull.Value);
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

    public IReadOnlyList<AppLogRow> AppLog(string tenantId)
    {
        var list = new List<AppLogRow>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT a.id,a.machine,a.user_name,s.data FROM agents a " +
            "JOIN summaries s ON s.tenant_id=a.tenant_id AND s.agent_id=a.id WHERE a.tenant_id=@t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
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

    public void AddWebUsage(string tenantId, string agentId, string machine, string user, IEnumerable<WebUsageDto> usage)
    {
        using var conn = _ds.OpenConnection();
        foreach (var u in usage)
        {
            using var cmd = new NpgsqlCommand(
                "INSERT INTO web_usage(tenant_id,agent_id,machine,user_name,site,domain,url,seconds,incognito,ts,title) " +
                "VALUES(@t,@a,@m,@u,@s,@d,@url,@sec,@i,@ts,@title)", conn);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("a", agentId);
            cmd.Parameters.AddWithValue("m", machine);
            cmd.Parameters.AddWithValue("u", user);
            cmd.Parameters.AddWithValue("s", u.Site);
            cmd.Parameters.AddWithValue("d", (object?)u.Domain ?? DBNull.Value);
            cmd.Parameters.AddWithValue("url", (object?)u.Url ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sec", u.Seconds);
            cmd.Parameters.AddWithValue("i", u.Incognito);
            cmd.Parameters.AddWithValue("ts", u.Ts);
            cmd.Parameters.AddWithValue("title", (object?)u.Title ?? DBNull.Value);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<WebReportRow> WebReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null)
    {
        var list = new List<WebReportRow>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT w.site, MAX(w.domain), MAX(w.url), SUM(w.seconds), bool_or(w.incognito), MAX(w.ts), MAX(w.title) " +
            "FROM web_usage w LEFT JOIN agents a ON a.id=w.agent_id " +
            "WHERE w.tenant_id=@t AND (@a::text IS NULL OR w.agent_id=@a) AND w.ts>=@f AND w.ts<=@to AND (@dep::text IS NULL OR a.department=@dep) " +
            "GROUP BY w.site ORDER BY SUM(w.seconds) DESC LIMIT 300", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", (object?)agentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("f", fromUtcIso);
        cmd.Parameters.AddWithValue("to", toUtcIso);
        cmd.Parameters.AddWithValue("dep", (object?)department ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new WebReportRow(
                r.GetString(0), r.IsDBNull(1) ? null : r.GetString(1), r.IsDBNull(2) ? null : r.GetString(2),
                r.IsDBNull(6) ? null : r.GetString(6),
                r.IsDBNull(3) ? 0 : r.GetInt64(3), !r.IsDBNull(4) && r.GetBoolean(4), r.IsDBNull(5) ? null : r.GetString(5)));
        return list;
    }

    public void AddAppUsage(string tenantId, string agentId, string machine, string user, IEnumerable<AppUsageDto> usage)
    {
        using var conn = _ds.OpenConnection();
        foreach (var u in usage)
        {
            using var cmd = new NpgsqlCommand(
                "INSERT INTO app_usage(tenant_id,agent_id,machine,user_name,app,exe,seconds,ts) " +
                "VALUES(@t,@a,@m,@u,@app,@exe,@sec,@ts)", conn);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("a", agentId);
            cmd.Parameters.AddWithValue("m", machine);
            cmd.Parameters.AddWithValue("u", user);
            cmd.Parameters.AddWithValue("app", (object?)u.App ?? DBNull.Value);
            cmd.Parameters.AddWithValue("exe", (object?)u.Exe ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sec", u.Seconds);
            cmd.Parameters.AddWithValue("ts", u.Ts);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<AppReportRow> AppReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null)
    {
        var list = new List<AppReportRow>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT MAX(p.app), p.exe, SUM(p.seconds), MAX(p.ts) FROM app_usage p " +
            "LEFT JOIN agents a ON a.id=p.agent_id " +
            "WHERE p.tenant_id=@t AND (@a::text IS NULL OR p.agent_id=@a) AND p.ts>=@f AND p.ts<=@to AND (@dep::text IS NULL OR a.department=@dep) " +
            "GROUP BY p.exe ORDER BY SUM(p.seconds) DESC LIMIT 300", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", (object?)agentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("f", fromUtcIso);
        cmd.Parameters.AddWithValue("to", toUtcIso);
        cmd.Parameters.AddWithValue("dep", (object?)department ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new AppReportRow(
                r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    public void AddDocUsage(string tenantId, string agentId, string machine, string user, IEnumerable<DocUsageDto> usage)
    {
        using var conn = _ds.OpenConnection();
        foreach (var u in usage)
        {
            using var cmd = new NpgsqlCommand(
                "INSERT INTO doc_usage(tenant_id,agent_id,machine,user_name,name,app,seconds,ts) " +
                "VALUES(@t,@a,@m,@u,@n,@app,@sec,@ts)", conn);
            cmd.Parameters.AddWithValue("t", tenantId);
            cmd.Parameters.AddWithValue("a", agentId);
            cmd.Parameters.AddWithValue("m", machine);
            cmd.Parameters.AddWithValue("u", user);
            cmd.Parameters.AddWithValue("n", (object?)u.Name ?? DBNull.Value);
            cmd.Parameters.AddWithValue("app", (object?)u.App ?? DBNull.Value);
            cmd.Parameters.AddWithValue("sec", u.Seconds);
            cmd.Parameters.AddWithValue("ts", u.Ts);
            cmd.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<DocReportRow> DocReport(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, string? department = null)
    {
        var list = new List<DocReportRow>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT d.name, MAX(d.app), SUM(d.seconds), MAX(d.ts) FROM doc_usage d " +
            "LEFT JOIN agents a ON a.id=d.agent_id " +
            "WHERE d.tenant_id=@t AND (@a::text IS NULL OR d.agent_id=@a) AND d.ts>=@f AND d.ts<=@to AND (@dep::text IS NULL OR a.department=@dep) " +
            "GROUP BY d.name ORDER BY SUM(d.seconds) DESC LIMIT 300", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", (object?)agentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("f", fromUtcIso);
        cmd.Parameters.AddWithValue("to", toUtcIso);
        cmd.Parameters.AddWithValue("dep", (object?)department ?? DBNull.Value);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new DocReportRow(
                r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? 0 : r.GetInt64(2), r.IsDBNull(3) ? null : r.GetString(3)));
        return list;
    }

    public IReadOnlyList<TenantEvent> EventsInRange(string tenantId, string? agentId, string fromUtcIso, string toUtcIso, int limit)
    {
        var list = new List<TenantEvent>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "SELECT agent_id,machine,ts,op,path,sensitivity,src,user_name FROM events " +
            "WHERE tenant_id=@t AND (@a::text IS NULL OR agent_id=@a) AND ts>=@f AND ts<=@to ORDER BY id DESC LIMIT @l", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("a", (object?)agentId ?? DBNull.Value);
        cmd.Parameters.AddWithValue("f", fromUtcIso);
        cmd.Parameters.AddWithValue("to", toUtcIso);
        cmd.Parameters.AddWithValue("l", limit);
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new TenantEvent(
                r.IsDBNull(0) ? "" : r.GetString(0), r.IsDBNull(1) ? "" : r.GetString(1),
                r.IsDBNull(2) ? "" : r.GetString(2), r.IsDBNull(3) ? "" : r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4), r.IsDBNull(5) ? null : r.GetString(5),
                r.IsDBNull(6) ? null : r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7)));
        return list;
    }

    public TenantSettings GetSettings(string tenantId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT data FROM settings WHERE tenant_id=@t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        var data = cmd.ExecuteScalar() as string;
        return data is null ? new TenantSettings() : JsonSerializer.Deserialize<TenantSettings>(data) ?? new TenantSettings();
    }

    public void SaveSettings(string tenantId, TenantSettings settings)
    {
        settings.UpdatedAt = DateTime.UtcNow.ToString("o");
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO settings(tenant_id,data,updated_at) VALUES(@t,@d,@u) " +
            "ON CONFLICT (tenant_id) DO UPDATE SET data=@d, updated_at=@u", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.Add(new NpgsqlParameter("d", NpgsqlDbType.Jsonb) { Value = JsonSerializer.Serialize(settings) });
        cmd.Parameters.AddWithValue("u", DateTime.UtcNow);
        cmd.ExecuteNonQuery();
    }

    // --- Panel kullanıcıları + oturumlar ---
    private const string UserCols = "id,tenant_id,username,password_hash,role,display_name,active,created_at,last_login,must_change,department";
    private static PanelUser ReadUser(NpgsqlDataReader r) => new()
    {
        Id = r.GetString(0), TenantId = r.GetString(1), Username = r.GetString(2), PasswordHash = r.GetString(3),
        Role = r.GetString(4), DisplayName = r.IsDBNull(5) ? "" : r.GetString(5), Active = r.GetBoolean(6),
        CreatedAt = r.GetDateTime(7), LastLogin = r.IsDBNull(8) ? null : r.GetDateTime(8),
        MustChangePassword = !r.IsDBNull(9) && r.GetBoolean(9),
        Department = r.IsDBNull(10) ? "" : r.GetString(10)
    };

    public int CountUsers(string tenantId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT COUNT(*) FROM panel_users WHERE tenant_id=@t", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    public PanelUser? GetUser(string tenantId, string username)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand($"SELECT {UserCols} FROM panel_users WHERE tenant_id=@t AND lower(username)=lower(@u)", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("u", username);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

    public PanelUser? GetUserByUsernameGlobal(string username)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand($"SELECT {UserCols} FROM panel_users WHERE lower(username)=lower(@u) LIMIT 1", conn);
        cmd.Parameters.AddWithValue("u", username);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

    public PanelUser? GetUserById(string tenantId, string id)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand($"SELECT {UserCols} FROM panel_users WHERE tenant_id=@t AND id=@id", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadUser(r) : null;
    }

    public IReadOnlyList<UserView> ListUsers(string tenantId)
    {
        var list = new List<UserView>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand($"SELECT {UserCols} FROM panel_users WHERE tenant_id=@t ORDER BY created_at", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var u = ReadUser(r);
            list.Add(new UserView(u.Id, u.Username, u.Role, u.DisplayName, u.Active, u.Department, u.CreatedAt, u.LastLogin));
        }
        return list;
    }

    public PanelUser CreateUser(string tenantId, string username, string passwordHash, string role, string displayName, string department = "")
    {
        var u = new PanelUser
        {
            Id = "usr_" + Guid.NewGuid().ToString("N")[..10], TenantId = tenantId, Username = username,
            PasswordHash = passwordHash, Role = role, DisplayName = displayName, Active = true,
            Department = department ?? "", CreatedAt = DateTime.UtcNow
        };
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO panel_users(id,tenant_id,username,password_hash,role,display_name,active,created_at,last_login,must_change,department) " +
            "VALUES(@id,@t,@u,@ph,@r,@dn,true,@c,NULL,true,@dep)", conn);
        cmd.Parameters.AddWithValue("id", u.Id);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("u", username);
        cmd.Parameters.AddWithValue("ph", passwordHash);
        cmd.Parameters.AddWithValue("r", role);
        cmd.Parameters.AddWithValue("dn", displayName);
        cmd.Parameters.AddWithValue("c", u.CreatedAt);
        cmd.Parameters.AddWithValue("dep", u.Department);
        cmd.ExecuteNonQuery();
        return u;
    }

    public bool UpdateUser(string tenantId, string id, string? role, string? displayName, bool? active, string? passwordHash, string? department = null)
    {
        var sets = new List<string>();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand { Connection = conn };
        if (role is not null) { sets.Add("role=@r"); cmd.Parameters.AddWithValue("r", role); }
        if (displayName is not null) { sets.Add("display_name=@dn"); cmd.Parameters.AddWithValue("dn", displayName); }
        if (active is not null) { sets.Add("active=@a"); cmd.Parameters.AddWithValue("a", active.Value); }
        if (passwordHash is not null) { sets.Add("password_hash=@ph"); cmd.Parameters.AddWithValue("ph", passwordHash); }
        if (department is not null) { sets.Add("department=@dep"); cmd.Parameters.AddWithValue("dep", department); }
        if (sets.Count == 0) return true;
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", id);
        cmd.CommandText = $"UPDATE panel_users SET {string.Join(",", sets)} WHERE tenant_id=@t AND id=@id";
        return cmd.ExecuteNonQuery() > 0;
    }

    public bool DeleteUser(string tenantId, string id)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("DELETE FROM panel_users WHERE tenant_id=@t AND id=@id", conn);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", id);
        return cmd.ExecuteNonQuery() > 0;
    }

    public void UpdateLastLogin(string tenantId, string userId)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("UPDATE panel_users SET last_login=@ls WHERE tenant_id=@t AND id=@id", conn);
        cmd.Parameters.AddWithValue("ls", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.ExecuteNonQuery();
    }

    public void SetPassword(string tenantId, string userId, string passwordHash)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("UPDATE panel_users SET password_hash=@ph, must_change=false WHERE tenant_id=@t AND id=@id", conn);
        cmd.Parameters.AddWithValue("ph", passwordHash);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("id", userId);
        cmd.ExecuteNonQuery();
    }

    public string CreateSession(string tenantId, string userId, DateTime expiresUtc)
    {
        var token = Passwords.NewToken();
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand(
            "INSERT INTO sessions(token,tenant_id,user_id,created_at,expires_at) VALUES(@tk,@t,@u,@c,@e)", conn);
        cmd.Parameters.AddWithValue("tk", token);
        cmd.Parameters.AddWithValue("t", tenantId);
        cmd.Parameters.AddWithValue("u", userId);
        cmd.Parameters.AddWithValue("c", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("e", expiresUtc);
        cmd.ExecuteNonQuery();
        return token;
    }

    public (string userId, string tenantId)? GetSession(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("SELECT user_id,tenant_id,expires_at FROM sessions WHERE token=@tk", conn);
        cmd.Parameters.AddWithValue("tk", token);
        using var r = cmd.ExecuteReader();
        if (!r.Read()) return null;
        if (r.GetDateTime(2) < DateTime.UtcNow) return null;
        return (r.GetString(0), r.GetString(1));
    }

    public void DeleteSession(string token)
    {
        using var conn = _ds.OpenConnection();
        using var cmd = new NpgsqlCommand("DELETE FROM sessions WHERE token=@tk", conn);
        cmd.Parameters.AddWithValue("tk", token);
        cmd.ExecuteNonQuery();
    }

    public Overview BuildOverview(string tenantId, string? department = null) => OverviewBuilder.Build(this, tenantId, department);

    private static void Touch(NpgsqlConnection conn, string agentId)
    {
        using var cmd = new NpgsqlCommand("UPDATE agents SET last_seen=@ls WHERE id=@id", conn);
        cmd.Parameters.AddWithValue("ls", DateTime.UtcNow);
        cmd.Parameters.AddWithValue("id", agentId);
        cmd.ExecuteNonQuery();
    }

    private static string? MachineOf(NpgsqlConnection conn, string agentId)
    {
        using var cmd = new NpgsqlCommand("SELECT machine FROM agents WHERE id=@id", conn);
        cmd.Parameters.AddWithValue("id", agentId);
        return cmd.ExecuteScalar() as string;
    }
}
