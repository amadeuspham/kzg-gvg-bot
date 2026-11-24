using Kzg.Discord.Commands;

namespace Kzg.Discord.Tests;

public sealed class InteractionBridgeTests
{
    private static OptionNode Sub(string name, params OptionNode[] children) => new(name, true, null, children);

    private static OptionNode Value(string name, string value) => new(name, false, value, []);

    [Fact]
    public void A_nested_sub_command_gives_its_path_and_its_values()
    {
        var (path, options) = InteractionBridge.Flatten([Sub("bot", Sub("assign", Value("bot", "relay2"), Value("channel", "102")))]);

        Assert.Equal(["bot", "assign"], path);
        Assert.Equal("relay2", options["bot"]);
        Assert.Equal("102", options["channel"]);
    }

    [Fact]
    public void A_command_with_values_and_no_sub_command_has_an_empty_path()
    {
        var (path, options) = InteractionBridge.Flatten([Value("x", "1")]);

        Assert.Empty(path);
        Assert.Equal("1", options["x"]);
    }

    [Fact]
    public void A_command_with_nothing_has_nothing()
    {
        var (path, options) = InteractionBridge.Flatten([]);

        Assert.Empty(path);
        Assert.Empty(options);
    }

    [Fact]
    public void A_value_option_without_a_value_is_left_out()
    {
        var (_, options) = InteractionBridge.Flatten([Sub("config", Sub("set", new OptionNode("key", false, null, []), Value("value", "9")))]);

        Assert.Equal(["value"], options.Keys);
    }
}
