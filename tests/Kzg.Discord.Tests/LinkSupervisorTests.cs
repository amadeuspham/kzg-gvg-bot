using Kzg.Core.Abstractions;
using Kzg.Core.Testing;
using Kzg.Discord.Voice;
using Microsoft.Extensions.Logging;

namespace Kzg.Discord.Tests;

public sealed class LinkSupervisorTests
{
    private static readonly ChannelId Here = new(101);

    private sealed class Joiner : IVoiceJoiner
    {
        private readonly Queue<Func<Task<IVoiceConnection>>> _plan = new();

        public int Joins { get; private set; }

        public List<ChannelId> Channels { get; } = [];

        public int Leaves { get; private set; }

        public void Then(Func<Task<IVoiceConnection>> next) => _plan.Enqueue(next);

        public async Task<IVoiceConnection> JoinAsync(ChannelId channel, CancellationToken cancellationToken)
        {
            Joins++;
            Channels.Add(channel);
            if (_plan.Count == 0)
            {
                // Nothing more planned: wait until stopped.
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            return await _plan.Dequeue()();
        }

        public Task LeaveAsync(CancellationToken cancellationToken)
        {
            Leaves++;
            return Task.CompletedTask;
        }
    }

    private readonly ManualTimeSource _time = new();
    private readonly List<TimeSpan> _pauses = [];

    private (RelayLink Link, Joiner Joiner, LinkSupervisor Supervisor) Build()
    {
        var link = new RelayLink(Here, () => 1, _time, new OwnBots());
        var joiner = new Joiner();
        var supervisor = new LinkSupervisor(link, joiner, delay: (pause, _) =>
        {
            _pauses.Add(pause);
            return Task.CompletedTask;
        });
        return (link, joiner, supervisor);
    }

    private static bool Wait(Func<bool> done) => SpinWait.SpinUntil(done, TimeSpan.FromSeconds(5));

    private sealed class Lines : ILogger
    {
        public List<string> Written { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Written)
            {
                Written.Add(formatter(state, exception));
            }
        }
    }

    [Fact]
    public async Task The_log_names_the_bot_by_its_team_name_as_it_is_now()
    {
        var link = new RelayLink(Here, () => 1, _time, new OwnBots());
        var joiner = new Joiner();
        var log = new Lines();
        var name = "A1 Broadcaster";
        var supervisor = new LinkSupervisor(link, joiner, log, (_, _) => Task.CompletedTask, name: () => name);
        joiner.Then(() => Task.FromResult<IVoiceConnection>(new FakeVoiceConnection { IsReady = true }));

        supervisor.Start();

        Assert.True(Wait(() => link.Ready));
        await supervisor.StopAsync();
        lock (log.Written)
        {
            Assert.Contains("A1 Broadcaster is in voice channel 101", log.Written);
        }
    }

    [Fact]
    public async Task It_joins_and_binds_a_connection_that_is_ready()
    {
        var (link, joiner, supervisor) = Build();
        var voice = new FakeVoiceConnection { IsReady = true };
        joiner.Then(() => Task.FromResult<IVoiceConnection>(voice));

        supervisor.Start();

        Assert.True(Wait(() => link.Ready));
        await supervisor.StopAsync();
        Assert.False(link.Ready);
        Assert.True(voice.Disposed);
        Assert.Equal(1, joiner.Leaves);
    }

    [Fact]
    public async Task A_lost_connection_is_replaced_after_a_pause()
    {
        var (link, joiner, supervisor) = Build();
        var first = new FakeVoiceConnection { IsReady = true };
        var second = new FakeVoiceConnection { IsReady = true };
        joiner.Then(() => Task.FromResult<IVoiceConnection>(first));
        joiner.Then(() => Task.FromResult<IVoiceConnection>(second));
        supervisor.Start();
        Assert.True(Wait(() => link.Ready));

        first.LoseForGood();

        Assert.True(Wait(() => first.Disposed));
        Assert.True(Wait(() => joiner.Joins >= 2 && link.Ready));
        Assert.Equal([LinkSupervisor.FirstPause], _pauses.Take(1));
        await supervisor.StopAsync();
        Assert.True(second.Disposed);
    }

    [Fact]
    public async Task A_failed_join_is_retried_with_pauses_that_double_up_to_the_longest()
    {
        var (link, joiner, supervisor) = Build();
        for (var i = 0; i < 8; i++)
        {
            joiner.Then(() => throw new InvalidOperationException("no"));
        }
        var good = new FakeVoiceConnection { IsReady = true };
        joiner.Then(() => Task.FromResult<IVoiceConnection>(good));

        supervisor.Start();

        Assert.True(Wait(() => link.Ready));
        await supervisor.StopAsync();
        Assert.Equal(
            [1, 2, 4, 8, 16, 30, 30, 30],
            _pauses.Take(8).Select(p => (int)p.TotalSeconds));
    }

