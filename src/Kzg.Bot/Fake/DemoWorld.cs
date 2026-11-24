using Kzg.Bot.Engine;
using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Pairing;
using Kzg.Core.Testing;

namespace Kzg.Bot.Fake;

/// <summary>Channel names from a table, for the offline bot and for tests.</summary>
public sealed class TableChannelNames(IReadOnlyDictionary<ChannelId, string> names) : IChannelNames
{
    public string NameOf(ChannelId channel) => names.TryGetValue(channel, out var name) ? name : $"Channel {channel}";
}

/// <summary>Discord's membership as a table: who is in the guild and by what name. It signs no one in by OAuth.</summary>
public sealed class TableDiscordIdentity(IReadOnlyDictionary<UserId, string> members) : IDiscordIdentity
{
    public Task<UserId?> ExchangeCodeAsync(string code, Uri redirectUri, CancellationToken cancellationToken) =>
        Task.FromResult<UserId?>(null);

    public Task<GuildMember?> GetMemberAsync(UserId user, CancellationToken cancellationToken) =>
        Task.FromResult(members.TryGetValue(user, out var name) ? new GuildMember(user, name) : null);
}

/// <summary>
/// A made-up guild for running the bot with no Discord: six team VCs with a relay bot in each, the
/// and the players in them. The offline host and its tests build on it.
/// </summary>
public sealed class DemoWorld
{
    public static readonly ChannelId Attack1 = new(101), Attack2 = new(102), Attack3 = new(103);
    public static readonly ChannelId Defense1 = new(201), Defense2 = new(202), Defense3 = new(203);

    public static readonly UserId Alice = new(1001), Bram = new(1002), Chen = new(1003), Dana = new(1004);
    public static readonly UserId Eve = new(2001), Finn = new(2002);

    private DemoWorld(FakeVoiceGateway gateway, BotConfig config, TableChannelNames names, TableDiscordIdentity identity)
    {
        (Gateway, Config, Names, Identity) = (gateway, config, names, identity);
    }

    public FakeVoiceGateway Gateway { get; }

    public BotConfig Config { get; }

    public TableChannelNames Names { get; }

    public TableDiscordIdentity Identity { get; }

    /// <summary>Every guild member and their name; Dana is in no voice channel.</summary>
    public static IReadOnlyDictionary<UserId, string> Members { get; } = new Dictionary<UserId, string>
    {
        [Alice] = "Alice",
        [Bram] = "Bram",
        [Chen] = "Chen",
        [Dana] = "Dana",
        [Eve] = "Eve",
        [Finn] = "Finn",
    };

    public static DemoWorld Create(ITimeSource time, string publicHost)
    {
        var channelNames = new Dictionary<ChannelId, string>
        {
            [Attack1] = "Attack 1",
            [Attack2] = "Attack 2",
            [Attack3] = "Attack 3",
            [Defense1] = "Defense 1",
            [Defense2] = "Defense 2",
            [Defense3] = "Defense 3",
        };
        var gateway = new FakeVoiceGateway(time);
        foreach (var channel in channelNames.Keys)
        {
            gateway.AddLink(channel);
        }
        var config = new BotConfig
        {
            PublicHost = publicHost,
            Groups = new GroupsConfig
            {
                Attack = new GroupConfig { Channels = [Attack1, Attack2, Attack3] },
                Defense = new GroupConfig { Channels = [Defense1, Defense2, Defense3] },
            },
        };
        return new DemoWorld(gateway, config, new TableChannelNames(channelNames), new TableDiscordIdentity(Members));
    }

    /// <summary>Puts the players where they sit: one per VC, apart from Dana, who is in none.</summary>
    public void SeatPlayers()
    {
        Gateway.Join(Alice, Attack1);
        Gateway.Join(Bram, Attack2);
        Gateway.Join(Chen, Attack3);
        Gateway.Join(Eve, Defense1);
        Gateway.Join(Finn, Defense2);
    }
}
