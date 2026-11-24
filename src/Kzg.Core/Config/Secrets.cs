using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace Kzg.Core.Config;

/// <summary>
/// The bot's secrets by name: the bot tokens <see cref="BotEntry.TokenRef"/> names, and the Discord
/// client secret. It prints how many it holds, never a name's value, so logging it reveals nothing.
/// </summary>
public sealed partial class Secrets
{
    /// <summary>The name the Discord application's client secret goes by.</summary>
    public const string OAuthClientSecretName = "oauthClientSecret";

    /// <summary>An environment variable named this and a secret's name sets or overrides that secret.</summary>
    public const string EnvironmentPrefix = "KZG_SECRET_";

    private readonly IReadOnlyDictionary<string, string> _values;

    internal Secrets(IReadOnlyDictionary<string, string> values, bool permissionsTightened)
    {
        _values = values;
        PermissionsTightened = permissionsTightened;
    }

    public static Secrets Empty { get; } = new(new Dictionary<string, string>(), false);

    /// <summary>The names with a value, from the file or the environment.</summary>
    public IEnumerable<string> Names => _values.Keys;

    /// <summary>
    /// The file could be read by others, so Kzg made it the owner's alone, as it must be. The host
    /// says so in its log; the secrets were exposed on disk until then.
    /// </summary>
    public bool PermissionsTightened { get; }

    /// <summary>A name the file and the environment can both spell: letters, digits and underscores.</summary>
    public static bool IsValidName(string? name) => name is not null && NamePattern().IsMatch(name);

    public bool Has(string name) => _values.ContainsKey(name);

    public bool TryGet(string name, [NotNullWhen(true)] out string? value) => _values.TryGetValue(name, out value);

    /// <summary>
    /// The file's secrets with the environment's laid over them. An empty value, in either, sets
    /// nothing: the example file's blanks are secrets not filled in yet.
    /// </summary>
    public static Secrets Combine(IReadOnlyDictionary<string, string> file, IDictionary environment, bool permissionsTightened = false)
    {
        var values = file.Where(pair => pair.Value.Length > 0).ToDictionary(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in environment)
        {
            if (entry.Key is string key && key.StartsWith(EnvironmentPrefix, StringComparison.Ordinal)
                && key[EnvironmentPrefix.Length..] is var name && IsValidName(name)
                && entry.Value is string { Length: > 0 } value)
            {
                values[name] = value;
            }
        }
        return new Secrets(values, permissionsTightened);
    }

    public override string ToString() => $"{_values.Count} secret(s)";

    [GeneratedRegex("^[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();
}

/// <summary>
/// <c>secrets.json</c>: one JSON object of names to secret strings. The setup page writes it, or an
/// admin fills it from the example file. It must be the owner's alone: Kzg writes it so from its first
/// byte, and makes a hand-made one so on Unix when it reads it.
/// </summary>
public sealed class SecretsStore(string filePath)
{
    public string FilePath { get; } = filePath;

    /// <summary>
    /// Sets the named secrets and removes others, keeping every other entry the file holds, such as a
    /// hand-added client secret. A file that is there but broken is left alone, so nothing is lost.
    /// </summary>
    /// <param name="set">Names to values; every name must be valid and every value non-empty.</param>
    public FileSave Save(IReadOnlyDictionary<string, string> set, IEnumerable<string>? remove = null)
    {
        if (set.Any(pair => !Secrets.IsValidName(pair.Key) || string.IsNullOrEmpty(pair.Value)))
        {
            return new FileSave.Invalid([new ConfigError(null, null, "a secret needs a valid name and a value")]);
        }
        Dictionary<string, string> file;
        switch (JsonFile.TryRead(FilePath, out var text))
        {
            case JsonFile.ReadOutcome.Missing:
                file = new Dictionary<string, string>(StringComparer.Ordinal);
                break;
            case JsonFile.ReadOutcome.Read:
                var parsed = JsonFile.Parse<Dictionary<string, string>>(text, out _);
                if (parsed is null || parsed.Any(pair => !Secrets.IsValidName(pair.Key) || pair.Value is null))
                {
                    return new FileSave.Invalid([new ConfigError(null, null, "secrets.json is there but broken; fix or remove it first")]);
                }
                file = new Dictionary<string, string>(parsed, StringComparer.Ordinal);
                break;
            default:
                return new FileSave.Unwritable();
        }
        foreach (var name in remove ?? [])
        {
            file.Remove(name);
        }
        foreach (var (name, value) in set)
        {
            file[name] = value;
        }
        var sorted = new SortedDictionary<string, string>(file, StringComparer.Ordinal);
        return JsonFile.TryWriteAtomic(FilePath, JsonFile.Serialize(sorted), ownerOnly: true)
            ? new FileSave.Saved() : new FileSave.Unwritable();
    }

    /// <summary>Reads the file afresh and lays the <c>KZG_SECRET_*</c> environment over it.</summary>
    /// <param name="environment">The process environment, as <see cref="Environment.GetEnvironmentVariables()"/> gives it.</param>
    public FileLoad<Secrets> Load(IDictionary environment)
    {
        switch (JsonFile.TryRead(FilePath, out var text))
        {
            case JsonFile.ReadOutcome.Missing:
                // The environment alone can hold every secret, as a container host would set them.
                return new FileLoad<Secrets>.Loaded(Secrets.Combine(new Dictionary<string, string>(), environment));
            case JsonFile.ReadOutcome.Unreadable:
                return new FileLoad<Secrets>.Unreadable();
            case JsonFile.ReadOutcome.TooLarge:
                return new FileLoad<Secrets>.Broken([new ConfigError(null, null, "the file is too large")]);
        }
        var file = JsonFile.Parse<Dictionary<string, string>>(text, out var error);
        if (file is null)
        {
            // A token pasted where a name goes would show in the path, so only a valid name is named.
            return new FileLoad<Secrets>.Broken([error! with { Setting = Secrets.IsValidName(error.Setting) ? error.Setting : null }]);
        }
        // Names are checked, never quoted: a bad one may be a token pasted in the wrong place.
        List<ConfigError> errors = [];
        foreach (var (name, value) in file)
        {
            if (!Secrets.IsValidName(name))
            {
                errors.Add(new ConfigError(null, null, "a secret's name is 1 to 64 letters, digits or underscores"));
            }
            // The reader lets a null value into a dictionary whatever its type says.
            else if (value is null)
            {
                errors.Add(new ConfigError(null, name, "a string; leave it \"\" while it isn't filled in"));
            }
        }
        if (errors.Count > 0)
        {
            return new FileLoad<Secrets>.Broken(errors);
        }
        return new FileLoad<Secrets>.Loaded(Secrets.Combine(file, environment, TightenPermissions()));
    }

    private bool TightenPermissions()
    {
        if (OperatingSystem.IsWindows())
        {
            return false;
        }
        const UnixFileMode Others = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        try
        {
            if ((File.GetUnixFileMode(FilePath) & Others) == 0)
            {
                return false;
            }
            File.SetUnixFileMode(FilePath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Someone else's file: it stays as it is, and the host can still read it.
            return false;
        }
    }
}
