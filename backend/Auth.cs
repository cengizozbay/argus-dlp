// Panel kimlik doğrulama yardımcıları: parola hash'leme + rol sabitleri.
// Parolalar ASLA düz saklanmaz; PBKDF2 (SHA-256, 120k tur) ile tuzlanıp hash'lenir.

using System.Security.Cryptography;

namespace Argus.Server;

public static class Roles
{
    public const string Superadmin = "superadmin"; // Platform (satıcı) — firma yönetimi, tüm tenantların üstünde
    public const string Owner = "owner";    // Patron — kendi firmasında her şey + kullanıcı yönetimi
    public const string Admin = "admin";    // Yönetici — ayar + kill switch, kullanıcı ekleyemez
    public const string Viewer = "viewer";  // İzleyici — sadece izleme

    // Süper-admin panelden kullanıcı olarak ATANAMAZ (yalnız sistemce seed edilir).
    public static bool IsValid(string? r) => r is Owner or Admin or Viewer;

    // Yetki hiyerarşisi: superadmin > owner > admin > viewer. "En az bu seviye" kontrolü için.
    public static int Rank(string? r) => r switch { Superadmin => 4, Owner => 3, Admin => 2, Viewer => 1, _ => 0 };
    public static bool AtLeast(string? role, string min) => Rank(role) >= Rank(min);
}

public static class Passwords
{
    private const int SaltSize = 16;
    private const int HashSize = 32;
    private const int Iterations = 120_000;

    // "base64(salt).base64(hash)" biçiminde tek dizeye kodlar.
    public static string Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, HashSize);
        return Convert.ToBase64String(salt) + "." + Convert.ToBase64String(hash);
    }

    public static bool Verify(string password, string stored)
    {
        try
        {
            var parts = stored.Split('.', 2);
            if (parts.Length != 2) return false;
            var salt = Convert.FromBase64String(parts[0]);
            var expected = Convert.FromBase64String(parts[1]);
            var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, Iterations, HashAlgorithmName.SHA256, expected.Length);
            return CryptographicOperations.FixedTimeEquals(actual, expected);
        }
        catch { return false; }
    }

    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}

// Kaba kuvvet (brute-force) koruması: kullanıcı/IP başına başarısız giriş sayar; eşik aşılınca kilitler.
// Bellek içi (tek sunucu için yeter); çok sunuculu üretimde ortak store'a taşınır.
public static class LoginThrottle
{
    private const int MaxFails = 5;                 // bu kadar art arda hatadan sonra
    private static readonly TimeSpan Lock = TimeSpan.FromMinutes(10);   // bu süre kilitli
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);  // sayaç bu sürede sıfırlanır

    private sealed class Entry { public int Fails; public DateTime First; public DateTime? LockedUntil; }
    private static readonly Dictionary<string, Entry> _map = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _gate = new();

    // Kilitliyse kalan saniyeyi döndürür (>0 = engelli), değilse 0.
    public static int RetryAfter(string key)
    {
        lock (_gate)
        {
            if (!_map.TryGetValue(key, out var e) || e.LockedUntil is null) return 0;
            var left = (e.LockedUntil.Value - DateTime.UtcNow).TotalSeconds;
            if (left <= 0) { _map.Remove(key); return 0; }
            return (int)Math.Ceiling(left);
        }
    }

    public static void OnFailure(string key)
    {
        lock (_gate)
        {
            var now = DateTime.UtcNow;
            if (!_map.TryGetValue(key, out var e) || (now - e.First) > Window)
                e = _map[key] = new Entry { First = now };
            e.Fails++;
            if (e.Fails >= MaxFails) e.LockedUntil = now.Add(Lock);
        }
    }

    public static void OnSuccess(string key)
    {
        lock (_gate) _map.Remove(key);
    }
}
