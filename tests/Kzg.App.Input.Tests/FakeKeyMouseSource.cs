namespace Kzg.App.Input.Tests;

/// <summary>A source a test drives by hand: it starts as asked, says what it was told, and delivers what the test raises.</summary>
internal sealed class FakeKeyMouseSource : IKeyMouseSource
{
    public event Action<RawInput>? Input;

    public event Action<HookHealth, string?>? HealthChanged;

    /// <summary>What <see cref="Start"/> turns the health into.</summary>
    public HookHealth StartsAs { get; set; } = HookHealth.Ok;

    public bool Recovers { get; set; }

    public HookHealth Health { get; private set; } = HookHealth.Stopped;

    public string? Detail { get; private set; }

    public int Starts { get; private set; }

    public int Maintains { get; private set; }

    public int Recoveries { get; private set; }

    public bool Disposed { get; private set; }

    public string? MarkedFailed { get; private set; }

    public void Start()
    {
        Starts++;
        Set(StartsAs, StartsAs == HookHealth.Failed ? "It would not start." : null);
    }

    public void Stop() => Set(HookHealth.Stopped, null);

    public void Dispose()
    {
        Disposed = true;
        Stop();
    }

    public void MarkFailed(string detail)
    {
        MarkedFailed = detail;
        Set(HookHealth.Failed, detail);
    }

    public bool TryRecover()
    {
        Recoveries++;
        return Recovers;
    }

    public void Maintain() => Maintains++;

    public void Set(HookHealth health, string? detail = null)
    {
        Health = health;
        Detail = detail;
        HealthChanged?.Invoke(health, detail);
    }

    public void Raise(RawInput raw) => Input?.Invoke(raw);

    public bool HasListeners => Input is not null;
}
