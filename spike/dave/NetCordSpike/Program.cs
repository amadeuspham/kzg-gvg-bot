using System.Collections.Concurrent;
using System.Text.Json;
using Kzg.Core.Config;
using Kzg.Spike.Dave;
using NetCord;
using NetCord.Gateway;
using NetCord.Gateway.Voice;

// Step 0a spike: NetCord + libdave. All bots in one process, so one clock times every hop.
// Attack 1: relay1 + tester1..3 (speakers). Attack 2: relay2 + listener1. relay1 forwards tester1 to relay2.

var soakSeconds = ArgInt("--soak", 600);
var runEvents = !args.Contains("--no-events");
const string GuildName = "KZG GvG Test";

var repo = FindRepo();
var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
var runDir = Path.Combine(repo, ".data", "dave", $"netcord-{stamp}");
Directory.CreateDirectory(runDir);
Log.Open(Path.Combine(runDir, "run.log"));
Log.Info($"run dir {runDir}; soak {soakSeconds}s; events {runEvents}; NetCord {typeof(GatewayClient).Assembly.GetName().Version}");

// ---- secrets, by name only ----
var load = new SecretsStore(Path.Combine(repo, ".data", "secrets.json")).Load(Environment.GetEnvironmentVariables());
if (load is not FileLoad<Secrets>.Loaded { Value: var secrets })
{
    Log.Warn($"secrets did not load: {load.GetType().Name}");
    return 2;
}
string[] needed = ["relay1", "relay2", "relay3", "tester1", "tester2", "tester3", "listener1"];
foreach (var name in secrets.Names)
{
    if (secrets.TryGet(name, out var value))
    {
        Log.AddSecret(name, value);
    }
}
var missing = needed.Where(n => !secrets.Has(n)).ToList();
if (missing.Count > 0)
{
    Log.Warn($"missing secrets: {string.Join(", ", missing)}");
    return 2;
}
string Token(string name) => secrets.TryGet(name, out var v) ? v : throw new InvalidOperationException(name);
Log.Info($"secrets: {secrets}");

// ---- clips: three distinct TTS clips, dithered so every 20 ms frame encodes to unique bytes ----
var clips = new List<(byte[] Data, ulong Hash)>[3];
Dictionary<ulong, int> clipOwner = [];
var collisions = 0;
using (var encoder = new OpusEncoder(VoiceChannels.Stereo, OpusApplication.Audio))
{
    var random = new Random(7);
    var outBuf = new byte[4000];
    for (var i = 0; i < 3; i++)
    {
        var raw = File.ReadAllBytes(Path.Combine(repo, ".data", "dave", "clips", $"t{i + 1}.raw"));
        var pcm = new short[raw.Length / 2];
        Buffer.BlockCopy(raw, 0, pcm, 0, raw.Length);
        clips[i] = [];
        for (var offset = 0; offset + 1920 <= pcm.Length; offset += 1920)
        {
            var frame = pcm.AsSpan(offset, 1920).ToArray();
            for (var s = 0; s < frame.Length; s++)
            {
                frame[s] = (short)Math.Clamp(frame[s] + random.Next(-3, 4), short.MinValue, short.MaxValue);
            }
            var length = encoder.Encode(frame, 960, outBuf);
            var data = outBuf.AsSpan(0, length).ToArray();
            var hash = Fnv(data);
            if (!clipOwner.TryAdd(hash, i))
            {
                collisions++;
            }
            clips[i].Add((data, hash));
        }
    }
}
Log.Info($"clips: {string.Join(", ", clips.Select((c, i) => $"t{i + 1}={c.Count} frames/{c.Sum(f => f.Data.Length) * 8.0 / (c.Count * 0.02) / 1000:F0} kbps"))}; hash collisions {collisions}");

using var metrics = new VoiceMetrics();

// ---- bots ----
var relay1 = new Bot("relay1");
var relay2 = new Bot("relay2");
var testers = new[] { new Bot("tester1"), new Bot("tester2"), new Bot("tester3") };
var listener = new Bot("listener1");
Bot[] all = [relay1, relay2, .. testers, listener];
var joinTimeout = TimeSpan.FromSeconds(20);

