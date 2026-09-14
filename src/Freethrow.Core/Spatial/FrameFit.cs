using System.Numerics;
using Freethrow.Core.Perception;

namespace Freethrow.Core.Spatial;

/// <summary>Borders of the camera frame, as the camera sees them (not mirrored).</summary>
[Flags]
public enum FrameEdge
{
    None = 0,
    Left = 1,
    Right = 2,
    Top = 4,
    Bottom = 8,
}

/// <summary>
/// Reports whether a hand is cut off by the edge of the camera's view.
/// </summary>
/// <remarks>
/// <para>
/// A clipped hand does not look clipped. The landmark model still returns all 21 points
/// with a confident score, but the ones past the border are extrapolated, not seen. A
/// pose measured from that is a guess at the fingers, and a position measured from it
/// is the camera's limit rather than where the hand actually went.
/// </para>
/// <para>
/// Calibration is where this matters most: nothing assumes the user's working area sits
/// in the middle of the frame, so a corner or a reach sweep can easily run off the side
/// of the view without anyone noticing.
/// </para>
/// </remarks>
public static class FrameFit
{
    /// <summary>
    /// The frame borders that any landmark is within <paramref name="marginFraction"/> of,
    /// or past.
    /// </summary>
    /// <param name="pose">The hand, with landmarks in frame pixels.</param>
    /// <param name="frameWidth">Frame width in pixels.</param>
    /// <param name="frameHeight">Frame height in pixels.</param>
    /// <param name="marginFraction">
    /// How close to a border counts as touching it, as a fraction of that dimension.
    /// The 2% default (about 13 px at 640 wide) is a starting value, not a measured one.
    /// </param>
    public static FrameEdge Edges(
        HandPose pose,
        int frameWidth,
        int frameHeight,
        float marginFraction = 0.02f)
    {
        ArgumentNullException.ThrowIfNull(pose);

        float marginX = frameWidth * marginFraction;
        float marginY = frameHeight * marginFraction;

        FrameEdge edges = FrameEdge.None;

        foreach (Vector3 point in pose.Landmarks)
        {
            if (point.X < marginX)
            {
                edges |= FrameEdge.Left;
            }

            if (point.X > frameWidth - marginX)
            {
                edges |= FrameEdge.Right;
            }

            if (point.Y < marginY)
            {
                edges |= FrameEdge.Top;
            }

            if (point.Y > frameHeight - marginY)
            {
                edges |= FrameEdge.Bottom;
            }
        }

        return edges;
    }
}
