<!-- Snapshot of the working plan. Where it disagrees with docs/decisions.md or the code, those win. Paths are repo-relative. -->

# kzg-gvg-bot: Discord big-team broadcast relay (v1)

## Context

Players take part in team voice events in two big teams, Attack and Defense. Each big team has 3 small teams of 5, and each small team sits in its own Discord voice channel (VC). Players talk freely in their small VC using Discord voice activity. Today they reach the whole big team by pressing push-to-talk on TeamSpeak.

That breaks for two kinds of players:
- Some can't install TeamSpeak, because its phone app is blocked in their country.
- Some can't use push-to-talk in game. They leave an open mic instead, which floods the big team with chatter.

Discord has no whisper, and a bot can't see key presses. So we build:
1. **A relay bot.** One bot account sits in each small VC. While a player broadcasts, the bot copies that player's Discord audio into the other VCs of their big team. Everyone hears big-team calls inside their own VC, with no setup.
2. **A PC hotkey app** for Windows and macOS. Holding a key tells the bot "broadcast me". The app only sends signals; the audio always comes from Discord.

Phone players have no app, so they can't trigger a broadcast. They hear every broadcast in their own team channel and talk in that channel (decision: the voice keyword was built, then removed entirely).

The outcome: the whole community runs on Discord for team comms. Small-team chatter stays in the small team. Anyone can reach their 15. The two shot callers keep using TeamSpeak to talk to each other (your decision).

**Settled in the interview**
- **Voice and teams.** Everyone uses Discord voice activity, and the PC push-to-talk key is only for broadcasting to the 15. Your team is set by the VC you're in. Several broadcasters at once are mixed together. No tones. A 300 ms pre-roll buffer catches the first word.
- **Setup.** Admins use slash commands. One Discord server. Local git only, with lean conventions.
- **Hosting.** The bot runs on the community's existing home server, which already hosts TeamSpeak and websites. The app connects to a stable address (e.g. `wss://bot.<domain>`) through the server's existing HTTPS reverse proxy. No tunnel.
- **Linking.** Players link the app **once** by either pasting a `/connect` code or signing in with Discord. The app then keeps a long-lived token, so there's nothing to do on event nights.
- **Stack and app.** .NET everywhere, starting with a voice-library test. The app is unsigned, installed with Velopack, and updated manually for now. English UI. Inputs are keyboard, mouse side buttons and controller.
- **Shot callers.** They get one extra: an **open-mic toggle**, which broadcasts to their own big team without holding a key.
- **Dropped.** All-call and caller-to-caller keys, since shot callers use TeamSpeak for that.
- **Confirmed: v1 has the PC hotkey app only.** Discord gives bots no key presses. Its Priority Speaker key works only in push-to-talk mode, which would end voice activity for small teams.

## Approach

### Solution layout (the repo root, `Kzg.slnx`, .NET 10)

| Project | Deps | Owns |
|---|---|---|
| `src/Kzg.Protocol` | none | WebSocket message types and the connect-code codec. Shared by bot and app. |
| `src/Kzg.Core` | Protocol | Routing engine, jitter/pre-roll buffer, PCM mixer, config model and store, pairing and device tokens, `ITimeSource`, and all interfaces. No Discord, UI or native deps. 80% line-coverage floor. |
| `src/Kzg.Audio` | Concentus | Opus encode/decode, downmix, 48k-to-16k resampler. |
| `src/Kzg.Discord` | library picked in Step 0 | `IVoiceGateway` adapter, presence tracking, slash commands, OAuth code exchange. The only project that changes if the library is swapped. |
| `src/Kzg.Bot` | all above, ASP.NET Core | Host exe: audio engine thread, Kestrel control server (`/ws`, `/auth/*`, `/healthz`), redacting logger. |
| `src/Kzg.App` | Avalonia, Protocol | UI, WebSocket client, settings. |
| `src/Kzg.App.Input` | SharpHook, SDL3 | Keyboard/mouse/controller input sources and the binding matcher. |
| `spike/` | candidate libraries | Throwaway Step 0 code, not in the solution. |

