using Kzg.Core.Abstractions;
using NetCord.Gateway;
using NetCord.Gateway.Voice;

namespace Kzg.Discord.Voice;

/// <summary>NetCord's voice client behind <see cref="IVoiceConnection"/>.</summary>
internal sealed class NetCordVoiceConnection : IVoiceConnection
{
    private readonly VoiceClient _voice;
    private readonly TaskCompletionSource _lost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _firstReady = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _ready;
    private Action? _detach;

    private NetCordVoiceConnection(VoiceClient voice)
    {
        _voice = voice;
        voice.VoiceReceive += args =>
        {
            // The frame's buffer is the library's and is reused once this returns; RelayLink copies it.
            Frame?.Invoke(args.Ssrc, args.SequenceNumber, args.Timestamp, args.Frame);
            return default;
        };
        voice.UserConnect += _ =>
        {
            UserJoined?.Invoke();
            return default;
        };
        voice.Ready += () =>
        {
            _ready = true;
            _firstReady.TrySetResult();
            Ready?.Invoke();
            return default;
        };
        voice.Resume += () =>
        {
            _ready = true;
            Resumed?.Invoke();
            return default;
        };
        voice.Disconnect += args =>
        {
            _ready = false;
            Interrupted?.Invoke();
            if (!args.Reconnect)
            {
                _lost.TrySetResult();
            }
            return default;
        };
    }

    public event FrameReceived? Frame;

    public event Action? UserJoined;

    public event Action? Ready;

    public event Action? Resumed;

    public event Action? Interrupted;

    public event Action<ChannelId?>? ChannelChanged;

    public bool IsReady => _ready;

    public Task Lost => _lost.Task;

    /// <summary>Starts the client and waits for its media path; the connection is closed again if that fails.</summary>
    /// <param name="watch">Hooks the bot's own voice-state updates to <see cref="ChannelChanged"/>; returns what unhooks them.</param>
    public static async Task<NetCordVoiceConnection> StartAsync(VoiceClient voice, TimeSpan timeout, CancellationToken cancellationToken, Func<Action<ChannelId?>, Action>? watch = null)
    {
        var connection = new NetCordVoiceConnection(voice);
        try
        {
            connection._detach = watch?.Invoke(channel => connection.ChannelChanged?.Invoke(channel));
            await voice.StartAsync(cancellationToken).ConfigureAwait(false);
            await connection._firstReady.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    public bool TryGetUser(uint ssrc, out ulong user) => _voice.Cache.SsrcUsers.TryGetValue(ssrc, out user);

    public void Send(ushort sequence, uint timestamp, ReadOnlySpan<byte> opus) => _voice.SendVoice(sequence, timestamp, opus);

    public async Task SetSpeakingAsync(bool on) =>
        await _voice.EnterSpeakingStateAsync(new SpeakingProperties(on ? SpeakingFlags.Microphone : 0)).ConfigureAwait(false);

    public async Task CloseAsync()
    {
        try
        {
            await _voice.CloseAsync().ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Already closed.
        }
    }

    public void Dispose()
    {
        _detach?.Invoke();
        _lost.TrySetResult();
        _voice.Dispose();
    }
}

/// <summary>Joins voice channels with one bot's gateway connection.</summary>
/// <param name="botUserId">The bot's own user, so its voice-state updates can be told from everyone's; 0 watches none.</param>
internal sealed class NetCordJoiner(GatewayClient gateway, ulong guildId, Func<VoiceClientConfiguration> configuration, ulong botUserId = 0) : IVoiceJoiner
{
    public static readonly TimeSpan JoinTimeout = TimeSpan.FromSeconds(20);

    public async Task<IVoiceConnection> JoinAsync(ChannelId channel, CancellationToken cancellationToken)
    {
        var voice = await gateway.JoinVoiceChannelAsync(guildId, channel.V, configuration(), JoinTimeout, cancellationToken: cancellationToken).ConfigureAwait(false);
        return await NetCordVoiceConnection.StartAsync(voice, JoinTimeout, cancellationToken, WatchOwnChannel).ConfigureAwait(false);
    }

    private Action WatchOwnChannel(Action<ChannelId?> changed)
    {
        if (botUserId == 0)
        {
            return () => { };
        }
        ValueTask Handle(VoiceState state)
        {
            if (state.GuildId == guildId && state.UserId == botUserId)
            {
                changed(state.ChannelId is { } channel ? new ChannelId(channel) : null);
            }
            return default;
        }
        gateway.VoiceStateUpdate += Handle;
        return () => gateway.VoiceStateUpdate -= Handle;
    }

    public async Task LeaveAsync(CancellationToken cancellationToken) =>
        await gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null), cancellationToken: cancellationToken).ConfigureAwait(false);
}
