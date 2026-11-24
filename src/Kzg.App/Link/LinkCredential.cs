using Kzg.Protocol;

namespace Kzg.App.Link;

/// <summary>What the app proves itself with in <c>hello</c>. Printing one never shows its secret.</summary>
public abstract record LinkCredential
{
    private LinkCredential()
    {
    }

    /// <summary>The whole pasted connect code. It works once, so a dropped attempt is never retried with it.</summary>
    public sealed record Code(string Text) : LinkCredential
    {
        public override string ToString() => "connect code";
    }

    /// <summary>The long-lived token the bot issued at the end of linking.</summary>
    public sealed record DeviceToken(string Value) : LinkCredential
    {
        public override string ToString() => "device token";
    }

    /// <summary>The random id of a Discord sign-in in progress in the browser.</summary>
    public sealed record LoginId(string Value) : LinkCredential
    {
        public override string ToString() => "login id";
    }

    public HelloMessage ToHello(string appVersion, Guid installId) => this switch
    {
        Code c => new HelloMessage(appVersion, installId, Code: c.Text),
        DeviceToken t => new HelloMessage(appVersion, installId, DeviceToken: t.Value),
        LoginId l => new HelloMessage(appVersion, installId, LoginId: l.Value),
        _ => throw new InvalidOperationException(),
    };
}