Tests go in `tests/<Project>.Tests`, using xUnit and `FakeTimeSource` (starts at 5h, `Advance(by)`). The gate is `scripts/check.sh`: build (warnings as errors is the type check), `dotnet format --verify-no-changes`, then tests with coverage.

### Patterns copied from Gengfu (the separate Gengfu repo, `orwir/gengfu`)
- `scripts/check.sh`, `scripts/dotnet.sh`, `.editorconfig`, `Directory.Build.props` (single `<Version>`, Nullable, TreatWarningsAsErrors), `global.json` (SDK 10.0.100 latestFeature).
- `src/Gengfu.Core/ITimeSource.cs` + `SystemTimeSource.cs`; the test `FakeTimeSource`.
- `src/Gengfu.Core/Settings/SettingsStore.cs`: JSON with comments, strict validation, `SettingsLoad` result union. Copy the pattern for `config.json`.
- `src/Gengfu.TeamSpeak/VoiceSender.cs`: frame pacing at `start + 20ms*i - lead`, with injected time and sleep. Reuse it for the engine tick.
- Concentus Opus at 48 kHz stereo with 20 ms frames (ADR-0005).

### Routing rules (pure, single-threaded, `Tick(now)` gives a `TickPlan`)
- Sessions are keyed by `(source, action)`. The actions are `Group` (hold key) and `OpenMic` (shot caller latch).
- Targets are every bound VC of the source user's **current** group, minus the user's own VC. They are recomputed every tick, so a mid-broadcast move retargets at once. Moving to an unbound VC gives no targets and the `notInTeamVc` warning.
- A source is never routed into a VC its user is in.
- `OpenMic` is allowed only for the configured shot caller while they sit in their own group's VCs. Clearing the caller or leaving the group ends it.
- When the app disconnects, or no heartbeat arrives for 6 s, that user's app sessions end. Each heartbeat carries the held keys, so a lost key-up heals itself. Holds are capped at 120 s.

### Audio pipeline
- **Receive.** Library threads only enqueue `OpusPacket(user, seq, rtpTs, data, arrival)`. Packets from our own bot accounts are dropped.
- **Per-user jitter + pre-roll buffer.** Holds 1.2 s of frames. Playout delay starts at 60 ms, grows up to 240 ms on underruns or late bursts (sized to the burst), and shrinks back after 10 s with none. Opus packet-loss concealment covers up to 3 frames. A new session starts reading 300 ms back. A gap in speech, or lag beyond pre-roll + 60 ms, snaps the read cursor back to live.
- **Mixer per destination VC, 20 ms tick.**
  - One source: pass its Opus through unchanged.
  - Two or more: sum, apply a soft limiter, then encode with that destination's encoder (Concentus 64 kbps VOIP).
- **Engine thread** (AboveNormal priority). Each tick: drain commands, fill buffers, decode, run routing, mix and encode, send. Frames go out 40 ms ahead. Routing state has no locks.
- **Latency target:** added p95 ≤ 300 ms. A key press reaches the server in roughly 20–80 ms, and the pre-roll covers that.

### Linking the app (done once per PC)
- **Option A, `/connect` code.**
  1. The player runs `/connect` in Discord and gets a private code: `KZG1.<base64url(host)>.<base64url(32 random bytes)>`.
  2. They paste it into the app. The code carries the server address, so there's nothing else to type.
  3. The code expires after 10 min and works once.
- **Option B, "Sign in with Discord".**
  1. The app holds the server address. It's prefilled in the build and can be edited in settings.
  2. The app picks a random `loginId`, connects to `/ws` with `hello{loginId}`, and opens the browser at `https://<host>/auth/start?login=<loginId>`.
  3. The bot redirects to Discord's sign-in page. It asks only for the `identify` scope, with `state` set to the `loginId`.
  4. Discord returns to `/auth/callback`. The bot exchanges the code using the client secret, which stays on the server, and reads the user ID.
  5. The bot checks the user is a member of the guild. The waiting app then gets its `welcome` over the WebSocket.
  6. `loginId` expires after 10 min and works once.
