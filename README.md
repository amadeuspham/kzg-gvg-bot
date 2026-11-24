# kzg-gvg-bot

A Discord relay for Where Winds Meet guild wars. A bot in each small voice channel copies a broadcasting player's audio into the other channels of their big team. A PC app, KZG Broadcast, triggers it: hold a key and your voice goes to the whole big team, or to every team. Phone players have no app; they hear broadcasts and talk in their own team channel.

## Run it

- Players: install KZG Broadcast by following [docs/guides/unsigned-install.md](docs/guides/unsigned-install.md). The installers are unsigned, so Windows and macOS warn first. Link KZG Broadcast once with Sign in with Discord, or `/connect` in Discord and paste the code.
  - Every player has three controls on the main window: hold F8 to talk to your whole team, hold F7 to talk to all teams (Attack and Defense), and press F9 (or click the switch) for open mic, which keeps your own team hearing you until you turn it off. Open mic works only while you sit in a team voice channel.
  - Click a key chip to change its key; while it waits, Clear leaves that control unset ("Not set") so no key fires it, and Cancel keeps the key. One key can't do two controls. Esc can be a key, so it doesn't cancel.
  - Settings has an "Open at login" switch, off by default.
  - Updates install automatically once an update feed is set; the app shows "Restart to update" when ready.
  - On a Mac, the app restarts by itself once Input Monitoring is granted.
- Input: macOS needs the Input Monitoring permission only (no Accessibility). Windows reads keys with Raw Input, with a low-level hook as fallback.
- Whoever hosts the bot: [docs/guides/host.md](docs/guides/host.md). No file needs editing by hand:
  - The first start opens a setup page (link printed in the bot's window or log): paste the bot tokens and invite each bot. Each field is named for the team channel its bot fills, A1 Broadcaster to A3 Broadcaster for Attack and D1 Broadcaster to D3 Broadcaster for Defense.
  - In Discord, `/gvg setup` picks the Attack and Defense voice channels; its `channel` option picks where problems (such as a relay bot going offline) are posted. Each bot then goes by its channel, such as "A1 Broadcaster", in replies, alerts and its server nickname.
  - Run it with Docker and Caddy for automatic HTTPS (`packaging/docker/`), on an Apple Silicon Mac (a login service that restarts after a crash and keeps the Mac awake), Windows, or Linux.
  - Players sign in with Discord (set up in the guide) or link with `/connect` codes.
  - Any member can run `/jungle` for spoken jungle calls in every team channel: `/jungle start` at the match start, `/jungle sync 24:30` to join a match in progress (the in-game time), then `pause`, `resume`, `stop`, `status` and `test`. The calls come 40 and 20 seconds before each jungle spawn. The clock lives in the bot, so after a bot restart run `/jungle sync` again. If the calls are too loud or too quiet, an admin sets `/gvg config set jungleVolume 1-100` (default 32; takes effect from the next call).
- Developers: `dotnet run --project src/Kzg.Bot -- --fake` runs the bot offline against a made-up server and prints demo connect codes. The wire and engine contracts are in [docs/contracts.md](docs/contracts.md).

## Layout

- `src/Kzg.Protocol`: WebSocket messages shared by bot and app.
- `src/Kzg.Core`: pure logic (routing, buffers, config, pairing). No Discord, UI or native dependencies.
- `src/Kzg.Audio`, `src/Kzg.Discord`: audio codec, Discord adapter.
- `src/Kzg.Bot`: host executable.
- `src/Kzg.App`, `src/Kzg.App.Input`: the KZG Broadcast PC app and its input sources.
- `tests/Kzg.<Project>.Tests`: one test project per project it covers.
- `packaging/`: scripts that build the release files. Output goes to `packaging/out/`, which is git-ignored.
- `docs/`: [contracts.md](docs/contracts.md) and the guides in `docs/guides/`.

## Gate

Run `scripts/check.sh` before every commit. It builds, checks formatting, and runs the tests. `Kzg.Core` must keep at least 80% line coverage.

Three opt-in groups of tests are skipped by the gate: `KZG_LIVE=1` runs checks against the Discord test server (bots in `.data/secrets.json`), `KZG_REALTIME=1` runs the tests that drive the engine against the wall clock and assert timing ranges (run them on a quiet machine), and `KZG_LOAD=1` runs the load test (five minutes by default, `KZG_LOAD_SECONDS` sets the length; needs macOS `say`). They are described in [docs/contracts.md](docs/contracts.md).

## Packaging

The version is `<Version>` in `Directory.Build.props`. Nothing here signs, uploads or releases.

- `packaging/publish-bot.sh --rid win-x64|linux-x64|linux-arm64|osx-arm64`: the bot as a self-contained folder and a `.zip` or `.tar.gz`, with libdave 1.2.1.9. The macOS one holds `install-mac.command`, which installs a launchd service for the user; Intel Macs aren't supported.
- `docker build -f packaging/docker/Dockerfile -t kzg-bot .`: the bot on Ubuntu 24.04, for Linux hosts whose glibc is older than 2.38.
- `packaging/docker/compose.yaml`: the bot plus Caddy for automatic HTTPS. Put the domain in `packaging/docker/kzg.env`, then `docker compose up -d` in that folder.
- `packaging/pack-app.sh --rid win-x64|osx-arm64|osx-x64`: the app as an unsigned Velopack installer, portable zip and update feed. The macOS packages build on a Mac only. vpk refuses to pack the Windows app until `Program.Main` calls `VelopackApp.Build().Run()`; `--allow-missing-velopack-hook` skips that check for trial builds.

## Local data

Local settings and secrets live in `.data/`, which is git-ignored except `.data/*.example.json`. Copy `.data/secrets.example.json` to `.data/secrets.json` and fill it in.
