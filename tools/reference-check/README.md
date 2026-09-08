# Reference check

Compares Freethrow's C# hand tracking against OpenCV Zoo's Python implementation of the
same two models, on **byte-identical input**.

Run this after any change to preprocessing, anchor decoding, crop geometry, the inverse
landmark transform, or the world-landmark de-rotation. Those produce output that looks
like a plausible hand while being subtly wrong, and no amount of watching the skeleton on
screen will reveal it. This check is how the palm-selection defect was found.

## Setup

Needs Python with `opencv-python` and `numpy`, plus OpenCV's reference implementations,
which are not vendored here:

```powershell
curl -o mp_palmdet.py  https://raw.githubusercontent.com/opencv/opencv_zoo/main/models/palm_detection_mediapipe/mp_palmdet.py
curl -o mp_handpose.py https://raw.githubusercontent.com/opencv/opencv_zoo/main/models/handpose_estimation_mediapipe/mp_handpose.py
```

Any photo of a hand works as input.

## Run

```powershell
# Reference: converts the image to Freethrow's raw frame format, then runs OpenCV's tracker.
python tools\reference-check\reference.py hand.jpg hand.ftraw models\palm_detection.onnx models\hand_landmark.onnx

# Ours, on exactly the same pixels.
dotnet run --project demos\Freethrow.Demo.Preview -- --landmarks hand.ftraw
```

Compare `openness`, `view align`, `scale` and the landmark table. The `.ftraw` format is
uncompressed precisely so no codec, colour management or chroma subsampling can make the
two sides disagree for reasons unrelated to the code.

## What agreement looks like

Measured 2026-09-02 on MediaPipe's `woman_hands.jpg`, both sides selecting the same hand:

| | Reference | Freethrow |
|---|---|---|
| World openness | 1.754 | 1.799 |
| World scale | 0.0885 | 0.0874 |
| Confidence | 0.980 | 0.979 |
| Palm centre | 378.9, 188.5 | 377.1, 189.4 |

Landmarks within a few pixels and openness within ~3% is expected. OpenCV integer-clips
its crop and resamples with `INTER_AREA`, while Freethrow samples the exact float geometry
in one pass, so perfect equality is not the goal — agreement to within resampling error is.

A disagreement in the *sign* or *axis* of anything, or openness differing by tens of
percent, means a real geometry bug.

## Inspecting a model

`inspect_models.py` prints input and output names, types and shapes. Use it when
swapping in a re-exported model, since `HandLandmarkDetector` depends on output *order*:
two outputs carry 63 values and two carry 1, so shape alone cannot identify them.

```powershell
python tools\reference-check\inspect_models.py models\palm_detection.onnx models\hand_landmark.onnx
```
