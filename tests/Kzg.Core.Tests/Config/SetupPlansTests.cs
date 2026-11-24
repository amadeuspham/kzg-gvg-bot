using Kzg.Core.Abstractions;
using Kzg.Core.Config;

namespace Kzg.Core.Tests.Config;

public sealed class SetupPlansTests
{
    private static readonly ChannelId A1 = new(101), A2 = new(102), A3 = new(103), D1 = new(201);
    private static readonly UserId Bot1 = new(9001), Bot2 = new(9002), Bot3 = new(9003);

    [Fact]
    public void A_first_setup_makes_the_bots_with_the_first_primary_in_the_server()
    {
        var config = SetupPlans.WithBots(null, [new SetupBot("relay1", Bot1), new SetupBot("relay2", null)], 5000, "gvg.example.com");

        Assert.Equal(5000UL, config.GuildId);
        Assert.Equal("gvg.example.com", config.PublicHost);
        Assert.Equal(["relay1", "relay2"], config.Bots.Select(b => b.TokenRef));
        Assert.Equal([true, false], config.Bots.Select(b => b.Primary));
        Assert.Equal(Bot1, config.Bots[0].BotUserId);
        Assert.All(config.Bots, b => Assert.Null(b.ChannelId));
        Assert.False(config.OAuth.Enabled);
        Assert.Equal("relay3", SetupPlans.TokenRefFor(3));
    }

    [Fact]
    public void Setup_again_in_the_same_server_keeps_the_settings_and_each_known_bots_channel()
    {
        var before = new BotConfig
        {
            GuildId = 5000,
            PublicHost = "old.example.com",
            AdminRoleId = 77,
            AlertsChannelId = new ChannelId(900),
            Groups = new GroupsConfig { Attack = new GroupConfig { Channels = [A1, A2] } },
            Bots = [new BotEntry { TokenRef = "bot1", BotUserId = Bot1, ChannelId = A1, Primary = true }, new BotEntry { TokenRef = "bot2", BotUserId = Bot2, ChannelId = A2 }],
        };

        var after = SetupPlans.WithBots(before, [new SetupBot("relay1", Bot2), new SetupBot("relay2", Bot3)], 5000, null);

        Assert.Equal("old.example.com", after.PublicHost);
        Assert.Equal(77UL, after.AdminRoleId);
        Assert.Equal(before.Groups, after.Groups);
        Assert.Equal(new ChannelId(900), after.AlertsChannelId);
        Assert.Equal([A2, (ChannelId?)null], after.Bots.Select(b => b.ChannelId));
    }

    [Fact]
    public void Setup_in_another_server_drops_the_old_servers_channels()
    {
        var before = new BotConfig
        {
            GuildId = 5000,
            AlertsChannelId = new ChannelId(900),
            Groups = new GroupsConfig { Attack = new GroupConfig { Channels = [A1] } },
            Bots = [new BotEntry { TokenRef = "relay1", BotUserId = Bot1, ChannelId = A1, Primary = true }],
        };

        var after = SetupPlans.WithBots(before, [new SetupBot("relay1", Bot1)], 6000, null);

        Assert.Equal(6000UL, after.GuildId);
        Assert.Empty(after.Groups.Attack.Channels);
        Assert.Null(after.AlertsChannelId);
        Assert.Null(after.Bots[0].ChannelId);
    }

    private static GroupsConfig Teams(ChannelId[] attack, params ChannelId[] defense) =>
        new() { Attack = new GroupConfig { Channels = attack }, Defense = new GroupConfig { Channels = defense } };

    private static BotEntry Bot(int slot, ChannelId? channel = null) =>
        new() { TokenRef = SetupPlans.TokenRefFor(slot), ChannelId = channel, Primary = slot == 1 };

    [Fact]
    public void Each_channel_takes_the_bot_whose_setup_field_named_it()
    {
        // Pasted in the A1, A3, D1 and D2 fields, so relay2 and relay5 are missing.
        IReadOnlyList<BotEntry> bots = [Bot(1), Bot(3), Bot(4), Bot(6)];

        var assigned = SetupPlans.AssignChannels(bots, Teams([A1, A2, A3], D1))!;

        // A1, A3 and D1 find their own bots; A2's is missing, so the bot whose channel wasn't picked fills it.
        Assert.Equal([A1, A3, D1, A2], assigned.Select(b => b.ChannelId!.Value));
        Assert.True(assigned[0].Primary);
    }

    [Fact]
    public void A_bots_own_channel_wins_over_where_it_sat_and_a_channel_without_its_bot_keeps_the_one_there()
    {
        ChannelId d2 = new(202);
        IReadOnlyList<BotEntry> bots = [Bot(1, A2), Bot(2, A1), Bot(4), Bot(5, d2)];

        var assigned = SetupPlans.AssignChannels(bots, Teams([A1, A2, A3], D1))!;

        // relay1 and relay2 go back to the channels their fields named; relay5 left D2, which isn't picked, for A3, whose bot is missing.
        Assert.Equal([A1, A2, (ChannelId?)D1, A3], assigned.Select(b => b.ChannelId));
        var kept = SetupPlans.AssignChannels([Bot(1), new BotEntry { TokenRef = "bot2", ChannelId = A3 }, Bot(3)], Teams([A1, A2], A3))!;
        // D1's own bot (relay4) is missing: bot2 was there already and stays, and relay3 fills A2.
        Assert.Equal([A1, A3, A2], kept.Select(b => b.ChannelId!.Value));
    }

    [Fact]
    public void Too_many_channels_are_refused_and_none_take_every_bot_out()
    {
        IReadOnlyList<BotEntry> bots = [Bot(1, A1), Bot(2, D1), Bot(3)];

        Assert.Null(SetupPlans.AssignChannels(bots, Teams([A1, A2, A3], D1)));
        Assert.All(SetupPlans.AssignChannels(bots, Teams([]))!, b => Assert.Null(b.ChannelId));
    }

    [Fact]
    public void A_slot_is_known_only_for_the_setup_pages_own_names()
    {
        Assert.Equal(4, SetupPlans.SlotOf("relay4"));
        Assert.Null(SetupPlans.SlotOf("relay7"));
        Assert.Null(SetupPlans.SlotOf("bot1"));
    }
}
