using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Discord;
using Kzg.Protocol;
using NetCord;
using NetCord.Rest;
using Xunit.Abstractions;

namespace Kzg.Bot.Tests.Live;

/// <summary>
/// Plan steps 9 and 11 on the "KZG GvG Test" server: the real bot host and engine, the key presses sent over
/// <c>/ws</c> by a test client that behaves like the PC app, test bots speaking known clips in the team VCs
/// and listener bots recording who hears what. Every scenario of the verification list that Discord can carry
/// runs here: routing from every VC by every action, pre-roll, latency, a move mid-broadcast and a
/// dropped app. The numbers are printed per scenario. Needs <c>KZG_LIVE=1</c>.
/// </summary>
public sealed class LiveScenarioTests(ITestOutputHelper output)
{
    private static readonly string[] AllVcs = ["A1", "A2", "A3", "D1", "D2", "D3"];

    private static readonly (string Name, string Vc)[] Listeners =
    [
        ("listener1", "A1"), ("listener2", "A2"), ("listener3", "A3"),
        ("listener4", "D1"), ("listener5", "D2"), ("listener6", "D3"),
    ];

    private static string Display(string vc) => $"{(vc[0] == 'A' ? "Attack" : "Defense")} {vc[1]}";

    private static bool SameGroup(string a, string b) => a[0] == b[0];

    /// <summary>Who should be heard by a listener, carrying which clip, in how much of it.</summary>
    private sealed record Expect(string Listener, ulong Sender, Clip Clip, double Share, int Lo = 0, int Hi = int.MaxValue, string? RefListener = null, ulong RefSender = 0);

    private sealed class Result(string title)
    {
        public string Title { get; } = title;

        public List<string> Lines { get; } = [];

        public List<string> Violations { get; } = [];

        public List<double> AddedMs { get; } = [];

        public List<double> EndToEndMs { get; } = [];

        /// <summary>The clip frames each listener heard from each sender, by "listener&lt;-sender".</summary>
        public Dictionary<string, SortedSet<int>> Indices { get; } = [];

        /// <summary>When each such frame was heard, ms on the test's clock.</summary>
        public Dictionary<string, List<(int Index, double At)>> Times { get; } = [];
    }

    /// <summary>Who is who on the test server.</summary>
    private sealed class Session
    {
        public Dictionary<ulong, string> NameOf { get; } = [];

        public Dictionary<string, ulong> RelayOf { get; } = [];
    }

