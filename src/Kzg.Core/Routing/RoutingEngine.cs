using Kzg.Core.Abstractions;
using Kzg.Protocol;

namespace Kzg.Core.Routing;

/// <summary>
/// Decides who is heard where, one engine tick at a time. It runs on the engine thread alone, so it
/// holds no locks: the host drains its queue into the <c>On…</c> methods, then calls <see cref="Tick"/>.
/// Every edge is a claim; VCs and targets are worked out afresh on every tick, so a move, a rebind or
/// a lost app takes effect on the next tick.
/// <para>
/// The relay plays behind live, so when the player lets go it drains: it goes on until the buffer has
/// played what arrived up to the release, then ends. Only the player's own release drains; anything
/// the bot ends, or a release it can only guess the time of, stops at once.
/// </para>
/// </summary>
public sealed class RoutingEngine(RoutingConfig config)
{
    /// <summary>The longest a drain lasts if the host never says it is done: the whole buffer.</summary>
    public static readonly TimeSpan MaxDrain = TimeSpan.FromMilliseconds(1200);

    private readonly SortedDictionary<ulong, UserState> _users = [];
    private readonly HashSet<ChannelId> _readyLinks = [];

    public RoutingConfig Config { get; private set; } = config;

    /// <summary>A new config, such as after <c>/gvg bind</c>; it applies from the next tick.</summary>
    public void Configure(RoutingConfig config) => Config = config;

    /// <summary>A user joined, moved between or left VCs; null when they left voice.</summary>
    public void OnPresence(UserId user, ChannelId? channel) => State(user).Channel = channel;

    /// <summary>A relay bot's link came up or went down; a VC without a ready bot is not sent to.</summary>
    public void OnLinkReady(ChannelId channel, bool ready)
    {
        if (ready)
        {
            _readyLinks.Add(channel);
        }
        else
        {
            _readyLinks.Remove(channel);
        }
    }

    /// <summary>The user's app signed in. One app per user: the control server closes an older one first.</summary>
    public void OnAppConnected(UserId user, TimeSpan at)
    {
        var state = State(user);
        state.ForceEndApp();
        (state.AppConnected, state.LastHeartbeat) = (true, at);
    }

    /// <summary>The app went: its key and open mic end at once.</summary>
    public void OnAppDisconnected(UserId user)
    {
        if (_users.TryGetValue(user.V, out var state))
        {
            state.ForceEndApp();
        }
    }

    /// <summary>
    /// The app's heartbeat, with what it holds. It heals a lost edge either way, except that it never
    /// restarts what the bot itself ended: that needs the app to let go first.
    /// </summary>
    public void OnHeartbeat(UserId user, IReadOnlyCollection<BroadcastAction> held, bool openMic, TimeSpan at)
    {
        if (!_users.TryGetValue(user.V, out var state) || !state.AppConnected)
        {
            return;
        }
        state.LastHeartbeat = at;
        // A key-up only a heartbeat brings came at some unknown time since the last: stop, don't drain.
        state.Forced |= state.Key.On && !held.Contains(BroadcastAction.Group);
        state.Forced |= state.AllTeamsKey.On && !held.Contains(BroadcastAction.AllTeams);
        state.Forced |= state.OpenMic.On && !openMic;
        state.Key.Report(held.Contains(BroadcastAction.Group), at);
        state.AllTeamsKey.Report(held.Contains(BroadcastAction.AllTeams), at);
        state.OpenMic.Report(openMic, at);
    }

    /// <summary>A press or an open-mic toggle. App edges count only while the app is connected.</summary>
    public void OnEdge(BroadcastEdge edge)
    {
        var state = State(edge.User);
        if (edge.Kind != TriggerKind.AppKey || !state.AppConnected)
        {
            // Synthetic edges are for sources that aren't players.
            return;
        }
        var hold = edge.Action switch
        {
            BroadcastAction.Group => state.Key,
            BroadcastAction.AllTeams => state.AllTeamsKey,
            BroadcastAction.OpenMic => state.OpenMic,
            _ => null,
        };
        if (hold is null)
        {
            return;
        }
        if (!edge.On && hold.On)
        {
            state.Release(edge.At);
        }
        hold.Press(edge.On, edge.At);
    }

    /// <summary>The host's buffer has played everything that arrived up to the drain's end; the relay ends on the next tick.</summary>
    public void OnRelayDrained(UserId user)
    {
        if (_users.TryGetValue(user.V, out var state) && state.Drain is not null)
        {
            state.Drained = true;
        }
    }

