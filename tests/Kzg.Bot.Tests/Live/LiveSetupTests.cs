using Kzg.Core.Config;
using Kzg.Discord.Commands;
using Kzg.Discord.Setup;
using NetCord;
using NetCord.Rest;

namespace Kzg.Bot.Tests.Live;

/// <summary>
/// What the setup page and <c>/gvg setup</c> need from the real Discord, without voice: every relay token is
/// a bot in the test server, and Discord accepts the slash commands with the new options. It posts nothing.
/// </summary>
[Collection(LiveCollection.Name)]
public sealed class LiveSetupTests
{
    private static (Secrets Secrets, BotConfig Config) Dev()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "Kzg.slnx")))
        {
            repo = repo.Parent ?? throw new InvalidOperationException("Kzg.slnx not found above the test");
        }
        var data = Path.Combine(repo.FullName, ".data");
        var secrets = ((FileLoad<Secrets>.Loaded)new SecretsStore(Path.Combine(data, "secrets.json")).Load(Environment.GetEnvironmentVariables())).Value;
        return (secrets, ((FileLoad<BotConfig>.Loaded)new ConfigStore(Path.Combine(data, "config.json")).Load(secrets)).Value);
    }

    [LiveFact]
    public async Task Every_relay_token_checks_out_and_is_in_the_test_server_as_the_setup_page_sees_it()
    {
        var (secrets, config) = Dev();
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var accounts = new DiscordBotAccounts(http);

        foreach (var bot in config.Bots)
        {
            Assert.True(secrets.TryGet(bot.TokenRef, out var token));
            var account = Assert.IsType<TokenCheck.Accepted>(await accounts.CheckAsync(DiscordBotAccounts.Normalize(token)!, default)).Account;
            Assert.Equal(bot.BotUserId!.Value.V, account.BotUserId);
            var servers = await accounts.ServersAsync(token!, default);
            Assert.Contains(servers!, s => s.Id == config.GuildId);
        }
        Assert.IsType<TokenCheck.Refused>(await accounts.CheckAsync(string.Join('.', new string('M', 26), "Gabcde", new string('x', 38)), default));
    }

    [LiveFact]
    public async Task Discord_accepts_the_slash_commands_with_setup_and_alerts()
    {
        var (secrets, config) = Dev();
        var primary = config.Bots.Single(b => b.Primary);
        Assert.True(secrets.TryGet(primary.TokenRef, out var token));
        using var rest = new RestClient(new BotToken(token!));
        var application = await rest.GetCurrentApplicationAsync();

        // The same call the bot makes at every start.
        var spec = CommandSpec.Build(config);
        var registered = await rest.BulkOverwriteGuildApplicationCommandsAsync(application.Id, config.GuildId!.Value, spec);

        // Discord kept every option of /gvg setup that the spec sends: a channel per team seat and the alerts channel.
        var setupSpec = ((SlashCommandProperties)spec.Single(c => c.Name == CommandSpec.Gvg)).Options!.Single(o => o.Name == "setup");
        var gvg = registered.Single(c => c.Name == CommandSpec.Gvg);
        var setup = gvg.Options!.Single(o => o.Name == "setup");
        Assert.Equal(setupSpec.Options!.Select(o => o.Name).Order(), setup.Options!.Select(o => o.Name).Order());
        Assert.Contains(gvg.Options!, o => o.Name == "alerts");
    }
}
