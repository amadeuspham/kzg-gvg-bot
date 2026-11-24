using System.Text;

namespace Kzg.Protocol.Tests;

/// <summary>Valid values for building messages. Secrets come from fixed bytes, never literals in source.</summary>
internal static class Samples
{
    public static readonly Guid InstallId = new("0f8fad5b-d9cb-469f-a165-70867728950e");

    public static string Secret(byte seed = 1)
    {
        var bytes = new byte[WireSecret.ByteLength];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(seed + i * 7);
        }
        return WireSecret.Encode(bytes);
    }

    public static string Code(string host = "gvg.example.com") => ConnectCode.Generate(host).Format();

    public static byte[] Utf8(string json) => Encoding.UTF8.GetBytes(json);

    public static string Json(KzgMessage message) => Encoding.UTF8.GetString(MessageCodec.Serialize(message));

    public static IEnumerable<ClientMessage> ClientMessages() =>
    [
        new HelloMessage("0.1.0", InstallId, Code: Code()),
        new HelloMessage("0.1.0", InstallId, DeviceToken: Secret()),
        new HelloMessage("0.1.0", InstallId, LoginId: Secret(9)),
        new PressMessage(WireAction.Group, true, 0),
        new PressMessage(WireAction.Group, false, long.MaxValue),
        new PressMessage(WireAction.AllTeams, true, 2),
        new OpenMicMessage(true, 3),
        new HeartbeatMessage(4, [WireAction.Group], true, 1_700_000_000_000),
        new HeartbeatMessage(6, [WireAction.Group, WireAction.AllTeams], false, 1),
        new HeartbeatMessage(5, [], false, 0),
        new UnlinkMessage(),
    ];

    public static IEnumerable<ServerMessage> ServerMessages() =>
    [
        new WelcomeMessage("123456789012345678", "Тимур", 2000, 6000, "0.1.0", Secret(3)),
        new WelcomeMessage("18446744073709551615", "Ann", 2000, 6000, "0.1.0"),
        new StatusMessage([WireAction.Group, WireAction.OpenMic], true, ["Attack 2", "Attack 3"], [], "Attack 1", WireGroup.Attack),
        new StatusMessage([WireAction.AllTeams], false, ["Attack 2", "Defense 1"], [], "Attack 1", WireGroup.Attack),
        new StatusMessage([], false, [], [StatusWarning.NotInTeamVc, StatusWarning.BotDown], "Lobby"),
        new StatusMessage([], true, [], [StatusWarning.NotInTeamVc]),
        new StatusMessage([], false, ["Attack 3"], [StatusWarning.BotDown], "Attack 1", WireGroup.Attack, ["Attack 2"]),
        new StatusMessage([], false, ["Attack 2", "Attack 3"], [], "Attack 1", WireGroup.Attack, []),
        new HeartbeatAckMessage(4, 1_700_000_000_000),
        new ErrorMessage(ErrorCode.Expired, "Link expired. Run /connect or sign in again."),
        new ErrorMessage(ErrorCode.BadCode, ""),
        new ErrorMessage(ErrorCode.Used, "used"),
        new ErrorMessage(ErrorCode.Revoked, "revoked"),
        new ErrorMessage(ErrorCode.NotMember, "not a member"),
        new ErrorMessage(ErrorCode.VersionMismatch, "update the app"),
    ];
}
