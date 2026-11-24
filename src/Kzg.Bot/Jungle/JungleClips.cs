using System.Reflection;
using Kzg.Core.Abstractions;
using Kzg.Core.Audio;
using Kzg.Core.Jungle;

namespace Kzg.Bot.Jungle;

/// <summary>
/// The four call clips, embedded as WAV and encoded to Opus once at startup, so the first call of a match
/// is not late and nothing is read from disk while the bot runs.
/// </summary>
public static class JungleClips
{
    private const int MaxPacketBytes = 1275;

    private static readonly (JungleClip Clip, string File)[] Files =
    [
        (JungleClip.Jungle40, "jungle40.wav"),
        (JungleClip.Jungle20, "jungle20.wav"),
        (JungleClip.JungleHalftime40, "jungle-halftime40.wav"),
        (JungleClip.JungleHalftime20, "jungle-halftime20.wav"),
    ];

    /// <summary>Every clip as 20 ms Opus packets at <see cref="Mixer.Bitrate"/>, at <paramref name="volume"/> (1 to 100, as <c>jungleVolume</c>).</summary>
    /// <exception cref="InvalidDataException">A clip is missing from the build or is not a WAV the bot can read.</exception>
    public static IReadOnlyDictionary<JungleClip, IReadOnlyList<ReadOnlyMemory<byte>>> Load(IOpusCodecFactory codecs, int volume)
    {
        ArgumentNullException.ThrowIfNull(codecs);
        var gain = JungleAudio.GainFor(volume);
        var clips = new Dictionary<JungleClip, IReadOnlyList<ReadOnlyMemory<byte>>>();
        foreach (var (clip, file) in Files)
        {
            using var stream = typeof(JungleClips).Assembly.GetManifestResourceStream("jungle/" + file)
                ?? throw new InvalidDataException($"the jungle clip {file} is not in the build");
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            // A fresh encoder per clip, so one clip's encoder state never leaks into the next.
            var encoder = codecs.Encoder(Mixer.Bitrate);
            var packet = new byte[MaxPacketBytes];
            var packets = new List<ReadOnlyMemory<byte>>();
            foreach (var pcm in JungleAudio.Frames(buffer.GetBuffer().AsSpan(0, (int)buffer.Length), gain))
            {
                var length = encoder.Encode(pcm, packet);
                packets.Add(packet.AsMemory(0, length).ToArray());
            }

            clips[clip] = packets;
        }

        return clips;
    }
}