await Task.WhenAll(all.Select(b => b.StartGatewayAsync(Token(b.Name), GuildName, TimeSpan.FromSeconds(30))));
var guild = relay1.Guild;
var names = all.ToDictionary(b => b.Id, b => b.Name);
Log.Info($"guild '{guild.Name}' id={guild.Id}; bots: {string.Join(", ", all.Select(b => $"{b.Name}={b.Id}"))}");
var voiceChannels = guild.Channels.Values.Where(c => c is VoiceGuildChannel).ToDictionary(c => c.Name, c => c.Id);
Log.Info($"voice channels: {string.Join(", ", voiceChannels.OrderBy(p => p.Key).Select(p => $"{p.Key}={p.Value}"))}");
var attack1 = voiceChannels["Attack 1"];
var attack2 = voiceChannels["Attack 2"];
string ChannelName(ulong? id) => id is null ? "(none)" : voiceChannels.FirstOrDefault(p => p.Value == id).Key ?? id.ToString()!;
string Who(ulong id) => names.TryGetValue(id, out var n) ? n : id.ToString();

// Initial presence snapshot, as relay1's GUILD_CREATE carried it.
Log.Info($"snapshot@relay1 GUILD_CREATE: {guild.VoiceStates.Count} in voice: [{string.Join(", ", guild.VoiceStates.Values.Select(v => $"{Who(v.UserId)}@{ChannelName(v.ChannelId)}"))}]");
var stale = guild.VoiceStates.Values.Where(v => names.ContainsKey(v.UserId)).ToList();
if (stale.Count > 0)
{
    Log.Warn($"our bots already in voice from an earlier run: {string.Join(", ", stale.Select(v => Who(v.UserId)))}");
}

ConcurrentQueue<(double T, ulong User, ulong? Channel)> presence = new();
relay1.Gateway.VoiceStateUpdate += state =>
{
    if (state.GuildId == guild.Id)
    {
        presence.Enqueue((Clock.Ms, state.UserId, state.ChannelId));
        Log.Info($"PresenceChanged({Who(state.UserId)}, {ChannelName(state.ChannelId)})");
    }
    return default;
};

// ---- receive: handlers copy and enqueue only ----
HashSet<ulong> relayIds = [relay1.Id, relay2.Id];
ConcurrentQueue<Rx> rxRelay = new();
ConcurrentQueue<Rx> rxListener = new();
ConcurrentQueue<(byte[] Data, ulong Hash, double RxT)> forward = new();
long droppedOwn = 0;
var tester1Id = testers[0].Id;
relay1.Frame += (bot, ssrc, seq, ts, frame) =>
{
    var t = Clock.Ms;
    var user = bot.Voice is { } v && v.Cache.SsrcUsers.TryGetValue(ssrc, out var u) ? u : 0UL;
    if (relayIds.Contains(user))
    {
        Interlocked.Increment(ref droppedOwn);
        return;
    }
    var data = frame.ToArray();
    var hash = Fnv(data);
    rxRelay.Enqueue(new Rx(t, ssrc, user, seq, ts, hash, data.Length, null));
    if (user == tester1Id && !IsSilence(data))
    {
        forward.Enqueue((data, hash, t));
    }
};
listener.Frame += (bot, ssrc, seq, ts, frame) =>
{
    var t = Clock.Ms;
    var user = bot.Voice is { } v && v.Cache.SsrcUsers.TryGetValue(ssrc, out var u) ? u : 0UL;
    var data = frame.ToArray();
    rxListener.Enqueue(new Rx(t, ssrc, user, seq, ts, Fnv(data), data.Length, data));
};

// ---- join: relays first, then the people ----
var joinStart = Clock.Ms;
await Task.WhenAll(relay1.JoinAsync(attack1, joinTimeout), relay2.JoinAsync(attack2, joinTimeout));
Log.Info($"relays in voice after {(Clock.Ms - joinStart) / 1000:F1}s: relay1 dave v{relay1.DaveVersion()}, relay2 dave v{relay2.DaveVersion()}");
await Task.WhenAll(testers.Select(t => t.JoinAsync(attack1, joinTimeout)).Append(listener.JoinAsync(attack2, joinTimeout)));
Log.Info($"everyone in voice after {(Clock.Ms - joinStart) / 1000:F1}s");

