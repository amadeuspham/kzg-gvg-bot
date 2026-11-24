using System.Globalization;
using System.Runtime.InteropServices;
using Xunit.Abstractions;

namespace Kzg.Bot.Tests.Load;

/// <summary>
/// Plan verification 8: thirty talkers in six VCs, six of them broadcasting at once (two players on open mic,
/// four on the group key). Opt-in: <c>KZG_LOAD=1</c>; <c>KZG_LOAD_SECONDS</c> sets the length. A line a minute
/// (the bot's working set and heap, the machine's load and memory pressure, any gap in the sampler) is also
/// appended to <c>.data/load/soak-*.log</c> as it happens.
/// Passes when the machine's CPU stays under 30% and no engine tick starts more than 20 ms late.
/// </summary>
public sealed class LoadTests(ITestOutputHelper output)
{
    private const double CpuLimitPercent = 30;
    private const double LateLimitMs = 20;

    [LoadFact]
    public void Thirty_talkers_six_broadcasting()
    {
        var seconds = int.TryParse(Environment.GetEnvironmentVariable("KZG_LOAD_SECONDS"), CultureInfo.InvariantCulture, out var s) ? s : 300;
        var repo = FindRepo();
        var logDir = Path.Combine(repo, ".data", "load");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, $"soak-{DateTime.Now:yyyyMMdd-HHmmss}.log");
        void Log(string line)
        {
            output.WriteLine(line);
            File.AppendAllText(logFile, $"{DateTime.Now:HH:mm:ss}  {line}\n");
        }
        output.WriteLine($"machine: {Environment.ProcessorCount} logical cores, {RuntimeInformation.OSDescription}, {RuntimeInformation.ProcessArchitecture}, {Cpu()}");
        var bank = SpeechBank.Build();
        output.WriteLine($"speech: {bank.Voices.Count} voices ({string.Join(", ", bank.Voices.Select(v => v.Name))})");

