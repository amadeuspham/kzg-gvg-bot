using Kzg.Bot.Logging;

namespace Kzg.Bot.Tests.Logging;

public sealed class RotatingLogWriterTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-log-tests", Guid.NewGuid().ToString("N"));

    public RotatingLogWriterTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Log => Path.Combine(_folder, "bot.log");

    [Fact]
    public void Past_its_size_the_log_moves_up_and_never_holds_more_than_its_files()
    {
        using (var log = new RotatingLogWriter(Log, maxBytes: 1000, keep: 2))
        {
            for (var i = 0; i < 500; i++)
            {
                log.WriteLine($"line {i:D4} " + new string('x', 40));
            }
        }

        var files = Directory.GetFiles(_folder).Select(f => Path.GetFileName(f)).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["bot.1.log", "bot.2.log", "bot.log"], files);
        // Each file is cut at the end of the line that passed the size, so it holds whole lines only.
        // The live file may be empty, just started; the older ones are full, cut at a line's end.
        Assert.InRange(new FileInfo(Log).Length, 0, 1000 + 60);
        foreach (var older in new[] { RotatingLogWriter.OlderFile(Log, 1), RotatingLogWriter.OlderFile(Log, 2) })
        {
            Assert.InRange(new FileInfo(older).Length, 1000, 1000 + 60);
            Assert.EndsWith("\n", File.ReadAllText(older));
        }
        var all = string.Concat(Directory.GetFiles(_folder).Select(File.ReadAllText));
        Assert.Contains("line 0499", all);
        Assert.DoesNotContain("line 0000", all);
        Assert.Equal(Path.Combine(_folder, "bot.2.log"), RotatingLogWriter.OlderFile(Log, 2));
    }

    [Fact]
    public void A_restart_appends_and_every_write_shape_ends_up_in_the_file()
    {
        using (var log = new RotatingLogWriter(Log))
        {
            log.WriteLine("first run");
        }
        using (var log = new RotatingLogWriter(Log))
        {
            log.Write("second ");
            log.Write('r');
            log.Write("un\n");
            log.WriteLine("{0} {1}", "formatted", 3);
            log.WriteLine();
        }

        Assert.Equal("first run\nsecond run\nformatted 3\n\n", File.ReadAllText(Log));
        Assert.Equal(RotatingLogWriter.DefaultMaxBytes * (RotatingLogWriter.DefaultKeep + 1), 30L * 1024 * 1024);
    }

    [Fact]
    public void A_reader_holding_the_file_open_does_not_stop_the_writer_or_its_rotation()
    {
        using var log = new RotatingLogWriter(Log, maxBytes: 100, keep: 1);
        log.WriteLine("before");
        using (var reader = new FileStream(Log, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            for (var i = 0; i < 20; i++)
            {
                log.WriteLine(new string('y', 30));
            }
        }

        Assert.True(new FileInfo(Log).Length <= 100 + 32);
        Assert.False(File.Exists(Path.Combine(_folder, "bot.2.log")));
    }

    [Fact]
    public void The_command_line_names_the_log_file_and_the_bot_falls_back_to_the_console_when_it_cannot_open_it()
    {
        Assert.Equal("x.log", LaunchOptions.Parse(["--log-file", "x.log"]).LogFile);
        Assert.Null(Program.OpenLogFile(["--fake"]));
        Assert.Null(Program.OpenLogFile(["--nope"]));

        using (var opened = Program.OpenLogFile(["--log-file", Log]))
        {
            Assert.NotNull(opened);
        }
        var blocked = Path.Combine(_folder, "dir-not-file");
        Directory.CreateDirectory(blocked);
        var console = new StringWriter();
        Assert.Null(Program.OpenLogFile(["--log-file", blocked], console));
        Assert.Contains("logging to the console", console.ToString());
    }

    [Fact]
    public async Task A_fake_run_with_a_log_file_option_still_runs()
    {
        // RunAsync takes --log-file and leaves the writing to Main, which passes the file as the output.
        var output = new StringWriter();
        var exit = await Program.RunAsync(["--help", "--log-file", Log], output, new StringWriter(), CancellationToken.None);

        Assert.Equal(0, exit);
        Assert.Contains("--log-file", output.ToString());
    }
}
