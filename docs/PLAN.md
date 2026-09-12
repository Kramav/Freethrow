# Freethrow — v1 Implementation Plan

## Context

`Freethrow` is currently an empty repository: a one-line README ("Gesture and Attention Tracking for controlling desktop environments, starting with Windows") and an AGPL-3.0 license. Everything below is greenfield.

The problem being solved: moving windows between monitors is a high-frequency, low-value chore that requires grabbing a mouse and dragging across a large virtual desktop. Freethrow replaces it with a physical gesture — look at a screen, reach out, grab the window, and move or throw it — while using attention as the safety gate that keeps the system from acting on incidental hand movement.

**v1 goal, end to end:** while the user is looking at a monitor, gesture control arms; a visible hand hovers to highlight a candidate window; a grab commits it; hand motion drags it; a flick throws it to the monitor being looked at.

**Design division of labor** (this is the load-bearing idea):

| Signal | Decides | Precision needed | Precision available |
|---|---|---|---|
| Gaze / head pose | *Which monitor* | ~1 of N screens | Webcam-realistic ✓ |
| Hand position | *Which window on it* | Window-sized target | Easily ✓ |
| Grab gesture | *Commit / release* | Binary | ✓ |

Gaze is never asked for pixel accuracy, because a webcam cannot deliver it. This is what makes the system honest rather than a demo that falls apart in use.

### Confirmed decisions

- **Stack:** C# / .NET (chosen for Win32 + WinRT access; native interop where warranted).
- **Form:** library + demos. `Freethrow.Core` carries no UI opinion; demos exercise each capability.
- **Selection:** hover highlights the candidate window, grab confirms and moves it.
- **Motion:** relative drag with gain + throw-to-send; **plus** a "grab and look to place" mode as a selectable alternative.
- **Perception:** whatever gives the best UX per unit of memory and compute — resolved below in favor of ONNX Runtime with adaptive scheduling.
- **Cameras:** single camera calibrated per setup; extreme head yaw handled explicitly; IR opt-in; one-camera-per-monitor noted as future work, not built.
- **Install:** a script must be able to do the whole long-term installation.

---

## Architecture

```
Freethrow.sln                          .NET 10 (LTS; fall back to 8 LTS if unavailable)
│
├─ src/
│  ├─ Freethrow.Core/                  Pure pipeline. No Win32. Testable headless.
│  │   Capture/       ICameraSource, FrameRef (pooled buffers)
│  │   Perception/    IHandTracker, IAttentionTracker, OnnxHandTracker, OnnxFaceTracker
│  │   Gestures/      GestureStateMachine, OneEuroFilter, VelocityTracker
│  │   Attention/     AttentionClassifier, CalibrationProfile, Hysteresis
│  │   Pipeline/      FrameScheduler  ← the power-state machine
│  │   Events/        HoverChanged, GrabStarted, DragUpdated, Released, Thrown
│  │
│  ├─ Freethrow.Windows/               All platform interop lives here.
│  │   Capture/       MediaFrameCameraSource (WinRT; RGB + IR), DirectShowFallback
│  │   Desktop/       WindowManager, WindowCache, MonitorTopology, DpiHelper
│  │   Overlay/       HighlightOverlay (WPF layered, click-through, per-monitor)
│  │   Input/         GlobalHotkey (arm/disarm, undo)
│  │
│  └─ Freethrow.Calibration/           Wizard logic + profile persistence
│
├─ demos/
│  ├─ Freethrow.Demo.Preview/          Landmarks, gesture state, attention, FPS, latency, RAM
│  ├─ Freethrow.Demo.Calibrate/        First-run wizard
│  └─ Freethrow.Demo.WindowGrab/       The v1 experience
│
├─ tests/Freethrow.Core.Tests/         FSM, classifier, throw physics — on recorded fixtures
├─ tools/install.ps1                   SDK check → build → model fetch (SHA-256) → calibrate
└─ models/                             NOT committed; fetched by install script
```

