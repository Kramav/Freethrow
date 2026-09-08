"""Convert an image to Freethrow's raw frame format and run OpenCV's reference tracker.

This exists to check the C# implementation against a known-good one on byte-identical
input. Any disagreement beyond a pixel or so means the C# preprocessing, anchor decode,
crop geometry or inverse transform is wrong.
"""
import struct
import sys

import cv2 as cv
import numpy as np

sys.path.insert(0, sys.path[0])
from mp_palmdet import MPPalmDet
from mp_handpose import MPHandPose

MAGIC = 0x57415246
VERSION = 1
FORMAT_BGRA32 = 1

LANDMARK_NAMES = [
    "Wrist", "ThumbCmc", "ThumbMcp", "ThumbIp", "ThumbTip",
    "IndexMcp", "IndexPip", "IndexDip", "IndexTip",
    "MiddleMcp", "MiddlePip", "MiddleDip", "MiddleTip",
    "RingMcp", "RingPip", "RingDip", "RingTip",
    "PinkyMcp", "PinkyPip", "PinkyDip", "PinkyTip",
]


def write_ftraw(image_bgr, path):
    """Writes BGRA pixels in the same layout Freethrow's FrameRef uses."""
    bgra = cv.cvtColor(image_bgr, cv.COLOR_BGR2BGRA)
    height, width = bgra.shape[:2]
    stride = width * 4
    with open(path, "wb") as handle:
        handle.write(struct.pack("<IiiiiI", MAGIC, VERSION, width, height, FORMAT_BGRA32, stride))
        handle.write(bgra.tobytes())
    return width, height


def main():
    image_path, raw_path, palm_model, hand_model = sys.argv[1:5]

    image = cv.imread(image_path)
    if image is None:
        raise SystemExit(f"could not read {image_path}")

    # Match the pipeline's capture resolution so both sides see the same pixels.
    image = cv.resize(image, (640, 480), interpolation=cv.INTER_AREA)
    width, height = write_ftraw(image, raw_path)
    print(f"frame     : {width}x{height} Bgra32 -> {raw_path}")

    palm_detector = MPPalmDet(modelPath=palm_model, nmsThreshold=0.3, scoreThreshold=0.5)
    # Threshold at 0 so every candidate's real confidence is visible.
    hand_detector = MPHandPose(modelPath=hand_model, confThreshold=0.0)

    palms = palm_detector.infer(image)
    if palms is None or len(palms) == 0:
        print("result    : no palm detected")
        return

    print(f"palms     : {len(palms)}")

    # Highest palm score, which is the candidate the C# tracker also selects. Comparing
    # the two on the same palm is the only way to tell a geometry bug from a difference
    # in which hand was picked.
    ranked = sorted(palms, key=lambda p: -p[-1])
    for palm in ranked:
        candidate = hand_detector.infer(image, palm)
        conf = candidate[131] if candidate is not None else 0.0
        print(f"  palm score {palm[-1]:.3f} box "
              f"({palm[0]:.0f},{palm[1]:.0f})-({palm[2]:.0f},{palm[3]:.0f}) "
              f"-> landmark conf {conf:.3f}")

    # Match the C# tracker: judge candidates by landmark confidence, not palm score.
    result = None
    for palm in ranked:
        candidate = hand_detector.infer(image, palm)
        if candidate is not None and (result is None or candidate[131] > result[131]):
            result = candidate

    if result is None:
        print("result    : landmark model rejected every crop")
        return
    print("selected  : best landmark confidence")

    landmarks = result[4:67].reshape(21, 3)
    world = result[67:130].reshape(21, 3)
    handedness = "Right" if result[130] >= 0.5 else "Left"

    print(f"handedness: {handedness}")
    print(f"confidence: {result[131]:.3f}")

    tips = [8, 12, 16, 20]
    palm_points = [0, 5, 9, 13, 17]

    wrist = landmarks[0, :2]
    scale = np.linalg.norm(landmarks[9, :2] - wrist)
    projected = float(np.mean([np.linalg.norm(landmarks[t, :2] - wrist) for t in tips]) / scale)
    palm_center = landmarks[palm_points, :2].mean(axis=0)

    world_wrist = world[0]
    world_scale = float(np.linalg.norm(world[9] - world_wrist))
    world_openness = float(np.mean([np.linalg.norm(world[t] - world_wrist) for t in tips]) / world_scale)

    axis = world[9] - world_wrist
    view_align = float(abs(axis[2] / np.linalg.norm(axis)))

    print(f"scale     : {scale:.2f} px screen, {world_scale:.4f} world")
    print(f"openness  : {world_openness:.3f} world, {projected:.3f} projected")
    print(f"view align: {view_align:.3f}")
    print(f"palm      : {palm_center[0]:.1f}, {palm_center[1]:.1f}")
    print("landmarks : screen x y z | world x y z")
    for i in range(21):
        x, y, z = landmarks[i]
        wx, wy, wz = world[i]
        print(f"  {i:2d} {LANDMARK_NAMES[i]:<10} {x:8.2f} {y:8.2f} {z:7.2f} | "
              f"{wx:8.4f} {wy:8.4f} {wz:8.4f}")


if __name__ == "__main__":
    main()
