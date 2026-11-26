# Infrastructure

How the bot host on Oracle Cloud is managed with Terraform and GitHub Actions, and the one-time setup it needs. For maintainers; see the [developer setup](../../docs/guide/development.md) for everything else.

## Overview

`infra/oci/` holds the Oracle Cloud resources of the bot host (network and the `kzg-broadcaster-instance` VM in eu-frankfurt-1). Change them through a PR, not the OCI console. `.github/workflows/terraform.yml` runs `fmt` and `validate` on every PR that touches `infra/`, a plan on same-repo PRs, and on `main` a plan followed by an apply that waits for approval in the `oci-apply` environment. The approval prompt only appears when the plan has changes. Approve only what the plan job's summary lists; the apply job plans again and stops if the list differs.

The repo is public, so nothing account-specific is in it: OCIDs, the SSH key and the state location come from secrets, and `infra/oci/ci.sh` keeps Terraform's output out of the log (only resource addresses and actions are printed, errors are redacted). Fork PRs get no secrets and only run `fmt` and `validate`.

State lives in an OCI Object Storage bucket (Terraform's `oci` backend, Terraform 1.12 or newer), not in Resource Manager.

## One-time setup

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
