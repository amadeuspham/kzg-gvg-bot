using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;

namespace Kzg.App.Services;

/// <summary>
/// One running app per data folder, and so per player. The first copy holds a lock file for as long as it lives
/// (the system lets go of it when the process dies, however it dies) and listens on a local pipe; a second copy
/// finds the lock taken, asks the first to show its window through the pipe, and exits before it starts a key
/// listener or a connection of its own.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string ShowRequest = "show";
    private const string Answer = "ok";

    private readonly FileStream? _lock;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _listener = Task.CompletedTask;
    private int _disposed;

    private SingleInstance(FileStream? held, string folder)
    {
        _lock = held;
        if (held is not null)
        {
            _listener = Task.Run(() => ListenAsync(PipeName(folder), _stop.Token));
        }
    }

    /// <summary>Raised on a background thread when another copy asked this one to show its window.</summary>
    public event Action? ShowRequested;

    /// <summary>True when this copy really holds the lock; false when the folder couldn't be locked and the guard is off.</summary>
    public bool Guarding => _lock is not null;

    /// <summary>
    /// Takes the right to run for <paramref name="folder"/>. Null means another copy runs: ask it to show itself with
    /// <see cref="RequestShow"/> and exit. A folder that can't be locked at all (read-only, say) turns the guard off
    /// rather than keeping the player from using the app.
    /// </summary>
    public static SingleInstance? Claim(string folder)
    {
        try
        {
            Directory.CreateDirectory(folder);
            var held = new FileStream(Path.Combine(folder, "instance.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new SingleInstance(held, folder);
        }
        catch (IOException) when (IsHeld(folder))
        {
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new SingleInstance(null, folder);
        }
    }

    /// <summary>
    /// Asks the copy that runs for <paramref name="folder"/> to show its window, and waits for its answer, so a request
    /// that landed between two of its listeners is sent again. False when nobody answered in time.
    /// </summary>
    public static bool RequestShow(string folder, TimeSpan patience)
    {
        var deadline = DateTime.UtcNow + patience;
        do
        {
            try
            {
                using var client = new NamedPipeClientStream(".", PipeName(folder), PipeDirection.InOut, PipeOptions.CurrentUserOnly);
                client.Connect(Math.Max(1, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                var bytes = Encoding.UTF8.GetBytes(ShowRequest + "\n");
                client.Write(bytes, 0, bytes.Length);
                client.Flush();
                using var reader = new StreamReader(client, Encoding.UTF8);
                if (reader.ReadLine() == Answer)
                {
                    return true;
                }
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(50);
            }
        }
        while (DateTime.UtcNow < deadline);
        return false;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }
        _stop.Cancel();
        try
        {
            _listener.Wait(TimeSpan.FromSeconds(1));
        }
        catch (AggregateException)
        {
            // The listener ended with the cancellation.
        }
        _lock?.Dispose();
        _stop.Dispose();
    }

    // The lock file is taken only when another process (or another stream here) has it open without sharing.
    private static bool IsHeld(string folder)
    {
        try
        {
            using var probe = new FileStream(Path.Combine(folder, "instance.lock"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return false;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return true;
        }
    }

    // Short and the same for the same folder: a Unix pipe name becomes a socket path with a small length limit.
    private static string PipeName(string folder)
    {
        var full = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar);
        return "kzg-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..16];
    }

    private async Task ListenAsync(string name, CancellationToken cancellation)
    {
        while (!cancellation.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                // Several instances, and the next one is up before this caller is read: two copies started at once are both heard.
                server = new NamedPipeServerStream(
                    name, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellation).ConfigureAwait(false);
                _ = ReadRequestAsync(server, cancellation);
            }
            catch (OperationCanceledException)
            {
                server?.Dispose();
            }
            catch (IOException)
            {
                server?.Dispose();
            }
        }
    }

    private async Task ReadRequestAsync(NamedPipeServerStream server, CancellationToken cancellation)
    {
        try
        {
            using var stream = server;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            using var patience = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            patience.CancelAfter(TimeSpan.FromSeconds(2));
            if (await reader.ReadLineAsync(patience.Token).ConfigureAwait(false) == ShowRequest)
            {
                ShowRequested?.Invoke();
                var answer = Encoding.UTF8.GetBytes(Answer + "\n");
                await stream.WriteAsync(answer, patience.Token).ConfigureAwait(false);
                await stream.FlushAsync(patience.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            // A caller that said nothing or went away mid-message.
        }
    }
}
