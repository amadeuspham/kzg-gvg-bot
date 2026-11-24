using System.Collections.Concurrent;
using System.Diagnostics;
using Kzg.Audio;
using Kzg.Core.Abstractions;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;
using NetCord.Logging;

namespace Kzg.Bot.Tests.Live;

/// <summary>One frame heard in a voice channel.</summary>
internal sealed record Heard(double At, ulong From, ulong Hash, int Length);

/// <summary>
/// A clip as 20 ms Opus frames, each with a hash of its bytes. A frame with no bytes is a pause: nothing is
/// sent for it, as Discord sends nothing while a player is quiet, but the sender's clock moves on.
/// </summary>
internal sealed record Clip(string Name, IReadOnlyList<(byte[] Data, ulong Hash)> Frames)
{
    public int Speech => Frames.Count(f => f.Data.Length > 0);

    public static Clip Load(string name, string rawFile, int seconds, int seed)
    {
        var raw = File.ReadAllBytes(rawFile);
        var pcm = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, pcm, 0, raw.Length);
        return FromPcm(name, pcm, seed, seconds * 50);
    }

    /// <summary>Interleaved 48 kHz stereo to Opus frames. Dither makes every frame encode to bytes of its own, so a frame can be told by its hash.</summary>
    public static Clip FromPcm(string name, short[] pcm, int seed, int maxFrames = int.MaxValue)
    {
        var encoder = new ConcentusOpusCodecFactory().Encoder(64_000);
        var random = new Random(seed);
        var buffer = new byte[4000];
        List<(byte[], ulong)> frames = [];
        for (var offset = 0; offset + OpusFormat.FrameShorts <= pcm.Length && frames.Count < maxFrames; offset += OpusFormat.FrameShorts)
        {
            var frame = pcm.AsSpan(offset, OpusFormat.FrameShorts).ToArray();
            for (var i = 0; i < frame.Length; i++)
            {
                frame[i] = (short)Math.Clamp(frame[i] + random.Next(-3, 4), short.MinValue, short.MaxValue);
            }
            var length = encoder.Encode(frame, buffer);
            var data = buffer.AsSpan(0, length).ToArray();
            frames.Add((data, Fnv(data)));
        }
        return new Clip(name, frames);
    }

    /// <summary>A tone that changes pitch as it goes: speech-like in length, never in content.</summary>
    public static Clip Tone(string name, double seconds, int baseHz, int seed)
    {
        var pcm = new short[(int)(seconds * 50) * OpusFormat.FrameShorts];
        for (var n = 0; n < pcm.Length / 2; n++)
        {
            var t = n / (double)OpusFormat.SampleRate;
            var sample = (short)(8000 * Math.Sin(2 * Math.PI * (baseHz + (60 * Math.Sin(2 * Math.PI * 1.3 * t))) * t));
            pcm[2 * n] = pcm[(2 * n) + 1] = sample;
        }
        return FromPcm(name, pcm, seed);
    }

    /// <summary>A click every 250 ms.</summary>
    public static Clip ClickTrain(string name, double seconds, int seed)
    {
        var pcm = new short[(int)(seconds * 50) * OpusFormat.FrameShorts];
        var random = new Random(seed);
        for (var n = 0; n < pcm.Length / 2; n++)
        {
            var inPeriod = n % (OpusFormat.SampleRate / 4);
            var click = inPeriod < 96 ? (int)(22000 * Math.Exp(-inPeriod / 24.0) * Math.Cos(inPeriod * 0.9)) : 0;
            pcm[2 * n] = pcm[(2 * n) + 1] = (short)Math.Clamp(click + random.Next(-40, 41), short.MinValue, short.MaxValue);
        }
        return FromPcm(name, pcm, seed + 1);
    }

    /// <summary>
    /// Speech made with macOS <c>say</c> (in <paramref name="voice"/>, or the system's), with a pause before and after. The frames that hold speech run from
    /// <paramref name="first"/> to <paramref name="last"/>; nothing is kept on disk.
    /// </summary>
    public static Clip Say(string name, string text, double lead, double tail, int seed, out int first, out int last, string? voice = null)
    {
        var dir = Directory.CreateTempSubdirectory("kzg-live-say-");
        try
        {
            var aiff = Path.Combine(dir.FullName, "say.aiff");
            var wav = Path.Combine(dir.FullName, "say.wav");
            Run("/usr/bin/say", voice is null ? ["-o", aiff, text] : ["-v", voice, "-o", aiff, text]);
            Run("/usr/bin/afconvert", "-f", "WAVE", "-d", "LEI16@48000", "-c", "2", aiff, wav);
            var bytes = File.ReadAllBytes(wav);
            var at = 12;
            while (at + 8 <= bytes.Length && !(bytes[at] == 'd' && bytes[at + 1] == 'a' && bytes[at + 2] == 't' && bytes[at + 3] == 'a'))
            {
                at += 8 + BitConverter.ToInt32(bytes, at + 4) + (BitConverter.ToInt32(bytes, at + 4) & 1);
            }
            var data = at + 8;
            var speech = new short[(bytes.Length - data) / 2];
            Buffer.BlockCopy(bytes, data, speech, 0, speech.Length * 2);
            // Whole frames, with the speech inside them found by its energy.
            var frames = speech.Length / OpusFormat.FrameShorts;
            speech = speech[..(frames * OpusFormat.FrameShorts)];
            var loud = Enumerable.Range(0, frames).Where(i => Rms(speech.AsSpan(i * OpusFormat.FrameShorts, OpusFormat.FrameShorts)) > 300).ToArray();
            var speechClip = FromPcm(name, speech, seed);
            var leadFrames = (int)(lead * 50);
            first = leadFrames + (loud.Length > 0 ? loud[0] : 0);
            last = leadFrames + (loud.Length > 0 ? loud[^1] : frames - 1);
            return Concat(name, Gap(lead), speechClip, Gap(tail));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    /// <summary>Quiet for <paramref name="seconds"/>: no packets.</summary>
    public static Clip Gap(double seconds) => new("gap", [.. Enumerable.Range(0, (int)Math.Round(seconds * 50)).Select(_ => (Array.Empty<byte>(), 0UL))]);

    public static Clip Concat(string name, params Clip[] parts) => new(name, [.. parts.SelectMany(p => p.Frames)]);

    /// <summary>The frame a hash is, or -1; frames of a pause have none.</summary>
    public Dictionary<ulong, int> IndexByHash()
    {
        Dictionary<ulong, int> index = [];
        for (var i = 0; i < Frames.Count; i++)
        {
            if (Frames[i].Data.Length > 0)
            {
                index[Frames[i].Hash] = i;
            }
        }
        return index;
    }

    public static ulong Fnv(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }
        return hash;
    }

    private static double Rms(ReadOnlySpan<short> pcm)
    {
        double sum = 0;
        foreach (var x in pcm)
        {
            sum += (double)x * x;
        }
        return Math.Sqrt(sum / pcm.Length);
    }

    /// <summary>
    /// Runs a tool to its end. macOS <c>say</c> can stall for good when the speech service wedges, which left a
    /// whole live run waiting; a stalled try is killed and made again, and the last one fails the run.
    /// </summary>
    private static void Run(string exe, params string[] args)
    {
        const int Tries = 3;
        for (var attempt = 1; ; attempt++)
        {
            var info = new ProcessStartInfo(exe) { RedirectStandardError = true };
            foreach (var arg in args)
            {
                info.ArgumentList.Add(arg);
            }
            using var process = Process.Start(info)!;
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(20)))
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit();
                if (attempt == Tries)
                {
                    throw new TimeoutException($"{Path.GetFileName(exe)} did not finish in 20 s, {Tries} times");
                }
                continue;
            }
            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException($"{exe} failed: {error.Result}");
            }
            return;
        }
    }
}

