using System.Diagnostics;
using Kzg.Audio;
using Kzg.Bot.Discord;
using Kzg.Bot.Fake;
using Kzg.Bot.Logging;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Testing;
using Kzg.Discord.Commands;
using Kzg.Protocol;
using Xunit.Abstractions;

namespace Kzg.Bot.Tests.Hosting;

// The plan's verification scenarios 2, 3, 4, 5 and 7 with no Discord: the bot host in real time over the fake
// gateway, an app on a real loopback WebSocket, audio fed in by the fake links. The live twins of these sit in
// Live/LiveScenarioTests; what differs is only who carries the audio.
[Collection("real time")]
public sealed class OfflineScenarioTests(ITestOutputHelper output)
{
    private static readonly ConcentusOpusCodecFactory Codecs = new();

    private static readonly ChannelId[] Attack = [DemoWorld.Attack1, DemoWorld.Attack2, DemoWorld.Attack3];
    private static readonly ChannelId[] Defense = [DemoWorld.Defense1, DemoWorld.Defense2, DemoWorld.Defense3];
    private static readonly ChannelId[] All = [.. Attack, .. Defense];

    // A player for every VC.
    private static readonly (UserId User, ChannelId Channel)[] Seats =
    [
        (DemoWorld.Alice, DemoWorld.Attack1), (DemoWorld.Bram, DemoWorld.Attack2), (DemoWorld.Chen, DemoWorld.Attack3),
        (DemoWorld.Eve, DemoWorld.Defense1), (DemoWorld.Finn, DemoWorld.Defense2), (DemoWorld.Dana, DemoWorld.Defense3),
    ];

    private static ChannelId[] GroupOf(ChannelId channel) => Attack.Contains(channel) ? Attack : Defense;

    private static HostRig Seated(HostRig rig)
    {
        rig.World.Gateway.Join(DemoWorld.Dana, DemoWorld.Defense3);
        return rig;
    }

    private static FakeVoiceChannelLink Link(HostRig rig, ChannelId channel) => rig.World.Gateway.Link(channel);

    private static List<int> Relayed(FakeVoiceChannelLink link, Dictionary<string, int> index) =>
        [.. link.Sent.Select(frame => SpeechAudio.IndexOf(index, frame.Opus))];

    private static void ClearGroup(HostRig rig, ChannelId[] group)
    {
        foreach (var channel in group)
        {
            Link(rig, channel).ClearSent();
        }
    }

    // ---- who hears what

    /// <summary>
    /// After one speaker's broadcast: every link of <paramref name="reach"/> (their group unless given) but
    /// their own carries their clip, and no other link carries any of it. Returns what went wrong, and one
    /// line of numbers per link.
    /// </summary>
    private static (List<string> Wrong, List<string> Numbers) Judge(HostRig rig, string label, ChannelId from, bool relayed, IReadOnlyList<byte[]> clip, double share, IReadOnlyList<byte[]>? other = null, ChannelId[]? reach = null)
    {
        var index = SpeechAudio.Index(clip);
        // Two clips may share a frame, silence above all; such a frame says nothing about who it came from.
        foreach (var shared in other?.Select(Convert.ToBase64String) ?? [])
        {
            index[shared] = SpeechAudio.Ambiguous;
        }
        var group = reach ?? GroupOf(from);
        List<string> wrong = [];
        List<string> numbers = [];
        foreach (var channel in All)
        {
            var frames = Relayed(Link(rig, channel), index);
            var mine = frames.Count(i => i >= 0);
            var unknown = frames.Count(i => i == -1);
            var expect = relayed && group.Contains(channel) && channel != from;
            numbers.Add($"{rig.World.Names.NameOf(channel)}: {mine}/{clip.Count}");
            if (expect)
            {
                if (mine < clip.Count * share)
                {
                    wrong.Add($"{label}: {rig.World.Names.NameOf(channel)} heard {mine} of {clip.Count} frames");
                }
                if (unknown > 0)
                {
                    wrong.Add($"{label}: {rig.World.Names.NameOf(channel)} heard {unknown} frames that are not the speaker's");
                }
            }
            else if (mine > 0)
            {
                wrong.Add($"{label}: {rig.World.Names.NameOf(channel)} heard {mine} frames it must not");
            }
        }
        return (wrong, numbers);
    }

    private readonly Dictionary<UserId, TimeSpan> _lastPacket = [];

