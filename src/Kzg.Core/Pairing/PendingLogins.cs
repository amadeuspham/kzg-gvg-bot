using Kzg.Core.Abstractions;

namespace Kzg.Core.Pairing;

/// <summary>How a "Sign in with Discord" ended for the app waiting on it.</summary>
public abstract record SignInOutcome
{
    private SignInOutcome()
    {
    }

    public sealed record Member(GuildMember Who) : SignInOutcome;

    /// <summary>Signed in as someone who isn't in the guild: <c>notMember</c>.</summary>
    public sealed record NotMember : SignInOutcome;
}

/// <summary>What an app's <c>hello{loginId}</c> gets.</summary>
public abstract record LoginClaim
{
    private LoginClaim()
    {
    }

    /// <summary>The app waits on <paramref name="Outcome"/> for at most <paramref name="Remaining"/>, when the login expires.</summary>
    public sealed record Waiting(Task<SignInOutcome> Outcome, TimeSpan Remaining) : LoginClaim;

    /// <summary>Another connection waits on it, or its outcome was already handed over: <c>used</c>.</summary>
    public sealed record Used : LoginClaim;

    public sealed record Expired : LoginClaim;
}

/// <summary>What the browser's <c>/auth/callback</c> may do with a <c>state</c>.</summary>
public enum CallbackClaim
{
    /// <summary>Go ahead: exchange Discord's code, then <see cref="PendingLogins.Complete"/> or <see cref="PendingLogins.Release"/>.</summary>
    Granted,

    /// <summary>No app is waiting on this login, or it never existed.</summary>
    Unknown,

    Expired,

    /// <summary>It has already signed in, or another callback is exchanging its code right now.</summary>
    Used,
}

/// <summary>
/// "Sign in with Discord" logins, keyed by the <c>loginId</c> the app made up: the app waits over its
/// WebSocket while its player signs in in the browser, where the <c>loginId</c> is the OAuth
/// <c>state</c>. Each works once, within 10 minutes; only hashes are kept. Thread-safe.
/// </summary>
public sealed class PendingLogins(ITimeSource time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Entry> _logins = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>
    /// An app's hello with a <c>loginId</c>. A new one starts the login; an app that reconnects with
    /// the same one waits again, or gets the outcome that came while it was away.
    /// </summary>
    public LoginClaim Claim(string loginIdHash)
    {
        lock (_gate)
        {
            Purge();
            if (!_logins.TryGetValue(loginIdHash, out var entry))
            {
                _logins[loginIdHash] = entry = new Entry(time.Now + Lifetime);
            }
            if (entry.Delivered || entry.Claimed)
            {
                return new LoginClaim.Used();
            }
            if (time.Now >= entry.ExpiresAt)
            {
                return new LoginClaim.Expired();
            }
            entry.Claimed = true;
            return new LoginClaim.Waiting(entry.Outcome.Task, entry.ExpiresAt - time.Now);
        }
    }

    /// <summary>The waiting app went away before the outcome; a reconnect with the same <c>loginId</c> may claim it again.</summary>
    public void Abandon(string loginIdHash)
    {
        lock (_gate)
        {
            if (_logins.TryGetValue(loginIdHash, out var entry))
            {
                entry.Claimed = false;
            }
        }
    }

    /// <summary>The app has the outcome; the login is spent.</summary>
    public void Deliver(string loginIdHash)
    {
        lock (_gate)
        {
            if (_logins.TryGetValue(loginIdHash, out var entry))
            {
                entry.Delivered = true;
            }
        }
    }

    /// <summary>Whether <c>/auth/start</c> may send the browser to Discord for this login.</summary>
    public CallbackClaim CanStart(string loginIdHash)
    {
        lock (_gate)
        {
            Purge();
            return !_logins.TryGetValue(loginIdHash, out var entry) ? CallbackClaim.Unknown
                : entry.Done || entry.Delivered ? CallbackClaim.Used
                : time.Now >= entry.ExpiresAt ? CallbackClaim.Expired
                : CallbackClaim.Granted;
        }
    }

    /// <summary>A callback takes the login to exchange Discord's code; only one at a time can.</summary>
    public CallbackClaim BeginCallback(string loginIdHash)
    {
        lock (_gate)
        {
            var claim = CanStart(loginIdHash);
            if (claim == CallbackClaim.Granted)
            {
                if (_logins[loginIdHash].Exchanging)
                {
                    return CallbackClaim.Used;
                }
                _logins[loginIdHash].Exchanging = true;
            }
            return claim;
        }
    }

    /// <summary>The exchange failed for a reason the player can retry: the login waits for another try.</summary>
    public void Release(string loginIdHash)
    {
        lock (_gate)
        {
            if (_logins.TryGetValue(loginIdHash, out var entry))
            {
                entry.Exchanging = false;
            }
        }
    }

    /// <summary>The callback found out who signed in; the waiting app hears it.</summary>
    public void Complete(string loginIdHash, SignInOutcome outcome)
    {
        lock (_gate)
        {
            if (_logins.TryGetValue(loginIdHash, out var entry) && entry.Exchanging)
            {
                (entry.Exchanging, entry.Done) = (false, true);
                entry.Outcome.TrySetResult(outcome);
            }
        }
    }

    // A spent or expired login is remembered one more lifetime, so a retry hears why.
    private void Purge()
    {
        var now = time.Now;
        foreach (var hash in _logins.Where(pair => now >= pair.Value.ExpiresAt + Lifetime).Select(pair => pair.Key).ToList())
        {
            _logins[hash].Outcome.TrySetCanceled();
            _logins.Remove(hash);
        }
    }

    private sealed class Entry(TimeSpan expiresAt)
    {
        public TimeSpan ExpiresAt { get; } = expiresAt;

        // Run on the thread pool, never inline under the lock: the waiting app sends its welcome then.
        public TaskCompletionSource<SignInOutcome> Outcome { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Claimed { get; set; }

        public bool Exchanging { get; set; }

        public bool Done { get; set; }

        public bool Delivered { get; set; }
    }
}
