# Hosting the bot

This guide gets the Kzg bot running on your own computer or server. Pick ONE way to run it, follow only that part, then do the two shared parts, "Set it up in Discord" and "Let players join". For players installing KZG Broadcast, see the [unsigned-install guide](unsigned-install.md). Technical background is in [contracts.md](../contracts.md).

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

## A. Docker (easiest)

You need Docker with Compose, and the project folder on the server.

1. Point your domain's DNS at your home address. Forward ports 80 and 443 from your router to the server.
2. Open `packaging/docker/kzg.env` and replace `gvg.example.com` with your domain.
3. In `packaging/docker/`, run:

   ```bash
   docker compose up -d
   ```

   The first run takes a few minutes.
4. Run `docker compose logs bot` and open the setup link it prints (the `https://` one works from any computer).
5. Continue with "Set it up in Discord" below.

## B. Mac

Apple Silicon only (M1 or newer).

1. Unpack `kzg-bot-<version>-osx-arm64.tar.gz` and move the folder somewhere it can stay, such as your home folder. Don't move it later.
2. Double-click `install-mac.command`. If macOS won't open it, Control-click it, choose Open, then Open again.
3. Your browser opens the setup page by itself. If it doesn't, find the link in `~/Library/Logs/Kzg/bot.log` (open it with Console).
4. Point your domain's DNS at your home address. Forward ports 80 and 443 from your router to this Mac.
5. In Terminal, install Caddy and give it your domain (replace `gvg.yourguild.com`):

   ```bash
   brew install caddy
   printf 'gvg.yourguild.com {\n    reverse_proxy 127.0.0.1:7878\n}\n' > "$(brew --prefix)/etc/Caddyfile"
   brew services start caddy
   ```

   `brew services` keeps Caddy running after a reboot.
6. Check it: `curl https://gvg.yourguild.com/healthz` prints `ok`.
7. Continue with "Set it up in Discord" below.

The bot now starts at login, restarts after a crash, and keeps the Mac awake. Leave the Mac logged in.

## C. Windows

1. Unzip `kzg-bot-<version>-win-x64.zip`, for example into `C:\Kzg\bot`.
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
   gvg.yourguild.com {
       reverse_proxy 127.0.0.1:7878
   }
   ```

7. In the same elevated PowerShell, allow the ports and install Caddy as a service that starts with the PC:

   ```powershell
   New-NetFirewallRule -DisplayName 'Caddy' -Direction Inbound -Protocol TCP -LocalPort 80,443 -Action Allow
   sc.exe create caddy start= auto binPath= "C:\caddy\caddy.exe run --config C:\caddy\Caddyfile"
   sc.exe start caddy
   ```

8. Check it: `curl.exe https://gvg.yourguild.com/healthz` prints `ok`.
9. Continue with "Set it up in Discord" below.

The bot now starts with the PC and restarts after a crash.

## D. Linux

1. Unpack the bot and install the service:

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
5. Give Caddy your domain (replace `gvg.yourguild.com`) and reload it:

   ```bash
   printf 'gvg.yourguild.com {\n    reverse_proxy 127.0.0.1:7878\n}\n' | sudo tee /etc/caddy/Caddyfile
   sudo systemctl reload caddy
   ```

6. Check it: `curl https://gvg.yourguild.com/healthz` prints `ok`.
7. Continue with "Set it up in Discord" below.

## Set it up in Discord

