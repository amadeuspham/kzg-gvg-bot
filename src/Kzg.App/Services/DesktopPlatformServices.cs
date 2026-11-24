using System.Diagnostics;
using Avalonia.Threading;
using Kzg.App.Input;

namespace Kzg.App.Services;

public sealed class AvaloniaUiThread : IUiThread
{
    public void Post(Action action) => Dispatcher.UIThread.Post(action);
}

/// <summary>The real thing: the default browser, the Windows administrator prompt, a macOS or Windows alert.</summary>
public sealed class DesktopPlatformServices(Action quit) : IPlatformServices
{
    public bool IsWindows => OperatingSystem.IsWindows();

    public bool IsMac => OperatingSystem.IsMacOS();

    public bool IsElevated => OperatingSystem.IsWindows() && WindowsProbe.IsSelfElevated();

    public void OpenUrl(string url)
    {
        // Only web pages: the address comes from settings, so never hand anything else to the shell.
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
        {
            return;
        }
        try
        {
            using var _ = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No browser to open; the link screen still says what to do.
        }
    }

    public bool TryRestartElevated() =>
        OperatingSystem.IsWindows()
        && Environment.ProcessPath is { } exe
        && WindowsProbe.TryRelaunchElevated(exe, Environment.GetCommandLineArgs().Skip(1));

    public bool TryRestartApp()
    {
        if (!OperatingSystem.IsMacOS() || Environment.ProcessPath is not { } exe || !MacLaunchAgent.IsInsideBundle(exe))
        {
            return false;
        }
        var bundle = exe[..(exe.IndexOf(".app/Contents/MacOS/", StringComparison.Ordinal) + ".app".Length)];
        // The new copy waits for this process to be gone: until then the single-instance lock is ours, and a copy started
        // too early would only hand over to us and leave. The values are passed as arguments, never put into the script.
        var info = new ProcessStartInfo("/bin/sh")
        {
            UseShellExecute = false,
            ArgumentList = { "-c", "while kill -0 \"$1\" 2>/dev/null; do sleep 0.2; done; exec open -n \"$2\"", "sh", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), bundle },
        };
        try
        {
            using var _ = Process.Start(info);
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Notify(string title, string text)
    {
        if (OperatingSystem.IsMacOS())
        {
            // A notification with the system's own alert sound; the text is ours and is quoted for AppleScript.
            var script = $"display notification \"{Quote(text)}\" with title \"{Quote(title)}\" sound name \"Basso\"";
            try
            {
                using var _ = Process.Start(new ProcessStartInfo("osascript") { ArgumentList = { "-e", script }, UseShellExecute = false });
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // No osascript: no notification.
            }
        }
        else if (OperatingSystem.IsWindows())
        {
            WindowsProbe.Beep();
        }
    }

    public void Quit() => quit();

    private static string Quote(string text) => text.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
