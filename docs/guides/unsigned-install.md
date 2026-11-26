# Installing Discord Broadcast

This guide installs Discord Broadcast on Windows or macOS. Follow only the part for your computer. For running the bot, see the [host guide](host.md).

The installers are not signed by Microsoft or Apple, so your computer will warn you. That does not mean the file is damaged. Only install a file you got from the person who runs your server or from the [download page](https://amadeuspham.github.io/kzg-gvg-bot/).

There is no phone app. Phone players hear broadcasts and talk in their own team channel as usual.

## Windows

1. Get `DiscordBroadcast-win-x64-Setup.exe` from the person who runs your server.
2. (Recommended) Check the file. In PowerShell: `Get-FileHash .\DiscordBroadcast-win-x64-Setup.exe -Algorithm SHA256`. Compare it with the sum you were given; if they differ, delete the file and ask again.
3. Run the file. If your browser says it isn't commonly downloaded, choose Keep, then Keep anyway.
4. When Windows says "Windows protected your PC", choose More info, then Run anyway.
5. The app installs and opens. Go to "Link the app" below.

To remove it: Settings, Apps, Installed apps, Discord Broadcast, Uninstall.

## macOS

1. Get the file for your Mac from the person who runs your server: `DiscordBroadcast-macOS-arm64-Setup.pkg`. It needs an Apple chip (M1 or newer); Intel Macs are not supported yet. Apple menu, About This Mac shows which you have.
2. (Recommended) Check the file. In Terminal: `shasum -a 256 ~/Downloads/DiscordBroadcast-macOS-arm64-Setup.pkg`. Compare it with the sum you were given; if they differ, delete the file and ask again.
3. Double-click the file. macOS says it can't verify it. Choose Done, not Move to Bin.
4. Open System Settings, Privacy & Security, scroll to Security, and choose Open Anyway next to the blocked `.pkg`. Enter your password or use Touch ID, choose Open Anyway again, and follow the installer.
5. Open the app from Applications. When it asks, choose the yellow Open Input Monitoring settings button and switch Discord Broadcast on. The app restarts itself.
6. Go to "Link the app" below.

To remove it: drag Discord Broadcast from Applications to the Bin.

## Link the app

Open the app and choose Sign in with Discord. If that doesn't work, type `/connect` in your Discord server and paste the code into the app instead.

## Use it

- **Hold F8** to talk to your whole team, **hold F7** to talk to all teams, Attack and Defense. Let go to stop.
- **Open mic broadcast** (F9, or click its switch) keeps your own team hearing you until you turn it off. It works while you sit in a team voice channel.
- **Change a key** by clicking it in the window and pressing the new one. While it waits, Clear leaves that control unset (it shows "Not set" and no key fires it) and Cancel keeps the old key. Click a "Not set" chip to set it again.

Optional, in the app's Settings:

- **Open at login** starts the app when you sign in. It is off by default.
- **Updates** are automatic. The app checks in the background; when "Version x is ready" shows, press Restart to Update between calls. The same button reads Check for Updates… when nothing is waiting and looks straight away. It sits in Settings, in the tray menu and, on a Mac, in the app menu (Discord Broadcast > Check for Updates…).

## If something goes wrong

- **Windows has no Run anyway button.** Close the box, right-click the file, choose Properties, tick Unblock, choose OK, and run it again. Or in PowerShell: `Unblock-File .\DiscordBroadcast-win-x64-Setup.exe`.
- **Your antivirus removed the file.** Restore it from quarantine and tell whoever runs your server.
- **Keys don't work in a game on Windows.** A game running as administrator can hide your key presses. The app says so and offers Restart as administrator.
- **macOS says Discord Broadcast is damaged or can't be checked.** Remove the quarantine mark, then open the app again:

  ```bash
  xattr -dr com.apple.quarantine "/Applications/Discord Broadcast.app"
  ```

- **No Open Anyway on macOS 14 or older.** Control-click the file, choose Open, then Open in the box. On macOS 15 and newer use Privacy & Security as above.
- **Keys don't work on a Mac.** In the app's Settings, under "Keys not working in game?", choose Open Input Monitoring settings and switch Discord Broadcast on. The app needs no Accessibility permission.
- **Remove Open at login manually.** On a Mac it is `~/Library/LaunchAgents/app.kzg.login.plist`; on Windows a `Discord Broadcast` entry under Settings, Apps, Startup.
