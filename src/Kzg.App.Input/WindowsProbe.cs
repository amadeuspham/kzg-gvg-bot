using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace Kzg.App.Input;

/// <summary>
/// Windows facts the input help needs. A hook set by a normal program doesn't see input aimed at an
/// administrator's window, such as a game started as administrator.
/// </summary>
[SupportedOSPlatform("windows")]
public static partial class WindowsProbe
{
    private const int ErrorCancelled = 1223;

    public static bool IsSelfElevated()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Starts this program again with the Windows administrator prompt. False when the user declines it.</summary>
    public static bool TryRelaunchElevated(string exePath, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(exePath) { UseShellExecute = true, Verb = "runas" };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        try
        {
            using var started = Process.Start(info);
            return started is not null;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == ErrorCancelled)
        {
            return false;
        }
    }

    /// <summary>
    /// The window the player is in: its process, and whether that runs as administrator. Null when there is no
    /// foreground window or its token can't be told apart from a failure. A process this one may not even open
    /// for a token counts as elevated: only an administrator's process refuses a normal one.
    /// </summary>
    public static ForegroundApp? ReadForeground()
    {
        var window = GetForegroundWindow();
        if (window == 0 || GetWindowThreadProcessId(window, out var processId) == 0 || processId == 0)
        {
            return null;
        }
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
        {
            return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? new ForegroundApp((int)processId, true) : null;
        }
        try
        {
            if (!OpenProcessToken(process, TokenQuery, out var token))
            {
                return Marshal.GetLastPInvokeError() == ErrorAccessDenied ? new ForegroundApp((int)processId, true) : null;
            }
            try
            {
                return GetTokenInformation(token, TokenElevationClass, out var elevation, sizeof(uint), out _)
                    ? new ForegroundApp((int)processId, elevation != 0)
                    : null;
            }
            finally
            {
                _ = CloseHandle(token);
            }
        }
        finally
        {
            _ = CloseHandle(process);
        }
    }

    private const int ErrorAccessDenied = 5;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenQuery = 0x0008;
    private const int TokenElevationClass = 20;

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, uint processId);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, out uint elevation, uint length, out uint returned);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    /// <summary>The system's alert sound.</summary>
    public static void Beep() => _ = MessageBeep(0x10);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MessageBeep(uint type);
}

/// <summary>The foreground window's process, as far as the elevation check needs.</summary>
public readonly record struct ForegroundApp(int ProcessId, bool IsElevated);

/// <summary>Tells what the player is in front of. A seam so the logic that reads it is tested without Windows.</summary>
public interface IForegroundProbe
{
    /// <summary>The foreground process, or null when there is none or it can't be told.</summary>
    ForegroundApp? Read();
}

/// <summary>The Windows probe; on any other system it sees nothing, so nothing in the app depends on the platform.</summary>
public sealed class SystemForegroundProbe : IForegroundProbe
{
    public ForegroundApp? Read() => OperatingSystem.IsWindows() ? WindowsProbe.ReadForeground() : null;
}
