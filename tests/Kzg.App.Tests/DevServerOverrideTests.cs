using Kzg.App.Link;

namespace Kzg.App.Tests;

public class DevServerOverrideTests
{
    [Theory]
    [InlineData("ws://127.0.0.1:7878", "ws://127.0.0.1:7878/ws")]
    [InlineData("ws://127.0.0.1:7878/", "ws://127.0.0.1:7878/ws")]
    [InlineData("ws://localhost:9000", "ws://localhost:9000/ws")]
    [InlineData("WS://LOCALHOST:9000", "ws://localhost:9000/ws")]
    [InlineData("ws://[::1]:7878", "ws://[::1]:7878/ws")]
    [InlineData("wss://127.0.0.1:7443", "wss://127.0.0.1:7443/ws")]
    public void A_loopback_address_is_accepted_and_pointed_at_ws(string text, string expected)
    {
        Assert.True(DevServerOverride.TryParse(text, out var address));

        Assert.Equal(new Uri(expected), address);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("127.0.0.1:7878")]
    [InlineData("http://127.0.0.1:7878")]
    [InlineData("ws://gvg.example.com")]
    [InlineData("ws://192.168.1.20:7878")]
    [InlineData("ws://0.0.0.0:7878")]
    [InlineData("ws://127.0.0.1:7878/other")]
    [InlineData("ws://127.0.0.1:7878/?x=1")]
    [InlineData("ws://user@127.0.0.1:7878")]
    [InlineData("ws://localhost.evil.example")]
    public void Anything_else_is_refused_so_a_token_never_travels_in_plain_text_off_this_machine(string? text)
    {
        Assert.False(DevServerOverride.TryParse(text, out _));
    }
}
