using Kzg.Core.Abstractions;

namespace Kzg.Discord.Commands;

/// <summary>Who ran a command, and what Discord says they may do.</summary>
/// <param name="Permissions">The member's permission bits in the channel, as Discord sent them.</param>
public sealed record Caller(UserId User, ulong Permissions, IReadOnlyList<ulong> Roles, bool IsBot);

/// <summary>
/// One slash command as the handler sees it, free of the library: the command's name, the path of
/// sub-command names, and its options as the text Discord sent. IDs are decimal snowflakes.
/// </summary>
public sealed record CommandInvocation(
    string Command,
    IReadOnlyList<string> Path,
    IReadOnlyDictionary<string, string> Options,
    Caller Caller,
    ulong? GuildId)
{
    public string? Option(string name) => Options.TryGetValue(name, out var value) ? value : null;

    public bool Is(params string[] path) => Path.SequenceEqual(path);
}

/// <summary>What to answer. Always ephemeral: only the one who ran the command sees it.</summary>
public sealed record CommandReply(string Text)
{
    /// <summary>Discord's limit on a message's text.</summary>
    public const int MaxLength = 2000;

    public static CommandReply Of(string text) => new(text.Length <= MaxLength ? text : text[..(MaxLength - 1)] + "…");
}
