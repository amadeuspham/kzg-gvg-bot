using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.Core.Pairing;

/// <summary>What redeeming a connect code gave.</summary>
public abstract record CodeRedemption
{
    private CodeRedemption()
    {
    }

    public sealed record Redeemed(UserId User) : CodeRedemption;

    /// <summary>Never issued here, or so long ago it is forgotten: <c>badCode</c>.</summary>
    public sealed record Unknown : CodeRedemption;

    public sealed record Expired : CodeRedemption;

    public sealed record Used : CodeRedemption;
}

/// <summary>
/// The codes <c>/connect</c> hands out: each works once, within 10 minutes. Only hashes are kept, in
/// memory, so a restart forgets them and the player runs <c>/connect</c> again. Thread-safe.
/// </summary>
public sealed class ConnectCodes(ITimeSource time)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(10);

    private readonly Dictionary<string, Entry> _codes = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    /// <summary>Codes held now, used or not, for tests and diagnostics.</summary>
    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _codes.Count;
            }
        }
    }

    /// <summary>A fresh code for <paramref name="user"/> that tells the app to connect to <paramref name="host"/>.</summary>
    public string Issue(UserId user, string host)
    {
        var code = ConnectCode.Generate(host);
        lock (_gate)
        {
            Purge();
            _codes[SecretHash.Of(code.Secret)] = new Entry(user, time.Now + Lifetime);
        }
        return code.Format();
    }

    public CodeRedemption Redeem(ConnectCode code)
    {
        var hash = SecretHash.Of(code.Secret);
        lock (_gate)
        {
            Purge();
            if (!_codes.TryGetValue(hash, out var entry))
            {
                return new CodeRedemption.Unknown();
            }
            if (entry.Used)
            {
                return new CodeRedemption.Used();
            }
            if (time.Now >= entry.ExpiresAt)
            {
                return new CodeRedemption.Expired();
            }
            entry.Used = true;
            return new CodeRedemption.Redeemed(entry.User);
        }
    }

    // A used or expired code is remembered one more lifetime, so a retry hears why, not "bad code".
    private void Purge()
    {
        var now = time.Now;
        foreach (var hash in _codes.Where(pair => now >= pair.Value.ExpiresAt + Lifetime).Select(pair => pair.Key).ToList())
        {
            _codes.Remove(hash);
        }
    }

    private sealed class Entry(UserId user, TimeSpan expiresAt)
    {
        public UserId User { get; } = user;

        public TimeSpan ExpiresAt { get; } = expiresAt;

        public bool Used { get; set; }
    }
}
