using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Freethrow.Core.Capture;
using Freethrow.Core.Config;
using Freethrow.Core.Gestures;
using Freethrow.Core.Perception;
using Freethrow.Core.Perception.Onnx;
using Freethrow.Desktop.Capture;

namespace Freethrow.Demo.Preview;

/// <summary>
/// A scripted test of how well hands are tracked: the same poses, in the same order, at
/// three distances, judged against the gates that act on them and compared with the last run.
/// </summary>
/// <remarks>
/// <para>
/// Free-form tracking — "hold a hand up and move it around" — produces numbers with nothing
/// to judge them by: the tool cannot know which frames were meant to be a fist, so a
/// confidence of 0.62 could be fine or fatal. Here every frame is recorded under a phase
/// whose pose is known, so each number has a label, a gate to be judged against, and a
/// baseline beside it: a fist is read next to an open hand at the same distance.
/// </para>
/// <para>
/// Each phase's clock starts on the first frame with a hand rather than on the button, so
/// time spent getting into position is not counted as a tracking failure — but every lost
/// frame after that is.
/// </para>
/// </remarks>
public partial class HandTestWindow : Window
{
    private static readonly HandTestPhase[] Phases =
    [
        new("open", "open, where you use it",
            "Hold your hand OPEN, fingers spread, palm toward the camera — where you would actually use Freethrow.",
            "This is the baseline everything else is compared with. Hold still until the bar fills.",
            HandShape.Open, Moving: false, Seconds: 6, Distance: "where you use it"),
        new("fist", "fist, where you use it",
            "Now close it into a FIST, the way you would grab a window. Same place.",
            "Read directly against the open hand you just held. Hold still.",
            HandShape.Closed, Moving: false, Seconds: 6, Distance: "where you use it"),
        new("carry", "carry a window",
            "Keep the FIST closed and move it slowly LEFT and RIGHT, as if carrying a window across the screen.",
            "Keep moving until the bar fills. Every grab lost here is a window that would be dropped mid-move.",
            HandShape.Closed, Moving: true, Seconds: 10, Distance: null),
        new("open-near", "open, closer",
            "OPEN hand again, about halfway from where you use it toward the camera.",
            "Hold still. The distance is measured, so it does not need to be exact.",
            HandShape.Open, Moving: false, Seconds: 6, Distance: "closer"),
        new("fist-near", "fist, closer",
            "Now a FIST at that same closer distance.",
            "Hold still.",
            HandShape.Closed, Moving: false, Seconds: 6, Distance: "closer"),
        new("open-far", "open, further",
            "OPEN hand, pulled back toward your body — the furthest from the camera you would use.",
            "Hold still. The distance is measured, so it does not need to be exact.",
            HandShape.Open, Moving: false, Seconds: 6, Distance: "further"),
        new("fist-far", "fist, further",
            "Now a FIST at that same distance.",
            "Hold still.",
            HandShape.Closed, Moving: false, Seconds: 6, Distance: "further"),
    ];

    private readonly WindowsCameraEnumerator _enumerator = new();
    private readonly object _frameGate = new();
    private readonly object _recordGate = new();
    private readonly string? _comparePath;
    private readonly HandTestPhaseResult?[] _results = new HandTestPhaseResult?[Phases.Length];

    private ICameraSource? _source;
    private IHandTracker? _tracker;
    private HandTrackingWorker? _worker;
    private GestureOptions _gestureOptions = GestureOptions.Default;
    private FrameRef? _pendingFrame;
    private WriteableBitmap? _bitmap;
    private string _camera = "unknown camera";
    private string _format = "unknown format";

    private int _phaseIndex;
    private Mode _mode = Mode.Preparing;

    // Written on the worker thread, read on the UI thread; all under _recordGate.
    private TrackingQuality? _recording;
    private double _recordingSeconds;
    private long _recordingStartedAt;
    private bool _recordingDone;

