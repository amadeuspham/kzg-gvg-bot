using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Kzg.App.Input;
using Kzg.App.Link;
using Kzg.App.Services;
using Kzg.App.Settings;
using Kzg.App.ViewModels;
using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.App;

public partial class App : Application
{
    private const int IconSize = 32;

    private MainViewModel? _viewModel;
    private InputService? _input;
    private IUpdater? _updater;
    private TrayIcon? _tray;
    private DispatcherTimer? _clock;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            Compose(desktop);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private void Compose(IClassicDesktopStyleApplicationLifetime desktop)
    {
        var demo = Program.Demo;
        var folder = Program.DataFolder();
        var store = new SettingsStore(Path.Combine(folder, "settings.json"));
        var load = store.Load();
        var settings = load.Settings;
        if (demo is not null)
        {
            // Pretend this PC is already linked, so the window opens on the connected screens.
            settings = settings with { ServerHost = "demo.example.com", DeviceToken = WireSecret.Generate() };
        }

        var time = new SystemTimeSource();
        ILinkTransportFactory transports = demo is { } mode
            ? new DemoTransportFactory(mode, action => Dispatcher.UIThread.Post(action))
            : new WebSocketLinkTransportFactory(action => Dispatcher.UIThread.Post(action));
        var connection = new ConnectionController(transports, time, settings.InstallId, AppInfo.Version, new ConnectionOptions { ServerOverride = Program.ServerOverride });
        var permissions = new SystemPermissions();
        _input = new InputService(time, permissions);
        var platform = new DesktopPlatformServices(() => desktop.Shutdown());
        _updater = UpdateFeed.ForThisProcess();
        _viewModel = new MainViewModel(
            settings, store, connection, _input, permissions, platform, new AvaloniaUiThread(), time,
            login: demo is null ? StartAtLogin.ForThisProcess() : null, updater: demo is null ? _updater : null);
        if (load is SettingsLoad.Invalid invalid)
        {
            _viewModel.ShowNote($"{invalid.Reason} Starting with fresh settings; the old file was kept as settings.invalid.json.");
        }

        var window = new MainWindow { DataContext = _viewModel };
        desktop.MainWindow = window;
        if (Program.StartMinimized)
        {
            window.WindowState = WindowState.Minimized;
        }
        // The macOS key hook is serviced by this UI loop, so it starts only once the window is up and the loop is running.
        window.Opened += (_, _) => Dispatcher.UIThread.Post(_viewModel.Start, DispatcherPriority.Background);

        _clock = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal, (_, _) => _viewModel.Tick());
        _clock.Start();

        CreateTray(window);
        if (Program.Instance is { } instance)
        {
            instance.ShowRequested += () => Dispatcher.UIThread.Post(() =>
            {
                window.Show();
                window.WindowState = WindowState.Normal;
                window.Activate();
            });
        }
        _viewModel.BadgeChanged += () => ShowBadge(window);
        desktop.Exit += (_, _) =>
        {
            _clock.Stop();
            _updater?.ApplyWhenQuitting();
            _viewModel.Dispose();
            _input.Dispose();
        };
    }

    private void CreateTray(Window window)
    {
        var show = new NativeMenuItem("Show window");
        show.Click += (_, _) =>
        {
            window.Show();
            window.Activate();
        };
        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => _viewModel!.QuitCommand.Execute(null);
        _tray = new TrayIcon { Menu = [show, quit] };
        TrayIcon.SetIcons(this, [_tray]);
        ShowBadge(window);
    }

    private void ShowBadge(Window window)
    {
        var icon = new WindowIcon(IconFor(_viewModel!.Tray));
        _tray!.Icon = icon;
        _tray.ToolTipText = _viewModel.TrayToolTip;
    }

    private static WriteableBitmap IconFor(TrayState state)
    {
        var bitmap = new WriteableBitmap(new PixelSize(IconSize, IconSize), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Premul);
        using var frame = bitmap.Lock();
        var pixels = StatusIcon.RenderBgra(state, IconSize);
        for (var row = 0; row < IconSize; row++)
        {
            System.Runtime.InteropServices.Marshal.Copy(pixels, row * IconSize * 4, frame.Address + (row * frame.RowBytes), IconSize * 4);
        }
        return bitmap;
    }
}
