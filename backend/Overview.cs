// Patron / yönetici özeti (executive overview) — mevcut IStore görünümlerinden hesaplanır.
// Store'a özel SQL yok; her iki depolama (SQLite/PG) bunu ortak kullanır.

namespace Argus.Server;

public static class OverviewBuilder
{
    public static Overview Build(IStore store, string tenantId, string? department = null)
    {
        var agents = store.AgentsView(tenantId);
        var alerts = store.AlertsForTenant(tenantId, 1000);
        var events = store.RecentEvents(tenantId, 2000);

        // Departman-bazlı izleyici: yalnız o departmanın uç noktaları + onların olay/uyarıları.
        if (!string.IsNullOrWhiteSpace(department))
        {
            agents = agents.Where(a => string.Equals(a.Department, department, StringComparison.OrdinalIgnoreCase)).ToList();
            var ids = agents.Select(a => a.AgentId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            alerts = alerts.Where(a => ids.Contains(a.AgentId)).ToList();
            events = events.Where(e => ids.Contains(e.AgentId)).ToList();
        }

        var endpoints = agents.Count;
        var online = agents.Count(a => a.Online);
        var totalActive = agents.Sum(a => a.ActiveSeconds);
        var totalIdle = agents.Sum(a => a.IdleSeconds);
        var workforcePct = (totalActive + totalIdle) > 0
            ? (int)Math.Round(100.0 * totalActive / (totalActive + totalIdle)) : 0;

        int CountType(string t) => alerts.Count(a => string.Equals(a.Alert.Type, t, StringComparison.OrdinalIgnoreCase));

        // Uç nokta başına hassas dosya + USB kopya sayısı (olay akışından).
        var sensPer = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var usbPer = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var e in events)
        {
            if (!string.IsNullOrEmpty(e.Sensitivity))
                sensPer[e.AgentId] = sensPer.GetValueOrDefault(e.AgentId) + 1;
            if (string.Equals(e.Op, "usb_copy", StringComparison.OrdinalIgnoreCase))
                usbPer[e.AgentId] = usbPer.GetValueOrDefault(e.AgentId) + 1;
        }

        // Risk skoru: hassas dosya (5) + USB kopya (3) + uyarı (2). En riskli 6 uç nokta.
        var topRisk = agents.Select(a =>
            {
                var sens = sensPer.GetValueOrDefault(a.AgentId);
                var usb = usbPer.GetValueOrDefault(a.AgentId);
                var score = (int)(sens * 5 + usb * 3 + a.AlertCount * 2);
                return new OverviewRisk(a.AgentId, a.Machine, a.User, score, sens, usb, a.AlertCount);
            })
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score)
            .Take(6)
            .ToList();

        OverviewPerson ToPerson(AgentView a)
        {
            var tot = a.ActiveSeconds + a.IdleSeconds;
            var pct = tot > 0 ? (int)Math.Round(100.0 * a.ActiveSeconds / tot) : 0;
            return new OverviewPerson(a.AgentId, a.Machine, a.User, a.ActiveSeconds, a.IdleSeconds, pct);
        }

        var withData = agents.Where(a => a.ActiveSeconds + a.IdleSeconds > 0).ToList();
        var mostActive = withData.OrderByDescending(a => a.ActiveSeconds).Take(5).Select(ToPerson).ToList();
        var leastActive = withData.OrderBy(a => a.ActiveSeconds).Take(5).Select(ToPerson).ToList();

        return new Overview(
            endpoints, online, endpoints - online,
            totalActive, totalIdle, workforcePct,
            CountType("sensitive_exfil"), CountType("usb_exfil"), CountType("mass_delete"), CountType("mass_copy"),
            alerts.Count,
            sensPer.Values.Sum(), usbPer.Values.Sum(),
            topRisk, mostActive, leastActive);
    }
}
