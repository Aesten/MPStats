#!/usr/bin/env bash
# Builds MPStats and installs it on the test server.
#
# Usage: scripts/deploy.sh [--no-build] [--restart [-- <run-server.sh options>]]
#   --no-build  copy the existing dist/ output without rebuilding
#   --restart   stop the running server (if any) and start it again, detached;
#               options after -- go to run-server.sh, e.g. --restart -- --tickrate 60
#
# run-server.sh, which starts the server, is installed by mpServerConfig's deploy.sh.
# MPS_HOST overrides the ssh target (default: aesten@7900x).

set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/.." && pwd)"

HOST="${MPS_HOST:-aesten@7900x}"
# Remote paths, relative to the remote home directory.
REMOTE_DS="Steam/steamapps/common/Mount & Blade II Dedicated Server"
REMOTE_SCRIPTS="bannerlord"

BUILD=1
RESTART=0
SERVER_ARGS=()
while [ $# -gt 0 ]; do
  case "$1" in
    --no-build) BUILD=0 ;;
    --restart) RESTART=1 ;;
    --) shift; SERVER_ARGS=("$@"); break ;;
    *) echo "Unknown option: $1" >&2; exit 1 ;;
  esac
  shift
done

if [ "$BUILD" = 1 ]; then
  dotnet build "$REPO_ROOT/src/MPStats" -c Release
fi

MODULE_DIR="$REPO_ROOT/dist/Modules/MPStats"
[ -f "$MODULE_DIR/bin/Linux64_Shipping_Server/MPStats.dll" ] || { echo "No build output in $MODULE_DIR" >&2; exit 1; }

# tar over ssh rather than scp: it replaces the module folder as a whole (no stale files)
# and sidesteps scp's remote-path quoting, which differs between OpenSSH versions.
echo "Installing module to $HOST:~/$REMOTE_DS/Modules/MPStats"
tar -C "$REPO_ROOT/dist/Modules" -cf - MPStats \
  | ssh "$HOST" "cd \"\$HOME/$REMOTE_DS/Modules\" && rm -rf MPStats && tar -xf -"

if [ "$RESTART" = 1 ]; then
  # %q-quote the pass-through options so they survive the remote shell.
  QUOTED_ARGS=""
  [ ${#SERVER_ARGS[@]} -gt 0 ] && QUOTED_ARGS="$(printf ' %q' "${SERVER_ARGS[@]}")"
  ssh "$HOST" "\"\$HOME/$REMOTE_SCRIPTS/run-server.sh\" --stop && \"\$HOME/$REMOTE_SCRIPTS/run-server.sh\" -d$QUOTED_ARGS"
fi
