using System.ComponentModel;
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
/// Fits grab thresholds to the user's own hand, with the camera visible throughout.
/// </summary>
/// <remarks>
/// <para>
/// An earlier console version asked people to hold poses on a fixed countdown with no
/// view of the camera. That is unusable: you cannot tell whether you are in frame,
/// whether your hand is being tracked, or whether the pose you are holding is the one
/// being measured — and a timer that starts without you guarantees the first samples
/// are of you still moving.
/// </para>
/// <para>
/// So: nothing starts until the user says so, progress advances only on frames where a
/// hand is actually tracked, and every measurement is on screen while it is taken. A
/// step takes exactly as long as it takes.
/// </para>
/// </remarks>
public partial class CalibrationWindow : Window
{
    /// <summary>Tracked frames needed per step, roughly a second and a half of good data.</summary>
    private const int SamplesPerStep = 45;

    /// <summary>Landmark confidence below which a frame is not worth measuring.</summary>
    private const float MinimumConfidence = 0.7f;

    private static readonly Step[] Steps =
    [
        new("Open hand",
            "Hold your hand OPEN, fingers spread, palm toward the camera.",
            "Keep your whole hand in frame, about an arm's length away."),
        new("Closed fist",
            "Now CLOSE your hand into a fist, still facing the camera.",
            "Squeeze the way you would to grab and carry a window."),
        new("Pointing at the camera",
            "Now POINT your hand at the camera, fingers toward the lens.",
            "This teaches Freethrow which poses it cannot read, so it stops grabbing by accident."),
    ];

    private readonly WindowsCameraEnumerator _enumerator = new();
    private readonly List<CalibrationPhase> _phases = [];
    private readonly object _gate = new();
    private readonly string? _profilePath;

    private ICameraSource? _source;
    private IHandTracker? _tracker;
    private HandTrackingWorker? _worker;
    private FrameRef? _pendingFrame;
    private WriteableBitmap? _bitmap;

    private CalibrationPhase? _recording;
    private int _stepIndex;
    private Mode _mode = Mode.Preparing;
    private GestureProfile? _fitted;

