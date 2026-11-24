using Kzg.App.Services;

namespace Kzg.App.Tests;

public sealed class SingleInstanceTests : IDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "kzg-single-" + Guid.NewGuid().ToString("N"));
    private readonly List<SingleInstance> _claims = [];

    public void Dispose()
    {
        foreach (var claim in _claims)
        {
            claim.Dispose();
        }
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private SingleInstance? Claim(string? folder = null)
    {
        var claim = SingleInstance.Claim(folder ?? _folder);
        if (claim is not null)
        {
            _claims.Add(claim);
        }
        return claim;
    }

    [Fact]
    public void The_first_copy_gets_the_folder_and_a_second_one_is_turned_away()
    {
        var first = Claim();

        Assert.NotNull(first);
        Assert.True(first.Guarding);
        Assert.Null(Claim());
    }

    [Fact]
    public void A_copy_for_another_folder_is_a_different_player_and_runs_beside_the_first()
    {
        Assert.NotNull(Claim());

        Assert.NotNull(Claim(_folder + "-other"));
    }

    [Fact]
    public void The_folder_is_free_again_once_the_first_copy_has_gone()
    {
        Claim()!.Dispose();

        Assert.NotNull(Claim());
    }

    [Fact]
    public async Task A_second_copy_asks_the_first_to_show_its_window()
    {
        var first = Claim()!;
        var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ShowRequested += () => asked.TrySetResult();

        Assert.Null(Claim());
        Assert.True(SingleInstance.RequestShow(_folder, Patience));

        await asked.Task.WaitAsync(Patience);
    }

    [Fact]
    public async Task Every_later_copy_is_heard_not_only_the_first()
    {
        var first = Claim()!;
        var asks = 0;
        var twice = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        first.ShowRequested += () =>
        {
            if (Interlocked.Increment(ref asks) == 2)
            {
                twice.TrySetResult();
            }
        };

        Assert.True(SingleInstance.RequestShow(_folder, Patience));
        Assert.True(SingleInstance.RequestShow(_folder, Patience));

        await twice.Task.WaitAsync(Patience);
    }

    [Fact]
    public void Asking_when_no_copy_runs_gives_up_instead_of_waiting_forever()
    {
        Assert.False(SingleInstance.RequestShow(_folder, TimeSpan.FromMilliseconds(300)));
    }

    [Fact]
    public void A_folder_that_cannot_be_locked_turns_the_guard_off_but_still_lets_the_app_start()
    {
        var file = Path.Combine(Path.GetTempPath(), "kzg-single-file-" + Guid.NewGuid().ToString("N"));
        File.WriteAllText(file, "not a folder");
        try
        {
            var claim = SingleInstance.Claim(Path.Combine(file, "inside"));

            Assert.NotNull(claim);
            Assert.False(claim.Guarding);
            claim.Dispose();
        }
        finally
        {
            File.Delete(file);
        }
    }
}
