namespace Kzg.App.Input;

public enum BindingEvent
{
    /// <summary>The team broadcast input went down.</summary>
    BroadcastDown,

    /// <summary>The team broadcast input came up, or the layer lost it.</summary>
    BroadcastUp,

    /// <summary>The all-teams broadcast input went down.</summary>
    AllTeamsDown,

    /// <summary>The all-teams broadcast input came up, or the layer lost it.</summary>
    AllTeamsUp,

    /// <summary>The open-mic input was pressed. The latch itself lives with the connection.</summary>
    OpenMicToggle,
}

/// <summary>
/// Turns raw inputs into the app's three actions. Auto-repeat is collapsed to one down, and an input that
/// was already down when it was bound never starts a broadcast. An action with no input never fires.
/// Not thread-safe: feed it from one thread.
/// </summary>
public sealed class BindingMatcher
{
    // Every input seen down and not yet up, bound or not, so repeats are told from first presses.
    private readonly HashSet<InputBinding> _physical = [];

    // The input whose down started each hold, for the two actions that are held.
    private readonly Dictionary<BindingAction, InputBinding> _holders = [];
    private BindingSet _bindings;
    private bool _muted;

    public BindingMatcher(BindingSet bindings)
    {
        _bindings = Validated(bindings);
    }

    public event Action<BindingEvent>? Event;

    /// <summary>True from the team broadcast input going down to it coming up.</summary>
    public bool BroadcastIsDown => _holders.ContainsKey(BindingAction.Broadcast);

    /// <summary>True from the all-teams broadcast input going down to it coming up.</summary>
    public bool AllTeamsIsDown => _holders.ContainsKey(BindingAction.AllTeams);

    /// <summary>
    /// While true nothing is emitted and no press counts, but physical state is still tracked, so a key
    /// held while the user picks a new binding doesn't start a broadcast when it repeats afterwards.
    /// </summary>
    public bool Muted
    {
        get => _muted;
        set
        {
            if (_muted == value)
            {
                return;
            }
            _muted = value;
            if (value)
            {
                EndHolds(_ => true);
            }
        }
    }

    /// <summary>Swaps the bindings. A hold on an input that no longer holds that action ends; nothing new starts.</summary>
    public void SetBindings(BindingSet bindings)
    {
        _bindings = Validated(bindings);
        EndHolds(hold => _bindings[hold.Key] != hold.Value);
    }

    public void Handle(RawInput raw)
    {
        var input = raw.Input;
        if (raw.Down)
        {
            if (!_physical.Add(input) || _muted)
            {
                return;
            }
            switch (_bindings.ActionOf(input))
            {
                case BindingAction.Broadcast:
                    _holders[BindingAction.Broadcast] = input;
                    Event?.Invoke(BindingEvent.BroadcastDown);
                    break;
                case BindingAction.AllTeams:
                    _holders[BindingAction.AllTeams] = input;
                    Event?.Invoke(BindingEvent.AllTeamsDown);
                    break;
                case BindingAction.OpenMic:
                    Event?.Invoke(BindingEvent.OpenMicToggle);
                    break;
            }
            return;
        }
        _physical.Remove(input);
        EndHolds(hold => hold.Value == input);
    }

    /// <summary>The capture layer stopped or restarted: forget everything held, ending any broadcast.</summary>
    public void ReleaseAll()
    {
        _physical.Clear();
        EndHolds(_ => true);
    }

    /// <summary>One source went away, such as the controller process: forget only its inputs.</summary>
    public void Release(InputKind kind)
    {
        _physical.RemoveWhere(i => i.Kind == kind);
        EndHolds(hold => hold.Value.Kind == kind);
    }

    private void EndHolds(Func<KeyValuePair<BindingAction, InputBinding>, bool> ends)
    {
        foreach (var hold in _holders.Where(ends).ToList())
        {
            _holders.Remove(hold.Key);
            Event?.Invoke(hold.Key == BindingAction.Broadcast ? BindingEvent.BroadcastUp : BindingEvent.AllTeamsUp);
        }
    }

    private static BindingSet Validated(BindingSet bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return bindings.Problem() is { } problem ? throw new ArgumentException(problem, nameof(bindings)) : bindings;
    }
}
