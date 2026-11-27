#!/usr/bin/env bash
# Run a command (default: make check) inside the dev toolbox container.
# Usage: ./scripts/dev.sh make setup check
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
if [ $# -eq 0 ]; then set -- make check; fi
MOUNT="$ROOT"
if command -v cygpath >/dev/null 2>&1; then MOUNT="$(cygpath -m "$ROOT")"; fi
docker build -q -t brightline-dev -f "$ROOT/docker/dev.Dockerfile" "$ROOT/docker" >/dev/null
MSYS_NO_PATHCONV=1 docker run --rm \
  -v "$MOUNT:/src" \
  -v brightline-nuget:/root/.nuget/packages \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e BRIGHTLINE_HOST=host.docker.internal \
  -e PLAYWRIGHT_BROWSERS_PATH=/root/.nuget/packages/.ms-playwright \
  --add-host host.docker.internal:host-gateway \
  -w /src brightline-dev "$@"
