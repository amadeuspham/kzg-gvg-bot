using System.Threading.Channels;
using SharpHook;
using SharpHook.Data;

namespace Kzg.App.Input;

/// <summary>
/// Keyboard and mouse through one SharpHook global hook in the app's process. The hook only listens: it
/// never sets <c>SuppressEvent</c> and never posts input. Hook callbacks only enqueue; a consumer
/// thread raises <see cref="Input"/>, because Windows silently removes a low-level hook whose callback
/// is slow, which looks just like a permission failure.
/// </summary>
public sealed class HookInputSource : IKeyMouseSource
{
    private readonly object _gate = new();
    private Session? _session;

    public event Action<RawInput>? Input;

    /// <summary>Raised on whichever thread changed it.</summary>
    public event Action<HookHealth, string?>? HealthChanged;

    public HookHealth Health { get; private set; } = HookHealth.Stopped;

    /// <summary>A short, plain reason for <see cref="HookHealth.Failed"/>; never user input.</summary>
    public string? Detail { get; private set; }

    /// <summary>Starts the hook. A started hook is left alone; call <see cref="Stop"/> first to restart.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_session is not null)
            {
                return;
            }
            _session = new Session(this);
        }
        SetHealth(HookHealth.Starting, null);
        _session.Run();
    }

    public void Stop()
    {
        Session? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }
        if (session is null)
        {
            return;
        }
        session.Dispose();
        SetHealth(HookHealth.Stopped, null);
    }

    public void Dispose() => Stop();

    /// <summary>A hook the system removed can't be put back from outside; it needs a restart.</summary>
    public bool TryRecover() => false;

    public void Maintain()
    {
    }

    /// <summary>Marks a running hook as failed from outside, e.g. the system disabled its tap. A restart is <see cref="Stop"/> then <see cref="Start"/>.</summary>
    public void MarkFailed(string detail)
    {
        if (Health == HookHealth.Ok)
        {
            SetHealth(HookHealth.Failed, detail);
        }
    }

    private void SetHealth(HookHealth health, string? detail)
    {
        Health = health;
        Detail = detail;
        HealthChanged?.Invoke(health, detail);
    }

    private bool IsCurrent(Session session)
    {
        lock (_gate)
        {
            return ReferenceEquals(_session, session);
        }
    }

    private sealed class Session : IDisposable
    {
        private readonly HookInputSource _owner;
        private readonly SimpleGlobalHook _hook = new();
        private readonly Channel<RawInput> _queue = Channel.CreateUnbounded<RawInput>(
            new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });

        public Session(HookInputSource owner)
        {
            _owner = owner;
            _hook.KeyPressed += (_, e) => Enqueue(KeyOf(e.Data.KeyCode), true);
            _hook.KeyReleased += (_, e) => Enqueue(KeyOf(e.Data.KeyCode), false);
            _hook.MousePressed += (_, e) => Enqueue(MouseOf(e.Data.Button), true);
            _hook.MouseReleased += (_, e) => Enqueue(MouseOf(e.Data.Button), false);
            _hook.HookEnabled += (_, _) =>
            {
                if (_owner.IsCurrent(this))
                {
                    _owner.SetHealth(HookHealth.Ok, null);
                }
            };
        }

        public void Run()
        {
            _ = Task.Run(async () =>
            {
                await foreach (var raw in _queue.Reader.ReadAllAsync())
                {
                    _owner.Input?.Invoke(raw);
                }
            });
            var run = _hook.RunAsync();
            _ = run.ContinueWith(OnHookEnded, TaskScheduler.Default);
        }

        public void Dispose()
        {
            _queue.Writer.TryComplete();
            _hook.Dispose();
        }

        // Runs on the hook thread: one enqueue and nothing else.
        private void Enqueue(InputBinding? input, bool down)
        {
            if (input is { } binding)
            {
                _queue.Writer.TryWrite(new RawInput(binding, down));
            }
        }

        private static InputBinding? KeyOf(KeyCode code) => code == KeyCode.VcUndefined ? null : InputBinding.Key(code);

        private static InputBinding? MouseOf(MouseButton button) => button == MouseButton.NoButton ? null : InputBinding.Mouse(button);

        // A hook that ends without being stopped has failed. The task is the only place the failure shows.
        private void OnHookEnded(Task run)
        {
            if (!_owner.IsCurrent(this))
            {
                return;
            }
            if (run.IsFaulted && run.Exception.GetBaseException() is HookException { Result: UioHookResult.ErrorAxApiDisabled or UioHookResult.ErrorAxApiRevoked })
            {
                _owner.SetHealth(HookHealth.NeedsAccessibility, null);
            }
            else if (run.IsFaulted && run.Exception.GetBaseException() is HookException hook)
            {
                _owner.SetHealth(HookHealth.Failed, $"The input hook failed to start ({hook.Result}).");
            }
            else
            {
                _owner.SetHealth(HookHealth.Failed, "The input hook stopped.");
            }
        }
    }
}