    [LiveFact]
    public async Task The_verification_scenarios_run_on_the_test_server()
    {
        var repo = FindRepo();
        var data = Path.Combine(repo, ".data");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var runDir = Path.Combine(data, "live", stamp + "-scenarios");
        Directory.CreateDirectory(runDir);

        var secrets = ((FileLoad<Secrets>.Loaded)new SecretsStore(Path.Combine(data, "secrets.json")).Load(Environment.GetEnvironmentVariables())).Value;
        var configPath = Path.Combine(data, "config.json");
        var originalConfig = await File.ReadAllTextAsync(configPath);
        var config = ((FileLoad<BotConfig>.Loaded)new ConfigStore(configPath).Load(secrets)).Value;
        var guild = config.GuildId ?? throw new InvalidOperationException("the dev config names the guild");
        var channel = new Dictionary<string, ulong>
        {
            ["A1"] = config.Groups.Attack.Channels[0].V,
            ["A2"] = config.Groups.Attack.Channels[1].V,
            ["A3"] = config.Groups.Attack.Channels[2].V,
            ["D1"] = config.Groups.Defense.Channels[0].V,
            ["D2"] = config.Groups.Defense.Channels[1].V,
            ["D3"] = config.Groups.Defense.Channels[2].V,
        };

        var clock = Stopwatch.StartNew();
        ConcurrentQueue<string> warnings = new();
        Dictionary<string, LiveBot> bots = [];
        foreach (var name in new[] { "tester1", "tester2" }.Concat(Listeners.Select(l => l.Name)))
        {
            bots[name] = new LiveBot(name, secrets.TryGet(name, out var token) ? token : throw new InvalidOperationException(name), guild, clock, warnings);
        }
        await Task.WhenAll(bots.Values.Select(b => b.ConnectAsync()));

        Assert.IsType<FileSave.Saved>(new ConfigStore(configPath).Save(config, secrets));

        var botLog = new StringWriter();
        var botOutput = TextWriter.Synchronized(botLog);
        var botError = new StringWriter();
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource<BotApp>(TaskCreationOptions.RunContinuationsAsynchronously);
        DiscordGateway? gateway = null;
        var run = Program.RunAsync(
            ["--data-dir", data, "--listen", "127.0.0.1:0"], botOutput, botError, stop.Token, started.SetResult,
            (c, s, t) => gateway = new DiscordGateway(c, s, t));
        if (await Task.WhenAny(started.Task, run) == run)
        {
            await File.WriteAllTextAsync(configPath, originalConfig);
            Assert.Fail($"the bot stopped at start-up: {botError}");
        }
        var bot = await started.Task;
        output.WriteLine($"bot up after {clock.Elapsed.TotalSeconds:0.0} s on {bot.Address}");

        var world = new Session();
        var app1 = new LiveAppClient(clock);
        var app2 = new LiveAppClient(clock);
        List<Result> results = [];
        List<string> issued = [];
        try
        {
            Assert.True(SpinWait.SpinUntil(() => bot.Engine.ReadyChannels.Count == 6, TimeSpan.FromSeconds(60)), "every relay link ready");
            await Task.WhenAll(
                bots["tester1"].JoinAsync(channel["A1"]),
                bots["tester2"].JoinAsync(channel["D1"]),
                Task.WhenAll(Listeners.Select(l => bots[l.Name].JoinAsync(channel[l.Vc]))));
            Assert.True(SpinWait.SpinUntil(() => bot.Config.Bots.All(b => b.BotUserId is not null), TimeSpan.FromSeconds(15)), "the relay bots' IDs were learned");
            foreach (var entry in bot.Config.Bots)
            {
                var vc = channel.First(c => c.Value == entry.ChannelId!.Value.V).Key;
                world.RelayOf[vc] = entry.BotUserId!.Value.V;
                world.NameOf[entry.BotUserId!.Value.V] = "relay@" + vc;
            }
            foreach (var (name, bt) in bots)
            {
                world.NameOf[bt.Id] = name;
            }
            var host = $"127.0.0.1:{bot.Address.Port}";
            var code1 = bot.Codes.Issue(new UserId(bots["tester1"].Id), host);
            var code2 = bot.Codes.Issue(new UserId(bots["tester2"].Id), host);
            await app1.ConnectAsync(bot.Address, code1);
            await app2.ConnectAsync(bot.Address, code2);
            issued.AddRange([code1, code2, code1.Split('.')[2], code2.Split('.')[2]]);
            issued.AddRange(new[] { app1.Welcome?.DeviceToken, app2.Welcome?.DeviceToken }.OfType<string>());
            await app1.WaitStatusAsync(s => s.ChannelName == "Attack 1");
            await app2.WaitStatusAsync(s => s.ChannelName == "Defense 1");
            output.WriteLine($"everyone in place after {clock.Elapsed.TotalSeconds:0.0} s");
            await Task.Delay(2500);

            var watch = new BotWatch(clock, gateway!, bot.Engine);
            var play = new Scenarios(output, clock, world, bots, app1, app2, channel, results, watch);
            await play.RunAllAsync();
            output.WriteLine($"scenarios done after {clock.Elapsed.TotalSeconds:0.0} s; warnings from NetCord: {warnings.Count}");
            foreach (var line in warnings.ToArray().GroupBy(w => w[..Math.Min(w.Length, 60)]).Select(g => $"{g.Count()} x {g.Key}").Take(15))
            {
                output.WriteLine("   " + line);
            }
            await File.WriteAllTextAsync(Path.Combine(runDir, "summary.json"), JsonSerializer.Serialize(results.Select(r => new { r.Title, r.Lines, r.Violations }), new JsonSerializerOptions { WriteIndented = true }));
            var all = results.SelectMany(r => r.Violations.Select(v => $"[{r.Title}] {v}")).ToList();
            output.WriteLine(all.Count == 0 ? "no violations" : $"{all.Count} violations");
            Assert.Empty(all);
        }
        finally
        {
            await app1.DisposeAsync();
            await app2.DisposeAsync();
            await Task.WhenAll(bots.Values.Select(b => b.DisconnectAsync()));
            await stop.CancelAsync();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine($"bot exited {exit} after {clock.Elapsed.TotalSeconds:0.0} s; stderr: '{botError.ToString().Trim()}'");
            await File.WriteAllTextAsync(configPath, originalConfig);
            await File.WriteAllTextAsync(Path.Combine(runDir, "bot.log"), botLog.ToString());
            Assert.Equal(0, exit);
            await AssertNobodyInVoice(secrets, guild, [.. bot.Config.Bots.Select(b => b.BotUserId!.Value.V), .. bots.Values.Select(b => b.Id)]);
            // Log redaction, for real: the bot's whole log and error output hold none of the tokens it ran on, the codes it issued or the device tokens it gave out.
            var log = botLog + "\n" + botError;
            var secretValues = new[] { "relay1", "relay2", "relay3", "relay4", "relay5", "relay6", "tester1", "tester2", "tester3", "listener1", "listener2", "listener3", "listener4", "listener5", "listener6" }
                .Select(n => secrets.TryGet(n, out var v) ? v : null).OfType<string>().Concat(issued).ToList();
            output.WriteLine($"log redaction: {log.Length} characters of log checked against {secretValues.Count} secrets");
            Assert.NotEmpty(secretValues);
            foreach (var secret in secretValues)
            {
                Assert.DoesNotContain(secret, log);
            }
        }
        output.WriteLine($"log: {runDir}");
    }

