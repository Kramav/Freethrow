using System.Diagnostics;
using Freethrow.Core.Capture;
using Freethrow.Core.Diagnostics;
using Freethrow.Core.Filters;
using Freethrow.Core.Gestures;
using Freethrow.Core.Spatial;

namespace Freethrow.Core.Perception;

/// <summary>One tracked hand's state for a frame.</summary>
/// <param name="Id">Stable track identifier.</param>
/// <param name="Pose">Landmarks this frame.</param>
/// <param name="Gesture">Gesture state, accumulated per hand.</param>
/// <param name="DepthProxy">
/// Smoothed pixels per metre. Larger means nearer the camera, and so nearer the monitor.
/// </param>
public sealed record TrackedHand(int Id, HandPose Pose, GestureUpdate Gesture, float DepthProxy);

/// <summary>One frame's perception output.</summary>
/// <param name="Hands">Every hand found this frame.</param>
/// <param name="ControllingId">The hand holding the interaction, or null.</param>
/// <param name="HoverId">The hand designating a target, or null.</param>
/// <param name="FrameSequence">Sequence number of the frame this came from.</param>
/// <param name="FrameWidth">Width of that frame, for mapping landmarks to a display.</param>
/// <param name="FrameHeight">Height of that frame.</param>
public sealed record HandTrackingResult(
    IReadOnlyList<TrackedHand> Hands,
    int? ControllingId,
    int? HoverId,
    long FrameSequence,
    int FrameWidth,
    int FrameHeight)
{
    /// <summary>The hand holding the interaction, if any.</summary>
    public TrackedHand? Controlling => Find(ControllingId);

    /// <summary>The hand designating a target, if any.</summary>
    public TrackedHand? Hover => Find(HoverId);

    /// <summary>
    /// The hand that matters right now: whichever is in control, else whichever is
    /// pointing, else any at all.
    /// </summary>
    public TrackedHand? Primary => Controlling ?? Hover ?? (Hands.Count > 0 ? Hands[0] : null);

    private TrackedHand? Find(int? id)
    {
        if (id is not { } wanted)
        {
            return null;
        }

        foreach (TrackedHand hand in Hands)
        {
            if (hand.Id == wanted)
            {
                return hand;
            }
        }

        return null;
    }
}

/// <summary>
/// Runs hand tracking on its own thread, always on the most recent frame.
/// </summary>
/// <remarks>
/// <para>
/// Inference must not happen on the capture callback. Even at 10 ms it would occupy a
/// third of the frame interval inside a driver callback, and any slower frame would
/// start costing captured frames outright.
/// </para>
/// <para>
/// The handoff keeps exactly one frame: a queue would grow without bound whenever
/// inference fell behind capture, and every result would describe a progressively older
/// hand position. Dropping intermediate frames is the correct behaviour here — a stale
/// hand position is worse than a skipped one.
/// </para>
/// <para>
/// Gesture state is kept per hand rather than globally, so each hand has its own
/// debounce and one hand opening cannot disturb the other's grab.
/// </para>
/// </remarks>
public sealed class HandTrackingWorker : IDisposable
{
    private readonly IHandTracker _tracker;
    private readonly GestureOptions _gestureOptions;
    private readonly HandArbiter _arbiter;
    private readonly Dictionary<int, GestureRecognizer> _recognizers = [];
    private readonly Dictionary<int, OneEuroFilter> _depthFilters = [];
    private readonly MovingAverage _inferenceTime = new();
    private readonly object _gate = new();
    private readonly Thread _thread;

    private FrameRef? _pending;
    private bool _running = true;
    private long _framesProcessed;
    private long _framesWithHand;

    public HandTrackingWorker(
        IHandTracker tracker,
        GestureOptions? gestureOptions = null,
        ArbiterOptions? arbiterOptions = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        _tracker = tracker;
        _gestureOptions = gestureOptions ?? GestureOptions.Default;
        _arbiter = new HandArbiter(arbiterOptions);

        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "Freethrow hand tracking",
            // Below normal so a busy perception loop never competes with the UI thread
            // that has to stay responsive for the user's actual work.
            Priority = ThreadPriority.BelowNormal,
        };

