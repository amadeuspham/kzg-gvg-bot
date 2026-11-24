using System.Runtime.CompilerServices;

namespace Kzg.App.Input.Tests;

public class PadLineTests
{
    [Theory]
    [InlineData("ready\t3.5.0")]
    [InlineData("added\tDualSense Wireless Controller")]
    [InlineData("removed\tXbox Controller")]
    [InlineData("down\ta")]
    [InlineData("up\tleftshoulder")]
    [InlineData("error\tSDL could not start")]
    public void A_line_the_helper_writes_is_read_back(string text)
    {
        Assert.True(PadLine.TryParse(text, out var line));

        Assert.Equal(text, PadLine.Format(line));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("down")]
    [InlineData("down\t")]
    [InlineData("\ta")]
    [InlineData("down\tNot A Button")]
    [InlineData("down\ta\tb")]
    [InlineData("sing\tla")]
    [InlineData("Unhandled exception. System.Exception")]
    public void Stray_output_is_ignored(string? text)
    {
        Assert.False(PadLine.TryParse(text, out _));
    }

    [Fact]
    public void A_device_name_is_kept_to_one_short_printable_line()
    {
        var text = PadLine.Format(new PadLine.ControllerAdded("Pad\nwith\ttabs" + new string('x', 200)));

        Assert.DoesNotContain('\n', text);
        Assert.Equal(1, text.Count(c => c == '\t'));
        Assert.True(PadLine.TryParse(text, out _));
    }
}

public class PadSupervisorTests
{
    private readonly FakeTimeSource _time = new();

    [Fact]
    public void A_helper_that_keeps_dying_is_restarted_with_longer_pauses_then_given_up_on()
    {
        var supervisor = new PadSupervisor(_time);
        var delays = new List<TimeSpan>();

        for (var i = 0; i < PadSupervisor.MaxQuickFailures - 1; i++)
        {
            supervisor.Started();
            _time.Advance(TimeSpan.FromMilliseconds(200));
            delays.Add(Assert.IsType<PadDecision.Restart>(supervisor.Exited()).After);
        }
        supervisor.Started();
        _time.Advance(TimeSpan.FromMilliseconds(200));

        Assert.Equal(
            [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(8)],
            delays);
        Assert.IsType<PadDecision.GiveUp>(supervisor.Exited());
    }

    [Fact]
    public void A_helper_that_ran_a_while_counts_as_healthy()
    {
        var supervisor = new PadSupervisor(_time);
        for (var i = 0; i < PadSupervisor.MaxQuickFailures - 1; i++)
        {
            supervisor.Started();
            supervisor.Exited();
        }

        supervisor.Started();
        _time.Advance(PadSupervisor.StableAfter);
        var decision = supervisor.Exited();

        Assert.Equal(TimeSpan.FromSeconds(1), Assert.IsType<PadDecision.Restart>(decision).After);
    }

    [Fact]
    public void Retrying_after_a_give_up_starts_the_count_again()
    {
        var supervisor = new PadSupervisor(_time);
        PadDecision decision;
        do
        {
            supervisor.Started();
            decision = supervisor.Exited();
        }
        while (decision is PadDecision.Restart);

        supervisor.Reset();
        supervisor.Started();

        Assert.IsType<PadDecision.Restart>(supervisor.Exited());
    }
}

public class PadProcessHostTests
{
    private readonly FakeTimeSource _time = new();

    [Fact]
    public async Task Helper_lines_become_inputs_and_health()
    {
        var launcher = new ScriptedLauncher(["ready\t3.5", "added\tPad One", "down\ta", "down\ta", "up\ta", "up\ta"]);
        var inputs = new List<RawInput>();
        using var host = new PadProcessHost(launcher, _time, (_, stop) => Task.Delay(Timeout.Infinite, stop));
        host.Input += inputs.Add;

        host.Start();
        await launcher.WaitForExitsAsync(1);

        Assert.Equal(
            [new RawInput(InputBinding.Pad("a"), true), new RawInput(InputBinding.Pad("a"), false)],
            inputs);
        Assert.Equal(PadHealth.Restarting, host.Health);
    }

    [Fact]
    public async Task A_button_still_down_when_the_helper_dies_is_released()
    {
        var launcher = new ScriptedLauncher(["ready\t3.5", "down\tb"]);
        var inputs = new List<RawInput>();
        using var host = new PadProcessHost(launcher, _time, (_, stop) => Task.Delay(Timeout.Infinite, stop));
        host.Input += inputs.Add;

        host.Start();
        await launcher.WaitForExitsAsync(1);

        Assert.Equal(
            [new RawInput(InputBinding.Pad("b"), true), new RawInput(InputBinding.Pad("b"), false)],
            inputs);
    }

    [Fact]
    public async Task A_helper_that_keeps_dying_ends_in_failed_after_restarts()
    {
        var launcher = new ScriptedLauncher([]);
        using var host = new PadProcessHost(launcher, _time, (_, _) => Task.CompletedTask);

        host.Start();
        await launcher.WaitForExitsAsync(PadSupervisor.MaxQuickFailures);
        await WaitUntilAsync(() => host.Health == PadHealth.Failed);

        Assert.Equal(PadSupervisor.MaxQuickFailures, launcher.Started);
        Assert.Equal(PadHealth.Failed, host.Health);
    }