1. **On the setup page**, follow its three steps:
   1. In the [Discord Developer Portal](https://discord.com/developers/applications), make one bot per team voice channel as the page describes, naming each application `KZG Broadcaster`, and paste each token. The fields are named for the channels the bots fill: A1 Broadcaster to A3 Broadcaster for the first to third Attack channels, D1 Broadcaster to D3 Broadcaster for Defense. With fewer bots, leave the fields of channels you don't use empty.
   2. Enter your domain under "Public address" (for example `gvg.yourguild.com`). Docker fills it in for you.
   3. Use each bot's Invite button (Invite A1 Broadcaster, and so on) to add it to your server, then choose Finish setup.
2. **In Discord**, run `/gvg setup`. Pick the Attack and Defense voice channels. A relay bot joins each channel: A1 Broadcaster the channel you pick as `attack-1`, D1 Broadcaster the one you pick as `defense-1`, and so on. A channel whose bot you left out takes a bot whose channel you didn't pick. Each bot sets its server nickname to its channel's name. Optionally set `channel` to a text channel where the bot reports problems.
3. **Turn on Sign in with Discord**, so players can sign in from KZG Broadcast:
   1. In the Developer Portal, open the first bot's application. Under OAuth2, choose Add Redirect and enter exactly `https://<your domain>/auth/callback`, using the same address as the public address. A redirect that differs in any character makes Discord refuse the sign-in.
   2. Still in OAuth2, choose Reset Secret and copy the Client Secret. Stop the bot and add it to `secrets.json` in the bot's data folder as `"oauthClientSecret": "..."`. Start the bot again. With Docker, instead keep it in `packaging/docker/secrets.env`, which git ignores: in `packaging/docker/`, run `touch secrets.env && chmod 600 secrets.env` so only you can read it, add the line `KZG_SECRET_oauthClientSecret=...` to it in a text editor, and run `docker compose up -d`. Never put it in `kzg.env`: that file is part of the project and anyone can read it. (Data folders: Docker `/data` in the `kzg-data` volume, Mac `~/Library/Application Support/Kzg`, Windows `C:\ProgramData\Kzg`, Linux `/var/lib/kzg`.)
   3. In Discord, run `/gvg config set oauth.clientId <the Client ID>`, then `/gvg config set oauth.enabled true`.

The setup link works once, in one browser, and only from the computer the bot runs on (or through your HTTPS address). Tokens are never shown again.

## Let players join

Each player installs KZG Broadcast (see the [unsigned-install guide](unsigned-install.md)) and chooses Sign in with Discord. If that fails, they type `/connect` in Discord and paste the code into KZG Broadcast (it works once, for 10 minutes). Players cannot connect until the public address is set.

## If something goes wrong

- **Setup link lost or already used.** Restart the bot; it prints a new one. (Docker: `docker compose restart bot`. Mac: run `install-mac.command` again. Windows: restart the `Kzg bot` task. Linux: `sudo systemctl restart kzg-bot`.)
- **A token is rejected.** The setup page names the bot with the wrong token. Paste a fresh one (Reset Token in the Developer Portal).
- **Players can't connect.** Check the public address: `/gvg config set publicHost gvg.yourguild.com`. Then check `curl https://gvg.yourguild.com/healthz` prints `ok`.
- **A relay bot is missing from voice.** `/gvg status` shows the bots. It rejoins by itself within seconds; if it does not, restart the bot.
- **A bot shows its account name instead of "A1 Broadcaster".** `/gvg status` says when a bot can't set its nickname. It relays all the same; give its role the Change Nickname permission in Server Settings, and it renames itself after the next `/gvg` change or when the bot restarts.
- **Linux says `GLIBC_2.38` or `GLIBCXX_3.4.32` not found.** Your system is too old (Debian 12, Ubuntu 22.04). Use A. Docker.
- **To change tokens or add bots later.** Start the bot with `--setup` (for example `./start.sh --setup`). Your teams and settings stay.
- **Stop or remove it.** Mac: `./install-mac.command --remove`. Windows: `.\install-windows-task.ps1 -Remove`. Linux: `sudo systemctl disable --now kzg-bot`. Docker: `docker compose down`. Your data is kept.
- **Updating.** Stop the bot, unpack the new version over the old folder, and start it again (Docker: `docker compose up -d --build`). Your data is untouched.

<details>
<summary>Reference</summary>

### Put it behind HTTPS

Parts A to D set this up for you. Any reverse proxy with a certificate a browser accepts works, in front of `127.0.0.1:7878`. It must pass four paths to the bot (`/ws`, `/auth/start`, `/auth/callback`, `/healthz`), allow WebSocket upgrades, and keep connections open for hours.

nginx (certificate from certbot or your own):

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    server_name kzg.example.com;
    ssl_certificate     /etc/letsencrypt/live/kzg.example.com/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/kzg.example.com/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:7878;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_read_timeout 1h;
        proxy_send_timeout 1h;
        proxy_buffering off;
    }
}
```

Check it: `curl https://kzg.example.com/healthz` prints `ok`.