    // A client's RTP timestamp follows its clock through silence, as the bot's own does: a player who speaks
    // again after a pause sends a timestamp that many frames on, not the next one.
    private async Task PlayAsync(HostRig rig, UserId user, ChannelId channel, IReadOnlyList<byte[]> clip)
    {
        var link = Link(rig, channel);
        TimeSpan? last;
        lock (_lastPacket)
        {
            last = _lastPacket.TryGetValue(user, out var at) ? at : null;
        }
        if (last is { } previous)
        {
            link.Pause(user, (int)((rig.Time.Now - previous).TotalMilliseconds / OpusFormat.FrameMs));
        }
        await link.PlayAsync(user, clip);
        lock (_lastPacket)
        {
            _lastPacket[user] = rig.Time.Now;
        }
    }

    private static async Task<TestApp> AppAsync(HostRig rig, UserId user)
    {
        var (client, _) = await rig.SignInAsync(user);
        var app = new TestApp(client, rig.Time);
        await app.WaitAsync(s => s.ChannelName is not null);
        return app;
    }

    private async Task<(List<string> Wrong, List<string> Rows)> MatrixAsync(HostRig rig, string action, ChannelId[] group, IReadOnlyList<byte[]> clip, Dictionary<UserId, TestApp> apps, Func<UserId, ChannelId, bool> allowed)
    {
        List<string> wrong = [];
        List<string> rows = [];
        foreach (var channel in group)
        {
            var started = Stopwatch.StartNew();
            var (user, _) = Seats.First(s => s.Channel == channel);
            var app = apps[user];
            await app.WaitAsync(s => s.ChannelName == rig.World.Names.NameOf(channel));
            var ok = allowed(user, channel);
            switch (action)
            {
                case "group key":
                    await app.PressAsync(true);
                    await app.WaitAsync(s => s.Active.Contains(WireAction.Group));
                    break;
                case "all teams":
                    await app.PressAllTeamsAsync(true);
                    await app.WaitAsync(s => s.Active.Contains(WireAction.AllTeams));
                    break;
                case "open mic":
                    await app.OpenMicAsync(true);
                    await app.WaitAsync(s => s.OpenMic);
                    break;
            }
            await PlayAsync(rig, user, channel, clip);
            await Task.Delay(300);
            switch (action)
            {
                case "group key":
                    await app.PressAsync(false);
                    break;
                case "all teams":
                    await app.PressAllTeamsAsync(false);
                    break;
                case "open mic":
                    await app.OpenMicAsync(false);
                    break;
            }
            await Task.Delay(500);
            var reach = action == "all teams" ? All : null;
            var (w, n) = Judge(rig, $"{action} from {rig.World.Names.NameOf(channel)}", channel, ok, clip, 0.9, reach: reach);
            wrong.AddRange(w);
            rows.Add($"{action,-9} from {rig.World.Names.NameOf(channel),-9} by {Name(user),-5}: {string.Join("  ", n)}  ({started.ElapsedMilliseconds} ms)");
            ClearGroup(rig, reach ?? group);
        }
        return (wrong, rows);
    }

    private static string Name(UserId user) => DemoWorld.Members[user].Split(' ')[0];

    [Fact]
    public async Task Every_key_and_every_open_mic_from_every_VC_reaches_where_it_should_and_nothing_else()
    {
        await using var rig = Seated(await new HostRig().StartAsync());
        Dictionary<UserId, TestApp> apps = [];
        foreach (var (user, _) in Seats)
        {
            apps[user] = await AppAsync(rig, user);
        }
        try
        {
            var attackClip = SpeechAudio.Tone(Codecs, 25, 300);
            var defenseClip = SpeechAudio.Tone(Codecs, 25, 700);
            List<string> wrong = [];
            List<string> rows = [];

            // Both teams broadcast at once, so a leak between them would show.
            foreach (var action in new[] { "group key", "open mic" })
            {
                var attack = MatrixAsync(rig, action, Attack, attackClip, apps, (_, _) => true);
                var defense = MatrixAsync(rig, action, Defense, defenseClip, apps, (_, _) => true);
                foreach (var (w, r) in await Task.WhenAll(attack, defense))
                {
                    wrong.AddRange(w);
                    rows.AddRange(r);
                }
            }

            // The all-teams key reaches both teams, so one VC at a time: two at once would hear each other.
            var (allWrong, allRows) = await MatrixAsync(rig, "all teams", All, attackClip, apps, (_, _) => true);
            wrong.AddRange(allWrong);
            rows.AddRange(allRows);

            foreach (var row in rows)
            {
                output.WriteLine(row);
            }
            Assert.Empty(wrong);
        }
        finally
        {
            foreach (var app in apps.Values)
            {
                await app.DisposeAsync();
            }
        }
    }

