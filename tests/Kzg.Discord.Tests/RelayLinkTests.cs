using Kzg.Core.Abstractions;
using Kzg.Core.Testing;
using Kzg.Discord.Voice;

namespace Kzg.Discord.Tests;

public sealed class RelayLinkTests
{
    private static readonly ChannelId Here = new(101);
    private const ulong Player = 1001, OtherBot = 9002;

    private readonly ManualTimeSource _time = new();
    private readonly OwnBots _own = new();
    private readonly List<OpusPacket> _received = [];
    private readonly RelayLink _link;
    private readonly FakeVoiceConnection _voice = new();
    private readonly TaskCompletionSource _announcementOver = new();
    private readonly TaskCompletionSource _joinSettled = new();

    public RelayLinkTests()
    {
        _own.Add(OtherBot);
        // The spacing of silence frames and the flag's lead pass at once; the announcement window and the
        // wait after a join stay open until a test closes them.
        _link = new RelayLink(Here, () => 9001, _time, _own, delay: Delay);
        _link.PacketReceived += _received.Add;
        _voice.MapUser(11, Player);
        _voice.MapUser(12, OtherBot);
    }

    private Task Delay(TimeSpan span, CancellationToken cancellationToken) =>
        span == RelayLink.JoinSettle ? _joinSettled.Task.WaitAsync(cancellationToken)
        : span == RelayLink.Announcement ? _announcementOver.Task.WaitAsync(cancellationToken)
        : Task.CompletedTask;

    private void Up()
    {
        _link.Bind(_voice);
        _voice.BecomeReady();
    }

    [Fact]
    public void It_is_not_ready_until_the_connection_is_and_not_ready_again_when_it_drops()
    {
        Assert.False(_link.Ready);

        _link.Bind(_voice);
        Assert.False(_link.Ready);
        _voice.BecomeReady();
        Assert.True(_link.Ready);
        _voice.Interrupt();
        Assert.False(_link.Ready);
        _voice.Resume();
        Assert.True(_link.Ready);
        _link.Unbind();
        Assert.False(_link.Ready);
    }

    [Fact]
    public void A_connection_that_was_ready_before_it_was_bound_makes_the_link_ready()
    {
        _voice.IsReady = true;

        _link.Bind(_voice);

        Assert.True(_link.Ready);
    }

    [Fact]
    public void It_reports_its_channel_and_bot()
    {
        Assert.Equal(Here, _link.Channel);
        Assert.Equal(9001UL, _link.BotUserId);
    }

    [Fact]
    public void A_packet_becomes_an_OpusPacket_for_the_player_whose_stream_it_is()
    {
        Up();
        _time.Advance(TimeSpan.FromMilliseconds(5));

        _voice.Receive(11, 77, 123456, [1, 2, 3]);

        var packet = Assert.Single(_received);
        Assert.Equal(new UserId(Player), packet.User);
        Assert.Equal((ushort)77, packet.Seq);
        Assert.Equal(123456u, packet.RtpTs);
        Assert.Equal(new byte[] { 1, 2, 3 }, packet.Data.ToArray());
        Assert.Equal(_time.Now, packet.Arrival);
    }

    [Fact]
    public void The_packet_is_copied_because_the_library_reuses_its_buffer()
    {
        Up();
        var buffer = new byte[] { 9, 8, 7 };

        _voice.Receive(11, 1, 1, buffer);
        Array.Fill(buffer, (byte)0);

        Assert.Equal(new byte[] { 9, 8, 7 }, _received[0].Data.ToArray());
    }

    [Fact]
    public void Our_own_bots_packets_are_dropped_and_counted()
    {
        Up();

        _voice.Receive(12, 1, 1, [1]);

        Assert.Empty(_received);
        Assert.Equal(1, _link.DroppedOwn);
    }

    [Fact]
    public void A_stream_whose_player_is_not_known_yet_is_dropped_and_counted()
    {
        Up();

        _voice.Receive(99, 1, 1, [1]);

        Assert.Empty(_received);
        Assert.Equal(1, _link.UnknownSsrc);
    }

    [Fact]
    public void An_empty_frame_and_a_packet_after_the_connection_went_are_ignored()
    {
        Up();
        _voice.Receive(11, 1, 1, []);
        _link.Unbind();

        // The old connection's thread may still deliver one.
        _voice.Receive(11, 2, 2, [1]);

        Assert.Empty(_received);
    }