    public CalibrationWindow(string? profilePath = null)
    {
        _profilePath = profilePath;

        InitializeComponent();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private enum Mode
    {
        Preparing,
        Ready,
        Capturing,
        StepComplete,
        Finished,
        Failed,
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _tracker = OnnxHandTracker.Create();
            _worker = new HandTrackingWorker(_tracker, new GestureRecognizer());

            IReadOnlyList<CameraDeviceInfo> devices = await _enumerator.EnumerateAsync();
            if (devices.Count == 0)
            {
                Fail("No cameras found.");
                return;
            }

            CameraDeviceInfo device =
                devices.FirstOrDefault(d => d.Kind == CameraKind.Color) ?? devices[0];

            ICameraSource source = await _enumerator.OpenAsync(device);
            source.FrameArrived += OnFrameArrived;
            await source.StartAsync();
            _source = source;

            CompositionTarget.Rendering += OnRendering;
            BeginStep(0);
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

    /// <summary>Runs on a capture thread: hands the frame to display and to tracking.</summary>
    private void OnFrameArrived(object? sender, FrameEventArgs e)
    {
        _worker?.Submit(e.Frame);

        FrameRef retained = e.Frame.Retain();
        FrameRef? displaced;

        lock (_gate)
        {
            displaced = _pendingFrame;
            _pendingFrame = retained;
        }

        displaced?.Dispose();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        FrameRef? frame;
        lock (_gate)
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
        Skeleton.Show(
            result?.Pose,
            GestureState.Hover,
            isArmingBlocked: false,
            result?.FrameWidth ?? 0,
            result?.FrameHeight ?? 0);

        CollectAndReport(result?.Pose);
    }

    /// <summary>
    /// Records a sample if one is being asked for, and keeps the readouts current.
    /// </summary>
    /// <remarks>
    /// Sampling is driven from the render loop rather than the worker thread so that what
    /// is counted is exactly what is on screen. If the user sees a tracked hand, that
    /// frame counted; if they see nothing, the bar visibly stops. Making the feedback and
    /// the measurement the same thing is what lets someone correct their own posture.
    /// </remarks>
    private void CollectAndReport(HandPose? pose)
    {
        bool usable = pose is not null && pose.Confidence >= MinimumConfidence;

        if (_mode == Mode.Capturing && usable)
        {
            _recording!.Add(pose!);

            if (_recording.Openness.Count >= SamplesPerStep)
            {
                CompleteStep();
                return;
            }
        }

        UpdateReadouts(pose, usable);
    }

    private void UpdateReadouts(HandPose? pose, bool usable)
    {
        if (_mode is Mode.Finished or Mode.Failed)
        {
            return;
        }

        if (pose is null)
        {
            TrackingText.Text = "no hand detected — move into frame";
            TrackingText.Foreground = (Brush)FindResource("Warn");
        }
        else
        {
            TrackingText.Text =
                $"openness {HandMetrics.Openness(pose),5:0.00}   "
                + $"view {HandMetrics.ViewAxisAlignment(pose),5:0.00}   "
                + $"confidence {pose.Confidence,5:0.00}"
                + (usable ? string.Empty : "   — too uncertain to measure");

            TrackingText.Foreground = (Brush)FindResource(usable ? "Muted" : "Warn");
        }

        if (_mode == Mode.Capturing)
        {
            int captured = _recording!.Openness.Count;
            CaptureProgress.Value = captured / (double)SamplesPerStep;
            SampleCountText.Text = $"{captured} / {SamplesPerStep}";
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

        _bitmap.WritePixels(
            new Int32Rect(0, 0, frame.Width, frame.Height),
            frame.Data,
            frame.Stride,
            0);
    }

    private void BeginStep(int index)
    {
        _stepIndex = index;
        _mode = Mode.Ready;

        Step step = Steps[index];
        StepLabel.Text = $"Step {index + 1} of {Steps.Length} — {step.Title}";
        PromptText.Text = step.Prompt;
        HintText.Text = step.Hint;

        CaptureProgress.Value = 0;
        SampleCountText.Text = string.Empty;
        StatusText.Text = "Get into position, then start. There is no time limit.";

        PrimaryButton.Content = "Start capturing";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
    }

    private void StartCapturing()
    {
        _recording = new CalibrationPhase(Steps[_stepIndex].Title, [], []);
        _mode = Mode.Capturing;

        StatusText.Text = "Hold the pose. The bar only fills while your hand is tracked.";
        PrimaryButton.Content = "Cancel step";
        SecondaryButton.Visibility = Visibility.Collapsed;
    }

    private void CompleteStep()
    {
        _mode = Mode.StepComplete;
        _phases.Add(_recording!);
        _recording = null;

        CaptureProgress.Value = 1;
        StatusText.Text = "Captured.";

        bool isLast = _stepIndex == Steps.Length - 1;
        PrimaryButton.Content = isLast ? "See results" : "Next step";
        SecondaryButton.Content = "Redo this step";
        SecondaryButton.Visibility = Visibility.Visible;
    }

    private void Finish()
    {
        (GestureProfile? profile, string? problem) =
            GrabCalibration.Fit(_phases[0], _phases[1], _phases[2]);

        if (profile is null)
        {
            _mode = Mode.Failed;
            StepLabel.Text = "Calibration incomplete";
            PromptText.Text = "Could not fit thresholds";
            HintText.Text = problem ?? "Unknown problem.";
            StatusText.Text = string.Empty;
            PrimaryButton.Content = "Start over";
            PrimaryButton.IsEnabled = true;
            SecondaryButton.Visibility = Visibility.Collapsed;
            return;
        }

        _fitted = profile;
        _mode = Mode.Finished;

        StepLabel.Text = "Calibration complete";
        PromptText.Text = "Your grab thresholds";
        HintText.Text =
            $"Grab when openness falls below {profile.GrabOpenness:0.00}, release when it rises "
            + $"above {profile.ReleaseOpenness:0.00}, and refuse to start a grab when the hand "
            + $"points at the camera beyond {profile.MaxViewAxisAlignment:0.00}.\n\n"
            + $"Measured: open {GrabCalibration.Describe(_phases[0].Openness)}\n"
            + $"          closed {GrabCalibration.Describe(_phases[1].Openness)}";

        TrackingText.Text = string.Empty;
        SampleCountText.Text = string.Empty;
        StatusText.Text = string.Empty;
        PrimaryButton.Content = "Save profile";
        SecondaryButton.Content = "Start over";
        SecondaryButton.Visibility = Visibility.Visible;
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Ready:
                StartCapturing();
                break;

            case Mode.Capturing:
                // Cancelling a step returns to its prompt rather than abandoning the run.
                _recording = null;
                BeginStep(_stepIndex);
                break;

            case Mode.StepComplete when _stepIndex == Steps.Length - 1:
                Finish();
                break;

            case Mode.StepComplete:
                BeginStep(_stepIndex + 1);
                break;

            case Mode.Finished:
                SaveAndClose();
                break;

            case Mode.Failed:
                RestartAll();
                break;
        }
    }

    private void OnSecondaryClick(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.StepComplete:
                // Drop the step just captured and take it again.
                _phases.RemoveAt(_phases.Count - 1);
                BeginStep(_stepIndex);
                break;

            case Mode.Finished:
                RestartAll();
                break;
        }
    }

    private void RestartAll()
    {
        _phases.Clear();
        _fitted = null;
        BeginStep(0);
    }

    private void SaveAndClose()
    {
        try
        {
            _fitted!.Save(_profilePath);
            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save: {exception.Message}";
        }
    }

    private void Fail(string message)
    {
        _mode = Mode.Failed;
        StepLabel.Text = "Calibration unavailable";
        PromptText.Text = "Cannot start";
        HintText.Text = message;
        TrackingText.Text = string.Empty;
        PrimaryButton.Content = "Close";
        PrimaryButton.IsEnabled = true;
        PrimaryButton.Click -= OnPrimaryClick;
        PrimaryButton.Click += (_, _) => Close();
        SecondaryButton.Visibility = Visibility.Collapsed;
    }

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

        _worker?.Dispose();
        _worker = null;
        _tracker?.Dispose();
        _tracker = null;

        FrameRef? pending;
        lock (_gate)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
        }

        pending?.Dispose();

        Close();
    }

    private sealed record Step(string Title, string Prompt, string Hint);
}
