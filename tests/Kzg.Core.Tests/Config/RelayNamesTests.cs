using Kzg.Core.Abstractions;
using Kzg.Core.Config;

namespace Kzg.Core.Tests.Config;

public sealed class RelayNamesTests
{
    private static readonly ChannelId A1 = new(101), A2 = new(102), D1 = new(201), Loose = new(300);

    private static readonly BotConfig Config = new()
    {
        Groups = new GroupsConfig
        {
            Attack = new GroupConfig { Channels = [A1, A2] },
            Defense = new GroupConfig { Channels = [D1] },
        },
        Bots =
        [
            new BotEntry { TokenRef = "relay1", ChannelId = A2, Primary = true },
            new BotEntry { TokenRef = "relay2", ChannelId = D1 },
            new BotEntry { TokenRef = "relay3" },
            new BotEntry { TokenRef = "relay4", ChannelId = Loose },
        ],
    };

    [Fact]
    public void A_bot_is_named_after_the_team_channel_it_is_in_now()
    {
        Assert.Equal("A2 Broadcaster", RelayNames.Of(Config, Config.Bots[0]));
        Assert.Equal("D1 Broadcaster", RelayNames.Of(Config, Config.Bots[1]));
        Assert.Equal("D1 Broadcaster", RelayNames.Team(Config, Config.Bots[1]));
    }

    [Fact]
    public void A_bot_without_a_team_channel_goes_by_its_token_name()
    {
        Assert.Equal("relay3 (spare)", RelayNames.Of(Config, Config.Bots[2]));
        Assert.Equal("relay4 (no team)", RelayNames.Of(Config, Config.Bots[3]));
        Assert.Null(RelayNames.Team(Config, Config.Bots[2]));
        Assert.Null(RelayNames.Team(Config, Config.Bots[3]));
    }

    [Fact]
    public void The_setup_slots_run_A1_to_A3_then_D1_to_D3()
    {
        Assert.Equal(
            ["A1 Broadcaster", "A2 Broadcaster", "A3 Broadcaster", "D1 Broadcaster", "D2 Broadcaster", "D3 Broadcaster"],
            Enumerable.Range(1, RelayNames.Slots).Select(RelayNames.ForSlot));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelayNames.ForSlot(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => RelayNames.ForSlot(7));
    }
}
