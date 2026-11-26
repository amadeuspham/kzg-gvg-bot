using System.Collections;
using Kzg.Bot.Setup;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Testing;

namespace Kzg.Bot.Tests.Setup;

public sealed class SetupWizardTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-setup-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAccounts _accounts = new();
    private readonly ManualTimeSource _time = new();
    private readonly List<string> _keys = [];

    public SetupWizardTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private DataPaths Paths => new(_folder);

    private SetupWizard Wizard(BotConfig? existing = null, string? host = null) =>
        new(_accounts, Paths, existing, host, _time, _keys.Add, new Hashtable());

    private static string?[] Fields(params string?[] tokens) => [.. tokens, .. Enumerable.Repeat<string?>(null, SetupWizard.MaxBots - tokens.Length)];

    [Fact]
    public void The_key_opens_one_session_once_and_the_spent_key_prints_a_new_one()
    {
        var wizard = Wizard();
        wizard.IssueKey();
        var key = Assert.Single(_keys);

        Assert.Equal(KeyUse.Wrong, wizard.UseKey(Kzg.Protocol.WireSecret.Generate(), out _));
        Assert.Equal(KeyUse.Wrong, wizard.UseKey("not-a-key", out _));
        Assert.Equal(KeyUse.Accepted, wizard.UseKey(key, out var session));
        Assert.True(wizard.IsSession(session));
        Assert.False(wizard.IsSession(Kzg.Protocol.WireSecret.Generate()));
        Assert.False(wizard.IsSession(null));

        Assert.Equal(KeyUse.Spent, wizard.UseKey(key, out _));
        Assert.Equal(2, _keys.Count);
        Assert.True(wizard.IsSession(session));
        Assert.Equal(KeyUse.Accepted, wizard.UseKey(_keys[1], out var second));
        // Only the newest browser is let in.
        Assert.False(wizard.IsSession(session));
        Assert.True(wizard.IsSession(second));
    }

    [Fact]
    public async Task Each_bad_token_is_told_why_and_nothing_moves_on()
    {
        var wizard = Wizard();

        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1), "123", FakeAccounts.Token(9), FakeAccounts.Token(1)), null, default);

        var view = wizard.View();
        Assert.Equal(SetupStep.Tokens, view.Step);
        Assert.Null(view.FieldErrors[0]);
        Assert.Contains("isn't a bot token", view.FieldErrors[1]);
        Assert.Contains("doesn't accept", view.FieldErrors[2]);
        Assert.Contains("again", view.FieldErrors[3]);
        Assert.Empty(view.Bots);
    }

    [Fact]
    public async Task No_token_a_bad_address_and_an_unreachable_Discord_are_problems()
    {
        var wizard = Wizard();

        await wizard.SubmitAsync(Fields(), null, default);
        Assert.Contains("at least one", wizard.View().Problem);

        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1)), "https://not a host/", default);
        Assert.Contains("public address", wizard.View().Problem);

        _accounts.Down = true;
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1)), null, default);
        Assert.Contains("couldn't be reached", wizard.View().FieldErrors[0]);
        Assert.Equal(SetupStep.Tokens, wizard.View().Step);
    }

    [Fact]
    public async Task Good_tokens_go_to_the_invite_step_where_the_servers_are_looked_at_every_few_seconds()
    {
        var wizard = Wizard();
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1), "", FakeAccounts.Token(2)), " gvg.example.com ", default);

        await wizard.LookAsync(default);
        await wizard.LookAsync(default);
        Assert.Equal(2, _accounts.Looks);
        Assert.Empty(wizard.View().Shared);

        _accounts.Join(1);
        _accounts.Join(2);
        _time.Advance(TimeSpan.FromSeconds(3));
        await wizard.LookAsync(default);

        var view = wizard.View();
        Assert.Equal(SetupStep.Invite, view.Step);
        Assert.Equal(["Kzg Relay 1", "Kzg Relay 2"], view.Bots.Select(b => b.Name));
        Assert.True(view.Bots[0].Primary);
        Assert.Equal(FakeAccounts.Guild, Assert.Single(view.Shared).Id);
        Assert.Equal("gvg.example.com", view.PublicHost);
    }

    [Fact]
    public async Task Finish_waits_for_one_shared_server_then_writes_both_files_with_the_tokens_owner_only()
    {
        var wizard = Wizard();
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1), FakeAccounts.Token(2)), "gvg.example.com", default);
        _accounts.Join(1);
        await wizard.LookAsync(default);

        Assert.False(await wizard.FinishAsync(null, default));
        Assert.Contains("same server", wizard.View().Problem);
        Assert.False(File.Exists(Paths.SecretsFile));

        _accounts.Join(2);
        _time.Advance(TimeSpan.FromSeconds(5));
        await wizard.LookAsync(default);
        Assert.True(await wizard.FinishAsync(null, default));

        Assert.Equal(SetupStep.Done, wizard.View().Step);
        var secrets = Assert.IsType<FileLoad<Secrets>.Loaded>(new SecretsStore(Paths.SecretsFile).Load(new Hashtable())).Value;
        Assert.True(secrets.TryGet("relay2", out var two) && two == FakeAccounts.Token(2));
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Paths.SecretsFile));
        }
        var config = Assert.IsType<FileLoad<BotConfig>.Loaded>(new ConfigStore(Paths.ConfigFile).Load(secrets)).Value;
        Assert.Equal(FakeAccounts.Guild, config.GuildId);
        Assert.Equal("gvg.example.com", config.PublicHost);
        Assert.Equal(["relay1", "relay2"], config.Bots.Select(b => b.TokenRef));
        Assert.Equal(new UserId(9001), config.Bots[0].BotUserId);
        Assert.False(config.OAuth.Enabled);
        Assert.Same(wizard.Written, wizard.Written);
        Assert.DoesNotContain(FakeAccounts.Token(1), File.ReadAllText(Paths.ConfigFile));
    }

    [Fact]
    public async Task Each_bot_keeps_the_field_it_was_pasted_in_so_a_gap_names_its_channel()
    {
        var wizard = Wizard();
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1), null, null, FakeAccounts.Token(3)), null, default);
        _accounts.Join(1);
        _accounts.Join(3);
        await wizard.LookAsync(default);

        var view = wizard.View();
        Assert.Equal([1, 4], view.Bots.Select(b => b.Slot));
        Assert.Equal([true, false], view.Bots.Select(b => b.Primary));
        var invite = SetupPages.Render(view);
        Assert.Contains("A1 Broadcaster: Kzg Relay 1 <small>(runs the commands)</small>", invite);
        Assert.Contains("D1 Broadcaster: Kzg &lt;Relay&gt; &amp; 3", invite);
        Assert.Contains(">Invite D1 Broadcaster</a>", invite);

        Assert.True(await wizard.FinishAsync(null, default));
        Assert.Equal(["relay1", "relay4"], wizard.Written!.Bots.Select(b => b.TokenRef));
        var secrets = Assert.IsType<FileLoad<Secrets>.Loaded>(new SecretsStore(Paths.SecretsFile).Load(new Hashtable())).Value;
        Assert.True(secrets.TryGet("relay4", out var four) && four == FakeAccounts.Token(3));
    }

    [Fact]
    public void The_token_fields_are_labelled_by_the_channel_each_bot_fills()
    {
        var page = SetupPages.Render(Wizard().View());

        Assert.Contains("<label for=\"token1\">A1 Broadcaster (this one also answers the /gvg and /connect commands)</label>", page);
        Assert.Contains("<label for=\"token3\">A3 Broadcaster</label>", page);
        Assert.Contains("<label for=\"token4\">D1 Broadcaster</label>", page);
        Assert.Contains("<label for=\"token6\">D3 Broadcaster</label>", page);
        Assert.DoesNotContain("Relay bot", page);
        Assert.Contains("name it <i>Broadcast Relay</i>", page);
    }

    [Fact]
    public async Task With_several_shared_servers_the_admin_picks_one()
    {
        var wizard = Wizard();
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1)), null, default);
        _accounts.Join(1);
        _accounts.Join(1, FakeAccounts.OtherGuild);
        await wizard.LookAsync(default);

        Assert.False(await wizard.FinishAsync(null, default));
        Assert.Contains("Pick the server", wizard.View().Problem);
        Assert.False(await wizard.FinishAsync(424242, default));
        Assert.True(await wizard.FinishAsync(FakeAccounts.OtherGuild, default));
        Assert.Equal(FakeAccounts.OtherGuild, wizard.Written!.GuildId);
    }

    [Fact]
    public async Task Setup_again_keeps_the_settings_and_other_secrets_and_takes_old_bot_tokens_off_the_disk()
    {
        var store = new SecretsStore(Paths.SecretsFile);
        Assert.IsType<FileSave.Saved>(store.Save(new Dictionary<string, string> { ["bot1"] = "old-token-one", ["oauthClientSecret"] = "kept-secret", ["tester1"] = "kept-too" }));
        var existing = new BotConfig
        {
            GuildId = FakeAccounts.Guild,
            AdminRoleId = 77,
            OAuth = new OAuthConfig { Enabled = true, ClientId = 1234 },
            Bots = [new BotEntry { TokenRef = "bot1", BotUserId = new UserId(9001), ChannelId = new ChannelId(101), Primary = true }],
        };
        var wizard = Wizard(existing);
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1), FakeAccounts.Token(3)), null, default);
        _accounts.Join(1);
        _accounts.Join(3);
        await wizard.LookAsync(default);

        Assert.True(await wizard.FinishAsync(null, default));

        var secrets = Assert.IsType<FileLoad<Secrets>.Loaded>(store.Load(new Hashtable())).Value;
        Assert.Equal(["oauthClientSecret", "relay1", "relay2", "tester1"], secrets.Names.Order(StringComparer.Ordinal));
        Assert.Equal(77UL, wizard.Written!.AdminRoleId);
        Assert.True(wizard.Written.OAuth.Enabled);
        Assert.Equal(new ChannelId(101), wizard.Written.Bots[0].ChannelId);
    }

    [Fact]
    public async Task Sign_in_left_on_without_its_secret_is_turned_off_rather_than_blocking_setup()
    {
        var wizard = Wizard(new BotConfig { OAuth = new OAuthConfig { Enabled = true, ClientId = 1234 } });
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1)), null, default);
        _accounts.Join(1);
        await wizard.LookAsync(default);

        Assert.True(await wizard.FinishAsync(null, default));
        Assert.False(wizard.Written!.OAuth.Enabled);
    }

    [Fact]
    public async Task Back_forgets_the_tokens_and_release_completes_the_wait()
    {
        var wizard = Wizard();
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1)), null, default);

        wizard.Back();
        Assert.Equal(SetupStep.Tokens, wizard.View().Step);
        Assert.Empty(wizard.View().Bots);

        Assert.False(wizard.Completed.IsCompleted);
        wizard.Release(new BotConfig());
        Assert.True(wizard.Completed.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task The_pages_show_names_encoded_and_never_a_token()
    {
        var wizard = Wizard();
        await wizard.SubmitAsync(Fields(FakeAccounts.Token(1), FakeAccounts.Token(3)), null, default);
        var tokens = SetupPages.Render(wizard.View());
        _accounts.Join(1);
        await wizard.LookAsync(default);

        var invite = SetupPages.Render(wizard.View());

        Assert.Contains("Kzg &lt;Relay&gt; &amp; 3", invite);
        Assert.DoesNotContain("<Relay>", invite);
        // Change Nickname (67108864) on top of View Channel, Connect, Speak and Move Members, and Send Messages for the first.
        Assert.Contains("client_id=1001&amp;scope=bot%20applications.commands&amp;permissions=87034880", invite);
        Assert.Contains("client_id=1003&amp;scope=bot&amp;permissions=87032832&amp;guild_id=5000", invite);
        Assert.Contains("http-equiv=\"refresh\"", invite);
        foreach (var page in new[] { tokens, invite, SetupPages.Render(wizard.View() with { Step = SetupStep.Tokens }) })
        {
            Assert.DoesNotContain(FakeAccounts.Token(1), page);
            Assert.DoesNotContain(FakeAccounts.Token(3), page);
        }
        Assert.Contains("/gvg setup", SetupPages.Render(wizard.View() with { Step = SetupStep.Done }));
    }
}
