# Contracts

This page is the reference for what the bot and the PC app say to each other over `/ws`, the connect-code format, and the engine's C# interfaces in `Kzg.Core`. It is for anyone writing the control server, the app's client or an engine part. It also covers the setup page and the Discord adapter. It doesn't cover routing rules, the audio pipeline or config; those live with their own code.

The code is the source of truth: `src/Kzg.Protocol` for the wire, `src/Kzg.Core/Abstractions` for the engine. This page explains the rules the types can't show.

## The `/ws` wire

### Envelope

Every message is one JSON text message: an object with `"v": 2` and a `"type"`, beside the type's own fields. The codec writes `v` and `type` first, but a reader takes the fields in any order.

A message with any other `v` is reported as `UnsupportedVersion` before its fields are read, so the bot can answer `error{code:"versionMismatch"}` even when a newer app sends fields this build doesn't know.

The bot reads only app-to-bot types and the app reads only bot-to-app types. A message of the other direction is malformed.

### Strict reading

`MessageCodec.ParseClient` and `ParseServer` refuse a message, as `TooLarge` or `Malformed`, when it:

- is over 4096 bytes of UTF-8, or isn't valid UTF-8;
- is not exactly one JSON object, or holds comments, trailing commas or nesting deeper than 4;
- lacks `v` or `type`, has either twice, or has a non-integer `v` or a non-string `type`;
- has an unknown `type` (names are case-sensitive);
- lacks a required field, has a field the type doesn't define, or has any field twice;
- has a field of the wrong JSON kind: a quoted number, a quoted `true`, a fractional `seq`, or `null` where the field isn't nullable;
- spells an enum any other way than its wire name, including other casing or a number;
- breaks one of the type's own rules in the table below.

A `Malformed` reason names fields only, never their values, so logging it can't leak a credential.

`MessageCodec.Serialize` checks the same rules and throws when a message breaks them or would exceed 4096 bytes. Whatever one side writes, the other reads.

### Size cap

`WireProtocol.MaxMessageBytes` is 4096. `MessageAssembler` gathers one WebSocket message from its fragments into a fixed 4097-byte buffer. A receive loop reads into `Remaining` and calls `Advance(count, endOfMessage)`; the result is `Incomplete`, `Complete` or `TooLarge`. The spare byte proves a message too large even when its last fragment is empty. On `TooLarge` the server stops reading and closes the connection.

Names stay UTF-8 on the wire, not `\uXXXX` escapes, so a Cyrillic channel name costs 2 bytes a letter, not 6.

### Messages

| Direction | `type` | Fields | Rules beyond the JSON types |
| --- | --- | --- | --- |
| app to bot | `hello` | `appVersion: string`, `installId: guid`, and one of `code`, `deviceToken`, `loginId` | Exactly one credential is present and not null. `code` is the whole connect code as pasted. `deviceToken` and `loginId` are secrets in the form below. `appVersion` is 1 to 64 printable characters. `installId` isn't the empty GUID. |
| app to bot | `press` | `action`, `down: bool`, `seq: long` | `action` is a held key, `"group"` or `"allTeams"`; open mic has its own message. `seq` ≥ 0, one sequence for both keys. `group` reaches the other bound VCs of the player's group; `allTeams` reaches every bound VC of both groups except the player's own. Either needs the player in a team VC. |
| app to bot | `openMic` | `on: bool`, `seq: long` | `seq` ≥ 0. Any player may latch it; it reaches the player's own group and only while they sit in one of its VCs, else the bot turns it off. |
| app to bot | `heartbeat` | `seq: long`, `held: action[]`, `openMic: bool`, `sentAtMs: long` | `held` lists keys only, so `"group"` and `"allTeams"` each at most once and never `"openMic"`. `seq` and `sentAtMs` ≥ 0. |
| app to bot | `unlink` | none | Revokes the device token this connection signed in with. |
| bot to app | `welcome` | `userId: snowflake`, `displayName: string`, `heartbeatMs: int`, `timeoutMs: int`, `serverVersion: string`, optional `deviceToken` | `deviceToken` is present only when this hello issued one. `heartbeatMs` > 0 and `timeoutMs` > `heartbeatMs`; the bot sends 2000 and 6000. `displayName` is 1 to 100 printable characters, `serverVersion` 1 to 64. |
| bot to app | `status` | `active: action[]`, `openMic: bool`, `targets: string[]`, `warnings: warning[]`, optional `channelName`, optional `group`, optional `botDownIn: string[]` | `channelName` is absent outside voice; `group` is absent outside a team VC and never present without `channelName`. `openMic` is the latch as requested; `active` holds `"openMic"` only while it actually relays. `targets` are the display names of the VCs reached. `botDownIn` holds the display names of the VCs of the player's group whose relay bot isn't ready, and is empty when there are none; an app that gets none of it still has the `botDown` warning. No action, warning or `botDownIn` name appears twice. Names are 1 to 100 printable characters. Sent on change, at most 10 a second. |
| bot to app | `heartbeatAck` | `seq: long`, `sentAtMs: long` | Echoes the heartbeat's values; both ≥ 0. |
| bot to app | `error` | `code: errorCode`, `message: string` | `message` is for a person, at most 500 characters; the app acts on `code`. |

