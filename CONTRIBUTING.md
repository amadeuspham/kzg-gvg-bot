# Contributing

What: how to work in this repo, for people and AI assistants alike. The agent-specific rules are in [AGENTS.md](AGENTS.md); machine setup is in [docs/guides/dev-setup.md](docs/guides/dev-setup.md).

## Flow

1. Branch from `main`: `feat/...`, `fix/...`, `docs/...`, `chore/...`, `refactor/...`.
2. Make the change with tests. Run `scripts/check.sh`; it must pass (build with warnings as errors, format check, tests, 80% line coverage on `Kzg.Core`).
3. Commit with [Conventional Commits](https://www.conventionalcommits.org/): `type(scope): imperative subject`, under 72 characters, body explains why.
4. Open a PR using the template. CI runs the same gate on macOS, Windows and Linux.
5. Squash-merge once green.

## Rules of thumb

- Secrets never enter git, logs, issues or PRs. Fixtures use made-up values.
- Keep `Kzg.Core` free of Discord, UI and native references.
- Live and load tests are opt-in (`KZG_LIVE=1`, `KZG_LOAD=1`) and never part of CI.
- libdave stays at 1.2.1.9.
- Record non-obvious decisions as one line in [docs/decisions.md](docs/decisions.md).
- Release steps and signing are covered by `.github/workflows/release.yml` and the dev-setup guide; a release needs the maintainer's explicit go-ahead.
