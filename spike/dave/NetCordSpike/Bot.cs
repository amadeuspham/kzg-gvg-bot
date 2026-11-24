using System.Reflection;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;

namespace Kzg.Spike.Dave;

public delegate void FrameHandler(Bot bot, uint ssrc, ushort seq, uint ts, ReadOnlySpan<byte> frame);

/// <summary>One bot account: its own gateway connection and, while in voice, its own voice client.</summary>
public sealed class Bot(string name)
{
    public static readonly byte[] Silence = [0xF8, 0xFF, 0xFE];

    public string Name { get; } = name;
    public GatewayClient Gateway { get; private set; } = null!;
    public ulong Id => Gateway.Id;
    public Guild Guild { get; private set; } = null!;
    public VoiceClient? Voice { get; private set; }
    public ulong? ChannelId { get; private set; }

    /// <summary>True between the voice client's Ready and a leave; senders only send while it is.</summary>
    public volatile bool Ready;

    public readonly KillableWebSocketProvider GatewaySocket = new();
    public readonly KillableWebSocketProvider VoiceSocket = new(name);
    public readonly BlackholeUdpProvider Udp = new();

    public event FrameHandler? Frame;

    public ushort Seq;
    public uint Ts;
    public long SendErrors;
    public long Sent;
    public int VoiceReadyCount;
    public int VoiceResumeCount;
    public int VoiceDisconnectCount;
    public double LastReadyMs;

    public async Task StartGatewayAsync(string token, string guildName, TimeSpan timeout)
    {
        TaskCompletionSource<Guild> found = new(TaskCreationOptions.RunContinuationsAsynchronously);
        List<string> seen = [];
        Gateway = new GatewayClient(new BotToken(token), new GatewayClientConfiguration
        {
            Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
            Logger = new NetLogger(Name, "gateway"),
            WebSocketConnectionProvider = GatewaySocket,
        });
        Gateway.GuildCreate += args =>
        {
            if (args.Guild is { } guild)
            {
                lock (seen)
                {
                    seen.Add(guild.Name);
                }
                if (guild.Name == guildName)
                {
                    found.TrySetResult(guild);
                }
            }
            return default;
        };
        await Gateway.StartAsync();
        try
        {
            Guild = await found.Task.WaitAsync(timeout);
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"{Name}: guild '{guildName}' not seen; guilds seen: [{string.Join(", ", seen)}]");
        }
    }

    public async Task JoinAsync(ulong channelId, TimeSpan timeout)
    {
        Ready = false;
        var voice = await Gateway.JoinVoiceChannelAsync(Guild.Id, channelId, new VoiceClientConfiguration
        {
            Logger = new NetLogger(Name, "voice"),
            WebSocketConnectionProvider = VoiceSocket,
            UdpConnectionProvider = Udp,
            ExternalSocketAddressDiscoveryTimeout = TimeSpan.FromSeconds(10),
        }, timeout: timeout);
        TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        voice.Ready += () =>
        {
            VoiceReadyCount++;
            LastReadyMs = Clock.Ms;
            Log.Info($"[{Name}] voice Ready, ssrc={voice.Cache.Ssrc}, dave v{DaveVersion(voice)}");
            ready.TrySetResult();
            return default;
        };
        voice.Resume += () =>
        {
            VoiceResumeCount++;
            Log.Info($"[{Name}] voice Resumed, dave v{DaveVersion(voice)}");
            return default;
        };
        voice.Disconnect += args =>
        {
            VoiceDisconnectCount++;
            Log.Info($"[{Name}] voice Disconnect status={args.CloseStatus} reconnect={args.Reconnect} '{args.CloseStatusDescription}'");
            return default;
        };
        voice.UserConnect += args =>
        {
            Log.Info($"[{Name}] voice UserConnect [{string.Join(",", args.UserIds)}]");
            return default;
        };
        voice.UserDisconnect += args =>
        {
            Log.Info($"[{Name}] voice UserDisconnect {args.UserId}");
            return default;
        };
        voice.Speaking += args =>
        {
            Log.Info($"[{Name}] voice Speaking user={args.UserId} ssrc={args.Ssrc} flags={args.Speaking}", console: false);
            return default;
        };
        voice.VoiceReceive += args =>
        {
            Frame?.Invoke(this, args.Ssrc, args.SequenceNumber, args.Timestamp, args.Frame);
            return default;
        };
        Voice = voice;
        ChannelId = channelId;
        await voice.StartAsync();
        await ready.Task.WaitAsync(timeout);
        await voice.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
        Ready = true;
    }

    public async Task LeaveAsync()
    {
        Ready = false;
        var voice = Voice;
        Voice = null;
        if (voice is not null)
        {
            try
            {
                await voice.CloseAsync();
            }
            catch (Exception exception)
            {
                Log.Info($"[{Name}] voice close: {exception.GetType().Name}");
            }
            voice.Dispose();
        }
        if (ChannelId is not null)
        {
            ChannelId = null;
            await Gateway.UpdateVoiceStateAsync(new VoiceStateProperties(Guild.Id, null));
        }
    }

    /// <summary>Sends one frame at the current RTP position. Returns false when the client can't send.</summary>
    public bool Send(ReadOnlySpan<byte> opus)
    {
        if (!Ready || Voice is not { } voice)
        {
            return false;
        }
        try
        {
            voice.SendVoice(Seq++, Ts, opus);
            Sent++;
            return true;
        }
        catch (Exception exception)
        {
            if (Interlocked.Increment(ref SendErrors) <= 3)
            {
                Log.Warn($"[{Name}] send failed: {exception.GetType().Name}: {exception.Message}");
            }
            return false;
        }
    }

    /// <summary>Advances the RTP clock one 20 ms frame, sent or not, as a real sender does.</summary>
    public void Tick() => Ts += 960;

    public int DaveVersion() => Voice is { } voice ? DaveVersion(voice) : -1;

    // NetCord keeps the DAVE session internal; the spike reads its protocol version to prove E2EE is on.
    private static readonly FieldInfo? UdpStateField = typeof(VoiceClient).GetField("_udpState", BindingFlags.Instance | BindingFlags.NonPublic);

    public static int DaveVersion(VoiceClient voice)
    {
        var udp = UdpStateField?.GetValue(voice);
        var session = udp?.GetType().GetProperty("DaveSession")?.GetValue(udp);
        var version = session?.GetType().GetMethod("GetProtocolVersion")?.Invoke(session, null);
        return version is ushort v ? v : -1;
    }

    public async Task StopAsync()
    {
        try
        {
            await LeaveAsync();
        }
        catch (Exception exception)
        {
            Log.Info($"[{Name}] leave: {exception.GetType().Name}");
        }
        try
        {
            await Gateway.CloseAsync();
        }
        catch (Exception exception)
        {
            Log.Info($"[{Name}] gateway close: {exception.GetType().Name}");
        }
        Gateway.Dispose();
    }
}
