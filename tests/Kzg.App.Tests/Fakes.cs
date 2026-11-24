using Kzg.App.Input;
using Kzg.App.Link;
using Kzg.App.Services;
using Kzg.Protocol;

namespace Kzg.App.Tests;

/// <summary>A transport that records what the app sends and lets a test play the bot.</summary>
internal sealed class FakeTransport : ILinkTransport
{
    public List<ClientMessage> Sent { get; } = [];

    public Uri? ConnectedTo { get; private set; }

    public bool Disposed { get; private set; }

    /// <summary>Set to make the connect fail.</summary>
    public Exception? ConnectFailure { get; set; }

    /// <summary>Set to hold the connect until the test completes it.</summary>
    public TaskCompletionSource? ConnectGate { get; set; }

    public event Action<ReadOnlyMemory<byte>>? Received;

    public event Action<int?>? Closed;

    public async Task ConnectAsync(Uri uri, CancellationToken cancellation)
    {
        ConnectedTo = uri;
        if (ConnectFailure is not null)
        {
            throw ConnectFailure;
        }
        if (ConnectGate is not null)
        {
            await ConnectGate.Task;
        }
    }

    /// <summary>Set to make every send fail.</summary>
    public Exception? SendFailure { get; set; }

    public Task SendAsync(ReadOnlyMemory<byte> message)
    {
        if (SendFailure is not null)
        {
            throw SendFailure;
        }
        var parsed = MessageCodec.ParseClient(message.Span);
        Sent.Add(Assert.IsType<MessageParse<ClientMessage>.Parsed>(parsed).Message);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }

    public void Receive(ServerMessage message) => Received?.Invoke(MessageCodec.Serialize(message));

    public void ReceiveRaw(string json) => Received?.Invoke(System.Text.Encoding.UTF8.GetBytes(json));

    public void Close(int? code = null) => Closed?.Invoke(code);

    public IEnumerable<T> SentOf<T>()
        where T : ClientMessage => Sent.OfType<T>();
}

internal sealed class FakeTransportFactory : ILinkTransportFactory
{
    public List<FakeTransport> Created { get; } = [];

    /// <summary>Applied to each new transport before it connects.</summary>
    public Action<FakeTransport>? Configure { get; set; }

    public FakeTransport Latest => Created[^1];

    public ILinkTransport Create()
    {
        var transport = new FakeTransport();
        Configure?.Invoke(transport);
        Created.Add(transport);
        return transport;
    }
}

internal static class Messages
{
    public static WelcomeMessage Welcome(string? token = null, string name = "Alex") =>
        new("123456789012345678", name, 2000, 6000, "1.0", token);

    public static StatusMessage Status(
        string? channel = "Attack 1",
        WireGroup? group = WireGroup.Attack,
        WireAction[]? active = null,
        bool openMic = false,
        string[]? targets = null,
        StatusWarning[]? warnings = null,
        string[]? botDownIn = null) =>
        new(active ?? [], openMic, targets ?? ["Attack 2", "Attack 3"], warnings ?? [], channel, group, botDownIn);

    public static ErrorMessage Error(ErrorCode code) => new(code, "message for a person");
}

internal sealed class FakeInputService : IInputService
{
    public event Action<RawInput>? Input;

    public event Action? Changed;

    public HookHealth Hook { get; set; } = HookHealth.Ok;

    public string? HookDetail { get; set; }

    public PadHealth Pad { get; set; } = PadHealth.NotNeeded;

    public IReadOnlyList<string> Controllers { get; set; } = [];

    public int HookStarts { get; private set; }

    public int PadStarts { get; private set; }

    public int PadStops { get; private set; }

    public void StartHook() => HookStarts++;

    public void StartPad() => PadStarts++;

    public void StopPad() => PadStops++;

    public void Raise(InputBinding input, bool down) => Input?.Invoke(new RawInput(input, down));

    public void RaiseChanged() => Changed?.Invoke();

    public void Dispose()
    {
    }
}

internal sealed class FakePermissions : IPermissionProbe
{
    public PermissionState InputMonitoring { get; set; } = PermissionState.NotApplicable;

    public int Prompts { get; private set; }

    public int OpenedInputMonitoring { get; private set; }

    public void RequestPrompts() => Prompts++;

    public void OpenInputMonitoringSettings() => OpenedInputMonitoring++;
}

/// <summary>The window the player is in, as a test sets it. Null is "no readable foreground window".</summary>
internal sealed class FakeForegroundProbe : IForegroundProbe
{
    public ForegroundApp? Foreground { get; set; }

    public int Reads { get; private set; }

    public ForegroundApp? Read()
    {
        Reads++;
        return Foreground;
    }
}

internal sealed class FakePlatform : IPlatformServices
{
    public bool IsWindows { get; set; }

    public bool IsMac { get; set; }

    public bool IsElevated { get; set; }

    public bool ElevatedRestartWorks { get; set; } = true;

    public bool AppRestartWorks { get; set; } = true;

    public int AppRestarts { get; private set; }

    public List<string> OpenedUrls { get; } = [];

    public List<string> Notifications { get; } = [];

    public int Quits { get; private set; }

    public int ElevatedRestarts { get; private set; }

    public void OpenUrl(string url) => OpenedUrls.Add(url);

    public bool TryRestartElevated()
    {
        ElevatedRestarts++;
        return ElevatedRestartWorks;
    }

    public bool TryRestartApp()
    {
        AppRestarts++;
        return AppRestartWorks;
    }

    public void Notify(string title, string text) => Notifications.Add(text);

    public void Quit() => Quits++;
}

internal sealed class InlineUi : IUiThread
{
    public void Post(Action action) => action();
}

internal sealed class FakeStartAtLogin : IStartAtLogin
{
    public bool IsAvailable { get; set; } = true;

    public bool IsEnabled { get; private set; }

    public bool Fails { get; set; }

    public void Set(bool enabled)
    {
        if (Fails)
        {
            throw new IOException("denied");
        }
        IsEnabled = enabled;
    }
}

internal sealed class FakeUpdater : IUpdater
{
    public UpdateStage Stage { get; set; } = UpdateStage.Idle;

    public string? ReadyVersion { get; set; }

    public int Checks { get; private set; }

    public int Restarts { get; private set; }

    public event Action? Changed;

    public Task CheckAsync()
    {
        Checks++;
        return Task.CompletedTask;
    }

    public void RestartToUpdate() => Restarts++;

    public void ApplyWhenQuitting()
    {
    }

    public void Raise() => Changed?.Invoke();
}
