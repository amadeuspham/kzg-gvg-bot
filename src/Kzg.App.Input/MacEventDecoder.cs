using SharpHook.Data;

namespace Kzg.App.Input;

/// <summary>
/// Turns what a macOS event tap sees (event type, key code, button number, modifier flags) into the same
/// <see cref="RawInput"/> the hook gave, with SharpHook's names, so saved bindings keep working. Pure, so it is
/// tested without the system.
/// </summary>
public static class MacEventDecoder
{
    // CGEventType
    public const uint LeftMouseDown = 1;
    public const uint LeftMouseUp = 2;
    public const uint RightMouseDown = 3;
    public const uint RightMouseUp = 4;
    public const uint KeyDown = 10;
    public const uint KeyUp = 11;
    public const uint FlagsChanged = 12;
    public const uint OtherMouseDown = 25;
    public const uint OtherMouseUp = 26;
    public const uint TapDisabledByTimeout = 0xFFFFFFFE;
    public const uint TapDisabledByUserInput = 0xFFFFFFFF;

    /// <summary>The bit mask of the event types a tap listens to: keys, modifiers and mouse buttons. Never movement or scrolling.</summary>
    public static ulong EventMask { get; } =
        Bit(KeyDown) | Bit(KeyUp) | Bit(FlagsChanged)
        | Bit(LeftMouseDown) | Bit(LeftMouseUp) | Bit(RightMouseDown) | Bit(RightMouseUp) | Bit(OtherMouseDown) | Bit(OtherMouseUp);

    /// <summary>The input an event means, or null for one that isn't a binding candidate (an unmapped key, the fn key).</summary>
    /// <param name="type">The CGEventType.</param>
    /// <param name="keyCode">The virtual key code of a key event.</param>
    /// <param name="buttonNumber">The button of an "other" mouse event: 2 is the middle button, 3 and up are the side buttons.</param>
    /// <param name="flags">The modifier flags of the event, with the device-dependent bits.</param>
    public static RawInput? Decode(uint type, long keyCode, long buttonNumber, ulong flags)
    {
        switch (type)
        {
            case KeyDown:
            case KeyUp:
                return KeyOf(keyCode) is { } key ? new RawInput(InputBinding.Key(key), type == KeyDown) : null;
            case FlagsChanged:
                return ModifierOf(keyCode, flags);
            case LeftMouseDown:
            case LeftMouseUp:
                return new RawInput(InputBinding.Mouse(MouseButton.Button1), type == LeftMouseDown);
            case RightMouseDown:
            case RightMouseUp:
                return new RawInput(InputBinding.Mouse(MouseButton.Button2), type == RightMouseDown);
            case OtherMouseDown:
            case OtherMouseUp:
                // The hook counted buttons from 1: the middle button (2 here) is Button3, a side button (3) is Button4.
                return buttonNumber is >= 2 and <= 4
                    ? new RawInput(InputBinding.Mouse((MouseButton)(buttonNumber + 1)), type == OtherMouseDown)
                    : null;
            default:
                return null;
        }
    }

    // A modifier key is a flags change: down when its own device bit is set, up when it is clear.
    private static RawInput? ModifierOf(long keyCode, ulong flags)
    {
        var (key, mask) = keyCode switch
        {
            0x3B => (KeyCode.VcLeftControl, 0x00000001UL),
            0x38 => (KeyCode.VcLeftShift, 0x00000002UL),
            0x3C => (KeyCode.VcRightShift, 0x00000004UL),
            0x37 => (KeyCode.VcLeftMeta, 0x00000008UL),
            0x36 => (KeyCode.VcRightMeta, 0x00000010UL),
            0x3A => (KeyCode.VcLeftAlt, 0x00000020UL),
            0x3D => (KeyCode.VcRightAlt, 0x00000040UL),
            0x3E => (KeyCode.VcRightControl, 0x00002000UL),
            0x39 => (KeyCode.VcCapsLock, 0x00010000UL),
            _ => (KeyCode.VcUndefined, 0UL),
        };
        return key == KeyCode.VcUndefined ? null : new RawInput(InputBinding.Key(key), (flags & mask) != 0);
    }