/// <summary>NetCord's log from a test bot, kept for the report: warnings and up only, with no token in it.</summary>
internal sealed class QuietLog(string bot, ConcurrentQueue<string> lines, Stopwatch? clock = null) : IGatewayLogger, IVoiceLogger
{
    public bool IsEnabled(NetCord.Logging.LogLevel level) => level >= NetCord.Logging.LogLevel.Warning;

    public void Log<TState>(NetCord.Logging.LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        if (level >= NetCord.Logging.LogLevel.Warning)
        {
            var message = formatter(state, exception);
            if (message.StartsWith("Failed to decrypt", StringComparison.Ordinal))
            {
                lines.Enqueue($"[{bot}] decrypt failure at {clock?.Elapsed.TotalMilliseconds:0} ms");
            }
            else
            {
                lines.Enqueue($"[{bot}] {message.Split('\n')[0]}");
            }
        }
    }
}

/// <summary>
/// A bot standing in for a person: it sits in one voice channel and either speaks a clip (a tester) or
/// records what it hears (a listener). It uses NetCord directly, as the spike did, so it checks the bot
/// under test against an independent client.
/// </summary>
internal sealed class LiveBot(string name, string token, ulong guildId, Stopwatch clock, ConcurrentQueue<string> warnings)
{
    private static readonly byte[] Silence = [0xF8, 0xFF, 0xFE];

    private GatewayClient? _gateway;
    private VoiceClient? _voice;
    private ushort _sequence;
    private uint _timestamp;
    private double? _streamAt;

    public string Name { get; } = name;

    public ulong Id { get; private set; }

    public ulong? Channel { get; private set; }

    public ConcurrentQueue<Heard> Heard { get; } = new();

    /// <summary>
    /// Whether the RTP timestamp keeps pace with the clock through a pause (true) or goes on from the last
    /// number (false). Real clients may do either; the bot must relay both.
    /// </summary>
    public bool TimestampFollowsClock { get; set; } = true;

    public long SilenceHeard;

    public long UnknownSsrc;

