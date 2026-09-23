# Guiding Coach — design

Status: agreed 2026-09-23. Replaces the "port PHD2's Guiding Assistant" idea: PHD2's assistant is inspiration
only. Goal: **teach the user how to get better guiding and which settings to use** — diagnose, explain,
recommend, *prove* recommendations on the user's own sky, and track progress over time.

## 1. Principles

* **Findings, not numbers.** Every measurement becomes a finding: what was measured, whether it is good,
  *why it matters* and *how to fix it*, with an estimated impact on guiding RMS so actions can be ranked.
* **Codes + parameters.** The engine emits findings as stable codes with numeric/string parameters; the
  UI renders localized teaching texts from the code (all 14 TNS languages). Engine English text is only
  a fallback for logs.
* **Prove it.** Recommended settings are verified with short guided trials against the current settings.
* **Safe and reversible.** Measurements never leave guiding in a changed state: temporary settings are
  restored unless the user applies them; safety monitors never trip because of a measurement; cancel works
  at any time.
* **Progress over time.** Each completed session produces a report that is stored; the UI compares with
  earlier reports.

## 2. Session and steps

A session runs selected steps in this order (all selected by default), then builds the report:

| Step | Needs | Default duration | What it does |
|---|---|---|---|
| 1 CameraCheck | camera; guiding/looping is stopped by the coach | ~2–3 min | exposure × gain sweep (existing settings-finder measurement) |
| 2 Drift | calibration (the coach calibrates first if needed) and a star | 3 min (min 2) | guiding output off; seeing vs. mount error |
| 3 MountResponse | calibration | ~1–2 min | Dec backlash, pulse response per direction |
| 4 Trials | calibration | 4 × 2 min | guided A/B comparison of settings sets |
| 5 Report | — | — | error budget, grade, ranked actions, history |

Steps can be skipped (`SkipCoachStep`) or the whole session cancelled. A step that cannot run (e.g. no
calibration and calibration fails) produces a `problem` finding explaining why and the session continues
with the remaining steps where possible.

### 2.1 CameraCheck
Per exposure × gain (defaults: exposures 1, 2, 3 s; gains: current plus two spread over the camera's gain
range; 5 frames each) measure SNR, HFD, saturation, usable stars (AutoFind count ≥ min SNR) and **centroid
jitter** (std of primary positions after removing a linear drift) in px and arcsec. Feasible = not
saturated, SNR ≥ 15, star found. Recommended = lowest jitter; within 10 % prefer more stars, then shorter
exposure. The recommended exposure/gain are used (temporarily) for the following steps.