    // ---- latency

    [Fact]
    public async Task A_click_train_reaches_the_other_VCs_with_a_p95_added_latency_of_300_ms_or_less()
    {
        await using var rig = Seated(await new HostRig().StartAsync());
        await using var app = await AppAsync(rig, DemoWorld.Alice);
        var train = SpeechAudio.ClickTrain(Codecs, 250);
        var index = SpeechAudio.Index(train);
        var arrival = new Dictionary<int, TimeSpan>();

        await app.PressAsync(true);
        await app.WaitAsync(s => s.Active.Contains(WireAction.Group));
        var link = Link(rig, DemoWorld.Attack1);
        var start = rig.Time.Now;
        for (var i = 0; i < train.Count; i++)
        {
            var due = start + TimeSpan.FromMilliseconds(OpusFormat.FrameMs * i);
            for (var wait = due - rig.Time.Now; wait > TimeSpan.Zero; wait = due - rig.Time.Now)
            {
                await Task.Delay(wait);
            }
            arrival[i] = link.Deliver(DemoWorld.Alice, train[i]).Arrival;
        }
        await Task.Delay(600);
        await app.PressAsync(false);

        List<double> added = [];
        foreach (var channel in new[] { DemoWorld.Attack2, DemoWorld.Attack3 })
        {
            foreach (var frame in Link(rig, channel).Sent)
            {
                var i = SpeechAudio.IndexOf(index, frame.Opus);
                if (i >= 0)
                {
                    added.Add((frame.At - arrival[i]).TotalMilliseconds);
                }
            }
        }
        added.Sort();
        var p50 = added[added.Count / 2];
        var p95 = added[(int)(added.Count * 0.95)];
        output.WriteLine($"click train: {added.Count} frames relayed of {train.Count * 2}; added latency p50 {p50:0} ms, p95 {p95:0} ms, max {added[^1]:0} ms");
        Assert.True(added.Count >= train.Count * 2 * 0.95);
        Assert.True(p95 <= 300, $"p95 {p95:0} ms");
    }

    // ---- pre-roll

