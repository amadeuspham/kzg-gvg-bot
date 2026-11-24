# Step 0a result: NetCord voice library spike

Pick: **NetCord 1.0.0-beta.30 with libdave 1.2.1.9** (ESCd NuGet package). All 7 acceptance items passed, so Discord.Net and DisCatSharp were not tried. Evidence is in `.data/dave/netcord-20261123-174011/summary.json` (git-ignored).

## Numbers (600 s soak, 3 testers, all bots in one process)

- Voice UDP: IP discovery worked 9 of 9 times; 313,730 packets decrypted.
- DAVE protocol v1 on every client for the whole soak.
- Per-user receive: 0% loss, 0 wrong user IDs, 0 unknown SSRCs (frames matched by content hash).
- Relay A1 to A2: 23,952 frames forwarded, 0 lost, 0 changed bytes.
- Added latency, relay receive to listener receive: p50 20.5 ms, p95 21.7 ms, p99 80.3 ms. The spike's 20 ms send tick accounts for about 15 ms of that.
- Join, leave and reconnect events: at most 0.7% loss; the voice websocket resumes in 0.27 s; a gateway resume loses nothing.
- libdave: runs on osx-arm64. The win-x64 and linux-x64 builds export the same 45 `dave*` functions.

## Adapter notes for step 7

- **Receive.** Each voice client delivers frames synchronously on its own UDP read loop. The buffer is pooled and reclaimed when the handler returns, so copy it before queueing. Map SSRC to user with `Cache.SsrcUsers`, which is safe from that thread. Presence events arrive on gateway threads.
- **Send.** `SendVoice(seq, ts, span)` is synchronous: it copies and encrypts on the caller's thread, so the caller can reuse its buffer at once. The adapter owns seq and ts, and adds 960 per 20 ms tick even when it sends nothing.
- **Ready.** Sending before the client is ready throws, so keep a cheap Ready flag. During DAVE transitions NetCord drops outgoing frames without signalling.
- **Silence.** NetCord doesn't send silence frames. Send `F8 FF FE` five times at the end of each burst.
- **Speaking.** Enter the speaking state once after Ready.
- **Presence snapshot.** Read voice states from the first guild snapshot (needs the GuildVoiceStates intent), then follow voice-state updates. A gateway resume sends no new snapshot; a full re-identify does, so diff it against known state.
- **Moves.** NetCord resumes a dropped voice websocket itself. If the voice server changes, create a new voice client.
- **Opus.** NetCord's own Opus wrapper needs native libopus. Avoid it: pass Opus packets straight to `SendVoice` and use Concentus (`Kzg.Audio`) for decode and encode.

## Risks

- **libdave 1.3.0 breaks NetCord beta.30 without any error.** It changed the native `daveSessionProcessProposals` and `daveSessionProcessWelcome` signatures, so no MLS group forms and every frame is dropped. The mismatch also writes to the wrong memory. Pin 1.2.1.9. At startup, refuse to run if `daveSessionAddRecognizedUser` is exported: 1.3.0 has it, 1.2.1.9 doesn't.
- **Linux.** `libdave.so` needs glibc 2.38 or newer and a recent libstdc++. Debian 12 and Ubuntu 22.04 can't load it, which matters for the home server.
- **NetCord is a beta**, and its DAVE state is internal. It threw a caught null-reference twice on packets that arrived before keys were set during a rejoin.
- **Latency tail** comes from one nearby Discord region, with pass-through only and no mixing.

## Spike code

`NetCordSpike/` holds the throwaway code; it isn't in `Kzg.slnx`. Run it with `dotnet spike/dave/NetCordSpike/bin/Debug/net10.0/osx-arm64/dave-spike.dll --soak 600`.
