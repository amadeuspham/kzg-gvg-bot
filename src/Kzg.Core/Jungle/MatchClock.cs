using Kzg.Core.Abstractions;

namespace Kzg.Core.Jungle;

public enum ClockState { Idle, Running, Paused, Over }

/// <summary>
/// Mirrors the in-game GvG timer, which counts down from 35:00 to 00:00.
/// One anchor says "at instant t the game showed r"; everything else derives from it.
/// Not thread-safe: <see cref="JungleMatch"/> serialises access.
/// </summary>
public sealed class MatchClock(ITimeSource time)
{
    public static readonly TimeSpan MatchLength = TimeSpan.FromMinutes(35);

    private TimeSpan _anchorInstant;
    private TimeSpan _anchorGame = MatchLength;

    private ClockState _state = ClockState.Idle;

    public ClockState State
    {
        get
        {
            _ = GameTime; // moves Running to Over once the time has run out
            return _state;
        }
    }

    public TimeSpan GameTime
    {
        get
        {
            if (_state == ClockState.Running)
            {
                var remaining = _anchorGame - (time.Now - _anchorInstant);
                if (remaining <= TimeSpan.Zero)
                {
                    _state = ClockState.Over;
                    _anchorGame = TimeSpan.Zero;
                }
                else
                {
                    return remaining;
                }
            }

            return _anchorGame;
        }
    }

    public void Start() => Anchor(MatchLength);

    /// <summary>
    /// Sets the game time. Running stays running, Paused stays paused at the new time.
    /// From Idle or Over it starts the clock, so a bot started late can join a match in progress.
    /// </summary>
    public void Sync(TimeSpan gameTime)
    {
        var clamped = gameTime < TimeSpan.Zero ? TimeSpan.Zero : gameTime > MatchLength ? MatchLength : gameTime;
        if (_state == ClockState.Paused)
        {
            _anchorGame = clamped;
            return;
        }

        Anchor(clamped);
    }

    public void Pause()
    {
        var now = GameTime;
        if (_state == ClockState.Running)
        {
            _anchorGame = now;
            _state = ClockState.Paused;
        }
    }

    public void Resume()
    {
        if (_state == ClockState.Paused)
        {
            Anchor(_anchorGame);
        }
    }

    public void Stop()
    {
        _state = ClockState.Idle;
        _anchorGame = MatchLength;
    }

    private void Anchor(TimeSpan gameTime)
    {
        _anchorGame = gameTime;
        _anchorInstant = time.Now;
        _state = gameTime <= TimeSpan.Zero ? ClockState.Over : ClockState.Running;
    }
}
