using Kzg.Bot.Engine;
using Kzg.Bot.Fake;
using Kzg.Core.Abstractions;
using Kzg.Core.Routing;
using Kzg.Protocol;

namespace Kzg.Bot.Tests.Engine;

public class StatusMapperTests
{
    private static readonly RoutingConfig Config = RoutingConfig.From(DemoWorld.Create(new SystemTimeSource(), "gvg.example.com").Config);
    private static readonly IChannelNames Names = new TableChannelNames(new Dictionary<ChannelId, string>
    {
        [DemoWorld.Attack2] = "Attack 2",
        [DemoWorld.Attack3] = "Attack 2",
    });

    private static UserStatus Status(Group? group) =>
        new(DemoWorld.Alice, DemoWorld.Attack1, group, [], false, [], [StatusWarning.BotDown]);

    [Fact]
    public void BotDownIn_lists_the_group_VCs_that_are_not_ready_in_config_order()
    {
        var ready = new HashSet<ChannelId> { DemoWorld.Attack1 };

        Assert.Equal([DemoWorld.Attack2, DemoWorld.Attack3], StatusMapper.BotDownIn(Group.Attack, Config, ready));
        Assert.Equal([DemoWorld.Defense1, DemoWorld.Defense2, DemoWorld.Defense3], StatusMapper.BotDownIn(Group.Defense, Config, ready));
        Assert.Empty(StatusMapper.BotDownIn(null, Config, ready));
        Assert.Empty(StatusMapper.BotDownIn(Group.Attack, Config, new HashSet<ChannelId> { DemoWorld.Attack1, DemoWorld.Attack2, DemoWorld.Attack3 }));
    }

    [Fact]
    public void The_message_carries_the_names_once_even_when_two_VCs_share_one()
    {
        var message = StatusMapper.ToMessage(Status(Group.Attack), Names, [DemoWorld.Attack2, DemoWorld.Attack3]);

        Assert.Equal(["Attack 2"], message.BotDownIn);
    }

    [Fact]
    public void Without_a_list_the_message_says_none_are_down()
    {
        Assert.Equal([], StatusMapper.ToMessage(Status(Group.Attack), Names).BotDownIn);
    }
}
