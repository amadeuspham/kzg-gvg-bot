# Decisions

Orientation: one line per decision, newest-relevant first within each topic; the reasoning is in [plan.md](plan.md). Where this file and the plan disagree, this file wins. Add a line here for any non-obvious choice.

## Voice and audio

- NetCord 1.0.0-beta.30 is the Discord library, with libdave pinned to 1.2.1.9; libdave 1.3.0 breaks voice silently.
- One relay bot per voice channel. Each destination gets a mix-minus of the broadcasters; one source passes through as Opus without re-encoding.
- The jitter buffer is adaptive, 60 ms growing to 240 ms; a new broadcast reads 300 ms of pre-roll.
- Engine ticks read players' buffers at their due time, not the wall clock, so ticks that catch up a stall under `MaxBehind` lose no audio.
- The keyword/trigger-word feature was built and removed. Phone players hear every broadcast and talk in their own team channel.
- A relay bot that is moved goes straight back to its own channel; only `/gvg bot assign` changes a bot's channel.
- A relay bot is named after the team channel it is in now ("A1 Broadcaster"), worked out from the config and never stored, and sets that as its nickname when it can; its `tokenRef` stays `relay<setup field>`, which is how `/gvg setup` puts each bot in the channel its setup field named.
- The bot restarts itself at a 512 MB memory cap; a daily restart is optional.
- Jungle calls come from the bot as an engine `IAudioSource`, driven by `/jungle` only (no app or `/ws` change). Any server member may run it; `/gvg` stays admin-only.
- `/jungle` replies are ephemeral, like every other command.
- The jungle clock lives in memory only: a bot restart mid-match loses it and players run `/jungle sync` again.
- The bot posts no jungle text on its own; over-the-match and nobody-heard events go to the log, and `/jungle status` and `/jungle test` say them on request.
- Jungle clips are embedded WAVs, encoded to Opus at the mixer bitrate (64 kbps) at startup and again when `jungleVolume` (1 to 100, default 32) changes. 100 is jungle-bot's gain of 0.5, which was too loud in real games; 32 is about 10 dB under it.

## App and input

- Input is listen-only. macOS needs Input Monitoring only (listen-only event tap, no Accessibility). Windows uses Raw Input, with the low-level hook as the fallback.
- The app warns when the game runs elevated; it never auto-elevates, and "Run as administrator" is the user's choice.
- The UI uses Inter and Tabler icons, a fixed 420x560 window, Settings on its own page, and the label "Open mic broadcast".
- Shot callers are gone: every player has the team key (F8), the all-teams key (F7, every bound VC of both groups but their own) and open mic (their own team, from a team VC); each key can be left unset, and keys are set on the main window only.
- Discord sign-in is optional; `/connect` codes need no OAuth setup.
- "Open at login" is off by default. Auto-update stays inactive until the GitHub repo exists.
- macOS release builds are signed with one free self-signed certificate so Input Monitoring survives updates; not notarized.

## Hosting

- The bot runs on Windows, Linux (Docker for glibc older than 2.38) or an Apple Silicon Mac. Intel Macs are not supported.
- `/gvg stop` is saved in `config.json`, so it survives restarts.
- Releases carry no bot archive: the bot runs only on our server, which builds it from source with Docker. Other hosts build with `packaging/publish-bot.sh`.
- OCI infrastructure is changed only through `infra/oci` and the Terraform workflow, never the console. State moved from Resource Manager to an Object Storage bucket (native `oci` backend, conditional-write lock): Resource Manager holds one config per stack, so PR plans would overwrite what the next apply uses, and its job logs carry the full plan.
- CI authenticates to OCI with API keys of two IAM users, a read-only one for plans and a write one for applies behind a reviewed, main-only GitHub environment. OCI federation with GitHub OIDC was left out: more setup than a single-maintainer repo needs.
- Terraform output stays out of the public Actions logs and artifacts; only resource addresses and actions are shown, and an apply runs only if its fresh plan lists the same changes that were approved.

## Testing

- Tests that run the engine against the wall clock (`[RealTimeFact]`, `KZG_REALTIME=1`) are opt-in like the live and load tests: their timing ranges fail on a busy CI runner, and the gate runs fake clocks only.
- The live test classes share one xUnit collection with parallelization off: each starts the bot on the same relay tokens and seats the same test bots, and Discord gives a bot one voice connection per server, so two at once drop each other's audio and leave bots in voice.

## Release

- The release workflow ends in one job that tags `v<version>` and creates the GitHub Release; app packs set `KZG_UPDATE_FEED` to the repo address so Velopack reads updates from release assets. Per-job artifacts hold only release files, so the last job can attach everything.
- Mac installer and portable files are named `macOS-arm64` for players; the Velopack channel, RID, `.nupkg`, `RELEASES-*` and `*.json` keep `osx-arm64`, because installed apps update from that channel. `pack-app.sh` does the rename.
- The app's display name is "Discord Broadcast", but the Velopack pack id `KZGBroadcast` and the Mac bundle id `app.kzg.broadcast` are kept, so installed apps keep updating and macOS keeps the Input Monitoring grant; only the installer file names, the Mac `.app` name (vpk's `--packTitle`) and the Windows startup entry changed.
- The default Bot address is set at build time through the `KzgDefaultHost` MSBuild property; a saved setting wins over it and the field stays editable.
