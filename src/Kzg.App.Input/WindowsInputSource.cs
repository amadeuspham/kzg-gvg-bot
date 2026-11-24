namespace Kzg.App.Input;

/// <summary>
/// Windows keyboard and mouse: Raw Input first, and the low-level hook behind it when Raw Input can't run.
/// The fallback hook is put in again from <see cref="Maintain"/>, new one before old one, because Windows
/// removes a low-level hook without a word. While the fallback runs, Raw Input is tried again each time.
/// A duplicate down or up that shows during the overlap changes nothing in the binding matcher.
/// </summary>
public sealed class WindowsInputSource : IKeyMouseSource
{
    private readonly Func<IKeyMouseSource> _rawFactory;
    private readonly Func<IKeyMouseSource> _hookFactory;
    private readonly object _gate = new();
    private IKeyMouseSource? _raw;
    private IKeyMouseSource? _hook;
    private IKeyMouseSource? _pending;
    private bool _running;

    public WindowsInputSource(Func<IKeyMouseSource> raw, Func<IKeyMouseSource> hook)
    {
        _rawFactory = raw;
        _hookFactory = hook;
    }

    public event Action<RawInput>? Input;

    public event Action<HookHealth, string?>? HealthChanged;

    /// <summary>True while the low-level hook is what delivers input, because Raw Input is not.</summary>
    public bool UsingFallback
    {
        get
        {
            lock (_gate)
            {
                return _hook is not null;
            }
        }
    }

    public HookHealth Health => Snapshot().Health;

    public string? Detail => Snapshot().Detail;

    public void Start()
    {
        lock (_gate)
        {
            if (_running)
            {
                return;
            }
            _running = true;
        }
        StartRaw();
    }

    public void Stop()
    {
        IKeyMouseSource?[] sources;
        lock (_gate)
        {
            _running = false;
            sources = [_raw, _hook, _pending];
            _raw = null;
            _hook = null;
            _pending = null;
        }
        foreach (var source in sources)
        {
            Retire(source);
        }
        HealthChanged?.Invoke(HookHealth.Stopped, null);
    }

    public void Dispose() => Stop();

    public void MarkFailed(string detail)
    {
        IKeyMouseSource? active;
        lock (_gate)
        {
            active = _raw is { Health: HookHealth.Ok } ? _raw : _hook;
        }
        active?.MarkFailed(detail);
    }

    public bool TryRecover()
    {
        IKeyMouseSource? active;
        lock (_gate)
        {
            active = _raw is { Health: HookHealth.Ok } ? _raw : _hook;
        }
        return active?.TryRecover() == true;
    }

    /// <summary>Call every few seconds, off the UI thread: tries Raw Input again if it failed, and puts the fallback hook in again.</summary>
    public void Maintain()
    {
        IKeyMouseSource? raw;
        bool hasHook;
        lock (_gate)
        {
            if (!_running)
            {
                return;
            }
            raw = _raw;
            hasHook = _hook is not null;
        }
        if (raw is null || raw.Health is HookHealth.Failed or HookHealth.Stopped)
        {
            StartRaw();
        }
        if (hasHook)
        {
            Reinstall();
        }
    }

    private void StartRaw()
    {
        var raw = _rawFactory();
        IKeyMouseSource? old;
        lock (_gate)
        {
            if (!_running)
            {
                raw.Dispose();
                return;
            }
            old = _raw;
            _raw = raw;
        }
        Retire(old);
        raw.Input += OnInput;
        raw.HealthChanged += (_, _) => OnRawHealthChanged(raw);
        raw.Start();
        // A source that failed inside Start has already raised its event, before the handler existed.
        OnRawHealthChanged(raw);
    }

    private void OnRawHealthChanged(IKeyMouseSource raw)
    {
        bool startFallback = false;
        IKeyMouseSource? retired = null;
        lock (_gate)
        {
            if (!_running || !ReferenceEquals(_raw, raw))
            {
                return;
            }
            if (raw.Health == HookHealth.Failed && _hook is null)
            {
                startFallback = true;
            }
            else if (raw.Health == HookHealth.Ok && _hook is not null)
            {
                retired = _hook;
                _hook = null;
            }
        }
        if (retired is not null)
        {
            Retire(retired);
        }
        if (startFallback)
        {
            StartHook();
        }
        HealthChanged?.Invoke(Health, Detail);
    }

    private void StartHook()
    {
        var hook = _hookFactory();
        lock (_gate)
        {
            if (!_running || _hook is not null)
            {
                hook.Dispose();
                return;
            }
            _hook = hook;
        }
        hook.Input += OnInput;
        hook.HealthChanged += (_, _) => OnHookHealthChanged(hook);
        hook.Start();
    }

    // The new hook starts beside the old one and takes over once it reports Ok, so no key falls into the gap.
    private void Reinstall()
    {
        var fresh = _hookFactory();
        IKeyMouseSource? abandoned;
        lock (_gate)
        {
            if (!_running || _hook is null)
            {
                fresh.Dispose();
                return;
            }
            abandoned = _pending;
            _pending = fresh;
        }
        abandoned?.Dispose();
        fresh.HealthChanged += (_, _) => OnHookHealthChanged(fresh);
        fresh.Start();
        OnHookHealthChanged(fresh);
    }

    private void OnHookHealthChanged(IKeyMouseSource hook)
    {
        IKeyMouseSource? replaced = null;
        IKeyMouseSource? dropped = null;
        lock (_gate)
        {
            if (ReferenceEquals(_pending, hook))
            {
                if (hook.Health == HookHealth.Ok && _hook is not null)
                {
                    replaced = _hook;
                    _hook = hook;
                    _pending = null;
                    hook.Input += OnInput;
                }
                else if (hook.Health == HookHealth.Failed)
                {
                    dropped = hook;
                    _pending = null;
                }
                else
                {
                    return;
                }
            }
            else if (!ReferenceEquals(_hook, hook))
            {
                return;
            }
        }
        Retire(replaced);
        dropped?.Dispose();
        HealthChanged?.Invoke(Health, Detail);
    }

    private void OnInput(RawInput raw) => Input?.Invoke(raw);

    private void Retire(IKeyMouseSource? source)
    {
        if (source is null)
        {
            return;
        }
        source.Input -= OnInput;
        source.Dispose();
    }

    private (HookHealth Health, string? Detail) Snapshot()
    {
        IKeyMouseSource? raw;
        IKeyMouseSource? hook;
        lock (_gate)
        {
            raw = _raw;
            hook = _hook;
        }
        if (raw is { Health: HookHealth.Ok })
        {
            return (HookHealth.Ok, null);
        }
        if (hook is not null)
        {
            return (hook.Health, hook.Detail);
        }
        return raw is null ? (HookHealth.Stopped, null) : (raw.Health, raw.Detail);
    }
}
