namespace Kzg.Protocol.Tests;

public class WireEnumsTests
{
    [Fact]
    public void Every_value_has_the_documented_wire_name()
    {
        Assert.Equal(["attack", "defense"], Enum.GetValues<WireGroup>().Select(new WireGroupConverter().NameOf));
        Assert.Equal(["group", "allTeams", "openMic"], Enum.GetValues<WireAction>().Select(new WireActionConverter().NameOf));
        Assert.Equal(["notInTeamVc", "botDown"], Enum.GetValues<StatusWarning>().Select(new StatusWarningConverter().NameOf));
        Assert.Equal(
            ["badCode", "expired", "used", "revoked", "notMember", "versionMismatch"],
            Enum.GetValues<ErrorCode>().Select(new ErrorCodeConverter().NameOf));
    }

    [Fact]
    public void A_value_outside_the_enum_has_no_wire_name()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new WireGroupConverter().NameOf((WireGroup)7));
        Assert.Throws<ArgumentException>(() => MessageCodec.Serialize(new PressMessage((WireAction)7, true, 1)));
    }

    [Fact]
    public void Writing_a_value_outside_the_enum_fails()
    {
        Assert.ThrowsAny<Exception>(() => MessageCodec.Serialize(new ErrorMessage((ErrorCode)99, "x")));
    }
}
