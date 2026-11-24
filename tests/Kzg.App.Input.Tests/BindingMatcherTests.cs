using SharpHook.Data;

namespace Kzg.App.Input.Tests;

public class BindingMatcherTests
{
    private static readonly InputBinding F7 = InputBinding.Key(KeyCode.VcF7);
    private static readonly InputBinding F8 = InputBinding.Key(KeyCode.VcF8);
    private static readonly InputBinding F9 = InputBinding.Key(KeyCode.VcF9);
    private static readonly InputBinding F10 = InputBinding.Key(KeyCode.VcF10);
    private static readonly InputBinding Side = InputBinding.Mouse(MouseButton.Button4);
    private static readonly InputBinding PadA = InputBinding.Pad("a");

    private readonly List<BindingEvent> _events = [];

    private BindingMatcher Matcher(InputBinding? broadcast = null, InputBinding? openMic = null, InputBinding? allTeams = null)
    {
        var matcher = new BindingMatcher(new BindingSet(broadcast ?? F8, allTeams ?? F7, openMic ?? F9));
        matcher.Event += _events.Add;
        return matcher;
    }

    private static RawInput Down(InputBinding input) => new(input, true);

    private static RawInput Up(InputBinding input) => new(input, false);

    [Theory]
    [InlineData("key")]
    [InlineData("mouse")]
    [InlineData("pad")]
    public void A_broadcast_input_of_any_kind_goes_down_and_up_once(string kind)
    {
        var input = kind switch { "key" => F10, "mouse" => Side, _ => PadA };
        var matcher = Matcher(broadcast: input);

        matcher.Handle(Down(input));
        Assert.True(matcher.BroadcastIsDown);
        matcher.Handle(Up(input));

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp], _events);
        Assert.False(matcher.BroadcastIsDown);
    }

    [Fact]
    public void Auto_repeat_is_collapsed_to_the_single_down()
    {
        var matcher = Matcher();

        matcher.Handle(Down(F8));
        matcher.Handle(Down(F8));
        matcher.Handle(Down(F8));
        matcher.Handle(Up(F8));

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp], _events);
    }

    [Fact]
    public void Inputs_that_are_not_bound_do_nothing_even_while_the_broadcast_is_held()
    {
        var matcher = Matcher();
        matcher.Handle(Down(F8));

        matcher.Handle(Down(F10));
        matcher.Handle(Up(F10));

        Assert.Equal([BindingEvent.BroadcastDown], _events);
        Assert.True(matcher.BroadcastIsDown);
    }

    [Fact]
    public void An_open_mic_input_toggles_once_per_press_not_per_repeat_or_release()
    {
        var matcher = Matcher();

        matcher.Handle(Down(F9));
        matcher.Handle(Down(F9));
        matcher.Handle(Up(F9));
        matcher.Handle(Down(F9));
        matcher.Handle(Up(F9));

        Assert.Equal([BindingEvent.OpenMicToggle, BindingEvent.OpenMicToggle], _events);
    }

    [Fact]
    public void An_up_without_a_down_is_ignored()
    {
        var matcher = Matcher();

        matcher.Handle(Up(F8));

        Assert.Empty(_events);
    }

    [Fact]
    public void A_hold_ends_at_once_when_its_input_is_no_longer_the_broadcast_input()
    {
        var matcher = Matcher();
        matcher.Handle(Down(F8));

        matcher.SetBindings(new BindingSet(Side, null, F9));
        matcher.Handle(Up(F8));

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp], _events);
    }

    [Fact]
    public void Rebinding_to_the_other_actions_input_swaps_cleanly()
    {
        var matcher = Matcher();

        matcher.SetBindings(new BindingSet(F9, null, F8));
        matcher.Handle(Down(F9));
        matcher.Handle(Up(F9));
        matcher.Handle(Down(F8));

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp, BindingEvent.OpenMicToggle], _events);
    }

    [Fact]
    public void An_input_already_down_when_it_is_bound_does_not_start_a_broadcast()
    {
        var matcher = Matcher();
        matcher.Handle(Down(F10));

        matcher.SetBindings(new BindingSet(F10, null, F9));
        matcher.Handle(Down(F10));
        matcher.Handle(Up(F10));

        Assert.Empty(_events);

        matcher.Handle(Down(F10));
        Assert.Equal([BindingEvent.BroadcastDown], _events);
    }

    [Fact]
    public void Muting_ends_a_hold_and_hides_presses_but_still_tracks_what_is_held()
    {
        var matcher = Matcher();
        matcher.Handle(Down(F8));

        matcher.Muted = true;
        matcher.Handle(Up(F8));
        matcher.Handle(Down(F8));
        matcher.Handle(Down(F8));
        matcher.Muted = false;
        matcher.Handle(Down(F8));

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp], _events);

        matcher.Handle(Up(F8));
        matcher.Handle(Down(F8));
        Assert.Equal(BindingEvent.BroadcastDown, _events[^1]);
    }

    [Fact]
    public void Releasing_everything_ends_a_hold_and_forgets_held_inputs()
    {
        var matcher = Matcher();
        matcher.Handle(Down(F8));

        matcher.ReleaseAll();
        matcher.Handle(Down(F8));

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp, BindingEvent.BroadcastDown], _events);
    }

    [Fact]
    public void Releasing_one_source_ends_a_hold_only_when_the_held_input_came_from_it()
    {
        var matcher = Matcher(broadcast: PadA);
        matcher.Handle(Down(PadA));

        matcher.Release(InputKind.Key);
        Assert.True(matcher.BroadcastIsDown);

        matcher.Release(InputKind.Pad);
        Assert.False(matcher.BroadcastIsDown);
        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp], _events);
    }

    [Fact]
    public void The_all_teams_input_holds_on_its_own_beside_the_team_input()
    {
        var matcher = Matcher();

        matcher.Handle(Down(F7));
        Assert.True(matcher.AllTeamsIsDown);
        Assert.False(matcher.BroadcastIsDown);
        matcher.Handle(Down(F8));
        matcher.Handle(Up(F7));
        Assert.True(matcher.BroadcastIsDown);
        Assert.False(matcher.AllTeamsIsDown);
        matcher.Handle(Up(F8));

        Assert.Equal([BindingEvent.AllTeamsDown, BindingEvent.BroadcastDown, BindingEvent.AllTeamsUp, BindingEvent.BroadcastUp], _events);
    }

    [Fact]
    public void Repeats_of_the_all_teams_input_count_once()
    {
        var matcher = Matcher();

        matcher.Handle(Down(F7));
        matcher.Handle(Down(F7));
        matcher.Handle(Up(F7));

        Assert.Equal([BindingEvent.AllTeamsDown, BindingEvent.AllTeamsUp], _events);
    }

    [Fact]
    public void An_action_left_unset_never_fires()
    {
        var matcher = new BindingMatcher(new BindingSet(null, null, null));
        matcher.Event += _events.Add;

        foreach (var input in new[] { F7, F8, F9 })
        {
            matcher.Handle(Down(input));
            matcher.Handle(Up(input));
        }

        Assert.Empty(_events);
        Assert.False(matcher.BroadcastIsDown);
        Assert.False(matcher.AllTeamsIsDown);
    }

    [Fact]
    public void Clearing_a_held_input_ends_its_hold_and_leaves_the_other_held()
    {
        var matcher = Matcher();
        matcher.Handle(Down(F7));
        matcher.Handle(Down(F8));

        matcher.SetBindings(new BindingSet(F8, null, F9));

        Assert.Equal([BindingEvent.AllTeamsDown, BindingEvent.BroadcastDown, BindingEvent.AllTeamsUp], _events);
        Assert.True(matcher.BroadcastIsDown);
        matcher.Handle(Up(F7));
        Assert.Equal(BindingEvent.AllTeamsUp, _events[^1]);
    }

    [Fact]
    public void Releasing_everything_ends_both_holds()
    {
        var matcher = Matcher();
        matcher.Handle(Down(F7));
        matcher.Handle(Down(F8));

        matcher.ReleaseAll();

        Assert.Equal(4, _events.Count);
        Assert.Equal([BindingEvent.AllTeamsUp, BindingEvent.BroadcastUp], _events.Skip(2).Order().Reverse());
        Assert.False(matcher.AllTeamsIsDown || matcher.BroadcastIsDown);
    }

    [Fact]
    public void Bindings_that_break_the_rules_are_refused()
    {
        Assert.Throws<ArgumentException>(() => new BindingMatcher(new BindingSet(F8, null, F8)));
        Assert.Throws<ArgumentException>(() => new BindingMatcher(new BindingSet(F8, F8, F9)));

        var matcher = Matcher();
        Assert.Throws<ArgumentException>(() => matcher.SetBindings(new BindingSet(F8, null, F8)));
    }
}
