namespace Kzg.App.Services;

/// <summary>Moves work onto the UI thread, which is where the model lives.</summary>
public interface IUiThread
{
    void Post(Action action);
}

/// <summary>What the app asks of the operating system, apart from input: all of it small and replaceable in tests.</summary>
public interface IPlatformServices
{
    bool IsWindows { get; }

    bool IsMac { get; }

    /// <summary>Windows only: this process runs as administrator.</summary>
    bool IsElevated { get; }

    /// <summary>Opens a web page in the default browser.</summary>
    void OpenUrl(string url);

    /// <summary>Windows only: starts the app again with the administrator prompt. True when it started, and this copy should quit.</summary>
    bool TryRestartElevated();

    /// <summary>
    /// macOS: starts the installed app again once this copy has exited (macOS only applies a new Input Monitoring grant to a
    /// new process). True when the restart is arranged, and this copy should quit; false for a dev build run from a build folder.
    /// </summary>
    bool TryRestartApp();

    /// <summary>A sound and a desktop notification, where the system offers them.</summary>
    void Notify(string title, string text);

    void Quit();
}
