# Models

This directory holds the ONNX models at run time. **They are not committed** — see
[.gitignore](../.gitignore). Run [tools/install.ps1](../tools/install.ps1) to download
and verify them.

| File | Purpose | Input |
|---|---|---|
| `palm_detection.onnx` | Finds hands in a full frame | 192x192 NHWC RGB, 0..1 |
| `hand_landmark.onnx` | 21 landmarks from a cropped, upright hand | 224x224 NHWC RGB, 0..1 |

## Provenance

Both are MediaPipe models converted to ONNX by the
[OpenCV Zoo](https://github.com/opencv/opencv_zoo) project and published on Hugging
Face. MediaPipe and the conversions are Apache-2.0, which is compatible with this
project's AGPL-3.0.

- Palm detection: [opencv/palm_detection_mediapipe](https://huggingface.co/opencv/palm_detection_mediapipe)
- Hand landmarks: [opencv/handpose_estimation_mediapipe](https://huggingface.co/opencv/handpose_estimation_mediapipe)

The install script verifies SHA-256 hashes and deletes any file that fails, because
these are fetched over the network and handed directly to an inference runtime.

## Where they are looked for

`ModelPaths` searches, in order:

1. The directory named by the `FREETHROW_MODELS` environment variable
2. `%LOCALAPPDATA%\Freethrow\models`
3. A `models` directory found by walking up from the running binary — which is what
   makes this directory work in a development tree

## Output layouts

These are assumptions the C# code depends on, verified at load time where possible.

**`palm_detection.onnx`** — one input, two outputs:
- `[1, 2016, 18]` box regressors: 4 box values then 7 keypoints as x,y pairs
- `[1, 2016, 1]` scores as logits, needing a sigmoid

The 2016 anchors are a 24x24 grid at stride 8 with 2 anchors per cell, followed by a
12x12 grid at stride 16 with 6. Anchor sizes are fixed, so only centres matter.

**`hand_landmark.onnx`** — one input, four outputs **in this order**:
1. `[1, 63]` screen landmarks, x/y in crop pixels and z relative
2. `[1, 1]` hand presence confidence, already a probability
3. `[1, 1]` handedness, 0 left to 1 right
4. `[1, 63]` world landmarks, metric, unused

Outputs 1 and 4 share a shape, as do 2 and 3, so order is the only thing distinguishing
them. `HandLandmarkDetector` validates the shapes and fails loudly if a re-exported
model does not match.
