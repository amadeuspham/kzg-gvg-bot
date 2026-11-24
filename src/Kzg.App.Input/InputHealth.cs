namespace Kzg.App.Input;

/// <summary>Whether the keyboard and mouse hook is delivering.</summary>
public enum HookHealth
{
    Stopped,
    Starting,
    Ok,

    /// <summary>macOS refused the listener until Input Monitoring is granted. The listener is not started in this state.</summary>
    NeedsAccessibility,
    Failed,
}

/// <summary>Whether the controller helper process is delivering. It runs only while a controller binding exists.</summary>
public enum PadHealth
{
    NotNeeded,
    Starting,
    Running,
    Restarting,

    /// <summary>The helper kept dying; the app shows "controller input stopped" until the user retries.</summary>
    Failed,
}

/// <summary>A macOS privacy permission, or <see cref="NotApplicable"/> on other systems.</summary>
public enum PermissionState
{
    NotApplicable,
    Granted,
    Denied,
    NotAsked,
}
