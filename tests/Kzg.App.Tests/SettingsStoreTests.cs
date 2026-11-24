using Kzg.App.Input;
using Kzg.App.Settings;
using Kzg.Protocol;
using SharpHook.Data;

namespace Kzg.App.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-app-tests-" + Guid.NewGuid().ToString("N"));

    private SettingsStore Store => new(Path.Combine(_folder, "settings.json"));

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void A_missing_file_is_a_first_run_with_a_fresh_install_id_and_default_keys()
    {
        var load = Store.Load();

        var first = Assert.IsType<SettingsLoad.FirstRun>(load);
        Assert.NotEqual(Guid.Empty, first.Settings.InstallId);
        Assert.Null(first.Settings.DeviceToken);
        Assert.Equal(BindingSet.Default, first.Settings.Bindings);
    }

    [Fact]
    public void Saved_settings_read_back_the_same()
    {
        var saved = AppSettings.CreateDefault() with
        {
            ServerHost = "gvg.example.com:8443",
            DeviceToken = WireSecret.Generate(),
            NotifyOnDisconnect = true,
            Bindings = new BindingSet(InputBinding.Mouse(MouseButton.Button4), null, InputBinding.Key(KeyCode.VcF10)),
        };
        Assert.True(Store.TrySave(saved));

        var load = Assert.IsType<SettingsLoad.Loaded>(Store.Load());

        Assert.Equal(saved.ServerHost, load.Settings.ServerHost);
        Assert.Equal(saved.InstallId, load.Settings.InstallId);
        Assert.Equal(saved.DeviceToken, load.Settings.DeviceToken);
        Assert.True(load.Settings.NotifyOnDisconnect);
        Assert.Equal(saved.Bindings, load.Settings.Bindings);
    }

    [Fact]
    public void Older_files_with_several_bindings_keep_the_first_of_each()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Store.Path, """
            {
              "schemaVersion": 1,
              "installId": "11111111-2222-3333-4444-555555555555",
              "bindings": { "broadcast": ["mouse:Button4", "pad:a", "key:VcF8"], "openMic": ["pad:b", "key:VcF9"] }
            }
            """);

        var load = Assert.IsType<SettingsLoad.Loaded>(Store.Load());

        Assert.Equal(new BindingSet(InputBinding.Mouse(MouseButton.Button4), BindingSet.Default.AllTeams, InputBinding.Pad("b")), load.Settings.Bindings);
    }

    [Fact]
    public void An_empty_binding_list_means_the_player_left_that_action_unset()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Store.Path, """
            { "schemaVersion": 1, "installId": "11111111-2222-3333-4444-555555555555", "bindings": { "broadcast": [], "allTeams": [], "openMic": ["key:VcF10"] } }
            """);

        var load = Assert.IsType<SettingsLoad.Loaded>(Store.Load());

        Assert.Equal(new BindingSet(null, null, InputBinding.Key(KeyCode.VcF10)), load.Settings.Bindings);
    }

    [Fact]
    public void Unset_actions_are_saved_as_empty_lists_and_read_back_unset()
    {
        var saved = AppSettings.CreateDefault() with { Bindings = new BindingSet(null, null, null) };
        Assert.True(Store.TrySave(saved));

        Assert.Contains("\"allTeams\": []", File.ReadAllText(Store.Path), StringComparison.Ordinal);
        Assert.Equal(saved.Bindings, Assert.IsType<SettingsLoad.Loaded>(Store.Load()).Settings.Bindings);
    }

    [Fact]
    public void A_file_from_before_the_all_teams_key_gets_f7_for_it()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Store.Path, """
            { "schemaVersion": 1, "installId": "11111111-2222-3333-4444-555555555555", "bindings": { "broadcast": ["key:VcF8"], "openMic": ["key:VcF9"] } }
            """);

        Assert.Equal(BindingSet.Default, Assert.IsType<SettingsLoad.Loaded>(Store.Load()).Settings.Bindings);
    }

    [Fact]
    public void A_file_from_before_the_all_teams_key_that_uses_f7_already_loads_with_all_teams_unset()
    {
        var token = WireSecret.Generate();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Store.Path, $$"""
            { "schemaVersion": 1, "installId": "11111111-2222-3333-4444-555555555555", "deviceToken": "{{token}}",
              "bindings": { "broadcast": ["key:VcF7"], "openMic": ["key:VcF9"] } }
            """);

        var load = Assert.IsType<SettingsLoad.Loaded>(Store.Load());

        Assert.Equal(new BindingSet(InputBinding.Key(KeyCode.VcF7), null, InputBinding.Key(KeyCode.VcF9)), load.Settings.Bindings);
        Assert.Equal(token, load.Settings.DeviceToken);
    }

    [Fact]
    public void Saving_leaves_no_temporary_file_and_replaces_the_old_one()
    {
        Assert.True(Store.TrySave(AppSettings.CreateDefault()));
        Assert.True(Store.TrySave(AppSettings.CreateDefault() with { ServerHost = "a.example.com" }));

        Assert.Equal(["settings.json"], Directory.GetFiles(_folder).Select(Path.GetFileName));
        Assert.Equal("a.example.com", Store.Load().Settings.ServerHost);
    }

    [Fact]
    public void On_unix_the_file_is_readable_by_its_owner_only()
    {
        Assert.True(Store.TrySave(AppSettings.CreateDefault() with { DeviceToken = WireSecret.Generate() }));

        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Store.Path));
        }
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":2,\"installId\":\"11111111-2222-3333-4444-555555555555\"}")]
    [InlineData("{\"schemaVersion\":1}")]
    [InlineData("{\"schemaVersion\":1,\"installId\":\"11111111-2222-3333-4444-555555555555\",\"serverHost\":\"https://x/y\"}")]
    [InlineData("{\"schemaVersion\":1,\"installId\":\"11111111-2222-3333-4444-555555555555\",\"deviceToken\":\"short\"}")]
    [InlineData("{\"schemaVersion\":1,\"installId\":\"11111111-2222-3333-4444-555555555555\",\"surprise\":1}")]
    [InlineData("{\"schemaVersion\":1,\"installId\":\"11111111-2222-3333-4444-555555555555\",\"bindings\":{\"broadcast\":[\"key:nope\"],\"openMic\":[]}}")]
    [InlineData("{\"schemaVersion\":1,\"installId\":\"11111111-2222-3333-4444-555555555555\",\"bindings\":{\"broadcast\":[\"key:VcF8\"],\"openMic\":[\"key:VcF8\"]}}")]
    [InlineData("{\"schemaVersion\":1,\"installId\":\"11111111-2222-3333-4444-555555555555\",\"bindings\":{\"broadcast\":[\"key:VcF8\"],\"allTeams\":[\"key:VcF8\"]}}")]
    public void A_file_that_breaks_a_rule_is_kept_aside_and_defaults_are_used(string json)
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Store.Path, json);

        var load = Assert.IsType<SettingsLoad.Invalid>(Store.Load());

        Assert.NotEqual(Guid.Empty, load.Settings.InstallId);
        Assert.Null(load.Settings.DeviceToken);
        Assert.Equal(json, File.ReadAllText(Path.Combine(_folder, "settings.invalid.json")));
    }

    [Fact]
    public void The_reason_for_an_invalid_file_never_quotes_the_file()
    {
        var secret = WireSecret.Generate();
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Store.Path, $"{{\"deviceToken\": \"{secret}\", \"deviceToken\": 5}}");

        var load = Assert.IsType<SettingsLoad.Invalid>(Store.Load());

        Assert.DoesNotContain(secret, load.Reason);
    }

    [Fact]
    public void A_folder_that_cannot_be_written_reports_failure_instead_of_throwing()
    {
        Directory.CreateDirectory(_folder);
        var blocker = Path.Combine(_folder, "blocker");
        File.WriteAllText(blocker, "a file where a folder should be");

        Assert.False(new SettingsStore(Path.Combine(blocker, "settings.json")).TrySave(AppSettings.CreateDefault()));
    }

    [Fact]
    public void Comments_and_trailing_commas_are_allowed_for_hand_edits()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Store.Path, """
            {
              // set by hand
              "schemaVersion": 1,
              "installId": "11111111-2222-3333-4444-555555555555",
              "serverHost": "gvg.example.com",
            }
            """);

        var load = Assert.IsType<SettingsLoad.Loaded>(Store.Load());

        Assert.Equal("gvg.example.com", load.Settings.ServerHost);
    }
}
