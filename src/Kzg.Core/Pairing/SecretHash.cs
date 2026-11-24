using System.Security.Cryptography;
using Kzg.Protocol;

namespace Kzg.Core.Pairing;

/// <summary>
/// How the bot keeps a secret: only its SHA-256, as 64 lower-case hex digits. A 256-bit random secret
/// can't be found from its hash, so a leaked file or log signs no one in; logs show 8 digits at most.
/// </summary>
public static class SecretHash
{
    public static string Of(ReadOnlySpan<byte> secret) => Convert.ToHexStringLower(SHA256.HashData(secret));

    /// <summary>The hash of a wire secret's bytes; null when the text isn't a canonical secret.</summary>
    public static string? OfWire(string? text) => WireSecret.TryDecode(text, out var bytes) ? Of(bytes) : null;

    /// <summary>Compares two hashes in time that doesn't depend on where they differ.</summary>
    public static bool Same(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(
            System.Text.Encoding.ASCII.GetBytes(a), System.Text.Encoding.ASCII.GetBytes(b));

    /// <summary>What a log may show of a hash.</summary>
    public static string Prefix(string hash) => hash[..Math.Min(8, hash.Length)];
}
