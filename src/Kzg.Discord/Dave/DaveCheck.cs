using System.Runtime.InteropServices;

namespace Kzg.Discord.Dave;

public enum DaveStatus
{
    Ok,

    /// <summary>The native library didn't load, so no voice channel can be joined.</summary>
    NotFound,

    /// <summary>The library is 1.3.0 or newer, which NetCord beta.30 can't talk to.</summary>
    TooNew,
}

public sealed record DaveCheckResult(DaveStatus Status, string Message)
{
    public bool Ok => Status == DaveStatus.Ok;
}

/// <summary>Asks the native library what it is, so a test can answer without one.</summary>
public interface IDaveProbe
{
    bool TryLoad();

    bool Exports(string symbol);
}

/// <summary>
/// The startup check for libdave. NetCord beta.30 calls libdave 1.2.1.9's functions by their old
/// signatures; 1.3.0 changed two of them, and with it no MLS group forms, every voice frame is dropped
/// and nothing says why. 1.3.0 adds <c>daveSessionAddRecognizedUser</c>, which 1.2.1.9 lacks, so
/// finding it means the wrong library is loaded.
/// </summary>
public static class DaveCheck
{
    public const string PinnedVersion = "1.2.1.9";

    /// <summary>Exported by libdave 1.3.0 and newer, never by 1.2.1.9.</summary>
    public const string NewerOnlyExport = "daveSessionAddRecognizedUser";

    public static DaveCheckResult Run(IDaveProbe? probe = null)
    {
        probe ??= new NativeDaveProbe();
        if (!probe.TryLoad())
        {
            return new DaveCheckResult(DaveStatus.NotFound,
                $"libdave {PinnedVersion} didn't load, so the bot can't join voice. Its native library ships beside the bot; on Linux it needs glibc 2.38 or newer.");
        }
        return probe.Exports(NewerOnlyExport)
            ? new DaveCheckResult(DaveStatus.TooNew,
                $"libdave 1.3.0 or newer is loaded, and NetCord beta.30 can't use it: no voice frame would get through. Use libdave {PinnedVersion}.")
            : new DaveCheckResult(DaveStatus.Ok, $"libdave {PinnedVersion} loaded");
    }

    /// <summary>Loads "libdave" the way NetCord's own import does, from beside the bot.</summary>
    private sealed class NativeDaveProbe : IDaveProbe
    {
        private nint _handle;

        public bool TryLoad()
        {
            LibDaveLoader.Install();
            return LibDaveLoader.TryLoad(out _handle);
        }

        public bool Exports(string symbol) => _handle != 0 && NativeLibrary.TryGetExport(_handle, symbol, out _);
    }
}