// ---- 20 ms tick: testers speak in staggered spurts; relay1 -> relay2 forwarder drains one frame per tick ----
List<Tx> tx = [];
List<Fwd> fwd = [];
var stop = false;
var talkEnabled = true;
long fwdDropped = 0, lateTicks = 0;
double maxLate = 0;
var tickThread = new Thread(() =>
{
    var start = Clock.Ms;
    var pos = new int[3];
    var silenceLeft = new int[3];
    int fwdSilence = 0, emptyTicks = 0;
    for (long k = 0; !Volatile.Read(ref stop); k++)
    {
        var target = start + k * 20;
        while (Clock.Ms < target - 1.5)
        {
            Thread.Sleep(1);
        }
        while (Clock.Ms < target)
        {
            Thread.SpinWait(20);
        }
        var now = Clock.Ms;
        if (now - target > 5)
        {
            lateTicks++;
        }
        maxLate = Math.Max(maxLate, now - target);
        for (var i = 0; i < 3; i++)
        {
            var bot = testers[i];
            var phase = ((now - start) / 1000 + i * 2.5) % 7.5;
            if (talkEnabled && bot.Ready && phase < 6.0)
            {
                var clip = clips[i];
                var frame = clip[pos[i]++ % clip.Count];
                if (bot.Send(frame.Data))
                {
                    lock (tx)
                    {
                        tx.Add(new Tx(i, now, frame.Hash));
                    }
                }
                silenceLeft[i] = 5;
            }
            else if (silenceLeft[i] > 0)
            {
                bot.Send(Bot.Silence);
                silenceLeft[i]--;
            }
            bot.Tick();
        }
        while (forward.Count > 5 && forward.TryDequeue(out _))
        {
            fwdDropped++;
        }
        if (forward.TryDequeue(out var item))
        {
            if (relay2.Send(item.Data))
            {
                lock (fwd)
                {
                    fwd.Add(new Fwd(now, item.Hash, item.RxT));
                }
            }
            emptyTicks = 0;
            fwdSilence = 5;
        }
        else if (++emptyTicks >= 3 && fwdSilence > 0)
        {
            relay2.Send(Bot.Silence);
            fwdSilence--;
        }
        relay2.Tick();
    }
})
{ IsBackground = true, Priority = ThreadPriority.Highest, Name = "tick" };
tickThread.Start();

// ---- soak ----
await Task.Delay(5000);
var soakStart = Clock.Ms;
Log.Info($"SOAK start, {soakSeconds}s");
for (var s = 0; s < soakSeconds; s += 60)
{
    await Task.Delay(TimeSpan.FromSeconds(Math.Min(60, soakSeconds - s)));
    Log.Info($"soak {(Clock.Ms - soakStart) / 1000:F0}s: relay rx {rxRelay.Count}, listener rx {rxListener.Count}, tx {tx.Count}, fwd {fwd.Count}, dave r1 v{relay1.DaveVersion()} r2 v{relay2.DaveVersion()} t1 v{testers[0].DaveVersion()} l1 v{listener.DaveVersion()}");
}
var soakEnd = Clock.Ms;
Log.Info("SOAK end");

// ---- events: join/leave, reconnects ----
List<EventMark> events = [];
async Task Event(string name, Func<Task> act, int settleSeconds)
{
    var start = Clock.Ms;
    Log.Info($"EVENT {name} start");
    string? error = null;
    try
    {
        await act();
    }
    catch (Exception exception)
    {
        error = $"{exception.GetType().Name}: {exception.Message}";
        Log.Warn($"EVENT {name} failed: {error}");
    }
    var acted = Clock.Ms;
    await Task.Delay(TimeSpan.FromSeconds(settleSeconds));
    events.Add(new EventMark(name, start, acted, Clock.Ms, error));
    Log.Info($"EVENT {name} end ({(acted - start) / 1000:F1}s action, settle {settleSeconds}s)");
}

