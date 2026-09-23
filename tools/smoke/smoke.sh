#!/usr/bin/env bash
# Smoke test of the full image: TNS native guider API + WebSocket + UI screenshot against the built-in simulator.
set -uo pipefail
IMAGE="${IMAGE:-aduffeck/pins:native-guider}"
C=pins-ng-smoke
D=/home/andre/astro/sw/pins-wt/ng-test
HOME_DIR=$D/home
API=http://127.0.0.1:31888/v2/api
TNS=http://127.0.0.1:35000
GUID=3e519099-8e1e-46ee-bed2-f775abd90619
ok=0; bad=0
res() { if [ "$1" -eq 0 ]; then ok=$((ok+1)); echo "PASS $2 ${3:-}"; else bad=$((bad+1)); echo "FAIL $2 ${3:-}"; fi; }
run() { docker run -d --name $C -e PHD2_AUTOSTART=false -e PINSDAEMON_AUTOSTART=false -e TZ=UTC -p 31888:1888 -p 35000:5000 -v "$HOME_DIR:/home/pins" "$IMAGE" >/dev/null; }
up() { for i in $(seq 1 80); do curl -s -m 3 $API/version | jq -e .Success >/dev/null 2>&1 && return 0; sleep 3; done; return 1; }
docker rm -f $C >/dev/null 2>&1; rm -rf "$HOME_DIR"; mkdir -p "$HOME_DIR"
run; up || { echo "pins did not come up"; docker logs $C | tail -30; exit 1; }
PID=$(curl -s "$API/profile/show?active=true" | jq -r .Response.Id)
curl -s "$API/profile/change-value?settingpath=GuiderSettings-GuiderName&newValue=PinsNativeGuider" >/dev/null
sleep 3; docker stop -t 60 $C >/dev/null; docker rm $C >/dev/null
python3 /home/andre/astro/sw/pins-wt/e2e/seed_plugin_settings.py "$HOME_DIR/.local/share/NINA/Profiles/$PID.profile" $GUID GuideCameraDriver=simulator ExposureSeconds=1 >/dev/null
run; up || { echo "pins did not come up (2)"; exit 1; }
for i in $(seq 1 40); do curl -s -m 3 $TNS/api/native-guider/status >/dev/null 2>&1 && break; sleep 3; done

r=$(curl -s $TNS/api/native-guider/status); echo "$r" | jq -e '.success == true' >/dev/null; res $? "TNS status before connect" "$(echo "$r" | jq -c '.response|{available,connected,deviceId}')"
r=$(curl -s $TNS/api/native-guider/settings); n=$(echo "$r" | jq '.response.settings|length'); [ "${n:-0}" -gt 40 ]; res $? "TNS settings before connect" "$n settings"
r=$(curl -s -X POST -H 'Content-Type: application/json' -d '{"name":"MinSnr","value":"7"}' $TNS/api/native-guider/settings); echo "$r" | jq -e '.success==true' >/dev/null; res $? "TNS set setting" "$(echo "$r" | jq -c '.response.value? // .error')"
r=$(curl -s -m 120 "$API/equipment/guider/connect?to=PinsNativeGuider"); echo "$r" | jq -e '.Success==true' >/dev/null; res $? "connect guider" "$(echo "$r" | jq -c .Response)"
r=$(curl -s -X POST $TNS/api/native-guider/loop); echo "$r" | jq -e '.success==true' >/dev/null; res $? "loop" "$(echo "$r" | jq -c .response)"
sleep 6
curl -s -o $D/frame.jpg -D $D/frame.hdr "$TNS/api/native-guider/image?maxWidth=800"; file $D/frame.jpg | grep -q JPEG; res $? "image JPEG" "$(grep -i x-frame-number $D/frame.hdr | tr -d '\r')"
r=$(curl -s "$TNS/api/native-guider/frame-info"); echo "$r" | jq -e '.response.width > 0' >/dev/null; res $? "frame-info" "$(echo "$r" | jq -c '.response|{frameNumber,width,height,stars:(.stars|length)}')"
python3 - <<'PY' > $D/ws.txt 2>&1 &
import asyncio, json, websockets, collections
async def main():
    c = collections.Counter()
    async with websockets.connect("ws://127.0.0.1:35000/ws/native-guider") as ws:
        try:
            if True:
                async with asyncio.timeout(150):
                    while True:
                        m = json.loads(await ws.recv())
                        c[m.get("type")] += 1
        except (TimeoutError, Exception):
            pass
    print(json.dumps(c))
