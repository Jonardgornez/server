using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using LabGuard.Server.Network;
using Microsoft.Extensions.Configuration;

namespace LabGuard.Server.Tests;

public sealed class PcChannelStoreTests : IDisposable
{
    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lykantra-store-{Guid.NewGuid():N}")).FullName;

    private string FilePath => Path.Combine(_folder, "pc-channel.dat");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private static string SecretOf(PcChannelStore store) => PcSetupCode.Normalize(store.SetupCode)[PcSetupCode.FingerprintLength..];

    private static string PublicKeyHash(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    [Fact]
    public void Open_FirstRun_CreatesTheFile()
    {
        Assert.False(File.Exists(FilePath));

        using var store = PcChannelStore.Open(FilePath);

        Assert.True(File.Exists(FilePath));
        Assert.False(File.Exists(FilePath + ".tmp"));
        Assert.True(store.Certificate.HasPrivateKey);
        Assert.Equal("CN=Lykantra PC channel", store.Certificate.Subject);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Open_RestrictsTheFileToTheServerAccountAndAdmins()
    {
        using var store = PcChannelStore.Open(FilePath);

        var security = new FileInfo(FilePath).GetAccessControl();
        var allowed = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
            .Cast<FileSystemAccessRule>()
            .Select(rule => rule.IdentityReference.Value)
            .ToHashSet();

        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            new HashSet<string>
            {
                WindowsIdentity.GetCurrent().User!.Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            },
            allowed);
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void RegenerateSecret_KeepsTheRestrictedPermissions()
    {
        using var store = PcChannelStore.Open(FilePath);

        store.RegenerateSecret();

        Assert.True(new FileInfo(FilePath).GetAccessControl().AreAccessRulesProtected);
    }

    [Fact]
    public void Open_CreatesMissingFolders()
    {
        var nested = Path.Combine(_folder, "a", "b", "pc-channel.dat");

        using var store = PcChannelStore.Open(nested);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public void Certificate_IsEcdsaP256_ValidForTwentyYears_WithServerAuthUsage()
    {
        using var store = PcChannelStore.Open(FilePath);
        var certificate = store.Certificate;

        using var key = certificate.GetECDsaPublicKey();
        Assert.NotNull(key);
        Assert.Equal(256, key.KeySize);
        Assert.InRange(certificate.NotAfter - DateTime.Now, TimeSpan.FromDays(365 * 20 - 3), TimeSpan.FromDays(365 * 20 + 10));
        var usage = Assert.Single(certificate.Extensions.OfType<X509EnhancedKeyUsageExtension>());
        Assert.Contains(usage.EnhancedKeyUsages.Cast<Oid>(), oid => oid.Value == "1.3.6.1.5.5.7.3.1");
    }

    [Fact]
    public void Open_AgainAfterRestart_KeepsTheSamePublicKeyAndSecret()
    {
        string firstKey;
        string firstCode;
        using (var first = PcChannelStore.Open(FilePath))
        {
            firstKey = PublicKeyHash(first.Certificate);
            firstCode = first.SetupCode;
        }

        using var second = PcChannelStore.Open(FilePath);

        Assert.Equal(firstKey, PublicKeyHash(second.Certificate));
        Assert.Equal(firstCode, second.SetupCode);
    }

    [Fact]
    public void File_DoesNotContainTheSecretOrThePfxInTheClear()
    {
        using var store = PcChannelStore.Open(FilePath);
        var bytes = File.ReadAllBytes(FilePath);
        // The loaded key is deliberately not exportable, so the certificate's DER stands in for the PFX (the PFX embeds it).
        var pfx = store.Certificate.RawData;
        var text = Encoding.ASCII.GetString(bytes);

        Assert.DoesNotContain(SecretOf(store), text);
        Assert.DoesNotContain("\"secret\"", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(Convert.ToBase64String(pfx.AsSpan(0, 24)), text);
        Assert.DoesNotContain("Lykantra", text);
        Assert.Equal(-1, bytes.AsSpan().IndexOf(pfx.AsSpan(0, 24)));
    }

    [Fact]
    public void Open_WithGarbageFile_FailsWithAClearMessage_AndLeavesTheFileAlone()
    {
        var garbage = "this is not a protected blob"u8.ToArray();
        File.WriteAllBytes(FilePath, garbage);

        var exception = Assert.Throws<InvalidOperationException>(() => PcChannelStore.Open(FilePath));

        Assert.Contains(FilePath, exception.Message);
        Assert.Contains("disconnect every registered PC", exception.Message);
        Assert.Equal(garbage, File.ReadAllBytes(FilePath));
    }

    [Fact]
    public void Open_WithEmptyFile_Fails_AndWithTruncatedFile_Fails()
    {
        File.WriteAllBytes(FilePath, []);
        Assert.Throws<InvalidOperationException>(() => PcChannelStore.Open(FilePath));

        File.Delete(FilePath);
        using (PcChannelStore.Open(FilePath))
        {
        }

        var bytes = File.ReadAllBytes(FilePath);
        File.WriteAllBytes(FilePath, bytes[..(bytes.Length / 2)]);
        Assert.Throws<InvalidOperationException>(() => PcChannelStore.Open(FilePath));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Open_WithValidlyProtectedButWrongContent_Fails()
    {
        File.WriteAllBytes(FilePath, ProtectedData.Protect("{}"u8.ToArray(), "Lykantra PC channel v1"u8.ToArray(), DataProtectionScope.LocalMachine));

        Assert.Throws<InvalidOperationException>(() => PcChannelStore.Open(FilePath));
    }

    [Fact]
    public void RegenerateSecret_ChangesOnlyTheSecret_AndPersists()
    {
        using var store = PcChannelStore.Open(FilePath);
        var oldCode = store.SetupCode;
        var oldSecret = SecretOf(store);
        var keyHash = PublicKeyHash(store.Certificate);

        var newCode = store.RegenerateSecret();

        Assert.Equal(newCode, store.SetupCode);
        Assert.NotEqual(oldSecret, SecretOf(store));
        Assert.Equal(PcSetupCode.Normalize(oldCode)[..PcSetupCode.FingerprintLength], PcSetupCode.Normalize(newCode)[..PcSetupCode.FingerprintLength]);
        Assert.Equal(keyHash, PublicKeyHash(store.Certificate));
        Assert.False(File.Exists(FilePath + ".tmp"));

        using var reopened = PcChannelStore.Open(FilePath);
        Assert.Equal(newCode, reopened.SetupCode);
        Assert.Equal(keyHash, PublicKeyHash(reopened.Certificate));
    }

    [Fact]
    public void SetupCode_HasTheAgreedFormat_AndTheFingerprintOfThePublicKey()
    {
        using var store = PcChannelStore.Open(FilePath);

        Assert.Matches("^[A-Z2-7]{4}(-[A-Z2-7]{4}){5}$", store.SetupCode);
        var expectedFingerprint = Base32Reference(SHA256.HashData(store.Certificate.PublicKey.ExportSubjectPublicKeyInfo()))[..14];
        Assert.Equal(expectedFingerprint, store.Fingerprint);
        Assert.Equal(expectedFingerprint, PcSetupCode.Normalize(store.SetupCode)[..14]);
    }

    [Fact]
    public void ReferenceBase32_MatchesRfc4648TestVectors()
    {
        Assert.Equal("MZXW6YTBOI", Base32Reference("foobar"u8.ToArray()).TrimEnd('='));
        Assert.Equal("MY", Base32Reference("f"u8.ToArray()));
    }

    [Theory]
    [InlineData("abcd-efgh-ijkl", "ABCDEFGHIJKL")]
    [InlineData("  ab cd - ef  ", "ABCDEF")]
    [InlineData("ABCD", "ABCD")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_RemovesSpacesAndHyphens_AndUppercases(string? input, string expected)
    {
        Assert.Equal(expected, PcSetupCode.Normalize(input));
    }

    [Fact]
    public void SecretMatches_NormalizesTheSubmittedSecret()
    {
        using var store = PcChannelStore.Open(FilePath);
        var secret = SecretOf(store);

        Assert.True(store.SecretMatches(secret));
        Assert.True(store.SecretMatches(secret.ToLowerInvariant()));
        Assert.True(store.SecretMatches($"{secret[..5]}-{secret[5..]}"));
        Assert.True(store.SecretMatches($" {secret} "));
        Assert.False(store.SecretMatches(null));
        Assert.False(store.SecretMatches(""));
        Assert.False(store.SecretMatches(secret[..9]));
        Assert.False(store.SecretMatches(secret + "A"));
        Assert.False(store.SecretMatches(store.SetupCode));
    }

    [Fact]
    public void NewSecret_IsTenBase32Characters_AndDiffers()
    {
        var secrets = Enumerable.Range(0, 50).Select(_ => PcSetupCode.NewSecret()).ToList();

        Assert.All(secrets, secret => Assert.Matches("^[A-Z2-7]{10}$", secret));
        Assert.True(secrets.Distinct().Count() > 45);
    }

    [Fact]
    public void ResolvePath_DefaultsNextToTheContentRoot_AndHonorsTheConfiguredPath()
    {
        var none = new ConfigurationBuilder().Build();
        var custom = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [PcChannelStore.FileConfigurationKey] = "other.dat" })
            .Build();

        Assert.Equal(Path.Combine(_folder, "pc-channel.dat"), PcChannelStore.ResolvePath(none, _folder));
        Assert.Equal(Path.Combine(_folder, "other.dat"), PcChannelStore.ResolvePath(custom, _folder));
    }

    // Independent RFC 4648 encoder for checking the product's output.
    private static string Base32Reference(byte[] bytes)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bits = string.Concat(bytes.Select(b => Convert.ToString(b, 2).PadLeft(8, '0')));
        bits = bits.PadRight((bits.Length + 4) / 5 * 5, '0');
        return string.Concat(Enumerable.Range(0, bits.Length / 5).Select(i => alphabet[Convert.ToInt32(bits.Substring(i * 5, 5), 2)]));
    }
}
