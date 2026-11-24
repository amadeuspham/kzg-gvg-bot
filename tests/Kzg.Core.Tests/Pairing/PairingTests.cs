using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Core.Tests.Config;
using Kzg.Protocol;

namespace Kzg.Core.Tests.Pairing;

/// <summary>A wall clock that moves only when a test sets or advances it.</summary>
internal sealed class FakeWallClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 11, 23, 20, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}

public class SecretHashTests
{
    [Fact]
    public void A_secret_is_kept_as_its_sha256_in_lower_case_hex()
    {
        var hash = SecretHash.Of(new byte[32]);

        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", hash);
        Assert.Equal(hash, SecretHash.OfWire(WireSecret.Encode(new byte[32])));
        Assert.True(DevicesStore.IsValidTokenHash(hash));
        Assert.Null(SecretHash.OfWire("not a secret"));
        Assert.Equal("66687aad", SecretHash.Prefix(hash));
        Assert.True(SecretHash.Same(hash, SecretHash.Of(new byte[32])));
        Assert.False(SecretHash.Same(hash, SecretHash.Of(new byte[31])));
    }
}

public class ConnectCodesTests
{
    private static readonly UserId Bob = new(2);
    private readonly FakeTimeSource _time = new();
    private readonly ConnectCodes _codes;

    public ConnectCodesTests() => _codes = new ConnectCodes(_time);

    private ConnectCode Issue()
    {
        Assert.True(ConnectCode.TryParse(_codes.Issue(Bob, "gvg.example.com"), out var code));
        return code;
    }

    [Fact]
    public void A_code_carries_the_host_and_works_once()
    {
        var code = Issue();

        Assert.Equal("gvg.example.com", code.Host);
        Assert.Equal(new CodeRedemption.Redeemed(Bob), _codes.Redeem(code));
        Assert.IsType<CodeRedemption.Used>(_codes.Redeem(code));
    }

    [Fact]
    public void A_code_expires_after_ten_minutes()
    {
        var code = Issue();
        _time.Advance(ConnectCodes.Lifetime - TimeSpan.FromTicks(1));
        var fresh = Issue();
        _time.Advance(TimeSpan.FromTicks(1));

        Assert.IsType<CodeRedemption.Expired>(_codes.Redeem(code));
        Assert.IsType<CodeRedemption.Redeemed>(_codes.Redeem(fresh));
    }

    [Fact]
    public void A_code_never_issued_is_unknown_and_old_ones_are_forgotten()
    {
        Assert.IsType<CodeRedemption.Unknown>(_codes.Redeem(ConnectCode.Generate("gvg.example.com")));

        var code = Issue();
        _codes.Redeem(code);
        _time.Advance(2 * ConnectCodes.Lifetime);

        Assert.IsType<CodeRedemption.Unknown>(_codes.Redeem(code));
        Assert.Equal(0, _codes.Count);
    }
}

public class PendingLoginsTests
{
    private static readonly GuildMember Bob = new(new UserId(2), "Bob");
    private readonly FakeTimeSource _time = new();
    private readonly PendingLogins _logins;
    private readonly string _login = SecretHash.Of(new byte[32]);

    public PendingLoginsTests() => _logins = new PendingLogins(_time);

    [Fact]
    public async Task The_app_waits_until_the_callback_completes_the_sign_in()
    {
        var waiting = Assert.IsType<LoginClaim.Waiting>(_logins.Claim(_login));
        Assert.Equal(PendingLogins.Lifetime, waiting.Remaining);
        Assert.Equal(CallbackClaim.Granted, _logins.CanStart(_login));
        Assert.False(waiting.Outcome.IsCompleted);

        Assert.Equal(CallbackClaim.Granted, _logins.BeginCallback(_login));
        _logins.Complete(_login, new SignInOutcome.Member(Bob));

        Assert.Equal(new SignInOutcome.Member(Bob), await waiting.Outcome);
        Assert.Equal(CallbackClaim.Used, _logins.CanStart(_login));
        Assert.Equal(CallbackClaim.Used, _logins.BeginCallback(_login));
    }

    [Fact]
    public void A_start_or_callback_for_a_login_no_app_began_is_refused()
    {
        Assert.Equal(CallbackClaim.Unknown, _logins.CanStart(_login));
        Assert.Equal(CallbackClaim.Unknown, _logins.BeginCallback(_login));
    }

    [Fact]
    public async Task Only_one_connection_waits_and_a_delivered_login_is_spent()
    {
        _logins.Claim(_login);
        Assert.IsType<LoginClaim.Used>(_logins.Claim(_login));

        _logins.Abandon(_login);
        var again = Assert.IsType<LoginClaim.Waiting>(_logins.Claim(_login));
        _logins.BeginCallback(_login);
        _logins.Complete(_login, new SignInOutcome.NotMember());
        _logins.Deliver(_login);
        _logins.Abandon(_login);

        Assert.IsType<SignInOutcome.NotMember>(await again.Outcome);
        Assert.IsType<LoginClaim.Used>(_logins.Claim(_login));
    }

