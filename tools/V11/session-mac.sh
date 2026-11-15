#!/bin/sh
# V11 Mac project-instruction mode only. No credential store or OS-wide enforcement.
set -eu
ROOT=$(CDPATH= cd -- "$(dirname -- "$0")/../.." && pwd)
PRIVATE="$ROOT/runtime-private"
ACTION=${1:-status}
case "$ACTION" in
  start)
    printf '%s\n' 'Only this deployment task. This is a project instruction, not an OS security lock.'
    printf '%s' 'Type START to confirm: '
    IFS= read -r ANSWER
    [ "$ANSWER" = START ] || { printf '%s\n' 'Cancelled'; exit 1; }
    umask 077
    mkdir -p "$PRIVATE"
    [ ! -L "$PRIVATE" ] || { printf '%s\n' 'Refusing symlink'; exit 1; }
    chmod 700 "$PRIVATE"
    printf '%s\n' '{"version":11,"mode":"Deploying","executionPlace":"MacHostInstructionsOnly"}' > "$PRIVATE/state.json.new"
    mv "$PRIVATE/state.json.new" "$PRIVATE/state.json"
    printf '%s\n' 'Deploying: project instructions enabled. Windows helpers run inside the Windows guest.'
    ;;
  lock)
    umask 077
    mkdir -p "$PRIVATE"
    [ ! -L "$PRIVATE" ] || { printf '%s\n' 'Refusing symlink'; exit 1; }
    chmod 700 "$PRIVATE"
    printf '%s\n' '{"version":11,"mode":"Locked","executionPlace":"MacHostInstructionsOnly"}' > "$PRIVATE/state.json.new"
    mv "$PRIVATE/state.json.new" "$PRIVATE/state.json"
    printf '%s\n' 'Locked: project instructions only; no global tool permissions changed.'
    ;;
  status)
    if [ -f "$PRIVATE/state.json" ]; then cat "$PRIVATE/state.json"; else printf '%s\n' '{"version":11,"mode":"Locked"}'; fi
    ;;
  *) printf '%s\n' 'Usage: sh session-mac.sh start|lock|status'; exit 1 ;;
esac