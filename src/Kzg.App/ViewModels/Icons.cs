namespace Kzg.App.ViewModels;

/// <summary>
/// The glyphs the window draws from the embedded Tabler Icons font (outline, MIT; see Assets/Fonts). Only
/// these are in the font, so a new icon means adding it to that file first.
/// </summary>
public static class Icons
{
    public const string CircleCheck = "";
    public const string Broadcast = "";
    public const string Microphone = "";
    public const string Refresh = "";
    public const string PlugConnectedX = "";
    public const string LinkOff = "";
    public const string Link = "";
    public const string BrandDiscord = "";
    public const string Keyboard = "";
    public const string Mouse = "";
    public const string DeviceGamepad2 = "\uF1D2";
    public const string Settings = "";
    public const string Power = "";
    public const string AlertTriangle = "";
    public const string Server = "";
    public const string ChevronDown = "";
    public const string ChevronLeft = "\uEA60";
    public const string ChevronUp = "";
    public const string Unlink = "";
}

/// <summary>The color family of the badge and the tinted panels.</summary>
public enum Tone
{
    Green,
    Blue,
    Amber,
    Red,
}

/// <summary>
/// A note for the window: <see cref="IsError"/> paints it red, <see cref="IsInfo"/> blue (good news that wants a click,
/// such as a waiting update), otherwise amber. It may carry one action button.
/// </summary>
public sealed record WarningNote(
    string Text,
    bool IsError,
    string? ActionLabel = null,
    System.Windows.Input.ICommand? Action = null,
    bool IsInfo = false)
{
    public bool IsAmber => !IsError && !IsInfo;

    public string Icon => IsInfo ? Icons.Refresh : Icons.AlertTriangle;
}
