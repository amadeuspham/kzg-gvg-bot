using Velopack;
using Velopack.Sources;

namespace Kzg.App.Services;

public enum UpdateStage
{
    /// <summary>No feed is set, or this is not an installed copy: the app never looks for updates.</summary>
    Off,
    Idle,
    Checking,
    Ready,
    Failed,
}

/// <summary>Automatic updates. Everything about them is quiet until a new version is downloaded and waiting.</summary>
public interface IUpdater
{
    UpdateStage Stage { get; }

    /// <summary>The version that is downloaded and waiting for a restart, when <see cref="Stage"/> is <see cref="UpdateStage.Ready"/>.</summary>
    string? ReadyVersion { get; }

    /// <summary>Raised on a background thread when the stage changes.</summary>
    event Action? Changed;

    /// <summary>Looks for a newer version and downloads it. Does nothing while off, or while a check or a waiting update exists. Never throws.</summary>
    Task CheckAsync();

    /// <summary>Installs the waiting update and starts the app again. The process ends.</summary>
    void RestartToUpdate();

    /// <summary>Has the waiting update installed when the app quits by itself, with no restart.</summary>
    void ApplyWhenQuitting();
}

/// <summary>The part of an update that touches Velopack, so the rest is tested without a feed.</summary>
public interface IUpdateEngine
{
    /// <summary>Looks at the feed and downloads a newer version; its version, or null when this one is the newest.</summary>
    Task<string?> DownloadNewerAsync();

    void ApplyAndRestart();

    void ApplyOnExit();
}

/// <inheritdoc />
public sealed class UpdateService : IUpdater
{
    private readonly IUpdateEngine? _engine;
    private readonly object _gate = new();
    private UpdateStage _stage;

    /// <param name="engine">Null turns updates off.</param>
    public UpdateService(IUpdateEngine? engine)
    {
        _engine = engine;
        _stage = engine is null ? UpdateStage.Off : UpdateStage.Idle;
    }

    public event Action? Changed;

    public UpdateStage Stage
    {
        get
        {
            lock (_gate)
            {
                return _stage;
            }
        }
    }

    public string? ReadyVersion { get; private set; }

    public async Task CheckAsync()
    {
        if (_engine is null || !Enter(UpdateStage.Checking, from: [UpdateStage.Idle, UpdateStage.Failed]))
        {
            return;
        }
        try
        {
            var version = await _engine.DownloadNewerAsync().ConfigureAwait(false);
            ReadyVersion = version;
            Set(version is null ? UpdateStage.Idle : UpdateStage.Ready);
        }
        catch (Exception)
        {
            // A third-party feed and the network: whatever goes wrong means "couldn't check", and the next try is another day.
            Set(UpdateStage.Failed);
        }
    }

    public void RestartToUpdate()
    {
        if (Stage == UpdateStage.Ready)
        {
            _engine!.ApplyAndRestart();
        }
    }

    public void ApplyWhenQuitting()
    {
        if (Stage == UpdateStage.Ready)
        {
            _engine!.ApplyOnExit();
        }
    }

    private bool Enter(UpdateStage next, UpdateStage[] from)
    {
        lock (_gate)
        {
            if (!from.Contains(_stage))
            {
                return false;
            }
            _stage = next;
        }
        Changed?.Invoke();
        return true;
    }

    private void Set(UpdateStage next)
    {
        lock (_gate)
        {
            _stage = next;
        }
        Changed?.Invoke();
    }
}

/// <summary>Where updates come from. The address is set when the app is built, or by the <c>KZG_UPDATE_FEED</c> environment variable.</summary>
public static class UpdateFeed
{
    public const string Variable = "KZG_UPDATE_FEED";

    /// <summary>The configured feed address, or null when there is none (updates stay off).</summary>
    public static string? Configured() =>
        Normalize(Environment.GetEnvironmentVariable(Variable)) ?? Normalize(AppInfo.UpdateFeed);

    /// <summary>A trimmed https address, or null: anything else (a file path, plain http) is not a feed this app will read.</summary>
    public static string? Normalize(string? text)
    {
        var trimmed = text?.Trim();
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps ? trimmed : null;
    }

    /// <summary>A GitHub repository address reads its releases; any other address is a folder of Velopack files.</summary>
    public static IUpdateSource CreateSource(string feed) =>
        new Uri(feed).Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? new GithubSource(feed, accessToken: null, prerelease: false)
            : new SimpleWebSource(feed);

    /// <summary>The updater for this run: off unless a feed is set and this is a copy the installer put there.</summary>
    public static IUpdater ForThisProcess()
    {
        if (Configured() is not { } feed)
        {
            return new UpdateService(null);
        }
        var manager = new UpdateManager(CreateSource(feed));
        return new UpdateService(manager.IsInstalled ? new VelopackEngine(manager) : null);
    }
}

internal sealed class VelopackEngine(UpdateManager manager) : IUpdateEngine
{
    public async Task<string?> DownloadNewerAsync()
    {
        if (manager.UpdatePendingRestart is { } pending)
        {
            return pending.Version.ToString();
        }
        var found = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
        if (found is null)
        {
            return null;
        }
        await manager.DownloadUpdatesAsync(found).ConfigureAwait(false);
        return found.TargetFullRelease.Version.ToString();
    }

    public void ApplyAndRestart()
    {
        if (manager.UpdatePendingRestart is { } pending)
        {
            manager.ApplyUpdatesAndRestart(pending);
        }
    }

    public void ApplyOnExit()
    {
        if (manager.UpdatePendingRestart is { } pending)
        {
            manager.WaitExitThenApplyUpdates(pending, silent: true, restart: false);
        }
    }
}
