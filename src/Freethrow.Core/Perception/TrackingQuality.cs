using Freethrow.Core.Gestures;

namespace Freethrow.Core.Perception;

/// <summary>How a hand's measured openness reads against the grab thresholds.</summary>
public enum HandShape
{
    /// <summary>At or below the grab threshold: a fist.</summary>
    Closed,

    /// <summary>Between the grab and release thresholds, where neither is decided.</summary>
    InBetween,

    /// <summary>At or above the release threshold.</summary>
    Open,
}

/// <summary>
/// Accumulates how well hands were tracked over a run of frames: confidence by hand
/// shape, how every grab ended, and how much of the scene the camera sees at the hand.
/// </summary>
/// <remarks>
/// <para>
/// Confidence is kept per hand shape because a fist hides its fingers and scores lower
/// than an open hand, while every gate that acts on confidence was set without measuring
/// that difference. A single overall figure would average the problem away.
/// </para>
/// <para>
/// Grab endings are split three ways because they fail in different places. A release is
/// the hand opening. A grab <em>past the grace</em> is the recognizer seeing too little
/// confidence for longer than it will coast. A grab <em>dropped by the tracker</em> is the
/// hand vanishing outright — and since the worker discards a vanished hand's recognizer on
/// the same frame, that grab never reports itself as ended; it can only be seen by
/// noticing a holding hand is gone.
/// </para>
/// </remarks>
public sealed class TrackingQuality(float grabOpenness, float releaseOpenness)
{
    private readonly Dictionary<HandShape, List<float>> _confidence = new()
    {
        [HandShape.Closed] = [],
        [HandShape.InBetween] = [],
        [HandShape.Open] = [],
    };

    private readonly List<float> _viewWidths = [];
    private readonly HashSet<int> _holding = [];

    /// <summary>Frames offered, with or without a hand.</summary>
    public int Frames { get; private set; }

    /// <summary>Frames in which at least one hand was tracked.</summary>
    public int FramesWithHand { get; private set; }

    /// <summary>Frames in which some hand was holding a grab.</summary>
    public int FramesHolding { get; private set; }

    public int GrabsStarted { get; private set; }

    /// <summary>Grabs ended by the hand opening.</summary>
    public int GrabsReleased { get; private set; }

    /// <summary>Grabs ended because the tracker stopped returning the hand at all.</summary>
    public int GrabsDroppedByTracker { get; private set; }

    /// <summary>Grabs ended because confidence stayed too low for longer than the grace.</summary>
    public int GrabsPastGrace { get; private set; }

    /// <summary>Every grab that ended without the hand opening.</summary>
    public int GrabsLost => GrabsDroppedByTracker + GrabsPastGrace;

    /// <summary>Hand-frames recorded, summed over every hand.</summary>
    public int HandFrames => _confidence.Values.Sum(values => values.Count);

    /// <summary>Which shape an openness reads as against these thresholds.</summary>
    public HandShape Classify(float openness) => Classify(openness, grabOpenness, releaseOpenness);

    /// <summary>Which shape an openness reads as against the given thresholds.</summary>
    /// <remarks>Static so a live readout files a hand exactly as the recorder will.</remarks>
    public static HandShape Classify(float openness, float grabOpenness, float releaseOpenness) =>
        openness <= grabOpenness ? HandShape.Closed
        : openness >= releaseOpenness ? HandShape.Open
        : HandShape.InBetween;

    /// <summary>Confidence of every hand-frame that read as <paramref name="shape"/>.</summary>
    public ConfidenceSample Confidence(HandShape shape) => new(_confidence[shape]);

    /// <summary>Confidence of every hand-frame, whatever its shape.</summary>
    public ConfidenceSample AllConfidence() => new([.. _confidence.Values.SelectMany(values => values)]);

    /// <summary>
    /// How wide a strip of the scene the camera sees at the hand's distance, in metres,
    /// one value per hand-frame.
    /// </summary>
    /// <remarks>
    /// Frame width divided by pixels per metre at the hand. It shrinks as the hand nears
    /// the camera, which is why working close to a laptop lid leaves so little room.
    /// </remarks>
    public ConfidenceSample ViewWidths() => new(_viewWidths);

    /// <summary>Records one processed frame.</summary>
    public void Add(HandTrackingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        Frames++;
        if (result.Hands.Count > 0)
        {
            FramesWithHand++;
        }

        foreach (TrackedHand hand in result.Hands)
        {
            if (hand.Gesture.GrabStarted)
            {
                GrabsStarted++;
            }

            if (hand.Gesture.GrabAborted)
            {
                GrabsPastGrace++;
            }
            else if (hand.Gesture.GrabEnded)
            {
                GrabsReleased++;
            }

            // Raw rather than the recognizer's smoothed openness, which stops updating
            // below its confidence gate — exactly the frames this is here to measure.
            _confidence[Classify(HandMetrics.Openness(hand.Pose))].Add(hand.Pose.Confidence);

            if (hand.DepthProxy > 0)
            {
                _viewWidths.Add(result.FrameWidth / hand.DepthProxy);
            }
        }

        GrabsDroppedByTracker += _holding.Count(id => !result.Hands.Any(hand => hand.Id == id));

        _holding.Clear();
        foreach (TrackedHand hand in result.Hands)
        {
            if (hand.Gesture.State == GestureState.Grab)
            {
                _holding.Add(hand.Id);
            }
        }

        if (_holding.Count > 0)
        {
            FramesHolding++;
        }
    }
}

/// <summary>A set of measurements, summarised the ways the gates need.</summary>
public sealed class ConfidenceSample
{
    private readonly float[] _sorted;

    public ConfidenceSample(IEnumerable<float> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        _sorted = [.. values.Order()];
    }

    public int Count => _sorted.Length;

    public bool IsEmpty => _sorted.Length == 0;

    /// <summary>The value below which <paramref name="fraction"/> of the sample lies.</summary>
    /// <remarks>
    /// The tenth percentile, not the minimum, is the figure to judge a gate by: one
    /// freak frame sets the minimum, while one frame in ten failing is a grab that
    /// visibly flickers.
    /// </remarks>
    public float Percentile(float fraction)
    {
        if (_sorted.Length == 0)
        {
            throw new InvalidOperationException("An empty sample has no percentiles.");
        }

        return _sorted[(int)MathF.Round(Math.Clamp(fraction, 0f, 1f) * (_sorted.Length - 1))];
    }

    public float Median => Percentile(0.5f);

    /// <summary>The share of the sample strictly below <paramref name="threshold"/>, 0 to 1.</summary>
    public double ShareUnder(float threshold) =>
        _sorted.Length == 0 ? 0 : _sorted.Count(value => value < threshold) / (double)_sorted.Length;
}
