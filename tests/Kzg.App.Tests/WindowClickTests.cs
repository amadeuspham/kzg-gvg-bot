using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Kzg.App.Input;
using Kzg.App.Link;
using Kzg.App.Settings;
using Kzg.App.ViewModels;
using Kzg.Protocol;

[assembly: AvaloniaTestApplication(typeof(Kzg.App.Tests.TestApp))]

namespace Kzg.App.Tests;

/// <summary>The real <see cref="App"/> styles on Avalonia's headless platform, so a test can show the real window.</summary>
public static class TestApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

/// <summary>
/// Clicks the real buttons of the real window with Avalonia's in-process headless mouse (no operating-system
/// input), then checks what the player would see. The view-model tests can't catch an unbound button or a
/// window that doesn't grow.
/// </summary>
public sealed class WindowClickTests : IDisposable
{
    private const string Host = "gvg.example.com";

    private static readonly HeadlessUnitTestSession Session = HeadlessUnitTestSession.StartNew(typeof(TestApp));

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-click-tests-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeSource _time = new();
    private readonly FakeTransportFactory _transports = new();
    private readonly FakeInputService _input = new();
    private readonly FakePermissions _permissions = new();
    private readonly FakePlatform _platform = new();
    private readonly List<MainViewModel> _models = [];

