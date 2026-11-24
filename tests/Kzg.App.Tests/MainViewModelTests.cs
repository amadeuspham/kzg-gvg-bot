using Kzg.App.Input;
using Kzg.App.Link;
using Kzg.App.Services;
using Kzg.App.Settings;
using Kzg.App.ViewModels;
using Kzg.Protocol;
using SharpHook.Data;

namespace Kzg.App.Tests;

public sealed class MainViewModelTests : IDisposable
{
    private const string Host = "gvg.example.com";

    private static readonly InputBinding F7 = InputBinding.Key(KeyCode.VcF7);
    private static readonly InputBinding F8 = InputBinding.Key(KeyCode.VcF8);
    private static readonly InputBinding F9 = InputBinding.Key(KeyCode.VcF9);
    private static readonly InputBinding F10 = InputBinding.Key(KeyCode.VcF10);
    private static readonly InputBinding Side = InputBinding.Mouse(MouseButton.Button4);
    private static readonly InputBinding PadA = InputBinding.Pad("a");

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-vm-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeSource _time = new();
    private readonly FakeTransportFactory _transports = new();
    private readonly FakeInputService _input = new();
    private readonly FakePermissions _permissions = new();
    private readonly FakePlatform _platform = new();
    private readonly FakeForegroundProbe _foreground = new();
    private FakeStartAtLogin? _login;
    private FakeUpdater? _updater;
    private MainViewModel _vm = null!;
    private SettingsStore _store = null!;

    public MainViewModelTests() => Build(AppSettings.CreateDefault());

