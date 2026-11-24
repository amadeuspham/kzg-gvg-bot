using System.Reflection;
using System.Runtime.InteropServices;

namespace Kzg.Discord.Dave;

/// <summary>
/// Finds libdave for NetCord. The libdave package copies its native files to <c>runtimes/&lt;rid&gt;/native</c>
/// unless the build names a runtime, and .NET looks there only for files a <c>.deps.json</c> lists, which these
/// aren't. So a plain build, a test run and a framework-dependent publish would all fail to load it;
/// this resolver also tries that folder, by the operating system and architecture alone.
/// </summary>
public static class LibDaveLoader
{
    public const string LibraryName = "libdave";

    private static readonly Lock Gate = new();
    private static bool _installed;

    /// <summary>Puts the resolver on NetCord's assembly, once.</summary>
    public static void Install()
    {
        lock (Gate)
        {
            if (_installed)
            {
                return;
            }
            _installed = true;
            try
            {
                NativeLibrary.SetDllImportResolver(typeof(NetCord.Gateway.Voice.VoiceClient).Assembly, Resolve);
            }
            catch (InvalidOperationException)
            {
                // Something else resolves for NetCord already; the check will say if libdave still doesn't load.
            }
        }
    }

    /// <summary>The folders a file named <paramref name="library"/> is looked for in, best first.</summary>
    internal static IEnumerable<string> Candidates(string baseDirectory, string rid, string library)
    {
        yield return Path.Combine(baseDirectory, library);
        yield return Path.Combine(baseDirectory, "runtimes", rid, "native", library);
    }

    /// <summary>The portable runtime identifier for this machine, such as <c>osx-arm64</c> or <c>linux-x64</c>.</summary>
    internal static string PortableRid(Architecture architecture)
    {
        var os = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
        var arch = architecture switch
        {
            Architecture.Arm64 => "arm64",
            Architecture.X86 => "x86",
            _ => "x64",
        };
        return $"{os}-{arch}";
    }

    internal static string FileName() =>
        OperatingSystem.IsWindows() ? "libdave.dll" : OperatingSystem.IsMacOS() ? "libdave.dylib" : "libdave.so";

    /// <summary>Loads libdave as NetCord's own import would, or from the runtimes folder; false when it can't be found.</summary>
    public static bool TryLoad(out nint handle)
    {
        var assembly = typeof(NetCord.Gateway.Voice.VoiceClient).Assembly;
        // The default probing first: a build with a runtime identifier puts the file beside the app.
        if (NativeLibrary.TryLoad(LibraryName, assembly, null, out handle))
        {
            return true;
        }
        var directories = new[] { AppContext.BaseDirectory, Path.GetDirectoryName(assembly.Location) }.OfType<string>().Distinct();
        foreach (var directory in directories)
        {
            foreach (var path in Candidates(directory, PortableRid(RuntimeInformation.ProcessArchitecture), FileName()))
            {
                if (File.Exists(path) && NativeLibrary.TryLoad(path, out handle))
                {
                    return true;
                }
            }
        }
        handle = 0;
        return false;
    }

    private static nint Resolve(string name, Assembly assembly, DllImportSearchPath? searchPath) =>
        name == LibraryName && TryLoad(out var handle) ? handle : 0;
}
