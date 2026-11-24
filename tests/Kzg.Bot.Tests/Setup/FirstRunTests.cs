using System.Collections;
using System.Net;
using Kzg.Bot.Setup;
using Kzg.Bot.Tests.Hosting;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Testing;

namespace Kzg.Bot.Tests.Setup;

/// <summary>
/// The real start-up with an empty data folder: the setup page comes first, and once it is done the bot runs
/// on the files it wrote, over a stand-in for Discord. No step needs a hand-edited file.
/// </summary>
public sealed class FirstRunTests : IAsyncDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-first-run-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAccounts _accounts = new();
    private readonly FakeDiscordRuntime _discord = new(new ManualTimeSource());
    private readonly CancellationTokenSource _stop = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        _stop.Dispose();
        _http.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private string Output
    {
        get
        {
            lock (_output)
            {
                return _output.ToString();
            }
        }
    }

    private (Task<int> Run, Task<SetupServer> Setup, Task<BotApp> Bot) Start(params string[] extra)
    {
        var setup = new TaskCompletionSource<SetupServer>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bot = new TaskCompletionSource<BotApp>(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = Program.RunAsync(
            ["--data-dir", _folder, "--listen", "127.0.0.1:0", .. extra], TextWriter.Synchronized(_output), _error, _stop.Token, bot.SetResult, (_, _, _) => _discord,
            new SetupHooks { Accounts = _accounts, Started = setup.SetResult, Environment = new Hashtable { [Program.PublicHostVariable] = "gvg.example.com" }, KeepAwake = false });
        return (run, setup.Task, bot.Task);
    }

    private async Task<HttpResponseMessage> Send(HttpMethod method, Uri url, string? cookie, Dictionary<string, string>? form = null)
    {
        using var request = new HttpRequestMessage(method, url) { Content = form is null ? null : new FormUrlEncodedContent(form) };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{SetupServer.CookieName}={cookie}");
        }
        return await _http.SendAsync(request);
    }

    /// <summary>What an admin does in the browser: open the printed link, paste, invite, finish.</summary>
    private async Task CompleteSetupAsync(SetupServer server, params int[] bots)
    {
        var link = Output.Split('\n').Last(l => l.Contains("http://localhost:", StringComparison.Ordinal)).Trim();
        var key = link[(link.IndexOf("key=", StringComparison.Ordinal) + 4)..];
        using var opened = await Send(HttpMethod.Get, new Uri(server.Address, "/setup?key=" + key), null);
        var cookie = opened.Headers.GetValues("Set-Cookie").Single().Split(';')[0].Split('=', 2)[1];
        var tokens = bots.Select((b, i) => ($"token{i + 1}", FakeAccounts.Token(b))).ToDictionary(p => p.Item1, p => p.Item2);
        (await Send(HttpMethod.Post, new Uri(server.Address, "/setup/tokens"), cookie, tokens)).Dispose();
        foreach (var bot in bots)
        {
            _accounts.Join(bot);
        }
        await Task.Delay(TimeSpan.FromSeconds(3.1));
        using var page = await Send(HttpMethod.Get, new Uri(server.Address, "/setup"), cookie);
        Assert.Contains("Finish setup", await page.Content.ReadAsStringAsync());
        using var done = await Send(HttpMethod.Post, new Uri(server.Address, "/setup/finish"), cookie, new() { ["server"] = "5000" });
        Assert.Equal(HttpStatusCode.OK, done.StatusCode);
    }

    [Fact]
    public async Task An_empty_data_folder_opens_the_setup_page_and_then_the_bot_runs_on_what_it_wrote()
    {
        var (run, setup, started) = Start();
        var server = await setup.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Contains("== Kzg bot setup ==", Output);
        Assert.Contains($"http://localhost:{server.Address.Port}/setup?key=", Output);
        Assert.Contains("https://gvg.example.com/setup?key=", Output);
        Assert.False(_discord.Started);

        await CompleteSetupAsync(server, 1, 2);
        var bot = await started.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.True(_discord.Started);
        Assert.NotNull(_discord.Control);
        Assert.Equal(5000UL, bot.Config.GuildId);
        Assert.Equal("gvg.example.com", bot.Config.PublicHost);
        Assert.Equal(["relay1", "relay2"], bot.Config.Bots.Select(b => b.TokenRef));
        await _stop.CancelAsync();
        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal("", _error.ToString());
        Assert.DoesNotContain(FakeAccounts.Token(1), Output);
        Assert.DoesNotContain(FakeAccounts.Token(2), Output);
        Assert.False(File.Exists(Path.Combine(_folder, RunMarker.FileName)));
    }

    [Fact]
    public async Task Stopping_during_setup_exits_cleanly_and_writes_nothing()
    {
        var (run, setup, _) = Start();
        await setup.WaitAsync(TimeSpan.FromSeconds(20));

        await _stop.CancelAsync();

        Assert.Equal(0, await run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.False(File.Exists(Path.Combine(_folder, "config.json")));
        Assert.False(_discord.Started);
    }

    [Fact]
    public async Task Setup_flag_reopens_the_page_and_keeps_the_teams()
    {
        Directory.CreateDirectory(_folder);
        var before = new BotConfig
        {
            GuildId = 5000,
            Groups = new GroupsConfig { Attack = new GroupConfig { Channels = [new ChannelId(101)] } },
            Bots = [new BotEntry { TokenRef = "relay1", BotUserId = new UserId(9001), ChannelId = new ChannelId(101), Primary = true }],
        };
        // Its token is gone: the file alone would not start, and setup is how to fix it.
        await File.WriteAllTextAsync(Path.Combine(_folder, "config.json"), ConfigStore.Serialize(before));
        var (run, setup, started) = Start("--setup");
        var server = await setup.WaitAsync(TimeSpan.FromSeconds(20));

        await CompleteSetupAsync(server, 1, 3);
        var bot = await started.WaitAsync(TimeSpan.FromSeconds(20));

        Assert.Equal([new ChannelId(101)], bot.Config.Groups.Attack.Channels);
        Assert.Equal(new ChannelId(101), bot.Config.Bots[0].ChannelId);
        Assert.Equal(2, bot.Config.Bots.Count);
        await _stop.CancelAsync();
        Assert.Equal(0, await run);
    }

    [Fact]
    public async Task A_config_whose_tokens_are_missing_says_to_run_setup()
    {
        Directory.CreateDirectory(_folder);
        await File.WriteAllTextAsync(Path.Combine(_folder, "config.json"), ConfigStore.Serialize(new BotConfig()));

        var (run, _, _) = Start();

        Assert.Equal(1, await run.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Contains("--setup", _error.ToString());
    }

    [Fact]
    public async Task A_run_that_did_not_stop_cleanly_is_reported_to_the_alerts_channel()
    {
        Directory.CreateDirectory(_folder);
        var config = new BotConfig
        {
            GuildId = 5000,
            AlertsChannelId = new ChannelId(900),
            Bots = [new BotEntry { TokenRef = "relay1", Primary = true }],
        };
        await File.WriteAllTextAsync(Path.Combine(_folder, "config.json"), ConfigStore.Serialize(config));
        await File.WriteAllTextAsync(Path.Combine(_folder, "secrets.json"), $"{{ \"relay1\": \"{FakeAccounts.Token(1)}\" }}");
        await File.WriteAllTextAsync(Path.Combine(_folder, RunMarker.FileName), "");

        var (run, _, started) = Start();
        await started.WaitAsync(TimeSpan.FromSeconds(20));

        var (channel, text) = Assert.Single(_discord.Posts);
        Assert.Equal(new ChannelId(900), channel);
        Assert.Contains("stopped unexpectedly", text);
        Assert.True(File.Exists(Path.Combine(_folder, RunMarker.FileName)));
        await _stop.CancelAsync();
        Assert.Equal(0, await run);
        Assert.False(File.Exists(Path.Combine(_folder, RunMarker.FileName)));
    }
}
