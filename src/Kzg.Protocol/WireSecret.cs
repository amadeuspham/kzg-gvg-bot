using System.Buffers.Text;
using System.Security.Cryptography;

namespace Kzg.Protocol;

/// <summary>
/// A 256-bit secret on the wire: a connect code's secret part, a <c>loginId</c> or a device token.
/// Always unpadded base64url of exactly 32 bytes, in its one canonical spelling, so two spellings
/// never name the same secret.
/// </summary>
public static class WireSecret
{
    public const int ByteLength = 32;

    /// <summary>The length of the base64url text: 32 bytes take 43 characters without padding.</summary>
    public const int TextLength = 43;

    public static string Generate()
    {
        Span<byte> bytes = stackalloc byte[ByteLength];
        RandomNumberGenerator.Fill(bytes);
        return Encode(bytes);
    }

    public static string Encode(ReadOnlySpan<byte> secret) =>
        secret.Length == ByteLength
            ? Base64Url.EncodeToString(secret)
            : throw new ArgumentException($"a secret is {ByteLength} bytes", nameof(secret));

    public static bool IsValid(string? text) => TryDecode(text, out _);

    public static bool TryDecode(string? text, out byte[] secret)
    {
        secret = [];
        var bytes = new byte[ByteLength];
        if (text is null || text.Length != TextLength || !TryDecodeCanonical(text, bytes, out var written)
            || written != ByteLength)
        {
            return false;
        }
        secret = bytes;
        return true;
    }

    /// <summary>
    /// Decodes unpadded base64url, refusing every spelling but the one encoding writes: the decoder
    /// alone skips whitespace, takes padding, and throws on stray bits in the last character.
    /// </summary>
    internal static bool TryDecodeCanonical(string text, Span<byte> destination, out int written)
    {
        written = 0;
        if (!IsBase64UrlText(text))
        {
            return false;
        }
        try
        {
            if (!Base64Url.TryDecodeFromChars(text, destination, out written))
            {
                return false;
            }
        }
        catch (FormatException)
        {
            return false;
        }
        return Base64Url.EncodeToString(destination[..written]) == text;
    }

    private static bool IsBase64UrlText(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!(c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_'))
            {
                return false;
            }
        }
        return true;
    }
}
