namespace Kzg.Core.Config;

/// <summary>The bot's data folder and the files in it.</summary>
public sealed record DataPaths(string Folder)
{
    public string ConfigFile => Path.Combine(Folder, "config.json");

    public string SecretsFile => Path.Combine(Folder, "secrets.json");

    public string DevicesFile => Path.Combine(Folder, "devices.json");

    /// <summary>
    /// <c>--data-dir</c> when given; otherwise "Kzg" in the OS's per-user application data folder:
    /// %LOCALAPPDATA% on Windows, ~/Library/Application Support on macOS, ~/.local/share on Linux.
    /// </summary>
    public static DataPaths Resolve(string? dataDir) =>
        new(Path.GetFullPath(string.IsNullOrWhiteSpace(dataDir)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify), "Kzg")
            : dataDir));
}
