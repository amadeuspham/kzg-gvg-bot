# Developer setup

What: what a machine needs to build, test and live-test kzg-gvg-bot, and how to hand work between machines. For developers and their AI assistants; not for running the bot ([host guide](host.md)) or installing the app ([unsigned install](unsigned-install.md)).

## Every machine

- Git and bash. On Windows use Git for Windows' bash; the gate and packaging scripts are bash.
- .NET SDK 10.0.100 or newer 10.0.x (`global.json`). `scripts/dotnet.sh` finds it in `DOTNET_ROOT`, `%LOCALAPPDATA%\Microsoft\dotnet` or `~/.dotnet`, then PATH.
- Check the setup: `scripts/check.sh` must pass. It needs no network, secrets or Discord.
- Claude Code: `CLAUDE.md` loads `AGENTS.md`, which holds the rules. Project permissions are in `.claude/settings.json`; personal ones go in `.claude/settings.local.json` (git-ignored).

## Extra, by task

| Task | Needs |
|---|---|
| Bot or engine work | The gate only. `dotnet run --project src/Kzg.Bot -- --fake` runs offline. |
| App work | Any OS. macOS needs the Input Monitoring permission for the terminal or app running it. Windows key behaviour under an elevated game needs a Windows PC with the game. |
| Live tests (`KZG_LIVE=1`) | A Discord test server with six voice channels (Attack 1-3, Defense 1-3), the bot accounts, and `.data/secrets.json`. Details below. Real network; UDP to Discord voice must be open. |
| Real-clock tests (`KZG_REALTIME=1`) | Any OS, a quiet machine: they assert timing ranges against the wall clock. `KZG_REALTIME=1 dotnet test tests/Kzg.Bot.Tests -c Release`. |
| Load test (`KZG_LOAD=1`) | macOS, for `say`. Five minutes by default. |
| Linux bot | glibc 2.38 or newer (Ubuntu 24.04). Older hosts use the Docker image. |
| Packaging | The host OS for its own target; macOS packages build on a Mac. See README "Packaging". |
| Mac release signing | The maintainer's self-signed code-signing certificate as a password-protected `.p12`, kept outside git; set as a CI secret when releasing. |

## Local data and secrets

`.data/` is the dev data folder, git-ignored except `*.example.json`. Copy `.data/secrets.example.json` to `.data/secrets.json` and fill it from a password manager. Names: `relay1`..`relay6`, `tester1`..`tester3`, `listener1`..`listener6`, `oauthClientSecret`. `KZG_SECRET_<name>` environment variables override the file. Under Docker Compose they come from `packaging/docker/secrets.env`, git-ignored and optional; `kzg.env` is tracked and holds no secrets.

Secrets move between machines by hand (password manager, encrypted copy), never through git, chat, issues or logs. Without them a machine still runs the gate and every offline test.

## Live tests

1. Warn the user: the tests join the real test server's voice channels with every bot.
2. `KZG_LIVE=1 dotnet test tests/Kzg.Bot.Tests -c Release --filter "FullyQualifiedName~Live"`. Output and recordings go to `.data/live/<timestamp>/`.
3. Afterwards every bot must be out of voice. Check the test server's member list. If a run was killed, rerun a live test to let the cleanup run, or remove the bots by hand.
4. A live test skipped with no variable set is normal; it is not a pass.

## GitHub

The repo has no remote yet. When it exists:

1. Create it empty, add the remote, push `main`.
2. CI (`.github/workflows/ci.yml`) runs the gate on macOS, Windows and Ubuntu 24.04 for every PR and push to `main`. It uses no secrets.
3. Turn on branch protection requiring the three gate jobs.
4. Auto-update works once a release exists: the release workflow packs the app with `KZG_UPDATE_FEED` set to the repo address, and the app reads the Velopack files from the repo's releases.
5. The release workflow is manual and stays inert until the secret `KZG_MAC_CERT_P12` exists (also `KZG_MAC_CERT_PASSWORD`, `KZG_MAC_SIGN_IDENTITY`). Review `packaging/pack-app.sh`'s signing variable name against the workflow before the first release. When the Mac, Windows and bot jobs pass, a last job tags `v<version>` at the run's commit and creates the GitHub Release with the bot archives (linux-x64, win-x64, osx-arm64), every file in `velopack-<rid>/`, and `SHA256SUMS`. The version input must equal `<Version>` in `Directory.Build.props`.

## Infrastructure (OCI)

`infra/oci/` holds the Oracle Cloud resources of the bot host (network and the `kzg-broadcaster-instance` VM in eu-frankfurt-1). Change them through a PR, not the OCI console. `.github/workflows/terraform.yml` runs `fmt` and `validate` on every PR that touches `infra/`, a plan on same-repo PRs, and on `main` a plan followed by an apply that waits for approval in the `oci-apply` environment. The approval prompt only appears when the plan has changes. Approve only what the plan job's summary lists; the apply job plans again and stops if the list differs.

