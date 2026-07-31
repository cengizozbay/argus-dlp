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
//   - Şifreli/parola korumalı dosya (veri okunamaz → tek başına şüphe sinyalidir)
//
// KAPSAM (2026-07-31'de genişletildi — önceki sürüm yalnız düz metin + yeni Office okuyordu):
//   düz metin · yeni Office (.docx/.xlsx/.pptx) · **PDF** · **eski Office (.doc/.xls/.ppt)** ·
//   **ZIP arşivi içi (özyinelemeli)**. Bunlar taranmadığı sürece sistem "temiz" diyordu —
//   sahadaki en hassas belgeler (fatura, sözleşme, kimlik taraması) tam olarak bu formatlardaydı.
//
// SINIRLAR (bilerek):
//   - TARANMIŞ (görüntü) PDF'te metin yoktur → OCR gerekir, kapsam dışı.
//   - RAR/7z tescilli formatlardır; sıfır-bağımlılık kuralı gereği açılmaz — ama şifreli/açılamaz
//     arşiv olarak İŞARETLENİR (sızıntı sinyali kaybolmaz).
//   - Boyut tavanı politikadan gelir (varsayılan 32 MB); üstü "taranmadı" sayılır.

using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Argus.Agent;

public sealed class ContentClassifier
{
    private const int MaxHits = 500;          // bu kadar bulgudan sonra taramayı sürdürmenin faydası yok
    private const int MaxArchiveEntries = 300;
    private const int MaxArchiveDepth = 2;    // zip içinde zip → 2 kat yeter, zip-bombasını keser

