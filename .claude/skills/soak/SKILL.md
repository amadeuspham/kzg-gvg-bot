---
name: soak
description: Run the one-hour load soak (thirty talkers, six broadcasting) and judge it by the load test's own pass criteria. Use when the user says "soak", "soak test", "load test", "run the load test for an hour", "KZG_LOAD", or asks whether the engine holds up under load.
---

# soak

Opt-in, macOS only (it uses `say`). It plays audio on the user's machine, so warn in chat and wait for go (AGENTS.md rule 3). It runs an hour; do not run it inside the gate.

1. Check the machine is quiet, because CPU is measured machine-wide: `uptime` (load average) and `top -l 1 -n 5 -o cpu`. If something heavy runs (a game, a build, the user's Kzg.App doing real work), tell the user and wait. Never stop or touch their processes (rule 2).
2. Run:
   `KZG_LOAD=1 KZG_LOAD_SECONDS=3600 caffeinate -i dotnet test tests/Kzg.Bot.Tests -c Release --filter "FullyQualifiedName~Load"`
   Without `KZG_LOAD=1` the test is skipped. Default length is 300 s.
3. A line a minute also goes to `.data/load/soak-<yyyyMMdd-HHmmss>.log`.
4. Pass criteria, from `tests/Kzg.Bot.Tests/Load/LoadTests.cs`; the test fails if any is broken:
   - Mean process CPU under 30% of the machine.
   - No engine tick starts more than 20 ms late.
   - Zero tick faults, decode errors, send errors and resyncs.
5. Also report, though they do not fail the test: working-set change between the first and last minute (a steady climb means a leak), any `PRESSURE` or `SAMPLER GAP` lines (the machine, not the bot, was struggling, so the run is suspect), underruns and dropped packets.
6. Report pass or fail with the failing criterion, the numbers, and the log file name.
