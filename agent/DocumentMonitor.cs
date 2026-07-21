// Dosya-içi süre: önplandaki belge uygulamasının pencere başlığından açık belgeyi çıkarır.
// "Rapor.docx - Word" → belge "Rapor.docx". Süre belge başına biriktirilir (zaman serisi).
// Not: başlık-tabanlı sezgisel; çoğu ofis/editör "Belge - Uygulama" kalıbı kullanır.

namespace Argus.Agent;

public static class DocumentMonitor
{
    // exe (uzantısız süreç adı) → uygulama adı. Belge açan uygulamalar.
    private static readonly Dictionary<string, string> DocApps = new(StringComparer.OrdinalIgnoreCase)
    {
        ["winword"] = "Word", ["excel"] = "Excel", ["powerpnt"] = "PowerPoint", ["onenote"] = "OneNote",
        ["acrord32"] = "Adobe Reader", ["acrobat"] = "Acrobat", ["foxitreader"] = "Foxit", ["sumatrapdf"] = "SumatraPDF",
        ["notepad"] = "Not Defteri", ["notepad++"] = "Notepad++", ["wordpad"] = "WordPad",
        ["code"] = "VS Code", ["sublime_text"] = "Sublime", ["devenv"] = "Visual Studio",
        ["wps"] = "WPS", ["et"] = "WPS Sheet", ["wpp"] = "WPS Presentation", ["hwp"] = "Hancom",
    };

    public static bool IsDocApp(string exe) => DocApps.ContainsKey(exe);
    public static string AppName(string exe) => DocApps.TryGetValue(exe, out var n) ? n : exe;

    // Tarayıcıda açılan belgeleri de Belge'ye düşürmek için: ad bir belge dosyası uzantısıyla mı bitiyor?
    private static readonly HashSet<string> DocExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".docx", ".doc", ".xlsx", ".xls", ".xlsm", ".csv", ".pptx", ".ppt",
        ".odt", ".ods", ".odp", ".rtf", ".txt",
    };
    public static bool IsDocumentFile(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var dot = name.LastIndexOf('.');
        return dot > 0 && DocExtensions.Contains(name.Substring(dot));
    }

    // Başlıktan belge adını çıkar: ilk " - " öncesi (çoğu uygulamada belge adı en başta).
    // Ör. "Bütçe.xlsx - Excel" → "Bütçe.xlsx" · "prog.cs - proje - Visual Studio Code" → "prog.cs".
    public static string? DocName(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var t = title.Trim().TrimStart('●', '*', ' ');   // değişiklik göstergesi işaretleri
        var idx = t.IndexOf(" - ", StringComparison.Ordinal);
        var name = (idx > 0 ? t.Substring(0, idx) : t).Trim();
        // Anlamsız/boş başlıkları ele.
        if (name.Length < 2) return null;
        if (name is "Belge1" or "Document1" or "Adsız" or "Untitled" or "Kitap1" or "Book1" or "Sunu1" or "Presentation1")
            return null;   // henüz kaydedilmemiş boş belge
        return name;
    }
}
