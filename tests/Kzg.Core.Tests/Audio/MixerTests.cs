using Kzg.Core.Abstractions;
using Kzg.Core.Audio;

namespace Kzg.Core.Tests.Audio;

public class MixerTests
{
    private readonly FakeEncoder _encoder = new();
    private readonly Mixer _mixer;

    public MixerTests() => _mixer = new Mixer(_encoder);

    private static MixInput Voice(byte packet, short level) =>
        new(FrameRead.Frame(new byte[] { packet }), Enumerable.Repeat(level, OpusFormat.FrameShorts).ToArray());

    private static MixInput Lost(short level) => new(FrameRead.Missing, Enumerable.Repeat(level, OpusFormat.FrameShorts).ToArray());

    private static readonly MixInput Quiet = new(FrameRead.Silent, ReadOnlyMemory<short>.Empty);

    [Fact]
    public void Nothing_is_sent_when_no_one_is_heard()
    {
        Assert.Equal(MixKind.Nothing, _mixer.Mix([]).Kind);
        Assert.Equal(MixKind.Nothing, _mixer.Mix([Quiet, Quiet]).Kind);
        Assert.Equal(0, _encoder.Encodes);
    }

    [Fact]
    public void A_lone_source_passes_through_untouched()
    {
        var output = _mixer.Mix([Quiet, Voice(42, 1000), Quiet]);

        Assert.Equal(MixKind.Passthrough, output.Kind);
        Assert.Equal(new byte[] { 42 }, output.Opus.ToArray());
        Assert.Equal(0, _encoder.Encodes);
    }

    [Fact]
    public void A_lone_lost_frame_is_left_to_the_listeners_decoder()
    {
        Assert.Equal(MixKind.Nothing, _mixer.Mix([Lost(500)]).Kind);
        Assert.Equal(0, _encoder.Encodes);
    }

    [Fact]
    public void Two_sources_are_summed_and_encoded_by_the_destinations_encoder()
    {
        var output = _mixer.Mix([Voice(1, 1000), Quiet, Voice(2, -300)]);

        Assert.Equal(MixKind.Mixed, output.Kind);
        Assert.Equal(new byte[] { FakeEncoder.Marker, 1 }, output.Opus.ToArray());
        Assert.All(_encoder.LastPcm!, sample => Assert.Equal(700, sample));
        Assert.Equal(OpusFormat.FrameShorts, _encoder.LastPcm!.Length);
    }

    [Fact]
    public void A_lost_frame_mixes_in_as_its_concealment()
    {
        _mixer.Mix([Voice(1, 1000), Lost(FakeDecoder.Concealed)]);

        Assert.All(_encoder.LastPcm!, sample => Assert.Equal(993, sample));
    }

    [Fact]
    public void Loud_voices_together_are_limited_never_clipped_or_wrapped()
    {
        _mixer.Mix([Voice(1, short.MaxValue), Voice(2, short.MaxValue), Voice(3, short.MaxValue)]);
        var loud = _encoder.LastPcm![0];
        _mixer.Mix([Voice(1, short.MinValue), Voice(2, short.MinValue)]);
        var quiet = _encoder.LastPcm![0];

        Assert.InRange(loud, SoftLimiter.Knee + 1, short.MaxValue);
        Assert.InRange(quiet, short.MinValue, -SoftLimiter.Knee - 1);
    }

    [Fact]
    public void Each_mix_reuses_the_mixers_buffer()
    {
        var first = _mixer.Mix([Voice(1, 1), Voice(2, 2)]);
        var second = _mixer.Mix([Voice(1, 1), Voice(2, 2)]);

        Assert.Equal(2, second.Opus.Span[1]);
        Assert.Equal(2, first.Opus.Span[1]);
    }

    [Fact]
    public void A_frame_of_the_wrong_size_is_refused()
    {
        var bad = new MixInput(FrameRead.Frame(new byte[] { 1 }), new short[10]);

        Assert.Throws<ArgumentException>(() => _mixer.Mix([Voice(1, 1), bad]));
    }

    [Fact]
    public void A_destinations_mixer_encodes_voice_at_64_kbps()
    {
        var codecs = new FakeOpusCodec();

        Mixer.Create(codecs);

        Assert.Equal([64_000], codecs.Bitrates);
    }
}

public class SoftLimiterTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1234)]
    [InlineData(-1234)]
    [InlineData(SoftLimiter.Knee)]
    [InlineData(-SoftLimiter.Knee)]
    public void Below_the_knee_nothing_changes(int sample)
    {
        Assert.Equal(sample, SoftLimiter.Limit(sample));
    }

    [Fact]
    public void Above_the_knee_it_bends_smoothly_toward_full_scale_and_never_reaches_past_it()
    {
        Assert.InRange(SoftLimiter.Limit(SoftLimiter.Knee + 1) - SoftLimiter.Knee, 0, 1);
        var previous = SoftLimiter.Limit(SoftLimiter.Knee);
        for (var sample = SoftLimiter.Knee; sample <= 8 * short.MaxValue; sample += 97)
        {
            var limited = SoftLimiter.Limit(sample);
            Assert.True(limited >= previous);
            Assert.Equal(-limited, SoftLimiter.Limit(-sample));
            previous = limited;
        }
        Assert.Equal(short.MaxValue, SoftLimiter.Limit(int.MaxValue));
        Assert.Equal(-short.MaxValue, SoftLimiter.Limit(int.MinValue));
    }

    [Fact]
    public void A_whole_frame_is_limited_into_an_output_that_fits_it()
    {
        Span<short> output = stackalloc short[3];
        SoftLimiter.Apply([5, 100_000, -100_000], output);

        Assert.Equal([5, short.MaxValue, -short.MaxValue], output.ToArray().Select(s => (int)s));
        Assert.Throws<ArgumentException>(() => SoftLimiter.Apply([1, 2], new short[1]));
    }
}

public class FrameDecodingTests
{
    private readonly FakeDecoder _decoder = new();
    private readonly short[] _pcm = new short[OpusFormat.FrameShorts];

    [Fact]
    public void A_frame_is_decoded_a_missing_one_concealed_and_silence_left_alone()
    {
        Assert.True(FrameDecoding.Decode(_decoder, FrameRead.Frame(new byte[] { 3 }), _pcm));
        Assert.All(_pcm, s => Assert.Equal(300, s));

        Assert.True(FrameDecoding.Decode(_decoder, FrameRead.Missing, _pcm));
        Assert.All(_pcm, s => Assert.Equal(FakeDecoder.Concealed, s));

        Assert.False(FrameDecoding.Decode(_decoder, FrameRead.Silent, _pcm));
        Assert.Equal((1, 1), (_decoder.Decodes, _decoder.Conceals));
    }

    [Fact]
    public void A_short_decode_leaves_silence_after_it()
    {
        _pcm.AsSpan().Fill(9);
        _decoder.SamplesPerChannel = 100;

        FrameDecoding.Decode(_decoder, FrameRead.Frame(new byte[] { 1 }), _pcm);

        Assert.All(_pcm[..200], s => Assert.Equal(100, s));
        Assert.All(_pcm[200..], s => Assert.Equal(0, s));
    }

    [Fact]
    public void The_pcm_must_hold_a_whole_frame()
    {
        Assert.Throws<ArgumentException>(() => FrameDecoding.Decode(_decoder, FrameRead.Missing, new short[10]));
    }
}