Findings (examples): `camera.recommendation` {exposure, gain, jitter, snr, stars}, `camera.snrLow` {snr,
noiseArcsec}, `camera.saturated` {exposure, gain}, `camera.defocused` {hfd} (HFD > 5 px or > 3″),
`camera.fewStars` {stars} (< 3: multi-star can't help), `camera.noDarks` (no dark library loaded; hot pixels),
`camera.good`.

### 2.2 Drift (guiding output off)
Samples mount-axis star positions (calibration transform) every frame for `DriftSeconds`.
* **Seeing** = high-frequency RMS per axis (high-pass, cutoff ~ max(6 s, 3 × exposure)).
* **RA periodic error**: detrend, then least-squares sinusoid scan over periods 60–1200 s (only report a
  period when the run covers ≥ 1.2 periods; otherwise peak-to-peak and max rate only). Amplitude, period,
  max drift rate ″/s.
* **Dec drift** (linear fit) → **polar alignment error** ≈ 3.8197·|decDrift px/min|·scale/cos(dec)
  (dec unknown → assume 0, parameter `decAssumed`).
* **Drift-limiting exposure**: longest exposure for which RA drift during an exposure stays below the
  seeing RMS.
* **Wind/gusts**: fraction of frame-to-frame jumps > 4 σ of the high-frequency jitter.

Findings: `drift.seeing` {rms, level good|average|poor}, `drift.periodicError` {amplitude, period},
`drift.raRate` {maxRate}, `drift.polarAlignment` {arcmin, decAssumed}, `drift.exposureLimit` {seconds},
`drift.wind` {fraction}.

### 2.3 MountResponse (pulses, guiding output off)
* **Dec backlash**, own method (fixes PHD2's low bias and edge cases): clear backlash northwards until
  consecutive moves are consistent, then reverse to south with fixed pulses and measure the pulse time
  spent before the star moves at the calibrated rate again (dead band = consumed ms). Report ms and ″.
* **Pulse response**: for each direction (E, W, N, S) pulses of 100/250/500/1000 ms (capped at max pulse)
  twice; measured move vs expected (calibration rate). Yields the **minimum effective pulse** (smallest
  pulse reaching ≥ 50 % of the expected move), **asymmetry** (W vs E, N vs S ratio) and **rate mismatch**
  (measured/calibrated rate).

Findings: `response.decBacklash` {ms, arcsec}, `response.minPulse` {axis, ms}, `response.asymmetry`
{axis, ratio}, `response.rateMismatch` {axis, ratio}, `response.good`.

### 2.4 Trials (guided)
Candidate settings sets derived from the previous steps:
* **A – current** settings;
* **B – coach suggestion**: min-move from seeing (≈ 1.28–1.65 × seeing σ in px, floor 0.1 px), RA
  aggression/hysteresis from PE rate and oscillation, Dec: backlash compensation from the backlash result,
  Dec guide mode North/South when backlash is large and drift is one-sided, exposure/gain from CameraCheck
  capped by the drift-limiting exposure;
* **C – variant**: B with the one parameter the coach is least sure about changed (e.g. RA aggression
  ±0.15 or the alternative exposure);
* **A′ – current again** (optional `RepeatBaseline`, default on) to detect changing conditions.
Each trial: apply temporarily, settle (tolerance from the profile, max 30 s), then guide `TrialSeconds`,
measure RMS RA/Dec/total, peak, oscillation index, frames. Winner = lowest total RMS; if A and A′ differ by
> 25 % a `trials.conditionsChanged` finding warns that the comparison is unreliable. Settings are restored
at the end (the user applies the winner explicitly).

Findings: `trials.winner` {id, rms, improvement}, `trials.conditionsChanged` {change}, `trials.noImprovement`.

## 3. Report
* **Guided RMS** (from trials, else from the last guiding window).
* **Error budget** (quadrature): seeing (Drift), centroid noise (CameraCheck, ≈ HFD/SNR-based estimate at
  the chosen settings), mount/other = √(max(0, guided² − seeing² − noise²)). Backlash and polar alignment
  are listed as contributors with their findings.
* **Grade** from RMS / imaging scale (profile camera pixel size + telescope focal length):
  excellent < 0.5, good < 0.75, fair < 1, poor ≥ 1 (no imaging scale → grade by RMS in ″).
* **Ranked actions**: findings with severity warning/problem ordered by `ImpactArcsec` (estimated RMS
  improvement), each with an optional one-tap *Apply* (setting + value).
* **History**: the host stores reports; the UI shows trends (RMS, grade, PA error, backlash) and compares with
  the previous report.

## 4. Live hints (while guiding normally)
A lightweight analyser over the guiding statistics window emits dismissable hints (same finding codes,
`hint.*`), rate-limited to one per code per 10 minutes: `hint.raOscillation` (oscillation index > 0.6 →
lower RA aggression), `hint.raSluggish` (index < 0.15 with large RMS → raise aggression),
`hint.pulseLimited`, `hint.lowSnr`, `hint.decDrift` (one-sided Dec corrections → polar alignment),
`hint.seeingBound` (RMS close to the seeing floor: settings are fine, seeing limits).

## 5. Engine/plugin/UI split
* Engine (`PinsGuider.Engine.Coach`): session orchestrator (async, drives the Guider through its public API
  plus internal measurement hooks), step implementations, analysers, findings, report, live hint analyser;
  tests with the closed-loop simulator in virtual time.
* Plugin: `IAdvancedGuider` Coach members → engine; applies recommendations to the plugin settings; stores
  reports as JSON (`~/.local/share/NINA/NativeGuider/Coach/`); provides the imaging scale and gain range.
* TNS: plugin API `/api/native-guider/coach/*` + WebSocket event types `coach` and `hint`; a *Coach* tab on
  the native guider page (step selection, live step views, report card, history) and a hint chip in the state
  strip. All finding codes have localized texts: title, explanation (why it matters) and how to fix.

## 6. Contract

`IAdvancedGuider` (pins `NINA.Equipment/Interfaces/IAdvancedGuider.cs`): `StartCoach(options)` (true = accepted and
running in the background; false = rejected, `Phase=Failed` + `Message`/`MessageCode`), `SkipCoachStep`,
`CancelCoach`, `GetCoachStatus`, `ApplyCoachActions(ids)` (finding ids or `trial:<id>`), `GetCoachHistory(max)`,
`DismissHint(id)`; `AdvancedGuiderStatus.Hints` / `CoachRunning`; events `coach` (AdvancedCoachStatus) and `hint`
(AdvancedCoachFinding). The engine mirrors these DTOs in `PinsGuider.Engine.Coach`; the plugin maps them 1:1.

Session rules:
* Accepted from Stopped/Looping/Selected/Guiding/Paused; rejected while calibrating, building darks, disconnected or
  when another session runs (`coach.busy`, `coach.notConnected`).
* Guiding active at the start → resumed with the original settings when the session ends/cancels/fails.
* External Start/Stop guiding, dither, loop stop, disconnect or a mount slew cancel the session (`coach.interrupted`
  with parameter `reason`); temporary settings are restored in every exit path.
* Measurement frames (CameraCheck, Drift, MountResponse) are excluded from the guiding stats and safety monitors;
  trials use normal guiding (stats and safety active; runaway during a trial fails that trial, not the session).
* Setting names in `Changes` are the plugin setting names (`AdvancedGuiderSetting.Name`): ExposureSeconds, Gain,
  Binning, MultiStar, RaAggression, RaHysteresis, RaMinMove, DecAggression, DecMinMove, DecGuideMode, DecAlgorithm,
  BacklashCompensation, BacklashPulseMs, MaxRaDurationMs, MaxDecDurationMs. Values are invariant-culture strings.

Failure/step message codes: `coach.busy`, `coach.notConnected`, `coach.noStar`, `coach.noCalibration`,
`coach.calibrationFailed`, `coach.starLost`, `coach.noPulseOutput`, `coach.interrupted` {reason: guiding|dither|
stopped|slew|disconnect}, `coach.cameraError`, `coach.internal`.

## 7. Finding catalogue

Severity in brackets; `→` = setting changes carried in `Changes`. Parameter units are part of the name
(Arcsec, Px, Ms, Seconds, Percent, Arcmin); unit-less ratios/indices are plain.

| Code | Severity | Parameters | Meaning / action |
|---|---|---|---|
| camera.recommendation | info | exposureSeconds, gain, jitterArcsec, snr, stars, currentJitterArcsec | best exposure × gain → ExposureSeconds, Gain |
| camera.good | good | snr, jitterArcsec | current camera settings are already the best measured |
| camera.noFeasible | problem | reason (saturated\|lowSnr\|noStar) | no combination gave an unsaturated star with SNR ≥ 15 |
| camera.snrLow | warning | snr, noiseArcsec | best SNR < 25: centroid noise adds to the RMS; longer exposure, higher gain, darks, focus |
| camera.saturated | warning | exposureSeconds, gain | current settings saturate the guide star (centroid bias) → lower gain/exposure |
| camera.defocused | warning | hfdPx, hfdArcsec | HFD > 5 px or > 3″ → focus the guide camera |
| camera.fewStars | info | stars | < 3 usable stars: multi-star can't average seeing; longer exposure / different field |
| camera.noDarks | info | — | no dark library: hot pixels can be mistaken for stars; build darks |
| drift.seeing | good/info/warning | rmsArcsec, level (good\|average\|poor) | seeing floor; guiding can't beat it |
| drift.minMove | info | raPx, decPx | min-move from seeing → RaMinMove, DecMinMove |
| drift.periodicError | info/warning | amplitudeArcsec, periodSeconds (null if unknown), maxRateArcsecPerSec | worm PE; PEC, shorter exposure |
| drift.exposureLimit | good/warning | seconds, currentSeconds | exposure longer than the RA drift allows → ExposureSeconds |
| drift.polarAlignment | good/info/warning/problem (<3′/<5′/<10′/≥10′) | arcmin, decAssumed, driftArcsecPerMin | polar alignment |
| drift.wind | warning | fraction | gusts/vibration; shield the scope, avoid chasing (higher min-move) |
| drift.decGuideMode | info | mode (North\|South), driftArcsecPerMin, backlashMs | one-sided Dec guiding avoids backlash → DecGuideMode |
| response.decBacklash | good/info/warning | ms, arcsec | Dec backlash → BacklashCompensation, BacklashPulseMs; or mechanical fix / slight Dec imbalance |
| response.minPulse | warning | axis (Ra\|Dec), ms | pulses shorter than this don't move the mount (stiction) → min-move above it |
| response.asymmetry | warning | axis, ratio | one direction moves much more (balance, gear mesh, guide rate) |
| response.rateMismatch | warning | axis, ratio | measured rate ≠ calibration → recalibrate |
| response.good | good | — | mount responds cleanly |
| trials.winner | good/info | id, rmsArcsec, baselineRmsArcsec, improvementPercent | best set; → its settings |
| trials.noImprovement | good | rmsArcsec | current settings are already the best |
| trials.conditionsChanged | warning | changePercent | baseline changed > 25 %: comparison unreliable |
| report.seeingLimited | good | seeingArcsec, guidedArcsec | seeing dominates: settings are fine |
| report.mountLimited | warning | mountArcsec, guidedArcsec | mount part dominates: see mount findings |
| report.noImagingScale | info | — | set camera pixel size and telescope focal length for a grade relative to the image scale |
| hint.raOscillation | warning | index | RA overcorrects → RaAggression −0.1 (≥ 0.3) |
| hint.raSluggish | info | index, rmsRaArcsec | RA corrects too little → RaAggression +0.1 (≤ 1.0) |
| hint.pulseLimited | warning | axis, percent | many pulses at the max duration → MaxRa/DecDurationMs or recalibrate |
| hint.lowSnr | warning | snr | star SNR dropped (clouds, dew) |
| hint.decDrift | info | driftArcsecPerMin, arcmin | Dec corrections one-sided → polar alignment / DecGuideMode |
| hint.seeingBound | good | rmsArcsec | RMS near the seeing floor: nothing to tune |
