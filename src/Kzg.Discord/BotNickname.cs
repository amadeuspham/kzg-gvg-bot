using Microsoft.Extensions.Logging;

namespace Kzg.Discord;

/// <summary>How a nickname change fared with Discord.</summary>
internal enum NicknameResult
{
    Set,

    /// <summary>The bot lacks Change Nickname in the server: it relays all the same, under its account name.</summary>
    Refused,

    /// <summary>Discord couldn't be reached or failed; the next sync tries again.</summary>
    Failed,
}

/// <summary>
/// Keeps one relay bot's server nickname on its team name, so the member list and the voice channel show
/// "A1 Broadcaster" rather than the bot account's own name. A refusal never stops the relay; it is reported in
/// <c>/gvg status</c> instead and tried again at the next sync, so granting the permission is enough.
/// </summary>
internal sealed class BotNickname(Func<string?, CancellationToken, Task<NicknameResult>> set, ILogger? log = null)
{
    private readonly SemaphoreSlim _syncing = new(1, 1);
    private volatile string? _wanted;
    private string? _current;
    private bool _changed;
    private volatile bool _refused;

    /// <summary>The name it should have, or null for a bot without a team channel, whose nickname this run set goes back to none.</summary>
    public string? Wanted
    {
        get => _wanted;
        set => _wanted = value;
    }

    /// <summary>The last try was refused for a missing permission.</summary>
    public bool Refused => _refused;

    /// <summary>Tells Discord the wanted name if it differs from the last one set. Never throws but for cancellation.</summary>
    public async Task SyncAsync(CancellationToken cancellationToken)
    {
        await _syncing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var wanted = _wanted;
            // A nickname this run never set is the admin's to keep.
            if (wanted == _current && (_changed || wanted is null))
            {
                _refused &= !_changed;
                return;
            }
            NicknameResult result;
            try
            {
                result = await set(wanted, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not (OperationCanceledException or OutOfMemoryException))
            {
                log?.LogDebug("setting a nickname failed: {Error}", exception.GetType().Name);
                result = NicknameResult.Failed;
            }
            switch (result)
            {
                case NicknameResult.Set:
                    (_current, _changed, _refused) = (wanted, true, false);
                    break;
                case NicknameResult.Refused:
                    if (!_refused)
                    {
                        log?.LogWarning("{Name} can't set its server nickname; it relays under its account name. Give it the Change Nickname permission", wanted ?? "a relay bot");
                    }
                    _refused = true;
                    break;
            }
        }
        finally
        {
            _syncing.Release();
        }
    }
}
