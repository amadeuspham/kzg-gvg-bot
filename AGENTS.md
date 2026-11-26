# Agent instructions: kzg-gvg-bot

A Discord relay for team voice events. One relay bot sits in each small voice channel and copies a broadcasting player's audio into the other channels of their big team. A PC app (Avalonia, .NET 10) sends the "broadcast me" signal. C# / .NET 10, macOS, Windows and Linux.

Read these first, in order: [docs/plan.md](docs/plan.md) (what and why), [docs/decisions.md](docs/decisions.md) (what changed since), [docs/reference/contracts.md](docs/reference/contracts.md) (wire and engine contracts), [docs/guide/development.md](docs/guide/development.md) (what each machine needs). Where the plan and decisions disagree, decisions win.

## Hard rules

1. **Never print, log, commit or paste a secret.** That covers bot tokens, the OAuth client secret, device tokens, connect codes, signing certificates and their passwords. Refer to them by name (`relay1`, `oauthClientSecret`). Do not read `.data/secrets.json` or `.data/devices.json` into the conversation; if a task needs a value, let the code read it. Test fixtures use made-up values only.
2. **Never touch the user's running processes.** Do not kill, restart, signal or attach to a bot, app, voice-chat or game process you did not start in this session. Do not change their data folders.
3. **Synthetic input only after a warning.** Anything that presses keys, moves the mouse or plays audio on the user's machine (CGEvent posting, SendInput, `say`, audio playback) runs only after you warn the user in chat and they say go. The app itself only listens; it never injects input.
4. **Commit and push only when asked.** Work stays uncommitted until the user says "commit". Never push, open a PR, merge, tag or release without an explicit go-ahead. Never use `--no-verify`.
5. **Live tests leave nothing behind.** They need `KZG_LIVE=1` and the test bots in `.data/secrets.json`. Afterwards every bot is out of voice; check with the live test's own cleanup output, and if a run was killed, say so and list the bots that may still be connected.
6. **libdave stays pinned to 1.2.1.9.** 1.3.0 breaks voice silently (no error, no audio). Do not bump it, and do not let a dependency update or `dotnet restore` float it.
7. **Never push to `main` directly once a remote exists**; use a branch and a PR.

## Commands

- Gate (build with warnings as errors, `dotnet format --verify-no-changes`, tests with coverage): `scripts/check.sh`. CI runs the same file. Run it before reporting work done, and say so if you could not.
- Format: `dotnet format Kzg.slnx`
- Docs site (VitePress, from `docs/`): `npm ci && npx vitepress build docs`. It fails on dead links. Run it when you move or rename a doc or change links in one.
- Offline bot with a made-up server and demo connect codes: `dotnet run --project src/Kzg.Bot -- --fake`
- Live tests (real Discord test server): `KZG_LIVE=1 dotnet test tests/Kzg.Bot.Tests -c Release --filter "FullyQualifiedName~Live"`. Warn the user first: it joins real voice channels.
- Real-clock tests (engine thread against the wall clock; skipped by the gate): `KZG_REALTIME=1 dotnet test tests/Kzg.Bot.Tests -c Release`
- Load test: `KZG_LOAD=1 [KZG_LOAD_SECONDS=300] dotnet test tests/Kzg.Bot.Tests -c Release --filter "FullyQualifiedName~Load"` (macOS `say`; see rule 3).
- Project skills in `.claude/skills/`: `ui-check` (render and inspect the app screens), `live-test` (live Discord tests, warn first), `release` (prepare a release, stops before publishing), `add-command` (checklist for a new `/gvg` subcommand), `soak` (hour-long load test).
- `scripts/dotnet.sh` finds a dotnet with the SDK that `global.json` asks for; the gate sources it. If `dotnet` is not on PATH, the SDK is usually in `~/.dotnet`.

## Code layout

- `src/Kzg.Protocol`: WebSocket messages and connect-code codec. No dependencies.
- `src/Kzg.Core`: pure logic (routing, jitter and pre-roll buffer, mixer, config, pairing). **No Discord, UI or native references.** 80% line-coverage floor, enforced by the gate.
- `src/Kzg.Audio`: Opus (Concentus), resampler.
- `src/Kzg.Discord`: the only project that knows NetCord; swapping the library touches only this.
- `src/Kzg.Bot`: host exe (audio engine thread, Kestrel control server, redacting logger).
- `src/Kzg.App`, `src/Kzg.App.Input`: Avalonia app and input sources.
- `tests/Kzg.<Project>.Tests` mirrors `src/Kzg.<Project>`. Time comes from a fake clock, never the wall clock.
- `spike/`: throwaway experiments, not in the solution. `packaging/`: release scripts (output in git-ignored `packaging/out/`). `.data/`: local settings and secrets, git-ignored except `*.example.json`.

## Conventions

- Version lives only in `Directory.Build.props` `<Version>`.
- Every new behaviour gets a test that fails without it. Integration tests use real I/O (real files, real local sockets); do not mock the filesystem. Tests run on folders of their own (temp dirs), never on `.data/` outside the live tests.
- Comments explain why, never what. No emojis in code or docs. Do not create new documentation files unprompted; update the existing ones.
- Stay in scope: no drive-by refactors, renames or dependency bumps. Validate at boundaries (Discord, WebSocket, files), not for impossible cases.
- A design decision that is not obvious goes in [docs/decisions.md](docs/decisions.md) as one line.
- Doc changes that must travel with code: user-visible control or setting changes go in the player guide ([docs/guide/getting-started.md](docs/guide/getting-started.md)), and in the host guide when they affect hosting; wire or engine changes go in `docs/reference/contracts.md`.

## Commits

[Conventional Commits](https://www.conventionalcommits.org/): `feat`, `fix`, `refactor`, `docs`, `test`, `chore`, `perf`, `build`, `ci`. Optional scope, imperative subject, under 72 characters, body explains why. Every commit Claude makes ends with:

```
🤖 Generated with [Claude Code](https://claude.com/claude-code)

Co-Authored-By: Claude <noreply@anthropic.com>
```

Pass messages by HEREDOC so formatting survives.

## Working on another machine

Follow [docs/guide/development.md](docs/guide/development.md). Secrets never travel through git or chat: the user copies `.data/secrets.json` between machines themselves. If a task needs a Discord test server, a Windows PC or a phone and you do not have it, say what is missing and stop; do not fake a pass.
