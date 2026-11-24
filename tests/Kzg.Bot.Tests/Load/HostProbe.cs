using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Kzg.Bot.Tests.Load;

/// <summary>How busy the whole machine is, so a long run's memory curve can be read against what else was going on.</summary>
internal static partial class HostProbe
{
    /// <summary>The one-minute load average, or NaN where the platform has none.</summary>
    public static double LoadAverage()
    {
        if (OperatingSystem.IsWindows())
        {
            return double.NaN;
        }
        Span<double> loads = stackalloc double[3];
        return getloadavg(loads, 3) >= 1 ? loads[0] : double.NaN;
    }

    /// <summary>
    /// macOS memory pressure: 1 normal, 2 warning, 4 critical, and the percentage of memory the system
    /// calls free. Both are 0 where the platform doesn't say.
    /// </summary>
    public static (int Level, int FreePercent) MemoryPressure()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return (0, 0);
        }
        var level = int.TryParse(Run("/usr/sbin/sysctl", "-n", "kern.memorystatus_vm_pressure_level").Trim(), CultureInfo.InvariantCulture, out var l) ? l : 0;
        var free = FreePattern().Match(Run("/usr/bin/memory_pressure", "-Q"));
        return (level, free.Success ? int.Parse(free.Groups[1].Value, CultureInfo.InvariantCulture) : 0);
    }

    private static string Run(string exe, params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo(exe) { RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            using var process = Process.Start(info)!;
            var text = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(5000))
            {
                process.Kill();
                return "";
            }
            return text.Result;
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "";
        }
    }

    [LibraryImport("libc", EntryPoint = "getloadavg")]
    private static partial int getloadavg(Span<double> loads, int count);

    [GeneratedRegex(@"free percentage:\s*(\d+)%")]
    private static partial Regex FreePattern();
}