Optional fields are left out when they have no value. A reader also takes an optional field written as `null`.

### Wire names

| Type | Values |
| --- | --- |
| group | `attack`, `defense` |
| action | `group`, `allTeams`, `openMic` |
| warning | `notInTeamVc`, `botDown` |
| errorCode | `badCode`, `expired`, `used`, `revoked`, `notMember`, `versionMismatch` |

Shot callers are gone from the wire: `welcome` has no `callerOf`, and `notCaller` is neither a warning nor an error code. `v` is 2 from this change on, so an app or bot from before it (`v` 1) gets `error{code:"versionMismatch"}` and stops, instead of reconnecting on a malformed message.

### IDs

Discord IDs travel as decimal strings, because JavaScript-style JSON parsers lose digits of a 64-bit number. `Snowflake` accepts only the canonical form: 1 to 20 digits, no sign, no leading zero, not 0, at most `ulong.MaxValue`.

### Secrets

A `loginId`, a device token and a connect code's secret part are each 32 random bytes, written as unpadded base64url: 43 characters of `A-Z a-z 0-9 - _`. `WireSecret` accepts only the one spelling encoding produces, so padding, whitespace, `+`, `/` or stray bits in the last character are refused, and two spellings never name the same secret.

`HelloMessage`, `WelcomeMessage` and `ConnectCode` print which credential they carry, never its value, so a logged message reveals nothing.

Whether a secret is known, unexpired, unused or revoked is the bot's call, not the codec's.

## Close codes

The bot ends a connection with a WebSocket close frame. `CloseCodes` in `src/Kzg.Bot/Control/ControlOptions.cs` names them. The close reason is a short fixed phrase for logs; it never carries a value, and the app acts on the code, and on an `error` message's `code` when one came first.

| Code | `CloseCodes` | Reason the bot gives | Sent when | The app |
| --- | --- | --- | --- | --- |
| 1000 | `Normal` | `closed by the app`, `unlinked` | The app closed first, or sent `unlink`, which revokes its device token. | Its own doing, so nothing to answer. After an unlink it holds no token to reconnect with. |
| 1001 | `GoingAway` | `server shutting down` | The bot is stopping. | Reconnects with backoff. |
| 1003 | `Binary` | `text messages only` | A binary message. The protocol is JSON text only. | A bug in the app; reconnects. |
| 1007 | `Malformed` | `malformed message` | A message that isn't valid JSON, isn't an app-to-bot type, or breaks its type's rules. | A bug in the app; reconnects. |
| 1008 | `Policy` | `no hello`, `hello sent twice`, `no heartbeat`, `protocol version`, `sign-in refused`, `revoked` | Against the protocol's order or timing: no `hello` within 5 s, a second `hello`, silence past the 6 s timeout. Also after an `error` that refuses or ends a sign-in, such as `revoked` or `versionMismatch`. | Without a preceding `error`, reconnects. After an `error`, acts on its `code`: `expired`, `used`, `badCode`, `revoked` and `notMember` need the player to link again, `versionMismatch` needs an update. |
| 1009 | `TooLarge` | `messages are 4096 bytes at most` | A message over 4096 bytes. | A bug in the app; reconnects. |
| 1011 | `ServerError` | `couldn't check the sign-in; try again` | The bot couldn't check the sign-in, as when Discord is unreachable, or the check outlasted the 6 s timeout. Nothing is known to be wrong with the app. | Reconnects with backoff. |
| 4000 | `Replaced` | `signed in from another app` | The same player signed in from another app; the older connection is closed. | **Doesn't reconnect.** Two apps would otherwise take the connection from each other forever. It shows that this player is signed in elsewhere. |

