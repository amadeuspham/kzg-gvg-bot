using System.Net;
using System.Text;
using Kzg.Discord.Setup;

namespace Kzg.Discord.Tests;

public sealed class BotAccountsTests
{
    // Made up: the shape of a bot token, nobody's.
    private static readonly string Token = string.Join('.', new string('M', 26), "Gabcde", new string('x', 38));

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<(string Url, string? Authorization)> Seen { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Seen.Add((request.RequestUri!.AbsoluteUri, request.Headers.Authorization?.ToString()));
            return Task.FromResult(reply(request));
        }
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string body) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task A_good_token_gives_the_application_its_bot_and_its_name_and_goes_in_the_header_only()
    {
        var handler = new Handler(_ => Json(HttpStatusCode.OK, """{"id":"111","name":"Kzg Relay 1","bot":{"id":"222","username":"x"}}"""));
        using var http = new HttpClient(handler);

        var check = await new DiscordBotAccounts(http).CheckAsync(Token, default);

        Assert.Equal(new BotAccount(111, 222, "Kzg Relay 1"), Assert.IsType<TokenCheck.Accepted>(check).Account);
        var (url, authorization) = Assert.Single(handler.Seen);
        Assert.Equal("https://discord.com/api/v10/applications/@me", url);
        Assert.Equal("Bot " + Token, authorization);
        Assert.DoesNotContain(Token, url);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, typeof(TokenCheck.Refused))]
    [InlineData(HttpStatusCode.Forbidden, typeof(TokenCheck.Refused))]
    [InlineData(HttpStatusCode.InternalServerError, typeof(TokenCheck.Unreachable))]
    public async Task A_refused_token_and_a_failing_Discord_are_told_apart(HttpStatusCode status, Type expected)
    {
        using var http = new HttpClient(new Handler(_ => Json(status, "{}")));

        Assert.IsType(expected, await new DiscordBotAccounts(http).CheckAsync(Token, default));
    }

    [Fact]
    public async Task No_network_and_a_reply_without_an_id_are_unreachable()
    {
        using var down = new HttpClient(new Handler(_ => throw new HttpRequestException("no route")));
        using var odd = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, """{"name":"x"}""")));

        Assert.IsType<TokenCheck.Unreachable>(await new DiscordBotAccounts(down).CheckAsync(Token, default));
        Assert.IsType<TokenCheck.Unreachable>(await new DiscordBotAccounts(odd).CheckAsync(Token, default));
        Assert.Null(await new DiscordBotAccounts(down).ServersAsync(Token, default));
    }

    [Fact]
    public async Task The_servers_are_read_with_their_names_and_a_refusal_gives_none()
    {
        using var http = new HttpClient(new Handler(_ => Json(HttpStatusCode.OK, """[{"id":"5000","name":"KZG GvG"},{"id":"bad"},{"id":"6000","name":"Other"}]""")));
        using var refused = new HttpClient(new Handler(_ => Json(HttpStatusCode.Unauthorized, "{}")));

        var servers = await new DiscordBotAccounts(http).ServersAsync(Token, default);

        Assert.Equal([new ServerRef(5000, "KZG GvG"), new ServerRef(6000, "Other")], servers);
        Assert.Null(await new DiscordBotAccounts(refused).ServersAsync(Token, default));
    }

    [Fact]
    public void A_pasted_token_loses_spaces_and_a_Bot_prefix_and_anything_else_is_turned_away()
    {
        Assert.Equal(Token, DiscordBotAccounts.Normalize("  " + Token + "\n"));
        Assert.Equal(Token, DiscordBotAccounts.Normalize("Bot " + Token));
        Assert.Null(DiscordBotAccounts.Normalize("1234567890"));
        Assert.Null(DiscordBotAccounts.Normalize("https://discord.com/oauth2/authorize?client_id=1"));
        Assert.Null(DiscordBotAccounts.Normalize(Token + "\r\nX-Injected: 1"));
        Assert.Null(DiscordBotAccounts.Normalize(null));
    }
}
