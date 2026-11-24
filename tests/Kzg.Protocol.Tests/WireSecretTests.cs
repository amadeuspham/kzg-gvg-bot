namespace Kzg.Protocol.Tests;

public class WireSecretTests
{
    [Fact]
    public void A_generated_secret_is_43_base64url_characters_of_32_bytes()
    {
        var text = WireSecret.Generate();

        Assert.Equal(WireSecret.TextLength, text.Length);
        Assert.True(WireSecret.TryDecode(text, out var bytes));
        Assert.Equal(WireSecret.ByteLength, bytes.Length);
        Assert.Equal(text, WireSecret.Encode(bytes));
        Assert.NotEqual(text, WireSecret.Generate());
    }

    public static TheoryData<string?> NonCanonical()
    {
        var a42 = new string('A', 42);
        return
        [
            null,
            "",
            a42,
            a42 + "AA",
            a42 + "=",
            a42 + "+",
            a42 + "/",
            new string('A', 21) + " " + new string('A', 21),
            // The last character's two unused bits are set: it decodes, but is not the secret's spelling.
            a42 + "B",
        ];
    }

    [Theory]
    [MemberData(nameof(NonCanonical))]
    public void Anything_but_the_canonical_spelling_is_refused(string? text)
    {
        Assert.False(WireSecret.IsValid(text));
        Assert.False(WireSecret.TryDecode(text, out var bytes));
        Assert.Empty(bytes);
    }

    [Fact]
    public void The_all_zero_secret_has_one_spelling()
    {
        Assert.True(WireSecret.IsValid(WireSecret.Encode(new byte[32])));
        Assert.Equal(new string('A', 43), WireSecret.Encode(new byte[32]));
    }

    [Fact]
    public void Encode_takes_32_bytes_only()
    {
        Assert.Throws<ArgumentException>(() => WireSecret.Encode(new byte[31]));
    }
}
