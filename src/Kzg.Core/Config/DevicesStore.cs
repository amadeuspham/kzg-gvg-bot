using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Kzg.Core.Abstractions;

namespace Kzg.Core.Config;

/// <summary>
/// One linked app install. Only the token's SHA-256 is kept, so the file can't sign anyone in. The
/// times are wall-clock, unlike the engine's: a token's life spans bot restarts.
/// </summary>
/// <param name="TokenHash">SHA-256 of the token's 32 bytes, as 64 lower-case hex digits.</param>
public sealed record DeviceRecord(string TokenHash, UserId UserId, Guid InstallId, DateTimeOffset IssuedAt, DateTimeOffset LastSeenAt)
{
    public override string ToString() => $"device {TokenHash[..Math.Min(8, TokenHash.Length)]}… of {UserId}";
}

/// <summary><c>devices.json</c> as it is written: a schema version and the records.</summary>
internal sealed record DevicesFile
{
    [JsonRequired]
    public int SchemaVersion { get; init; }

    public IReadOnlyList<DeviceRecord> Devices { get; init; } = [];
}

/// <summary>
/// <c>devices.json</c>: every linked app install. Written whole, atomically and owner-only, after
/// each change; whether a token is known, expired or revoked is the pairing code's call.
/// </summary>
public sealed partial class DevicesStore(string filePath)
{
    public const int CurrentSchemaVersion = 1;

    public string FilePath { get; } = filePath;

    /// <summary>Reads the file afresh; a missing file is no devices yet.</summary>
    public FileLoad<IReadOnlyList<DeviceRecord>> Load()
    {
        switch (JsonFile.TryRead(FilePath, out var text))
        {
            case JsonFile.ReadOutcome.Missing:
                return new FileLoad<IReadOnlyList<DeviceRecord>>.Loaded([]);
            case JsonFile.ReadOutcome.Unreadable:
                return new FileLoad<IReadOnlyList<DeviceRecord>>.Unreadable();
            case JsonFile.ReadOutcome.TooLarge:
                return new FileLoad<IReadOnlyList<DeviceRecord>>.Broken([new ConfigError(null, null, "the file is too large")]);
        }
        var file = JsonFile.Parse<DevicesFile>(text, out var error);
        if (file is null)
        {
            return new FileLoad<IReadOnlyList<DeviceRecord>>.Broken([error!]);
        }
        List<ConfigError> errors = [];
        if (file.SchemaVersion != CurrentSchemaVersion)
        {
            errors.Add(new ConfigError(null, "schemaVersion", $"this build reads schema version {CurrentSchemaVersion} only"));
        }
        errors.AddRange(Validate(file.Devices));
        return errors.Count > 0 ? new FileLoad<IReadOnlyList<DeviceRecord>>.Broken(errors)
            : new FileLoad<IReadOnlyList<DeviceRecord>>.Loaded(file.Devices);
    }

    /// <summary>Writes every record if they keep the rules; otherwise writes nothing and says why.</summary>
    public FileSave Save(IReadOnlyList<DeviceRecord> devices)
    {
        var errors = Validate(devices);
        if (errors.Count > 0)
        {
            return new FileSave.Invalid(errors);
        }
        var text = JsonFile.Serialize(new DevicesFile { SchemaVersion = CurrentSchemaVersion, Devices = devices });
        return JsonFile.TryWriteAtomic(FilePath, text, ownerOnly: true) ? new FileSave.Saved() : new FileSave.Unwritable();
    }

    public static bool IsValidTokenHash(string? hash) => hash is not null && HashPattern().IsMatch(hash);

    private static List<ConfigError> Validate(IReadOnlyList<DeviceRecord> devices)
    {
        List<ConfigError> errors = [];
        HashSet<string> hashes = new(StringComparer.Ordinal);
        for (var i = 0; i < devices.Count; i++)
        {
            void Error(string field, string problem) => errors.Add(new ConfigError(null, $"devices[{i}]{field}", problem));
            if (devices[i] is not { } device)
            {
                Error("", "a device, not null");
                continue;
            }
            if (!IsValidTokenHash(device.TokenHash))
            {
                Error(".tokenHash", "64 lower-case hex digits");
            }
            else if (!hashes.Add(device.TokenHash))
            {
                Error(".tokenHash", "each token is listed once");
            }
            if (device.UserId.V == 0)
            {
                Error(".userId", "a Discord user");
            }
            if (device.InstallId == Guid.Empty)
            {
                Error(".installId", "not the empty GUID");
            }
            if (device.LastSeenAt < device.IssuedAt)
            {
                Error(".lastSeenAt", "not before issuedAt");
            }
        }
        return errors;
    }

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex HashPattern();
}
