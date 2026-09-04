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

    public HandPose(Vector3[] landmarks, Handedness handedness, float confidence)
    {
        ArgumentNullException.ThrowIfNull(landmarks);
        if (landmarks.Length != LandmarkCount)
        {
            throw new ArgumentException(
                $"Expected {LandmarkCount} landmarks, got {landmarks.Length}.",
                nameof(landmarks));
        }

        Landmarks = landmarks;
        Handedness = handedness;
        Confidence = confidence;
    }

    /// <summary>
    /// The 21 landmarks. X and Y are frame pixels; Z is depth relative to the wrist, in
    /// the same rough scale as X and Y, and is far less reliable than either.
    /// </summary>
    public Vector3[] Landmarks { get; }

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
}
