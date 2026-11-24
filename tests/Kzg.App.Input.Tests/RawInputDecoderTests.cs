using SharpHook.Data;

namespace Kzg.App.Input.Tests;

public class RawInputDecoderTests
{
    private const int Header64 = 24;
    private const int Header32 = 16;

    private static byte[] Key(ushort make, ushort flags, ushort vkey, int header = Header64)
    {
        var data = new byte[header + 24];
        BitConverter.GetBytes(RawInputDecoder.TypeKeyboard).CopyTo(data, 0);
        BitConverter.GetBytes(make).CopyTo(data, header);
        BitConverter.GetBytes(flags).CopyTo(data, header + 2);
        BitConverter.GetBytes(vkey).CopyTo(data, header + 6);
        return data;
    }

    private static byte[] Mouse(ushort buttons, int header = Header64)
    {
        var data = new byte[header + 24];
        BitConverter.GetBytes(RawInputDecoder.TypeMouse).CopyTo(data, 0);
        BitConverter.GetBytes(buttons).CopyTo(data, header + 4);
        return data;
    }

    private static IReadOnlyList<RawInput> Decode(byte[] data, int header = Header64) => RawInputDecoder.Decode(data, header);

    private static RawInput Pressed(KeyCode key, bool down) => new(InputBinding.Key(key), down);

    [Fact]
    public void A_key_goes_down_and_up_under_the_name_the_old_hook_gave_it()
    {
        Assert.Equal([Pressed(KeyCode.VcF8, true)], Decode(Key(0x42, 0, 0x77)));
        Assert.Equal([Pressed(KeyCode.VcF8, false)], Decode(Key(0x42, 1, 0x77)));
    }

    [Fact]
    public void A_record_from_a_32_bit_process_reads_the_same()
    {
        Assert.Equal([Pressed(KeyCode.VcA, true)], Decode(Key(0x1E, 0, 0x41, Header32), Header32));
        Assert.Equal([new RawInput(InputBinding.Mouse(MouseButton.Button4), true)], Decode(Mouse(0x40, Header32), Header32));
    }

    [Theory]
    [InlineData(0x0D, 0, KeyCode.VcEnter)]
    [InlineData(0x0D, 2, KeyCode.VcNumPadEnter)]
    [InlineData(0x11, 0, KeyCode.VcLeftControl)]
    [InlineData(0x11, 2, KeyCode.VcRightControl)]
    [InlineData(0x12, 0, KeyCode.VcLeftAlt)]
    [InlineData(0x12, 2, KeyCode.VcRightAlt)]
    [InlineData(0x2D, 2, KeyCode.VcInsert)]
    [InlineData(0x2D, 0, KeyCode.VcNumPad0)]
    [InlineData(0x24, 2, KeyCode.VcHome)]
    [InlineData(0x24, 0, KeyCode.VcNumPad7)]
    [InlineData(0x2E, 2, KeyCode.VcDelete)]
    [InlineData(0x2E, 0, KeyCode.VcNumPadDecimal)]
    public void One_virtual_key_for_two_physical_keys_is_told_apart_by_the_E0_prefix(int vkey, int flags, KeyCode expected)
    {
        Assert.Equal([Pressed(expected, true)], Decode(Key(0x10, (ushort)flags, (ushort)vkey)));
    }

    [Fact]
    public void Left_and_right_shift_are_told_apart_by_the_make_code()
    {
        Assert.Equal([Pressed(KeyCode.VcLeftShift, true)], Decode(Key(0x2A, 0, 0x10)));
        Assert.Equal([Pressed(KeyCode.VcRightShift, true)], Decode(Key(0x36, 0, 0x10)));
    }

    [Fact]
    public void The_fake_shift_windows_adds_around_the_arrow_keys_is_dropped()
    {
        Assert.Empty(Decode(Key(0x2A, 2, 0x10)));
        Assert.Equal([Pressed(KeyCode.VcLeft, true)], Decode(Key(0x4B, 2, 0x25)));
    }

    [Fact]
    public void A_keyboard_overrun_and_an_unnamed_key_are_dropped()
    {
        Assert.Empty(Decode(Key(0, 0, 0xFF)));
        Assert.Empty(Decode(Key(0x10, 0, 0xE7)));
    }

    [Fact]
    public void Pause_is_kept_though_windows_sends_it_with_the_E1_prefix()
    {
        Assert.Equal([Pressed(KeyCode.VcPause, true)], Decode(Key(0x1D, 4, 0x13)));
    }

    [Fact]
    public void The_whole_function_row_and_the_letters_and_digits_have_names()
    {
        for (ushort vkey = 0x70; vkey <= 0x87; vkey++)
        {
            Assert.NotEmpty(Decode(Key(1, 0, vkey)));
        }
        for (ushort vkey = 0x41; vkey <= 0x5A; vkey++)
        {
            Assert.NotEmpty(Decode(Key(1, 0, vkey)));
        }
        for (ushort vkey = 0x30; vkey <= 0x39; vkey++)
        {
            Assert.NotEmpty(Decode(Key(1, 0, vkey)));
        }
    }

    [Theory]
    [InlineData(0x0001, 0x0002, MouseButton.Button1)]
    [InlineData(0x0004, 0x0008, MouseButton.Button2)]
    [InlineData(0x0010, 0x0020, MouseButton.Button3)]
    [InlineData(0x0040, 0x0080, MouseButton.Button4)]
    [InlineData(0x0100, 0x0200, MouseButton.Button5)]
    public void Each_mouse_button_goes_down_and_up_under_its_number(int down, int up, MouseButton button)
    {
        Assert.Equal([new RawInput(InputBinding.Mouse(button), true)], Decode(Mouse((ushort)down)));
        Assert.Equal([new RawInput(InputBinding.Mouse(button), false)], Decode(Mouse((ushort)up)));
    }

    [Fact]
    public void Two_buttons_changing_in_one_record_give_both_changes()
    {
        var changes = Decode(Mouse(0x0002 | 0x0004));

        Assert.Equal(
            [new RawInput(InputBinding.Mouse(MouseButton.Button1), false), new RawInput(InputBinding.Mouse(MouseButton.Button2), true)],
            changes);
    }

    [Fact]
    public void Movement_and_the_wheel_are_dropped()
    {
        Assert.Empty(Decode(Mouse(0)));
        Assert.Empty(Decode(Mouse(0x0400)));
    }

    [Fact]
    public void A_record_that_is_too_short_or_from_another_device_is_dropped()
    {
        Assert.Empty(RawInputDecoder.Decode(new byte[10], Header64));
        var hid = Key(0x1E, 0, 0x41);
        BitConverter.GetBytes(2u).CopyTo(hid, 0);
        Assert.Empty(Decode(hid));
    }

    [Fact]
    public void A_decoded_key_and_a_decoded_side_button_reach_the_matcher_as_broadcasts()
    {
        var events = new List<BindingEvent>();
        var matcher = new BindingMatcher(new BindingSet(InputBinding.Key(KeyCode.VcF8), null, InputBinding.Mouse(MouseButton.Button4)));
        matcher.Event += events.Add;

        foreach (var raw in Decode(Key(0x42, 0, 0x77)).Concat(Decode(Key(0x42, 1, 0x77))).Concat(Decode(Mouse(0x40))))
        {
            matcher.Handle(raw);
        }

        Assert.Equal([BindingEvent.BroadcastDown, BindingEvent.BroadcastUp, BindingEvent.OpenMicToggle], events);
    }
}