    [Fact]
    public void Frames_go_out_with_the_clocks_numbers_and_the_speaking_flag_goes_up_once()
    {
        Up();

        for (var i = 0; i < 3; i++)
        {
            _link.Send(new byte[] { (byte)i, 0xAA });
            _time.Advance(TimeSpan.FromMilliseconds(20));
        }

        Assert.Equal(3, _voice.Sent.Count);
        Assert.Equal([0, 1, 2], _voice.Sent.Select(p => (int)p.Opus[0]));
        Assert.Equal(_voice.Sent[0].Timestamp + 960, _voice.Sent[1].Timestamp);
        Assert.Equal((ushort)(_voice.Sent[0].Sequence + 1), _voice.Sent[1].Sequence);
        Assert.Equal([true], _voice.Speaking);
        Assert.Equal(3, _link.FramesSent);
    }

    [Fact]
    public void The_sender_may_reuse_its_buffer_at_once()
    {
        Up();
        var buffer = new byte[] { 5, 5, 5 };

        _link.Send(buffer);
        Array.Fill(buffer, (byte)0);

        Assert.Equal(new byte[] { 5, 5, 5 }, _voice.Sent[0].Opus);
    }

    [Fact]
    public void A_frame_sent_while_not_ready_goes_nowhere_without_a_throw()
    {
        _link.Bind(_voice);

        _link.Send(new byte[] { 1 });

        Assert.Empty(_voice.Sent);
        Assert.Equal(1, _link.SentNotReady);
    }

    [Fact]
    public void A_frame_sent_with_no_connection_goes_nowhere_without_a_throw()
    {
        _link.Send(new byte[] { 1 });

        Assert.Equal(1, _link.SentNotReady);
    }

    [Fact]
    public void An_empty_frame_is_not_sent()
    {
        Up();

        _link.Send(ReadOnlyMemory<byte>.Empty);

        Assert.Empty(_voice.Sent);
    }

    [Fact]
    public void A_send_that_throws_is_counted_and_never_reaches_the_engine()
    {
        Up();
        _voice.ThrowOnSend = true;

        _link.Send(new byte[] { 1 });

        Assert.Equal(1, _link.SendErrors);
        Assert.Equal(0, _link.FramesSent);
    }

    [Fact]
    public void A_burst_ends_with_five_silence_frames_and_then_the_speaking_flag_goes_down()
    {
        Up();
        _link.Send(new byte[] { 1, 2 });
        _time.Advance(TimeSpan.FromMilliseconds(20));

        _link.SetSpeaking(false);
        // The silence is sent by a task; the test's delay is already complete.
        SpinWait.SpinUntil(() => _voice.Speaking.Count == 2, TimeSpan.FromSeconds(2));

        var silence = _voice.Sent.Skip(1).ToList();
        Assert.Equal(RelayLink.SilenceFrames, silence.Count);
        Assert.All(silence, p => Assert.Equal(new byte[] { 0xF8, 0xFF, 0xFE }, p.Opus));
        Assert.Equal([true, false], _voice.Speaking);
    }

    [Fact]
    public void Silence_frames_continue_the_numbering_of_the_burst()
    {
        Up();
        _link.Send(new byte[] { 1 });

        _link.SetSpeaking(false);
        SpinWait.SpinUntil(() => _voice.Speaking.Count == 2, TimeSpan.FromSeconds(2));

        var all = _voice.Sent;
        for (var i = 1; i < all.Count; i++)
        {
            Assert.Equal((ushort)(all[i - 1].Sequence + 1), all[i].Sequence);
            Assert.True(all[i].Timestamp > all[i - 1].Timestamp);
        }
    }

    [Fact]
    public void New_audio_in_the_middle_of_the_ending_stops_it()
    {
        var gate = new TaskCompletionSource();
        var link = new RelayLink(Here, () => 9001, _time, _own, delay: (span, _) => span == RelayLink.Announcement ? _announcementOver.Task : span >= RelayLink.FlagLead ? Task.CompletedTask : gate.Task);
        link.Bind(_voice);
        _voice.BecomeReady();
        link.Send(new byte[] { 1 });

        link.SetSpeaking(false);
        SpinWait.SpinUntil(() => _voice.Sent.Count == 2, TimeSpan.FromSeconds(2));
        link.Send(new byte[] { 2 });
        gate.SetResult();
        Thread.Sleep(100);

        // The first silence frame was out before the new audio; no more followed, and the flag stayed up.
        Assert.Equal(3, _voice.Sent.Count);
        Assert.Equal(new byte[] { 2 }, _voice.Sent[2].Opus);
        Assert.Equal([true], _voice.Speaking);
    }

