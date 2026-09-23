#!/usr/bin/env bash
# End-to-end test of the PINS native guider plugin inside a real PINS container.
#
#   ./run-e2e.sh                 run phases A B C E D against pins:native-guider-e2e-gsc
#                                (A startup, B simulator loop, C INDI camera + INDI mount,
#                                 E INDI camera ST4 output, D robustness)
#   ./run-e2e.sh --build         rebuild the images first (build.sh; plugin from the local checkout)
#   ./run-e2e.sh --phases "A B"  run a subset (A is always run; later phases need the setup it does)
#   ./run-e2e.sh --keep          leave the pins-e2e container running afterwards
#
# Env: IMAGE (pins:native-guider-e2e-gsc), GUIDE_SECONDS (240, simulator guiding window),
#      INDI_GUIDE_SECONDS (120), CAL_TIMEOUT (600), PLUGIN_SOURCE (worktree|head, for --build)
#
# The container runs with bridge networking on remapped ports (ninaAPI 21888, SignalR hubs
# 24782, indiserver 27624) and a fresh scratch home in ./home; it never touches the
# production `pins` container. Results: ./logs/run-<timestamp>/ (summary.txt, run.log,
# api.log, notifications.log, indi-watch.log, guide samples, graph dumps, PINS logs).
set -uo pipefail
# Everything is inside one brace group so bash parses the whole file before running it
# (editing the script while a run is in progress cannot break that run).
{

E2E="$(cd "$(dirname "$0")" && pwd)"
IMAGE="${IMAGE:-pins:native-guider-e2e-gsc}"
GUIDE_SECONDS="${GUIDE_SECONDS:-240}"
INDI_GUIDE_SECONDS="${INDI_GUIDE_SECONDS:-120}"
CAL_TIMEOUT="${CAL_TIMEOUT:-600}"
HOME_DIR="$E2E/home"
PHASES="A B C E D"
KEEP=0
BUILD=0
PLUGIN_GUID="3e519099-8e1e-46ee-bed2-f775abd90619"
GUIDER_ID="PinsNativeGuider"
INDI_MOUNT_DRIVER="indi_simulator_telescope"
INDI_MOUNT_DEVICE="Telescope Simulator"
INDI_CAM_DRIVER="indi_simulator_guide"
INDI_CAM_DEVICE="Guide Simulator"
SIM_FOCAL_LENGTH=200

while [ $# -gt 0 ]; do
  case "$1" in
    --build) BUILD=1 ;;
    --keep) KEEP=1 ;;
    --phases) PHASES="$2"; shift ;;
    -h|--help) sed -n '2,16p' "$0"; exit 0 ;;
    *) echo "unknown option $1" >&2; exit 2 ;;
  esac
  shift
done

RUN="$E2E/logs/run-$(date +%Y%m%d-%H%M%S)"
mkdir -p "$RUN"
# shellcheck source=lib.sh
. "$E2E/lib.sh"

has_phase() { case " $PHASES " in *" $1 "*) return 0 ;; esac; return 1; }

teardown() {
  kill_bg
  {
    docker logs "$CONTAINER" > "$RUN/container.log" 2>&1
    mkdir -p "$RUN/pins-logs"
    cp -a "$HOME_DIR"/.local/share/NINA/Logs/. "$RUN/pins-logs/" 2>/dev/null
    cp -a "$HOME_DIR"/.local/share/NINA/NativeGuider "$RUN/native-guider-data" 2>/dev/null
  } || true
  if [ "$KEEP" -eq 0 ]; then
    docker stop -t 60 "$CONTAINER" >/dev/null 2>&1
    docker rm -f "$CONTAINER" >/dev/null 2>&1
  fi
}
trap teardown EXIT
trap 'echo "interrupted"; exit 130' INT TERM

if [ "$BUILD" -eq 1 ]; then
  "$E2E/build.sh" 2>&1 | tee -a "$RUN/run.log"
  [ "${PIPESTATUS[0]}" -eq 0 ] || { fail "build" "image build failed (see $RUN/run.log)"; exit 1; }
