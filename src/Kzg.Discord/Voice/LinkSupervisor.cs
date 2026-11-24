using Kzg.Core.Abstractions;
using Microsoft.Extensions.Logging;

namespace Kzg.Discord.Voice;

/// <summary>Opens a bot's voice connection into a channel and closes it again.</summary>
internal interface IVoiceJoiner
{
    /// <summary>Joins the channel and returns once the media path is up; throws if it can't.</summary>
    Task<IVoiceConnection> JoinAsync(ChannelId channel, CancellationToken cancellationToken);

    /// <summary>Leaves voice in the guild; the bot stays signed in.</summary>
    Task LeaveAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Keeps one link joined: joins the channel, waits until the connection is lost for good, then joins
/// again after a pause that doubles up to half a minute. Until a join works the link is simply not ready,
/// which is what raises the app's "bot is down" warning.
/// A bot someone moves or disconnects goes straight back: its link stops being ready the moment Discord
/// says so, so nothing is sent into the wrong channel, and it rejoins after a short pause. A move never
/// sticks; the assigned channel changes only with <c>/gvg bot assign</c>.
/// </summary>
internal sealed class LinkSupervisor
{
    public static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan LongestPause = TimeSpan.FromSeconds(30);

    /// <summary>The pause before going back after a move; a bot moved again soon after doubles it, so two movers can't make it thrash.</summary>
    public static readonly TimeSpan MovePause = TimeSpan.FromMilliseconds(250);

    /// <summary>The longest that doubling goes: a bot that keeps being moved still comes back within seconds.</summary>
    public static readonly TimeSpan LongestMovePause = TimeSpan.FromSeconds(2);

    // A connection that lasted this long was a good one: the next failure starts the pauses over.
    private static readonly TimeSpan Stable = TimeSpan.FromSeconds(30);

    private readonly RelayLink _link;
    private readonly IVoiceJoiner _joiner;
    private readonly ILogger? _log;
    private readonly Func<string>? _name;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeProvider _clock;
    private readonly CancellationTokenSource _stop = new();
    private Task _run = Task.CompletedTask;

    /// <param name="name">How the log names the bot, such as "A1 Broadcaster"; asked each time, as the name follows its channel.</param>
    public LinkSupervisor(RelayLink link, IVoiceJoiner joiner, ILogger? log = null, Func<TimeSpan, CancellationToken, Task>? delay = null, TimeProvider? clock = null, Func<string>? name = null)
    {
        (_link, _joiner, _log, _name) = (link, joiner, log, name);
        _delay = delay ?? Task.Delay;
        _clock = clock ?? TimeProvider.System;
    }

    /// <summary>How many joins have failed or ended since the supervisor started.</summary>
    public int Rejoins { get; private set; }

    private string BotName => _name?.Invoke() ?? $"relay bot {_link.BotUserId}";

    public void Start() => _run = Task.Run(() => RunAsync(_stop.Token));

    /// <summary>Stops rejoining, leaves the channel and unbinds the link.</summary>
    public async Task StopAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _run.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped.
        }
        _link.Unbind();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await _joiner.LeaveAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _log?.LogDebug("leaving voice failed: {Error}", exception.GetType().Name);
        }
        _stop.Dispose();
    }

    private async Task RunAsync(CancellationToken token)
    {
        var pause = FirstPause;
        var movePause = MovePause;
        while (!token.IsCancellationRequested)
        {
            IVoiceConnection? connection = null;
            Action<ChannelId?>? watch = null;
            var wait = pause;
            var wasMoved = false;
            try
            {
                connection = await _joiner.JoinAsync(_link.Channel, token).ConfigureAwait(false);
                var started = _clock.GetTimestamp();
                var moved = new TaskCompletionSource<ChannelId?>(TaskCreationOptions.RunContinuationsAsynchronously);
                watch = channel =>
                {
                    if (channel != _link.Channel)
                    {
                        // At once, on the gateway's thread: the engine must not send another frame into the wrong channel.
                        _link.Unbind();
                        moved.TrySetResult(channel);
                    }
                };
                connection.ChannelChanged += watch;
                _link.Bind(connection);
                _log?.LogInformation("{Bot} is in voice channel {Channel}", BotName, _link.Channel);
                var ended = await Task.WhenAny(connection.Lost, moved.Task).WaitAsync(token).ConfigureAwait(false);
                var stable = _clock.GetElapsedTime(started) >= Stable;
                if (ended == moved.Task)
                {
                    (wait, wasMoved) = (movePause, true);
                    movePause = stable ? MovePause : TimeSpan.FromTicks(Math.Min(movePause.Ticks * 2, LongestMovePause.Ticks));
                    _log?.LogWarning("{Bot} was moved out of voice channel {Channel}; going back", BotName, _link.Channel);
                }
                else
                {
                    if (stable)
                    {
                        pause = FirstPause;
                    }
                    wait = pause;
                    _log?.LogWarning("{Bot} lost its voice connection to channel {Channel}", BotName, _link.Channel);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                _log?.LogWarning("{Bot} couldn't join voice channel {Channel}: {Error}", BotName, _link.Channel, exception.GetType().Name);
            }
            finally
            {
                _link.Unbind();
                if (connection is not null && watch is not null)
                {
                    connection.ChannelChanged -= watch;
                }
                connection?.Dispose();
            }
            Rejoins++;
            try
            {
                await _delay(wait, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            if (!wasMoved)
            {
                pause = TimeSpan.FromTicks(Math.Min(pause.Ticks * 2, LongestPause.Ticks));
            }
        }
    }
}
