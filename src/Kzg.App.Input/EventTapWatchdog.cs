using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kzg.App.Input;

/// <summary>What the system says about this process's keyboard and mouse tap.</summary>
public enum TapState
{
    /// <summary>Can't tell: not macOS, or no tap of ours is listed.</summary>
    Unknown,
    Enabled,

    /// <summary>macOS switched the tap off, e.g. after the app was too slow to answer it. The hook then delivers nothing.</summary>
    Disabled,
}

public interface IEventTapProbe
{
    TapState Check();
}

/// <summary>
/// Reads the system's list of event taps (<c>CGGetEventTapList</c>, no permission needed) and reports this
/// process's tap. libuiohook's tap is an active one, and macOS turns it off for good if its callback
/// stalls, which is what happens when the main thread stops draining the main queue.
/// </summary>
public static unsafe partial class MacEventTaps
{
    public static IEventTapProbe Probe { get; } = new SystemProbe();

    private sealed class SystemProbe : IEventTapProbe
    {
        public TapState Check() => OperatingSystem.IsMacOS() ? ReadOwnTap() : TapState.Unknown;
    }

    // Mirrors CGEventTapInformation: 48 bytes, naturally aligned.
    [StructLayout(LayoutKind.Sequential)]
    private struct TapInformation
    {
        public uint EventTapId;
        public uint TapPoint;
        public uint Options;
        public ulong EventsOfInterest;
        public int TappingProcess;
        public int ProcessBeingTapped;
        public byte Enabled;
        public float MinLatency;
        public float AvgLatency;
        public float MaxLatency;
    }

    [SupportedOSPlatform("macos")]
    private static TapState ReadOwnTap()
    {
        uint count = 0;
        if (CGGetEventTapList(0, null, &count) != 0 || count == 0)
        {
            return TapState.Unknown;
        }
        var list = new TapInformation[count];
        fixed (TapInformation* first = list)
        {
            if (CGGetEventTapList(count, first, &count) != 0)
            {
                return TapState.Unknown;
            }
        }
        var ours = list.Take((int)count).Where(t => t.TappingProcess == Environment.ProcessId).ToList();
        return ours.Count == 0 ? TapState.Unknown
            : ours.Any(t => t.Enabled != 0) ? TapState.Enabled
            : TapState.Disabled;
    }

    [LibraryImport("/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics")]
    private static partial int CGGetEventTapList(uint maxTaps, TapInformation* list, uint* count);
}

/// <summary>Turns "our tap is disabled" into a failed hook. Run <see cref="Check"/> every few seconds, off the UI thread.</summary>
public sealed class HookWatchdog(IEventTapProbe probe)
{
    public const string FailureText = "macOS switched the key listener off.";

    /// <summary>True when a hook that claims to be running has been turned off by the system.</summary>
    public bool Check(HookHealth health) => health == HookHealth.Ok && probe.Check() == TapState.Disabled;
}
