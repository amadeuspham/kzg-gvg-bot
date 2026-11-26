using System.Diagnostics.CodeAnalysis;
using System.Text;
using SharpHook.Data;

namespace Kzg.App.Input;

public enum InputKind
{
    Key,
    Mouse,
    Pad,
}

/// <summary>
/// One physical input, by code and not by character, so a layout change never moves a binding. The
/// text form is <c>key:VcF8</c>, <c>mouse:Button4</c> or <c>pad:leftshoulder</c>: SharpHook's names for
/// keys and mouse buttons, SDL's for controller buttons.
/// </summary>
public readonly record struct InputBinding(InputKind Kind, string Name)
{
    public const int MaxNameLength = 24;

    public static InputBinding Key(KeyCode code) => new(InputKind.Key, code.ToString());

    public static InputBinding Mouse(MouseButton button) => new(InputKind.Mouse, button.ToString());

    public static InputBinding Pad(string button) => new(InputKind.Pad, button);

    /// <summary>The exact form <see cref="Format"/> writes; anything else, including a number for a key, is refused.</summary>
    public static bool TryParse(string? text, out InputBinding binding)
    {
        binding = default;
        if (text is null)
        {
            return false;
        }
        var colon = text.IndexOf(':');
        if (colon <= 0)
        {
            return false;
        }
        var name = text[(colon + 1)..];
        InputKind kind;
        switch (text[..colon])
        {
            case "key":
                kind = InputKind.Key;
                if (!Enum.TryParse<KeyCode>(name, ignoreCase: false, out var code) || !Enum.IsDefined(code) || code.ToString() != name)
                {
                    return false;
                }
                break;
            case "mouse":
                kind = InputKind.Mouse;
                if (!Enum.TryParse<MouseButton>(name, ignoreCase: false, out var button) || !Enum.IsDefined(button)
                    || button == MouseButton.NoButton || button.ToString() != name)
                {
                    return false;
                }
                break;
            case "pad":
                kind = InputKind.Pad;
                if (!IsPadName(name))
                {
                    return false;
                }
                break;
            default:
                return false;
        }
        binding = new InputBinding(kind, name);
        return true;
    }

    public string Format() => $"{Kind switch { InputKind.Key => "key", InputKind.Mouse => "mouse", _ => "pad" }}:{Name}";

    /// <summary>Why this input can't be a broadcast binding, or null when it can.</summary>
    public string? RefusalReason() => (Kind, Name) switch
    {
        (InputKind.Key, nameof(KeyCode.VcUndefined)) => "That key isn't recognised.",
        (InputKind.Mouse, nameof(MouseButton.Button1)) => "The left mouse button can't be a binding.",
        (InputKind.Mouse, nameof(MouseButton.Button2)) => "The right mouse button can't be a binding.",
        _ => null,
    };

    /// <summary>
    /// A name for people: "F8", "Left Shift", "/", "Mouse side button 1", "Controller: left shoulder". Letter, digit
    /// and punctuation keys show what the current layout types; read it on the UI thread (see <see cref="SystemKeyLabels"/>).
    /// </summary>
    public string DisplayName => NameWith(SystemKeyLabels.CharacterOf);

    /// <summary>
    /// <see cref="DisplayName"/> with <paramref name="layout"/> as the source of a key's character: for letters,
    /// digits and punctuation keys, what it returns (letters in upper case) unless that is null, blank or a control
    /// character, when the built-in symbol is used. Other keys ignore it.
    /// </summary>
    internal string NameWith(Func<KeyCode, string?> layout) => Kind switch
    {
        InputKind.Key => KeyName(Name, layout),
        InputKind.Mouse => Name switch
        {
            nameof(MouseButton.Button3) => "Middle mouse button",
            nameof(MouseButton.Button4) => "Mouse side button 1",
            nameof(MouseButton.Button5) => "Mouse side button 2",
            _ => $"Mouse {Name}",
        },
        _ => "Controller: " + PadDisplay(Name),
    };

    public override string ToString() => Format();

    private static bool IsPadName(string name) =>
        name.Length is > 0 and <= MaxNameLength && name.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');

    // Punctuation by US position: the symbol is the fallback when the layout can't say, and the layout's own
    // character wins otherwise, since the same key types "-" on one layout and "/" on another.
    private static readonly Dictionary<KeyCode, string> Symbols = new()
    {
        [KeyCode.VcMinus] = "-",
        [KeyCode.VcEquals] = "=",
        [KeyCode.VcOpenBracket] = "[",
        [KeyCode.VcCloseBracket] = "]",
        [KeyCode.VcBackslash] = "\\",
        [KeyCode.VcSemicolon] = ";",
        [KeyCode.VcQuote] = "'",
        [KeyCode.VcBackQuote] = "`",
        [KeyCode.VcComma] = ",",
        [KeyCode.VcPeriod] = ".",
        [KeyCode.VcSlash] = "/",
    };

    private static readonly Dictionary<KeyCode, string> NumPadSymbols = new()
    {
        [KeyCode.VcNumPadDivide] = "Numpad /",
        [KeyCode.VcNumPadMultiply] = "Numpad *",
        [KeyCode.VcNumPadSubtract] = "Numpad -",
        [KeyCode.VcNumPadAdd] = "Numpad +",
        [KeyCode.VcNumPadDecimal] = "Numpad .",
        [KeyCode.VcNumPadEquals] = "Numpad =",
    };

    private static string KeyName(string name, Func<KeyCode, string?> layout)
    {
        if (!Enum.TryParse<KeyCode>(name, ignoreCase: false, out var code) || !Enum.IsDefined(code))
        {
            return KeyDisplay(name);
        }
        if (NumPadSymbols.TryGetValue(code, out var numpad))
        {
            return numpad;
        }
        if (name.StartsWith("VcNumPad", StringComparison.Ordinal) && name.Length == 9 && char.IsAsciiDigit(name[8]))
        {
            return "Numpad " + name[8];
        }
        var isSymbol = Symbols.TryGetValue(code, out var symbol);
        if (isSymbol || IsLetterOrDigit(name))
        {
            var typed = layout(code);
            if (!string.IsNullOrWhiteSpace(typed) && !typed.Any(char.IsControl))
            {
                return typed.Trim().ToUpperInvariant();
            }
        }
        return symbol ?? KeyDisplay(name);
    }

    // SharpHook's key codes are scan codes, so letters and digits are not a contiguous range; the name is.
    private static bool IsLetterOrDigit(string name) =>
        name.Length == 3 && name.StartsWith("Vc", StringComparison.Ordinal) && char.IsAsciiLetterOrDigit(name[2]);

    private static string KeyDisplay(string name)
    {
        var text = name.StartsWith("Vc", StringComparison.Ordinal) ? name[2..] : name;
        var spaced = new StringBuilder(text.Length + 4);
        for (var i = 0; i < text.Length; i++)
        {
            if (i > 0 && char.IsUpper(text[i]) && char.IsLower(text[i - 1]))
            {
                spaced.Append(' ');
            }
            spaced.Append(text[i]);
        }
        return spaced.ToString();
    }

    private static string PadDisplay(string name) => name switch
    {
        "a" or "b" or "x" or "y" => name.ToUpperInvariant() + " button",
        "dpad_up" => "D-pad up",
        "dpad_down" => "D-pad down",
        "dpad_left" => "D-pad left",
        "dpad_right" => "D-pad right",
        "leftshoulder" => "left shoulder",
        "rightshoulder" => "right shoulder",
        "leftstick" => "left stick (press)",
        "rightstick" => "right stick (press)",
        _ => name.Replace('_', ' '),
    };
}

