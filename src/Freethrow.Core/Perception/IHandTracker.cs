using Freethrow.Core.Capture;

namespace Freethrow.Core.Perception;

/// <summary>
/// Follows a hand across frames.
/// </summary>
/// <remarks>
/// Kept as an interface so the gesture and window layers never depend on which model or
/// runtime is doing the work. That is what leaves room to swap in a MediaPipe native
/// backend, or to replay recorded landmark fixtures in tests, without touching anything
/// downstream.
/// </remarks>
public interface IHandTracker : IDisposable
{
    /// <summary>Whether a hand is currently being followed.</summary>
    bool IsTracking { get; }

    /// <summary>How many times the expensive detection stage has run.</summary>
    long DetectionRuns { get; }

    /// <summary>How many times the cheap tracking stage has run.</summary>
    long TrackingRuns { get; }

    /// <summary>
    /// Processes one frame, returning the hand found or <see langword="null"/> if there
    /// is none.
    /// </summary>
    HandPose? Track(FrameRef frame);

    /// <summary>Forgets the tracked hand, forcing a fresh detection next frame.</summary>
    void Reset();
}
