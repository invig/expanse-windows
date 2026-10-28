#!/bin/bash
# Drives the Windows 11 on Arm dev VM (UTM, VM named "Windows") from the Mac.
#
#   scripts/vm.sh setup            one-time: OpenSSH with this Mac's key, .NET 10 SDK (via UTM's guest agent)
#   scripts/vm.sh sync             copy the source to %USERPROFILE%\expanse-windows in the VM
#   scripts/vm.sh build [PROJ...]  sync and build (default: every project under src/)
#   scripts/vm.sh start            (re)start Expanse on the VM's desktop
#   scripts/vm.sh stop             quit Expanse (every window goes back where it was)
#   scripts/vm.sh ctl CMD...       expansectl: state, zoomout, pick TITLE, back, bench S, log...
#   scripts/vm.sh desk EXE [ARGS]  run a program on the VM's desktop (EXE in the VM's terms)
#   scripts/vm.sh guest STEPS...   the Guest helper on the desktop: shot PATH | click X Y | key COMBO | wait MS
#   scripts/vm.sh shot [FILE]      the VM's screen as a PNG (default out/shot.png)
#   scripts/vm.sh run CMD          run CMD over SSH (cmd.exe)
#   scripts/vm.sh ps CMD           run a PowerShell command over SSH
#   scripts/vm.sh snapshot NAME    save the VM's state; `restore NAME` goes back to it
#
# VM_HOST and VM_USER come from .vm.env (written by setup) or the environment.
set -euo pipefail

ROOT=$(cd "$(dirname "$0")/.." && pwd)
UTMCTL=/Applications/UTM.app/Contents/MacOS/utmctl
VM_NAME=${VM_NAME:-Windows}
VM_KEY=${VM_KEY:-$HOME/.ssh/windows_vm_ed25519}
[ -f "$ROOT/.vm.env" ] && . "$ROOT/.vm.env"
REPO='expanse-windows'
BIN="$REPO\\artifacts\\bin"

exe() { echo "$BIN\\$1\\debug_win-arm64\\$2.exe"; }

vm_host() {
  if [ -z "${VM_HOST:-}" ]; then
    VM_HOST=$("$UTMCTL" ip-address "$VM_NAME" 2>/dev/null | grep -Eo '^[0-9]+(\.[0-9]+){3}$' | grep -v '^169\.254\.' | head -1 || true)
  fi
  [ -n "$VM_HOST" ] || { echo "can't find the VM's address: is it running, with UTM's guest tools?" >&2; exit 1; }
  echo "$VM_HOST"
}

ssh_vm() {
  ssh -i "$VM_KEY" -o BatchMode=yes -o LogLevel=ERROR -o StrictHostKeyChecking=accept-new \
    -o ConnectTimeout=5 "${VM_USER:?run scripts/vm.sh setup first}@$(vm_host)" "$@"
}

agent() { "$UTMCTL" exec "$VM_NAME" --cmd "$@"; }

setup() {
  "$UTMCTL" file push "$VM_NAME" 'C:\ProgramData\expanse-setup.ps1' < "$ROOT/scripts/guest/setup.ps1"
  "$UTMCTL" file push "$VM_NAME" 'C:\ProgramData\expanse-mac.pub' < "$VM_KEY.pub"
  echo "setting up OpenSSH and the .NET SDK in the VM (a few minutes)..."
  # The agent returns as soon as the command starts; setup.ps1 leaves a status file.
  agent powershell.exe -NoProfile -ExecutionPolicy Bypass -File 'C:\ProgramData\expanse-setup.ps1' \
    -PublicKeyFile 'C:\ProgramData\expanse-mac.pub'
  local status=running
  for _ in $(seq 180); do
    sleep 5
    status=$("$UTMCTL" file pull "$VM_NAME" 'C:\ProgramData\expanse-setup.status' 2>/dev/null | tr -d '\r\n' || true)
    case $status in done|failed*) break ;; esac
  done
  "$UTMCTL" file pull "$VM_NAME" 'C:\ProgramData\expanse-setup.log' | tr -d '\r' | grep -v '^\*\*' | tail -25
  [ "$status" = done ] || { echo "setup: $status" >&2; exit 1; }
  local user host
  user=$("$UTMCTL" file pull "$VM_NAME" 'C:\ProgramData\expanse-user.txt' | tr -d '\r\n' | sed 's/.*\\//')
  host=$(VM_HOST='' vm_host)
  printf 'VM_USER=%s\nVM_HOST=%s\n' "$user" "$host" > "$ROOT/.vm.env"
  echo "wrote .vm.env: $user@$host"
  VM_USER=$user VM_HOST=$host ssh_vm 'whoami && dotnet --version'
}

sync() {
  (cd "$ROOT" && COPYFILE_DISABLE=1 tar --no-xattrs --exclude=.git --exclude=artifacts --exclude=out \
    --exclude=.vm.env -czf - .) |
    ssh_vm "(if exist $REPO\\src rmdir /s /q $REPO\\src) & (if not exist $REPO\\out mkdir $REPO\\out) & tar -xzf - -C $REPO"
}

ctl() { ssh_vm "$(exe Ctl expansectl) $*"; }

stop() {
  ctl quit >/dev/null 2>&1 || true
  ssh_vm 'ping -n 2 127.0.0.1 >nul & taskkill /im Expanse.exe /f >nul 2>&1 & exit 0'
}

desk() {
  local exe=$1; shift
  ssh_vm "powershell -NoProfile -ExecutionPolicy Bypass -File $REPO\\scripts\\guest\\desk.ps1 -Exe \"$exe\" $*"
}

guest() {
  desk "$(exe Guest Guest)" -Arguments "\"$*\"" -Name ExpanseGuest -Wait 60 ||
    { ssh_vm "type $REPO\\out\\guest.log" | tail -5; exit 1; }
}

case ${1:-} in
  setup) setup ;;
  sync) sync ;;
  build)
    shift
    projects=${*:-Expanse Ctl Guest Spikes}
    sync
    stop
    case " $projects " in *" Spikes "*) ssh_vm 'taskkill /im Spikes.exe /f >nul 2>&1 & exit 0' ;; esac
    cmd="cd $REPO"
    for p in $projects; do cmd="$cmd && dotnet build src\\$p -c Debug -nologo -v q -clp:NoSummary"; done
    ssh_vm "$cmd"
    ;;
  start)
    stop
    desk "$(exe Expanse Expanse)" -Name ExpanseApp
    sleep 2
    ctl state | head -12
    ;;
  stop) stop ;;
  ctl) shift; ctl "$@" ;;
  desk) shift; desk "$@" ;;
  guest) shift; guest "$@" ;;
  shot)
    out=${2:-$ROOT/out/shot.png}
    mkdir -p "$(dirname "$out")"
    guest shot "C:\\Users\\$VM_USER\\$REPO\\out\\shot.png"
    scp -O -q -i "$VM_KEY" -o BatchMode=yes "$VM_USER@$(vm_host):$REPO/out/shot.png" "$out"
    echo "$out"
    ;;
  run) shift; ssh_vm "$*" ;;
  ps) shift; ssh_vm "powershell -NoProfile -Command \"$*\"" ;;
  snapshot) "$UTMCTL" snapshot create "$VM_NAME" "${2:?name}" ;;
  restore) "$UTMCTL" snapshot restore "$VM_NAME" "${2:?name}" ;;
  *)
    sed -n '2,18p' "$0"
    exit 1
    ;;
esac
