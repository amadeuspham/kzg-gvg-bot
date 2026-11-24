using System.Net;
using System.Text;
using Kzg.Core.Abstractions;
using Kzg.Core.Pairing;
using Kzg.Discord.Identity;
using Microsoft.Extensions.Logging;

namespace Kzg.Discord.Tests;

public sealed class DiscordIdentityTests
{
    private const ulong ClientId = 123456789012345678;
    private const string ClientSecret = "the-client-secret-value-0123456789";
    private const string AuthCode = "the-oauth-code-from-the-browser";
    private const string AccessToken = "the-access-token-discord-issued";
    private static readonly Uri Redirect = new("https://gvg.example.com/auth/callback");

    private sealed record Seen(HttpMethod Method, string Url, string? Authorization, string Body);

    private sealed class Handler(Func<Seen, HttpResponseMessage> reply) : HttpMessageHandler
    {
        public List<Seen> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var seen = new Seen(
                request.Method,
                request.RequestUri!.AbsoluteUri,
                request.Headers.Authorization?.ToString(),
                request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellationToken));
            Requests.Add(seen);
            return reply(seen);
        }
    }

    private sealed class Capture : ILogger
    {
        public StringBuilder Text { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Text.AppendLine(formatter(state, exception));
    }

    private sealed class Directory(Dictionary<ulong, MemberNames> members) : IGuildDirectory
    {
        public Task<MemberNames?> FindAsync(ulong guildId, ulong userId, CancellationToken cancellationToken) =>
            Task.FromResult(guildId == 5000 && members.TryGetValue(userId, out var names) ? names : null);
    }

    private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Good(Seen request) => request.Url switch
    {
        var url when url.EndsWith("/oauth2/token", StringComparison.Ordinal) => Json(HttpStatusCode.OK, $$"""{"access_token":"{{AccessToken}}","token_type":"Bearer","scope":"identify"}"""),
        var url when url.EndsWith("/users/@me", StringComparison.Ordinal) => Json(HttpStatusCode.OK, """{"id":"4242","username":"alice"}"""),
        _ => new HttpResponseMessage(HttpStatusCode.OK),
    };

    private static (DiscordIdentity Identity, Handler Handler, Capture Log) Build(
        Func<Seen, HttpResponseMessage> reply, bool withCredentials = true, Dictionary<ulong, MemberNames>? members = null, ulong? guild = 5000)
    {
        var handler = new Handler(reply);
        var log = new Capture();
        var identity = new DiscordIdentity(
            new HttpClient(handler),
            () => withCredentials ? new OAuthCredentials(ClientId, ClientSecret) : null,
            () => guild,
            new Directory(members ?? []),
            log);
        return (identity, handler, log);
    }

    [Fact]
    public async Task A_good_code_is_traded_for_the_user_who_signed_in()
    {
        var (identity, handler, _) = Build(Good);

        var user = await identity.ExchangeCodeAsync(AuthCode, Redirect, default);

        Assert.Equal(new UserId(4242), user);
        Assert.Equal(["POST /oauth2/token", "GET /users/@me", "POST /oauth2/token/revoke"], handler.Requests.Select(r => $"{r.Method} {new Uri(r.Url).AbsolutePath.Replace("/api/v10", "")}"));
    }

    [Fact]
    public async Task The_exchange_sends_the_code_and_redirect_and_signs_with_the_client_credentials()
    {
        var (identity, handler, _) = Build(Good);

        await identity.ExchangeCodeAsync(AuthCode, Redirect, default);

        var token = handler.Requests[0];
        Assert.Equal("https://discord.com/api/v10/oauth2/token", token.Url);
        Assert.Contains("grant_type=authorization_code", token.Body);
        Assert.Contains($"code={AuthCode}", token.Body);
        Assert.Contains("redirect_uri=" + Uri.EscapeDataString(Redirect.AbsoluteUri), token.Body);
        var expected = Convert.ToBase64String(Encoding.UTF8.GetBytes($"{ClientId}:{ClientSecret}"));
        Assert.Equal("Basic " + expected, token.Authorization);
        // The secret is only ever in the header, never in a body or a URL.
        Assert.All(handler.Requests, r => Assert.DoesNotContain(ClientSecret, r.Body + r.Url));
    }

    [Fact]
    public async Task The_user_is_asked_for_with_the_access_token_and_the_token_is_revoked_after()
    {
        var (identity, handler, _) = Build(Good);

        await identity.ExchangeCodeAsync(AuthCode, Redirect, default);

        Assert.Equal("Bearer " + AccessToken, handler.Requests[1].Authorization);
        Assert.Equal("https://discord.com/api/v10/oauth2/token/revoke", handler.Requests[2].Url);
        Assert.Contains($"token={AccessToken}", handler.Requests[2].Body);
    }

    [Fact]
    public async Task A_revoke_that_fails_does_not_fail_the_sign_in()
    {
        var (identity, _, _) = Build(r => r.Url.EndsWith("/revoke", StringComparison.Ordinal) ? throw new HttpRequestException("down") : Good(r));

        var user = await identity.ExchangeCodeAsync(AuthCode, Redirect, default);

        Assert.Equal(new UserId(4242), user);
    }

    [Fact]
    public async Task A_code_Discord_refuses_gives_no_user()
    {
        var (identity, handler, _) = Build(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));

        var user = await identity.ExchangeCodeAsync(AuthCode, Redirect, default);

        Assert.Null(user);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Refused_client_credentials_are_an_error_not_a_refused_code_and_the_log_names_no_secret()
    {
        var (identity, _, log) = Build(_ => Json(HttpStatusCode.Unauthorized, """{"error":"invalid_client"}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.ExchangeCodeAsync(AuthCode, Redirect, default));

        Assert.Contains("client credentials", log.Text.ToString());
        Assert.DoesNotContain(ClientSecret, log.Text.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests)]
    [InlineData(HttpStatusCode.InternalServerError)]
    [InlineData(HttpStatusCode.BadGateway)]
    public async Task Discord_being_unreachable_or_busy_is_an_error_so_the_page_says_so(HttpStatusCode status)
    {
        var (identity, _, _) = Build(_ => new HttpResponseMessage(status));

        await Assert.ThrowsAsync<HttpRequestException>(() => identity.ExchangeCodeAsync(AuthCode, Redirect, default));
    }

    [Fact]
    public async Task A_network_failure_is_an_error()
    {
        var (identity, _, _) = Build(_ => throw new HttpRequestException("no route"));

        await Assert.ThrowsAsync<HttpRequestException>(() => identity.ExchangeCodeAsync(AuthCode, Redirect, default));
    }

    [Fact]
    public async Task A_token_reply_without_an_access_token_is_an_error()
    {
        var (identity, _, _) = Build(_ => Json(HttpStatusCode.OK, """{"token_type":"Bearer"}"""));

        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.ExchangeCodeAsync(AuthCode, Redirect, default));
    }

    [Theory]
    [InlineData("""{"id":"not-a-snowflake"}""")]
    [InlineData("""{"id":4242}""")]
    [InlineData("""{"username":"x"}""")]
    public async Task A_user_reply_without_a_proper_id_gives_no_user_and_the_token_is_still_revoked(string me)
    {
        var (identity, handler, _) = Build(r => r.Url.EndsWith("/users/@me", StringComparison.Ordinal) ? Json(HttpStatusCode.OK, me) : Good(r));

        var user = await identity.ExchangeCodeAsync(AuthCode, Redirect, default);

        Assert.Null(user);
        Assert.EndsWith("/revoke", handler.Requests[^1].Url);
    }

    [Fact]
    public async Task An_access_token_Discord_then_refuses_gives_no_user()
    {
        var (identity, _, _) = Build(r => r.Url.EndsWith("/users/@me", StringComparison.Ordinal) ? Json(HttpStatusCode.Unauthorized, "{}") : Good(r));

        Assert.Null(await identity.ExchangeCodeAsync(AuthCode, Redirect, default));
    }

    [Fact]
    public async Task Without_credentials_nothing_is_sent()
    {
        var (identity, handler, _) = Build(Good, withCredentials: false);

        var user = await identity.ExchangeCodeAsync(AuthCode, Redirect, default);

        Assert.Null(user);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Neither_the_code_nor_the_tokens_nor_the_secret_reach_the_log()
    {
        var (identity, _, log) = Build(Good);
        await identity.ExchangeCodeAsync(AuthCode, Redirect, default);
        var (refused, _, refusedLog) = Build(_ => Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}"""));
        await refused.ExchangeCodeAsync(AuthCode, Redirect, default);

        var text = log.Text.ToString() + refusedLog.Text;

        Assert.DoesNotContain(AuthCode, text);
        Assert.DoesNotContain(AccessToken, text);
        Assert.DoesNotContain(ClientSecret, text);
    }

    [Fact]
    public void The_credentials_print_the_client_id_and_never_the_secret()
    {
        Assert.DoesNotContain(ClientSecret, new OAuthCredentials(ClientId, ClientSecret).ToString());
    }

    // ---- membership

    [Fact]
    public async Task A_member_is_known_by_their_nickname_first()
    {
        var (identity, _, _) = Build(Good, members: new() { [42] = new MemberNames("Nick", "Global", "user") });

        var member = await identity.GetMemberAsync(new UserId(42), default);

        Assert.Equal(new GuildMember(new UserId(42), "Nick"), member);
    }

    [Theory]
    [InlineData(null, "Global", "user", "Global")]
    [InlineData("  ", null, "user", "user")]
    [InlineData(null, null, null, "Player 42")]
    [InlineData("A\u0007B\nC", null, null, "ABC")]
    public async Task A_name_falls_back_and_is_tidied_to_what_the_wire_takes(string? nick, string? global, string? user, string expected)
    {
        var (identity, _, _) = Build(Good, members: new() { [42] = new MemberNames(nick, global, user) });

        var member = await identity.GetMemberAsync(new UserId(42), default);

        Assert.Equal(expected, member!.DisplayName);
    }

    [Fact]
    public async Task A_long_name_is_cut_to_100_characters_without_splitting_a_pair()
    {
        var name = new string('x', 99) + "\U0001F600";
        var (identity, _, _) = Build(Good, members: new() { [42] = new MemberNames(name, null, null) });

        var member = await identity.GetMemberAsync(new UserId(42), default);

        Assert.Equal(99, member!.DisplayName.Length);
    }

    [Fact]
    public async Task Someone_who_is_not_in_the_guild_is_not_a_member()
    {
        var (identity, _, _) = Build(Good, members: new() { [42] = new MemberNames("Nick", null, null) });

        Assert.Null(await identity.GetMemberAsync(new UserId(43), default));
    }

    [Fact]
    public async Task Before_the_bot_knows_its_server_a_membership_check_is_an_error()
    {
        var (identity, _, _) = Build(Good, guild: null);

        await Assert.ThrowsAsync<InvalidOperationException>(() => identity.GetMemberAsync(new UserId(42), default));
    }
}
