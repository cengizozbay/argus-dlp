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

    public void Start()
    {
        // Yalnız 4663 (bir nesneye erişildi) — dosya sistemi denetim olayları.
        var q = new EventLogQuery("Security", PathType.LogName, "*[System[(EventID=4663)]]");
        _watcher = new EventLogWatcher(q);
        _watcher.EventRecordWritten += OnRecord;
        _watcher.Enabled = true;   // canlı abonelik: yeni olaylar anında gelir
    }

    public string Describe() => "Fileserver denetim (Güvenlik 4663)";

    private void OnRecord(object? sender, EventRecordWrittenEventArgs e)
    {
        try
        {
            if (e.EventRecord is null) return;
            var doc = XDocument.Parse(e.EventRecord.ToXml());
            string D(string name) => doc.Descendants(Ns + "Data")
                .FirstOrDefault(x => (string?)x.Attribute("Name") == name)?.Value ?? "";

            var user = D("SubjectUserName");
            var domain = D("SubjectDomainName");
            var obj = D("ObjectName");
            var maskStr = D("AccessMask");

            if (string.IsNullOrWhiteSpace(user) || string.IsNullOrWhiteSpace(obj)) return;
            // Makine/sistem hesaplarını ele (gürültü): NAME$ , SYSTEM, LOCAL/NETWORK SERVICE, ANONYMOUS
            if (user.EndsWith("$") || user is "SYSTEM" or "LOCAL SERVICE" or "NETWORK SERVICE" or "ANONYMOUS LOGON") return;
            // Klasör/geçici gürültüsünü ele
            if (obj.EndsWith("\\") || NoiseFilter.IsNoise("modify", obj)) return;

            long mask = 0;
            try { mask = Convert.ToInt64(maskStr.Trim(), 16); } catch { }
            // 0x10000 = DELETE · 0x2 = WriteData/AddFile · 0x4 = AppendData
            string? op = (mask & 0x10000) != 0 ? "delete"
                        : (mask & 0x6) != 0 ? "modify"
                        : null;
            if (op is null) return;   // salt-okuma vb. atla (çok gürültülü)

            var who = string.IsNullOrWhiteSpace(domain) ? user : $"{domain}\\{user}";
            OnEvent?.Invoke(new FileEvent(DateTime.Now.ToString("o"), op, obj, null, _machine, who));
        }
        catch { /* tek olay okunamadıysa geç */ }
    }

    public void Dispose()
    {
        try { if (_watcher is not null) { _watcher.Enabled = false; _watcher.Dispose(); } } catch { }
    }
}
