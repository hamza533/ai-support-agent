#!/usr/bin/env bash
# Records the demo story against the running app (make dev) with the real model and produces:
#   artifacts/video/brightline-demo.mp4  – showcase video (upload to YouTube/Loom; not committed)
#   docs/images/demo.gif                 – the chat part for the top of the README
# Run inside the toolbox: ./scripts/dev.sh scripts/record-demo.sh
# Needs RECORDING_STAFF_EMAIL / RECORDING_STAFF_PASSWORD in .env (create with: docker compose run --rm
# -e ADMIN_PASSWORD=... web --create-admin <email>). APP_URL defaults to the local stack.
set -euo pipefail
cd "$(dirname "$0")/.."

PW=tests/Brightline.E2ETests/bin/Release/net10.0/.playwright
[ -d "$PW" ] || dotnet build tests/Brightline.E2ETests -c Release
if [ -f .env ]; then
  export RECORDING_STAFF_EMAIL="${RECORDING_STAFF_EMAIL:-$(grep -E '^RECORDING_STAFF_EMAIL=' .env | cut -d= -f2-)}"
  export RECORDING_STAFF_PASSWORD="${RECORDING_STAFF_PASSWORD:-$(grep -E '^RECORDING_STAFF_PASSWORD=' .env | cut -d= -f2-)}"
fi
export PW_CORE="$PWD/$PW/package/index.mjs" OUT_DIR=artifacts/video

# Chromium's system libraries (as in `make e2e`) and ffmpeg for encoding – skipped when already installed.
ldconfig -p | grep -q libglib-2.0 || "$PW/node/linux-x64/node" "$PW/package/cli.js" install --with-deps chromium >/dev/null
command -v ffmpeg >/dev/null || { apt-get update -qq && DEBIAN_FRONTEND=noninteractive apt-get install -y -qq ffmpeg >/dev/null; }

"$PW/node/linux-x64/node" scripts/record-demo.mjs

ffmpeg -loglevel error -y -f concat -safe 0 -i "$OUT_DIR/concat.txt" \
  -vf "fps=30,format=yuv420p" -c:v libx264 -crf 18 -preset medium -movflags +faststart "$OUT_DIR/brightline-demo.mp4"

# shellcheck disable=SC1091
. "$OUT_DIR/gif.env"
ffmpeg -loglevel error -y -ss "$GIF_START" -t "$GIF_DURATION" -i "$OUT_DIR/brightline-demo.mp4" \
  -vf "setpts=PTS/1.5,fps=12,scale=960:-1:flags=lanczos,split[a][b];[a]palettegen=max_colors=128:stats_mode=diff[p];[b][p]paletteuse=dither=bayer:bayer_scale=4:diff_mode=rectangle" \
  docs/images/demo.gif

ls -lh "$OUT_DIR/brightline-demo.mp4" docs/images/demo.gif
