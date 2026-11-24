# Decisions

Orientation: one line per decision, newest-relevant first within each topic; the reasoning is in [plan.md](plan.md). Where this file and the plan disagree, this file wins. Add a line here for any non-obvious choice.

## Voice and audio

- NetCord 1.0.0-beta.30 is the Discord library, with libdave pinned to 1.2.1.9; libdave 1.3.0 breaks voice silently.
- One relay bot per voice channel. Each destination gets a mix-minus of the broadcasters; one source passes through as Opus without re-encoding.
- The jitter buffer is adaptive, 60 ms growing to 240 ms; a new broadcast reads 300 ms of pre-roll.
- The keyword/trigger-word feature was built and removed. Phone players hear every broadcast and talk in their own team channel.
- A relay bot that is moved goes straight back to its own channel; only `/gvg bot assign` changes a bot's channel.
- A relay bot is named after the team channel it is in now ("A1 Broadcaster"), worked out from the config and never stored, and sets that as its nickname when it can; its `tokenRef` stays `relay<setup field>`, which is how `/gvg setup` puts each bot in the channel its setup field named.
- The bot restarts itself at a 512 MB memory cap; a daily restart is optional.
- Jungle calls come from the bot as an engine `IAudioSource`, driven by `/jungle` only (no app or `/ws` change). Any guild member may run it; `/gvg` stays admin-only.
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
