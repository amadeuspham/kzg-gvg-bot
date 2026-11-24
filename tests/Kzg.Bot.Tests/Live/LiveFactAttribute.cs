namespace Kzg.Bot.Tests.Live;

/// <summary>
/// A test that talks to the real Discord test server. It is skipped unless <c>KZG_LIVE=1</c>, so the gate
/// never needs Discord or its secrets.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string Variable = "KZG_LIVE";

    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(Variable) != "1")
        {
            Skip = $"talks to Discord; set {Variable}=1 to run it";
        }
    }
}
