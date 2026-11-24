using SharpHook.Data;

namespace Kzg.App.Input.Tests;

public class MacEventDecoderTests
{
    private static readonly InputBinding F8 = InputBinding.Key(KeyCode.VcF8);

    [Fact]
    public void A_key_goes_down_and_up_under_the_name_the_old_hook_gave_it()
    {
        Assert.Equal(new RawInput(F8, true), MacEventDecoder.Decode(MacEventDecoder.KeyDown, 0x64, 0, 0));
        Assert.Equal(new RawInput(F8, false), MacEventDecoder.Decode(MacEventDecoder.KeyUp, 0x64, 0, 0));
        Assert.Equal(new RawInput(InputBinding.Key(KeyCode.VcA), true), MacEventDecoder.Decode(MacEventDecoder.KeyDown, 0x00, 0, 0));
    }

    [Fact]
    public void A_key_macOS_has_no_name_for_is_ignored()
    {
        Assert.Null(MacEventDecoder.Decode(MacEventDecoder.KeyDown, 0x3F, 0, 0));
    }

    [Theory]
    [InlineData(0x38L, 0x2UL, KeyCode.VcLeftShift)]
    [InlineData(0x3CL, 0x4UL, KeyCode.VcRightShift)]
    [InlineData(0x3BL, 0x1UL, KeyCode.VcLeftControl)]
    [InlineData(0x3AL, 0x20UL, KeyCode.VcLeftAlt)]
    [InlineData(0x36L, 0x10UL, KeyCode.VcRightMeta)]
    public void A_modifier_is_down_while_its_own_flag_is_set_and_up_when_it_clears(long code, ulong flag, KeyCode key)
    {
        Assert.Equal(new RawInput(InputBinding.Key(key), true), MacEventDecoder.Decode(MacEventDecoder.FlagsChanged, code, 0, flag));
        Assert.Equal(new RawInput(InputBinding.Key(key), false), MacEventDecoder.Decode(MacEventDecoder.FlagsChanged, code, 0, 0));
    }

    [Fact]
    public void A_modifier_is_told_from_its_twin_by_the_device_flag()
    {
        // Left shift still held while right shift goes up: the change belongs to the right one.
        var raw = MacEventDecoder.Decode(MacEventDecoder.FlagsChanged, 0x3C, 0, 0x2);

        Assert.Equal(new RawInput(InputBinding.Key(KeyCode.VcRightShift), false), raw);
    }

    [Fact]
    public void A_flags_change_from_a_key_that_is_not_a_modifier_is_ignored()
    {
        Assert.Null(MacEventDecoder.Decode(MacEventDecoder.FlagsChanged, 0x3F, 0, 0x800000));
    }

    [Fact]
    public void The_left_and_right_buttons_are_buttons_one_and_two()
    {
        Assert.Equal(new RawInput(InputBinding.Mouse(MouseButton.Button1), true), MacEventDecoder.Decode(MacEventDecoder.LeftMouseDown, 0, 0, 0));
        Assert.Equal(new RawInput(InputBinding.Mouse(MouseButton.Button1), false), MacEventDecoder.Decode(MacEventDecoder.LeftMouseUp, 0, 0, 0));
        Assert.Equal(new RawInput(InputBinding.Mouse(MouseButton.Button2), true), MacEventDecoder.Decode(MacEventDecoder.RightMouseDown, 0, 0, 0));
        Assert.Equal(new RawInput(InputBinding.Mouse(MouseButton.Button2), false), MacEventDecoder.Decode(MacEventDecoder.RightMouseUp, 0, 0, 0));
    }

    [Theory]
    [InlineData(2L, MouseButton.Button3)]
    [InlineData(3L, MouseButton.Button4)]
    [InlineData(4L, MouseButton.Button5)]
    public void The_middle_and_side_buttons_keep_the_numbers_a_saved_binding_has(long number, MouseButton button)
    {
        Assert.Equal(new RawInput(InputBinding.Mouse(button), true), MacEventDecoder.Decode(MacEventDecoder.OtherMouseDown, 0, number, 0));
        Assert.Equal(new RawInput(InputBinding.Mouse(button), false), MacEventDecoder.Decode(MacEventDecoder.OtherMouseUp, 0, number, 0));
    }

    [Fact]
    public void A_button_beyond_the_fifth_is_ignored()
    {
        Assert.Null(MacEventDecoder.Decode(MacEventDecoder.OtherMouseDown, 0, 7, 0));
    }

    [Fact]
    public void The_tap_listens_to_keys_and_buttons_and_never_to_movement_or_scrolling()
    {
        var mask = MacEventDecoder.EventMask;

        foreach (var type in new[] { 1, 2, 3, 4, 10, 11, 12, 25, 26 })
        {
            Assert.NotEqual(0UL, mask & (1UL << type));
        }
        // kCGEventMouseMoved 5, the three dragged types 6 to 7 and 27, scrollWheel 22.
        foreach (var type in new[] { 5, 6, 7, 22, 27 })
        {
            Assert.Equal(0UL, mask & (1UL << type));
        }
    }

    [Fact]
    public void No_two_macOS_key_codes_share_a_key()
    {
        var keys = Enumerable.Range(0, 0x80).Select(code => MacEventDecoder.KeyOf(code)).Where(key => key is not null).ToList();

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.DoesNotContain(KeyCode.VcUndefined, keys.Select(key => key!.Value));
    }

    [Fact]
    public void A_decoded_key_reaches_the_matcher_as_a_broadcast()
    {
        var events = new List<BindingEvent>();
        var matcher = new BindingMatcher(new BindingSet(F8, null, InputBinding.Key(KeyCode.VcF9)));
        matcher.Event += events.Add;

        matcher.Handle(MacEventDecoder.Decode(MacEventDecoder.KeyDown, 0x64, 0, 0)!.Value);
        matcher.Handle(MacEventDecoder.Decode(MacEventDecoder.KeyDown, 0x64, 0, 0)!.Value);
        matcher.Handle(MacEventDecoder.Decode(MacEventDecoder.KeyUp, 0x64, 0, 0)!.Value);

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp], events);
    }

    [Fact]
    public void A_side_button_reaches_the_matcher_as_a_broadcast()
    {
        var events = new List<BindingEvent>();
        var matcher = new BindingMatcher(new BindingSet(InputBinding.Mouse(MouseButton.Button4), null, F8));
        matcher.Event += events.Add;

        matcher.Handle(MacEventDecoder.Decode(MacEventDecoder.OtherMouseDown, 0, 3, 0)!.Value);
        matcher.Handle(MacEventDecoder.Decode(MacEventDecoder.OtherMouseUp, 0, 3, 0)!.Value);

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp], events);
    }
}
