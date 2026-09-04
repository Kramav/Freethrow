using Freethrow.Core.Capture;

namespace Freethrow.Core.Perception;

/// <summary>A hand being followed, with an identity stable across frames.</summary>
/// <param name="Id">
/// Stable while the hand stays tracked. Gesture state is accumulated per hand, so an id
/// that churned frame to frame would reset every debounce on every frame.
/// </param>
/// <param name="Pose">The hand's landmarks this frame.</param>
public readonly record struct TrackedHandPose(int Id, HandPose Pose);

/// <summary>
/// Follows hands across frames.
/// </summary>
/// <remarks>
/// Kept as an interface so the gesture and window layers never depend on which model or
/// runtime is doing the work. That is what leaves room to swap in a MediaPipe native
/// backend, or to replay recorded landmark fixtures in tests, without touching anything
/// downstream.
/// </remarks>
public interface IHandTracker : IDisposable
{
    /// <summary>Whether any hand is currently being followed.</summary>
    bool IsTracking { get; }

    /// <summary>How many hands are currently being followed.</summary>
    int TrackedHands { get; }

    /// <summary>How many times the expensive detection stage has run.</summary>
    long DetectionRuns { get; }

    /// <summary>How many times the cheap tracking stage has run.</summary>
    long TrackingRuns { get; }

    /// <summary>
    /// Processes one frame, returning every hand found. Empty if there are none.
    /// </summary>
    IReadOnlyList<TrackedHandPose> Track(FrameRef frame);

    /// <summary>Forgets all tracked hands, forcing fresh detection next frame.</summary>
    void Reset();
}
