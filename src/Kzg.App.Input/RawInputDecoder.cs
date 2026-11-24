using SharpHook.Data;

namespace Kzg.App.Input;

/// <summary>
/// Turns a Windows <c>RAWINPUT</c> record into the same <see cref="RawInput"/> the low-level hook gave, with
/// SharpHook's key and button names, so saved bindings keep working. Pure, so it is tested without Windows.
/// </summary>
public static class RawInputDecoder
{
    public const uint TypeMouse = 0;
    public const uint TypeKeyboard = 1;

    private const ushort KeyBreak = 0x01;
    private const ushort KeyE0 = 0x02;
    private const ushort KeyE1 = 0x04;

    /// <summary>Decodes one <c>RAWINPUT</c> (header and data, as <c>GetRawInputData</c> fills it). Empty for anything that isn't a key or a mouse button.</summary>
    /// <param name="data">The record bytes.</param>
    /// <param name="headerSize">The <c>RAWINPUTHEADER</c> size: 24 in a 64-bit process, 16 in a 32-bit one.</param>
    public static IReadOnlyList<RawInput> Decode(ReadOnlySpan<byte> data, int headerSize)
    {
        if (data.Length < headerSize + 8)
        {
            return [];
        }
        var type = BitConverter.ToUInt32(data);
        var body = data[headerSize..];
        if (type == TypeKeyboard && body.Length >= 10)
        {
            var make = BitConverter.ToUInt16(body);
            var flags = BitConverter.ToUInt16(body[2..]);
            var vkey = BitConverter.ToUInt16(body[6..]);
            return DecodeKey(make, flags, vkey) is { } key ? [key] : [];
        }
        if (type == TypeMouse && body.Length >= 8)
        {
            // RAWMOUSE: usFlags (2), two bytes of padding, then usButtonFlags (2) and usButtonData (2).
            return DecodeMouse(BitConverter.ToUInt16(body[4..]));
        }
        return [];
    }

    public static RawInput? DecodeKey(ushort makeCode, ushort flags, ushort vkey)
    {
        var extended = (flags & KeyE0) != 0;
        // 0xFF is the overrun marker, and an E0 0x2A shift is the fake one Windows adds around Insert, Delete and the arrows.
        if (vkey == 0xFF || (extended && makeCode == 0x2A) || ((flags & KeyE1) != 0 && vkey != 0x13))
        {
            return null;
        }
        return KeyOf(vkey, makeCode, extended) is { } key ? new RawInput(InputBinding.Key(key), (flags & KeyBreak) == 0) : null;
    }

    public static IReadOnlyList<RawInput> DecodeMouse(ushort buttonFlags)
    {
        if (buttonFlags == 0)
        {
            return [];
        }
        var changes = new List<RawInput>(2);
        (ushort Down, ushort Up, MouseButton Button)[] buttons =
        [
            (0x0001, 0x0002, MouseButton.Button1),
            (0x0004, 0x0008, MouseButton.Button2),
            (0x0010, 0x0020, MouseButton.Button3),
            (0x0040, 0x0080, MouseButton.Button4),
            (0x0100, 0x0200, MouseButton.Button5),
        ];
        foreach (var (down, up, button) in buttons)
        {
            if ((buttonFlags & down) != 0)
            {
                changes.Add(new RawInput(InputBinding.Mouse(button), true));
            }
            if ((buttonFlags & up) != 0)
            {
                changes.Add(new RawInput(InputBinding.Mouse(button), false));
            }
        }
        return changes;
    }