fi

info "image $IMAGE ($(docker image inspect -f '{{.Created}}' "$IMAGE" 2>/dev/null)), results in $RUN"

############################################################################
# Setup: fresh home, default profile, seed the native guider settings
############################################################################
setup() {
  info "setup: fresh home $HOME_DIR"
  docker rm -f "$CONTAINER" >/dev/null 2>&1
  rm -rf "$HOME_DIR"; mkdir -p "$HOME_DIR"
  start_container || { fail "setup: start container" "docker run failed"; exit 1; }
  if ! wait_until 240 3 api_up; then fail "A1 ninaAPI reachable" "no answer on $API/version after 240s"; exit 1; fi
  local prof_id prof_file
  prof_id="$(api '/profile/show?active=true' | jq -r '.Response.Id')"
  # Profile values used by later phases (saved by pins one second after the change).
  api "/profile/change-value?settingpath=TelescopeSettings-IndiDriver&newValue=$INDI_MOUNT_DRIVER" >/dev/null
  sleep 3
  stop_container
  prof_file="$HOME_DIR/.local/share/NINA/Profiles/$prof_id.profile"
  if [ ! -f "$prof_file" ]; then fail "setup: profile" "profile file $prof_file missing"; exit 1; fi
  # The native guider can only be configured through ninaAPI while it is connected, and it
  # cannot connect with the default camera driver (indi_asi_ccd) here, so seed the simulator.
  python3 "$E2E/seed_plugin_settings.py" "$prof_file" "$PLUGIN_GUID" \
    GuideCameraDriver=simulator SaveGuideLog=true >> "$RUN/run.log" \
    || { fail "setup: seed plugin settings" "see run.log"; exit 1; }
  docker start "$CONTAINER" >/dev/null
  if ! wait_until 240 3 api_up; then fail "A1 ninaAPI reachable" "no answer after restart"; exit 1; fi
  bg python3 "$E2E/notify_watch.py" 127.0.0.1 "$HUB_PORT" >> "$RUN/notifications.log" 2>&1
  info "setup done (profile $prof_id)"
}

############################################################################
# A: container, ninaAPI, plugin loaded, device listed
############################################################################
NINA_PID=""
phase_A() {
  info "=== Phase A: startup ==="
  local v; v="$(api /version | jq -r '.Response')"
  check "A1 ninaAPI reachable" $? "version $v"
  NINA_PID="$(nina_pid)"

  local line
  line="$(pins_log_grep 'Successfully loaded plugin PINS Native Guider' | tail -1)"
  if [ -n "$line" ]; then pass "A2 plugin loaded" "${line##*|}"; else
    fail "A2 plugin loaded" "$(pins_log_grep -i 'Failed to load plugin' | tail -2 | tr '\n' ' ')"; fi

  local ids
  ids="$(api /equipment/guider/list-devices | jq -r '[.Response[].Id] | join(",")')"
  if ! grep -q "$GUIDER_ID" <<<"$ids"; then
    ids="$(api /equipment/guider/rescan 120 | jq -r '[.Response[].Id] | join(",")')"
  fi
  grep -q "$GUIDER_ID" <<<"$ids"
  check "A3 guider list-devices has $GUIDER_ID" $? "$ids"
}

############################################################################
# guiding helpers
############################################################################
# start_guiding TAG CALIBRATE TIMEOUT -> waits for the start request; sets START_BODY / START_SECS
start_guiding() {
  local tag="$1" cal="$2" timeout="$3" t0 pid
  t0=$(date +%s)
  curl -s --max-time "$timeout" "$API/equipment/guider/start?calibrate=$cal" > "$RUN/start-$tag.json" 2>&1 &
  pid=$!
  while kill -0 "$pid" 2>/dev/null; do
    printf '%s %s\n' "$(ts)" "$(api /equipment/guider/info | jq -c '.Response | {State, Connected}')" >> "$RUN/states-$tag.log"
    sleep 5
  done
  wait "$pid"
  START_SECS=$(( $(date +%s) - t0 ))
  echo "$START_SECS" > "$RUN/start-$tag.secs"
  START_BODY="$(cat "$RUN/start-$tag.json")"
  echo "$(ts) start($tag) returned after ${START_SECS}s: $START_BODY" >> "$RUN/api.log"
}