if (runEvents)
{
    await Event("tester3 leaves, rejoins after 15s", async () =>
    {
        await testers[2].LeaveAsync();
        await Task.Delay(15000);
        await testers[2].JoinAsync(attack1, joinTimeout);
    }, 30);
    await Event("listener1 leaves, rejoins after 10s", async () =>
    {
        await listener.LeaveAsync();
        await Task.Delay(10000);
        await listener.JoinAsync(attack2, joinTimeout);
    }, 20);
    await Event("relay1 voice websocket cut (library resume)", () =>
    {
        relay1.VoiceSocket.KillCurrent();
        return Task.CompletedTask;
    }, 25);
    await Event("relay1 voice UDP black-holed 8s", async () =>
    {
        relay1.Udp.Blackholed = true;
        await Task.Delay(8000);
        relay1.Udp.Blackholed = false;
    }, 25);
    await Event("relay1 gateway websocket cut (gateway resume)", () =>
    {
        relay1.GatewaySocket.KillCurrent();
        return Task.CompletedTask;
    }, 20);
    await Event("relay2 full voice rejoin (new VoiceClient)", async () =>
    {
        await relay2.LeaveAsync();
        await relay2.JoinAsync(attack2, joinTimeout);
    }, 20);
    await Event("relay3 starts late: initial presence snapshot", async () =>
    {
        var late = new Bot("relay3");
        await late.StartGatewayAsync(Token("relay3"), GuildName, TimeSpan.FromSeconds(30));
        var states = late.Guild.VoiceStates.Values.ToList();
        Log.Info($"snapshot@relay3 GUILD_CREATE: [{string.Join(", ", states.Select(v => $"{Who(v.UserId)}@{ChannelName(v.ChannelId)}"))}]");
        var expected = new Dictionary<ulong, ulong> { [relay1.Id] = attack1, [relay2.Id] = attack2, [listener.Id] = attack2 };
        foreach (var t in testers)
        {
            expected[t.Id] = attack1;
        }
        var ok = expected.All(p => states.Any(v => v.UserId == p.Key && v.ChannelId == p.Value));
        Log.Info($"snapshot@relay3 matches the 6 bots' known positions: {ok}");
        await late.Gateway.CloseAsync();
        late.Gateway.Dispose();
    }, 2);
}

// ---- stop and disconnect everyone ----
Volatile.Write(ref stop, true);
tickThread.Join();
await Task.Delay(1500);
var endMs = Clock.Ms;
await Task.WhenAll(all.Select(b => b.StopAsync()));
Log.Info("all bots left voice and closed their gateways");

// Confirm from a fresh connection that none of our bots is still in voice.
{
    var check = new Bot("relay3");
    await check.StartGatewayAsync(Token("relay3"), GuildName, TimeSpan.FromSeconds(30));
    var leftover = check.Guild.VoiceStates.Values.Where(v => names.ContainsKey(v.UserId)).Select(v => Who(v.UserId)).ToList();
    Log.Info($"post-run voice check: {(leftover.Count == 0 ? "no bot in voice" : "STILL IN VOICE: " + string.Join(", ", leftover))}");
    await check.Gateway.CloseAsync();
    check.Gateway.Dispose();
}

// ---- analysis ----
var report = Analysis.Run(new Analysis.Input(
    tx, [.. rxRelay], [.. rxListener], fwd, [.. presence], events, clipOwner,
    testers.Select(t => t.Id).ToArray(), relay2.Id, soakStart, soakEnd, endMs, runDir));
report["droppedOwnBotPackets"] = Interlocked.Read(ref droppedOwn);
report["forwarderDroppedForDepth"] = fwdDropped;
report["tickLateOver5ms"] = lateTicks;
report["tickMaxLateMs"] = Math.Round(maxLate, 2);
report["sendErrors"] = all.ToDictionary(b => b.Name, b => b.SendErrors);
report["voiceReady"] = all.ToDictionary(b => b.Name, b => b.VoiceReadyCount);
report["voiceResumed"] = all.ToDictionary(b => b.Name, b => b.VoiceResumeCount);
report["voiceDisconnects"] = all.ToDictionary(b => b.Name, b => b.VoiceDisconnectCount);
report["netcordUdpMetrics"] = metrics.Counts.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);
report["netcordWarnings"] = NetLogger.WarningCounts.OrderBy(p => p.Key).ToDictionary(p => p.Key, p => p.Value);
report["logScrubHits"] = Log.ScrubHits;
var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
File.WriteAllText(Path.Combine(runDir, "summary.json"), json);
Log.Info("summary written");
Console.WriteLine(json);
Log.Close();
return 0;

int ArgInt(string name, int fallback)
{
    var i = Array.IndexOf(args, name);
    return i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var v) ? v : fallback;
}

static string FindRepo()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Kzg.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? throw new InvalidOperationException("repo root not found");
}

static ulong Fnv(ReadOnlySpan<byte> data)
{
    var hash = 14695981039346656037UL;
    foreach (var b in data)
    {
        hash = (hash ^ b) * 1099511628211UL;
    }
    return hash;
}

static bool IsSilence(byte[] data) => data.AsSpan().SequenceEqual(Bot.Silence);

public readonly record struct Tx(int Tester, double T, ulong Hash);
public readonly record struct Rx(double T, uint Ssrc, ulong User, ushort Seq, uint RtpTs, ulong Hash, int Len, byte[]? Data);
public readonly record struct Fwd(double T, ulong Hash, double RxT);
public readonly record struct EventMark(string Name, double Start, double Acted, double End, string? Error);