    [Fact]
    public void Ending_a_burst_that_never_began_does_nothing()
    {
        Up();

        _link.SetSpeaking(false);

        // Only the announcement's flag: no silence frames for a burst that never was.
        Assert.Empty(_voice.Sent);
        Assert.Equal([true], _voice.Speaking);
    }

    [Fact]
    public void Setting_speaking_on_raises_the_flag_once()
    {
        Up();

        _link.SetSpeaking(true);
        _link.SetSpeaking(true);

        Assert.Equal([true], _voice.Speaking);
    }

    [Fact]
    public void A_new_connection_starts_with_the_flag_down_again()
    {
        Up();
        _link.Send(new byte[] { 1 });
        var again = new FakeVoiceConnection();

        _link.Bind(again);
        again.BecomeReady();
        _link.Send(new byte[] { 1 });

        Assert.Equal([true], again.Speaking);
    }

    // A link that has announced itself and heard everyone join; its flag's lead is closed until the test opens it.
    private (RelayLink Link, TaskCompletionSource Lead) Settled()
    {
        TaskCompletionSource lead = new();
        var link = new RelayLink(Here, () => 9001, _time, _own, delay: (span, _) => span == RelayLink.JoinSettle ? _joinSettled.Task : span == RelayLink.FlagLead ? lead.Task : Task.CompletedTask);
        link.Bind(_voice);
        _voice.BecomeReady();
        SpinWait.SpinUntil(() => _voice.Speaking.Count == 2, TimeSpan.FromSeconds(2));
        return (link, lead);
    }

    [Fact]
    public void A_connection_that_comes_up_announces_its_stream_before_any_audio_and_lowers_the_flag_after_the_window()
    {
        _link.Bind(_voice);
        Assert.Empty(_voice.Speaking);

        _voice.BecomeReady();
        Assert.Equal([true], _voice.Speaking);
        _announcementOver.SetResult();
        SpinWait.SpinUntil(() => _voice.Speaking.Count == 2, TimeSpan.FromSeconds(2));

        Assert.Equal([true, false], _voice.Speaking);
        Assert.Empty(_voice.Sent);
    }

    [Fact]
    public void A_burst_that_begins_in_the_announcement_window_keeps_the_flag_up()
    {
        Up();

        _link.Send(new byte[] { 1 });
        _announcementOver.SetResult();
        Thread.Sleep(100);

        Assert.Equal([true], _voice.Speaking);
        Assert.Single(_voice.Sent);
    }

    [Fact]
    public void A_resume_announces_again()
    {
        Up();
        _announcementOver.SetResult();
        SpinWait.SpinUntil(() => _voice.Speaking.Count == 2, TimeSpan.FromSeconds(2));

        _voice.Interrupt();
        _voice.Resume();

        SpinWait.SpinUntil(() => _voice.Speaking.Count >= 4, TimeSpan.FromSeconds(2));
        Assert.Equal([true, false, true, false], _voice.Speaking.Take(4));
    }

    [Fact]
    public void A_player_who_joins_gets_an_announcement_once_the_joining_has_settled()
    {
        Up();
        _announcementOver.SetResult();
        SpinWait.SpinUntil(() => _voice.Speaking.Count == 2, TimeSpan.FromSeconds(2));

        _voice.Join();
        _voice.Join();
        Thread.Sleep(50);
        Assert.Equal(2, _voice.Speaking.Count);
        _joinSettled.SetResult();
        SpinWait.SpinUntil(() => _voice.Speaking.Count == 4, TimeSpan.FromSeconds(2));
        Thread.Sleep(50);

        Assert.Equal([true, false, true, false], _voice.Speaking);
        Assert.Empty(_voice.Sent);
    }

    [Fact]
    public void A_burst_that_begins_before_the_joining_has_settled_takes_the_announcements_place()
    {
        var (link, lead) = Settled();
        _voice.Join();
        link.Send(new byte[] { 1 });
        lead.SetResult();
        SpinWait.SpinUntil(() => _voice.Sent.Count == 1, TimeSpan.FromSeconds(2));

        _joinSettled.SetResult();
        Thread.Sleep(100);

        // The burst raised the flag itself; the announcement found it up and stood down.
        Assert.Equal([true, false, true], _voice.Speaking);
    }