        using var world = new LoadWorld(bank, seconds, seed: 7);
        output.WriteLine($"world: {world.Talkers} talkers in {LoadWorld.VoiceChannels} VCs, {world.Broadcasters} broadcasting at once, {seconds} s, log in {logFile}");
        var warmUp = TimeSpan.FromSeconds(Math.Min(10, seconds / 4));
        var result = world.Run(TimeSpan.FromSeconds(seconds), warmUp, Log);
        var failures = Report(result);
        Assert.Empty(failures);
    }

    private List<string> Report(LoadResult r)
    {
        var failures = new List<string>();
        var cores = r.Cores;
        var process = r.Seconds.Select(x => x.ProcessCores).ToArray();
        var engine = r.Seconds.Select(x => x.EngineCores).ToArray();
        var sender = r.Seconds.Select(x => x.SenderCores).ToArray();
        output.WriteLine($"-- after {r.WarmUp.TotalSeconds:0} s of warm-up, over {r.Seconds.Count} s");
        output.WriteLine($"process CPU: mean {Mean(process):0.00} cores, p95 {Percentile(process, 95):0.00}, max {process.Max():0.00} = mean {Mean(process) / cores * 100:0.0}% of this machine ({cores} cores), p95 {Percentile(process, 95) / cores * 100:0.0}%");
        output.WriteLine($"engine thread: mean {Mean(engine) * 100:0.0}% of one core, p95 {Percentile(engine, 95) * 100:0.0}%, max {engine.Max() * 100:0.0}%");
        output.WriteLine($"load harness thread (not the bot): mean {Mean(sender) * 100:0.0}% of one core");
        var rest = r.Seconds.Select(x => Math.Max(0, x.ProcessCores - x.EngineCores - x.SenderCores)).ToArray();
        output.WriteLine($"everything else in the process (GC, sampler): mean {Mean(rest):0.00} cores, p95 {Percentile(rest, 95):0.00}");
        var botCores = process.Zip(sender, (p, s) => p - s).ToArray();
        output.WriteLine($"bot only (process minus the harness thread): mean {Mean(botCores):0.00} cores, p95 {Percentile(botCores, 95):0.00}, max {botCores.Max():0.00}");
        foreach (var server in new[] { 2, 4, 8 })
        {
            output.WriteLine($"  on a {server}-core server with cores as fast as these: mean {Mean(botCores) / server * 100:0.0}%, p95 {Percentile(botCores, 95) / server * 100:0.0}%");
        }

        output.WriteLine($"tick start lateness (ms, against its slot): p50 {Percentile(r.LateMs, 50):0.00}, p95 {Percentile(r.LateMs, 95):0.00}, p99 {Percentile(r.LateMs, 99):0.00}, max {r.LateMs.Max():0.00}; over 5 ms: {r.LateMs.Count(x => x > 5)}, over 20 ms: {r.LateMs.Count(x => x > LateLimitMs)} of {r.LateMs.Length}");
        output.WriteLine($"tick duration (ms): p50 {Percentile(r.TookMs, 50):0.00}, p95 {Percentile(r.TookMs, 95):0.00}, p99 {Percentile(r.TookMs, 99):0.00}, max {r.TookMs.Max():0.00} (budget 20)");
        var st = r.Stats;
        output.WriteLine($"engine: {st.Ticks} ticks, {st.FramesSent} frames sent, underruns (concealed relay frames) {st.Concealed}, packets dropped {st.PacketsDropped}, decode errors {st.DecodeErrors}, send errors {st.SendErrors}, tick faults {st.TickFaults}, resyncs {st.Resyncs}");
        output.WriteLine($"GC: gen0 {r.Memory[^1].Gen0}, gen1 {r.Memory[^1].Gen1}, gen2 {r.Memory[^1].Gen2}, total pause {r.GcPause.TotalMilliseconds:0} ms, allocated {r.Memory[^1].AllocatedMb:0} MB ({r.Memory[^1].AllocatedMb / r.Memory[^1].At:0.0} MB/s)");
        output.WriteLine("memory over time (s: managed MB, working set MB, GCs gen0/1/2):");
        foreach (var m in r.Memory.Where((_, i) => i % Math.Max(1, r.Memory.Count / 14) == 0 || i == r.Memory.Count - 1))
        {
            output.WriteLine($"  {m.At,5:0}: {m.ManagedMb,6:0.0}  {m.WorkingSetMb,6:0.0}  {m.Gen0}/{m.Gen1}/{m.Gen2}");
        }
        var first = r.Memory.Where(m => m.At >= r.WarmUp.TotalSeconds && m.At <= r.WarmUp.TotalSeconds + 60).Select(m => m.WorkingSetMb).DefaultIfEmpty(0).Average();
        var last = r.Memory.Where(m => m.At >= r.Duration.TotalSeconds - 60).Select(m => m.WorkingSetMb).DefaultIfEmpty(0).Average();
        output.WriteLine($"working set: first minute after warm-up {first:0} MB, last minute {last:0} MB, change {last - first:+0;-0} MB");
        var minutes = r.Minutes.Where(m => m.GapSeconds == 0).ToArray();
        output.WriteLine($"machine during the run: load average mean {Mean(minutes.Select(m => m.LoadAverage).ToArray()):0.0}, max {minutes.Select(m => m.LoadAverage).DefaultIfEmpty(0).Max():0.0}; memory pressure above normal in {minutes.Count(m => m.PressureLevel > 1)} of {minutes.Length} minutes; lowest free memory {minutes.Select(m => m.FreePercent).DefaultIfEmpty(0).Min()}%");
        foreach (var m in minutes.Where(m => m.PressureLevel > 1))
        {
            output.WriteLine($"  PRESSURE at {m.At:0} s: level {m.PressureLevel}, {m.FreePercent}% free, load {m.LoadAverage:0.0}");
        }
        foreach (var gap in r.Minutes.Where(m => m.GapSeconds > 0))
        {
            output.WriteLine($"  SAMPLER GAP at {gap.At:0} s run time: {gap.GapSeconds:0.0} s");
        }

        if (Mean(process) / cores * 100 >= CpuLimitPercent)
        {
            failures.Add($"process CPU {Mean(process) / cores * 100:0.0}% of the machine, limit {CpuLimitPercent}%");
        }
        if (r.LateMs.Max() > LateLimitMs)
        {
            failures.Add($"a tick started {r.LateMs.Max():0.0} ms late, limit {LateLimitMs} ms");
        }
        if (st.TickFaults > 0 || st.DecodeErrors > 0 || st.SendErrors > 0 || st.Resyncs > 0)
        {
            failures.Add($"faults: {st.TickFaults} tick faults, {st.DecodeErrors} decode errors, {st.SendErrors} send errors, {st.Resyncs} resyncs");
        }
        foreach (var failure in failures)
        {
            output.WriteLine("FAIL: " + failure);
        }
        return failures;
    }

    private static double Mean(double[] values) => values.Length == 0 ? 0 : values.Average();

    private static double Percentile(double[] values, double percent)
    {
        if (values.Length == 0)
        {
            return 0;
        }
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percent / 100 * sorted.Length);
        return sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)];
    }

    private static string Cpu()
    {
        try
        {
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("/usr/sbin/sysctl", "-n machdep.cpu.brand_string") { RedirectStandardOutput = true });
            return process!.StandardOutput.ReadToEnd().Trim();
        }
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return "cpu unknown";
        }
    }

    private static string FindRepo()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !File.Exists(Path.Combine(dir, "Kzg.slnx")))
        {
            dir = Path.GetDirectoryName(dir);
        }
        return dir ?? throw new InvalidOperationException("the repository root wasn't found");
    }
}
