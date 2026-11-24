using System.Text;

namespace Kzg.Bot.Logging;

/// <summary>
/// The bot's log as a file that stays small on a host that runs for months: when it passes
/// <see cref="MaxBytes"/> at the end of a line it becomes <c>bot.1.log</c>, the older ones move up, and the
/// oldest past <see cref="Keep"/> goes. So the log never holds more than about (Keep + 1) x MaxBytes.
/// Others may read the file while it is written. Safe for one writer; wrap it in TextWriter.Synchronized for more.
/// </summary>
public sealed class RotatingLogWriter : TextWriter
{
    /// <summary>10 MB a file, two old ones kept: 30 MB at most, weeks of an ordinary bot's log.</summary>
    public const long DefaultMaxBytes = 10 * 1024 * 1024;

    public const int DefaultKeep = 2;

    private readonly string _path;
    private FileStream? _stream;
    private StreamWriter? _writer;

    public RotatingLogWriter(string path, long maxBytes = DefaultMaxBytes, int keep = DefaultKeep)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(keep);
        (_path, MaxBytes, Keep) = (Path.GetFullPath(path), maxBytes, keep);
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        Open(FileMode.Append);
    }

    public long MaxBytes { get; }

    public int Keep { get; }

    public override Encoding Encoding => Encoding.UTF8;

    /// <summary>The n-th older file: <c>bot.1.log</c> is the newest after the live one.</summary>
    public static string OlderFile(string path, int n) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(path))!, $"{Path.GetFileNameWithoutExtension(path)}.{n}{Path.GetExtension(path)}");

    public override void Write(char value)
    {
        _writer?.Write(value);
        if (value == '\n')
        {
            EndOfLine();
        }
    }

    public override void Write(string? value)
    {
        _writer?.Write(value);
        if (value is { Length: > 0 } && value[^1] == '\n')
        {
            EndOfLine();
        }
    }

    public override void WriteLine(string? value)
    {
        _writer?.Write(value);
        _writer?.Write('\n');
        EndOfLine();
    }

    public override void WriteLine() => WriteLine((string?)null);

    public override void Flush() => _writer?.Flush();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _writer?.Dispose();
            _writer = null;
            _stream = null;
        }
        base.Dispose(disposing);
    }

    private void EndOfLine()
    {
        _writer?.Flush();
        if (_stream is { } stream && stream.Length >= MaxBytes)
        {
            Rotate();
        }
    }

    private void Rotate()
    {
        _writer?.Dispose();
        try
        {
            for (var n = Keep; n >= 1; n--)
            {
                var from = n == 1 ? _path : OlderFile(_path, n - 1);
                if (File.Exists(from))
                {
                    File.Move(from, OlderFile(_path, n), overwrite: true);
                }
            }
            Open(FileMode.Append);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another program holds the file so it can't be renamed (Windows): start it afresh instead, so
            // the size stays bounded even if this stretch of the log is lost.
            TryOpen(FileMode.Create);
        }
    }

    /// <summary>A log that can't be written never stops the bot: it writes on where it can, or nowhere.</summary>
    private void TryOpen(FileMode mode)
    {
        foreach (var attempt in new[] { mode, FileMode.Append })
        {
            try
            {
                Open(attempt);
                return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Try the next way, then give up on the file.
            }
        }
        (_stream, _writer) = (null, null);
    }

    private void Open(FileMode mode)
    {
        _stream = new FileStream(_path, mode, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        if (mode == FileMode.Append)
        {
            _stream.Seek(0, SeekOrigin.End);
        }
        _writer = new StreamWriter(_stream, new UTF8Encoding(false)) { AutoFlush = false };
    }
}
