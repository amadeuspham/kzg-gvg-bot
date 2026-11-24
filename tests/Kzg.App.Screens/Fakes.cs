using Kzg.App.Input;
using Kzg.App.Link;
using Kzg.App.Services;
using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.App.Screens;

internal sealed class ManualTime : ITimeSource
{
    public TimeSpan Now { get; private set; } = TimeSpan.FromHours(5);

    public void Advance(double seconds) => Now += TimeSpan.FromSeconds(seconds);
}

internal enum Reach
{
    Works,
    Fails,
    Hangs,
}

/// <summary>A transport the scene plays the bot through: it answers nothing by itself.</summary>
internal sealed class ScriptedTransport(Reach reach) : ILinkTransport
{
    public event Action<ReadOnlyMemory<byte>>? Received;

    public event Action<int?>? Closed;

    public Task ConnectAsync(Uri uri, CancellationToken cancellation) => reach switch
    {
        Reach.Works => Task.CompletedTask,
        Reach.Fails => Task.FromException(new HttpRequestException("unreachable")),
        _ => new TaskCompletionSource().Task,
    };

    public Task SendAsync(ReadOnlyMemory<byte> message) => Task.CompletedTask;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public void Receive(ServerMessage message) => Received?.Invoke(MessageCodec.Serialize(message));

    public void Close(int? code = null) => Closed?.Invoke(code);
}

internal sealed class ScriptedTransports : ILinkTransportFactory
{
    public Reach Reach { get; set; } = Reach.Works;

    public ScriptedTransport Latest { get; private set; } = null!;

    public ILinkTransport Create() => Latest = new ScriptedTransport(Reach);
}

internal sealed class ScriptedInput : IInputService
{
    public event Action<RawInput>? Input;

    public event Action? Changed;

    public HookHealth Hook { get; set; } = HookHealth.Ok;

    public string? HookDetail { get; set; }

    public PadHealth Pad { get; set; } = PadHealth.NotNeeded;

    public IReadOnlyList<string> Controllers { get; set; } = [];

    public void StartHook()
    {
    }

    public void StartPad()
    {
    }

    public void StopPad()
    {
    }

    public void Raise(InputBinding input, bool down) => Input?.Invoke(new RawInput(input, down));

    public void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
    }
}

internal sealed class ScriptedPermissions : IPermissionProbe
{
    public PermissionState InputMonitoring { get; set; } = PermissionState.NotApplicable;

    public void RequestPrompts()
    {
    }

    public void OpenInputMonitoringSettings()
    {
    }
}

internal sealed class ScriptedForeground : IForegroundProbe
{
    public ForegroundApp? Foreground { get; set; }

    public ForegroundApp? Read() => Foreground;
}

internal sealed class ScriptedPlatform : IPlatformServices
{
    public bool IsWindows { get; set; }

    public bool IsMac { get; set; }

    public bool IsElevated { get; set; }

    public void OpenUrl(string url)
    {
    }

    public bool TryRestartElevated() => false;

    public bool TryRestartApp() => false;

    public void Notify(string title, string text)
    {
    }

    public void Quit()
    {
    }
}

internal sealed class InlineUi : IUiThread
{
    public void Post(Action action) => action();
}

internal sealed class ScriptedStartAtLogin(bool enabled) : IStartAtLogin
{
    public bool IsAvailable => true;

    public bool IsEnabled { get; private set; } = enabled;

    public void Set(bool on) => IsEnabled = on;
}

internal sealed class ScriptedUpdater(UpdateStage stage, string? version = null) : IUpdater
{
    public UpdateStage Stage { get; } = stage;

    public string? ReadyVersion { get; } = version;

    public event Action? Changed
    {
        add { }
        remove { }
    }

    public Task CheckAsync() => Task.CompletedTask;

    public void RestartToUpdate()
    {
    }

    public void ApplyWhenQuitting()
    {
    }
}