    private sealed class Scenarios(ITestOutputHelper output, Stopwatch clock, Session world, Dictionary<string, LiveBot> bots, LiveAppClient app1, LiveAppClient app2, Dictionary<string, ulong> channel, List<Result> results, BotWatch watch)
    {
        private LiveBot T1 => bots["tester1"];

        private LiveBot T2 => bots["tester2"];

        private double Now => clock.Elapsed.TotalMilliseconds;

        private string Name(ulong id) => world.NameOf.GetValueOrDefault(id, id.ToString());

        public async Task RunAllAsync()
        {
            await KeyMatrixAsync();
            await AllTeamsMatrixAsync();
            await OpenMicMatrixAsync();
            await OpenMicInTheOtherTeamAsync();
            await PrerollAsync();
            await LatencyAsync();
            await TalkSpurtStylesAsync();
            await MoveMidBroadcastAsync();
            await DroppedAppAsync();
        }

        // ---- the scenarios

        private async Task KeyMatrixAsync()
        {
            // Both teams broadcast at once, a speaker per team, so a leak between the teams would show.
            foreach (var (a, d) in new[] { ("A1", "D1"), ("A2", "D2"), ("A3", "D3") })
            {
                var ca = Clip.Tone("a", 5, 300, 11);
                var cd = Clip.Tone("d", 5, 700, 22);
                await MoveAsync(T1, app1, a);
                await MoveAsync(T2, app2, d);
                var result = await RunAsync(
                    $"group key: tester1 in {Display(a)}, tester2 in {Display(d)}",
                    async () =>
                    {
                        await Task.WhenAll(app1.PressAsync(true), app2.PressAsync(true));
                        await Task.WhenAll(app1.WaitStatusAsync(s => s.Active.Contains(WireAction.Group)), app2.WaitStatusAsync(s => s.Active.Contains(WireAction.Group)));
                        await Task.WhenAll(T1.SpeakAsync(ca), T2.SpeakAsync(cd));
                        await Task.Delay(500);
                        await Task.WhenAll(app1.PressAsync(false), app2.PressAsync(false));
                    },
                    [.. Group(a, T1, ca, true), .. Group(d, T2, cd, true)]);
                results.Add(result);
            }
        }

