#!/usr/bin/env bash
# Builds the e2e images:
#   pins:native-guider-e2e      pins core + ninaAPI + pins-guider plugin (local checkout)
#   pins:native-guider-e2e-gsc  the same plus the GSC catalog for the INDI CCD/guide simulators
#
# The plugin is taken from $PINS_WT/NINA.Plugins/pins-guider and copied into a
# private BuildKit context (never the whole NINA.Plugins dir, other agents edit
# other submodules there).
#   PLUGIN_SOURCE=worktree (default)  rsync of the working tree (uncommitted edits included)
#   PLUGIN_SOURCE=head                `git archive HEAD` of the plugin repo (last commit only)
set -euo pipefail

E2E="$(cd "$(dirname "$0")" && pwd)"
PINS_WT="${PINS_WT:-/home/andre/astro/sw/pins-wt/native-guider}"
PLUGIN_SRC="$PINS_WT/NINA.Plugins/pins-guider"
CTX="$E2E/local-plugins"
IMAGE="${IMAGE:-pins:native-guider-e2e}"
BUILD_PLUGINS="${BUILD_PLUGINS:-ninaapi pins-guider}"
PLUGIN_SOURCE="${PLUGIN_SOURCE:-worktree}"
mkdir -p "$E2E/logs"
log="$E2E/logs/build-$(date +%Y%m%d-%H%M%S).log"

echo "[build] plugin repo: $(git -C "$PLUGIN_SRC" log --oneline -1) (source: $PLUGIN_SOURCE)"
git -C "$PLUGIN_SRC" status --short | sed 's/^/[build]   dirty: /' || true

rm -rf "$CTX"
mkdir -p "$CTX/pins-guider"
case "$PLUGIN_SOURCE" in
  worktree)
    rsync -a --delete --exclude bin/ --exclude obj/ --exclude .git --exclude '.vs/' \
      "$PLUGIN_SRC/" "$CTX/pins-guider/" ;;
  head)
    git -C "$PLUGIN_SRC" archive --format=tar HEAD | tar -x -C "$CTX/pins-guider" ;;
  *) echo "unknown PLUGIN_SOURCE=$PLUGIN_SOURCE" >&2; exit 2 ;;
esac

start=$(date +%s)
echo "[build] docker build $IMAGE (BUILD_PLUGINS=\"$BUILD_PLUGINS\"), log: $log"
if ! (cd "$PINS_WT" && docker build --network=host --progress=plain \
      --build-context local-plugins="$CTX" \
      --build-arg BUILD_PLUGINS="$BUILD_PLUGINS" \
      -t "$IMAGE" .) >"$log" 2>&1; then
  echo "[build] FAILED after $(( $(date +%s) - start ))s; compiler errors:" >&2
  grep -E "error [A-Z]+[0-9]+|ERROR:|error:" "$log" | sed 's/^#[0-9]* [0-9.]* //' | sort -u | head -60 >&2
  exit 1
fi
mid=$(date +%s)
echo "[build] $IMAGE built in $(( mid - start ))s"

(cd "$E2E/gsc" && docker build --network=host -q --build-arg BASE="$IMAGE" -t "$IMAGE-gsc" .) >>"$log" 2>&1
end=$(date +%s)
echo "[build] $IMAGE-gsc built in $(( end - mid ))s (total $(( end - start ))s)"
echo "$(date -Is) image=$IMAGE plugin=$(git -C "$PLUGIN_SRC" rev-parse --short HEAD) source=$PLUGIN_SOURCE total=$(( end - start ))s" >> "$E2E/logs/build-times.txt"
