using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Security.Principal;
using System.Text.Json;

namespace LabGuard.Server.Network;

// The identity of the PC channel: a self-signed certificate that PCs pin by public key, stored as one small JSON
// file holding the PFX and a format version. How the file is protected depends on the platform:
// - Windows: the file is encrypted as a whole with DPAPI (machine scope) and its ACL admits only the server account,
//   Administrators and SYSTEM, so copying it to another machine reveals nothing.
// - Elsewhere (the Linux container): there is no DPAPI, so the JSON is stored as is ("portable" format) and its only
//   protection is the file mode. It is created as 0600, and a file that grants any group or other access stops startup.
// ExportPortable converts a Windows file into the portable format once, so a server can move into the container
// without setting up every PC again.
public sealed class PcChannelStore : IDisposable
{
    public const string DefaultFileName = "pc-channel.dat";
    public const string FileConfigurationKey = "Server:PcChannelFile";

    private const int FormatVersion = 1;
    private const UnixFileMode GroupOrOtherAccess =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
    private static readonly byte[] Entropy = "Lykantra PC channel v1"u8.ToArray();

    private readonly string _path;
    private readonly byte[] _pfx;

    private PcChannelStore(string path, byte[] pfx)
    {
        _path = path;
        _pfx = pfx;
        Certificate = LoadCertificate(pfx);
    }

    // Kestrel serves this one instance for the life of the process.
    public X509Certificate2 Certificate { get; }

    public static string ResolvePath(IConfiguration configuration, string contentRootPath)
    {
        var configured = configuration[FileConfigurationKey];
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultFileName : configured, contentRootPath);
    }

    // Reads the file, or creates it on first run. A file that can't be read is an error, never replaced:
    // a new identity would silently disconnect every registered PC.
    public static PcChannelStore Open(string path)
    {
        if (!File.Exists(path))
        {
            var created = new PcChannelStore(path, CreatePfx());
            created.Save();
            return created;
        }

        return OperatingSystem.IsWindows() ? Load(path, Unprotect) : OpenPortable(path);
    }

    // Writes the identity held in an existing DPAPI file to a new portable file. It never creates an identity
    // and never overwrites the destination. The destination holds the private key unencrypted.
    [SupportedOSPlatform("windows")]
    public static void ExportPortable(string sourceDpapiPath, string destinationPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Exporting reads a DPAPI-protected file, which only Windows can do.");
        }

        if (!File.Exists(sourceDpapiPath))
        {
            throw new InvalidOperationException($"There is no PC channel file at '{sourceDpapiPath}', so there is nothing to export.");
        }

        if (File.Exists(destinationPath))
        {
            throw new InvalidOperationException($"'{destinationPath}' already exists. It is not overwritten; choose another destination.");
        }

        using var source = Load(sourceDpapiPath, Unprotect);
        WritePortable(destinationPath, source._pfx, overwrite: false);
    }

    internal static PcChannelStore OpenPortable(string path)
    {
        EnsureOwnerOnly(path);
        return Load(path, static bytes => bytes);
    }

    internal static void WritePortable(string path, byte[] pfx, bool overwrite)
    {
        var json = Serialize(pfx);
        try
        {
            WriteAtomically(path, json, overwrite);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(json);
        }
    }

    // Releases the certificate, which also removes the temporary key container Windows created for it.
    public void Dispose() => Certificate.Dispose();

    private void Save()
    {
        if (!OperatingSystem.IsWindows())
        {
            WritePortable(_path, _pfx, overwrite: true);
            return;
        }

        var json = Serialize(_pfx);
        var protectedBytes = Protect(json);
        CryptographicOperations.ZeroMemory(json);
        WriteAtomically(_path, protectedBytes, overwrite: true);
    }

    private static PcChannelStore Load(string path, Func<byte[], byte[]> decode)
    {
        try
        {
            // Files written before setup codes were removed also hold a Secret field. It is ignored here.
            var stored = JsonSerializer.Deserialize<StoredIdentity>(decode(File.ReadAllBytes(path)));
            if (stored is not { Version: FormatVersion, Pfx: not null })
            {
                throw new InvalidDataException("The file has an unexpected layout.");
            }

            return new PcChannelStore(path, Convert.FromBase64String(stored.Pfx));
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

    private static byte[] Serialize(byte[] pfx) =>
        JsonSerializer.SerializeToUtf8Bytes(new StoredIdentity(FormatVersion, Convert.ToBase64String(pfx)));

    // Written beside the target and moved over it, so a crash never leaves a half-written file.
    private static void WriteAtomically(string path, byte[] bytes, bool overwrite)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = path + ".tmp";
        if (OperatingSystem.IsWindows())
        {
            WriteRestricted(temp, bytes);
        }
        else
        {
            WriteOwnerOnly(temp, bytes);
        }

        try
        {
            File.Move(temp, path, overwrite);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    // DPAPI's machine scope lets any local process decrypt the file, so its permissions are what
    // keep the private key from other accounts on this PC. Moving the file keeps these permissions.
    [SupportedOSPlatform("windows")]
    private static void WriteRestricted(string path, byte[] bytes)
    {
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

    // The mode is set when the file is created, so the key is never readable by others, not even briefly.
    // It only applies to a new file, so a temp file left by a crash is removed first.
    [UnsupportedOSPlatform("windows")]
    private static void WriteOwnerOnly(string path, byte[] bytes)
    {
        File.Delete(path);
        using var stream = new FileStream(path, new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        });
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    // A portable file is protected by its mode alone, so a looser mode is refused rather than tightened silently:
    // the key may already have been exposed, and the operator should know.
    private static void EnsureOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        if ((File.GetUnixFileMode(path) & GroupOrOtherAccess) != 0)
        {
            throw new InvalidOperationException(
                $"The PC channel file '{path}' holds the channel's private key unencrypted, but other accounts can access it. " +
                $"Restrict it to the server account (chmod 600 '{path}') and start the server again.");
        }
    }

    private static byte[] Protect(byte[] plain)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is available on Windows only.");
        }

        return ProtectedData.Protect(plain, Entropy, DataProtectionScope.LocalMachine);
    }

    private static byte[] Unprotect(byte[] encrypted)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("DPAPI is available on Windows only.");
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
    // is disposed (no PersistKeySet), and it doesn't need machine-store permissions. On Linux the same flags simply
    // keep the key in memory.
    private static X509Certificate2 LoadCertificate(byte[] pfx) =>
        X509CertificateLoader.LoadPkcs12(pfx, password: null, X509KeyStorageFlags.DefaultKeySet);

    private sealed record StoredIdentity(int Version, string? Pfx);
}