        private async Task OpenMicMatrixAsync()
        {
            foreach (var (a, d) in new[] { ("A3", "D3"), ("A2", "D2"), ("A1", "D1") })
            {
                var ca = Clip.Tone("a", 5, 330, 33);
                var cd = Clip.Tone("d", 5, 730, 44);
                await MoveAsync(T1, app1, a);
                await MoveAsync(T2, app2, d);
                var result = await RunAsync(
                    $"open mic: tester1 in {Display(a)}, tester2 in {Display(d)}",
                    async () =>
                    {
                        await Task.WhenAll(app1.OpenMicAsync(true), app2.OpenMicAsync(true));
                        await Task.WhenAll(app1.WaitStatusAsync(s => s.OpenMic && s.Active.Contains(WireAction.OpenMic)), app2.WaitStatusAsync(s => s.OpenMic && s.Active.Contains(WireAction.OpenMic)));
                        await Task.WhenAll(T1.SpeakAsync(ca), T2.SpeakAsync(cd));
                        await Task.Delay(500);
                        await Task.WhenAll(app1.OpenMicAsync(false), app2.OpenMicAsync(false));
                    },
                    [.. Group(a, T1, ca, true), .. Group(d, T2, cd, true)]);
                results.Add(result);
            }
        }

        private async Task AllTeamsMatrixAsync()
        {
            // One speaker at a time: the all-teams key reaches both teams, so two at once would hear each other.
            foreach (var vc in AllVcs)
            {
                var clip = Clip.Tone("all", 5, 340, 77);
                await MoveAsync(T1, app1, vc);
                var result = await RunAsync(
                    $"all-teams key: tester1 in {Display(vc)}",
                    async () =>
                    {
                        await app1.PressAsync(true, allTeams: true);
                        await app1.WaitStatusAsync(s => s.Active.Contains(WireAction.AllTeams));
                        await T1.SpeakAsync(clip);
                        await Task.Delay(500);
                        await app1.PressAsync(false, allTeams: true);
                    },
                    AllTeams(vc, T1, clip));
                results.Add(result);
            }
        }

        private async Task OpenMicInTheOtherTeamAsync()
        {
            // Open mic is anyone's, and reaches the team of the VC the player sits in.
            var ca = Clip.Tone("a", 4, 360, 55);
            var cd = Clip.Tone("d", 4, 760, 66);
            await MoveAsync(T1, app1, "D2");
            await MoveAsync(T2, app2, "A2");
            var result = await RunAsync(
                "open mic: tester1 in Defense 2, tester2 in Attack 2",
                async () =>
                {
                    await Task.WhenAll(app1.OpenMicAsync(true), app2.OpenMicAsync(true));
                    await Task.WhenAll(app1.WaitStatusAsync(s => s.Active.Contains(WireAction.OpenMic)), app2.WaitStatusAsync(s => s.Active.Contains(WireAction.OpenMic)));
                    await Task.WhenAll(T1.SpeakAsync(ca), T2.SpeakAsync(cd));
                    await Task.Delay(500);
                    await Task.WhenAll(app1.OpenMicAsync(false), app2.OpenMicAsync(false));
                },
                [.. Group("D2", T1, ca, true), .. Group("A2", T2, cd, true)]);
            results.Add(result);
        }

