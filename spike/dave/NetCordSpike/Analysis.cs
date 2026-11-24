using System.Globalization;
using System.Text;
using NetCord.Gateway.Voice;

namespace Kzg.Spike.Dave;

/// <summary>Matches every received frame to the frame a tester sent, by content, then scores loss, identity and latency.</summary>
public static class Analysis
{
    public sealed record Input(
        List<Tx> Tx, Rx[] Relay, Rx[] Listener, List<Fwd> Fwd, (double T, ulong User, ulong? Channel)[] Presence,
        List<EventMark> Events, Dictionary<ulong, int> ClipOwner, ulong[] TesterIds, ulong Relay2Id,
        double SoakStart, double SoakEnd, double End, string RunDir);

    private const double MatchWindowMs = 3000;
    private const double InFlightMs = 1000;

    public static Dictionary<string, object?> Run(Input input)
    {
        var report = new Dictionary<string, object?>();
        var silence = Bot.Silence;

        // tx index: hash -> sorted send times (index into Tx)
        var txByHash = new Dictionary<ulong, List<int>>();
        for (var i = 0; i < input.Tx.Count; i++)
        {
            if (!txByHash.TryGetValue(input.Tx[i].Hash, out var list))
            {
                txByHash[input.Tx[i].Hash] = list = [];
            }
            list.Add(i);
        }
        int FindTx(ulong hash, double t)
        {
            if (!txByHash.TryGetValue(hash, out var list))
            {
                return -1;
            }
            var best = -1;
            foreach (var i in list)
            {
                var txT = input.Tx[i].T;
                if (txT <= t && t - txT < MatchWindowMs)
                {
                    best = i;
                }
            }
            return best;
        }

        // relay1 receive -> tester frames
        var txMatch = new int[input.Tx.Count];
        Array.Fill(txMatch, -1);
        var txAttribution = new byte[input.Tx.Count]; // 1 correct, 2 wrong user, 3 unknown user
        int relayUnknownHash = 0, relayDuplicates = 0, relaySilence = 0;
        for (var r = 0; r < input.Relay.Length; r++)
        {
            var rx = input.Relay[r];
            if (rx.Len == silence.Length && rx.Hash == SilenceHash)
            {
                relaySilence++;
                continue;
            }
            var i = FindTx(rx.Hash, rx.T);
            if (i < 0)
            {
                relayUnknownHash++;
                continue;
            }
            if (txMatch[i] >= 0)
            {
                relayDuplicates++;
                continue;
            }
            txMatch[i] = r;
            var owner = input.Tx[i].Tester;
            txAttribution[i] = rx.User == input.TesterIds[owner] ? (byte)1 : rx.User == 0 ? (byte)3 : (byte)2;
        }

        Dictionary<string, object?> RelayWindow(double w0, double w1)
        {
            var perTester = new Dictionary<string, object?>();
            for (var tester = 0; tester < 3; tester++)
            {
                int sent = 0, got = 0, correct = 0, wrong = 0, unknown = 0;
                var arrivals = new List<double>();
                for (var i = 0; i < input.Tx.Count; i++)
                {
                    var x = input.Tx[i];
                    if (x.Tester != tester || x.T < w0 || x.T > w1 - InFlightMs)
                    {
                        continue;
                    }
                    sent++;
                    if (txMatch[i] >= 0)
                    {
                        got++;
                        switch (txAttribution[i])
                        {
                            case 1: correct++; arrivals.Add(input.Relay[txMatch[i]].T); break;
                            case 2: wrong++; break;
                            default: unknown++; break;
                        }
                    }
                }
                arrivals.Sort();
                double maxGap = 0;
                for (var k = 1; k < arrivals.Count; k++)
                {
                    maxGap = Math.Max(maxGap, arrivals[k] - arrivals[k - 1]);
                }
                perTester[$"tester{tester + 1}"] = new Dictionary<string, object?>
                {
                    ["sent"] = sent,
                    ["received"] = got,
                    ["lossPct"] = sent == 0 ? null : Math.Round(100.0 * (sent - got) / sent, 3),
                    ["correctUserId"] = correct,
                    ["wrongUserId"] = wrong,
                    ["unknownUserId"] = unknown,
                    ["maxGapBetweenCorrectArrivalsMs"] = Math.Round(maxGap, 0),
                };
            }
            return perTester;
        }

        // listener receive -> forwarded frames
        var fwdByHash = new Dictionary<ulong, List<int>>();
        for (var i = 0; i < input.Fwd.Count; i++)
        {
            if (!fwdByHash.TryGetValue(input.Fwd[i].Hash, out var list))
            {
                fwdByHash[input.Fwd[i].Hash] = list = [];
            }
            list.Add(i);
        }
        var fwdMatch = new int[input.Fwd.Count];
        Array.Fill(fwdMatch, -1);
        int listenerUnknownHash = 0, listenerSilence = 0, listenerFromRelay2 = 0, listenerOtherUser = 0, listenerNoUser = 0;
        List<double> hop1 = [], relayInternal = [], hop2 = [], added = [], endToEnd = [];
        for (var r = 0; r < input.Listener.Length; r++)
        {
            var rx = input.Listener[r];
            if (rx.Hash == SilenceHash)
            {
                listenerSilence++;
                continue;
            }
            if (rx.User == input.Relay2Id)
            {
                listenerFromRelay2++;
            }
            else if (rx.User == 0)
            {
                listenerNoUser++;
            }
            else
            {
                listenerOtherUser++;
            }
            var best = -1;
            if (fwdByHash.TryGetValue(rx.Hash, out var list))
            {
                foreach (var i in list)
                {
                    if (input.Fwd[i].T <= rx.T && rx.T - input.Fwd[i].T < MatchWindowMs)
                    {
                        best = i;
                    }
                }
            }
            if (best < 0)
            {
                listenerUnknownHash++;
                continue;
            }
            if (fwdMatch[best] >= 0)
            {
                continue;
            }
            fwdMatch[best] = r;
            var f = input.Fwd[best];
            if (rx.T >= input.SoakStart && rx.T <= input.SoakEnd)
            {
                hop2.Add(rx.T - f.T);
                added.Add(rx.T - f.RxT);
                relayInternal.Add(f.T - f.RxT);
                var source = FindTx(f.Hash, f.RxT);
                if (source >= 0)
                {
                    endToEnd.Add(rx.T - input.Tx[source].T);
                }
            }
        }
        for (var i = 0; i < input.Tx.Count; i++)
        {
            var x = input.Tx[i];
            if (txMatch[i] >= 0 && x.T >= input.SoakStart && x.T <= input.SoakEnd)
            {
                hop1.Add(input.Relay[txMatch[i]].T - x.T);
            }
        }

        Dictionary<string, object?> ListenerWindow(double w0, double w1)
        {
            int sent = 0, got = 0;
            for (var i = 0; i < input.Fwd.Count; i++)
            {
                var f = input.Fwd[i];
                if (f.T < w0 || f.T > w1 - InFlightMs)
                {
                    continue;
                }
                sent++;
                if (fwdMatch[i] >= 0)
                {
                    got++;
                }
            }
            return new Dictionary<string, object?>
            {
                ["forwarded"] = sent,
                ["heardByListener"] = got,
                ["lossPct"] = sent == 0 ? null : Math.Round(100.0 * (sent - got) / sent, 3),
            };
        }

        report["soakSeconds"] = Math.Round((input.SoakEnd - input.SoakStart) / 1000, 1);
        report["soakRelay1PerSpeaker"] = RelayWindow(input.SoakStart, input.SoakEnd);
        report["soakRelayToListener"] = ListenerWindow(input.SoakStart, input.SoakEnd);
        report["relay1Rx"] = new Dictionary<string, object?>
        {
            ["packets"] = input.Relay.Length,
            ["silenceFrames"] = relaySilence,
            ["unmatchedContent"] = relayUnknownHash,
            ["duplicates"] = relayDuplicates,
            ["distinctSsrcs"] = input.Relay.Select(x => x.Ssrc).Distinct().Count(),
        };
        report["listenerRx"] = new Dictionary<string, object?>
        {
            ["packets"] = input.Listener.Length,
            ["silenceFrames"] = listenerSilence,
            ["fromRelay2UserId"] = listenerFromRelay2,
            ["noUserId"] = listenerNoUser,
            ["otherUserId"] = listenerOtherUser,
            ["notBitExactToAForwardedFrame"] = listenerUnknownHash,
        };
        report["latencyMsSoak"] = new Dictionary<string, object?>
        {
            ["hop1_testerSend_to_relay1Receive"] = Stats(hop1),
            ["relayInternal_relay1Receive_to_relay2Send"] = Stats(relayInternal),
            ["hop2_relay2Send_to_listenerReceive"] = Stats(hop2),
            ["ADDED_relay1Receive_to_listenerReceive"] = Stats(added),
            ["endToEnd_testerSend_to_listenerReceive"] = Stats(endToEnd),
        };

        var eventReports = new List<object?>();
        foreach (var e in input.Events)
        {
            eventReports.Add(new Dictionary<string, object?>
            {
                ["name"] = e.Name,
                ["error"] = e.Error,
                ["actionSeconds"] = Math.Round((e.Acted - e.Start) / 1000, 2),
                ["relay1PerSpeaker"] = RelayWindow(e.Start, e.End),
                ["relayToListener"] = ListenerWindow(e.Start, e.End),
                ["firstCorrectArrivalAfterActionMs"] = Enumerable.Range(0, 3).ToDictionary(t => $"tester{t + 1}", t => FirstCorrect(t, e.Acted)),
                ["firstListenerArrivalAfterActionMs"] = input.Listener.Where(x => x.T >= e.Acted && x.Hash != SilenceHash).Select(x => (double?)Math.Round(x.T - e.Acted, 0)).FirstOrDefault(),
            });
        }
        report["events"] = eventReports;
        report["presenceEvents"] = input.Presence.Length;

        double? FirstCorrect(int tester, double after)
        {
            double? best = null;
            for (var i = 0; i < input.Tx.Count; i++)
            {
                if (input.Tx[i].Tester == tester && txAttribution[i] == 1)
                {
                    var t = input.Relay[txMatch[i]].T;
                    if (t >= after && (best is null || t < best))
                    {
                        best = t;
                    }
                }
            }
            return best is { } b ? Math.Round(b - after, 0) : null;
        }

        WriteCsv(input);
        report["listenerWav"] = WriteWav(input, out var seconds);
        report["listenerWavSeconds"] = seconds;
        return report;
    }

