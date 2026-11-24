using System.Runtime.InteropServices;

namespace Kzg.Bot.Tests.Load;

/// <summary>CPU time the calling thread has used so far. The engine thread reads it on itself, because .NET can't read another thread's on macOS.</summary>
internal static partial class ThreadCpu
{
    private const int MacThreadCpuClock = 16;
    private const int LinuxThreadCpuClock = 3;

    public static long NowNanoseconds()
    {
        if (OperatingSystem.IsWindows())
        {
            return GetThreadTimes(GetCurrentThread(), out _, out _, out var kernel, out var user) ? (kernel + user) * 100 : 0;
        }
        var clock = OperatingSystem.IsMacOS() ? MacThreadCpuClock : LinuxThreadCpuClock;
        return clock_gettime(clock, out var time) == 0 ? (time.Seconds * 1_000_000_000L) + time.Nanoseconds : 0;
    }

    [LibraryImport("libc", EntryPoint = "clock_gettime")]
    private static partial int clock_gettime(int clock, out TimeSpec time);

    [LibraryImport("kernel32")]
    private static partial nint GetCurrentThread();

    [LibraryImport("kernel32")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetThreadTimes(nint thread, out long creation, out long exit, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct TimeSpec
    {
        public long Seconds;
        public long Nanoseconds;
    }
}
