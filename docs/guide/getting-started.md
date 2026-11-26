# Installing Discord Broadcast

Discord Broadcast is a small app for Windows and Mac. Hold a key and your voice goes to your whole team, or to every team. Follow only the part for your computer. There is no phone app: phone players hear broadcasts and talk in their own team channel as usual.

Your computer will warn you when you install, because the app is not signed by Microsoft or Apple. That does not mean the file is damaged. Only install a file you got from the [download page](https://amadeuspham.github.io/kzg-gvg-bot/) or from the person who runs your server.

## Windows

1. Get `DiscordBroadcast-win-x64-Setup.exe` from the download page.
2. Open the file. If your browser says it isn't commonly downloaded, choose Keep, then Keep anyway.
3. When Windows says "Windows protected your PC", choose More info, then Run anyway.
4. The app installs and opens. Go to "Sign in" below.

To remove it: Settings, Apps, Installed apps, Discord Broadcast, Uninstall.

## macOS

You need a Mac with an Apple chip (M1 or newer). Intel Macs are not supported yet.

1. Get `DiscordBroadcast-macOS-arm64-Setup.pkg` from the download page.
2. Double-click the file. macOS says it can't verify it. Choose Done, not Move to Bin.
3. Open System Settings, Privacy & Security, scroll down to Security, and choose Open Anyway next to the blocked file. Enter your password or use Touch ID, choose Open Anyway again, and follow the installer.
4. Open Discord Broadcast from Applications. When it asks, choose the yellow Open Input Monitoring settings button and switch Discord Broadcast on. The app restarts itself.
5. Go to "Sign in" below.

To remove it: drag Discord Broadcast from Applications to the Bin.

## Sign in

Open the app and choose Sign in with Discord.

The sign-in screen has a Bot address field. It comes filled in, so leave it alone unless the person who runs your server gave you a different address. Change it only to join another bot.

If Sign in with Discord doesn't work, type `/connect` in your Discord server and paste the code into the app instead.

## Use it

- **Hold F8** to talk to your whole team. **Hold F7** to talk to all teams, Attack and Defense. Let go to stop.
- **Open mic** (F9, or click its switch) keeps your own team hearing you until you turn it off. It works while you sit in a team voice channel.
- **Change a key** by clicking it in the window and pressing the new one.
  - One key can't do two controls. Pick a key the other controls don't use.
  - Esc can be a key too, so pressing it sets Esc and does not cancel. To keep the old key, choose Cancel.
  - Clear leaves a control with no key. It shows "Not set" and nothing fires it. Click a "Not set" key to set it again.

In the app's Settings:

- **Open at login** starts the app when you sign in. It is off by default.
- **Updates** install by themselves. When "Version x is ready" shows, press Restart to Update between calls. You can also press Check for Updates in Settings.

## If something goes wrong

- **Windows has no Run anyway button.** Close the box, right-click the file, choose Properties, tick Unblock, choose OK, and open it again.
- **Your antivirus removed the file.** Restore it from quarantine, or ask the person who runs your server.
- **Keys don't work in a game on Windows.** A game running as administrator can hide your key presses. The app says so and offers Restart as administrator.
- **Keys don't work on a Mac.** In the app's Settings, under "Keys not working in game?", choose Open Input Monitoring settings and switch Discord Broadcast on.
- **macOS says there is no Open Anyway button.** On macOS 14 or older, Control-click the file, choose Open, then Open again in the box.
- **macOS says Discord Broadcast is damaged or can't be checked.** Ask the person who runs your server; they have a one-line fix in the [host guide](hosting.md).