        private async Task PrerollAsync()
        {
            await MoveAsync(T1, app1, "A1");
            await MoveAsync(T2, app2, "D1");
            for (var rep = 1; rep <= 3; rep++)
            {
                // Speech begins at once and the key goes down 200 ms in, as a player who starts talking and then reaches for it.
                var ca = Clip.Say("pre-a", "Alpha bravo charlie delta echo foxtrot", 0, 0.5, 80 + rep, out var firstA, out _);
                var cd = Clip.Say("pre-d", "Golf hotel india juliet kilo lima", 0, 0.5, 90 + rep, out var firstD, out _);
                var result = await RunAsync(
                    $"pre-roll {rep}: the key goes down 200 ms after speech began",
                    async () =>
                    {
                        var talking = Task.WhenAll(T1.SpeakAsync(ca), T2.SpeakAsync(cd));
                        await Task.Delay(200);
                        await Task.WhenAll(app1.PressAsync(true), app2.PressAsync(true));
                        await talking;
                        await Task.Delay(400);
                        await Task.WhenAll(app1.PressAsync(false), app2.PressAsync(false));
                    },
                    [.. Group("A1", T1, ca, true, firstWord: firstA), .. Group("D1", T2, cd, true, firstWord: firstD)]);
                foreach (var (listener, sender, first) in new[] { ("listener2", world.RelayOf["A2"], firstA), ("listener3", world.RelayOf["A3"], firstA), ("listener5", world.RelayOf["D2"], firstD), ("listener6", world.RelayOf["D3"], firstD) })
                {
                    var got = result.Indices.GetValueOrDefault($"{listener}<-{Name(sender)}") ?? [];
                    var window = Enumerable.Range(first, 25).ToArray();
                    var missing = window.Count(i => !got.Contains(i));
                    Note(result, $"{listener}: first relayed frame {(got.Count == 0 ? -1 : got.Min)} (speech starts at {first}); the first 500 ms of speech missing {missing} of 25 frames");
                    if (missing > 1)
                    {
                        Fail(result, $"{listener}: the first word is not intact, {missing} of its first 25 frames are missing");
                    }
                }
                results.Add(result);
                await Task.Delay(800);
            }
        }

