# Next-generation guiding — ideas and evaluation

Status: brainstorm, 2026-09-23. Nothing here is agreed design yet; items move into `DESIGN.md` once decided.
Related: [DESIGN.md](DESIGN.md) (v1, PHD2 port), [COACH.md](COACH.md) (Guiding Coach), [USAGE.md](USAGE.md).

v1 is a faithful PHD2 port (about 60 % of the engine) plus our own controller, safety monitors, simulator, plugin
and UI. Now that every layer is under our control, this document asks which parts we would build differently for
the best possible PINS guider, however unusual the idea.

## 1. Goal: round stars and usable subs, not low RMS

PHD2 optimises guide RMS. What users want is **round stars and more usable subs per night**. That changes the
priorities:

1. **Don't chase seeing.** The high-frequency motion of the guide star is atmosphere; correcting it adds error.
2. **Correct predictable errors before they appear:** periodic error, drift, polar-alignment drift.
3. **Cut latency and dead time.** Today a correction is based on a frame that is already 2–4 s old.
4. **Catch errors the guide camera cannot see.** Differential flexure between guide scope and main scope looks like
   perfect guiding in the RMS.
5. **Stop losing imaging time:** calibration, settling, lost stars, ruined subs.
6. **Be statistically honest.** Only claim improvements or faults that exceed the measurement noise. The first Coach
   run falsely reported stiction and declared a 5.6 % "winner" within the noise.

What we have that PHD2 does not:
- the closed-loop simulator (virtual time)
- the Guiding Coach, which measures seeing, periodic error, backlash, stiction and asymmetry
- on-sky A/B trials
- plate solving and the imaging camera's frames in the same process
- the sequencer next door

## 2. Current loop (v1)

`Guider.cs`: expose → download → process → pulse and **await the pulse** → next exposure. Per axis, a PHD2 filter
(Hysteresis / ResistSwitch / Lowpass2) turns the measured offset into a pulse, shaped by hand-tuned knobs
(aggression, hysteresis, min-move). Calibration is PHD2's W/E/N/S procedure, adjusted for declination and pier
side. The `GaussianProcess` and `ZFilter` algorithm kinds are reserved but not implemented.

## 3. Ideas by area

Scales. Impact: ★ small, ★★ clear, ★★★ large (on star shape or imaging time). Effort: S ≤ 2 days, M ≤ 2 weeks,
L more. Risk: how likely it is to fail or regress.

### 3.1 Control loop

| ID | Idea | Why it is better | Impact | Effort | Risk |
|---|---|---|---|---|---|
| L1 | **Model-based estimator** (Kalman filter per axis): state = position error, drift rate, periodic-error phase and amplitude; pulses as known inputs; seeing = measurement noise, mount = process noise | Balances trusting the measurement against trusting the prediction automatically; aggression, hysteresis and min-move stop being user settings; the noise levels come from the Coach and adapt during the night | ★★★ | M | medium |
| L2 | **Periodic-error prediction** from a harmonic model (worm fundamental + 2 harmonics), seeded by the Coach's fit, updated online | Corrects periodic error before it shows up. Simpler than PHD2's Gaussian-process PPEC (C++/Eigen, not ported) | ★★★ on cheap mounts | M | medium |
| L3 | **Overlapped capture**: start the next exposure right after download, pulse during it; the estimator accounts for the motion inside the frame | 30–50 % more measurements per minute at short exposures, about half the latency | ★★ | M | medium (smeared frames) |
| L4 | **Short exposures + estimator** (0.5–1 s with low-noise CMOS) | Timing information, no periodic-error blur within a frame; may beat single 2–3 s frames | ★★ | S (after L1) | low; decide in the simulator |

L1 and L2 form an **Adaptive** mode that sits next to the PHD2 **Classic** mode (see §5). The engine's algorithm
factory already has a registration hook for kinds outside the core.

### 3.2 Calibration

| ID | Idea | Why | Impact | Effort | Risk |
|---|---|---|---|---|---|
| C1 | **Continuous calibration**: every pulse and the star motion that follows is a calibration sample; recursive least squares with outlier rejection refines rates and angles all night | No recalibrating after big declination changes; detects a changing mount response (balance, cable snag) | ★★★ usability | M | medium (noise; update slowly) |
| C2 | **Calibration from a plate solve**: solve one guide frame (camera angle, exact pixel scale) + the mount's guide rate → predicted calibration in seconds, confirmed by two short pulses | Removes the 2–4 min calibration; exact arcsec values even with a wrong focal length setting. A 248 mm guide scope with an ASI120 (~1.2° field) solves easily; off-axis-guider fields are harder | ★★ | S–M | low (fallback to C3) |
| C3 | **Least-squares calibration** over all recorded step points (already recorded for the plot) instead of start/end displacement | More accurate angles and rates, with an uncertainty estimate | ★ | S | low |
| C4 | **Dithers as calibration samples** | Free extra data points | ★ | S (with C1) | low |