/// <summary>One input going down or up, as the capture layer saw it.</summary>
public readonly record struct RawInput(InputBinding Input, bool Down);

/// <summary>What a binding does. Each has one input at most; none means the player doesn't use it.</summary>
public enum BindingAction
{
    /// <summary>Held: broadcast to your own team.</summary>
    Broadcast,

    /// <summary>Held: broadcast to every team.</summary>
    AllTeams,

    /// <summary>Pressed: turns open mic on or off.</summary>
    OpenMic,
}

/// <summary>
/// The input for each <see cref="BindingAction"/>, or null where the player left it unset: that action
/// then never fires from input. No two actions share an input.
/// </summary>
public sealed record BindingSet(InputBinding? Broadcast, InputBinding? AllTeams, InputBinding? OpenMic)
{
    public static BindingSet Default { get; } = new(InputBinding.Key(KeyCode.VcF8), InputBinding.Key(KeyCode.VcF7), InputBinding.Key(KeyCode.VcF9));

    public static IReadOnlyList<BindingAction> Actions { get; } = [BindingAction.Broadcast, BindingAction.AllTeams, BindingAction.OpenMic];

    public InputBinding? this[BindingAction action] => action switch
    {
        BindingAction.Broadcast => Broadcast,
        BindingAction.AllTeams => AllTeams,
        BindingAction.OpenMic => OpenMic,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public BindingSet With(BindingAction action, InputBinding? input) => action switch
    {
        BindingAction.Broadcast => this with { Broadcast = input },
        BindingAction.AllTeams => this with { AllTeams = input },
        BindingAction.OpenMic => this with { OpenMic = input },
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>The action <paramref name="input"/> is bound to, or null when it is bound to none.</summary>
    public BindingAction? ActionOf(InputBinding input)
    {
        foreach (var action in Actions)
        {
            if (this[action] == input)
            {
                return action;
            }
        }
        return null;
    }

    /// <summary>Why this set can't be used, or null: no input that can't be bound, and no input for two actions.</summary>
    public string? Problem()
    {
        var bound = Actions.Select(action => this[action]).OfType<InputBinding>().ToList();
        return bound.Distinct().Count() != bound.Count
            ? "One input can't do two things: each broadcast needs an input of its own."
            : bound.Select(input => input.RefusalReason()).FirstOrDefault(reason => reason is not null);
    }

    public bool UsesPad => Actions.Any(action => this[action] is { Kind: InputKind.Pad });
}
