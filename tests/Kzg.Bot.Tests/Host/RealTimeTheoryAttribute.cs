namespace Kzg.Bot.Tests.Hosting;

/// <summary>The <see cref="RealTimeFactAttribute"/> for a test with data rows.</summary>
public sealed class RealTimeTheoryAttribute : TheoryAttribute
{
    public RealTimeTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable(RealTimeFactAttribute.Variable) != "1")
        {
            Skip = $"runs against the wall clock; set {RealTimeFactAttribute.Variable}=1 to run it";
        }
    }
}
