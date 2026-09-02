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

**Milestone 0 — capture.** Camera enumeration (colour and infrared), WinRT frame
capture into pooled buffers, and a preview demo with live telemetry. No tracking,
gestures, or window control yet.

## Requirements

- Windows 10 1809 or later
- .NET SDK 8.0 or later
- A webcam. Camera access must be enabled for desktop apps:
  Settings > Privacy & security > Camera.

## Build

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

# Open the live preview window.
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

## Layout

```
src/Freethrow.Core       Platform-agnostic pipeline. Never references Win32 or WinRT.
src/Freethrow.Desktop    Windows integration: capture, and later windows and overlay.
demos/                   Runnable demonstrations of each capability.
```

`Freethrow.Core` stays platform-clean on purpose: everything platform-specific enters
through an interface, which is what lets the pipeline be tested against recorded
fixtures with no camera and no desktop attached.

The platform layer is called `Desktop` rather than `Windows` because a namespace
ending in `.Windows` shadows WinRT's own `Windows` root and breaks `Windows.Media.Capture`.

## Licence

AGPL-3.0-or-later. See [LICENSE](LICENSE).
