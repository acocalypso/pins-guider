# PINS Native Guider — Design

Status: agreed 2026-09-23 (design grilling session). Scope: v1.

## 1. Goal

A native autoguider for PINS that replaces the external PHD2 process for users who choose it, driving
INDI guide cameras directly, with **PHD2-level precision** and a rich Touch-N-Stars (TNS) UI.
PHD2 stays selectable side-by-side until the native guider has proven itself on real nights.

Acceptance bar for v1: on the same rig and night, total RMS within ~10% of PHD2, both on defaults.

## 2. Architecture

```
 ┌──────────────── pins-guider (this repo, MPL-2.0) ─────────────────┐
 │                                                                   │
 │  PinsGuider.Engine  (pure C#, net10.0, no NINA/WPF dependency)     │
 │   Imaging ─ Stars ─ MultiStar ─ Calibration ─ Algorithms ─ Guider  │
 │   Stats ─ GuideLog ─ Safety ─ Events                               │
 │            ▲ ICameraSource  ▲ IPulseOutput  ▲ IMountState  ▲ IClock │
 │  PinsGuider.Plugin  (M2: NINA plugin)                              │
 │   IEquipmentProvider<IGuider> → NativeGuider : IGuider,            │
 │   IAdvancedGuider; INDI camera source; pulse outputs; profile      │
 │   settings; calibration store                                      │
 └───────────────────────────────────────────────────────────────────┘
        │ IGuider (sequencer, ninaAPI)      │ IAdvancedGuider (PINS core contract)
        ▼                                   ▼
   NINA sequencer / ninaAPI           TNS plugin: /api/guider/*, /ws/guider
                                            ▼
                                      TNS frontend guider page
```

* **Engine** is host-agnostic and deterministic under test: time comes from `IClock`, frames from
  `ICameraSource`, corrections go to `IPulseOutput`, mount state from `IMountState`. This allows
  unit tests, frame replay and a closed-loop simulator without hardware.
* **Plugin** (M2) exports an `IEquipmentProvider<IGuider>` so the guider appears in the chooser next
  to PHD2. Its device Id is distinct (`PinsNativeGuider`) so TNS can branch its UI.
* **PINS core changes** (M2, branch in `aduffeck/pins`): INDI `GuideCamera` category with its own
  driver setting (loading it must not unload the main camera driver); a **second dedicated
  indiserver connection** for guide-camera BLOBs and guide pulses (no head-of-line blocking behind
  main-camera BLOBs); in-memory FITS decode (no temp file); `IAdvancedGuider` contract interface.
* **TNS plugin** (M3, branch in aduffeck fork): controller `/api/guider/*` and WebSocket
  `/ws/guider` pushing every guide step, frame metadata and a downscaled JPEG per frame. The TNS
  2 s poll remains as fallback.
* **TNS frontend** (M4, branch in aduffeck fork): new guider page (see §8).

## 3. Hardware scope (v1)

* Guide cameras: INDI only (behind `ICameraSource`, native SDK cameras later).
* Correction outputs (selectable): mount pulse guide `TELESCOPE_TIMED_GUIDE_NS/WE` (default),
  camera ST4 port (INDI guider interface on the camera device), separate INDI guide-port device.
* Target host: Raspberry Pi 5 / Pi 4 4 GB, ARM64. Budget: < 150 ms processing for a 1936×1216
  frame including detection and all tracked stars.

## 4. Engine

The core is a faithful port of PHD2 (commit `a6c02722`, BSD-3). Ported files keep the PHD2
copyright header and name the source file (see `THIRD_PARTY_NOTICES.md`). KStars (GPL) is used for
ideas only — no code is copied.

### 4.1 Imaging pipeline
Capture → dark subtraction **or** defect-map correction → optional noise reduction (2×2 mean /
3×3 median) → optional software binning → star processing. Frames are `ushort` with a pedestal.
* Dark library: 5 frames per exposure, pick smallest dark ≥ exposure else longest;
  `pedestal = max(median(dark) − median(light), 0)`, `out = clamp(light + pedestal − dark)`.
* Defect map: master dark, 15×15 median filtered, σ-threshold hot/cold pixels, replaced by the
  median of their 8 neighbours. Replaces dark subtraction when loaded.
* No auto-exposure in v1.