- **Both options end the same way:** the bot issues a **device token** (32 random bytes) bound to that Discord user.
  - The app saves it in its settings file.
  - The server stores only `SHA256(token)` in `devices.json`, so the token survives bot restarts.
  - It expires after 90 days without use.
  - Admins revoke it with `/gvg revoke user`. A player can also unlink from the app.

### PC app: connection status always visible
- **One large status badge** at the top of the window, with a matching tray/menu-bar icon color:
  - **Connected** (green): shows your Discord name, your VC and team, and round-trip time in ms.
  - **Reconnecting** (amber): network blip, retrying with the device token. Shows seconds since the connection dropped.
  - **Disconnected** (red): a plain next step, e.g. "Server unreachable, retrying" or "Link expired. Run /connect or sign in again."
  - **Broadcasting** (blue, while the key is held): shows which VCs you reach.
- **Warnings in words, under the badge:**
  - "You're not in a team voice channel".
  - "Bot is down in Attack 2".
  - "Windows: run as administrator if keys don't work in game".
- **Color is never the only signal.** Every state also has a text label and an icon shape.
- Optional sound or desktop notification when the state changes to Disconnected.

### Data contracts

```csharp
readonly record struct UserId(ulong V); readonly record struct ChannelId(ulong V);
enum Group { Attack, Defense }
enum BroadcastAction { Group, OpenMic }
enum TriggerKind { AppKey, Synthetic }
readonly record struct SourceId(string V);   // "user:<id>" | "tts:<name>" (phase 2)
record OpusPacket(UserId User, ushort Seq, uint RtpTs, ReadOnlyMemory<byte> Data, TimeSpan Arrival);
interface IVoiceGateway { IReadOnlyList<IVoiceChannelLink> Links { get; } event Action<UserId, ChannelId?> PresenceChanged; }
interface IVoiceChannelLink { ulong BotUserId { get; } ChannelId Channel { get; } bool Ready { get; }
  event Action<OpusPacket> PacketReceived; void Send(ReadOnlyMemory<byte> opus); void SetSpeaking(bool on); }
interface IBroadcastTrigger { event Action<BroadcastEdge> Edge; } // BroadcastEdge(UserId, BroadcastAction, bool On, TriggerKind, TimeSpan At)
interface IAudioSource { SourceId Id { get; } TargetSpec Targets { get; } FrameRead Read(long tick); } // TargetSpec: Users | Groups | Channels
interface IOpusCodecFactory { IOpusDecoder Decoder(); IOpusEncoder Encoder(int bitrate); }
```

In phase 2, The jungle calls plug in as an `IAudioSource` with `Targets = Groups{...}`.

**WebSocket `/ws`** (JSON text, `{"v":1,"type":...}`, max 4 KB, IDs as strings, `wss://` only)

| Direction | Type | Fields |
|---|---|---|
| app to bot | `hello` | exactly one of `code`, `deviceToken`, `loginId`; plus `appVersion`, `installId:guid` |
| app to bot | `press` | `action:"group"`, `down:bool`, `seq:long` |
| app to bot | `openMic` | `on:bool`, `seq:long` |
| app to bot | `heartbeat` | `seq`, `held:action[]`, `openMic:bool`, `sentAtMs:long` (every 2 s) |
| app to bot | `unlink` | none (revokes this device token) |
| bot to app | `welcome` | `userId`, `displayName`, `callerOf:"attack"\|"defense"\|null`, `heartbeatMs:2000`, `timeoutMs:6000`, `deviceToken?` (only when newly issued), `serverVersion` |
| bot to app | `status` | `channelName?`, `group?`, `active:action[]`, `openMic`, `targets:string[]`, `warnings:("notInTeamVc"\|"notCaller"\|"botDown")[]` (sent on change, max 10/s) |
| bot to app | `heartbeatAck` | `seq`, `sentAtMs` |
| bot to app | `error` | `code:"badCode"\|"expired"\|"used"\|"revoked"\|"notMember"\|"notCaller"\|"versionMismatch"`, `message` |

**HTTP:** `GET /auth/start?login=`, `GET /auth/callback`, `GET /healthz`. The callback page just says "Signed in, return to the app".