The bot queues any `error` message before the close frame, so the app reads it first. Only the writer sends, so a close follows every message queued before it. After the bot's close, a peer has 2 s to answer the close handshake before the socket is dropped.

## Connect code

A connect code is `KZG1.<base64url(host)>.<base64url(32 bytes)>`, all three parts unpadded base64url in canonical form. The player pastes it into the app; the host tells the app where to connect, so there is nothing else to type.

The host is an ASCII DNS name (IDNs in punycode), an IPv4 address or a bracketed IPv6 address without a zone, optionally followed by `:port` with a port from 1 to 65535. It carries no scheme, user, path or query; the app always connects with `wss://<host>/ws`.

`ConnectCode.TryParse` accepts only the exact form `Format` writes, at most 512 characters. The app trims whitespace around a pasted code before parsing it; the codec doesn't. `ConnectCode.Generate(host)` makes a code with a fresh random secret.

## Rate limits (`Kzg.Bot.Http`)

The host is in every connect code, so the public routes are limited before any of their logic runs. `HttpLimits` holds the numbers (`BotSetup.Limits`); `RateLimits` applies them with ASP.NET's rate limiter. A route is told by its endpoint, not its path, so `/WS` and `/ws/` count as `/ws`.

| Route | Per client | In all |
| --- | --- | --- |
| `/ws` | 30 new connections, then one more every 2 s; 32 open at once | 1024 open at once |
| `/auth/start` | 10, then one more every 6 s | none |
| `/auth/callback` | 10, then one more every 6 s, apart from `/auth/start` | none |
| `/healthz` | none | none |

- **Open sockets.** The `/ws` count is held for the socket's whole life, so it bounds connections waiting on a Discord sign-in (up to 10 minutes) as well as signed-in ones. Kestrel also keeps at most 2048 TCP connections that aren't WebSockets; one past that is closed before a request is read.
- **Refused.** Over any limit the reply is `429` with `Retry-After` (seconds, when the bucket knows), the headers every reply carries and a plain-text line, before the WebSocket upgrade. The app treats it as a failed connect and backs off as usual. The log says at most once a minute how many were refused and the latest route, never the address.
- **Who the client is.** An IPv4 address, or the /64 of an IPv6 one. It is Kestrel's peer, unless the peer is in `trustedProxies` (config, read at startup: IPs or CIDR networks): then it is the last `X-Forwarded-For` hop, the one that proxy added, and earlier hops are ignored. Only `X-Forwarded-For` is read; scheme and host are not taken from headers. The default lists loopback, the private IPv4 ranges and `fc00::/7`, where a proxy on the same computer, LAN or Docker network comes from; never a public address. `[]` ignores the header from everyone. A request that carries the header from a peer not listed is limited as that peer, and the log says so once, because every app behind that proxy then shares one client's limits.

## Engine contracts (`Kzg.Core.Abstractions`)

`Kzg.Core` references `Kzg.Protocol`, never the reverse, so the wire has its own enums and `WireMapping` converts between them.

