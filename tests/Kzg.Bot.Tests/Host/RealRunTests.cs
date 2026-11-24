using System.Net;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Core.Testing;
using Kzg.Discord;
using Kzg.Discord.Commands;
using Kzg.Protocol;
using NetCord;
using static Kzg.Bot.Tests.Engine.EngineRig;

namespace Kzg.Bot.Tests.Hosting;

/// <summary>
/// The real bot's start-up (<c>Program</c> without <c>--fake</c>) over a stand-in for Discord: the data
/// folder is read, the engine is wired to the adapter, the slash commands act on the config on disk and the
/// bots in voice, and every bot is signed out at the end.
/// </summary>
public sealed class RealRunTests : IAsyncDisposable
{
    private static readonly string[] BotNames = ["relay1", "relay2", "relay3", "relay4"];
    private const ulong Guild = 5000;
    private static readonly UserId Admin = new(7), Member = new(8);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-real-run-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeDiscordRuntime _discord = new(new ManualTimeSource());
    private readonly CancellationTokenSource _stop = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private Task<int>? _run;
    private BotApp? _bot;

    public RealRunTests() => Directory.CreateDirectory(_folder);

    private static string Token(string name) => $"fake-{name}-token-{new string('z', 30)}";

    private string ConfigFile => Path.Combine(_folder, "config.json");

    private BotConfig OnDisk() => Assert.IsType<FileLoad<BotConfig>.Loaded>(
        new ConfigStore(ConfigFile).Load(Secrets.Combine(BotNames.ToDictionary(n => n, Token), new Dictionary<string, string>()))).Value;

    private async Task StartAsync(Func<BotConfig, BotConfig>? configure = null)
    {
        var config = new BotConfig
        {
            GuildId = Guild,
            PublicHost = "gvg.example.com",
            Bots =
            [
                new BotEntry { TokenRef = "relay1", Primary = true, ChannelId = A1 },
                new BotEntry { TokenRef = "relay2", ChannelId = A2 },
                new BotEntry { TokenRef = "relay3" },
                new BotEntry { TokenRef = "relay4" },
            ],
            Groups = new GroupsConfig { Attack = new GroupConfig { Channels = [A1, A2] } },
        };
        config = (configure ?? (c => c))(config);
        await File.WriteAllTextAsync(ConfigFile, ConfigStore.Serialize(config));
        await File.WriteAllTextAsync(
            Path.Combine(_folder, "secrets.json"),
            "{" + string.Join(",", BotNames.Select(n => $"\"{n}\": \"{Token(n)}\"")) + "}");
        _discord.Names.Reset([(101, "Attack 1"), (102, "Attack 2"), (103, "Attack 3"), (201, "Defense 1")]);
        var listening = new TaskCompletionSource<BotApp>();
        _run = Program.RunAsync(
            ["--data-dir", _folder, "--listen", "127.0.0.1:0"], _output, _error, _stop.Token, listening.SetResult, (_, _, _) => _discord);
        _bot = await listening.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private async Task<int> StopAsync()
    {
        await _stop.CancelAsync();
        return await _run!.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private GvgCommands Commands => new(_discord.Control ?? throw new InvalidOperationException("no control attached"));

    private static Caller Manager => new(Admin, (ulong)Permissions.ManageGuild, [], false);

    private Task<CommandReply> Gvg(string[] path, params (string Name, string Value)[] options) =>
        Commands.HandleAsync(new CommandInvocation("gvg", path, options.ToDictionary(o => o.Name, o => o.Value), Manager, Guild), default);

    private static void WaitUntil(Func<bool> condition, string what) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "timed out waiting for " + what);

    public async ValueTask DisposeAsync()
    {
        if (_run is { IsCompleted: false })
        {
            await StopAsync();
        }
        _stop.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Left for the temp cleaner.
        }
    }

    [Fact]
    public async Task The_discord_side_is_started_after_the_bot_listens_and_the_bots_with_a_channel_are_put_in_it()
    {
        await StartAsync();

        Assert.True(_discord.Started);
        Assert.NotNull(_discord.Control);
        Assert.Equal([A1, A2], _discord.Current.Select(l => l.Channel).OrderBy(c => c.V));
        WaitUntil(() => _bot!.Engine.ReadyChannels.Count == 2, "the engine to see both links");
        var exit = await StopAsync();
        Assert.Equal(0, exit);
        Assert.Equal("", _error.ToString());
    }