    [Fact]
    public async Task A_helper_that_cannot_start_counts_as_a_failure()
    {
        var launcher = new ScriptedLauncher([]) { FailToStart = true };
        using var host = new PadProcessHost(launcher, _time, (_, _) => Task.CompletedTask);

        host.Start();
        await WaitUntilAsync(() => host.Health == PadHealth.Failed);

        Assert.Equal(PadHealth.Failed, host.Health);
    }

    [Fact]
    public async Task Starting_again_after_a_failure_tries_again()
    {
        var launcher = new ScriptedLauncher([]) { FailToStart = true };
        using var host = new PadProcessHost(launcher, _time, (_, _) => Task.CompletedTask);
        host.Start();
        await WaitUntilAsync(() => host.Health == PadHealth.Failed);
        var before = launcher.Attempts;

        launcher.FailToStart = false;
        host.Start();
        await WaitUntilAsync(() => launcher.Attempts > before);

        Assert.True(launcher.Attempts > before);
    }

    [Fact]
    public async Task Stopping_tells_the_helper_to_go_and_clears_the_controllers()
    {
        var launcher = new ScriptedLauncher(["ready\t3.5", "added\tPad One"], stayOpen: true);
        using var host = new PadProcessHost(launcher, _time, (_, _) => Task.CompletedTask);
        host.Start();
        await WaitUntilAsync(() => host.Controllers.Count == 1);

        host.Stop();

        Assert.Equal(PadHealth.NotNeeded, host.Health);
        Assert.Empty(host.Controllers);
        Assert.True(launcher.AllStopped);
    }

    [Fact]
    public async Task A_running_helper_is_left_alone_by_a_second_start()
    {
        var launcher = new ScriptedLauncher(["ready\t3.5"], stayOpen: true);
        using var host = new PadProcessHost(launcher, _time, (_, _) => Task.CompletedTask);
        host.Start();
        await WaitUntilAsync(() => host.Health == PadHealth.Running);

        host.Start();

        Assert.Equal(1, launcher.Attempts);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            await Task.Delay(5, limit.Token);
        }
    }

    private sealed class ScriptedLauncher(IReadOnlyList<string> lines, bool stayOpen = false) : IPadChildLauncher
    {
        private readonly SemaphoreSlim _exits = new(0);
        private readonly List<Child> _children = [];
        private int _attempts;
        private int _started;

        public bool FailToStart { get; set; }

        public int Attempts => Volatile.Read(ref _attempts);

        public int Started => Volatile.Read(ref _started);

        public bool AllStopped => _children.All(c => c.Stopped);

        public IPadChild Start()
        {
            Interlocked.Increment(ref _attempts);
            if (FailToStart)
            {
                throw new InvalidOperationException("no process");
            }
            Interlocked.Increment(ref _started);
            var child = new Child(lines, stayOpen, _exits);
            _children.Add(child);
            return child;
        }

        public async Task WaitForExitsAsync(int count)
        {
            for (var i = 0; i < count; i++)
            {
                await _exits.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }

        private sealed class Child(IReadOnlyList<string> lines, bool stayOpen, SemaphoreSlim exits) : IPadChild
        {
            private readonly TaskCompletionSource _closed = new();

            public bool Stopped { get; private set; }

            public async IAsyncEnumerable<string> ReadLinesAsync([EnumeratorCancellation] CancellationToken cancellation)
            {
                foreach (var line in lines)
                {
                    yield return line;
                }
                if (stayOpen)
                {
                    await _closed.Task.WaitAsync(cancellation);
                }
                exits.Release();
            }

            public Task StopAsync()
            {
                Stopped = true;
                _closed.TrySetResult();
                return Task.CompletedTask;
            }

            public void Dispose()
            {
            }
        }
    }
}

public class HookWatchdogTests
{
    [Theory]
    [InlineData(TapState.Disabled, HookHealth.Ok, true)]
    [InlineData(TapState.Enabled, HookHealth.Ok, false)]
    [InlineData(TapState.Unknown, HookHealth.Ok, false)]
    [InlineData(TapState.Disabled, HookHealth.Starting, false)]
    [InlineData(TapState.Disabled, HookHealth.Stopped, false)]
    [InlineData(TapState.Disabled, HookHealth.Failed, false)]
    public void A_disabled_tap_fails_a_hook_that_claims_to_be_running(TapState tap, HookHealth health, bool expected)
    {
        var watchdog = new HookWatchdog(new FixedProbe(tap));

        Assert.Equal(expected, watchdog.Check(health));
    }

    [Fact]
    public void The_system_probe_never_throws_and_knows_nothing_off_macos()
    {
        var state = MacEventTaps.Probe.Check();

        Assert.True(state == TapState.Unknown || OperatingSystem.IsMacOS());
    }

    private sealed class FixedProbe(TapState state) : IEventTapProbe
    {
        public TapState Check() => state;
    }
}