# sample_guiding TAG SECONDS -> CSV of guider info every 10 s; sets NOT_GUIDING_SAMPLES / SAMPLES
sample_guiding() {
  local tag="$1" secs="$2" end csv row
  csv="$RUN/samples-$tag.csv"
  echo "time,state,rms_ra_arcsec,rms_dec_arcsec,rms_total_arcsec,ra_raw,dec_raw,ra_ms,dec_ms,pixel_scale" > "$csv"
  NOT_GUIDING_SAMPLES=0; SAMPLES=0
  end=$(( $(date +%s) + secs ))
  while [ "$(date +%s)" -lt "$end" ]; do
    row="$(api /equipment/guider/info | jq -r '.Response | [.State, .RMSError.RA.Arcseconds, .RMSError.Dec.Arcseconds, .RMSError.Total.Arcseconds, .LastGuideStep.RADistanceRaw, .LastGuideStep.DECDistanceRaw, .LastGuideStep.RADuration, .LastGuideStep.DECDuration, .PixelScale] | @csv' 2>/dev/null)"
    echo "$(ts),$row" >> "$csv"
    SAMPLES=$((SAMPLES + 1))
    grep -q '"Guiding"' <<<"$row" || NOT_GUIDING_SAMPLES=$((NOT_GUIDING_SAMPLES + 1))
    sleep 10
  done
}

graph_eval() { # graph_eval TAG -> writes graph-TAG.json; prints "steps rms_ra rms_dec rms_total scale"
  api /equipment/guider/graph > "$RUN/graph-$1.json"
  jq -r '.Response | "\(.GuideSteps | length) \(.RMS.RA) \(.RMS.Dec) \(.RMS.Total) \(.PixelScale // .RMS.Scale)"' "$RUN/graph-$1.json" 2>/dev/null
}

run_dither() { # -> DITHER_STATUS, DITHER_SECS
  local t0 st
  t0=$(date +%s)
  api_post_file /sequence/load "$E2E/fixtures/dither-sequence.json" > /dev/null
  api "/sequence/start?skipValidation=true" > /dev/null
  DITHER_STATUS="TIMEOUT"
  for _ in $(seq 1 90); do
    sleep 2
    st="$(api /sequence/json | jq -r '.Response[2].Items[0].Status' 2>/dev/null)"
    case "$st" in FINISHED|FAILED|SKIPPED) DITHER_STATUS="$st"; break ;; esac
  done
  DITHER_SECS=$(( $(date +%s) - t0 ))
  api /sequence/stop > /dev/null
}