    public TickPlan Tick(TimeSpan now)
    {
        List<SourceRoute> routes = [];
        List<UserId> ended = [];
        List<UserStatus> statuses = [];
        List<ulong> gone = [];
        foreach (var (id, state) in _users)
        {
            var user = new UserId(id);
            Expire(state, now);
            var group = Config.GroupOf(state.Channel);
            var actions = ActiveActions(state);
            var allTeams = actions.Contains(BroadcastAction.AllTeams);
            var targets = actions.Count > 0 && group is { } g ? Targets(g, allTeams, state.Channel) : [];
            if (actions.Count > 0)
            {
                // A new session during a drain carries on the same relay, read head and all.
                routes.Add(new SourceRoute(user, state.Channel, targets, actions, state.Relaying ? null : ReadFrom(state, now)));
                state.Drain = null;
                state.ReachedAllTeams = allTeams;
            }
            else if (state.Relaying && Draining(state, group, now) is { } drain)
            {
                routes.Add(new SourceRoute(user, state.Channel, Targets(drain.Group, drain.AllTeams, state.Channel), [], null, drain.Until));
            }
            else if (state.Relaying)
            {
                ended.Add(user);
                state.Drain = null;
            }
            state.Relaying = actions.Count > 0 || state.Drain is not null;
            (state.Released, state.Forced, state.Drained) = (null, false, false);
            if (state.AppConnected)
            {
                var status = new UserStatus(user, state.Channel, group, group is null ? [] : actions,
                    state.OpenMic.Requested, targets, Warnings(group));
                if (state.LastStatus is null || !status.SameAs(state.LastStatus))
                {
                    statuses.Add(status);
                    state.LastStatus = status;
                }
            }
            if (state.IsIdle)
            {
                gone.Add(id);
            }
        }
        foreach (var id in gone)
        {
            _users.Remove(id);
        }
        return new TickPlan(now, routes, ended, statuses);
    }

    /// <summary>
    /// The bound VCs with a ready bot that a source's targets name now, each once. Unlike a player's
    /// targets no VC is left out for being someone's own: a source is not a player, so a call reaches the
    /// VC a speaker sits in too.
    /// </summary>
    public IReadOnlyList<ChannelId> Resolve(TargetSpec spec)
    {
        List<ChannelId> resolved = [];
        switch (spec)
        {
            case TargetSpec.Groups groups:
                foreach (var group in groups.Names)
                {
                    foreach (var channel in Config.ChannelsOf(group))
                    {
                        AddResolved(resolved, channel);
                    }
                }
                break;
            case TargetSpec.Channels channels:
                foreach (var channel in channels.Ids)
                {
                    AddResolved(resolved, channel);
                }
                break;
            case TargetSpec.Users users:
                foreach (var user in users.Ids)
                {
                    if (_users.TryGetValue(user.V, out var state) && state.Channel is { } channel)
                    {
                        AddResolved(resolved, channel);
                    }
                }
                break;
        }
        return resolved;
    }

    private void AddResolved(List<ChannelId> resolved, ChannelId channel)
    {
        if (Config.GroupOf(channel) is not null && _readyLinks.Contains(channel) && !resolved.Contains(channel))
        {
            resolved.Add(channel);
        }
    }

    /// <summary>
    /// The drain the relay is in, starting one when the last session just ended by the player's own
    /// release; null when it ends now. It stops when the host says the buffer is played out, after
    /// <see cref="MaxDrain"/>, or if the player moves out of the group they spoke from. It reaches the
    /// VCs the relay reached last, so an all-teams hold drains to both groups.
    /// </summary>
    private static DrainState? Draining(UserState state, Group? group, TimeSpan now)
    {
        if (state.Drain is null && state.Released is { } until && !state.Forced && group is { } g)
        {
            state.Drain = new DrainState(until, now, g, state.ReachedAllTeams);
        }
        if (state.Drain is { } drain && (state.Drained || now - drain.Since >= MaxDrain || group != drain.Group))
        {
            state.Drain = null;
        }
        return state.Drain;
    }

    private void Expire(UserState state, TimeSpan now)
    {
        if (state.AppConnected && now - state.LastHeartbeat >= Config.HeartbeatTimeout)
        {
            state.ForceEndApp();
        }
        foreach (var key in new[] { state.Key, state.AllTeamsKey })
        {
            if (key.On && now - key.Since >= Config.MaxHold)
            {
                key.Suppress();
                state.Forced = true;
            }
        }
        if (state.OpenMic.On && Config.GroupOf(state.Channel) is null)
        {
            // Open mic needs a seat in a team VC: refused or ended, the app sees its latch go off and must toggle it again.
            state.OpenMic.Suppress();
            state.Forced = true;
        }
    }

    private static List<BroadcastAction> ActiveActions(UserState state)
    {
        List<BroadcastAction> actions = [];
        if (state.Key.On)
        {
            actions.Add(BroadcastAction.Group);
        }
        if (state.AllTeamsKey.On)
        {
            actions.Add(BroadcastAction.AllTeams);
        }
        if (state.OpenMic.On)
        {
            actions.Add(BroadcastAction.OpenMic);
        }
        return actions;
    }

