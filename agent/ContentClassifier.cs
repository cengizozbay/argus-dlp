// Hassas içerik sınıflandırıcı (DLP çekirdeği).
//
// Bir dosyanın İÇERİĞİNİ tarayıp hassas veri barındırıp barındırmadığını belirler.
// Argus'u "aktivite izleme"den gerçek DLP'ye taşıyan parça budur: "kopyalama" olayı artık
// yalnız dosya adına değil, TAŞINAN VERİYE göre değerlendirilir. Böylece panelde
// "personel USB'ye TC kimlik no içeren 3 dosya kopyaladı" gibi anlamlı uyarı çıkar.
//
// Tespit edilenler (Türkiye'ye uygun, doğrulamalı → düşük yanlış-pozitif):
//   - TC Kimlik No  (11 hane + resmî sağlama algoritması)
//   - IBAN          (TR + mod-97 doğrulaması)
//   - Kredi kartı   (13-19 hane + Luhn doğrulaması)
//   - Anahtar kelime (politika ile gelen liste: "gizli", "confidential", "maaş" …)
//
// Kapsam: yalnız metin çıkarılabilen dosyalar. Düz metin (.txt/.csv/.json/.xml/.html/.md/.sql…)
//   ve Office (.docx/.xlsx/.pptx — ZIP içindeki XML'den metin çıkarılır) taranır.
//   8 MB üzeri ya da tanınmayan/ikili tür taranmaz (performans + gürültü). Dosya kilitliyse atlanır.
// Not: tarama olay ipliğinde senkron çalışır; olay hızı düşük ve boyut sınırlı olduğundan güvenli.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Argus.Agent;

public sealed class ContentClassifier
{
    private const long MaxBytes = 8 * 1024 * 1024;   // 8 MB üzeri taranmaz

