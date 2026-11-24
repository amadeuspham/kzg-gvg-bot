using System.Globalization;

namespace Kzg.Core.Jungle;

public enum JungleCommandKind { Start, Sync, Pause, Resume, Stop, Status, Test }

/// <param name="Time">Set only for <see cref="JungleCommandKind.Sync"/>.</param>
public sealed record JungleCommand(JungleCommandKind Kind, TimeSpan? Time = null);

/// <summary>Parses and formats game times, the MM:SS the in-game timer shows.</summary>
public static class JungleTime
{
    public const string SyncUsage = "Use MM:SS, from 35:00 down to 00:00, such as 24:30.";

    /// <summary>Parses MM:SS. On failure <paramref name="error"/> is the reply for the user.</summary>
    public static bool TryParse(string? text, out TimeSpan time, out string error)
    {
        time = default;
        error = SyncUsage;

        var split = text?.Trim().Split(':');
        if (split is not [var minutes, var seconds]
            || !IsDigits(minutes, 1, 2)
            || !IsDigits(seconds, 2, 2)
            || int.Parse(seconds, CultureInfo.InvariantCulture) > 59)
        {
            return false;
        }

        var parsed = new TimeSpan(0, int.Parse(minutes, CultureInfo.InvariantCulture),
            int.Parse(seconds, CultureInfo.InvariantCulture));
        if (parsed > MatchClock.MatchLength)
        {
            error = $"The match starts at 35:00. {SyncUsage}";
            return false;
        }

        time = parsed;
        error = "";
        return true;
    }

    /// <summary>Formats a game time as MM:SS, rounded down to whole seconds.</summary>
    public static string Format(TimeSpan time)
    {
        var seconds = (int)Math.Max(0, Math.Floor(time.TotalSeconds));
        return string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:00}:{seconds % 60:00}");
    }

    private static bool IsDigits(string s, int min, int max) =>
        s.Length >= min && s.Length <= max && s.All(char.IsAsciiDigit);
}
