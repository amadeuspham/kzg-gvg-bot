---
name: ui-check
description: Render the Kzg.App window in every state, light and dark, and look at the screens a change touched. Use when the user says "ui check", "check the UI", "screenshots", "look at the screens", "does it look right", or after any change under src/Kzg.App (views, view models, styles, strings).
---

# ui-check

Draws the real `MainWindow` headless, so nothing on screen is mocked and nothing of the user's running Kzg.App is touched.

1. Run once: `dotnet run --project tests/Kzg.App.Screens -c Release` (use the SDK `scripts/dotnet.sh` finds if `dotnet` lacks it). It deletes the old PNGs in `.data/screens` and writes `<NN>-<scene>-light.png` and `<NN>-<scene>-dark.png`. Scenes are defined in `tests/Kzg.App.Screens/Scenes.cs`.
2. Pick the scenes the change affects (grep `Scenes.cs` for the view model state you changed). Open both the light and the dark PNG of each with the Read tool.
3. Look for: clipped or overlapping text, long strings that do not wrap or scroll, unreadable contrast in either theme, a state that lacks its warning or button.
4. Report which scenes you opened and what you saw. If a changed state has no scene, say so; add one to `Scenes.cs` only if asked.

A headless render does not cover keyboard use, the macOS Input Monitoring prompt, or Windows behaviour under an elevated game; say these are unchecked.
