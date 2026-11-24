using Kzg.App.Services;
using Velopack.Sources;

namespace Kzg.App.Tests;

public class UpdateServiceTests
{
    private sealed class FakeEngine : IUpdateEngine
    {
        public string? Newer { get; set; }

        public Exception? Fails { get; set; }

        public int Looks { get; private set; }

        public int Restarts { get; private set; }

        public int Exits { get; private set; }

        public Task<string?> DownloadNewerAsync()
        {
            Looks++;
            return Fails is null ? Task.FromResult(Newer) : Task.FromException<string?>(Fails);
        }

        public void ApplyAndRestart() => Restarts++;

        public void ApplyOnExit() => Exits++;
    }

    [Fact]
    public async Task Without_an_engine_updates_are_off_and_a_check_does_nothing()
    {
        var service = new UpdateService(null);

        await service.CheckAsync();

        Assert.Equal(UpdateStage.Off, service.Stage);
    }

    [Fact]
    public async Task A_newer_version_is_downloaded_and_waits_for_a_restart()
    {
        var engine = new FakeEngine { Newer = "0.2.0" };
        var service = new UpdateService(engine);
        var changes = new List<UpdateStage>();
        service.Changed += () => changes.Add(service.Stage);

        await service.CheckAsync();

        Assert.Equal(UpdateStage.Ready, service.Stage);
        Assert.Equal("0.2.0", service.ReadyVersion);
        Assert.Equal([UpdateStage.Checking, UpdateStage.Ready], changes);
    }

    [Fact]
    public async Task No_newer_version_leaves_the_app_idle()
    {
        var service = new UpdateService(new FakeEngine());

        await service.CheckAsync();

        Assert.Equal(UpdateStage.Idle, service.Stage);
        Assert.Null(service.ReadyVersion);
    }

    [Fact]
    public async Task A_feed_that_fails_is_reported_as_failed_and_tried_again_on_the_next_check()
    {
        var engine = new FakeEngine { Fails = new HttpRequestException("offline") };
        var service = new UpdateService(engine);

        await service.CheckAsync();
        Assert.Equal(UpdateStage.Failed, service.Stage);

        engine.Fails = null;
        engine.Newer = "0.3.0";
        await service.CheckAsync();
        Assert.Equal(UpdateStage.Ready, service.Stage);
        Assert.Equal(2, engine.Looks);
    }

    [Fact]
    public async Task A_waiting_update_is_not_looked_for_again()
    {
        var engine = new FakeEngine { Newer = "0.2.0" };
        var service = new UpdateService(engine);
        await service.CheckAsync();

        await service.CheckAsync();

        Assert.Equal(1, engine.Looks);
    }

    [Fact]
    public async Task Only_a_waiting_update_can_be_installed_by_restart_or_on_quit()
    {
        var engine = new FakeEngine { Newer = "0.2.0" };
        var service = new UpdateService(engine);

        service.RestartToUpdate();
        service.ApplyWhenQuitting();
        Assert.Equal(0, engine.Restarts + engine.Exits);

        await service.CheckAsync();
        service.RestartToUpdate();
        service.ApplyWhenQuitting();
        Assert.Equal(1, engine.Restarts);
        Assert.Equal(1, engine.Exits);
    }

    [Theory]
    [InlineData("https://github.com/owner/repo", "https://github.com/owner/repo")]
    [InlineData("  https://updates.example.com/kzg  ", "https://updates.example.com/kzg")]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("http://updates.example.com/kzg", null)]
    [InlineData("/Users/me/feed", null)]
    [InlineData("file:///tmp/feed", null)]
    [InlineData("not an address", null)]
    public void Only_an_https_address_counts_as_an_update_feed(string? text, string? expected)
    {
        Assert.Equal(expected, UpdateFeed.Normalize(text));
    }

    [Fact]
    public void A_github_repository_reads_its_releases_and_any_other_address_is_a_folder_of_files()
    {
        Assert.IsType<GithubSource>(UpdateFeed.CreateSource("https://github.com/owner/repo"));
        Assert.IsType<SimpleWebSource>(UpdateFeed.CreateSource("https://updates.example.com/kzg"));
    }

    [Fact]
    public void A_build_with_no_feed_keeps_updates_off()
    {
        Assert.Equal("", AppInfo.UpdateFeed);
        Assert.Equal(UpdateStage.Off, UpdateFeed.ForThisProcess().Stage);
    }
}
