using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace LabGuard.Server.Network;

// The identity of the PC channel: a self-signed certificate (PCs pin its public key) and the registration secret.
// Both live in one file that is encrypted as a whole with DPAPI (machine scope), so copying the file to another
// machine or reading it as another process without DPAPI access reveals nothing. The setup code an admin types
// into a PC is the key fingerprint followed by the secret; see PcSetupCode.
public sealed class PcChannelStore : IDisposable
{
    public const string DefaultFileName = "pc-channel.dat";
    public const string FileConfigurationKey = "Server:PcChannelFile";

    private const int FormatVersion = 1;
    private static readonly byte[] Entropy = "Lykantra PC channel v1"u8.ToArray();

    private readonly object _gate = new();
    private readonly string _path;
    private readonly byte[] _pfx;
    private string _secret;

    private PcChannelStore(string path, byte[] pfx, string secret)
    {
        _path = path;
        _pfx = pfx;
        _secret = secret;
        Certificate = LoadCertificate(pfx);
        Fingerprint = PcSetupCode.FingerprintOf(Certificate);
    }

    // Kestrel serves this one instance for the life of the process.
    public X509Certificate2 Certificate { get; }

    // First 14 characters of the setup code: the pinned key's fingerprint.
    public string Fingerprint { get; }

    public static string ResolvePath(IConfiguration configuration, string contentRootPath)
    {
        var configured = configuration[FileConfigurationKey];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultFileName : configured, contentRootPath);
    }

    // Reads the file, or creates it on first run. A file that can't be read is an error, never replaced:
    // a new identity would silently disconnect every registered PC.
    public static PcChannelStore Open(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The PC channel store uses Windows DPAPI, so this server runs on Windows only.");
        }

        if (!File.Exists(path))
        {
            var created = new PcChannelStore(path, CreatePfx(), PcSetupCode.NewSecret());
            created.Save(created._secret);
            return created;
        }

        try
        {
            var stored = JsonSerializer.Deserialize<StoredIdentity>(Unprotect(File.ReadAllBytes(path)));
            if (stored is not { Version: FormatVersion, Pfx: not null, Secret: not null } || !PcSetupCode.IsValidSecret(stored.Secret))
            {
                throw new InvalidDataException("The file has an unexpected layout.");
            }

            return new PcChannelStore(path, Convert.FromBase64String(stored.Pfx), stored.Secret);
        }
        catch (Exception exception) when (exception is CryptographicException or JsonException or FormatException or InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                $"The PC channel file '{path}' can't be read ({exception.GetType().Name}). It is not replaced automatically, " +
                "because a new identity would disconnect every registered PC. Restore it from a backup, or delete it " +
                "deliberately and set up each PC again.",
                exception);
        }
    }

    // Releases the certificate, which also removes the temporary key container Windows created for it.
    public void Dispose() => Certificate.Dispose();

    public string SetupCode
    {
        get
        {
            lock (_gate)
            {
                return PcSetupCode.Format(Fingerprint, _secret);
            }
        }
    }

    // Normalizes what the PC sent and compares in constant time.
    public bool SecretMatches(string? submitted)
    {
        var normalized = PcSetupCode.Normalize(submitted);
        string current;
        lock (_gate)
        {
            current = _secret;
        }

        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(normalized), Encoding.ASCII.GetBytes(current));
    }

    // New secret, same key: PCs that are already registered use their token and are unaffected.
    public string RegenerateSecret()
    {
        lock (_gate)
        {
            var secret = PcSetupCode.NewSecret();
            Save(secret);
            _secret = secret;
            return PcSetupCode.Format(Fingerprint, secret);
        }
    }

    private void Save(string secret)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(new StoredIdentity(FormatVersion, Convert.ToBase64String(_pfx), secret));
        var protectedBytes = Protect(json);
        CryptographicOperations.ZeroMemory(json);

        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Written beside the target and moved over it, so a crash never leaves a half-written file.
        var temp = _path + ".tmp";
        WriteRestricted(temp, protectedBytes);
        File.Move(temp, _path, overwrite: true);
    }

    // DPAPI's machine scope lets any local process decrypt the file, so its permissions are what
    // keep the private key from other accounts on this PC. Moving the file keeps these permissions.
    private static void WriteRestricted(string path, byte[] bytes)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The PC channel store uses Windows DPAPI, so this server runs on Windows only.");
        }

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        var owners = new[]
        {
            WindowsIdentity.GetCurrent().User!,
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
        };
        foreach (var owner in owners)
        {
            security.AddAccessRule(new FileSystemAccessRule(owner, FileSystemRights.FullControl, AccessControlType.Allow));
        }

        using var stream = new FileInfo(path).Create(
            FileMode.Create, FileSystemRights.FullControl, FileShare.None, bufferSize: 4096, FileOptions.None, security);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    private static byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The PC channel store uses Windows DPAPI, so this server runs on Windows only.");
        }

        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
    }

    private static byte[] Unprotect(byte[] encrypted)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("The PC channel store uses Windows DPAPI, so this server runs on Windows only.");
        }

        return ProtectedData.Unprotect(encrypted, Entropy, DataProtectionScope.LocalMachine);
    }

    private static byte[] CreatePfx()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=Lykantra PC channel", key, HashAlgorithmName.SHA256);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], critical: false));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(20));
        return certificate.Export(X509ContentType.Pkcs12);
    }

    // Schannel can't use the ephemeral key a fresh self-signed certificate holds, so the PFX is loaded with the
    // default key set: the key is imported into a temporary container that is removed again when the certificate
    // is disposed (no PersistKeySet), and it doesn't need machine-store permissions.
    private static X509Certificate2 LoadCertificate(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.DefaultKeySet);

    private sealed record StoredIdentity(int Version, string? Pfx, string? Secret);
}
