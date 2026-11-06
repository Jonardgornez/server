using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LabGuard.Server.Network;
using Microsoft.Extensions.Configuration;

namespace LabGuard.Server.Tests;

public sealed class PcChannelStoreTests : IDisposable
{
    private static readonly byte[] Entropy = "Lykantra PC channel v1"u8.ToArray();

    private readonly string _folder = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"lykantra-store-{Guid.NewGuid():N}")).FullName;

    private string FilePath => Path.Combine(_folder, "pc-channel.dat");

    public void Dispose() => Directory.Delete(_folder, recursive: true);

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
    public void Open_FirstRun_StoresOnlyTheCertificateAndItsFormatVersion()
    {
        using var store = PcChannelStore.Open(FilePath);

        var stored = JsonNode.Parse(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.LocalMachine))!.AsObject();

        Assert.Equal(new[] { "Pfx", "Version" }, stored.Select(property => property.Key).Order().ToArray());
        Assert.Equal(1, stored["Version"]!.GetValue<int>());
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
    public void Open_AgainAfterRestart_KeepsTheSamePublicKey()
    {
        string firstKey;
        using (var first = PcChannelStore.Open(FilePath))
        {
            firstKey = PublicKeyHash(first.Certificate);
        }

        using var second = PcChannelStore.Open(FilePath);

        Assert.Equal(firstKey, PublicKeyHash(second.Certificate));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Open_FileWrittenWithARegistrationSecret_StillOpens_WithTheSameCertificate()
    {
        string keyHash;
        using (var created = PcChannelStore.Open(FilePath))
        {
            keyHash = PublicKeyHash(created.Certificate);
        }

        // Writes the file the way the previous version did: the same identity plus the registration secret.
        var stored = JsonNode.Parse(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.LocalMachine))!.AsObject();
        stored["Secret"] = "ABCDEFGHIJ";
        File.WriteAllBytes(
            FilePath,
            ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(stored), Entropy, DataProtectionScope.LocalMachine));

        using var reopened = PcChannelStore.Open(FilePath);

        Assert.Equal(keyHash, PublicKeyHash(reopened.Certificate));
        Assert.True(reopened.Certificate.HasPrivateKey);
    }

    [Fact]
    public void File_DoesNotContainThePfxInTheClear()
    {
        using var store = PcChannelStore.Open(FilePath);
        var bytes = File.ReadAllBytes(FilePath);
        // The loaded key is deliberately not exportable, so the certificate's DER stands in for the PFX (the PFX embeds it).
        var pfx = store.Certificate.RawData;
        var text = Encoding.ASCII.GetString(bytes);

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
        File.WriteAllBytes(FilePath, ProtectedData.Protect("{}"u8.ToArray(), Entropy, DataProtectionScope.LocalMachine));

        Assert.Throws<InvalidOperationException>(() => PcChannelStore.Open(FilePath));
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
}
