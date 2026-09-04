using System.Numerics;

namespace Freethrow.Core.Perception;

/// <summary>
/// Scale-invariant measurements derived from a hand's landmarks.
/// </summary>
/// <remarks>
/// <para>
/// Measurements split by which coordinate space can actually answer the question.
/// <b>Shape and orientation</b> come from <see cref="HandPose.WorldLandmarks"/>, which
/// are metric and hand-relative; <b>screen position</b> comes from
/// <see cref="HandPose.Landmarks"/>, which are pixels.
/// </para>
/// <para>
/// That split is not tidiness. Judging shape from the projection is what made an open
/// hand pointing at the camera register as a fist: out-of-plane rotation foreshortens
/// the fingers faster than the palm, so the ratio collapses even though the hand never
/// closed. No threshold can separate two poses that project to the same picture.
/// </para>
/// <para>
/// Everything is divided by the hand's own size, which is what makes one threshold hold
/// whether the hand is near the camera or at arm's length.
/// </para>
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
    /// The hand's apparent size on screen: wrist to middle-finger knuckle, in frame
    /// pixels. Useful for display and for judging how many pixels the tracker had to
    /// work with; not a basis for shape measurements.
    /// </summary>
    public static float Scale(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        return Vector2.Distance(pose[HandLandmark.Wrist], pose[HandLandmark.MiddleMcp]);
    }

    /// <summary>
    /// The hand's true size: wrist to middle-finger knuckle in metric space. Unlike
    /// <see cref="Scale"/> this barely changes as the hand rotates, which is what makes
    /// it a usable denominator.
    /// </summary>
    public static float WorldScale(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);
        return Vector3.Distance(
            pose.World(HandLandmark.Wrist),
            pose.World(HandLandmark.MiddleMcp));
    }

    /// <summary>
    /// How open the hand is: mean fingertip-to-wrist distance over the four fingers,
    /// divided by <see cref="WorldScale"/>. Measured in metric space, so rotating the
    /// hand does not change it — only actually closing the hand does.
    /// </summary>
    /// <remarks>
    /// The thumb is excluded because it folds across the palm rather than toward the
    /// wrist, and including it blurs the open and closed clusters together.
    /// </remarks>
    public static float Openness(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        float scale = WorldScale(pose);
        if (scale <= float.Epsilon)
        {
            return 0;
        }

        Vector3 wrist = pose.World(HandLandmark.Wrist);
        float total = 0;

        foreach (HandLandmark tip in FingerTips)
        {
            total += Vector3.Distance(pose.World(tip), wrist);
        }

        return total / (FingerTips.Length * scale);
    }

    /// <summary>
    /// The same measure taken from the projected landmarks, as
    /// <see cref="Openness"/> used to be.
    /// </summary>
    /// <remarks>
    /// Kept only for diagnostics. Comparing it against <see cref="Openness"/> shows how
    /// much the current pose is being distorted by projection: when the two diverge, the
    /// hand is rotated out of the image plane. Never threshold on this.
    /// </remarks>
    public static float ProjectedOpenness(HandPose pose)
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
    /// How much the hand points along the camera's view axis, from 0 (lying in the image
    /// plane, fully visible) to 1 (pointing straight at or away from the camera).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the ambiguity detector. Near 1 the hand is foreshortened to the point
    /// where the landmark model is guessing at the fingers, so a grab must not arm no
    /// matter what <see cref="Openness"/> says.
    /// </para>
    /// <para>
    /// Deliberately measured on the <em>palm</em> axis, wrist to middle knuckle, rather
    /// than any finger direction. The palm axis is stable whether the hand is open or
    /// closed, while a finger direction degenerates in exactly the state being judged.
    /// </para>
    /// <para>
    /// The absolute value makes this indifferent to whether the palm faces the camera or
    /// away, and so indifferent to the sign convention of the model's depth axis — both
    /// are equally unreadable, and neither should arm a grab.
    /// </para>
    /// </remarks>
    public static float ViewAxisAlignment(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        Vector3 axis = pose.World(HandLandmark.MiddleMcp) - pose.World(HandLandmark.Wrist);
        float length = axis.Length();

        // A degenerate axis means the landmarks collapsed; report full ambiguity so the
        // caller refuses to arm rather than treating nonsense as a clean frontal pose.
        return length <= float.Epsilon ? 1f : MathF.Abs(axis.Z / length);
    }

    /// <summary>
    /// Thumb-tip to index-tip distance relative to <see cref="WorldScale"/>. Reserved
    /// for a future pinch gesture; a grab is a whole-hand close, which
    /// <see cref="Openness"/> separates far more reliably at webcam resolution.
    /// </summary>
    public static float PinchDistance(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        float scale = WorldScale(pose);
        return scale <= float.Epsilon
            ? 0
            : Vector3.Distance(
                pose.World(HandLandmark.ThumbTip),
                pose.World(HandLandmark.IndexTip)) / scale;
    }

    /// <summary>
    /// Centre of the palm in frame pixels, averaged over the wrist and the four knuckles.
    /// </summary>
    /// <remarks>
    /// Stays in screen space because this is the point the pointer follows, and it
    /// averages the palm rather than tracking a fingertip: fingertips move several
    /// centimetres purely from opening and closing the hand, which would inject a
    /// position jump into every grab.
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
