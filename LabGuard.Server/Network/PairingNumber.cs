using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace LabGuard.Server.Network;

// Six digits that the PC and the administrator both show, so the administrator can confirm they are talking to the
// right PC before accepting it. It binds the PC's nonce and the server's nonce to the pinned channel key. The PC commits
// to its nonce before it sees the server's, and the server fixes its nonce before the PC reveals, so neither side can steer
// the number. The PC computes the same value; the vector in the unit tests is the cross-check.
public static class PairingNumber
{
    private const uint Modulus = 1_000_000;

    public static string Compute(X509Certificate2 certificate, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> serverNonce) =>
        Compute(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()), nonce, serverNonce);

    // SHA-256 over the label's UTF-8 bytes, then the 32-byte SHA-256 of the key's SubjectPublicKeyInfo, then both nonces.
    public static string Compute(ReadOnlySpan<byte> publicKeyHash, ReadOnlySpan<byte> nonce, ReadOnlySpan<byte> serverNonce)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData("lykantra-pairing-v2"u8);
        hash.AppendData(publicKeyHash);
        hash.AppendData(nonce);
        hash.AppendData(serverNonce);
        Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
        hash.GetHashAndReset(digest);

        var number = BinaryPrimitives.ReadUInt32BigEndian(digest) % Modulus;
        return number.ToString("D6", CultureInfo.InvariantCulture);
    }
}