    [Fact]
    public void An_outcome_that_comes_while_the_app_is_away_waits_for_its_reconnect()
    {
        _logins.Claim(_login);
        _logins.Abandon(_login);
        _logins.BeginCallback(_login);
        _logins.Complete(_login, new SignInOutcome.Member(Bob));

        var reconnect = Assert.IsType<LoginClaim.Waiting>(_logins.Claim(_login));

        Assert.True(reconnect.Outcome.IsCompletedSuccessfully);
    }

    [Fact]
    public void One_callback_exchanges_at_a_time_and_a_failed_one_lets_the_next_try()
    {
        _logins.Claim(_login);
        Assert.Equal(CallbackClaim.Granted, _logins.BeginCallback(_login));
        Assert.Equal(CallbackClaim.Used, _logins.BeginCallback(_login));

        _logins.Release(_login);

        Assert.Equal(CallbackClaim.Granted, _logins.BeginCallback(_login));
    }

    [Fact]
    public void A_complete_without_a_callback_in_progress_changes_nothing()
    {
        var waiting = Assert.IsType<LoginClaim.Waiting>(_logins.Claim(_login));

        _logins.Complete(_login, new SignInOutcome.Member(Bob));
        _logins.Complete("unknown", new SignInOutcome.Member(Bob));
        _logins.Release("unknown");
        _logins.Abandon("unknown");
        _logins.Deliver("unknown");

        Assert.False(waiting.Outcome.IsCompleted);
    }

    [Fact]
    public void A_login_expires_after_ten_minutes_and_is_forgotten_after_ten_more()
    {
        var waiting = Assert.IsType<LoginClaim.Waiting>(_logins.Claim(_login));
        _time.Advance(PendingLogins.Lifetime);

        Assert.Equal(CallbackClaim.Expired, _logins.CanStart(_login));
        _logins.Abandon(_login);
        Assert.IsType<LoginClaim.Expired>(_logins.Claim(_login));

        _time.Advance(PendingLogins.Lifetime);
        Assert.Equal(CallbackClaim.Unknown, _logins.CanStart(_login));
        Assert.True(waiting.Outcome.IsCanceled);
    }
}

public sealed class DeviceRegistryTests : IDisposable
{
    private static readonly UserId Bob = new(2), Carol = new(3);
    private static readonly Guid Laptop = Guid.Parse("6f1c2a4e-0000-4000-8000-000000000001");
    private static readonly Guid Desktop = Guid.Parse("6f1c2a4e-0000-4000-8000-000000000002");
    private static readonly TimeSpan Days90 = TimeSpan.FromDays(90);

    private readonly TempFolder _folder = new();
    private readonly FakeWallClock _clock = new();
    private readonly DevicesStore _store;
    private readonly DeviceRegistry _registry;

    public DeviceRegistryTests()
    {
        _store = new DevicesStore(_folder.File("devices.json"));
        _registry = DeviceRegistry.Over(_store, [], _clock, Days90);
    }

    public void Dispose() => _folder.Dispose();

    private DeviceRegistry Restarted() =>
        DeviceRegistry.Over(_store, Assert.IsType<FileLoad<IReadOnlyList<DeviceRecord>>.Loaded>(_store.Load()).Value, _clock, Days90);

    [Fact]
    public void An_issued_token_signs_its_player_in_and_survives_a_restart()
    {
        var token = _registry.Issue(Bob, Laptop);

        Assert.True(WireSecret.IsValid(token));
        Assert.DoesNotContain(token, File.ReadAllText(_store.FilePath), StringComparison.Ordinal);
        var device = Assert.IsType<DeviceAuth.Valid>(Restarted().Authenticate(token)).Device;
        Assert.Equal((Bob, Laptop), (device.UserId, device.InstallId));
        Assert.False(_registry.Unsaved);
    }

    [Fact]
    public void A_token_lasts_while_used_and_expires_after_ninety_idle_days()
    {
        var token = _registry.Issue(Bob, Laptop);
        _clock.Advance(Days90 - TimeSpan.FromSeconds(1));
        Assert.IsType<DeviceAuth.Valid>(_registry.Authenticate(token));

        _clock.Advance(Days90 - TimeSpan.FromSeconds(1));
        Assert.IsType<DeviceAuth.Valid>(Restarted().Authenticate(token));

        _clock.Advance(Days90);
        Assert.IsType<DeviceAuth.Expired>(Restarted().Authenticate(token));
        Assert.IsType<DeviceAuth.Unknown>(Restarted().Authenticate(token));
    }

