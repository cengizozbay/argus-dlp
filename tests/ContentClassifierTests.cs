// İçerik sınıflandırıcı testleri — DLP'nin kalbi burası, en çok test edilmesi gereken parça.
// Özellikle 2026-07-31'de eklenen PDF / eski Office / ZIP-içi kapsamı doğrular.

using System.IO.Compression;
using System.Text;
using Argus.Agent;
using Xunit;

namespace Argus.Tests;

public sealed class ContentClassifierTests : IDisposable
{
    // Sağlaması geçerli örnek veriler (gerçek kişiye ait değil, algoritmayı sağlayan üretilmiş değerler).
    private const string ValidTc = "10000000078";
    private const string ValidIban = "TR330006100519786457841326";
    private const string ValidCard = "4111111111111111";

    private readonly string _dir;

    public ContentClassifierTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "argus-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static ContentClassifier New(int maxMb = 32, bool archives = true, bool flagEnc = true)
    {
        var c = new ContentClassifier();
        c.Configure(true, new[] { "gizli" }, maxMb, archives, flagEnc);
        return c;
    }

    private string Write(string name, string content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllText(p, content, Encoding.UTF8);
        return p;
    }

    private string WriteBytes(string name, byte[] content)
    {
        var p = Path.Combine(_dir, name);
        File.WriteAllBytes(p, content);
        return p;
    }

    // --- Doğrulayıcılar: yanlış pozitif olmamalı ---

    [Fact]
    public void GecerliTcYakalanir()
    {
        var (label, hits) = New().Classify(Write("a.txt", $"Personel listesi\nTC: {ValidTc}\n"));
        Assert.NotNull(label);
        Assert.Contains("TC Kimlik", label);
        Assert.True(hits >= 1);
    }

    [Fact]
    public void GecersizTcYakalanmaz()
    {
        // Son hanesi bozuk → sağlama tutmaz, uyarı üretmemeli (yanlış pozitif kaynağı).
        var (label, _) = New().Classify(Write("b.txt", "TC: 10000000079"));
        Assert.Null(label);
    }

    [Fact]
    public void GecerliIbanYakalanir()
    {
        var (label, _) = New().Classify(Write("c.txt", $"Hesap: {ValidIban}"));
        Assert.Contains("IBAN", label);
    }

    [Fact]
    public void BosluklaYazilanIbanYakalanir()
    {
        var spaced = "TR33 0006 1005 1978 6457 8413 26";
        var (label, _) = New().Classify(Write("c2.txt", $"Hesap: {spaced}"));
        Assert.Contains("IBAN", label);
    }

    [Fact]
    public void GecerliKartYakalanir()
    {
        var (label, _) = New().Classify(Write("d.txt", $"Kart {ValidCard}"));
        Assert.Contains("Kredi Kartı", label);
    }

    [Fact]
    public void LuhnTutmayanKartYakalanmaz()
    {
        var (label, _) = New().Classify(Write("e.txt", "Kart 4111111111111112"));
        Assert.Null(label);
    }

    // Gerçek kart aileleri (Türkiye'de kullanılanlar dahil) yakalanmalı.
    [Theory]
    [InlineData("5500005555555559")]   // MasterCard
    [InlineData("340000000000009")]    // American Express
    [InlineData("9792024000000003")]   // Troy
    public void FarkliKartAileleriYakalanir(string kart)
    {
        var (label, _) = New().Classify(Write("k" + kart[..4] + ".txt", $"Odeme {kart}"));
        Assert.NotNull(label);
        Assert.Contains("Kredi Kartı", label);
    }

