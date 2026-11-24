using System.Diagnostics;

namespace Kzg.Bot.Tests.Setup;

public sealed class KeepAwakeTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-marker-tests", Guid.NewGuid().ToString("N"));

    public KeepAwakeTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    [Fact]
    public void On_a_Mac_caffeinate_is_asked_to_hold_off_sleep_until_this_process_ends()
    {
        ProcessStartInfo? seen = null;
        var log = new StringWriter();

        using var awake = KeepAwake.Start(log, info =>
        {
            seen = info;
            return null;
        }, isMac: true);

        Assert.Equal(KeepAwake.Caffeinate, seen!.FileName);
        Assert.Equal(["-i", "-s", "-w", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture)], seen.ArgumentList);
        Assert.False(awake.Holding);
        Assert.Contains("couldn't be kept awake", log.ToString());
    }

    [Fact]
    public void Elsewhere_nothing_is_started_and_a_failing_start_is_only_logged()
    {
        var started = false;
        using var other = KeepAwake.Start(new StringWriter(), _ =>
        {
            started = true;
            return null;
        }, isMac: false);
        var log = new StringWriter();
        using var failing = KeepAwake.Start(log, _ => throw new System.ComponentModel.Win32Exception(2), isMac: true);

        Assert.False(started);
        Assert.False(other.Holding);
        Assert.Contains("Win32Exception", log.ToString());
    }

    [Fact]
    public void On_this_Mac_the_hold_is_real_and_ends_with_dispose()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var awake = KeepAwake.Start(new StringWriter());
        Assert.True(awake.Holding);

        awake.Dispose();

        Assert.False(awake.Holding);
    }

    [Fact]
    public void The_run_marker_tells_an_unclean_stop_from_a_clean_one()
    {
        var marker = new RunMarker(_folder);

        Assert.False(marker.Begin());
        Assert.True(new RunMarker(_folder).Begin());
        marker.End();
        Assert.False(marker.Begin());
        marker.End();
        marker.End();
        Assert.False(File.Exists(marker.FilePath));
    }
}
