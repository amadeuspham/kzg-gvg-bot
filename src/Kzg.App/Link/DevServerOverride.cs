using System.Diagnostics.CodeAnalysis;
using System.Net;

namespace Kzg.App.Link;

/// <summary>
/// The dev-only <c>--server</c> option: connect to a bot on this machine without TLS, such as the fake bot
/// at <c>ws://127.0.0.1:7878</c>. Only loopback addresses are accepted, so the option can never send a
/// device token over plain text to another machine. Normal use is <c>wss://</c> to the linked host.
/// </summary>
public static class DevServerOverride
{
    /// <summary>The <c>/ws</c> address for <paramref name="text"/>, such as <c>ws://127.0.0.1:7878</c>; false for anything else.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Uri? address)
    {
        address = null;
        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("ws" or "wss")
            || uri.AbsolutePath != "/"
            || uri.Query.Length > 0
            || uri.UserInfo.Length > 0
            || !IsLoopback(uri.Host))
        {
            return false;
        }
        address = new UriBuilder(uri) { Path = "/ws" }.Uri;
        return true;
    }

    private static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
        || (IPAddress.TryParse(host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip));
}
