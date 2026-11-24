using System.Globalization;

namespace Kzg.Protocol;

/// <summary>
/// A Discord ID on the wire: a decimal string, because a 64-bit ID loses digits as a JSON number in
/// JavaScript-like parsers. Only the canonical form is accepted: digits, no sign, no leading zero, not 0.
/// </summary>
public static class Snowflake
{
    public static bool TryParse(string? text, out ulong id)
    {
        id = 0;
        if (string.IsNullOrEmpty(text) || text.Length > 20 || text[0] == '0')
        {
            return false;
        }
        foreach (var c in text)
        {
            if (c is < '0' or > '9')
            {
                return false;
            }
        }
        return ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out id);
    }

    public static bool IsValid(string? text) => TryParse(text, out _);

    public static string Format(ulong id) =>
        id == 0 ? throw new ArgumentOutOfRangeException(nameof(id), "a snowflake is never 0") : id.ToString(CultureInfo.InvariantCulture);
}
