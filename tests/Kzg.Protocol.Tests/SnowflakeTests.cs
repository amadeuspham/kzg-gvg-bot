namespace Kzg.Protocol.Tests;

public class SnowflakeTests
{
    [Theory]
    [InlineData("1", 1UL)]
    [InlineData("123456789012345678", 123456789012345678UL)]
    [InlineData("18446744073709551615", ulong.MaxValue)]
    public void A_canonical_id_parses_and_formats_back(string text, ulong id)
    {
        Assert.True(Snowflake.TryParse(text, out var parsed));
        Assert.Equal(id, parsed);
        Assert.Equal(text, Snowflake.Format(id));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("01")]
    [InlineData("+1")]
    [InlineData("-1")]
    [InlineData(" 1")]
    [InlineData("1 ")]
    [InlineData("1.0")]
    [InlineData("1e3")]
    [InlineData("١٢")]
    [InlineData("18446744073709551616")]
    [InlineData("123456789012345678901")]
    public void Anything_else_is_refused(string? text)
    {
        Assert.False(Snowflake.IsValid(text));
    }

    [Fact]
    public void Zero_is_never_written()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Snowflake.Format(0));
    }
}
