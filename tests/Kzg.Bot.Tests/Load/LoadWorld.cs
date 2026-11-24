using System.Collections.Concurrent;
using System.Diagnostics;
using Kzg.Audio;
using Kzg.Bot.Engine;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Testing;

namespace Kzg.Bot.Tests.Load;

/// <summary>
/// The real engine and real Opus codec over a fake gateway with six bound VCs and thirty
/// talkers. One thread plays the talkers (speech, with arrival jitter and loss) and presses their keys; another
/// samples CPU and memory once the run is going.
/// </summary>
internal sealed class LoadWorld : IDisposable
{
    public const int VoiceChannels = 6;
    public const int PerChannel = 5;

    private static readonly long Hz = Stopwatch.Frequency;

    private readonly SystemTimeSource _time = new();
    private readonly FakeVoiceGateway _gateway;
    private readonly FakeVoiceChannelLink[] _links;
    private readonly AudioEngine _engine;
    private readonly List<Talker> _talkers = [];
    private readonly List<Controller> _controllers = [];
    private readonly PriorityQueue<Delivery, long> _deliveries = new();
    private readonly long[] _late;
    private readonly long[] _took;
    private readonly ConcurrentQueue<(long At, double LateMs)> _lateTicks = new();
    private long _engineCpu;
    private long _senderCpu;
    private int _tickIndex;
    private volatile bool _stop;

    public LoadWorld(SpeechBank bank, int seconds, int seed)
    {
        _gateway = new FakeVoiceGateway(_time);
        _links = [.. Enumerable.Range(0, VoiceChannels).Select(i => _gateway.AddLink(Channel(i)))];
        var config = Config();
        _engine = new AudioEngine(config, _gateway, new ConcentusOpusCodecFactory(), _time, new EngineOptions());
        var ticks = (seconds * 50) + 3000;
        (_late, _took) = (new long[ticks], new long[ticks]);
        _engine.TickObserver = (late, took) =>
        {
            var index = _tickIndex;
            if (index < _late.Length)
            {
                _late[index] = late.Ticks;
                _took[index] = took.Ticks;
                _tickIndex = index + 1;
            }
            if (late.TotalMilliseconds > 20)
            {
                _lateTicks.Enqueue((Stopwatch.GetTimestamp(), late.TotalMilliseconds));
            }
            Volatile.Write(ref _engineCpu, ThreadCpu.NowNanoseconds());
        };
        var random = new Random(seed);
        for (var i = 0; i < VoiceChannels * PerChannel; i++)
        {
            var user = new UserId(1001 + (ulong)i);
            // Channel i / 5; the first of each channel's five is a key holder or on open mic, as below.
            var channel = i / PerChannel;
            var broadcaster = i % PerChannel == 0;
            var voice = bank.Voices[i % bank.Voices.Count];
            var duty = broadcaster ? 0.9 : 0.2 + (random.NextDouble() * 0.4);
            _talkers.Add(new Talker(user, _links[channel], voice, duty, broadcaster, new Random(random.Next())));
            _gateway.Join(user, Channel(channel));
        }
        // Broadcasters: one player of each team on open mic, and two key holders in each (the first of channels 1, 2, 4 and 5).
        foreach (var index in new[] { 0, 3 })
        {
            _controllers.Add(new Controller(_talkers[index * PerChannel], BroadcastAction.OpenMic, 20, 40, 3, 6, new Random(random.Next())));
        }
        foreach (var index in new[] { 1, 2, 4, 5 })
        {
            _controllers.Add(new Controller(_talkers[index * PerChannel], BroadcastAction.Group, 6, 15, 1, 3, new Random(random.Next())));
        }
    }

    public int Talkers => _talkers.Count;

    public int Broadcasters => _controllers.Count;

    public AudioEngine Engine => _engine;

    private static ChannelId Channel(int index) => new((index < 3 ? 101UL : 198UL) + (ulong)index);

    /// <summary>Attack is the first three channels, defense the last three; the first talker of each team's first channel goes on open mic.</summary>
    private static BotConfig Config() => new()
    {
        Groups = new GroupsConfig
        {
            Attack = new GroupConfig { Channels = [Channel(0), Channel(1), Channel(2)] },
            Defense = new GroupConfig { Channels = [Channel(3), Channel(4), Channel(5)] },
        },
    };

