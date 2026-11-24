using Kzg.App.Input;
using Kzg.App.Link;
using Kzg.App.Services;
using Kzg.App.Settings;
using Kzg.App.ViewModels;
using Kzg.Protocol;
using SharpHook.Data;

namespace Kzg.App.Screens;

/// <summary>One window state to draw: a name for the file, and the view model that is in that state.</summary>
internal sealed record Scene(string Name, MainViewModel Model);

/// <summary>
/// Builds the real view model in each situation by playing the bot's messages at the real connection
/// controller, so every picture is what the app would show, not a mock of it.
/// </summary>
internal sealed class SceneBuilder : IDisposable
{
    private const string Host = "gvg.example.com";

    private static readonly InputBinding F7 = InputBinding.Key(KeyCode.VcF7);
    private static readonly InputBinding F8 = InputBinding.Key(KeyCode.VcF8);
    private static readonly InputBinding F9 = InputBinding.Key(KeyCode.VcF9);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-screens-" + Guid.NewGuid().ToString("N"));
    private readonly List<MainViewModel> _built = [];
    private ManualTime _time = new();
    private ScriptedTransports _transports = new();
    private ScriptedInput _input = new();
    private ScriptedPermissions _permissions = new();
    private ScriptedPlatform _platform = new();
    private ScriptedForeground _foreground = new();
    private ScriptedStartAtLogin? _login;
    private ScriptedUpdater? _updater;