    /// <summary>
    /// The bound VCs with a ready bot, never the VC the player sits in: the player's group, or with
    /// <paramref name="allTeams"/> Attack's then Defense's.
    /// </summary>
    private List<ChannelId> Targets(Group group, bool allTeams, ChannelId? own)
    {
        var channels = allTeams ? Config.ChannelsOf(Group.Attack).Concat(Config.ChannelsOf(Group.Defense)) : Config.ChannelsOf(group);
        return [.. channels.Where(channel => channel != own && _readyLinks.Contains(channel))];
    }

    /// <summary>The earliest start among the sessions that began the relay, never later than now.</summary>
    private TimeSpan ReadFrom(UserState state, TimeSpan now)
    {
        var from = now;
        foreach (var hold in new[] { state.Key, state.AllTeamsKey, state.OpenMic })
        {
            if (hold.On)
            {
                from = Min(from, hold.Since - Config.Preroll);
            }
        }
        return from;
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    private List<StatusWarning> Warnings(Group? group)
    {
        List<StatusWarning> warnings = [];
        if (group is null)
        {
            warnings.Add(StatusWarning.NotInTeamVc);
        }
        if (group is { } own && Config.ChannelsOf(own).Any(channel => !_readyLinks.Contains(channel)))
        {
            warnings.Add(StatusWarning.BotDown);
        }
        return warnings;
    }

    private UserState State(UserId user)
    {
        if (!_users.TryGetValue(user.V, out var state))
        {
            _users[user.V] = state = new UserState();
        }
        return state;
    }

    /// <summary>A held key or an open-mic latch, as the app reports it, and whether the bot has ended it.</summary>
    private sealed class Hold
    {
        public bool Requested { get; private set; }

        /// <summary>The bot ended it; reports of it held are ignored until the app lets go.</summary>
        public bool Suppressed { get; private set; }

        public TimeSpan Since { get; private set; }

        public bool On => Requested && !Suppressed;

        /// <summary>An explicit edge from the app: a fresh press lifts the bot's suppression.</summary>
        public void Press(bool down, TimeSpan at)
        {
            if (down && (!Requested || Suppressed))
            {
                (Requested, Suppressed, Since) = (true, false, at);
            }
            else if (!down)
            {
                (Requested, Suppressed) = (false, false);
            }
        }

        /// <summary>What a heartbeat says is held: heals a lost down or up, never what the bot ended.</summary>
        public void Report(bool held, TimeSpan at)
        {
            if (held && !Requested && !Suppressed)
            {
                (Requested, Since) = (true, at);
            }
            else if (!held)
            {
                (Requested, Suppressed) = (false, false);
            }
        }

        public void Suppress() => (Requested, Suppressed) = (false, true);

        public void Reset() => (Requested, Suppressed) = (false, false);
    }

    /// <param name="Until">The release: the relay plays what arrived up to it.</param>
    /// <param name="Group">The group spoken from; a drain ends if the player leaves it.</param>
    /// <param name="AllTeams">The relay reached both groups, so the drain does too.</param>
    private sealed record DrainState(TimeSpan Until, TimeSpan Since, Group Group, bool AllTeams);

    private sealed class UserState
    {
        public ChannelId? Channel { get; set; }

        public bool AppConnected { get; set; }

        public TimeSpan LastHeartbeat { get; set; }

        /// <summary>The team key.</summary>
        public Hold Key { get; } = new();

        public Hold AllTeamsKey { get; } = new();

        public Hold OpenMic { get; } = new();

        /// <summary>The relay reached both groups as of its last tick with a session, for the drain after it.</summary>
        public bool ReachedAllTeams { get; set; }

        /// <summary>Relaying as of the last tick, so a start and an end are each seen once.</summary>
        public bool Relaying { get; set; }

        public UserStatus? LastStatus { get; set; }

        /// <summary>The latest time the player let go of a session since the last tick.</summary>
        public TimeSpan? Released { get; set; }

        /// <summary>A session ended since the last tick in a way that must not drain.</summary>
        public bool Forced { get; set; }

        public DrainState? Drain { get; set; }

        /// <summary>The host said the drain is played out.</summary>
        public bool Drained { get; set; }

        public bool IsIdle => Channel is null && !AppConnected && !Relaying;

        public void Release(TimeSpan at) => Released = Released is { } r && r > at ? r : at;

        /// <summary>The app's sessions end at once.</summary>
        public void ForceEndApp()
        {
            Forced |= Key.On || AllTeamsKey.On || OpenMic.On;
            Key.Reset();
            AllTeamsKey.Reset();
            OpenMic.Reset();
            (AppConnected, LastStatus) = (false, null);
        }
    }
}
