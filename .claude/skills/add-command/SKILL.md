---
name: add-command
description: Checklist for adding or changing a /gvg subcommand across the spec, handler, control interface, host, fakes, tests and docs. Use when the user says "add a /gvg command", "new subcommand", "add /gvg <name>", or changes the options of an existing one.
---

# add-command

Every step below names a real file. Skip a step only when the command truly needs nothing there.

1. **Spec**: `src/Kzg.Discord/Commands/CommandSpec.cs`, in `Build`. Add a `Sub(...)`, or a `Sub` inside a `Group(...)`. Option descriptions are at most 100 characters; required options come first. If options are shared, reuse the local helpers (`GroupOption`, `ChannelOption`, `UserOption`).
2. **Handler**: `src/Kzg.Discord/Commands/GvgCommands.cs`. Add the path to the `call.Path switch` in `HandleAsync`, and a private method. Rights are already checked for every `/gvg` path by `IsAdmin`; do not add a second check. Change config only through `control.EditAsync` returning `ConfigEdit.Change` or `ConfigEdit.Refuse`, so validation runs. Read options by name with `call.Option(...)`.
3. **Control interface**, only if the handler needs something new from the bot: `src/Kzg.Discord/Commands/IGvgControl.cs`.
4. **Host**: `src/Kzg.Bot/Discord/GvgHost.cs`, the only implementation of `IGvgControl`. Implement any new member there. A new status field also goes through `GvgStatus` and `Status()`.
5. **Fakes**, one edit per new `IGvgControl` member:
   - `tests/Kzg.Discord.Tests/FakeControl.cs`
   - `tests/Kzg.Bot.Tests/Host/FakeDiscordRuntime.cs` only if `IDiscordRuntime` (`src/Kzg.Discord/IDiscordRuntime.cs`) changes; it only holds the control through `AttachControl`.
   A new `IGvgControl` member breaks the build of `FakeControl`, which is the reminder.
6. **Tests**, each failing without the change:
   - `tests/Kzg.Discord.Tests/CommandSpecTests.cs`: add the path to the `The_gvg_sub_commands_are_the_plan_s_...` list and its option names.
   - `tests/Kzg.Discord.Tests/GvgCommandsTests.cs` (or `SetupCommandTests.cs` for setup/alerts): reply, config change, refusal, and no change from a non-admin.
   - `tests/Kzg.Bot.Tests/Host/` when host behaviour changes.
   - The live test `RegisteredCommands` in `tests/Kzg.Bot.Tests/Live/LiveRelayTests.cs` compares Discord's commands with `CommandSpec`; it picks the change up with no edit (do not run it without the user's go).
7. **Docs**: `docs/reference/contracts.md` ("Commands" paragraph lists every subcommand and its rules); `docs/plan.md` command list if the plan's list is meant to stay current; `README.md` and the relevant guide for user-visible behaviour; one line in `docs/decisions.md` if the design is not obvious.
8. Run `scripts/check.sh`. Registration happens at bot start, so a new command shows in Discord only after the bot restarts; never restart the user's bot.
