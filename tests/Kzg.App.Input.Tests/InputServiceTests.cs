using SharpHook.Data;

namespace Kzg.App.Input.Tests;

public class InputServiceTests : IDisposable
{
    private static readonly InputBinding F8 = InputBinding.Key(KeyCode.VcF8);

    private readonly FakeKeyMouseSource _source = new();
    private readonly FakePermissions _permissions = new();
    private readonly FakeTaps _taps = new();
    private readonly InputService _service;

    public InputServiceTests() => _service = new InputService(new FakeTimeSource(), _permissions, _taps, _source);

    public void Dispose() => _service.Dispose();

    private sealed class FakePermissions : IPermissionProbe
    {
        public PermissionState InputMonitoring { get; set; } = PermissionState.NotApplicable;

        public void RequestPrompts()
        {
        }

        public void OpenInputMonitoringSettings()
        {
        }
    }

    private sealed class FakeTaps : IEventTapProbe
    {
        public TapState State { get; set; } = TapState.Enabled;

        public TapState Check() => State;
    }

    [Fact]
    public async Task What_the_source_hears_reaches_the_matcher_through_the_service()
    {
        var events = new List<BindingEvent>();
        var matcher = new BindingMatcher(new BindingSet(F8, null, InputBinding.Key(KeyCode.VcF9)));
        matcher.Event += events.Add;
        _service.Input += matcher.Handle;
        await _service.StartHookAsync();

        _source.Raise(new RawInput(F8, true));
        _source.Raise(new RawInput(F8, false));

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp], events);
    }

    [Fact]
    public async Task Starting_starts_the_source_and_it_reports_listening()
    {
        await _service.StartHookAsync();

        Assert.Equal(1, _source.Starts);
        Assert.Equal(HookHealth.Ok, _service.Hook);
    }

    [Fact]
    public async Task Without_Input_Monitoring_nothing_starts_and_the_service_says_it_waits_for_permission()
    {
        _permissions.InputMonitoring = PermissionState.Denied;
        var changed = 0;
        _service.Changed += () => changed++;

        await _service.StartHookAsync();

        Assert.Equal(0, _source.Starts);
        Assert.Equal(HookHealth.NeedsAccessibility, _service.Hook);
        Assert.True(changed > 0);
    }

    [Fact]
    public async Task After_the_grant_a_restart_starts_the_source()
    {
        _permissions.InputMonitoring = PermissionState.Denied;
        await _service.StartHookAsync();

        _permissions.InputMonitoring = PermissionState.Granted;
        await _service.StartHookAsync();

        Assert.Equal(1, _source.Starts);
        Assert.Equal(HookHealth.Ok, _service.Hook);
    }

    [Fact]
    public async Task A_permission_nobody_asked_yet_still_lets_the_source_start_so_macOS_can_ask()
    {
        _permissions.InputMonitoring = PermissionState.NotAsked;

        await _service.StartHookAsync();

        Assert.Equal(1, _source.Starts);
    }

    [Fact]
    public async Task A_restart_stops_the_source_before_starting_it_again()
    {
        await _service.StartHookAsync();
        var seen = new List<HookHealth>();
        _source.HealthChanged += (health, _) => seen.Add(health);

        await _service.StartHookAsync();

        Assert.Equal([HookHealth.Stopped, HookHealth.Ok], seen);
    }

    [Fact]
    public async Task A_tap_macOS_switched_off_is_switched_back_on_and_the_hook_stays_healthy()
    {
        await _service.StartHookAsync();
        _taps.State = TapState.Disabled;
        _source.Recovers = true;

        _service.Maintain();

        Assert.Equal(1, _source.Recoveries);
        Assert.Null(_source.MarkedFailed);
        Assert.Equal(HookHealth.Ok, _service.Hook);
    }

    [Fact]
    public async Task A_tap_that_cannot_be_switched_back_on_is_called_failed_so_the_player_can_restart_input()
    {
        await _service.StartHookAsync();
        _taps.State = TapState.Disabled;
        _source.Recovers = false;

        _service.Maintain();

        Assert.Equal(HookWatchdog.FailureText, _source.MarkedFailed);
        Assert.Equal(HookHealth.Failed, _service.Hook);
        Assert.Equal(HookWatchdog.FailureText, _service.HookDetail);
    }

    [Fact]
    public async Task An_enabled_tap_is_left_alone()
    {
        await _service.StartHookAsync();

        _service.Maintain();

        Assert.Equal(0, _source.Recoveries);
        Assert.Null(_source.MarkedFailed);
    }

    [Fact]
    public void A_source_that_is_not_running_is_not_recovered()
    {
        _taps.State = TapState.Disabled;

        _service.Maintain();

        Assert.Equal(0, _source.Recoveries);
    }

    [Fact]
    public void Every_round_gives_the_source_its_own_housekeeping()
    {
        _service.Maintain();
        _service.Maintain();

        Assert.Equal(2, _source.Maintains);
    }
}
