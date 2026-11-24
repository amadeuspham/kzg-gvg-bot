using Kzg.Bot.Control;
using Kzg.Core.Pairing;
using Kzg.Protocol;
using static Kzg.Bot.Tests.Control.Harness;

namespace Kzg.Bot.Tests.Control;

public sealed class DiscordSignInTests : IAsyncDisposable
{
    private readonly Harness _bot = new();
    private readonly string _login = WireSecret.Generate();

    public ValueTask DisposeAsync() => _bot.DisposeAsync();

    /// <summary>The app connects with its loginId, then "opens the browser".</summary>
    private async Task<TestApp> WaitingAppAsync()
    {
        var app = await _bot.ConnectAsync();
        await app.SendAsync(Hello(login: _login));
        for (var i = 0; i < 200 && _bot.Logins.CanStart(SecretHash.OfWire(_login)!) != CallbackClaim.Granted; i++)
        {
            await Task.Delay(10);
        }
        return app;
    }

    [Fact]
    public async Task The_waiting_app_is_welcomed_with_a_token_once_its_player_signs_in()
    {
        var app = await WaitingAppAsync();
        _bot.Discord.Codes["discord-code"] = Alice;

        var start = _bot.Auth.Start(_login);
        Assert.Equal(302, start.Status);
        Assert.Contains("state=" + _login, start.Location!.Query, StringComparison.Ordinal);
        Assert.Contains("scope=identify&", start.Location.Query, StringComparison.Ordinal);
        Assert.Contains("client_id=333333333333333333", start.Location.Query, StringComparison.Ordinal);

        var callback = await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None);
        Assert.Equal(200, callback.Status);
        Assert.Contains("Signed in, return to the app.", callback.Body, StringComparison.Ordinal);
        Assert.Equal([new Uri("https://gvg.example.com/auth/callback")], _bot.Discord.RedirectUris);

        var welcome = await app.ExpectAsync<WelcomeMessage>();
        Assert.Equal("1", welcome.UserId);
        Assert.True(WireSecret.IsValid(welcome.DeviceToken));
        Assert.IsType<DeviceAuth.Valid>(_bot.Devices.Authenticate(welcome.DeviceToken!));

