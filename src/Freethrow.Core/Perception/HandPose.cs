using System.Numerics;

namespace Freethrow.Core.Perception;

/// <summary>
/// A hand detected in one frame, with landmarks in that frame's pixel coordinates.
/// </summary>
/// <remarks>
/// Coordinates are frame pixels rather than normalised units on purpose: every consumer
/// downstream reasons about screen-sized things, and carrying normalised values would
/// mean multiplying by the frame size at each use, which is exactly where sign and
/// aspect-ratio mistakes breed.
/// </remarks>
public sealed class HandPose
{
    /// <summary>Number of landmarks a hand always has.</summary>
    public const int LandmarkCount = 21;

    public HandPose(
        Vector3[] landmarks,
        Vector3[] worldLandmarks,
        Handedness handedness,
        float confidence)
    {
        ArgumentNullException.ThrowIfNull(landmarks);
        ArgumentNullException.ThrowIfNull(worldLandmarks);

        Require(landmarks, nameof(landmarks));
        Require(worldLandmarks, nameof(worldLandmarks));

        Landmarks = landmarks;
        WorldLandmarks = worldLandmarks;
        Handedness = handedness;
        Confidence = confidence;

        static void Require(Vector3[] points, string name)
        {
            if (points.Length != LandmarkCount)
            {
                throw new ArgumentException(
                    $"Expected {LandmarkCount} landmarks, got {points.Length}.",
                    name);
            }
        }
    }

    /// <summary>
    /// The 21 landmarks. X and Y are frame pixels; Z is depth relative to the wrist, in
    /// the same rough scale as X and Y, and is far less reliable than either.
    /// </summary>
    public Vector3[] Landmarks { get; }

    /// <summary>
    /// The same 21 landmarks in metric, hand-relative space, rotated so the axes line up
    /// with the image.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is where the hand's actual <em>shape</em> lives. <see cref="Landmarks"/> is a
    /// projection, so any measurement taken from it changes as the hand rotates: an open
    /// hand pointing at the camera projects to the same compact blob as a fist. Shape and
    /// orientation must therefore be judged here, and only screen position taken from
    /// <see cref="Landmarks"/>.
    /// </para>
    /// <para>
    /// The origin is roughly the hand's geometric centre and the units are metres, so
    /// absolute positions mean nothing — only distances, directions, and ratios do.
    /// </para>
    /// </remarks>
    public Vector3[] WorldLandmarks { get; }

    /// <summary>Which hand the model believes this is.</summary>
    public Handedness Handedness { get; }

    /// <summary>Model confidence that a hand is present, in 0..1.</summary>
    public float Confidence { get; }

    /// <summary>Convenience accessor for a landmark's 2D position in frame pixels.</summary>
    public Vector2 this[HandLandmark landmark]
    {
        get
        {
            Vector3 point = Landmarks[(int)landmark];
            return new Vector2(point.X, point.Y);
        }
    }

    /// <summary>A landmark's position in metric, hand-relative space.</summary>
    public Vector3 World(HandLandmark landmark) => WorldLandmarks[(int)landmark];
}
