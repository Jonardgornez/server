using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using LabGuard.Server.Network;

namespace LabGuard.Server.Tests;

public class PairingNumberTests
{
    // The PC computes the same number from the same inputs. A change to either side breaks this vector.
    [Fact]
    public void Compute_MatchesTheClientVector()
    {
        var keyHash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var nonce = Enumerable.Repeat((byte)0x22, 32).ToArray();

        Assert.Equal("860956", PairingNumber.Compute(keyHash, nonce));
    }

    [Fact]
    public void Compute_FromCertificate_UsesTheSha256OfThePublicKeyInfo()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=pairing test", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        var nonce = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();
        var keyHash = SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo());

        Assert.Equal(PairingNumber.Compute(keyHash, nonce), PairingNumber.Compute(certificate, nonce));
    }

    [Fact]
    public void Compute_ChangesWhenTheNonceChanges()
    {
        var keyHash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var nonce = Enumerable.Repeat((byte)0x22, 32).ToArray();
        var otherNonce = Enumerable.Repeat((byte)0x23, 32).ToArray();

        Assert.NotEqual(PairingNumber.Compute(keyHash, nonce), PairingNumber.Compute(keyHash, otherNonce));
    }

    [Fact]
    public void Compute_IsAlwaysSixDigits_WithLeadingZerosKept()
    {
        var keyHash = Enumerable.Repeat((byte)0x11, 32).ToArray();
        var numbers = Enumerable.Range(0, 2000)
            .Select(i => PairingNumber.Compute(keyHash, BitConverter.GetBytes(i).Concat(new byte[28]).ToArray()))
            .ToList();

        Assert.All(numbers, number => Assert.Matches("^[0-9]{6}$", number));
        Assert.Contains(numbers, number => number.StartsWith('0'));
    }
}
