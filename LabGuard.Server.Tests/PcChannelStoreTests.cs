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

    private string PortablePath => Path.Combine(_folder, "pc-channel.json");

    private static string PublicKeyHash(X509Certificate2 certificate) =>
        Convert.ToHexString(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()));

    private static byte[] NewPfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var certificate = new CertificateRequest("CN=Test channel", key, HashAlgorithmName.SHA256)
            .CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        return certificate.Export(X509ContentType.Pkcs12);
    }

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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var store = PcChannelStore.Open(FilePath);

        var stored = JsonNode.Parse(ProtectedData.Unprotect(File.ReadAllBytes(FilePath), Entropy, DataProtectionScope.LocalMachine))!.AsObject();

        Assert.Equal(new[] { "Pfx", "Version" }, stored.Select(property => property.Key).Order().ToArray());
        Assert.Equal(1, stored["Version"]!.GetValue<int>());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Open_RestrictsTheFileToTheServerAccountAndAdmins()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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
        if (!OperatingSystem.IsWindows())
        {
            // Owner-only, so the content check is what fails rather than the permission check.
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

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
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

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

    [Fact]
    public void Portable_WriteThenOpen_GivesTheSameCertificate_AndLeavesNoTempFile()
    {
        var pfx = NewPfx();
        using var expected = X509CertificateLoader.LoadPkcs12(pfx, password: null);

        PcChannelStore.WritePortable(PortablePath, pfx, overwrite: false);
        using var store = PcChannelStore.OpenPortable(PortablePath);

        Assert.Equal(expected.Thumbprint, store.Certificate.Thumbprint);
        Assert.True(store.Certificate.HasPrivateKey);
        Assert.False(File.Exists(PortablePath + ".tmp"));
    }

    [Fact]
    public void Portable_File_IsPlainJsonWithTheSameLayout()
    {
        var pfx = NewPfx();

        PcChannelStore.WritePortable(PortablePath, pfx, overwrite: false);

        var stored = JsonNode.Parse(File.ReadAllBytes(PortablePath))!.AsObject();
        Assert.Equal(new[] { "Pfx", "Version" }, stored.Select(property => property.Key).Order().ToArray());
        Assert.Equal(1, stored["Version"]!.GetValue<int>());
        Assert.Equal(Convert.ToBase64String(pfx), stored["Pfx"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"Version\":2,\"Pfx\":\"AAAA\"}")]
    [InlineData("{\"Version\":1,\"Pfx\":\"not base64!\"}")]
    [InlineData("{\"Version\":1,\"Pfx\":\"AAAA\"}")]
    [InlineData("null")]
    [InlineData("this is not json")]
    [InlineData("")]
    public void Portable_OpenWithABadLayout_FailsWithAClearMessage_AndLeavesTheFileAlone(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        File.WriteAllBytes(PortablePath, bytes);
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(PortablePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        var exception = Assert.Throws<InvalidOperationException>(() => PcChannelStore.OpenPortable(PortablePath));

        Assert.Contains(PortablePath, exception.Message);
        Assert.Contains("disconnect every registered PC", exception.Message);
        Assert.Equal(bytes, File.ReadAllBytes(PortablePath));
    }

    [Fact]
    public void Portable_WriteWithoutOverwrite_RefusesAnExistingFile_AndRemovesItsTempFile()
    {
        File.WriteAllText(PortablePath, "existing");

        Assert.ThrowsAny<IOException>(() => PcChannelStore.WritePortable(PortablePath, NewPfx(), overwrite: false));

        Assert.Equal("existing", File.ReadAllText(PortablePath));
        Assert.False(File.Exists(PortablePath + ".tmp"));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Export_WritesTheDpapiIdentityAsAPortableFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string keyHash;
        string thumbprint;
        using (var created = PcChannelStore.Open(FilePath))
        {
            keyHash = PublicKeyHash(created.Certificate);
            thumbprint = created.Certificate.Thumbprint;
        }

        var source = File.ReadAllBytes(FilePath);

        PcChannelStore.ExportPortable(FilePath, PortablePath);
        using var exported = PcChannelStore.OpenPortable(PortablePath);

        Assert.Equal(thumbprint, exported.Certificate.Thumbprint);
        Assert.Equal(keyHash, PublicKeyHash(exported.Certificate));
        Assert.True(exported.Certificate.HasPrivateKey);
        Assert.Equal(source, File.ReadAllBytes(FilePath));
        Assert.False(File.Exists(PortablePath + ".tmp"));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Export_RestrictsTheExportedFileToTheServerAccountAndAdmins()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (PcChannelStore.Open(FilePath))
        {
        }

        PcChannelStore.ExportPortable(FilePath, PortablePath);

        var security = new FileInfo(PortablePath).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        Assert.Equal(
            new HashSet<string>
            {
                WindowsIdentity.GetCurrent().User!.Value,
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null).Value,
                new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value,
            },
            security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .Select(rule => rule.IdentityReference.Value)
                .ToHashSet());
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Export_RefusesToOverwriteTheDestination()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using (PcChannelStore.Open(FilePath))
        {
        }

        File.WriteAllText(PortablePath, "existing");

        var exception = Assert.Throws<InvalidOperationException>(() => PcChannelStore.ExportPortable(FilePath, PortablePath));

        Assert.Contains(PortablePath, exception.Message);
        Assert.Equal("existing", File.ReadAllText(PortablePath));
    }

    [Fact]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public void Export_WithoutASourceFile_Fails_AndCreatesNoIdentity()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var exception = Assert.Throws<InvalidOperationException>(() => PcChannelStore.ExportPortable(FilePath, PortablePath));

        Assert.Contains(FilePath, exception.Message);
        Assert.False(File.Exists(FilePath));
        Assert.False(File.Exists(PortablePath));
    }

    [Fact]
    public void Open_OnUnix_CreatesThePortableFileReadableByTheOwnerOnly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var store = PcChannelStore.Open(FilePath);

        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(FilePath));
        using var reopened = PcChannelStore.OpenPortable(FilePath);
        Assert.Equal(store.Certificate.Thumbprint, reopened.Certificate.Thumbprint);
    }

    [Fact]
    public void Open_OnUnix_WithAFileOthersCanRead_RefusesToStart_AndLeavesTheFileAlone()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        PcChannelStore.WritePortable(FilePath, NewPfx(), overwrite: false);
        var bytes = File.ReadAllBytes(FilePath);
        var looseMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
        File.SetUnixFileMode(FilePath, looseMode);

        var exception = Assert.Throws<InvalidOperationException>(() => PcChannelStore.Open(FilePath));

        Assert.Contains("chmod 600", exception.Message);
        Assert.Equal(looseMode, File.GetUnixFileMode(FilePath));
        Assert.Equal(bytes, File.ReadAllBytes(FilePath));
    }
}