### 4.2 Star finding (port of `star.cpp`)
* `Star.Find`: 3×3 [1 2 1] smoothed peak search within ±searchRegion; background annulus
  r ∈ (7, 12] with iterative 2σ clipping; single-pass background-subtracted first moment over
  pixels r ≤ 7 above bg + 3σ; SNR = mass / sqrt(mass/0.5 + σ²·n·(1 + 1/nbg)); HFD by cumulative
  mass interpolation; saturation test; result codes (LowMass <10, LowSNR <3, LowHFD <1.5, HiHFD >20,
  MassChange, Saturated, Edge, Error).
* `AutoFind`: median3 → optional ×2 downsample → 9×9 PSF convolution → local maxima → merge,
  crowding and edge rejection → 3-pass selection → multi-star candidate list.
  Extra (KStars idea, own code): penalise lowest-HFD candidates (hot pixels), crowding, edges.
* Mass checker: 0.5 threshold, 45 s window, median + high/low water marks.

### 4.3 Multi-star (port of `guider_multistar.cpp` + fallback)
* Up to 9 stars, min SNR 6. Always full frames; per frame only small search boxes around known
  stars are measured; full-frame AutoFind only at start, reacquire, and after dither recovery.
* PHD2 combination: 5-frame stabilisation, 5σ enter / 2σ exit, per-star 2.5σ rejection, SNR-ratio
  weights, weighted offset used only if smaller than the primary-only offset; hot-pixel (zero
  displacement) eviction; reference re-snap after lock moves. Optional jump filter.
* **Primary-dropout fallback** (default on, toggle): if the primary is lost/saturated for a frame and
  ≥ 3 secondaries agree, the primary position is estimated from the secondaries' displacements and
  guiding continues instead of emitting StarLost.
* Optional subframe mode: single star, faster download.

### 4.4 Calibration (port of `scope.cpp`, `mount.cpp`, `calstep_dialog.cpp`)
* Distance `ceil(max(25 px, 20″/scale))`; step from guide rate, declination, 12 steps, 50 ms
  rounding. Default 750 ms if rate unknown.
* State machine WEST → EAST → CLEAR_BACKLASH → NORTH → SOUTH → NUDGE_SOUTH → COMPLETE with fast
  recenter; separate xAngle/yAngle and xRate/yRate; PHD2 camera→mount transform with yAngleError.
* Sanity alerts (overrideable warnings): steps < 4, orthogonality error > 12.5°, RA/Dec rate ratio vs
  cos(dec) off by > 0.2, Dec rate differs > 20 % from previous calibration.
* Reuse: stored per profile keyed by camera + mount + binning + focal length; adjustments for
  binning, declination (skip / alert if calibrated above |60°|), pier side (flip xAngle, Dec flip per
  mount setting), guide-rate change > 5 % alert, pixel-size change invalidates.
* **Post-flip Dec self-check**: for the first 5 guide frames after a pier-side change, verify Dec
  corrections reduce the error; if the error grows consistently in the corrected direction, invert
  Dec, alert, persist the mount setting.

### 4.5 Guide algorithms
Pluggable `IGuideAlgorithm` (so PPEC/GP and dark guiding can land in v2).
* RA default Hysteresis (aggression 0.7, hysteresis 0.1), alternative Lowpass2 (aggressiveness 80 %).
* Dec default ResistSwitch (aggression 1.0, fast switch on), alternatives Hysteresis/Lowpass2.
* Min-move default `max(0.1515 + 0.1548/scale, 0.15)` px.
* Dec guide mode Off / Auto / North / South. Dec backlash compensation (adaptive, PHD2 port).
* Max pulse 2500 ms per axis (hard clamp).

### 4.6 Loop, timing, settling, dithering
* Fixed-cadence loop on its own task: expose → process → send RA pulse then Dec pulse (per-mount
  option: simultaneous) → wait for pulse completion → next exposure.
* Settling exactly as PHD2: EMA α = 0.3 of offset distance (RA-only when RA-only), value 100 if no
  star for > 20 s, success after `time` in range, failure after `timeout`. Mapped from NINA
  `SettlePixels`, `SettleTime`, `SettleTimeout`.
* Dither: random ±amount × scale (RA-only option), mount→camera conversion, edge reflection;
  fast recenter moves after dither and calibration.
* Events use PHD2 names and fields (GuideStep, StarLost, SettleBegin/Settling/SettleDone,
  StartCalibration/Calibrating/CalibrationComplete/CalibrationFailed, GuidingDithered, Paused,
  Resumed, LockPositionSet, StarSelected, Alert, AppState). No PHD2 TCP/JSON-RPC server.