    [Fact]
    public async Task Stopping_while_a_join_is_pending_returns_and_leaves_voice()
    {
        var (link, joiner, supervisor) = Build();
        supervisor.Start();
        Assert.True(Wait(() => joiner.Joins == 1));

        await supervisor.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(link.Ready);
        Assert.Equal(1, joiner.Leaves);
    }

    [Fact]
    public async Task A_drop_the_library_recovers_from_keeps_the_same_connection()
    {
        var (link, joiner, supervisor) = Build();
        var voice = new FakeVoiceConnection { IsReady = true };
        joiner.Then(() => Task.FromResult<IVoiceConnection>(voice));
        supervisor.Start();
        Assert.True(Wait(() => link.Ready));

        voice.Interrupt();
        Assert.False(link.Ready);
        voice.Resume();

        Assert.True(link.Ready);
        Assert.Equal(1, joiner.Joins);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task A_bot_moved_to_another_channel_stops_sending_at_once_and_goes_back()
    {
        var (link, joiner, supervisor) = Build();
        var first = new FakeVoiceConnection { IsReady = true };
        var second = new FakeVoiceConnection { IsReady = true };
        joiner.Then(() => Task.FromResult<IVoiceConnection>(first));
        joiner.Then(() => Task.FromResult<IVoiceConnection>(second));
        supervisor.Start();
        Assert.True(Wait(() => link.Ready));
        link.Send(new byte[] { 1, 2, 3 });
        var sentBefore = first.Sent.Count;

        first.MoveTo(new ChannelId(202));

        // Not ready on the same call, before the supervisor has even woken: nothing more goes into channel 202.
        Assert.False(link.Ready);
        link.Send(new byte[] { 4, 5, 6 });
        Assert.Equal(sentBefore, first.Sent.Count);
        Assert.True(Wait(() => first.Disposed));
        Assert.True(Wait(() => joiner.Joins >= 2 && link.Ready));
        Assert.Equal([Here, Here], joiner.Channels);
        Assert.Equal([LinkSupervisor.MovePause], _pauses.Take(1));
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task A_bot_disconnected_from_voice_goes_back_too()
    {
        var (link, joiner, supervisor) = Build();
        var first = new FakeVoiceConnection { IsReady = true };
        var second = new FakeVoiceConnection { IsReady = true };
        joiner.Then(() => Task.FromResult<IVoiceConnection>(first));
        joiner.Then(() => Task.FromResult<IVoiceConnection>(second));
        supervisor.Start();
        Assert.True(Wait(() => link.Ready));

        first.MoveTo(null);

        Assert.False(link.Ready);
        Assert.True(Wait(() => joiner.Joins >= 2 && link.Ready));
        Assert.Equal([Here, Here], joiner.Channels);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task A_voice_state_in_the_assigned_channel_is_not_a_move()
    {
        var (link, joiner, supervisor) = Build();
        var voice = new FakeVoiceConnection { IsReady = true };
        joiner.Then(() => Task.FromResult<IVoiceConnection>(voice));
        supervisor.Start();
        Assert.True(Wait(() => link.Ready));

        voice.MoveTo(Here);

        Assert.True(link.Ready);
        Assert.Equal(1, joiner.Joins);
        await supervisor.StopAsync();
    }

    [Fact]
    public async Task A_bot_moved_again_soon_after_waits_longer_each_time()
    {
        var (link, joiner, supervisor) = Build();
        var voices = Enumerable.Range(0, 6).Select(_ => new FakeVoiceConnection { IsReady = true }).ToArray();
        foreach (var voice in voices)
        {
            joiner.Then(() => Task.FromResult<IVoiceConnection>(voice));
        }
        supervisor.Start();
        for (var i = 0; i < 5; i++)
        {
            Assert.True(Wait(() => joiner.Joins == i + 1 && link.Ready));
            voices[i].MoveTo(new ChannelId(300 + (ulong)i));
        }

        Assert.True(Wait(() => joiner.Joins >= 6 && link.Ready));
        Assert.Equal([250, 500, 1000, 2000, 2000], _pauses.Take(5).Select(p => (int)p.TotalMilliseconds));
        await supervisor.StopAsync();
    }
}
