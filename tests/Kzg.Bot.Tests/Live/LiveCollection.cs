namespace Kzg.Bot.Tests.Live;

/// <summary>
/// The live tests share one set of bot accounts and one test server: every test that joins voice starts the
/// real bot on the same six relay tokens and seats the same tester and listener bots in the same channels.
/// Discord gives a bot one voice connection per server, so two such tests at once take each other's bots out
/// of voice mid-broadcast. xUnit runs test classes in parallel by default; this collection runs them one at a time.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class LiveCollection
{
    public const string Name = "live";
}