    public void Dispose()
    {
        foreach (var model in _built)
        {
            model.Dispose();
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    public IEnumerable<Scene> All()
    {
        yield return Make("01-not-linked-first-run", () => Build(linked: false));
        yield return Make("02-signing-in-with-discord", () =>
        {
            var model = Build(linked: false);
            model.ServerHostText = Host;
            model.SignInWithDiscordCommand.Execute(null);
            return model;
        });
        yield return Make("03-connecting", () =>
        {
            _transports.Reach = Reach.Hangs;
            return Build();
        });
        yield return Make("04-reconnecting", () =>
        {
            var model = Online();
            Drop();
            Pass(model, 4);
            return model;
        });
        yield return Make("05-disconnected-server-unreachable", () =>
        {
            var model = Online();
            Drop();
            Pass(model, 4);
            Pass(model, 12);
            return model;
        });
        yield return Make("06-disconnected-link-expired", () =>
        {
            var model = Online();
            _transports.Latest.Receive(Error(ErrorCode.Expired));
            return model;
        });
        yield return Make("07-disconnected-link-revoked", () =>
        {
            var model = Online();
            _transports.Latest.Receive(Error(ErrorCode.Revoked));
            return model;
        });
        yield return Make("08-disconnected-linked-on-another-device", () =>
        {
            var model = Online();
            _transports.Latest.Close(4000);
            Pass(model, 1);
            return model;
        });
        yield return Make("09-connected-player", () => Online());
        yield return Make("10-open-mic-off-beside-a-warning", () =>
        {
            var model = Online();
            _transports.Latest.Receive(Status(warnings: [StatusWarning.BotDown], botDownIn: ["Attack 2"]));
            return model;
        });
        yield return Make("11-open-mic-on", () =>
        {
            var model = Online();
            model.OpenMicIsOn = true;
            _transports.Latest.Receive(Status(active: [WireAction.OpenMic], openMic: true));
            return model;
        });
        yield return Make("28-broadcasting-all-teams", () =>
        {
            var model = Online();
            _input.Raise(F7, true);
            _transports.Latest.Receive(Status(active: [WireAction.AllTeams], targets: ["Attack 2", "Attack 3", "Defense 1", "Defense 2"]));
            return model;
        });
        yield return Make("29-all-teams-key-not-set", () => Online(bindings: new BindingSet(F8, null, F9)));
        yield return Make("30-all-keys-not-set", () => Online(bindings: new BindingSet(null, null, null)));
        yield return Make("31-all-keys-not-set-open-mic-on", () =>
        {
            var model = Online(bindings: new BindingSet(null, null, null));
            model.OpenMicIsOn = true;
            _transports.Latest.Receive(Status(active: [WireAction.OpenMic], openMic: true));
            return model;
        });
        yield return Make("32-rebind-open-mic-key-waiting", () =>
        {
            var model = Online();
            model.ChangeOpenMicCommand.Execute(null);
            return model;
        });
        yield return Make("12-broadcasting-team", () =>
        {
            var model = Online();
            _input.Raise(F8, true);
            return model;
        });
        yield return Make("13-rebind-waiting", () =>
        {
            var model = Online();
            model.ChangeBroadcastCommand.Execute(null);
            return model;
        });
        yield return Make("14-input-is-a-key", () => Online(bindings: new BindingSet(InputBinding.Key(KeyCode.VcLeftShift), F7, F9)));
        yield return Make("15-input-is-a-mouse-button", () => Online(bindings: new BindingSet(InputBinding.Mouse(MouseButton.Button4), F7, F9)));
        yield return Make("16-input-is-a-controller-button", () => Online(bindings: new BindingSet(InputBinding.Pad("rightshoulder"), F7, F9)));
        yield return Make("17-warning-not-in-team-voice-channel", () =>
        {
            var model = Online();
            _transports.Latest.Receive(Status(channel: null, group: null, targets: [], warnings: [StatusWarning.NotInTeamVc]));
            return model;
        });
        yield return Make("18-warning-bot-down", () =>
        {
            var model = Online();
            _transports.Latest.Receive(Status(warnings: [StatusWarning.BotDown], botDownIn: ["Attack 2"]));
            return model;
        });
        yield return Make("24-warning-two-bots-down", () =>
        {
            var model = Online();
            _transports.Latest.Receive(Status(warnings: [StatusWarning.BotDown], botDownIn: ["Attack 2", "Attack 3"]));
            return model;
        });
        yield return Make("19-warning-input-stopped", () =>
        {
            var model = Online();
            _input.Hook = HookHealth.Failed;
            _input.HookDetail = "macOS switched the key listener off.";
            _input.Pad = PadHealth.Failed;
            _input.RaiseChanged();
            return model;
        });
        yield return Make("20-warning-windows-administrator", () =>
        {
            _platform.IsWindows = true;
            _foreground.Foreground = new ForegroundApp(4242, IsElevated: true);
            return Online();
        });
        yield return Make("21-settings-open", () =>
        {
            var model = Online();
            model.ShowSettings = true;
            return model;
        });
        yield return Make("23-two-warnings", () =>
        {
            var model = Online();
            _transports.Latest.Receive(Status(warnings: [StatusWarning.BotDown]));
            _input.Hook = HookHealth.Failed;
            _input.HookDetail = "macOS switched the key listener off.";
            _input.RaiseChanged();
            return model;
        });
        yield return Make("25-settings-open-at-login-and-updates", () =>
        {
            _login = new ScriptedStartAtLogin(enabled: true);
            _updater = new ScriptedUpdater(UpdateStage.Idle);
            var model = Online();
            model.ShowSettings = true;
            return model;
        });
        yield return Make("26-update-ready", () =>
        {
            _updater = new ScriptedUpdater(UpdateStage.Ready, "0.2.0");
            return Online();
        });
        yield return Make("27-update-ready-beside-a-warning", () =>
        {
            _updater = new ScriptedUpdater(UpdateStage.Ready, "0.2.0");
            var model = Online();
            _transports.Latest.Receive(Status(warnings: [StatusWarning.BotDown]));
            return model;
        });
        yield return Make("22-macos-permissions-needed", () =>
        {
            _platform.IsMac = true;
            _permissions.InputMonitoring = PermissionState.Denied;
            _input.Hook = HookHealth.NeedsAccessibility;
            var model = Online();
            _input.RaiseChanged();
            return model;
        });
    }

    private Scene Make(string name, Func<MainViewModel> arrange)
    {
        // Every scene starts from a clean world.
        _time = new ManualTime();
        _transports = new ScriptedTransports();
        _input = new ScriptedInput();
        _permissions = new ScriptedPermissions();
        _platform = new ScriptedPlatform();
        _foreground = new ScriptedForeground();
        _login = null;
        _updater = null;
        return new Scene(name, arrange());
    }

    private MainViewModel Build(bool linked = true, BindingSet? bindings = null)
    {
        var settings = AppSettings.CreateDefault() with
        {
            ServerHost = Host,
            DeviceToken = linked ? WireSecret.Generate() : null,
            Bindings = bindings ?? BindingSet.Default,
        };
        var store = new SettingsStore(Path.Combine(_folder, Guid.NewGuid().ToString("N"), "settings.json"));
        var connection = new ConnectionController(_transports, _time, settings.InstallId, "0.1.0");
        var model = new MainViewModel(settings, store, connection, _input, _permissions, _platform, new InlineUi(), _time, _foreground, _login, _updater);
        _built.Add(model);
        model.Start();
        return model;
    }

    private MainViewModel Online(BindingSet? bindings = null)
    {
        var model = Build(bindings: bindings);
        _transports.Latest.Receive(new WelcomeMessage("123456789012345678", "Alex", 2000, 6000, "0.1.0"));
        _transports.Latest.Receive(Status());
        _time.Advance(2);
        model.Tick();
        _time.Advance(0.024);
        _transports.Latest.Receive(new HeartbeatAckMessage(1, (long)(_time.Now.TotalMilliseconds - 24)));
        return model;
    }

    private void Drop()
    {
        _transports.Reach = Reach.Fails;
        _transports.Latest.Close();
    }

    private void Pass(MainViewModel model, double seconds)
    {
        _time.Advance(seconds);
        model.Tick();
    }

    private static StatusMessage Status(
        string? channel = "Attack 1",
        WireGroup? group = WireGroup.Attack,
        WireAction[]? active = null,
        bool openMic = false,
        string[]? targets = null,
        StatusWarning[]? warnings = null,
        string[]? botDownIn = null) =>
        new(active ?? [], openMic, targets ?? ["Attack 2", "Attack 3"], warnings ?? [], channel, group, botDownIn);

    private static ErrorMessage Error(ErrorCode code) => new(code, "for a person");
}
