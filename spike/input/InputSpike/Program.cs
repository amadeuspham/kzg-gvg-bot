using System.Runtime.InteropServices;
using SharpHook;
using SharpHook.Data;
using InputSpike;

// Listen-only input spike. Never injects, never suppresses (we never touch e.SuppressEvent).
// Usage: InputSpike [--keys-only | --pad-only] [--perms] [--request-perms] [--poll] [--log <file>]
// Default: hook (keys, mouse) in this process, SDL (controller) in a child process. See README.

System.Globalization.CultureInfo.DefaultThreadCurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.InvariantCulture;
var a = args.ToHashSet();
var isWin = OperatingSystem.IsWindows();
var isMac = OperatingSystem.IsMacOS();
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// Child: the controller half only, relaying to the parent's log. Never loads libuiohook.
if (a.Contains("--child"))
{
    Log.OpenRelay();
    PadProcess.WatchParent(cts);
    Gamepad.Run(cts.Token);
    Log.Flush();
    return 0;
}

var padOnly = a.Contains("--pad-only");
var keysOnly = a.Contains("--keys-only");
if (padOnly && keysOnly) { Console.Error.WriteLine("--pad-only and --keys-only exclude each other"); return 2; }
var logPath = args.SkipWhile(x => x != "--log").Skip(1).FirstOrDefault()
    ?? Path.Combine(Environment.CurrentDirectory, $"input-spike-{DateTime.Now:yyyyMMdd-HHmmss}.log");
Log.Open(logPath);

Log.Write("info", $"{RuntimeInformation.OSDescription} {RuntimeInformation.ProcessArchitecture}, .NET {Environment.Version}, log: {logPath}");
Log.Write("info", "mode: " + (padOnly ? "controller only (SDL, no hook)" : keysOnly ? "keyboard and mouse only (hook, no SDL)"
    : "both (hook here, SDL in a child process)"));
if (isWin) Log.Write("info", $"This process is {(WinProbe.IsSelfElevated() ? "ELEVATED (administrator)" : "NOT elevated")}");
if (isMac)
{
    MacPermissions.Report();
    if (a.Contains("--request-perms")) { MacPermissions.Request(); MacPermissions.Report(); }
    if (a.Contains("--perms")) { Log.Flush(); return 0; }
}

long events = 0;
var held = new HashSet<string>();
var repeats = new Dictionary<string, int>();

// Runs on the hook thread: only take the window handle here. A slow callback makes Windows drop the LL hook.
IntPtr FgHandle() => isWin ? WinProbe.ForegroundHandle() : IntPtr.Zero;

// Auto-repeat floods KeyPressed while a key is held; log only the first press and count the rest.
void Down(string kind, string name)
{
    Interlocked.Increment(ref events);
    bool first;
    lock (held)
    {
        first = held.Add(name);
        if (!first) repeats[name] = repeats.GetValueOrDefault(name) + 1;
    }
    if (first) Log.Write(kind, $"{name} DOWN", FgHandle());
}
void Up(string kind, string name)
{
    Interlocked.Increment(ref events);
    int r;
    lock (held) { held.Remove(name); repeats.Remove(name, out r); }
    Log.Write(kind, $"{name} UP" + (r > 0 ? $"  (+{r} auto-repeat)" : ""), FgHandle());
}

SimpleGlobalHook? hook = null;
Task? hookTask = null;
if (!padOnly)
{
    hook = new SimpleGlobalHook();
    hook.KeyPressed += (_, e) => Down("key", e.Data.KeyCode.ToString());
    hook.KeyReleased += (_, e) => Up("key", e.Data.KeyCode.ToString());
    hook.MousePressed += (_, e) => Down("mouse", e.Data.Button.ToString());
    hook.MouseReleased += (_, e) => Up("mouse", e.Data.Button.ToString());
    hook.HookEnabled += (_, _) => Log.Write("hook", "hook ENABLED (libuiohook started)");
    hook.HookDisabled += (_, _) => Log.Write("hook", "hook DISABLED");
    hookTask = hook.RunAsync();
    _ = hookTask.ContinueWith(t => Log.Write("hook", $"HOOK FAILED: {t.Exception?.GetBaseException().GetType().Name}: {t.Exception?.GetBaseException().Message}"
        + (isMac ? " -> grant Accessibility (and Input Monitoring) to the host app in System Settings > Privacy & Security, then restart" : "")),
        TaskContinuationOptions.OnlyOnFaulted);
}

Thread? poll = null;
if (isWin && a.Contains("--poll"))
{
    poll = new Thread(() => WinProbe.PollLoop(cts.Token)) { IsBackground = true, Name = "poll" };
    poll.Start();
}

Log.Write("info", "Ready. Alt-tab to the game, hold a key / mouse side button / pad button. Ctrl+C to quit.");
if (hook != null)
{
    var beat = new Thread(() =>
    {
        var lastEvents = -1L;
        while (!cts.Token.WaitHandle.WaitOne(5000))
        {
            var n = Interlocked.Read(ref events);
            // A heartbeat that does not move while you press keys in the game is the failure signature.
            Log.Write("alive", $"hook events so far: {n}{(n == lastEvents ? " (no change in 5 s)" : "")}", FgHandle());
            lastEvents = n;
        }
    }) { IsBackground = true, Name = "heartbeat" };
    beat.Start();
}

if (padOnly) Gamepad.Run(cts.Token);   // SDL on the main thread, blocks until Ctrl+C
else
{
    // Never SDL_Init in the hook's process: on macOS the hook then delivers nothing (README).
    using var pad = keysOnly ? null : PadProcess.Start();
    // macOS: the hook needs the main thread to drain the main dispatch queue, or it deadlocks (MainLoop.cs).
    MainLoop.Run(cts.Token);
}

hook?.Dispose();
Log.Write("info", "bye");
Log.Flush();
return 0;
