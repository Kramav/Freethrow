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
    /// <remarks>
    /// Measured against <see cref="HandMetrics.Openness"/>, which is metric and so has a
    /// different range from the old projected measure. A frontal open hand reads about
    /// 1.8 and a fist about 1.1, so this sits well inside the closed cluster.
    /// </remarks>
    public float GrabOpenness { get; init; } = 1.35f;

    /// <summary>Openness above which a held hand counts as released.</summary>
    /// <remarks>
    /// Placed below the midpoint of the measured open/closed gap, biasing toward
    /// release. The previous 1.75 sat almost on top of the open-hand value itself, so a
    /// hand that was merely half-open satisfied neither threshold and stayed stuck in
    /// <see cref="GestureState.Grab"/> indefinitely — the dead band swallowed it.
    /// </remarks>
    public float ReleaseOpenness { get; init; } = 1.50f;

    /// <summary>
    /// How long the hand must read closed before a grab commits — long enough to reject
    /// a hand passing through a fist on its way somewhere else.
    /// </summary>
    /// <remarks>
    /// Expressed in seconds rather than frames because the tracking worker drops frames
    /// under load, which made a frame count an unpredictable amount of wall-clock time.
    /// </remarks>
    public double GrabConfirmSeconds { get; init; } = 0.10;

    /// <summary>
    /// How long the hand must read open before a release commits. Deliberately shorter
    /// than <see cref="GrabConfirmSeconds"/>: picking something up by accident is a
    /// nuisance, but a release that feels sticky makes the whole interaction feel broken.
    /// </summary>
    public double ReleaseConfirmSeconds { get; init; } = 0.06;

    /// <summary>
    /// How fast a contrary frame erodes accumulated evidence, relative to how fast
    /// supporting evidence builds it.
    /// </summary>
    /// <remarks>
    /// The previous implementation reset its counter to zero on a single contrary frame,
    /// so a release needed two <em>consecutive</em> clean frames and one noisy sample
    /// sent it back to the start. Decaying instead of resetting keeps the debounce
    /// meaningful while tolerating the odd bad frame, which is what landmark output
    /// actually looks like.
    /// </remarks>
    public double EvidenceDecayRate { get; init; } = 0.5;

    /// <summary>
    /// How far the hand may point along the camera's view axis and still be allowed to
    /// start a grab, from 0 (must lie flat to the camera) to 1 (no restriction).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Above this, the hand is foreshortened badly enough that the landmark model is
    /// guessing at the fingers and an open hand is genuinely indistinguishable from a
    /// fist. Refusing to arm is the only honest response.
    /// </para>
    /// <para>
    /// Measured separation is wide: hands lying flat to the camera read 0.11 to 0.21,
    /// while a hand angled toward it read 0.78. This sits in the gap.
    /// </para>
    /// </remarks>
    public float MaxViewAxisAlignment { get; init; } = 0.55f;

    /// <summary>
    /// One-euro filter minimum cutoff for the openness signal, governing how much
    /// jitter is removed while the hand is holding still.
    /// </summary>
    public double OpennessMinCutoff { get; init; } = 2.0;

    /// <summary>
    /// One-euro filter speed coefficient for the openness signal, governing how much lag
    /// is removed while the hand is actively opening or closing.
    /// </summary>
    /// <remarks>
    /// Tuned high on purpose. Deliberately opening the hand is a fast, large change, and
    /// filter lag on that signal is felt directly as a release that will not come — the
    /// very complaint this work addresses. Smoothing at rest is <see cref="OpennessMinCutoff"/>'s
    /// job; this one exists so intent passes through immediately.
    /// </remarks>
    public double OpennessBeta { get; init; } = 1.5;

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
/// <param name="Openness">Smoothed openness measure, for display and tuning.</param>
/// <param name="Confidence">Landmark confidence for this frame.</param>
/// <param name="IsCoasting">
/// True when the hand is not currently visible but a grab is being held through the
/// grace window. Consumers should keep the held object still rather than follow a
/// stale position.
/// </param>
/// <param name="ViewAlignment">
/// How much the hand points along the camera's view axis, 0 to 1. Reported so a refusal
/// to arm can be shown as a cause rather than felt as an unexplained failure.
/// </param>
/// <param name="IsArmingBlocked">
/// True when the pose is too foreshortened to judge, so no new grab can start. Never
/// affects a grab already in progress.
/// </param>
public readonly record struct GestureUpdate(
    GestureState State,
    GestureState PreviousState,
    Vector2 Position,
    float Openness,
    float Confidence,
    bool IsCoasting,
    float ViewAlignment,
    bool IsArmingBlocked)
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
    /// <summary>Longest gap treated as a real time step, in seconds.</summary>
    /// <remarks>
    /// A pause — the worker stalling, the process being suspended — must not dump a
    /// large chunk of evidence into the accumulators and fire a transition the hand
    /// never made.
    /// </remarks>
    private const double MaxTimeStepSeconds = 0.2;

    private readonly GestureOptions _options;
    private readonly OneEuroFilter2D _position;
    private readonly OneEuroFilter _openness;

    private GestureState _state = GestureState.NoHand;
    private Vector2 _lastPosition;
    private float _lastOpenness;
    private float _lastConfidence;
    private float _lastViewAlignment;
    private bool _lastArmingBlocked;
    private double _lastSeenTimestamp = double.NegativeInfinity;
    private double _lastUpdateTimestamp = double.NaN;
    private double _closedEvidence;
    private double _openEvidence;

    public GestureRecognizer(GestureOptions? options = null)
    {
        _options = options ?? GestureOptions.Default;
        _position = new OneEuroFilter2D(_options.PositionMinCutoff, _options.PositionBeta);
        _openness = new OneEuroFilter(_options.OpennessMinCutoff, _options.OpennessBeta);
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

        double elapsed = ResolveTimeStep(timestampSeconds);

        _lastSeenTimestamp = timestampSeconds;
        _lastConfidence = pose.Confidence;
        _lastOpenness = (float)_openness.Filter(HandMetrics.Openness(pose), timestampSeconds);
        _lastViewAlignment = HandMetrics.ViewAxisAlignment(pose);
        _lastPosition = _position.Filter(HandMetrics.PalmCenter(pose), timestampSeconds);

        if (_state == GestureState.Grab)
        {
            // Orientation is deliberately not consulted here. A wrist turns throughout a
            // drag, and dropping whatever is being carried because the hand rotated
            // would be a worse failure than the false grabs this gate exists to prevent.
            _lastArmingBlocked = false;
            _closedEvidence = 0;

            Accumulate(ref _openEvidence, _lastOpenness > _options.ReleaseOpenness, elapsed);

            if (_openEvidence >= _options.ReleaseConfirmSeconds)
            {
                _state = GestureState.Hover;
                _openEvidence = 0;
            }
        }
        else
        {
            _lastArmingBlocked = _lastViewAlignment > _options.MaxViewAxisAlignment;
            _openEvidence = 0;

            // A foreshortened hand cannot be judged open or closed at all, so it
            // contributes no evidence either way rather than counting as open.
            Accumulate(
                ref _closedEvidence,
                !_lastArmingBlocked && _lastOpenness < _options.GrabOpenness,
                elapsed);

            if (_closedEvidence >= _options.GrabConfirmSeconds)
            {
                _state = GestureState.Grab;
                _closedEvidence = 0;
            }
            else
            {
                _state = GestureState.Hover;
            }
        }

        return BuildUpdate(previous, isCoasting: false);
    }

    /// <summary>
    /// Builds or erodes evidence for a transition.
    /// </summary>
    /// <remarks>
    /// Contrary evidence decays the accumulator rather than clearing it, so a single bad
    /// landmark frame delays a transition instead of cancelling all progress toward it.
    /// </remarks>
    private void Accumulate(ref double evidence, bool supports, double elapsed)
    {
        evidence = supports
            ? evidence + elapsed
            : Math.Max(0, evidence - (elapsed * _options.EvidenceDecayRate));
    }

    /// <summary>Time since the last update, clamped to something physically plausible.</summary>
    private double ResolveTimeStep(double timestampSeconds)
    {
        double elapsed = double.IsNaN(_lastUpdateTimestamp)
            ? 0
            : timestampSeconds - _lastUpdateTimestamp;

        _lastUpdateTimestamp = timestampSeconds;
        return Math.Clamp(elapsed, 0, MaxTimeStepSeconds);
    }

    private GestureUpdate BuildUpdate(GestureState previous, bool isCoasting) => new(
        _state,
        previous,
        _lastPosition,
        _lastOpenness,
        _lastConfidence,
        isCoasting,
        _lastViewAlignment,
        _lastArmingBlocked);

    /// <summary>Clears all state, as though nothing had ever been tracked.</summary>
    public void Reset()
    {
        _state = GestureState.NoHand;
        _closedEvidence = 0;
        _openEvidence = 0;
        _lastSeenTimestamp = double.NegativeInfinity;
        _lastUpdateTimestamp = double.NaN;
        _lastPosition = Vector2.Zero;
        _lastOpenness = 0;
        _lastConfidence = 0;
        _lastViewAlignment = 0;
        _lastArmingBlocked = false;
        _position.Reset();
        _openness.Reset();
    }

    private GestureUpdate HandleMissingHand(GestureState previous, double timestampSeconds)
    {
        bool withinGrace =
            timestampSeconds - _lastSeenTimestamp <= _options.TrackingLossGraceSeconds;

        if (_state == GestureState.Grab && withinGrace)
        {
            // Hold the grab, but report the position as stale so nothing follows it.
            // Evidence is left untouched: with no hand there is nothing to judge, and
            // decaying it here would make a dropout quietly count toward a release.
            _lastUpdateTimestamp = timestampSeconds;
            return BuildUpdate(previous, isCoasting: true);
        }

        _state = GestureState.NoHand;
        _closedEvidence = 0;
        _openEvidence = 0;
        _lastConfidence = 0;
        _lastOpenness = 0;
        _lastViewAlignment = 0;
        _lastArmingBlocked = false;
        _lastPosition = Vector2.Zero;
        _lastUpdateTimestamp = timestampSeconds;
        _position.Reset();
        _openness.Reset();

        return BuildUpdate(previous, isCoasting: false);
    }
}
