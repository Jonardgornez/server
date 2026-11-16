using System.Security.Cryptography;
using System.Text;
using LabGuard.Server.Data;

namespace LabGuard.Server.Settings;

// What a PC needs to check the uninstall password itself: PBKDF2-HMAC-SHA256 of the UTF-8 password, with this salt and iteration count.
public record UninstallPasswordHash(string Salt, string Hash, int Iterations);

// The one uninstall password of all PCs. The server keeps only its salted hash, which every PC gets in the reply to its service report.
public static class UninstallPassword
{
    public const int MinLength = 8;
    public const int MaxLength = 128;
    public const int Iterations = 600_000;
    private const int SaltBytes = 16;
    private const int HashBytes = 32;

    // NFC, so the same password typed on the admin page and in the uninstaller hashes the same. The PC normalizes the same way.
    // Throws for a lone surrogate, which callers refuse first.
    public static string Normalize(string password) => password.Normalize(NormalizationForm.FormC);

    public static UninstallPasswordHash Hash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltBytes);
        var hash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Normalize(password)), salt, Iterations, HashAlgorithmName.SHA256, HashBytes);
        return new UninstallPasswordHash(Convert.ToBase64String(salt), Convert.ToBase64String(hash), Iterations);
    }

    public static UninstallPasswordHash? StoredIn(ServerSettings settings) =>
        settings is { UninstallPasswordSalt: { } salt, UninstallPasswordHash: { } hash, UninstallPasswordIterations: { } iterations }
            ? new UninstallPasswordHash(salt, hash, iterations)
            : null;

    public static void Store(ServerSettings settings, UninstallPasswordHash? hash)
    {
        settings.UninstallPasswordSalt = hash?.Salt;
        settings.UninstallPasswordHash = hash?.Hash;
        settings.UninstallPasswordIterations = hash?.Iterations;
    }
}
