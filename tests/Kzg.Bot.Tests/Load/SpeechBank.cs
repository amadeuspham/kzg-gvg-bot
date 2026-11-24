using Kzg.Bot.Tests.Live;

namespace Kzg.Bot.Tests.Load;

/// <summary>Real speech as 20 ms Opus frames, in a few voices, made with macOS <c>say</c>: what a talker's client would send.</summary>
internal sealed class SpeechBank
{
    private static readonly string[] Chat =
    [
        "Attack team, move to the east tower and hold the position until the others arrive.",
        "I need two more people at the bridge, they are pushing hard from the south side.",
        "Healers stay behind the front line, and watch your stamina before you jump in.",
        "Their cavalry is coming around the left flank, someone cover the gate please.",
        "We have the temple now, keep the pressure on and do not chase them into the woods.",
        "Wait for my signal, then everybody goes in at the same time through the main road.",
        "Three of them are down, the rest are retreating toward the river, stay together.",
        "Rotate to the west camp when the timer reaches one minute and bring your shields.",
    ];

    private static readonly string[] Candidates = ["Samantha", "Daniel", "Fred", "Alex", "Karen", "Moira", "Tessa", "Rishi"];

    private SpeechBank(IReadOnlyList<Voice> voices) => Voices = voices;

    public IReadOnlyList<Voice> Voices { get; }

    /// <summary>One voice's utterances: the frames of each, trimmed to the speech.</summary>
    public sealed record Voice(string Name, IReadOnlyList<byte[][]> Chat);

    /// <summary>Makes the bank; voices this Mac lacks are skipped. Takes a few seconds, in a temp folder that is removed.</summary>
    public static SpeechBank Build(int wanted = 4)
    {
        List<Voice> voices = [];
        var seed = 5000;
        foreach (var name in Candidates)
        {
            if (voices.Count == wanted)
            {
                break;
            }
            try
            {
                voices.Add(new Voice(name, [.. Chat.Select(t => Frames(t, name, seed++))]));
            }
            catch (InvalidOperationException)
            {
                // This voice isn't installed.
            }
        }
        return voices.Count > 0 ? new SpeechBank(voices) : throw new InvalidOperationException("no macOS 'say' voice worked; the load test needs speech");
    }

    private static byte[][] Frames(string text, string voice, int seed)
    {
        var clip = Clip.Say(voice, text, 0, 0, seed, out var first, out var last, voice);
        return [.. clip.Frames.Skip(first).Take(last - first + 1).Select(f => f.Data)];
    }
}
