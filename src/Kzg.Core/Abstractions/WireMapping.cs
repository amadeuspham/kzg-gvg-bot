using Kzg.Protocol;

namespace Kzg.Core.Abstractions;

/// <summary>Converts between the engine's types and the wire's, which Kzg.Protocol keeps free of Core.</summary>
public static class WireMapping
{
    public static WireGroup ToWire(this Group group) => group switch
    {
        Group.Attack => WireGroup.Attack,
        Group.Defense => WireGroup.Defense,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    public static Group ToCore(this WireGroup group) => group switch
    {
        WireGroup.Attack => Group.Attack,
        WireGroup.Defense => Group.Defense,
        _ => throw new ArgumentOutOfRangeException(nameof(group)),
    };

    public static WireAction ToWire(this BroadcastAction action) => action switch
    {
        BroadcastAction.Group => WireAction.Group,
        BroadcastAction.AllTeams => WireAction.AllTeams,
        BroadcastAction.OpenMic => WireAction.OpenMic,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public static BroadcastAction ToCore(this WireAction action) => action switch
    {
        WireAction.Group => BroadcastAction.Group,
        WireAction.AllTeams => BroadcastAction.AllTeams,
        WireAction.OpenMic => BroadcastAction.OpenMic,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>The snowflake string; throws for the default (0) ID, which no Discord user has.</summary>
    public static string ToWire(this UserId user) => Snowflake.Format(user.V);

    public static string ToWire(this ChannelId channel) => Snowflake.Format(channel.V);
}
