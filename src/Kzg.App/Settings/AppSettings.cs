using Kzg.App.Input;

namespace Kzg.App.Settings;

/// <summary>Everything the app remembers between runs. The device token is the only secret in it.</summary>
public sealed record AppSettings
{
    /// <summary>The server's address as <c>ConnectCode</c> writes a host: a name, IPv4 or bracketed IPv6, maybe with a port. Empty until known.</summary>
    public string ServerHost { get; init; } = "";

    public required Guid InstallId { get; init; }

    /// <summary>Set once the player has linked this PC; the app signs in with it from then on.</summary>
    public string? DeviceToken { get; init; }

    public BindingSet Bindings { get; init; } = BindingSet.Default;

    /// <summary>Play a sound and show a notification when the connection drops.</summary>
    public bool NotifyOnDisconnect { get; init; }

    public static AppSettings CreateDefault() => new()
    {
        InstallId = Guid.NewGuid(),
        ServerHost = AppInfo.DefaultHost,
    };
}
