using System.Collections;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;

namespace Kzg.Core.Tests.Config;

public sealed class ConfigStoreTests : IDisposable
{
    private static readonly Secrets Tokens = Secrets.Combine(
        new Dictionary<string, string> { ["relay1"] = "t1", ["relay2"] = "t2", ["oauthClientSecret"] = "s" }, new Hashtable());

    private readonly TempFolder _folder = new();
    private readonly ConfigStore _store;

    public ConfigStoreTests() => _store = new ConfigStore(_folder.File("config.json"));

    public void Dispose() => _folder.Dispose();

    /// <summary>A config that keeps every rule, as a bound two-group setup has it.</summary>
    internal static BotConfig Valid() => new()
    {
        GuildId = 111111111111111111,
        AdminRoleId = 222222222222222222,
        PublicHost = "gvg.example.com",
        OAuth = new OAuthConfig { Enabled = true, ClientId = 333333333333333333 },
        Groups = new GroupsConfig
        {
            Attack = new GroupConfig { Channels = [new ChannelId(11), new ChannelId(12)] },
            Defense = new GroupConfig { Channels = [new ChannelId(21)] },
        },
        Bots =
        [
            new BotEntry { TokenRef = "relay1", BotUserId = new UserId(801), ChannelId = new ChannelId(11), Primary = true },
            new BotEntry { TokenRef = "relay2", ChannelId = new ChannelId(12) },
        ],
    };

    [Fact]
    public void A_saved_config_loads_back_the_same()
    {
        var config = Valid() with { PrerollMs = 200 };

        Assert.IsType<FileSave.Saved>(_store.Save(config, Tokens));
        var loaded = Assert.IsType<FileLoad<BotConfig>.Loaded>(new ConfigStore(_store.FilePath).Load(Tokens)).Value;

        Assert.Equal(ConfigStore.Serialize(config), ConfigStore.Serialize(loaded));
        Assert.Equal(config.Groups.Attack.Channels, loaded.Groups.Attack.Channels);
        Assert.Equal(TimeSpan.FromMilliseconds(200), loaded.Preroll);
        Assert.Equal(TimeSpan.FromMinutes(2), loaded.MaxHold);
    }

