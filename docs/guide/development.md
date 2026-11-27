# Developer setup

What a machine needs to build, test and live-test kzg-gvg-bot, and how to hand work between machines. For developers and their AI assistants; not for running the bot (see the [hosting guide](hosting.md)) or installing the app (see the [install guide](getting-started.md)).

The short version: install git, bash and the .NET SDK, then run `scripts/check.sh`. If it passes, you can build and test everything that needs no secrets.

## Every machine

- Git and bash. On Windows use Git for Windows' bash; the gate and packaging scripts are bash.
- .NET SDK 10.0.100 or newer 10.0.x (`global.json`). `scripts/dotnet.sh` finds it in `DOTNET_ROOT`, `%LOCALAPPDATA%\Microsoft\dotnet` or `~/.dotnet`, then PATH.
- Check the setup: `scripts/check.sh` must pass. It needs no network, secrets or Discord.
- Claude Code: `CLAUDE.md` loads `AGENTS.md`, which holds the rules. Project permissions are in `.claude/settings.json`; personal ones go in `.claude/settings.local.json` (git-ignored).

## Extra requirements by task

| Task | Needs |
|---|---|
| Bot or engine work | The gate only. `dotnet run --project src/Kzg.Bot -- --fake` runs offline. |
| App work | Any OS. macOS needs the Input Monitoring permission for the terminal or app running it. Windows key behaviour under an elevated game needs a Windows PC with the game. |
| Live tests (`KZG_LIVE=1`) | A Discord test server with six voice channels (Attack 1-3, Defense 1-3), the bot accounts, and `.data/secrets.json`. See [Live tests](#live-tests). Real network; UDP to Discord voice must be open. |
| Real-clock tests (`KZG_REALTIME=1`) | Any OS, a quiet machine: they assert timing ranges against the wall clock. `KZG_REALTIME=1 dotnet test tests/Kzg.Bot.Tests -c Release`. |
| Load test (`KZG_LOAD=1`) | macOS, for `say`. Five minutes by default. |
| Linux bot | glibc 2.38 or newer (Ubuntu 24.04). Older hosts use the Docker image. |
| Packaging | The host OS for its own target; macOS packages build on a Mac. See [Packaging](#packaging). |
| Mac release signing | The maintainer's self-signed code-signing certificate as a password-protected `.p12`, kept outside git; set as a CI secret when releasing. |

## Local data and secrets

`.data/` is the dev data folder, git-ignored except `*.example.json`. Copy `.data/secrets.example.json` to `.data/secrets.json` and fill it from a password manager. Names: `relay1`..`relay6`, `tester1`..`tester3`, `listener1`..`listener6`, `oauthClientSecret`. `KZG_SECRET_<name>` environment variables override the file. Under Docker Compose they come from `packaging/docker/secrets.env`, git-ignored and optional; `kzg.env` is tracked and holds no secrets.

Secrets move between machines by hand (password manager, encrypted copy), never through git, chat, issues or logs. Without them a machine still runs the gate and every offline test.

## Live tests

1. Warn the user: the tests join the real test server's voice channels with every bot.
2. `KZG_LIVE=1 dotnet test tests/Kzg.Bot.Tests -c Release --filter "FullyQualifiedName~Live"`. Output and recordings go to `.data/live/<timestamp>/`.
3. Afterwards every bot must be out of voice. Check the test server's member list. If a run was killed, rerun a live test to let the cleanup run, or remove the bots by hand.
4. A live test skipped with no variable set is normal; it is not a pass.

## Packaging

The version is `<Version>` in `Directory.Build.props`. The release workflow (`.github/workflows/release.yml`, run by hand) builds and signs the app and creates the GitHub Release; the bot is not released.

- `packaging/publish-bot.sh --rid win-x64|linux-x64|linux-arm64|osx-arm64`: the bot as a self-contained folder and a `.zip` or `.tar.gz`, with libdave 1.2.1.9. The macOS one holds `install-mac.command`, which installs a launchd service for the user; Intel Macs aren't supported. Releases carry no bot archive; build it with this script.
- `docker build -f packaging/docker/Dockerfile -t kzg-bot .`: the bot on Ubuntu 24.04, for Linux hosts whose glibc is older than 2.38.
- `packaging/docker/compose.yaml`: the bot plus Caddy for automatic HTTPS. Put the domain in `packaging/docker/kzg.env`, then `docker compose up -d` in that folder.
- `packaging/pack-app.sh --rid win-x64|osx-arm64|osx-x64`: the app as an unsigned Velopack installer, portable zip and update feed. The macOS packages build on a Mac only. The installer files are named `DiscordBroadcast-<platform>-Setup.*` and `-Portable.zip`; the update-feed files keep the `KZGBroadcast` id. The app icons are rendered from `src/Kzg.App/Assets/icon.svg` by `src/Kzg.App/Assets/render-icons.sh` (macOS only; needs Google Chrome, which renders them with a transparent background). vpk refuses to pack the Windows app until `Program.Main` calls `VelopackApp.Build().Run()`; `--allow-missing-velopack-hook` skips that check for trial builds.
- Default bot address: the sign-in screen's Bot address field is filled from the address built into the app, set at build time with `-p:KzgDefaultHost=<host>` (for example `bot.example.org`). An address a player saved wins over it.

## CI and releases

- CI (`.github/workflows/ci.yml`) runs the gate on macOS 26, Windows and Ubuntu 24.04 for every PR and push to `main`. It uses no secrets. The format check runs on Ubuntu only.
- CI always runs. A `changes` job skips the gate when every changed path is docs, site, infra, spike, `.claude`, Markdown, `package*.json` or the pages, terraform and release workflows. The `CI result` job (`ci-result` in `ci.yml`) always reports and fails only if a gate job failed or was cancelled. Make `CI result` the only required status check on `main`; do not require the `gate (...)` checks, which are skipped on docs-only changes.
- Auto-update works once a release exists: the release workflow packs the app with `KZG_UPDATE_FEED` set to the repo address, and the app reads the Velopack files from the repo's releases.
- The release workflow is manual and does nothing until the secret `KZG_MAC_CERT_P12` exists (also `KZG_MAC_CERT_PASSWORD`, `KZG_MAC_SIGN_IDENTITY`). Review `packaging/pack-app.sh`'s signing variable name against the workflow before the first release. When the Mac and Windows jobs pass, a last job tags `v<version>` at the run's commit and creates the GitHub Release with every file in `velopack-<rid>/` and `SHA256SUMS`. The release notes carry a Download table (installers and portable zips) and mark the `.nupkg` and `.json` files as update-feed files (`RELEASES-*` and `assets.*.json` are not uploaded; the app reads only `releases.<channel>.json`). The bot is not released; hosts build it from source (see the [hosting guide](hosting.md)). The version input must equal `<Version>` in `Directory.Build.props`.

The bot host's infrastructure is described in the [infrastructure notes](../../infra/oci/README.md).

## Handing work to another machine

Commit or stash on a branch, push it, then pull on the other machine and read [AGENTS.md](../../AGENTS.md). State not in git (secrets, running test bots, a half-done manual check) is the user's to carry; note it in the PR description or commit body.

## Bot package notes

- Build the bot archives with `packaging/publish-bot.sh --rid win-x64`, `--rid linux-x64` or `--rid osx-arm64`. Each holds the bot, the .NET runtime and libdave. Releases don't carry them; this is how a host builds its own.
- libdave 1.2.1.9 (Discord voice encryption) is bundled in the archives and the Docker image. It keeps its own license; read upstream's before redistributing a package.
- Memory test, one hour with 30 talkers and 6 broadcasting: the process went from about 135 MB to 143 MB, managed heap flat at 15.7 MB, CPU about 1% of a 14-core Mac. The small remaining growth is outside the managed heap; cause not found. The 512 MB cap in the [bot reference](../reference/bot.md#memory-cap-and-daily-restart) is about three times what a full war needs.
- Log rotation: at 10 MB a log moves to `bot.1.log`, two old files kept (30 MB total). Docker keeps three 10 MB log files per container.
