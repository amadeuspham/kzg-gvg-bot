using System.Runtime.InteropServices;

namespace InputSpike;

/// <summary>
/// macOS privacy checks for a global listen-only hook.
/// Accessibility: AXIsProcessTrusted. Input Monitoring: IOHIDCheckAccess / CGPreflightListenEventAccess.
/// The "Request" calls show the system prompt (once per binary identity); they never change settings.
/// </summary>
static partial class MacPermissions
{
    const string AppServices = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    [LibraryImport(AppServices)] [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AXIsProcessTrusted();
    [LibraryImport(AppServices)] [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool AXIsProcessTrustedWithOptions(IntPtr options);
    [LibraryImport(CoreFoundation)]
    private static partial IntPtr CFDictionaryCreate(IntPtr alloc, IntPtr[] keys, IntPtr[] values, nint n, IntPtr keyCb, IntPtr valCb);
    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);
    [LibraryImport(IOKit)]
    private static partial int IOHIDCheckAccess(uint requestType);   // 1 = listen event
    [LibraryImport(IOKit)] [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool IOHIDRequestAccess(uint requestType);
    [LibraryImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CGPreflightListenEventAccess();
    [LibraryImport(CoreGraphics)] [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CGRequestListenEventAccess();

    const uint HidListenEvent = 1;

    public static bool Accessibility() => AXIsProcessTrusted();

    /// <summary>0 = granted, 1 = denied, 2 = not yet asked (kIOHIDAccessType*).</summary>
    public static string InputMonitoring() => IOHIDCheckAccess(HidListenEvent) switch
    {
        0 => "granted",
        1 => "denied",
        2 => "not-determined",
        var n => $"unknown({n})",
    };

    public static void Report()
    {
        Log.Write("perm", $"Accessibility    : {(Accessibility() ? "granted" : "NOT granted")}");
        Log.Write("perm", $"Input Monitoring : {InputMonitoring()} (CGPreflightListenEventAccess={CGPreflightListenEventAccess()})");
    }

    /// <summary>Triggers both system prompts. Safe to call repeatedly; macOS only prompts while the state is not-determined.</summary>
    public static void Request()
    {
        var lib = NativeLibrary.Load(AppServices);
        var keyPtr = NativeLibrary.GetExport(lib, "kAXTrustedCheckOptionPrompt");
        var cf = NativeLibrary.Load(CoreFoundation);
        var key = Marshal.ReadIntPtr(keyPtr);
        var yes = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFBooleanTrue"));
        var keyCb = NativeLibrary.GetExport(cf, "kCFTypeDictionaryKeyCallBacks");
        var valCb = NativeLibrary.GetExport(cf, "kCFTypeDictionaryValueCallBacks");
        var dict = CFDictionaryCreate(IntPtr.Zero, [key], [yes], 1, keyCb, valCb);
        var ax = AXIsProcessTrustedWithOptions(dict);
        CFRelease(dict);
        Log.Write("perm", $"Requested Accessibility prompt; trusted now = {ax}");
        var im = IOHIDRequestAccess(HidListenEvent);
        Log.Write("perm", $"Requested Input Monitoring prompt; granted now = {im}");
    }
}
