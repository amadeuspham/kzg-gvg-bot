using System.Reflection;

namespace Kzg.Bot.Tests.Live;

/// <summary>Runs in the gate, without Discord: a live test class left out of the live collection would run alongside the others and fight them for the same bots.</summary>
public sealed class LiveCollectionTests
{
    [Fact]
    public void Every_live_test_class_is_in_the_live_collection()
    {
        var liveClasses = typeof(LiveCollection).Assembly.GetTypes()
            .Where(t => t.GetMethods().Any(m => m.GetCustomAttribute<LiveFactAttribute>() is not null))
            .ToArray();

        Assert.NotEmpty(liveClasses);
        Assert.All(liveClasses, t => Assert.Equal(
            LiveCollection.Name,
            t.GetCustomAttributesData().SingleOrDefault(a => a.AttributeType == typeof(CollectionAttribute))?.ConstructorArguments[0].Value));
    }
}
