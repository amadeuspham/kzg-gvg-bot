#!/usr/bin/env bash
# Terraform for CI. The repo and its Actions logs are public, so Terraform's own output (which
# holds OCIDs, IPs and attribute values) goes to a file and is never printed. Only the change
# summary (resource addresses and actions) is printed; errors are printed with identifiers redacted.
#
#   ci.sh init                      backend from KZG_TF_BUCKET, KZG_TF_NAMESPACE, KZG_TF_REGION
#   ci.sh plan <summary-file> [--no-lock]
#                                   writes ./tfplan and the summary; exit 0 no changes, 2 changes
#   ci.sh apply                     applies ./tfplan
set -euo pipefail

cd "$(dirname "$0")"
log="${RUNNER_TEMP:-${TMPDIR:-/tmp}}/kzg-tf.log"
export TF_IN_AUTOMATION=1
export TF_INPUT=0
unset TF_LOG TF_LOG_CORE TF_LOG_PROVIDER

redact() {
  sed -E \
    -e 's/ocid1\.[A-Za-z0-9._-]+/ocid1.<redacted>/g' \
    -e 's/[A-Za-z0-9._%+-]+@[A-Za-z0-9.-]+\.[A-Za-z]{2,}/<email>/g' \
    -e 's/([0-9]{1,3}\.){3}[0-9]{1,3}/<ip>/g' \
    -e 's/(opc-request-id|[Nn]amespace|[Bb]ucket)([":= ]+)[A-Za-z0-9\/._-]+/\1\2<redacted>/g'
}

# Runs a Terraform command quietly. Exit codes listed in $ok pass through; anything else prints
# the redacted tail of the log and fails.
run() {
  local ok="$1"
  shift
  local rc=0
  "$@" >"$log" 2>&1 || rc=$?
  for code in $ok; do
    if [ "$rc" -eq "$code" ]; then
      return "$rc"
    fi
  done
  echo "terraform $2 failed (exit $rc). Redacted output:" >&2
  grep -v '^\s*$' "$log" | tail -n 60 | redact >&2
  exit 1
}

cmd="${1:-}"
case "$cmd" in
init)
  : "${KZG_TF_BUCKET:?}" "${KZG_TF_NAMESPACE:?}" "${KZG_TF_REGION:?}"
  run 0 terraform init -no-color -lockfile=readonly \
    -backend-config="bucket=$KZG_TF_BUCKET" \
    -backend-config="namespace=$KZG_TF_NAMESPACE" \
    -backend-config="region=$KZG_TF_REGION"
  echo "init ok"
  ;;
plan)
  summary="${2:?summary file}"
  lock=true
  if [ "${3:-}" = "--no-lock" ]; then lock=false; fi
  rc=0
  run "0 2" terraform plan -no-color -lock="$lock" -detailed-exitcode -out=tfplan || rc=$?
  # Addresses and actions only: no attribute values, so safe for a public log and artifact.
  terraform show -json tfplan >"${log}.json" 2>/dev/null
  jq -r '
    [.resource_changes[]? | select(.change.actions != ["no-op"] and .change.actions != ["read"])
      | "\(.change.actions | join("+")) \(.address)"]
    + [.output_changes // {} | to_entries[] | select(.value.actions != ["no-op"])
      | "\(.value.actions | join("+")) output.\(.key)"]
    | sort | .[]' "${log}.json" >"$summary"
  rm -f "${log}.json"
  if [ -s "$summary" ]; then
    echo "Planned changes:"
    cat "$summary"
  else
    echo "No changes."
  fi
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    {
      echo "### Terraform plan (infra/oci)"
      if [ -s "$summary" ]; then
        echo '```'
        cat "$summary"
        echo '```'
      else
        echo "No changes."
      fi
    } >>"$GITHUB_STEP_SUMMARY"
  fi
  exit "$rc"
  ;;
apply)
  run 0 terraform apply -no-color tfplan
  echo "apply ok"
  ;;
*)
  echo "usage: ci.sh init | plan <summary-file> [--no-lock] | apply" >&2
  exit 64
  ;;
esac
