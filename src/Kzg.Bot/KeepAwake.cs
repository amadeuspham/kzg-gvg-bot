using System.Diagnostics;

namespace Kzg.Bot;

/// <summary>
/// Keeps a Mac from sleeping while the bot runs: sleep would drop every relay bot from voice. It runs
/// macOS's own <c>caffeinate</c>, tied to this process, so the hold ends when the bot stops, even by a
/// crash. The screen may still turn off; a laptop still sleeps when its lid is closed. Elsewhere it does nothing.
/// </summary>
public sealed class KeepAwake : IDisposable
{
    public const string Caffeinate = "/usr/bin/caffeinate";

    private Process? _process;

    private KeepAwake(Process? process) => _process = process;

    /// <summary>Whether the Mac is being kept awake.</summary>
    public bool Holding => _process is { HasExited: false };

    /// <summary>
    /// The arguments: <c>-i</c> holds off idle sleep, <c>-s</c> all system sleep while on power, and <c>-w</c>
    /// lets go when the bot's process ends.
    /// </summary>
    public static IReadOnlyList<string> Arguments(int pid) => ["-i", "-s", "-w", pid.ToString(System.Globalization.CultureInfo.InvariantCulture)];

    /// <param name="start">Starts the process; tests pass their own. Null when it couldn't start.</param>
    public static KeepAwake Start(TextWriter log, Func<ProcessStartInfo, Process?>? start = null, bool? isMac = null)
    {
        if (!(isMac ?? OperatingSystem.IsMacOS()))
        {
            return new KeepAwake(null);
        }
        var info = new ProcessStartInfo(Caffeinate) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in Arguments(Environment.ProcessId))
        {
            info.ArgumentList.Add(argument);
        }
        try
        {
            var process = (start ?? Process.Start)(info);
            log.WriteLine(process is null
                ? "the Mac couldn't be kept awake; turn sleep off in System Settings, or the bots drop out of voice when it sleeps"
                : "keeping the Mac awake while the bot runs");
            return new KeepAwake(process);
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            log.WriteLine("the Mac couldn't be kept awake ({0}); turn sleep off in System Settings, or the bots drop out of voice when it sleeps", exception.GetType().Name);
            return new KeepAwake(null);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _process, null) is not { } process)
        {
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                process.Kill();
            }
        }
        catch (InvalidOperationException)
        {
            // It ended on its own.
        }
        process.Dispose();
    }
}
