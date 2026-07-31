// Depolama katmanı testleri — özellikle 2026-07-31'de eklenen GERİYE DÖNÜK sorgular.
// SqliteStore ile koşar; PostgresStore aynı IStore sözleşmesini uygular (SQL'leri paraleldir).

using Argus.Server;
using Xunit;

namespace Argus.Tests;

public sealed class StoreTests : IDisposable
{
    private readonly string _dir;
    private readonly SqliteStore _store;
    private readonly Tenant _tenant;
    private readonly Argus.Server.Agent _agentA;
    private readonly Argus.Server.Agent _agentB;

    public StoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "argus-store-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
        _store = new SqliteStore(Path.Combine(_dir, "test.db"));
        _tenant = _store.SeedTenant("Test A.Ş.", "tk_test_001");
        _agentA = _store.EnrollAgent(_tenant.Id, "PC-AHMET", "ahmet", "lan");
        _agentB = _store.EnrollAgent(_tenant.Id, "PC-AYSE", "ayse", "lan");
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Ts(DateTime d) => d.ToString("o");

    private static AlertDto Alert(string type, string severity, DateTime ts) =>
        new(Ts(ts), severity, "kural-" + type, type, "Baslik", "Detay", 1, 30, new List<string>(), "PC", "kullanici");

    private static EventDto Event(string op, string path, DateTime ts, string? sensitivity = null) =>
        new(Ts(ts), null, op, null, null, null, path, sensitivity);

    // --- Geriye dönük UYARI sorgusu ---

    [Fact]
    public void UyariGecmisiTarihAraligiylaGelir()
    {
        var bugun = DateTime.UtcNow;
        _store.AddAlerts(_tenant.Id, _agentA.Id, new[] { Alert("usb_exfil", "critical", bugun) });

        var hepsi = _store.AlertsInRange(_tenant.Id, null, bugun.AddDays(-1), bugun.AddDays(1), null, null, 100, 0);
        Assert.Single(hepsi);

        // Aralık dışı → boş dönmeli (eskiden bu sorgu hiç yoktu, panel son 200'e mahkûmdu).
        var disarida = _store.AlertsInRange(_tenant.Id, null, bugun.AddDays(-10), bugun.AddDays(-5), null, null, 100, 0);
        Assert.Empty(disarida);
    }

    [Fact]
    public void UyariGecmisiTipeGoreSuzulur()
    {
        var now = DateTime.UtcNow;
        _store.AddAlerts(_tenant.Id, _agentA.Id, new[]
        {
            Alert("usb_exfil", "critical", now),
            Alert("mass_delete", "warning", now),
            Alert("mass_copy", "warning", now)
        });

        var usb = _store.AlertsInRange(_tenant.Id, null, now.AddDays(-1), now.AddDays(1), "usb_exfil", null, 100, 0);
        Assert.Single(usb);
        Assert.Equal("usb_exfil", usb[0].Alert.Type);

        var kritik = _store.AlertsInRange(_tenant.Id, null, now.AddDays(-1), now.AddDays(1), null, "critical", 100, 0);
        Assert.Single(kritik);
        Assert.Equal("critical", kritik[0].Alert.Severity);
    }

    [Fact]
    public void UyariGecmisiKisiyeGoreSuzulur()
    {
        var now = DateTime.UtcNow;
        _store.AddAlerts(_tenant.Id, _agentA.Id, new[] { Alert("usb_exfil", "critical", now) });
        _store.AddAlerts(_tenant.Id, _agentB.Id, new[] { Alert("mass_copy", "warning", now) });

        var sadeceA = _store.AlertsInRange(_tenant.Id, _agentA.Id, now.AddDays(-1), now.AddDays(1), null, null, 100, 0);
        Assert.Single(sadeceA);
        Assert.Equal(_agentA.Id, sadeceA[0].AgentId);
    }

    [Fact]
    public void UyariGecmisiSayfalanir()
    {
        var now = DateTime.UtcNow;
        _store.AddAlerts(_tenant.Id, _agentA.Id,
            Enumerable.Range(0, 25).Select(i => Alert("mass_copy", "warning", now.AddSeconds(-i))).ToList());

        var s1 = _store.AlertsInRange(_tenant.Id, null, now.AddDays(-1), now.AddDays(1), null, null, 10, 0);
        var s2 = _store.AlertsInRange(_tenant.Id, null, now.AddDays(-1), now.AddDays(1), null, null, 10, 10);
        var s3 = _store.AlertsInRange(_tenant.Id, null, now.AddDays(-1), now.AddDays(1), null, null, 10, 20);
        Assert.Equal(10, s1.Count);
        Assert.Equal(10, s2.Count);
        Assert.Equal(5, s3.Count);
    }

    // --- Geriye dönük OLAY sorgusu ---