### 4.7 Statistics
RMS RA/Dec/total (population σ over a window, px and ″, dither/settle excluded), peak per axis,
drift RA/Dec (″/min, linear fit), polar alignment error estimate (3.8197·|decDrift px/min|·scale /
cos dec), oscillation index, correction duty (% frames with a pulse), SNR min/avg, star count.

### 4.8 Guide log
PHD2-compatible guide log format so PHD2LogViewer and similar tools can analyse sessions.

## 5. Safety and error handling

| Condition | Behaviour |
|---|---|
| Star lost | No corrections; reacquire 60 s (last position, then full-frame AutoFind, mass/SNR plausibility vs previous star); then `Failed` + event |
| Max pulse | Hard clamp per axis; repeated limit hits → alert |
| Runaway (total correction per minute over limit) | Stop guiding + alert |
| Mount not responding (≥ 3 consecutive large pulses, no star movement) | Stop guiding + alert |
| Calibration sanity failure | Warning, user may override |
| Pier side changed | Auto flip per rules + Dec self-check; option to force recalibration |
| Mount slewing / parked / homing / tracking off | Auto-pause; resume after reacquire + settle; after large move only on sequencer request |
| Camera stall / BLOB timeout | Retry exposure ×2 → reconnect camera ×1 → `Failed` |

Every error has a stable code, a human explanation and a suggested fix, used identically in logs,
API and TNS. Guiding-stopping errors additionally trigger a push notification.

## 6. Settings & persistence
Stored in the NINA profile (per-profile plugin settings). Calibrations stored per profile, keyed by
guide camera + mount + binning + focal length; auto-invalidated on change.

## 7. Sequencer integration
Existing Start/Stop Guiding, Dither, Restore Guiding and RMS-based triggers work unchanged via
`IGuider`. v1 adds a "Change native guider parameters" instruction.

## 8. TNS UI
* State strip: Looping → Calibrating (step n/N) → Guiding → Settling → Lost → Paused; live RMS
  (total/RA/Dec ″), primary SNR, star count.
* Live frame: stretched guide frame, overlays (primary, secondaries coloured by weight with SNR
  labels, rejected stars with reason, lock position, dither target), pinch-zoom, star-profile inset
  (radial profile + HFD).
* Graph (uPlot): RA/Dec error lines, correction bars, ″/px toggle, 50/100/200/400 frame windows,
  dither/settle/lost/calibration markers, SNR/mass sub-trace. Target scatter plot with RMS circles.
* Stats card, calibration card (vector diagram, orthogonality, rates, verdict), event log with error
  codes, Basic/Advanced settings sheet, dark-library / defect-map assistant, setup-wizard step.
* Phone: tabbed stack (frame / graph / stats / calibration). chart.js stays elsewhere in TNS.

## 9. Scope
* v1: everything above.
* v2: guiding assistant (seeing, backlash measurement, min-move recommendation), predictive PEC
  (GP port), dark guiding, manual star selection, auto-exposure, native SDK cameras.

## 10. Validation
1. Unit tests on synthetic star fields (sub-pixel truth, SNR, noise).
2. Parity tests: centroid/SNR/HFD compared to PHD2's compiled `star.cpp` on identical frames
   (≈ 0.01 px).
3. Closed-loop simulator (mount drift, periodic error, seeing, backlash) and frame replay.
4. End-to-end against INDI CCD + telescope simulators (M2).
5. Pi 5 profiling (M5). Side-by-side sky tests vs PHD2 (M5).

## 11. Milestones
1. **M1 Engine core** — this repo, pure C#, tests + parity + simulator.
2. **M2 PINS integration** — INDI core changes, plugin, outputs, mount watch, settings, calibration
   store, guide log, INDI-simulator E2E.
3. **M3 API** — `IAdvancedGuider`, TNS plugin controller + WebSocket, specs.
4. **M4 TNS UI**.
5. **M5 Hardening** — Pi profiling, sky tests.

## 12. Repos & git
* `aduffeck/pins-guider` (this repo), later a submodule under `pins/NINA.Plugins/`.
* PINS core changes: branch in `aduffeck/pins`. TNS plugin + frontend: branches in aduffeck forks.
* Agents push only to aduffeck remotes. Upstream PRs, public mirrors and releases are human-owned.
