using System.Numerics;
using Freethrow.Core.Imaging;

namespace Freethrow.Core.Perception.Onnx;

/// <summary>
/// Works out which rotated square of the frame to feed the landmark model.
/// </summary>
/// <remarks>
/// <para>
/// The landmark model expects a roughly upright, tightly framed hand. Getting that
/// region wrong is the most common cause of landmarks that look plausible but drift:
/// too loose and the hand occupies too few pixels, too tight and fingertips fall
/// outside the crop entirely.
/// </para>
/// <para>
/// The shift and enlargement constants come from MediaPipe's hand pipeline. The upward
/// shift exists because the palm detector boxes the palm, while the landmark model needs
/// the fingers too — which extend out of the top of that box once the hand is upright.
/// </para>
/// </remarks>
public static class HandCropGeometry
{
    /// <summary>Fraction of box height to shift a palm box upward.</summary>
    private const float PalmShiftY = -0.4f;

    /// <summary>How much to enlarge a palm box to take in the fingers.</summary>
    private const float PalmEnlarge = 3.0f;

    /// <summary>Fraction of box height to shift a landmark box upward.</summary>
    private const float HandShiftY = -0.1f;

    /// <summary>
    /// How much to enlarge a landmark box. Far smaller than the palm equivalent because
    /// the landmarks already include the fingers; this only adds margin for movement
    /// between frames.
    /// </summary>
    private const float HandEnlarge = 1.65f;

    /// <summary>Builds the crop for a freshly detected palm.</summary>
    public static RotatedCrop FromPalm(PalmDetection palm, int size)
    {
        ArgumentNullException.ThrowIfNull(palm);

        // Keypoint 0 is the palm base and keypoint 2 the middle-finger base; the line
        // between them is the hand's long axis.
        float rotation = RotationBetween(palm.Keypoints[0], palm.Keypoints[2]);
        return Build(palm.Keypoints, rotation, PalmShiftY, PalmEnlarge, size);
    }

    /// <summary>
    /// Builds the crop for the next frame from the landmarks found in this one.
    /// </summary>
    /// <remarks>
    /// This is what lets the palm detector stay switched off while a hand is being
    /// followed, which is the single largest saving in the perception budget.
    /// </remarks>
    public static RotatedCrop FromLandmarks(HandPose pose, int size)
    {
        ArgumentNullException.ThrowIfNull(pose);

        float rotation = RotationBetween(
            pose[HandLandmark.Wrist],
            pose[HandLandmark.MiddleMcp]);

        Span<Vector2> points = stackalloc Vector2[HandPose.LandmarkCount];
        for (int i = 0; i < HandPose.LandmarkCount; i++)
        {
            points[i] = new Vector2(pose.Landmarks[i].X, pose.Landmarks[i].Y);
        }

        return Build(points, rotation, HandShiftY, HandEnlarge, size);
    }

    /// <summary>
    /// The rotation that brings the <paramref name="from"/> to <paramref name="to"/>
    /// direction upright, in radians, wrapped to (-pi, pi].
    /// </summary>
    private static float RotationBetween(Vector2 from, Vector2 to)
    {
        // The negated Y accounts for screen coordinates growing downward while the angle
        // convention treats up as positive.
        float radians = (MathF.PI / 2) - MathF.Atan2(-(to.Y - from.Y), to.X - from.X);
        return radians - (2 * MathF.PI * MathF.Floor((radians + MathF.PI) / (2 * MathF.PI)));
    }

    /// <summary>
    /// Boxes the points in the rotated frame, shifts and enlarges that box, and returns
    /// it as a square crop back in frame coordinates.
    /// </summary>
    private static RotatedCrop Build(
        ReadOnlySpan<Vector2> points,
        float rotation,
        float shiftY,
        float enlarge,
        int size)
    {
        float cos = MathF.Cos(rotation);
        float sin = MathF.Sin(rotation);

        var min = new Vector2(float.MaxValue);
        var max = new Vector2(float.MinValue);

        foreach (Vector2 point in points)
        {
            // Rotate into the upright frame, where an axis-aligned box is tight.
            var rotated = new Vector2(
                (cos * point.X) + (sin * point.Y),
                (-sin * point.X) + (cos * point.Y));

            min = Vector2.Min(min, rotated);
            max = Vector2.Max(max, rotated);
        }

        Vector2 extent = max - min;
        float shift = shiftY * extent.Y;
        Vector2 center = ((min + max) / 2) + new Vector2(0, shift);

        // A square, so the crop is not distorted when resampled to the model's input.
        float side = MathF.Max(extent.X, extent.Y) * enlarge;

        var frameCenter = new Vector2(
            (cos * center.X) - (sin * center.Y),
            (sin * center.X) + (cos * center.Y));

        return new RotatedCrop(frameCenter, side, rotation, size);
    }
}
