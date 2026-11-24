using Kzg.Core.Abstractions;

namespace Kzg.Core.Jungle;

/// <summary>
/// Runs the match: commands change the clock, and every <see cref="TickEvery"/> the due calls are played.
/// Commands arrive on the Discord thread and the tick on the host's timer, so the clock is only touched
/// under a lock. Nothing is posted on its own: over-the-match and nobody-heard events go to the log,
/// and <see cref="JungleCommandKind.Status"/> and <see cref="JungleCommandKind.Test"/> report on request.
/// </summary>
/// <param name="play">Queues a clip for the channels; false when it cannot take one now.</param>
/// <param name="reach">How many team channels a clip would reach now.</param>
public sealed class JungleMatch
{
    public static readonly TimeSpan TickEvery = TimeSpan.FromMilliseconds(100);

    private readonly Lock _gate = new();
    private readonly MatchClock _clock;
    private readonly CallSchedule _schedule;
    private readonly Func<JungleClip, bool> _play;
    private readonly Func<int> _reach;
    private readonly Action<string>? _log;
    private bool _overAnnounced;

    public JungleMatch(ITimeSource time, Func<JungleClip, bool> play, Func<int> reach, Action<string>? log = null)
    {
        _clock = new MatchClock(time);
        _schedule = new CallSchedule(_clock);
        _play = play;
        _reach = reach;
        _log = log;
    }

    /// <summary>Applies a command and returns the reply text.</summary>
    public string Handle(JungleCommand command)
    {
        if (command.Kind == JungleCommandKind.Test)
        {
            return Test();
        }

        lock (_gate)
        {
            _overAnnounced = false;
            switch (command.Kind)
            {
                case JungleCommandKind.Start:
                    _clock.Start();
                    return $"Clock started at {JungleTime.Format(MatchClock.MatchLength)}. {NextCall()}";
                case JungleCommandKind.Sync:
                    if (command.Time is not { } at)
                    {
                        return JungleTime.SyncUsage;
                    }

                    _clock.Sync(at);
                    _overAnnounced = _clock.State == ClockState.Over;
                    return $"Synced to {JungleTime.Format(at)}{(_clock.State == ClockState.Paused ? " (paused)" : "")}. {NextCall()}";
                case JungleCommandKind.Pause:
                    if (_clock.State != ClockState.Running)
                    {
                        return _clock.State == ClockState.Paused ? "Already paused." : "The clock is not running.";
                    }

                    _clock.Pause();
                    return $"Paused at {JungleTime.Format(_clock.GameTime)}.";
                case JungleCommandKind.Resume:
                    if (_clock.State != ClockState.Paused)
                    {
                        return "The clock is not paused.";
                    }

                    _clock.Resume();
                    return $"Resumed at {JungleTime.Format(_clock.GameTime)}. {NextCall()}";
                case JungleCommandKind.Stop:
                    _clock.Stop();
                    return "Stopped.";
                case JungleCommandKind.Status:
                    _overAnnounced = _clock.State == ClockState.Over;
                    return Status();
                default:
                    throw new ArgumentOutOfRangeException(nameof(command), command.Kind, "unknown jungle command");
            }
        }
    }

    /// <summary>Plays every call that is due now. Called every <see cref="TickEvery"/>.</summary>
    public void Tick()
    {
        IReadOnlyList<JungleCall> due;
        bool over;
        lock (_gate)
        {
            due = _schedule.TakeDue();
            over = _clock.State == ClockState.Over && !_overAnnounced;
            _overAnnounced |= over;
        }

        foreach (var call in due)
        {
            var at = JungleTime.Format(call.At);
            if (_reach() == 0)
            {
                Log($"Call {at} reached nobody: no team channel has a relay bot in it.");
            }
            else if (_play(call.Clip))
            {
                Log($"Call {at}: {call.Clip}.");
            }
            else
            {
                Log($"Call {at} dropped: a call is still playing.");
            }
        }

        if (over)
        {
            Log("Match over.");
        }
    }

    private string Test()
    {
        var channels = _reach();
        if (channels == 0)
        {
            return Logged("No team channel has a relay bot in it now, so nobody heard it.");
        }

        return _play(JungleClip.Jungle20)
            ? Logged($"Test call sent to {channels} team {(channels == 1 ? "channel" : "channels")}.")
            : Logged("A call is playing. Try again in a few seconds.");
    }

    // Callers hold the lock.
    private string Status()
    {
        var clock = JungleTime.Format(_clock.GameTime);
        return _clock.State switch
        {
            ClockState.Running => $"Running, clock {clock}. {NextCall()}",
            ClockState.Paused => $"Paused at {clock}. {NextCall()}",
            ClockState.Over => "Match over.",
            _ => "Stopped. Run /jungle start, or /jungle sync MM:SS to join a match in progress.",
        };
    }

    // Callers hold the lock.
    private string NextCall() =>
        _schedule.Next is { } next ? $"Next call at {JungleTime.Format(next.At)}." : "No calls left.";

    private string Logged(string text)
    {
        Log(text);
        return text;
    }

    private void Log(string text) => _log?.Invoke(text);
}