    [Fact]
    public async Task Every_bot_is_signed_out_when_the_bot_stops()
    {
        await StartAsync();

        await StopAsync();

        Assert.True(_discord.Disposed);
    }

    [Fact]
    public async Task The_status_the_apps_get_names_channels_from_Discord()
    {
        await StartAsync();

        var names = new Kzg.Bot.Discord.DiscordChannelNames(_discord.Names);

        Assert.Equal("Attack 2", names.NameOf(A2));
        Assert.Equal("Channel 5", names.NameOf(new ChannelId(5)));
    }

    [Fact]
    public async Task Bind_changes_the_file_and_a_bot_joins_the_channel_the_engine_then_sends_to()
    {
        await StartAsync();

        var reply = await Gvg(["bind"], ("group", "attack"), ("channel", "103"));

        Assert.Contains("A3 Broadcaster will join it", reply.Text);
        Assert.Contains(A3, OnDisk().Groups.Attack.Channels);
        Assert.Equal(A3, OnDisk().Bots.Single(b => b.TokenRef == "relay3").ChannelId);
        WaitUntil(() => _bot!.Engine.ReadyChannels.Contains(A3), "the new link to be ready");
        Assert.Contains(A3, _bot!.Config.Groups.Attack.Channels);
    }

    [Fact]
    public async Task Unbind_removes_the_link_so_the_bot_leaves_and_the_engine_stops_using_it()
    {
        await StartAsync();
        WaitUntil(() => _bot!.Engine.ReadyChannels.Contains(A2), "relay2 to be ready");

        var reply = await Gvg(["unbind"], ("channel", "102"));

        Assert.Contains("A2 Broadcaster left it", reply.Text);
        Assert.DoesNotContain(A2, _discord.Current.Select(l => l.Channel));
        WaitUntil(() => !_bot!.Engine.ReadyChannels.Contains(A2), "the engine to drop the link");
        Assert.DoesNotContain(A2, OnDisk().Groups.Attack.Channels);
    }

    [Fact]
    public async Task Assigning_a_bot_elsewhere_moves_its_link()
    {
        await StartAsync();

        await Gvg(["bot", "assign"], ("bot", "relay2"), ("channel", "103"));

        Assert.Equal([A1, A3], _discord.Current.Select(l => l.Channel).OrderBy(c => c.V));
        WaitUntil(() => _bot!.Engine.ReadyChannels.SetEquals([A1, A3]), "the engine to follow");
    }

    [Fact]
    public async Task Stop_takes_every_bot_out_of_voice_and_start_puts_them_back()
    {
        await StartAsync();
        WaitUntil(() => _bot!.Engine.ReadyChannels.Count == 2, "both links");

        await Gvg(["stop"]);
        WaitUntil(() => _bot!.Engine.ReadyChannels.Count == 0, "the links to go");
        Assert.Empty(_discord.Current);
        // The config on disk still says where they belong.
        Assert.Equal(A2, OnDisk().Bots.Single(b => b.TokenRef == "relay2").ChannelId);

        await Gvg(["start"]);
        WaitUntil(() => _bot!.Engine.ReadyChannels.Count == 2, "the links to return");
    }

    [Fact]
    public async Task A_command_that_would_break_a_rule_changes_neither_the_file_nor_the_bot()
    {
        await StartAsync();
        var before = await File.ReadAllTextAsync(ConfigFile);
        var applies = _discord.Applies;

        var reply = await Gvg(["config", "set"], ("key", "prerollMs"), ("value", "70"));

        Assert.Contains("break the config", reply.Text);
        Assert.Equal(before, await File.ReadAllTextAsync(ConfigFile));
        Assert.Equal(300, _bot!.Config.PrerollMs);
        Assert.Equal(applies, _discord.Applies);
    }

    [Fact]
    public async Task A_setting_changed_by_a_command_is_written_and_applied_to_the_running_bot()
    {
        await StartAsync();

        await Gvg(["config", "set"], ("key", "prerollMs"), ("value", "200"));

        Assert.Equal(200, OnDisk().PrerollMs);
        Assert.Equal(200, _bot!.Config.PrerollMs);
    }

