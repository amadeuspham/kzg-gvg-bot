using Kzg.Core.Abstractions;
using Kzg.Core.Pairing;
using Kzg.Protocol;

namespace Kzg.Bot.Control;

/// <summary>How a hello ended.</summary>
internal abstract record SignInResult
{
    private SignInResult()
    {
    }

    /// <param name="TokenHash">The device token this connection is signed in with, for <c>unlink</c>.</param>
    /// <param name="NewToken">The token to hand over in the welcome when this hello issued one.</param>
    public sealed record Accepted(GuildMember Member, string TokenHash, string? NewToken) : SignInResult;

    public sealed record Refused(ErrorMessage Error) : SignInResult;
}

/// <summary>Checks a hello's one credential against pairing, then that its player is in the guild.</summary>
internal static class SignIn
{
    public static Task<SignInResult> RunAsync(ControlServer server, HelloMessage hello, Action<string> waitingOnLogin, CancellationToken cancellationToken) =>
        (hello.Code, hello.DeviceToken, hello.LoginId) switch
        {
            ({ } code, null, null) => WithCodeAsync(server, hello, code, cancellationToken),
            (null, { } token, null) => WithTokenAsync(server, token, cancellationToken),
            (null, null, { } login) => WithLoginAsync(server, hello, login, waitingOnLogin, cancellationToken),
            // The codec refuses a hello without exactly one credential before it gets here.
            _ => throw new ArgumentException("a hello carries exactly one credential", nameof(hello)),
        };

    private static async Task<SignInResult> WithCodeAsync(ControlServer server, HelloMessage hello, string text, CancellationToken cancellationToken)
    {
        if (!ConnectCode.TryParse(text, out var code))
        {
            return Refuse(ErrorCode.BadCode, "That code isn't a connect code. Run /connect in Discord for a new one.");
        }
        switch (server.Codes.Redeem(code))
        {
            case CodeRedemption.Redeemed redeemed:
                var member = await server.Discord.GetMemberAsync(redeemed.User, cancellationToken).ConfigureAwait(false);
                return member is null ? NotMember() : Issue(server, member, hello.InstallId);
            case CodeRedemption.Expired:
                return Refuse(ErrorCode.Expired, "That code has expired. Run /connect in Discord for a new one.");
            case CodeRedemption.Used:
                return Refuse(ErrorCode.Used, "That code was already used. Run /connect in Discord for a new one.");
            default:
                return Refuse(ErrorCode.BadCode, "That code isn't known. Run /connect in Discord for a new one.");
        }
    }

    private static async Task<SignInResult> WithTokenAsync(ControlServer server, string token, CancellationToken cancellationToken)
    {
        switch (server.Devices.Authenticate(token))
        {
            case DeviceAuth.Valid valid:
                // Leaving the guild ends a link as surely as a revoke.
                var member = await server.Discord.GetMemberAsync(valid.Device.UserId, cancellationToken).ConfigureAwait(false);
                return member is null ? NotMember() : new SignInResult.Accepted(member, valid.Device.TokenHash, null);
            case DeviceAuth.Expired:
                return Refuse(ErrorCode.Expired, "This link expired after going unused. Run /connect or sign in again.");
            default:
                return Refuse(ErrorCode.Revoked, "This app isn't linked any more. Run /connect or sign in again.");
        }
    }

    private static async Task<SignInResult> WithLoginAsync(
        ControlServer server, HelloMessage hello, string loginId, Action<string> waitingOnLogin, CancellationToken cancellationToken)
    {
        var hash = SecretHash.OfWire(loginId)!;
        switch (server.Logins.Claim(hash))
        {
            case LoginClaim.Waiting waiting:
                waitingOnLogin(hash);
                SignInOutcome outcome;
                try
                {
                    outcome = await waiting.Outcome.WaitAsync(waiting.Remaining, server.Timers, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is TimeoutException || (exception is TaskCanceledException && !cancellationToken.IsCancellationRequested))
                {
                    return Refuse(ErrorCode.Expired, "Sign-in took too long. Try again.");
                }
                server.Logins.Deliver(hash);
                return outcome is SignInOutcome.Member member ? Issue(server, member.Who, hello.InstallId) : NotMember();
            case LoginClaim.Used:
                return Refuse(ErrorCode.Used, "That sign-in was already used. Try again.");
            default:
                return Refuse(ErrorCode.Expired, "Sign-in took too long. Try again.");
        }
    }

    private static SignInResult.Accepted Issue(ControlServer server, GuildMember member, Guid installId)
    {
        var token = server.Devices.Issue(member.User, installId);
        return new SignInResult.Accepted(member, SecretHash.OfWire(token)!, token);
    }

    private static SignInResult.Refused NotMember() =>
        Refuse(ErrorCode.NotMember, "That Discord account isn't a member of the guild's server.");

    private static SignInResult.Refused Refuse(ErrorCode code, string message) => new(new ErrorMessage(code, message));

    /// <summary>A name the welcome can carry: Discord's, or a stand-in when it breaks the wire's rules.</summary>
    public static string DisplayName(GuildMember member)
    {
        var name = member.DisplayName.Trim();
        return name.Length is > 0 and <= 100 && !name.Any(char.IsControl) ? name : "Player " + member.User;
    }
}
