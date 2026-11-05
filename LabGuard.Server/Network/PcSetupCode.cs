using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace LabGuard.Server.Network;

// Setup code: 24 characters of RFC 4648 base32, shown as six groups of four.
// First 14 characters: fingerprint of the certificate's public key. Last 10: the registration secret.
public static class PcSetupCode
{
    public const int FingerprintLength = 14;
    public const int SecretLength = 10;
    public const int CodeLength = FingerprintLength + SecretLength;

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string FingerprintOf(X509Certificate2 certificate) =>
        Base32(SHA256.HashData(certificate.PublicKey.ExportSubjectPublicKeyInfo()))[..FingerprintLength];

    // 50 bits: the low five bits of ten random bytes (256 is a multiple of 32, so there is no bias).
    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(SecretLength);
        var builder = new StringBuilder(SecretLength);
        foreach (var value in bytes)
        {
            builder.Append(Alphabet[value & 31]);
        }

        return builder.ToString();
    }

    public static bool IsValidSecret(string secret) =>
        secret.Length == SecretLength && secret.All(character => Alphabet.Contains(character));

    // Removes spaces and hyphens and uppercases.
    public static string Normalize(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text.Replace(" ", "").Replace("-", "").ToUpperInvariant();

    public static string Format(string fingerprint, string secret)
    {
        var code = fingerprint + secret;
        return string.Join('-', Enumerable.Range(0, CodeLength / 4).Select(group => code.Substring(group * 4, 4)));
    }

    private static string Base32(byte[] bytes)
    {
        var builder = new StringBuilder((bytes.Length * 8 + 4) / 5);
        var buffer = 0;
        var bits = 0;
        foreach (var value in bytes)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }

            buffer &= (1 << bits) - 1;
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }
}
