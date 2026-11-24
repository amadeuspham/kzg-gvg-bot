using Kzg.Bot.Logging;
using Kzg.Core.Config;
using Kzg.Protocol;
using Microsoft.Extensions.Logging;

namespace Kzg.Bot.Tests.Logging;

// What the plan promises: tokens, the client secret, codes and device tokens never reach a log.
public sealed class RedactionTests
{
    // Made up at run time, so no scanner sees a token-shaped string in the source.
    private static readonly string BotToken = string.Join('.', new string('A', 24), "G1x2y3", new string('b', 27));
    private static readonly string ClientSecret = "cs_" + new string('7', 29);

    private static Redactor Known() => For(BotToken, ClientSecret);

    private static Redactor For(string token, string clientSecret) =>
        Redactor.For(Secrets.Combine(
            new Dictionary<string, string> { ["relay1"] = token, [Secrets.OAuthClientSecretName] = clientSecret, ["blank"] = "" },
            new Dictionary<string, string>()));

    [Fact]
    public void The_secrets_Kzg_holds_are_masked_by_value_wherever_they_appear()
    {
        var line = $"login failed for token {BotToken} with secret={ClientSecret}, also {{\"x\":\"{ClientSecret}\"}}";

        var safe = Known().Redact(line);

        Assert.DoesNotContain(BotToken, safe);
        Assert.DoesNotContain(ClientSecret, safe);
        Assert.Contains("login failed for token", safe);
    }

    [Fact]
    public void A_secret_too_short_to_tell_from_ordinary_words_is_not_masked_by_value()
    {
        var redactor = For("abc", "x");

        Assert.Equal("abc and x are fine", redactor.Redact("abc and x are fine"));
    }

    [Fact]
    public void A_connect_code_is_masked_whole_host_and_secret()
    {
        var code = ConnectCode.Generate("gvg.example.com").Format();

        var safe = Known().Redact($"/connect gave {code} to a player");

        Assert.DoesNotContain(code, safe);
        Assert.DoesNotContain(code.Split('.')[2], safe);
        Assert.Contains("KZG1.[redacted]", safe);
        Assert.EndsWith("to a player", safe);
    }

    [Fact]
    public void A_login_id_or_device_token_is_masked_in_a_url_a_json_body_or_bare()
    {
        var token = WireSecret.Generate();
        var login = WireSecret.Generate();

        var lines = new[]
        {
            $"GET /auth/start?login={login} 302",
            $"{{\"v\":2,\"type\":\"welcome\",\"deviceToken\":\"{token}\"}}",
            $"hello with {token} accepted",
            $"state={login}&code=abc123",
        };

        foreach (var line in lines)
        {
            var safe = Known().Redact(line);
            Assert.DoesNotContain(token, safe);
            Assert.DoesNotContain(login, safe);
            Assert.DoesNotContain("abc123", safe);
        }
    }

    [Fact]
    public void Oauth_parameters_and_authorization_headers_are_masked_by_name()
    {
        var safe = Known().Redact(
            "Request starting GET http://h/auth/callback?code=DiscordCode99&state=whatever&other=fine Authorization: Bearer abcdefghijklmnopqrstuvwxyz0123");

        Assert.DoesNotContain("DiscordCode99", safe);
        Assert.DoesNotContain("whatever", safe);
        Assert.DoesNotContain("abcdefghijklmnopqrstuvwxyz0123", safe);
        Assert.Contains("other=fine", safe);
        Assert.Contains("code=[redacted]", safe);
    }

    [Fact]
    public void Something_shaped_like_a_discord_bot_token_is_masked_even_when_unknown_to_Kzg()
    {
        var stranger = string.Join('.', new string('Q', 24), "Xy12Zw", new string('k', 38));

        var safe = Known().Redact($"saw {stranger} in a header");

        Assert.DoesNotContain(stranger, safe);
    }

    [Fact]
    public void A_stored_hash_shows_only_its_first_eight_digits()
    {
        var hash = new string('a', 8) + new string('b', 56);

        var safe = Known().Redact($"device {hash} revoked");

        Assert.Equal($"device {new string('a', 8)}… revoked", safe);
    }