    /// <summary>Plays the world for <paramref name="duration"/> and samples it; returns what it saw.</summary>
    public LoadResult Run(TimeSpan duration, TimeSpan warmUp, Action<string> log)
    {
        var start = Stopwatch.GetTimestamp();
        var cores = Environment.ProcessorCount;
        foreach (var talker in _talkers)
        {
            _engine.Connected(talker.User, _time.Now);
        }
        _engine.Start();
        var sender = new Thread(() => Play(start, duration)) { Name = "load-sender", Priority = ThreadPriority.AboveNormal };
        sender.Start();

        var process = Process.GetCurrentProcess();
        List<Second> seconds = [];
        List<MemorySample> memory = [];
        List<SystemSample> system = [];
        var (nextMinute, lastWall) = (start + (60 * Hz), DateTime.UtcNow);
        var (lastCpu, lastEngine, lastSender, lastAt) = (process.TotalProcessorTime, Volatile.Read(ref _engineCpu), Volatile.Read(ref _senderCpu), start);
        var nextSecond = start + Hz;
        var nextMemory = start;
        var warmUpTicks = (int)(warmUp.TotalSeconds * 50);
        var concealedAtWarmUp = -1L;
        while (Stopwatch.GetTimestamp() < start + (long)(duration.TotalSeconds * Hz))
        {
            Thread.Sleep(100);
            var now = Stopwatch.GetTimestamp();
            // The monotonic clock stands still while the Mac sleeps and the wall clock does not: a jump is a sleep.
            var wall = DateTime.UtcNow;
            if (wall - lastWall > TimeSpan.FromSeconds(2))
            {
                var gap = new SystemSample((now - start) / (double)Hz, double.NaN, 0, 0, 0, 0, (wall - lastWall).TotalSeconds);
                system.Add(gap);
                log($"{gap.At,6:0} s  SAMPLER GAP {gap.GapSeconds:0.0} s (the machine slept or the process was suspended)");
            }
            lastWall = wall;
            if (now - start >= (long)(warmUp.TotalSeconds * Hz))
            {
                if (concealedAtWarmUp < 0)
                {
                    concealedAtWarmUp = _engine.Stats.Concealed;
                }
            }
            if (now >= nextMemory)
            {
                nextMemory += 5 * Hz;
                process.Refresh();
                memory.Add(new MemorySample((now - start) / (double)Hz, GC.GetTotalMemory(false) / 1048576.0, process.WorkingSet64 / 1048576.0, GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalAllocatedBytes() / 1048576.0));
            }
            while (_lateTicks.TryDequeue(out var lateTick))
            {
                log($"{(lateTick.At - start) / (double)Hz,6:0.0} s  LATE TICK {lateTick.LateMs:0} ms (system load {HostProbe.LoadAverage():0.0})");
            }
            if (now >= nextMinute)
            {
                nextMinute += 60 * Hz;
                process.Refresh();
                var load = HostProbe.LoadAverage();
                var (level, free) = HostProbe.MemoryPressure();
                var info = GC.GetGCMemoryInfo();
                var minute = new SystemSample((now - start) / (double)Hz, load, level, free, info.GenerationInfo[2].SizeAfterBytes / 1048576.0 + (info.GenerationInfo[3].SizeAfterBytes / 1048576.0), _engine.PlayerBuffers, 0);
                system.Add(minute);
                log($"{minute.At,6:0} s  working set {process.WorkingSet64 / 1048576.0,6:0.0} MB  managed {GC.GetTotalMemory(false) / 1048576.0,6:0.0} MB  " +
                    $"live after last GC (gen2+LOH) {minute.LiveAfterGcMb,6:0.0} MB  GCs {GC.CollectionCount(0)}/{GC.CollectionCount(1)}/{GC.CollectionCount(2)}  player buffers {minute.PlayerBuffers}  " +
                    $"system load {load:0.00}  memory pressure level {level} (1 normal, 2 warning, 4 critical), {free}% free");
            }
            if (now >= nextSecond)
            {
                nextSecond += Hz;
                process.Refresh();
                var (cpu, engine, senderCpu) = (process.TotalProcessorTime, Volatile.Read(ref _engineCpu), Volatile.Read(ref _senderCpu));
                var span = (now - lastAt) / (double)Hz;
                seconds.Add(new Second(
                    (now - start) / (double)Hz,
                    (cpu - lastCpu).TotalSeconds / span,
                    (engine - lastEngine) / 1e9 / span,
                    (senderCpu - lastSender) / 1e9 / span));
                (lastCpu, lastEngine, lastSender, lastAt) = (cpu, engine, senderCpu, now);
                foreach (var link in _links)
                {
                    // The fake keeps every frame it is sent; a real link doesn't, and the memory curve shouldn't show it.
                    link.ClearSent();
                }
            }
        }
        _stop = true;
        sender.Join();
        _engine.Stop();
        var stats = _engine.Stats;
        var late = _late.Take(_tickIndex).Skip(warmUpTicks).Select(t => t * 1000.0 / TimeSpan.TicksPerSecond).ToArray();
        var took = _took.Take(_tickIndex).Skip(warmUpTicks).Select(t => t * 1000.0 / TimeSpan.TicksPerSecond).ToArray();
        return new LoadResult(
            cores, duration, warmUp,
            [.. seconds.Where(s => s.At > warmUp.TotalSeconds)], memory, [.. system], late, took,
            stats with { Concealed = stats.Concealed - Math.Max(0, concealedAtWarmUp) },
            GC.GetTotalPauseDuration());
    }