**Boundary rule:** `Freethrow.Core` never references `user32`/`WinRT`. Everything platform-specific enters through an interface. This is what keeps the library reusable and the pipeline unit-testable without a camera or a desktop.

---

## Key technical decisions

### 1. Perception: ONNX Runtime, managed C#

`Microsoft.ML.OnnxRuntime` running MediaPipe-derived models (Apache-2.0, ONNX-convertible):

| Model | Purpose | Input | Cadence |
|---|---|---|---|
| BlazePalm | Find hand when none tracked | 192² | Only on re-acquire |
| Hand landmark (21 pt) | Track hand, ROI-cropped from prior frame | 224² | 30 Hz while engaged |
| BlazeFace + 6-pt PnP | Head pose → attention | 128² | 5–10 Hz |
| Iris (opt-in) | Gaze refinement beyond head pose | 64² | Off by default |

Rejected: a MediaPipe C++ DLL via P/Invoke. Better tracking out of the box, but a Bazel toolchain and a hand-built binary to ship and update forever — a poor trade against the "runnable through a script" requirement. The `IHandTracker` / `IAttentionTracker` interfaces are shaped so that backend can be added later without touching gesture or window code.

Execution provider: **CPU by default, DirectML optional.** DirectML over CUDA deliberately — it works on Intel, AMD, and NVIDIA alike, which is the portability requirement. Selected at runtime with fallback to CPU.

### 2. Adaptive scheduling — this is the efficiency answer

Running every model every frame is what makes gesture systems into space heaters. Three power states:

| State | What runs | Rate | Entered when |
|---|---|---|---|
| **Idle** | Frame differencing on 160×120 gray | 10 Hz | No motion for 5 s |
| **Attentive** | + head pose, palm detection | 10 / 5 Hz | Motion detected |
| **Engaged** | + hand landmark (head pose drops to 5 Hz — attention is already latched) | 30 Hz | Hand present |

Idle costs approximately nothing, so the library is tolerable as an always-on background service. Supporting measures: INT8 quantization where accuracy holds, `ArrayPool<byte>` frame buffers with zero per-frame allocation, pinned reusable `OrtValue` input tensors, capped `IntraOpNumThreads`, 640×480 capture.

### 3. Attention model and calibration

Features per frame: head yaw/pitch, iris offset relative to eye corners, face bbox position and scale. Calibration walks the user through looking at points on each monitor (and, per your suggestion, grabbing and hovering at those points — which simultaneously fits the hand→screen mapping). Classifier: ridge regression / k-NN over features → per-monitor score.

Three defenses against the failure mode that kills these systems (attention flickering between monitors):

1. **Hysteresis + dwell** — the winning monitor must beat the runner-up by a margin for ≥150 ms.
2. **Confidence gating** — at extreme yaw (50–70°, the lid-camera-to-side-monitor case), landmark confidence collapses. On low confidence, *hold the last stable state*; never flip on a guess.
3. **An explicit `Unknown` state** that disarms grabbing rather than picking a monitor. Refusing to act beats acting wrongly.

Calibration also detects layouts that cannot be tracked from the current camera position and says so, instead of silently underperforming.

### 4. Hand → screen mapping, and the clutch

Everything in this section exists to defeat **gorilla arm**: holding an unsupported arm in mid-air fatigues the deltoid within 30–60 seconds, after which the hand sags and precision collapses. It is the main reason mid-air gesture systems get abandoned after the demo, and it is why absolute hand-to-screen mapping was rejected — reaching a far corner of a multi-monitor desktop would mean extending the arm and *holding it there* while positioning.

