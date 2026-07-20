// Argus Web Host — Chrome/Edge native messaging köprüsü.
// Tarayıcı uzantısı tarafından başlatılır; stdin'den (4 bayt uzunluk + UTF8 JSON) mesaj okur.
//  - "active" tipi mesaj  -> web-current.json'a yazılır (agent bunu okuyup süreyi ilgili URL'ye yazar)
//  - tüm mesajlar         -> web-events.jsonl'a eklenir (tam geçmiş)
// stdout'a ASLA serbest metin yazılmaz (native messaging protokolünü bozar).

using System.Text;
using System.Text.Json;

namespace Argus.WebHost;

internal static class Program
{
    private static void Main()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Argus");
        Directory.CreateDirectory(dir);
        var currentPath = Path.Combine(dir, "web-current.json");
        var logPath = Path.Combine(dir, "web-events.jsonl");
        var diagPath = Path.Combine(dir, "webhost.log");
        void Diag(string m) { try { File.AppendAllText(diagPath, DateTime.Now.ToString("o") + " " + m + "\n"); } catch { } }

        Diag("start pid=" + Environment.ProcessId + " dir=" + dir);
        try
        {
            using var stdin = Console.OpenStandardInput();
            var lenBuf = new byte[4];
            var count = 0;

            while (ReadExact(stdin, lenBuf, 4))
            {
                var len = BitConverter.ToInt32(lenBuf, 0);          // native byte order = little-endian (Windows)
                if (len <= 0 || len > 2_000_000) { Diag("bad len=" + len); break; }

                var buf = new byte[len];
                if (!ReadExact(stdin, buf, len)) { Diag("short read len=" + len); break; }

                var json = Encoding.UTF8.GetString(buf);
                var isActive = false;
                try
                {
                    using var doc = JsonDocument.Parse(json);
                    isActive = doc.RootElement.TryGetProperty("type", out var t) && t.GetString() == "active";
                }
                catch { /* bozuk mesaj — yine de logla */ }

                if (isActive) File.WriteAllText(currentPath, json);
                File.AppendAllText(logPath, json + "\n");
                count++;
            }
            Diag("exit normal, mesaj=" + count);
        }
        catch (Exception ex) { Diag("EXCEPTION: " + ex); }
    }

    private static bool ReadExact(Stream s, byte[] b, int n)
    {
        var off = 0;
        while (off < n)
        {
            var r = s.Read(b, off, n - off);
            if (r <= 0) return false;
            off += r;
        }
        return true;
    }
}
