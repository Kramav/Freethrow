# Freethrow — working context

Gesture and attention tracking for controlling Windows desktops. Look at a monitor, reach
out, grab a window, move or throw it to another screen.

The full design record, including rationale and milestones, is in [docs/PLAN.md](docs/PLAN.md).
Read it before proposing architectural changes — most obvious alternatives were considered
and rejected there for stated reasons.

## The load-bearing idea

| Signal | Decides | Precision needed |
|---|---|---|
| Gaze / head pose | which monitor | one of N screens |
| Hand position | which window on it | window-sized target |
| Grab gesture | commit / release | binary |

Gaze is never asked for pixel accuracy, because a webcam cannot deliver it. Anything that
starts depending on precise gaze is going the wrong way.

## First run on a new machine

Nothing machine-specific is in git. Expect to do all of this:

```powershell
.\tools\install.ps1     # checks the SDK, downloads + hash-verifies models, builds
```

- **.NET SDK 8+.** Projects target `net8.0` / `net8.0-windows10.0.19041.0`.
- **Models are not committed** (`models/` is gitignored, ~8 MB). The install script fetches
  them from Hugging Face with SHA-256 verification. `ModelPaths` searches `FREETHROW_MODELS`,
  then `%LOCALAPPDATA%\Freethrow\models`, then walks up from the binary looking for `models/`.
- **Camera permission** must be on for desktop apps: Settings → Privacy & security → Camera.
  `CameraAccessDeniedException` says exactly this; it is usually the toggle, not a bug.
- **Calibration profiles live in `%LOCALAPPDATA%\Freethrow\`**, not the repo, so a new machine
  has none and falls back to built-in defaults. Those defaults were fitted from *one* hand and
  are known to be off by roughly the width of the entire grab/release band for other people.
  Re-run `--calibrate-grab` on any new machine before judging how the gestures feel.

Verify capture works before anything else:

```powershell
dotnet run --project demos\Freethrow.Demo.Preview -- --probe
```

## Commands

```powershell
dotnet build Freethrow.sln
dotnet test tests\Freethrow.Core.Tests\Freethrow.Core.Tests.csproj

dotnet run --project demos\Freethrow.Demo.Preview                              # preview window
dotnet run --project demos\Freethrow.Demo.Preview -- --list                    # cameras, IR included
dotnet run --project demos\Freethrow.Demo.Preview -- --formats [i]             # every format a camera offers
dotnet run --project demos\Freethrow.Demo.Preview -- --probe [i] [secs] [sub]  # capture health; [sub] forces a subtype
dotnet run --project demos\Freethrow.Demo.Preview -- --track [secs]            # tracking + arbitration
dotnet run --project demos\Freethrow.Demo.Preview -- --monitors                # displays + mappings
dotnet run --project demos\Freethrow.Demo.Preview -- --overlay [secs]          # overlay placement
dotnet run --project demos\Freethrow.Demo.Preview -- --calibrate-grab          # calibration wizard; add --sideways for left-right only
dotnet run --project demos\Freethrow.Demo.Preview -- --snap f.ftraw            # save one raw frame
dotnet run --project demos\Freethrow.Demo.Preview -- --landmarks f.ftraw       # track a saved frame
```

## Layout

```
src/Freethrow.Core/      Platform-agnostic. NEVER references Win32 or WinRT.
  Capture/     FrameRef (pooled), ICameraSource, RawFrameFile
  Perception/  IHandTracker, HandMetrics, HandTrackingWorker, Onnx/
  Gestures/    GestureRecognizer (per hand), HandArbiter
  Spatial/     MappingFit, ScreenMapping, ScreenPoint, Homography,
               HandSpace (pixels -> metres), FrameFit, IdleZone
  Filters/     OneEuroFilter
  Config/      GestureProfile, SpatialProfile
