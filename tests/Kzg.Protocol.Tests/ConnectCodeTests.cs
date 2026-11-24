using System.Buffers.Text;
using System.Text;

namespace Kzg.Protocol.Tests;

public class ConnectCodeTests
{
    private static string B64(string text) => Base64Url.EncodeToString(Encoding.UTF8.GetBytes(text));

    private static string B64(byte[] bytes) => Base64Url.EncodeToString(bytes);

    [Theory]
    [InlineData("gvg.example.com")]
    [InlineData("gvg.example.com:8443")]
    [InlineData("localhost:7878")]
    [InlineData("127.0.0.1")]
    [InlineData("[::1]:7878")]
    [InlineData("[2001:db8::1]")]
    [InlineData("xn--80ak6aa92e.com")]
    public void A_code_round_trips_with_its_host_and_secret(string host)
    {
        var code = ConnectCode.Generate(host);
        var text = code.Format();

        Assert.True(ConnectCode.TryParse(text, out var parsed));
        Assert.Equal(host, parsed.Host);
        Assert.Equal(code.Secret.ToArray(), parsed.Secret.ToArray());
        Assert.Equal(text, parsed.Format());
    }

    [Fact]
    public void A_code_spells_out_the_documented_form()
    {
        var secret = Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

        var text = ConnectCode.Create("gvg.example.com", secret).Format();

        Assert.Equal($"KZG1.{B64("gvg.example.com")}.{B64(secret)}", text);
        Assert.Equal(4 + 1 + B64("gvg.example.com").Length + 1 + WireSecret.TextLength, text.Length);
    }

    [Fact]
    public void Every_generated_secret_is_new()
    {
        Assert.NotEqual(ConnectCode.Generate("a.b").Secret.ToArray(), ConnectCode.Generate("a.b").Secret.ToArray());
    }

    [Fact]
    public void A_code_never_prints_its_secret()
    {
        var code = ConnectCode.Generate("gvg.example.com");

        Assert.DoesNotContain(code.Format().Split('.')[2], code.ToString());
        Assert.Contains("gvg.example.com", code.ToString());
    }

    public static TheoryData<string?> Malformed()
    {
        var secret = B64(Enumerable.Range(0, 32).Select(i => (byte)i).ToArray());
        var host = B64("gvg.example.com");
        return
        [
            null,
            "",
            "KZG1",
            $"KZG1.{host}",
            $"KZG1.{host}.",
            $"KZG1..{secret}",
            $"KZG2.{host}.{secret}",
            $"kzg1.{host}.{secret}",
            $"KZG1.{host}.{secret}.",
            $"KZG1.{host}.{secret}.{secret}",
            $" KZG1.{host}.{secret}",
            $"KZG1.{host}.{secret}\n",
            $"KZG1.{host}=.{secret}",
            $"KZG1.{host}.{secret}=",
            $"KZG1.{host}.{secret[..^1]}",
            $"KZG1.{host}.{secret}A",
            $"KZG1.{host}.{B64(new byte[31])}",
            $"KZG1.{host}.{B64(new byte[33])}",
            $"KZG1.{host}.{secret.Replace('-', '+').Replace('_', '/')}+",
            $"KZG1.{host.Replace("Z", "Z ")}.{secret}",
            // Base64url with nonzero unused bits decodes, but is not how the secret is spelt.
            $"KZG1.{host}.{secret[..^1]}B",
            $"KZG1.{B64("gvg.example.com/path")}.{secret}",
            $"KZG1.{B64("user@gvg.example.com")}.{secret}",
            $"KZG1.{B64("https://gvg.example.com")}.{secret}",
            $"KZG1.{B64("gvg.example.com:0")}.{secret}",
            $"KZG1.{B64("gvg.example.com:65536")}.{secret}",
            $"KZG1.{B64("gvg.example.com:")}.{secret}",
            $"KZG1.{B64("gvg..com")}.{secret}",
            $"KZG1.{B64("-gvg.com")}.{secret}",
            $"KZG1.{B64("gvg.пример.com")}.{secret}",
            $"KZG1.{B64("[::1")}.{secret}",
            $"KZG1.{B64("[127.0.0.1]")}.{secret}",
            $"KZG1.{B64("[fe80::1%eth0]")}.{secret}",
            $"KZG1.{B64("[::1]x")}.{secret}",
            $"KZG1.{B64("gvg example.com")}.{secret}",
            "KZG1." + new string('A', 600) + "." + secret,
        ];
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void A_malformed_code_is_refused(string? text)
    {
        Assert.False(ConnectCode.TryParse(text, out var code));
        Assert.Null(code);
    }

    [Fact]
    public void Create_refuses_a_bad_host_or_secret_length()
    {
        Assert.Throws<ArgumentException>(() => ConnectCode.Create("not a host", new byte[32]));
        Assert.Throws<ArgumentException>(() => ConnectCode.Create("gvg.example.com", new byte[16]));
    }

    [Theory]
    [InlineData("a", true)]
    [InlineData("a.b-c.d", true)]
    [InlineData("gvg.example.com:1", true)]
    [InlineData("gvg.example.com:65535", true)]
    [InlineData("gvg.example.com:01", false)]
    [InlineData("gvg.example.com:+1", false)]
    [InlineData("gvg.example.com:1:2", false)]
    [InlineData("a-.b", false)]
    [InlineData(null, false)]
    public void Host_rules(string? host, bool valid)
    {
        Assert.Equal(valid, ConnectCode.IsValidHost(host));
    }

    [Fact]
    public void Host_length_limits()
    {
        var label = new string('a', 63);
        Assert.True(ConnectCode.IsValidHost(label));
        Assert.False(ConnectCode.IsValidHost(new string('a', 64)));
        Assert.False(ConnectCode.IsValidHost(string.Join('.', Enumerable.Repeat(label, 4))));
    }
}
