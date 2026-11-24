using Avalonia;
using Avalonia.Headless;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Kzg.App;
using Kzg.App.Screens;

// Draws the real main window in every situation, light and dark, into one folder of PNGs.
// Usage: dotnet run --project tests/Kzg.App.Screens -c Release [-- <output folder>]
var output = args.Length > 0 ? Path.GetFullPath(args[0]) : DefaultFolder();
Directory.CreateDirectory(output);
foreach (var old in Directory.GetFiles(output, "*.png"))
{
    File.Delete(old);
}

using var session = HeadlessUnitTestSession.StartNew(typeof(ScreensApp));
var written = await session.Dispatch(
    () =>
    {
        var files = new List<string>();
        using var builder = new SceneBuilder();
        foreach (var scene in builder.All())
        {
            foreach (var (theme, suffix) in new[] { (ThemeVariant.Light, "light"), (ThemeVariant.Dark, "dark") })
            {
                Console.WriteLine($"{scene.Name} {suffix}");
                files.Add(Render(scene, theme, suffix, output));
            }
        }
        return files;
    },
    CancellationToken.None);

foreach (var file in written)
{
    Console.WriteLine(file);
}
Console.WriteLine($"{written.Count} pictures in {output}");

// Disposing the headless session blocks on its dispatcher thread, so the process would never end by itself.
Environment.Exit(0);
return 0;

static string Render(Scene scene, ThemeVariant theme, string suffix, string output)
{
    // The window's own fixed size: anything longer than it scrolls, as it does for a player.
    var window = new MainWindow { DataContext = scene.Model, RequestedThemeVariant = theme };
    window.Show();
    Dispatcher.UIThread.RunJobs();
    AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException($"{scene.Name} was not drawn");
    var path = Path.Combine(output, $"{scene.Name}-{suffix}.png");
    frame.Save(path, PngBitmapEncoderOptions.Default);
    window.Close();
    return path;
}

static string DefaultFolder()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
    {
        if (File.Exists(Path.Combine(dir.FullName, "Kzg.slnx")))
        {
            return Path.Combine(dir.FullName, ".data", "screens");
        }
    }
    return Path.Combine(Environment.CurrentDirectory, "screens");
}

namespace Kzg.App.Screens
{
    /// <summary>The headless app: the real <see cref="App"/>, drawn by Skia so the pictures have real pixels and fonts.</summary>
    public static class ScreensApp
    {
        public static AppBuilder BuildAvaloniaApp() =>
            AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
    }
}