- **Hover (absolute):** hand position in frame → attended monitor's bounds, via the calibration homography. Window-level precision only.
- **Grab (clutch switch):** mapping flips to **relative × gain**, so a ~15 cm hand motion crosses a large desktop and the hand stays in a small zone near the torso. Release / recenter / re-grab continues a move, exactly like lifting and repositioning a mouse.
- **Neutral-zone calibration:** fit the hover mapping around wherever the user's hand *naturally rests* — captured during calibration — rather than centering it on the camera's field of view. The comfortable posture must be the default one, not one the user has to hunt for.
- **Velocity-dependent gain:** treat gain as a tuned curve, not the constant 1:3 starting guess — low gain when moving slowly (fine placement), higher when moving fast (traversal), mirroring mouse acceleration. Expose it in config; the curve wants real tuning against `Demo.Preview`.
- **Smoothing:** One-Euro filter on both — low lag at speed, no jitter at rest. A plain low-pass will feel laggy; do not substitute one.

Note that `LookToPlace` (§7) is the strongest available answer to gorilla arm: grab, glance at the destination, release — the arm barely moves. Worth evaluating as the default if the drag path proves tiring in practice.

### 5. Window layer — the Win32 details that matter

- **Enumerate:** `EnumWindows` filtered to visible, titled, alt-tab-eligible, **not DWM-cloaked** (`DWMWA_CLOAKED` — this is what excludes ghost UWP windows).
- **Bounds:** `DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`, *not* `GetWindowRect`, which includes invisible drop-shadow margins and makes highlights look misaligned.
- **Cache, don't poll:** maintain the window list via `SetWinEventHook` (foreground / location / create / destroy) instead of enumerating per frame. Hit-test the hover point against the cache; commit a highlight only after ~200 ms dwell so it doesn't strobe across overlapping windows.
- **Move:** `SetWindowPos` with `SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE`. Restore maximized windows first via `GetWindowPlacement` / `SW_RESTORE`, preserving restore bounds.
- **DPI:** per-monitor-v2 awareness in the manifest; scale window size proportionally when crossing monitors with different scale factors.
- **UIPI:** a non-elevated process cannot move an elevated window. Detect this and surface a clear message rather than failing silently.

### 6. Overlay

WPF window per monitor: `WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE`, topmost, never focus-stealing, excluded from its own hit-testing. Draws the candidate highlight, grab state, and throw destination.

### 7. Release semantics — two modes

- **`ThrowToSend` (default):** velocity over the trailing ~100 ms; if speed exceeds threshold *and* direction resolves toward the attended monitor, animate the window there. Otherwise it stays where it was dragged.
- **`LookToPlace`:** no velocity heuristic — release places the window on the attended monitor, snapped to a region.

Selectable via config enum; both share the drag path.

### 8. Safety

Global arm/disarm hotkey; auto-disarm on tracking loss; **never** move a window while attention is `Unknown`; abort-and-restore if the gesture is lost mid-drag for >N ms; a global undo hotkey restoring the last moved window's prior bounds.

### 9. Gesture robustness — why grabs misfire and stick

Two defects found in use once M1 was running. Both trace to one root cause: **the gesture machine reasons about a 2D projection of a 3D hand.**

