using Kzg.Audio;
using Kzg.Bot.Fake;
using Kzg.Core.Abstractions;
using Kzg.Core.Testing;
using Kzg.Protocol;

namespace Kzg.Bot.Tests.Hosting;

// The whole bot with no Discord: real Kestrel and a real loopback WebSocket in, the fake gateway's links out,
// the engine on its own thread in real time. Only timing ranges are asserted that a busy machine keeps.
[Collection("real time")]
public sealed class OfflineEndToEndTests
{
    private static readonly ConcentusOpusCodecFactory Codecs = new();

    private static FakeVoiceChannelLink Link(HostRig rig, ChannelId channel) => rig.World.Gateway.Link(channel);

    private static List<int> Relayed(FakeVoiceChannelLink link, Dictionary<string, int> index) =>
        [.. link.Sent.Select(frame => SpeechAudio.IndexOf(index, frame.Opus))];

    [Fact]
    public async Task A_press_from_the_app_relays_A1_to_A2_and_A3_never_to_A1_and_the_preroll_keeps_the_first_frames()
    {
        await using var rig = await new HostRig().StartAsync();
        var (app, _) = await rig.SignInAsync(DemoWorld.Alice);
        await using var _ = app;
        List<ServerMessage> seen = [];
        await app.UntilAsync<StatusMessage>(s => s.ChannelName == "Attack 1", seen);
        var tone = SpeechAudio.Tone(Codecs, 60);
        var index = SpeechAudio.Index(tone);

        // Alice starts talking; 200 ms in, her app reports the key down. The first 10 frames came before it.
        var talking = Link(rig, DemoWorld.Attack1).PlayAsync(DemoWorld.Alice, tone);
        await Task.Delay(200);
        await app.SendAsync(new PressMessage(WireAction.Group, true, 0));
        var relaying = await app.UntilAsync<StatusMessage>(s => s.Active.Contains(WireAction.Group), seen);
        await talking;
        await Task.Delay(300);
        await app.SendAsync(new PressMessage(WireAction.Group, false, 1));
        var stopped = await app.UntilAsync<StatusMessage>(s => s.Active.Count == 0 && seen.OfType<StatusMessage>().Any(p => p.Active.Count > 0), seen);
        await Task.Delay(200);

        Assert.Equal(["Attack 2", "Attack 3"], relaying.Targets);
        Assert.Equal(WireGroup.Attack, relaying.Group);
        Assert.Empty(stopped.Targets);
        Assert.Empty(Link(rig, DemoWorld.Attack1).Sent);
        foreach (var channel in new[] { DemoWorld.Defense1, DemoWorld.Defense2, DemoWorld.Defense3 })
        {
            Assert.Empty(Link(rig, channel).Sent);
        }
        foreach (var channel in new[] { DemoWorld.Attack2, DemoWorld.Attack3 })
        {
            var frames = Relayed(Link(rig, channel), index);
            // Every frame is one of Alice's, passed through whole, in order, from the very first.
            Assert.DoesNotContain(-1, frames);
            Assert.Equal(0, frames[0]);
            Assert.Equal(frames.Order(), frames);
            Assert.Equal(frames.Distinct().Count(), frames.Count);
            Assert.True(frames.Count >= 55, $"{frames.Count} of 60 frames reached {channel}");
            Assert.Equal(0, Link(rig, channel).SentWhileNotReady);
        }
        Assert.Equal([true, false], Link(rig, DemoWorld.Attack2).SpeakingChanges.Select(c => c.On));
    }

    [Fact]
    public async Task Two_players_pressing_at_once_reach_each_others_VCs_and_are_mixed_where_both_are_heard()
    {
        await using var rig = await new HostRig().StartAsync();
        var (alice, _) = await rig.SignInAsync(DemoWorld.Alice);
        var (bram, _) = await rig.SignInAsync(DemoWorld.Bram);
        await using var _ = alice;
        await using var __ = bram;
        await alice.UntilAsync<StatusMessage>(s => s.ChannelName == "Attack 1");
        await bram.UntilAsync<StatusMessage>(s => s.ChannelName == "Attack 2");
        var aliceTone = SpeechAudio.Tone(Codecs, 40);
        var aliceIndex = SpeechAudio.Index(aliceTone);

        await alice.SendAsync(new PressMessage(WireAction.Group, true, 0));
        await bram.SendAsync(new PressMessage(WireAction.Group, true, 0));
        await alice.UntilAsync<StatusMessage>(s => s.Active.Contains(WireAction.Group));
        await bram.UntilAsync<StatusMessage>(s => s.Active.Contains(WireAction.Group));
        await Task.WhenAll(
            Link(rig, DemoWorld.Attack1).PlayAsync(DemoWorld.Alice, aliceTone),
            Link(rig, DemoWorld.Attack2).PlayAsync(DemoWorld.Bram, SpeechAudio.Tone(Codecs, 40)));
        await Task.Delay(300);

        // A1 hears only Bram and A2 only Alice, each as sent; A3 hears both, which is a new packet.
        Assert.NotEmpty(Link(rig, DemoWorld.Attack1).Sent);
        var heardByA2 = Relayed(Link(rig, DemoWorld.Attack2), aliceIndex);
        Assert.NotEmpty(heardByA2);
        Assert.DoesNotContain(-1, heardByA2);
        var a3 = Link(rig, DemoWorld.Attack3).Sent;
        Assert.True(a3.Count >= 30);
        Assert.True(a3.Count(frame => SpeechAudio.IndexOf(aliceIndex, frame.Opus) == -1) >= 25, "A3 got mixed packets");
    }
}