    [Fact]
    public void A_burst_after_a_player_joined_raises_the_flag_before_its_frames_go()
    {
        var (link, lead) = Settled();
        _voice.Join();

        link.Send(new byte[] { 1 });
        link.Send(new byte[] { 2 });

        Assert.Equal([true, false, true], _voice.Speaking);
        Assert.Empty(_voice.Sent);
        lead.SetResult();
        SpinWait.SpinUntil(() => _voice.Sent.Count == 2, TimeSpan.FromSeconds(2));
        Assert.Equal([1, 2], _voice.Sent.Select(p => (int)p.Opus[0]));
        Assert.Equal(_voice.Sent[0].Timestamp + 960, _voice.Sent[1].Timestamp);
        Assert.Equal((ushort)(_voice.Sent[0].Sequence + 1), _voice.Sent[1].Sequence);
    }

    [Fact]
    public void Frames_after_the_held_ones_go_straight_out()
    {
        var (link, lead) = Settled();
        _voice.Join();
        link.Send(new byte[] { 1 });
        lead.SetResult();
        SpinWait.SpinUntil(() => _voice.Sent.Count == 1, TimeSpan.FromSeconds(2));

        link.Send(new byte[] { 2 });

        Assert.Equal(2, _voice.Sent.Count);
    }

    [Fact]
    public void A_burst_that_ends_while_its_first_frames_wait_sends_them_before_its_silence()
    {
        var (link, lead) = Settled();
        _voice.Join();
        link.Send(new byte[] { 1 });
        link.Send(new byte[] { 2 });

        link.SetSpeaking(false);
        lead.SetResult();
        SpinWait.SpinUntil(() => _voice.Sent.Count == 2 + RelayLink.SilenceFrames, TimeSpan.FromSeconds(2));

        Assert.Equal([1, 2], _voice.Sent.Take(2).Select(p => (int)p.Opus[0]));
        Assert.All(_voice.Sent.Skip(2), p => Assert.Equal(RelayLink.SilenceFrame, p.Opus));
        Assert.Equal((ushort)(_voice.Sent[0].Sequence + 6), _voice.Sent[6].Sequence);
    }

    [Fact]
    public void A_burst_when_everyone_has_heard_the_announcement_sends_its_first_frame_at_once_and_the_flag_follows()
    {
        var (link, _) = Settled();

        link.Send(new byte[] { 9 });

        Assert.Single(_voice.Sent);
        Assert.Equal([true, false, true], _voice.Speaking);
    }

    [Fact]
    public void Frames_held_for_a_connection_that_drops_are_lost_and_the_next_burst_starts_clean()
    {
        var (link, lead) = Settled();
        _voice.Join();
        link.Send(new byte[] { 1 });
        _voice.Interrupt();
        lead.SetResult();
        Thread.Sleep(100);
        Assert.Empty(_voice.Sent);

        _voice.Resume();
        SpinWait.SpinUntil(() => _voice.Speaking.Count >= 5, TimeSpan.FromSeconds(2));
        link.Send(new byte[] { 2 });

        SpinWait.SpinUntil(() => _voice.Sent.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 2 }, _voice.Sent[0].Opus);
    }

    [Fact]
    public void Frames_held_for_a_connection_that_is_unbound_are_dropped()
    {
        var (link, lead) = Settled();
        _voice.Join();
        link.Send(new byte[] { 1 });

        link.Unbind();
        lead.SetResult();
        Thread.Sleep(100);

        Assert.Empty(_voice.Sent);
    }

    [Fact]
    public void A_flag_that_cannot_be_raised_does_not_cost_the_audio_or_leave_the_link_stuck_speaking()
    {
        _voice.ThrowOnSpeaking = true;
        Up();
        Thread.Sleep(50);

        _link.Send(new byte[] { 1 });

        SpinWait.SpinUntil(() => _voice.Sent.Count == 1, TimeSpan.FromSeconds(2));
        Assert.Equal(new byte[] { 1 }, _voice.Sent[0].Opus);
        _link.SetSpeaking(false);
        SpinWait.SpinUntil(() => _voice.Sent.Count == 1 + RelayLink.SilenceFrames, TimeSpan.FromSeconds(2));
        Assert.Equal(1 + RelayLink.SilenceFrames, _voice.Sent.Count);
    }
}