        // The login is spent: neither the browser nor another app can use it again.
        Assert.Equal(409, _bot.Auth.Start(_login).Status);
        Assert.Equal(409, (await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None)).Status);
        var replay = await _bot.ConnectAsync();
        await replay.SendAsync(Hello(login: _login));
        var (_, _, before) = await replay.ExpectCloseAsync();
        Assert.Equal(ErrorCode.Used, Assert.IsType<ErrorMessage>(Assert.Single(before)).Code);
    }

    [Fact]
    public async Task A_sign_in_by_someone_outside_the_guild_is_refused_on_both_sides()
    {
        var app = await WaitingAppAsync();
        _bot.Discord.Codes["discord-code"] = Stranger;

        var callback = await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None);

        Assert.Equal(403, callback.Status);
        var (status, _, before) = await app.ExpectCloseAsync();
        Assert.Equal(CloseCodes.Policy, status);
        Assert.Equal(ErrorCode.NotMember, Assert.IsType<ErrorMessage>(Assert.Single(before)).Code);
        Assert.Equal(0, _bot.Devices.Count);
    }

    [Fact]
    public async Task The_browser_cannot_start_or_finish_a_login_no_app_is_waiting_on()
    {
        Assert.Equal(404, _bot.Auth.Start(_login).Status);
        Assert.Equal(404, (await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None)).Status);
        Assert.Equal(400, _bot.Auth.Start("not-a-login").Status);
        Assert.Equal(400, _bot.Auth.Start(null).Status);
        Assert.Equal(400, (await _bot.Auth.CallbackAsync("discord-code", "not-a-login", null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task A_cancelled_or_refused_sign_in_leaves_the_login_open_for_another_try()
    {
        var app = await WaitingAppAsync();

        Assert.Equal(400, (await _bot.Auth.CallbackAsync(null, _login, "access_denied", CancellationToken.None)).Status);
        Assert.Equal(400, (await _bot.Auth.CallbackAsync(null, _login, null, CancellationToken.None)).Status);
        Assert.Equal(400, (await _bot.Auth.CallbackAsync(new string('c', 513), _login, null, CancellationToken.None)).Status);
        Assert.Equal(400, (await _bot.Auth.CallbackAsync("code-discord-refuses", _login, null, CancellationToken.None)).Status);
        _bot.Discord.Unreachable = true;
        Assert.Equal(502, (await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None)).Status);
        _bot.Discord.Unreachable = false;

        _bot.Discord.Codes["discord-code"] = Bob;
        Assert.Equal(302, _bot.Auth.Start(_login).Status);
        Assert.Equal(200, (await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None)).Status);
        Assert.Equal("2", (await app.ExpectAsync<WelcomeMessage>()).UserId);
    }

    [Fact]
    public async Task A_login_expires_after_ten_minutes()
    {
        var app = await WaitingAppAsync();
        _bot.Time.Now += PendingLogins.Lifetime;

        Assert.Equal(410, _bot.Auth.Start(_login).Status);
        Assert.Equal(410, (await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None)).Status);
        await app.DisposeAsync();

        var late = await _bot.ConnectAsync();
        await late.SendAsync(Hello(login: _login));
        var (_, _, before) = await late.ExpectCloseAsync();
        Assert.Equal(ErrorCode.Expired, Assert.IsType<ErrorMessage>(Assert.Single(before)).Code);
    }

    [Fact]
    public async Task An_app_that_reconnects_with_its_login_picks_up_a_sign_in_that_finished_while_it_was_away()
    {
        var app = await WaitingAppAsync();
        await app.DisposeAsync();
        _bot.Discord.Codes["discord-code"] = Alice;

        Assert.Equal(200, (await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None)).Status);
        var back = await _bot.ConnectAsync();
        await back.SendAsync(Hello(login: _login));

        Assert.Equal("1", (await back.ExpectAsync<WelcomeMessage>()).UserId);
    }

    [Fact]
    public async Task A_second_app_with_the_same_login_is_refused_while_the_first_waits()
    {
        await WaitingAppAsync();

        var second = await _bot.ConnectAsync();
        await second.SendAsync(Hello(login: _login));

        var (_, _, before) = await second.ExpectCloseAsync();
        Assert.Equal(ErrorCode.Used, Assert.IsType<ErrorMessage>(Assert.Single(before)).Code);
    }

    [Fact]
    public async Task With_sign_in_off_the_auth_pages_say_so()
    {
        _bot.SignInOn = false;

        Assert.Equal(404, _bot.Auth.Start(_login).Status);
        Assert.Equal(404, (await _bot.Auth.CallbackAsync("c", _login, null, CancellationToken.None)).Status);
    }

    [Fact]
    public async Task Pages_carry_fixed_words_only_and_are_never_cached()
    {
        var page = await _bot.Auth.CallbackAsync("<script>", _login, "<script>", CancellationToken.None);

        Assert.DoesNotContain("<script>", page.Body, StringComparison.Ordinal);
        Assert.DoesNotContain(_login, page.Body, StringComparison.Ordinal);
        Assert.StartsWith("text/html", page.ContentType, StringComparison.Ordinal);
        Assert.Equal("no-store", HttpReply.Headers["Cache-Control"]);
        Assert.Equal("no-referrer", HttpReply.Headers["Referrer-Policy"]);
        Assert.Equal(new HttpReply(200, "text/plain; charset=utf-8", "ok\n"), AuthEndpoints.Healthz());
    }

    [Fact]
    public async Task A_cancelled_callback_lets_the_next_try_in()
    {
        await WaitingAppAsync();
        _bot.Discord.Codes["discord-code"] = Alice;
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var blocking = new BlockingDiscord();
        var auth = new AuthEndpoints(_bot.Logins, blocking, () => new SignInSettings(1, Host));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => auth.CallbackAsync("discord-code", _login, null, cancelled.Token));

        Assert.Equal(200, (await _bot.Auth.CallbackAsync("discord-code", _login, null, CancellationToken.None)).Status);
    }

    private sealed class BlockingDiscord : IDiscordIdentity
    {
        public Task<Kzg.Core.Abstractions.UserId?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken) =>
            Task.FromCanceled<Kzg.Core.Abstractions.UserId?>(cancellationToken);

        public Task<GuildMember?> GetMemberAsync(Kzg.Core.Abstractions.UserId user, CancellationToken cancellationToken) =>
            Task.FromCanceled<GuildMember?>(cancellationToken);
    }
}