    [Theory]
    [InlineData("listening on http://127.0.0.1:7878/")]
    [InlineData("   at Kzg.Bot.Control.ControlServer.RunAsync(WebSocket socket, CancellationToken cancellationToken)")]
    [InlineData("   at Microsoft.Extensions.DependencyInjection.ServiceLookup.CallSiteRuntimeResolver.Resolve()")]
    [InlineData("Bot is down in Attack 2, 6f1c2a4e-0000-4000-8000-000000000001 connected, code 1008")]
    [InlineData("forgot 3 device token(s) unused past their lifetime")]
    public void Ordinary_log_lines_pass_unchanged(string line)
    {
        Assert.Equal(line, Known().Redact(line));
    }

    [Fact]
    public void Nothing_and_empty_text_pass()
    {
        Assert.Equal("", Known().Redact(null));
        Assert.Equal("", Known().Redact(""));
    }

    [Fact]
    public void Redacting_twice_changes_nothing_more()
    {
        var once = Known().Redact($"{BotToken} {WireSecret.Generate()} code=zzz");

        Assert.Equal(once, Known().Redact(once));
    }

    [Fact]
    public void A_logger_writes_the_message_the_arguments_and_the_exception_redacted()
    {
        var output = new StringWriter();
        using var factory = LoggerFactory.Create(logging => logging.AddRedactingLogger(output, Known(), LogLevel.Trace));
        var log = factory.CreateLogger("Kzg.Bot.Test");
        var device = WireSecret.Generate();

        using (log.BeginScope("scope {Secret}", ClientSecret))
        {
            log.LogInformation("device {Token} signed in with {Bot}", device, BotToken);
            log.LogError(new InvalidOperationException($"the client secret {ClientSecret} was refused"), "sign-in failed");
        }

        var text = output.ToString();
        Assert.DoesNotContain(device, text);
        Assert.DoesNotContain(BotToken, text);
        Assert.DoesNotContain(ClientSecret, text);
        Assert.Contains("info Kzg.Bot.Test: device [redacted] signed in", text);
        Assert.Contains("fail Kzg.Bot.Test: sign-in failed", text);
        Assert.Contains("InvalidOperationException", text);
    }

    [Fact]
    public void The_framework_is_kept_to_warnings_unless_asked_and_what_it_does_say_is_redacted()
    {
        var quiet = new StringWriter();
        using (var factory = LoggerFactory.Create(logging => logging.AddRedactingLogger(quiet, Known(), LogLevel.Trace)))
        {
            var framework = factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics");
            framework.LogInformation("Request starting GET /auth/callback?code=SecretCode1&state=SecretState1");
            framework.LogWarning("slow request /auth/callback?code=SecretCode2");
        }
        var loud = new StringWriter();
        using (var factory = LoggerFactory.Create(logging => logging.AddRedactingLogger(loud, Known(), LogLevel.Trace, quietFramework: false)))
        {
            factory.CreateLogger("Microsoft.AspNetCore.Hosting.Diagnostics")
                .LogInformation("Request starting GET /auth/callback?code=SecretCode3&state=SecretState3");
        }

        Assert.DoesNotContain("SecretCode1", quiet.ToString());
        Assert.DoesNotContain("Request starting", quiet.ToString());
        Assert.Contains("slow request", quiet.ToString());
        Assert.DoesNotContain("SecretCode2", quiet.ToString());
        Assert.Contains("Request starting", loud.ToString());
        Assert.DoesNotContain("SecretCode3", loud.ToString());
        Assert.DoesNotContain("SecretState3", loud.ToString());
    }

    [Fact]
    public async Task Lines_from_many_threads_stay_whole()
    {
        var output = new StringWriter();
        using var factory = LoggerFactory.Create(logging => logging.AddRedactingLogger(output, Known(), LogLevel.Information));
        var log = factory.CreateLogger("Kzg.Bot.Test");

        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (var i = 0; i < 200; i++)
            {
                log.LogInformation("thread {T} line {I} {Token}", t, i, BotToken);
            }
        })));

        var lines = output.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(1600, lines.Length);
        Assert.All(lines, line => Assert.Matches(@"^\S+ info Kzg\.Bot\.Test: thread \d line \d+ \[redacted\]$", line));
    }

    [Fact]
    public void The_redactor_for_secrets_knows_every_value_the_file_and_the_environment_gave()
    {
        var secrets = Secrets.Combine(
            new Dictionary<string, string> { ["fromFile"] = "file-secret-value" },
            new Dictionary<string, string> { ["KZG_SECRET_fromEnv"] = "env-secret-value" });

        var safe = Redactor.For(secrets).Redact("file-secret-value and env-secret-value");

        Assert.Equal("[redacted] and [redacted]", safe);
    }
}
