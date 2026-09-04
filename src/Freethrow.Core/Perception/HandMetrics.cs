using System.Numerics;

namespace Freethrow.Core.Perception;

/// <summary>
/// Scale-invariant measurements derived from a hand's landmarks.
/// </summary>
/// <remarks>
/// Every measure here is divided by <see cref="Scale"/>, which is what makes the gesture
/// thresholds hold whether the hand is near the camera or at arm's length. Raw pixel
/// distances would need a different threshold at every depth, which is the usual reason
/// naive gesture detection works in a demo and fails in a room.
/// </remarks>
public static class HandMetrics
{
    private static readonly HandLandmark[] FingerTips =
    [
        HandLandmark.IndexTip,
        HandLandmark.MiddleTip,
        HandLandmark.RingTip,
        HandLandmark.PinkyTip,
    ];

    private static readonly HandLandmark[] PalmPoints =
    [
        HandLandmark.Wrist,
        HandLandmark.IndexMcp,
        HandLandmark.MiddleMcp,
        HandLandmark.RingMcp,
        HandLandmark.PinkyMcp,
    ];

    /// <summary>
    /// The hand's reference size: wrist to middle-finger knuckle, in frame pixels.
    /// </summary>
    /// <remarks>
    /// This span is chosen because it barely changes as fingers open and close — unlike,
    /// say, wrist-to-fingertip — so it stays a stable denominator for every other measure.
    /// </remarks>
    public static float Scale(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        return Vector2.Distance(pose[HandLandmark.Wrist], pose[HandLandmark.MiddleMcp]);
    }

    /// <summary>
    /// How open the hand is: mean fingertip-to-wrist distance over the four fingers,
    /// divided by <see cref="Scale"/>. A closed fist sits near 1.1, a flat open hand
    /// near 2.2. The thumb is excluded because it folds across the palm rather than
    /// toward the wrist, and including it blurs the two states together.
    /// </summary>
    public static float Openness(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        float scale = Scale(pose);
        if (scale <= float.Epsilon)
        {
            return 0;
        }

        Vector2 wrist = pose[HandLandmark.Wrist];
        float total = 0;

        foreach (HandLandmark tip in FingerTips)
        {
            total += Vector2.Distance(pose[tip], wrist);
        }

        return total / (FingerTips.Length * scale);
    }

    /// <summary>
    /// Thumb-tip to index-tip distance relative to <see cref="Scale"/>. Reserved for a
    /// future pinch gesture; a grab is a whole-hand close, which <see cref="Openness"/>
    /// separates far more reliably at webcam resolution.
    /// </summary>
    public static float PinchDistance(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        float scale = Scale(pose);
        return scale <= float.Epsilon
            ? 0
            : Vector2.Distance(pose[HandLandmark.ThumbTip], pose[HandLandmark.IndexTip]) / scale;
    }

    /// <summary>
    /// Centre of the palm, averaged over the wrist and the four knuckles.
    /// </summary>
    /// <remarks>
    /// This is the point the pipeline tracks for position, not a fingertip. Fingertips
    /// move several centimetres purely from opening and closing the hand, which would
    /// inject a position jump into every grab.
    /// </remarks>
    public static Vector2 PalmCenter(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        Vector2 total = Vector2.Zero;
        foreach (HandLandmark point in PalmPoints)
        {
            total += pose[point];
        }

        return total / PalmPoints.Length;
    }
}
