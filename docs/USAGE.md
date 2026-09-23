# PINS Native Guider — user guide

The native guider runs inside PINS (no PHD2 process). It appears in the guider chooser as
**PINS Native Guider** next to PHD2; switching back to PHD2 is just selecting PHD2 again.

## 1. Requirements

* PINS image built with the `pins-guider` plugin and the `IAdvancedGuider` core contract
  (branch `feat/native-guider` of `aduffeck/pins`, see [Deployment](#7-deployment)).
* Guide camera with an **INDI driver** (e.g. `indi_asi_ccd` for ZWO, `indi_qhy_ccd`, `indi_playerone_ccd`,
  `indi_toupcam_ccd`). The main imaging camera may use any driver; the guide camera driver is loaded
  separately and never unloads the main camera's driver.
* Mount connected in PINS with **pulse guiding** support (INDI, ASCOM/Alpaca…), or the guide camera's
  **ST4** port (`PulseOutput = CameraST4`; works without a mount connection, but then pier side and
  declination are unknown — recalibrate after a meridian flip).

## 2. First-time setup (Touch-N-Stars)

1. Connect the **mount** first.
2. Equipment → Guider: choose **PINS Native Guider** and press connect (note: "Connect all" in older
   TNS builds skips the guider unless a PHD2 guide camera was selected — connect it individually).
3. Open the **Guiding** page → Settings (or use the API, see below) and check the *basic* settings:

| Setting | What to set | Default / fallback |
|---|---|---|
| `GuideCameraDriver` | INDI driver of the guide camera | `indi_asi_ccd` |
| `GuideCameraDevice` | e.g. `ZWO CCD ASI120MM-S` (list via the camera picker) | empty = the only camera of the driver that isn't the main camera (error if there are several — then set it) |
| `FocalLengthMm` | guide scope focal length (main scope FL with an OAG) | PHD2 focal length from the profile, else unknown |
| `ExposureSeconds` | 1–4 s typical | PHD2 exposure from the profile, else 2 s |
| `Gain` | camera gain in the driver's native units (−1 = leave as is) | −1 (PHD2's gain is a percentage and is not carried over) |
| `Binning` | 1 (2 for very long focal lengths) | 1 |
| `DecGuideMode` | Auto / North / South / Off | PHD2 setting, else Auto |

Every setting you never changed falls back to the matching **PHD2 setting of the PINS profile**, so an
existing PHD2 setup (algorithms, aggressiveness, min-move, max pulses, Dec mode…) carries over.
Settings marked *requires reconnect* (camera driver/device, guide output) apply after reconnecting.

**Darks**: if `UseDarkLibrary` is on (default), the guider uses its own dark library, or — if none was
built — **PHD2's dark library** (`~/.phd2/darks_defects/PHD2_dark_lib_*.fit`) when its frame size matches.
Build a fresh one from the Guiding page (cover the scope; default 0.5–4 s, 5 frames each).

### Settings without the UI (curl)

```sh
H=http://<pins-host>:1888/v2/api/equipment/guider
curl "$H/get-settings"                                                   # all device properties
curl "$H/set-setting?settingName=FocalLengthMm&newValue=248"
curl "$H/set-setting?settingName=GuideCameraDevice&newValue=ZWO%20CCD%20ASI120MM-S"
curl "$H/set-setting?settingName=NativeSetting&newValue=MinSnr=8"          # any setting: Name=Value
```

## 3. Guiding

* **Start guiding** (TNS button, or the sequencer's *Start Guiding*): selects the best star
  (auto-select avoids hot pixels, saturated, crowded and edge stars), calibrates if there is no valid
  calibration (or when forced), starts guiding and waits until settled (NINA profile settle pixels /
  time / timeout).
* **Calibration** is PHD2's procedure (West/East/backlash/North/South), is **saved** and reused across
  sessions, adjusted for declination, binning and pier side. After a meridian flip the calibration is
  flipped automatically; a **post-flip Dec self-check** watches the first corrections and fixes the
  *Reverse Dec after meridian flip* setting if Dec runs away.
* **Dithering** uses the NINA profile dither settings (pixels, RA only) and PHD2's settle semantics.
* **Multi-star**: up to 9 stars (PHD2 algorithm). If the primary star drops out for a frame, its
  position is estimated from ≥3 agreeing secondaries instead of declaring the star lost.
* **Guide algorithms**: RA Hysteresis (0.7 / 0.1), Dec ResistSwitch (1.0, fast switch), min-move from the
  image scale — PHD2 defaults; Lowpass2 available.

## 4. Safety protocols

| Situation | What the guider does |
|---|---|
| Star lost (clouds, obstruction) | no corrections; searches near the last position, then full frame (only accepting a star near the old position); after 60 s (`ReacquireTimeoutSec`) a critical notification, then it **keeps searching** and resumes by itself when the star returns (`StopOnLostStarTimeout` in the engine stops instead) |
| Corrections make the error grow / excessive pulse duty | **Runaway** → stops guiding, notification |
| Large pulses without star movement | **Mount not responding** → stops, notification |
| Mount slewing / parked / tracking off | pauses corrections, resumes afterwards; after a large move it waits for an explicit resume / restart |
| Guide camera errors | retries 2×, reconnects once, then stops with a notification |
| Calibration looks wrong (non-orthogonal, odd rates, few steps) | warning, guiding continues |
| Dec runs away within 15 min after a meridian flip | Dec direction inverted once (and the *Reverse Dec after meridian flip* setting saved); a second runaway stops guiding |
| Max pulse | hard limit per axis (`MaxRaDurationMs` / `MaxDecDurationMs`) |

If your mount driver misreports *slewing* or *tracking*, turn off `PauseWhenSlewing` /
`PauseWhenTrackingOff`.

## 5. Guiding Coach

The **Coach** tab on the Guiding page measures your rig under tonight's sky, explains what limits your guiding and
recommends settings — and proves them with short guided trials. A full session takes about 15 minutes; pick steps
to make it shorter. Design details: [COACH.md](COACH.md).

1. **Camera check** — loops exposure × gain combinations (guiding stopped) and recommends the combination with the
   lowest centroid noise that doesn't saturate the star.
2. **Sky & mount drift** — 3 minutes with guiding output off: seeing, periodic error, RA/Dec drift, polar alignment
   error, wind gusts, longest useful exposure. Calibrates first if needed.
3. **Mount response** — Dec backlash, smallest pulse that moves the mount, direction asymmetry, guide rate check.
4. **Guided trials** — guides ~2 minutes each with your current settings, the coach's suggestion and a variant
   (and your settings again to detect changing conditions). Your settings are restored afterwards.
5. **Report card** — grade relative to your imaging scale (camera pixel size + telescope focal length from the
   profile), error budget (seeing / centroid noise / mount), ranked actions with *Apply* buttons, and a per-night
   history (per profile).

Guiding that was running when the coach started is resumed with your original settings when it ends — also on
cancel or errors. Starting/stopping guiding, dithering or a slew (e.g. from a running sequence) interrupts the
session. While guiding normally, the guider shows occasional **hints** (e.g. RA oscillating → lower aggression) as a
dismissable chip in the status strip; `LiveHints` in the engine settings is on by default.

## 6. Files

* Guide logs (PHD2 format, open with PHD2 Log Viewer): `~/.local/share/NINA/NativeGuider/Logs/PinsGuider_GuideLog_<night>.txt`
  (one file per night, noon to noon)
* Calibrations: `~/.local/share/NINA/NativeGuider/calibrations.json`
* Dark libraries: `~/.local/share/NINA/NativeGuider/Darks/`
* Guiding Coach reports: `~/.local/share/NINA/NativeGuider/Coach/coach-*.json`
* PINS log (search for `NativeGuider`): `~/.local/share/NINA/Logs/`

In the docker setup `~` is `./data/home` on the host.

## 7. Deployment

Build the image from the `aduffeck/pins` branch `feat/native-guider` with local plugin and web app sources:

```sh
cd pins   # checkout of feat/native-guider with submodules NINA.Plugins/pins-guider and NINA.Plugins/Touch-N-Stars
docker build --network host --build-context local-plugins=NINA.Plugins --build-context tns-frontend=../Touch-N-Stars \
  --build-arg BUILD_PLUGINS="ninaapi touch-n-stars polaralignment joko livestack nightsummary pins-guider" \
  -t aduffeck/pins:native-guider .
```

## 8. Troubleshooting

* *"Guide camera '…' not found"* — the message lists the devices the driver offers; set `GuideCameraDevice`
  to one of them (or clear it for auto-selection).
* *"No suitable guide star"* — increase exposure/gain, check focus, lower `MinSnr`.
* *Calibration failed: RA did not move* — check the guide output (mount driver pulse guiding / ST4 cable),
  guide rate and that the mount tracks.
* Arcsec values look wrong — set `FocalLengthMm`.
* The guide frame is white/says *saturated* — the sky is still too bright or exposure/gain are too high (a 12-bit
  camera like the ASI120 saturates at 65520 ADU); reduce exposure or gain, or wait for darkness.
* Back to PHD2: select PHD2 in the guider chooser; nothing else changes.
