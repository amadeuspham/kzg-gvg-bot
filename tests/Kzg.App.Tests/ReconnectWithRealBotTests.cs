using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Kzg.App.Link;
using Kzg.App.Services;
using Kzg.App.Settings;
using Kzg.App.ViewModels;
using Kzg.Core.Abstractions;

namespace Kzg.App.Tests;

/// <summary>
/// The app's real connection controller and view model against the real bot host (<c>--fake</c>, so no Discord),
/// over a real loopback WebSocket, in real time: the bot dies and comes back, and the app must follow on its own.
/// </summary>
public sealed class ReconnectWithRealBotTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-reconnect-" + Guid.NewGuid().ToString("N"));
    private readonly UiLoop _ui = new();
    private MainViewModel? _vm;

    public void Dispose()
    {
        _ui.Run(() => _vm?.Dispose());
        _ui.Dispose();
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <summary>One run of the real bot on a fixed loopback port, with a data folder that outlives it (devices.json).</summary>
    private sealed class RunningBot
    {
        private readonly CancellationTokenSource _stop = new();
        private readonly Task<int> _run;
        private readonly StringWriter _output = new();

        private RunningBot(string dataFolder, int port, TaskCompletionSource<Kzg.Bot.BotApp> started)
        {
            _run = Kzg.Bot.Program.RunAsync(
                ["--fake", "--data-dir", dataFolder, "--listen", $"127.0.0.1:{port}"],
                TextWriter.Synchronized(_output), new StringWriter(), _stop.Token, started.SetResult);
        }

        public static async Task<RunningBot> StartAsync(string dataFolder, int port)
        {
            var started = new TaskCompletionSource<Kzg.Bot.BotApp>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bot = new RunningBot(dataFolder, port, started);
            await started.Task.WaitAsync(Patience);
            return bot;
        }

        /// <summary>A connect code printed for Alice, as a player would copy it from Discord.</summary>
        public string AliceCode()
        {
            var line = _output.ToString().Split('\n').First(text => text.Contains("Alice", StringComparison.Ordinal));
            return line[line.IndexOf("KZG1.", StringComparison.Ordinal)..].Trim();
        }

        public async Task<int> StopAsync()
        {
            await _stop.CancelAsync();
            return await _run.WaitAsync(Patience);
        }
    }

    /// <summary>The window's thread in miniature: everything the view model touches runs here, and it ticks like the real clock.</summary>
    private sealed class UiLoop : IUiThread, IDisposable
    {
        private readonly BlockingCollection<Action> _queue = [];
        private readonly Thread _thread;
        private Action? _tick;

        public UiLoop()
        {
            _thread = new Thread(Loop) { IsBackground = true, Name = "test ui" };
            _thread.Start();
        }

        public void Post(Action action) => _queue.Add(action);

        public void TickWith(Action tick) => _tick = tick;

        public T Run<T>(Func<T> read)
        {
            var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            Post(() =>
            {
                try
                {
                    done.SetResult(read());
                }
                catch (Exception exception)
                {
                    done.SetException(exception);
                }
            });
            return done.Task.GetAwaiter().GetResult();
        }

        public void Run(Action action) => Run(() =>
        {
            action();
            return 0;
        });

        public void Dispose() => _queue.CompleteAdding();

        private void Loop()
        {
            while (!_queue.IsAddingCompleted)
            {
                if (_queue.TryTake(out var action, 50))
                {
                    action();
                }
                _tick?.Invoke();
            }
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    private MainViewModel BuildApp(int port)
    {
        var options = new ConnectionOptions
        {
            ServerOverride = new Uri($"ws://127.0.0.1:{port}/ws"),
            Backoff = [TimeSpan.FromMilliseconds(100)],
            // Must outlast a slow runner stopping the bot, or the test never sees "Reconnecting".
            ReconnectingFor = TimeSpan.FromSeconds(5),
        };
        var time = new SystemTimeSource();
        var settings = AppSettings.CreateDefault();
        var store = new SettingsStore(Path.Combine(_folder, "app", "settings.json"));
        var connection = new ConnectionController(new WebSocketLinkTransportFactory(_ui.Post), time, settings.InstallId, "1.0.0", options);
        var vm = new MainViewModel(settings, store, connection, new FakeInputService(), new FakePermissions(), new FakePlatform(), _ui, time);
        _ui.TickWith(vm.Tick);
        _vm = vm;
        return vm;
    }

    private async Task Eventually(Func<bool> condition, string what)
    {
        var until = DateTime.UtcNow + Patience;
        while (DateTime.UtcNow < until)
        {
            if (_ui.Run(condition))
            {
                return;
            }
            await Task.Delay(25);
        }
        throw new TimeoutException($"Timed out waiting for: {what}. Badge now: {_ui.Run(() => _vm!.BadgeLabel)}.");
    }

    [Fact]
    public async Task The_app_follows_the_bot_through_a_stop_and_a_restart_on_its_device_token_without_linking_again()
    {
        var botFolder = Path.Combine(_folder, "bot");
        var port = FreePort();
        var bot = await RunningBot.StartAsync(botFolder, port);
        var vm = BuildApp(port);

        _ui.Run(() =>
        {
            vm.Start();
            vm.CodeText = bot.AliceCode();
            vm.LinkWithCodeCommand.Execute(null);
        });
        await Eventually(() => vm.Badge == BadgeState.Connected, "connected after linking with a code");
        Assert.True(_ui.Run(() => vm.IsLinked));

        // The bot goes away: first "Reconnecting", then, once the grace period passes, "Can't reach the bot".
        await bot.StopAsync();
        await Eventually(() => vm.Badge == BadgeState.Reconnecting, "reconnecting after the bot stopped");
        await Eventually(() => vm.Badge == BadgeState.Disconnected && vm.BadgeLabel == "Can't reach the bot", "can't reach the bot");

        // The bot comes back with the same devices.json: the app gets in with its device token, on its own.
        var again = await RunningBot.StartAsync(botFolder, port);
        await Eventually(() => vm.Badge == BadgeState.Connected, "connected again after the bot restarted");

        Assert.False(_ui.Run(() => vm.ShowLinkScreen));
        Assert.Equal("", _ui.Run(() => vm.LinkMessage));
        Assert.Equal(0, await again.StopAsync());
    }
}
