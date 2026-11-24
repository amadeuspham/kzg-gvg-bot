namespace Kzg.Protocol;

/// <summary>The fixed numbers of the <c>/ws</c> protocol, shared by the bot and the app.</summary>
public static class WireProtocol
{
    /// <summary>The <c>v</c> every message carries. A message with any other value is refused whole.</summary>
    public const int Version = 2;

    /// <summary>The largest message either side accepts or sends, in UTF-8 bytes.</summary>
    public const int MaxMessageBytes = 4096;

    /// <summary>How often the app sends a heartbeat, as the bot announces it in <c>welcome</c>.</summary>
    public const int HeartbeatMs = 2000;

    /// <summary>How long without a heartbeat before the bot ends that app's sessions.</summary>
    public const int TimeoutMs = 6000;

    /// <summary>How long the bot waits for <c>hello</c> after the WebSocket opens.</summary>
    public const int HelloDeadlineMs = 5000;

    /// <summary>The most <c>status</c> messages the bot sends one app per second.</summary>
    public const int StatusMaxPerSecond = 10;

    /// <summary>The actions that are held keys, which <c>press</c> and <c>heartbeat.held</c> carry; open mic is a latch.</summary>
    public static bool IsKey(WireAction action) => action is WireAction.Group or WireAction.AllTeams;
}