############################################################################
# B: built-in simulator, closed loop
############################################################################
phase_B() {
  info "=== Phase B: simulator closed loop ==="
  local r
  r="$(api "/equipment/guider/connect?to=$GUIDER_ID" 120)"
  jq -e '.Success == true' <<<"$r" >/dev/null && guider_connected
  check "B1 connect guider (simulator)" $? "$(jq -c '{Response,Error}' <<<"$r" 2>/dev/null || echo "$r") / $(api /equipment/guider/info | jq -c '.Response | {Connected, DeviceId, State, PixelScale}')"

  api /equipment/guider/get-settings > "$RUN/settings-B.json"
  local drv; drv="$(jq -r '.Response.GuideCameraDriver' "$RUN/settings-B.json")"
  [ "$drv" = "simulator" ]
  check "B2 get-settings (GuideCameraDriver)" $? "GuideCameraDriver=$drv, $(jq -r '.Response | keys | length' "$RUN/settings-B.json" 2>/dev/null) properties; $(jq -c '.Error' "$RUN/settings-B.json" 2>/dev/null)"

  r="$(api '/equipment/guider/set-setting?settingName=ExposureSeconds&newValue=1')"
  local exp; exp="$(api /equipment/guider/get-settings | jq -r '.Response.ExposureSeconds')"
  [ "$exp" = "1" ]
  check "B3 set-setting ExposureSeconds=1" $? "response=$(jq -c '.Response' <<<"$r" 2>/dev/null) now=$exp"

  r="$(api "/equipment/guider/set-setting?settingName=NativeSetting&newValue=$(urlenc 'MinSnr=7')")"
  local snr; snr="$(api /equipment/guider/get-settings | jq -r '.Response.MinSnr')"
  [ "$snr" = "7" ]
  check "B3b set-setting NativeSetting MinSnr=7" $? "response=$(jq -c '{Response,Error}' <<<"$r" 2>/dev/null || echo "$r") now=$snr"

  if ! guider_connected; then skip "B4-B8" "guider not connected"; return; fi

  start_guiding B true "$CAL_TIMEOUT"
  local st; st="$(guider_state)"
  [ "$st" = "Guiding" ]
  check "B4 start guiding + calibration" $? "start returned after ${START_SECS}s: $(jq -c '{Response,Error}' <<<"$START_BODY" 2>/dev/null || echo "$START_BODY"); State=$st"
  if [ "$st" != "Guiding" ]; then
    grep -a 'Native guider' "$RUN/notifications.log" | tail -3 | sed 's/^/        /'
    skip "B5-B6" "not guiding"; stop_disconnect B; return
  fi

  api /equipment/guider/graph/clear > /dev/null
  info "B: guiding for ${GUIDE_SECONDS}s"
  sample_guiding B "$GUIDE_SECONDS"
  local g steps rms_ra rms_dec rms_total scale rms_info
  g="$(graph_eval B)"; read -r steps rms_ra rms_dec rms_total scale <<<"$g"
  rms_info="$(api /equipment/guider/info | jq -r '.Response.RMSError | "RA \(.RA.Arcseconds)\" Dec \(.Dec.Arcseconds)\" Total \(.Total.Arcseconds)\" (\(.Total.Pixel) px)"')"
  local min_steps=$(( GUIDE_SECONDS / 4 ))
  [ "${steps:-0}" -ge "$min_steps" ] && [ "$NOT_GUIDING_SAMPLES" -eq 0 ]
  check "B5 guide steps flowing" $? "$steps steps in graph after ${GUIDE_SECONDS}s (min $min_steps), $NOT_GUIDING_SAMPLES/$SAMPLES samples not Guiding"
  local tot; tot="$(api /equipment/guider/info | jq -r '.Response.RMSError.Total.Arcseconds')"
  awk -v t="$tot" 'BEGIN { exit !(t > 0 && t < 1.5) }'
  check "B6 RMS sane (< 1.5\" total)" $? "$rms_info; graph RMS(px) RA $rms_ra Dec $rms_dec Total $rms_total"

  run_dither
  local st_after; sleep 5; st_after="$(guider_state)"
  [ "$DITHER_STATUS" = "FINISHED" ] && [ "$st_after" = "Guiding" ]
  check "B7 dither via sequencer" $? "Dither instruction $DITHER_STATUS after ${DITHER_SECS}s, state afterwards $st_after; guide log: $(guide_log_grep 'DITHER by' | tail -1 | cut -c1-120)"
  sample_guiding B-postdither 30

  stop_disconnect B
}

stop_disconnect() {
  local tag="$1" r st
  r="$(api /equipment/guider/stop 60)"
  wait_until 30 2 sh -c "[ \"\$(curl -s --max-time 5 '$API/equipment/guider/info' | jq -r '.Response.State')\" != Guiding ]"
  st="$(guider_state)"
  [ "$st" != "Guiding" ]
  check "$tag stop guiding" $? "$(jq -c '.Response' <<<"$r" 2>/dev/null) State=$st"
  r="$(api /equipment/guider/disconnect 60)"
  sleep 2
  ! guider_connected
  check "$tag disconnect guider" $? "$(jq -c '.Response' <<<"$r" 2>/dev/null)"
}