    [Fact]
    public async Task A_key_pressed_200_ms_after_speech_began_still_delivers_the_first_word_whole()
    {
        await using var rig = Seated(await new HostRig().StartAsync());
        await using var app = await AppAsync(rig, DemoWorld.Alice);
        var tone = SpeechAudio.Tone(Codecs, 60);
        var index = SpeechAudio.Index(tone);

        var talking = PlayAsync(rig, DemoWorld.Alice, DemoWorld.Attack1, tone);
        await Task.Delay(200);
        await app.PressAsync(true);
        await talking;
        await Task.Delay(300);
        await app.PressAsync(false);
        await Task.Delay(500);

        foreach (var channel in new[] { DemoWorld.Attack2, DemoWorld.Attack3 })
        {
            var frames = Relayed(Link(rig, channel), index);
            var firstWord = Enumerable.Range(0, 25).Count(i => !frames.Contains(i));
            output.WriteLine($"{rig.World.Names.NameOf(channel)}: first frame {frames[0]}, {frames.Count} of 60, first 500 ms missing {firstWord} frames");
            Assert.Equal(0, frames[0]);
            Assert.Equal(0, firstWord);
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_second_broadcast_after_a_long_pause_is_relayed_whole_whichever_way_the_client_numbers_it(bool timestampFollowsClock)
    {
        await using var rig = Seated(await new HostRig().StartAsync());
        await using var app = await AppAsync(rig, DemoWorld.Alice);
        var link = Link(rig, DemoWorld.Attack1);
        var first = SpeechAudio.Tone(Codecs, 40, 300);
        var second = SpeechAudio.Tone(Codecs, 40, 500);
        var index = SpeechAudio.Index(second);

        await app.PressAsync(true);
        await app.WaitAsync(s => s.Active.Contains(WireAction.Group));
        await link.PlayAsync(DemoWorld.Alice, first);
        await Task.Delay(400);
        await app.PressAsync(false);
        await Task.Delay(3000);
        Link(rig, DemoWorld.Attack2).ClearSent();
        if (timestampFollowsClock)
        {
            link.Pause(DemoWorld.Alice, 3400 / OpusFormat.FrameMs);
        }
        await app.PressAsync(true);
        await app.WaitAsync(s => s.Active.Contains(WireAction.Group));
        await link.PlayAsync(DemoWorld.Alice, second);
        await Task.Delay(500);
        await app.PressAsync(false);
        await Task.Delay(600);

        var frames = Relayed(Link(rig, DemoWorld.Attack2), index);
        output.WriteLine($"timestamp {(timestampFollowsClock ? "follows the clock" : "goes on from the last number")}: {frames.Count(i => i >= 0)} of {second.Count} frames of the second broadcast reached Attack 2");
        Assert.True(frames.Count(i => i >= 0) >= second.Count * 0.95);
        Assert.DoesNotContain(-1, frames);
    }

    // ---- logs

    [Fact]
    public async Task A_whole_session_of_signing_in_broadcasting_and_reconnecting_leaves_no_secret_in_the_log()
    {
        await using var rig = Seated(await new HostRig().StartAsync());
        var code = rig.Bot.Codes.Issue(DemoWorld.Alice, $"127.0.0.1:{rig.Bot.Address.Port}");
        var client = await rig.ConnectAsync();
        await client.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), Code: code));
        var token = (await client.ExpectAsync<WelcomeMessage>()).DeviceToken!;
        var app = new TestApp(client, rig.Time);
        var tone = SpeechAudio.Tone(Codecs, 30);
        await app.PressAsync(true);
        await app.WaitAsync(s => s.Active.Contains(WireAction.Group));
        await PlayAsync(rig, DemoWorld.Alice, DemoWorld.Attack1, tone);
        await app.PressAsync(false);
        await app.DisposeAsync();