    private volatile bool _enabled = true;
    private volatile string[] _keywords = Array.Empty<string>();
    private volatile string[] _keywordsFolded = Array.Empty<string>();
    private volatile int _maxBytes = 32 * 1024 * 1024;
    private volatile bool _scanArchives = true;
    private volatile bool _flagEncrypted = true;

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".csv", ".tsv", ".log", ".json", ".xml", ".html", ".htm",
        ".md", ".sql", ".ini", ".cfg", ".yml", ".yaml", ".rtf", ".eml", ".vcf"
    };
    private static readonly HashSet<string> OoxmlExt = new(StringComparer.OrdinalIgnoreCase)
        { ".docx", ".xlsx", ".pptx", ".docm", ".xlsm", ".pptm" };
    // Eski ikili Office + Outlook iletisi → yazdırılabilir dizi çıkarımıyla taranır.
    private static readonly HashSet<string> LegacyExt = new(StringComparer.OrdinalIgnoreCase)
        { ".doc", ".xls", ".ppt", ".msg", ".mdb", ".dbf", ".pst" };
    private static readonly HashSet<string> ZipExt = new(StringComparer.OrdinalIgnoreCase)
        { ".zip" };
    // Açamadığımız arşivler — içerik görülemez, ama "arşivle ve kaçır" sinyali kaydedilir.
    private static readonly HashSet<string> OpaqueArchiveExt = new(StringComparer.OrdinalIgnoreCase)
        { ".rar", ".7z", ".gz", ".tar", ".tgz", ".bz2", ".cab", ".iso" };

    // 11 hane; ilk hane 0 olamaz. Sağlama ayrıca doğrular.
    private static readonly Regex RxTc = new(@"(?<![0-9])[1-9][0-9]{10}(?![0-9])", RegexOptions.Compiled);
    // TR IBAN: TR + 24 karakter (toplam 26). Boşluklu yazımı da yakala, sonra sadeleştir.
    private static readonly Regex RxIban = new(@"TR[0-9]{2}(?:[ ]?[0-9A-Z]){22}", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    // 13-19 hane, aralarında boşluk/tire olabilir (kart yazımı). Luhn ayrıca doğrular.
    private static readonly Regex RxCard = new(@"(?<![0-9])(?:[0-9][ -]?){13,19}(?![0-9])", RegexOptions.Compiled);

    // Politikadan gelen ayarları uygula (panelde değişince canlı geçerli olur).
    public void Configure(bool enabled, IEnumerable<string>? keywords, int maxMb = 32,
                          bool scanArchives = true, bool flagEncrypted = true)
    {
        _enabled = enabled;
        _keywords = (keywords ?? Enumerable.Empty<string>())
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _keywordsFolded = _keywords.Select(TrFold).ToArray();
        _maxBytes = Math.Clamp(maxMb, 1, 512) * 1024 * 1024;
        _scanArchives = scanArchives;
        _flagEncrypted = flagEncrypted;
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

            // Açılamayan arşiv türleri: içerik görülemez ama hareketin kendisi sinyaldir.
            if (_flagEncrypted && OpaqueArchiveExt.Contains(ext))
                return ("Açılamayan arşiv", 1);

            var fi = new FileInfo(path);
            if (fi.Length == 0) return (null, 0);
            if (fi.Length > _maxBytes) return (null, 0);   // tavan üstü → taranmadı

            var acc = new Findings(_keywordsFolded);
            ScanFile(path, ext, acc, depth: 0);
            return acc.Result();
        }
        catch { return (null, 0); }   // kilitli/erişilemez dosya → sessizce atla
    }

    // Tek bir dosyayı (ya da arşiv girdisini) türüne göre uygun çıkarıcıya yönlendirir.
    private void ScanFile(string path, string ext, Findings acc, int depth)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            ScanStream(fs, ext, acc, depth, (int)new FileInfo(path).Length);
        }
        catch { /* kilitli/erişilemez → atla */ }
    }

    private void ScanStream(Stream s, string ext, Findings acc, int depth, int sizeHint)
    {
        long budget = _maxBytes;

        if (OoxmlExt.Contains(ext))
        {
            // Parola korumalı OOXML aslında OLE2 kabuğudur → ZIP olarak açılamaz.
            try { acc.Feed(TextExtract.Ooxml(s, budget)); }
            catch { if (_flagEncrypted) acc.MarkEncrypted(); }
            return;
        }

        if (ZipExt.Contains(ext))
        {
            if (_scanArchives && depth < MaxArchiveDepth) ScanZip(s, acc, depth);
            else if (_flagEncrypted) acc.MarkEncrypted();
            return;
        }

        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            var buf = ReadAll(s, (int)Math.Min(budget, Math.Max(sizeHint, 4096)));
            if (buf.Length == 0) return;
            if (TextExtract.PdfIsEncrypted(buf)) { if (_flagEncrypted) acc.MarkEncrypted(); return; }
            acc.Feed(TextExtract.Pdf(buf, budget));
            return;
        }

        if (TextExt.Contains(ext)) { acc.Feed(TextExtract.PlainText(s, budget)); return; }
        if (LegacyExt.Contains(ext)) { acc.Feed(TextExtract.BinaryStrings(s, budget)); return; }
        // Tanınmayan tür → taranmaz (ikili gürültü + performans).
    }

    // ZIP içindeki her girdiyi kendi türüne göre tarar. Şifreli girdi açılamaz → işaretlenir.
    private void ScanZip(Stream s, Findings acc, int depth)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true); }
        catch { if (_flagEncrypted) acc.MarkEncrypted(); return; }   // şifreli/bozuk arşiv

        using (zip)
        {
            var n = 0;
            long extracted = 0;
            foreach (var e in zip.Entries)
            {
                if (acc.Hits >= MaxHits || ++n > MaxArchiveEntries || extracted > _maxBytes) break;
                if (e.Length <= 0 || e.Length > _maxBytes) continue;
                var ext = Path.GetExtension(e.Name);
                if (OpaqueArchiveExt.Contains(ext)) { if (_flagEncrypted) acc.MarkEncrypted(); continue; }

                try
                {
                    // Girdiyi belleğe al: PDF/ZIP çıkarıcıları rastgele erişim ister,
                    // ZipArchiveEntry akışı ileri-sarımlıdır.
                    using var es = e.Open();
                    using var ms = new MemoryStream();
                    es.CopyTo(ms, 81920);
                    extracted += ms.Length;
                    ms.Position = 0;
                    ScanStream(ms, ext, acc, depth + 1, (int)ms.Length);
                }
                catch (InvalidDataException) { if (_flagEncrypted) acc.MarkEncrypted(); }   // parola korumalı girdi
                catch { /* bozuk girdi → atla */ }
            }
        }
    }

    private static byte[] ReadAll(Stream s, int cap)
    {
        using var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while (ms.Length < cap && (n = s.Read(buf, 0, buf.Length)) > 0) ms.Write(buf, 0, n);
        return ms.ToArray();
    }

    // --- Bulgu biriktirici ---
    //
    // Parça parça gelen metni tarar. Parça sınırında bölünen deseni kaçırmamak için önceki
    // parçanın kuyruğunu bir sonrakinin başına ekler; aynı numara iki kez sayılmasın diye
    // normalize edilmiş değerler bir kümede tutulur.
    private sealed class Findings
    {
        private readonly string[] _keywords;        // Türkçe katlanmış aranacak kelimeler
        private readonly HashSet<string> _seen = new();
        private readonly HashSet<string> _kw = new(StringComparer.Ordinal);
        private int _tc, _iban, _card;
        private bool _encrypted;
        private string _tail = "";

        public Findings(string[] keywords) => _keywords = keywords;

        public int Hits => _tc + _iban + _card + _kw.Count + (_encrypted ? 1 : 0);

        public void MarkEncrypted() => _encrypted = true;

        public void Feed(IEnumerable<string> chunks)
        {
            foreach (var raw in chunks)
            {
                if (string.IsNullOrEmpty(raw)) continue;
                var text = _tail.Length > 0 ? _tail + raw : raw;
                _tail = text.Length > TextExtract.OverlapChars ? text[^TextExtract.OverlapChars..] : text;
                Scan(text);
                if (Hits >= MaxHits) return;
            }
            _tail = "";   // sonraki dosya/girdi için sıfırla
        }

        private void Scan(string text)
        {
            _tc += CountValid(RxTc.Matches(text), IsValidTc, _seen);
            _iban += CountValid(RxIban.Matches(text), IsValidIban, _seen);
            _card += CountValid(RxCard.Matches(text), IsValidCard, _seen);
            if (_keywords.Length > 0)
            {
                var folded = TrFold(text);   // "GİZLİ" ile "gizli" eşleşsin
                foreach (var k in _keywords)
                    if (folded.Contains(k, StringComparison.Ordinal)) _kw.Add(k);
            }
        }

        public (string? Label, int Hits) Result()
        {
            var labels = new List<string>();
            if (_tc > 0) labels.Add("TC Kimlik");
            if (_iban > 0) labels.Add("IBAN");
            if (_card > 0) labels.Add("Kredi Kartı");
            if (_kw.Count > 0) labels.Add("Anahtar kelime");
            if (_encrypted) labels.Add("Şifreli/açılamayan");
            return labels.Count == 0 ? (null, 0) : (string.Join(", ", labels), Hits);
        }
    }

    private static int CountValid(MatchCollection matches, Func<string, bool> valid, HashSet<string> seen)
    {
        var n = 0;
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

    // Türkçe'ye duyarlı büyük/küçük harf katlaması.
    //
    // NEDEN GEREKLİ: OrdinalIgnoreCase, Türkçe'nin dört I harfini birbirine EŞLEMEZ.
    // Politikaya "gizli" yazan yönetici, belgede "GİZLİ" geçen dosyayı YAKALAYAMIYORDU —
    // Türkiye'ye satılan bir DLP için sessiz ama ciddi bir kaçak. I / İ / ı / i hepsi 'i'ye
    // katlanır; diğer Türkçe harfleri (ş/ğ/ü/ö/ç) ToLowerInvariant zaten doğru eşler.
    internal static string TrFold(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
            sb.Append(c switch
            {
                'I' or 'İ' or 'ı' or 'i' => 'i',
                _ => char.ToLowerInvariant(c)
            });
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

    // Kredi kartı: Luhn + KART AİLESİ ÖN EKİ + uzunluk uyumu.
    //
    // NEDEN ÖN EK ŞART: Luhn tek başına rastgele 16 haneli bir dizinin ~%10'unu geçirir.
    // PDF içerik akışları ve ikili belgeler bol miktarda sayı dizisi üretir; sahada
    // romanlarda/taslaklarda bile "Kredi Kartı" bulgusu çıkıyordu → uyarı çöplüğü ve
    // güven kaybı. Gerçek kartlar bilinen BIN ön ekleriyle başlar; bu filtre yanlış
    // pozitifi pratikte sıfıra indirir, gerçek kartı kaçırmaz.
    private static bool IsValidCard(string raw)
    {
        var digits = raw.Where(char.IsDigit).ToArray();
        if (digits.Length < 13 || digits.Length > 19) return false;
        if (digits.All(c => c == digits[0])) return false;   // 000…/111… → gürültü
        if (!HasKnownCardPrefix(new string(digits))) return false;

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

    // Bilinen kart ailesi ön eki + o aileye uyan uzunluk. Türkiye'de kullanılanlar dahil (Troy).
    private static bool HasKnownCardPrefix(string d)
    {
        var n = d.Length;
        var p2 = (d[0] - '0') * 10 + (d[1] - '0');
        var p3 = p2 * 10 + (d[2] - '0');
        var p4 = p3 * 10 + (d[3] - '0');

        if (d[0] == '4') return n is 13 or 16 or 19;                       // Visa
        if (p2 is >= 51 and <= 55) return n == 16;                          // MasterCard (klasik)
        if (p4 is >= 2221 and <= 2720) return n == 16;                      // MasterCard (yeni aralık)
        if (p2 is 34 or 37) return n == 15;                                 // American Express
        if (p4 == 9792) return n == 16;                                     // Troy (Türkiye)
        if (p2 == 62) return n is >= 16 and <= 19;                          // UnionPay
        if (p2 == 65 || p4 == 6011 || p3 is >= 644 and <= 649) return n is 16 or 19;   // Discover
        if (p4 is >= 3528 and <= 3589) return n is >= 16 and <= 19;         // JCB
        if (p2 is 36 or 38 or 39 || p3 is >= 300 and <= 305) return n is >= 14 and <= 19;  // Diners
        return false;
    }
}
