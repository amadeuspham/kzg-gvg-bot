# Developer setup

What: what a machine needs to build, test and live-test kzg-gvg-bot, and how to hand work between machines. For developers and their AI assistants; not for running the bot ([host guide](host.md)) or installing the app ([unsigned install](unsigned-install.md)).

## Every machine

- Git and bash. On Windows use Git for Windows' bash; the gate and packaging scripts are bash.
- .NET SDK 10.0.100 or newer 10.0.x (`global.json`). `scripts/dotnet.sh` finds it in `DOTNET_ROOT`, `%LOCALAPPDATA%\Microsoft\dotnet` or `~/.dotnet`, then PATH.
- Check the setup: `scripts/check.sh` must pass. It needs no network, secrets or Discord.
- Claude Code: `CLAUDE.md` loads `AGENTS.md`, which holds the rules. Project permissions are in `.claude/settings.json`; personal ones go in `.claude/settings.local.json` (git-ignored).

## Extra, by task

| Task | Needs |
|---|---|
| Bot or engine work | The gate only. `dotnet run --project src/Kzg.Bot -- --fake` runs offline. |
| App work | Any OS. macOS needs the Input Monitoring permission for the terminal or app running it. Windows key behaviour under an elevated game needs a Windows PC with the game. |
| Live tests (`KZG_LIVE=1`) | A Discord test server with six voice channels (Attack 1-3, Defense 1-3), the bot accounts, and `.data/secrets.json`. Details below. Real network; UDP to Discord voice must be open. |
| Load test (`KZG_LOAD=1`) | macOS, for `say`. Five minutes by default. |
| Linux bot | glibc 2.38 or newer (Ubuntu 24.04). Older hosts use the Docker image. |
| Packaging | The host OS for its own target; macOS packages build on a Mac. See README "Packaging". |
| Mac release signing | The maintainer's self-signed code-signing certificate as a password-protected `.p12`, kept outside git; set as a CI secret when releasing. |

## Local data and secrets

`.data/` is the dev data folder, git-ignored except `*.example.json`. Copy `.data/secrets.example.json` to `.data/secrets.json` and fill it from a password manager. Names: `relay1`..`relay6`, `tester1`..`tester3`, `listener1`..`listener6`, `oauthClientSecret`. `KZG_SECRET_<name>` environment variables override the file. Under Docker Compose they come from `packaging/docker/secrets.env`, git-ignored and optional; `kzg.env` is tracked and holds no secrets.

Secrets move between machines by hand (password manager, encrypted copy), never through git, chat, issues or logs. Without them a machine still runs the gate and every offline test.

## Live tests

1. Warn the user: the tests join the real test server's voice channels with every bot.
2. `KZG_LIVE=1 dotnet test tests/Kzg.Bot.Tests -c Release --filter "FullyQualifiedName~Live"`. Output and recordings go to `.data/live/<timestamp>/`.
3. Afterwards every bot must be out of voice. Check the test server's member list. If a run was killed, rerun a live test to let the cleanup run, or remove the bots by hand.
4. A live test skipped with no variable set is normal; it is not a pass.

## GitHub

The repo has no remote yet. When it exists:

1. Create it empty, add the remote, push `main`.
2. CI (`.github/workflows/ci.yml`) runs the gate on macOS, Windows and Ubuntu 24.04 for every PR and push to `main`. It uses no secrets.
3. Turn on branch protection requiring the three gate jobs.
4. Auto-update in the app stays inactive until the update feed URL points at the repo's releases; set that then.
5. The release workflow is manual and stays inert until the secret `KZG_MAC_CERT_P12` exists (also `KZG_MAC_CERT_PASSWORD`, `KZG_MAC_SIGN_IDENTITY`). Review `packaging/pack-app.sh`'s signing variable name against the workflow before the first release.

## Handing work to another machine

Commit or stash on a branch, push it, then pull on the other machine and read [AGENTS.md](../../AGENTS.md). State not in git (secrets, running test bots, a half-done manual check) is the user's to carry; note it in the PR description or commit body.

## Bot packages and memory

- Build the bot archives with `packaging/publish-bot.sh --rid win-x64`, `--rid linux-x64` or `--rid osx-arm64`. Each holds the bot, the .NET runtime and libdave.
- libdave 1.2.1.9 (Discord voice encryption) is bundled in the archives and the Docker image. It keeps its own license; read upstream's before redistributing a package.
- Memory test, one hour with 30 talkers and 6 broadcasting: the process went from about 135 MB to 143 MB, managed heap flat at 15.7 MB, CPU about 1% of a 14-core Mac. The small remaining growth is outside the managed heap; cause not found. The 512 MB cap in the host guide (Reference, "Memory cap and daily restart") is about three times what a full war needs.
- Log rotation: at 10 MB a log moves to `bot.1.log`, two old files kept (30 MB total). Docker keeps three 10 MB log files per container.
