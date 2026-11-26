# Contributing

What: how to work in this repo, for people and AI assistants alike. The agent-specific rules are in [AGENTS.md](AGENTS.md); machine setup is in [docs/guides/dev-setup.md](docs/guides/dev-setup.md).

## Flow

1. Branch from `main`: `feat/...`, `fix/...`, `docs/...`, `chore/...`, `refactor/...`.
2. Make the change with tests. Run `scripts/check.sh`; it must pass (build with warnings as errors, format check, tests, 80% line coverage on `Kzg.Core`).
3. Commit with [Conventional Commits](https://www.conventionalcommits.org/): `type(scope): imperative subject`, under 72 characters, body explains why.
4. Open a PR using the template. CI runs the same gate on macOS, Windows and Linux.
5. Squash-merge once green.

## Layout

- `src/Kzg.Protocol`: WebSocket messages shared by bot and app.
- `src/Kzg.Core`: pure logic (routing, buffers, config, pairing). No Discord, UI or native dependencies.
- `src/Kzg.Audio`, `src/Kzg.Discord`: audio codec, Discord adapter.
- `src/Kzg.Bot`: host executable.
- `src/Kzg.App`, `src/Kzg.App.Input`: the Discord Broadcast PC app and its input sources.
- `tests/Kzg.<Project>.Tests`: one test project per project it covers.
- `packaging/`: scripts that build the release files. Output goes to `packaging/out/`, which is git-ignored.
- `docs/`: [contracts.md](docs/contracts.md) and the guides in `docs/guides/`.

## Gate

Run `scripts/check.sh` before every commit. It builds, checks formatting, and runs the tests. `Kzg.Core` must keep at least 80% line coverage.

Three opt-in groups of tests are skipped by the gate: `KZG_LIVE=1` runs checks against the Discord test server (bots in `.data/secrets.json`), `KZG_REALTIME=1` runs the tests that drive the engine against the wall clock and assert timing ranges (run them on a quiet machine), and `KZG_LOAD=1` runs the load test (five minutes by default, `KZG_LOAD_SECONDS` sets the length; needs macOS `say`). They are described in [docs/contracts.md](docs/contracts.md).

## Try it offline

`dotnet run --project src/Kzg.Bot -- --fake` runs the bot against a made-up server and prints demo connect codes. The wire and engine contracts are in [docs/contracts.md](docs/contracts.md).

## Rules of thumb

- Secrets never enter git, logs, issues or PRs. Fixtures use made-up values.
- Keep `Kzg.Core` free of Discord, UI and native references.
- Live, real-clock and load tests are opt-in (`KZG_LIVE=1`, `KZG_REALTIME=1`, `KZG_LOAD=1`) and never part of CI.
- libdave stays at 1.2.1.9.
- Record non-obvious decisions as one line in [docs/decisions.md](docs/decisions.md).
- Release steps and signing are covered by `.github/workflows/release.yml` and the dev-setup guide; a release needs the maintainer's explicit go-ahead.
