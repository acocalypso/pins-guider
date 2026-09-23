# shellcheck shell=bash
# Helpers for run-e2e.sh (sourced).

CONTAINER="${CONTAINER:-pins-e2e}"
API_PORT="${API_PORT:-21888}"
HUB_PORT="${HUB_PORT:-24782}"
INDI_PORT="${INDI_PORT:-27624}"
API="http://127.0.0.1:${API_PORT}/v2/api"

PASSES=0
FAILS=0
SKIPS=0
FAILED_STEPS=()

ts() { date +%H:%M:%S; }
info() { echo "[$(ts)] $*" | tee -a "$RUN/run.log"; }

pass() {
  PASSES=$((PASSES + 1))
  printf '[%s] PASS  %-38s %s\n' "$(ts)" "$1" "${2:-}" | tee -a "$RUN/run.log" "$RUN/summary.txt"
}

fail() {
  FAILS=$((FAILS + 1))
  FAILED_STEPS+=("$1")
  printf '[%s] FAIL  %-38s %s\n' "$(ts)" "$1" "${2:-}" | tee -a "$RUN/run.log" "$RUN/summary.txt"
  dump_on_failure "$1"
}

skip() {
  SKIPS=$((SKIPS + 1))
  printf '[%s] SKIP  %-38s %s\n' "$(ts)" "$1" "${2:-}" | tee -a "$RUN/run.log" "$RUN/summary.txt"
}

# check NAME CONDITION-EXIT-CODE EVIDENCE
check() {
  local name="$1" rc="$2" evidence="${3:-}"
  if [ "$rc" -eq 0 ]; then pass "$name" "$evidence"; else fail "$name" "$evidence"; fi
}

# api PATH [MAX_TIME] -> prints the response body; every call is recorded in $RUN/api.log
api() {
  local path="$1" max="${2:-30}" body
  body="$(curl -s --max-time "$max" "$API$path" 2>&1)" || true
  printf '%s GET %s\n  -> %s\n' "$(ts)" "$path" "$(printf '%s' "$body" | head -c 1500)" >> "$RUN/api.log"
  printf '%s' "$body"
}

api_post_file() {
  local path="$1" file="$2" body
  body="$(curl -s --max-time 30 -X POST --data-binary @"$file" "$API$path" 2>&1)" || true
  printf '%s POST %s (%s)\n  -> %s\n' "$(ts)" "$path" "$file" "$(printf '%s' "$body" | head -c 1500)" >> "$RUN/api.log"
  printf '%s' "$body"
}

urlenc() { jq -rn --arg v "$1" '$v|@uri'; }

# wait_until TIMEOUT_S INTERVAL_S COMMAND... ; returns 0 when COMMAND succeeded in time
wait_until() {
  local timeout="$1" interval="$2"; shift 2
  local deadline=$(( $(date +%s) + timeout ))
  while :; do
    if "$@"; then return 0; fi
    [ "$(date +%s)" -ge "$deadline" ] && return 1
    sleep "$interval"
  done
}

api_up() { curl -s --max-time 3 "$API/version" | jq -e '.Success == true' >/dev/null 2>&1; }

guider_state() { api /equipment/guider/info | jq -r '.Response.State // "?"' 2>/dev/null; }
guider_connected() { api /equipment/guider/info | jq -e '.Response.Connected == true' >/dev/null 2>&1; }
mount_info() { api /equipment/mount/info; }

pins_logs() { ls -1 "$HOME_DIR"/.local/share/NINA/Logs/*.log 2>/dev/null; }
# grep in all PINS logs of this run
pins_log_grep() { local f; f="$(pins_logs)"; [ -n "$f" ] && grep -a -h "$@" $f; }

# grep in the native guider's PHD2-style guide logs
guide_log_grep() { local f; f="$(ls -1 "$HOME_DIR"/.local/share/NINA/NativeGuider/Logs/*.txt 2>/dev/null)"; [ -n "$f" ] && grep -a -h "$@" $f; }

nina_pid() { docker exec "$CONTAINER" sh -c 'pgrep -f "^/opt/pins/NINA" | head -1' 2>/dev/null; }

container_running() { [ "$(docker inspect -f '{{.State.Running}}' "$CONTAINER" 2>/dev/null)" = "true" ]; }

DUMPED_FOR=""
dump_on_failure() {
  local step="$1" dir
  dir="$RUN/failure-$(echo "$step" | tr -c 'A-Za-z0-9_.-' '_')"
  mkdir -p "$dir"
  docker logs --tail 400 "$CONTAINER" > "$dir/container.log" 2>&1 || true
  local f; for f in $(pins_logs); do tail -n 400 "$f" > "$dir/$(basename "$f")"; done
  {
    echo "---- last PINS log errors/warnings ----"
    pins_log_grep -E '\|(ERROR|WARNING|FATAL)\|' | tail -n 25
  } > "$dir/errors.txt" 2>/dev/null || true
  echo "        (logs: $dir)" | tee -a "$RUN/run.log"
  sed 's/^/        /' "$dir/errors.txt" | tail -n 12
}

start_container() {
  docker rm -f "$CONTAINER" >/dev/null 2>&1 || true
  docker run -d --name "$CONTAINER" --stop-timeout 60 \
    -e PHD2_AUTOSTART=false -e PINSDAEMON_AUTOSTART=false -e TZ=UTC \
    -p "${API_PORT}:1888" -p "${HUB_PORT}:4782" -p "${INDI_PORT}:7624" \
    -v "$HOME_DIR:/home/pins" \
    "$IMAGE" >/dev/null
}

stop_container() { docker stop -t 60 "$CONTAINER" >/dev/null 2>&1 || true; }

# Background helpers (pids recorded for teardown)
BG_PIDS=()
bg() { "$@" & BG_PIDS+=($!); }
kill_bg() { local p; for p in "${BG_PIDS[@]:-}"; do [ -n "$p" ] && kill "$p" 2>/dev/null; done; BG_PIDS=(); }
