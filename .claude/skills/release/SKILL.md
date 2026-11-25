---
name: release
description: Prepare a release locally: bump the version, run the gate, build the bot for linux-x64, win-x64 and osx-arm64, and write SHA-256 sums. Stops before publishing or pushing. Use when the user says "release", "cut a release", "prepare a release", "package the bot", or "build the installers".
---

# release

Prepare only. Never commit, tag, push, upload or open a PR; that needs the user's explicit go-ahead (AGENTS.md rule 4, CONTRIBUTING.md). Nothing the scripts build is uploaded.

1. Ask the user for the new version. Do not guess it.
2. Bump `<Version>` in `Directory.Build.props`, the only place it lives.
3. Gate: `scripts/check.sh`. Stop if it fails.
4. Bot, one run per target (output in git-ignored `packaging/out/`):
   - `packaging/publish-bot.sh --rid linux-x64`
   - `packaging/publish-bot.sh --rid win-x64`
   - `packaging/publish-bot.sh --rid osx-arm64`
   It checks the pinned libdave 1.2.1.9 itself; do not bump libdave (rule 6).
5. Sums, with `<v>` the version:
   `cd packaging/out && shasum -a 256 kzg-bot-<v>-linux-x64.tar.gz kzg-bot-<v>-win-x64.zip kzg-bot-<v>-osx-arm64.tar.gz > SHA256SUMS`
   Check each sum against the `sha256:` line the script printed.
6. Hand back: version, gate result, archive names and sums, and what was not run (Mac and Windows app pack, Docker image). CI packs and signs the apps, writes the sums and publishes the GitHub Release in `.github/workflows/release.yml`; locally nothing is uploaded. Per dev-setup.md, check `pack-app.sh`'s signing variable name against `.github/workflows/release.yml` before the first CI release.
