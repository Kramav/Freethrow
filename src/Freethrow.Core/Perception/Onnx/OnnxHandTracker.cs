using System.Diagnostics;
using System.Numerics;
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
    /// How many hands to follow at once.
    /// </summary>
    /// <remarks>
    /// Two by default. With one, whichever hand the detector happened to find first owns
    /// the interaction and a deliberate grab with the other does nothing — detection
    /// order deciding control is the defect this exists to remove. Drop to one on a
    /// machine that cannot afford the second landmark pass.
    /// </remarks>
    public int MaxHands { get; init; } = 2;

    /// <summary>
    /// How many palm candidates to evaluate when acquiring.
    /// </summary>
    /// <remarks>
    /// The palm detector's own score is a poor guide to which candidate will yield good
    /// landmarks. Measured on MediaPipe's two-hand sample image, the top-scoring palm
    /// (0.871) produced landmark confidence of just 0.686, while the runner-up (0.829)
    /// produced 0.980 — so trusting the detector's ranking picks the worse hand.
    /// </remarks>
    public int PalmCandidates { get; init; } = 3;

    /// <summary>
    /// How long to wait between hunts for an additional hand while at least one is
    /// already being followed.
    /// </summary>
    /// <remarks>
    /// With a free slot, the naive loop runs the whole-frame detector every frame looking
    /// for a second hand, which discards the entire saving the acquire-then-track loop
    /// exists for. Detection costs around 10 ms, so hunting a few times a second notices
    /// a hand entering within about a third of a second for a few percent of the budget.
    /// It does not apply when nothing is tracked: then the detector must run every frame
    /// or nothing is ever picked up.
    /// </remarks>
    public double RescanIntervalSeconds { get; init; } = 1.0 / 3;
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
/// detector stays switched off until a hand is lost or a slot is free.
/// </para>
/// <para>
/// The <see cref="DetectionRuns"/> and <see cref="TrackingRuns"/> counters exist to make
/// that ratio visible: if detections are not far rarer than tracking runs, the loop is
/// thrashing and the confidence thresholds need attention.
/// </para>
/// </remarks>
public sealed class OnnxHandTracker : IHandTracker
{
    /// <summary>Below this side length in pixels a crop is degenerate and unusable.</summary>
    private const float MinimumCropSide = 8f;

    /// <summary>
    /// How close two palms may be, as a fraction of hand width, before they are treated
    /// as the same hand.
    /// </summary>
    /// <remarks>
    /// Two regions of interest can drift onto one hand, producing a phantom second hand
    /// that shadows the first and competes with it for control. Hands genuinely held
    /// this close together cannot be told apart at webcam resolution anyway.
    /// </remarks>
    private const float DuplicateDistanceFraction = 0.9f;

    private readonly PalmDetector _palmDetector;
    private readonly HandLandmarkDetector _landmarkDetector;
    private readonly HandTrackerOptions _options;
    private readonly List<HandTrack> _tracks = [];

    private long _detectionRuns;
    private long _trackingRuns;
    private long _lastDetectionTimestamp;
    private int _nextTrackId = 1;

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
    public bool IsTracking => _tracks.Count > 0;

    /// <inheritdoc />
    public int TrackedHands => _tracks.Count;

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
    public IReadOnlyList<TrackedHandPose> Track(FrameRef frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        var found = new List<TrackedHandPose>(_options.MaxHands);

        FollowExisting(frame, found);
        RemoveDuplicates(found);

        if (found.Count < _options.MaxHands && ShouldDetect(frame))
        {
            AcquireMore(frame, found);
        }

        return found;
    }

    /// <inheritdoc />
    public void Reset()
    {
        _tracks.Clear();
        _lastDetectionTimestamp = 0;
    }

    public void Dispose()
    {
        _palmDetector.Dispose();
        _landmarkDetector.Dispose();
    }

    /// <summary>Follows every hand already being tracked, dropping any that are lost.</summary>
    private void FollowExisting(FrameRef frame, List<TrackedHandPose> found)
    {
        for (int i = _tracks.Count - 1; i >= 0; i--)
        {
            HandTrack track = _tracks[i];

            _trackingRuns++;
            HandPose pose = _landmarkDetector.Detect(frame, track.Crop);

            if (pose.Confidence < _options.TrackingConfidence || !track.Advance(pose))
            {
                _tracks.RemoveAt(i);
                continue;
            }

            found.Add(new TrackedHandPose(track.Id, pose));
        }
    }

