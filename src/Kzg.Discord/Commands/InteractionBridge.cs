using Kzg.Core.Abstractions;
using Microsoft.Extensions.Logging;
using NetCord;
using NetCord.Rest;

namespace Kzg.Discord.Commands;

/// <summary>One option of a slash command, free of the library: a sub-command or group, or a value.</summary>
internal sealed record OptionNode(string Name, bool IsSubCommand, string? Value, IReadOnlyList<OptionNode> Children);

/// <summary>
/// Turns NetCord's interactions into <see cref="CommandInvocation"/>s and the handler's reply back into a
/// message only the caller sees. The work runs off the gateway's thread, so a slow command never delays
/// its heartbeat; Discord is told at once that an answer is coming.
/// </summary>
internal sealed class InteractionBridge(Func<GvgCommands?> commands, ILogger log)
{
    public ValueTask OnInteraction(Interaction interaction)
    {
        if (interaction is SlashCommandInteraction slash)
        {
            _ = Task.Run(() => RunAsync(slash));
        }
        return default;
    }

    /// <summary>The sub-command path and the value options, by name.</summary>
    internal static (IReadOnlyList<string> Path, IReadOnlyDictionary<string, string> Options) Flatten(IEnumerable<OptionNode> options)
    {
        List<string> path = [];
        Dictionary<string, string> values = new(StringComparer.Ordinal);
        var level = options;
        while (true)
        {
            var next = level.FirstOrDefault(o => o.IsSubCommand);
            if (next is null)
            {
                break;
            }
            path.Add(next.Name);
            level = next.Children;
        }
        foreach (var option in level.Where(o => !o.IsSubCommand && o.Value is not null))
        {
            values[option.Name] = option.Value!;
        }
        return (path, values);
    }

    private async Task RunAsync(SlashCommandInteraction slash)
    {
        try
        {
            await slash.SendResponseAsync(InteractionCallback.DeferredMessage(MessageFlags.Ephemeral)).ConfigureAwait(false);
            var reply = await HandleAsync(slash).ConfigureAwait(false);
            await slash.ModifyResponseAsync(message => message
                .WithContent(reply.Text)
                .WithAllowedMentions(AllowedMentionsProperties.None)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            // Never the command's options or its reply: a connect code is in one of them.
            log.LogWarning("a slash command failed: {Error}", exception.GetType().Name);
        }
    }

    private async Task<CommandReply> HandleAsync(SlashCommandInteraction slash)
    {
        if (commands() is not { } handler)
        {
            return CommandReply.Of("The bot is still starting. Try again in a moment.");
        }
        try
        {
            var (path, options) = Flatten(slash.Data.Options.Select(ToNode));
            var user = slash.User;
            var guildUser = user as GuildInteractionUser;
            var caller = new Caller(
                new UserId(user.Id),
                guildUser is null ? 0 : (ulong)guildUser.Permissions,
                guildUser?.RoleIds ?? [],
                user.IsBot);
            var call = new CommandInvocation(slash.Data.Name, path, options, caller, slash.GuildId);
            return await handler.HandleAsync(call, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            log.LogWarning("a slash command handler failed: {Error}", exception.GetType().Name);
            return CommandReply.Of("That didn't work. Try again; if it keeps failing, check the bot's log.");
        }
    }

    private static OptionNode ToNode(ApplicationCommandInteractionDataOption option) => new(
        option.Name,
        option.Type is ApplicationCommandOptionType.SubCommand or ApplicationCommandOptionType.SubCommandGroup,
        option.Value,
        [.. (option.Options ?? []).Select(ToNode)]);
}
