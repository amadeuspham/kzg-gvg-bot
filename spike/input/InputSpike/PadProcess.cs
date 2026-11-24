using System.Diagnostics;

namespace InputSpike;

/// <summary>
/// Runs the SDL3 controller listener in a child copy of this exe, so SDL and libuiohook never share a process.
/// On macOS they do not coexist in one process (README, "Hook plus SDL in one process"). The child relays
/// "source\tmessage" lines on stdout; it exits when its stdin closes, so it cannot outlive the parent.
/// </summary>
sealed class PadProcess : IDisposable
{
    readonly Process _p;

    PadProcess(Process p) => _p = p;

    public static PadProcess? Start()
    {
        var exe = Environment.ProcessPath;
        if (exe == null) { Log.Write("pad", "cannot find own exe path; controller disabled"); return null; }
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        // Started as "dotnet InputSpike.dll": the muxer needs the dll again.
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet") psi.ArgumentList.Add(typeof(PadProcess).Assembly.Location);
        // Framework-dependent apphost: point the child at the runtime we run on, not whatever dotnet is on PATH.
        var fx = Path.GetDirectoryName(typeof(object).Assembly.Location);
        if (fx != null && fx.Replace('\\', '/').Contains("/shared/Microsoft.NETCore.App/"))
            psi.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(fx, "..", "..", ".."));
        psi.ArgumentList.Add("--pad-only");
        psi.ArgumentList.Add("--child");
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) =>
        {
            if (e.Data == null) return;
            var tab = e.Data.IndexOf('\t');
            if (tab < 0) Log.Write("pad", e.Data);
            else Log.Write(e.Data[..tab], e.Data[(tab + 1)..]);
        };
        p.Exited += (_, _) =>
        {
            try { Log.Write("pad", $"controller process exited (code {p.ExitCode})"); } catch (InvalidOperationException) { /* disposed */ }
        };
        try { p.Start(); }
        catch (Exception ex) { Log.Write("pad", $"controller process failed to start: {ex.Message}"); return null; }
        p.BeginOutputReadLine();
        Log.Write("pad", $"controller process started (pid {p.Id})");
        return new PadProcess(p);
    }

    /// <summary>Child side: cancel when the parent closes our stdin (normal stop, crash or kill).</summary>
    public static void WatchParent(CancellationTokenSource cts) =>
        new Thread(() =>
        {
            try { while (Console.In.Read() >= 0) { } } catch { /* stdin gone */ }
            cts.Cancel();
        }) { IsBackground = true, Name = "parent-watch" }.Start();

    public void Dispose()
    {
        try { _p.StandardInput.Close(); } catch { /* already gone */ }
        if (_p.WaitForExit(2000)) _p.WaitForExit();   // the untimed overload also drains the redirected stdout
        else
        {
            Log.Write("pad", "controller process did not stop in 2 s; killing it");
            try { _p.Kill(); } catch { /* raced with exit */ }
        }
        _p.Dispose();
    }
}
