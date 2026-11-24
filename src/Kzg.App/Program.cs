using Avalonia;
using Kzg.App.Input;
using Velopack;

namespace Kzg.App;

public static class Program
{
    /// <summary>The data folder from <c>--data-dir</c>, or null for the per-user default.</summary>
    public static string? DataDirectory { get; private set; }

    /// <summary>Dev only: the loopback <c>/ws</c> address from <c>--server</c>, or null for the real <c>wss://</c> host.</summary>
    public static Uri? ServerOverride { get; private set; }

    /// <summary>Which stand-in server to use instead of the real one (<c>--demo</c>), or null.</summary>
    public static Services.DemoMode? Demo { get; private set; }

    /// <summary>Started by the login entry (<c>--minimized</c>): the window starts out of the way, in the taskbar or Dock.</summary>
    public static bool StartMinimized { get; private set; }

    /// <summary>The claim on this player's data folder, held until the app exits; null until <see cref="Main"/> has made it.</summary>
    public static Services.SingleInstance? Instance { get; private set; }

    /// <summary>The folder the settings live in: <c>--data-dir</c>, a scratch folder for <c>--demo</c>, else the per-user default.</summary>
    public static string DataFolder() => DataDirectory
        ?? (Demo is null ? DefaultDataFolder() : Path.Combine(Path.GetTempPath(), "kzg-app-demo"));

    [STAThread]
    public static int Main(string[] args)
    {
        // Before anything can start .NET's terminal handling: see TerminalInput. The pad helper's stdin is its pipe to us.
        if (!PadHelper.IsHelperInvocation(args))
        {
            Services.TerminalInput.Detach();
        }
        // Runs the installer's hooks (install, update, uninstall) and exits for them; for a normal start it returns at once.
        VelopackApp.Build().Run();
        // The controller helper is this same exe. It must never reach Avalonia or the key hook: SDL and the hook can't share a process.
        if (PadHelper.IsHelperInvocation(args))
        {
            return PadHelper.Run();
        }
        DataDirectory = ValueOf(args, "--data-dir");
        StartMinimized = args.Contains("--minimized");
        if (ValueOf(args, "--server") is { } server)
        {
            if (!Link.DevServerOverride.TryParse(server, out var address))
            {
                Console.Error.WriteLine("--server takes a loopback address like ws://127.0.0.1:7878 (dev only).");
                return 2;
            }
            ServerOverride = address;
        }
        Demo = ValueOf(args, "--demo") switch
        {
            "online" => Services.DemoMode.Online,
            "offline" => Services.DemoMode.Offline,
            "flaky" => Services.DemoMode.Flaky,
            _ => null,
        };
        // A second copy must not start a key listener or a connection of its own: it shows the first one and leaves.
        var folder = DataFolder();
        Instance = Services.SingleInstance.Claim(folder);
        if (Instance is null)
        {
            _ = Services.SingleInstance.RequestShow(folder, TimeSpan.FromSeconds(2));
            return 0;
        }
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        finally
        {
            Instance.Dispose();
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();

    private static string DefaultDataFolder()
    {
        var root = OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support")
            : Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        return Path.Combine(root, "Kzg", "App");
    }

    private static string? ValueOf(string[] args, string name)
    {
        var at = Array.IndexOf(args, name);
        return at >= 0 && at + 1 < args.Length ? args[at + 1] : null;
    }
}
