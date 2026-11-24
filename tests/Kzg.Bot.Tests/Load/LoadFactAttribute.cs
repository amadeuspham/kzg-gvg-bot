namespace Kzg.Bot.Tests.Load;

/// <summary>
/// A test that runs the engine under load for minutes. It is skipped unless <c>KZG_LOAD=1</c>, so the gate
/// stays quick. <c>KZG_LOAD_SECONDS</c> sets the length (default 300).
/// </summary>
public sealed class LoadFactAttribute : FactAttribute
{
    public const string Variable = "KZG_LOAD";

    public LoadFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"runs the engine under load for minutes; set {Variable}=1 to run it";
        }
    }
}
