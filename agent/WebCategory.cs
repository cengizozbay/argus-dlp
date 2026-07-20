// Web sitesi kategorisi + risk seviyesi sınıflandırıcı (agent tarafı).
// Panel ile aynı mantık; ama uyarı üretmek için agent'ın da tanıması gerekir.
// Girdi domain (uzantı kuruluysa) ya da sekme başlığı (yalnız UIA) olabilir.
//
// Risk seviyeleri:
//   critical → Yetişkin/+18, İllegal (kumar/bahis/korsan) → UYARI üretilir
//   warning  → Sosyal medya, video/eğlence, oyun
//   info     → İş, alışveriş, haber, spor, arama
//   neutral  → Diğer

namespace Argus.Agent;

public enum WebRisk { Neutral, Info, Warning, Critical }

public static class WebCategory
{
    public sealed record Result(string Category, WebRisk Risk);

    // (kategori, risk, anahtar kelimeler). Sıra önemli: kritik olanlar en üstte.
    private static readonly (string Cat, WebRisk Risk, string[] Kw)[] Rules =
    {
        ("Yetişkin / +18", WebRisk.Critical, new[]
            { "porn", "porno", "xnxx", "xvideos", "xhamster", "pornhub", "brazzers", "onlyfans",
              "hentai", "redtube", "youporn", "erotik", "sikiş", "sikis", " sex ", "seks", "+18",
              "yetişkin", "yetiskin", "escort", "gay porn", "çıplak" }),
        ("İllegal / Riskli", WebRisk.Critical, new[]
            { "bet", "bahis", "iddaa", "casino", "kumar", "1xbet", "betboo", "rulet", "poker",
              "torrent", "warez", "crack", "keygen", "korsan", "dark web", "darkweb" }),
        ("Sosyal Medya", WebRisk.Warning, new[]
            { "facebook", "instagram", "twitter", "tiktok", "whatsapp", "telegram", "reddit",
              "snapchat", "threads", "sosyal", "social" }),
        ("Video / Eğlence", WebRisk.Warning, new[]
            { "youtube", "netflix", "twitch", "anime", "animeci", "dizi", "film", "izle", "movie",
              "stream", "müzik", "muzik", "music", "blutv", "exxen", "eğlence", "eglence" }),
        ("Oyun", WebRisk.Warning, new[]
            { "oyun", "game", "gaming", "steam", "valorant", "league of legends", "minecraft", "roblox" }),
        ("Haber", WebRisk.Info, new[] { "haber", "news", "gazete", "son dakika", "gündem", "gundem", "manşet" }),
        ("Spor", WebRisk.Info, new[] { "futbol", "maç", "transfer", "süper lig", "super lig", "nba", "basketbol" }),
        ("Alışveriş", WebRisk.Info, new[]
            { "trendyol", "hepsiburada", "amazon", "alışveriş", "alisveris", "mağaza", "magaza",
              "sepet", "satın al", "satin al", "indirim", "shop", "market" }),
        ("İş / Üretkenlik", WebRisk.Info, new[]
            { "github", "stackoverflow", "office", "outlook", "gmail", "drive", "docs", "notion",
              "slack", "teams", "zoom", "jira", "confluence" }),
    };

    public static Result Classify(string? domainOrTitle)
    {
        var d = (domainOrTitle ?? "").ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(d)) return new Result("Diğer", WebRisk.Neutral);
        foreach (var (cat, risk, kws) in Rules)
            foreach (var kw in kws)
                if (d.Contains(kw)) return new Result(cat, risk);
        return new Result("Diğer", WebRisk.Neutral);
    }
}