        // The same player again, by the device token, and a hello the bot turns away.
        var again = await rig.ConnectAsync();
        await again.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), DeviceToken: token));
        await again.ExpectAsync<WelcomeMessage>();
        var wrongToken = WireSecret.Generate();
        var refused = await rig.ConnectAsync();
        await refused.SendAsync(new HelloMessage("1.0.0", Guid.NewGuid(), DeviceToken: wrongToken));
        await refused.UntilClosedAsync();
        await again.DisposeAsync();
        await refused.DisposeAsync();
        await Task.Delay(300);

        var log = rig.LogText;
        output.WriteLine($"the log is {log.Length} characters over {log.Count(c => c == '\n')} lines");
        Assert.NotEmpty(log);
        foreach (var secret in new[] { HostRig.BotToken, code, code.Split('.')[2], token, wrongToken })
        {
            Assert.DoesNotContain(secret, log);
        }
    }

    // ---- robustness

    /// <summary>
    /// Frames <paramref name="from"/> up to <paramref name="to"/> of a clip, the <i>n</i>th due <i>n</i> frames after the first
    /// by the clock, as a real client sends them. A wait of one frame after each send would add the timer's
    /// overshoot every time, a sender slower than its own timestamps, and the jitter buffer rightly plays
    /// that late and then skips ahead to catch up.
    /// </summary>
    private static async Task SendPacedAsync(HostRig rig, FakeVoiceChannelLink link, UserId user, IReadOnlyList<byte[]> clip, int from, int to, Action<int>? before = null)
    {
        var start = rig.Time.Now;
        for (var i = from; i < to; i++)
        {
            var due = start + (TimeSpan.FromMilliseconds(OpusFormat.FrameMs) * (i - from));
            for (var wait = due - rig.Time.Now; wait > TimeSpan.Zero; wait = due - rig.Time.Now)
            {
                await Task.Delay(wait);
            }
            before?.Invoke(i);
            link.Deliver(user, clip[i]);
        }
    }

    [Fact]
    public async Task A_move_in_the_middle_of_a_broadcast_retargets_it()
    {
        await using var rig = Seated(await new HostRig().StartAsync());
        await using var app = await AppAsync(rig, DemoWorld.Alice);
        var tone = SpeechAudio.Tone(Codecs, 150);
        var index = SpeechAudio.Index(tone);
        const int Moved = 60;

        await app.PressAsync(true);
        await app.WaitAsync(s => s.Active.Contains(WireAction.Group) && s.Targets.Contains("Attack 2"));
        var before = Link(rig, DemoWorld.Attack1);
        await SendPacedAsync(rig, before, DemoWorld.Alice, tone, 0, Moved);
        rig.World.Gateway.Join(DemoWorld.Alice, DemoWorld.Defense2);
        var movedAt = rig.Time.Now;
        var retargeted = await app.WaitAsync(s => s.Targets.Contains("Defense 1"));
        var statusAfter = rig.Time.Now - movedAt;
        var after = Link(rig, DemoWorld.Defense2);
        await SendPacedAsync(rig, after, DemoWorld.Alice, tone, Moved, tone.Count);
        await Task.Delay(600);
        await app.PressAsync(false);

        output.WriteLine($"retargeted to [{string.Join(", ", retargeted.Targets)}] {statusAfter.TotalMilliseconds:0} ms after the move");
        Assert.Equal(["Defense 1", "Defense 3"], retargeted.Targets);
        foreach (var channel in new[] { DemoWorld.Attack2, DemoWorld.Attack3 })
        {
            var frames = Relayed(Link(rig, channel), index);
            Assert.NotEmpty(frames);
            Assert.True(frames.Max() < Moved + 5, $"{rig.World.Names.NameOf(channel)} still heard frame {frames.Max()} after the move");
        }
        foreach (var channel in new[] { DemoWorld.Defense1, DemoWorld.Defense3 })
        {
            var frames = Relayed(Link(rig, channel), index);
            Assert.NotEmpty(frames);
            Assert.True(frames.Count(i => i >= Moved) >= 80, $"{rig.World.Names.NameOf(channel)} heard {frames.Count(i => i >= Moved)} of {tone.Count - Moved} frames after the move");
            Assert.True(frames.Min() >= Moved - 16, $"{rig.World.Names.NameOf(channel)} began at frame {frames.Min()}");
        }
        Assert.Empty(Link(rig, DemoWorld.Defense2).Sent);
        Assert.Empty(Link(rig, DemoWorld.Attack1).Sent);
    }

    [Fact]
    public async Task Leaving_the_team_channels_ends_the_open_mic_at_once()
    {
        await using var rig = Seated(await new HostRig().StartAsync());
        await using var app = await AppAsync(rig, DemoWorld.Alice);
        var tone = SpeechAudio.Tone(Codecs, 150);
        var index = SpeechAudio.Index(tone);

        await app.OpenMicAsync(true);
        await app.WaitAsync(s => s.OpenMic);
        var link = Link(rig, DemoWorld.Attack1);
        TimeSpan clearedAt = default;
        await SendPacedAsync(rig, link, DemoWorld.Alice, tone, 0, tone.Count, i =>
        {
            if (i == 60)
            {
                rig.World.Gateway.Join(DemoWorld.Alice, null);
                clearedAt = rig.Time.Now;
            }
        });
        await Task.Delay(500);

        var last = Link(rig, DemoWorld.Attack2).Sent.Where(f => SpeechAudio.IndexOf(index, f.Opus) >= 0).Max(f => f.At);
        var frames = Relayed(Link(rig, DemoWorld.Attack2), index);
        output.WriteLine($"open mic ended: the relay's last frame left {(last - clearedAt).TotalMilliseconds:0} ms after the change; {frames.Count} frames relayed, last index {frames.Max()}");
        Assert.True(last - clearedAt < TimeSpan.FromMilliseconds(250), $"{(last - clearedAt).TotalMilliseconds:0} ms");
        Assert.True(frames.Max() < 60 + 10);
    }

    [Fact]
    public async Task An_app_that_goes_quiet_or_is_cut_off_ends_its_relay_within_six_seconds()
    {
        await using var rig = Seated(await new HostRig().StartAsync());
        var quiet = await AppAsync(rig, DemoWorld.Alice);
        var cut = await AppAsync(rig, DemoWorld.Eve);
        var attackTone = SpeechAudio.Tone(Codecs, 600, 300);
        var defenseTone = SpeechAudio.Tone(Codecs, 600, 700);
        var attackIndex = SpeechAudio.Index(attackTone);
        var defenseIndex = SpeechAudio.Index(defenseTone);

        await quiet.PressAsync(true);
        await cut.PressAsync(true);
        await quiet.WaitAsync(s => s.Active.Contains(WireAction.Group));
        await cut.WaitAsync(s => s.Active.Contains(WireAction.Group));
        var talk = Task.WhenAll(PlayAsync(rig, DemoWorld.Alice, DemoWorld.Attack1, attackTone), PlayAsync(rig, DemoWorld.Eve, DemoWorld.Defense1, defenseTone));
        await Task.Delay(2500);
        var lastBeatAt = quiet.Unplug();
        var unpluggedAt = rig.Time.Now;
        var severedAt = rig.Time.Now;
        cut.Sever();
        await talk;
        await Task.Delay(500);

        var quietEnd = Link(rig, DemoWorld.Attack2).Sent.Where(f => SpeechAudio.IndexOf(attackIndex, f.Opus) >= 0).Max(f => f.At);
        var cutEnd = Link(rig, DemoWorld.Defense2).Sent.Where(f => SpeechAudio.IndexOf(defenseIndex, f.Opus) >= 0).Max(f => f.At);
        output.WriteLine($"network lost: the relay ended {(quietEnd - unpluggedAt).TotalSeconds:0.00} s after (and {(quietEnd - lastBeatAt).TotalSeconds:0.00} s after the last heartbeat); connection cut: {(cutEnd - severedAt).TotalSeconds:0.00} s after");
        Assert.True(quietEnd - unpluggedAt <= TimeSpan.FromSeconds(6), $"{(quietEnd - unpluggedAt).TotalSeconds:0.00} s");
        Assert.True(quietEnd - lastBeatAt >= TimeSpan.FromSeconds(5), "a quiet app is not cut off before the heartbeat timeout runs out");
        Assert.True(cutEnd - severedAt <= TimeSpan.FromSeconds(1), $"{(cutEnd - severedAt).TotalSeconds:0.00} s");
        await quiet.DisposeAsync();
        await cut.DisposeAsync();
    }

    /// <summary>The PC app: signs in, holds keys, beats every second, keeps the latest status, and can lose its network.</summary>
    private sealed class TestApp : IAsyncDisposable
    {
        private readonly AppClient _client;
        private readonly ITimeSource _time;
        private readonly CancellationTokenSource _stop = new();
        private readonly SemaphoreSlim _sending = new(1, 1);
        private readonly Task _beating;
        private readonly Task _reading;
        private long _seq;
        private volatile bool _held, _allTeamsHeld, _openMic, _unplugged;
        private volatile StatusMessage? _status;
        private TimeSpan _lastBeatAt;

        public TestApp(AppClient client, ITimeSource time)
        {
            (_client, _time) = (client, time);
            _beating = Task.Run(BeatAsync);
            _reading = Task.Run(ReadAsync);
        }

        public Task PressAsync(bool down)
        {
            _held = down;
            return SendAsync(new PressMessage(WireAction.Group, down, Interlocked.Increment(ref _seq)));
        }

        public Task PressAllTeamsAsync(bool down)
        {
            _allTeamsHeld = down;
            return SendAsync(new PressMessage(WireAction.AllTeams, down, Interlocked.Increment(ref _seq)));
        }

        public Task OpenMicAsync(bool on)
        {
            _openMic = on;
            return SendAsync(new OpenMicMessage(on, Interlocked.Increment(ref _seq)));
        }

        /// <summary>Waits until the latest status the bot sent satisfies <paramref name="match"/>.</summary>
        public async Task<StatusMessage> WaitAsync(Func<StatusMessage, bool> match)
        {
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_status is { } status && match(status))
                {
                    return status;
                }
                await Task.Delay(5);
            }
            throw new TimeoutException("no status like that; the last was " + (_status is { } s ? $"[{string.Join(",", s.Active)}] open mic {s.OpenMic} to [{string.Join(", ", s.Targets)}] in {s.ChannelName}" : "none"));
        }

        /// <summary>The network goes: nothing more is sent, the connection stays up. Returns when the last heartbeat went.</summary>
        public TimeSpan Unplug()
        {
            _unplugged = true;
            return _lastBeatAt;
        }

        /// <summary>The connection is cut without a word.</summary>
        public void Sever()
        {
            _unplugged = true;
            _client.Socket.Abort();
        }

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            // The read is waiting on the socket: closing it ends the read.
            _client.Socket.Abort();
            await Task.WhenAll(_beating.ContinueWith(_ => { }), _reading.ContinueWith(_ => { }));
            await _client.DisposeAsync();
        }

        private List<WireAction> Held() =>
            [.. _held ? [WireAction.Group] : Array.Empty<WireAction>(), .. _allTeamsHeld ? [WireAction.AllTeams] : Array.Empty<WireAction>()];

        private async Task SendAsync(ClientMessage message)
        {
            await _sending.WaitAsync();
            try
            {
                if (!_unplugged)
                {
                    await _client.SendAsync(message);
                }
            }
            finally
            {
                _sending.Release();
            }
        }

        private async Task ReadAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested && await _client.ReceiveAsync(TimeSpan.FromMinutes(5)) is { } message)
                {
                    if (message is StatusMessage status)
                    {
                        _status = status;
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
            {
                // The connection ended.
            }
        }

        private async Task BeatAsync()
        {
            try
            {
                while (!_stop.IsCancellationRequested)
                {
                    await Task.Delay(1000, _stop.Token);
                    await _sending.WaitAsync(_stop.Token);
                    try
                    {
                        if (!_unplugged)
                        {
                            await _client.SendAsync(new HeartbeatMessage(Interlocked.Increment(ref _seq), Held(), _openMic, Environment.TickCount64));
                            _lastBeatAt = _time.Now;
                        }
                    }
                    finally
                    {
                        _sending.Release();
                    }
                }
            }
            catch (Exception exception) when (exception is OperationCanceledException or System.Net.WebSockets.WebSocketException or ObjectDisposedException)
            {
                // The app was closed.
            }
        }
    }

    // ---- jungle calls

    private static async Task<CommandReply> Jungle(HostRig rig, string sub, params (string Name, string Value)[] options)
    {
        var discord = new FakeDiscordRuntime(rig.Time);
        var host = new GvgHost(rig.Bot, new ConfigStore(Path.Combine(rig.Folder, "config.json")), Secrets.Empty, discord, new Redactor([HostRig.BotToken]));
        var member = new Caller(DemoWorld.Dana, 0, [], false);
        return await new GvgCommands(host).HandleAsync(
            new CommandInvocation("jungle", [sub], options.ToDictionary(o => o.Name, o => o.Value), member, rig.Bot.Config.GuildId ?? 5000), default);
    }

    private static void WaitUntil(Func<bool> condition, string what) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(8)), "timed out waiting for " + what);

    [Fact]
    public async Task Jungle_test_from_any_member_reaches_every_bound_VC()
    {
        await using var rig = Seated(await new HostRig(c => c with { GuildId = 5000 }).StartAsync());
        WaitUntil(() => rig.Bot.Engine.ReadyChannels.Count == All.Length, "every relay link to be ready");

        var reply = await Jungle(rig, "test");

        Assert.Equal($"Test call sent to {All.Length} team channels.", reply.Text);
        foreach (var channel in All)
        {
            WaitUntil(() => Link(rig, channel).Sent.Count >= 20, $"the call in {rig.World.Names.NameOf(channel)}");
            Assert.NotEmpty(Link(rig, channel).SpeakingChanges);
        }
    }

    [Fact]
    public async Task A_synced_clock_plays_its_call_through_the_host_timer_with_no_command_after_the_sync()
    {
        await using var rig = Seated(await new HostRig(c => c with { GuildId = 5000 }).StartAsync());
        WaitUntil(() => rig.Bot.Engine.ReadyChannels.Count == All.Length, "every relay link to be ready");

        var reply = await Jungle(rig, "sync", ("time", "25:41"));

        Assert.StartsWith("Synced to 25:41.", reply.Text);
        foreach (var channel in All)
        {
            WaitUntil(() => Link(rig, channel).Sent.Count >= 20, $"the 25:40 call in {rig.World.Names.NameOf(channel)}");
        }
    }

    [Fact]
    public async Task Jungle_test_with_no_relay_bot_in_voice_says_nobody_heard_it()
    {
        await using var rig = await new HostRig(c => c with { GuildId = 5000 }).StartAsync();
        foreach (var channel in All)
        {
            Link(rig, channel).Ready = false;
        }
        WaitUntil(() => rig.Bot.Engine.ReadyChannels.Count == 0, "the links to be reported down");

        var reply = await Jungle(rig, "test");

        Assert.Equal("No team channel has a relay bot in it now, so nobody heard it.", reply.Text);
    }
}
