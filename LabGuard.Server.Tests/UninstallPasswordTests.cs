using System.Security.Cryptography;
using System.Text;
using LabGuard.Server.Data;
using LabGuard.Server.Settings;

namespace LabGuard.Server.Tests;

// Hashing the uninstall password, and keeping the hash on the settings row.
public class UninstallPasswordTests
{
    private const string Password = "correct horse battery";

    [Fact]
    public void Hash_IsPbkdf2Sha256OfTheUtf8Password_WithA16ByteSaltAnd32ByteHash()
    {
        var result = UninstallPassword.Hash(Password);

        var salt = Convert.FromBase64String(result.Salt);
        var hash = Convert.FromBase64String(result.Hash);
        Assert.Equal(16, salt.Length);
        Assert.Equal(32, hash.Length);
        Assert.Equal(600_000, result.Iterations);
        var expected = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), salt, 600_000, HashAlgorithmName.SHA256, 32);
        Assert.Equal(expected, hash);
    }

    // "é" as one character (U+00E9) and as "e" plus a combining accent (U+0065 U+0301) hash the same, as NFC.
    [Fact]
    public void Hash_OfTheDecomposedPassword_MatchesTheComposedOne()
    {
        const string composed = "café-password";
        const string decomposed = "café-password";

        var result = UninstallPassword.Hash(decomposed);

        var salt = Convert.FromBase64String(result.Salt);
        var composedHash = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(composed), salt, result.Iterations, HashAlgorithmName.SHA256, 32);
        Assert.Equal(composedHash, Convert.FromBase64String(result.Hash));
    }

    [Fact]
    public void Normalize_ComposesTheDecomposedForm_AndKeepsTheComposedOne()
    {
        Assert.Equal("café", UninstallPassword.Normalize("café"));
        Assert.Equal("café", UninstallPassword.Normalize("café"));
    }

    [Fact]
    public void Hash_UsesANewSaltEachTime()
    {
        var first = UninstallPassword.Hash(Password);
        var second = UninstallPassword.Hash(Password);

        Assert.NotEqual(first.Salt, second.Salt);
        Assert.NotEqual(first.Hash, second.Hash);
    }

    [Fact]
    public void StoredIn_ANewSettingsRow_IsNull()
    {
        Assert.Null(UninstallPassword.StoredIn(new ServerSettings()));
    }

    [Fact]
    public void Store_ThenStoredIn_ReturnsTheSameHash()
    {
        var settings = new ServerSettings();
        var hash = new UninstallPasswordHash("c2FsdA==", "aGFzaA==", 600_000);

        UninstallPassword.Store(settings, hash);

        Assert.Equal(hash, UninstallPassword.StoredIn(settings));
    }

    [Fact]
    public void Store_Null_ClearsAllThreeColumns()
    {
        var settings = new ServerSettings();
        UninstallPassword.Store(settings, new UninstallPasswordHash("c2FsdA==", "aGFzaA==", 600_000));

        UninstallPassword.Store(settings, null);

        Assert.Null(settings.UninstallPasswordSalt);
        Assert.Null(settings.UninstallPasswordHash);
        Assert.Null(settings.UninstallPasswordIterations);
        Assert.Null(UninstallPassword.StoredIn(settings));
    }

    [Fact]
    public void StoredIn_WithAMissingPart_IsNull()
    {
        var settings = new ServerSettings { UninstallPasswordSalt = "c2FsdA==", UninstallPasswordHash = "aGFzaA==" };

        Assert.Null(UninstallPassword.StoredIn(settings));
    }
}
