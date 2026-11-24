using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using NetCord.Gateway.Voice.UdpSockets;
using NetCord.Gateway.WebSockets;
using NetCord.Logging;

namespace Kzg.Spike.Dave;

/// <summary>One monotonic clock for every bot: all bots run in this process, so times compare directly.</summary>
public static class Clock
{
    private static readonly Stopwatch Watch = Stopwatch.StartNew();
    public static double Ms => Watch.Elapsed.TotalMilliseconds;
}

/// <summary>
/// The run log. Every line passes a scrub that replaces any token value with its name, so a library
/// message can never carry a secret into the file or the console.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;
    private static readonly List<(string Name, string Value)> Secrets = [];
    public static int ScrubHits;

    public static void Open(string path) => _file = new StreamWriter(path, append: false) { AutoFlush = true };

    public static void AddSecret(string name, string value)
    {
        lock (Gate)
        {
            Secrets.Add((name, value));
        }
    }

    public static void Info(string line, bool console = true) => Write("INF", line, console);
    public static void Warn(string line) => Write("WRN", line, true);

    public static void Write(string level, string line, bool console)
    {
        lock (Gate)
        {
            foreach (var (name, value) in Secrets)
            {
                if (line.Contains(value, StringComparison.Ordinal))
                {
                    line = line.Replace(value, $"<{name}>", StringComparison.Ordinal);
                    ScrubHits++;
                }
            }
            var text = $"{Clock.Ms / 1000,9:F3} {level} {line}";
            _file?.WriteLine(text);
            if (console)
            {
                Console.WriteLine(text);
            }
        }
    }

    public static void Close()
    {
        lock (Gate)
        {
            _file?.Dispose();
            _file = null;
        }
    }
}

/// <summary>NetCord's gateway and voice log, prefixed with the bot it belongs to; counts warnings by text.</summary>
public sealed class NetLogger(string bot, string kind) : IGatewayLogger, IVoiceLogger
{
    public static readonly ConcurrentDictionary<string, int> WarningCounts = new();

    public bool IsEnabled(LogLevel level) => level >= LogLevel.Debug;

    public void Log<TState>(LogLevel level, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
    {
        var message = formatter(state, exception);
        if (level >= LogLevel.Warning)
        {
            // Collapse the SSRC so counts group by kind of failure.
            var key = $"{kind}:{System.Text.RegularExpressions.Regex.Replace(message.Split('\n')[0], @"\d+", "#")}";
            WarningCounts.AddOrUpdate(key, 1, (_, n) => n + 1);
        }
        // Per-packet warnings would flood the console; they go to the file and the counts.
        var console = level >= LogLevel.Information && !message.StartsWith("Failed to decrypt", StringComparison.Ordinal)
            && !message.StartsWith("Heartbeat", StringComparison.Ordinal);
        Kzg.Spike.Dave.Log.Write(level.ToString()[..3].ToUpperInvariant(), $"[{bot}/{kind}] {message}", console);
    }
}

/// <summary>Wraps NetCord's WebSocket so the spike can cut a live connection and watch the library resume.</summary>
public sealed class KillableWebSocketProvider(string? traceName = null) : IWebSocketConnectionProvider
{
    private readonly WebSocketConnectionProvider _inner = new(null);
    private IWebSocketConnection? _last;

    public IWebSocketConnection CreateConnection()
    {
        var connection = _inner.CreateConnection();
        return _last = traceName is null ? connection : new Tracing(connection, traceName);
    }

    /// <summary>Aborts the socket under the client, as a network drop would; the client sees a disconnect.</summary>
    public void KillCurrent() => _last?.Abort();

    /// <summary>
    /// Logs voice-gateway opcodes in both directions. Payloads are logged only for the user-ID and DAVE
    /// transition ops; identify, resume and the session description carry secrets and are never logged.
    /// </summary>
    private sealed class Tracing(IWebSocketConnection inner, string name) : IWebSocketConnection
    {
        private static readonly HashSet<int> PayloadOps = [11, 13, 21, 22, 24];
        private readonly MemoryStream _partial = new();

        public int? CloseStatus => inner.CloseStatus;
        public string? CloseStatusDescription => inner.CloseStatusDescription;