    public void Dispose()
    {
        _stop = true;
        _engine.Dispose();
    }

    // ---- The talkers' thread.

    private void Play(long start, TimeSpan duration)
    {
        var end = start + (long)(duration.TotalSeconds * Hz);
        var frame = Hz / 50;
        var nextHeartbeat = start + Hz;
        foreach (var talker in _talkers)
        {
            // Each starts quiet for a random while, so they don't all begin at once.
            talker.NextFrame = start + (long)(talker.Rng.NextDouble() * 4 * Hz);
        }
        foreach (var controller in _controllers)
        {
            controller.NextToggle = start + (long)((2 + (controller.Rng.NextDouble() * 4)) * Hz);
        }
        var ticks = 0;
        while (!_stop && Stopwatch.GetTimestamp() < end)
        {
            var now = Stopwatch.GetTimestamp();
            foreach (var talker in _talkers)
            {
                while (talker.NextFrame <= now)
                {
                    Emit(talker, frame);
                }
            }
            foreach (var controller in _controllers)
            {
                if (now >= controller.NextToggle)
                {
                    Toggle(controller, now);
                }
            }
            if (now >= nextHeartbeat)
            {
                nextHeartbeat += Hz;
                foreach (var talker in _talkers)
                {
                    _engine.Heartbeat(talker.User, talker.Held ? [BroadcastAction.Group] : [], talker.OpenMic, _time.Now);
                }
            }
            while (_deliveries.TryPeek(out var next, out var due) && due <= now)
            {
                _deliveries.Dequeue();
                if (next.Lost)
                {
                    next.Talker.Link.Pause(next.Talker.User, 1);
                }
                else
                {
                    if (next.PauseFrames > 0)
                    {
                        next.Talker.Link.Pause(next.Talker.User, next.PauseFrames);
                    }
                    next.Talker.Link.Deliver(next.Talker.User, next.Opus);
                }
            }
            if (++ticks % 32 == 0)
            {
                Volatile.Write(ref _senderCpu, ThreadCpu.NowNanoseconds());
            }
            Thread.Sleep(1);
        }
    }

