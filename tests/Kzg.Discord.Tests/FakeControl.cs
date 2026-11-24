using Kzg.Core.Abstractions;
using Kzg.Core.Config;
using Kzg.Core.Jungle;
using Kzg.Discord.Commands;

namespace Kzg.Discord.Tests;

/// <summary>The bot around the commands, as a table: edits are applied to a config the test can read, with the real rules checked.</summary>
internal sealed class FakeControl : IGvgControl
{
    public static readonly ChannelId A1 = new(101), A2 = new(102), A3 = new(103), D1 = new(201), D2 = new(202);

    public FakeControl(BotConfig? config = null)
    {
        Config = config ?? new BotConfig
        {
            GuildId = Guild,
            PublicHost = "gvg.example.com",
            Bots =
            [
                new BotEntry { TokenRef = "relay1", Primary = true, ChannelId = A1 },
                new BotEntry { TokenRef = "relay2" },
                new BotEntry { TokenRef = "relay3" },
            ],
            Groups = new GroupsConfig { Attack = new GroupConfig { Channels = [A1] } },
        };
    }

    public const ulong Guild = 5000;

    public BotConfig Config { get; private set; }

    public bool Running { get; private set; } = true;

    public Secrets Secrets { get; } = Secrets.Combine(
        new Dictionary<string, string> { ["relay1"] = "t1", ["relay2"] = "t2", ["relay3"] = "t3" }, new Dictionary<string, string>());

    public int Edits { get; private set; }

    public List<UserId> Revoked { get; } = [];

    public List<(UserId User, string Host)> Issued { get; } = [];

    public string Code { get; set; } = "KZG1.fake.code";

    public int RevokeCount { get; set; } = 2;

    public GvgStatus StatusToShow { get; set; } = new(true, [], [], 0, 0, 0, 0);

    public Task<EditOutcome> EditAsync(Func<BotConfig, ConfigEdit> edit, CancellationToken cancellationToken)
    {
        switch (edit(Config))
        {
            case ConfigEdit.Refuse refuse:
                return Task.FromResult(new EditOutcome(false, refuse.Reason));
            case ConfigEdit.Change change:
                var errors = ConfigValidation.Validate(change.Config, Secrets);
                if (errors.Count > 0)
                {
                    return Task.FromResult(new EditOutcome(false, "That would break the config: " + string.Join("; ", errors)));
                }
                Config = change.Config;
                Edits++;
                return Task.FromResult(new EditOutcome(true, change.Message));
        }
        throw new InvalidOperationException();
    }

    public Task SetRunningAsync(bool running, CancellationToken cancellationToken)
    {
        Running = running;
        return Task.CompletedTask;
    }

    public GvgStatus Status() => StatusToShow;

    public string ChannelName(ChannelId channel) => channel.V switch
    {
        101 => "Attack 1",
        102 => "Attack 2",
        103 => "Attack 3",
        201 => "Defense 1",
        202 => "Defense 2",
        _ => $"Channel {channel}",
    };

    public string IssueConnectCode(UserId user, string host)
    {
        Issued.Add((user, host));
        return Code;
    }

    public int Revoke(UserId user)
    {
        Revoked.Add(user);
        return RevokeCount;
    }

    public List<(UserId Caller, JungleCommand Command)> JungleCalls { get; } = [];

    public string JungleReply { get; set; } = "jungle reply";

    public string Jungle(UserId caller, JungleCommand command)
    {
        JungleCalls.Add((caller, command));
        return JungleReply;
    }

    public string Redact(string text) => text.Replace("t1", "[redacted]", StringComparison.Ordinal);

    public List<(ChannelId Channel, string Text)> Posts { get; } = [];

    public bool RefusePosts { get; set; }

    public Task<bool> PostAsync(ChannelId channel, string text, CancellationToken cancellationToken)
    {
        if (RefusePosts)
        {
            return Task.FromResult(false);
        }
        Posts.Add((channel, text));
        return Task.FromResult(true);
    }
}
