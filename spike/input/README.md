# Input spike (plan step 0c)

Orientation: a throwaway .NET 10 console app that proves global, listen-only capture of a hold-to-broadcast input
(keyboard key, mouse side button, gamepad button) while another app has focus. It de-risks step 12 (the real PC app).
It never injects or suppresses input. Not part of `Kzg.slnx`. The Windows run is in
[WINDOWS_CHECKLIST.md](WINDOWS_CHECKLIST.md).

## Conclusions

1. Use SharpHook for keyboard and mouse (side buttons arrive as `Button4`/`Button5`), SDL3 for controllers.
   Both bind and load on win-x64 and osx-arm64 (natives ship in the NuGet packages).
2. macOS: the hook cannot start without Accessibility. Verified here: `HookException ErrorAxApiDisabled (40)`.
   The app must detect that and walk the user through the grant.
3. Windows: the elevated-game case is NOT yet verified. It needs the user's PC (checklist). The expected
   result, from how Windows UIPI works, is that a non-elevated hook sees nothing while an elevated window is focused.
4. Controllers are the exception: SDL3 reads them through its own device path, so they are expected to work in both cases.
   Verified on this Mac with a DualSense (USB or Bluetooth, background events on).
5. macOS: the hook deadlocks unless the main thread services the main dispatch queue. libuiohook's tap callback
   does `dispatch_sync_f` onto the main queue for every `NX_SYSDEFINED` event; with the main thread parked, the hook
   delivers nothing and, because the tap is active, desktop input lags until macOS disables the tap. Proven on this Mac
   (section below). The spike now runs a CFRunLoop on the main thread. SDL runs in a child process.

## Run it

```
export PATH=$HOME/.dotnet:$PATH     # the SDK that global.json asks for (10.0.100+)
cd spike/input/InputSpike
dotnet run -- [options]
```

Default mode: keys and mouse from the hook in this process, controller from SDL in a child copy of the same exe
(`--pad-only --child`, relays its lines over stdout, exits when the parent closes its stdin).

| Option | Effect |
|---|---|
| `--keys-only` | keyboard and mouse only: hook, no SDL, no child |
| `--pad-only` | controller only: SDL on the main thread, no hook |
| `--perms` | macOS: print Accessibility and Input Monitoring state and exit |
| `--request-perms` | macOS: also trigger the system prompts |
| `--poll` | Windows: also poll F8, XButton1, XButton2 with `GetAsyncKeyState` (fallback probe) |
| `--log <file>` | log file (default `input-spike-<time>.log` in the current folder; ignored by git) |

Log format: `HH:mm:ss.fffZ +<monotonic ms> [source] NAME DOWN|UP`. Key auto-repeat is collapsed into
`(+N auto-repeat)` on the UP line. On Windows each line also names the focused process and whether it is elevated.

Code: `Program.cs` (modes, hook), `Gamepad.cs` (SDL3), `PadProcess.cs` (controller child), `MainLoop.cs`
(main run loop), `MacPermissions.cs`,
`WinProbe.cs`, `Log.cs`.

Threading: hook callbacks only take timestamps and the foreground window handle, then enqueue to a logger thread that
does the console/file I/O and the process lookup. In the hook process the main thread runs a CFRunLoop on macOS
(`MainLoop.cs`), which the hook needs. In the controller process the SDL loop runs on the main thread. The
parent re-logs the child's lines on its own clock (pipe latency is a few ms). The heartbeat counts hook events only.

## Windows build

Command, from `spike/input/InputSpike`:
`dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o ../publish/win-x64`

- Output folder: `spike/input/publish/win-x64/` (about 81 MB, includes `uiohook.dll` and `SDL3.dll`).
- Zip for transfer: `spike/input/publish/InputSpike-win-x64.zip` (about 36 MB).
- `publish/` is git-ignored. Not run on Windows yet (this Mac cannot).
- Not single-file: the two native DLLs must sit beside the exe. The controller child is the same `InputSpike.exe`.

## Findings per input type and OS

| Input | macOS (this Mac, arm64) | Windows x64 |
|---|---|---|
| Keyboard | Works until the first `NX_SYSDEFINED` event, then dead for the rest of the run, unless the main thread runs a run loop (fixed: `MainLoop.cs`). Fixed build: user re-test pending. | Not run. Checklist runs C, A, B. |
| Mouse side buttons | Same as keyboard (`Button4` logged). SharpHook maps side buttons to `Button4`/`Button5` on both OSes. | Not run. Same checklist. |
| Gamepad (SDL3) | Works (user, DualSense: face buttons, d-pad, back, touchpad; the spike was not the frontmost app). | Not run. Needs a pad. |
| All three together | Hook plus main run loop in the parent, SDL in a child process. Without input: tap enabled, DualSense opened in the child. Real presses: user re-test pending. | Not run. |

