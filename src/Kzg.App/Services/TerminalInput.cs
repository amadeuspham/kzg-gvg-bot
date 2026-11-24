using System.Runtime.InteropServices;

namespace Kzg.App.Services;

/// <summary>
/// Lets go of the terminal as the app's standard input. When stdin is a terminal, .NET keeps its settings and
/// puts them back on SIGCONT; from a background process group (the shell took the terminal after a Ctrl-Z or a
/// SIGSTOP) that call raises SIGTTOU and stops the whole process, so the app stays frozen after "continue".
/// The app never reads stdin, so a started-from-a-terminal app gets /dev/null, as one started from Finder has.
/// </summary>
internal static partial class TerminalInput
{
    private const int ReadWrite = 2;

    /// <summary>Replaces a terminal on <paramref name="fd"/> with /dev/null. True when it did; anything else is left as it is.</summary>
    public static bool Detach(int fd = 0)
    {
        if (OperatingSystem.IsWindows() || IsATty(fd) != 1)
        {
            return false;
        }
        var empty = Open("/dev/null", ReadWrite);
        if (empty < 0)
        {
            return false;
        }
        try
        {
            return Duplicate(empty, fd) >= 0;
        }
        finally
        {
            _ = Close(empty);
        }
    }

    [LibraryImport("libc", EntryPoint = "isatty")]
    private static partial int IsATty(int fd);

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int Open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "dup2")]
    private static partial int Duplicate(int from, int to);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