**`config.json`** (per-OS app-data folder, `--data-dir` overrides):
```json
{ "schemaVersion": 1, "guildId": "", "adminRoleId": null, "publicHost": "bot.example.org",
  "listen": "127.0.0.1:7878", "prerollMs": 300, "maxHoldMs": 120000, "deviceTokenDays": 90,
  "oauth": { "enabled": true, "clientId": "" },
  "groups": { "attack": { "channels": [], "shotCallerUserId": null }, "defense": { "channels": [], "shotCallerUserId": null } },
  "bots": [ { "tokenRef": "bot1", "botUserId": "", "channelId": "", "primary": true } ] }
```
- `secrets.json` holds the bot tokens and the `oauthClientSecret` (chmod 600 on Unix). `KZG_SECRET_<name>` env vars override entries.
- `devices.json` holds `{tokenHash, userId, installId, issuedAt, lastSeenAt}`, with atomic writes.
- Validation: a channel is in at most 1 group, a bot has at most 1 channel, exactly 1 bot is primary, and every `tokenRef` resolves.

**Slash commands** (primary bot, guild-scoped, Manage Server permission unless noted):
- `/gvg bind`, `/gvg unbind`, `/gvg bot assign`, `/gvg caller set|clear`
- `/gvg config show|set` (output redacted)
- `/gvg start|stop`, `/gvg status`
- `/gvg revoke user`
- `/connect` (any member, ephemeral reply)

### Security
- **Secrets.** All codes, `loginId`s and device tokens are 256-bit random. The server stores only their hashes and compares in constant time.
- **Lifetimes.** Codes and `loginId`s expire after 10 min and work once. Device tokens are bound to one Discord user and expire after 90 days idle. Revoking takes effect immediately and closes that user's live sessions.
- **Discord sign-in.**
  - `identify` scope only. The `state` parameter must match a pending `loginId`.
  - The client secret never leaves the server.
  - The user must be a guild member.
- **No rate limits.** Secrets this size can't be guessed. The only connection guards are a 4 KB message cap and a rule that `hello` must arrive within 5 s.
- **Server-side checks.** Caller rights are checked on the server for every edge; nothing the app claims is trusted.
- **Logs.** Bot tokens, the client secret, codes and device tokens are never logged; a redacting log enricher masks them, and logs show only hash prefixes.
- **Network.** Kestrel listens on loopback behind the existing HTTPS reverse proxy. The proxy must pass WebSocket upgrades.
- **Input hooks** only listen; they never inject input.

## Where things run

The Discord app can't be installed on this machine, but the bot doesn't need it: it talks to Discord's servers directly. `https://discord.com/api/v10/gateway` answers 200 from here. So almost all work, Discord tests included, happens in this session.

- **Bots stand in for human testers.** Extra test bot accounts (`tester-1..3`) join VCs and **play known audio clips** as speakers. A `listener` test bot in each other VC **records what it hears**. A test client sends key presses over the WebSocket like the PC app.
  - Relay, routing, pre-roll and latency are all checked automatically from those recordings.
  - These checks live in `tools/Kzg.Harness/` and `tests/Kzg.E2E/`, and they don't block `scripts/check.sh` when Discord or the secrets are absent.
- **Offline simulator:** a `FakeVoiceGateway` feeds recorded Opus clips through the full engine with no network. Engine-level tests run anywhere.
- **The PC app** is built and tested here on macOS, against the bot running locally.
- **You do, once, before step 0a** (any browser; the Developer Portal is a website). 0a is the voice-library test, the first implementation step, and it's the only early step that talks to Discord:
  - Create a Discord test server.
  - Create 5 bot accounts for 0a: 2 relay, 2 tester, 1 listener.
  - Put the tokens in `secrets.json` on this machine. Never paste them in chat.
  - Steps 0b, 0c and 1–6 need no Discord setup, so they start right away. If your setup comes later, only 0a waits.
  - Before step 9 (test harness), add the rest: 6 relay, 3 tester and 6 listener bots in total.