**Defect 1 — a hand facing away registers a grab.** `Openness` is
`mean‖tip − wrist‖ / ‖middleMcp − wrist‖` computed with `Vector2.Distance`
([HandMetrics.cs:67](src/Freethrow.Core/Perception/HandMetrics.cs#L67)), discarding Z. Rotate the hand out of the image plane and the finger segments foreshorten faster than the palm segment, because the fingers rotate about the MCP joints on top of the whole-hand rotation. Openness falls below the grab threshold and a grab fires. An open hand pointing at the camera and a fist are *the same shape in projection* — this is information loss in the measurement, not a threshold that needs tuning.

**Defect 2 — grabs stick well past a release.** Four compounding causes:

- **The hysteresis dead band traps the state.** Grab below 1.55, release above 1.75; a near-open hand landing in [1.55, 1.75] satisfies neither condition and stays in `Grab` indefinitely. *Primary cause.*
- **The release counter resets on one frame.** [GestureRecognizer.cs:156](src/Freethrow.Core/Gestures/GestureRecognizer.cs#L156) zeroes `_openFrames` on any sub-threshold frame, so release needs two *consecutive* clean frames — while a fist clears its own threshold with margin to spare.
- **Openness is never smoothed**, though position is, so the signal driving transitions is the raw one.
- **Debounce counts frames, not time**, and the worker drops frames under load, so "2 frames" is a variable wall-clock duration.

**The fix, in order:**

1. **Surface the world landmarks.** The model already emits them as output 3; `HandLandmarkDetector` validates that output exists and never reads it. They are metric 3D coordinates in a hand-relative frame — rotation-invariant by construction. De-rotate them by the crop rotation exactly as the screen landmarks are, so their axes align with the image, and add `WorldLandmarks` to `HandPose`.

2. **Compute openness in 3D** from those landmarks. Foreshortening can no longer fake a closed hand. Note this changes the metric's numeric range outright, so **every existing threshold becomes meaningless** and must be re-derived — step 6.

3. **Gate arming on posture, not on palm direction.** The ambiguity comes from the hand's long axis pointing along the view direction, so measure exactly that: `a = normalise(worldMiddleMcp − worldWrist)`, and refuse to arm when `|a · viewAxis|` exceeds roughly cos 60°. This deliberately uses the *palm* axis rather than a finger direction, because the palm axis is stable whether the hand is open or closed — a finger direction degenerates in precisely the state being judged. Palm-toward-camera and palm-down both pass; pointing at or away from the camera does not.

4. **Gate arming only.** Once a grab is held, orientation is ignored: a wrist turns naturally during a drag, and dropping the window then would be a worse bug than the one being fixed.

5. **Smooth openness, and make debouncing dropout-tolerant.** Run openness through the existing [OneEuroFilter](src/Freethrow.Core/Filters/OneEuroFilter.cs). Replace the frame counters with time accumulators that *decay* on a contrary frame rather than resetting to zero, and express the windows in seconds (`GrabConfirmSeconds` / `ReleaseConfirmSeconds`) so they do not stretch when frames are dropped.

6. **Set defaults by measurement, then calibrate per person.** Capture open, closed and half-open frames with `--snap`, run the new metric over them, and place thresholds in the measured gap **biased toward release** — a window dropped early is re-grabbed in a second, while one welded to the hand feels broken. Then add `--calibrate-grab`, which prompts through the sequence, prints the distributions, and writes a profile.

**Check before building on it:** confirm the world landmarks are non-degenerate and correctly de-rotated. A sign error here produces a plausible-looking hand with subtly wrong geometry, so re-run the Python reference comparison first — the same check that caught the palm-selection defect.

**Files:** `HandLandmarkDetector.cs` (read output 3, de-rotate), `HandPose.cs` (carry `WorldLandmarks`), `HandMetrics.cs` (3D openness, palm axis; keep `PalmCenter` in *frame pixels* — position comes from screen landmarks, shape and orientation from world ones), `GestureRecognizer.cs` (openness filter, decaying time-based debounce, arming gate), `MainWindow.xaml.cs` + `SkeletonOverlay.cs` (show the gate state and palm angle), `Program.cs` (`--calibrate-grab`), plus a new `tests/Freethrow.Core.Tests`.

### 10. Spatial calibration — mapping hand space to screen space

The threshold calibration answers *is the hand closed*. Nothing yet answers *where is it pointing*, so the hover mapping in §4 has no basis. Four corners per monitor, confirmed by a real gesture, give it one.

**Work in a metric hand space, not in pixels.** Palm centre arrives in frame pixels, which change with distance from the camera — and reaching for a corner extends the arm, so the four corners are not even at a constant depth. Both halves of the fix already exist: `HandMetrics.Scale` (pixels) and `HandMetrics.WorldScale` (metres) give pixels-per-metre, and dividing the pixel offset from frame centre by it yields a lateral position in metres that is independent of depth:

```
metric = (palmCentrePx − frameCentre) ÷ (Scale ÷ WorldScale)
```

Without this, leaning in or out after calibration silently rescales the whole mapping. `WorldScale` is a model estimate and noisy, so smooth it with the existing `OneEuroFilter` before dividing.

**Captured per monitor:** the neutral rest position, and four comfortable-reach corners. **Captured once, globally:** the maximum-reach envelope.

**That split is a deliberate reduction, and the one place this deviates from the brief.** Four corners × two envelopes × N monitors is punishing — 2 monitors would be 18 captures. But the two envelopes are not used the same way: the comfortable one *is* the homography and needs four precise correspondences, while the maximum one only supplies headroom bounds so movement past the screen edge keeps tracking instead of clamping. Bounds need only a bounding box, so the maximum envelope is a single free sweep — and since it is a property of your arm rather than of a monitor, it is captured once. Additional monitors are calibrated on demand rather than in the first run.

**The fit.** Four correspondences solve a homography exactly (DLT, an 8×8 linear system). A homography rather than a scale-and-offset because the camera is not square-on to the plane your hand sweeps, so the mapping has genuine keystone in it. Guard against a degenerate quadrilateral — non-convex, or too small in area — and fall back to an axis-aligned fit from the bounding box, saying so, rather than emitting a wild transform.

**Keying monitors.** Store against the device name from `MONITORINFOEX` (`\\.\DISPLAY1`), not an index or a bounds rectangle — both change when a monitor is unplugged or moved. A monitor with no calibration falls back to the nearest calibrated one *and says so*; silently guessing a mapping is how a system loses trust.

**Show the target on the actual screen.** A borderless, click-through, topmost overlay on the monitor being calibrated draws the corner being asked for. Describing "the top-left of monitor 2" inside a window sitting on monitor 1 is a poor substitute for putting a mark where the hand should point.

**Corner confirmation — three modes, selectable in the wizard** (deferred to you):

| Mode | What it proves | Cost |
|---|---|---|
| **Grab and hold** *(suggested default)* | Position, depth, orientation **and** that a grab can arm at the extremes — where the wrist is most rotated and `MaxViewAxisAlignment` is most likely to block | Most tiring; the gesture itself can shift the hand slightly |
| **Hover dwell** | Position and depth, with a stability gate | Never tests whether a grab works at that corner |
| **Key press** | Position exactly, undisturbed by any gesture | Needs the other hand; tests none of the gesture path |

**This pulls M2 groundwork forward:** per-monitor calibration needs monitor enumeration, and on-screen targets need the overlay. Both were M2 items and get built here instead.

**Files:** `src/Freethrow.Core/Spatial/Homography.cs` (solve, apply, degeneracy check), `src/Freethrow.Core/Spatial/HandSpace.cs` (pixel→metric), `src/Freethrow.Core/Config/SpatialProfile.cs` (per-monitor maps keyed by device name), `src/Freethrow.Desktop/Desktop/MonitorTopology.cs` (`EnumDisplayMonitors`, `GetMonitorInfo`, `GetDpiForMonitor`), `src/Freethrow.Desktop/Overlay/CalibrationTargetOverlay.cs`, and extra steps plus a confirmation-mode selector in `demos/Freethrow.Demo.Preview/CalibrationWindow.xaml(.cs)`.

### 11. Two hands — why a grab gets missed

Raising the right hand to grab does nothing if the left was detected first. The cause is a single field: [`OnnxHandTracker`](src/Freethrow.Core/Perception/Onnx/OnnxHandTracker.cs) keeps one `RotatedCrop`, and `Acquire` picks the candidate with the best *landmark confidence* and locks onto it. From then on the other hand is invisible until the tracked one leaves frame. **Detection order decides control, and it should not.**

Most of the work is already done and thrown away. `PalmDetector.Detect` already returns a list, and `Acquire` already runs the landmark model on *both* candidates before discarding the loser — so acquisition already pays for two hands. Only steady-state tracking adds a second inference.

**Track a list, not a field.** Each track carries its own ROI, predicted from its own previous landmarks, so association across frames is implicit. Two failure modes must be handled explicitly:

- **Duplicate lock.** Two ROIs can converge on the same hand, giving a phantom second hand that shadows the first. Reject a track whose palm centre sits within a fraction of a hand-width of another's.
- **Detector cost.** With a free slot, the naive loop re-runs palm detection every frame hunting for a second hand — discarding the entire saving the acquire-then-track loop exists for. So: **every frame when nothing is tracked, ~3 Hz when a slot is free, never when full.** Detection measures about 10 ms, so a third-of-a-second latency on a second hand appearing costs only a few percent.

**Arbitration — first to grab wins.** Both hands are tracked and drawn; neither controls anything until one closes. That hand owns the interaction until it opens, and a grab from the other is ignored meanwhile. Detection order stops mattering entirely; only intent does. This is pure logic over gesture states and belongs in `Freethrow.Core/Gestures`, driven by synthetic sequences in tests.

**Hover follows the hand nearest the monitor.** `HandSpace.PixelsPerMetre` — palm pixels divided by palm metres — is already computed for the depth-invariant mapping, and is inversely proportional to distance from the camera, which sits at the monitor. The hand you reach out with is the hand you mean. Comparing the *same person's* two hands is the favourable case: their palms are near-identical in size, so the systematic error in the world-scale estimate largely cancels.

Two safeguards, because that estimate is noisy: smooth it per hand with the existing `OneEuroFilter`, and require one hand to be nearer by a clear margin before the hover switches. Without the margin, two hands at similar depth would trade the highlight back and forth — the same chatter the grab thresholds and the window dwell timer already exist to prevent. When neither hand is clearly nearer, keep whichever currently has hover; if none does, take the more confident.

**Shape of the change.** `IHandTracker.Track` returns a list rather than one pose; `HandTrackingResult` carries the tracked hands plus the controlling and hovering ids; the worker holds one `GestureRecognizer` per track id, so each hand keeps its own debounce state. Track ids must stay stable while a hand is followed, or that state resets every frame.

**On by default**, with `MaxHands` in `HandTrackerOptions` to drop back to one on a slow machine.

**Verify handedness against the mirror rather than asserting it.** The preview is mirrored, so the model's "left" and the user's left need checking empirically before either is shown in the UI.

**Files:** `OnnxHandTracker.cs` (track list, duplicate suppression, rescan throttle), `IHandTracker.cs` (list-returning contract), `HandTrackingWorker.cs` (per-hand recognizers, multi-hand result), a new `Freethrow.Core/Gestures/HandArbiter.cs`, `SkeletonOverlay.cs` and `MainWindow.xaml.cs` (draw both hands, distinguish controlling from hovering from ignored), and `tests/Freethrow.Core.Tests`.

---

## Milestones

| # | Deliverable | Proves |
|---|---|---|
| **M0** ✅ | Solution skeleton, `ICameraSource`, WinRT capture, `Demo.Preview` showing a live frame + enumerated RGB/IR devices | Camera works on an arbitrary machine |
| **M1** ✅ | ONNX hand tracking, gesture FSM, One-Euro smoothing, landmarks + state in preview | Grab/release is reliable before anything moves |
| **M1.5** ✅ | World landmarks, 3D openness, posture gate on arming, time-based decaying debounce, measured defaults, visual `--calibrate-grab` | Grabs fire only when meant, and release when meant |
| **M1.6** ✅ | Metric hand space, 4-corner homography per monitor, `MonitorTopology`, on-screen target overlay, live mapping test | Hand position means something on screen |
| **M1.7** ← | Multi-hand tracking with duplicate suppression and a throttled rescan, grab-first-wins arbitration, hover on the nearest hand | The hand you raise is the hand that acts |
| **M2** | `WindowManager`, `WindowCache`, overlay, hover-highlight + grab-drag on a **single** monitor, first pass at the gain curve | The core interaction feels right |
| **M3** | Head pose, attention classifier, calibration wizard (per-monitor gaze **+ neutral rest zone**), monitor gating | Attention actually gates control |
| **M4** | Throw physics, look-to-place, multi-monitor + DPI correctness | The headline feature |
| **M5** | Adaptive scheduling, quantization, allocation audit, `install.ps1`, IR opt-in path | Ships and stays cheap |

---

## Verification

**Headless (CI-able):** record fixture clips (frames + labels) once, replay through a file-backed `ICameraSource`. This makes the gesture FSM, attention classifier, and throw physics unit-testable with no camera and no desktop — and is the only way to regression-test tracking changes honestly.

**Gesture state machine (M1.5, create `tests/Freethrow.Core.Tests` here):** the recognizer takes a pose and a timestamp and returns a state, so it can be driven entirely by synthetic sequences with no models involved. The three cases that must be covered are the ones that actually failed:

- an openness sequence that settles *inside* the old dead band must still reach `Hover`, not stall in `Grab`;
- a single contrary frame mid-release must not restart the confirm window;
- a hand whose palm axis points along the view direction must not arm, and a grab already held must survive that same rotation.

**Spatial mapping (M1.6), headless:** the homography and the pixel→metric conversion are pure functions and belong in `Freethrow.Core.Tests` alongside the gesture machine:

- a known quadrilateral round-trips: map the four corners forward and they land on the unit square's corners;
- **depth invariance** — the same hand at two simulated distances (pixel positions and `Scale` both halved, `WorldScale` unchanged) must produce the same metric position. This is the property the whole approach rests on;
- a degenerate quadrilateral (collinear or tiny) is rejected rather than fitted;
- a monitor absent from the profile falls back rather than throwing.

**Spatial mapping (M1.6), on screen:** after calibrating, the overlay shows a live dot at the mapped hand position over the four corner targets. Reaching toward a corner must put the dot in that corner. This is the only honest test of a mapping, and it is also how the metric conversion gets proven: lean toward the camera and back, and the dot must stay put.

**Two hands (M1.7), headless:** arbitration is pure logic over gesture states and depth proxies, so it is driven entirely by synthetic pairs of hands:

- the hand that grabs **second** never takes control while the first still holds it;
- control is released only when the holding hand opens — not when the other one does anything;
- a hand detected first but never closed never takes control, which is the reported bug;
- hover follows the nearer hand, and does **not** switch when the two are within the margin — the anti-chatter case;
- two tracks converging on one hand collapse to a single track.

**Two hands (M1.7), on screen:** raise both hands, then grab with each in turn. The grabbing hand must take control regardless of which appeared first, and the other must visibly not. Reach one hand toward the screen and the hover highlight must follow it; hold both at the same distance and the highlight must stay put rather than flicker. Watch `runs N detect / M track` while two hands are up: detection should be near-idle, not climbing per frame.

**Reference comparison (run whenever landmark geometry changes):** `--snap` a frame, run it through both the C# tracker and OpenCV's Python reference, and diff the landmarks. This is what caught the palm-selection defect; it is the check for the world-landmark de-rotation too.

**Instrumented:** `Demo.Preview` displays FPS, per-stage latency (ms), CPU %, and working set. The efficiency requirement is verified by reading these in each power state, not asserted.

Targets: ≥25 FPS engaged end to end; <60 ms hand-motion-to-window-motion latency; Idle-state CPU near zero; working set <300 MB.

**Manual end-to-end (M4 acceptance):** with ≥2 monitors — look at monitor 2, raise hand, confirm a window highlights on hover, grab, drag across the boundary, release. Then repeat with a flick. Then look at monitor 1 and throw it back. Confirm: maximized windows restore correctly, mixed-DPI moves scale correctly, elevated windows fail loudly, and looking away mid-drag aborts safely.

**Fatigue check (also M4 acceptance):** perform 20 consecutive window moves. If the arm tires or precision degrades over that run, the gain curve or the neutral zone is wrong — retune before adding features. This is the test that catches gorilla arm, and it cannot be automated.

**Gesture acceptance (M1.5), in the preview window:** rotate an open hand to point at the camera — the skeleton must show arming as blocked and never turn amber. Close the hand side-on — it must turn amber within about 100 ms. Open it only partway — it must turn blue again promptly rather than lingering. Grab, then rotate the wrist through its natural range — it must stay amber throughout. The preview's status line reports filtered openness and the palm-axis angle, so a failure says which of the two is at fault instead of just feeling wrong.

---

## Open items (deliberately deferred)

- **One camera per monitor** — sidesteps the yaw problem entirely, but requires multi-stream fusion. The `ICameraSource` interface is shaped to allow it; not built in v1.
- **Iris/gaze refinement** — model wired but off by default; enable if head pose alone proves too coarse to separate adjacent monitors.
- **Non-Windows backends** — `Freethrow.Core` stays platform-clean so this stays possible, but no work is done toward it.
- **Actions beyond window movement** (resize, snap-to-region, virtual desktop switching) — the event bus is shaped to carry them; v1 ships only movement.
- **Throw to a CrossDrop display** ([kramav/CrossDrop](https://github.com/kramav/CrossDrop)) — a room display driven over the tailnet as one more throw destination: Freethrow picks the screen and the moment, CrossDrop's frozen `/v1` HTTP API puts it on the wall. A separate process talking HTTP + bearer token, no shared code; CrossDrop needs no change. Not before M2 — it reuses the grab/drag/release path. Three constraints decide its shape:
  - **It throws what a window shows, not the window.** CrossDrop accepts URLs (`POST /v1/navigate`) and files (`/v1/upload`: pdf, images, txt, short audio/video, 25 MB cap because uploads are RAM on the Pi) and refuses pixel streaming with no trigger (its PLAN §13 — the boundary that keeps it from being a bad VNC). Most windows are a view of a URL or a file, so the work is resolving `HWND` → content, **on this side**, and CrossDrop needs almost nothing:

    | Window | Resolve to | Send |
    |---|---|---|
    | Browser | URL — **built on CrossDrop's side:** its extension's Freethrow switch PUTs every window's active tab to `127.0.0.1:47800/crossdrop/windows`. Freethrow hosts that listener and matches an `HWND` by bounds, then title. Contract, and the listener's Origin rule: CrossDrop `extension/README.md`. UI Automation on the address bar stays the fallback (fragile, unverified) | `navigate`. Logins do not travel; the kiosk has its own profile |
    | Explorer | selected file, via `Shell.Application` COM — reliable | `upload` |
    | Word / PowerPoint / Excel | `ActiveDocument.FullName` via COM, **exported to PDF here** (`ExportAsFixedFormat`) | `upload` the PDF |
    | Single-document apps (PDF reader, image viewer, media player) | file path from the process command line; fails for tabbed and multi-document apps, which expose only a title | `upload`; over 25 MB, serve it from this PC on the tailnet and `navigate` to it (the PC must stay on while it plays) |
    | No file behind it (Spotify, Discord, a game) | nothing | refuse, visibly — that is screen sharing |

    Build in that order: browser, Explorer, Office. Three lines not to cross: **conversion stays on the PC** (LibreOffice on the Pi would fight Chromium for RAM and make its agent a document converter — Office is already here); **no HTML, SVG or XML** uploads (CrossDrop's `/files` is unauthenticated and same-origin with its web UI's token; `test_nothing_in_types_can_execute` enforces it); **new text types** (`.md`, `.csv`, `.log`, served as `text/plain`) are a few lines in CrossDrop's `storage.TYPES`, added one at a time when a real throw needs one — not ahead of it.
  - **The wall is not a Windows monitor.** It has no `MONITORINFOEX` device name, so it needs a virtual entry in `SpatialProfile` (e.g. `crossdrop:<target>/<screen>`). Gaze toward a wall across the room is likely the extreme-yaw case of §3, which disarms — so the first version should skip attention: a **portal edge** on the side of the desktop facing the wall, where a throw off that edge sends. Acceptable because a wrong send is undone by CrossDrop's Home, unlike a lost window.
  - **Device list:** read `roomctl/targets.toml` as CrossDrop's tray app does, so the PC keeps one list of displays and tokens rather than two.
