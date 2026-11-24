using System.Collections;
using System.Net;
using Kzg.Bot.Logging;
using Kzg.Bot.Setup;
using Kzg.Core.Config;
using Kzg.Core.Testing;

namespace Kzg.Bot.Tests.Setup;

/// <summary>The setup page over real HTTP on a loopback port: who gets in, and the whole first run in a browser's shoes.</summary>
public sealed class SetupServerTests : IAsyncLifetime
{
    private const string PublicHost = "gvg.example.com";

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-setup-server-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeAccounts _accounts = new();
    private readonly List<string> _keys = [];
    private readonly ManualTimeSource _time = new();
    private readonly StringWriter _log = new();
    private readonly HttpClient _http = new(new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false });
    private SetupWizard _wizard = null!;
    private SetupServer _server = null!;

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_folder);
        _wizard = new SetupWizard(_accounts, new DataPaths(_folder), null, PublicHost, _time, _keys.Add, new Hashtable());
        _server = SetupServer.Create(_wizard, new IPEndPoint(IPAddress.Loopback, 0), PublicHost, TextWriter.Synchronized(_log), Redactor.For(Secrets.Empty));
        await _server.StartAsync(default);
        _wizard.IssueKey();
    }

    public async Task DisposeAsync()
    {
        _http.Dispose();
        await _server.StopAsync();
        await _server.DisposeAsync();
        Directory.Delete(_folder, recursive: true);
    }

    private Uri Url(string path) => new(_server.Address, path);

    private async Task<HttpResponseMessage> Get(string path, string? cookie = null, Action<HttpRequestMessage>? edit = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Url(path));
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{SetupServer.CookieName}={cookie}");
        }
        edit?.Invoke(request);
        return await _http.SendAsync(request);
    }

    private async Task<HttpResponseMessage> Post(string path, string? cookie, Dictionary<string, string> form, Action<HttpRequestMessage>? edit = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Url(path)) { Content = new FormUrlEncodedContent(form) };
        if (cookie is not null)
        {
            request.Headers.Add("Cookie", $"{SetupServer.CookieName}={cookie}");
        }
        edit?.Invoke(request);
        return await _http.SendAsync(request);
    }

    /// <summary>Opens the printed link as a browser would; the session cookie it sets.</summary>
    private async Task<string> OpenLinkAsync(Action<HttpRequestMessage>? edit = null)
    {
        using var opened = await Get("/setup?key=" + _keys[^1], edit: edit);
        Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
        Assert.Equal("/setup", opened.Headers.Location!.OriginalString);
        var cookie = Assert.Single(opened.Headers.GetValues("Set-Cookie"));
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);
        return cookie.Split(';')[0].Split('=', 2)[1];
    }

    [Fact]
    public async Task Without_the_link_nothing_but_healthz_is_served()
    {
        using var health = await Get("/healthz");
        using var page = await Get("/setup");
        using var wrong = await Get("/setup?key=" + Kzg.Protocol.WireSecret.Generate());
        using var forged = await Get("/setup", cookie: Kzg.Protocol.WireSecret.Generate());
        using var post = await Post("/setup/tokens", null, new() { ["token1"] = FakeAccounts.Token(1) });
        using var other = await Get("/ws");

        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, page.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, wrong.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, forged.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, post.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, other.StatusCode);
        Assert.Equal(SetupStep.Tokens, _wizard.View().Step);
    }

    [Fact]
    public async Task The_link_works_once_and_shown_again_it_prints_a_new_one()
    {
        var cookie = await OpenLinkAsync();
        var spentKey = _keys[0];

        using var page = await Get("/setup", cookie);
        using var again = await Get("/setup?key=" + spentKey);

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Step 1 of 3", await page.Content.ReadAsStringAsync());
        Assert.Equal("default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'",
            page.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("no-store", page.Headers.CacheControl!.ToString());
        Assert.Equal(HttpStatusCode.Gone, again.StatusCode);
        Assert.Equal(2, _keys.Count);
    }

    [Theory]
    [InlineData("evil.example", null, null)]
    [InlineData("gvg.example.com", null, null)]
    [InlineData("gvg.example.com", "http", "203.0.113.9")]
    [InlineData("evil.example", "https", "203.0.113.9")]
    public async Task A_wrong_host_or_plain_http_through_a_proxy_is_turned_away_even_with_the_key(string host, string? proto, string? forwardedFor)
    {
        using var response = await Get("/setup?key=" + _keys[^1], edit: request =>
        {
            request.Headers.Host = host;
            if (proto is not null)
            {
                request.Headers.Add("X-Forwarded-Proto", proto);
                request.Headers.Add("X-Forwarded-For", forwardedFor);
            }
        });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        // The key is still good: a turned-away request didn't spend it.
        await OpenLinkAsync();
    }

    [Fact]
    public async Task Through_the_HTTPS_proxy_at_the_public_address_the_page_opens_with_a_secure_cookie()
    {
        void Proxied(HttpRequestMessage request)
        {
            request.Headers.Host = PublicHost;
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-For", "203.0.113.9");
        }
        using var opened = await Get("/setup?key=" + _keys[^1], edit: Proxied);

        Assert.Equal(HttpStatusCode.Redirect, opened.StatusCode);
        Assert.Contains("secure", opened.Headers.GetValues("Set-Cookie").Single(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_form_posted_from_another_site_is_refused()
    {
        var cookie = await OpenLinkAsync();
        var form = new Dictionary<string, string> { ["token1"] = FakeAccounts.Token(1) };

        using var crossOrigin = await Post("/setup/tokens", cookie, form, r => r.Headers.Add("Origin", "https://evil.example"));
        using var crossSite = await Post("/setup/tokens", cookie, form, r => r.Headers.Add("Sec-Fetch-Site", "cross-site"));

        Assert.Equal(HttpStatusCode.Forbidden, crossOrigin.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, crossSite.StatusCode);
        Assert.Equal(SetupStep.Tokens, _wizard.View().Step);
    }

    [Fact]
    public async Task The_whole_first_run_paste_invite_finish_writes_the_files_and_releases_the_bot()
    {
        var cookie = await OpenLinkAsync();

        using var submitted = await Post("/setup/tokens", cookie, new() { ["token1"] = FakeAccounts.Token(1), ["token2"] = FakeAccounts.Token(2), ["publicHost"] = PublicHost },
            r => r.Headers.Add("Origin", _server.Address.GetLeftPart(UriPartial.Authority)));
        Assert.Equal(HttpStatusCode.SeeOther, submitted.StatusCode);

        using var waiting = await Get("/setup", cookie);
        var invite = await waiting.Content.ReadAsStringAsync();
        Assert.Contains("Step 2 of 3", invite);
        Assert.Contains("Not yet", invite);
        Assert.DoesNotContain("Finish setup", invite);

        _accounts.Join(1);
        _accounts.Join(2);
        _time.Advance(TimeSpan.FromSeconds(5));
        using var ready = await Get("/setup", cookie);
        Assert.Contains("Finish setup", await ready.Content.ReadAsStringAsync());

        using var finished = await Post("/setup/finish", cookie, new() { ["server"] = FakeAccounts.Guild.ToString(System.Globalization.CultureInfo.InvariantCulture) });
        var done = await finished.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, finished.StatusCode);
        Assert.Contains("/gvg setup", done);
        var config = await _wizard.Completed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(FakeAccounts.Guild, config.GuildId);
        Assert.True(File.Exists(Path.Combine(_folder, "secrets.json")));
        foreach (var page in new[] { invite, done, _log.ToString() })
        {
            Assert.DoesNotContain(FakeAccounts.Token(1), page);
            Assert.DoesNotContain(FakeAccounts.Token(2), page);
        }
    }

    [Fact]
    public async Task Bad_tokens_come_back_as_errors_on_the_page_without_the_token()
    {
        var cookie = await OpenLinkAsync();

        using var submitted = await Post("/setup/tokens", cookie, new() { ["token1"] = FakeAccounts.Token(7) });
        using var page = await Get("/setup", cookie);
        var html = await page.Content.ReadAsStringAsync();

        Assert.Contains("doesn&#39;t accept this token", html);
        Assert.DoesNotContain(FakeAccounts.Token(7), html);
    }

    [Fact]
    public void Admits_reads_loopback_hosts_and_the_forwarded_header()
    {
        static Microsoft.AspNetCore.Http.HttpRequest Request(string host, params (string, string)[] headers)
        {
            var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            context.Request.Host = new Microsoft.AspNetCore.Http.HostString(host);
            foreach (var (name, value) in headers)
            {
                context.Request.Headers[name] = value;
            }
            return context.Request;
        }

        Assert.True(SetupServer.Admits(Request("localhost:7878"), null));
        Assert.True(SetupServer.Admits(Request("[::1]:7878"), null));
        Assert.True(SetupServer.Admits(Request("gvg.example.com:8443", ("Forwarded", "for=1.2.3.4;proto=https")), "gvg.example.com:8443"));
        Assert.False(SetupServer.Admits(Request("gvg.example.com", ("X-Real-IP", "1.2.3.4")), "gvg.example.com"));
        Assert.False(SetupServer.Admits(Request("192.168.1.5:7878"), null));
    }
}