    public void Dispose()
    {
        _vm.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private void Build(AppSettings settings)
    {
        _store = new SettingsStore(Path.Combine(_folder, "settings.json"));
        var connection = new ConnectionController(_transports, _time, settings.InstallId, "1.0.0");
        _vm = new MainViewModel(settings, _store, connection, _input, _permissions, _platform, new InlineUi(), _time, _foreground, _login, _updater);
    }

    private static AppSettings Linked(BindingSet? bindings = null) => AppSettings.CreateDefault() with
    {
        ServerHost = Host,
        DeviceToken = WireSecret.Generate(),
        Bindings = bindings ?? BindingSet.Default,
    };

    private FakeTransport StartLinkedOnline(BindingSet? bindings = null)
    {
        Build(Linked(bindings));
        _vm.Start();
        _transports.Latest.Receive(Messages.Welcome());
        _transports.Latest.Receive(Messages.Status());
        return _transports.Latest;
    }

    private void Tick(double seconds)
    {
        _time.Advance(TimeSpan.FromSeconds(seconds));
        _vm.Tick();
    }

    private void Press(InputBinding input)
    {
        _input.Raise(input, true);
        _input.Raise(input, false);
    }

    // ----- Start -----

    [Fact]
    public void Starting_starts_the_key_hook_once_and_the_controller_only_when_a_binding_needs_it()
    {
        Build(Linked());

        _vm.Start();
        _vm.Start();

        Assert.Equal(1, _input.HookStarts);
        Assert.Equal(0, _input.PadStarts);
    }

    [Fact]
    public void The_hook_is_not_started_before_Start_is_called()
    {
        Build(Linked());

        Assert.Equal(0, _input.HookStarts);
        Assert.Empty(_transports.Created);
    }

    [Fact]
    public void A_controller_binding_starts_the_helper_at_start_up()
    {
        Build(Linked(new BindingSet(PadA, null, F9)));

        _vm.Start();

        Assert.Equal(1, _input.PadStarts);
    }

    [Fact]
    public void A_linked_pc_connects_with_its_token_at_start()
    {
        var settings = Linked();
        Build(settings);

        _vm.Start();

        Assert.Equal(settings.DeviceToken, _transports.Latest.SentOf<HelloMessage>().Single().DeviceToken);
        Assert.Equal("Connecting…", _vm.BadgeLabel);
        Assert.Equal(Tone.Amber, _vm.BadgeTone);
        Assert.True(_vm.IsReconnecting);
    }

    [Fact]
    public void An_unlinked_pc_shows_the_link_screen_and_a_blue_badge_with_a_next_step()
    {
        _vm.Start();

        Assert.True(_vm.ShowLinkScreen);
        Assert.False(_vm.ShowMainScreen);
        Assert.True(_vm.IsDisconnected);
        Assert.Equal("Link this PC to start", _vm.BadgeLabel);
        Assert.Equal(Tone.Blue, _vm.BadgeTone);
        Assert.Contains("two ways", _vm.BadgeDetail);
        Assert.Empty(_transports.Created);
    }

    // ----- Badge states -----

    [Fact]
    public void Connected_names_the_channel_and_the_round_trip_in_one_line()
    {
        var transport = StartLinkedOnline();
        Tick(2);
        var beat = transport.SentOf<HeartbeatMessage>().Last();
        _time.Advance(TimeSpan.FromMilliseconds(37));
        transport.Receive(new HeartbeatAckMessage(beat.Seq, beat.SentAtMs));

        Assert.True(_vm.IsConnected);
        Assert.Equal("You're connected", _vm.BadgeLabel);
        Assert.Equal(Tone.Green, _vm.BadgeTone);
        Assert.Equal(Icons.CircleCheck, _vm.BadgeIcon);
        Assert.Equal("Attack 1 · 37 ms", _vm.BadgeDetail);
    }

    [Fact]
    public void Connected_outside_voice_says_so()
    {
        var transport = StartLinkedOnline();

        transport.Receive(Messages.Status(channel: null, group: null, targets: [], warnings: [StatusWarning.NotInTeamVc]));

        Assert.Equal("Not in a voice channel", _vm.BadgeDetail);
        Assert.Contains("You're not in a team voice channel.", _vm.Warnings);
    }

    [Fact]
    public void The_broadcast_panel_names_the_team_and_the_key()
    {
        var transport = StartLinkedOnline();
        Assert.Equal("Hold", _vm.BroadcastLead);
        Assert.Equal("to talk to all of Attack", _vm.BroadcastTail);
        Assert.Equal("F8", _vm.BroadcastInputName);
        Assert.Equal(Icons.Keyboard, _vm.BroadcastGlyph);
        Assert.Equal("Click any key to change it", _vm.BroadcastHint);

        transport.Receive(Messages.Status(channel: "Defense 2", group: WireGroup.Defense));
        Assert.Equal("to talk to all of Defense", _vm.BroadcastTail);

        transport.Receive(Messages.Status(channel: null, group: null, targets: []));
        Assert.Equal("to talk to your whole team", _vm.BroadcastTail);
    }

    [Fact]
    public void While_the_key_is_held_the_panel_says_so_and_the_hint_says_how_to_stop()
    {
        StartLinkedOnline();

        _input.Raise(F8, true);

        Assert.True(_vm.ShowBroadcastHeld);
        Assert.Equal("Holding", _vm.BroadcastLead);
        Assert.Equal("Talking to all of Attack", _vm.BroadcastTail);
        Assert.Equal("Let go to stop", _vm.BroadcastHint);
    }

    [Fact]
    public void The_broadcast_panel_is_marked_unavailable_while_the_link_is_down_even_with_the_key_held()
    {
        var transport = StartLinkedOnline();
        transport.Close();
        _input.Raise(F8, true);

        Assert.True(_vm.BroadcastUnavailable);
        Assert.False(_vm.ShowBroadcastHeld);
        Assert.Equal("Hold", _vm.BroadcastLead);
        Assert.Equal("Works again once you're back", _vm.BroadcastTail);
    }

    [Fact]
    public void Open_mic_on_says_so_in_the_badge_and_names_who_hears_it()
    {
        var transport = StartLinkedOnline();

        _vm.OpenMicIsOn = true;
        transport.Receive(Messages.Status(active: [WireAction.OpenMic], openMic: true));

        Assert.Equal("Open mic broadcast is on", _vm.BadgeLabel);
        Assert.Equal("All of Attack hears you", _vm.BadgeDetail);
        Assert.Equal(Tone.Red, _vm.BadgeTone);
        Assert.Equal(Icons.Microphone, _vm.BadgeIcon);
        Assert.Equal("Live now · all of Attack hears you", _vm.OpenMicSubtitle);
        Assert.Equal("Stays live until you turn it off", _vm.OpenMicNote);
    }

    [Fact]
    public void Broadcasting_follows_the_key_turns_the_badge_blue_and_names_the_voice_channels_reached()
    {
        StartLinkedOnline();

        _input.Raise(F8, true);

        Assert.True(_vm.IsBroadcasting);
        Assert.Equal("You're live", _vm.BadgeLabel);
        Assert.Equal(Tone.Blue, _vm.BadgeTone);
        Assert.Equal("Attack 2 and Attack 3 hear you", _vm.BadgeDetail);

        _input.Raise(F8, false);

        Assert.True(_vm.IsConnected);
    }

    [Fact]
    public void Broadcasting_with_nobody_to_reach_says_so_in_words()
    {
        var transport = StartLinkedOnline();
        transport.Receive(Messages.Status(channel: null, group: null, targets: []));

        _input.Raise(F8, true);

        Assert.Equal("No one hears you yet", _vm.BadgeDetail);
    }

    [Fact]
    public void The_broadcast_button_lights_up_while_its_input_is_held_and_not_after()
    {
        StartLinkedOnline();
        Assert.False(_vm.IsBroadcastHeld);

        _input.Raise(F8, true);
        Assert.True(_vm.IsBroadcastHeld);

        _input.Raise(F8, false);
        Assert.False(_vm.IsBroadcastHeld);
    }

    [Fact]
    public void A_drop_shows_Reconnecting_with_the_seconds_since_then_and_later_Disconnected()
    {
        var transport = StartLinkedOnline();
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException();
        transport.Close();

        Tick(4);
        Assert.True(_vm.IsReconnecting);
        Assert.Equal("Reconnecting…", _vm.BadgeLabel);
        Assert.Equal(Tone.Amber, _vm.BadgeTone);
        Assert.Equal("Lost the bot 4 s ago", _vm.BadgeDetail);
        Assert.False(_vm.CanRetryNow);

        Tick(11);

        Assert.True(_vm.IsDisconnected);
        Assert.Equal("Can't reach the bot", _vm.BadgeLabel);
        Assert.Equal(Tone.Red, _vm.BadgeTone);
        Assert.Matches(@"^Trying again in \d+ s$", _vm.BadgeDetail);
        Assert.True(_vm.CanRetryNow);
    }

    [Fact]
    public void The_badge_text_is_refreshed_as_the_seconds_pass()
    {
        var transport = StartLinkedOnline();
        transport.Close();
        var changes = new List<string?>();
        _vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        Tick(1);

        Assert.Contains(string.Empty, changes);
    }

    [Fact]
    public void The_badge_changed_event_fires_for_the_tray_only_when_the_state_changes()
    {
        var transport = StartLinkedOnline();
        var count = 0;
        _vm.BadgeChanged += () => count++;

        _input.Raise(F8, true);
        _input.Raise(F8, false);
        transport.Receive(Messages.Status(channel: "Attack 1"));

        Assert.Equal(2, count);
    }

    [Fact]
    public void Retry_now_connects_at_once_instead_of_waiting_out_the_pause()
    {
        var transport = StartLinkedOnline();
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException();
        transport.Close();
        Tick(15);
        Assert.True(_vm.CanRetryNow);
        var attempts = _transports.Created.Count;

        _vm.RetryNowCommand.Execute(null);

        Assert.Equal(attempts + 1, _transports.Created.Count);
    }

    [Theory]
    [InlineData(ErrorCode.Revoked, "Your link was removed")]
    [InlineData(ErrorCode.Expired, "Your link expired")]
    public void A_dead_token_is_forgotten_and_the_next_step_is_plain(ErrorCode code, string title)
    {
        var transport = StartLinkedOnline();

        transport.Receive(Messages.Error(code));

        Assert.True(_vm.IsDisconnected);
        Assert.Equal(title, _vm.BadgeLabel);
        Assert.Equal("Link this PC again, one way below", _vm.BadgeDetail);
        Assert.Equal(Tone.Red, _vm.BadgeTone);
        Assert.Equal("", _vm.LinkMessage);
        Assert.True(_vm.ShowLinkScreen);
        Assert.Null(_store.Load().Settings.DeviceToken);
    }

    [Fact]
    public void Being_replaced_by_another_app_says_so_offers_a_manual_retry_and_keeps_the_token()
    {
        var transport = StartLinkedOnline();

        transport.Close(4000);
        Tick(300);

        Assert.Equal("Linked on another PC", _vm.BadgeLabel);
        Assert.Equal(Tone.Red, _vm.BadgeTone);
        Assert.True(_vm.CanReconnect);
        Assert.Single(_transports.Created);

        _vm.ReconnectCommand.Execute(null);

        Assert.Equal(2, _transports.Created.Count);
        Assert.NotNull(_vm.Settings.DeviceToken);
    }

    // ----- Linking -----

    [Fact]
    public void A_pasted_text_that_is_not_a_connect_code_is_explained_and_nothing_connects()
    {
        _vm.CodeText = "hello there";

        _vm.LinkWithCodeCommand.Execute(null);

        Assert.Contains("KZG1", _vm.LinkMessage);
        Assert.Empty(_transports.Created);
    }

    [Fact]
    public void A_pasted_code_is_trimmed_and_tried_against_the_host_inside_it_then_the_token_is_saved()
    {
        var code = ConnectCode.Generate("bot.example.org:8443");
        _vm.CodeText = $"  {code.Format()}\n";

        _vm.LinkWithCodeCommand.Execute(null);

        Assert.Equal(new Uri("wss://bot.example.org:8443/ws"), _transports.Latest.ConnectedTo);
        Assert.Equal(code.Format(), _transports.Latest.SentOf<HelloMessage>().Single().Code);
        Assert.True(_vm.IsLinkBusy);

        var token = WireSecret.Generate();
        _transports.Latest.Receive(Messages.Welcome(token: token));

        var saved = _store.Load().Settings;
        Assert.Equal(token, saved.DeviceToken);
        Assert.Equal("bot.example.org:8443", saved.ServerHost);
        Assert.True(_vm.ShowMainScreen);
        Assert.Equal("", _vm.CodeText);
        Assert.Equal("Signed in as Alex", _vm.LinkedAs);
    }

    [Fact]
    public void A_refused_code_says_what_to_do_next()
    {
        _vm.CodeText = ConnectCode.Generate(Host).Format();
        _vm.LinkWithCodeCommand.Execute(null);

        _transports.Latest.Receive(Messages.Error(ErrorCode.Used));

        Assert.Equal("That code was already used", _vm.BadgeLabel);
        Assert.Contains("/connect", _vm.BadgeDetail);
        Assert.Equal(Tone.Red, _vm.BadgeTone);
        Assert.Equal("", _vm.LinkMessage);
        Assert.False(_vm.IsLinkBusy);
    }

    [Fact]
    public void The_input_help_opens_by_itself_only_while_macOS_waits_for_permission_and_the_player_can_override_it()
    {
        StartLinkedOnline();
        Assert.False(_vm.ShowInputHelp);

        _input.Hook = HookHealth.NeedsAccessibility;
        Assert.True(_vm.ShowInputHelp);

        _vm.ToggleInputHelpCommand.Execute(null);
        Assert.False(_vm.ShowInputHelp);

        _vm.ToggleInputHelpCommand.Execute(null);
        Assert.True(_vm.ShowInputHelp);
    }

    [Fact]
    public void Settings_is_its_own_page_that_replaces_the_main_view_and_Back_returns()
    {
        StartLinkedOnline();
        Assert.True(_vm.ShowMainView);
        Assert.False(_vm.ShowSettingsPage);
        Assert.True(_vm.ShowStatus);

        _vm.ToggleSettingsCommand.Execute(null);

        Assert.False(_vm.ShowMainView);
        Assert.True(_vm.ShowSettingsPage);
        Assert.False(_vm.ShowStatus);

        _vm.BackCommand.Execute(null);

        Assert.True(_vm.ShowMainView);
        Assert.False(_vm.ShowSettingsPage);
        Assert.True(_vm.ShowStatus);
    }

    [Fact]
    public void The_link_screen_never_shows_the_settings_page()
    {
        _vm.Start();

        Assert.False(_vm.ShowSettingsPage);
        Assert.False(_vm.ShowMainView);
        Assert.True(_vm.ShowStatus);
    }

    [Fact]
    public void The_settings_button_opens_and_closes_the_settings()
    {
        StartLinkedOnline();

        _vm.ToggleSettingsCommand.Execute(null);
        Assert.True(_vm.ShowSettings);

        _vm.ToggleSettingsCommand.Execute(null);
        Assert.False(_vm.ShowSettings);
    }

    [Fact]
    public void Waiting_for_Discord_is_a_blue_badge_with_the_Discord_icon()
    {
        _vm.ServerHostText = Host;

        _vm.SignInWithDiscordCommand.Execute(null);

        Assert.Equal("Waiting for Discord", _vm.BadgeLabel);
        Assert.Equal(Tone.Blue, _vm.BadgeTone);
        Assert.Equal(Icons.BrandDiscord, _vm.BadgeIcon);
    }

    [Fact]
    public void The_server_field_hides_behind_Change_server_unless_there_is_no_address_yet()
    {
        Build(AppSettings.CreateDefault() with { ServerHost = Host });
        Assert.False(_vm.ShowServerField);

        _vm.ToggleServerFieldCommand.Execute(null);
        Assert.True(_vm.ShowServerField);

        _vm.ToggleServerFieldCommand.Execute(null);
        Assert.False(_vm.ShowServerField);

        Build(AppSettings.CreateDefault() with { ServerHost = "" });
        Assert.True(_vm.ShowServerField);
    }

    [Fact]
    public void Warnings_about_stopped_input_are_errors_and_the_rest_are_not()
    {
        StartLinkedOnline().Receive(Messages.Status(warnings: [StatusWarning.BotDown]));
        Assert.Equal([new WarningNote("A relay bot is down in your team.", false)], _vm.WarningNotes);

        _input.Hook = HookHealth.Failed;
        _input.RaiseChanged();

        Assert.Contains(_vm.WarningNotes, note => note.IsError && note.Text.StartsWith("Keyboard and mouse input stopped", StringComparison.Ordinal));
        Assert.Equal(_vm.WarningNotes.Select(note => note.Text), _vm.Warnings);
    }

    [Fact]
    public void Signing_in_with_Discord_needs_an_address()
    {
        _vm.ServerHostText = " ";

        _vm.SignInWithDiscordCommand.Execute(null);

        Assert.Contains("server address", _vm.LinkMessage);
        Assert.Empty(_platform.OpenedUrls);
        Assert.Empty(_transports.Created);
    }

    [Fact]
    public void Signing_in_with_Discord_connects_with_a_login_id_and_opens_the_browser_with_that_id()
    {
        _vm.ServerHostText = Host;

        _vm.SignInWithDiscordCommand.Execute(null);

        var loginId = _transports.Latest.SentOf<HelloMessage>().Single().LoginId;
        Assert.NotNull(loginId);
        Assert.Equal([$"https://{Host}/auth/start?login={loginId}"], _platform.OpenedUrls);
        Assert.True(_vm.IsSigningIn);
        Assert.Contains("browser", _vm.BadgeDetail);
        Assert.Equal(Host, _store.Load().Settings.ServerHost);
    }

    [Fact]
    public void A_not_a_host_is_refused_for_sign_in()
    {
        _vm.ServerHostText = "https://evil.example/ path";

        _vm.SignInWithDiscordCommand.Execute(null);

        Assert.Empty(_platform.OpenedUrls);
    }

    [Fact]
    public void Cancelling_a_link_attempt_stops_it()
    {
        _vm.ServerHostText = Host;
        _vm.SignInWithDiscordCommand.Execute(null);

        _vm.CancelLinkCommand.Execute(null);

        Assert.False(_vm.IsLinkBusy);
        Assert.True(_transports.Latest.Disposed);
    }

    [Fact]
    public async Task Unlinking_asks_first_then_tells_the_bot_and_forgets_the_token()
    {
        var transport = StartLinkedOnline();

        _vm.UnlinkCommand.Execute(null);
        Assert.True(_vm.ShowUnlinkConfirm);
        Assert.Empty(transport.SentOf<UnlinkMessage>());

        _vm.ConfirmUnlinkCommand.Execute(null);
        await Task.Delay(50);

        Assert.Single(transport.SentOf<UnlinkMessage>());
        Assert.False(_vm.IsLinked);
        Assert.True(_vm.ShowLinkScreen);
        Assert.Null(_store.Load().Settings.DeviceToken);
    }

    [Fact]
    public void Changing_your_mind_about_unlinking_keeps_everything()
    {
        StartLinkedOnline();
        _vm.UnlinkCommand.Execute(null);

        _vm.CancelUnlinkCommand.Execute(null);

        Assert.False(_vm.ShowUnlinkConfirm);
        Assert.True(_vm.IsLinked);
    }

    // ----- Keys to the connection -----

    [Fact]
    public void The_broadcast_key_sends_press_down_and_up_to_the_bot_once_each_despite_auto_repeat()
    {
        var transport = StartLinkedOnline();

        _input.Raise(F8, true);
        _input.Raise(F8, true);
        _input.Raise(F8, true);
        _input.Raise(F8, false);

        Assert.Equal([true, false], transport.SentOf<PressMessage>().Select(p => p.Down));
    }

    [Fact]
    public void A_key_that_is_not_bound_sends_nothing()
    {
        var transport = StartLinkedOnline();

        Press(F10);

        Assert.Empty(transport.SentOf<PressMessage>());
    }

    [Theory]
    [InlineData("mouse")]
    [InlineData("pad")]
    public void A_mouse_side_button_or_a_controller_button_holds_the_broadcast_too(string kind)
    {
        var input = kind == "mouse" ? Side : PadA;
        var transport = StartLinkedOnline(bindings: new BindingSet(input, null, F9));

        Press(input);

        Assert.Equal([true, false], transport.SentOf<PressMessage>().Select(p => p.Down));
    }

    [Fact]
    public void The_button_shows_the_kind_of_input_bound()
    {
        StartLinkedOnline();
        Assert.True(_vm.BroadcastIsKey);
        Assert.Equal("F8", _vm.BroadcastInputName);

        StartLinkedOnline(bindings: new BindingSet(Side, null, F9));
        Assert.True(_vm.BroadcastIsMouse);
        Assert.Equal("Mouse side button 1", _vm.BroadcastInputName);

        StartLinkedOnline(bindings: new BindingSet(PadA, null, F9));
        Assert.True(_vm.BroadcastIsPad);
        Assert.Equal("Controller: A button", _vm.BroadcastInputName);
    }

    [Fact]
    public void The_open_mic_key_toggles_open_mic_for_any_player()
    {
        var transport = StartLinkedOnline();

        Press(F9);
        Assert.True(_vm.OpenMicIsOn);
        Press(F9);

        Assert.Equal([true, false], transport.SentOf<OpenMicMessage>().Select(m => m.On));
        Assert.False(_vm.OpenMicIsOn);
    }

    [Fact]
    public void The_open_mic_row_says_who_hears_it_and_its_chip_names_the_key()
    {
        var transport = StartLinkedOnline();
        Assert.True(_vm.CanToggleOpenMic);
        Assert.Equal("All of Attack hears it", _vm.OpenMicSubtitle);
        Assert.Equal("Stays live until you turn it off", _vm.OpenMicNote);
        Assert.Equal("F9", _vm.OpenMicInputName);
        Assert.Equal(Icons.Keyboard, _vm.OpenMicGlyph);

        transport.Receive(Messages.Status(channel: "Defense 2", group: WireGroup.Defense));
        Assert.Equal("All of Defense hears it", _vm.OpenMicSubtitle);

        transport.Receive(Messages.Status(channel: null, group: null, targets: []));
        Assert.Equal("Your whole team hears it", _vm.OpenMicSubtitle);
    }

    [Fact]
    public void The_open_mic_switch_is_off_limits_while_offline_but_the_row_stays_and_says_why_it_turned_off()
    {
        var transport = StartLinkedOnline();
        _vm.OpenMicIsOn = true;

        transport.Close();

        Assert.False(_vm.CanToggleOpenMic);
        Assert.False(_vm.OpenMicIsOn);
        Assert.Contains("Open mic broadcast turned off when the connection dropped.", _vm.Warnings);

        _vm.OpenMicIsOn = true;
        Assert.False(_vm.OpenMicIsOn);
    }

    [Fact]
    public void A_hook_that_stops_releases_a_held_key_so_the_broadcast_cannot_stick()
    {
        var transport = StartLinkedOnline();
        _input.Raise(F8, true);

        _input.Hook = HookHealth.Failed;
        _input.HookDetail = "macOS switched the key listener off.";
        _input.RaiseChanged();

        Assert.Equal([true, false], transport.SentOf<PressMessage>().Select(p => p.Down));
        Assert.Contains(_vm.Warnings, w => w.StartsWith("Keyboard and mouse input stopped.", StringComparison.Ordinal));
        Assert.Contains(_vm.Warnings, w => w.Contains("switched the key listener off"));
        var note = Assert.Single(_vm.WarningNotes, n => n.Text.StartsWith("Keyboard and mouse input stopped.", StringComparison.Ordinal));
        Assert.Equal("Restart input", note.ActionLabel);
        Assert.Same(_vm.RestartInputCommand, note.Action);
    }

    [Fact]
    public void A_stopped_keyboard_hook_without_a_reason_gets_no_trailing_hint_to_open_Settings()
    {
        StartLinkedOnline();

        _input.Hook = HookHealth.Failed;
        _input.RaiseChanged();

        Assert.Contains("Keyboard and mouse input stopped.", _vm.Warnings);
        Assert.DoesNotContain(_vm.Warnings, w => w.Contains("Settings", StringComparison.Ordinal));
    }

    [Fact]
    public void The_restart_button_of_a_stopped_input_warning_restarts_the_input()
    {
        StartLinkedOnline();
        _input.Hook = HookHealth.Failed;
        _input.RaiseChanged();
        var starts = _input.HookStarts;

        _vm.WarningNotes.Single(n => n.Action is not null).Action!.Execute(null);

        Assert.Equal(starts + 1, _input.HookStarts);
    }

    [Fact]
    public void A_controller_helper_that_dies_releases_a_held_controller_button()
    {
        var transport = StartLinkedOnline(bindings: new BindingSet(PadA, null, F9));
        _input.Raise(PadA, true);

        _input.Pad = PadHealth.Failed;
        _input.RaiseChanged();

        Assert.Equal([true, false], transport.SentOf<PressMessage>().Select(p => p.Down));
        var note = Assert.Single(_vm.WarningNotes, n => n.Text == "Controller input stopped.");
        Assert.True(note.IsError);
        Assert.Equal("Restart input", note.ActionLabel);
        Assert.Same(_vm.RestartInputCommand, note.Action);
    }

    // ----- Warnings -----

    [Fact]
    public void A_bot_down_in_the_team_is_said_in_words()
    {
        var transport = StartLinkedOnline();

        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown]));

        Assert.True(_vm.HasWarnings);
        Assert.Contains("A relay bot is down in your team.", _vm.Warnings);
        Assert.Null(_vm.WarningNotes.Single().Action);
    }

    [Fact]
    public void A_bot_down_in_one_VC_names_it()
    {
        var transport = StartLinkedOnline();

        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown], botDownIn: ["Attack 2"]));

        Assert.Equal(["Attack 2's bot is offline, so Attack 2 can't hear your broadcasts."], _vm.Warnings);
    }

    [Fact]
    public void Bots_down_in_several_VCs_name_them_all()
    {
        var transport = StartLinkedOnline();

        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown], botDownIn: ["Attack 2", "Attack 3"]));
        Assert.Equal(["Bots are offline in Attack 2 and Attack 3, so they can't hear your broadcasts."], _vm.Warnings);

        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown], botDownIn: ["Attack 1", "Attack 2", "Attack 3"]));
        Assert.Equal(["Bots are offline in Attack 1, Attack 2 and Attack 3, so they can't hear your broadcasts."], _vm.Warnings);
    }

    [Fact]
    public void An_empty_or_missing_list_keeps_the_general_bot_down_text()
    {
        var transport = StartLinkedOnline();

        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown], botDownIn: []));
        Assert.Equal(["A relay bot is down in your team."], _vm.Warnings);

        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown]));
        Assert.Equal(["A relay bot is down in your team."], _vm.Warnings);
    }

    [Fact]
    public void A_change_of_which_bots_are_down_alone_refreshes_the_warning()
    {
        var transport = StartLinkedOnline();
        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown], botDownIn: ["Attack 2"]));
        var changes = new List<string?>();
        _vm.PropertyChanged += (_, e) => changes.Add(e.PropertyName);

        transport.Receive(Messages.Status(warnings: [StatusWarning.BotDown], botDownIn: ["Attack 3"]));

        Assert.NotEmpty(changes);
        Assert.Equal(["Attack 3's bot is offline, so Attack 3 can't hear your broadcasts."], _vm.Warnings);
    }

    private const string GameAsAdministrator = "Your game runs as administrator, so the app can't hear your key.";

    private static readonly ForegroundApp Game = new(4242, IsElevated: true);
    private static readonly ForegroundApp Browser = new(777, IsElevated: false);

    [Fact]
    public void Windows_has_no_administrator_warning_until_an_administrator_window_is_in_front()
    {
        _platform.IsWindows = true;
        StartLinkedOnline();
        _foreground.Foreground = Browser;

        Tick(1);

        Assert.DoesNotContain(_vm.Warnings, w => w.Contains("administrator"));
        Assert.True(_vm.CanRestartAsAdministrator);
    }

    [Fact]
    public void An_administrator_game_in_front_warns_with_a_button_to_restart_as_administrator()
    {
        _platform.IsWindows = true;
        StartLinkedOnline();
        _foreground.Foreground = Game;

        Tick(1);

        var note = Assert.Single(_vm.WarningNotes, n => n.Text == GameAsAdministrator);
        Assert.False(note.IsError);
        Assert.Equal("Restart as administrator", note.ActionLabel);
        Assert.Same(_vm.RestartAsAdministratorCommand, note.Action);

        note.Action!.Execute(null);

        Assert.Equal(1, _platform.ElevatedRestarts);
    }

    [Fact]
    public void The_warning_clears_when_a_normal_window_comes_to_the_front_and_not_when_this_app_does()
    {
        _platform.IsWindows = true;
        StartLinkedOnline();
        _foreground.Foreground = Game;
        Tick(1);
        Assert.Contains(GameAsAdministrator, _vm.Warnings);

        // The player clicks this app's own window to reach the button: the warning has to stay.
        _foreground.Foreground = new ForegroundApp(Environment.ProcessId, IsElevated: false);
        Tick(1);
        Assert.Contains(GameAsAdministrator, _vm.Warnings);

        _foreground.Foreground = null;
        Tick(1);
        Assert.Contains(GameAsAdministrator, _vm.Warnings);

        _foreground.Foreground = Browser;
        Tick(1);
        Assert.DoesNotContain(GameAsAdministrator, _vm.Warnings);
    }

    [Fact]
    public void The_window_looks_at_the_foreground_about_once_a_second()
    {
        _platform.IsWindows = true;
        StartLinkedOnline();
        _foreground.Foreground = Browser;
        Tick(1);
        var reads = _foreground.Reads;

        Tick(0.25);
        Tick(0.25);
        Tick(0.25);
        Assert.Equal(reads, _foreground.Reads);

        Tick(0.25);
        Assert.Equal(reads + 1, _foreground.Reads);
    }

    [Fact]
    public void Running_as_administrator_never_warns_or_looks()
    {
        _platform.IsWindows = true;
        _platform.IsElevated = true;
        StartLinkedOnline();
        _foreground.Foreground = Game;

        Tick(1);

        Assert.DoesNotContain(_vm.Warnings, w => w.Contains("administrator"));
        Assert.Equal(0, _foreground.Reads);
        Assert.False(_vm.CanRestartAsAdministrator);
    }

    [Fact]
    public void Becoming_administrator_clears_the_warning()
    {
        _platform.IsWindows = true;
        StartLinkedOnline();
        _foreground.Foreground = Game;
        Tick(1);
        Assert.Contains(GameAsAdministrator, _vm.Warnings);

        _platform.IsElevated = true;

        Assert.DoesNotContain(GameAsAdministrator, _vm.Warnings);
    }

    [Fact]
    public void Restarting_as_administrator_quits_this_copy_only_if_the_new_one_started()
    {
        _platform.IsWindows = true;

        _platform.ElevatedRestartWorks = false;
        _vm.RestartAsAdministratorCommand.Execute(null);
        Assert.Equal(0, _platform.Quits);

        _platform.ElevatedRestartWorks = true;
        _vm.RestartAsAdministratorCommand.Execute(null);

        Assert.Equal(2, _platform.ElevatedRestarts);
        Assert.Equal(1, _platform.Quits);
    }

    [Fact]
    public void Macs_have_no_administrator_warning()
    {
        _platform.IsMac = true;
        StartLinkedOnline();
        _foreground.Foreground = Game;

        Tick(1);

        Assert.DoesNotContain(_vm.Warnings, w => w.Contains("administrator"));
        Assert.Equal(0, _foreground.Reads);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_saved_is_said_in_words()
    {
        StartLinkedOnline();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
        File.WriteAllText(_folder, "a file where the folder should be");

        try
        {
            _vm.NotifyOnDisconnect = true;

            Assert.Contains(_vm.Warnings, w => w.StartsWith("Settings could not be saved.", StringComparison.Ordinal));
        }
        finally
        {
            File.Delete(_folder);
        }
    }

    // ----- macOS permission help -----

    [Fact]
    public void Without_input_monitoring_the_screen_says_so_in_words_and_shows_the_permission()
    {
        _platform.IsMac = true;
        _permissions.InputMonitoring = PermissionState.NotAsked;
        Build(Linked());
        _input.Hook = HookHealth.NeedsAccessibility;

        Assert.True(_vm.NeedsAccessibility);
        var note = Assert.Single(_vm.WarningNotes, note => note.Text.Contains("Input Monitoring", StringComparison.Ordinal));
        Assert.Equal("Open Input Monitoring settings", note.ActionLabel);
        note.Action!.Execute(null);
        Assert.Equal(1, _permissions.OpenedInputMonitoring);
        Assert.Equal("Input Monitoring: not asked yet", _vm.InputMonitoringText);
    }

    [Fact]
    public void The_permission_warning_is_on_the_main_view_and_does_not_throw_the_player_into_settings()
    {
        _platform.IsMac = true;
        _permissions.InputMonitoring = PermissionState.Denied;
        Build(Linked());
        _vm.Start();

        _input.Hook = HookHealth.NeedsAccessibility;
        _input.RaiseChanged();

        Assert.False(_vm.ShowSettings);
        Assert.True(_vm.ShowWarnings);
    }

    [Fact]
    public void The_permission_buttons_open_the_page_and_only_the_prompt_button_asks_macOS()
    {
        _platform.IsMac = true;

        _vm.OpenInputMonitoringSettingsCommand.Execute(null);
        Assert.Equal(0, _permissions.Prompts);

        _vm.ShowMacPromptCommand.Execute(null);

        Assert.Equal(1, _permissions.OpenedInputMonitoring);
        Assert.Equal(1, _permissions.Prompts);
    }

    [Fact]
    public void A_grant_is_noticed_within_a_second_and_the_app_starts_itself_again_once_not_in_a_loop()
    {
        _platform.IsMac = true;
        _permissions.InputMonitoring = PermissionState.Denied;
        Build(Linked());
        _vm.Start();
        _input.Hook = HookHealth.NeedsAccessibility;

        Tick(1.2);
        Tick(1.2);
        Assert.Equal(0, _platform.AppRestarts);

        _permissions.InputMonitoring = PermissionState.Granted;
        Tick(1.2);
        Tick(1.2);
        Tick(1.2);

        Assert.Equal(1, _platform.AppRestarts);
        Assert.Equal(1, _platform.Quits);
        Assert.Equal("Input Monitoring: allowed", _vm.InputMonitoringText);
    }

    [Fact]
    public void Where_the_app_cannot_start_itself_again_a_grant_starts_the_listener_in_place_once()
    {
        _platform.IsMac = true;
        _platform.AppRestartWorks = false;
        _permissions.InputMonitoring = PermissionState.Denied;
        Build(Linked());
        _vm.Start();
        _input.Hook = HookHealth.NeedsAccessibility;
        var starts = _input.HookStarts;

        _permissions.InputMonitoring = PermissionState.Granted;
        Tick(1.2);
        Tick(1.2);
        Tick(1.2);

        Assert.Equal(starts + 1, _input.HookStarts);
        Assert.Equal(0, _platform.Quits);
    }

    [Fact]
    public void A_permission_that_was_never_missing_does_not_restart_the_hook()
    {
        _platform.IsMac = true;
        _permissions.InputMonitoring = PermissionState.Granted;
        Build(Linked());
        _vm.Start();
        var starts = _input.HookStarts;

        Tick(1.2);
        Tick(1.2);

        Assert.Equal(starts, _input.HookStarts);
    }

    [Fact]
    public void Restarting_input_starts_the_hook_again_and_lets_go_of_held_keys()
    {
        var transport = StartLinkedOnline();
        _input.Raise(F8, true);
        var starts = _input.HookStarts;

        _vm.RestartInputCommand.Execute(null);

        Assert.Equal(starts + 1, _input.HookStarts);
        Assert.Equal([true, false], transport.SentOf<PressMessage>().Select(p => p.Down));
    }

    // ----- Changing the broadcast input: the next input of any kind becomes the binding -----

    [Theory]
    [InlineData("key")]
    [InlineData("mouse")]
    [InlineData("pad")]
    public void Clicking_the_button_then_pressing_any_kind_of_input_binds_it_saves_it_and_listens_to_it(string kind)
    {
        var picked = kind switch { "key" => F10, "mouse" => Side, _ => PadA };
        var transport = StartLinkedOnline();

        _vm.ChangeBroadcastCommand.Execute(null);
        Assert.True(_vm.IsCapturingBroadcast);
        Assert.Equal("Press any input", _vm.BroadcastInputName);
        Assert.Equal("Press any key, mouse button or controller button.", _vm.BroadcastHint);
        _input.Raise(picked, true);

        Assert.False(_vm.IsCapturingBroadcast);
        Assert.Equal(picked.DisplayName, _vm.BroadcastInputName);
        Assert.Equal(picked, _store.Load().Settings.Bindings.Broadcast);
        Assert.Equal("Click any key to change it", _vm.BroadcastHint);
        _input.Raise(picked, false);
        Press(F8);
        Assert.Empty(transport.SentOf<PressMessage>());
        Press(picked);
        Assert.Equal([true, false], transport.SentOf<PressMessage>().Select(p => p.Down));
    }

    [Fact]
    public void The_helper_runs_only_while_a_pick_waits_or_a_controller_is_bound()
    {
        StartLinkedOnline();

        _vm.ChangeBroadcastCommand.Execute(null);
        Assert.Equal(1, _input.PadStarts);
        _vm.ChangeBroadcastCommand.Execute(null);
        Assert.Equal(1, _input.PadStops);

        _vm.ChangeBroadcastCommand.Execute(null);
        _input.Raise(PadA, true);
        Assert.Equal(1, _input.PadStops);
        Assert.True(_vm.Settings.Bindings.UsesPad);

        _vm.ChangeBroadcastCommand.Execute(null);
        _input.Raise(F10, true);
        Assert.Equal(2, _input.PadStops);
    }

    [Fact]
    public void Clicking_the_button_again_cancels_the_pick()
    {
        StartLinkedOnline();
        _vm.ChangeBroadcastCommand.Execute(null);

        _vm.ChangeBroadcastCommand.Execute(null);

        Assert.False(_vm.IsCapturingBroadcast);
        Assert.Equal("F8", _vm.BroadcastInputName);
    }

    [Fact]
    public void The_input_picked_does_not_start_a_broadcast_when_it_auto_repeats_afterwards()
    {
        var transport = StartLinkedOnline();
        _vm.ChangeBroadcastCommand.Execute(null);

        _input.Raise(F10, true);
        _input.Raise(F10, true);
        _input.Raise(F10, true);
        _input.Raise(F10, false);

        Assert.Empty(transport.SentOf<PressMessage>());
    }

    [Fact]
    public void While_picking_a_binding_the_old_input_starts_no_broadcast()
    {
        var transport = StartLinkedOnline();
        _vm.ChangeBroadcastCommand.Execute(null);

        Press(F8);

        Assert.Empty(transport.SentOf<PressMessage>());
    }

    [Fact]
    public void Escape_is_bound_like_any_other_key_and_Cancel_is_how_picking_stops()
    {
        StartLinkedOnline();
        var escape = InputBinding.Key(KeyCode.VcEscape);

        _vm.ChangeBroadcastCommand.Execute(null);
        _vm.CancelCaptureCommand.Execute(null);
        Assert.False(_vm.IsCapturingBroadcast);
        Assert.Equal(BindingSet.Default, _vm.Settings.Bindings);

        _vm.ChangeBroadcastCommand.Execute(null);
        _input.Raise(escape, true);

        Assert.False(_vm.IsCapturingBroadcast);
        Assert.Equal(escape, _vm.Settings.Bindings.Broadcast);
        Assert.Equal("Escape", _vm.BroadcastInputName);
    }

    [Fact]
    public void The_input_of_the_other_action_is_refused_and_picking_goes_on()
    {
        StartLinkedOnline();
        _vm.ChangeBroadcastCommand.Execute(null);

        _input.Raise(F9, true);

        Assert.True(_vm.IsCapturingBroadcast);
        Assert.Equal("F9 is taken by open mic broadcast. Press another input.", _vm.BroadcastHint);
        Assert.Equal(BindingSet.Default, _vm.Settings.Bindings);

        _input.Raise(F10, true);
        Assert.False(_vm.IsCapturingBroadcast);
    }

    [Fact]
    public void The_left_mouse_button_is_refused_as_a_binding_but_the_click_that_opened_the_pick_is_not_counted()
    {
        StartLinkedOnline();
        _vm.ChangeBroadcastCommand.Execute(null);
        var left = InputBinding.Mouse(MouseButton.Button1);

        _input.Raise(left, true);
        _input.Raise(left, false);
        Assert.Equal("Press any key, mouse button or controller button.", _vm.BroadcastHint);

        _time.Advance(TimeSpan.FromSeconds(1));
        _input.Raise(left, true);

        Assert.True(_vm.IsCapturingBroadcast);
        Assert.Equal("The left mouse button can't be a binding. Press another input.", _vm.BroadcastHint);
    }

    [Fact]
    public void The_open_mic_key_is_changed_from_its_chip_the_same_way()
    {
        StartLinkedOnline();

        _vm.ChangeOpenMicCommand.Execute(null);
        Assert.True(_vm.IsCapturingOpenMic);
        Assert.Equal("Press any input", _vm.OpenMicInputName);
        _input.Raise(Side, true);

        Assert.Equal("Mouse side button 1", _vm.OpenMicInputName);
        Assert.Equal(Side, _store.Load().Settings.Bindings.OpenMic);
        Assert.Equal(F8, _vm.Settings.Bindings.Broadcast);
    }

    // ----- Settings -----

    [Fact]
    public void Settings_are_collapsed_by_default_and_show_who_and_where_and_the_version()
    {
        StartLinkedOnline();

        Assert.False(_vm.ShowSettings);

        _vm.ShowSettings = true;

        Assert.Equal("Signed in as Alex", _vm.LinkedAs);
        Assert.Equal($"Server: {Host}", _vm.ServerLine);
        Assert.StartsWith("Version ", _vm.Version, StringComparison.Ordinal);
    }

    [Fact]
    public void A_drop_to_Disconnected_notifies_once_when_the_player_asked_for_it()
    {
        var settings = Linked() with { NotifyOnDisconnect = true };
        Build(settings);
        _vm.Start();
        _transports.Latest.Receive(Messages.Welcome());
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException();
        _transports.Latest.Close();

        Tick(5);
        Assert.Empty(_platform.Notifications);
        Tick(11);
        Tick(1);

        Assert.Single(_platform.Notifications);
    }

    [Fact]
    public void Nothing_is_announced_when_notifications_are_off_or_the_app_never_connected()
    {
        StartLinkedOnline();
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException();
        _transports.Latest.Close();
        Tick(20);
        Assert.Empty(_platform.Notifications);

        Build(Linked() with { NotifyOnDisconnect = true });
        _transports.Configure = t => t.ConnectFailure = new HttpRequestException();
        _vm.Start();
        Tick(20);

        Assert.Empty(_platform.Notifications);
    }

    [Fact]
    public void The_notification_choice_is_saved()
    {
        StartLinkedOnline();

        _vm.NotifyOnDisconnect = true;

        Assert.True(_store.Load().Settings.NotifyOnDisconnect);
    }

    [Fact]
    public void A_note_from_before_the_window_opened_is_shown()
    {
        _vm.ShowNote("Settings were broken.");

        Assert.Equal("Settings were broken.", _vm.LinkMessage);
    }

    [Fact]
    public void The_tray_tooltip_names_the_state()
    {
        StartLinkedOnline();

        Assert.Equal("KZG Broadcast: Connected", _vm.TrayToolTip);
    }

    [Fact]
    public void The_tray_shows_a_red_mic_while_open_mic_is_on_and_tells_the_tray_when_it_changes()
    {
        var transport = StartLinkedOnline();
        var count = 0;
        _vm.BadgeChanged += () => count++;

        _vm.OpenMicIsOn = true;
        transport.Receive(Messages.Status(active: [WireAction.OpenMic], openMic: true));
        Assert.Equal(TrayState.OpenMic, _vm.Tray);
        Assert.Equal("KZG Broadcast: Open mic on", _vm.TrayToolTip);

        // Holding a key on top of open mic keeps the mic: it is still live when the key comes up.
        _input.Raise(F8, true);
        Assert.Equal(TrayState.OpenMic, _vm.Tray);
        _input.Raise(F8, false);

        // Off: the bot relays on until its next status, which the tray shows as broadcasting.
        _vm.OpenMicIsOn = false;
        Assert.Equal(TrayState.Broadcasting, _vm.Tray);
        transport.Receive(Messages.Status());
        Assert.Equal(TrayState.Connected, _vm.Tray);
        Assert.Equal(3, count);
    }

    [Fact]
    public void A_link_that_is_down_wins_over_open_mic_in_the_tray()
    {
        var transport = StartLinkedOnline();
        _vm.OpenMicIsOn = true;
        transport.Receive(Messages.Status(active: [WireAction.OpenMic], openMic: true));

        _transports.Configure = t => t.ConnectFailure = new HttpRequestException();
        transport.Close();
        Assert.Equal(TrayState.Reconnecting, _vm.Tray);

        Tick(16);
        Assert.Equal(TrayState.Disconnected, _vm.Tray);
    }

    // ----- The all-teams key -----

    [Fact]
    public void The_all_teams_card_names_both_teams_and_its_key()
    {
        StartLinkedOnline();

        Assert.Equal("Hold", _vm.AllTeamsLead);
        Assert.Equal("to talk to all teams", _vm.AllTeamsTail);
        Assert.Equal("F7", _vm.AllTeamsInputName);
        Assert.Equal(Icons.Keyboard, _vm.AllTeamsGlyph);
        Assert.True(_vm.AllTeamsShowsGlyph);
        Assert.False(_vm.AllTeamsIsUnset);
    }

    [Fact]
    public void Holding_the_all_teams_key_sends_its_press_lights_its_card_and_says_live_to_all_teams()
    {
        var transport = StartLinkedOnline();

        _input.Raise(F7, true);
        transport.Receive(Messages.Status(active: [WireAction.AllTeams], targets: ["Attack 2", "Attack 3", "Defense 1", "Defense 2"]));

        Assert.Equal([(WireAction.AllTeams, true)], transport.SentOf<PressMessage>().Select(p => (p.Action, p.Down)));
        Assert.True(_vm.ShowAllTeamsHeld);
        Assert.False(_vm.ShowBroadcastHeld);
        Assert.Equal("Holding", _vm.AllTeamsLead);
        Assert.Equal("Talking to all teams", _vm.AllTeamsTail);
        Assert.Equal("Let go to stop", _vm.BroadcastHint);
        Assert.Equal("You're live to all teams", _vm.BadgeLabel);
        Assert.Equal("Attack and Defense hear you", _vm.BadgeDetail);
        Assert.Equal(TrayState.Broadcasting, _vm.Tray);

        transport.Receive(Messages.Status(active: [WireAction.AllTeams], targets: []));
        Assert.Equal("No one hears you yet", _vm.BadgeDetail);

        _input.Raise(F7, false);
        Assert.Equal((WireAction.AllTeams, false), transport.SentOf<PressMessage>().Select(p => (p.Action, p.Down)).Last());
        Assert.False(_vm.ShowAllTeamsHeld);
    }

    [Fact]
    public void The_all_teams_key_is_changed_from_its_chip()
    {
        var transport = StartLinkedOnline();

        _vm.ChangeAllTeamsCommand.Execute(null);
        Assert.True(_vm.IsCapturingAllTeams);
        Assert.False(_vm.ShowAllTeamsTail);
        Assert.Equal("Press any input", _vm.AllTeamsInputName);
        _input.Raise(F10, true);
        _input.Raise(F10, false);

        Assert.False(_vm.IsCapturingAllTeams);
        Assert.Equal(F10, _store.Load().Settings.Bindings.AllTeams);
        Press(F10);
        Assert.Equal([WireAction.AllTeams, WireAction.AllTeams], transport.SentOf<PressMessage>().Select(p => p.Action));
    }

    [Fact]
    public void An_input_bound_to_one_action_is_refused_for_another()
    {
        StartLinkedOnline();
        _vm.ChangeAllTeamsCommand.Execute(null);

        _input.Raise(F8, true);

        Assert.True(_vm.IsCapturingAllTeams);
        Assert.Equal("F8 is taken by broadcast to your team. Press another input.", _vm.BroadcastHint);
        Assert.Equal(BindingSet.Default, _vm.Settings.Bindings);

        _vm.ChangeOpenMicCommand.Execute(null);
        _input.Raise(F7, true);
        Assert.Equal("F7 is taken by broadcast to all teams. Press another input.", _vm.BroadcastHint);
    }

    // ----- Clearing a key -----

    [Theory]
    [InlineData(BindingAction.Broadcast)]
    [InlineData(BindingAction.AllTeams)]
    [InlineData(BindingAction.OpenMic)]
    public void Clear_leaves_the_key_unset_saves_that_and_its_key_then_does_nothing(BindingAction action)
    {
        var transport = StartLinkedOnline();
        var (change, key) = action switch
        {
            BindingAction.Broadcast => (_vm.ChangeBroadcastCommand, F8),
            BindingAction.AllTeams => (_vm.ChangeAllTeamsCommand, F7),
            _ => (_vm.ChangeOpenMicCommand, F9),
        };

        change.Execute(null);
        _vm.ClearBindingCommand.Execute(null);

        Assert.Null(_vm.Settings.Bindings[action]);
        Assert.Null(_store.Load().Settings.Bindings[action]);
        Assert.Equal("Click a key to set it", _vm.BroadcastHint);
        Press(key);
        Assert.Empty(transport.SentOf<PressMessage>());
        Assert.Empty(transport.SentOf<OpenMicMessage>());
    }

    [Fact]
    public void An_unset_chip_reads_not_set_quietly_and_is_still_clicked_to_set_it()
    {
        var transport = StartLinkedOnline(bindings: new BindingSet(F8, null, F9));

        Assert.Equal("Not set", _vm.AllTeamsInputName);
        Assert.True(_vm.AllTeamsIsUnset);
        Assert.False(_vm.AllTeamsShowsGlyph);
        Assert.Equal("Click a key to set it", _vm.BroadcastHint);

        _vm.ChangeAllTeamsCommand.Execute(null);
        Assert.False(_vm.AllTeamsIsUnset);
        _input.Raise(F7, true);

        Assert.Equal("F7", _vm.AllTeamsInputName);
        Assert.Equal("Click any key to change it", _vm.BroadcastHint);
        // The press that picked it is not a broadcast.
        Assert.Empty(transport.SentOf<PressMessage>());
    }

    [Fact]
    public void With_no_open_mic_key_the_switch_still_turns_it_on_and_off()
    {
        var transport = StartLinkedOnline(bindings: new BindingSet(null, null, null));
        Assert.Equal("Not set", _vm.OpenMicInputName);
        Assert.True(_vm.OpenMicKeyIsUnset);
        Assert.Equal("Not set", _vm.BroadcastInputName);

        _vm.OpenMicIsOn = true;
        _vm.OpenMicIsOn = false;

        Assert.Equal([true, false], transport.SentOf<OpenMicMessage>().Select(m => m.On));
    }

    [Fact]
    public void Cancel_keeps_the_key_as_it_was()
    {
        StartLinkedOnline();
        _vm.ChangeOpenMicCommand.Execute(null);

        _vm.CancelCaptureCommand.Execute(null);

        Assert.False(_vm.IsCapturingOpenMic);
        Assert.Equal("F9", _vm.OpenMicInputName);
        Assert.Equal(BindingSet.Default, _vm.Settings.Bindings);
    }

    [Fact]
    public void Clear_with_nothing_being_picked_changes_nothing()
    {
        StartLinkedOnline();

        _vm.ClearBindingCommand.Execute(null);

        Assert.Equal(BindingSet.Default, _vm.Settings.Bindings);
    }

    [Fact]
    public void Quit_asks_the_platform_to_quit()
    {
        _vm.QuitCommand.Execute(null);

        Assert.Equal(1, _platform.Quits);
    }

    [Fact]
    public void A_dev_override_lets_a_linked_pc_connect_without_a_saved_host()
    {
        var settings = Linked() with { ServerHost = "" };
        _store = new SettingsStore(Path.Combine(_folder, "settings.json"));
        var connection = new ConnectionController(
            _transports, _time, settings.InstallId, "1.0.0", new ConnectionOptions { ServerOverride = new Uri("ws://127.0.0.1:7878/ws") });
        _vm = new MainViewModel(settings, _store, connection, _input, _permissions, _platform, new InlineUi(), _time, _foreground);

        _vm.Start();

        Assert.Equal(new Uri("ws://127.0.0.1:7878/ws"), _transports.Latest.ConnectedTo);
    }

    // ----- Open at login -----

    [Fact]
    public void Open_at_login_is_off_until_the_player_turns_it_on_and_follows_the_systems_list()
    {
        _login = new FakeStartAtLogin();
        Build(Linked());

        Assert.True(_vm.ShowOpenAtLogin);
        Assert.False(_vm.OpenAtLogin);

        _vm.OpenAtLogin = true;
        Assert.True(_login.IsEnabled);
        Assert.True(_vm.OpenAtLogin);

        _vm.OpenAtLogin = false;
        Assert.False(_login.IsEnabled);
    }

    [Fact]
    public void Open_at_login_is_hidden_where_the_app_cannot_be_started_at_login()
    {
        _login = new FakeStartAtLogin { IsAvailable = false };
        Build(Linked());

        Assert.False(_vm.ShowOpenAtLogin);
        _vm.OpenAtLogin = true;
        Assert.False(_login.IsEnabled);
    }

    [Fact]
    public void A_login_list_that_refuses_leaves_the_switch_off_and_says_so()
    {
        _login = new FakeStartAtLogin { Fails = true };
        Build(Linked());

        _vm.OpenAtLogin = true;

        Assert.False(_vm.OpenAtLogin);
        Assert.Contains("opens at login", _vm.LinkMessage, StringComparison.Ordinal);
    }

    // ----- Updates -----

    [Fact]
    public void Without_an_update_feed_nothing_about_updates_shows_and_nothing_is_checked()
    {
        _updater = new FakeUpdater { Stage = UpdateStage.Off };
        Build(Linked());
        _vm.Start();

        Tick(60);

        Assert.False(_vm.ShowUpdates);
        Assert.Equal(0, _updater.Checks);
    }

    [Fact]
    public void An_update_is_looked_for_half_a_minute_after_the_start_and_then_every_six_hours()
    {
        _updater = new FakeUpdater();
        Build(Linked());
        _vm.Start();

        Tick(20);
        Assert.Equal(0, _updater.Checks);
        Tick(11);
        Assert.Equal(1, _updater.Checks);
        Tick(5.9 * 3600);
        Assert.Equal(1, _updater.Checks);
        Tick(0.2 * 3600);
        Assert.Equal(2, _updater.Checks);
    }

    [Fact]
    public void A_scheduled_check_waits_while_the_player_is_broadcasting()
    {
        _updater = new FakeUpdater();
        StartLinkedOnline();
        _input.Raise(F8, true);
        Assert.True(_vm.IsBroadcasting);

        // Past the 30 s mark, with the link kept alive so the badge stays "live".
        for (var beat = 0; beat < 17; beat++)
        {
            Tick(2);
            _transports.Latest.Receive(new HeartbeatAckMessage(beat, 0));
        }
        Assert.True(_vm.IsBroadcasting);
        Assert.Equal(0, _updater.Checks);

        _input.Raise(F8, false);
        Tick(1);
        Assert.Equal(1, _updater.Checks);
    }

    [Fact]
    public void The_restart_to_update_button_is_off_while_the_player_is_broadcasting()
    {
        _updater = new FakeUpdater { Stage = UpdateStage.Ready, ReadyVersion = "0.2.0" };
        StartLinkedOnline();
        _input.Raise(F8, true);

        Assert.False(_vm.RestartToUpdateCommand.CanExecute(null));
    }

    [Fact]
    public void A_downloaded_update_is_offered_as_a_note_with_a_restart_button()
    {
        _updater = new FakeUpdater { Stage = UpdateStage.Ready, ReadyVersion = "0.2.0" };
        Build(Linked());

        var note = Assert.Single(_vm.WarningNotes);
        Assert.True(note.IsInfo);
        Assert.False(note.IsAmber);
        Assert.Contains("0.2.0", note.Text, StringComparison.Ordinal);
        Assert.Equal("Restart to update", note.ActionLabel);

        note.Action!.Execute(null);
        Assert.Equal(1, _updater.Restarts);
    }

    [Fact]
    public void The_updates_row_says_where_it_stands_and_checks_on_request()
    {
        _updater = new FakeUpdater();
        Build(Linked());

        Assert.True(_vm.ShowUpdates);
        Assert.Equal("You have the newest version", _vm.UpdateStatusText);
        Assert.True(_vm.CanCheckForUpdates);

        _vm.CheckForUpdatesCommand.Execute(null);
        Assert.Equal(1, _updater.Checks);

        _updater.Stage = UpdateStage.Failed;
        Assert.Equal("Couldn't check for updates", _vm.UpdateStatusText);
        _updater.Stage = UpdateStage.Checking;
        Assert.False(_vm.CanCheckForUpdates);
    }
}
