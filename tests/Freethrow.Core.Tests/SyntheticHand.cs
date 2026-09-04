using System.Numerics;
using Freethrow.Core.Perception;

namespace Freethrow.Core.Tests;

/// <summary>
/// Builds hand poses with chosen openness and orientation.
/// </summary>
/// <remarks>
/// The gesture machine consumes poses and timestamps and nothing else, so it can be
/// driven entirely by constructed input — no camera, no models, no desktop. That is the
/// whole reason the recognizer was kept free of platform dependencies, and it is what
/// makes the failures it exhibited reproducible in milliseconds.
/// </remarks>
internal static class SyntheticHand
{
    /// <summary>Wrist-to-knuckle length in metres, matching a measured real hand.</summary>
    private const float WorldScale = 0.087f;

    /// <summary>
    /// Creates a pose whose <see cref="HandMetrics.Openness"/> and
    /// <see cref="HandMetrics.ViewAxisAlignment"/> equal the requested values.
    /// </summary>
    /// <param name="openness">Target openness: about 1.1 is a fist, 1.8 a flat hand.</param>
    /// <param name="viewAlignment">
    /// Target alignment with the view axis, 0 (flat to the camera) to 1 (pointing at it).
    /// </param>
    /// <param name="confidence">Landmark confidence to report.</param>
    public static HandPose Create(float openness, float viewAlignment = 0f, float confidence = 0.9f)
    {
        // Tilt the palm axis out of the image plane by exactly enough that its normalised
        // Z component equals the requested alignment.
        float z = viewAlignment;
        float x = MathF.Sqrt(MathF.Max(0, 1 - (z * z)));
        var axis = new Vector3(x, 0, z);

        var world = new Vector3[HandPose.LandmarkCount];
        var screen = new Vector3[HandPose.LandmarkCount];

        Vector3 wrist = Vector3.Zero;
        world[(int)HandLandmark.Wrist] = wrist;
        world[(int)HandLandmark.MiddleMcp] = axis * WorldScale;

        // Openness is the mean fingertip-to-wrist distance over the palm length, so
        // placing every tip at that multiple of the scale produces it exactly.
        float tipDistance = openness * WorldScale;
        foreach (HandLandmark tip in new[]
                 {
                     HandLandmark.IndexTip,
                     HandLandmark.MiddleTip,
                     HandLandmark.RingTip,
                     HandLandmark.PinkyTip,
                 })
        {
            world[(int)tip] = axis * tipDistance;
        }

        // Remaining landmarks only need to be plausible; no measure under test reads them.
        for (int i = 0; i < HandPose.LandmarkCount; i++)
        {
            if (world[i] == Vector3.Zero && i != (int)HandLandmark.Wrist)
            {
                world[i] = axis * (WorldScale * 0.5f);
            }

            screen[i] = new Vector3(320 + (world[i].X * 1000), 240 + (world[i].Y * 1000), 0);
        }

        return new HandPose(screen, world, Handedness.Right, confidence);
    }
}
