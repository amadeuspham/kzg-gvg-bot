using System.Collections.Concurrent;
using System.Diagnostics;

namespace InputSpike;

/// <summary>
/// Timestamped console + file log. Callers only enqueue (cheap, never blocks); a logger thread does the I/O,
/// so a stalled console (QuickEdit selection) cannot slow the hook thread.
/// </summary>
static class Log
{
    sealed record Item(DateTime Utc, double Ms, string Source, string Message, IntPtr Hwnd);

    static readonly BlockingCollection<Item> Queue = new(new ConcurrentQueue<Item>());
    static readonly Stopwatch Mono = Stopwatch.StartNew();
    static StreamWriter? _file;
    static Thread? _thread;

    static bool _relay;

    public static string? Path { get; private set; }

    public static void Open(string path)
    {
        Path = path;
        _file = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Start();
    }

    /// <summary>Child mode: no file; each item goes to stdout as "source\tmessage" for the parent to re-log on its clock.</summary>
    public static void OpenRelay()
    {
        _relay = true;
        Start();
    }

    static void Start()
    {
        _thread = new Thread(Drain) { IsBackground = true, Name = "log" };
        _thread.Start();
    }

    /// <summary>Takes the timestamps now; an optional window handle is resolved to a process name on the logger thread.</summary>
    public static void Write(string source, string message, IntPtr hwnd = default) =>
        TryAdd(new Item(DateTime.UtcNow, Mono.Elapsed.TotalMilliseconds, source, message, hwnd));

    static void TryAdd(Item i)
    {
        try { Queue.Add(i); } catch (InvalidOperationException) { /* shutting down: late hook events are dropped */ }
    }

    public static void Flush()
    {
        Queue.CompleteAdding();
        _thread?.Join(2000);
    }

    static void Drain()
    {
        foreach (var i in Queue.GetConsumingEnumerable())
        {
            if (_relay)
            {
                try { Console.Out.WriteLine($"{i.Source}\t{i.Message}"); Console.Out.Flush(); } catch { /* parent gone */ }
                continue;
            }
            var fg = i.Hwnd != IntPtr.Zero && OperatingSystem.IsWindows() ? "  fg=" + WinProbe.Describe(i.Hwnd) : "";
            var line = $"{i.Utc:HH:mm:ss.fff}Z +{i.Ms,10:F1}ms [{i.Source,-7}] {i.Message}{fg}";
            try { Console.WriteLine(line); } catch { /* console gone */ }
            _file?.WriteLine(line);
        }
    }
}