| Type | What it is |
| --- | --- |
| `UserId`, `ChannelId` | A Discord user or voice channel, by value. `TryParse` takes a snowflake; `ToWire()` writes one and refuses 0. |
| `Group` | `Attack` or `Defense`. |
| `BroadcastAction` | `Group` (the held team key), `AllTeams` (the held all-teams key) or `OpenMic` (any player's latch). Sessions are keyed by (`SourceId`, `BroadcastAction`); a player holding several relays once, to the widest reach among them. |
| `TriggerKind` | `AppKey` or `Synthetic` for a non-person source or the engine ending a session itself. |
| `SourceId` | `user:<id>` for a player, `tts:<name>` for a synthetic source. `ForUser`, `ForTts`, `TryGetUser`. |
| `OpusPacket` | One received packet: user, RTP sequence and timestamp, the Opus bytes, the arrival time from `ITimeSource` and, optionally, the SSRC of its stream (0 when unknown). Library threads only enqueue it. |
| `OpusFormat` | 48 kHz, stereo, 20 ms frames: 960 samples a channel, 1920 interleaved shorts. |
| `IVoiceGateway` | The relay bots' links, `PresenceChanged(user, channel?)` with null meaning the user left voice, and `LinkAdded` and `LinkRemoved`, which say that links change while the bot runs. |
| `IVoiceChannelLink` | One bot in one VC: `Ready`, `PacketReceived`, `Send(opus)`, `SetSpeaking`. Packets from our own bots are dropped by the adapter. |
| `IBroadcastTrigger`, `BroadcastEdge` | An edge is a claim (user, action, on, kind, time). Routing checks VCs itself on every edge. |
| `IAudioSource`, `TargetSpec`, `FrameRead` | A non-player source, read once per tick by the engine (`AudioEngine`'s `sources`). `TargetSpec` is `Users`, `Groups` or `Channels`; `RoutingEngine.Resolve(spec)` turns it into the bound VCs whose link is ready, with no own-VC exclusion because a source is not a player. `FrameRead` is `Silent`, a `Frame` of Opus, or `Missing` for the decoder to conceal. A source alone passes through as Opus unchanged; with a broadcasting player in the same VC the mixer sums both. A source that throws is logged, counted and read as `Silent`. |
| `IOpusCodecFactory`, `IOpusDecoder`, `IOpusEncoder` | A decoder per source with `Decode` and `Conceal`, an encoder per destination with `Encode`; all in `OpusFormat`. Each returns samples per channel or bytes written. |
| `ITimeSource`, `SystemTimeSource` | Monotonic time since an arbitrary origin. Tests use `FakeTimeSource`, which starts at 5 h and moves only on `Advance(by)`. |

### Links that change while the bot runs

`/gvg bind`, `unbind` and `bot assign` put relay bots into VCs and take them out, so the engine's links are not fixed when it is built. `IVoiceGateway.LinkAdded` and `LinkRemoved` carry this. They are additive: both have a default implementation that does nothing, so a gateway whose links never change, such as `FakeVoiceGateway`, needs no edit.

- **Added.** The link may not be `Ready` yet; the engine polls `Ready` as before, and a VC without a ready link is `botDown`. The engine subscribes before it reads `Links`, so a link that appears in between is seen by one or both paths and kept once. A link for a VC that already has one replaces it.
- **Removed.** The engine stops sending, stops listening to the link and tells routing the VC has no bot. A removal of a link that was already replaced leaves the replacement alone.
- **Threads.** Both are raised on any thread. The engine's handlers only enqueue.

## Streams

A player who moves to another VC, or leaves a VC and comes back, sends a stream with a new SSRC and sequence and timestamp numbers of its own. The engine reads a packet from another VC than the player's last, or with another SSRC, as a new stream: the player's jitter buffer and decoders start afresh and a relay under way carries on from the new stream's first packet. Without that the buffer takes the new numbers for late packets of the old stream and drops them. The frames still in the old buffer's playout delay are not carried over. The buffer keeps its delay: the new stream comes over the same network.

A client may number its packets across a pause either way: the timestamp keeps pace with the clock, or it goes on from the last number. The engine relays both. A packet that comes more than `EngineOptions.TalkSpurtGap` (500 ms) later than its timestamp says, after the player's last, starts a new talk-spurt: the buffer and decoders start afresh and a relay under way carries on from that packet. Without it a client of the second kind would sit far behind the buffer's live position after a long pause and relay nothing. A pause shorter than that is left to the buffer, which plays late and catches up.

The buffer plays a relay out a delay behind the earliest each frame could have arrived. The delay starts at 60 ms. A packet later than the delay grows it to that packet's lateness in whole frames, up to 240 ms, and the relay goes back over the slots it passed without a frame since its last one, so a burst held back on the way plays late instead of being lost. Ten seconds after it last grew, the delay sizes down to the latest packet of the last ten seconds, 60 ms at the least. A relay at live when it shrinks plays on behind by the difference and catches up at the next gap in speech. A start takes nothing that arrived before it; a start at or before a stream's first packet takes the stream from its first frame, even when its first frames came in a burst.

## Checks against Discord

`tests/Kzg.Bot.Tests/Live/` holds the checks that need the test server (`KZG_LIVE=1`, bots in `.data/secrets.json`). `LiveRelayTests` is the first-burst check. `LiveScenarioTests` is the plan's verification list: the real bot host and engine, key presses sent over `/ws` by a test client that heartbeats like the PC app, two tester bots that speak known clips and six listener bots that record. It saves `.data/config.json` for the run and puts the file back as it was. `LiveMovedRelayTests` has a bot with Move Members drag a relay into another team's channel in the middle of a broadcast: it checks that nothing is heard in the wrong channel, that no frame is heard twice, and that the relay goes back and carries on. `OfflineScenarioTests` in `tests/Kzg.Bot.Tests/Host/` runs the same scenarios over the fake gateway in real time, so it is opt-in (`KZG_REALTIME=1`) and not part of `scripts/check.sh`: it runs the engine on its own thread against the wall clock and a busy machine can break its timing ranges. The same holds for `OfflineEndToEndTests` and `JungleClipsTests`. Everything else in the gate runs on fake clocks.

`tests/Kzg.Bot.Tests/Load/` holds the load test (plan verification 8), which `scripts/check.sh` skips unless `KZG_LOAD=1`: the real engine and Opus codec over the fake gateway with six bound VCs and thirty talkers, six of them broadcasting at once (two players on open mic, four on the group key). The talkers say real speech made with macOS `say`, with arrival jitter, loss and talk-spurt pauses. `KZG_LOAD_SECONDS` sets the length (default 300). It prints the engine thread's CPU, the process's CPU, tick lateness and duration, relay underruns and memory over time, and fails if the CPU is 30% of the machine or more or a tick starts more than 20 ms late. `AudioEngine.TickObserver` and `EngineStats.Concealed` exist for it. Every minute it also appends the working set, the managed heap, what the last GC left alive, the host's load average and memory pressure, and any stretch where the sampler did not run (the machine slept), to `.data/load/soak-*.log` as it happens, so an hour-long run can be watched and read against what else the machine was doing.

## Discord adapter (`Kzg.Discord`)

`DiscordGateway` is the one `IVoiceGateway` for a real run, behind `IDiscordRuntime`. `Kzg.Bot` builds it, starts it after the engine has subscribed, and signs every bot out when it stops. Tests of the host use a stand-in for `IDiscordRuntime`, so no test needs Discord.

- **Library.** NetCord 1.0.0-beta.30 with libdave pinned to 1.2.1.9. At start `DaveCheck` loads libdave and refuses to run if it exports `daveSessionAddRecognizedUser`, which only 1.3.0 and newer do: with 1.3.0 NetCord drops every voice frame without an error. The libdave package leaves its files where .NET doesn't look for them unless the build names a runtime, so `LibDaveLoader` also tries `runtimes/<os>-<arch>/native`.
- **Opus.** The adapter never touches Opus: packets go in and out as bytes, and the engine uses Concentus through `Kzg.Audio`.
- **Receive.** A packet is copied before it is queued, because NetCord reuses the buffer when its handler returns. The SSRC is mapped to a user with the voice client's own table. A packet from one of our relay bots, or from a stream whose user isn't known yet, is dropped and counted.
- **Send.** The link owns the RTP sequence number and timestamp. The timestamp moves 960 for every 20 ms of wall time, sent or not, so a pause shows as a jump. A burst ends with five `F8 FF FE` frames, 20 ms apart. A link that is not `Ready` drops the frame, because NetCord throws and drops silently during a DAVE transition.
- **Speaking.** Receivers place a stream from the speaking flag and drop what they can't place, so a player who joined after the bot loses the first frames of its first burst. The link announces itself: the flag goes up for 250 ms with no audio when the connection comes up or resumes, and again 1.5 s after the last player joined (`RelayLink.JoinSettle`). A burst that begins before the announcement, with a player who has not heard one, holds its first frames until the flag has been out for `RelayLink.FlagLead` (80 ms) and then sends them in order. Any other burst sends its first frame at once and raises the flag behind it. The flag goes down after the burst's five silence frames.
- **Presence.** The primary bot's guild snapshot sets the whole picture and voice-state updates follow it. A re-identify sends a new snapshot, which is diffed against what is known; a resume sends none. Our own relay bots are not players and raise nothing.
- **Names.** `ChannelNames` holds every channel's name from the snapshot and channel events, for `status.channelName` and `botDownIn`.
- **Links.** `ApplyAsync(config, running)` puts each bot with a `channelId` into that VC and takes the others out. A bot signs in to Discord the first time it is put in a VC; a lost voice connection is rejoined after a pause of 1 s that doubles up to 30 s.
- **Moved bots.** A relay bot always goes back. `LinkSupervisor` listens to the bot's own voice state; when Discord says it is in a channel other than its assigned one, or in none, the link stops being ready on that same call, on the gateway's thread, so the engine sends nothing more into the wrong channel and routing shows the VC as `botDown`. The supervisor then rejoins the assigned channel after 250 ms. A bot moved again within 30 s of its last rejoin waits twice as long, up to 2 s. A move never changes the assignment: only `/gvg bot assign` does.
- **Posting.** `IDiscordRuntime.PostAsync(channel, text)` sends a message as the primary bot with no mentions allowed, cut to Discord's 2000 characters. It returns false, and logs only the error's type, when Discord refuses (no View Channel or Send Messages there) or can't be reached; it never throws for that.
- **Names.** `RelayNames.Of` names a bot everywhere an admin reads it (replies, status, alerts, logs, the `/gvg bot assign` choices): "A1 Broadcaster" for the first channel in `groups.attack.channels`, "D2 Broadcaster" for the second in `groups.defense.channels`, and `<tokenRef> (spare)` or `<tokenRef> (no team)` for a bot with no channel or an unbound one. The name is worked out from the config each time, never stored. On every apply, and each time a bot joins voice, `BotNickname` sets the bot's server nickname to its team name (`PATCH /guilds/{guild}/members/@me`), only when it changed; a bot left without a team clears only a nickname this run set. A 403 leaves the relay running, marks `RelayBotState.NicknameRefused` for `/gvg status`, and is tried again at the next apply.
- **Commands.** `/gvg`, `/connect` and `/jungle` are registered for the one guild when the bot starts and when the admin role, the bot list or a bot's name changes. Each answer is ephemeral. Rights are checked by the bot on every call: Manage Server, Administrator, or the configured admin role. `/gvg bind` also puts a free relay bot into the channel, and `unbind` takes its bot out; `/gvg bot assign` without a channel takes a bot out of voice; `/gvg stop` takes all of them out until `/gvg start`. `/gvg setup` takes `attack-1` and `defense-1` (required voice channels), `attack-2`, `attack-3`, `defense-2`, `defense-3` and `channel` (a text channel for alerts): the teams become exactly those channels, `SetupPlans.AssignChannels` puts the bot whose setup field named a channel in it (`relay1` in the first Attack channel up to `relay6` in the third Defense one), keeps a bot already in a channel whose own bot is missing, fills the rest with the bots left in config order, and bots left over leave voice. It refuses a channel picked twice, a team with none, and more channels than bots. `/jungle` is open to every guild member (no default permission, and the handler routes it before the admin check, as `/connect`): subs `start`, `sync`, `pause`, `resume`, `stop`, `status`, `test`; `sync` takes a required string `time` of 4 to 5 characters, parsed as MM:SS by `JungleTime.TryParse`, and a bad value is refused before the bot is asked. Each answer is ephemeral. There is no `/gvg caller`. `/gvg alerts [channel]` sets or clears `alertsChannelId`. Both post a test message to a new alerts channel and say so when the post fails.
- **Alerts.** `AlertMonitor` (in `Kzg.Bot`) is looked at every 5 s: a bot assigned a channel while the relay runs, and not `Ready` in that channel for 30 s, is reported once, bots that fall together in one message, and again when it is back. A stopped relay or a bot taken out of voice is not watched. `RunMarker` keeps `.running` in the data folder while the bot runs; found at start, it means the last run never stopped cleanly, and that is posted too.
- **Sign in with Discord.** `DiscordIdentity` trades the code for the user with the client secret in an `Authorization: Basic` header, asks `/users/@me` with the access token, then revokes the token. A refused code gives no user; unreachable or refused credentials throw. Membership is the bot's own look-up of the guild member, and a 404 means not a member.

## Jungle calls (`Kzg.Core.Jungle`, `Kzg.Bot.Jungle`)

- **Clock.** `MatchClock` counts 35:00 down to 00:00 and is `Idle`, `Running`, `Paused` or `Over`. `CallSchedule` lists a call 40 s and 20 s before each jungle spawn (25:00, 20:00, 15:00, 10:00, 05:00; the 20:00 spawn uses the "Jungle and halftime" clips) and hands out the ones due, with 2 s of grace for a late tick. `JungleMatch` runs the commands (`JungleCommand`) and replies in text; the host's 100 ms timer calls `JungleMatch.Tick`. The clock is in memory only.
- **Audio.** `JungleClips.Load` reads the four embedded WAVs (`src/Kzg.Bot/Jungle/audio/`, made by `scripts/make-jungle-audio.sh`), turns each into 20 ms stereo frames with `JungleAudio.Frames` at the gain for `jungleVolume` (`JungleAudio.GainFor`: 100 is gain 0.5, the default 32 is 0.16) and encodes it at `Mixer.Bitrate`, once at startup. When `jungleVolume` changes, `BotApp.Reconfigure` re-encodes the clips off the calling thread (the latest volume wins) and swaps them in with `JungleSource.SetClips`; a clip already playing or queued finishes at the old level. Startup fails if a clip is missing or unreadable. `JungleSource` is the `IAudioSource` for them: `Targets` is both groups, `Play` queues a clip (false when 4 already wait), and each tick reads one packet.
- **Reach.** A call goes to every bound Attack and Defense VC whose relay bot is ready. With none, `JungleMatch` logs that nobody heard it, and `/jungle test` says so.
- **Logging.** The host logs each `/jungle` command with the caller's ID. Over-the-match and nobody-heard events go to the log only; the bot posts nothing on its own.

## Setup page (`Kzg.Bot.Setup`)

With no `config.json` in the data folder, or with `--setup`, the real bot serves only the setup page, on the address it would listen on, until setup is done; then that server stops and the bot proper starts on the same port, in the same process. `--setup` reads the earlier config without checking its tokens (`ConfigStore.Load(secrets, checkSecrets: false)`), so it works when the tokens are gone, and refuses a config that is otherwise broken rather than overwrite it.

- **Routes.** `GET /setup?key=` trades the key for a session cookie (`kzg_setup`, `HttpOnly`, `SameSite=Strict`, path `/setup`, `Secure` behind a proxy) and redirects to `/setup`. `GET /setup` shows the step. `POST /setup/tokens` (`token1`..`token6`, `publicHost`), `POST /setup/back` and `POST /setup/finish` (`server`) answer 303 to `/setup`, or the done page. `GET /healthz` answers `ok`. Anything else is 404; nothing else of the bot is served.
- **The key.** 256 random bits, kept as its SHA-256 and compared in constant time, printed on the console only (never through the logger). It opens one session; the newest session alone counts. Presented again, the spent key prints a fresh one (410); any other key gets 403 and spends nothing.
- **Who is admitted.** A request with no forwarding header (`X-Forwarded-For`, `X-Forwarded-Proto`, `Forwarded`, `X-Real-IP`) must name `localhost`, `127.0.0.1` or `[::1]` as its Host, which also defeats DNS rebinding. A forwarded one must say the browser used HTTPS and name this computer or `publicHost` (from the earlier config or `KZG_PUBLIC_HOST`). A form must not come cross-site: an `Origin` must match the Host, and `Sec-Fetch-Site: cross-site` is refused. Bodies are capped at 16 KB.
- **Pages.** No script; `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; frame-ancestors 'none'`, plus `no-store`, `Referrer-Policy: same-origin` (not `no-referrer`: a browser then posts the page's own forms with `Origin: null`, which the same-origin check must refuse), `nosniff` and `X-Frame-Options: DENY`. Bot and server names from Discord are HTML-encoded. No page carries a token.
- **Tokens.** `DiscordBotAccounts` checks each with `GET /applications/@me` and reads the servers with `GET /users/@me/guilds` (at most every 3 s), the token in the `Authorization: Bot` header only. A pasted token is trimmed and loses a leading `Bot `; anything not shaped like a bot token is refused before it is sent. Tokens stay in memory until finish.
- **Fields.** `token1`..`token6` are labelled by `RelayNames.ForSlot`: A1 Broadcaster to A3 Broadcaster, then D1 Broadcaster to D3 Broadcaster. The first is the primary's; blanks are allowed anywhere.
- **Invite links.** `https://discord.com/oauth2/authorize?client_id=<application>&scope=bot&permissions=87032832` (View Channel, Connect, Speak, Move Members, Change Nickname); the first bot adds `applications.commands` and Send Messages (87034880). Once the first bot is in exactly one server, the others' links name it (`guild_id`, `disable_guild_select=true`).
- **Finish.** The server is the one every bot is in, or the admin's pick among several. `SecretsStore.Save` writes each token as `relay<field>`, so fields 1 and 4 give `relay1` and `relay4`, keeps every other entry and removes the earlier config's bot tokens no longer used, owner-only from the first byte. `SetupPlans.WithBots` writes the bots (the first pasted primary), `guildId` and `publicHost`, keeping the other settings; a bot already known by its user ID keeps its channel, and a different server drops the teams and alerts channel. A `groups.*.shotCallerUserId` left in an older `config.json` is read and dropped, whatever its value, and never written. Sign-in left on without its client secret is turned off.

## Stopped relays

`BotConfig.Stopped` (`stopped` in `config.json`, default `false`) is what `/gvg stop` sets and `/gvg start` clears. The bot starts with its relays out of voice while it is set. It is additive: a config without the field loads as running, and the schema version stays 1.

## Keeping a Mac awake

On macOS the real bot runs `/usr/bin/caffeinate -i -s -w <its pid>` for as long as it runs (`KeepAwake`): idle sleep, and system sleep on power, are held off, and the hold ends with the bot's process even after a crash. `packaging/bot/install-mac.command` installs `~/Library/LaunchAgents/com.kzg.bot.plist`: `RunAtLoad`, `KeepAlive` with `SuccessfulExit` false (restart unless it exited 0), `ThrottleInterval` 10, `ProcessType` `Interactive`, data in `~/Library/Application Support/Kzg`, and `--log-file ~/Library/Logs/Kzg/bot.log`, with launchd's own output to `/dev/null`, since launchd never trims a file.

## Bounded logs

`--log-file <file>` (`RotatingLogWriter`) takes the place of the console, including `Console.Out`, `Console.Error` and an unhandled exception's report (redacted). At the end of a line past 10 MB the file moves to `<name>.1.log`, `.1` to `.2`, and the oldest past two goes: 30 MB at most. Readers may hold the file open; when Windows won't let it be renamed, it starts afresh instead. The Mac service and the Windows task use it; the Docker bundle caps json-file at three files of 10 MB; under systemd the journal's own limits apply.
