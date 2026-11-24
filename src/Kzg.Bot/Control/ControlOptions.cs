using System.Net.WebSockets;
using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.Bot.Control;

/// <summary>The control server's timings and what its welcome says about the bot.</summary>
public sealed record ControlOptions
{
    /// <summary>How long a new connection has to send <c>hello</c>.</summary>
    public TimeSpan HelloDeadline { get; init; } = TimeSpan.FromMilliseconds(WireProtocol.HelloDeadlineMs);

    /// <summary>How long a signed-in app may stay silent; its heartbeat comes every 2 s.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMilliseconds(WireProtocol.TimeoutMs);

    /// <summary>The least time between two <c>status</c> messages to one app: 10 a second at most.</summary>
    public TimeSpan StatusInterval { get; init; } = TimeSpan.FromSeconds(1.0 / WireProtocol.StatusMaxPerSecond);

    /// <summary>How long a closing peer has to answer our close before the socket is dropped.</summary>
    public TimeSpan CloseGrace { get; init; } = TimeSpan.FromSeconds(2);

    public string ServerVersion { get; init; } = typeof(ControlOptions).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
}

/// <summary>
/// The close codes the bot ends a connection with. The app reconnects after any of them except
/// <see cref="Replaced"/>, and after an <c>error</c> it acts on the error's code.
/// </summary>
public static class CloseCodes
{
    /// <summary>The app unlinked, or the app closed first.</summary>
    public const WebSocketCloseStatus Normal = WebSocketCloseStatus.NormalClosure;

    /// <summary>The bot is shutting down.</summary>
    public const WebSocketCloseStatus GoingAway = WebSocketCloseStatus.EndpointUnavailable;

    /// <summary>A binary message: the protocol is JSON text only.</summary>
    public const WebSocketCloseStatus Binary = WebSocketCloseStatus.InvalidMessageType;

    /// <summary>A message that isn't valid JSON, isn't a known app-to-bot type, or breaks its rules.</summary>
    public const WebSocketCloseStatus Malformed = WebSocketCloseStatus.InvalidPayloadData;

    /// <summary>
    /// Against the protocol's order or timing: no hello in time, a second hello, silence past the
    /// timeout. Also after an <c>error</c> refusing a sign-in or ending one, such as a revoke.
    /// </summary>
    public const WebSocketCloseStatus Policy = WebSocketCloseStatus.PolicyViolation;

    /// <summary>A message over 4096 bytes.</summary>
    public const WebSocketCloseStatus TooLarge = WebSocketCloseStatus.MessageTooBig;

    /// <summary>The bot couldn't check the sign-in, as when Discord is unreachable; try again.</summary>
    public const WebSocketCloseStatus ServerError = WebSocketCloseStatus.InternalServerError;

    /// <summary>The same player signed in from another app; this one must not reconnect by itself.</summary>
    public const WebSocketCloseStatus Replaced = (WebSocketCloseStatus)4000;
}

/// <summary>
/// What the engine hears from the apps, besides their edges. Called on connection threads: the
/// handler only enqueues for the engine thread, which feeds <c>RoutingEngine</c>.
/// </summary>
public interface IAppEvents
{
    /// <summary>A player's app signed in; any older app of theirs was closed first.</summary>
    void Connected(UserId user, TimeSpan at);

    void Disconnected(UserId user);

    void Heartbeat(UserId user, IReadOnlyList<BroadcastAction> held, bool openMic, TimeSpan at);
}
