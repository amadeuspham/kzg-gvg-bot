using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Kzg.App.Input;
using Kzg.Protocol;

namespace Kzg.App.Settings;

/// <summary>What reading the settings file gave.</summary>
public abstract record SettingsLoad(AppSettings Settings)
{
    /// <summary>The file was read as written.</summary>
    public sealed record Loaded(AppSettings Settings) : SettingsLoad(Settings);

    /// <summary>No file yet: first run, with defaults.</summary>
    public sealed record FirstRun(AppSettings Settings) : SettingsLoad(Settings);

    /// <summary>
    /// The file was unreadable or broke a rule. It was kept as <c>settings.invalid.json</c> beside it,
    /// and defaults are in use, so the player can link again without losing the broken file.
    /// </summary>
    public sealed record Invalid(AppSettings Settings, string Reason) : SettingsLoad(Settings);
}

/// <summary>
/// The app's settings file: strict JSON (an unknown or repeated setting, or a value of the wrong kind,
/// is an error), written whole or not at all, and readable by its owner only on Unix because it holds
/// the device token.
/// </summary>
public sealed class SettingsStore(string path)
{
    public const int SchemaVersion = 1;

    private const int MaxBytes = 1 << 16;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public SettingsLoad Load()
    {
        string text;
        try
        {
            using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes)
            {
                return Invalid("The settings file is too large.");
            }
            using var reader = new StreamReader(stream, new UTF8Encoding(false, throwOnInvalidBytes: true));
            text = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return new SettingsLoad.FirstRun(AppSettings.CreateDefault());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return Invalid("The settings file could not be read.");
        }
        SettingsFile? file;
        try
        {
            file = JsonSerializer.Deserialize<SettingsFile>(text, Options);
        }
        catch (JsonException exception)
        {
            // The exception text can quote the file, which holds a token; the path names only where it went wrong.
            return Invalid($"The settings file is not valid at {exception.Path ?? "$"}.");
        }
        if (file?.ToSettings() is not { } settings)
        {
            return Invalid(file is null ? "The settings file must hold a JSON object." : file.Problem() ?? "The settings file is not valid.");
        }
        return new SettingsLoad.Loaded(settings);
    }

    /// <summary>Writes the whole file or nothing. False when the folder or file can't be written.</summary>
    public bool TrySave(AppSettings settings)
    {
        var folder = System.IO.Path.GetDirectoryName(Path)!;
        var temp = System.IO.Path.Combine(folder, $".{System.IO.Path.GetFileName(Path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(folder);
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            }
            var json = JsonSerializer.Serialize(SettingsFile.From(settings), Options) + "\n";
            using (var stream = new FileStream(temp, options))
            {
                stream.Write(new UTF8Encoding(false).GetBytes(json));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, Path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                // A stray temporary file is harmless.
            }
            return false;
        }
    }

    private SettingsLoad.Invalid Invalid(string reason)
    {
        try
        {
            File.Copy(Path, System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Path)!, "settings.invalid.json"), overwrite: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Nothing to keep.
        }
        return new SettingsLoad.Invalid(AppSettings.CreateDefault(), reason);
    }

    private sealed class SettingsFile
    {
        public int SchemaVersion { get; init; }

        public string ServerHost { get; init; } = "";

        public Guid InstallId { get; init; }

        public string? DeviceToken { get; init; }

        public BindingsFile Bindings { get; init; } = new();

        public bool NotifyOnDisconnect { get; init; }

        public static SettingsFile From(AppSettings settings) => new()
        {
            SchemaVersion = Settings.SettingsStore.SchemaVersion,
            ServerHost = settings.ServerHost,
            InstallId = settings.InstallId,
            DeviceToken = settings.DeviceToken,
            Bindings = new BindingsFile
            {
                Broadcast = Write(settings.Bindings.Broadcast),
                AllTeams = Write(settings.Bindings.AllTeams),
                OpenMic = Write(settings.Bindings.OpenMic),
            },
            NotifyOnDisconnect = settings.NotifyOnDisconnect,
        };

        public string? Problem()
        {
            if (SchemaVersion != Settings.SettingsStore.SchemaVersion)
            {
                return "The settings file is from a different version of the app.";
            }
            if (ServerHost.Length > 0 && !ConnectCode.IsValidHost(ServerHost))
            {
                return "The server address is not a host name.";
            }
            if (InstallId == Guid.Empty)
            {
                return "The install id is missing.";
            }
            if (DeviceToken is not null && !WireSecret.IsValid(DeviceToken))
            {
                return "The device token is not valid.";
            }
            return Parse(Bindings, out _, out var problem) ? null : problem;
        }

        public AppSettings? ToSettings() =>
            Problem() is null && Parse(Bindings, out var set, out _)
                ? new AppSettings
                {
                    ServerHost = ServerHost,
                    InstallId = InstallId,
                    DeviceToken = DeviceToken,
                    Bindings = set!,
                    NotifyOnDisconnect = NotifyOnDisconnect,
                }
                : null;

        // An action the player left unset is written as an empty list.
        private static List<string> Write(InputBinding? input) => input is { } bound ? [bound.Format()] : [];

        // One input per action: an older file may list several, and the first one stays. An empty list is
        // unset. A missing list, as in a file from before the action existed, takes the default, unless
        // another action already has that input: then it is unset, so the file still loads.
        private static bool Parse(BindingsFile file, out BindingSet? set, out string? problem)
        {
            set = null;
            problem = null;
            var parsed = new BindingSet(null, null, null);
            List<BindingAction> missing = [];
            foreach (var action in BindingSet.Actions)
            {
                switch (file[action])
                {
                    case null:
                        missing.Add(action);
                        break;
                    case []:
                        break;
                    case [var text, ..] when InputBinding.TryParse(text, out var binding):
                        parsed = parsed.With(action, binding);
                        break;
                    default:
                        problem = "A binding is not an input the app knows.";
                        return false;
                }
            }
            foreach (var action in missing)
            {
                var fallback = BindingSet.Default[action];
                if (fallback is { } input && parsed.ActionOf(input) is null)
                {
                    parsed = parsed.With(action, input);
                }
            }
            problem = parsed.Problem();
            set = problem is null ? parsed : null;
            return problem is null;
        }
    }

    private sealed class BindingsFile
    {
        public List<string>? Broadcast { get; init; }

        public List<string>? AllTeams { get; init; }

        public List<string>? OpenMic { get; init; }

        public List<string>? this[BindingAction action] => action switch
        {
            BindingAction.Broadcast => Broadcast,
            BindingAction.AllTeams => AllTeams,
            _ => OpenMic,
        };
    }
}