    // YANLIŞ POZİTİF KORUMASI: Luhn tek başına rastgele 16 hanelinin ~%10'unu geçirir.
    // Sahada roman/taslak PDF'lerinde bile "Kredi Kartı" bulgusu çıkıyordu (uyarı çöplüğü).
    // Bilinen kart ön eki olmayan, Luhn'u tesadüfen geçen sayı dizisi ELENMELİ.
    [Fact]
    public void LuhnGecenAmaKartOlmayanSayiElenir()
    {
        // 1234567812345670 → Luhn geçer, ama hiçbir kart ailesinin ön ekiyle başlamaz.
        var (label, _) = New().Classify(Write("fp.txt", "Sipariş no 1234567812345670"));
        Assert.Null(label);
    }

    [Fact]
    public void UzunSayiDizileriKartSanilmaz()
    {
        // Belge/fatura numarası, barkod, ISBN gibi diziler uyarı üretmemeli.
        var metin = "Barkod 8691234567895 Belge 7000000000000008 Seri 1000000000000009";
        var (label, _) = New().Classify(Write("fp2.txt", metin));
        Assert.Null(label);
    }

    [Fact]
    public void AnahtarKelimeYakalanir()
    {
        var (label, _) = New().Classify(Write("f.txt", "Bu belge gizli tutulmalıdır."));
        Assert.Contains("Anahtar kelime", label);
    }

    // Türkçe'nin dört I harfi: politikaya "gizli" yazılınca belgedeki her yazımı yakalamalı.
    // OrdinalIgnoreCase bunları eşlemez → sessiz kaçak olurdu.
    [Theory]
    [InlineData("Bu belge GİZLİ tutulmalıdır.")]
    [InlineData("Bu belge GIZLI tutulmalidir.")]
    [InlineData("Bu belge Gizli tutulmalıdır.")]
    [InlineData("Bu belge gızlı tutulmalıdır.")]
    public void TurkceBuyukKucukHarfKacagiYok(string metin)
    {
        var (label, _) = New().Classify(Write("tr-" + metin.GetHashCode() + ".txt", metin));
        Assert.NotNull(label);
        Assert.Contains("Anahtar kelime", label);
    }

    [Fact]
    public void TemizDosyaIsaretlenmez()
    {
        var (label, hits) = New().Classify(Write("g.txt", "Toplantı notları: pazartesi saat 10'da bulusalim."));
        Assert.Null(label);
        Assert.Equal(0, hits);
    }

    // --- Kapsam: eskiden kör olunan formatlar ---

    [Fact]
    public void ZipIcindekiHassasDosyaYakalanir()
    {
        // "Zip'le ve USB'ye at" — eski sürümün tamamen kaçırdığı sızıntı yolu.
        var zipPath = Path.Combine(_dir, "arsiv.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("ic/personel.txt");
            using var w = new StreamWriter(e.Open());
            w.Write($"TC: {ValidTc}");
        }
        var (label, _) = New().Classify(zipPath);
        Assert.NotNull(label);
        Assert.Contains("TC Kimlik", label);
    }