    /// <param name="comparePath">
    /// A saved run to compare with, or null for the most recent run on this machine.
    /// </param>
    public HandTestWindow(string? comparePath = null)
    {
        _comparePath = comparePath;

        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private enum Mode
    {
        Preparing,
        Ready,
        Recording,
        PhaseDone,
        Results,
        Failed,
    }

    private HandTestPhase Current => Phases[_phaseIndex];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // The user's own thresholds, as the product would run: a test on the defaults
            // would measure a grab nobody actually makes.
            _gestureOptions = GestureProfile.LoadOptionsOrDefault();

            // One hand, as in calibration: each frame is filed under the pose one hand was
            // asked to make, and a second hand wandering in would be filed under it too.
            _tracker = OnnxHandTracker.Create(new HandTrackerOptions { MaxHands = 1 });
            _worker = new HandTrackingWorker(_tracker, _gestureOptions);
            _worker.ResultAvailable += OnResult;

            IReadOnlyList<CameraDeviceInfo> devices = await _enumerator.EnumerateAsync();
            if (devices.Count == 0)
            {
                Fail("No cameras found.");
                return;
            }

            CameraDeviceInfo device = devices.FirstOrDefault(d => d.Kind == CameraKind.Color) ?? devices[0];

            ICameraSource source = await _enumerator.OpenAsync(device);
            source.FrameArrived += OnFrameArrived;
            await source.StartAsync();
            _source = source;

            _camera = device.GroupName;
            _format = source.ActiveFormat.ToString();

            CompositionTarget.Rendering += OnRendering;
            BeginPhase(0);
        }
        catch (FileNotFoundException exception)
        {
            Fail(exception.Message);
        }
        catch (Exception exception)
        {
            Fail(exception is CameraException ? exception.Message : exception.ToString());
        }
    }

    /// <summary>Runs on a capture thread: hands the frame to tracking and to the display.</summary>
    private void OnFrameArrived(object? sender, FrameEventArgs e)
    {
        _worker?.Submit(e.Frame);

        FrameRef retained = e.Frame.Retain();
        FrameRef? displaced;

        lock (_frameGate)
        {
            displaced = _pendingFrame;
            _pendingFrame = retained;
        }

        displaced?.Dispose();
    }

    /// <summary>Runs on the worker thread for every processed frame.</summary>
    /// <remarks>
    /// Recorded here rather than from the render loop, which samples the latest result at
    /// the display's rate: that would count some frames twice and miss others entirely, and
    /// a missed frame is exactly the dropout this test exists to count.
    /// </remarks>
    private void OnResult(object? sender, HandTrackingResult result)
    {
        lock (_recordGate)
        {
            if (_recording is null || _recordingDone)
            {
                return;
            }

            if (_recordingStartedAt == 0)
            {
                if (result.Hands.Count == 0)
                {
                    return;
                }

                _recordingStartedAt = Stopwatch.GetTimestamp();
            }

            _recording.Add(result);

            if (Stopwatch.GetElapsedTime(_recordingStartedAt).TotalSeconds >= _recordingSeconds)
            {
                _recordingDone = true;
            }
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        FrameRef? frame;
        lock (_frameGate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        if (frame is not null)
        {
            using (frame)
            {
                Blit(frame);
            }
        }

        HandTrackingResult? result = _worker?.Latest;
        TrackedHand? hand = result?.Primary;

        if (result is null || hand is null)
        {
            Skeleton.Clear();
        }
        else
        {
            Skeleton.Show(
                [new HandRender(hand.Pose, hand.Gesture.State, hand.Gesture.IsArmingBlocked, false, true)],
                result.FrameWidth,
                result.FrameHeight);
        }

        if (_mode is Mode.Results or Mode.Failed)
        {
            return;
        }

        ShowLiveReadout(result, hand);

        if (_mode == Mode.Recording)
        {
            UpdateRecording();
        }
    }

    /// <summary>What the tracker sees right now, in the same terms the results will use.</summary>
    private void ShowLiveReadout(HandTrackingResult? result, TrackedHand? hand)
    {
        if (result is null || hand is null)
        {
            TrackingText.Text = "no hand in view";
            TrackingText.Foreground = (Brush)FindResource("Warn");
            return;
        }

        HandShape shape = TrackingQuality.Classify(
            HandMetrics.Openness(hand.Pose),
            _gestureOptions.GrabOpenness,
            _gestureOptions.ReleaseOpenness);

        string sees = hand.DepthProxy > 0 ? $"{result.FrameWidth / hand.DepthProxy * 100:0} cm" : "—";

        TrackingText.Text =
            $"reads as {HandTestReport.ShapeName(shape),-10}  confidence {hand.Pose.Confidence:0.00}  "
            + $"grab {(hand.Gesture.State == GestureState.Grab ? "held" : "no"),-4}  camera sees {sees}";

        // Warn in the colour the user is watching when a frame would not count toward a grab.
        TrackingText.Foreground = (Brush)FindResource(
            hand.Pose.Confidence < _gestureOptions.MinConfidence ? "Warn" : "Muted");
    }

    private void UpdateRecording()
    {
        long startedAt;
        bool done;
        int frames;

        lock (_recordGate)
        {
            startedAt = _recordingStartedAt;
            done = _recordingDone;
            frames = _recording?.Frames ?? 0;
        }

        if (startedAt == 0)
        {
            PhaseProgress.Value = 0;
            ClockText.Text = string.Empty;
            StatusText.Text = "Waiting for your hand. The clock starts when it is seen.";
            return;
        }

        double elapsed = Math.Min(Stopwatch.GetElapsedTime(startedAt).TotalSeconds, Current.Seconds);
        PhaseProgress.Value = elapsed / Current.Seconds;
        ClockText.Text = $"{elapsed:0.0} / {Current.Seconds:0} s   {frames} frames";
        StatusText.Text = Current.Moving
            ? "Recording. Keep the fist closed and keep moving."
            : "Recording. Hold still.";

        if (done)
        {
            CompletePhase();
        }
    }

    private void BeginPhase(int index)
    {
        _phaseIndex = index;
        _mode = Mode.Ready;

        lock (_recordGate)
        {
            _recording = null;
        }

        StepLabel.Text = $"Phase {index + 1} of {Phases.Length} — {Current.Title}";
        PromptText.Text = Current.Prompt;
        HintText.Text = Current.Hint;

        PhaseProgress.Value = 0;
        ClockText.Text = string.Empty;
        StatusText.Text = "Get into position, then start. The clock starts when your hand is seen.";

        PreviewBorder.Visibility = Visibility.Visible;
        ResultsText.Visibility = Visibility.Collapsed;

        PrimaryButton.Content = "Start";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }

    private void StartRecording()
    {
        lock (_recordGate)
        {
            _recording = new TrackingQuality(_gestureOptions.GrabOpenness, _gestureOptions.ReleaseOpenness);
            _recordingSeconds = Current.Seconds;
            _recordingStartedAt = 0;
            _recordingDone = false;
        }

        _mode = Mode.Recording;
        PrimaryButton.Content = "Cancel phase";
    }

    private void CompletePhase()
    {
        TrackingQuality? quality;
        lock (_recordGate)
        {
            quality = _recording;
            _recording = null;
        }

        if (quality is null)
        {
            return;
        }

        HandTestPhaseResult result = HandTestPhaseResult.From(Current, quality);
        _results[_phaseIndex] = result;
        _mode = Mode.PhaseDone;

        PhaseProgress.Value = 1;
        StatusText.Text =
            $"Tracked {result.Tracked:0%} of frames, confidence {Describe(result.ConfidenceP10)} or better nine "
            + $"frames in ten, read as {HandTestReport.ShapeName(Current.Expected)} {result.ReadAsExpected:0%} "
            + $"of the time" + (Current.Expected == HandShape.Closed ? $", {result.GrabsLost} grab(s) lost." : ".");

        bool isLast = _phaseIndex == Phases.Length - 1;
        PrimaryButton.Content = isLast ? "See results" : "Next phase";
        SecondaryButton.Content = "Redo phase";
        SecondaryButton.Visibility = Visibility.Visible;
    }

    private void ShowResults()
    {
        var run = new HandTestRun(
            DateTimeOffset.Now,
            Environment.MachineName,
            _camera,
            _format,
            _gestureOptions.GrabOpenness,
            _gestureOptions.ReleaseOpenness,
            HandTrackerOptions.Default.TrackingConfidence,
            _gestureOptions.MinConfidence,
            CalibrationWindow.MinimumConfidence,
            [.. _results.OfType<HandTestPhaseResult>()]);

        // Loaded before saving, so "the latest run" is the one before this.
        HandTestRun? previous = _comparePath is null ? HandTestReport.LoadLatest() : HandTestReport.Load(_comparePath);

        string saved;
        try
        {
            saved = HandTestReport.Save(run);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            saved = $"(not saved: {exception.Message})";
        }

        string report = HandTestReport.Format(run, previous);

        // Also to the console, so it can be pasted without touching the window.
        Console.WriteLine();
        Console.WriteLine(report);
        Console.WriteLine($"Saved to {saved}");

        _mode = Mode.Results;
        StepLabel.Text = "Results";
        PromptText.Text = previous is null
            ? "Hand tracking test complete"
            : "Hand tracking test complete — compared with your previous run";
        HintText.Text = "Select and copy the text below, or copy it from the console.";

        PreviewBorder.Visibility = Visibility.Collapsed;
        ResultsText.Visibility = Visibility.Visible;
        ResultsText.Text = report;

        TrackingText.Text = string.Empty;
        ClockText.Text = string.Empty;
        PhaseProgress.Value = 0;
        StatusText.Text = $"Saved to {saved}";

        PrimaryButton.Content = "Close";
        SecondaryButton.Content = "Run again";
        SecondaryButton.Visibility = Visibility.Visible;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Ready:
                StartRecording();
                break;

            case Mode.Recording:
                BeginPhase(_phaseIndex);
                break;

            case Mode.PhaseDone when _phaseIndex == Phases.Length - 1:
                ShowResults();
                break;

            case Mode.PhaseDone:
                BeginPhase(_phaseIndex + 1);
                break;

            case Mode.Results:
            case Mode.Failed:
                Close();
                break;
        }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.PhaseDone:
                _results[_phaseIndex] = null;
                BeginPhase(_phaseIndex);
                break;

            case Mode.Results:
                Array.Clear(_results);
                BeginPhase(0);
                break;
        }
    }

    private void Blit(FrameRef frame)
    {
        PixelFormat pixelFormat = frame.Format switch
        {
            FramePixelFormat.Bgra32 => PixelFormats.Bgra32,
            FramePixelFormat.Gray8 => PixelFormats.Gray8,
            _ => throw new NotSupportedException($"Cannot display {frame.Format} frames."),
        };

        if (_bitmap is null
            || _bitmap.PixelWidth != frame.Width
            || _bitmap.PixelHeight != frame.Height
            || _bitmap.Format != pixelFormat)
        {
            _bitmap = new WriteableBitmap(frame.Width, frame.Height, 96, 96, pixelFormat, null);
            PreviewImage.Source = _bitmap;
        }

        _bitmap.WritePixels(new Int32Rect(0, 0, frame.Width, frame.Height), frame.Data, frame.Stride, 0);
    }

    private void Fail(string message)
    {
        _mode = Mode.Failed;
        StepLabel.Text = "Cannot start";
        PromptText.Text = "The hand test could not start";
        HintText.Text = message;
        StatusText.Text = string.Empty;
        PrimaryButton.Content = "Close";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }

    private static string Describe(float? value) => value is { } number ? $"{number:0.00}" : "—";

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_source is null && _worker is null)
        {
            return;
        }

        // Shut the camera and worker down before the window goes, or the capture thread
        // can outlive the dispatcher and fault on the way out.
        e.Cancel = true;

        CompositionTarget.Rendering -= OnRendering;

        ICameraSource? source = _source;
        _source = null;

        if (source is not null)
        {
            source.FrameArrived -= OnFrameArrived;
            await source.DisposeAsync();
        }

        if (_worker is not null)
        {
            _worker.ResultAvailable -= OnResult;
            _worker.Dispose();
            _worker = null;
        }

        _tracker?.Dispose();
        _tracker = null;

        FrameRef? pending;
        lock (_frameGate)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
        }

        pending?.Dispose();

        Close();
    }
}