    [Fact]
    public void OlayGecmisiIslemeGoreSuzulur()
    {
        var now = DateTime.Now;   // olay damgaları YEREL saatle tutulur
        _store.AddEvents(_tenant.Id, _agentA.Id, new[]
        {
            Event("usb_copy", @"E:\gizli.xlsx", now),
            Event("usb_insert", "E:\\ (Kingston)", now),
            Event("delete", @"C:\a.txt", now),
            Event("copy", @"C:\b.txt", now)
        });

        var from = Ts(now.AddDays(-1));
        var to = Ts(now.AddDays(1));

        // op="usb" → tüm USB olayları (usb_copy + usb_insert)
        var usb = _store.EventsInRange(_tenant.Id, null, from, to, 100, "usb");
        Assert.Equal(2, usb.Count);
        Assert.All(usb, e => Assert.StartsWith("usb", e.Op));

        // tek işlem
        var silme = _store.EventsInRange(_tenant.Id, null, from, to, 100, "delete");
        Assert.Single(silme);
    }

    [Fact]
    public void OlayGecmisiYalnizHassasSuzulur()
    {
        var now = DateTime.Now;
        _store.AddEvents(_tenant.Id, _agentA.Id, new[]
        {
            Event("usb_copy", @"E:\personel.xlsx", now, "TC Kimlik"),
            Event("usb_copy", @"E:\resim.png", now)
        });

        var hassas = _store.EventsInRange(_tenant.Id, null, Ts(now.AddDays(-1)), Ts(now.AddDays(1)),
            100, null, sensitiveOnly: true);
        Assert.Single(hassas);
        Assert.Equal("TC Kimlik", hassas[0].Sensitivity);
    }

    [Fact]
    public void OlayGecmisiSayfalanir()
    {
        var now = DateTime.Now;
        _store.AddEvents(_tenant.Id, _agentA.Id,
            Enumerable.Range(0, 15).Select(i => Event("copy", $@"C:\d{i}.txt", now)).ToList());

        var from = Ts(now.AddDays(-1));
        var to = Ts(now.AddDays(1));
        var s1 = _store.EventsInRange(_tenant.Id, null, from, to, 10, null, false, 0);
        var s2 = _store.EventsInRange(_tenant.Id, null, from, to, 10, null, false, 10);
        Assert.Equal(10, s1.Count);
        Assert.Equal(5, s2.Count);
        // Sayfalar çakışmamalı
        Assert.Empty(s1.Select(x => x.Path).Intersect(s2.Select(x => x.Path)));
    }

    [Fact]
    public void OlayGecmisiAralikDisiniGetirmez()
    {
        var now = DateTime.Now;
        _store.AddEvents(_tenant.Id, _agentA.Id, new[] { Event("copy", @"C:\eski.txt", now.AddDays(-30)) });

        var son7 = _store.EventsInRange(_tenant.Id, null, Ts(now.AddDays(-7)), Ts(now.AddDays(1)), 100);
        Assert.Empty(son7);

        var son60 = _store.EventsInRange(_tenant.Id, null, Ts(now.AddDays(-60)), Ts(now.AddDays(1)), 100);
        Assert.Single(son60);
    }

    // --- Firma izolasyonu (multi-tenant) ---

    [Fact]
    public void BaskaFirmaninUyarisiGorunmez()
    {
        var digerFirma = _store.SeedTenant("Rakip Ltd.", "tk_test_002");
        var digerAgent = _store.EnrollAgent(digerFirma.Id, "PC-X", "x", "lan");
        var now = DateTime.UtcNow;
        _store.AddAlerts(digerFirma.Id, digerAgent.Id, new[] { Alert("usb_exfil", "critical", now) });

        var bizim = _store.AlertsInRange(_tenant.Id, null, now.AddDays(-1), now.AddDays(1), null, null, 100, 0);
        Assert.Empty(bizim);
    }

    // --- USB politikası normalizasyonu (eski/yeni alan uyumu) ---

    [Fact]
    public void EskiUsbBlockedAlaniYeniAlanaCevrilir()
    {
        // Eski panelden gelen kayıt: yalnız UsbBlocked dolu.
        _store.SaveSettings(_tenant.Id, new TenantSettings { UsbBlocked = true });
        var s = _store.GetSettings(_tenant.Id);
        Assert.Equal("block", s.UsbAccess);
        Assert.True(s.UsbBlocked);
    }

    [Fact]
    public void YeniUsbAccessAlaniEskiAlaniSurer()
    {
        _store.SaveSettings(_tenant.Id, new TenantSettings { UsbAccess = "readonly" });
        var s = _store.GetSettings(_tenant.Id);
        Assert.Equal("readonly", s.UsbAccess);
        Assert.False(s.UsbBlocked);   // salt-okunur "engelli" değildir
    }

    [Fact]
    public void GecersizUsbAccessSerbestSayilir()
    {
        _store.SaveSettings(_tenant.Id, new TenantSettings { UsbAccess = "saçmalık" });
        Assert.Equal("allow", _store.GetSettings(_tenant.Id).UsbAccess);
    }

    // --- Parola güvenliği ---

    [Fact]
    public void ParolaHashiDogrulanir()
    {
        var h = Passwords.Hash("Gizli.123");
        Assert.True(Passwords.Verify("Gizli.123", h));
        Assert.False(Passwords.Verify("gizli.123", h));
        Assert.False(Passwords.Verify("", h));
    }

    [Fact]
    public void AyniParolaFarkliHashUretir()
    {
        // Tuz kullanılmazsa aynı parola aynı hash'i verir → sözlük saldırısına açık olurdu.
        Assert.NotEqual(Passwords.Hash("aynisi"), Passwords.Hash("aynisi"));
    }
}