### Docker details

- Already have a web server on ports 80 and 443? Skip Caddy: `docker compose up -d bot` listens on `127.0.0.1:7878`; point your server at it (above). Still set `KZG_PUBLIC_HOST` in `kzg.env`.
- Secrets: `KZG_SECRET_<name>` lines go in `packaging/docker/secrets.env` (git-ignored, `chmod 600`), never in `kzg.env`. Compose starts without it. Run `docker compose up -d` after changing it.
- Logs: `docker compose logs -f bot`. Restart: `docker compose restart bot`. The bot's files live in the `kzg-data` volume; back it up.
- Without Compose, with files in a folder of your own:

  ```bash
  docker build -f packaging/docker/Dockerfile -t kzg-bot .
  sudo mkdir -p /srv/kzg && sudo chown 1654:1654 /srv/kzg
  docker run -d --name kzg-bot --restart unless-stopped --memory 512m --memory-swap 512m \
      --log-opt max-size=10m --log-opt max-file=3 \
      -e KZG_PUBLIC_HOST=gvg.yourguild.com -p 127.0.0.1:7878:7878 -v /srv/kzg:/data kzg-bot
  docker logs kzg-bot
  ```

  UID 1654 is the image's `app` user. Publish the port on `127.0.0.1` only.

### Discord commands

- `/gvg setup`: up to three Attack channels (`attack-1` to `attack-3`), up to three Defense, and optionally `channel`. It sets the teams to exactly these channels. Rerun it any time.
- `/gvg alerts channel:#admins` sets the alerts channel; `/gvg alerts` with no channel stops alerts. The bot posts a test message or names the missing permission.
- `/gvg status` shows bots and channels. There are no shot callers: every player has open mic and both keys, and a `shotCallerUserId` left in an older `config.json` is ignored. `/gvg bind`, `/gvg unbind`, `/gvg bot assign` change one thing. `/gvg stop` takes every bot out of voice until `/gvg start`.
- A bot is named after the team channel it is in now: the second Attack channel's bot is "A2 Broadcaster", in every reply, status line, alert and log, and as its server nickname. The name follows the bot when `/gvg bot assign` moves it or `/gvg unbind` shifts the channels after one. A bot with no team channel goes by its `secrets.json` name, such as "relay5 (spare)", which is also how `/gvg bot assign` lists it.
- `/gvg config set publicHost gvg.yourguild.com` sets the players' address.
- `/jungle` is open to every member, not only admins. `start` runs the match clock from 35:00; `sync time:MM:SS` sets it to the in-game time (from 35:00 down to 00:00); `pause`, `resume` and `stop` do what they say; `status` shows the clock and the next call; `test` plays a call now. Calls are spoken 40 and 20 seconds before the jungle spawns at 25:00, 20:00 (with halftime), 15:00, 10:00 and 05:00, in every team channel that has a relay bot in it. Replies are private to whoever ran the command. Admins set the loudness with `/gvg config set jungleVolume`; a call already playing finishes at the old level. The clock is kept in memory only, so a bot restart loses it and players run `/jungle sync` again.
- Alerts: with a channel set, the bot posts when relay bots have been out for 30 seconds, when they are back, and when it restarts after an unexpected stop. `/gvg bot assign` is the only way to change a bot's channel.

### Sign in with Discord

Steps are in "Set it up in Discord". Sign-in asks for the `identify` scope only, then checks with the bot's own token that the account is in your server.

### Bot account permissions

Invite buttons carry: View Channel, Connect, Speak, Move Members, Change Nickname, and for the first bot also Send Messages and slash commands. Change Nickname lets a bot show its team name; a bot invited without it relays anyway. Move Members lets a bot enter a full channel; without it, set each channel's user limit to at least six. Leave every Privileged Gateway Intent off.

### Files and settings

