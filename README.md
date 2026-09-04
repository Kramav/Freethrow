# Freethrow

Gesture and Attention Tracking for controlling desktop environments, starting with Windows.

Look at a monitor, reach out, grab a window, and move or throw it to another screen.
Attention is the safety gate: gesture control arms only while you are actually looking
at a display, so the system never acts on incidental hand movement.

The division of labour is what makes this work with an ordinary webcam:

| Signal | Decides | Precision needed |
|---|---|---|
| Gaze / head pose | which monitor | one of N screens |
| Hand position | which window on it | window-sized target |
| Grab gesture | commit / release | binary |

Gaze is never asked for pixel accuracy, because a webcam cannot deliver it.

## Status

**Milestone 1 — hand tracking and gestures.** Camera enumeration (colour and
infrared), pooled-buffer capture, ONNX hand tracking, a grab/release gesture machine
measured in metric 3D and gated on hand posture, per-person threshold calibration, and
a preview window drawing the skeleton live. No window control yet — that is M2.

## Requirements

- Windows 10 1809 or later
- .NET SDK 8.0 or later
- A webcam. Camera access must be enabled for desktop apps:
  Settings > Privacy & security > Camera.

## Setup

```powershell
.\tools\install.ps1
```

This checks the toolchain, downloads the two ONNX models into [models/](models/) with
SHA-256 verification, and builds. See [models/README.md](models/README.md) for their
provenance and tensor layouts.

To build without touching the models:

```powershell
dotnet build Freethrow.sln
```

## Run

The preview demo answers on the console or opens a window, depending on arguments.

```powershell
# List every camera source, infrared included.
dotnet run --project demos\Freethrow.Demo.Preview -- --list

# Stream briefly and report capture health: negotiated format, frame rate,
# dropped frames, latency, and bytes allocated per frame.
dotnet run --project demos\Freethrow.Demo.Preview -- --probe 0 5

# Track a hand live and report what perception costs.
dotnet run --project demos\Freethrow.Demo.Preview -- --track 10

# Fit the grab thresholds to your own hand (recommended once).
dotnet run --project demos\Freethrow.Demo.Preview -- --calibrate-grab

# Save one frame uncompressed, then run the tracker over it.
dotnet run --project demos\Freethrow.Demo.Preview -- --snap hand.ftraw
dotnet run --project demos\Freethrow.Demo.Preview -- --landmarks hand.ftraw

# Open the live preview window with the skeleton drawn over the video.
dotnet run --project demos\Freethrow.Demo.Preview
```

`--probe` is the fastest way to tell whether a machine can run Freethrow at all, and
it needs no GUI. Healthy output looks like this:

```
device  : Integrated Webcam (Color)
format  : 640x480 @ 30fps (NV12)
frames  : 166 delivered, 0 dropped
rate    : 27.7 fps average over the probe
latency : 33.0 ms mean, 43.2 ms worst
```

Rising `dropped` counts come with a `last drop` line explaining the cause.

`--snap` and `--landmarks` exist so the exact pixels the pipeline saw can be replayed.
That is what makes tracking changes testable, and it is how the C# implementation was
checked against OpenCV's Python reference on byte-identical input.

## How tracking works

Two models are used asymmetrically, and the asymmetry is the point. Palm detection
scans the whole frame and is expensive; the landmark model looks at a small crop and is
cheap. Detection therefore runs only to *acquire* a hand, after which each frame's
landmarks predict where to crop the next one and the detector stays off until tracking
is lost. In steady state that is one cheap inference per frame instead of two.

The preview reports `runs N detect / M track`. If those numbers are close, the loop is
thrashing rather than tracking.

## How grabbing works

Grab detection uses **openness** — mean fingertip-to-wrist distance divided by
wrist-to-knuckle distance. Dividing by the hand's own size is what makes one threshold
work at any distance from the camera.

Crucially it is measured on the model's **world landmarks**, which are metric and
hand-relative, not on the projected pixel positions. A projected measurement cannot tell
an open hand pointing at the camera from a fist — they are the same shape in a
photograph — so a hand merely angled away used to register as a grab. Measured in three
dimensions, rotating the hand does not change it; only closing the hand does.

Two further rules make it behave:

- **Arming is gated on posture.** If the hand's palm axis points along the camera's view
  axis, the landmark model is guessing at the fingers, so no new grab may start and the
  skeleton greys out to say so. Once a grab is *held*, orientation is ignored — a wrist
  turns throughout a drag, and dropping the window then would be worse.
- **Debouncing is in seconds and tolerates dropouts.** Contrary frames decay the
  accumulated evidence instead of resetting it, so one bad landmark frame delays a
  transition rather than cancelling it. The windows are durations, not frame counts,
  because the tracking worker drops frames under load.

Thresholds ship with defaults measured from one hand. Hands vary by more than the gap
between the grab and release thresholds, so run `--calibrate-grab` once: it measures
your open, closed and camera-pointing hand and fits the thresholds to the gap between
them, biased toward releasing readily.

## Layout

```
src/Freethrow.Core       Platform-agnostic pipeline. Never references Win32 or WinRT.
  Capture/               Frame contracts, pooled buffers, raw frame files
  Perception/            Hand tracking contracts and the ONNX implementation
  Gestures/              Grab/release state machine
  Filters/               One-euro smoothing
  Imaging/               Resampling frames into model tensors
  Config/                Per-person calibration profiles
tests/Freethrow.Core.Tests
                         Gesture machine driven by synthetic poses, no camera needed
src/Freethrow.Desktop    Windows integration: capture, and later windows and overlay.
demos/                   Runnable demonstrations of each capability.
models/                  ONNX models, downloaded rather than committed.
tools/                   Setup scripts.
```

`Freethrow.Core` stays platform-clean on purpose: everything platform-specific enters
through an interface, which is what lets the pipeline be tested against recorded
fixtures with no camera and no desktop attached.

The platform layer is called `Desktop` rather than `Windows` because a namespace
ending in `.Windows` shadows WinRT's own `Windows` root and breaks `Windows.Media.Capture`.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE).