asyncio.run(main())
PY
WSPID=$!
r=$(curl -s -X POST "$TNS/api/native-guider/start-guiding?calibrate=true"); echo "$r" | jq -e '.success==true' >/dev/null; res $? "start guiding (202)" "$(echo "$r" | jq -c .response)"
for i in $(seq 1 60); do s=$(curl -s $TNS/api/native-guider/status | jq -r .response.status.state); [ "$s" = Guiding ] && break; sleep 5; done
[ "$s" = Guiding ]; res $? "reaches Guiding" "state=$s"
sleep 60
r=$(curl -s "$TNS/api/native-guider/steps?max=100"); n=$(echo "$r" | jq '.response|length'); [ "${n:-0}" -gt 10 ]; res $? "steps" "$n steps"
r=$(curl -s $TNS/api/native-guider/status); echo "$r" | jq -e '.response.status.windowStats.rmsTotalArcsec < 1.5' >/dev/null; res $? "status stats" "$(echo "$r" | jq -c '.response.status|{state,starsUsed,rms:.windowStats.rmsTotalArcsec,snr:.primaryStar.snr}')"
r=$(curl -s $TNS/api/native-guider/calibration); echo "$r" | jq -e '.response.raRatePxPerSec > 0' >/dev/null; res $? "calibration" "$(echo "$r" | jq -c '.response|{raAngleDeg,decAngleDeg,orthogonalityErrorDeg}')"
r=$(curl -s -X POST "$TNS/api/native-guider/dither?pixels=3"); echo "$r" | jq -e '.success==true' >/dev/null; res $? "dither" "$(echo "$r" | jq -c .response)"
sleep 40
r=$(curl -s "$TNS/api/native-guider/alerts?max=50"); echo "$r" | jq -e '.success==true' >/dev/null; res $? "alerts" "$(echo "$r" | jq -c '[.response[].codeName]')"

# --- Guiding Coach: short session while guiding (camera check, drift, mount response, trials) ---
r=$(curl -s $TNS/api/native-guider/coach); echo "$r" | jq -e '.response.phase == "Idle" and .response.gainMax > .response.gainMin' >/dev/null; res $? "coach idle + gain range" "$(echo "$r" | jq -c '.response|{phase,gainMin,gainMax,currentGain,currentExposureSeconds}')"
python3 - <<'PY' > $D/ws-coach.txt 2>&1 &
import asyncio, json, websockets, collections
async def main():
    c = collections.Counter()
    async with websockets.connect("ws://127.0.0.1:35000/ws/native-guider") as ws:
        try:
            async with asyncio.timeout(1300):
                while True:
                    m = json.loads(await ws.recv())
                    c[m.get("type")] += 1
                    if m.get("type") == "coach" and (m.get("payload") or m.get("data") or {}).get("phase") in ("Complete", "Failed", "Cancelled"):
                        break
        except (TimeoutError, Exception):
            pass
    print(json.dumps(c))
asyncio.run(main())
PY
WSCPID=$!
sleep 2
OPTS='{"exposureSeconds":[1],"framesPerCombination":3,"driftSeconds":120,"trialSeconds":30,"repeatBaseline":false}'
r=$(curl -s -X POST -H 'Content-Type: application/json' -d "$OPTS" $TNS/api/native-guider/coach/start); echo "$r" | jq -e '.success==true and .response.accepted==true' >/dev/null; res $? "coach start while guiding" "$(echo "$r" | jq -c '.response|{accepted,phase:.status.phase,steps:[.status.steps[]?.name]}')"
SID=$(curl -s $TNS/api/native-guider/coach | jq -r .response.sessionId)
code=$(curl -s -o $D/coach-busy.json -w '%{http_code}' -X POST -H 'Content-Type: application/json' -d "$OPTS" $TNS/api/native-guider/coach/start)
sid2=$(curl -s $TNS/api/native-guider/coach | jq -r .response.sessionId); ph=$(curl -s $TNS/api/native-guider/coach | jq -r .response.phase)
[ "$code" = 409 ] && jq -e '.messageCode=="coach.busy"' $D/coach-busy.json >/dev/null && [ "$sid2" = "$SID" ] && [ "$ph" = Running ]; res $? "second start rejected, session untouched" "http $code $(jq -c '{messageCode}' $D/coach-busy.json) phase=$ph"
r=$(curl -s $TNS/api/native-guider/status); echo "$r" | jq -e '.response.status.coachRunning == true' >/dev/null; res $? "status.coachRunning" "$(echo "$r" | jq -c '.response.status|{state,coachRunning}')"
last=""; for i in $(seq 1 240); do
  c=$(curl -s $TNS/api/native-guider/coach); ph=$(echo "$c" | jq -r .response.phase); st=$(echo "$c" | jq -r '.response.step // "-"')
  cur="$ph/$st/$(echo "$c" | jq -r '[.response.steps[]? | select(.state=="Running") | .detailCode // ""] | first // ""')"
  [ "$cur" != "$last" ] && echo "  coach $(date +%H:%M:%S) $cur" && last="$cur"
  [ "$ph" != Running ] && break; sleep 5
