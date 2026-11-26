using System.Collections.Concurrent;
using System.Diagnostics;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Discord;
using Kzg.Protocol;
using NetCord;
using NetCord.Rest;
using Xunit.Abstractions;

namespace Kzg.Bot.Tests.Live;

/// <summary>
/// A bot with Move Members drags a relay into another team's channel in the middle of a broadcast. The
/// relay must stop sending at once (nothing reaches the wrong channel but what was in flight), go back to
/// its own channel on its own, and carry on with the broadcast, with no frame heard twice anywhere.
/// Needs <c>KZG_LIVE=1</c> and a bot with Move Members: the secret <c>mover</c>, else the first of the test bots that has it.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class LiveMovedRelayTests(ITestOutputHelper output)
{
    private const int ClipSeconds = 14;

    // How long after the move completes a frame may still reach the wrong channel: it is what Discord had
    // already been given, plus the gateway's trip to tell the bot.
    private static readonly TimeSpan LeakAllowance = TimeSpan.FromMilliseconds(600);

    [LiveFact]
    public async Task A_relay_moved_mid_broadcast_goes_back_and_the_broadcast_resumes()
    {
        var repo = FindRepo();
        var data = Path.Combine(repo, ".data");
        var runDir = Path.Combine(data, "live", DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-moved-relay");
        Directory.CreateDirectory(runDir);
        var clock = Stopwatch.StartNew();
        ConcurrentQueue<string> warnings = new();

        var secrets = ((FileLoad<Secrets>.Loaded)new SecretsStore(Path.Combine(data, "secrets.json")).Load(Environment.GetEnvironmentVariables())).Value;
        var config = ((FileLoad<BotConfig>.Loaded)new ConfigStore(Path.Combine(data, "config.json")).Load(secrets)).Value;
        var guild = config.GuildId ?? throw new InvalidOperationException("the dev config names the guild");
        var attack = config.Groups.Attack.Channels.Select(c => c.V).ToArray();
        var defense = config.Groups.Defense.Channels.Select(c => c.V).ToArray();
        // Whichever of these bots may move members does it: the secret "mover" first, then the spare tester, then the others.
        string[] moverCandidates = ["mover", "tester3", "tester2", "listener1", "listener4", "listener6", "listener3", "listener5", "relay6", "relay5", "relay4", "relay3"];

        // The speaker is in Attack 1; the relay that gets dragged is Attack 2's, into Defense 2.
        var (speakerChannel, victimChannel, wrongChannel) = (attack[0], attack[1], defense[1]);
        var clip = Clip.Tone("move", ClipSeconds, 410, 77);
        Dictionary<string, (string Bot, ulong Channel)> seats = new()
        {
            ["speaker"] = ("tester1", speakerChannel),
            ["victim"] = ("listener2", victimChannel),
            ["sibling"] = ("listener3", attack[2]),
            ["wrong"] = ("listener5", wrongChannel),
        };

        var botLog = new StringWriter();
        var botOutput = TextWriter.Synchronized(botLog);
        var botError = new StringWriter();
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource<BotApp>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Program.RunAsync(
            ["--data-dir", data, "--listen", "127.0.0.1:0"], botOutput, botError, stop.Token, started.SetResult,
            (c, s, t) => new DiscordGateway(c, s, t));
        if (await Task.WhenAny(started.Task, run) == run)
        {
            Assert.Fail($"the bot stopped at start-up: {botError}");
        }
        var bot = await started.Task;
        Dictionary<string, LiveBot> bots = [];
        var app = new LiveAppClient(clock);
        using var watchStop = new CancellationTokenSource();
        Task? watching = null;
        try
        {
            Assert.True(SpinWait.SpinUntil(() => bot.Engine.ReadyChannels.Count == 6, TimeSpan.FromSeconds(60)), "every relay link ready");
            foreach (var (_, (name, _)) in seats)
            {
                bots[name] = new LiveBot(name, secrets.TryGet(name, out var token) ? token : throw new InvalidOperationException(name), guild, clock, warnings);
            }
            await Task.WhenAll(bots.Values.Select(b => b.ConnectAsync()));
            await Task.WhenAll(seats.Values.Select(s => bots[s.Bot].JoinAsync(s.Channel)));
            Assert.True(SpinWait.SpinUntil(() => bot.Config.Bots.All(b => b.BotUserId is not null), TimeSpan.FromSeconds(15)), "the relay bots' IDs were learned");
            var victimRelay = bot.Config.Bots.Single(b => b.ChannelId!.Value.V == victimChannel).BotUserId!.Value.V;
            Dictionary<ulong, string> nameOf = bot.Config.Bots.ToDictionary(b => b.BotUserId!.Value.V, b => "relay@" + b.ChannelId!.Value.V);
            foreach (var b in bots.Values)
            {
                nameOf[b.Id] = b.Name;
            }

            await app.ConnectAsync(bot.Address, bot.Codes.Issue(new UserId(bots["tester1"].Id), $"127.0.0.1:{bot.Address.Port}"));
            await app.WaitStatusAsync(s => s.ChannelName is not null);
            await Task.Delay(2000);

            // Watch the victim's link from outside: when the engine stops and resumes seeing it ready.
            List<(double At, bool Ready)> readiness = [];
            watching = Task.Run(async () =>
            {
                var last = true;
                while (!watchStop.IsCancellationRequested)
                {
                    var now = bot.Engine.ReadyChannels.Contains(new ChannelId(victimChannel));
                    if (now != last)
                    {
                        readiness.Add((clock.Elapsed.TotalMilliseconds, now));
                        last = now;
                    }
                    await Task.Delay(5);
                }
            });

            var from = clock.Elapsed.TotalMilliseconds;
            await app.PressAsync(true);
            await app.WaitStatusAsync(s => s.Active.Contains(WireAction.Group));
            var speaking = bots["tester1"].SpeakAsync(clip);

            await Task.Delay(4000);
            var moveStart = clock.Elapsed.TotalMilliseconds;
            string? moverName = null;
            foreach (var candidate in moverCandidates.Where(c => secrets.TryGet(c, out _)))
            {
                using var rest = new RestClient(new BotToken(secrets.TryGet(candidate, out var token) ? token : ""));
                try
                {
                    await rest.ModifyGuildUserAsync(guild, victimRelay, o => o.ChannelId = wrongChannel);
                    moverName = candidate;
                    break;
                }
                catch (RestException exception) when (exception.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    output.WriteLine($"{candidate} lacks Move Members");
                }
            }
            Assert.True(moverName is not null, "no bot with Move Members: add its token to secrets.json as 'mover'");
            var moveDone = clock.Elapsed.TotalMilliseconds;
            output.WriteLine($"{moverName} moved the Attack 2 relay into channel {wrongChannel} (REST call {moveDone - moveStart:0} ms)");

            await speaking;
            await Task.Delay(500);
            await app.PressAsync(false);
            await Task.Delay(2500);
            var to = clock.Elapsed.TotalMilliseconds;
            await watchStop.CancelAsync();
            await watching;

            // ---- what each listener heard
            var clipHashes = clip.IndexByHash();
            IEnumerable<Heard> HeardBy(string seat) => bots[seats[seat].Bot].Heard.Where(h => h.At >= from && h.At <= to);
            var relayName = nameOf[victimRelay];
            var violations = new List<string>();

            var notReady = readiness.FirstOrDefault(r => !r.Ready);
            var readyAgain = readiness.FirstOrDefault(r => r.Ready && r.At > notReady.At);
            output.WriteLine($"engine saw the moved relay not ready {notReady.At - moveDone:0} ms after the move call returned, ready again {readyAgain.At - moveDone:0} ms after it");
            if (notReady.At == 0)
            {
                violations.Add("the engine never saw the moved relay go not ready");
            }
            if (readyAgain.At == 0)
            {
                violations.Add("the moved relay never became ready again");
            }

            // Nothing in the wrong channel but what was already in flight.
            var inWrong = HeardBy("wrong").Where(h => h.From == victimRelay).ToArray();
            var leaked = inWrong.Where(h => h.At > moveDone + LeakAllowance.TotalMilliseconds).ToArray();
            output.WriteLine($"wrong channel: {inWrong.Length} frame(s) from the moved relay, {leaked.Length} later than {LeakAllowance.TotalMilliseconds:0} ms after the move; last at {(inWrong.Length == 0 ? "-" : (inWrong.Max(h => h.At) - moveDone).ToString("0"))} ms");
            if (leaked.Length > 0)
            {
                violations.Add($"{leaked.Length} frame(s) reached the wrong channel more than {LeakAllowance.TotalMilliseconds:0} ms after the move");
            }
            foreach (var group in HeardBy("wrong").GroupBy(h => h.From).Where(g => g.Key != victimRelay))
            {
                violations.Add($"the wrong channel heard {group.Count()} frame(s) from {nameOf.GetValueOrDefault(group.Key, group.Key.ToString())}");
            }

            // The victim's own channel: frames until the move, a gap, then the rest of the broadcast.
            var victimHeard = HeardBy("victim").Where(h => h.From == victimRelay && clipHashes.ContainsKey(h.Hash)).ToArray();
            var afterBack = readyAgain.At == 0 ? [] : victimHeard.Where(h => h.At > readyAgain.At).ToArray();
            var gap = afterBack.Length == 0 ? double.NaN : afterBack.Min(h => h.At) - moveDone;
            output.WriteLine($"Attack 2 heard {victimHeard.Length} clip frames from its relay; {afterBack.Length} after it came back; first of them {gap:0} ms after the move");
            if (afterBack.Length < 100)
            {
                violations.Add($"the relay carried on with only {afterBack.Length} frame(s) after coming back");
            }
            if (gap > 5000)
            {
                violations.Add($"the relay took {gap:0} ms after the move to be heard again");
            }
            var lastIndex = clipHashes.Where(p => victimHeard.Any(h => h.Hash == p.Key)).Select(p => p.Value).DefaultIfEmpty(-1).Max();
            output.WriteLine($"Attack 2's last clip frame heard: {lastIndex} of {clip.Frames.Count - 1}");
            if (lastIndex < clip.Frames.Count - 10)
            {
                violations.Add($"the broadcast did not run to its end in Attack 2: last frame {lastIndex} of {clip.Frames.Count - 1}");
            }

            // The sibling that never lost its relay hears the whole call.
            var siblingRelay = bot.Config.Bots.Single(b => b.ChannelId!.Value.V == attack[2]).BotUserId!.Value.V;
            var siblingFrames = HeardBy("sibling").Where(h => h.From == siblingRelay && clipHashes.ContainsKey(h.Hash)).Select(h => h.Hash).Distinct().Count();
            output.WriteLine($"Attack 3 heard {siblingFrames} of {clip.Frames.Count} clip frames");
            if (siblingFrames < clip.Frames.Count * 0.9)
            {
                violations.Add($"Attack 3, whose relay was left alone, heard only {siblingFrames} of {clip.Frames.Count} frames");
            }

            // No echo: nobody hears one frame twice from the same sender.
            foreach (var seat in seats.Keys)
            {
                var twice = HeardBy(seat).GroupBy(h => (h.From, h.Hash)).Count(g => g.Count() > 1);
                if (twice > 0)
                {
                    violations.Add($"{seat} heard {twice} frame(s) twice");
                }
            }
            // The speaker's own channel hears the speaker and no relay.
            foreach (var group in HeardBy("speaker").GroupBy(h => h.From).Where(g => g.Key != bots["tester1"].Id))
            {
                violations.Add($"the speaker's channel heard {group.Count()} frame(s) from {nameOf.GetValueOrDefault(group.Key, group.Key.ToString())}");
            }
            output.WriteLine($"bot log lines about the move: {string.Join(" | ", botLog.ToString().Split('\n').Where(l => l.Contains("moved out of", StringComparison.Ordinal)).Select(l => l[25..].Trim()))}");
            foreach (var violation in violations)
            {
                output.WriteLine("VIOLATION: " + violation);
            }
            Assert.Equal(relayName, nameOf[victimRelay]);
            Assert.Empty(violations);
        }
        finally
        {
            await watchStop.CancelAsync();
            await app.DisposeAsync();
            await Task.WhenAll(bots.Values.Select(b => b.DisconnectAsync()));
            await stop.CancelAsync();
            var exit = await run.WaitAsync(TimeSpan.FromSeconds(60));
            output.WriteLine($"bot exited {exit}; stderr: '{botError.ToString().Trim()}'");
            await File.WriteAllTextAsync(Path.Combine(runDir, "bot.log"), botLog.ToString());
            Assert.Equal(0, exit);
            await AssertNobodyInVoice(secrets, guild, [.. bot.Config.Bots.Select(b => b.BotUserId!.Value.V), .. bots.Values.Select(b => b.Id)]);
        }
    }

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
        throw new InvalidOperationException("Kzg.slnx not found above the test");
    }
}
