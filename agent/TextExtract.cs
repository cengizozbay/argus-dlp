// Metin çıkarma — DLP içerik taramasının besleyicisi. SIFIR dış bağımlılık.
//
// NEDEN: Sınıflandırıcı eskiden yalnız düz metin ve yeni Office (.docx/.xlsx/.pptx) okuyabiliyordu.
// Sahada en hassas belgeler PDF (fatura, sözleşme, kimlik taraması), eski Office (.doc/.xls) ve
// ZIP arşivi içinde taşınır — bunlar taranmadığı için sistem "temiz" diyordu. YANLIŞ GÜVEN.
// Bu dosya o kör noktaları kapatır.
//
// Tasarım notları:
//  - Her çıkarıcı PARÇA PARÇA (chunk) döner → 300 MB'lık dosya belleğe alınmaz.
//  - Hiçbir yol istisna fırlatmaz; bozuk/kilitli dosyada boş döner.
//  - Amaç "kusursuz metin" değil, DOĞRULANABİLİR DESEN yakalamak (TC/IBAN/kart sağlamalı).
//    Bu yüzden bir miktar çöp metin zararsızdır — sağlama zaten yanlış pozitifi eler.

using System.IO.Compression;
using System.Text;

namespace Argus.Agent;

internal static class TextExtract
{
    // Bir parçanın hedef boyutu. Desenlerin parça sınırında bölünmemesi için çağıran taraf
    // parçalar arasında OverlapChars kadar bindirme uygular.
    public const int ChunkChars = 256 * 1024;
    public const int OverlapChars = 128;   // en uzun desen (boşluklu kart no) ~40 karakter

    // --- Düz metin ---
    public static IEnumerable<string> PlainText(Stream s, long maxChars)
    {
        StreamReader sr;
        try { sr = new StreamReader(s, Encoding.UTF8, detectEncodingFromByteOrderMarks: true); }
        catch { yield break; }
        using (sr)
        {
            var buf = new char[ChunkChars];
            long total = 0;
            while (total < maxChars)
            {
                int n;
                try { n = sr.Read(buf, 0, buf.Length); } catch { yield break; }
                if (n <= 0) break;
                total += n;
                yield return new string(buf, 0, n);
            }
        }
    }

    // --- Yeni Office (OOXML) = ZIP içinde XML ---
    public static IEnumerable<string> Ooxml(Stream s, long maxChars)
    {
        ZipArchive zip;
        try { zip = new ZipArchive(s, ZipArchiveMode.Read, leaveOpen: true); }
        catch { yield break; }
        using (zip)
        {
            long total = 0;
            foreach (var e in zip.Entries)
            {
                if (total >= maxChars) break;
                var n = e.FullName;
                if (!n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) continue;
                if (!(n.StartsWith("word/") || n.StartsWith("xl/") || n.StartsWith("ppt/"))) continue;
                string xml;
                try
                {
                    using var es = e.Open();
                    using var sr = new StreamReader(es, Encoding.UTF8);
                    xml = sr.ReadToEnd();
                }
                catch { continue; }
                var text = StripTags(xml);
                total += text.Length;
                yield return text;
            }
        }
    }

    // XML etiketlerini boşlukla değiştir (tam DOM'a gerek yok).
    public static string StripTags(string xml)
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

    // --- Eski Office (.doc/.xls/.ppt) ve tanınmayan ikili formatlar ---
    //
    // OLE2 birleşik belgesini tam ayrıştırmak yerine YAZDIRILABİLİR DİZİ çıkarıyoruz.
    // .doc metni ağırlıkla UTF-16LE, .xls ise tek-bayt kayıtlarda tutar → iki geçiş de yapılır.
    // Bu, TC/IBAN/kart gibi doğrulamalı desenleri yakalamak için fazlasıyla yeterli.
    public static IEnumerable<string> BinaryStrings(Stream s, long maxChars)
    {
        var buf = new byte[512 * 1024];
        long produced = 0;
        while (produced < maxChars)
        {
            int n;
            try { n = s.Read(buf, 0, buf.Length); } catch { yield break; }
            if (n <= 0) break;

            var ascii = AsciiRuns(buf, n);
            if (ascii.Length > 0) { produced += ascii.Length; yield return ascii; }

            var wide = Utf16LeRuns(buf, n);
            if (wide.Length > 0) { produced += wide.Length; yield return wide; }
        }
    }

    private static string AsciiRuns(byte[] b, int len)
    {
        var sb = new StringBuilder();
        var run = new StringBuilder();
        for (var i = 0; i < len; i++)
        {
            var c = b[i];
            if (c >= 32 && c < 127) run.Append((char)c);
            else { if (run.Length >= 4) sb.Append(run).Append(' '); run.Clear(); }
        }
        if (run.Length >= 4) sb.Append(run);
        return sb.ToString();
    }

    private static string Utf16LeRuns(byte[] b, int len)
    {
        var sb = new StringBuilder();
        var run = new StringBuilder();
        for (var i = 0; i + 1 < len; i += 2)
        {
            var lo = b[i]; var hi = b[i + 1];
            if (hi == 0 && lo >= 32 && lo < 127) run.Append((char)lo);
            else { if (run.Length >= 4) sb.Append(run).Append(' '); run.Clear(); }
        }
        if (run.Length >= 4) sb.Append(run);
        return sb.ToString();
    }

