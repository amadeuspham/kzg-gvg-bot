using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace InputSpike;

/// <summary>Windows-only helpers: our elevation, the foreground process and its elevation, and a GetAsyncKeyState poller.</summary>
static partial class WinProbe
{
    [LibraryImport("user32.dll")] private static partial IntPtr GetForegroundWindow();
    [LibraryImport("user32.dll")] private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [LibraryImport("user32.dll")] private static partial short GetAsyncKeyState(int vKey);
    [LibraryImport("kernel32.dll")] private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);
    [LibraryImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool CloseHandle(IntPtr h);
    [LibraryImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool OpenProcessToken(IntPtr proc, uint access, out IntPtr token);
    [LibraryImport("advapi32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static partial bool GetTokenInformation(IntPtr token, int cls, out int info, int len, out int ret);

    const uint ProcessQueryLimitedInformation = 0x1000;
    const uint TokenQuery = 0x0008;
    const int TokenElevation = 20;

    public static bool IsSelfElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    /// <summary>Cheap call safe on the hook thread: just the handle. Resolve later with <see cref="Describe"/>.</summary>
    public static IntPtr ForegroundHandle() => GetForegroundWindow();

    public static string Foreground() => Describe(GetForegroundWindow());

    /// <summary>"name (pid N, elevated|not elevated|elevation unknown)" for a window. Slow: never call from the hook thread.</summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "none";
        GetWindowThreadProcessId(hwnd, out var pid);
        string name;
        try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = "?"; }
        var h = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        string elev = "elevation unknown (access denied: probably elevated)";
        if (h != IntPtr.Zero)
        {
            if (OpenProcessToken(h, TokenQuery, out var tok))
            {
                if (GetTokenInformation(tok, TokenElevation, out var e, sizeof(int), out _))
                    elev = e != 0 ? "elevated" : "not elevated";
                CloseHandle(tok);
            }
            CloseHandle(h);
        }
        return $"{name} (pid {pid}, {elev})";
    }

    /// <summary>Polls GetAsyncKeyState for F8 and the two mouse side buttons; a comparison probe against the hook.</summary>
    public static void PollLoop(CancellationToken ct)
    {
        (int vk, string name)[] keys = [(0x77, "F8"), (0x05, "XButton1"), (0x06, "XButton2")];
        var down = new bool[keys.Length];
        Log.Write("poll", "GetAsyncKeyState poller running for F8, XButton1, XButton2 (5 ms interval)");
        while (!ct.IsCancellationRequested)
        {
            for (var i = 0; i < keys.Length; i++)
            {
                var d = (GetAsyncKeyState(keys[i].vk) & 0x8000) != 0;
                if (d == down[i]) continue;
                down[i] = d;
                Log.Write("poll", $"{keys[i].name} {(d ? "DOWN" : "UP")}", GetForegroundWindow());
            }
            Thread.Sleep(5);
        }
    }
}