- **You do, at the end, on your other machine (step 14 only):** the checks only real people and devices can do:
  - how the relay sounds in a real Discord client and on a phone;
  - Windows hotkeys under an elevated game;
  - the OAuth redirect and reverse proxy on the home server;
  - installing the app.
- **Open risk:** Discord voice also needs UDP. If this network blocks Discord's voice UDP, step 0a finds out first; the fallback is running the bot on the home server.

## Discord setup checklist (you, on your personal laptop)

Do part A, part B for the first 5 bots and part D before step 0a. Parts C and E come later. It takes about 30 minutes.

**A. Test server** (in the Discord app)
1. Click **+** (Add a Server), then **Create My Own**, then **For me and my friends**. Name it `KZG GvG Test`.
2. Create a category `Attack` with voice channels `Attack 1`, `Attack 2` and `Attack 3`.
3. Create a category `Defense` with voice channels `Defense 1`, `Defense 2` and `Defense 3`.
   - Step 0a only uses `Attack 1` and `Attack 2`; create them all now anyway.
4. Make sure `@everyone` has **Use Voice Activity**:
   - Open **Server Settings**, then **Roles**, then **@everyone**, then **Permissions**.
   - It's on by default.

**B. Bot accounts.** Repeat for each bot at https://discord.com/developers/applications.
- **Names:**
  - For 0a: `KZG Relay 1`, `KZG Relay 2`, `KZG Tester 1`, `KZG Tester 2`, `KZG Listener 1`.
  - Later, before step 9: `KZG Relay 3`–`6`, `KZG Tester 3`, `KZG Listener 2`–`6`.
1. Click **New Application**, enter the name, and accept the terms.
2. Open the **Installation** tab. Set **Install Link** to **None** and save.
   - This must come before step 3, or Discord refuses to make the bot private.
3. Open the **Bot** tab:
   - Turn **Public Bot** off, so nobody else can add it.
   - Under Privileged Gateway Intents, turn on **Server Members Intent** for `KZG Relay 1` only. Leave all intents off on the others.
   - Click **Reset Token** and copy the token. It's shown only once.
   - Save it straight into your password manager under the bot's name.
   - **The token is a password: never post it in chat, email or a screenshot.**
4. Open **OAuth2**, then **URL Generator**:
   - **Scopes:** `bot` for every bot, plus `applications.commands` for `KZG Relay 1` only.
   - **Relay bots:** View Channels, Connect, Speak, Use Voice Activity, Move Members.
   - **Tester bots:** View Channels, Connect, Speak, Use Voice Activity.
   - **Listener bots:** View Channels, Connect.
5. Open the generated URL in your browser, pick `KZG GvG Test`, and click **Authorize**. The bot now appears offline in the member list.

**C. Discord sign-in.** Needed before step 14; do it on `KZG Relay 1` only.
1. Open **OAuth2**, then **Redirects**, and add `https://bot.<your-domain>/auth/callback`. Save.
2. On the same page, click **Reset Secret** under Client Secret. Save it in your password manager as `Broadcast oauth secret`.

**D. Hand the tokens to this machine.** Never through chat.
1. After step 1 (scaffold) has run, open `.data/secrets.json` in the repo on this machine. It's git-ignored, and an example file sits next to it.
2. Fill it from your password manager:
   ```json
   { "relay1": "", "relay2": "", "tester1": "", "tester2": "", "listener1": "" }
   ```
   - Add `relay3`–`relay6`, `tester3` and `listener2`–`listener6` later, plus `oauthClientSecret` after part C.
3. Tell me in chat only "secrets are in". The bot finds the server and channels itself, so no IDs are needed.

**E. People and devices for step 14.** Arrange these later.
- 2–3 friends, or your own alt accounts, joined to the test server.
- At least one phone with Discord installed.
- A Windows PC with the game installed.

## Steps

Roles are roster agents. "check" means `scripts/check.sh` is green.