    public void Dispose()
    {
        foreach (var model in _models)
        {
            model.Dispose();
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private MainViewModel Build(bool linked)
    {
        var settings = AppSettings.CreateDefault() with
        {
            ServerHost = Host,
            DeviceToken = linked ? WireSecret.Generate() : null,
        };
        var store = new SettingsStore(Path.Combine(_folder, Guid.NewGuid().ToString("N"), "settings.json"));
        var connection = new ConnectionController(_transports, _time, settings.InstallId, "1.0.0");
        var model = new MainViewModel(settings, store, connection, _input, _permissions, _platform, new InlineUi(), _time);
        _models.Add(model);
        model.Start();
        return model;
    }

    private MainViewModel Online()
    {
        var model = Build(linked: true);
        _transports.Latest.Receive(Messages.Welcome());
        _transports.Latest.Receive(Messages.Status());
        return model;
    }

    private static Window Show(MainViewModel model)
    {
        var window = new MainWindow { DataContext = model };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        return window;
    }

    private static Button ButtonWith(Window window, string text) => window.GetVisualDescendants().OfType<Button>()
        .First(button => button.IsEffectivelyVisible
            && (button.Content as string == text || button.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == text)));

    private static void Click(Window window, Button button)
    {
        // The pages scroll when they are longer than the window, as a player would scroll to reach a button.
        button.BringIntoView();
        Dispatcher.UIThread.RunJobs();
        var centre = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)
            ?? throw new InvalidOperationException("The button is not on screen.");
        window.MouseMove(centre);
        window.MouseDown(centre, MouseButton.Left);
        window.MouseUp(centre, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsShown(Window window, string text) =>
        window.GetVisualDescendants().OfType<TextBlock>().Any(block => block.Text == text && block.IsEffectivelyVisible);

    [Fact]
    public async Task Clicking_Settings_shows_the_settings_page_and_Back_returns_to_the_main_view()
    {
        await Session.Dispatch(
            () =>
            {
                var model = Online();
                var window = Show(model);
                Assert.True(IsShown(window, "You're connected"));
                Assert.True(IsShown(window, "Open mic broadcast"));
                Assert.False(IsShown(window, "Unlink this PC"));

                Click(window, ButtonWith(window, "Settings"));

                Assert.True(model.ShowSettingsPage);
                Assert.True(IsShown(window, "Unlink this PC"));
                Assert.True(IsShown(window, "Settings"));
                // Keys are set on the main window's chips only.
                Assert.False(IsShown(window, "Open mic broadcast key"));
                Assert.False(IsShown(window, "F9"));
                Assert.False(IsShown(window, "You're connected"));
                Assert.False(IsShown(window, "Hold"));

                Click(window, ButtonWith(window, "Back"));

                Assert.False(model.ShowSettings);
                Assert.True(IsShown(window, "You're connected"));
                Assert.True(IsShown(window, "Hold"));
                Assert.False(IsShown(window, "Unlink this PC"));
                window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task The_window_keeps_its_size_across_every_toggle()
    {
        await Session.Dispatch(
            () =>
            {
                var model = Online();
                var window = Show(model);
                var size = (window.Bounds.Width, window.Bounds.Height, window.ClientSize);
                Assert.Equal(420, size.Width);

                void AssertSameSize(string after) =>
                    Assert.True((window.Bounds.Width, window.Bounds.Height, window.ClientSize) == size, $"The window changed size after {after}.");

                Click(window, ButtonWith(window, "Settings"));
                AssertSameSize("opening Settings");
                Click(window, ButtonWith(window, "Keys not working in game?"));
                Assert.True(model.ShowInputHelp);
                AssertSameSize("opening the input help");
                Click(window, ButtonWith(window, "Unlink this PC"));
                Assert.True(model.ShowUnlinkConfirm);
                AssertSameSize("asking to unlink");
                Click(window, ButtonWith(window, "Keep linked"));
                Assert.False(model.ShowUnlinkConfirm);
                AssertSameSize("keeping the link");
                Click(window, ButtonWith(window, "Keys not working in game?"));
                Assert.False(model.ShowInputHelp);
                AssertSameSize("closing the input help");
                Click(window, ButtonWith(window, "Back"));
                Assert.False(model.ShowSettings);
                AssertSameSize("going back");
                window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task The_link_screen_keeps_the_same_size_when_the_server_field_opens()
    {
        await Session.Dispatch(
            () =>
            {
                var model = Build(linked: false);
                var window = Show(model);
                var size = window.ClientSize;
                Assert.False(IsServerFieldShown(window));

                Click(window, ButtonWith(window, "Change bot address"));

                Assert.True(IsServerFieldShown(window));
                Assert.Equal(size, window.ClientSize);
                Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), box => box.PlaceholderText == "bot.example.org" && AutomationProperties.GetName(box) == "Bot address");
                Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), block => block.Text == "Bot address" && block.IsEffectivelyVisible);
                Assert.Equal("Discord Broadcast", window.Title);

                Click(window, ButtonWith(window, "Change bot address"));

                Assert.False(IsServerFieldShown(window));
                Assert.Equal(size, window.ClientSize);
                window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Clicking_Keys_not_working_in_game_opens_and_closes_its_help_on_the_settings_page()
    {
        await Session.Dispatch(
            () =>
            {
                var model = Online();
                var window = Show(model);
                Click(window, ButtonWith(window, "Settings"));
                Assert.False(IsShown(window, "Restart input"));

                Click(window, ButtonWith(window, "Keys not working in game?"));

                Assert.True(model.ShowInputHelp);
                Assert.True(IsShown(window, "Restart input"));

                Click(window, ButtonWith(window, "Keys not working in game?"));

                Assert.False(IsShown(window, "Restart input"));
                window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Going_Back_cancels_a_pending_unlink_question()
    {
        await Session.Dispatch(
            () =>
            {
                var model = Online();
                var window = Show(model);
                Click(window, ButtonWith(window, "Settings"));
                Click(window, ButtonWith(window, "Unlink this PC"));
                Assert.True(model.ShowUnlinkConfirm);

                Click(window, ButtonWith(window, "Back"));
                Click(window, ButtonWith(window, "Settings"));

                Assert.False(model.ShowUnlinkConfirm);
                Assert.True(IsShown(window, "Unlink this PC"));
                window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Clicking_Retry_now_connects_at_once()
    {
        await Session.Dispatch(
            () =>
            {
                var model = Online();
                _transports.Configure = t => t.ConnectFailure = new HttpRequestException("down");
                _transports.Latest.Close();
                _time.Advance(TimeSpan.FromSeconds(15));
                model.Tick();
                var window = Show(model);
                Assert.True(IsShown(window, "Can't reach the bot"));
                var attempts = _transports.Created.Count;

                Click(window, ButtonWith(window, "Retry now"));

                Assert.Equal(attempts + 1, _transports.Created.Count);
                window.Close();
            },
            CancellationToken.None);
    }

    [Fact]
    public async Task Clicking_Restart_input_in_a_stopped_input_warning_restarts_the_input()
    {
        await Session.Dispatch(
            () =>
            {
                var model = Online();
                _input.Hook = HookHealth.Failed;
                _input.Pad = PadHealth.Failed;
                _input.RaiseChanged();
                var window = Show(model);
                var starts = _input.HookStarts;
                var buttons = window.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible && b.Content as string == "Restart input").ToList();
                Assert.Equal(2, buttons.Count);

                Click(window, buttons[0]);

                Assert.Equal(starts + 1, _input.HookStarts);
                window.Close();
            },
            CancellationToken.None);
    }

    private static bool IsServerFieldShown(Window window) =>
        window.GetVisualDescendants().OfType<TextBox>().Any(box => box.PlaceholderText == "bot.example.org" && box.IsEffectivelyVisible);
}
