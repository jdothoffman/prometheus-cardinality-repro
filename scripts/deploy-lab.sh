#!/usr/bin/env bash
# Bring up the cardinality repro stack on Ubuntu (Azure telemetry lab VM, or any Linux host).
# Usage (from repo root on the VM):
#   ./scripts/deploy-lab.sh prereqs   # Docker, k3d, helm, kubectl, make
#   ./scripts/deploy-lab.sh all       # prereqs (if needed) + make all
#   ./scripts/deploy-lab.sh status
#   ./scripts/deploy-lab.sh down
#   ./scripts/deploy-lab.sh ui-hint   # how to reach the consoles via SSH tunnels
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$ROOT"

CLUSTER="${CLUSTER:-cardinality}"
K3D_VERSION="${K3D_VERSION:-v5.8.3}"
# Host has 4 vCPUs on Standard_B4ms; keep node caps inside that envelope.
APP_NODE_CPUS="${APP_NODE_CPUS:-2}"
AGENT_NODE_CPUS="${AGENT_NODE_CPUS:-1}"

log() { printf '\n==> %s\n' "$*"; }
die() { printf 'error: %s\n' "$*" >&2; exit 1; }

need_root_apt() {
  if [[ "$(id -u)" -eq 0 ]]; then
    "$@"
  else
    sudo "$@"
  fi
}

have_docker() {
  command -v docker >/dev/null 2>&1 && docker info >/dev/null 2>&1
}

user_in_docker_group() {
  getent group docker 2>/dev/null | grep -qw "${USER:-}" || id -nG 2>/dev/null | tr ' ' '\n' | grep -qx docker
}

# After usermod -aG docker, this shell still lacks the group. Re-exec under sg docker once.
# Always re-run the original top-level command (e.g. all), not a nested prereqs-only call.
reexec_with_docker_group() {
  [[ "$(id -u)" -eq 0 ]] && return 0
  have_docker && return 0
  user_in_docker_group || return 0
  if [[ "${DEPLOY_LAB_DOCKER_REEXEC:-}" == "1" ]]; then
    die "Docker still not usable under sg docker. Check: sudo systemctl status docker && ls -l /var/run/docker.sock"
  fi
  local top="${DEPLOY_LAB_TOP_CMD:-all}"
  log "Re-running '$top' under sg docker (group membership just applied)"
  exec sg docker -c "cd $(printf '%q' "$ROOT") && DEPLOY_LAB_DOCKER_REEXEC=1 DEPLOY_LAB_TOP_CMD=$(printf '%q' "$top") ./scripts/deploy-lab.sh $(printf '%q' "$top")"
}

cmd_prereqs() {
  log "Installing OS packages"
  need_root_apt apt-get update -y
  need_root_apt DEBIAN_FRONTEND=noninteractive apt-get install -y \
    ca-certificates curl gnupg lsb-release apt-transport-https \
    make git jq uidmap dbus-user-session

  if ! command -v docker >/dev/null 2>&1; then
    log "Installing Docker Engine"
    need_root_apt apt-get install -y docker.io docker-buildx docker-compose-v2
    need_root_apt systemctl enable --now docker
  else
    log "Docker already installed: $(docker --version)"
  fi

  if [[ "$(id -u)" -ne 0 ]]; then
    if ! user_in_docker_group; then
      log "Adding $USER to the docker group"
      need_root_apt usermod -aG docker "$USER"
    fi
  fi

  if ! have_docker; then
    reexec_with_docker_group
    die "Docker is not usable. Check: sudo systemctl status docker"
  fi

  if ! command -v kubectl >/dev/null 2>&1; then
    log "Installing kubectl"
    local arch kver
    arch="$(uname -m)"
    case "$arch" in
      x86_64) arch=amd64 ;;
      aarch64|arm64) arch=arm64 ;;
      *) die "unsupported arch: $arch" ;;
    esac
    kver="$(curl -fsSL https://dl.k8s.io/release/stable.txt)"
    curl -fsSLo /tmp/kubectl "https://dl.k8s.io/release/${kver}/bin/linux/${arch}/kubectl"
    need_root_apt install -m 0755 /tmp/kubectl /usr/local/bin/kubectl
  else
    log "kubectl already installed: $(kubectl version --client -o yaml 2>/dev/null | awk '/gitVersion:/{print $2; exit}')"
  fi

  if ! command -v helm >/dev/null 2>&1; then
    log "Installing Helm"
    curl -fsSL https://raw.githubusercontent.com/helm/helm/main/scripts/get-helm-3 | bash
  else
    log "Helm already installed: $(helm version --short)"
  fi

  if ! command -v k3d >/dev/null 2>&1; then
    log "Installing k3d ${K3D_VERSION}"
    curl -fsSL "https://raw.githubusercontent.com/k3d-io/k3d/main/install.sh" | TAG="${K3D_VERSION}" bash
  else
    log "k3d already installed: $(k3d version | head -1)"
  fi

  log "Prereqs OK"
  docker version --format 'docker {{.Server.Version}}' || docker version
  kubectl version --client --short 2>/dev/null || kubectl version --client
  helm version --short
  k3d version | head -1
  free -h | head -2
  nproc
}