    [Fact]
    public async Task What_the_bots_learned_while_signing_in_is_saved()
    {
        _discord.ResolveGuild = 4321;
        _discord.LearnBots["relay2"] = 5_000_000_002;
        await StartAsync(c => c with { GuildId = null });

        WaitUntil(() => OnDisk().GuildId == 4321, "the guild to be saved");
        WaitUntil(() => OnDisk().Bots.Single(b => b.TokenRef == "relay2").BotUserId == new UserId(5_000_000_002), "the bot's ID to be saved");
        Assert.Null(OnDisk().Bots.Single(b => b.TokenRef == "relay1").BotUserId);
    }

    [Fact]
    public async Task A_guild_the_config_names_is_not_replaced_by_the_one_the_bot_found()
    {
        _discord.ResolveGuild = 4321;
        await StartAsync();

        await StopAsync();

        Assert.Equal(Guild, OnDisk().GuildId);
    }

    [Fact]
    public async Task Connect_issues_a_code_for_the_public_host_that_the_bot_then_accepts_once()
    {
        await StartAsync();

        var reply = await Commands.HandleAsync(
            new CommandInvocation("connect", [], new Dictionary<string, string>(), new Caller(Member, 0, [], false), Guild), default);

        var text = reply.Text[reply.Text.IndexOf("KZG1.", StringComparison.Ordinal)..].Trim('`', ' ', '\n');
        Assert.True(ConnectCode.TryParse(text, out var code));
        Assert.Equal("gvg.example.com", code.Host);
        Assert.IsType<CodeRedemption.Redeemed>(_bot!.Codes.Redeem(code));
        Assert.IsType<CodeRedemption.Used>(_bot.Codes.Redeem(code));
    }

    [Fact]
    public async Task Revoke_forgets_the_players_device_tokens()
    {
        await StartAsync();
        _bot!.Devices.Issue(Member, Guid.NewGuid());
        _bot.Devices.Issue(Member, Guid.NewGuid());

        var reply = await Gvg(["revoke"], ("user", "8"));

        Assert.Contains("Revoked 2", reply.Text);
        Assert.Equal(0, _bot.Devices.Count);
    }

    [Fact]
    public async Task Status_reports_what_the_bot_and_the_adapter_know()
    {
        await StartAsync();
        _discord.Bots = [new RelayBotState("relay1", true, A1, BotState.Ready, 0, 0), new RelayBotState("relay2", false, A2, BotState.Joining, 0, 0, NicknameRefused: true)];

        var text = (await Gvg(["status"])).Text;

        Assert.Contains("Relay: running", text);
        Assert.Contains("Attack 1: A1 Broadcaster ready\n", text);
        Assert.Contains("Attack 2: A2 Broadcaster not in yet (it can't set its nickname", text);
        Assert.Contains("Free bots: relay3 (spare), relay4 (spare)", text);
        Assert.DoesNotContain("caller", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Config_show_never_prints_a_token()
    {
        await StartAsync();

        var text = (await Gvg(["config", "show"])).Text;

        Assert.DoesNotContain("fake-relay", text);
        Assert.DoesNotContain("zzzzzzzz", text);
        Assert.DoesNotContain("fake-relay", _output.ToString());
    }

    [Fact]
    public async Task A_Discord_that_will_not_start_stops_the_bot_with_its_reason_and_signs_everything_out()
    {
        _discord.FailStart = new DiscordStartException("libdave 1.3.0 or newer is loaded");

        var exit = await Program.RunAsync(
            ["--data-dir", Seed(), "--listen", "127.0.0.1:0"], _output, _error, CancellationToken.None, discord: (_, _, _) => _discord);

        Assert.Equal(1, exit);
        Assert.Contains("libdave 1.3.0", _error.ToString());
        Assert.True(_discord.Disposed);
    }

    [Fact]
    public async Task Stopping_while_Discord_is_still_starting_ends_cleanly()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var exit = await Program.RunAsync(["--data-dir", Seed(), "--listen", "127.0.0.1:0"], _output, _error, cancelled.Token, discord: (_, _, _) => _discord);

        Assert.Equal(0, exit);
        Assert.True(_discord.Disposed);
    }

    private string Seed()
    {
        File.WriteAllText(ConfigFile, ConfigStore.Serialize(new BotConfig
        {
            GuildId = Guild,
            Bots = [new BotEntry { TokenRef = "relay1", Primary = true }],
        }));
        File.WriteAllText(Path.Combine(_folder, "secrets.json"), $"{{\"relay1\": \"{Token("relay1")}\"}}");
        return _folder;
    }
}
