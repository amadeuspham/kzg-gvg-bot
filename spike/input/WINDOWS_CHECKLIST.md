# Windows checklist: global hooks while an elevated window has focus

Orientation: this checks whether a normal (non-elevated) program can still see key, mouse side-button and controller
presses while an elevated ("Run as administrator") window, such as the game, has focus. It is a listen-only test.
Nothing is injected or blocked. Background: [README.md](README.md). Plan step: 0c, verified for real in step 14.

Build: `spike/input/publish/win-x64/` on the Mac, or the zip `spike/input/publish/InputSpike-win-x64.zip` (self-contained,
no .NET install needed, win-x64 only). Copy the zip to the Windows PC and unzip it to e.g. `C:\spike\`.
If SmartScreen warns about the unsigned exe: More info, then Run anyway. If files are blocked after unzip:
`Get-ChildItem C:\spike -Recurse | Unblock-File`.

## What the test app prints

One line per event: `HH:mm:ss.fffZ +ms [source] NAME DOWN|UP  fg=<focused process (pid, elevated?)>`.
- `[key]` and `[mouse]` come from the SharpHook global hook. Mouse side buttons show as `Button4` / `Button5`.
- `[pad]` comes from SDL3 (gamepad buttons). SDL runs in a second `InputSpike.exe` process that the first one starts
  and stops; you will see two in Task Manager. `[pad]` lines carry no `fg=` text.
- `[poll]` appears only with `--poll`. It watches F8, XButton1, XButton2 with `GetAsyncKeyState`, as a fallback probe.
- `[alive]` every 5 s shows the hook event count and the focused window. A count that does not move while you press
  keys in the focused elevated window is the failure signature.
- The first lines say whether the test app itself is ELEVATED.

## Setup

1. Pick the elevated target. Best: the game, started as administrator. Fallback: Notepad started with right-click,
   Run as administrator (title bar will not show it; check the `fg=` text in the log says `elevated`).
2. Have a mouse with side buttons and, if available, a controller (Xbox or DualShock, USB or Bluetooth).
3. Open a terminal for each run. For run A use a normal Command Prompt. For run B use one opened with
   Run as administrator. Check the first log line says NOT elevated / ELEVATED as expected.

## Runs

Before each run: in the terminal window do NOT click into the text or select anything. A selection (QuickEdit mode)
freezes console output and can make the log look dead. Better: turn QuickEdit off (terminal title bar, Properties,
Options, untick QuickEdit Mode), or use Windows Terminal. The app logs from a separate thread to the file as well, so
the log file is the source of truth even if the console is paused.

For each run: start the test app, click into the target window, then do the actions in the table. Hold each
input for about 2 seconds, then release. Stop with Ctrl+C in the terminal (alt-tab back first). The log
is written to the folder you started from as `input-spike-<time>.log`.

```
cd C:\spike
InputSpike.exe --poll
```

| Run | Test app | Target in focus | Expect (hypothesis) |
|---|---|---|---|
| C. Control | not elevated | NOT elevated window (e.g. normal Notepad) | everything logs |
| A. The risk | not elevated | ELEVATED window (game or admin Notepad) | keys and mouse: probably nothing; pad: logs |
| B. The fix | ELEVATED | ELEVATED window | everything logs |

Actions per run, in the focused target window:

| Input | Do | Log shows |
|---|---|---|
| Keyboard | hold F8, release; hold Left Ctrl, release; tap a letter | `[key] VcF8 DOWN` then `UP` |
| Mouse side back | hold the back side button, release | `[mouse] Button4 DOWN` then `UP` |
| Mouse side forward | hold the forward side button, release | `[mouse] Button5 DOWN` then `UP` |
| Controller | start the app with the pad already on, or plug it in while running; hold A, hold a shoulder button | `[pad] controller opened`, then `a DOWN` / `UP` |

## Record per run

Copy this table into your reply and fill it. Put the three log files next to it.

| Run | App elevated? (first log line) | Target `fg=` says elevated? | F8 hook | Button4 hook | Button5 hook | Pad | F8 / Button4 poll |
|---|---|---|---|---|---|---|---|
| C | | | | | | | |
| A | | | | | | | |
| B | | | | | | | |

Use: logs, no logs, or logs with a delay (note roughly how long). Also note:
- Does the game have anti-cheat (name, if you know)? Does the app, or the game, behave oddly while hooked
  (crash, kick, warning)?
- In run B, does the hook still work when the game goes fullscreen versus borderless windowed?
- Do `[key]` events still arrive after Win+L lock / unlock (hooks can be dropped)? Optional.

## Reading the result

- A shows no `[key]`/`[mouse]` lines and B does: Windows UIPI is blocking the hook. Step 12 must offer
  "Run as administrator" and show the warning in the plan.
- A logs fine: the game is not blocking; players' push-to-talk problems have another cause. Ask which exact input they bind.
- A shows `[poll]` lines but no `[key]` lines: `GetAsyncKeyState` polling is a viable non-elevated fallback for
  keys and side buttons. Report that, since it changes the step 12 design.
- No `[pad]` in any run: note the controller model and whether it shows "controller opened".
- `HOOK FAILED` at the top: send the exact message.