    /// <summary>
    /// Collapses tracks that have converged on the same hand, keeping the more confident.
    /// </summary>
    private void RemoveDuplicates(List<TrackedHandPose> found)
    {
        for (int i = found.Count - 1; i > 0; i--)
        {
            for (int j = i - 1; j >= 0; j--)
            {
                if (!AreSameHand(found[i].Pose, found[j].Pose))
                {
                    continue;
                }

                int drop = found[i].Pose.Confidence < found[j].Pose.Confidence ? i : j;
                _tracks.RemoveAll(track => track.Id == found[drop].Id);
                found.RemoveAt(drop);
                break;
            }
        }
    }

    /// <summary>Runs the whole-frame detector and starts tracking any new hands it finds.</summary>
    private void AcquireMore(FrameRef frame, List<TrackedHandPose> found)
    {
        _detectionRuns++;
        _lastDetectionTimestamp = frame.CaptureTimestamp;

        IReadOnlyList<PalmDetection> palms = _palmDetector.Detect(frame, _options.PalmCandidates);

        foreach (PalmDetection palm in palms)
        {
            if (found.Count >= _options.MaxHands)
            {
                return;
            }

            // Skip palms that belong to a hand already being followed, or the same hand
            // would be picked up a second time under a new identity.
            if (found.Any(existing => Overlaps(palm, existing.Pose)))
            {
                continue;
            }

            RotatedCrop crop = HandCropGeometry.FromPalm(palm, HandLandmarkDetector.InputSize);
            if (crop.Side < MinimumCropSide)
            {
                continue;
            }

            _trackingRuns++;
            HandPose pose = _landmarkDetector.Detect(frame, crop);

            if (pose.Confidence < _options.DetectionConfidence)
            {
                continue;
            }

            if (found.Any(existing => AreSameHand(pose, existing.Pose)))
            {
                continue;
            }

            var track = new HandTrack(_nextTrackId++);
            if (!track.Advance(pose))
            {
                continue;
            }

            _tracks.Add(track);
            found.Add(new TrackedHandPose(track.Id, pose));
        }
    }

    /// <summary>
    /// Whether the whole-frame detector should run this frame.
    /// </summary>
    private bool ShouldDetect(FrameRef frame)
    {
        // Nothing is tracked, so there is nothing to lose by looking every frame — and
        // everything to lose by not.
        if (_tracks.Count == 0)
        {
            return true;
        }

        double elapsed =
            (frame.CaptureTimestamp - _lastDetectionTimestamp) / (double)Stopwatch.Frequency;

        return elapsed >= _options.RescanIntervalSeconds || elapsed < 0;
    }

    /// <summary>Whether two poses describe the same physical hand.</summary>
    private static bool AreSameHand(HandPose left, HandPose right)
    {
        float scale = Math.Max(HandMetrics.Scale(left), HandMetrics.Scale(right));
        if (scale <= float.Epsilon)
        {
            return true;
        }

        return Vector2.Distance(HandMetrics.PalmCenter(left), HandMetrics.PalmCenter(right))
            < scale * DuplicateDistanceFraction;
    }

    /// <summary>Whether a fresh palm detection lands on a hand already being followed.</summary>
    private static bool Overlaps(PalmDetection palm, HandPose pose)
    {
        float scale = HandMetrics.Scale(pose);
        if (scale <= float.Epsilon)
        {
            return false;
        }

        var palmCentre = (palm.Min + palm.Max) / 2;
        return Vector2.Distance(palmCentre, HandMetrics.PalmCenter(pose))
            < scale * DuplicateDistanceFraction;
    }

    /// <summary>One hand being followed, carrying the region to crop next frame.</summary>
    private sealed class HandTrack(int id)
    {
        public int Id { get; } = id;

        public RotatedCrop Crop { get; private set; }

        /// <summary>
        /// Predicts the next frame's crop from this frame's landmarks. Returns false if
        /// the landmarks collapsed, which would otherwise zoom the crop into a few pixels
        /// with no way back.
        /// </summary>
        public bool Advance(HandPose pose)
        {
            RotatedCrop next = HandCropGeometry.FromLandmarks(pose, HandLandmarkDetector.InputSize);
            if (next.Side < MinimumCropSide)
            {
                return false;
            }

            Crop = next;
            return true;
        }
    }
}