    /// <summary>The talker's next 20 ms: a frame to send with its arrival jitter, or the step from quiet into the next burst.</summary>
    private void Emit(Talker talker, long frame)
    {
        var rng = talker.Rng;
        if (talker.Opus is { } opus)
        {
            var late = (long)(rng.NextDouble() * 8 * Hz / 1000);
            if (rng.NextDouble() < 0.03)
            {
                late += (long)((20 + (rng.NextDouble() * 40)) * Hz / 1000);
            }
            // A client's packets arrive in order: a late one holds back those behind it.
            var due = Math.Max(talker.NextFrame + late, talker.LastDue + 1);
            talker.LastDue = due;
            _deliveries.Enqueue(new Delivery(talker, opus[talker.Index], talker.PauseFrames, rng.NextDouble() < 0.005), due);
            talker.PauseFrames = 0;
            talker.NextFrame += frame;
            if (++talker.Index == opus.Length)
            {
                talker.Opus = null;
                if (--talker.UtterancesLeft > 0)
                {
                    // A breath between sentences: a few frames with nothing sent.
                    var gap = 5 + rng.Next(10);
                    talker.PauseFrames += gap;
                    talker.NextFrame += gap * frame;
                    Start(talker);
                }
                else
                {
                    // Quiet for as long as the duty cycle says: the burst's mean length is about 6 s.
                    var mean = 6.0 * (1 - talker.Duty) / talker.Duty;
                    var quiet = Math.Max(0.4, -mean * Math.Log(1 - rng.NextDouble()));
                    var frames = (int)(quiet * 50);
                    talker.PauseFrames += frames;
                    talker.NextFrame += frames * frame;
                    talker.UtterancesLeft = 1 + rng.Next(3);
                    Start(talker);
                }
            }
            return;
        }
        Start(talker);
    }

    private void Start(Talker talker)
    {
        var rng = talker.Rng;
        talker.Opus = talker.Voice.Chat[rng.Next(talker.Voice.Chat.Count)];
        talker.Index = 0;
    }

    private void Toggle(Controller controller, long now)
    {
        var on = !controller.On;
        controller.On = on;
        var talker = controller.Talker;
        if (controller.Action == BroadcastAction.OpenMic)
        {
            talker.OpenMic = on;
        }
        else
        {
            talker.Held = on;
        }
        _engine.OnEdge(new BroadcastEdge(talker.User, controller.Action, on, TriggerKind.AppKey, _time.Now));
        var (low, high) = on ? (controller.OnLow, controller.OnHigh) : (controller.OffLow, controller.OffHigh);
        controller.NextToggle = now + (long)((low + (controller.Rng.NextDouble() * (high - low))) * Hz);
    }

    private sealed class Talker(UserId user, FakeVoiceChannelLink link, SpeechBank.Voice voice, double duty, bool broadcaster, Random rng)
    {
        public UserId User { get; } = user;

        public FakeVoiceChannelLink Link { get; } = link;

        public SpeechBank.Voice Voice { get; } = voice;

        public double Duty { get; } = duty;

        public bool Broadcaster { get; } = broadcaster;

        public Random Rng { get; } = rng;

        public byte[][]? Opus { get; set; }

        public int Index { get; set; }

        public int UtterancesLeft { get; set; }

        public int PauseFrames { get; set; }

        public long NextFrame { get; set; }

        public long LastDue { get; set; }

        public bool Held { get; set; }

        public bool OpenMic { get; set; }
    }

    private sealed class Controller(Talker talker, BroadcastAction action, double onLow, double onHigh, double offLow, double offHigh, Random rng)
    {
        public Talker Talker { get; } = talker;

        public BroadcastAction Action { get; } = action;

        public double OnLow { get; } = onLow;

        public double OnHigh { get; } = onHigh;

        public double OffLow { get; } = offLow;

        public double OffHigh { get; } = offHigh;

        public Random Rng { get; } = rng;

        public bool On { get; set; }

        public long NextToggle { get; set; }
    }

    private readonly record struct Delivery(Talker Talker, byte[] Opus, int PauseFrames, bool Lost);
}

internal readonly record struct Second(double At, double ProcessCores, double EngineCores, double SenderCores);

/// <summary>One minute of the machine, or (with a gap) a stretch where the sampler did not run. The load and pressure are the host's, not the bot's.</summary>
internal readonly record struct SystemSample(double At, double LoadAverage, int PressureLevel, int FreePercent, double LiveAfterGcMb, int PlayerBuffers, double GapSeconds);

internal readonly record struct MemorySample(double At, double ManagedMb, double WorkingSetMb, int Gen0, int Gen1, int Gen2, double AllocatedMb);

internal sealed record LoadResult(
    int Cores,
    TimeSpan Duration,
    TimeSpan WarmUp,
    IReadOnlyList<Second> Seconds,
    IReadOnlyList<MemorySample> Memory,
    IReadOnlyList<SystemSample> Minutes,
    double[] LateMs,
    double[] TookMs,
    EngineStats Stats,
    TimeSpan GcPause);