    /// <summary>The key for a virtual key, split by make code and the E0 prefix where one VK covers two keys.</summary>
    public static KeyCode? KeyOf(ushort vkey, ushort makeCode, bool extended) => vkey switch
    {
        >= 0x41 and <= 0x5A => Letters[vkey - 0x41],
        >= 0x30 and <= 0x39 => Digits[vkey - 0x30],
        >= 0x60 and <= 0x69 => NumPadDigits[vkey - 0x60],
        >= 0x70 and <= 0x87 => FunctionKeys[vkey - 0x70],
        0x08 => KeyCode.VcBackspace,
        0x09 => KeyCode.VcTab,
        0x0C => KeyCode.VcNumPadClear,
        0x0D => extended ? KeyCode.VcNumPadEnter : KeyCode.VcEnter,
        0x10 => makeCode == 0x36 ? KeyCode.VcRightShift : KeyCode.VcLeftShift,
        0x11 => extended ? KeyCode.VcRightControl : KeyCode.VcLeftControl,
        0x12 => extended ? KeyCode.VcRightAlt : KeyCode.VcLeftAlt,
        0x13 => KeyCode.VcPause,
        0x14 => KeyCode.VcCapsLock,
        0x1B => KeyCode.VcEscape,
        0x20 => KeyCode.VcSpace,
        0x21 => extended ? KeyCode.VcPageUp : KeyCode.VcNumPad9,
        0x22 => extended ? KeyCode.VcPageDown : KeyCode.VcNumPad3,
        0x23 => extended ? KeyCode.VcEnd : KeyCode.VcNumPad1,
        0x24 => extended ? KeyCode.VcHome : KeyCode.VcNumPad7,
        0x25 => extended ? KeyCode.VcLeft : KeyCode.VcNumPad4,
        0x26 => extended ? KeyCode.VcUp : KeyCode.VcNumPad8,
        0x27 => extended ? KeyCode.VcRight : KeyCode.VcNumPad6,
        0x28 => extended ? KeyCode.VcDown : KeyCode.VcNumPad2,
        0x2C => KeyCode.VcPrintScreen,
        0x2D => extended ? KeyCode.VcInsert : KeyCode.VcNumPad0,
        0x2E => extended ? KeyCode.VcDelete : KeyCode.VcNumPadDecimal,
        0x2F => KeyCode.VcHelp,
        0x5B => KeyCode.VcLeftMeta,
        0x5C => KeyCode.VcRightMeta,
        0x5D => KeyCode.VcContextMenu,
        0x5F => KeyCode.VcSleep,
        0x6A => KeyCode.VcNumPadMultiply,
        0x6B => KeyCode.VcNumPadAdd,
        0x6C => KeyCode.VcNumPadSeparator,
        0x6D => KeyCode.VcNumPadSubtract,
        0x6E => KeyCode.VcNumPadDecimal,
        0x6F => KeyCode.VcNumPadDivide,
        0x90 => KeyCode.VcNumLock,
        0x91 => KeyCode.VcScrollLock,
        0xA6 => KeyCode.VcBrowserBack,
        0xA7 => KeyCode.VcBrowserForward,
        0xA8 => KeyCode.VcBrowserRefresh,
        0xA9 => KeyCode.VcBrowserStop,
        0xAA => KeyCode.VcBrowserSearch,
        0xAB => KeyCode.VcBrowserFavorites,
        0xAC => KeyCode.VcBrowserHome,
        0xAD => KeyCode.VcVolumeMute,
        0xAE => KeyCode.VcVolumeDown,
        0xAF => KeyCode.VcVolumeUp,
        0xB0 => KeyCode.VcMediaNext,
        0xB1 => KeyCode.VcMediaPrevious,
        0xB2 => KeyCode.VcMediaStop,
        0xB3 => KeyCode.VcMediaPlay,
        0xB4 => KeyCode.VcAppMail,
        0xB5 => KeyCode.VcMediaSelect,
        0xBA => KeyCode.VcSemicolon,
        0xBB => KeyCode.VcEquals,
        0xBC => KeyCode.VcComma,
        0xBD => KeyCode.VcMinus,
        0xBE => KeyCode.VcPeriod,
        0xBF => KeyCode.VcSlash,
        0xC0 => KeyCode.VcBackQuote,
        0xDB => KeyCode.VcOpenBracket,
        0xDC => KeyCode.VcBackslash,
        0xDD => KeyCode.VcCloseBracket,
        0xDE => KeyCode.VcQuote,
        0xE2 => KeyCode.VcSection,
        _ => null,
    };

    private static readonly KeyCode[] Letters =
    [
        KeyCode.VcA, KeyCode.VcB, KeyCode.VcC, KeyCode.VcD, KeyCode.VcE, KeyCode.VcF, KeyCode.VcG, KeyCode.VcH, KeyCode.VcI,
        KeyCode.VcJ, KeyCode.VcK, KeyCode.VcL, KeyCode.VcM, KeyCode.VcN, KeyCode.VcO, KeyCode.VcP, KeyCode.VcQ, KeyCode.VcR,
        KeyCode.VcS, KeyCode.VcT, KeyCode.VcU, KeyCode.VcV, KeyCode.VcW, KeyCode.VcX, KeyCode.VcY, KeyCode.VcZ,
    ];

    private static readonly KeyCode[] Digits =
    [
        KeyCode.Vc0, KeyCode.Vc1, KeyCode.Vc2, KeyCode.Vc3, KeyCode.Vc4, KeyCode.Vc5, KeyCode.Vc6, KeyCode.Vc7, KeyCode.Vc8, KeyCode.Vc9,
    ];

    private static readonly KeyCode[] NumPadDigits =
    [
        KeyCode.VcNumPad0, KeyCode.VcNumPad1, KeyCode.VcNumPad2, KeyCode.VcNumPad3, KeyCode.VcNumPad4,
        KeyCode.VcNumPad5, KeyCode.VcNumPad6, KeyCode.VcNumPad7, KeyCode.VcNumPad8, KeyCode.VcNumPad9,
    ];

    private static readonly KeyCode[] FunctionKeys =
    [
        KeyCode.VcF1, KeyCode.VcF2, KeyCode.VcF3, KeyCode.VcF4, KeyCode.VcF5, KeyCode.VcF6, KeyCode.VcF7, KeyCode.VcF8,
        KeyCode.VcF9, KeyCode.VcF10, KeyCode.VcF11, KeyCode.VcF12, KeyCode.VcF13, KeyCode.VcF14, KeyCode.VcF15, KeyCode.VcF16,
        KeyCode.VcF17, KeyCode.VcF18, KeyCode.VcF19, KeyCode.VcF20, KeyCode.VcF21, KeyCode.VcF22, KeyCode.VcF23, KeyCode.VcF24,
    ];
}