    // --- PDF ---
    //
    // PDF gövdesindeki "stream … endstream" bloklarını bulur, FlateDecode olanları açar ve
    // içerik akışındaki METİN GÖSTERME işleçlerinin dizgelerini toplar: (…)Tj, […]TJ, <hex>Tj.
    // Word/Excel/muhasebe programlarının ürettiği metin-tabanlı PDF'lerde iyi çalışır.
    // TARANMIŞ (görüntü) PDF'lerde metin yoktur → OCR gerekir, kapsam dışı (raporda belirtilir).
    public static IEnumerable<string> Pdf(byte[] buf, long maxChars)
    {
        long produced = 0;
        var i = 0;
        while (produced < maxChars)
        {
            var st = IndexOf(buf, "stream", i);
            if (st < 0) break;

            // Bu stream'in sözlüğü: geriye doğru en yakın "<<" ile stream arası.
            var dictStart = LastIndexOf(buf, "<<", st);
            var flate = dictStart >= 0 && Contains(buf, "/FlateDecode", dictStart, st);
            // Görüntü/font akışlarını atla — ikili çöp üretirler, metin taşımazlar.
            var skip = dictStart >= 0 && (Contains(buf, "/Image", dictStart, st)
                                       || Contains(buf, "/DCTDecode", dictStart, st)
                                       || Contains(buf, "/JPXDecode", dictStart, st)
                                       || Contains(buf, "/FontFile", dictStart, st));

            var dataStart = st + "stream".Length;
            while (dataStart < buf.Length && (buf[dataStart] == '\r' || buf[dataStart] == '\n')) dataStart++;
            var end = IndexOf(buf, "endstream", dataStart);
            if (end < 0) break;
            i = end + "endstream".Length;

            if (skip) continue;
            var len = end - dataStart;
            if (len <= 0 || len > 48 * 1024 * 1024) continue;

            string content;
            if (flate)
            {
                try
                {
                    using var ms = new MemoryStream(buf, dataStart, len, writable: false);
                    using var z = new ZLibStream(ms, CompressionMode.Decompress);
                    using var outMs = new MemoryStream();
                    z.CopyTo(outMs, 81920);
                    content = Encoding.Latin1.GetString(outMs.GetBuffer(), 0, (int)outMs.Length);
                }
                catch { continue; }   // bozuk/desteklenmeyen filtre → atla
            }
            else
            {
                content = Encoding.Latin1.GetString(buf, dataStart, len);
            }

            var text = PdfStrings(content);
            if (text.Length == 0) continue;
            produced += text.Length;
            yield return text;
        }
    }

    // İçerik akışındaki dizge sabitlerini topla — hem düz "(…)" hem onaltılık "<…>".
    private static string PdfStrings(string content)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '(')
            {
                i++;
                var depth = 1;
                while (i < content.Length && depth > 0)
                {
                    var ch = content[i];
                    if (ch == '\\')
                    {
                        i++;
                        if (i >= content.Length) break;
                        var e = content[i];
                        if (e >= '0' && e <= '7')
                        {
                            // sekizlik kaçış \ddd
                            var v = 0; var k = 0;
                            while (k < 3 && i < content.Length && content[i] >= '0' && content[i] <= '7')
                            { v = v * 8 + (content[i] - '0'); i++; k++; }
                            sb.Append((char)v);
                            continue;
                        }
                        sb.Append(e switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'f' => '\f', _ => e });
                        i++;
                        continue;
                    }
                    if (ch == '(') depth++;
                    else if (ch == ')') { depth--; if (depth == 0) { i++; break; } }
                    sb.Append(ch);
                    i++;
                }
                sb.Append(' ');
                i--;
            }
            else if (c == '<' && i + 1 < content.Length && content[i + 1] != '<')
            {
                var j = content.IndexOf('>', i + 1);
                if (j < 0) break;
                sb.Append(HexString(content, i + 1, j - i - 1)).Append(' ');
                i = j;
            }
        }
        return sb.ToString();
    }

    // <48656C6C6F> → "Hello". Tüm baytlar ASCII ise tek-bayt, değilse UTF-16BE dener.
    private static string HexString(string s, int start, int len)
    {
        var hex = new StringBuilder(len);
        for (var i = start; i < start + len && i < s.Length; i++)
        {
            var c = s[i];
            if (Uri.IsHexDigit(c)) hex.Append(c);
        }
        if (hex.Length < 2) return "";
        if (hex.Length % 2 == 1) hex.Append('0');
        var bytes = new byte[hex.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(hex.ToString(i * 2, 2), System.Globalization.NumberStyles.HexNumber,
                               System.Globalization.CultureInfo.InvariantCulture, out var b)) return "";
            bytes[i] = b;
        }
        var anyHigh = bytes.Any(b => b == 0 || b > 127);
        try { return anyHigh ? Encoding.BigEndianUnicode.GetString(bytes) : Encoding.ASCII.GetString(bytes); }
        catch { return ""; }
    }

    // --- İkili arama yardımcıları (bayt dizisinde ASCII imza arar) ---
    private static int IndexOf(byte[] hay, string needle, int from)
    {
        if (from < 0) from = 0;
        var n = needle.Length;
        for (var i = from; i + n <= hay.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < n; j++) if (hay[i + j] != (byte)needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static int LastIndexOf(byte[] hay, string needle, int before)
    {
        var n = needle.Length;
        for (var i = Math.Min(before, hay.Length - n) - 1; i >= 0; i--)
        {
            var ok = true;
            for (var j = 0; j < n; j++) if (hay[i + j] != (byte)needle[j]) { ok = false; break; }
            if (ok) return i;
        }
        return -1;
    }

    private static bool Contains(byte[] hay, string needle, int from, int to)
    {
        var idx = IndexOf(hay, needle, from);
        return idx >= 0 && idx < to;
    }

    public static bool PdfIsEncrypted(byte[] buf) => IndexOf(buf, "/Encrypt", 0) >= 0;
}