    public async Task ConnectAsync()
    {
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var inGuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gateway = new GatewayClient(new BotToken(token), new GatewayClientConfiguration
        {
            Intents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
            Logger = new QuietLog(Name, warnings),
        });
        _gateway.Ready += args =>
        {
            Id = args.User.Id;
            ready.TrySetResult();
            return default;
        };
        _gateway.GuildCreate += args =>
        {
            if (args.GuildId == guildId)
            {
                inGuild.TrySetResult();
            }
            return default;
        };
        await _gateway.StartAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            await inGuild.Task.WaitAsync(TimeSpan.FromSeconds(15));
        }
        catch (TimeoutException)
        {
            throw new InvalidOperationException($"{Name} never saw the test server: is it invited?");
        }
    }

    /// <summary>Leaves the voice channel for another, as a player moving between VCs: a new connection, with a stream of numbers of its own.</summary>
    public async Task MoveAsync(ulong channel)
    {
        if (_voice is { } old)
        {
            _voice = null;
            await old.CloseAsync();
            old.Dispose();
        }
        _streamAt = null;
        await JoinAsync(channel);
    }

    public async Task JoinAsync(ulong channel)
    {
        var voice = await _gateway!.JoinVoiceChannelAsync(guildId, channel, new VoiceClientConfiguration
        {
            Logger = new QuietLog(Name, warnings, clock),
            ExternalSocketAddressDiscoveryTimeout = TimeSpan.FromSeconds(10),
        }, timeout: TimeSpan.FromSeconds(20));
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        voice.Ready += () =>
        {
            ready.TrySetResult();
            return default;
        };
        voice.VoiceReceive += args =>
        {
            var at = clock.Elapsed.TotalMilliseconds;
            if (args.Frame.Length <= Silence.Length)
            {
                Interlocked.Increment(ref SilenceHeard);
                return default;
            }
            if (!voice.Cache.SsrcUsers.TryGetValue(args.Ssrc, out var from))
            {
                Interlocked.Increment(ref UnknownSsrc);
                return default;
            }
            Heard.Enqueue(new Heard(at, from, Clip.Fnv(args.Frame), args.Frame.Length));
            return default;
        };
        _voice = voice;
        Channel = channel;
        await voice.StartAsync();
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(20));
        await voice.EnterSpeakingStateAsync(new SpeakingProperties(SpeakingFlags.Microphone));
    }

    /// <summary>Speaks the clip in real time, one frame every 20 ms, then ends the burst; returns when each frame was sent.</summary>
    public Task<IReadOnlyDictionary<ulong, double>> SpeakAsync(Clip clip) => Task.Factory.StartNew<IReadOnlyDictionary<ulong, double>>(
        () =>
        {
            Dictionary<ulong, double> sentAt = [];
            // One RTP stream per bot, as a real client keeps: the timestamp moves with wall time between bursts,
            // never jumps, so the receivers' reorder logic sees a continuous stream.
            var start = clock.Elapsed.TotalMilliseconds;
            if (_streamAt is { } last)
            {
                if (TimestampFollowsClock)
                {
                    _timestamp += (uint)((start - last) / 20.0 * OpusFormat.FrameSamples);
                }
            }
            else
            {
                _sequence = (ushort)Random.Shared.Next();
                _timestamp = (uint)Random.Shared.NextInt64(uint.MaxValue);
            }
            var silent = 0;
            for (var i = 0; i < clip.Frames.Count; i++)
            {
                var due = start + (20.0 * i);
                while (clock.Elapsed.TotalMilliseconds < due)
                {
                    Thread.Sleep(1);
                }
                var at = clock.Elapsed.TotalMilliseconds;
                if (clip.Frames[i].Data.Length > 0)
                {
                    _voice!.SendVoice(_sequence++, _timestamp, clip.Frames[i].Data);
                    sentAt[clip.Frames[i].Hash] = at;
                    silent = 0;
                }
                else if (i > 0 && (clip.Frames[i - 1].Data.Length > 0 || silent is > 0 and < 5))
                {
                    // A client ends each talkspurt with a few silent frames, then says nothing.
                    _voice!.SendVoice(_sequence++, _timestamp, Silence);
                    silent++;
                }
                _timestamp += OpusFormat.FrameSamples;
            }
            for (var i = 0; i < 5 && silent < 5; i++, silent++)
            {
                Thread.Sleep(20);
                _voice!.SendVoice(_sequence++, _timestamp, Silence);
                _timestamp += OpusFormat.FrameSamples;
            }
            _streamAt = clock.Elapsed.TotalMilliseconds;
            return sentAt;
        },
        CancellationToken.None,
        TaskCreationOptions.LongRunning,
        TaskScheduler.Default);

    public async Task DisconnectAsync()
    {
        try
        {
            if (_voice is { } voice)
            {
                _voice = null;
                await voice.CloseAsync();
                voice.Dispose();
            }
            if (_gateway is { } gateway)
            {
                _gateway = null;
                await gateway.UpdateVoiceStateAsync(new VoiceStateProperties(guildId, null));
                await gateway.CloseAsync();
                gateway.Dispose();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            warnings.Enqueue($"[{Name}] disconnect: {exception.GetType().Name}");
        }
        Channel = null;
    }
}