| # | Step | Role | Owns | Depends | Accept |
|---|---|---|---|---|---|
| 0a | Voice library spike: NetCord, then Discord.Net, then DisCatSharp, half a day each. Runs here; tester bots speak and a listener bot records. | specialist | `spike/dave/` | none | First: voice UDP works from this network. Then: two bots in one process in two VCs with E2EE on. Per-user audio with correct user IDs for 3 speakers over 10 min, under 1% loss. Clean A-to-B relay. Survives users joining or leaving and a reconnect. Added p95 ≤ 250 ms. libdave loads on win-x64, osx-arm64 and linux-x64. **If no library passes, switch the bot to Go (disgo + godave)**, keeping these contracts. |
| 0b | Keyword benchmark (dropped: the keyword was removed) | engineer | `spike/kws/` | none | CPU per stream; recall over 100 utterances; false triggers over 1 h of chatter |
| 0c | Input spike (macOS here; the Windows build is checked by you in step 14) | engineer | `spike/input/` | none | Side buttons through SharpHook; SDL3 background controller input; macOS permission prompts. Ships a Windows test build with a checklist for the elevated-window case. |
| 1 | Scaffold: git init, copied conventions, empty projects, README, git-ignored `.data/` with `secrets.example.json` (dev data dir) | task-worker | repo root, `scripts/`, `src/*/*.csproj`, `tests/*/*.csproj`, `.data/` | none | check passes; `git check-ignore .data/secrets.json` succeeds |
| 2 | Contracts | specialist | `src/Kzg.Protocol/`, `src/Kzg.Core/Abstractions/`, `docs/contracts.md` | 1 | JSON round-trips; `hello` with zero or two credentials rejected; malformed, expired and used codes rejected |
| 3 | Config store | engineer | `src/Kzg.Core/Config/` | 2 | validation rules, atomic write, check |
| 4 | Routing | specialist | `src/Kzg.Core/Routing/` | 2 | every rule above tested, incl. moves, caller cleared, heartbeat timeout, stuck key, never routed back into own VC |
| 5 | Jitter buffer + mixer | specialist | `src/Kzg.Core/Audio/` | 2 | reorder, loss and concealment, buffer growth and shrink, pre-roll, catch-up, passthrough, clipping (fake codec) |
| 6 | Codec + resampler | engineer | `src/Kzg.Audio/` | 2 | sine round-trip SNR, resampler response |
| 7 | Discord adapter + slash commands + OAuth exchange | engineer | `src/Kzg.Discord/` | 0a, 2, 3 | joins bound VCs, presence events, commands and Discord sign-in work on the test server |
| 8 | Control server, pairing, device tokens, `/auth` endpoints | specialist | `src/Kzg.Bot/Control/`, `src/Kzg.Core/Pairing/` | 2, 3 | code single use + expiry; `loginId` state match + expiry + guild-member check; device token survives restart, expires, revoke closes live session; 4 KB cap; 5 s hello timeout |
| 9 | Test harness: tester/listener bots, scripted scenarios, recording analysis (click-train latency, who-heard-what matrix), `FakeVoiceGateway` | engineer | `tools/Kzg.Harness/`, `tests/Kzg.E2E/`, `src/Kzg.Core/Testing/` | 0a, 2 | a scripted A1-to-A2 relay passes on the test server; the offline simulator runs in check |
| 10 | Keyword spotter (removed) | engineer | none | none | the voice keyword is gone: no `src/Kzg.Keyword`, no models, no sherpa-onnx or ONNX Runtime |
| 11 | Bot host composition | engineer | `src/Kzg.Bot/Program.cs`, `Engine/`, `Logging/` | 4–10 | harness scenarios pass on the test server (routing matrix, pre-roll, latency, a relay moved mid-broadcast); log redaction test |
| 12 | PC app | engineer | `src/Kzg.App/`, `src/Kzg.App.Input/` | 0c, 2 | link by code or Discord sign-in; keyboard/mouse via SharpHook in the app process and the controller via SDL3 in a helper child process (same exe, helper argument, events over stdout), never `SDL_Init` in the hook's process, because on macOS it stops the key hook (step 0c, `spike/input/README.md`); fallback if that fails: an input mode setting, "Keyboard & mouse" or "Controller", one at a time; bindings for the chosen mode; no synthetic input in tests unless the user is warned first and says go; open-mic toggle for callers; the status badge and warnings above; permission help; Windows run-as-admin option. Tests: binding matcher, state transitions Connected/Reconnecting/Disconnected/Broadcasting with `FakeTimeSource` |
| 12b | Listen-only key input | engineer | `src/Kzg.App.Input/` | 12 | The OS never waits for the app, so a slow app can't lag the computer or lose the hook. macOS: a listen-only event tap (`kCGEventTapOptionListenOnly`) instead of libuiohook's active tap; the watchdog re-enables a tap that macOS disables. Windows: Raw Input with `RIDEV_INPUTSINK` for keyboard and mouse instead of the low-level hook, which Windows removes silently after its timeout. If Raw Input misses keys under an elevated game, keep the low-level hook, reinstall it every few seconds, and detect a dead hook on Windows too. Bindings, side buttons and the controller helper keep working; no synthetic input in tests unless the user is warned first and says go. Tests: the matcher fed from each source; watchdog recovery. |
| 13 | Packaging + docs | engineer | `packaging/`, `docs/guides/`, `README.md` | 11, 12 | Velopack installers for win-x64, osx-arm64 and osx-x64; self-contained bot per OS with native libs. Host guide covers bot accounts, permissions (Connect/Speak/Move Members), the OAuth redirect, and nginx/Caddy snippets with WebSocket upgrade. Unsigned-install guide. |
| 14 | End-to-end verification: automated parts here, human parts by you on the other machine | specialist, with you and community testers | none (read-only) | 13 | the Verification section below |