    public static readonly ulong SilenceHash = Fnv(Bot.Silence);

    private static ulong Fnv(ReadOnlySpan<byte> data)
    {
        var hash = 14695981039346656037UL;
        foreach (var b in data)
        {
            hash = (hash ^ b) * 1099511628211UL;
        }
        return hash;
    }

    private static Dictionary<string, object?> Stats(List<double> values)
    {
        if (values.Count == 0)
        {
            return new() { ["n"] = 0 };
        }
        values.Sort();
        double P(double p) => Math.Round(values[Math.Min(values.Count - 1, (int)Math.Ceiling(p * values.Count) - 1)], 1);
        return new()
        {
            ["n"] = values.Count,
            ["p50"] = P(0.50),
            ["p95"] = P(0.95),
            ["p99"] = P(0.99),
            ["max"] = Math.Round(values[^1], 1),
        };
    }

    private static void WriteCsv(Input input)
    {
        static string F(double v) => v.ToString("F2", CultureInfo.InvariantCulture);
        using (var w = new StreamWriter(Path.Combine(input.RunDir, "tx.csv")))
        {
            w.WriteLine("t_ms,tester,hash");
            foreach (var x in input.Tx)
            {
                w.WriteLine($"{F(x.T)},{x.Tester + 1},{x.Hash:x16}");
            }
        }
        foreach (var (name, rows) in new[] { ("rx_relay1.csv", input.Relay), ("rx_listener1.csv", input.Listener) })
        {
            using var w = new StreamWriter(Path.Combine(input.RunDir, name));
            w.WriteLine("t_ms,ssrc,user,seq,rtp_ts,len,hash");
            foreach (var x in rows)
            {
                w.WriteLine($"{F(x.T)},{x.Ssrc},{x.User},{x.Seq},{x.RtpTs},{x.Len},{x.Hash:x16}");
            }
        }
        using (var w = new StreamWriter(Path.Combine(input.RunDir, "fwd.csv")))
        {
            w.WriteLine("t_ms,relay1_rx_ms,hash");
            foreach (var x in input.Fwd)
            {
                w.WriteLine($"{F(x.T)},{F(x.RxT)},{x.Hash:x16}");
            }
        }
    }