        private async Task LatencyAsync()
        {
            var ca = Clip.ClickTrain("clicks-a", 12, 5);
            var cd = Clip.ClickTrain("clicks-d", 12, 6);
            var result = await RunAsync(
                "click train: 12 s of clicks from tester1 in Attack 1 and tester2 in Defense 1",
                async () =>
                {
                    await Task.WhenAll(app1.PressAsync(true), app2.PressAsync(true));
                    await Task.WhenAll(app1.WaitStatusAsync(s => s.Active.Contains(WireAction.Group)), app2.WaitStatusAsync(s => s.Active.Contains(WireAction.Group)));
                    await Task.WhenAll(T1.SpeakAsync(ca), T2.SpeakAsync(cd));
                    await Task.Delay(500);
                    await Task.WhenAll(app1.PressAsync(false), app2.PressAsync(false));
                },
                [.. Group("A1", T1, ca, true), .. Group("D1", T2, cd, true)]);
            var sorted = result.AddedMs.Order().ToArray();
            var p95 = sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(0.95 * sorted.Length))];
            Note(result, $"added latency (relay minus direct) n={sorted.Length}: p50 {Pct(sorted, 0.5):0} ms, p95 {p95:0} ms, p99 {Pct(sorted, 0.99):0} ms, max {(sorted.Length == 0 ? double.NaN : sorted[^1]):0} ms");
            if (sorted.Length < 1000 || !(p95 <= 300))
            {
                Fail(result, $"added latency p95 {p95:0} ms over {sorted.Length} frames (needs 1000 or more frames and p95 of 300 ms or less)");
            }
            results.Add(result);
        }

        private async Task TalkSpurtStylesAsync()
        {
            // Two ways a client may number its packets across a pause: tester1 goes on from its last number,
            // tester2 lets the timestamp keep pace with the clock. A second broadcast must relay either way.
            T1.TimestampFollowsClock = false;
            T2.TimestampFollowsClock = true;
            await MoveAsync(T1, app1, "A1");
            await MoveAsync(T2, app2, "D1");
            for (var round = 1; round <= 2; round++)
            {
                var ca = Clip.Tone("a", 4, 400 + (round * 30), 300 + round);
                var cd = Clip.Tone("d", 4, 800 + (round * 30), 310 + round);
                var result = await RunAsync(
                    $"second broadcast after a pause, round {round}: tester1's timestamp goes on from its last number, tester2's follows the clock",
                    async () =>
                    {
                        await Task.WhenAll(app1.PressAsync(true), app2.PressAsync(true));
                        await Task.WhenAll(app1.WaitStatusAsync(s => s.Active.Contains(WireAction.Group)), app2.WaitStatusAsync(s => s.Active.Contains(WireAction.Group)));
                        await Task.WhenAll(T1.SpeakAsync(ca), T2.SpeakAsync(cd));
                        await Task.Delay(500);
                        await Task.WhenAll(app1.PressAsync(false), app2.PressAsync(false));
                        await Task.Delay(2500);
                    },
                    [.. Group("A1", T1, ca, true), .. Group("D1", T2, cd, true)]);
                results.Add(result);
            }
            T1.TimestampFollowsClock = true;
        }

        private async Task MoveMidBroadcastAsync()
        {
            var first = Clip.Tone("before", 4, 300, 201);
            var second = Clip.Tone("after", 4, 900, 202);
            await MoveAsync(T1, app1, "A1");
            await Task.Delay(1500);
            var from = Now;
            await app1.PressAsync(true);
            await app1.WaitStatusAsync(s => s.Active.Contains(WireAction.Group));
            await T1.SpeakAsync(first);
            await Task.Delay(400);
            // The player moves from Attack 1 to Defense 2 with the key still held.
            var moveStart = Now;
            await T1.MoveAsync(channel["D2"]);
            var movedAt = Now;
            await app1.WaitStatusAsync(s => s.ChannelName == "Defense 2" && s.Targets.Contains("Defense 1"));
            var retargetedAt = app1.Statuses.Where(s => s.At >= moveStart && s.Status.Targets.Contains("Defense 1")).Select(s => s.At).First();
            // A client speaks a moment after it joins, once Discord has told the others whose stream it is.
            await Task.Delay(700);
            var sent = await T1.SpeakAsync(second);
            await Task.Delay(500);
            await app1.PressAsync(false);
            await Task.Delay(1800);
            var to = Now;

            var before = Analyse("move mid-broadcast, before the move: Attack 1 to Attack 2 and 3", from, movedAt, Group("A1", T1, first, true), []);
            var after = Analyse("move mid-broadcast, after the move: Defense 2 to Defense 1 and 3, nothing to Attack", movedAt, to, Group("D2", T1, second, true), []);
            var firstSent = sent.Values.Min();
            var firstHeard = after.Times.Where(p => p.Key.Contains("<-relay@", StringComparison.Ordinal)).SelectMany(p => p.Value).Select(t => t.At).DefaultIfEmpty(double.NaN).Min();
            Note(after, $"retargeted: the app was told Defense 1 and 3 {retargetedAt - moveStart:0} ms after the tester began to move ({retargetedAt - movedAt:0} ms after its join finished); first frame in Defense 2 sent {firstSent - movedAt:0} ms after the join (700 ms pause by design), first heard in Defense 1 or 3 {firstHeard - firstSent:0} ms after that");
            results.Add(before);
            results.Add(after);
        }

        private async Task DroppedAppAsync()
        {
            var ca = Clip.Tone("drop-a", 14, 400, 221);
            var cd = Clip.Tone("drop-d", 14, 800, 222);
            await MoveAsync(T1, app1, "A1");
            await MoveAsync(T2, app2, "D1");
            var from = Now;
            await Task.WhenAll(app1.PressAsync(true), app2.PressAsync(true));
            await Task.WhenAll(app1.WaitStatusAsync(s => s.Active.Contains(WireAction.Group)), app2.WaitStatusAsync(s => s.Active.Contains(WireAction.Group)));
            var speaking = Task.WhenAll(T1.SpeakAsync(ca), T2.SpeakAsync(cd));
            await Task.Delay(3500);
            // tester1's network goes quiet with the connection left open; tester2's connection is cut.
            app1.Unplug();
            var unpluggedAt = Now;
            var lastBeat = app1.LastBeatAt;
            app2.Sever();
            var severedAt = Now;
            await speaking;
            await Task.Delay(1500);
            var to = Now;
            var result = Analyse("dropped app: tester1's heartbeats stop, tester2's connection is cut", from, to, [.. Group("A1", T1, ca, true, relayShare: 0), .. Group("D1", T2, cd, true, relayShare: 0)], []);
            double LastHeard(string listener, string vc, Clip clip) => HeardTimes(from, to, listener, world.RelayOf[vc], clip).DefaultIfEmpty(double.NaN).Max();
            var quietEnd = Math.Max(LastHeard("listener2", "A2", ca), LastHeard("listener3", "A3", ca));
            var cutEnd = Math.Max(LastHeard("listener5", "D2", cd), LastHeard("listener6", "D3", cd));
            Note(result, $"heartbeats stopped: the last frame reached Attack 2 or 3 {(quietEnd - unpluggedAt) / 1000:0.00} s after the network went and {(quietEnd - lastBeat) / 1000:0.00} s after the last heartbeat");
            Note(result, $"connection cut: the last frame reached Defense 2 or 3 {(cutEnd - severedAt) / 1000:0.00} s after");
            if (!(quietEnd - lastBeat <= 6250) || !(quietEnd - lastBeat >= 4000))
            {
                Fail(result, $"a quiet app's relay ended {(quietEnd - lastBeat) / 1000:0.00} s after its last heartbeat (the timeout is 6 s)");
            }
            if (!(cutEnd - severedAt <= 1500))
            {
                Fail(result, $"a cut app's relay ended {(cutEnd - severedAt) / 1000:0.00} s after the cut");
            }
            results.Add(result);
        }

        // ---- machinery

        private async Task MoveAsync(LiveBot who, LiveAppClient app, string vc)
        {
            if (who.Channel != channel[vc])
            {
                await who.MoveAsync(channel[vc]);
            }
            await app.WaitStatusAsync(s => s.ChannelName == Display(vc));
        }

        private async Task<Result> RunAsync(string title, Func<Task> body, IReadOnlyList<Expect> expects)
        {
            await Task.Delay(1500);
            var from = Now;
            await body();
            await Task.Delay(1800);
            var result = Analyse(title, from, Now, expects, []);
            return result;
        }

        /// <summary>The expectations of one speaker's broadcast: heard directly in their own VC, relayed to the rest of their team, and nowhere else.</summary>
        /// <summary>The all-teams key: the speaker's own VC hears them directly, every other VC through its relay.</summary>
        private List<Expect> AllTeams(string speakerVc, LiveBot speaker, Clip clip) =>
            [.. Listeners.Select(l => l.Vc == speakerVc
                ? new Expect(l.Name, speaker.Id, clip, 0.97)
                : new Expect(l.Name, world.RelayOf[l.Vc], clip, 0.9, RefListener: Listeners.First(x => x.Vc == speakerVc).Name, RefSender: speaker.Id))];

        private List<Expect> Group(string speakerVc, LiveBot speaker, Clip clip, bool relayed, int? firstWord = null, double relayShare = 0.9)
        {
            _ = firstWord;
            List<Expect> expects = [];
            foreach (var (listener, vc) in Listeners)
            {
                if (vc == speakerVc)
                {
                    expects.Add(new Expect(listener, speaker.Id, clip, 0.97));
                }
                else if (relayed && SameGroup(vc, speakerVc))
                {
                    expects.Add(new Expect(listener, world.RelayOf[vc], clip, relayShare, RefListener: Listeners.First(l => l.Vc == speakerVc).Name, RefSender: speaker.Id));
                }
            }
            return expects;
        }

        private IEnumerable<double> HeardTimes(double from, double to, string listener, ulong sender, Clip clip)
        {
            var index = clip.IndexByHash();
            return bots[listener].Heard.Where(h => h.At >= from && h.At <= to && h.From == sender && index.ContainsKey(h.Hash)).Select(h => h.At);
        }

        private Result Analyse(string title, double from, double to, IReadOnlyList<Expect> expects, IReadOnlyList<string> notes)
        {
            var result = new Result(title);
            result.Lines.AddRange(notes);
            foreach (var (listener, vc) in Listeners)
            {
                var heard = bots[listener].Heard.Where(h => h.At >= from && h.At <= to).ToArray();
                var mine = expects.Where(e => e.Listener == listener).ToArray();
                var indexes = mine.ToDictionary(e => e, e => e.Clip.IndexByHash());
                // Anything not on the list is somebody else's audio in this VC.
                foreach (var stray in heard.Where(h => !mine.Any(e => e.Sender == h.From && indexes[e].ContainsKey(h.Hash))).GroupBy(h => h.From))
                {
                    result.Violations.Add($"{listener} in {Display(vc)} heard {stray.Count()} frames from {Name(stray.Key)} that it should not have");
                }
                if (mine.Length == 0)
                {
                    result.Lines.Add($"{listener} in {Display(vc),-9}: heard {heard.Length} frames{(heard.Length == 0 ? " (nothing, as it must be)" : "")}");
                }
                foreach (var e in mine)
                {
                    var key = $"{listener}<-{Name(e.Sender)}";
                    var got = heard.Where(h => h.From == e.Sender && indexes[e].ContainsKey(h.Hash)).Select(h => (Index: indexes[e][h.Hash], h.At, h.Hash)).GroupBy(t => t.Index).Select(g => g.First()).OrderBy(t => t.Index).ToArray();
                    var inRange = got.Count(t => t.Index >= e.Lo && t.Index <= e.Hi);
                    var want = e.Clip.Frames.Select((f, i) => (f, i)).Count(p => p.f.Data.Length > 0 && p.i >= e.Lo && p.i <= e.Hi);
                    result.Indices[key] = [.. got.Select(t => t.Index)];
                    result.Times[key] = [.. got.Select(t => (t.Index, t.At))];
                    var leading = got.Length == 0 ? "none" : $"first {got[0].Index}, last {got[^1].Index}";
                    result.Lines.Add($"{listener} in {Display(vc),-9}: {inRange}/{want} frames of the clip from {Name(e.Sender)} ({leading})");
                    if (inRange < want * e.Share)
                    {
                        result.Violations.Add($"{listener} in {Display(vc)} heard {inRange} of {want} frames from {Name(e.Sender)} (needs {(int)(want * e.Share)})");
                    }
                    if (e.RefListener is not null)
                    {
                        var direct = bots[e.RefListener].Heard.Where(h => h.At >= from && h.At <= to && h.From == e.RefSender).GroupBy(h => h.Hash).ToDictionary(g => g.Key, g => g.Min(h => h.At));
                        foreach (var t in got.Where(t => direct.ContainsKey(t.Hash)))
                        {
                            result.AddedMs.Add(t.At - direct[t.Hash]);
                        }
                    }
                }
            }
            result.Lines.AddRange(watch.Describe(from, to, world.NameOf));
            output.WriteLine($"== {title}");
            foreach (var line in result.Lines)
            {
                output.WriteLine("   " + line);
            }
            foreach (var violation in result.Violations)
            {
                output.WriteLine("   VIOLATION: " + violation);
            }
            return result;
        }

        private void Note(Result result, string line)
        {
            result.Lines.Add(line);
            output.WriteLine("   " + line);
        }

        private void Fail(Result result, string violation)
        {
            result.Violations.Add(violation);
            output.WriteLine("   VIOLATION: " + violation);
        }

        private static double Pct(double[] sorted, double p) => sorted.Length == 0 ? double.NaN : sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
    }

    /// <summary>Every bot is out of voice once the run is over: Discord answers 404 for a user who isn't connected.</summary>
    private async Task AssertNobodyInVoice(Secrets secrets, ulong guild, ulong[] ids)
    {
        using var rest = new RestClient(new BotToken(secrets.TryGet("listener1", out var token) ? token : throw new InvalidOperationException("listener1")));
        List<ulong> still = [];
        foreach (var id in ids.Distinct())
        {
            var gone = false;
            for (var attempt = 0; attempt < 10 && !gone; attempt++)
            {
                try
                {
                    await rest.GetGuildUserVoiceStateAsync(guild, id);
                    await Task.Delay(500);
                }
                catch (RestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    gone = true;
                }
            }
            if (!gone)
            {
                still.Add(id);
            }
        }
        output.WriteLine($"still in voice after the run: {still.Count} of {ids.Distinct().Count()} bots checked");
        Assert.Empty(still);
    }

    private static string FindRepo()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Kzg.slnx")))
            {
                return dir.FullName;
            }
        }
        throw new InvalidOperationException("the repository root was not found");
    }
}
