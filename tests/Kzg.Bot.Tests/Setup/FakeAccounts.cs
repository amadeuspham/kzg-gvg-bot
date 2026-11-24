using Kzg.Discord.Setup;

namespace Kzg.Bot.Tests.Setup;

/// <summary>Discord's view of bot tokens as a table: which tokens are bots, and which servers each is in.</summary>
internal sealed class FakeAccounts : IBotAccounts
{
    public const ulong Guild = 5000, OtherGuild = 6000;

    // Made up at run time, in a bot token's shape; nobody's.
    public static string Token(int n) => string.Join('.', new string((char)('A' + n), 26), "Gabcde", new string('q', 37) + n);

    public Dictionary<string, BotAccount> Bots { get; } = new(StringComparer.Ordinal)
    {
        [Token(1)] = new BotAccount(1001, 9001, "Kzg Relay 1"),
        [Token(2)] = new BotAccount(1002, 9002, "Kzg Relay 2"),
        [Token(3)] = new BotAccount(1003, null, "Kzg <Relay> & 3"),
    };

    public Dictionary<string, List<ServerRef>> Servers { get; } = new(StringComparer.Ordinal);

    public bool Down { get; set; }

    public int Looks { get; private set; }

    public void Join(int bot, ulong guild = Guild) =>
        (Servers.TryGetValue(Token(bot), out var list) ? list : Servers[Token(bot)] = []).Add(new ServerRef(guild, guild == Guild ? "KZG GvG" : "Other"));

    public Task<TokenCheck> CheckAsync(string token, CancellationToken cancellationToken) =>
        Task.FromResult<TokenCheck>(Down ? new TokenCheck.Unreachable() : Bots.TryGetValue(token, out var bot) ? new TokenCheck.Accepted(bot) : new TokenCheck.Refused());

    public Task<IReadOnlyList<ServerRef>?> ServersAsync(string token, CancellationToken cancellationToken)
    {
        Looks++;
        return Task.FromResult<IReadOnlyList<ServerRef>?>(Down ? null : Servers.TryGetValue(token, out var list) ? [.. list] : []);
    }
}
