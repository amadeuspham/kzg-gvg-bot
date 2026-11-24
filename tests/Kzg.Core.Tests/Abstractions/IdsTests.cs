using Kzg.Core.Abstractions;

namespace Kzg.Core.Tests.Abstractions;

public class IdsTests
{
    [Fact]
    public void Ids_parse_from_snowflakes_and_print_as_them()
    {
        Assert.True(UserId.TryParse("123456789012345678", out var user));
        Assert.Equal(new UserId(123456789012345678), user);
        Assert.Equal("123456789012345678", user.ToString());

        Assert.True(ChannelId.TryParse("18446744073709551615", out var channel));
        Assert.Equal(new ChannelId(ulong.MaxValue), channel);
        Assert.Equal("18446744073709551615", channel.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("012")]
    [InlineData("-1")]
    [InlineData("1 ")]
    public void Ids_refuse_anything_but_a_canonical_snowflake(string? text)
    {
        Assert.False(UserId.TryParse(text, out _));
        Assert.False(ChannelId.TryParse(text, out _));
    }

    [Fact]
    public void A_user_source_names_its_user_and_gives_it_back()
    {
        var source = SourceId.ForUser(new UserId(42));

        Assert.Equal("user:42", source.V);
        Assert.Equal("user:42", source.ToString());
        Assert.True(source.TryGetUser(out var user));
        Assert.Equal(new UserId(42), user);
    }

    [Fact]
    public void A_synthetic_source_has_no_user()
    {
        var source = SourceId.ForTts("jungle");

        Assert.Equal("tts:jungle", source.V);
        Assert.False(source.TryGetUser(out _));
        Assert.False(new SourceId("user:zero").TryGetUser(out _));
        Assert.False(default(SourceId).TryGetUser(out _));
    }

    [Fact]
    public void Sources_need_a_real_user_or_a_name()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => SourceId.ForUser(default));
        Assert.Throws<ArgumentException>(() => SourceId.ForTts(" "));
    }

    [Fact]
    public void Sources_are_equal_by_value_so_they_can_key_sessions()
    {
        var sessions = new HashSet<(SourceId, BroadcastAction)>
        {
            (SourceId.ForUser(new UserId(7)), BroadcastAction.Group),
        };

        Assert.Contains((SourceId.ForUser(new UserId(7)), BroadcastAction.Group), sessions);
        Assert.DoesNotContain((SourceId.ForUser(new UserId(7)), BroadcastAction.OpenMic), sessions);
    }
}
