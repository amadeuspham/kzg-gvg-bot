using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kzg.Core.Config;

/// <summary>
/// Reading and writing the bot's JSON data files, all alike: comments and trailing commas allowed for
/// hand edits, but a misspelt setting, one written twice, or a value of the wrong kind is an error, so
/// a slip never passes unseen. Writes go to a temporary file beside the target and are moved over it.
/// </summary>
internal static class JsonFile
{
    /// <summary>Far beyond any real file; a bigger one is a mistake, not data.</summary>
    internal const int MaxBytes = 1 << 20;

    internal static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        // Channel and player names stay readable: the files are never put into a web page.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        Converters = { new UserIdConverter(), new ChannelIdConverter() },
    };

    internal enum ReadOutcome
    {
        Read,
        Missing,
        Unreadable,
        TooLarge,
    }

    internal static ReadOutcome TryRead(string path, out string text)
    {
        text = "";
        try
        {
            // Shared with whatever else has the file open, such as an editor saving it.
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes)
            {
                return ReadOutcome.TooLarge;
            }
            using var reader = new StreamReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: true));
            text = reader.ReadToEnd();
            return ReadOutcome.Read;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return ReadOutcome.Missing;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return ReadOutcome.Unreadable;
        }
    }

    /// <summary>The value, or the one error that stopped the reader. The error never quotes the file.</summary>
    internal static T? Parse<T>(string text, out ConfigError? error)
        where T : class
    {
        error = null;
        try
        {
            var value = JsonSerializer.Deserialize<T>(text, Options);
            if (value is null)
            {
                error = new ConfigError(1, null, "the file must hold a JSON object");
            }
            return value;
        }
        catch (JsonException exception)
        {
            // The reader counts lines from 0, and its paths start at "$", the file as a whole.
            var setting = exception.Path is { Length: > 1 } path ? path[1..].TrimStart('.') : null;
            error = new ConfigError((int)(exception.LineNumber ?? 0) + 1, setting,
                "not valid here: an unknown or repeated setting, or a value of the wrong kind");
            return null;
        }
    }

    internal static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options) + "\n";

    /// <summary>
    /// Writes the whole file or nothing: a crash mid-write leaves the old file, never half a new one.
    /// An owner-only file is created so from its first byte, never readable by others even briefly.
    /// </summary>
    internal static bool TryWriteAtomic(string path, string text, bool ownerOnly)
    {
        var full = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(full)!;
        // A name of its own, so two writers never share a temporary file.
        var temp = Path.Combine(folder, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (ownerOnly && !OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(text));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            TryDelete(temp);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Left behind: a stray temporary file is harmless, and the next save picks another name.
        }
    }
}
