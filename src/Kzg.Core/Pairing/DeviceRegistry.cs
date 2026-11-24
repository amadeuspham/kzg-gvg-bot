using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Protocol;

namespace Kzg.Core.Pairing;

/// <summary>What signing in with a device token gave.</summary>
public abstract record DeviceAuth
{
    private DeviceAuth()
    {
    }

    public sealed record Valid(DeviceRecord Device) : DeviceAuth;

    /// <summary>Never issued, revoked, or unlinked: <c>revoked</c>, and the player links again.</summary>
    public sealed record Unknown : DeviceAuth;

    /// <summary>Unused for longer than its lifetime, and now forgotten: <c>expired</c>.</summary>
    public sealed record Expired : DeviceAuth;
}

/// <summary>
/// The long-lived device tokens linked apps sign in with. Only each token's hash is kept, in
/// <c>devices.json</c>, so tokens survive restarts and the file signs no one in. A token lasts while
/// it is used; idle past its lifetime it expires. Times are wall-clock. Thread-safe.
/// </summary>
public sealed class DeviceRegistry
{
    private readonly Dictionary<string, DeviceRecord> _devices = new(StringComparer.Ordinal);
    private readonly Func<IReadOnlyList<DeviceRecord>, bool> _save;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();

    /// <param name="devices">What <c>devices.json</c> held at start.</param>
    /// <param name="save">Writes every record; false when the disk refused, to be tried again on the next change.</param>
    /// <param name="idleLifetime">How long an unused token lasts: the config's <c>deviceTokenDays</c>.</param>
    public DeviceRegistry(IReadOnlyList<DeviceRecord> devices, Func<IReadOnlyList<DeviceRecord>, bool> save, TimeProvider clock, TimeSpan idleLifetime)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(idleLifetime, TimeSpan.Zero);
        foreach (var device in devices)
        {
            _devices[device.TokenHash] = device;
        }
        (_save, _clock, IdleLifetime) = (save, clock, idleLifetime);
    }

    /// <summary>A registry that writes <c>devices.json</c> through <paramref name="store"/>.</summary>
    public static DeviceRegistry Over(DevicesStore store, IReadOnlyList<DeviceRecord> devices, TimeProvider clock, TimeSpan idleLifetime) =>
        new(devices, records => store.Save(records) is FileSave.Saved, clock, idleLifetime);

    public TimeSpan IdleLifetime { get; }

    /// <summary>The last write failed: the tokens work now but would be lost on a restart until a write succeeds.</summary>
    public bool Unsaved { get; private set; }

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _devices.Count;
            }
        }
    }

    /// <summary>
    /// A new token for <paramref name="user"/> on the install <paramref name="installId"/>, as the wire
    /// spells it. It replaces any token that install had, so relinking leaves no stray token behind.
    /// </summary>
    public string Issue(UserId user, Guid installId)
    {
        var token = WireSecret.Generate();
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            foreach (var old in _devices.Values.Where(d => d.InstallId == installId).Select(d => d.TokenHash).ToList())
            {
                _devices.Remove(old);
            }
            var hash = SecretHash.OfWire(token)!;
            _devices[hash] = new DeviceRecord(hash, user, installId, now, now);
            Save();
        }
        return token;
    }

    /// <summary>Signs in with a token; a valid one counts as used now.</summary>
    public DeviceAuth Authenticate(string token)
    {
        if (SecretHash.OfWire(token) is not { } hash)
        {
            return new DeviceAuth.Unknown();
        }
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            if (!_devices.TryGetValue(hash, out var device))
            {
                return new DeviceAuth.Unknown();
            }
            if (now - device.LastSeenAt >= IdleLifetime)
            {
                _devices.Remove(hash);
                Save();
                return new DeviceAuth.Expired();
            }
            // A clock set back never makes a token look used before it was issued.
            var seen = device with { LastSeenAt = now > device.LastSeenAt ? now : device.LastSeenAt };
            _devices[hash] = seen;
            Save();
            return new DeviceAuth.Valid(seen);
        }
    }

    /// <summary>Forgets one token, as unlinking from the app does; false when it wasn't known.</summary>
    public bool Revoke(string tokenHash)
    {
        lock (_gate)
        {
            if (!_devices.Remove(tokenHash))
            {
                return false;
            }
            Save();
            return true;
        }
    }

    /// <summary>Forgets every token of a player, as <c>/gvg revoke user</c> does; returns how many.</summary>
    public int RevokeUser(UserId user)
    {
        lock (_gate)
        {
            var hashes = _devices.Values.Where(d => d.UserId == user).Select(d => d.TokenHash).ToList();
            foreach (var hash in hashes)
            {
                _devices.Remove(hash);
            }
            if (hashes.Count > 0)
            {
                Save();
            }
            return hashes.Count;
        }
    }

    /// <summary>Forgets tokens idle past their lifetime; the host may call it now and then.</summary>
    public int Sweep()
    {
        var now = _clock.GetUtcNow();
        lock (_gate)
        {
            var stale = _devices.Values.Where(d => now - d.LastSeenAt >= IdleLifetime).Select(d => d.TokenHash).ToList();
            foreach (var hash in stale)
            {
                _devices.Remove(hash);
            }
            if (stale.Count > 0)
            {
                Save();
            }
            return stale.Count;
        }
    }

    public IReadOnlyList<DeviceRecord> Snapshot()
    {
        lock (_gate)
        {
            return [.. _devices.Values.OrderBy(d => d.IssuedAt).ThenBy(d => d.TokenHash, StringComparer.Ordinal)];
        }
    }

    private void Save() => Unsaved = !_save([.. _devices.Values.OrderBy(d => d.IssuedAt).ThenBy(d => d.TokenHash, StringComparer.Ordinal)]);
}
