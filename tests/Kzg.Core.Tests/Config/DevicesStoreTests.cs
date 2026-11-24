using Kzg.Core.Abstractions;
using Kzg.Core.Config;

namespace Kzg.Core.Tests.Config;

public sealed class DevicesStoreTests : IDisposable
{
    private static readonly DateTimeOffset Issued = new(2026, 11, 1, 20, 0, 0, TimeSpan.Zero);

    private readonly TempFolder _folder = new();
    private readonly DevicesStore _store;

    public DevicesStoreTests() => _store = new DevicesStore(_folder.File("devices.json"));

    public void Dispose() => _folder.Dispose();

    private static DeviceRecord Device(char hash = 'a', ulong user = 900) =>
        new(new string(hash, 64), new UserId(user), Guid.Parse("6f1c2a4e-0000-4000-8000-000000000001"), Issued, Issued.AddDays(3));

    [Fact]
    public void Saved_devices_load_back_the_same()
    {
        DeviceRecord[] devices = [Device('a'), Device('b', 901) with { LastSeenAt = Issued }];

        Assert.IsType<FileSave.Saved>(_store.Save(devices));

        Assert.Equal(devices, Assert.IsType<FileLoad<IReadOnlyList<DeviceRecord>>.Loaded>(new DevicesStore(_store.FilePath).Load()).Value);
    }

    [Fact]
    public void No_file_is_no_devices_yet()
    {
        Assert.Empty(Assert.IsType<FileLoad<IReadOnlyList<DeviceRecord>>.Loaded>(_store.Load()).Value);
    }

    [Fact]
    public void The_file_is_the_owners_alone()
    {
        Assert.IsType<FileSave.Saved>(_store.Save([Device()]));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(_store.FilePath));
        }
        Assert.Contains("\"userId\": \"900\"", File.ReadAllText(_store.FilePath), StringComparison.Ordinal);
    }

    public static TheoryData<string, DeviceRecord[]> BrokenRecords => new()
    {
        { "devices[0].tokenHash", [Device() with { TokenHash = new string('A', 64) }] },
        { "devices[0].tokenHash", [Device() with { TokenHash = new string('a', 63) }] },
        { "devices[1].tokenHash", [Device(), Device(user: 901)] },
        { "devices[0].userId", [Device(user: 0)] },
        { "devices[0].installId", [Device() with { InstallId = Guid.Empty }] },
        { "devices[0].lastSeenAt", [Device() with { LastSeenAt = Issued.AddSeconds(-1) }] },
        { "devices[1]", [Device(), null!] },
    };

    [Theory]
    [MemberData(nameof(BrokenRecords))]
    public void A_record_that_breaks_a_rule_is_never_written(string setting, DeviceRecord[] devices)
    {
        var refused = Assert.IsType<FileSave.Invalid>(_store.Save(devices));

        Assert.Equal(setting, Assert.Single(refused.Errors).Setting);
        Assert.False(File.Exists(_store.FilePath));
    }

    [Theory]
    [InlineData("""{ "devices": [] }""", null)]
    [InlineData("""{ "schemaVersion": 2, "devices": [] }""", "schemaVersion")]
    [InlineData("""{ "schemaVersion": 1, "devices": [ { "tokenHash": "ab", "userId": "9", "installId": "6f1c2a4e-0000-4000-8000-000000000001", "issuedAt": "2026-11-01T20:00:00+00:00", "lastSeenAt": "2026-11-01T20:00:00+00:00" } ] }""", "devices[0].tokenHash")]
    [InlineData("""{ "schemaVersion": 1, "devices": [ { "tokenHash": "ab" } ] }""", "devices[0]")]
    public void A_broken_file_is_refused(string text, string? setting)
    {
        File.WriteAllText(_store.FilePath, text);

        var error = Assert.IsType<FileLoad<IReadOnlyList<DeviceRecord>>.Broken>(_store.Load()).Errors[0];

        Assert.Equal(setting, error.Setting);
    }

    [Fact]
    public void A_record_prints_only_a_hash_prefix()
    {
        Assert.Equal("device aaaaaaaa… of 900", Device().ToString());
        Assert.True(DevicesStore.IsValidTokenHash(new string('0', 64)));
        Assert.False(DevicesStore.IsValidTokenHash(null));
    }
}