        public ValueTask OpenAsync(Uri uri, CancellationToken cancellationToken = default) => inner.OpenAsync(uri, cancellationToken);
        public void Abort() => inner.Abort();
        public ValueTask CloseAsync(int status, string? description, CancellationToken cancellationToken = default) => inner.CloseAsync(status, description, cancellationToken);
        public void Dispose() => inner.Dispose();

        public async ValueTask<WebSocketConnectionReceiveResult> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var result = await inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (result.MessageType is not WebSocketMessageType.Close)
            {
                _partial.Write(buffer.Span[..result.Count]);
                if (result.EndOfMessage)
                {
                    Trace("<-", result.MessageType, _partial.ToArray(), binaryHasSeq: true);
                    _partial.SetLength(0);
                }
            }
            return result;
        }

        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, WebSocketMessageType messageType, WebSocketMessageFlags messageFlags, CancellationToken cancellationToken = default)
        {
            Trace("->", messageType, buffer.ToArray(), binaryHasSeq: false);
            return inner.SendAsync(buffer, messageType, messageFlags, cancellationToken);
        }

        private void Trace(string direction, WebSocketMessageType type, byte[] data, bool binaryHasSeq)
        {
            try
            {
                if (type is WebSocketMessageType.Binary)
                {
                    var opIndex = binaryHasSeq ? 2 : 0;
                    var op = data.Length > opIndex ? data[opIndex] : -1;
                    var extra = op is 29 or 30 && data.Length > opIndex + 2 ? $" transition={(data[opIndex + 1] << 8) | data[opIndex + 2]}" : "";
                    Kzg.Spike.Dave.Log.Write("TRC", $"[{name}] {direction} bin op{op} {data.Length}B{extra}", false);
                    return;
                }
                using var doc = System.Text.Json.JsonDocument.Parse(data);
                var opText = doc.RootElement.GetProperty("op").GetInt32();
                var payload = PayloadOps.Contains(opText) && doc.RootElement.TryGetProperty("d", out var d) ? " " + d.GetRawText() : "";
                if (opText is not (3 or 6))
                {
                    Kzg.Spike.Dave.Log.Write("TRC", $"[{name}] {direction} op{opText}{payload}", false);
                }
            }
            catch (Exception)
            {
                Kzg.Spike.Dave.Log.Write("TRC", $"[{name}] {direction} {type} {data.Length}B (unparsed)", false);
            }
        }
    }
}

/// <summary>Wraps NetCord's UDP socket so the spike can black-hole voice for a while, as a network stall would.</summary>
public sealed class BlackholeUdpProvider : IUdpConnectionProvider
{
    public volatile bool Blackholed;

    public IUdpConnection CreateConnection(string hostname, ushort port) => new Connection(UdpConnectionProvider.Instance.CreateConnection(hostname, port), this);

    private sealed class Connection(IUdpConnection inner, BlackholeUdpProvider owner) : IUdpConnection
    {
        public ValueTask OpenAsync(CancellationToken cancellationToken = default) => inner.OpenAsync(cancellationToken);

        public ValueTask SendAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => owner.Blackholed ? default : inner.SendAsync(buffer, cancellationToken);

        public void Send(ReadOnlySpan<byte> buffer)
        {
            if (!owner.Blackholed)
            {
                inner.Send(buffer);
            }
        }

        public async ValueTask<int> ReceiveAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (true)
            {
                var length = await inner.ReceiveAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (!owner.Blackholed)
                {
                    return length;
                }
            }
        }

        public void Abort() => inner.Abort();

        public void Dispose() => inner.Dispose();
    }
}

/// <summary>Counts NetCord's own UDP receive outcomes (Success, PayloadDecryptionFailed, ...) process-wide.</summary>
public sealed class VoiceMetrics : IDisposable
{
    private readonly MeterListener _listener = new();
    public readonly ConcurrentDictionary<string, long> Counts = new();

    public VoiceMetrics()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "NetCord.Gateway.Voice.VoiceClient" && instrument.Name.EndsWith(".packets", StringComparison.Ordinal))
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) =>
        {
            string kind = "", outcome = "";
            foreach (var tag in tags)
            {
                if (tag.Key == "kind")
                {
                    kind = tag.Value as string ?? "";
                }
                else if (tag.Key == "outcome")
                {
                    outcome = tag.Value as string ?? "";
                }
            }
            Counts.AddOrUpdate($"{instrument.Name}:{kind}:{outcome}", value, (_, n) => n + value);
        });
        _listener.Start();
    }

    public void Dispose() => _listener.Dispose();
}