    [Fact]
    public void The_file_spells_ids_as_strings_and_unset_ones_as_null()
    {
        var text = ConfigStore.Serialize(Valid() with { AdminRoleId = null });

        Assert.Contains("\"guildId\": \"111111111111111111\"", text, StringComparison.Ordinal);
        Assert.Contains("\"adminRoleId\": null", text, StringComparison.Ordinal);
        Assert.Contains("\"oauth\": {", text, StringComparison.Ordinal);
        Assert.Contains("\"channels\": [\n        \"11\",", text.ReplaceLineEndings("\n"), StringComparison.Ordinal);
        Assert.DoesNotContain("shotCallerUserId", text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_hand_written_file_may_leave_settings_out_and_have_comments_and_trailing_commas()
    {
        File.WriteAllText(_store.FilePath, """
            // Only what differs from the defaults.
            {
              "schemaVersion": 1,
              "groups": { "attack": { "channels": ["11"], }, },
              "bots": [ { "tokenRef": "relay1", "channelId": "11", "primary": true } ],
            }
            """);

        var config = Assert.IsType<FileLoad<BotConfig>.Loaded>(_store.Load(Tokens)).Value;

        Assert.Equal([new ChannelId(11)], config.Groups.Attack.Channels);
        Assert.Empty(config.Groups.Defense.Channels);
        Assert.Null(config.GuildId);
        Assert.Null(config.PublicHost);
        Assert.Equal("127.0.0.1:7878", config.Listen);
        Assert.Equal(300, config.PrerollMs);
        Assert.Equal(120_000, config.MaxHoldMs);
        Assert.Equal(90, config.DeviceTokenDays);
        Assert.Equal(32, config.JungleVolume);
        Assert.False(config.OAuth.Enabled);
    }

    [Fact]
    public void A_config_from_when_teams_had_shot_callers_loads_with_them_dropped_and_saves_without_them()
    {
        File.WriteAllText(_store.FilePath, """
            {
              "schemaVersion": 1,
              "groups": {
                "attack": { "channels": ["11"], "shotCallerUserId": "900" },
                "defense": { "channels": ["21"], "shotCallerUserId": null },
              },
              "bots": [
                { "tokenRef": "relay1", "channelId": "11", "primary": true },
                { "tokenRef": "relay2", "channelId": "21" },
              ],
            }
            """);

        var config = Assert.IsType<FileLoad<BotConfig>.Loaded>(_store.Load(Tokens)).Value;

        Assert.Equal([new ChannelId(11)], config.Groups.Attack.Channels);
        Assert.Equal([new ChannelId(21)], config.Groups.Defense.Channels);
        Assert.IsType<FileSave.Saved>(_store.Save(config, Tokens));
        Assert.DoesNotContain("shotCallerUserId", File.ReadAllText(_store.FilePath), StringComparison.Ordinal);
        Assert.IsType<FileLoad<BotConfig>.Loaded>(_store.Load(Tokens));
    }

    [Fact]
    public void There_is_no_config_until_one_is_saved()
    {
        Assert.IsType<FileLoad<BotConfig>.Missing>(_store.Load(Tokens));
    }

    [Fact]
    public void The_defaults_name_one_primary_bot_whose_token_must_be_filled_in()
    {
        var errors = ConfigValidation.Validate(new BotConfig(), Secrets.Empty);

        Assert.Equal("bots[0].tokenRef", Assert.Single(errors).Setting);
        Assert.Empty(ConfigValidation.Validate(new BotConfig(), Secrets.Combine(new Dictionary<string, string> { ["bot1"] = "t" }, new Hashtable())));
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 1, "prerollMs": "300" }""", 1, "prerollMs")]
    [InlineData("""{ "schemaVersion": 1, "guildId": 111111111111111111 }""", 1, "guildId")]
    [InlineData("""{ "schemaVersion": 1, "guildId": "0111" }""", 1, "guildId")]
    [InlineData("""{ "schemaVersion": 1, "guildId": "" }""", 1, "guildId")]
    [InlineData("{ \"schemaVersion\": 1,\n  \"groups\": { \"attack\": { \"channels\": [\"11\", \"x\"] } } }", 2, "groups.attack.channels[1]")]
    [InlineData("{ \"schemaVersion\": 1,\n  \"prerolMs\": 300 }", 2, "prerolMs")]
    [InlineData("{ \"schemaVersion\": 1,\n \"listen\": \"a\",\n \"listen\": \"b\" }", 3, "listen")]
    [InlineData("""{ "schemaVersion": 1, "keyword": { "enabled": false } }""", 1, "keyword")]
    [InlineData("""{ "schemaVersion": 1, "bots": [ { "primary": true } ] }""", 1, "bots[0]")]
    [InlineData("""{ "prerollMs": 300 }""", 1, null)]
    [InlineData("[]", 1, null)]
    [InlineData("null", 1, null)]
    [InlineData("{ \"schemaVersion\": 1 } {}", 1, null)]
    public void A_misspelt_repeated_or_wrongly_typed_setting_is_named_with_its_line(string text, int line, string? setting)
    {
        var error = Assert.Single(Assert.IsType<FileLoad<BotConfig>.Broken>(ConfigStore.Parse(text, Tokens)).Errors);

        Assert.Equal(line, error.Line);
        Assert.Equal(setting, error.Setting);
    }

    public static TheoryData<string, Func<BotConfig, BotConfig>> BrokenRules => new()
    {
        { "schemaVersion", c => c with { SchemaVersion = 2 } },
        { "publicHost", c => c with { PublicHost = "https://gvg.example.com" } },
        { "publicHost", c => c with { PublicHost = "" } },
        { "listen", c => c with { Listen = "127.0.0.1" } },
        { "listen", c => c with { Listen = "localhost:7878" } },
        { "listen", c => c with { Listen = "::1:7878" } },
        { "listen", c => c with { Listen = "127.0.0.1:0" } },
        { "prerollMs", c => c with { PrerollMs = 310 } },
        { "prerollMs", c => c with { PrerollMs = 1020 } },
        { "prerollMs", c => c with { PrerollMs = -20 } },
        { "maxHoldMs", c => c with { MaxHoldMs = 999 } },
        { "maxHoldMs", c => c with { MaxHoldMs = 600_001 } },
        { "deviceTokenDays", c => c with { DeviceTokenDays = 0 } },
        { "jungleVolume", c => c with { JungleVolume = 0 } },
        { "jungleVolume", c => c with { JungleVolume = 101 } },
        { "oauth.clientId", c => c with { OAuth = new OAuthConfig { Enabled = true } } },
        {
            "groups.defense.channels[1]",
            c => c with { Groups = c.Groups.With(Group.Defense, new GroupConfig { Channels = [new ChannelId(21), new ChannelId(11)] }) }
        },
        {
            "groups.attack.channels[2]",
            c => c with { Groups = c.Groups.With(Group.Attack, c.Groups.Attack with { Channels = [new ChannelId(11), new ChannelId(12), new ChannelId(11)] }) }
        },
        { "bots", c => c with { Bots = [c.Bots[0], c.Bots[1] with { Primary = true }] } },
        { "bots", c => c with { Bots = [c.Bots[0] with { Primary = false }, c.Bots[1]] } },
        { "bots", c => c with { Bots = [] } },
        { "bots[1]", c => c with { Bots = [c.Bots[0], null!] } },
        { "bots[1].tokenRef", c => c with { Bots = [c.Bots[0], c.Bots[1] with { TokenRef = "relay1" }] } },
        { "bots[1].tokenRef", c => c with { Bots = [c.Bots[0], c.Bots[1] with { TokenRef = "relay3" }] } },
        { "bots[1].tokenRef", c => c with { Bots = [c.Bots[0], c.Bots[1] with { TokenRef = "relay-2" }] } },
        { "bots[1].botUserId", c => c with { Bots = [c.Bots[0], c.Bots[1] with { BotUserId = new UserId(801) }] } },
        { "bots[1].channelId", c => c with { Bots = [c.Bots[0], c.Bots[1] with { ChannelId = new ChannelId(11) }] } },
    };

    [Theory]
    [MemberData(nameof(BrokenRules))]
    public void A_config_that_breaks_a_rule_is_named_and_never_saved(string setting, Func<BotConfig, BotConfig> breakIt)
    {
        Assert.Empty(ConfigValidation.Validate(Valid(), Tokens));
        Assert.IsType<FileSave.Saved>(_store.Save(Valid(), Tokens));
        var before = File.ReadAllText(_store.FilePath);
        var broken = breakIt(Valid());

        var error = Assert.Single(ConfigValidation.Validate(broken, Tokens));
        var refused = Assert.IsType<FileSave.Invalid>(_store.Save(broken, Tokens));

        Assert.Equal(setting, error.Setting);
        Assert.Equal(error, Assert.Single(refused.Errors));
        Assert.Equal(before, File.ReadAllText(_store.FilePath));
    }

    [Fact]
    public void A_hand_edit_that_breaks_a_rule_is_refused_on_loading()
    {
        File.WriteAllText(_store.FilePath, ConfigStore.Serialize(Valid() with { PrerollMs = 7 }));

        var error = Assert.Single(Assert.IsType<FileLoad<BotConfig>.Broken>(_store.Load(Tokens)).Errors);

        Assert.Equal(new ConfigError(null, "prerollMs", "0 to 1000, in whole 20 ms frames"), error);
    }

    [Fact]
    public void Discord_sign_in_needs_the_client_secret()
    {
        var withoutSecret = Secrets.Combine(new Dictionary<string, string> { ["relay1"] = "t1", ["relay2"] = "t2" }, new Hashtable());

        Assert.Equal("oauth.enabled", Assert.Single(ConfigValidation.Validate(Valid(), withoutSecret)).Setting);
        Assert.Empty(ConfigValidation.Validate(Valid() with { OAuth = new OAuthConfig() }, withoutSecret));
    }

    [Theory]
    [InlineData("0.0.0.0:7878")]
    [InlineData("[::1]:7878")]
    [InlineData("192.168.1.5:65535")]
    public void Kestrel_may_listen_on_any_ip_and_port(string listen)
    {
        Assert.Empty(ConfigValidation.Validate(Valid() with { Listen = listen }, Tokens));
    }

    [Fact]
    public void Every_error_is_reported_at_once()
    {
        var errors = ConfigValidation.Validate(Valid() with { PrerollMs = 5, MaxHoldMs = 5, Bots = [] }, Tokens);

        Assert.Equal(["prerollMs", "maxHoldMs", "bots"], errors.Select(e => e.Setting));
    }

    [Fact]
    public void A_save_replaces_the_file_whole_and_leaves_nothing_beside_it()
    {
        var store = new ConfigStore(Path.Combine(_folder.Path, "new", "config.json"));
        Assert.IsType<FileSave.Saved>(store.Save(Valid(), Tokens));
        Assert.IsType<FileSave.Saved>(store.Save(Valid() with { PrerollMs = 100 }, Tokens));

        Assert.Equal(["config.json"], Directory.GetFiles(Path.GetDirectoryName(store.FilePath)!).Select(Path.GetFileName));
        Assert.Equal(100, Assert.IsType<FileLoad<BotConfig>.Loaded>(store.Load(Tokens)).Value.PrerollMs);
    }

    [Fact]
    public void A_save_the_disk_refuses_changes_nothing()
    {
        // A folder where the file should be: the move over it fails, as a locked file would.
        Directory.CreateDirectory(_store.FilePath);

        Assert.IsType<FileSave.Unwritable>(_store.Save(Valid(), Tokens));
        Assert.Empty(Directory.GetFiles(_folder.Path));
        Assert.IsType<FileLoad<BotConfig>.Unreadable>(_store.Load(Tokens));
    }

    [Fact]
    public void A_huge_or_non_utf8_file_is_refused_unread()
    {
        File.WriteAllText(_store.FilePath, new string(' ', (1 << 20) + 1));
        Assert.IsType<FileLoad<BotConfig>.Broken>(_store.Load(Tokens));

        File.WriteAllBytes(_store.FilePath, [0x7B, 0xFF, 0x7D]);
        Assert.IsType<FileLoad<BotConfig>.Unreadable>(_store.Load(Tokens));
    }

    [Fact]
    public void An_error_reads_as_where_then_what()
    {
        Assert.Equal("line 3, bots[0]: bad", new ConfigError(3, "bots[0]", "bad").ToString());
        Assert.Equal("line 3: bad", new ConfigError(3, null, "bad").ToString());
        Assert.Equal("bots: bad", new ConfigError(null, "bots", "bad").ToString());
        Assert.Equal("bad", new ConfigError(null, null, "bad").ToString());
    }

    [Fact]
    public void Each_group_is_read_and_replaced_by_name()
    {
        var groups = new GroupsConfig().With(Group.Defense, new GroupConfig { Channels = [new ChannelId(5)] });

        Assert.Equal([new ChannelId(5)], groups[Group.Defense].Channels);
        Assert.Empty(groups[Group.Attack].Channels);
        Assert.Throws<ArgumentOutOfRangeException>(() => groups[(Group)9]);
        Assert.Throws<ArgumentOutOfRangeException>(() => groups.With((Group)9, new GroupConfig()));
    }

    [Fact]
    public void The_data_folder_is_the_one_given_or_kzg_in_app_data()
    {
        var given = DataPaths.Resolve(_folder.Path);
        Assert.Equal(Path.Combine(_folder.Path, "config.json"), given.ConfigFile);
        Assert.Equal(Path.Combine(_folder.Path, "secrets.json"), given.SecretsFile);
        Assert.Equal(Path.Combine(_folder.Path, "devices.json"), given.DevicesFile);

        Assert.Equal("Kzg", Path.GetFileName(DataPaths.Resolve(null).Folder));
        Assert.True(Path.IsPathRooted(DataPaths.Resolve(" ").Folder));
    }

    [Fact]
    public void The_alerts_channel_round_trips_as_a_string_id()
    {
        var config = Valid() with { AlertsChannelId = new ChannelId(444444444444444444) };

        Assert.IsType<FileSave.Saved>(_store.Save(config, Tokens));

        Assert.Contains("\"alertsChannelId\": \"444444444444444444\"", File.ReadAllText(_store.FilePath), StringComparison.Ordinal);
        Assert.Equal(new ChannelId(444444444444444444), Assert.IsType<FileLoad<BotConfig>.Loaded>(_store.Load(Tokens)).Value.AlertsChannelId);
    }

    [Fact]
    public void Without_checking_secrets_a_config_whose_tokens_are_gone_still_loads_but_other_rules_hold()
    {
        Assert.IsType<FileSave.Saved>(_store.Save(Valid(), Tokens));

        Assert.IsType<FileLoad<BotConfig>.Broken>(_store.Load(Secrets.Empty));
        Assert.IsType<FileLoad<BotConfig>.Loaded>(_store.Load(Secrets.Empty, checkSecrets: false));
        Assert.Contains(ConfigValidation.Validate(Valid() with { PrerollMs = 7 }, Secrets.Empty, checkSecrets: false), e => e.Setting == "prerollMs");
    }

    [Fact]
    public void The_packaged_example_config_is_a_valid_config_but_for_its_tokens()
    {
        var folder = new DirectoryInfo(AppContext.BaseDirectory);
        while (folder is not null && !File.Exists(Path.Combine(folder.FullName, "Kzg.slnx")))
        {
            folder = folder.Parent;
        }
        var example = Path.Combine(folder!.FullName, "packaging", "bot", "config.example.json");

        var loaded = Assert.IsType<FileLoad<BotConfig>.Loaded>(new ConfigStore(example).Load(Secrets.Empty, checkSecrets: false)).Value;

        Assert.Null(loaded.AlertsChannelId);
        Assert.False(loaded.OAuth.Enabled);
    }
}