############################################################################
# C: INDI guide camera + INDI telescope simulator
############################################################################
# pulse_lines DEVICE < indi-watch.log -> TELESCOPE_TIMED_GUIDE_* updates of DEVICE with a non-zero duration
pulse_lines() { grep -a "PULSE $1 TELESCOPE_TIMED_GUIDE_.*(setNumberVector)" | grep -av '_[NSWE]=0 TIMED_GUIDE_[NSWE]=0 '; }

indi_prop() { docker exec "$CONTAINER" indi_getprop -p 7624 -t 3 "$@" 2>&1; }

phase_C() {
  info "=== Phase C: INDI camera path ==="
  bg python3 "$E2E/indi_watch.py" 127.0.0.1 "$INDI_PORT" >> "$RUN/indi-watch.log" 2>&1

  local r ids lst ra
  ids="$(api /equipment/mount/rescan 120 | jq -r '[.Response[].Id] | join(",")')"
  r="$(api "/equipment/mount/connect?to=$(urlenc "$INDI_MOUNT_DEVICE")" 120)"
  api /equipment/mount/unpark 60 > /dev/null
  api "/equipment/mount/tracking?mode=0" > /dev/null
  lst="$(mount_info | jq -r '.Response.SiderealTime')"
  ra="$(awk -v l="$lst" 'BEGIN { r = l * 15 - 10; if (r < 0) r += 360; printf "%.4f", r }')"
  api "/equipment/mount/slew?ra=$ra&dec=40&waitForResult=true" 180 > /dev/null
  local mi; mi="$(mount_info | jq -c '.Response | {Connected, TrackingEnabled, CanPulseGuide, RightAscensionString, DeclinationString, SideOfPier}')"
  jq -e '.Connected and .TrackingEnabled and .CanPulseGuide' <<<"$mi" >/dev/null
  check "C1 INDI mount connected + tracking" $? "devices [$ids]; $mi"

  # switch the guider to the INDI guide camera (settings can only be changed while connected)
  api "/equipment/guider/connect?to=$GUIDER_ID" 120 > /dev/null
  api "/equipment/guider/set-setting?settingName=GuideCameraDriver&newValue=$INDI_CAM_DRIVER" > /dev/null
  api "/equipment/guider/set-setting?settingName=GuideCameraDevice&newValue=$(urlenc "$INDI_CAM_DEVICE")" > /dev/null
  api "/equipment/guider/set-setting?settingName=ExposureSeconds&newValue=1" > /dev/null
  api /equipment/guider/disconnect 60 > /dev/null
  sleep 2
  r="$(api "/equipment/guider/connect?to=$GUIDER_ID" 180)"
  api /equipment/guider/get-settings > "$RUN/settings-C.json"
  jq -e '.Success == true' <<<"$r" >/dev/null && guider_connected
  check "C2 connect guider (INDI $INDI_CAM_DEVICE)" $? "$(jq -c '{Response,Error}' <<<"$r" 2>/dev/null || echo "$r"); driver=$(jq -r '.Response.GuideCameraDriver' "$RUN/settings-C.json") device=$(jq -r '.Response.GuideCameraDevice' "$RUN/settings-C.json")"

  # The INDI CCD/guide simulators only render stars (from the GSC catalog around the snooped
  # telescope position) when the camera's SCOPE_INFO focal length is set; it defaults to 0.
  docker exec "$CONTAINER" indi_setprop -p 7624 "$INDI_CAM_DEVICE.SCOPE_INFO.FOCAL_LENGTH;APERTURE=$SIM_FOCAL_LENGTH;50" >> "$RUN/run.log" 2>&1
  api "/equipment/guider/set-setting?settingName=FocalLengthMm&newValue=$SIM_FOCAL_LENGTH" > /dev/null
  indi_prop "$INDI_CAM_DEVICE.CCD_INFO.*" "$INDI_CAM_DEVICE.SCOPE_INFO.*" "$INDI_CAM_DEVICE.ACTIVE_DEVICES.*" "$INDI_CAM_DEVICE.SIMULATOR_SETTINGS.*" "$INDI_CAM_DEVICE.CCD_GAIN.*" > "$RUN/indi-guide-camera-props.txt"
  if ! guider_connected; then skip "C3-C7" "guider not connected"; return; fi

  local gsc; gsc="$(docker exec "$CONTAINER" sh -c 'command -v gsc && ls -d /usr/share/GSC' 2>/dev/null | tr '\n' ' ')"
  info "C: GSC catalog in image: ${gsc:-none (simulated frames contain no stars)}"

  start_guiding C true "$CAL_TIMEOUT" &
  local sg=$!
  sleep 30
  local exposures; exposures="$(grep -ac "EXPOSURE $INDI_CAM_DEVICE Ok" "$RUN/indi-watch.log")"
  [ "${exposures:-0}" -ge 3 ]
  check "C3 guide frames captured via INDI" $? "$exposures completed CCD_EXPOSURE cycles of '$INDI_CAM_DEVICE' within 30 s"
  wait "$sg"
  START_BODY="$(cat "$RUN/start-C.json")"; START_SECS="$(cat "$RUN/start-C.secs" 2>/dev/null)"
  local st; st="$(guider_state)"
  local pulses; pulses="$(pulse_lines "$INDI_MOUNT_DEVICE" < "$RUN/indi-watch.log" | wc -l)"
  [ "${pulses:-0}" -ge 1 ]
  check "C4 guide pulses reach INDI telescope" $? "$pulses non-zero TELESCOPE_TIMED_GUIDE_* updates on '$INDI_MOUNT_DEVICE'; e.g. $(pulse_lines "$INDI_MOUNT_DEVICE" < "$RUN/indi-watch.log" | head -1 | cut -c1-120)"
  if [ "$st" = "Guiding" ]; then
    pass "C5 INDI calibration + guiding" "start returned after ${START_SECS:-?}s, State=$st"
    api /equipment/guider/graph/clear > /dev/null
    sample_guiding C "$INDI_GUIDE_SECONDS"
    local g steps rms_ra rms_dec rms_total scale
    g="$(graph_eval C)"; read -r steps rms_ra rms_dec rms_total scale <<<"$g"
    local tot; tot="$(api /equipment/guider/info | jq -r '.Response.RMSError.Total.Arcseconds')"
    [ "${steps:-0}" -ge $(( INDI_GUIDE_SECONDS / 6 )) ] && awk -v t="$tot" 'BEGIN { exit !(t > 0 && t < 3) }'
    check "C6 INDI guiding steps + RMS (< 3\")" $? "$steps steps in ${INDI_GUIDE_SECONDS}s, RMS total ${tot}\", $NOT_GUIDING_SAMPLES/$SAMPLES samples not Guiding"
  else
    local why; why="$(grep -a 'Native guider' "$RUN/notifications.log" | tail -2 | cut -c1-300 | tr '\n' ' ')"
    if [ -z "$gsc" ]; then
      skip "C5 INDI calibration + guiding" "no GSC catalog -> no stars (expected); State=$st; $why"
    else
      fail "C5 INDI calibration + guiding" "State=$st after ${START_SECS:-?}s; start: $(jq -c '{Response,Error}' <<<"$START_BODY" 2>/dev/null); notifications: $why"
    fi
  fi
  stop_disconnect C
}

