using System.Numerics;
using Freethrow.Core.Filters;
using Freethrow.Core.Perception;

namespace Freethrow.Core.Gestures;

/// <summary>What the hand is doing.</summary>
public enum GestureState
{
    /// <summary>No hand is being tracked.</summary>
    NoHand = 0,

    /// <summary>A hand is present and open — designating, not holding.</summary>
    Hover,

    /// <summary>A hand is present and closed — holding.</summary>
    Grab,
}

/// <summary>Tuning for <see cref="GestureRecognizer"/>.</summary>
public sealed record GestureOptions
{
    public static GestureOptions Default { get; } = new();

    /// <summary>
    /// Openness below which the hand counts as closed. Paired with
    /// <see cref="ReleaseOpenness"/> as a Schmitt trigger: a single threshold would
    /// chatter between grab and release whenever the hand rests near it, which is
    /// precisely where a half-closed hand tends to sit.
    /// </summary>
    public float GrabOpenness { get; init; } = 1.55f;

    /// <summary>Openness above which a held hand counts as released.</summary>
    public float ReleaseOpenness { get; init; } = 1.75f;

    /// <summary>
    /// Consecutive closed frames before a grab commits. Three frames is about 100 ms —
    /// long enough to reject a hand passing through a fist on its way somewhere else.
    /// </summary>
    public int GrabConfirmFrames { get; init; } = 3;

    /// <summary>
    /// Consecutive open frames before a release commits. Deliberately shorter than
    /// <see cref="GrabConfirmFrames"/>: picking something up by accident is a nuisance,
    /// but a release that feels sticky makes the whole interaction feel broken.
    /// </summary>
    public int ReleaseConfirmFrames { get; init; } = 2;

    /// <summary>
    /// How long a grab survives losing the hand.
    /// </summary>
    /// <remarks>
    /// Landmark tracking drops the odd frame — a hand turns edge-on, or the exposure
    /// shifts. Releasing on the first missed frame would drop whatever is being carried
    /// several times per move. Holding briefly is the difference between a usable drag
    /// and an infuriating one.
    /// </remarks>
    public double TrackingLossGraceSeconds { get; init; } = 0.25;

    /// <summary>Landmark confidence below which a detection is ignored entirely.</summary>
    public float MinConfidence { get; init; } = 0.6f;

    /// <summary>One-euro filter minimum cutoff for the tracked palm position.</summary>
    public double PositionMinCutoff { get; init; } = 1.0;

    /// <summary>One-euro filter speed coefficient for the tracked palm position.</summary>
    public double PositionBeta { get; init; } = 0.7;
}

/// <summary>The result of feeding one frame to the recognizer.</summary>
/// <param name="State">State after this frame.</param>
/// <param name="PreviousState">State before this frame.</param>
/// <param name="Position">Smoothed palm centre in frame pixels; zero when no hand.</param>
/// <param name="Openness">Raw openness measure, for display and tuning.</param>
/// <param name="Confidence">Landmark confidence for this frame.</param>
/// <param name="IsCoasting">
/// True when the hand is not currently visible but a grab is being held through the
/// grace window. Consumers should keep the held object still rather than follow a
/// stale position.
/// </param>
public readonly record struct GestureUpdate(
    GestureState State,
    GestureState PreviousState,
    Vector2 Position,
    float Openness,
    float Confidence,
    bool IsCoasting)
{
    /// <summary>True on the frame a grab commits.</summary>
    public bool GrabStarted => State == GestureState.Grab && PreviousState != GestureState.Grab;

    /// <summary>True on the frame a grab ends, whether released or lost.</summary>
    public bool GrabEnded => PreviousState == GestureState.Grab && State != GestureState.Grab;

    /// <summary>
    /// True when a grab ended because tracking was lost rather than because the hand
    /// opened. The two deserve different treatment: a deliberate release drops the
    /// window where it is, a lost grab should put it back.
    /// </summary>
    public bool GrabAborted => GrabEnded && State == GestureState.NoHand;
}