    /// <summary>The listener's recording of the soak, placed by RTP timestamp so pauses stay pauses.</summary>
    private static string WriteWav(Input input, out double seconds)
    {
        var frames = input.Listener
            .Where(x => x.User == input.Relay2Id && x.T >= input.SoakStart && x.T <= input.SoakEnd && x.Data is not null)
            .OrderBy(x => x.T)
            .ToList();
        var path = Path.Combine(input.RunDir, "listener1-soak.wav");
        seconds = 0;
        if (frames.Count == 0)
        {
            return "(no frames)";
        }
        var ts0 = frames[0].RtpTs;
        var totalSamples = (long)(frames[^1].RtpTs - ts0) + 960;
        totalSamples = Math.Min(totalSamples, 48000L * 60 * 15);
        var pcm = new short[totalSamples * 2];
        using var decoder = new OpusDecoder(VoiceChannels.Stereo);
        var buffer = new short[5760 * 2];
        foreach (var f in frames)
        {
            var at = (long)(f.RtpTs - ts0);
            var n = decoder.Decode(f.Data!, buffer, 5760, false);
            if (at < 0 || (at + n) * 2 > pcm.Length)
            {
                continue;
            }
            Array.Copy(buffer, 0, pcm, at * 2, n * 2);
        }
        using var stream = new FileStream(path, FileMode.Create);
        using var bw = new BinaryWriter(stream);
        var bytes = pcm.Length * 2;
        bw.Write(Encoding.ASCII.GetBytes("RIFF"));
        bw.Write(36 + bytes);
        bw.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        bw.Write(16);
        bw.Write((short)1);
        bw.Write((short)2);
        bw.Write(48000);
        bw.Write(48000 * 4);
        bw.Write((short)4);
        bw.Write((short)16);
        bw.Write(Encoding.ASCII.GetBytes("data"));
        bw.Write(bytes);
        foreach (var s in pcm)
        {
            bw.Write(s);
        }
        seconds = Math.Round(totalSamples / 48000.0, 1);
        return path;
    }
}
