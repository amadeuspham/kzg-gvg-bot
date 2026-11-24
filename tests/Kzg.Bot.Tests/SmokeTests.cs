namespace Kzg.Bot.Tests;

public class SmokeTests
{
    [Fact]
    public void Project_is_wired_and_reports_its_name()
    {
        Assert.Equal("Kzg.Bot", BotInfo.Describe());
    }
}