    /// <summary>The key for a macOS virtual key code (kVK_*), for a US-style layout of codes: by position, not by character.</summary>
    public static KeyCode? KeyOf(long code) => code switch
    {
        0x00 => KeyCode.VcA,
        0x01 => KeyCode.VcS,
        0x02 => KeyCode.VcD,
        0x03 => KeyCode.VcF,
        0x04 => KeyCode.VcH,
        0x05 => KeyCode.VcG,
        0x06 => KeyCode.VcZ,
        0x07 => KeyCode.VcX,
        0x08 => KeyCode.VcC,
        0x09 => KeyCode.VcV,
        0x0B => KeyCode.VcB,
        0x0C => KeyCode.VcQ,
        0x0D => KeyCode.VcW,
        0x0E => KeyCode.VcE,
        0x0F => KeyCode.VcR,
        0x10 => KeyCode.VcY,
        0x11 => KeyCode.VcT,
        0x12 => KeyCode.Vc1,
        0x13 => KeyCode.Vc2,
        0x14 => KeyCode.Vc3,
        0x15 => KeyCode.Vc4,
        0x16 => KeyCode.Vc6,
        0x17 => KeyCode.Vc5,
        0x18 => KeyCode.VcEquals,
        0x19 => KeyCode.Vc9,
        0x1A => KeyCode.Vc7,
        0x1B => KeyCode.VcMinus,
        0x1C => KeyCode.Vc8,
        0x1D => KeyCode.Vc0,
        0x1E => KeyCode.VcCloseBracket,
        0x1F => KeyCode.VcO,
        0x20 => KeyCode.VcU,
        0x21 => KeyCode.VcOpenBracket,
        0x22 => KeyCode.VcI,
        0x23 => KeyCode.VcP,
        0x24 => KeyCode.VcEnter,
        0x25 => KeyCode.VcL,
        0x26 => KeyCode.VcJ,
        0x27 => KeyCode.VcQuote,
        0x28 => KeyCode.VcK,
        0x29 => KeyCode.VcSemicolon,
        0x2A => KeyCode.VcBackslash,
        0x2B => KeyCode.VcComma,
        0x2C => KeyCode.VcSlash,
        0x2D => KeyCode.VcN,
        0x2E => KeyCode.VcM,
        0x2F => KeyCode.VcPeriod,
        0x30 => KeyCode.VcTab,
        0x31 => KeyCode.VcSpace,
        0x32 => KeyCode.VcBackQuote,
        0x33 => KeyCode.VcBackspace,
        0x35 => KeyCode.VcEscape,
        0x40 => KeyCode.VcF17,
        0x41 => KeyCode.VcNumPadDecimal,
        0x43 => KeyCode.VcNumPadMultiply,
        0x45 => KeyCode.VcNumPadAdd,
        0x47 => KeyCode.VcNumLock,
        0x48 => KeyCode.VcVolumeUp,
        0x49 => KeyCode.VcVolumeDown,
        0x4A => KeyCode.VcVolumeMute,
        0x4B => KeyCode.VcNumPadDivide,
        0x4C => KeyCode.VcNumPadEnter,
        0x4E => KeyCode.VcNumPadSubtract,
        0x4F => KeyCode.VcF18,
        0x50 => KeyCode.VcF19,
        0x51 => KeyCode.VcNumPadEquals,
        0x52 => KeyCode.VcNumPad0,
        0x53 => KeyCode.VcNumPad1,
        0x54 => KeyCode.VcNumPad2,
        0x55 => KeyCode.VcNumPad3,
        0x56 => KeyCode.VcNumPad4,
        0x57 => KeyCode.VcNumPad5,
        0x58 => KeyCode.VcNumPad6,
        0x59 => KeyCode.VcNumPad7,
        0x5A => KeyCode.VcF20,
        0x5B => KeyCode.VcNumPad8,
        0x5C => KeyCode.VcNumPad9,
        0x60 => KeyCode.VcF5,
        0x61 => KeyCode.VcF6,
        0x62 => KeyCode.VcF7,
        0x63 => KeyCode.VcF3,
        0x64 => KeyCode.VcF8,
        0x65 => KeyCode.VcF9,
        0x67 => KeyCode.VcF11,
        0x69 => KeyCode.VcF13,
        0x6A => KeyCode.VcF16,
        0x6B => KeyCode.VcF14,
        0x6D => KeyCode.VcF10,
        0x6E => KeyCode.VcContextMenu,
        0x6F => KeyCode.VcF12,
        0x71 => KeyCode.VcF15,
        0x72 => KeyCode.VcInsert,
        0x73 => KeyCode.VcHome,
        0x74 => KeyCode.VcPageUp,
        0x75 => KeyCode.VcDelete,
        0x76 => KeyCode.VcF4,
        0x77 => KeyCode.VcEnd,
        0x78 => KeyCode.VcF2,
        0x79 => KeyCode.VcPageDown,
        0x7A => KeyCode.VcF1,
        0x7B => KeyCode.VcLeft,
        0x7C => KeyCode.VcRight,
        0x7D => KeyCode.VcDown,
        0x7E => KeyCode.VcUp,
        _ => null,
    };

    private static ulong Bit(uint type) => 1UL << (int)type;
}
