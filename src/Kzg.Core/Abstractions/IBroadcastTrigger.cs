namespace Kzg.Core.Abstractions;

/// <summary>
/// A request to start or stop broadcasting. It is a claim, not a right: routing checks the user's VC
/// itself on every edge.
/// </summary>
public readonly record struct BroadcastEdge(UserId User, BroadcastAction Action, bool On, TriggerKind Kind, TimeSpan At);

/// <summary>Something that turns presses and latches into edges: the control server.</summary>
public interface IBroadcastTrigger
{
    event Action<BroadcastEdge> Edge;
}
