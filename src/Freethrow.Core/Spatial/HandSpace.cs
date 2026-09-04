using System.Numerics;
using Freethrow.Core.Perception;

namespace Freethrow.Core.Spatial;

/// <summary>
/// Converts hand positions from frame pixels into a metric space that does not move
/// when the user does.
/// </summary>
/// <remarks>
/// <para>
/// Palm centre arrives in frame pixels, and pixels mean different distances depending on
/// how far the hand is from the camera. Lean in and the same physical gesture sweeps
/// twice as many pixels. Worse, reaching for a far corner extends the arm, so the four
/// calibration corners are not even captured at a constant depth — a mapping fitted in
/// pixels is fitted to a moving target.
/// </para>
/// <para>
/// The hand carries its own ruler. <see cref="HandMetrics.Scale"/> measures the palm in
/// pixels and <see cref="HandMetrics.WorldScale"/> measures the same span in metres, so
/// their ratio is the projection's pixels-per-metre at the hand's actual distance.
/// Dividing a pixel offset by it recovers a lateral position in metres:
/// </para>
/// <code>
/// metres = (palmCentrePx − frameCentre) ÷ (pixelScale ÷ worldScale)
/// </code>
/// <para>
/// No camera intrinsics, no depth sensor — the hand is the calibration target, and it is
/// always in shot.
/// </para>
/// </remarks>
public static class HandSpace
{
    /// <summary>
    /// The projection's scale at the hand's current distance, in pixels per metre.
    /// Larger means closer to the camera.
    /// </summary>
    public static float PixelsPerMetre(HandPose pose)
    {
        ArgumentNullException.ThrowIfNull(pose);

        float worldScale = HandMetrics.WorldScale(pose);
        return worldScale <= float.Epsilon ? 0 : HandMetrics.Scale(pose) / worldScale;
    }

    /// <summary>
    /// Converts a point in frame pixels to metres relative to the frame centre.
    /// </summary>
    /// <param name="pixelPoint">Point in frame pixels.</param>
    /// <param name="frameCentre">
    /// The frame's centre, standing in for the camera's principal point. The true
    /// principal point is rarely more than a few percent off centre, which is far below
    /// the precision this mapping needs.
    /// </param>
    /// <param name="pixelsPerMetre">Scale from <see cref="PixelsPerMetre"/>, ideally smoothed.</param>
    public static Vector2 ToMetric(Vector2 pixelPoint, Vector2 frameCentre, float pixelsPerMetre)
    {
        if (pixelsPerMetre <= float.Epsilon)
        {
            return Vector2.Zero;
        }

        return (pixelPoint - frameCentre) / pixelsPerMetre;
    }

    /// <summary>
    /// Converts a pose's palm centre to metres relative to the frame centre.
    /// </summary>
    /// <remarks>
    /// Uses this frame's own scale estimate. Callers tracking continuously should smooth
    /// <see cref="PixelsPerMetre"/> over time and use the overload above instead, because
    /// the world-landmark scale is a model estimate and jitters frame to frame.
    /// </remarks>
    public static Vector2 ToMetric(HandPose pose, int frameWidth, int frameHeight)
    {
        ArgumentNullException.ThrowIfNull(pose);

        return ToMetric(
            HandMetrics.PalmCenter(pose),
            new Vector2(frameWidth / 2f, frameHeight / 2f),
            PixelsPerMetre(pose));
    }
}
