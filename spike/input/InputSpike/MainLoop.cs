using System.Runtime.InteropServices;

namespace InputSpike;

/// <summary>
/// Keeps the main thread servicing its run loop and the main dispatch queue until cancelled.
/// macOS: libuiohook's tap callback runs on the hook thread and, for NX_SYSDEFINED events (and key-typed lookups),
/// does dispatch_sync_f onto the main queue. A main thread parked in WaitOne never drains it, so the callback blocks
/// forever, the active tap stalls everyone's input until macOS disables it, and the hook delivers nothing (README).
/// Avalonia's UI thread drains the main queue already; this is the console stand-in.
/// </summary>
static partial class MainLoop
{
    const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    [LibraryImport(CoreFoundation)]
    private static partial int CFRunLoopRunInMode(IntPtr mode, double seconds, [MarshalAs(UnmanagedType.U1)] bool returnAfterSourceHandled);

    public static void Run(CancellationToken ct)
    {
        if (!OperatingSystem.IsMacOS()) { ct.WaitHandle.WaitOne(); return; }
        var cf = NativeLibrary.Load(CoreFoundation);
        var defaultMode = Marshal.ReadIntPtr(NativeLibrary.GetExport(cf, "kCFRunLoopDefaultMode"));
        // The main run loop in a common mode is never "empty" (the main queue counts as a source), so each call
        // blocks up to 250 ms or until work arrives; the short slice is only for noticing cancellation.
        while (!ct.IsCancellationRequested) CFRunLoopRunInMode(defaultMode, 0.25, false);
    }
}