/// <summary>
/// Turns a stream of hand poses into a debounced open/closed gesture with a smoothed
/// position.
/// </summary>
/// <remarks>
/// Nothing here touches windows or the screen. That separation is what lets the whole
/// state machine be tested against recorded landmark sequences, with no camera and no
/// desktop involved.
/// </remarks>
public sealed class GestureRecognizer
{
    private readonly GestureOptions _options;
    private readonly OneEuroFilter2D _position;

    private GestureState _state = GestureState.NoHand;
    private Vector2 _lastPosition;
    private float _lastOpenness;
    private float _lastConfidence;
    private double _lastSeenTimestamp = double.NegativeInfinity;
    private int _closedFrames;
    private int _openFrames;

    public GestureRecognizer(GestureOptions? options = null)
    {
        _options = options ?? GestureOptions.Default;
        _position = new OneEuroFilter2D(_options.PositionMinCutoff, _options.PositionBeta);
    }

    /// <summary>Current state.</summary>
    public GestureState State => _state;

    /// <summary>Options in force.</summary>
    public GestureOptions Options => _options;

    /// <summary>Feeds one frame's detection result.</summary>
    /// <param name="pose">The detected hand, or <see langword="null"/> if none was found.</param>
    /// <param name="timestampSeconds">Monotonic timestamp, in seconds.</param>
    public GestureUpdate Update(HandPose? pose, double timestampSeconds)
    {
        GestureState previous = _state;

        if (pose is null || pose.Confidence < _options.MinConfidence)
        {
            return HandleMissingHand(previous, timestampSeconds);
        }

        _lastSeenTimestamp = timestampSeconds;
        _lastConfidence = pose.Confidence;
        _lastOpenness = HandMetrics.Openness(pose);
        _lastPosition = _position.Filter(HandMetrics.PalmCenter(pose), timestampSeconds);

        if (_state == GestureState.Grab)
        {
            _openFrames = _lastOpenness > _options.ReleaseOpenness ? _openFrames + 1 : 0;
            _closedFrames = 0;

            if (_openFrames >= _options.ReleaseConfirmFrames)
            {
                _state = GestureState.Hover;
                _openFrames = 0;
            }
        }
        else
        {
            _closedFrames = _lastOpenness < _options.GrabOpenness ? _closedFrames + 1 : 0;
            _openFrames = 0;

            _state = _closedFrames >= _options.GrabConfirmFrames
                ? GestureState.Grab
                : GestureState.Hover;

            if (_state == GestureState.Grab)
            {
                _closedFrames = 0;
            }
        }

        return new GestureUpdate(
            _state,
            previous,
            _lastPosition,
            _lastOpenness,
            _lastConfidence,
            IsCoasting: false);
    }

    /// <summary>Clears all state, as though nothing had ever been tracked.</summary>
    public void Reset()
    {
        _state = GestureState.NoHand;
        _closedFrames = 0;
        _openFrames = 0;
        _lastSeenTimestamp = double.NegativeInfinity;
        _lastPosition = Vector2.Zero;
        _lastOpenness = 0;
        _lastConfidence = 0;
        _position.Reset();
    }

    private GestureUpdate HandleMissingHand(GestureState previous, double timestampSeconds)
    {
        bool withinGrace =
            timestampSeconds - _lastSeenTimestamp <= _options.TrackingLossGraceSeconds;

        if (_state == GestureState.Grab && withinGrace)
        {
            // Hold the grab, but report the position as stale so nothing follows it.
            return new GestureUpdate(
                _state,
                previous,
                _lastPosition,
                _lastOpenness,
                _lastConfidence,
                IsCoasting: true);
        }

        _state = GestureState.NoHand;
        _closedFrames = 0;
        _openFrames = 0;
        _lastConfidence = 0;
        _position.Reset();

        return new GestureUpdate(
            _state,
            previous,
            Vector2.Zero,
            0,
            0,
            IsCoasting: false);
    }
}