    [Fact]
    public void ArsivTaramasiKapaliykenZipIcineBakilmaz()
    {
        var zipPath = Path.Combine(_dir, "arsiv2.zip");
        using (var fs = File.Create(zipPath))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("x.txt");
            using var w = new StreamWriter(e.Open());
            w.Write($"TC: {ValidTc}");
        }
        // Arşiv taraması kapalı + şifreli işaretleme kapalı → hiç bulgu olmamalı.
        var c = new ContentClassifier();
        c.Configure(true, null, 32, scanArchives: false, flagEncrypted: false);
        var (label, _) = c.Classify(zipPath);
        Assert.Null(label);
    }

    [Fact]
    public void PdfIcindekiHassasVeriYakalanir()
    {
        var (label, _) = New().Classify(WriteBytes("fatura.pdf", MinimalPdf($"Musteri TC {ValidTc}")));
        Assert.NotNull(label);
        Assert.Contains("TC Kimlik", label);
    }

    [Fact]
    public void EskiOfficeIkiliMetniYakalanir()
    {
        // .doc metni UTF-16LE tutar → ikili dizi çıkarımı bunu görmeli.
        var bytes = new List<byte> { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 };   // OLE2 imzası
        bytes.AddRange(Encoding.Unicode.GetBytes($"Personel kayitlari TC {ValidTc} son"));
        var (label, _) = New().Classify(WriteBytes("eski.doc", bytes.ToArray()));
        Assert.NotNull(label);
        Assert.Contains("TC Kimlik", label);
    }

    [Fact]
    public void AcilamayanArsivSinyalUretir()
    {
        var (label, _) = New().Classify(WriteBytes("veri.rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 1, 2, 3, 4 }));
        Assert.Contains("Açılamayan arşiv", label);
    }

    [Fact]
    public void SifreliArsivSinyaliKapatilabilir()
    {
        var c = new ContentClassifier();
        c.Configure(true, null, 32, scanArchives: true, flagEncrypted: false);
        var (label, _) = c.Classify(WriteBytes("veri2.rar", new byte[] { 0x52, 0x61, 0x72, 0x21, 1, 2, 3, 4 }));
        Assert.Null(label);
    }

    // --- Sınırlar ---

    [Fact]
    public void BoyutTavaniUstuTaranmaz()
    {
        // 2 MB'lık dosya, tavan 1 MB → taranmamalı.
        var big = new string('a', 2 * 1024 * 1024) + ValidTc;
        var (label, _) = New(maxMb: 1).Classify(Write("buyuk.txt", big));
        Assert.Null(label);
    }

    [Fact]
    public void ParcaSinirindaBolunenTcKacmaz()
    {
        // Parça sınırı 256K karakter. TC'yi tam sınıra denk getir → bindirme çalışmazsa kaçardı.
        var pad = new string('x', TextExtract.ChunkChars - 5);
        var (label, _) = New().Classify(Write("sinir.txt", pad + ValidTc + "son"));
        Assert.NotNull(label);
        Assert.Contains("TC Kimlik", label);
    }

    [Fact]
    public void AyniNumaraIkiKezSayilmaz()
    {
        var (_, hits) = New().Classify(Write("tekrar.txt", $"{ValidTc} {ValidTc} {ValidTc}"));
        Assert.Equal(1, hits);
    }

    [Fact]
    public void TaramaKapaliykenHicbirSeyDonmez()
    {
        var c = new ContentClassifier();
        c.Configure(false, null);
        var (label, _) = c.Classify(Write("kapali.txt", $"TC: {ValidTc}"));
        Assert.Null(label);
    }

    [Fact]
    public void OlmayanDosyaPatlamaz()
    {
        var (label, hits) = New().Classify(Path.Combine(_dir, "yok-boyle-bir-dosya.txt"));
        Assert.Null(label);
        Assert.Equal(0, hits);
    }

    // Sıkıştırmasız, tek sayfalık geçerli bir PDF üretir (içerik akışında Tj ile metin).
    private static byte[] MinimalPdf(string text)
    {
        var content = $"BT /F1 12 Tf 72 720 Td ({text}) Tj ET";
        var sb = new StringBuilder();
        var offsets = new List<int>();
        void Obj(string body)
        {
            offsets.Add(sb.Length);
            sb.Append(body);
        }
        sb.Append("%PDF-1.4\n");
        Obj("1 0 obj\n<< /Type /Catalog /Pages 2 0 R >>\nendobj\n");
        Obj("2 0 obj\n<< /Type /Pages /Kids [3 0 R] /Count 1 >>\nendobj\n");
        Obj("3 0 obj\n<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R >>\nendobj\n");
        Obj($"4 0 obj\n<< /Length {content.Length} >>\nstream\n{content}\nendstream\nendobj\n");
        var xref = sb.Length;
        sb.Append($"xref\n0 {offsets.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append(o.ToString("D10")).Append(" 00000 n \n");
        sb.Append($"trailer\n<< /Size {offsets.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF");
        return Encoding.Latin1.GetBytes(sb.ToString());
    }
}
