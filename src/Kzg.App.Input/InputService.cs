using System.Runtime.Versioning;
using Kzg.Core.Abstractions;

namespace Kzg.App.Input;

/// <summary>What the app sees of the capture layer: raw inputs, and whether each half is delivering.</summary>
public interface IInputService : IDisposable
{
    /// <summary>Raised on a background thread; the receiver hops to its own.</summary>
    event Action<RawInput>? Input;

    /// <summary>Raised on a background thread when either health changes.</summary>
    event Action? Changed;

    HookHealth Hook { get; }

    string? HookDetail { get; }

    PadHealth Pad { get; }

    IReadOnlyList<string> Controllers { get; }

    /// <summary>
    /// Starts the keyboard and mouse listener, or restarts it. On macOS without Input Monitoring it does not
    /// start it (that would raise a system prompt unexplained) and reports
    /// <see cref="HookHealth.NeedsAccessibility"/>, which here means "waiting for permission".
    /// </summary>
    void StartHook();

    /// <summary>Starts the controller helper process, or retries after it gave up.</summary>
    void StartPad();

    /// <summary>Stops the controller helper; keyboard-only use never starts it.</summary>
    void StopPad();
}

/// <inheritdoc />
public sealed class InputService : IInputService
{
    private readonly IKeyMouseSource _source;
    private readonly PadProcessHost _pad;
    private readonly IPermissionProbe _permissions;
    private readonly HookWatchdog _watchdog;
    private readonly Timer _watchdogTimer;
    private HookHealth _blocked = HookHealth.Stopped;
    private int _maintaining;

    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(3);

    /// <param name="source">The keyboard and mouse source; by default the one for this operating system.</param>
    public InputService(ITimeSource time, IPermissionProbe permissions, IEventTapProbe? taps = null, IKeyMouseSource? source = null)
    {
        _permissions = permissions;
        _source = source ?? DefaultSource();
        _watchdog = new HookWatchdog(taps ?? MacEventTaps.Probe);
        // A thread-pool timer: the check must never run on, or wait for, the UI thread.
        _watchdogTimer = new Timer(_ => Maintain(), null, WatchdogInterval, WatchdogInterval);
        _pad = new PadProcessHost(new SelfPadChildLauncher(), time);
        _source.Input += raw => Input?.Invoke(raw);
        _pad.Input += raw => Input?.Invoke(raw);
        _source.HealthChanged += (_, _) => Changed?.Invoke();
        _pad.Changed += () => Changed?.Invoke();
    }

    public event Action<RawInput>? Input;

    public event Action? Changed;

    public HookHealth Hook => _blocked != HookHealth.Stopped ? _blocked : _source.Health;

    public string? HookDetail => _blocked != HookHealth.Stopped ? null : _source.Detail;

    public PadHealth Pad => _pad.Health;

    public IReadOnlyList<string> Controllers => _pad.Controllers;

    public void StartHook() => _ = StartHookAsync();

    /// <summary>Does what <see cref="StartHook"/> does, and completes when the source has been asked to start.</summary>
    public Task StartHookAsync() =>
        // Off the caller's thread: stopping a running source waits for its thread, and the UI thread must never wait.
        Task.Run(() =>
        {
            _source.Stop();
            if (_permissions.InputMonitoring == PermissionState.Denied)
            {
                _blocked = HookHealth.NeedsAccessibility;
                Changed?.Invoke();
                return;
            }
            _blocked = HookHealth.Stopped;
            _source.Start();
        });

    public void StartPad() => _ = Task.Run(_pad.Start);

    public void StopPad() => _ = Task.Run(_pad.Stop);

    public void Dispose()
    {
        _watchdogTimer.Dispose();
        // Not waited for: stopping the source and the helper can take a moment, and the caller may be the UI thread.
        // The helper exits by itself when the app's end of its stdin closes.
        _ = Task.Run(() =>
        {
            _source.Dispose();
            _pad.Dispose();
        });
    }

    /// <summary>
    /// One round of the watchdog, every few seconds off the UI thread: switches a tap macOS disabled back on, and
    /// calls it failed when that doesn't work; then lets the source do its own housekeeping.
    /// </summary>
    public void Maintain()
    {
        if (Interlocked.Exchange(ref _maintaining, 1) == 1)
        {
            return;
        }
        try
        {
            if (_watchdog.Check(_source.Health) && !_source.TryRecover())
            {
                _source.MarkFailed(HookWatchdog.FailureText);
            }
            _source.Maintain();
        }
        finally
        {
            Volatile.Write(ref _maintaining, 0);
        }
    }

    private static IKeyMouseSource DefaultSource()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacEventTapSource();
        }
        if (OperatingSystem.IsWindows())
        {
            return WindowsSource();
        }
        return new HookInputSource();
    }

    [SupportedOSPlatform("windows")]
    private static WindowsInputSource WindowsSource() =>
        new(() => new WindowsRawInputSource(), () => new HookInputSource());
}
