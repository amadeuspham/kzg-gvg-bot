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
/// The real bot on the "KZG GvG Test" server: six relay bots in Attack 1-3 and Defense 1-3, two tester bots
/// that speak and press the key like the PC app, and six listener bots that record. A broadcast must reach
/// the other VCs of its own group, and only those: not back into the speaker's VC, not the other group.
/// Needs the data folder's <c>config.json</c> and <c>secrets.json</c>, and <c>KZG_LIVE=1</c>.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class LiveRelayTests(ITestOutputHelper output)
{
    private const string GuildName = "KZG GvG Test";
    private const int ClipSeconds = 6;

    private sealed record Role(string Secret, string Channel);

    // Where each test bot sits: testers speak in Attack 1 and Defense 1; a listener sits in every channel.
    private const int LeadingFramesThatMustArrive = 4;

    private static string[] StatusTargets(string line)
    {
        var open = line.IndexOf('[') + 1;
        return line[open..line.IndexOf(']', open)].Split(", ");
    }

    private static readonly (string Name, string Channel)[] Seats =
    [
        ("tester1", "A1"), ("tester2", "D1"),
        ("listener1", "A1"), ("listener2", "A2"), ("listener3", "A3"),
        ("listener4", "D1"), ("listener5", "D2"), ("listener6", "D3"),
    ];

    [LiveFact]
    public async Task A_broadcast_reaches_the_other_VCs_of_its_group_and_nowhere_else()
    {
        var repo = FindRepo();
        var data = Path.Combine(repo, ".data");
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
        var runDir = Path.Combine(data, "live", stamp);
        Directory.CreateDirectory(runDir);
        var clock = Stopwatch.StartNew();
        ConcurrentQueue<string> warnings = new();

        var secrets = ((FileLoad<Secrets>.Loaded)new SecretsStore(Path.Combine(data, "secrets.json")).Load(Environment.GetEnvironmentVariables())).Value;
        var config = ((FileLoad<BotConfig>.Loaded)new ConfigStore(Path.Combine(data, "config.json")).Load(secrets)).Value;
        var channel = new Dictionary<string, ulong>
        {
            ["A1"] = config.Groups.Attack.Channels[0].V,
            ["A2"] = config.Groups.Attack.Channels[1].V,
            ["A3"] = config.Groups.Attack.Channels[2].V,
            ["D1"] = config.Groups.Defense.Channels[0].V,
            ["D2"] = config.Groups.Defense.Channels[1].V,
            ["D3"] = config.Groups.Defense.Channels[2].V,
        };
        var guild = config.GuildId ?? throw new InvalidOperationException("the dev config names the guild");
        var clip1 = Clip.Load("t1", Path.Combine(data, "dave", "clips", "t1.raw"), ClipSeconds, 11);
        var clip2 = Clip.Load("t2", Path.Combine(data, "dave", "clips", "t2.raw"), ClipSeconds, 22);
        Clip[] clips = [clip1, clip2];

        // ---- the bot under test, through Program as `Kzg.Bot` runs it: no --fake, the real Discord adapter
        var botLog = new StringWriter();
        var botOutput = TextWriter.Synchronized(botLog);
        var botError = new StringWriter();
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource<BotApp>(TaskCreationOptions.RunContinuationsAsynchronously);
        DiscordGateway? gateway = null;
        var run = Program.RunAsync(
            ["--data-dir", data, "--listen", "127.0.0.1:0"], botOutput, botError, stop.Token, started.SetResult,
            (c, s, t) => gateway = new DiscordGateway(c, s, t));
        var runOrStart = await Task.WhenAny(started.Task, run);
        if (runOrStart == run)
        {
            Assert.Fail($"the bot stopped at start-up: {botError}");
        }
        var bot = await started.Task;
        output.WriteLine($"bot up after {clock.Elapsed.TotalSeconds:0.0} s on {bot.Address}");
        Dictionary<string, LiveBot> bots = [];
        List<LiveAppClient> apps = [];
        try
        {
            Assert.True(SpinWait.SpinUntil(() => bot.Engine.ReadyChannels.Count == 6, TimeSpan.FromSeconds(60)), "every relay link ready: " + string.Join(",", bot.Engine.ReadyChannels));
            output.WriteLine($"6 relays ready after {clock.Elapsed.TotalSeconds:0.0} s");

            // ---- test bots
            foreach (var (name, _) in Seats)
            {
                bots[name] = new LiveBot(name, secrets.TryGet(name, out var token) ? token : throw new InvalidOperationException(name), guild, clock, warnings);
            }
            await Task.WhenAll(bots.Values.Select(b => b.ConnectAsync()));
            await Task.WhenAll(Seats.Select(seat => bots[seat.Name].JoinAsync(channel[seat.Channel])));
            output.WriteLine($"test bots in voice after {clock.Elapsed.TotalSeconds:0.0} s");
            Assert.True(SpinWait.SpinUntil(() => bot.Config.Bots.All(b => b.BotUserId is not null), TimeSpan.FromSeconds(15)), "the relay bots' IDs were learned and saved");
            var relayId = bot.Config.Bots.ToDictionary(b => b.TokenRef, b => b.BotUserId!.Value.V);
            Dictionary<ulong, string> nameOf = [];
            foreach (var (n, id) in relayId)
            {
                nameOf[id] = n;
            }
            foreach (var b in bots.Values)
            {
                nameOf[b.Id] = b.Name;
            }

            // ---- the apps
            var host = $"127.0.0.1:{bot.Address.Port}";
            LiveAppClient app1 = new(), app2 = new();
            apps.AddRange([app1, app2]);
            await app1.ConnectAsync(bot.Address, bot.Codes.Issue(new UserId(bots["tester1"].Id), host));
            await app2.ConnectAsync(bot.Address, bot.Codes.Issue(new UserId(bots["tester2"].Id), host));
            Assert.True(SpinWait.SpinUntil(() => app1.LastStatus?.ChannelName is not null && app2.LastStatus?.ChannelName is not null, TimeSpan.FromSeconds(20)), "both apps were told where they are");
            var idle1 = app1.LastStatus!;
            var idle2 = app2.LastStatus!;
            output.WriteLine($"welcome tester1: '{app1.Welcome!.DisplayName}'; status: channel '{idle1.ChannelName}' group {idle1.Group} warnings [{string.Join(",", idle1.Warnings)}]");
            output.WriteLine($"welcome tester2: '{app2.Welcome!.DisplayName}'; status: channel '{idle2.ChannelName}' group {idle2.Group} warnings [{string.Join(",", idle2.Warnings)}]");

            // ---- scenarios
            if (int.TryParse(Environment.GetEnvironmentVariable("KZG_LIVE_SETTLE_SECONDS"), out var settle) && settle > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(settle));
            }
            List<ScenarioResult> results = [];
            results.Add(await RunScenario("A: tester1 speaks in Attack 1", [(bots["tester1"], app1, clip1)]));
            results.Add(await RunScenario("B: tester2 speaks in Defense 1", [(bots["tester2"], app2, clip2)]));
            results.Add(await RunScenario("C: both at once", [(bots["tester1"], app1, clip1), (bots["tester2"], app2, clip2)]));

            async Task<ScenarioResult> RunScenario(string title, (LiveBot Speaker, LiveAppClient App, Clip Clip)[] speakers)
            {
                var from = clock.Elapsed.TotalMilliseconds;
                foreach (var s in speakers)
                {
                    await s.App.PressAsync(true);
                }
                await Task.Delay(400);
                var sends = await Task.WhenAll(speakers.Select(s => s.Speaker.SpeakAsync(s.Clip)));
                var during = speakers.Select(s => s.App.LastStatus).ToArray();
                await Task.Delay(1500);
                foreach (var s in speakers)
                {
                    await s.App.PressAsync(false);
                }
                await Task.Delay(2500);
                var to = clock.Elapsed.TotalMilliseconds;
                var sentAt = new Dictionary<ulong, double>();
                foreach (var d in sends)
                {
                    foreach (var (hash, at) in d)
                    {
                        sentAt[hash] = at;
                    }
                }
                return Analyse(title, from, to, speakers.Select(s => (s.Speaker.Name, s.Clip)).ToArray(), sentAt, during);
            }

            ScenarioResult Analyse(string title, double from, double to, (string Speaker, Clip Clip)[] speaking, Dictionary<ulong, double> sentAt, StatusMessage?[] during)
            {
                var hashClip = clips.SelectMany(c => c.Frames.Select(f => (f.Hash, c.Name))).ToDictionary(p => p.Hash, p => p.Name);
                var speakerNames = speaking.Select(s => s.Speaker).ToHashSet();
                var report = new ScenarioResult(title, [], [], [], [], during.Select(d => d is null ? "none" : $"{d.ChannelName} -> [{string.Join(", ", d.Targets)}] active [{string.Join(",", d.Active)}] warnings [{string.Join(",", d.Warnings)}]").ToArray());
                // Who should be heard where: a listener hears the speaker in its own VC directly, and the relay of its group's other VCs.
                var groupOf = new Dictionary<string, string> { ["A1"] = "attack", ["A2"] = "attack", ["A3"] = "attack", ["D1"] = "defense", ["D2"] = "defense", ["D3"] = "defense" };
                foreach (var (name, seat) in Seats.Where(s => s.Name.StartsWith("listener", StringComparison.Ordinal)))
                {
                    var heard = bots[name].Heard.Where(h => h.At >= from && h.At <= to).ToArray();
                    string? expectSender = null;
                    string? expectClip = null;
                    foreach (var (speaker, clip) in speaking)
                    {
                        var speakerSeat = Seats.First(s => s.Name == speaker).Channel;
                        if (speakerSeat == seat)
                        {
                            (expectSender, expectClip) = (speaker, clip.Name);
                        }
                        else if (groupOf[speakerSeat] == groupOf[seat])
                        {
                            (expectSender, expectClip) = ("relay" + (Array.IndexOf(["A1", "A2", "A3", "D1", "D2", "D3"], seat) + 1), clip.Name);
                        }
                    }
                    foreach (var senderGroup in heard.GroupBy(h => h.From))
                    {
                        var sender = nameOf.GetValueOrDefault(senderGroup.Key, senderGroup.Key.ToString());
                        var matched = senderGroup.Count(h => hashClip.ContainsKey(h.Hash));
                        var byClip = senderGroup.Where(h => hashClip.ContainsKey(h.Hash)).GroupBy(h => hashClip[h.Hash]).ToDictionary(g => g.Key, g => g.Select(h => h.Hash).Distinct().Count());
                        report.Heard.Add(new HeardRow(name, seat, sender, senderGroup.Count(), matched, string.Join(",", byClip.Select(p => $"{p.Key}:{p.Value}"))));
                        if (sender != expectSender)
                        {
                            report.Violations.Add($"{name} in {seat} heard {senderGroup.Count()} frame(s) from {sender}; expected {(expectSender ?? "nothing")}");
                        }
                        else if (matched != senderGroup.Count())
                        {
                            report.Violations.Add($"{name} heard {senderGroup.Count() - matched} frame(s) from {sender} that are not the clip");
                        }
                    }
                    if (expectSender is not null)
                    {
                        var distinct = heard.Where(h => nameOf.GetValueOrDefault(h.From) == expectSender && hashClip.GetValueOrDefault(h.Hash) == expectClip).Select(h => h.Hash).Distinct().Count();
                        var needed = (int)(clips.First(c => c.Name == expectClip).Frames.Count * (expectSender.StartsWith("relay", StringComparison.Ordinal) ? 0.9 : 0.97));
                        if (distinct < needed)
                        {
                            report.Violations.Add($"{name} in {seat} heard only {distinct} of {clips.First(c => c.Name == expectClip).Frames.Count} {expectClip} frames from {expectSender} (needs {needed})");
                        }
                        report.Coverage[$"{name}<-{expectSender}"] = distinct;
                        var got = heard.Where(h => nameOf.GetValueOrDefault(h.From) == expectSender).Select(h => h.Hash).ToHashSet();
                        var missing = clips.First(c => c.Name == expectClip).Frames.Select((f, i) => (f.Hash, i)).Where(p => !got.Contains(p.Hash)).Select(p => p.i).ToArray();
                        // The start of the call is what the pre-roll protects: the first words must not be cut.
                        var cutStart = missing.Where(i => i < LeadingFramesThatMustArrive).ToArray();
                        if (cutStart.Length > 0)
                        {
                            report.Violations.Add($"{name} in {seat} lost the start of the burst: clip frames {string.Join(",", cutStart)} from {expectSender}");
                        }
                        if (missing.Length > 0)
                        {
                            report.Missing.Add($"{name} missed clip frames {string.Join(",", missing.Take(12))}{(missing.Length > 12 ? "..." : "")} ({missing.Length})");
                        }
                    }
                }
                // Added latency: a relayed frame's arrival minus the same frame's arrival in the speaker's own VC.
                foreach (var (speaker, clip) in speaking)
                {
                    var speakerSeat = Seats.First(s => s.Name == speaker).Channel;
                    var direct = bots[Seats.First(s => s.Channel == speakerSeat && s.Name.StartsWith("listener", StringComparison.Ordinal)).Name].Heard
                        .Where(h => h.At >= from && h.At <= to && nameOf.GetValueOrDefault(h.From) == speaker)
                        .GroupBy(h => h.Hash).ToDictionary(g => g.Key, g => g.Min(h => h.At));
                    foreach (var (name, seat) in Seats.Where(s => s.Name.StartsWith("listener", StringComparison.Ordinal) && s.Channel != speakerSeat && groupOf[s.Channel] == groupOf[speakerSeat]))
                    {
                        foreach (var h in bots[name].Heard.Where(h => h.At >= from && h.At <= to && direct.ContainsKey(h.Hash)))
                        {
                            report.AddedMs.Add(h.At - direct[h.Hash]);
                            if (sentAt.TryGetValue(h.Hash, out var sent))
                            {
                                report.EndToEndMs.Add(h.At - sent);
                            }
                        }
                    }
                }
                return report;
            }

            // ---- slash commands registered on the server
            var commandNames = await RegisteredCommands(secrets, config, guild);
            output.WriteLine("registered slash commands: " + string.Join(", ", commandNames));

            // ---- what the bot counted
            foreach (var state in gateway!.Bots.OrderBy(b => b.TokenRef))
            {
                output.WriteLine($"bot {state.TokenRef}: {state.State}, frames sent {state.FramesSent}, voice decrypt failures {state.DecryptFailures}");
            }
            foreach (var link in gateway!.Links.OfType<Kzg.Discord.Voice.RelayLink>().OrderBy(l => l.Channel.V))
            {
                output.WriteLine($"link {link.Channel} (bot {link.BotUserId}): sent {link.FramesSent}, send errors {link.SendErrors}, not ready {link.SentNotReady}, unknown streams {link.UnknownSsrc}, own bots dropped {link.DroppedOwn}");
            }
            var stats = bot.Engine.Stats;
            output.WriteLine($"engine: ticks {stats.Ticks}, frames sent {stats.FramesSent}, packets dropped {stats.PacketsDropped}, decode errors {stats.DecodeErrors}, send errors {stats.SendErrors}, tick faults {stats.TickFaults}, resyncs {stats.Resyncs}");

            foreach (var r in results)
            {
                output.WriteLine($"== {r.Title}");
                output.WriteLine($"   app status while speaking: {string.Join(" | ", r.AppStatus)}");
                foreach (var row in r.Heard.OrderBy(h => h.Listener).ThenBy(h => h.Sender))
                {
                    output.WriteLine($"   {row.Listener,-10} in {row.Seat}: {row.Frames,4} frames from {row.Sender} ({row.Matched} are clip frames {row.PerClip})");
                }
                var silent = Seats.Where(s => s.Name.StartsWith("listener", StringComparison.Ordinal) && r.Heard.All(h => h.Listener != s.Name)).Select(s => $"{s.Name}@{s.Channel}");
                output.WriteLine($"   heard nothing: {string.Join(", ", silent)}");
                output.WriteLine($"   added latency (relay minus direct, ms): {Describe(r.AddedMs)}");
                output.WriteLine($"   end to end from the tester's send (ms): {Describe(r.EndToEndMs)}");
                foreach (var missed in r.Missing)
                {
                    output.WriteLine("   " + missed);
                }
                foreach (var violation in r.Violations)
                {
                    output.WriteLine("   VIOLATION: " + violation);
                }
            }
            var unknown = bots.Values.Sum(b => Interlocked.Read(ref b.UnknownSsrc));
            output.WriteLine($"frames from unknown streams at the listeners: {unknown}; warnings from NetCord: {warnings.Count}");
            foreach (var line in warnings.ToArray().GroupBy(w => w).Select(g => $"{g.Count()} x {g.Key}").Take(40))
            {
                output.WriteLine("   " + line);
            }

            await File.WriteAllTextAsync(Path.Combine(runDir, "summary.json"), JsonSerializer.Serialize(new { results, commandNames, stats }, new JsonSerializerOptions { WriteIndented = true }));

            Assert.Equal(Kzg.Discord.Commands.CommandSpec.Build(config).Select(c => c.Name).Order(), commandNames.Order());
            Assert.All(results, r => Assert.Empty(r.Violations));
            Assert.Equal("Attack 1", idle1.ChannelName);
            Assert.Equal(WireGroup.Attack, idle1.Group);
            Assert.Equal("Defense 1", idle2.ChannelName);
            Assert.Equal(["Attack 2", "Attack 3"], StatusTargets(results[0].AppStatus[0]));
            Assert.Equal(["Defense 2", "Defense 3"], StatusTargets(results[1].AppStatus[0]));
        }
        finally
        {
            foreach (var app in apps)
            {
                await app.DisposeAsync();
            }
            await Task.WhenAll(bots.Values.Select(b => b.DisconnectAsync()));
            await stop.CancelAsync();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine($"bot exited {exit} after {clock.Elapsed.TotalSeconds:0.0} s; stderr: '{botError.ToString().Trim()}'");
            Assert.Equal(0, exit);
            await AssertNobodyInVoice(secrets, guild, [.. bot.Config.Bots.Select(b => b.BotUserId!.Value.V), .. bots.Values.Select(b => b.Id)]);
            await File.WriteAllTextAsync(Path.Combine(runDir, "bot.log"), botLog.ToString());
        }

        output.WriteLine($"log: {runDir}");
        _ = GuildName;
    }

    private async Task<string[]> RegisteredCommands(Secrets secrets, BotConfig config, ulong guild)
    {
        using var rest = new RestClient(new BotToken(secrets.TryGet("relay1", out var token) ? token : throw new InvalidOperationException("relay1")));
        var app = await rest.GetCurrentApplicationAsync();
        var commands = await rest.GetGuildApplicationCommandsAsync(app.Id, guild);
        var gvg = commands.Single(c => c.Name == "gvg");
        output.WriteLine($"/gvg default permission: {gvg.DefaultGuildPermissions}; sub-commands: {string.Join(", ", Leaves(gvg.Options, ""))}");
        // What Discord holds is what the handler reads: the same sub-commands, with the same options, as the spec.
        var spec = (SlashCommandProperties)Kzg.Discord.Commands.CommandSpec.Build(config).Single(c => c.Name == "gvg");
        Assert.Equal(
            Leaves(spec.Options, "").Order(),
            Leaves(gvg.Options, "").Order());
        return [.. commands.Select(c => c.Name)];
    }

    private static IEnumerable<string> Leaves(IEnumerable<ApplicationCommandOptionProperties>? options, string prefix)
    {
        foreach (var option in options ?? [])
        {
            if (option.Type == ApplicationCommandOptionType.SubCommand)
            {
                yield return $"{prefix}{option.Name}({string.Join(",", (option.Options ?? []).Select(o => o.Name))})";
            }
            else if (option.Type == ApplicationCommandOptionType.SubCommandGroup)
            {
                foreach (var leaf in Leaves(option.Options, prefix + option.Name + " "))
                {
                    yield return leaf;
                }
            }
        }
    }

    private static IEnumerable<string> Leaves(IReadOnlyList<ApplicationCommandOption>? options, string prefix)
    {
        foreach (var option in options ?? [])
        {
            if (option.Type == ApplicationCommandOptionType.SubCommand)
            {
                yield return $"{prefix}{option.Name}({string.Join(",", (option.Options ?? []).Select(o => o.Name))})";
            }
            else if (option.Type == ApplicationCommandOptionType.SubCommandGroup)
            {
                foreach (var leaf in Leaves(option.Options, prefix + option.Name + " "))
                {
                    yield return leaf;
                }
            }
        }
    }

    /// <summary>Every bot is out of voice once the run is over: Discord answers 404 for a user who isn't connected.</summary>
    private async Task AssertNobodyInVoice(Secrets secrets, ulong guild, ulong[] ids)
    {
        using var rest = new RestClient(new BotToken(secrets.TryGet("listener1", out var token) ? token : throw new InvalidOperationException("listener1")));
        List<ulong> still = [];
        foreach (var id in ids.Distinct())
        {
            // The leave takes a moment to reach the guild.
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

    private static string Describe(List<double> values)
    {
        if (values.Count == 0)
        {
            return "no frames";
        }
        var sorted = values.Order().ToArray();
        double At(double p) => sorted[Math.Min(sorted.Length - 1, (int)(p * sorted.Length))];
        return $"n={sorted.Length} p50={At(0.5):0.0} p95={At(0.95):0.0} p99={At(0.99):0.0} max={sorted[^1]:0.0}";
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
        throw new InvalidOperationException("Kzg.slnx not found above the test");
    }

    private sealed record HeardRow(string Listener, string Seat, string Sender, int Frames, int Matched, string PerClip);

    private sealed record ScenarioResult(string Title, List<HeardRow> Heard, List<string> Violations, List<double> AddedMs, List<double> EndToEndMs, string[] AppStatus)
    {
        public Dictionary<string, int> Coverage { get; } = [];

        public List<string> Missing { get; } = [];
    }
}