### 3.3 Star measurement

| ID | Idea | Why | Impact | Effort | Risk |
|---|---|---|---|---|---|
| M1 | **Whole-frame registration** (phase correlation, including rotation and scale) instead of star tracking | Uses the flux of every star; ignores hot pixels; works at low signal, with doughnut stars, and even on comets, planets or nebulae. Big win for off-axis guiders with few faint stars. Needs a downsampled or cropped frame on a Pi (target < 30 ms) | ★★ | M | medium (performance) |
| M2 | **Treat all stars equally**: no primary star; a robust fit of shift, rotation and scale to all stars | Survives losing any star; field rotation becomes visible (polar-alignment and flexure indicator) | ★★ | M | low |
| M3 | **Per-frame measurement uncertainty** ≈ star size / SNR (with a camera noise model) | Clouds then mean "trust this frame less" instead of chasing noise; feeds L1 and the Coach's significance tests | ★★ | S | low |
| M4 | **Automatic hot-pixel map**: pixels that stay put while the stars move (after dithers) are defects | No dark frames needed; the map keeps up with sensor ageing and temperature | ★ | S | low |

### 3.4 Camera

| ID | Idea | Why | Impact | Effort | Risk |
|---|---|---|---|---|---|
| K1 | **Automatic exposure and gain**: keep the star inside a target SNR band (clouds, dew, altitude), with hysteresis | The Coach's camera check running continuously; saturated or too-faint frames stop being a user problem | ★★ | S | low |
| K2 | **Readout region** around the stars | Much higher frame rate on USB2 cameras (ASI120); pays off with L3/L4 | ★ (★★★ with L3/L4) | S–M | low |
| K3 | **Direct camera SDK** (ZWO/QHY) instead of INDI for the guide camera | No FITS encode or INDI blob transfer, lower latency, direct readout-region control | ★ at 2 s, ★★★ for fast guiding | L | high (driver conflicts with INDI, per-vendor code) |

### 3.5 Imaging camera

| ID | Idea | Why | Impact | Effort | Risk |
|---|---|---|---|---|---|
| I1 | **Differential flexure correction**: measure the star shift between consecutive imaging subs; a slowly growing shift while guide RMS looks perfect is flexure → slowly move the guider's lock position to compensate | Fixes "RMS 0.5″ but elongated stars" for guide-scope users; no other guider does this well | ★★★ | M | medium (sequencer hooks, star matching between subs) |
| I2 | **Grade guiding by the real image**: star FWHM and eccentricity per sub as the true quality signal; the Coach grade and trials can use it | Optimises what matters instead of a proxy | ★★ | S–M | low |
| I3 | **Unguided mode**: high-precision encoder mounts or short CMOS subs, drift corrected from the imaging frames only; the Coach can detect when a mount does not need a guide camera | One camera less for some users | ★ | M | medium |

### 3.6 Mount

| ID | Idea | Why | Impact | Effort | Risk |
|---|---|---|---|---|---|
| T1 | **Tracking-rate guiding**: feed the estimated drift in as a custom tracking-rate offset (INDI `TELESCOPE_TRACK_RATE` where supported); pulses only for the residual | Smooth, no backlash kick in Dec, polar-alignment drift disappears from the error | ★★ | S–M | medium (driver support varies; pulses stay the fallback) |
| T2 | **Automatic Dec guide direction**: guide one direction only along the measured Dec drift, switch when the drift reverses, optional intentional bias | Avoids Dec backlash without a manual setting (PHD2 leaves it to the user) | ★★ | S | low |
| T3 | **Direction-specific pulse model**: per-direction rate, stiction threshold and backlash (from the Coach, refined by C1) decide how long each pulse is | PHD2 assumes a symmetric linear mount | ★★ | S | low (only with significant measurements) |

### 3.7 Safety and robustness