src/Freethrow.Desktop/   All Win32/WinRT. Capture/, Desktop/, Overlay/
demos/Freethrow.Demo.Preview/   Every capability, runnable
tests/Freethrow.Core.Tests/     61 tests, no camera or desktop needed
tools/reference-check/          Regression check against OpenCV's implementation
```

**The boundary rule is real.** `Freethrow.Core` stays platform-clean so the pipeline is
testable headless. The platform project is named `Desktop`, not `Windows`, because a
namespace ending in `.Windows` shadows WinRT's own `Windows` root and breaks
`Windows.Media.Capture`.

## Status

M0–M1.7 complete. Capture, hand tracking, gestures, and both halves of calibration work.
**M2 is next: the window layer** — nothing moves a window yet.

M2 needs `WindowManager` and a `WindowCache`; `MonitorTopology`, the click-through overlay,
and the hand→screen mapping already exist (pulled forward during M1.6).

**The mapping can be side-to-side only** (`MappingKind.HorizontalOnly`), for a camera that
cannot see vertical reach. Its `ScreenPoint.Y` is null, never a stand-in. M2 must hit-test a
vertical band when `Y` is null, in one named place — see PLAN.md §4 before writing hover.

## Gotchas that cost real debugging time

- **`ArrayPool<byte>.Shared` silently refuses arrays over 1 MB.** A 640×480 BGRA frame is
  1.2 MB, so "pooled" frames were allocating fresh every time. Frames use
  `FrameRef.DefaultPool`. Never switch them back to `Shared`.
- **`InvariantGlobalization=true` kills every WPF window at startup** — font fallback
  constructs `CultureInfo("en")`. It is explicitly `false` in `Directory.Build.props`.
- **WPF projects drop `System.IO` from implicit usings.** `Path` and `FileNotFoundException`
  need an explicit `using System.IO;`.
- **Under CsWinRT, the classic `[ComImport]` cast for `IMemoryBufferByteAccess` throws.**
  Projected WinRT objects are not classic RCWs. `MemoryBufferAccess` does an explicit
  QueryInterface and vtable call instead.
- **Gesture measurements must use `HandPose.WorldLandmarks`, never the projected ones.**
  An open hand pointing at the camera and a fist are the same shape in a photograph. A real
  measured case: projected openness 2.675 against a true 1.673.
- **The landmark model's output *order* matters.** Two outputs carry 63 values and two carry
  1, so shape cannot identify them. `HandLandmarkDetector` validates and fails loudly.
- **The palm detector's score does not predict landmark quality.** Measured: the top-scoring
  palm (0.871) gave landmark confidence 0.686 while the runner-up (0.829) gave 0.980. The
  tracker evaluates several candidates and judges by landmark confidence.
- **Handedness is unreliable.** The model labels *both* hands in MediaPipe's own two-hand
  sample as "Right". It is displayed but nothing depends on it. Do not arbitrate with it.
- **Overlays: position in physical pixels, lay out in DIPs.** `MonitorInfo` reports physical
  pixels; WPF lays out in device-independent units. `CalibrationTargetOverlay` sets both.
- **PowerShell is DPI-unaware**, so `SystemInformation.VirtualScreen` under-reports on a
  scaled display and screenshots capture only part of the screen. Use explicit physical
  bounds when verifying overlay placement, or you will "find" bugs that are not there.
- **A laptop lid is not a camera mount.** The spatial mapping is fitted to one camera pose,
  and tilting the lid moves the camera, so a full-screen profile goes stale every time the
  screen angle changes. A lid camera is also aimed at the face, so reaching for a bottom
  corner can leave its view entirely and that wizard step can never finish. Scout the
  visible area in the preview before calibrating, and use `--sideways` if the bottom
  corners are out of reach.

## Conventions

- **Comments explain why, and name the failure mode being prevented.** The codebase is dense
  with them because most of these decisions look arbitrary until you know what broke.
- **Verify, do not assert.** Every performance and accuracy claim here came from a
  measurement. Run the thing and read the number before saying it works.
- **Tests are synthetic and headless.** `SyntheticHand` builds poses with a chosen openness,
  view alignment, depth and offset, so gestures, arbitration and geometry are all testable
  with no camera. Add to these rather than reaching for the webcam.
- **Instrument rather than guess.** When something is wrong, add the diagnostic that names
  the cause — `LastDropReason`, the `detect / track` ratio, the `--overlay` command — instead
  of reasoning from symptoms.
- **Do not run destructive build operations while the user may be building.** Wiping
  `bin`/`obj` during someone's `dotnet run` breaks their build and looks like a code fault.

## Known unverified

Be honest about these rather than assuming they work. The first five need a person and a
webcam; [docs/hardware-checks.md](docs/hardware-checks.md) is the runbook, with pass criteria.
Do the first three before M2.

- **The two-hand path has never been tested with two real hands.** It is correct on a static
  two-hand image (`--landmarks` reports `hands: 2`) and the arbitration logic is covered by
  ten tests, but duplicate suppression — two ROIs converging on one hand — has never met
  live input.
- **No spatial profile has ever been produced.** The corner-calibration path runs but has not
  been completed end to end, so the homography has never been fitted from real captures.
- **`MaxViewAxisAlignment` fell back to its default** in the one real profile produced so far,
  meaning the pointing-at-camera phase did not separate cleanly. The posture gate is running
  on a guess, not a measurement.
- **The side-to-side thresholds are derived, not measured.** `MappingFit` treats an axis as
  unsteerable under 10 cm of hand travel, or when the vertical is more than twice as
  sensitive as the horizontal. Both come from "one window per centimetre of tremor", not
  from data. The first check is the held-still band in `Test the mapping`.
- **"Side to side survives lid tilt" is reasoning, not a measurement.** Tilting rotates the
  camera about a horizontal axis, which should move the image vertically and barely
  sideways. It is the strongest argument for the mode on a laptop and has never been tried.
- **Infrared capture is weak on the reference machine**: ~4.8 fps in a burst, decaying to
  ~0.6, because the Hello illuminator idles outside authentication. May differ on other
  hardware; it is opt-in and nothing depends on it.

## Reference measurements

Two machines, and they are **not** interchangeable. Re-measure on a third rather than
assuming either column applies to it.

- **Laptop** — Intel Iris Xe, 1920×1200 @ 120 DPI, integrated webcam. Every number here
  was originally fitted on this machine. Capture rows re-measured 2026-09-18 (Release,
  4 s probe); the two model rows have not been re-measured since.
- **Adesso** — the second machine. An Adesso CyberTrack K4 is its *only* camera, so its
  rate is a floor, not a setting.

| | Laptop | Adesso CyberTrack K4 |
|---|---|---|
| Capture | 640×480 NV12, ~27 fps, 0 dropped | 640×480 NV12, **15.9 fps** |
| Capture latency | 27.5 ms mean (sensor to handler) | not measured |
| Palm detection | ~10 ms per run | not measured |
| Tracking loop | 5.7–7.0 ms mean per frame | not measured |
| Allocation | ~25 KB/frame (WinRT projection churn; frame buffers are pooled) | not measured |

Capture latency alone consumes nearly half the 60 ms end-to-end budget on the laptop, which
is the main constraint on everything downstream.

**On the Adesso that budget is gone at the sensor.** 15.9 fps is 63 ms between frames, so
the capture interval alone exceeds the whole 60 ms hand-motion-to-window-motion target
before tracking, gesture or window work runs at all. No pipeline optimization reaches it —
nothing can react to motion the camera has not sampled. That machine needs its own target.
Flick detection (M4) is where it will hurt most: a flick is measured over a handful of
frames, and there are half as many.

Its other formats were measured too, because "offers 30 formats" is not an answer when a
camera runs at half its nominal rate. NV12, YUY2 and MJPG all advertise 25 fps at
640×480; NV12 delivered 15.9, YUY2 14.1, and **MJPG failed to start outright** with
`OutputFormatNotSupported`. `--formats` lists what a device offers and
`--probe [i] [secs] [sub]` forces one, so this is re-checkable anywhere.

**Allocation was ~18 KB/frame when first recorded and measures ~25 KB today**, identically
in Debug and Release. Nobody has found the extra 7 KB. It is well inside budget, so it is
noted rather than chased — but it is drift, not noise.
