namespace Kzg.Core.Abstractions;

/// <summary>A big team: three small-team VCs that hear each other's broadcasts.</summary>
public enum Group
{
    Attack,
    Defense,
}

/// <summary>What a broadcast session does. Sessions are keyed by (source, action).</summary>
public enum BroadcastAction
{
    /// <summary>Held: the app's team key. Reaches the source's own group.</summary>
    Group,

    /// <summary>Held: the app's all-teams key. Reaches the bound VCs of both groups.</summary>
    AllTeams,

    /// <summary>Latched by the player; reaches the same VCs as <see cref="Group"/> without holding anything.</summary>
    OpenMic,
}

/// <summary>What started or ended a broadcast edge.</summary>
public enum TriggerKind
{
    AppKey,

    /// <summary>Not a person: a synthetic source, or the engine ending a session itself.</summary>
    Synthetic,
}
