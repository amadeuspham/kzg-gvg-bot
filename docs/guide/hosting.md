# Hosting the bot

This guide gets the Kzg bot running on your own computer or server. Pick ONE way to run it, follow only that part, then do the two shared parts, "Set it up in Discord" and "Let players join". For players installing Discord Broadcast, see the [install guide](getting-started.md). Settings, commands and other details are in the [bot reference](../reference/bot.md); technical background is in [contracts.md](../reference/contracts.md).

## Before you start

- A Discord server where you have Manage Server.
- A web address (domain name) that you can point at your home or server address. Players' apps connect through it over HTTPS.
- One Discord bot account per team voice channel, up to six (three Attack, three Defense). You can start with fewer. The setup page shows how to make them.

## Pick one way to run it

| If you have | Use |
| --- | --- |
| A Linux server with Docker, ports 80 and 443 free | [A. Docker](#a-docker-easiest) (easiest, HTTPS included) |
| A Mac with Apple Silicon (M1 or newer) | [B. Mac](#b-mac) |
| A Windows PC | [C. Windows](#c-windows) (the HTTPS step is fiddly; Docker or a Mac is simpler) |
| A Linux server without Docker (glibc 2.38+, such as Ubuntu 24.04) | [D. Linux](#d-linux) |

Not sure? Use A. It works on any Linux server.

Releases carry no bot archive. Parts B, C and D build the bot from source: clone the repo, install the .NET 10 SDK (see the [development guide](development.md)), and run `packaging/publish-bot.sh --rid <rid>` from the repo root. The archive lands in `packaging/out/`.

## A. Docker (easiest)

You need Docker with Compose, and the project folder on the server.

1. Point your domain's DNS at your home address. Forward ports 80 and 443 from your router to the server.
2. Open `packaging/docker/kzg.env` and replace `bot.example.org` with your domain.
3. In `packaging/docker/`, run:

   ```bash
   docker compose up -d
   ```

   The first run takes a few minutes.
4. Run `docker compose logs bot` and open the setup link it prints (the `https://` one works from any computer).
5. Continue with "Set it up in Discord" below.

## B. Mac

Apple Silicon only (M1 or newer).

1. Build it: `packaging/publish-bot.sh --rid osx-arm64`. Then unpack `packaging/out/kzg-bot-<version>-osx-arm64.tar.gz` and move the folder somewhere it can stay, such as your home folder. Don't move it later.
2. Double-click `install-mac.command`. If macOS won't open it, Control-click it, choose Open, then Open again.
3. Your browser opens the setup page by itself. If it doesn't, find the link in `~/Library/Logs/Kzg/bot.log` (open it with Console).
4. Point your domain's DNS at your home address. Forward ports 80 and 443 from your router to this Mac.
5. In Terminal, install Caddy and give it your domain (replace `bot.example.org`):

   ```bash
   brew install caddy
   printf 'bot.example.org {\n    reverse_proxy 127.0.0.1:7878\n}\n' > "$(brew --prefix)/etc/Caddyfile"
   brew services start caddy
   ```

   `brew services` keeps Caddy running after a reboot.
6. Check it: `curl https://bot.example.org/healthz` prints `ok`.
7. Continue with "Set it up in Discord" below.

The bot now starts at login, restarts after a crash, and keeps the Mac awake. Leave the Mac logged in.

## C. Windows

1. Build it in Git Bash from the repo root: `packaging/publish-bot.sh --rid win-x64`. Then unzip `packaging/out/kzg-bot-<version>-win-x64.zip`, for example into `C:\Kzg\bot`.
2. Open PowerShell as administrator, go to that folder and run:

   ```powershell
   Set-ExecutionPolicy -Scope Process Bypass
   .\install-windows-task.ps1
   Start-ScheduledTask -TaskName 'Kzg bot'
   ```

3. Open `C:\ProgramData\Kzg\bot.log` in Notepad run as administrator, and find the line with the setup link (`http://localhost:7878/setup?key=...`). Open it in a browser on this PC.
4. Point your domain's DNS at your home address. Forward ports 80 and 443 from your router to this PC.
5. Download Caddy for Windows (amd64) from <https://caddyserver.com/download> and save it as `C:\caddy\caddy.exe`.
6. In `C:\caddy`, create a file named `Caddyfile` (no extension) with your domain:

   ```
   bot.example.org {
       reverse_proxy 127.0.0.1:7878
   }
   ```

7. In the same elevated PowerShell, allow the ports and install Caddy as a service that starts with the PC:

   ```powershell
   New-NetFirewallRule -DisplayName 'Caddy' -Direction Inbound -Protocol TCP -LocalPort 80,443 -Action Allow
   sc.exe create caddy start= auto binPath= "C:\caddy\caddy.exe run --config C:\caddy\Caddyfile"
   sc.exe start caddy
   ```

8. Check it: `curl.exe https://bot.example.org/healthz` prints `ok`.
9. Continue with "Set it up in Discord" below.

The bot now starts with the PC and restarts after a crash.

## D. Linux

1. Build the bot on a machine with the .NET 10 SDK, as above: `packaging/publish-bot.sh --rid linux-x64`. Copy `packaging/out/kzg-bot-<version>-linux-x64.tar.gz` to the server, then unpack it and install the service:

   ```bash
   sudo useradd --system --home /var/lib/kzg --create-home --shell /usr/sbin/nologin kzg
   sudo mkdir -p /opt/kzg-bot && sudo tar -xzf kzg-bot-<version>-linux-x64.tar.gz --strip-components=1 -C /opt/kzg-bot
   sudo cp /opt/kzg-bot/kzg-bot.service /etc/systemd/system/
   sudo systemctl daemon-reload && sudo systemctl enable --now kzg-bot
   ```

2. Run `journalctl -u kzg-bot -f` and find the setup link (`http://localhost:7878/setup?key=...`). Open it in a browser on the server, or from your own computer through an SSH tunnel: `ssh -L 7878:127.0.0.1:7878 <server>`.
3. Point your domain's DNS at your address. Forward ports 80 and 443 from your router to the server.
4. Install Caddy. Ubuntu 24.04 and Debian 13 carry it:

   ```bash
   sudo apt install caddy
   ```

   The package starts Caddy now and at every boot. Other distributions: see <https://caddyserver.com/docs/install>.
5. Give Caddy your domain (replace `bot.example.org`) and reload it:

   ```bash
   printf 'bot.example.org {\n    reverse_proxy 127.0.0.1:7878\n}\n' | sudo tee /etc/caddy/Caddyfile
   sudo systemctl reload caddy
   ```

6. Check it: `curl https://bot.example.org/healthz` prints `ok`.
7. Continue with "Set it up in Discord" below.

## Set it up in Discord

1. **On the setup page**, follow its three steps:
   1. In the [Discord Developer Portal](https://discord.com/developers/applications), make one bot per team voice channel as the page describes, naming each application `Broadcast Relay`, and paste each token. The fields are named for the channels the bots fill: A1 Broadcaster to A3 Broadcaster for the first to third Attack channels, D1 Broadcaster to D3 Broadcaster for Defense. With fewer bots, leave the fields of channels you don't use empty.
   2. Enter your domain under "Public address" (for example `bot.example.org`). Docker fills it in for you.
   3. Use each bot's Invite button (Invite A1 Broadcaster, and so on) to add it to your server, then choose Finish setup.
2. **In Discord**, run `/gvg setup`. Pick the Attack and Defense voice channels. A relay bot joins each channel: A1 Broadcaster the channel you pick as `attack-1`, D1 Broadcaster the one you pick as `defense-1`, and so on. A channel whose bot you left out takes a bot whose channel you didn't pick. Each bot sets its server nickname to its channel's name. Optionally set `channel` to a text channel where the bot reports problems.
3. **Turn on Sign in with Discord**, so players can sign in from Discord Broadcast:
   1. In the Developer Portal, open the first bot's application. Under OAuth2, choose Add Redirect and enter exactly `https://<your domain>/auth/callback`, using the same address as the public address. A redirect that differs in any character makes Discord refuse the sign-in.
   2. Still in OAuth2, choose Reset Secret and copy the Client Secret. Stop the bot and add it to `secrets.json` in the bot's data folder as `"oauthClientSecret": "..."`. Start the bot again. With Docker, instead keep it in `packaging/docker/secrets.env`, which git ignores: in `packaging/docker/`, run `touch secrets.env && chmod 600 secrets.env` so only you can read it, add the line `KZG_SECRET_oauthClientSecret=...` to it in a text editor, and run `docker compose up -d`. Never put it in `kzg.env`: that file is part of the project and anyone can read it. (Data folders: Docker `/data` in the `kzg-data` volume, Mac `~/Library/Application Support/Kzg`, Windows `C:\ProgramData\Kzg`, Linux `/var/lib/kzg`.)
   3. In Discord, run `/gvg config set oauth.clientId <the Client ID>`, then `/gvg config set oauth.enabled true`.

The setup link works once, in one browser, and only from the computer the bot runs on (or through your HTTPS address). Tokens are never shown again.

## Let players join

Each player installs Discord Broadcast (see the [install guide](getting-started.md)) and chooses Sign in with Discord. The sign-in screen has a "Bot address" field: it comes filled in with the address the app was built with, and a player whose bot lives elsewhere types the public address from above there. If that fails, they type `/connect` in Discord and paste the code into Discord Broadcast (it works once, for 10 minutes). Players cannot connect until the public address is set.

### Helping players install

Players get the app from the [download page](https://amadeuspham.github.io/kzg-gvg-bot/) and follow the [install guide](getting-started.md), which leaves out the technical fixes. Keep these for when a player asks you:

- **Check a file.** Every release lists checksums in `SHA256SUMS`. Windows (PowerShell): `Get-FileHash .\DiscordBroadcast-win-x64-Setup.exe -Algorithm SHA256`. Mac (Terminal): `shasum -a 256 ~/Downloads/DiscordBroadcast-macOS-arm64-Setup.pkg`. The sums must match; if not, delete the file and download it again.
- **Windows has no Run anyway button.** In PowerShell: `Unblock-File .\DiscordBroadcast-win-x64-Setup.exe`.
- **macOS says the app is damaged or can't be checked.** Remove the quarantine mark, then open the app again: `xattr -dr com.apple.quarantine "/Applications/Discord Broadcast.app"`.
- **Remove Open at login by hand.** On a Mac delete `~/Library/LaunchAgents/app.kzg.login.plist`; on Windows turn off the `Discord Broadcast` entry under Settings, Apps, Startup.
- **Mac input permission.** The app needs Input Monitoring only, not Accessibility. Windows reads keys with Raw Input, with a low-level hook as fallback.

## If something goes wrong

- **Setup link lost or already used.** Restart the bot; it prints a new one. (Docker: `docker compose restart bot`. Mac: run `install-mac.command` again. Windows: restart the `Kzg bot` task. Linux: `sudo systemctl restart kzg-bot`.)
- **A token is rejected.** The setup page names the bot with the wrong token. Paste a fresh one (Reset Token in the Developer Portal).
- **Players can't connect.** Check the public address: `/gvg config set publicHost bot.example.org`. Then check `curl https://bot.example.org/healthz` prints `ok`.
- **A relay bot is missing from voice.** `/gvg status` shows the bots. It rejoins by itself within seconds; if it does not, restart the bot.
- **A bot shows its account name instead of "A1 Broadcaster".** `/gvg status` says when a bot can't set its nickname. It relays all the same; give its role the Change Nickname permission in Server Settings, and it renames itself after the next `/gvg` change or when the bot restarts.
- **Linux says `GLIBC_2.38` or `GLIBCXX_3.4.32` not found.** Your system is too old (Debian 12, Ubuntu 22.04). Use A. Docker.
- **To change tokens or add bots later.** Start the bot with `--setup` (for example `./start.sh --setup`). Your teams and settings stay.
- **Stop or remove it.** Mac: `./install-mac.command --remove`. Windows: `.\install-windows-task.ps1 -Remove`. Linux: `sudo systemctl disable --now kzg-bot`. Docker: `docker compose down`. Your data is kept.
- **Updating.** Stop the bot, unpack the new version over the old folder, and start it again. Build the new version as described in its part above (B, C or D). Docker: `git pull` and `docker compose up -d --build`. Your data is untouched.

## Next

- [Bot reference](../reference/bot.md): HTTPS behind your own proxy, Docker details, Discord commands, settings, files, permissions, memory cap and service details.
- [Contracts](../reference/contracts.md): the wire and engine contracts.
