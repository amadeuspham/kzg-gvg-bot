namespace Kzg.Bot.Tests.Hosting;

/// <summary>
/// A test that runs the engine on its own thread against the wall clock and asserts timing ranges, so a busy
/// machine can fail it. It is skipped unless <c>KZG_REALTIME=1</c>; the gate runs the fake-clock tests only.
/// </summary>
public sealed class RealTimeFactAttribute : FactAttribute
{
    public const string Variable = "KZG_REALTIME";

    public RealTimeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"runs against the wall clock; set {Variable}=1 to run it";
        }
    }
}
