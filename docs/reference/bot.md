# Bot reference

Details for people who run the Kzg bot: proxy and Docker options, Discord commands, settings, files, permissions, the memory cap and how each install runs as a service. To get the bot running first, follow the [hosting guide](../guide/hosting.md).

## Put it behind HTTPS

[Parts A to D](../guide/hosting.md#pick-one-way-to-run-it) set this up for you. Any reverse proxy with a certificate a browser accepts works, in front of `127.0.0.1:7878`. It must pass four paths to the bot (`/ws`, `/auth/start`, `/auth/callback`, `/healthz`), allow WebSocket upgrades, keep connections open for hours, and set `X-Forwarded-For` to the player's address (Caddy does by itself), or every player shares one rate limit.

nginx (certificate from certbot or your own):

```nginx
map $http_upgrade $connection_upgrade {
    default upgrade;
    ''      close;
}

server {
    listen 443 ssl;
    server_name bot.example.org;
    ssl_certificate     /etc/letsencrypt/live/bot.example.org/fullchain.pem;
    ssl_certificate_key /etc/letsencrypt/live/bot.example.org/privkey.pem;

    location / {
        proxy_pass http://127.0.0.1:7878;
        proxy_http_version 1.1;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection $connection_upgrade;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-For $remote_addr;
        proxy_read_timeout 1h;
        proxy_send_timeout 1h;
        proxy_buffering off;
    }
}
```

Check it: `curl https://bot.example.org/healthz` prints `ok`.

## Docker details

- Already have a web server on ports 80 and 443? Skip Caddy: `docker compose up -d bot` listens on `127.0.0.1:7878`; point your server at it (see "Put it behind HTTPS" above). Still set `KZG_PUBLIC_HOST` in `kzg.env`.
- Secrets: `KZG_SECRET_<name>` lines go in `packaging/docker/secrets.env` (git-ignored, `chmod 600`), never in `kzg.env`. Compose starts without it. Run `docker compose up -d` after changing it.
- Logs: `docker compose logs -f bot`. Restart: `docker compose restart bot`. The bot's files live in the `kzg-data` volume; back it up.
- Without Compose, with files in a folder of your own:

  ```bash
  docker build -f packaging/docker/Dockerfile -t kzg-bot .
  sudo mkdir -p /srv/kzg && sudo chown 1654:1654 /srv/kzg
  docker run -d --name kzg-bot --restart unless-stopped --memory 512m --memory-swap 512m \
      --log-opt max-size=10m --log-opt max-file=3 \
      -e KZG_PUBLIC_HOST=bot.example.org -p 127.0.0.1:7878:7878 -v /srv/kzg:/data kzg-bot
  docker logs kzg-bot
  ```

  UID 1654 is the image's `app` user. Publish the port on `127.0.0.1` only.

## Discord commands

`/gvg` commands are for admins (Manage Server, or the role in `adminRoleId`). `/jungle` is open to every member.

| Command | What it does |
| --- | --- |
| `/gvg setup` | Takes up to three Attack channels (`attack-1` to `attack-3`), up to three Defense, and optionally `channel`. It sets the teams to exactly these channels. Rerun it any time. |
| `/gvg alerts channel:#admins` | Sets the alerts channel. `/gvg alerts` with no channel stops alerts. The bot posts a test message or names the missing permission. |
| `/gvg status` | Shows bots and channels. |
| `/gvg bind`, `/gvg unbind`, `/gvg bot assign` | Change one thing each. `/gvg bot assign` is the only way to change a bot's channel. |
| `/gvg stop`, `/gvg start` | `stop` takes every bot out of voice until `start`. The stop is saved in `config.json`, so it survives a restart of the bot or the server. |
| `/gvg config set publicHost bot.example.org` | Sets the players' address. |
| `/gvg config set jungleVolume <1-100>` | Sets how loud `/jungle` calls are. A call already playing finishes at the old level. |
| `/jungle start` | Runs the match clock from 35:00. |
| `/jungle sync time:MM:SS` | Sets the clock to the in-game time (from 35:00 down to 00:00). |
| `/jungle pause`, `resume`, `stop` | Do what they say. |
| `/jungle status` | Shows the clock and the next call. |
| `/jungle test` | Plays a call now. |

Notes:

- There are no shot callers: every player has open mic and both keys, and a `shotCallerUserId` left in an older `config.json` is ignored.
- A bot is named after the team channel it is in now: the second Attack channel's bot is "A2 Broadcaster", in every reply, status line, alert and log, and as its server nickname. The name follows the bot when `/gvg bot assign` moves it or `/gvg unbind` shifts the channels after one. A bot with no team channel goes by its `secrets.json` name, such as "relay5 (spare)", which is also how `/gvg bot assign` lists it.
- Jungle calls are spoken 40 and 20 seconds before the jungle spawns at 25:00, 20:00 (with halftime), 15:00, 10:00 and 05:00, in every team channel that has a relay bot in it. Replies are private to whoever ran the command. The clock is kept in memory only, so a bot restart loses it and players run `/jungle sync` again.
- Alerts: with a channel set, the bot posts when relay bots have been out for 30 seconds, when they are back, and when it restarts after an unexpected stop.

## Sign in with Discord

Steps are in "[Set it up in Discord](../guide/hosting.md#set-it-up-in-discord)". Sign-in asks for the `identify` scope only, then checks with the bot's own token that the account is in your server.

## Bot account permissions

Invite buttons carry: View Channel, Connect, Speak, Move Members, Change Nickname, and for the first bot also Send Messages and slash commands. Change Nickname lets a bot show its team name; a bot invited without it relays anyway. Move Members lets a bot enter a full channel; without it, set each channel's user limit to at least six. Leave every Privileged Gateway Intent off.

## Files and settings

The bot keeps `config.json` (settings, teams, bots), `secrets.json` (tokens, optional OAuth secret; readable by its owner only) and `devices.json` (linked apps) in its data folder, set by `--data-dir`. Back all three up; without `devices.json` every player links again. Defaults: `%LOCALAPPDATA%\Kzg` (Windows), `~/Library/Application Support/Kzg` (macOS), `~/.local/share/Kzg` (Linux). The Linux, Docker and Windows installs use `/var/lib/kzg`, `/data` and `C:\ProgramData\Kzg`. `--log-file <file>` logs to a file capped at 30 MB.

You can edit the files while the bot is stopped (`config.example.json` and `secrets.example.json` ship in each archive). `KZG_SECRET_<name>` (for example `KZG_SECRET_relay1`) overrides `secrets.json`; `secrets.json` maps each `bots[].tokenRef` to its token.

| Setting | Default | Meaning |
| --- | --- | --- |
| `publicHost` | none | Name apps connect to, such as `bot.example.org`, optionally `:port`. Goes into every connect code. |
| `listen` | `127.0.0.1:7878` | Plain HTTP, behind the proxy. Keep on loopback. |
| `trustedProxies` | loopback and private ranges | Proxies whose `X-Forwarded-For` names the player, as IPs or CIDR networks such as `10.0.0.0/8`. Each player is rate limited by their own address only through these; `[]` ignores the header. If the log says a proxy isn't in `trustedProxies`, add its address. |
| `guildId` | none | Your server's ID. Empty: the primary bot's one server; with several the bot refuses to start until set. |
| `adminRoleId` | none | A role that may use `/gvg` besides Manage Server. |
| `oauth.enabled`, `oauth.clientId` | off | Discord sign-in. |
| `bots` | one bot, `bot1` | Each relay bot: `tokenRef` and `primary`. Exactly one is primary. |
| `prerollMs`, `maxHoldMs`, `deviceTokenDays` | 300, 120000, 90 | Pre-roll before a key press, longest key hold, how long an unused link lasts. |
| `jungleVolume` | 32 | How loud `/jungle` calls are, 1 to 100. 100 is the level the original jungle bot used, which was too loud in games; 32 is about 10 dB quieter. |
| `alertsChannelId` | none | Alerts text channel, set by `/gvg setup` or `/gvg alerts`. |

## Memory cap and daily restart

The bot is held to 512 MB with no swap and is restarted if it passes that: systemd `MemoryMax=512M` and `MemorySwapMax=0`, Docker `--memory 512m --memory-swap 512m` with `--restart unless-stopped`. A kill leaves bots listed in voice briefly; apps reconnect on their own. If the cap was hit (`journalctl -u kzg-bot` shows `oom-kill`; `docker inspect kzg-bot --format '{{.State.OOMKilled}}'` prints `true`), raise it and tell whoever maintains the bot.

A daily restart is optional and off by default. It ends a team call in progress, so pick a quiet hour.

- systemd: `sudo cp /opt/kzg-bot/kzg-bot-restart.service /opt/kzg-bot/kzg-bot-restart.timer /etc/systemd/system/`, edit `OnCalendar` (05:00 by default), then `sudo systemctl daemon-reload && sudo systemctl enable --now kzg-bot-restart.timer`.
- Docker: cron `0 5 * * * docker restart kzg-bot`.
- Windows: `schtasks /Create /TN "Kzg bot daily restart" /SC DAILY /ST 05:00 /RU SYSTEM /TR "powershell -NoProfile -Command \"Stop-ScheduledTask -TaskName 'Kzg bot'; Start-Sleep 20; Start-ScheduledTask -TaskName 'Kzg bot'\""`.

## Service details

- Mac: service file `~/Library/LaunchAgents/com.kzg.bot.plist`; data `~/Library/Application Support/Kzg`; log `~/Library/Logs/Kzg/bot.log`. The installer removes the download quarantine and signs the bot for this Mac only. It restarts the bot within 10 seconds of a crash.
- Windows: the task runs as SYSTEM, restarts the bot a minute after a failure, and limits `C:\ProgramData\Kzg` to SYSTEM and administrators. Stopping the task skips the Discord sign-out, so a bot may stay listed in voice briefly. For a clean stop, run `start.cmd --data-dir C:\ProgramData\Kzg` under a wrapper that sends Ctrl+C, such as NSSM.
- Linux: the unit runs as user `kzg`, stops the bot with SIGTERM and restarts it on failure.
- To run in a terminal first: `./start.sh --data-dir /var/lib/kzg` (Linux) or `start.cmd --data-dir C:\ProgramData\Kzg` (Windows). Ctrl+C signs every bot out. A wrong token or bad `config.json` stops it with a one-line reason.