############################################################################
# E: INDI guide camera with its own ST4 output (PulseOutput=CameraST4)
############################################################################
phase_E() {
  info "=== Phase E: camera ST4 output ==="
  local r st
  api "/equipment/guider/connect?to=$GUIDER_ID" 180 > /dev/null
  r="$(api '/equipment/guider/set-setting?settingName=PulseOutput&newValue=CameraST4')"
  api /equipment/guider/disconnect 60 > /dev/null; sleep 2
  r="$(api "/equipment/guider/connect?to=$GUIDER_ID" 180)"
  jq -e '.Success == true' <<<"$r" >/dev/null && guider_connected && [ "$(api /equipment/guider/get-settings | jq -r '.Response.PulseOutput')" = "CameraST4" ]
  check "E1 connect with PulseOutput=CameraST4" $? "$(jq -c '{Response,Error}' <<<"$r" 2>/dev/null)"
  if ! guider_connected; then skip "E2-E3" "guider not connected"; return; fi
  docker exec "$CONTAINER" indi_setprop -p 7624 "$INDI_CAM_DEVICE.SCOPE_INFO.FOCAL_LENGTH;APERTURE=$SIM_FOCAL_LENGTH;50" >> "$RUN/run.log" 2>&1
  local mark; mark="$(wc -l < "$RUN/indi-watch.log")"
  start_guiding E true "$CAL_TIMEOUT"
  st="$(guider_state)"
  local cam_pulses mount_pulses
  cam_pulses="$(tail -n +"$((mark + 1))" "$RUN/indi-watch.log" | pulse_lines "$INDI_CAM_DEVICE" | wc -l)"
  mount_pulses="$(tail -n +"$((mark + 1))" "$RUN/indi-watch.log" | pulse_lines "$INDI_MOUNT_DEVICE" | wc -l)"
  [ "$st" = "Guiding" ] && [ "${cam_pulses:-0}" -ge 1 ] && [ "${mount_pulses:-0}" -eq 0 ]
  check "E2 ST4 calibration + guiding" $? "State=$st after ${START_SECS}s; ST4 pulses on '$INDI_CAM_DEVICE': $cam_pulses, on mount: $mount_pulses"
  if [ "$st" = "Guiding" ]; then
    api /equipment/guider/graph/clear > /dev/null
    sample_guiding E 60
    local tot; tot="$(api /equipment/guider/info | jq -r '.Response.RMSError.Total.Arcseconds')"
    awk -v t="$tot" 'BEGIN { exit !(t > 0 && t < 3) }' && [ "$NOT_GUIDING_SAMPLES" -eq 0 ]
    check "E3 ST4 guiding RMS (< 3\")" $? "RMS total ${tot}\", $NOT_GUIDING_SAMPLES/$SAMPLES samples not Guiding"
  fi
  api '/equipment/guider/set-setting?settingName=PulseOutput&newValue=Mount' > /dev/null
  stop_disconnect E
}