The repo is public, so nothing account-specific is in it: OCIDs, the SSH key and the state location come from secrets, and `infra/oci/ci.sh` keeps Terraform's output out of the log (only resource addresses and actions are printed, errors are redacted). Fork PRs get no secrets and only run `fmt` and `validate`.

State lives in an OCI Object Storage bucket (Terraform's `oci` backend, Terraform 1.12 or newer), not in Resource Manager.

### One-time setup

Do these by hand, in this order. Until step 6 is done the plan job fails with a missing-secret error; that is expected.

1. Bucket: in the root compartment, eu-frankfurt-1, create a private bucket (for example `kzg-tfstate`) with object versioning on. Note the Object Storage namespace (Tenancy details).
2. Identities: create two IAM users with API keys only (no console password), in groups `kzg-tf-plan` and `kzg-tf-apply`. Add a policy in the root compartment (the resources live there), replacing the bucket name if yours differs. `inspect compartments` is there because the config looks up the availability domain (`ListAvailabilityDomains` needs `COMPARTMENT_INSPECT`). The plan group cannot write objects, so CI plans never take the state lock; only the apply job does:

   ```text
   Allow group kzg-tf-plan to inspect compartments in tenancy
   Allow group kzg-tf-plan to read virtual-network-family in tenancy
   Allow group kzg-tf-plan to read instance-family in tenancy
   Allow group kzg-tf-plan to read buckets in tenancy where target.bucket.name = 'kzg-tfstate'
   Allow group kzg-tf-plan to read objects in tenancy where target.bucket.name = 'kzg-tfstate'
   Allow group kzg-tf-apply to inspect compartments in tenancy
   Allow group kzg-tf-apply to manage virtual-network-family in tenancy
   Allow group kzg-tf-apply to manage instance-family in tenancy
   Allow group kzg-tf-apply to use volume-family in tenancy
   Allow group kzg-tf-apply to read buckets in tenancy where target.bucket.name = 'kzg-tfstate'
   Allow group kzg-tf-apply to manage objects in tenancy where target.bucket.name = 'kzg-tfstate'
   ```

3. Move the state out of Resource Manager, on your own machine with your own `~/.oci/config`:
   1. Resource Manager, the stack, "Download Terraform state". Keep the file outside the repo.
   2. In `infra/oci/`, copy `backend.hcl.example` to `backend.hcl` and `terraform.tfvars.example` to `terraform.tfvars` and fill in the real values (both git-ignored). `ssh_authorized_keys` must be the instance's current key line exactly, comment included.
   3. `terraform init -backend-config=backend.hcl`, then `terraform state push <downloaded file>`.
   4. `terraform plan` must print "No changes". If it does not, stop and fix the config; do not apply.
   5. Delete the downloaded state file. Commit `infra/oci/.terraform.lock.hcl`: CI installs providers with `-lockfile=readonly` and fails without it.
4. Retire the Resource Manager stack once CI's first plan on `main` shows no changes: delete the stack (this removes only its copy of the state, not the resources). Never run its Destroy job. Two state owners would drift.
5. GitHub environments (Settings, Environments): `oci-plan` with no protection rules; `oci-apply` with you as required reviewer and deployment branches limited to `main`.
6. GitHub secrets:
   - Repository secrets: `OCI_TENANCY_OCID`, `OCI_STATE_NAMESPACE`, `OCI_STATE_BUCKET`, `OCI_INSTANCE_IMAGE_OCID` (the image the instance was built from), `OCI_SSH_AUTHORIZED_KEYS`.
   - In each environment, that environment's identity: `OCI_USER_OCID`, `OCI_FINGERPRINT`, `OCI_PRIVATE_KEY` (the PEM text). `oci-plan` gets the `kzg-tf-plan` user, `oci-apply` the `kzg-tf-apply` user.
7. Turn on branch protection for `main` requiring the `fmt and validate` job too.

## Handing work to another machine

Commit or stash on a branch, push it, then pull on the other machine and read [AGENTS.md](../../AGENTS.md). State not in git (secrets, running test bots, a half-done manual check) is the user's to carry; note it in the PR description or commit body.

## Bot packages and memory

- Build the bot archives with `packaging/publish-bot.sh --rid win-x64`, `--rid linux-x64` or `--rid osx-arm64`. Each holds the bot, the .NET runtime and libdave.
- libdave 1.2.1.9 (Discord voice encryption) is bundled in the archives and the Docker image. It keeps its own license; read upstream's before redistributing a package.
- Memory test, one hour with 30 talkers and 6 broadcasting: the process went from about 135 MB to 143 MB, managed heap flat at 15.7 MB, CPU about 1% of a 14-core Mac. The small remaining growth is outside the managed heap; cause not found. The 512 MB cap in the host guide (Reference, "Memory cap and daily restart") is about three times what a full war needs.
- Log rotation: at 10 MB a log moves to `bot.1.log`, two old files kept (30 MB total). Docker keeps three 10 MB log files per container.