    private volatile bool _enabled = true;
    private volatile string[] _keywords = Array.Empty<string>();

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".log", ".json", ".xml", ".html", ".htm",
        ".md", ".sql", ".ini", ".cfg", ".yml", ".yaml", ".rtf", ".eml"
    };
    private static readonly HashSet<string> OfficeExt = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx" };

    // 11 hane; ilk hane 0 olamaz. Sağlama ayrıca doğrular.
    private static readonly Regex RxTc = new(@"(?<![0-9])[1-9][0-9]{10}(?![0-9])", RegexOptions.Compiled);
    // TR IBAN: TR + 24 karakter (toplam 26). Boşluklu yazımı da yakala, sonra sadeleştir.
    private static readonly Regex RxIban = new(@"TR[0-9]{2}(?:[ ]?[0-9A-Z]){22}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // 13-19 hane, aralarında boşluk/tire olabilir (kart yazımı). Luhn ayrıca doğrular.
    private static readonly Regex RxCard = new(@"(?<![0-9])(?:[0-9][ -]?){13,19}(?![0-9])", RegexOptions.Compiled);

    // Politikadan gelen ayarları uygula (panelde değişince canlı geçerli olur).
    public void Configure(bool enabled, IEnumerable<string>? keywords)
    {
        _enabled = enabled;
        _keywords = (keywords ?? Enumerable.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool Enabled => _enabled;

    // Dosyayı tara. Bulgu varsa (etiket, adet); yoksa (null, 0). Hiçbir durumda fırlatmaz.
    public (string? Label, int Hits) Classify(string path)
    {
        if (!_enabled) return (null, 0);
        try
        {
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return (null, 0);
            var ext = Path.GetExtension(path);
            var isText = TextExt.Contains(ext);
            var isOffice = OfficeExt.Contains(ext);
            if (!isText && !isOffice) return (null, 0);

            var fi = new FileInfo(path);
            if (fi.Length == 0 || fi.Length > MaxBytes) return (null, 0);

            var text = isOffice ? ReadOffice(path) : ReadText(path);
            if (string.IsNullOrEmpty(text)) return (null, 0);

            return Inspect(text);
        }
        catch { return (null, 0); }   // kilitli/erişilemez dosya → sessizce atla
    }

    // Metni tara, tür başına EN AZ bir eşleşmeyi say. Etiket okunur sırayla birleştirilir.
    private (string? Label, int Hits) Inspect(string text)
    {
        var labels = new List<string>();
        var hits = 0;

        var tc = CountValid(RxTc.Matches(text), IsValidTc);
        if (tc > 0) { labels.Add("TC Kimlik"); hits += tc; }

        var iban = CountValid(RxIban.Matches(text), IsValidIban);
        if (iban > 0) { labels.Add("IBAN"); hits += iban; }

        var card = CountValid(RxCard.Matches(text), IsValidCard);
        if (card > 0) { labels.Add("Kredi Kartı"); hits += card; }

        if (_keywords.Length > 0)
        {
            var kwHits = 0;
            foreach (var kw in _keywords)
                if (text.IndexOf(kw, StringComparison.OrdinalIgnoreCase) >= 0) kwHits++;
            if (kwHits > 0) { labels.Add("Anahtar kelime"); hits += kwHits; }
        }

        return labels.Count == 0 ? (null, 0) : (string.Join(", ", labels), hits);
    }

    private static int CountValid(MatchCollection matches, Func<string, bool> valid)
    {
        var n = 0;
        var seen = new HashSet<string>();
        foreach (Match m in matches)
        {
            var v = m.Value;
            if (valid(v) && seen.Add(Normalize(v))) n++;   // aynı numara iki kez sayılmaz
        }
        return n;
    }

    private static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s) if (!char.IsWhiteSpace(c) && c != '-') sb.Append(char.ToUpperInvariant(c));
        return sb.ToString();
    }

    // --- Doğrulayıcılar ---

    // TC Kimlik No resmî sağlaması.
    private static bool IsValidTc(string s)
    {
        if (s.Length != 11 || s[0] == '0') return false;
        var d = new int[11];
        for (var i = 0; i < 11; i++)
        {
            if (s[i] < '0' || s[i] > '9') return false;
            d[i] = s[i] - '0';
        }
        var oddSum = d[0] + d[2] + d[4] + d[6] + d[8];
        var evenSum = d[1] + d[3] + d[5] + d[7];
        var d10 = ((oddSum * 7) - evenSum) % 10;
        if (d10 < 0) d10 += 10;
        if (d10 != d[9]) return false;
        var total = 0;
        for (var i = 0; i < 10; i++) total += d[i];
        return total % 10 == d[10];
    }

    // IBAN mod-97 doğrulaması (ISO 13616). TR IBAN = 26 karakter.
    private static bool IsValidIban(string raw)
    {
        var s = Normalize(raw);
        if (s.Length != 26 || !s.StartsWith("TR", StringComparison.OrdinalIgnoreCase)) return false;

        // İlk 4 karakteri sona al, harfleri sayıya çevir (A=10..Z=35), mod 97 == 1 olmalı.
        var moved = s.Substring(4) + s.Substring(0, 4);
        var rem = 0;
        foreach (var c in moved)
        {
            int val;
            if (c >= '0' && c <= '9') val = c - '0';
            else if (c >= 'A' && c <= 'Z') val = c - 'A' + 10;
            else return false;
            rem = val > 9 ? (rem * 100 + val) % 97 : (rem * 10 + val) % 97;
        }
        return rem == 1;
    }

    // Kredi kartı Luhn doğrulaması + zayıf desenleri ele (hepsi aynı hane vb.).
    private static bool IsValidCard(string raw)
    {
        var digits = raw.Where(char.IsDigit).ToArray();
        if (digits.Length < 13 || digits.Length > 19) return false;
        if (digits.All(c => c == digits[0])) return false;   // 000…/111… → gürültü

        var sum = 0; var alt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var n = digits[i] - '0';
            if (alt) { n *= 2; if (n > 9) n -= 9; }
            sum += n;
            alt = !alt;
        }
        return sum % 10 == 0;
    }

    // --- Metin çıkarma ---

    private static string ReadText(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var sr = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return sr.ReadToEnd();
    }

    // Office = ZIP. İçindeki XML parçalarından (word/xl/ppt) metni çıkarır (etiketleri atarak).
    private static string ReadOffice(string path)
    {
        var sb = new StringBuilder();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Read);
        foreach (var entry in zip.Entries)
        {
            var n = entry.FullName;
            if (!n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
            if (!(n.StartsWith("word/") || n.StartsWith("xl/") || n.StartsWith("ppt/"))) continue;
            if (sb.Length > MaxBytes) break;
            try
            {
                using var es = entry.Open();
                using var sr = new StreamReader(es, Encoding.UTF8);
                var xml = sr.ReadToEnd();
                sb.Append(StripTags(xml)).Append(' ');
            }
            catch { /* bozuk parça → atla */ }
        }
        return sb.ToString();
    }

    // XML etiketlerini boşlukla değiştir (basit metin çıkarımı; tam DOM'a gerek yok).
    private static string StripTags(string xml)
    {
        var sb = new StringBuilder(xml.Length);
        var inside = false;
        foreach (var c in xml)
        {
            if (c == '<') inside = true;
            else if (c == '>') { inside = false; sb.Append(' '); }
            else if (!inside) sb.Append(c);
        }
        return sb.ToString();
    }
}
