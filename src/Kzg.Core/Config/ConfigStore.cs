namespace Kzg.Core.Config;

/// <summary>
/// <c>config.json</c>: JSON an admin can read and edit, comments and trailing commas allowed. Kzg
/// writes it whole, so comments go with the next save. Every value is checked on reading and before
/// writing, so neither a hand edit nor a <c>/gvg</c> command can leave a config that breaks a rule.
/// </summary>
public sealed class ConfigStore(string filePath)
{
    public string FilePath { get; } = filePath;

    public static string Serialize(BotConfig config) => JsonFile.Serialize(config);

    /// <summary>Reads the file afresh; a missing file is <see cref="FileLoad{T}.Missing"/>, for the host to write the defaults.</summary>
    /// <param name="secrets">Where every <see cref="BotEntry.TokenRef"/> must resolve.</param>
    /// <param name="checkSecrets">False to read the settings while the tokens aren't there, as the setup page does.</param>
    public FileLoad<BotConfig> Load(Secrets secrets, bool checkSecrets = true)
    {
        switch (JsonFile.TryRead(FilePath, out var text))
        {
            case JsonFile.ReadOutcome.Missing:
                return new FileLoad<BotConfig>.Missing();
            case JsonFile.ReadOutcome.Unreadable:
                return new FileLoad<BotConfig>.Unreadable();
            case JsonFile.ReadOutcome.TooLarge:
                return new FileLoad<BotConfig>.Broken([new ConfigError(null, null, "the file is too large")]);
        }
        return Parse(text, secrets, checkSecrets);
    }

    public static FileLoad<BotConfig> Parse(string text, Secrets secrets, bool checkSecrets = true)
    {
        var config = JsonFile.Parse<BotConfig>(text, out var error);
        if (config is null)
        {
            return new FileLoad<BotConfig>.Broken([error!]);
        }
        var errors = ConfigValidation.Validate(config, secrets, checkSecrets);
        return errors.Count > 0 ? new FileLoad<BotConfig>.Broken(errors) : new FileLoad<BotConfig>.Loaded(config);
    }

    /// <summary>Writes the config if it keeps every rule; otherwise writes nothing and says why.</summary>
    public FileSave Save(BotConfig config, Secrets secrets)
    {
        var errors = ConfigValidation.Validate(config, secrets);
        if (errors.Count > 0)
        {
            return new FileSave.Invalid(errors);
        }
        return JsonFile.TryWriteAtomic(FilePath, Serialize(config), ownerOnly: false)
            ? new FileSave.Saved() : new FileSave.Unwritable();
    }
}
