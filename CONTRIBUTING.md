# Contributing

How to work in this repo, for people and AI assistants alike. The agent-specific rules are in [AGENTS.md](AGENTS.md); machine setup is in [docs/guide/development.md](docs/guide/development.md).

## Quick start

1. Set up your machine as the [developer setup](docs/guide/development.md) describes.
2. Run `scripts/check.sh`. It builds with warnings as errors, checks formatting, runs the tests, and enforces 80% line coverage on `Kzg.Core`.
3. Try the bot offline: `dotnet run --project src/Kzg.Bot -- --fake` runs it against a made-up server and prints demo connect codes. The wire and engine contracts are in [docs/reference/contracts.md](docs/reference/contracts.md).

## Making a change

1. Branch from `main`: `feat/...`, `fix/...`, `docs/...`, `chore/...`, `refactor/...`.
2. Make the change with tests. Run `scripts/check.sh`; it must pass. Run it before every commit.
3. Commit with [Conventional Commits](https://www.conventionalcommits.org/): `type(scope): imperative subject`, under 72 characters, body explains why.
4. Open a PR using the template. CI runs the same gate on macOS, Windows and Linux.
5. Squash-merge once green.

## Project layout

| Path | What it holds |
| --- | --- |
| `src/Kzg.Protocol` | WebSocket messages shared by bot and app. |
| `src/Kzg.Core` | Pure logic (routing, buffers, config, pairing). No Discord, UI or native dependencies. |
| `src/Kzg.Audio`, `src/Kzg.Discord` | Audio codec, Discord adapter. |
| `src/Kzg.Bot` | Host executable. |
| `src/Kzg.App`, `src/Kzg.App.Input` | The Discord Broadcast PC app and its input sources. |
| `tests/Kzg.<Project>.Tests` | One test project per project it covers. |
| `packaging/` | Scripts that build the release files. Output goes to `packaging/out/`, which is git-ignored. |
| `docs/` | The documentation site source (VitePress): guides in `docs/guide/`, reference in `docs/reference/` ([contracts.md](docs/reference/contracts.md), [bot.md](docs/reference/bot.md)), and internal notes (`plan.md`, `decisions.md`) that are not published. |

## Tests

`scripts/check.sh` runs the normal tests. Three opt-in groups are skipped by the gate and never part of CI. They are described in [docs/reference/contracts.md](docs/reference/contracts.md).

| Variable | Runs | Notes |
| --- | --- | --- |
| `KZG_LIVE=1` | Checks against the Discord test server | Bots in `.data/secrets.json`. |
| `KZG_REALTIME=1` | Tests that drive the engine against the wall clock and assert timing ranges | Run them on a quiet machine. |
| `KZG_LOAD=1` | The load test | Five minutes by default; `KZG_LOAD_SECONDS` sets the length. Needs macOS `say`. |

## Documentation

Preview the site with `npm ci && npx vitepress dev docs`. `npx vitepress build docs` fails on dead links. Record non-obvious decisions as one line in [docs/decisions.md](docs/decisions.md).

## Rules of thumb

- Secrets never enter git, logs, issues or PRs. Fixtures use made-up values.
- Keep `Kzg.Core` free of Discord, UI and native references.
- libdave stays at 1.2.1.9.
- Release steps and signing are covered by `.github/workflows/release.yml` and the development guide; a release needs the maintainer's explicit go-ahead.