done
echo "$c" > $D/coach-final.json
[ "$ph" = Complete ]; res $? "coach session completes" "phase=$ph message=$(jq -r '.response.messageCode // ""' $D/coach-final.json) steps=$(jq -c '[.response.steps[]|{(.name):.state}]|add' $D/coach-final.json)"
jq -e '.response.report as $r | ($r.grade|test("excellent|good|fair|poor|unknown")) and ($r.steps|length) >= 3 and ($r.findings|length) > 0' $D/coach-final.json >/dev/null; res $? "coach report card" "$(jq -c '.response.report|{grade,gradeRatio,guidedRmsArcsec,guidedSource,seeingArcsec,centroidNoiseArcsec,mountArcsec,actions:(.actions|length),findings:(.findings|length)}' $D/coach-final.json)"
jq -e '.response.drift.seeingTotalArcsec > 0 and (.response.drift.samples|length) > 20' $D/coach-final.json >/dev/null; res $? "coach drift measured" "$(jq -c '.response.drift|{seeingTotalArcsec,raPeakToPeakArcsec,polarAlignmentErrorArcmin,decDriftArcsecPerMin,driftLimitingExposureSeconds,samples:(.samples|length)}' $D/coach-final.json)"
jq -e '.response.response.backlashState != null and (.response.response.pulses|length) > 0' $D/coach-final.json >/dev/null; res $? "coach mount response" "$(jq -c '.response.response|{backlashState,backlashMs,minEffectivePulseRaMs,minEffectivePulseDecMs,asymmetryRa,asymmetryDec,rateRatioRa,rateRatioDec}' $D/coach-final.json)"
jq -e '[.response.trials[] | select(.rmsTotalArcsec != null)] | length >= 2' $D/coach-final.json >/dev/null; res $? "coach trials" "$(jq -c '[.response.trials[]|{id,state,rms:.rmsTotalArcsec,winner:.isWinner}]' $D/coach-final.json)"
jq -r '.response.report.findings[] | "    finding \(.severity) \(.code) \(.parameters|tostring|.[0:110])"' $D/coach-final.json | head -30
for i in $(seq 1 24); do s=$(curl -s $TNS/api/native-guider/status | jq -r .response.status.state); [ "$s" = Guiding ] && break; sleep 5; done
[ "$s" = Guiding ]; res $? "guiding resumed after the coach" "state=$s"
r=$(curl -s "$TNS/api/native-guider/coach/history?max=5"); echo "$r" | jq -e '(.response|length) >= 1' >/dev/null; res $? "coach history" "$(echo "$r" | jq -c '[.response[]|{night,grade,profileName}]')"
AID=$(jq -r '[.response.report.findings[] | select((.changes|length) > 0 and (.applied|not))][0].id // empty' $D/coach-final.json)
if [ -n "$AID" ]; then
  r=$(curl -s -X POST -H 'Content-Type: application/json' -d "{\"ids\":[\"$AID\"]}" $TNS/api/native-guider/coach/apply)
  name=$(jq -r --arg id "$AID" '.response.report.findings[]|select(.id==$id)|.changes[0].name' $D/coach-final.json); want=$(jq -r --arg id "$AID" '.response.report.findings[]|select(.id==$id)|.changes[0].value' $D/coach-final.json)
  now=$(curl -s $TNS/api/native-guider/settings | jq -r --arg n "$name" '.response.settings[]|select(.name==$n)|.value')
  echo "$r" | jq -e '.success==true' >/dev/null && [ "$now" = "$want" ]; res $? "coach apply action" "$AID: $name $want (now $now)"
else
  res 0 "coach apply action" "no action with setting changes in this run"
fi
wait $WSCPID; grep -q '"coach"' $D/ws-coach.txt; res $? "coach websocket events" "$(cat $D/ws-coach.txt)"
python3 $(dirname "$0")/shot.py $TNS $D Coach >/dev/null 2>&1; [ -s $D/ui-desktop-coach.png ]; res $? "Coach tab screenshots" "$D/ui-desktop-coach.png $D/ui-phone-coach.png"
google-chrome --headless=new --no-sandbox --disable-gpu --hide-scrollbars --window-size=1366,1000 --virtual-time-budget=20000 --screenshot=$D/ui-desktop.png "$TNS/guider" >/dev/null 2>&1
google-chrome --headless=new --no-sandbox --disable-gpu --hide-scrollbars --window-size=390,900 --virtual-time-budget=20000 --screenshot=$D/ui-phone.png "$TNS/guider" >/dev/null 2>&1
[ -s $D/ui-desktop.png ]; res $? "UI screenshots" "$D/ui-desktop.png $D/ui-phone.png"
wait $WSPID; [ -s $D/ws.txt ] && grep -q step $D/ws.txt; res $? "websocket events" "$(cat $D/ws.txt)"
docker logs $C 2>&1 | grep -iE "exception|error" | grep -iv "phd2\|pinsdaemon" | tail -5
echo "== $ok passed, $bad failed"
