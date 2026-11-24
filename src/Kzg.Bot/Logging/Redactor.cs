using System.Text.RegularExpressions;
using Kzg.Core.Config;

namespace Kzg.Bot.Logging;

/// <summary>
/// Masks everything in a log line that could sign someone in or act as the bot: the secrets Kzg holds,
/// by value; connect codes; anything shaped like a Discord token or a 256-bit secret; and the usual
/// credential parameters and headers. A SHA-256 shrinks to its first 8 digits. It over-masks on purpose.
/// </summary>
public sealed partial class Redactor
{
    public const string Mask = "[redacted]";

    // A secret shorter than this would also mask ordinary words.
    private const int MinLiteralLength = 6;

    private readonly string[] _literals;

    public Redactor(IEnumerable<string> secretValues)
    {
        // Longest first, so a secret that contains another is masked whole.
        _literals = [.. secretValues.Where(v => v.Length >= MinLiteralLength).Distinct(StringComparer.Ordinal).OrderByDescending(v => v.Length)];
    }

    /// <summary>A redactor that knows every value in <paramref name="secrets"/>.</summary>
    public static Redactor For(Secrets secrets) =>
        new(secrets.Names.Select(name => secrets.TryGet(name, out var value) ? value : null).OfType<string>());

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? "";
        }
        foreach (var literal in _literals)
        {
            text = text.Replace(literal, Mask, StringComparison.Ordinal);
        }
        text = ConnectCode().Replace(text, "KZG1." + Mask);
        text = JsonCredential().Replace(text, "\"$1\":\"" + Mask + "\"");
        text = Parameter().Replace(text, "$1=" + Mask);
        text = Authorization().Replace(text, "$1 " + Mask);
        text = Sha256Hex().Replace(text, match => match.Value[..8] + "…");
        text = LongToken().Replace(text, Mask);
        return DiscordToken().Replace(text, Mask);
    }

    [GeneratedRegex(@"KZG1\.[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+", RegexOptions.CultureInvariant)]
    private static partial Regex ConnectCode();

    [GeneratedRegex("\"(deviceToken|loginId|code|token|secret|oauthClientSecret|clientSecret|password)\"\\s*:\\s*\"[^\"]*\"", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex JsonCredential();

    [GeneratedRegex(@"(?<![A-Za-z0-9_])(code|state|login|loginId|token|deviceToken|access_token|refresh_token|client_secret|secret|password)=[^&\s""'<>]*", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Parameter();

    [GeneratedRegex(@"\b(Bearer|Bot|Basic)\s+[A-Za-z0-9_.~+/=-]{16,}", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase)]
    private static partial Regex Authorization();

    [GeneratedRegex("(?<![0-9a-fA-F])[0-9a-f]{64}(?![0-9a-fA-F])", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();

    // A loginId, device token or connect code's secret is 43 characters; nothing else in a log runs this long unbroken.
    [GeneratedRegex("(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{40,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex LongToken();

    // A bot token: three dotted parts, the first the bot's ID in base64.
    [GeneratedRegex(@"(?<![A-Za-z0-9_-])[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{20,}(?![A-Za-z0-9_-])", RegexOptions.CultureInvariant)]
    private static partial Regex DiscordToken();
}
