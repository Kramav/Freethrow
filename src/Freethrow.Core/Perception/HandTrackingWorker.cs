using System.Diagnostics;
using Freethrow.Core.Capture;
using Freethrow.Core.Diagnostics;
using Freethrow.Core.Gestures;

namespace Freethrow.Core.Perception;

/// <summary>One frame's perception output.</summary>
/// <param name="Pose">The hand found, or <see langword="null"/>.</param>
/// <param name="Gesture">Gesture state after this frame.</param>
/// <param name="FrameSequence">Sequence number of the frame this came from.</param>
/// <param name="FrameWidth">Width of that frame, for mapping landmarks to a display.</param>
/// <param name="FrameHeight">Height of that frame.</param>
public sealed record HandTrackingResult(
    HandPose? Pose,
    GestureUpdate Gesture,
    long FrameSequence,
    int FrameWidth,
    int FrameHeight);

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
/// </remarks>
public sealed class HandTrackingWorker : IDisposable
{
    private readonly IHandTracker _tracker;
    private readonly GestureRecognizer _recognizer;
    private readonly MovingAverage _inferenceTime = new();
    private readonly object _gate = new();
    private readonly Thread _thread;

    private FrameRef? _pending;
    private bool _running = true;
    private long _framesProcessed;
    private long _framesWithHand;

    public HandTrackingWorker(IHandTracker tracker, GestureRecognizer? recognizer = null)
    {
        ArgumentNullException.ThrowIfNull(tracker);

        _tracker = tracker;
        _recognizer = recognizer ?? new GestureRecognizer();

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

    /// <summary>Processed frames in which a hand was found.</summary>
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
        HandPose? pose = _tracker.Track(frame);
        _inferenceTime.Add((Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency);

        // Timestamp the gesture from when the frame was captured, not from now. The
        // filter's notion of elapsed time drives its smoothing, and feeding it the
        // moment inference happened to finish would make that vary with CPU load.
        double timestamp = frame.CaptureTimestamp / (double)Stopwatch.Frequency;
        GestureUpdate gesture = _recognizer.Update(pose, timestamp);

        Interlocked.Increment(ref _framesProcessed);
        if (pose is not null)
        {
            Interlocked.Increment(ref _framesWithHand);
        }

        var result = new HandTrackingResult(pose, gesture, frame.Sequence, frame.Width, frame.Height);
        Latest = result;
        ResultAvailable?.Invoke(this, result);
    }
}
