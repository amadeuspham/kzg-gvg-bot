using System.Buffers.Text;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace Kzg.Protocol;

/// <summary>
/// The code <c>/connect</c> hands a player: <c>KZG1.&lt;base64url(host)&gt;.&lt;base64url(32 bytes)&gt;</c>.
/// It carries the server address, so pasting it is the whole link step. Parsing checks the form only;
/// whether the secret is known, unexpired and unused is the bot's call.
/// </summary>
public sealed class ConnectCode
{
    public const string Prefix = "KZG1";

    // Prefix, two dots, a 253-character name with a port, and the 43-character secret, with room to spare.
    private const int MaxTextLength = 512;

    private readonly byte[] _secret;

    private ConnectCode(string host, byte[] secret)
    {
        Host = host;
        _secret = secret;
    }

    /// <summary>The server's public address: a DNS name, IPv4 or bracketed IPv6, with an optional port.</summary>
    public string Host { get; }

    public ReadOnlySpan<byte> Secret => _secret;

    public static ConnectCode Create(string host, ReadOnlySpan<byte> secret)
    {
        if (!IsValidHost(host))
        {
            throw new ArgumentException("not a host name, IPv4 or bracketed IPv6, with an optional port", nameof(host));
        }
        if (secret.Length != WireSecret.ByteLength)
        {
            throw new ArgumentException($"a secret is {WireSecret.ByteLength} bytes", nameof(secret));
        }
        return new ConnectCode(host, secret.ToArray());
    }

    /// <summary>A new code for <paramref name="host"/> with a fresh random secret.</summary>
    public static ConnectCode Generate(string host)
    {
        Span<byte> secret = stackalloc byte[WireSecret.ByteLength];
        RandomNumberGenerator.Fill(secret);
        return Create(host, secret);
    }

    /// <summary>Accepts only the exact form <see cref="Format"/> writes; the app trims a pasted code first.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out ConnectCode? code)
    {
        code = null;
        if (text is null || text.Length > MaxTextLength)
        {
            return false;
        }
        var parts = text.Split('.');
        if (parts.Length != 3 || parts[0] != Prefix || !TryDecodeHost(parts[1], out var host)
            || !WireSecret.TryDecode(parts[2], out var secret))
        {
            return false;
        }
        code = new ConnectCode(host, secret);
        return true;
    }

    public string Format() =>
        $"{Prefix}.{Base64Url.EncodeToString(Encoding.ASCII.GetBytes(Host))}.{WireSecret.Encode(_secret)}";

    /// <summary>Never the secret, so a code that reaches a log reveals nothing.</summary>
    public override string ToString() => $"{Prefix} code for {Host}";

    private static bool TryDecodeHost(string text, [NotNullWhen(true)] out string? host)
    {
        host = null;
        var bytes = new byte[Base64Url.GetMaxDecodedLength(text.Length)];
        if (text.Length == 0 || !WireSecret.TryDecodeCanonical(text, bytes, out var written))
        {
            return false;
        }
        var decoded = bytes.AsSpan(0, written);
        foreach (var b in decoded)
        {
            if (b is < 0x21 or > 0x7E)
            {
                return false;
            }
        }
        var candidate = Encoding.ASCII.GetString(decoded);
        if (!IsValidHost(candidate))
        {
            return false;
        }
        host = candidate;
        return true;
    }

    /// <summary>A DNS name (IDNs in punycode), IPv4 or <c>[IPv6]</c>, then optionally <c>:port</c>.</summary>
    public static bool IsValidHost(string? text)
    {
        if (string.IsNullOrEmpty(text) || text.Length > 261)
        {
            return false;
        }
        string name;
        string? port = null;
        if (text[0] == '[')
        {
            var close = text.IndexOf(']');
            if (close < 0)
            {
                return false;
            }
            var rest = text[(close + 1)..];
            if (rest.Length > 0)
            {
                if (rest[0] != ':')
                {
                    return false;
                }
                port = rest[1..];
            }
            return IPAddress.TryParse(text[1..close], out var address)
                && address.AddressFamily == AddressFamily.InterNetworkV6
                && !text[1..close].Contains('%')
                && (port is null || IsValidPort(port));
        }
        var colon = text.IndexOf(':');
        if (colon >= 0)
        {
            name = text[..colon];
            port = text[(colon + 1)..];
        }
        else
        {
            name = text;
        }
        return IsDnsName(name) && (port is null || IsValidPort(port));
    }

    private static bool IsDnsName(string name)
    {
        if (name.Length is 0 or > 253)
        {
            return false;
        }
        foreach (var label in name.Split('.'))
        {
            if (label.Length is 0 or > 63 || label[0] == '-' || label[^1] == '-')
            {
                return false;
            }
            foreach (var c in label)
            {
                if (!(c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-'))
                {
                    return false;
                }
            }
        }
        return true;
    }

    private static bool IsValidPort(string port) =>
        port.Length is > 0 and <= 5 && port[0] != '0' && port.All(char.IsAsciiDigit)
        && int.Parse(port, CultureInfo.InvariantCulture) <= 65535;
}