**Parallel:** 0a, 0b, 0c and 1 run together. After step 2: 3, 4, 5, 6 and 12 run together (separate folders). Steps 7, 8, 9, 10, 11 and 13 follow their dependencies.

## Ease of use (added after step 13)

Approved by the user so that people without a tech background can host and use it.

**Host and admin** (bot engineer: src/Kzg.Bot, src/Kzg.Discord, src/Kzg.Core/Config, packaging/, docs/guides/host.md, README.md)
1. Setup page: on first start the bot serves a local setup page. Paste the bot tokens once; it shows an invite button per bot with the right permissions, checks each bot joined, then points to `/gvg setup`. No hand-edited secrets.json or config.json.
2. `/gvg setup` in Discord: pick the Attack channels, the Defense channels and the two shot callers; relay bots are assigned to channels automatically.
3. One-file hosting: a Docker compose bundle with the bot plus Caddy for automatic HTTPS. The host fills in the domain and runs one command.
4. Problems posted to an admin channel the admin picks (for example a relay bot offline).
5. Discord sign-in is optional; `/connect` codes need no OAuth setup, and the guides say so.
6. Mac hosting, Apple Silicon only (no Intel Macs): a self-contained osx-arm64 bot package; a launchd service that starts the bot at login and restarts it after a crash; the bot keeps the Mac awake while it runs (sleep would drop every relay bot); a Mac section in the host guide.

**Players** (app engineer: src/Kzg.App*, tests/Kzg.App*, docs/guides/unsigned-install.md)
1. "Open at login": a Settings switch, off by default.
2. Automatic updates through Velopack, with the update feed configurable; it stays inactive until the GitHub repo exists.
3. macOS Input Monitoring: the app checks it, shows a warning with a button that opens the exact System Settings page, and restarts itself once access is granted.
4. Stable Mac signing (free): release builds are signed with one self-made code-signing certificate that the maintainer keeps, so macOS keeps Input Monitoring across updates. `packaging/pack-app.sh` takes the identity from an environment variable and signs only when it's set; dev builds stay ad-hoc. The certificate is exported as a password-protected .p12, backed up, never committed, and usable from other Macs or a CI secret. Not notarized, so the first-install warning stays. Step 14 checks that an update over an older version keeps the permission.

## GitHub-ready repo and AI handover (added after step 13)