cluster_exists() {
  k3d cluster list -o json 2>/dev/null | jq -e --arg n "$CLUSTER" 'map(select(.name==$n)) | length > 0' >/dev/null 2>&1
}

cmd_all() {
  command -v make >/dev/null 2>&1 || die "make not found; run: ./scripts/deploy-lab.sh prereqs"
  if ! have_docker; then
    reexec_with_docker_group
    die "docker not usable; run: ./scripts/deploy-lab.sh prereqs"
  fi
  command -v k3d >/dev/null 2>&1 || die "k3d not found; run: ./scripts/deploy-lab.sh prereqs"
  command -v helm >/dev/null 2>&1 || die "helm not found; run: ./scripts/deploy-lab.sh prereqs"
  command -v kubectl >/dev/null 2>&1 || die "kubectl not found; run: ./scripts/deploy-lab.sh prereqs"

  if cluster_exists; then
    log "k3d cluster '$CLUSTER' already exists — refreshing monitoring, agent, tracing, app"
    make monitoring
    make agent
    make tracing
    make app
  else
    log "Creating full stack (make all) with APP_NODE_CPUS=${APP_NODE_CPUS} AGENT_NODE_CPUS=${AGENT_NODE_CPUS}"
    make all APP_NODE_CPUS="$APP_NODE_CPUS" AGENT_NODE_CPUS="$AGENT_NODE_CPUS"
  fi

  cmd_status
  cmd_ui_hint
}

cmd_status() {
  log "Cluster / workloads"
  if ! cluster_exists; then
    echo "No k3d cluster named '$CLUSTER'."
    return 0
  fi
  kubectl --context "k3d-${CLUSTER}" get nodes -L pool -o wide || true
  echo
  kubectl --context "k3d-${CLUSTER}" get pods -A -o wide 2>/dev/null | head -60 || true
  echo
  free -h | head -2
}

cmd_down() {
  log "Deleting k3d cluster '$CLUSTER'"
  k3d cluster delete "$CLUSTER" || true
}

cmd_ui_hint() {
  local ip
  ip="$(curl -fsS -H Metadata:true --noproxy '*' \
    'http://169.254.169.254/metadata/instance/network/interface/0/ipv4/ipAddress/0/publicIpAddress?api-version=2021-02-01&format=text' 2>/dev/null || true)"
  [[ -z "$ip" ]] && ip="<vm-public-ip>"

  cat <<EOF

UI is in-cluster only (NSG is SSH-only). From your Mac:

  ssh -L 8080:localhost:8080 -L 3000:localhost:3000 -L 9090:localhost:9090 azureuser@${ip}

On the VM (second SSH session), start port-forwards:

  cd ${ROOT} && make ui

Then open:
  Load test  http://localhost:8080
  Grafana    http://localhost:3000  (admin/admin)
  Prometheus http://localhost:9090

EOF
}

usage() {
  cat <<EOF
Usage: ./scripts/deploy-lab.sh <command>

  prereqs   Install Docker, k3d, helm, kubectl, make (Ubuntu/Debian)
  all       Install prereqs if needed, then bring up the full stack
  status    Show nodes and pods
  down      Delete the k3d cluster
  ui-hint   Print SSH tunnel instructions for the consoles

Env:
  CLUSTER=${CLUSTER}
  APP_NODE_CPUS=${APP_NODE_CPUS}
  AGENT_NODE_CPUS=${AGENT_NODE_CPUS}
  K3D_VERSION=${K3D_VERSION}
EOF
}

main() {
  local cmd="${1:-}"
  export DEPLOY_LAB_TOP_CMD="${DEPLOY_LAB_TOP_CMD:-$cmd}"
  case "$cmd" in
    prereqs) cmd_prereqs ;;
    all)
      if ! have_docker || ! command -v k3d >/dev/null 2>&1 || ! command -v helm >/dev/null 2>&1; then
        cmd_prereqs
      fi
      if ! have_docker; then
        reexec_with_docker_group
      fi
      cmd_all
      ;;
    status)
      if ! have_docker; then reexec_with_docker_group; fi
      cmd_status
      ;;
    down)
      if ! have_docker; then reexec_with_docker_group; fi
      cmd_down
      ;;
    ui-hint) cmd_ui_hint ;;
    -h|--help|help|"") usage; [[ -n "$cmd" ]] || exit 1 ;;
    *) die "unknown command: $cmd (try --help)" ;;
  esac
}

main "$@"