############################################################################
# D: robustness
############################################################################
phase_D() {
  info "=== Phase D: robustness ==="
  local i r ok=0 details="" drv_count
  r="$(api "/equipment/guider/connect?to=$GUIDER_ID" 180)"
  guider_connected && details+="connect ok; " || details+="initial connect FAILED $(jq -c '.Error' <<<"$r" 2>/dev/null); "
  for i in 1 2; do
    api /equipment/guider/disconnect 60 > /dev/null; sleep 2
    guider_connected && details+="disconnect#$i still connected; " || details+="disconnect#$i ok; "
    r="$(api "/equipment/guider/connect?to=$GUIDER_ID" 180)"
    if jq -e '.Success == true' <<<"$r" >/dev/null && guider_connected; then
      ok=$((ok + 1)); details+="reconnect#$i ok; "
    else
      details+="reconnect#$i FAILED $(jq -c '.Error' <<<"$r" 2>/dev/null); "
    fi
  done
  drv_count="$(docker exec "$CONTAINER" sh -c "pgrep -c -f ^$INDI_CAM_DRIVER" 2>/dev/null)"
  [ "$ok" -eq 2 ] && [ "${drv_count:-0}" -le 1 ]
  check "D1 disconnect/reconnect x2" $? "${details}$INDI_CAM_DRIVER processes: ${drv_count:-0}"

  api /equipment/mount/disconnect 60 > /dev/null
  sleep 2
  local mc; mc="$(mount_info | jq -r '.Response.Connected')"
  local n0; n0="$(wc -l < "$RUN/notifications.log")"
  if guider_connected; then
    start_guiding D false 120
    sleep 5
    local st; st="$(guider_state)"
    local msg; msg="$(tail -n +"$((n0 + 1))" "$RUN/notifications.log" | grep -a -i 'native guider' | tail -1 | cut -c1-220)"
    [ "$st" != "Guiding" ] && [ -n "$msg" ] && api_up
    check "D2 start without mount fails cleanly" $? "mount Connected=$mc; start returned after ${START_SECS}s: $(jq -c '.Response' <<<"$START_BODY" 2>/dev/null); State=$st; notification: ${msg:-none}"
  else
    skip "D2 start without mount fails cleanly" "guider not connected"
  fi

  local pid; pid="$(nina_pid)"
  local unhandled; unhandled="$(pins_log_grep -a -c -i 'unhandled exception\|FATAL' | awk '{s+=$1} END {print s+0}')"
  api_up && container_running && [ "$pid" = "$NINA_PID" ] && [ "$unhandled" -eq 0 ]
  check "D3 PINS still running" $? "API up, NINA pid $pid (start $NINA_PID), unhandled/fatal log entries: $unhandled"
  # D4: typo in the guide camera device name must fail cleanly and name the available devices.
  api "/equipment/guider/set-setting?settingName=GuideCameraDevice&newValue=$(urlenc 'Bogus Camera')" > /dev/null
  api /equipment/guider/disconnect 60 > /dev/null; sleep 2
  n0="$(wc -l < "$RUN/notifications.log")"
  r="$(api "/equipment/guider/connect?to=$GUIDER_ID" 180)"
  sleep 2
  local msg4; msg4="$(tail -n +"$((n0 + 1))" "$RUN/notifications.log" | grep -a -i 'native guider' | tail -1 | cut -c1-260)"
  ! guider_connected && [ -n "$msg4" ] && api_up
  check "D4 wrong camera device fails cleanly" $? "connect: $(jq -c '{Success,Error}' <<<"$r" 2>/dev/null); notification: ${msg4:-none}"
  r="$(api "/equipment/guider/set-setting?settingName=GuideCameraDevice&newValue=$(urlenc "$INDI_CAM_DEVICE")")"
  info "D: known limitation - settings while disconnected: set-setting -> $(jq -c '{Error,StatusCode}' <<<"$r" 2>/dev/null)"

  pid="$(nina_pid)"
  api_up && container_running && [ "$pid" = "$NINA_PID" ]
  check "D5 PINS still running (final)" $? "API up, NINA pid $pid (start $NINA_PID)"
}

############################################################################
setup
phase_A
has_phase B && phase_B
has_phase C && phase_C
has_phase E && phase_E
has_phase D && phase_D

# Plugin errors in the PINS log (with stack traces) for the report
pins_log_grep -a -A12 -E '\|ERROR\|' 2>/dev/null | grep -a -B2 -A12 -i 'PinsGuider\|NativeGuider\|Native guider' > "$RUN/plugin-errors.txt" 2>/dev/null
plugin_err_count="$(grep -a -c '|ERROR|' "$RUN/plugin-errors.txt" 2>/dev/null)"

echo
info "=============== SUMMARY ==============="
cat "$RUN/summary.txt"
info "PASS=$PASSES FAIL=$FAILS SKIP=$SKIPS; plugin-related ERROR entries in PINS log: ${plugin_err_count:-0} ($RUN/plugin-errors.txt)"
info "notifications: $(grep -a -c NOTIFY "$RUN/notifications.log" 2>/dev/null) ($RUN/notifications.log)"
[ "$FAILS" -eq 0 ]
exit $?
}
