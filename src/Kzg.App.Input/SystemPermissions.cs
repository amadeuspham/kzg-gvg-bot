using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Kzg.App.Input;

/// <summary>
/// What the app needs from the operating system before keys reach it. Only macOS asks: a listen-only event
/// tap needs Input Monitoring, and nothing else (not Accessibility). Reading the state never shows a prompt.
/// </summary>
public interface IPermissionProbe
{
    PermissionState InputMonitoring { get; }

    /// <summary>Asks macOS to show its own prompts. Call it only after the user chose to.</summary>
    void RequestPrompts();

    void OpenInputMonitoringSettings();
}

public sealed partial class SystemPermissions : IPermissionProbe
{
    public PermissionState InputMonitoring => OperatingSystem.IsMacOS() ? MacOs.InputMonitoring() : PermissionState.NotApplicable;

    public void RequestPrompts()
    {
        if (OperatingSystem.IsMacOS())
        {
            MacOs.RequestPrompts();
        }
    }

    public void OpenInputMonitoringSettings() => Open("Privacy_ListenEvent");

    private static void Open(string pane)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        // A settings page only; nothing is changed for the user.
        using var _ = Process.Start(new ProcessStartInfo("open", $"x-apple.systempreferences:com.apple.preference.security?{pane}")
        {
            UseShellExecute = false,
        });
    }

    [SupportedOSPlatform("macos")]
    private static partial class MacOs
    {
        private const string IoKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
        private const uint HidListenEvent = 1;

        public static PermissionState InputMonitoring() => IOHIDCheckAccess(HidListenEvent) switch
        {
            0 => PermissionState.Granted,
            1 => PermissionState.Denied,
            _ => PermissionState.NotAsked,
        };

        public static void RequestPrompts() => _ = IOHIDRequestAccess(HidListenEvent);

        [LibraryImport(IoKit)]
        private static partial int IOHIDCheckAccess(uint requestType);

        [LibraryImport(IoKit)]
        [return: MarshalAs(UnmanagedType.U1)]
        private static partial bool IOHIDRequestAccess(uint requestType);
    }
}
