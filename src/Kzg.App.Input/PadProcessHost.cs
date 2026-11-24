using System.Diagnostics;
using System.Text;
using Kzg.Core.Abstractions;

namespace Kzg.App.Input;

/// <summary>A running controller helper: its stdout lines, and a way to stop it.</summary>
public interface IPadChild : IDisposable
{
    /// <summary>One line per helper event, until the process ends.</summary>
    IAsyncEnumerable<string> ReadLinesAsync(CancellationToken cancellation);

    /// <summary>Closes the helper's stdin, which tells it to exit, then waits briefly and kills it if it ignores that.</summary>
    Task StopAsync();
}

public interface IPadChildLauncher
{
    /// <summary>Starts a helper; throws when the process can't be created.</summary>
    IPadChild Start();
}

/// <summary>Starts a copy of the running exe with <see cref="PadHelper.Argument"/>.</summary>
public sealed class SelfPadChildLauncher : IPadChildLauncher
{
    public IPadChild Start()
    {
        var exe = Environment.ProcessPath ?? throw new InvalidOperationException("The app can't find its own path.");
        var info = new ProcessStartInfo(exe)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
        };
        // Started through "dotnet app.dll" the child needs the dll again; otherwise it is the app host itself.
        if (Path.GetFileNameWithoutExtension(exe) == "dotnet")
        {
            info.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location ?? throw new InvalidOperationException("The app can't find its own path."));
        }
        // A framework-dependent app host must find the same runtime the parent runs on.
        var framework = Path.GetDirectoryName(typeof(object).Assembly.Location)?.Replace('\\', '/');
        if (framework is not null && framework.Contains("/shared/Microsoft.NETCore.App/", StringComparison.Ordinal))
        {
            info.Environment["DOTNET_ROOT"] = Path.GetFullPath(Path.Combine(framework, "..", "..", ".."));
        }
        info.ArgumentList.Add(PadHelper.Argument);
        var process = Process.Start(info) ?? throw new InvalidOperationException("The controller helper did not start.");
        return new ProcessChild(process);
    }

    private sealed class ProcessChild(Process process) : IPadChild
    {
        public async IAsyncEnumerable<string> ReadLinesAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellation)
        {
            while (await process.StandardOutput.ReadLineAsync(cancellation) is { } line)
            {
                yield return line;
            }
        }

        public async Task StopAsync()
        {
            try
            {
                process.StandardInput.Close();
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await process.WaitForExitAsync(patience.Token);
            }
            catch (Exception exception) when (exception is OperationCanceledException or IOException or InvalidOperationException)
            {
                try
                {
                    process.Kill();
                }
                catch (Exception killed) when (killed is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Already gone.
                }
            }
        }

        public void Dispose() => process.Dispose();
    }
}

/// <summary>
/// Keeps the controller helper running while a controller binding exists, turns its lines into
/// <see cref="RawInput"/>, and restarts it when it dies unexpectedly. Every button down it reported is
/// matched by an up, even when the helper dies mid-press.
/// </summary>
public sealed class PadProcessHost(IPadChildLauncher launcher, ITimeSource time, Func<TimeSpan, CancellationToken, Task>? delay = null) : IDisposable
{
    private readonly PadSupervisor _supervisor = new(time);
    private readonly Func<TimeSpan, CancellationToken, Task> _delay = delay ?? Task.Delay;
    private readonly object _gate = new();
    private CancellationTokenSource? _cancel;
    private Task _loop = Task.CompletedTask;

    public event Action<RawInput>? Input;

    /// <summary>Raised on a background thread.</summary>
    public event Action? Changed;

    public PadHealth Health { get; private set; } = PadHealth.NotNeeded;

    public IReadOnlyList<string> Controllers { get; private set; } = [];

    /// <summary>Starts the helper, or, after a give-up, tries again. A running helper is left alone.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_cancel is not null && Health != PadHealth.Failed)
            {
                return;
            }
            _cancel?.Cancel();
            _supervisor.Reset();
            _cancel = new CancellationTokenSource();
            var token = _cancel.Token;
            Set(PadHealth.Starting);
            _loop = Task.Run(() => RunAsync(token));
        }
    }

    public void Stop()
    {
        Task loop;
        lock (_gate)
        {
            if (_cancel is null)
            {
                return;
            }
            _cancel.Cancel();
            _cancel = null;
            loop = _loop;
        }
        // Waits for the helper to be told to exit; bounded by StopAsync's own patience.
        loop.ContinueWith(_ => { }, TaskScheduler.Default).Wait(TimeSpan.FromSeconds(4));
        Controllers = [];
        Set(PadHealth.NotNeeded);
    }

    public void Dispose() => Stop();

    private async Task RunAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            var held = new HashSet<string>();
            IPadChild? child = null;
            try
            {
                // Before the launch, so a helper that can't even start counts as a quick failure.
                _supervisor.Started();
                child = launcher.Start();
                await foreach (var text in child.ReadLinesAsync(stop))
                {
                    if (PadLine.TryParse(text, out var line))
                    {
                        Handle(line, held);
                    }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                // Stopped on purpose.
            }
            catch (Exception exception) when (exception is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // The helper could not start or its pipe broke: handled like an exit.
            }
            finally
            {
                foreach (var name in held)
                {
                    Input?.Invoke(new RawInput(InputBinding.Pad(name), false));
                }
                if (child is not null)
                {
                    await child.StopAsync();
                    child.Dispose();
                }
            }
            if (stop.IsCancellationRequested)
            {
                return;
            }
            Controllers = [];
            switch (_supervisor.Exited())
            {
                case PadDecision.Restart restart:
                    Set(PadHealth.Restarting);
                    try
                    {
                        await _delay(restart.After, stop);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    break;
                default:
                    Set(PadHealth.Failed);
                    return;
            }
        }
    }

    private void Handle(PadLine line, HashSet<string> held)
    {
        switch (line)
        {
            case PadLine.Ready:
                Set(PadHealth.Running);
                break;
            case PadLine.ControllerAdded added:
                Controllers = [.. Controllers, added.Name];
                Set(Health);
                break;
            case PadLine.ControllerRemoved removed:
                Controllers = [.. Controllers.Where(n => n != removed.Name)];
                Set(Health);
                break;
            case PadLine.Button button:
                // A repeated down or a stray up carries no news.
                if (button.Down ? held.Add(button.Name) : held.Remove(button.Name))
                {
                    Input?.Invoke(new RawInput(InputBinding.Pad(button.Name), button.Down));
                }
                break;
        }
    }

    private void Set(PadHealth health)
    {
        Health = health;
        Changed?.Invoke();
    }
}