| ID | Idea | Why | Impact | Effort | Risk |
|---|---|---|---|---|---|
| S1 | **One "surprise" signal** from the estimator's prediction error, with cause classification: all stars dim → clouds; one sudden jump → bump or wind; corrections at the limit while the error grows → cable snag or stall | Earlier detection and better explanations than separate thresholds | ★★ | M (after L1) | medium |
| S2 | **Guiding-quality gate for the sequencer**: tell NINA a sub was ruined mid-exposure so it can repeat it; write guiding quality into the FITS header | Saves the night's data quality automatically | ★★ | S–M | low |
| S3 | **Pause before the star is lost**: a dimming trend pauses corrections early; resume with a recenter | Fewer lost-star events and wild corrections at cloud edges | ★ | S | low |
| S4 | **Flight recorder**: keep the raw frames around star-lost, runaway and not-responding events; replay them in the UI | Very cheap and very useful for diagnosis | ★★ | S | low |

### 3.8 Dithering and settling

| ID | Idea | Why | Impact | Effort | Risk |
|---|---|---|---|---|---|
| D1 | **Dither when the imaging exposure ends**, not after its download | Settling overlaps the download: 2–5 s saved per sub | ★★ | S | low |
| D2 | **Settle on estimator confidence** ("error below X with confidence") instead of a fixed pixel/time rule | Usually faster, never early | ★ | S (after L1) | low |
| D3 | **Evenly spread dither pattern** (Halton sequence) with drizzle-friendly sub-pixel offsets | Better coverage with fewer dithers than random offsets | ★ | S | low |

### 3.9 Development tools

| ID | Idea | Why | Impact | Effort | Risk |
|---|---|---|---|---|---|
| V1 | **Replay recorded nights**: record frames, pulses and mount state; run new algorithms against real nights offline | Algorithm work based on evidence instead of waiting for clear sky | ★★★ (development) | M | low |
| V2 | **Digital twin**: fit the simulator's parameters (seeing, periodic error, backlash, stiction, asymmetry) to the Coach's measurements; run trial candidates in the twin in seconds and test only the best on the sky | Faster, more reliable tuning; the Coach trials get shorter | ★★ | M | medium (twin fidelity) |

## 4. What stays from PHD2

- Star detection, multi-star tracking and the PHD2 filters as **Classic** mode: proven, bit-identical in the parity
  tests, and the baseline every new mode must beat.
- The PHD2-format guide log (PHD2 Log Viewer keeps working).
- The calibration procedure as the fallback for C1/C2.

## 5. How changes are validated

Nothing replaces Classic behaviour without evidence:

1. **Simulator**: every new mode is tested in the closed-loop simulator with the injected faults (seeing, periodic
   error, polar-alignment drift, backlash, stiction, asymmetry) and must recover them within tolerance.
2. **Replay** (V1): must match or beat Classic on recorded real nights.
3. **On-sky A/B**: Coach trials Classic vs Adaptive with the significance rule (improvement > 2 × the combined
   standard error), on several nights and mounts.
4. **Opt-in first**: Adaptive ships as a selectable mode; it becomes the default only after steps 1–3.

## 6. Roadmap

1. **Quick wins** (days each, low risk): S4 flight recorder, K1 automatic exposure and gain, M3 per-frame
   uncertainty, C3 least-squares calibration, T2 automatic Dec direction, D1 dither at exposure end, D3 Halton
   pattern, T3 direction-specific pulses, M4 automatic hot-pixel map.
2. **The core**: V1 replay first (it is the test bench), then L1 estimator + L2 periodic-error prediction + C1
   continuous calibration as the **Adaptive** mode, validated per §5.
3. **Differentiators**: I1 differential flexure correction, C2 plate-solve calibration, T1 tracking-rate guiding,
   L3 overlapped capture with L4 short exposures, M1 whole-frame registration, S1 surprise signal, S2 quality gate.
4. **Research**: K3 direct camera SDKs, I3 unguided mode, V2 digital twin driving the Coach trials.

The deepest change is **L1 + L2 + C1**: the guider becomes a model of the user's mount that tunes itself, instead of
a filter with knobs. **I1** is the feature no other guider has.

## 7. Open questions

- Which INDI mount drivers accept custom tracking-rate offsets reliably (T1)? Survey EQMod, OnStep, iOptron,
  Celestron, 10Micron.
- Can the Pi 4/5 run phase correlation (M1) at the guide frame rate: downsampling, readout region, SIMD?
- What does the sequencer need to expose for I1/S2 (sub start/end events, star positions per sub, a "repeat this
  sub" hook)? PINS owns NINA, so these can be added to core if needed.
- Plate solving guide frames (C2) for off-axis guiders: field of view too small for ASTAP's default catalogues?
- What evidence is enough before Adaptive becomes the default (§5 step 4): how many nights, which mounts?