        _thread.Start();
    }

    /// <summary>Raised on the worker thread when a frame has been processed.</summary>
    public event EventHandler<HandTrackingResult>? ResultAvailable;

    /// <summary>Most recent result, or <see langword="null"/> before the first frame.</summary>
    public HandTrackingResult? Latest { get; private set; }

    /// <summary>Mean milliseconds spent per processed frame.</summary>
    public double InferenceMilliseconds => _inferenceTime.Value;

    /// <summary>Worst milliseconds spent on a single frame.</summary>
    public double WorstInferenceMilliseconds => _inferenceTime.Max;

    /// <summary>Frames the worker actually processed, which is fewer than were captured.</summary>
    public long FramesProcessed => Interlocked.Read(ref _framesProcessed);

    /// <summary>Processed frames in which at least one hand was found.</summary>
    public long FramesWithHand => Interlocked.Read(ref _framesWithHand);

    /// <summary>Fraction of processed frames containing a hand, in 0..1.</summary>
    public double HandRate
    {
        get
        {
            long processed = FramesProcessed;
            return processed == 0 ? 0 : FramesWithHand / (double)processed;
        }
    }

    /// <summary>
    /// Offers a frame for processing, replacing any frame still waiting.
    /// </summary>
    public void Submit(FrameRef frame)
    {
        ArgumentNullException.ThrowIfNull(frame);

        FrameRef retained = frame.Retain();
        FrameRef? displaced;

        lock (_gate)
        {
            if (!_running)
            {
                retained.Dispose();
                return;
            }

            displaced = _pending;
            _pending = retained;
            Monitor.Pulse(_gate);
        }

        displaced?.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _running = false;
            Monitor.Pulse(_gate);
        }

        _thread.Join(TimeSpan.FromSeconds(2));

        FrameRef? leftover;
        lock (_gate)
        {
            leftover = _pending;
            _pending = null;
        }

        leftover?.Dispose();
    }

    private void Loop()
    {
        while (true)
        {
            FrameRef? frame;

            lock (_gate)
            {
                while (_pending is null && _running)
                {
                    Monitor.Wait(_gate);
                }

                if (!_running)
                {
                    return;
                }

                frame = _pending;
                _pending = null;
            }

            if (frame is null)
            {
                continue;
            }

            using (frame)
            {
                Process(frame);
            }
        }
    }

    private void Process(FrameRef frame)
    {
        long start = Stopwatch.GetTimestamp();
        IReadOnlyList<TrackedHandPose> tracked = _tracker.Track(frame);
        _inferenceTime.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);

        // Timestamp from when the frame was captured, not from now. The filters' notion
        // of elapsed time drives their smoothing, and feeding them the moment inference
        // happened to finish would make that vary with CPU load.
        double timestamp = frame.CaptureTimestamp / (double)Stopwatch.Frequency;

        var hands = new List<TrackedHand>(tracked.Count);
        var signals = new List<HandSignal>(tracked.Count);

        foreach ((int id, HandPose pose) in tracked)
        {
            GestureUpdate gesture = Recognizer(id).Update(pose, timestamp);

            // Smoothed, because the world-scale estimate behind this jitters frame to
            // frame and it decides which hand owns the highlight.
            float depth = (float)DepthFilter(id).Filter(HandSpace.PixelsPerMetre(pose), timestamp);

            hands.Add(new TrackedHand(id, pose, gesture, depth));
            signals.Add(new HandSignal(id, gesture.State, depth, pose.Confidence));
        }

        Forget(tracked);

        ArbitrationResult arbitration = _arbiter.Update(signals);

        Interlocked.Increment(ref _framesProcessed);
        if (hands.Count > 0)
        {
            Interlocked.Increment(ref _framesWithHand);
        }

        var result = new HandTrackingResult(
            hands,
            arbitration.ControllingId,
            arbitration.HoverId,
            frame.Sequence,
            frame.Width,
            frame.Height);

        Latest = result;
        ResultAvailable?.Invoke(this, result);
    }

    private GestureRecognizer Recognizer(int id)
    {
        if (!_recognizers.TryGetValue(id, out GestureRecognizer? recognizer))
        {
            recognizer = new GestureRecognizer(_gestureOptions);
            _recognizers[id] = recognizer;
        }

        return recognizer;
    }

    private OneEuroFilter DepthFilter(int id)
    {
        if (!_depthFilters.TryGetValue(id, out OneEuroFilter? filter))
        {
            filter = new OneEuroFilter(minCutoff: 1.0, beta: 0.0);
            _depthFilters[id] = filter;
        }

        return filter;
    }

    /// <summary>Drops per-hand state for hands that are no longer tracked.</summary>
    private void Forget(IReadOnlyList<TrackedHandPose> tracked)
    {
        if (_recognizers.Count == tracked.Count)
        {
            return;
        }

        foreach (int id in _recognizers.Keys.ToArray())
        {
            if (!tracked.Any(hand => hand.Id == id))
            {
                _recognizers.Remove(id);
                _depthFilters.Remove(id);
            }
        }
    }
}
