using System.Windows.Input;
using Kzg.App.Input;
using Kzg.App.Link;
using Kzg.App.Services;
using Kzg.App.Settings;
using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.App.ViewModels;

/// <summary>
/// Everything the window shows and does, with no UI types in it: the status badge, the warnings, the link
/// screen, the two broadcast keys, open mic and the settings. It owns the pipe from raw input to the
/// connection: input, then <see cref="BindingMatcher"/>, then <see cref="ConnectionController"/>. Lives
/// on the UI thread; <see cref="Tick"/> is called about four times a second.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan PermissionPoll = TimeSpan.FromSeconds(1);

    // A quiet look for a new version: soon after the start, then a few times a day.
    private static readonly TimeSpan UpdateFirstCheck = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan UpdateEvery = TimeSpan.FromHours(6);

    // Clicking a key chip is itself a left click the hook reports a moment later; it must not be taken as the new binding.
    private static readonly TimeSpan ClickEcho = TimeSpan.FromMilliseconds(400);

    private readonly SettingsStore _store;
    private readonly ConnectionController _connection;
    private readonly IInputService _input;
    private readonly IPermissionProbe _permissions;
    private readonly IPlatformServices _platform;
    private readonly IUiThread _ui;
    private readonly ITimeSource _time;
    private readonly BindingMatcher _matcher;
    private readonly ElevationWatch _elevation;
    private readonly IStartAtLogin? _login;
    private readonly IUpdater? _updater;
    private AppSettings _settings;
    private ConnectionSnapshot _snapshot;
    private TrayState _tray;
    private bool _everOnline;
    private string _lastDetail;
    private TimeSpan _lastPermissionPoll;
    private PermissionState _inputMonitoring;
    private HookHealth _lastHook;
    private BindingAction? _capturing;
    private TimeSpan _captureStartedAt;
    private bool _showSettings;
    private bool _restartedForPermission;
    private TimeSpan _nextUpdateCheckAt;
    private bool _unlinkAsked;
    private bool _serverFieldOpen;
    private bool? _inputHelpOpen;
    private bool _started;
    private bool _saveFailed;

    public MainViewModel(
        AppSettings settings,
        SettingsStore store,
        ConnectionController connection,
        IInputService input,
        IPermissionProbe permissions,
        IPlatformServices platform,
        IUiThread ui,
        ITimeSource time,
        IForegroundProbe? foreground = null,
        IStartAtLogin? login = null,
        IUpdater? updater = null)
    {
        _settings = settings;
        _store = store;
        _connection = connection;
        _input = input;
        _permissions = permissions;
        _platform = platform;
        _ui = ui;
        _time = time;
        _login = login is { IsAvailable: true } ? login : null;
        _updater = updater is { Stage: not UpdateStage.Off } ? updater : null;
        _elevation = new ElevationWatch(foreground ?? new SystemForegroundProbe(), time, Environment.ProcessId);
        _snapshot = connection.Snapshot;
        _matcher = new BindingMatcher(settings.Bindings);
        ServerHostText = settings.ServerHost;
        // A first run starts with the build's default address; show it so the player sees where the sign-in goes and can change it.
        _serverFieldOpen = settings.DeviceToken is null && AppInfo.DefaultHost.Length > 0 && settings.ServerHost == AppInfo.DefaultHost;
        _inputMonitoring = permissions.InputMonitoring;

        _matcher.Event += OnMatch;
        _connection.Changed += OnConnectionChanged;
        _connection.DeviceTokenIssued += OnDeviceTokenIssued;
        _connection.TokenRejected += OnTokenRejected;
        _input.Input += raw => _ui.Post(() => OnInput(raw));
        _input.Changed += () => _ui.Post(OnInputHealthChanged);
        if (_updater is not null)
        {
            _updater.Changed += () => _ui.Post(RefreshAll);
        }

        LinkWithCodeCommand = new RelayCommand(LinkWithCode, () => !IsLinkBusy);
        SignInWithDiscordCommand = new RelayCommand(SignInWithDiscord, () => !IsLinkBusy);
        CancelLinkCommand = new RelayCommand(CancelLink);
        ChangeBroadcastCommand = new RelayCommand(() => ToggleCapture(BindingAction.Broadcast));
        ChangeAllTeamsCommand = new RelayCommand(() => ToggleCapture(BindingAction.AllTeams));
        ChangeOpenMicCommand = new RelayCommand(() => ToggleCapture(BindingAction.OpenMic));
        ClearBindingCommand = new RelayCommand(ClearBinding);
        CancelCaptureCommand = new RelayCommand(() =>
        {
            EndCapture();
            RefreshAll();
        });
        UnlinkCommand = new RelayCommand(() =>
        {
            _unlinkAsked = true;
            RefreshAll();
        });
        ConfirmUnlinkCommand = new RelayCommand(() => _ = UnlinkAsync());
        CancelUnlinkCommand = new RelayCommand(() =>
        {
            _unlinkAsked = false;
            RefreshAll();
        });
        RetryNowCommand = new RelayCommand(_connection.RetryNow);
        ToggleSettingsCommand = new RelayCommand(() => ShowSettings = !ShowSettings);
        BackCommand = new RelayCommand(() => ShowSettings = false);
        ToggleInputHelpCommand = new RelayCommand(() => ShowInputHelp = !ShowInputHelp);
        ToggleServerFieldCommand = new RelayCommand(() =>
        {
            _serverFieldOpen = !_serverFieldOpen;
            RefreshAll();
        });
        ReconnectCommand = new RelayCommand(() =>
        {
            if (_settings.DeviceToken is { } token)
            {
                _connection.Start(new LinkCredential.DeviceToken(token), _settings.ServerHost);
            }
        });
        RestartInputCommand = new RelayCommand(RestartInput);
        RestartAsAdministratorCommand = new RelayCommand(RestartAsAdministrator);
        RestartToUpdateCommand = new RelayCommand(() => _updater?.RestartToUpdate(), () => !IsBroadcasting);
        UpdateActionCommand = new RelayCommand(RunUpdateAction);
        OpenInputMonitoringSettingsCommand = new RelayCommand(_permissions.OpenInputMonitoringSettings);
        ShowMacPromptCommand = new RelayCommand(() =>
        {
            _permissions.RequestPrompts();
            PollPermissions();
        });
        QuitCommand = new RelayCommand(_platform.Quit);
        _lastDetail = BadgeDetail;
        _tray = Tray;
    }

    public ICommand LinkWithCodeCommand { get; }

    public ICommand SignInWithDiscordCommand { get; }

    public ICommand CancelLinkCommand { get; }

    public ICommand ChangeBroadcastCommand { get; }

    public ICommand ChangeAllTeamsCommand { get; }

    public ICommand ChangeOpenMicCommand { get; }

    /// <summary>Leaves the input being picked unset, so its action never fires. A button, because any key may be a real binding.</summary>
    public ICommand ClearBindingCommand { get; }

    /// <summary>Stops picking and keeps the input as it was.</summary>
    public ICommand CancelCaptureCommand { get; }

    public ICommand UnlinkCommand { get; }

    public ICommand ConfirmUnlinkCommand { get; }

    public ICommand CancelUnlinkCommand { get; }

    public ICommand ReconnectCommand { get; }

    /// <summary>Skips the wait before the next connection attempt.</summary>
    public ICommand RetryNowCommand { get; }

    public ICommand ToggleServerFieldCommand { get; }

    public ICommand ToggleSettingsCommand { get; }

    /// <summary>Leaves the settings page for the main view.</summary>
    public ICommand BackCommand { get; }

    public ICommand ToggleInputHelpCommand { get; }

    public ICommand RestartInputCommand { get; }

    public ICommand RestartAsAdministratorCommand { get; }

    public ICommand RestartToUpdateCommand { get; }

    /// <summary>The one update action every surface (Settings, tray, macOS app menu) shows: restart when an update is ready, otherwise look for one.</summary>
    public ICommand UpdateActionCommand { get; }

    public ICommand OpenInputMonitoringSettingsCommand { get; }

    public ICommand ShowMacPromptCommand { get; }

    public ICommand QuitCommand { get; }

    /// <summary>Raised when <see cref="Tray"/> changes, so the tray icon can follow.</summary>
    public event Action? BadgeChanged;

    // The two text boxes of the link screen. Typing never calls back into the model.
    public string CodeText { get; set; } = "";

    public string ServerHostText { get; set; }

    /// <summary>Help with something the player typed or a note from before the window opened. Never what the badge already says.</summary>
    public string LinkMessage { get; private set; } = "";

    public bool HasLinkMessage => LinkMessage.Length > 0;

    public string Version => $"Version {AppInfo.Version}";

    public ConnectionSnapshot Snapshot => _snapshot;

    public AppSettings Settings => _settings;

    // ----- Status badge -----

    public BadgeState Badge => _snapshot.Badge;

    public bool IsConnected => Badge == BadgeState.Connected;

    public bool IsReconnecting => Badge == BadgeState.Reconnecting;

    public bool IsDisconnected => Badge == BadgeState.Disconnected;

    public bool IsBroadcasting => Badge == BadgeState.Broadcasting;

    /// <summary>The badge's headline: what the player needs to know first.</summary>
    public string BadgeLabel => CurrentBadge().Title;

    /// <summary>The line under the headline.</summary>
    public string BadgeDetail => CurrentBadge().Subtitle;

    public string BadgeIcon => CurrentBadge().Icon;

    public Tone BadgeTone => CurrentBadge().Tone;

    public bool IsToneGreen => BadgeTone == Tone.Green;

    public bool IsToneBlue => BadgeTone == Tone.Blue;

    public bool IsToneAmber => BadgeTone == Tone.Amber;

    public bool IsToneRed => BadgeTone == Tone.Red;

    /// <summary>The link is waiting out its pause: offer to try this second.</summary>
    public bool CanRetryNow => _snapshot.Phase == LinkPhase.Offline && Badge == BadgeState.Disconnected;

    /// <summary>The tray icon: the badge, except that open mic shows as a red mic. A link that is down wins over it.</summary>
    public TrayState Tray => Badge switch
    {
        BadgeState.Reconnecting => TrayState.Reconnecting,
        BadgeState.Disconnected => TrayState.Disconnected,
        _ when _snapshot.IsOnline && _snapshot.OpenMic => TrayState.OpenMic,
        BadgeState.Broadcasting => TrayState.Broadcasting,
        _ => TrayState.Connected,
    };

    public string TrayToolTip => $"{AppInfo.Name}: {Tray switch
    {
        TrayState.Connected => "Connected",
        TrayState.Broadcasting => "Broadcasting",
        TrayState.OpenMic => "Open mic on",
        TrayState.Reconnecting => _everOnline ? "Reconnecting" : "Connecting",
        _ => "Disconnected",
    }}";

    // ----- Warnings, in words, under the badge -----

    public IReadOnlyList<string> Warnings => BuildWarnings().Select(note => note.Text).ToList();

    /// <summary>The same warnings with their severity: the window paints errors red and the rest amber.</summary>
    public IReadOnlyList<WarningNote> WarningNotes => BuildWarnings();

    public bool HasWarnings => WarningNotes.Count > 0;

    public bool ShowWarnings => HasWarnings && ShowStatus;

    // ----- Link screen -----

    public bool IsLinked => _settings.DeviceToken is not null;

    public bool ShowLinkScreen => !IsLinked;

    public bool ShowMainScreen => IsLinked;

    /// <summary>Linked and not in the settings: the broadcast panel, open mic and the footer.</summary>
    public bool ShowMainView => IsLinked && !_showSettings;

    /// <summary>Settings is its own page that replaces the main view, so the window never has to change size.</summary>
    public bool ShowSettingsPage => IsLinked && _showSettings;

    /// <summary>The badge and warnings: everywhere except the settings page.</summary>
    public bool ShowStatus => !ShowSettingsPage;

    /// <summary>The server address, tucked away behind "Change bot address" while it is the build's default on a first run, or when there is none yet.</summary>
    public bool ShowServerField => _serverFieldOpen || _settings.ServerHost.Length == 0;

    /// <summary>A link attempt is under way: the code is being tried, or the browser sign-in is waiting.</summary>
    public bool IsLinkBusy => !IsLinked && _snapshot.Phase is LinkPhase.Connecting or LinkPhase.SigningIn or LinkPhase.Offline;

    public bool IsSigningIn => !IsLinked && _snapshot.Phase == LinkPhase.SigningIn;

    // ----- Main window -----

    /// <summary>Linked, but the connection was refused for a reason that waiting won't fix: offer a manual try.</summary>
    public bool CanReconnect => IsLinked && _snapshot.Phase == LinkPhase.Failed;

    /// <summary>The team a broadcast reaches, or null before the bot has said.</summary>
    private string? TeamName => _snapshot.Group switch
    {
        WireGroup.Attack => "Attack",
        WireGroup.Defense => "Defense",
        _ => null,
    };

    /// <summary>The broadcast keys can't do anything right now: the link is down or still coming up.</summary>
    public bool BroadcastUnavailable => IsLinked && !_snapshot.IsOnline;

    private static string GlyphOf(InputBinding? input) => input?.Kind switch
    {
        InputKind.Mouse => Icons.Mouse,
        InputKind.Pad => Icons.DeviceGamepad2,
        null => "",
        _ => Icons.Keyboard,
    };

    private string InputNameOf(BindingAction action) =>
        _capturing == action ? "Press any input" : _settings.Bindings[action]?.DisplayName ?? "Not set";

    private string TeamTarget() => TeamName is { } team ? $"all of {team}" : "your whole team";

    /// <summary>A line under a key card: what holding it does, or that it can't now.</summary>
    private string TailOf(bool held, string target) => BroadcastUnavailable
        ? "Works again once you're back"
        : held ? $"Talking to {target}" : $"to talk to {target}";

    // ----- The team key -----

    /// <summary>The team card lights up: the key is down and the broadcast can go out.</summary>
    public bool ShowBroadcastHeld => IsBroadcastHeld && !BroadcastUnavailable;

    public string BroadcastLead => ShowBroadcastHeld ? "Holding" : "Hold";

    public string BroadcastTail => TailOf(ShowBroadcastHeld, TeamTarget());

    public string BroadcastInputName => InputNameOf(BindingAction.Broadcast);

    /// <summary>The icon for the kind of input bound: keyboard, mouse or controller; none while unset.</summary>
    public string BroadcastGlyph => GlyphOf(_settings.Bindings.Broadcast);

    public bool BroadcastIsKey => _settings.Bindings.Broadcast?.Kind == InputKind.Key;

    public bool BroadcastIsMouse => _settings.Bindings.Broadcast?.Kind == InputKind.Mouse;

    public bool BroadcastIsPad => _settings.Bindings.Broadcast?.Kind == InputKind.Pad;

    public bool BroadcastIsSet => _settings.Bindings.Broadcast is not null;

    /// <summary>The card is quiet: no input is bound, so holding does nothing.</summary>
    public bool BroadcastIsUnset => !BroadcastIsSet && !IsCapturingBroadcast;

    public bool BroadcastShowsGlyph => BroadcastIsSet && !IsCapturingBroadcast;

    /// <summary>The bound input is down right now, so the card lights up.</summary>
    public bool IsBroadcastHeld => _matcher.BroadcastIsDown;

    public bool IsCapturingBroadcast => _capturing == BindingAction.Broadcast;

    public bool ShowBroadcastTail => !IsCapturingBroadcast;

    // ----- The all-teams key -----

    public bool ShowAllTeamsHeld => IsAllTeamsHeld && !BroadcastUnavailable;

    public string AllTeamsLead => ShowAllTeamsHeld ? "Holding" : "Hold";

    public string AllTeamsTail => TailOf(ShowAllTeamsHeld, "all teams");

    public string AllTeamsInputName => InputNameOf(BindingAction.AllTeams);

    public string AllTeamsGlyph => GlyphOf(_settings.Bindings.AllTeams);

    public bool AllTeamsIsSet => _settings.Bindings.AllTeams is not null;

    public bool AllTeamsIsUnset => !AllTeamsIsSet && !IsCapturingAllTeams;

    public bool AllTeamsShowsGlyph => AllTeamsIsSet && !IsCapturingAllTeams;

    public bool IsAllTeamsHeld => _matcher.AllTeamsIsDown;

    public bool IsCapturingAllTeams => _capturing == BindingAction.AllTeams;

    public bool ShowAllTeamsTail => !IsCapturingAllTeams;

    /// <summary>The line under the two cards, for both keys and the open mic chip.</summary>
    public string BroadcastHint => _capturing is not null
        ? CaptureHint()
        : ShowBroadcastHeld || ShowAllTeamsHeld
            ? "Let go to stop"
            : BindingSet.Actions.Any(action => _settings.Bindings[action] is null) ? "Click a key to set it" : "Click any key to change it";

    // ----- Open mic, for every player -----

    /// <summary>The switch works whenever the link is up; the bot ends it outside a team voice channel.</summary>
    public bool CanToggleOpenMic => _snapshot.IsOnline;

    public bool OpenMicIsOn
    {
        get => _snapshot.OpenMic;
        set
        {
            if (value != _snapshot.OpenMic)
            {
                _connection.SetOpenMic(value);
            }
            RefreshAll();
        }
    }

    /// <summary>The row's first line: who hears it, and while it is on, that it is live.</summary>
    public string OpenMicSubtitle => OpenMicIsOn
        ? $"Live now · {TeamTarget()} hears you"
        : TeamName is { } team ? $"All of {team} hears it" : "Your whole team hears it";

    public string OpenMicNote => "Stays live until you turn it off";

    public string OpenMicInputName => InputNameOf(BindingAction.OpenMic);

    public string OpenMicGlyph => GlyphOf(_settings.Bindings.OpenMic);

    public bool OpenMicKeyIsSet => _settings.Bindings.OpenMic is not null;

    public bool OpenMicKeyIsUnset => !OpenMicKeyIsSet && !IsCapturingOpenMic;

    public bool OpenMicShowsGlyph => OpenMicKeyIsSet && !IsCapturingOpenMic;

    public bool IsCapturingOpenMic => _capturing == BindingAction.OpenMic;

    // ----- Settings -----

    public bool ShowSettings
    {
        get => _showSettings;
        set
        {
            _showSettings = value;
            if (!value)
            {
                _unlinkAsked = false;
            }
            RefreshAll();
        }
    }

    public string LinkedAs => _snapshot.DisplayName is { } name ? $"Signed in as {name}" : "Signed in";

    public string ServerLine => $"Bot address: {_settings.ServerHost}";

    public bool NotifyOnDisconnect
    {
        get => _settings.NotifyOnDisconnect;
        set
        {
            if (_settings.NotifyOnDisconnect != value)
            {
                Save(_settings with { NotifyOnDisconnect = value });
            }
        }
    }

    /// <summary>The "Open at login" switch is shown only where the app can be started at login (an installed copy).</summary>
    public bool ShowOpenAtLogin => _login is not null;

    /// <summary>On when the system will open the app at login. The system's list is the truth; nothing is kept in settings.</summary>
    public bool OpenAtLogin
    {
        get => _login?.IsEnabled ?? false;
        set
        {
            if (_login is null || _login.IsEnabled == value)
            {
                return;
            }
            try
            {
                _login.Set(value);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                ShowNote("The app couldn't change whether it opens at login.");
                return;
            }
            RefreshAll();
        }
    }

    /// <summary>The "Updates" row of Settings is shown only when updates are on: a feed is set and this is an installed copy.</summary>
    public bool ShowUpdates => _updater is not null;

    public string UpdateStatusText => _updater switch
    {
        { Stage: UpdateStage.Checking } => "Checking for updates…",
        { Stage: UpdateStage.Ready } => $"Version {_updater.ReadyVersion} is ready.",
        { Stage: UpdateStage.Failed } => "Couldn't check for updates",
        _ => $"You have the newest version (v{AppInfo.Version})",
    };

    public string UpdateActionText => _updater switch
    {
        { Stage: UpdateStage.Checking } => "Checking for Updates…",
        { Stage: UpdateStage.Ready } => "Restart to Update",
        _ => "Check for Updates…",
    };

    /// <summary>Off while a check runs, and while broadcasting when the action would restart the app.</summary>
    public bool CanRunUpdateAction => _updater switch
    {
        { Stage: UpdateStage.Ready } => !IsBroadcasting,
        { Stage: UpdateStage.Idle or UpdateStage.Failed } => true,
        _ => false,
    };

    public bool UpdateIsReady => _updater is { Stage: UpdateStage.Ready };

    public bool ShowUnlinkConfirm => _unlinkAsked;

    public bool ShowUnlinkButton => !_unlinkAsked;

    // ----- Keys not working in game? -----

    public bool IsMac => _platform.IsMac;

    public bool IsWindows => _platform.IsWindows;

    public string InputHelpChevron => ShowInputHelp ? Icons.ChevronUp : Icons.ChevronDown;

    public bool NeedsAccessibility => _input.Hook == HookHealth.NeedsAccessibility;

    /// <summary>The "Keys not working in game?" help is open: by the player's choice, else when macOS is waiting for permission.</summary>
    public bool ShowInputHelp
    {
        get => _inputHelpOpen ?? NeedsAccessibility;
        set
        {
            _inputHelpOpen = value;
            RefreshAll();
        }
    }

    public string InputStatusText => $"Keyboard and mouse: {HookText()}. Controller: {PadText()}.";

    public string InputMonitoringText => "Input Monitoring: " + PermissionText(_inputMonitoring);

    public string MacHelpText =>
        "Allow this app in Privacy & Security so it can hear your key in games. It only sends when the key goes down and up, never what you type.";

    public string WindowsHelpText => _platform.IsElevated
        ? "This app runs as administrator, so it hears keys in games that run as administrator."
        : "Restart as administrator if your game runs as administrator. This app only watches keys; it never presses them.";

    public bool CanRestartAsAdministrator => IsWindows && !_platform.IsElevated;

    // ----- Lifetime -----

    /// <summary>
    /// Starts input and, when this PC is already linked, the connection. Call it once the window is up:
    /// on macOS the key hook is serviced by the app's own UI loop, so it must not start before it.
    /// </summary>
    public void Start()
    {
        if (_started)
        {
            return;
        }
        _started = true;
        _lastHook = _input.Hook;
        _nextUpdateCheckAt = _time.Now + UpdateFirstCheck;
        _input.StartHook();
        if (_settings.Bindings.UsesPad)
        {
            _input.StartPad();
        }
        if (_settings.DeviceToken is { } token && (_settings.ServerHost.Length > 0 || _connection.HasServerOverride))
        {
            _connection.Start(new LinkCredential.DeviceToken(token), _settings.ServerHost);
        }
        RefreshAll();
    }

    /// <summary>The clock: the connection's timers, the elapsed seconds in the badge, and the permission check.</summary>
    public void Tick()
    {
        _connection.Tick();
        var now = _time.Now;
        var changed = false;
        if (IsMac && now - _lastPermissionPoll >= PermissionPoll
            && (NeedsAccessibility || _showSettings || now - _lastPermissionPoll >= PermissionPoll * 5))
        {
            _lastPermissionPoll = now;
            changed |= PollPermissions();
        }
        if (IsWindows && !_platform.IsElevated)
        {
            changed |= _elevation.Poll();
        }
        if (_updater is not null && now >= _nextUpdateCheckAt && _nextUpdateCheckAt != TimeSpan.Zero && Badge != BadgeState.Broadcasting)
        {
            _nextUpdateCheckAt = now + UpdateEvery;
            _ = CheckForUpdatesAsync();
        }
        var detail = BadgeDetail;
        if (detail != _lastDetail)
        {
            _lastDetail = detail;
            changed = true;
        }
        if (changed)
        {
            RefreshAll();
        }
    }

    public void Dispose()
    {
        _connection.Changed -= OnConnectionChanged;
        _connection.DeviceTokenIssued -= OnDeviceTokenIssued;
        _connection.TokenRejected -= OnTokenRejected;
    }

    /// <summary>A message for the link screen from before the window opened, such as an unreadable settings file.</summary>
    public void ShowNote(string note)
    {
        LinkMessage = note;
        RefreshAll();
    }

    // ----- Linking -----

    private void LinkWithCode()
    {
        var text = CodeText.Trim();
        if (!ConnectCode.TryParse(text, out var code))
        {
            LinkMessage = "Paste the whole /connect reply from Discord. It starts with KZG1.";
            RefreshAll();
            return;
        }
        LinkMessage = "";
        ServerHostText = code.Host;
        Save(_settings with { ServerHost = code.Host });
        _connection.Start(new LinkCredential.Code(text), code.Host);
        RefreshAll();
    }

    private void SignInWithDiscord()
    {
        var host = ServerHostText.Trim();
        if (host.Length == 0 || !ConnectCode.IsValidHost(host))
        {
            LinkMessage = "Enter the bot address first, like bot.example.org.";
            RefreshAll();
            return;
        }
        LinkMessage = "";
        Save(_settings with { ServerHost = host });
        var loginId = WireSecret.Generate();
        _connection.Start(new LinkCredential.LoginId(loginId), host);
        _platform.OpenUrl($"https://{host}/auth/start?login={loginId}");
        RefreshAll();
    }

    private void CancelLink()
    {
        _connection.Stop();
        LinkMessage = "";
        RefreshAll();
    }

    private async Task UnlinkAsync()
    {
        await _connection.UnlinkAsync();
        _unlinkAsked = false;
        _showSettings = false;
        LinkMessage = "";
        Save(_settings with { DeviceToken = null });
        RefreshAll();
    }

    private void OnDeviceTokenIssued(string token)
    {
        CodeText = "";
        LinkMessage = "";
        Save(_settings with { DeviceToken = token });
        RefreshAll();
    }

    private void OnTokenRejected()
    {
        Save(_settings with { DeviceToken = null });
        _showSettings = false;
        RefreshAll();
    }

    private void OnConnectionChanged(ConnectionSnapshot snapshot)
    {
        var before = _snapshot;
        _snapshot = snapshot;
        _everOnline |= snapshot.IsOnline;
        if (snapshot.Badge == BadgeState.Disconnected && before.Badge != BadgeState.Disconnected && _everOnline
            && _settings.NotifyOnDisconnect)
        {
            _platform.Notify(AppInfo.Name, "Disconnected from the broadcast server. " + DisconnectedDetail());
        }
        if (Tray != _tray)
        {
            _tray = Tray;
            BadgeChanged?.Invoke();
        }
        RaiseCommandStates();
        RefreshAll();
    }

    // ----- Input -----

    private void OnInput(RawInput raw)
    {
        // While a binding is being picked the matcher only tracks what is held, so the picked input
        // doesn't start a broadcast when it auto-repeats afterwards.
        _matcher.Handle(raw);
        if (_capturing is { } target && raw.Down)
        {
            Capture(target, raw.Input);
        }
    }

    private void OnMatch(BindingEvent match)
    {
        switch (match)
        {
            case BindingEvent.BroadcastDown:
                _connection.SetGroupKey(true);
                break;
            case BindingEvent.BroadcastUp:
                _connection.SetGroupKey(false);
                break;
            case BindingEvent.AllTeamsDown:
                _connection.SetAllTeamsKey(true);
                break;
            case BindingEvent.AllTeamsUp:
                _connection.SetAllTeamsKey(false);
                break;
            case BindingEvent.OpenMicToggle:
                if (CanToggleOpenMic)
                {
                    _connection.SetOpenMic(!_snapshot.OpenMic);
                }
                break;
        }
        RefreshAll();
    }

    private void OnInputHealthChanged()
    {
        if (_input.Hook != _lastHook)
        {
            // Whatever was held when the hook went away is not held any more as far as we can tell.
            if (_input.Hook != HookHealth.Ok)
            {
                _matcher.Release(InputKind.Key);
                _matcher.Release(InputKind.Mouse);
            }
            _lastHook = _input.Hook;
        }
        if (_input.Pad is not (PadHealth.Running or PadHealth.Starting))
        {
            _matcher.Release(InputKind.Pad);
        }
        _inputMonitoring = _permissions.InputMonitoring;
        RefreshAll();
    }

    private void RestartInput()
    {
        _matcher.ReleaseAll();
        _input.StartHook();
        if (_settings.Bindings.UsesPad || _capturing is not null)
        {
            _input.StartPad();
        }
        RefreshAll();
    }

    private void RestartAsAdministrator()
    {
        if (_platform.TryRestartElevated())
        {
            _platform.Quit();
        }
    }

    private bool PollPermissions()
    {
        var inputMonitoring = _permissions.InputMonitoring;
        var changed = inputMonitoring != _inputMonitoring;
        var granted = _inputMonitoring != PermissionState.Granted && inputMonitoring == PermissionState.Granted;
        _inputMonitoring = inputMonitoring;
        if (granted && _input.Hook == HookHealth.NeedsAccessibility)
        {
            // macOS applies a new Input Monitoring grant only to a process that starts afterwards, so the app starts itself
            // again, once. Where it can't (a build run from a folder), the listener starts in place, once, never a loop.
            if (!_restartedForPermission && _platform.TryRestartApp())
            {
                _restartedForPermission = true;
                _platform.Quit();
            }
            else
            {
                _input.StartHook();
            }
        }
        return changed;
    }

    // ----- Choosing bindings: the next input of any kind becomes the binding -----

    private void ToggleCapture(BindingAction target)
    {
        if (_capturing == target)
        {
            EndCapture();
        }
        else
        {
            _capturing = target;
            _captureStartedAt = _time.Now;
            _matcher.Muted = true;
            // Any input means controller buttons too, so the helper runs while a pick is waiting.
            _input.StartPad();
        }
        CaptureMessage = "";
        RefreshAll();
    }

    private string CaptureMessage { get; set; } = "";

    private string CaptureHint() =>
        CaptureMessage.Length > 0 ? CaptureMessage : "Press any key, mouse button or controller button.";

    private void EndCapture()
    {
        _capturing = null;
        _matcher.Muted = false;
        if (!_settings.Bindings.UsesPad)
        {
            _input.StopPad();
        }
    }

    private void Capture(BindingAction target, InputBinding input)
    {
        if (input is { Kind: InputKind.Mouse, Name: nameof(SharpHook.Data.MouseButton.Button1) }
            && _time.Now - _captureStartedAt < ClickEcho)
        {
            return;
        }
        var current = _settings.Bindings;
        var candidate = current.With(target, input);
        if (current.ActionOf(input) is { } taken && taken != target)
        {
            CaptureMessage = $"{input.DisplayName} is taken by {ActionName(taken)}. Press another input.";
        }
        else if (candidate.Problem() is { } problem)
        {
            CaptureMessage = problem + " Press another input.";
        }
        else
        {
            SetBindings(candidate);
            EndCapture();
        }
        RefreshAll();
    }

    private void ClearBinding()
    {
        if (_capturing is { } target)
        {
            SetBindings(_settings.Bindings.With(target, null));
            EndCapture();
        }
        RefreshAll();
    }

    private void SetBindings(BindingSet bindings)
    {
        _matcher.SetBindings(bindings);
        Save(_settings with { Bindings = bindings });
        CaptureMessage = "";
    }

    private static string ActionName(BindingAction action) => action switch
    {
        BindingAction.Broadcast => "broadcast to your team",
        BindingAction.AllTeams => "broadcast to all teams",
        _ => "open mic broadcast",
    };

    // ----- Texts -----

    private readonly record struct BadgeView(Tone Tone, string Icon, string Title, string Subtitle);

    /// <summary>The badge for the state of the link, in the app's own words. One place, so words and colors can't drift apart.</summary>
    private BadgeView CurrentBadge()
    {
        var recovery = "Link this PC again, one way below";
        switch (_snapshot.Phase)
        {
            case LinkPhase.NotLinked:
                return new(Tone.Blue, Icons.Link, "Link this PC to start", "Use one of the two ways below");
            case LinkPhase.SigningIn:
                return new(Tone.Blue, Icons.BrandDiscord, "Waiting for Discord", "Finish in your browser");
            case LinkPhase.Failed:
                return _snapshot.Failure switch
                {
                    LinkFailure.TokenExpired => new(Tone.Red, Icons.LinkOff, "Your link expired", recovery),
                    LinkFailure.Revoked => new(Tone.Red, Icons.LinkOff, "Your link was removed", recovery),
                    LinkFailure.ReplacedByNewerApp => new(Tone.Red, Icons.LinkOff, "Linked on another PC", "Restart this app to take it back"),
                    LinkFailure.BadCode => new(Tone.Red, Icons.LinkOff, "That code isn't valid", "Type /connect in Discord for a new one"),
                    LinkFailure.CodeUsed => new(Tone.Red, Icons.LinkOff, "That code was already used", "Type /connect in Discord for a new one"),
                    LinkFailure.CodeExpired => new(Tone.Red, Icons.LinkOff, "That code expired", "Type /connect in Discord for a new one"),
                    LinkFailure.NotMember => new(Tone.Red, Icons.LinkOff, "You're not in the server", "Join your team's Discord server, then try again"),
                    LinkFailure.VersionMismatch => new(Tone.Red, Icons.PlugConnectedX, "This app is out of date", "Install the newest version"),
                    LinkFailure.SignInTimedOut => new(Tone.Red, Icons.LinkOff, "Sign-in took too long", "Try again below"),
                    LinkFailure.Interrupted => new(Tone.Red, Icons.LinkOff, "The connection dropped", "Type /connect in Discord for a new code"),
                    LinkFailure.ProtocolError => new(Tone.Red, Icons.PlugConnectedX, "Something unexpected happened", "Install the newest version"),
                    _ => new(Tone.Red, Icons.PlugConnectedX, "Not connected", "Try again in a moment"),
                };
            case LinkPhase.Online:
                return OnlineBadge();
        }
        return Badge == BadgeState.Disconnected
            ? new(Tone.Red, Icons.PlugConnectedX, "Can't reach the bot", RetryText())
            : _everOnline
                ? new(Tone.Amber, Icons.Refresh, "Reconnecting…", $"Lost the bot {OfflineSeconds()} s ago")
                : new(Tone.Amber, Icons.Refresh, "Connecting…", "Contacting the server");
    }

    private BadgeView OnlineBadge()
    {
        var everyone = TeamName is { } team ? $"All of {team} hears" : "Your whole team hears";
        if (_snapshot.OpenMic && !_snapshot.GroupHeld && !_snapshot.AllTeamsHeld)
        {
            return new(Tone.Red, Icons.Microphone, "Open mic broadcast is on", $"{everyone} you");
        }
        if (Badge == BadgeState.Broadcasting)
        {
            return new(Tone.Blue, Icons.Broadcast, _snapshot.AllTeamsHeld ? "You're live to all teams" : "You're live", _snapshot.AllTeamsHeld ? AllTeamsHearingText() : HearingText());
        }
        return new(Tone.Green, Icons.CircleCheck, "You're connected", ConnectedDetail());
    }

    private string HearingText() => _snapshot.Targets.Count switch
    {
        0 => "No one hears you yet",
        1 => $"{_snapshot.Targets[0]} hears you",
        var count => $"{string.Join(", ", _snapshot.Targets.Take(count - 1))} and {_snapshot.Targets[count - 1]} hear you",
    };

    // Up to six channels would not fit on one line, so the all-teams badge names the two groups instead.
    private string AllTeamsHearingText() => _snapshot.Targets.Count == 0 ? "No one hears you yet" : "Attack and Defense hear you";

    private string RetryText()
    {
        if (_snapshot.NextAttemptAt is not { } next)
        {
            return "Trying again now…";
        }
        var seconds = Math.Max(1, (int)Math.Ceiling((next - _time.Now).TotalSeconds));
        return $"Trying again in {seconds} s";
    }

    private string ConnectedDetail()
    {
        var parts = new List<string> { _snapshot.ChannelName ?? "Not in a voice channel" };
        if (_snapshot.RoundTripMs is { } rtt)
        {
            parts.Add($"{rtt} ms");
        }
        return string.Join(" · ", parts);
    }

    private string DisconnectedDetail() => _snapshot.Phase == LinkPhase.SigningIn
        ? "Finish signing in with Discord in your browser."
        : _snapshot.Failure switch
        {
            LinkFailure.NotLinked => "Link this PC with a /connect code or Discord.",
            LinkFailure.ServerUnreachable => "Server unreachable, retrying.",
            LinkFailure.BadCode => "That code isn't valid. Run /connect again.",
            LinkFailure.CodeUsed => "That code was already used. Run /connect for a new one.",
            LinkFailure.CodeExpired => "That code expired. Run /connect for a new one.",
            LinkFailure.TokenExpired => "Link expired. Run /connect or sign in again.",
            LinkFailure.Revoked => "Link revoked. Run /connect or sign in again.",
            LinkFailure.ReplacedByNewerApp => "Linked on another device. Restart this app to take the link back.",
            LinkFailure.NotMember => "Join your team's Discord server, then try again.",
            LinkFailure.VersionMismatch => "Install the newest version of this app.",
            LinkFailure.SignInTimedOut => "Sign-in took too long. Try again.",
            LinkFailure.Interrupted => "The connection dropped while linking. Run /connect for a new code.",
            LinkFailure.ProtocolError => "The server sent something unexpected. Install the newest version.",
            _ => "Not connected.",
        };

    private int OfflineSeconds() =>
        _snapshot.OfflineSince is { } since ? Math.Max(0, (int)(_time.Now - since).TotalSeconds) : 0;

    private List<WarningNote> BuildWarnings()
    {
        var list = new List<WarningNote>();
        if (IsLinked && _snapshot.IsOnline)
        {
            foreach (var warning in _snapshot.Warnings)
            {
                list.Add(new(
                    warning switch
                    {
                        StatusWarning.NotInTeamVc => "You're not in a team voice channel.",
                        _ => BotDownText(),
                    },
                    IsError: false));
            }
        }
        if (_saveFailed)
        {
            list.Add(new("Settings could not be saved. Check that the settings folder is writable.", IsError: true));
        }
        switch (_snapshot.Notice)
        {
            case LinkNotice.OpenMicOffAfterDrop:
                list.Add(new("Open mic broadcast turned off when the connection dropped.", IsError: false));
                break;
        }
        switch (_input.Hook)
        {
            case HookHealth.NeedsAccessibility:
                list.Add(new(
                    "Allow this app in macOS Input Monitoring so it can hear your key. It restarts by itself once you do.",
                    IsError: false, "Open Input Monitoring settings", OpenInputMonitoringSettingsCommand));
                break;
            case HookHealth.Failed:
                list.Add(new(
                    "Keyboard and mouse input stopped." + (_input.HookDetail is { Length: > 0 } detail ? " " + detail : ""),
                    IsError: true, "Restart input", RestartInputCommand));
                break;
        }
        if (_input.Pad == PadHealth.Failed)
        {
            list.Add(new("Controller input stopped.", IsError: true, "Restart input", RestartInputCommand));
        }
        if (IsWindows && !_platform.IsElevated && _elevation.ElevatedWindowInFront)
        {
            list.Add(new(
                "Your game runs as administrator, so the app can't hear your key.",
                IsError: false, "Restart as administrator", RestartAsAdministratorCommand));
        }
        if (_updater is { Stage: UpdateStage.Ready })
        {
            list.Add(new($"Version {_updater.ReadyVersion} is ready.", IsError: false, "Restart to update", RestartToUpdateCommand, IsInfo: true));
        }
        return list;
    }

    private void RunUpdateAction()
    {
        // The tray and app menu can fire a stale click, so the guard lives here and not only in the enabled state.
        if (!CanRunUpdateAction)
        {
            return;
        }
        if (_updater is { Stage: UpdateStage.Ready })
        {
            _updater.RestartToUpdate();
        }
        else
        {
            _ = CheckForUpdatesAsync();
        }
    }

    private async Task CheckForUpdatesAsync()
    {
        if (_updater is not null)
        {
            await _updater.CheckAsync().ConfigureAwait(false);
        }
    }

    /// <summary>Names the VCs that can't hear a broadcast, when the bot says which; otherwise the general sentence.</summary>
    private string BotDownText() => _snapshot.BotDownIn switch
    {
        [] => "A relay bot is down in your team.",
        [var only] => $"{only}'s bot is offline, so {only} can't hear your broadcasts.",
        var several => $"Bots are offline in {string.Join(", ", several.Take(several.Count - 1))} and {several[^1]}, so they can't hear your broadcasts.",
    };

    private string HookText() => _input.Hook switch
    {
        HookHealth.Ok => "listening",
        HookHealth.Starting => "starting",
        HookHealth.NeedsAccessibility => "waiting for permission",
        HookHealth.Failed => "stopped",
        _ => "off",
    };

    private string PadText() => _input.Pad switch
    {
        PadHealth.Running => _input.Controllers.Count > 0 ? "listening to " + string.Join(", ", _input.Controllers) : "listening, none plugged in",
        PadHealth.Starting => "starting",
        PadHealth.Restarting => "restarting",
        PadHealth.Failed => "stopped",
        _ => "not used",
    };

    private static string PermissionText(PermissionState state) => state switch
    {
        PermissionState.Granted => "allowed",
        PermissionState.Denied => "not allowed yet",
        PermissionState.NotAsked => "not asked yet",
        _ => "not needed",
    };

    private void RaiseCommandStates()
    {
        (LinkWithCodeCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (SignInWithDiscordCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private void Save(AppSettings settings)
    {
        _settings = settings;
        _saveFailed = !_store.TrySave(settings);
        RaiseCommandStates();
    }
}
