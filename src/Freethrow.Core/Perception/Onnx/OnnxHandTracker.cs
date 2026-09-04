using Freethrow.Core.Capture;
using Freethrow.Core.Imaging;
using Microsoft.ML.OnnxRuntime;

namespace Freethrow.Core.Perception.Onnx;

/// <summary>Tuning for <see cref="OnnxHandTracker"/>.</summary>
public sealed record HandTrackerOptions
{
    public static HandTrackerOptions Default { get; } = new();

    /// <summary>
    /// Landmark confidence required to start following a newly detected hand.
    /// </summary>
    public float DetectionConfidence { get; init; } = 0.7f;

    /// <summary>
    /// Landmark confidence required to keep following a hand already being tracked.
    /// </summary>
    /// <remarks>
    /// Deliberately lower than <see cref="DetectionConfidence"/>. Acquiring a hand
    /// should take real evidence, but once one is being followed the surrounding
    /// context makes a marginal frame far more likely to be the same hand than a false
    /// positive — and dropping tracking re-runs the expensive detector.
    /// </remarks>
    public float TrackingConfidence { get; init; } = 0.5f;

    /// <summary>
    /// How many palm candidates to evaluate when acquiring a hand.
    /// </summary>
    /// <remarks>
    /// The palm detector's own score is a poor guide to which candidate will yield good
    /// landmarks. Measured on MediaPipe's two-hand sample image, the top-scoring palm
    /// (0.871) produced landmark confidence of just 0.686, while the runner-up (0.829)
    /// produced 0.980 — so trusting the detector's ranking picks the worse hand. Trying
    /// a couple of candidates and keeping whichever the landmark model actually likes
    /// costs one extra cheap inference, and only when acquiring.
    /// </remarks>
    public int PalmCandidates { get; init; } = 2;
}

/// <summary>
/// Hand tracking via MediaPipe's palm detection and hand landmark models on ONNX Runtime.
/// </summary>
/// <remarks>
/// <para>
/// The two models are used asymmetrically, and that asymmetry is the entire point.
/// Palm detection scans the whole frame and is expensive; the landmark model looks only
/// at a small crop and is cheap. So detection runs only to <em>acquire</em> a hand,
/// after which each frame's landmarks predict where to crop the next one, and the
/// detector stays switched off until tracking is lost.
/// </para>
/// <para>
/// In steady state that means one cheap inference per frame instead of two, which is
/// what keeps a continuously running gesture system off the CPU. The
/// <see cref="DetectionRuns"/> and <see cref="TrackingRuns"/> counters exist to make
/// that ratio visible: if detections are not far rarer than tracking runs, the loop is
/// thrashing and the confidence thresholds need attention.
/// </para>
/// </remarks>
public sealed class OnnxHandTracker : IHandTracker
{
    /// <summary>Below this side length in pixels a crop is degenerate and unusable.</summary>
    private const float MinimumCropSide = 8f;

    private readonly PalmDetector _palmDetector;
    private readonly HandLandmarkDetector _landmarkDetector;
    private readonly HandTrackerOptions _options;

    private RotatedCrop? _crop;
    private long _detectionRuns;
    private long _trackingRuns;

    public OnnxHandTracker(
        string palmDetectionModelPath,
        string handLandmarkModelPath,
        HandTrackerOptions? options = null,
        SessionOptions? sessionOptions = null)
    {
        _options = options ?? HandTrackerOptions.Default;
        _palmDetector = new PalmDetector(palmDetectionModelPath, sessionOptions);
        _landmarkDetector = new HandLandmarkDetector(handLandmarkModelPath, sessionOptions);
    }

    /// <inheritdoc />
    public bool IsTracking => _crop is not null;

    /// <inheritdoc />
    public long DetectionRuns => _detectionRuns;

    /// <inheritdoc />
    public long TrackingRuns => _trackingRuns;

    /// <summary>Creates a tracker using the installed models.</summary>
    public static OnnxHandTracker Create(HandTrackerOptions? options = null) => new(
        ModelPaths.Resolve(ModelPaths.PalmDetection),
        ModelPaths.Resolve(ModelPaths.HandLandmark),
        options);

    /// <inheritdoc />
    public HandPose? Track(FrameRef frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        if (_crop is { } crop && TryFollow(frame, crop) is { } followed)
        {
            return followed;
        }

        return Acquire(frame);
    }

    /// <inheritdoc />
    public void Reset() => _crop = null;

    public void Dispose()
    {
        _palmDetector.Dispose();
        _landmarkDetector.Dispose();
    }

    /// <summary>Follows an already-tracked hand using the previous frame's crop.</summary>
    private HandPose? TryFollow(FrameRef frame, RotatedCrop crop)
    {
        _trackingRuns++;
        HandPose pose = _landmarkDetector.Detect(frame, crop);

        if (pose.Confidence < _options.TrackingConfidence)
        {
            _crop = null;
            return null;
        }

        return AdvanceCrop(pose);
    }

    /// <summary>Runs the full detector to find a hand from scratch.</summary>
    private HandPose? Acquire(FrameRef frame)
    {
        _detectionRuns++;
        IReadOnlyList<PalmDetection> palms = _palmDetector.Detect(frame, _options.PalmCandidates);

        HandPose? best = null;

        // Judge candidates by landmark confidence rather than palm score; see
        // HandTrackerOptions.PalmCandidates for why the two disagree.
        foreach (PalmDetection palm in palms)
        {
            RotatedCrop crop = HandCropGeometry.FromPalm(palm, HandLandmarkDetector.InputSize);
            if (crop.Side < MinimumCropSide)
            {
                continue;
            }

            _trackingRuns++;
            HandPose pose = _landmarkDetector.Detect(frame, crop);

            if (best is null || pose.Confidence > best.Confidence)
            {
                best = pose;
            }
        }

        return best is null || best.Confidence < _options.DetectionConfidence
            ? null
            : AdvanceCrop(best);
    }

    /// <summary>Predicts the next frame's crop from this frame's landmarks.</summary>
    private HandPose? AdvanceCrop(HandPose pose)
    {
        RotatedCrop next = HandCropGeometry.FromLandmarks(pose, HandLandmarkDetector.InputSize);

        // A collapsed crop means the landmarks degenerated to a point; following it
        // would zoom into a few pixels and never recover.
        if (next.Side < MinimumCropSide)
        {
            _crop = null;
            return null;
        }

        _crop = next;
        return pose;
    }
}
