using SharpHook.Data;

namespace Kzg.App.Input.Tests;

public class InputBindingTests
{
    [Theory]
    [InlineData("key:VcF8")]
    [InlineData("key:VcLeftShift")]
    [InlineData("mouse:Button4")]
    [InlineData("mouse:Button5")]
    [InlineData("pad:leftshoulder")]
    [InlineData("pad:dpad_up")]
    public void A_binding_round_trips_through_its_text(string text)
    {
        Assert.True(InputBinding.TryParse(text, out var binding));

        Assert.Equal(text, binding.Format());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("F8")]
    [InlineData("key:")]
    [InlineData("key:vcf8")]
    [InlineData("key:5")]
    [InlineData("key:99999")]
    [InlineData("mouse:NoButton")]
    [InlineData("mouse:button4")]
    [InlineData("mouse:4")]
    [InlineData("pad:A")]
    [InlineData("pad:")]
    [InlineData("pad:has space")]
    [InlineData("joystick:a")]
    public void Anything_but_the_exact_text_is_refused(string? text)
    {
        Assert.False(InputBinding.TryParse(text, out _));
    }

    [Fact]
    public void Keys_are_bound_by_code_not_by_character()
    {
        Assert.Equal("key:VcA", InputBinding.Key(KeyCode.VcA).Format());
    }

    [Theory]
    [InlineData("key:VcUndefined")]
    [InlineData("mouse:Button1")]
    [InlineData("mouse:Button2")]
    public void An_unknown_key_and_the_main_mouse_buttons_cannot_be_bindings(string text)
    {
        Assert.True(InputBinding.TryParse(text, out var binding));

        Assert.NotNull(binding.RefusalReason());
    }

    [Fact]
    public void Side_buttons_are_allowed_and_named_for_people()
    {
        var side = InputBinding.Mouse(MouseButton.Button4);

        Assert.Null(side.RefusalReason());
        Assert.Equal("Mouse side button 1", side.DisplayName);
        Assert.Equal("F8", InputBinding.Key(KeyCode.VcF8).DisplayName);
        Assert.Equal("Left Shift", InputBinding.Key(KeyCode.VcLeftShift).DisplayName);
        Assert.Equal("Controller: left shoulder", InputBinding.Pad("leftshoulder").DisplayName);
        Assert.Equal("Controller: A button", InputBinding.Pad("a").DisplayName);
    }

    [Fact]
    public void The_default_bindings_are_valid()
    {
        Assert.Null(BindingSet.Default.Problem());
        Assert.False(BindingSet.Default.UsesPad);
    }

    [Fact]
    public void One_input_cannot_do_both_actions()
    {
        var f8 = InputBinding.Key(KeyCode.VcF8);

        Assert.NotNull(new BindingSet(f8, null, f8).Problem());
    }

    [Fact]
    public void A_set_with_a_refused_input_is_refused_for_either_action()
    {
        var f8 = InputBinding.Key(KeyCode.VcF8);
        var left = InputBinding.Mouse(MouseButton.Button1);

        Assert.NotNull(new BindingSet(left, null, f8).Problem());
        Assert.NotNull(new BindingSet(f8, null, left).Problem());
    }

    [Fact]
    public void Escape_can_be_a_binding_since_picking_is_cancelled_with_a_button()
    {
        Assert.Null(InputBinding.Key(KeyCode.VcEscape).RefusalReason());
        Assert.Null(new BindingSet(InputBinding.Key(KeyCode.VcEscape), null, null).Problem());
    }

    [Fact]
    public void The_defaults_are_f8_for_the_team_f7_for_all_teams_and_f9_for_open_mic()
    {
        Assert.Equal(InputBinding.Key(KeyCode.VcF8), BindingSet.Default.Broadcast);
        Assert.Equal(InputBinding.Key(KeyCode.VcF7), BindingSet.Default.AllTeams);
        Assert.Equal(InputBinding.Key(KeyCode.VcF9), BindingSet.Default.OpenMic);
    }

    [Fact]
    public void No_input_does_two_actions_but_any_number_may_be_unset()
    {
        var f7 = InputBinding.Key(KeyCode.VcF7);
        var f8 = InputBinding.Key(KeyCode.VcF8);

        Assert.NotNull(new BindingSet(f8, f8, null).Problem());
        Assert.NotNull(new BindingSet(null, f7, f7).Problem());
        Assert.Null(new BindingSet(null, null, null).Problem());
        Assert.Null(new BindingSet(f8, null, f7).Problem());
    }

    [Fact]
    public void A_set_says_which_action_an_input_does_and_changes_one_action_at_a_time()
    {
        var set = BindingSet.Default.With(BindingAction.AllTeams, null).With(BindingAction.OpenMic, InputBinding.Key(KeyCode.VcF7));

        Assert.Equal(BindingAction.Broadcast, set.ActionOf(InputBinding.Key(KeyCode.VcF8)));
        Assert.Equal(BindingAction.OpenMic, set.ActionOf(InputBinding.Key(KeyCode.VcF7)));
        Assert.Null(set.ActionOf(InputBinding.Key(KeyCode.VcF9)));
        Assert.Null(set[BindingAction.AllTeams]);
        Assert.Equal(InputBinding.Key(KeyCode.VcF8), set[BindingAction.Broadcast]);
    }

    [Fact]
    public void A_set_knows_whether_it_needs_the_controller_helper()
    {
        var f8 = InputBinding.Key(KeyCode.VcF8);

        Assert.True(new BindingSet(InputBinding.Pad("a"), null, f8).UsesPad);
        Assert.True(new BindingSet(f8, null, InputBinding.Pad("b")).UsesPad);
        Assert.False(new BindingSet(InputBinding.Mouse(MouseButton.Button4), null, f8).UsesPad);
        Assert.True(new BindingSet(null, InputBinding.Pad("x"), null).UsesPad);
    }
}