    [Fact]
    public void Revoking_a_player_forgets_every_token_of_theirs_only()
    {
        var laptop = _registry.Issue(Bob, Laptop);
        var desktop = _registry.Issue(Bob, Desktop);
        var carol = _registry.Issue(Carol, Guid.NewGuid());

        Assert.Equal(2, _registry.RevokeUser(Bob));
        Assert.Equal(0, _registry.RevokeUser(Bob));

        Assert.IsType<DeviceAuth.Unknown>(Restarted().Authenticate(laptop));
        Assert.IsType<DeviceAuth.Unknown>(_registry.Authenticate(desktop));
        Assert.IsType<DeviceAuth.Valid>(_registry.Authenticate(carol));
    }

    [Fact]
    public void Unlinking_forgets_one_token()
    {
        var token = _registry.Issue(Bob, Laptop);

        Assert.True(_registry.Revoke(SecretHash.OfWire(token)!));
        Assert.False(_registry.Revoke(SecretHash.OfWire(token)!));
        Assert.IsType<DeviceAuth.Unknown>(_registry.Authenticate(token));
    }

    [Fact]
    public void Relinking_an_install_replaces_its_old_token()
    {
        var old = _registry.Issue(Bob, Laptop);
        var fresh = _registry.Issue(Bob, Laptop);

        Assert.Equal(1, _registry.Count);
        Assert.IsType<DeviceAuth.Unknown>(_registry.Authenticate(old));
        Assert.IsType<DeviceAuth.Valid>(_registry.Authenticate(fresh));
    }

    [Fact]
    public void A_malformed_token_is_unknown()
    {
        Assert.IsType<DeviceAuth.Unknown>(_registry.Authenticate("not-a-token"));
    }

    [Fact]
    public void A_sweep_forgets_idle_tokens()
    {
        _registry.Issue(Bob, Laptop);
        _clock.Advance(TimeSpan.FromDays(60));
        var kept = _registry.Issue(Carol, Desktop);
        _clock.Advance(TimeSpan.FromDays(31));

        Assert.Equal(1, _registry.Sweep());
        Assert.Equal(0, _registry.Sweep());
        Assert.Equal(Carol, Assert.Single(_registry.Snapshot()).UserId);
        Assert.IsType<DeviceAuth.Valid>(_registry.Authenticate(kept));
    }

    [Fact]
    public void A_clock_set_back_never_moves_last_seen_back()
    {
        var token = _registry.Issue(Bob, Laptop);
        var issued = _clock.Now;
        _clock.Advance(TimeSpan.FromHours(-1));

        Assert.Equal(issued, Assert.IsType<DeviceAuth.Valid>(_registry.Authenticate(token)).Device.LastSeenAt);
    }

    [Fact]
    public void A_refused_write_keeps_tokens_working_and_says_so_until_a_write_succeeds()
    {
        var accept = false;
        var registry = new DeviceRegistry([], _ => accept, _clock, Days90);

        var token = registry.Issue(Bob, Laptop);
        Assert.True(registry.Unsaved);
        Assert.IsType<DeviceAuth.Valid>(registry.Authenticate(token));

        accept = true;
        registry.Authenticate(token);
        Assert.False(registry.Unsaved);
        Assert.Equal(Days90, registry.IdleLifetime);
        Assert.Throws<ArgumentOutOfRangeException>(() => new DeviceRegistry([], _ => true, _clock, TimeSpan.Zero));
    }
}

public class DiscordAuthorizeTests
{
    [Fact]
    public void Sign_in_asks_for_identify_only_with_the_login_as_state_and_returns_to_the_bot()
    {
        var state = WireSecret.Encode(new byte[32]);

        var url = DiscordAuthorize.Url(333333333333333333, "gvg.example.com:8443", state);

        Assert.Equal("https://discord.com/oauth2/authorize", url.GetLeftPart(UriPartial.Path));
        Assert.Contains("client_id=333333333333333333", url.Query, StringComparison.Ordinal);
        Assert.Contains("scope=identify&", url.Query, StringComparison.Ordinal);
        Assert.Contains("response_type=code", url.Query, StringComparison.Ordinal);
        Assert.Contains("state=" + state, url.Query, StringComparison.Ordinal);
        Assert.Contains("redirect_uri=https%3A%2F%2Fgvg.example.com%3A8443%2Fauth%2Fcallback", url.Query, StringComparison.Ordinal);
        Assert.Equal(new Uri("https://gvg.example.com:8443/auth/callback"), DiscordAuthorize.RedirectUri("gvg.example.com:8443"));
    }
}
