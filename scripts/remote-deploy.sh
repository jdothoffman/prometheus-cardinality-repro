#!/usr/bin/env bash
# From your Mac: sync this repo to the Azure telemetry lab VM and run deploy-lab.sh there.
# Usage:
#   ./scripts/remote-deploy.sh              # rsync + ./scripts/deploy-lab.sh all
#   ./scripts/remote-deploy.sh prereqs
#   ./scripts/remote-deploy.sh status
#   ./scripts/remote-deploy.sh down
#   VM_HOST=azureuser@1.2.3.4 ./scripts/remote-deploy.sh all
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
# shellcheck disable=SC1091
[[ -f "$ROOT/.lab.env" ]] && source "$ROOT/.lab.env"
VM_HOST="${VM_HOST:-}"
REMOTE_DIR="${REMOTE_DIR:-~/grafanPromPoc}"
CMD="${1:-all}"

log() { printf '==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

[[ -n "$VM_HOST" ]] || die "Set VM_HOST (e.g. azureuser@x.x.x.x) or copy .lab.env.example → .lab.env"

log "Syncing $ROOT → ${VM_HOST}:${REMOTE_DIR}"
rsync -az --delete \
  --exclude '.git/' \
  --exclude 'reports/' \
  --exclude 'bin/' \
  --exclude 'obj/' \
  --exclude '.DS_Store' \
  --exclude '*.user' \
  "$ROOT/" "${VM_HOST}:${REMOTE_DIR}/"

log "Running deploy-lab.sh ${CMD} on ${VM_HOST}"
# Use a login shell so a fresh docker group membership is picked up after prereqs.
ssh -t "$VM_HOST" "cd ${REMOTE_DIR} && chmod +x scripts/deploy-lab.sh && bash -lc './scripts/deploy-lab.sh ${CMD}'"
