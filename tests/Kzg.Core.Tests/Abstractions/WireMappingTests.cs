using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.Core.Tests.Abstractions;

public class WireMappingTests
{
    [Theory]
    [InlineData(Group.Attack, WireGroup.Attack)]
    [InlineData(Group.Defense, WireGroup.Defense)]
    public void Groups_map_both_ways(Group core, WireGroup wire)
    {
        Assert.Equal(wire, core.ToWire());
        Assert.Equal(core, wire.ToCore());
    }

    [Theory]
    [InlineData(BroadcastAction.Group, WireAction.Group)]
    [InlineData(BroadcastAction.OpenMic, WireAction.OpenMic)]
    public void Actions_map_both_ways(BroadcastAction core, WireAction wire)
    {
        Assert.Equal(wire, core.ToWire());
        Assert.Equal(core, wire.ToCore());
    }

    [Fact]
    public void Values_outside_the_enums_are_refused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ((Group)9).ToWire());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((WireGroup)9).ToCore());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((BroadcastAction)9).ToWire());
        Assert.Throws<ArgumentOutOfRangeException>(() => ((WireAction)9).ToCore());
    }

    [Fact]
    public void Ids_go_on_the_wire_as_snowflakes_and_never_as_zero()
    {
        Assert.Equal("42", new UserId(42).ToWire());
        Assert.Equal("43", new ChannelId(43).ToWire());
        Assert.Throws<ArgumentOutOfRangeException>(() => default(UserId).ToWire());
    }

    [Fact]
    public void Every_core_enum_value_has_a_wire_form()
    {
        Assert.All(Enum.GetValues<Group>(), g => Assert.Equal(g, g.ToWire().ToCore()));
        Assert.All(Enum.GetValues<BroadcastAction>(), a => Assert.Equal(a, a.ToWire().ToCore()));
    }
}
