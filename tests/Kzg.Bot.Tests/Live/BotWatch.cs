using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Kzg.Bot.Engine;
using Kzg.Core.Abstractions;
using Kzg.Discord.Voice;

namespace Kzg.Bot.Tests.Live;

/// <summary>
/// What the bot under test saw during a scenario, so a miss can be put down to its cause: packets that never
/// reached the bot, or an engine thread that ran late. It listens to the relay links beside the engine.
/// </summary>
internal sealed class BotWatch
{
    private readonly Stopwatch _clock;
    private readonly ConcurrentQueue<(double At, ulong User)> _packets = new();
    private readonly ConcurrentQueue<(double At, double LateMs, double TookMs)> _slowTicks = new();
    private readonly AudioEngine _engine;
    private readonly IVoiceGateway _gateway;
    private EngineStats _stats;
    private long _unknownSsrc;

    public BotWatch(Stopwatch clock, IVoiceGateway gateway, AudioEngine engine)
    {
        (_clock, _engine, _gateway) = (clock, engine, gateway);
        _unknownSsrc = UnknownSsrc();
        foreach (var link in gateway.Links)
        {
            link.PacketReceived += packet => _packets.Enqueue((_clock.Elapsed.TotalMilliseconds, packet.User.V));
        }
        engine.TickObserver = (late, took) =>
        {
            if (late.TotalMilliseconds > 20 || took.TotalMilliseconds > 10)
            {
                _slowTicks.Enqueue((_clock.Elapsed.TotalMilliseconds, late.TotalMilliseconds, took.TotalMilliseconds));
            }
        };
        _stats = engine.Stats;
    }

    /// <summary>Lines on the window from <paramref name="from"/> to <paramref name="to"/>, ms on the test's clock; the engine counters since the last call.</summary>
    public IEnumerable<string> Describe(double from, double to, IReadOnlyDictionary<ulong, string> names)
    {
        foreach (var group in _packets.Where(p => p.At >= from && p.At <= to).GroupBy(p => p.User))
        {
            var times = group.Select(p => p.At).Order().ToArray();
            var gaps = times.Zip(times.Skip(1), (a, b) => b - a).ToArray();
            var long40 = gaps.Count(g => g > 40 && g < 300);
            yield return $"   bot got {times.Length} packets from {names.GetValueOrDefault(group.Key, group.Key.ToString(CultureInfo.InvariantCulture))}; " +
                $"gaps of 40-300 ms: {long40}, longest under 300 ms: {gaps.Where(g => g < 300).DefaultIfEmpty(0).Max():0} ms";
        }
        var slow = _slowTicks.Where(t => t.At >= from && t.At <= to).ToArray();
        yield return $"   engine: {slow.Length} ticks over 20 ms late or 10 ms long; latest {slow.Select(t => t.LateMs).DefaultIfEmpty(0).Max():0} ms, longest {slow.Select(t => t.TookMs).DefaultIfEmpty(0).Max():0} ms";
        var now = _engine.Stats;
        yield return $"   engine counters: resyncs +{now.Resyncs - _stats.Resyncs}, concealed +{now.Concealed - _stats.Concealed}, dropped +{now.PacketsDropped - _stats.PacketsDropped}, " +
            $"decode errors +{now.DecodeErrors - _stats.DecodeErrors}, send errors +{now.SendErrors - _stats.SendErrors}, faults +{now.TickFaults - _stats.TickFaults}";
        _stats = now;
        var unknown = UnknownSsrc();
        yield return $"   relay links: {unknown - _unknownSsrc} packets from streams they couldn't place";
        _unknownSsrc = unknown;
    }

    private long UnknownSsrc() => _gateway.Links.OfType<RelayLink>().Sum(link => link.UnknownSsrc);
}