(repo worker: .github/, AGENTS.md, CLAUDE.md, CONTRIBUTING.md, .claude/, docs/plan.md, docs/decisions.md, .gitignore)
1. The repo can be pushed to a new GitHub repo at any time: complete .gitignore, no secrets or build output in the tree, a CI workflow running `scripts/check.sh` on macOS, Windows and Linux with least-privilege permissions, and a release workflow that does nothing until secrets and the repo exist.
2. Another machine with Claude can pick up the work: AGENTS.md (with CLAUDE.md pointing to it) holds the working rules, the plan moves into docs/plan.md, the decisions made so far go in docs/decisions.md, and setup and live-test instructions say what each machine needs.

## Verification
Items 2–5, 7 and 8 run automatically here with the harness bots. Items 1, 6 and 9–11, plus a listening pass of 2–5 with real people, are done by you on the other machine.

1. **Test server.** 6 VCs (2 groups of 3), 6 relay bots, and the tester and listener bots. For the human pass: real Discord clients and 2 phones.
2. **Routing.** Test each action from each VC and record who hears what. Pass: no one hears themselves, and other groups hear nothing.
3. **Latency.** Play a click train in A1. Record a direct listener in A1 and a relay listener in A2, then compare. Pass: added p95 ≤ 300 ms.
4. **Pre-roll.** Press the key 200 ms after speech starts. Pass: the first word arrives intact.
5. **Keyword.** Removed. Phone players hear all broadcasts and talk in their own team channel.
6. **Linking.**
   - Pair once by code and once by Discord sign-in.
   - Restart the bot; both apps reconnect on their own.
   - A non-member's sign-in is refused.
   - A reused code is rejected.
   - Revoke ends a live session.
7. **Robustness.**
   - A move mid-broadcast retargets.
   - A relay bot moved mid-broadcast by a bot with Move Members: nothing reaches the wrong channel, no frame is heard twice, and it rejoins its own channel within about a second and carries on.
   - Clearing the caller ends their open mic.
   - Unplugging the app's network stops the relay within 6 s, and the badge shows Reconnecting, then Connected.
8. **Load.** 30 speakers and 6 broadcasters at once. Host CPU under 30% on the home server.
9. **Windows.** Hotkey while an elevated window has focus, both non-elevated and run as administrator. The hotkey still works after the PC is under full CPU load for 5 min.
10. **macOS.** Fresh account permission flow, side buttons, controller in the background.
11. **Install.** Unsigned install on both OSes, following the guide.

## Risks
- **Discord voice encryption in .NET.** Not yet proven for per-user receive with several bots. Step 0a decides; the fallback is Go.
- **VC slots.** Each bot takes a slot in its VC. Give bots Move Members, or set the limit to 6.
- **Moved relay bots.** A mover bot (e.g. fmove) or a drag can move a relay bot. The bot stops sending at once, so nothing reaches the wrong channel, and rejoins its own channel within about a second. A move never sticks; `/gvg bot assign` is the only way to change a bot's channel.
- **Speaker identity.** Relayed voices all come from the bot account, so listeners can't see who is speaking or set per-person volume. Not in v1.
- **Phone players.** They can't trigger a broadcast. They hear all broadcasts and talk in their own team channel.
- **Home server.** If the server or its internet drops, every app shows Disconnected and reconnects by itself when it's back. The reverse proxy must allow WebSocket upgrades and long-lived connections; the 2 s heartbeat keeps the connection from idling out.
- **Windows input.** Windows blocks a normal program from seeing key presses sent to a program running as administrator. Many games with anti-cheat run that way, which may also be why some players' push-to-talk fails in game. Fix: a "Run as administrator" option in our app. The app only watches keys and never presses them, like Discord's push-to-talk. Whether this game's anti-cheat objects is untested.
- **Home server OS** is still open: Windows, Linux or an Apple Silicon Mac (Intel Macs are not supported). On Linux, `libdave.so` needs glibc 2.38 or newer, so Debian 12 and Ubuntu 22.04 can't run the bot directly. Step 13 ships the bot for win-x64 and linux-x64, plus a Docker image on a new enough base (e.g. Ubuntu 24.04) for older Linux hosts; Ease of use host item 6 adds osx-arm64.
- **Phase 2** (jungle calls) is not in this plan. The `IAudioSource` and `TargetSpec` design leaves room for it.
