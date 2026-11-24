namespace Kzg.App.Input;

/// <summary>
/// Where keyboard and mouse input comes from: a listen-only event tap on macOS, Raw Input on Windows, a global
/// hook elsewhere. The operating system must never wait for the app, so a source only enqueues what it hears.
/// </summary>
public interface IKeyMouseSource : IDisposable
{
    /// <summary>Raised on a background thread, never on the thread the system calls.</summary>
    event Action<RawInput>? Input;

    /// <summary>Raised on whichever thread changed it.</summary>
    event Action<HookHealth, string?>? HealthChanged;

    HookHealth Health { get; }

    /// <summary>A short, plain reason for <see cref="HookHealth.Failed"/>; never user input.</summary>
    string? Detail { get; }

    /// <summary>Starts listening. A started source is left alone; <see cref="Stop"/> first to restart.</summary>
    void Start();

    void Stop();

    /// <summary>Marks a running source as failed from outside, e.g. the system disabled its tap.</summary>
    void MarkFailed(string detail);

    /// <summary>Tries to bring a source the system switched off back without a restart. True when it is delivering again.</summary>
    bool TryRecover();

    /// <summary>
    /// Housekeeping every few seconds, off the UI thread: a source that has a fallback it must keep fresh does that
    /// here. Most sources have nothing to do.
    /// </summary>
    void Maintain();
}
