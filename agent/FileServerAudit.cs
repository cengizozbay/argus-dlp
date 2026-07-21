// FİLESERVER denetim kaynağı — Windows Güvenlik günlüğündeki dosya-erişim olaylarını (Event 4663)
// okur ve "kullanıcı X şu dosyayı sildi/değiştirdi" olayına çevirir — GERÇEK kullanıcı adıyla.
// Client FSW'nin aksine "kim yaptı"yı doğru verir (denetim logunda kullanıcı hesabı yazılıdır).
//
// ÖN KOŞUL (fileserver'da):
//   1) Paylaşım klasörüne SACL denetimi (Denetim > Everyone > Delete/Write başarı).
//   2) GPO: "Nesne Erişimini Denetle > Dosya Sistemi" = Başarı açık.
// Bu kaynak SYSTEM (servis) olarak çalışmalı — Güvenlik günlüğünü okumak için gerekir.

using System.Diagnostics.Eventing.Reader;
using System.Xml.Linq;

namespace Argus.Agent;

public sealed class FileServerAudit : IFileEventSource
{
    public event Action<FileEvent>? OnEvent;
    private EventLogWatcher? _watcher;
    private readonly string _machine = Environment.MachineName;
    private static readonly XNamespace Ns = "http://schemas.microsoft.com/win/2004/08/events/event";

    // Taşma önleme: aynı (kullanıcı|işlem|dosya) bu süre içinde tekrar ederse TEK kez gönder.
    // Yoğun sunucuda "değiştirdi" seli kuyruğu boğup başkalarının olayını düşürmesin.
    private const int DedupSeconds = 90;
    private readonly Dictionary<string, DateTime> _recent = new();
    private DateTime _lastPrune = DateTime.Now;

    // 4660 (silme) dosya adını taşımaz → önce açılan handle'ı (4656) önbelleğe alıp eşleştiririz.
    private readonly Dictionary<string, (string who, string path)> _handles = new();

    public void Start()
    {
        // 4656 = handle istendi (yol burada), 4660 = nesne silindi, 4663 = erişildi (yazma/değiştirme).
        var q = new EventLogQuery("Security", PathType.LogName,
            "*[System[(EventID=4656 or EventID=4660 or EventID=4663)]]");
        _watcher = new EventLogWatcher(q);
        _watcher.EventRecordWritten += OnRecord;
        _watcher.Enabled = true;   // canlı abonelik
    }

    public string Describe() => "Fileserver denetim (Güvenlik 4656/4660/4663)";

    private void OnRecord(object? sender, EventRecordWrittenEventArgs e)
    {
        try
        {
            var rec = e.EventRecord;
            if (rec is null) return;
            var doc = XDocument.Parse(rec.ToXml());
            string D(string name) => doc.Descendants(Ns + "Data")
                .FirstOrDefault(x => (string?)x.Attribute("Name") == name)?.Value ?? "";

            var user = D("SubjectUserName");
            var domain = D("SubjectDomainName");
            if (string.IsNullOrWhiteSpace(user) ||
                user.EndsWith("$") || user is "SYSTEM" or "LOCAL SERVICE" or "NETWORK SERVICE" or "ANONYMOUS LOGON")
                return;
            var who = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
            var handle = D("HandleId");

            switch (rec.Id)
            {
                case 4656:   // handle açıldı → SADECE silme (DELETE) erişimi istenenleri önbelleğe al (4660 bunu kullanır)
                    var ms656 = D("AccessMask").Trim();
                    if (ms656.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) ms656 = ms656[2..];
                    long m656 = 0; try { m656 = Convert.ToInt64(ms656, 16); } catch { }
                    if ((m656 & 0x10000) == 0) return;   // silme istenmemiş → önbelleğe alma (çoğu erişimi eler)
                    var p4656 = D("ObjectName");
                    if (!string.IsNullOrWhiteSpace(handle) && !string.IsNullOrWhiteSpace(p4656) && !p4656.EndsWith("\\"))
                        lock (_handles)
                        {
                            _handles[handle] = (who, p4656);
                            if (_handles.Count > 8000) _handles.Clear();   // basit taşma koruması
                        }
                    return;

                case 4660:   // NESNE SİLİNDİ → yolu önbellekteki handle'dan al
                    string? delPath = null;
                    lock (_handles) { if (_handles.TryGetValue(handle, out var h)) delPath = h.path; }
                    if (delPath is not null) Emit(who, "delete", delPath);
                    return;

                default:     // 4663 = erişim (yazma → değiştirdi; bazı silmeler burada DELETE ile de düşer)
                    var obj = D("ObjectName");
                    if (string.IsNullOrWhiteSpace(obj) || obj.EndsWith("\\")) return;
                    var ms = D("AccessMask").Trim();
                    if (ms.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) ms = ms[2..];
                    long mask = 0; try { mask = Convert.ToInt64(ms, 16); } catch { }
                    string? op = (mask & 0x10000) != 0 ? "delete" : (mask & 0x6) != 0 ? "modify" : null;
                    if (op is not null) Emit(who, op, obj);
                    return;
            }
        }
        catch { /* tek olay okunamadıysa geç */ }
    }

    private void Emit(string who, string op, string path)
    {
        if (NoiseFilter.IsNoise(op, path)) return;
        var key = who + "|" + op + "|" + path;
        lock (_recent)
        {
            if (_recent.TryGetValue(key, out var last) && (DateTime.Now - last).TotalSeconds < DedupSeconds) return;
            _recent[key] = DateTime.Now;
            if ((DateTime.Now - _lastPrune).TotalMinutes >= 5)
            {
                var cut = DateTime.Now.AddSeconds(-DedupSeconds);
                foreach (var k in _recent.Where(kv => kv.Value < cut).Select(kv => kv.Key).ToList()) _recent.Remove(k);
                _lastPrune = DateTime.Now;
            }
        }
        OnEvent?.Invoke(new FileEvent(DateTime.Now.ToString("o"), op, path, null, _machine, who));
    }

    public void Dispose()
    {
        try { if (_watcher is not null) { _watcher.Enabled = false; _watcher.Dispose(); } } catch { }
    }
}
