using Kzg.Core.Abstractions;

namespace Kzg.App.Input;

/// <summary>
/// Notices when the player is in a window that runs as administrator while this app does not: Windows hides
/// such a window's input from a normal program's hook. It looks about once a second. This app's own window,
/// and a moment with no readable foreground window, change nothing: the player has to be able to click the
/// warning's button, which puts this app in front.
/// </summary>
public sealed class ElevationWatch(IForegroundProbe probe, ITimeSource time, int ownProcessId)
{
    private static readonly TimeSpan Every = TimeSpan.FromSeconds(1);

    private TimeSpan _nextLook = TimeSpan.MinValue;

    /// <summary>The last other window the player was in runs as administrator.</summary>
    public bool ElevatedWindowInFront { get; private set; }

    /// <summary>Looks if a second has passed since the last look. True when <see cref="ElevatedWindowInFront"/> changed.</summary>
    public bool Poll()
    {
        var now = time.Now;
        if (now < _nextLook)
        {
            return false;
        }
        _nextLook = now + Every;
        if (probe.Read() is not { } app || app.ProcessId == ownProcessId)
        {
            return false;
        }
        var changed = app.IsElevated != ElevatedWindowInFront;
        ElevatedWindowInFront = app.IsElevated;
        return changed;
    }
}
