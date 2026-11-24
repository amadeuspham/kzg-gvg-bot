using System.Security;
using System.Text;

namespace Kzg.App.Services;

/// <summary>The "Open at login" switch. Off until the player turns it on; the system's own list is the truth, not a setting of ours.</summary>
public interface IStartAtLogin
{
    /// <summary>False where the app can't be started at login: a dev build run from a build folder, or a system without a way.</summary>
    bool IsAvailable { get; }

    bool IsEnabled { get; }

    /// <exception cref="IOException">The system's list could not be changed.</exception>
    /// <exception cref="UnauthorizedAccessException">The system's list may not be changed by this user.</exception>
    void Set(bool enabled);
}

/// <summary>The one place that chooses the start-at-login mechanism for this system and this copy of the app.</summary>
public static class StartAtLogin
{
    /// <summary>The login entry for the installed app, or null when this run is not an installed copy (so a dev build never registers itself).</summary>
    public static IStartAtLogin? ForThisProcess()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
        {
            return null;
        }
        if (OperatingSystem.IsMacOS() && MacLaunchAgent.IsInsideBundle(exe))
        {
            var folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");
            return new MacLaunchAgent(folder, exe);
        }
        if (OperatingSystem.IsWindows() && WindowsRunKey.IsInstalledCopy(exe))
        {
            return new WindowsRunKey(new RegistryRunValues(), exe);
        }
        return null;
    }
}

/// <summary>
/// macOS: a launch agent in <c>~/Library/LaunchAgents</c> that opens the app, minimized, at login. A file in the
/// user's own folder is all it takes; nothing is loaded now, so it takes effect at the next login.
/// </summary>
public sealed class MacLaunchAgent(string folder, string exe) : IStartAtLogin
{
    public const string Label = "app.kzg.login";

    private string PlistPath => Path.Combine(folder, Label + ".plist");

    public bool IsAvailable => true;

    public bool IsEnabled => File.Exists(PlistPath);

    /// <summary>True for <c>/Applications/KZG Broadcast.app/Contents/MacOS/Kzg.App</c>, not for a binary in a build folder.</summary>
    public static bool IsInsideBundle(string exe) => exe.Contains(".app/Contents/MacOS/", StringComparison.Ordinal);

    public void Set(bool enabled)
    {
        if (!enabled)
        {
            File.Delete(PlistPath);
            return;
        }
        Directory.CreateDirectory(folder);
        var temporary = PlistPath + ".tmp";
        File.WriteAllText(temporary, Plist(), new UTF8Encoding(false));
        File.Move(temporary, PlistPath, overwrite: true);
    }

    private string Plist() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{SecurityElement.Escape(Label)}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{SecurityElement.Escape(exe)}</string>
                <string>--minimized</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
        </dict>
        </plist>

        """;
}

/// <summary>The current user's Run list on Windows, as far as the app needs it.</summary>
public interface IRunValues
{
    string? Get(string name);

    void Set(string name, string command);

    void Remove(string name);
}

/// <summary>Windows: a value in the current user's <c>Run</c> key, which Windows starts at sign-in without asking for administrator rights.</summary>
public sealed class WindowsRunKey(IRunValues values, string exe) : IStartAtLogin
{
    public const string ValueName = "KZG Broadcast";

    public bool IsAvailable => true;

    public bool IsEnabled => values.Get(ValueName) is { Length: > 0 };

    /// <summary>Velopack runs the app from <c>%LOCALAPPDATA%\KZGBroadcast\current</c>; any other folder is a build or a portable copy.</summary>
    public static bool IsInstalledCopy(string exe) =>
        exe.Contains(@"\KZGBroadcast\current\", StringComparison.OrdinalIgnoreCase);

    public void Set(bool enabled)
    {
        if (enabled)
        {
            values.Set(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            values.Remove(ValueName);
        }
    }
}

internal sealed class RegistryRunValues : IRunValues
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public string? Get(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(name) as string;
    }

    public void Set(string name, string command)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        key.SetValue(name, command);
    }

    public void Remove(string name)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        key?.DeleteValue(name, throwOnMissingValue: false);
    }
}
