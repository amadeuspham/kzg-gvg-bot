namespace Kzg.Protocol.Tests;

public class MessageAssemblerTests
{
    [Fact]
    public void Fragments_join_into_one_message()
    {
        var assembler = new MessageAssembler();

        Assert.Equal(AssemblyState.Incomplete, assembler.Append("{\"v\":2,"u8, false));
        Assert.True(assembler.Message.IsEmpty);
        Assert.Equal(AssemblyState.Complete, assembler.Append("\"type\":\"unlink\"}"u8, true));

        Assert.IsType<MessageParse<ClientMessage>.Parsed>(MessageCodec.ParseClient(assembler.Message));
    }

    [Fact]
    public void A_receive_loop_reads_straight_into_the_buffer()
    {
        var assembler = new MessageAssembler();
        var json = """{"v":2,"type":"unlink"}"""u8;

        json.CopyTo(assembler.Remaining.Span);

        Assert.Equal(AssemblyState.Complete, assembler.Advance(json.Length, true));
        Assert.Equal(json.ToArray(), assembler.Message.ToArray());
        Assert.True(assembler.Remaining.IsEmpty);
    }

    [Fact]
    public void Exactly_the_cap_completes()
    {
        var assembler = new MessageAssembler();

        Assert.Equal(AssemblyState.Incomplete, assembler.Append(new byte[WireProtocol.MaxMessageBytes], false));
        Assert.Equal(AssemblyState.Complete, assembler.Append([], true));
        Assert.Equal(WireProtocol.MaxMessageBytes, assembler.Message.Length);
    }

    [Fact]
    public void One_byte_past_the_cap_is_too_large_even_split_over_fragments()
    {
        var assembler = new MessageAssembler();

        Assert.Equal(AssemblyState.Incomplete, assembler.Append(new byte[3000], false));
        Assert.Equal(AssemblyState.TooLarge, assembler.Append(new byte[1097], false));
        Assert.True(assembler.Message.IsEmpty);
        Assert.True(assembler.Remaining.IsEmpty);
    }

    [Fact]
    public void A_receive_that_fills_the_spare_byte_is_too_large()
    {
        var assembler = new MessageAssembler();

        Assert.Equal(WireProtocol.MaxMessageBytes + 1, assembler.Remaining.Length);
        Assert.Equal(AssemblyState.TooLarge, assembler.Advance(WireProtocol.MaxMessageBytes + 1, true));
    }

    [Fact]
    public void A_huge_fragment_is_cut_short_not_copied_whole()
    {
        var assembler = new MessageAssembler();

        Assert.Equal(AssemblyState.TooLarge, assembler.Append(new byte[1 << 20], true));
    }

    [Fact]
    public void Nothing_more_is_taken_until_reset()
    {
        var assembler = new MessageAssembler();
        assembler.Append("{}"u8, true);

        Assert.Throws<InvalidOperationException>(() => assembler.Append("{}"u8, true));
        Assert.Throws<InvalidOperationException>(() => assembler.Advance(0, true));

        assembler.Reset();
        Assert.Equal(AssemblyState.Complete, assembler.Append("[]"u8, true));
        Assert.Equal("[]"u8.ToArray(), assembler.Message.ToArray());
    }

    [Fact]
    public void Advance_refuses_counts_outside_the_buffer()
    {
        var assembler = new MessageAssembler();

        Assert.Throws<ArgumentOutOfRangeException>(() => assembler.Advance(-1, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => assembler.Advance(WireProtocol.MaxMessageBytes + 2, false));
    }
}
