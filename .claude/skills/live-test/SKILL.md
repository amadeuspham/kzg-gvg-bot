---
name: live-test
description: Run the live Discord tests against the real test server and confirm every bot left voice. Use when the user says "live test", "run the live tests", "test against Discord", "KZG_LIVE", or needs proof that relay, move or setup behaviour works on the real server.
---

# live-test

Rules: AGENTS.md hard rules 1, 2, 3 and 5. Details: `docs/guides/dev-setup.md` "Live tests".

1. **Warn first, in chat, and wait for the user to say go.** The tests join the real test server's voice channels with every bot, and the Live tests use macOS `say` to play audio. Needs `.data/secrets.json` and open UDP to Discord voice. Without them, say what is missing and stop.
2. Run:
   `KZG_LIVE=1 dotnet test tests/Kzg.Bot.Tests -c Release --filter "FullyQualifiedName~Live"`
   Without `KZG_LIVE=1` every Live test is skipped; a skip is not a pass.
3. Cleanup check. Each live test that joins voice (all but `LiveSetupTests.cs`) ends with `AssertNobodyInVoice` and prints `still in voice after the run: N of M bots checked`. Require N = 0 in every test that ran. If the run was killed or timed out, say so, list the bots that may still be connected, and rerun a live test so its cleanup runs, or ask the user to remove them by hand.
4. Each run writes `.data/live/<yyyyMMdd-HHmmss>[-suffix]/` (`summary.json`, `bot.log`, recordings). `.claude/settings.json` denies Read on `.data/live/**`, so do not read it around the deny; use the test output printed by `dotnet test`, and ask the user to open the folder or lift the deny if the files are needed.
5. Never print tokens, connect codes or device tokens. Refer to bots by name (`relay1`, `tester1`).
6. Report: tests passed, failed, skipped; bots still in voice; the run folder name.