The bot keeps `config.json` (settings, teams, bots), `secrets.json` (tokens, optional OAuth secret; readable by its owner only) and `devices.json` (linked apps) in its data folder, set by `--data-dir`. Back all three up; without `devices.json` every player links again. Defaults: `%LOCALAPPDATA%\Kzg` (Windows), `~/Library/Application Support/Kzg` (macOS), `~/.local/share/Kzg` (Linux). The Linux, Docker and Windows installs use `/var/lib/kzg`, `/data` and `C:\ProgramData\Kzg`. `--log-file <file>` logs to a file capped at 30 MB.

You can edit the files while the bot is stopped (`config.example.json` and `secrets.example.json` ship in each archive). `KZG_SECRET_<name>` (for example `KZG_SECRET_relay1`) overrides `secrets.json`; `secrets.json` maps each `bots[].tokenRef` to its token.

| Setting | Default | Meaning |
| --- | --- | --- |
| `publicHost` | none | Name apps connect to, such as `kzg.example.com`, optionally `:port`. Goes into every connect code. |
| `listen` | `127.0.0.1:7878` | Plain HTTP, behind the proxy. Keep on loopback. |
| `guildId` | none | Your server's ID. Empty: the primary bot's one server; with several the bot refuses to start until set. |
| `adminRoleId` | none | A role that may use `/gvg` besides Manage Server. |
| `oauth.enabled`, `oauth.clientId` | off | Discord sign-in. |
| `bots` | one bot, `bot1` | Each relay bot: `tokenRef` and `primary`. Exactly one is primary. |
| `prerollMs`, `maxHoldMs`, `deviceTokenDays` | 300, 120000, 90 | Pre-roll before a key press, longest key hold, how long an unused link lasts. |
| `jungleVolume` | 32 | How loud `/jungle` calls are, 1 to 100. 100 is the level the original jungle bot used, which was too loud in games; 32 is about 10 dB quieter. |
| `alertsChannelId` | none | Alerts text channel, set by `/gvg setup` or `/gvg alerts`. |

### Memory cap and daily restart

The bot is held to 512 MB with no swap and is restarted if it passes that: systemd `MemoryMax=512M` and `MemorySwapMax=0`, Docker `--memory 512m --memory-swap 512m` with `--restart unless-stopped`. A kill leaves bots listed in voice briefly; apps reconnect on their own. If the cap was hit (`journalctl -u kzg-bot` shows `oom-kill`; `docker inspect kzg-bot --format '{{.State.OOMKilled}}'` prints `true`), raise it and tell whoever maintains the bot.

A daily restart is optional and off by default. It ends a war call in progress, so pick a quiet hour.

- systemd: `sudo cp /opt/kzg-bot/kzg-bot-restart.service /opt/kzg-bot/kzg-bot-restart.timer /etc/systemd/system/`, edit `OnCalendar` (05:00 by default), then `sudo systemctl daemon-reload && sudo systemctl enable --now kzg-bot-restart.timer`.
- Docker: cron `0 5 * * * docker restart kzg-bot`.
- Windows: `schtasks /Create /TN "Kzg bot daily restart" /SC DAILY /ST 05:00 /RU SYSTEM /TR "powershell -NoProfile -Command \"Stop-ScheduledTask -TaskName 'Kzg bot'; Start-Sleep 20; Start-ScheduledTask -TaskName 'Kzg bot'\""`.

### Service details

- Mac: service file `~/Library/LaunchAgents/com.kzg.bot.plist`; data `~/Library/Application Support/Kzg`; log `~/Library/Logs/Kzg/bot.log`. The installer removes the download quarantine and signs the bot for this Mac only. It restarts the bot within 10 seconds of a crash.
- Windows: the task runs as SYSTEM, restarts the bot a minute after a failure, and limits `C:\ProgramData\Kzg` to SYSTEM and administrators. Stopping the task skips the Discord sign-out, so a bot may stay listed in voice briefly. For a clean stop, run `start.cmd --data-dir C:\ProgramData\Kzg` under a wrapper that sends Ctrl+C, such as NSSM.
- Linux: the unit runs as user `kzg`, stops the bot with SIGTERM and restarts it on failure.
- To run in a terminal first: `./start.sh --data-dir /var/lib/kzg` (Linux) or `start.cmd --data-dir C:\ProgramData\Kzg` (Windows). Ctrl+C signs every bot out. A wrong token or bad `config.json` stops it with a one-line reason.

</details>
