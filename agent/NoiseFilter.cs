// Dosya olayı GÜRÜLTÜ filtresi.
// Sistem/uygulama/tarayıcı sürekli geçici dosya oluşturup siler (temp, cache, AppData, Windows...).
// Bunlar "kullanıcı eylemi" değil; panele düşünce sahte "toplu silme" alarmları ve şişmiş sayılar üretir.
// Bu filtre böyle yolları/dosyaları daha kaynağında eler. USB / harici disk olaylarına DOKUNMAZ (DLP kritik).

namespace Argus.Agent;

internal static class NoiseFilter
{
    // Yol içinde geçerse gürültü say (küçük harfe çevrilmiş, ters-eğik-çizgili yol üzerinde aranır).
    private static readonly string[] NoisePathParts =
    {
        @"\appdata\",                     // uygulama verisi (roaming/local/locallow) — kullanıcı belgesi değil
        @"\local\temp\", @"\temp\", @"\tmp\",
        @"\windows\", @"\programdata\",
        @"\program files\", @"\program files (x86)\",
        @"\$recycle.bin\", @"\system volume information\", @"\recovery\",
        @"\microsoft\", @"\packages\", @"\package cache\", @"\windowsapps\",
        @"\google\chrome\", @"\bravesoftware\", @"\mozilla\", @"\opera software\", @"\vivaldi\",
        @"\inetcache\", @"\webcache\", @"\code cache\", @"\gpucache\", @"\service worker\",
        @"\cache\", @"\caches\", @"\cache2\", @"\crashpad\", @"\crashdumps\",
        @"\.git\", @"\node_modules\", @"\.vscode\", @"\.nuget\", @"\.gradle\", @"\.m2\",
        @"\ntuser", @"\appdata\locallow\",
    };

    // Dosya adı bu kalıplarda ise gürültü.
    private static readonly string[] NoiseNamePrefixes = { "~$", "~wr", ".~lock", "~$" };
    private static readonly string[] NoiseExtensions =
    {
        ".tmp", ".temp", ".log", ".etl", ".dmp", ".crdownload", ".part", ".partial",
        ".download", ".~tmp", ".ldb", ".lock", ".swp", ".bak~", ".pyc", ".class",
    };
    private static readonly string[] NoiseExactNames =
    {
        "thumbs.db", "desktop.ini", ".ds_store", "iconcache.db", "ntuser.dat", "ntuser.ini",
    };

    public static bool IsNoise(string? op, string? path)
    {
        // USB / harici disk olayları asla gürültü değildir (takılma/çıkarma/medyaya kopya).
        if (op is "usb_copy" or "usb_insert" or "usb_remove") return false;
        if (string.IsNullOrWhiteSpace(path)) return false;

        var p = path.Replace('/', '\\').ToLowerInvariant();

        foreach (var part in NoisePathParts)
            if (p.Contains(part)) return true;

        var name = p.Substring(p.LastIndexOf('\\') + 1);
        if (name.Length == 0) return true;                              // klasör olayı → gürültü

        foreach (var n in NoiseExactNames) if (name == n) return true;
        foreach (var pre in NoiseNamePrefixes) if (name.StartsWith(pre)) return true;
        foreach (var ext in NoiseExtensions) if (name.EndsWith(ext)) return true;

        return false;
    }
}