### macOS permissions (verified here)

- Without Accessibility, `SimpleGlobalHook.RunAsync()` faults with `HookException: ... ErrorAxApiDisabled (40)`
  within about 200 ms. The process keeps running and the failure is only visible via that task. The real app must
  observe the task and surface it, or the user sees a silent dead key.
- `AXIsProcessTrusted()` returned false and `IOHIDCheckAccess(ListenEvent)` returned denied (not "not determined")
  for this host. So the host process was already denied Input Monitoring, and `--request-perms` raised no new
  prompt that I could observe (the prompts are system dialogs and cannot be read back). Both APIs work without
  special entitlements and are in `MacPermissions.cs`.
- Permissions attach to the responsible app, not to our code. Run from a terminal and the terminal (or the Claude
  host app) is the thing that needs the grant. A packaged app is its own identity, so Step 13's Velopack bundle needs a
  stable bundle id. An unsigned app may lose the grant on each update (the signature changes), which the user must
  re-grant.
- What the real app must show on macOS: a first-run screen that says what the keys are used for ("Listens for your
  broadcast key. Never records or sends what you type; only sends press/release"); one button that opens
  System Settings > Privacy & Security > Accessibility and one for Input Monitoring; live state for each
  (poll `AXIsProcessTrusted` / `IOHIDCheckAccess`, about every second while the screen is open); and after a grant
  start the hook again, since a failed hook does not resume. Expect a restart prompt on some macOS versions.
  Whether libuiohook needs Input Monitoring in addition to Accessibility (it creates an event tap) is not
  settled: the hook never got far enough to say. Check on a machine that grants one at a time.
- I did not change any system setting.

### Windows (not verified)

- UIPI: a hook set by a lower-integrity process does not receive input aimed at a higher-integrity window. A game
  started as administrator is high integrity. Run A in the checklist tests this directly. Run B (test app elevated)
  is the proposed fix and matches the plan's "Run as administrator" option.
- `GetAsyncKeyState` polling (`--poll`) is included as an alternative probe. If it works in run A, it is a
  non-elevated fallback for keys and side buttons (polling at ~5 ms, adding at most 5 ms).
- Raw Input and anti-cheat: not tested. Kernel anti-cheat can ban or block hooks. Checklist asks about it.

### Controller (SDL3)

- Package: `ppy.SDL3-CS` 2026.1002.1 (bundles SDL 3.5.0 natives for win/osx/linux) and `SharpHook` 8.0.3.
- The hint is set before `SDL_Init`. The app runs `SDL_WaitEventTimeout` on its own thread and opens each gamepad on
  `SDL_EVENT_GAMEPAD_ADDED`, so hot-plug works. Without the hint SDL ignores joystick events when no SDL window has
  focus.
- macOS main thread: the helper process owns its main thread and runs SDL there, so Avalonia's Cocoa run loop never
  meets SDL. Background delivery works: the user's DualSense presses logged while the terminal, not the spike, was the
  frontmost app.
- Test with a controller: `dotnet run -- --pad-only`, press buttons while another app is focused. Expect
  `controller opened: "<name>"` then `[pad] a DOWN` / `UP`. On macOS a Bluetooth pad may need to be paired first and
  PS/Xbox pads may trigger an Input Monitoring prompt (not yet seen). If nothing logs, check the
  `no controller attached` line first.

### Why the hook went silent (macOS)

Proven on this Mac, from the user's live `--keys-only` process that logged 0 events:

- `sample <pid>`: the hook thread was blocked in `event_to_objc` -> `dispatch_sync_f` -> waiting for the main queue,
  called from the tap callback (`_XPostEventTapData`). The main thread sat in `WaitHandle.WaitOne`.
- `CGGetEventTapList`: that process's tap was at the session tap point, `options=0` (active filter, not listen-only),
  and `enabled=0`. Secure Input was off.
- Source, SharpHook's libuiohook fork (`TolikPylypchuk/libuiohook`, `src/macos`):
  - `input_hook.c` passes every `NX_SYSDEFINED` event to `dispatch_system_key`, which `dispatch_event.c` sends to
    `event_to_objc` "on the main runloop" (caps-lock fix, kwhat/libuiohook#108).
  - `input_helper.c` `event_to_objc`: off the main thread, it calls `dispatch_sync_f(_dispatch_main_q, ...)`. That
    never returns unless the main thread drains the main queue. Key-typed lookups take the same path, but key-typed is
    off by default.
  - `input_hook.c` creates the tap with `kCGEventTapOptionDefault` (active), hardcoded so suppression works; SharpHook
    8.0.3 offers no listen-only switch. The `kCGEventTapDisabledByTimeout` re-enable runs in the same blocked
    callback, so the tap stays off.
- Effect: each event waits for the stuck tap until macOS times it out (the user needed 3 clicks to focus an app), then
  the tap is disabled for good.
- Fix check (no input posted): with `MainLoop.cs` the new tap reads `enabled=1` and the hook thread idles in its run
  loop. Real key delivery with the fix: user re-test pending.

Inferred, not proven:
- `NX_SYSDEFINED` covers system keys (media, brightness, caps lock) and system or app-posted events, so the trigger
  arrives at random times. That explains the flaky history: the hook-only run at 15:21 happened to get none in 17 s.
  The SDL runs failed because SDL's loop did not drain the main queue either, not because of SDL itself. The earlier
  "1 in 10 hook-only misses" with synthetic posts is the same deadlock or the post racing tap start; either way it
  is not a separate bug.
- Hook plus SDL in one process may therefore work once the main queue is serviced. Not tested; the spike keeps SDL
  in a child process, which needs no answer to that.

## What step 12 should do
## What step 12 should do

0. Hook callbacks must be near-free. SharpHook raises events on the hook thread; if a callback is slow, Windows silently
   removes the low-level hook (`LowLevelHooksTimeout`), which looks exactly like a UIPI failure. In the callback, only
   take a timestamp and the hwnd and push to a queue (or use `EventLoopGlobalHook`). Do no I/O, no process lookups, no UI
   calls, no locks held elsewhere. A blocked console (QuickEdit text selection) has the same effect if logging is inline.
0b. macOS main-thread rule: the hook thread blocks on the main dispatch queue for system-defined events, and the tap
   is active, so a busy or blocked main (UI) thread delays every user's keystroke and click system-wide, and a
   main thread that never drains the queue kills the hook. In Avalonia the UI thread's Cocoa run loop drains it;
   keep that thread free of long work. Watchdog: check our own tap with `CGGetEventTapList` (enabled flag) every few
   seconds; if it is off, report `HookFailed`. Better, and worth a step-12 task: a listen-only tap, so the hook can
   never delay input. Either a one-line patched libuiohook build (`kCGEventTapOptionListenOnly`, no suppression,
   which we never use) or a small own CGEventTap on macOS. Both remove the active-tap stall; the own tap also removes
   the main-queue dependency.
   Controllers: SDL3 in a helper process, the same app binary started with a helper argument (as `PadProcess.cs`).
   The helper prints one line per button down/up and hot-plug on stdout and exits when its stdin closes. The app
   restarts it if it dies, and starts it only when a controller binding exists. Same design on Windows.
   In-process SDL may also work now that the deadlock is explained (see above); untested, so not the plan.
   Fallback: one input mode at a time (`--keys-only`, `--pad-only`), and switching modes restarts the input layer.
0c. Self-test after `HookEnabled`: if the user presses their bound key on the setup screen and nothing arrives,
   say so and offer to restart the hook. Do not post synthetic input to test it.
1. Put capture behind `IBroadcastTrigger` in `Kzg.App.Input`: one `SimpleGlobalHook` (SharpHook) in the app, and the
   SDL3 helper process, as in `Program.cs`, `Gamepad.cs` and `PadProcess.cs`. Collapse auto-repeat to a single down;
   the plan's heartbeat then repeats the held state.
2. Treat the hook as fallible. Observe the `RunAsync` task, expose `InputHealth { Ok | NeedsAccessibility | HookFailed }`, and show it in
   the status badge warnings.
3. macOS: permission screen and live checks as described above. Never request permission at launch without
   explaining first.
4. Windows: detect own elevation and show "Run as administrator if keys don't work in game"; offer a relaunch
   elevated (UAC) option; include a "key test" screen that shows the last input and focused-window elevation, like this spike's log. Use the
   checklist result to decide whether to add `GetAsyncKeyState` polling as a fallback.
5. Keep SDL's `SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS=1` set before `SDL_Init`.
6. Never set `SuppressEvent` and never inject; this stays a listen-only app.
7. Bind by key code, not character. Support `Button4`/`Button5` and gamepad button names (`SDL_GetGamepadStringForButton`).

## Open items

- Windows runs A, B, C (user).
- macOS: user re-test of the default (combined) mode with real keys, side buttons and pad presses.
- A real controller on Windows.
- macOS: whether Input Monitoring is required in addition to Accessibility (both were granted when keys worked).
- Whether SDL works in the hook's process now that the main queue is serviced (only matters to drop the helper).
- Listen-only tap for step 12: patched libuiohook or own CGEventTap.
