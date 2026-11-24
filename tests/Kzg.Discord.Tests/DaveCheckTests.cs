using Kzg.Discord.Dave;

namespace Kzg.Discord.Tests;

public sealed class DaveCheckTests
{
    private sealed class Probe(bool loads, params string[] exports) : IDaveProbe
    {
        public bool TryLoad() => loads;

        public bool Exports(string symbol) => exports.Contains(symbol);
    }

    [Fact]
    public void A_library_that_does_not_load_is_refused_with_a_reason()
    {
        var result = DaveCheck.Run(new Probe(loads: false));

        Assert.Equal(DaveStatus.NotFound, result.Status);
        Assert.Contains(DaveCheck.PinnedVersion, result.Message);
    }

    [Fact]
    public void A_library_that_exports_the_1_3_0_function_is_refused()
    {
        var result = DaveCheck.Run(new Probe(loads: true, DaveCheck.NewerOnlyExport, "daveSessionProcessWelcome"));

        Assert.Equal(DaveStatus.TooNew, result.Status);
        Assert.Contains("1.3.0", result.Message);
    }

    [Fact]
    public void A_library_without_it_passes()
    {
        var result = DaveCheck.Run(new Probe(loads: true, "daveSessionProcessWelcome"));

        Assert.True(result.Ok);
    }

    [Fact]
    public void The_library_this_build_ships_loads_and_is_not_1_3_0()
    {
        var result = DaveCheck.Run();

        // The natives ship for the platforms the bot runs on; an old Linux can fail to load one, but never as 1.3.0.
        Assert.NotEqual(DaveStatus.TooNew, result.Status);
        if (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows())
        {
            Assert.True(result.Ok, result.Message);
        }
    }
}
