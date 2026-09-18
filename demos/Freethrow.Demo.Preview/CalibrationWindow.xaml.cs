using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Freethrow.Core.Capture;
using Freethrow.Core.Config;
using Freethrow.Core.Gestures;
using Freethrow.Core.Perception;
using Freethrow.Core.Perception.Onnx;
using Freethrow.Core.Spatial;
using Freethrow.Desktop.Capture;
using Freethrow.Desktop.Desktop;
using Freethrow.Desktop.Overlay;

namespace Freethrow.Demo.Preview;

/// <summary>
/// Fits both halves of calibration to the user: how closed their grab is, and where
/// their hand is relative to the screen.
/// </summary>
/// <remarks>
/// <para>
/// Nothing starts until the user says so, progress advances only on frames where a hand
/// is actually tracked, and every measurement is on screen while it is taken. A step
/// takes exactly as long as it takes.
/// </para>
/// <para>
/// The two halves are ordered deliberately: the thresholds must be measured first,
/// because a corner cannot be confirmed with a grab until the system knows what this
/// person's grab looks like. The freshly fitted thresholds are applied to the tracker
/// before the spatial steps begin.
/// </para>
/// </remarks>
public partial class CalibrationWindow : Window
{
    private const int SamplesPerPose = 45;

    /// <summary>Landmark confidence a frame needs before it counts as a calibration sample.</summary>
    /// <remarks>Internal so <c>--track</c> can report how often a hand falls under it.</remarks>
    internal const float MinimumConfidence = 0.7f;
    private const string IdleCapturingHint = "Rest your hands as you normally would. The bar fills while they're tracked.";
    private const string IdleSkipLabel = "My hands are out of frame";

    /// <summary>
    /// How long the idle step waits with no hand in view before pointing at the skip
    /// button. Without the prompt, hands resting out of shot look like a stalled wizard.
    /// </summary>
    private static readonly TimeSpan IdleSkipPrompt = TimeSpan.FromSeconds(3);

    private readonly WindowsCameraEnumerator _enumerator = new();
    private readonly Dictionary<string, CalibrationPhase> _poses = [];

    /// <summary>
    /// Captured reach points, each tagged with the spot it was captured for. Tagged rather
    /// than positional so that redoing a step, or a mode with a different number of points,
    /// cannot misname one or feed the fit out of order.
    /// </summary>
    private readonly List<(Spot Target, Vector2 Position, FrameEdge Edges)> _corners = [];

    private readonly object _gate = new();
    private readonly string? _gesturePath;
    private readonly string? _spatialPath;
    private readonly ReachMode? _requestedReach;

    private ICameraSource? _source;
    private IHandTracker? _tracker;
    private HandTrackingWorker? _worker;
    private CalibrationTargetOverlay? _overlay;
    private FrameRef? _pendingFrame;
    private WriteableBitmap? _bitmap;
    private MonitorInfo? _monitor;

    private WizardStep[] _steps = [];
    private int _stepIndex;
    private Mode _mode = Mode.Preparing;
    private ReachMode _reachMode;

    private CalibrationPhase? _recordingPose;
    private IdleCapture? _recordingIdle;
    private PointCapture? _recordingPoint;
    private SweepCapture? _recordingSweep;
    private IdleZone? _idle;
    private bool _spaceHeld;

    private FrameEdge _lastEdges;
    private FrameEdge _lostAtEdges;
    private bool _handWasVisible;
    private long _idleQuietSince;
    private bool _idleSkipPromptShown;

    private GestureProfile? _fittedGesture;
    private MonitorMapping? _fittedMapping;
    private SweepCapture? _reach;
    private ScreenMapping? _testMapping;

    /// <param name="gesturePath">Where to save the grab thresholds, or null for the default.</param>
    /// <param name="spatialPath">Where to save the screen mapping, or null for the default.</param>
    /// <param name="reach">
    /// The working-area mode to start in, or null to reuse the mode this monitor was last
    /// calibrated in. Either way it can be changed in the window before the first reach step.
    /// </param>
    public CalibrationWindow(string? gesturePath = null, string? spatialPath = null, ReachMode? reach = null)
    {
        _gesturePath = gesturePath;
        _spatialPath = spatialPath;
        _requestedReach = reach;

        InitializeComponent();

        ConfirmationMode.ItemsSource = new[]
        {
            "Grab and hold",
            "Hover and hold still",
            "Press and hold space",
        };
        ConfirmationMode.SelectedIndex = 0;

        // Indexed by ReachMode. No selection yet: that waits for OnLoaded, which knows the
        // monitor and so what it was last calibrated in.
        ReachModeBox.ItemsSource = new[]
        {
            "Full screen — four corners",
            "Side to side only — two points",
        };

        Loaded += OnLoaded;
        Closing += OnClosing;
        KeyDown += (_, e) => _spaceHeld |= e.Key == Key.Space;
        KeyUp += (_, e) => _spaceHeld &= e.Key != Key.Space;
    }

    private enum Mode
    {
        Preparing,
        Ready,
        Capturing,
        StepComplete,
        Testing,
        Finished,
        Failed,
    }

    private CornerConfirmation Confirmation => (CornerConfirmation)ConfirmationMode.SelectedIndex;

    private WizardStep Current => _steps[_stepIndex];

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            _monitor = MonitorTopology.Primary() ?? MonitorTopology.Enumerate().FirstOrDefault();
            if (_monitor is null)
            {
                Fail("No monitors reported.");
                return;
            }

            _reachMode = _requestedReach ?? StoredReachMode(_monitor);
            _steps = BuildSteps(_monitor, _reachMode);
            ReachModeBox.SelectedIndex = (int)_reachMode;

            // One hand at a time: calibration measures a specific hand held in a specific
            // pose, so a second hand wandering into frame could only confuse which one is
            // being recorded.
            _tracker = OnnxHandTracker.Create(new HandTrackerOptions { MaxHands = 1 });
            _worker = new HandTrackingWorker(_tracker);

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

    /// <remarks>
    /// The reach mode changes only the tail of the list — pose and idle steps are the same
    /// in both — so the current step index stays valid when the mode is changed mid-wizard.
    /// That invariant is what lets the mode be a plain selector rather than a wizard step of
    /// its own, which would be one more step type for every step-type switch to handle.
    /// </remarks>
    private static WizardStep[] BuildSteps(MonitorInfo monitor, ReachMode reach) =>
    [
        new PoseStep("open", "Open hand",
            "Hold your hand OPEN, fingers spread, palm toward the camera.",
            "Keep your whole hand in frame, about an arm's length away."),
        new PoseStep("closed", "Closed fist",
            "Now CLOSE your hand into a fist, still facing the camera.",
            "Squeeze the way you would to grab and carry a window."),
        new PoseStep("pointing", "Pointing at the camera",
            "Now POINT your hand at the camera, fingers toward the lens.",
            "This teaches Freethrow which poses it cannot read, so it stops grabbing by accident."),

        new IdleStep("Idle position",
            "Put your hands where they sit when you're not using Freethrow — keyboard, mouse, lap.",
            "If the camera can't see them there, choose \"My hands are out of frame\". That is a normal setup, not a problem."),

        .. (reach switch
        {
            ReachMode.FullScreen => CornerSteps(monitor),
            ReachMode.SideToSide => SideSteps(monitor),
        }),

        reach == ReachMode.SideToSide
            ? new SweepStep("Maximum reach",
                "Now sweep your hand as far LEFT and RIGHT as you can reach.",
                "Height doesn't matter. This is measured once and is never where the screen edges land — it only gives room to overshoot without losing tracking.")
            : new SweepStep("Maximum reach",
                "Now sweep your hand around the FULL area you can reach.",
                "Go as far as you can in every direction. This is measured once and is never where the screen edges land — it only gives room to overshoot without losing tracking."),
    ];

    private static WizardStep[] CornerSteps(MonitorInfo monitor) =>
    [
        new PointStep(Spot.TopLeft, "Top-left corner",
            $"Reach toward the TOP-LEFT marker on {monitor.Description}.",
            "Reach only as far as stays comfortable — this becomes the edge of your working area, and you will go there often."),
        new PointStep(Spot.TopRight, "Top-right corner",
            "Now the TOP-RIGHT marker.",
            "Same comfortable reach. Do not stretch."),
        new PointStep(Spot.BottomRight, "Bottom-right corner",
            "Now the BOTTOM-RIGHT marker.",
            "Same comfortable reach."),
        new PointStep(Spot.BottomLeft, "Bottom-left corner",
            "Now the BOTTOM-LEFT marker.",
            "Last corner. Same comfortable reach."),
    ];

    /// <remarks>
    /// The hints say height does not matter because it is the one thing the user will
    /// otherwise get wrong: aiming for the marker's height, halfway down the screen, is
    /// what put the hand out of view in the first place.
    /// </remarks>
    private static WizardStep[] SideSteps(MonitorInfo monitor) =>
    [
        new PointStep(Spot.MidLeft, "Left edge",
            $"Reach toward the LEFT marker on {monitor.Description}.",
            "Height doesn't matter — reach at whatever height is comfortable and in view. Only how far left counts, and it becomes the edge of your working area."),
        new PointStep(Spot.MidRight, "Right edge",
            "Now the RIGHT marker.",
            "Same comfortable reach, at about the same height. Do not stretch."),
    ];

    /// <summary>The mode this monitor was last calibrated in, so recalibrating does not mean choosing again.</summary>
    /// <remarks>
    /// A lid camera's profile goes stale whenever the lid is tilted, so recalibration is
    /// routine on exactly the machines that need side to side. Keyed on the stored kind
    /// rather than how it was captured: four corners too flat to steer by is as good a sign
    /// as any that this camera cannot see vertical reach.
    /// </remarks>
    private ReachMode StoredReachMode(MonitorInfo monitor) =>
        SpatialProfile.Load(_spatialPath)?.Find(monitor.DeviceName)?.Kind == MappingKind.HorizontalOnly
            ? ReachMode.SideToSide
            : ReachMode.FullScreen;

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

        Process(result);
    }

    /// <summary>
    /// Records a sample if one is being asked for, and keeps the readouts current.
    /// </summary>
    /// <remarks>
    /// Driven from the render loop so that what is counted is exactly what is on screen.
    /// If the user sees a tracked hand, that frame counted; if they see nothing, the bar
    /// visibly stops. Making the feedback and the measurement the same thing is what
    /// lets someone correct their own posture.
    /// </remarks>
    private void Process(HandTrackingResult? result)
    {
        TrackedHand? hand = result?.Primary;
        HandPose? pose = hand?.Pose;
        bool usable = pose is not null && pose.Confidence >= MinimumConfidence;

        Vector2 metric = usable && result is not null
            ? HandSpace.ToMetric(pose!, result.FrameWidth, result.FrameHeight)
            : Vector2.Zero;

        // Edges come from any tracked pose, confident or not: a hand sliding off the side
        // of the view usually loses confidence before it disappears, and that is exactly
        // the loss worth explaining.
        FrameEdge edges = pose is not null && result is not null
            ? FrameFit.Edges(pose, result.FrameWidth, result.FrameHeight)
            : FrameEdge.None;

        NoteVisibility(pose is not null, edges);
        UpdateIdleSkipPrompt(pose is not null);

        if (_mode == Mode.Testing)
        {
            ShowTestPointer(usable ? metric : null);
            UpdateReadouts(pose, usable, null);
            return;
        }

        string? blocked = null;

        if (_mode == Mode.Capturing && usable)
        {
            blocked = Record(pose!, metric, edges, hand!.Gesture.State);
            if (blocked is null && IsCurrentStepComplete())
            {
                CompleteStep();
                return;
            }
        }

        UpdateReadouts(pose, usable, blocked);
    }

    private string? Record(HandPose pose, Vector2 metric, FrameEdge edges, GestureState state)
    {
        switch (Current)
        {
            case PoseStep when _recordingPose is not null:
                // Pose steps read the hand's shape, which needs no position at all — but
                // it does need all of the hand. Past the border the fingers are the model's
                // guess, and a guessed fist is how thresholds end up in the wrong place.
                if (edges != FrameEdge.None)
                {
                    return $"hand at the {SideText(edges)} edge of the camera's view — move toward the centre";
                }

                _recordingPose.Add(pose);
                return null;

            case IdleStep when _recordingIdle is not null:
                // A resting hand half out of shot is still a resting hand; where it enters
                // the frame is exactly what the idle zone should record.
                _recordingIdle.Offer(metric);
                return null;

            case PointStep when _recordingPoint is not null:
                return _recordingPoint.Offer(metric, edges, state, _spaceHeld);

            case SweepStep when _recordingSweep is not null:
                _recordingSweep.Offer(metric, edges);
                return null;

            default:
                return null;
        }
    }

    private bool IsCurrentStepComplete() => Current switch
    {
        PoseStep => _recordingPose!.Openness.Count >= SamplesPerPose,
        IdleStep => _recordingIdle!.IsComplete,
        PointStep => _recordingPoint!.IsComplete,
        SweepStep => _recordingSweep!.IsComplete,
        _ => false,
    };

    /// <summary>Remembers which frame border, if any, the hand was touching when it was lost.</summary>
    private void NoteVisibility(bool visible, FrameEdge edges)
    {
        if (visible)
        {
            _lastEdges = edges;
            _lostAtEdges = FrameEdge.None;
        }
        else if (_handWasVisible)
        {
            _lostAtEdges = _lastEdges;
        }

        _handWasVisible = visible;
    }

    /// <summary>
    /// On the idle step, points at the skip button once no hand has been seen for a while.
    /// </summary>
    private void UpdateIdleSkipPrompt(bool handVisible)
    {
        if (_mode != Mode.Capturing || Current is not IdleStep)
        {
            return;
        }

        if (handVisible)
        {
            _idleQuietSince = Stopwatch.GetTimestamp();
        }

        bool quiet = Stopwatch.GetElapsedTime(_idleQuietSince) >= IdleSkipPrompt;
        if (quiet == _idleSkipPromptShown)
        {
            return;
        }

        _idleSkipPromptShown = quiet;
        StatusText.Text = quiet
            ? $"No hand seen for {IdleSkipPrompt.TotalSeconds:0} s. If your hands rest out of view, choose \"My hands are out of frame\"."
            : IdleCapturingHint;
    }

    /// <summary>
    /// Names frame borders the way the user sees them.
    /// </summary>
    /// <remarks>
    /// The preview is mirrored (<c>ScaleX="-1"</c> in the XAML), so the camera frame's left
    /// border is on the right of what the user watches — and it is also their physical
    /// right. Naming the camera's side would send them the wrong way every time.
    /// </remarks>
    private static string SideText(FrameEdge edges)
    {
        List<string> sides = [];

        if ((edges & FrameEdge.Top) != 0)
        {
            sides.Add("top");
        }

        if ((edges & FrameEdge.Bottom) != 0)
        {
            sides.Add("bottom");
        }

        if ((edges & FrameEdge.Right) != 0)
        {
            sides.Add("left");
        }

        if ((edges & FrameEdge.Left) != 0)
        {
            sides.Add("right");
        }

        return string.Join(" and ", sides);
    }

    private void UpdateReadouts(HandPose? pose, bool usable, string? blocked)
    {
        if (_mode is Mode.Finished or Mode.Failed)
        {
            return;
        }

        if (pose is null && Current is IdleStep && _mode is Mode.Ready or Mode.Capturing)
        {
            // Not a warning here: hands resting out of shot is one of the two right answers.
            TrackingText.Text = "no hand in view";
            TrackingText.Foreground = (Brush)FindResource("Muted");
        }
        else if (pose is null)
        {
            TrackingText.Text = _lostAtEdges == FrameEdge.None
                ? "no hand detected — move into frame"
                : $"hand left the camera's view at the {SideText(_lostAtEdges)} edge"
                  + (_mode == Mode.Capturing && Current is PointStep or SweepStep
                      ? LostHandAdvice(_lostAtEdges)
                      : string.Empty);

            TrackingText.Foreground = (Brush)FindResource("Warn");
        }
        else
        {
            TrackingText.Text =
                $"openness {HandMetrics.Openness(pose),5:0.00}   "
                + $"view {HandMetrics.ViewAxisAlignment(pose),5:0.00}   "
                + $"confidence {pose.Confidence,5:0.00}"
                + (usable ? string.Empty : "   — too uncertain to measure")
                + (blocked is null ? string.Empty : $"   — {blocked}");

            TrackingText.Foreground = (Brush)FindResource(usable && blocked is null ? "Muted" : "Warn");
        }

        if (_mode != Mode.Capturing)
        {
            return;
        }

        (double progress, string counter) = Current switch
        {
            PoseStep => (_recordingPose!.Openness.Count / (double)SamplesPerPose,
                $"{_recordingPose.Openness.Count} / {SamplesPerPose}"),
            IdleStep => (_recordingIdle!.Progress, $"{_recordingIdle.Count} / {_recordingIdle.Target}"),
            PointStep => (_recordingPoint!.Progress, $"{_recordingPoint.Count} / {_recordingPoint.Target}"),
            SweepStep => (_recordingSweep!.Progress,
                $"{_recordingSweep.Extent.X * 100:0} x {_recordingSweep.Extent.Y * 100:0} cm"),
            _ => (0.0, string.Empty),
        };

        CaptureProgress.Value = progress;
        SampleCountText.Text = counter;
    }

    /// <summary>What to do about a hand lost at the edge of the view while reaching.</summary>
    /// <remarks>
    /// In side-to-side mode a hand lost off the top or bottom is not reaching too far — height
    /// is not being measured — and "reach less far" would send the user chasing the wrong
    /// fix. The right one is simply to bring the hand back into view at any height.
    /// </remarks>
    private string LostHandAdvice(FrameEdge edges) =>
        _reachMode == ReachMode.SideToSide && (edges & (FrameEdge.Left | FrameEdge.Right)) == 0
            ? " — raise or lower your hand into view; height doesn't matter here"
            : " — reach less far, or aim the camera toward your working area";

    private void ShowTestPointer(Vector2? metric)
    {
        if (_overlay is null || _testMapping is null)
        {
            return;
        }

        _overlay.SetPointer(metric is { } value ? _testMapping.Map(value) : ScreenPoint.Nothing);
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
        _recordingPose = null;
        _recordingIdle = null;
        _recordingPoint = null;
        _recordingSweep = null;

        WizardStep step = Current;
        StepLabel.Text = StepLabelFor(index);
        PromptText.Text = step.Prompt;
        HintText.Text = step.Hint;

        CaptureProgress.Value = 0;
        SampleCountText.Text = string.Empty;
        StatusText.Text = "Get into position, then start. There is no time limit.";

        PrimaryButton.Content = "Start capturing";
        PrimaryButton.IsEnabled = true;
        ShowIdleSkip(step is IdleStep);

        // Only corners are confirmed. The idle step in particular must never ask for a
        // grab: nobody makes a fist to rest.
        ConfirmationPanel.Visibility = step is PointStep
            ? Visibility.Visible
            : Visibility.Collapsed;

        // The mode decides which reach steps follow, so it can change only before the first
        // of them. Pose and idle steps always come first; after that the captured points
        // belong to one mode and switching would strand them.
        ReachPanel.Visibility = step is PoseStep or IdleStep
            ? Visibility.Visible
            : Visibility.Collapsed;

        UpdateOverlayForStep(step);
    }

    private string StepLabelFor(int index) => $"Step {index + 1} of {_steps.Length} — {_steps[index].Title}";

    private void UpdateOverlayForStep(WizardStep step)
    {
        if (step is PoseStep)
        {
            _overlay?.Hide();
            return;
        }

        _overlay ??= new CalibrationTargetOverlay();
        _overlay.ShowOn(_monitor!);
        _overlay.SetPointer(ScreenPoint.Nothing);
        _overlay.SetTargets(ReachSpots());
        _overlay.SetTarget((step as PointStep)?.Target);
        _overlay.SetCaption(step switch
        {
            SweepStep when _reachMode == ReachMode.SideToSide => "Sweep as far left and right as you can",
            SweepStep => "Sweep your whole reach",
            IdleStep => "Relax your arm",
            _ => "Reach to the highlighted marker",
        });
    }

    /// <summary>The marks to draw, taken from the steps so the two can never disagree.</summary>
    private Spot[] ReachSpots() => [.. _steps.OfType<PointStep>().Select(step => step.Target)];

    private void ShowIdleSkip(bool visible)
    {
        SecondaryButton.Content = IdleSkipLabel;
        SecondaryButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartCapturing()
    {
        _mode = Mode.Capturing;

        switch (Current)
        {
            case PoseStep pose:
                _recordingPose = new CalibrationPhase(pose.Title, [], []);
                _poses[pose.Key] = _recordingPose;
                break;

            case IdleStep:
                _recordingIdle = new IdleCapture();
                _idleQuietSince = Stopwatch.GetTimestamp();
                _idleSkipPromptShown = false;
                break;

            case PointStep:
                _recordingPoint = new PointCapture(Confirmation);
                break;

            case SweepStep:
                _recordingSweep = new SweepCapture(requireVertical: _reachMode == ReachMode.FullScreen);
                break;
        }

        StatusText.Text = Current switch
        {
            PointStep => ConfirmationHint(),
            IdleStep => IdleCapturingHint,
            _ => "Hold the pose. The bar only fills while your hand is tracked.",
        };

        PrimaryButton.Content = "Cancel step";

        // The skip stays available mid-capture: finding out that the camera cannot see
        // your resting hands is usually what capturing reveals.
        ShowIdleSkip(Current is IdleStep);
    }

    private string ConfirmationHint() => Confirmation switch
    {
        CornerConfirmation.GrabAndHold => "Reach to the marker, close your hand and hold.",
        CornerConfirmation.HoverDwell => "Reach to the marker and hold still.",
        _ => "Reach to the marker, then press and hold space.",
    };

    private void CompleteStep()
    {
        _mode = Mode.StepComplete;

        switch (Current)
        {
            case IdleStep:
                // No capture means the skip was chosen: the hands rest out of view.
                _idle = _recordingIdle?.Result;
                break;

            case PointStep point:
                _corners.Add((point.Target, _recordingPoint!.Result, _recordingPoint.EdgeContact));
                break;

            case SweepStep:
                _reach = _recordingSweep;
                break;
        }

        CaptureProgress.Value = 1;
        StatusText.Text = Current is IdleStep && _idle is null
            ? "Recorded: your hands rest out of the camera's view."
            : "Captured.";

        bool isLast = _stepIndex == _steps.Length - 1;
        PrimaryButton.Content = isLast ? "See results" : "Next step";
        SecondaryButton.Content = "Redo this step";
        SecondaryButton.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Fits the thresholds and applies them immediately.
    /// </summary>
    /// <remarks>
    /// The spatial steps can confirm a corner with a grab, which only works if the
    /// tracker already knows what this person's grab looks like. Rebuilding the worker
    /// with the freshly fitted options is what makes that true.
    /// </remarks>
    private bool ApplyThresholds()
    {
        (GestureProfile? profile, string? problem) = GrabCalibration.Fit(
            _poses["open"], _poses["closed"], _poses["pointing"]);

        if (profile is null)
        {
            ShowFailure("Could not fit thresholds", problem);
            return false;
        }

        _fittedGesture = profile;

        _worker?.Dispose();
        _worker = new HandTrackingWorker(_tracker!, profile.ToOptions());
        return true;
    }

    private void Finish()
    {
        (MonitorMapping? mapping, string? explanation, string? problem) =
            SpatialCalibration.Fit(FitPoints(), _idle, _monitor!);

        if (mapping is null)
        {
            ShowFailure("Could not fit the screen mapping", problem);
            return;
        }

        _fittedMapping = mapping;
        _testMapping = mapping.ToMapping();
        _mode = Mode.Finished;

        string? edgeWarning = DescribeCameraEdges();
        Vector2 reach = _reach?.Extent ?? Vector2.Zero;

        // The sweep's height is the camera's limit, not the arm's, when vertical was never asked for.
        string reachSize = _reachMode == ReachMode.SideToSide
            ? $"{reach.X * 100:0} cm wide"
            : $"{reach.X * 100:0} x {reach.Y * 100:0} cm";

        StepLabel.Text = "Calibration complete";
        PromptText.Text = "Your profile";
        HintText.Text =
            $"Grab below {_fittedGesture!.GrabOpenness:0.00}, release above "
            + $"{_fittedGesture.ReleaseOpenness:0.00}, no grab past view "
            + $"{_fittedGesture.MaxViewAxisAlignment:0.00}.\n"
            + $"Working area {WorkingAreaDescription()} mapped to {_monitor!.Description}, "
            + $"inside a {reachSize} maximum reach.\n"
            + $"{explanation}\n"
            + SpatialCalibration.DescribeIdle(mapping)
            + (edgeWarning is null ? string.Empty : $"\n\n{edgeWarning}");

        TrackingText.Text = string.Empty;
        SampleCountText.Text = string.Empty;
        StatusText.Text = string.Empty;
        CaptureProgress.Value = 0;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        ReachPanel.Visibility = Visibility.Collapsed;

        PrimaryButton.Content = "Save profile";
        SecondaryButton.Content = "Test the mapping";
        SecondaryButton.Visibility = Visibility.Visible;

        _overlay?.SetTarget(null);
        _overlay?.SetCaption("Calibration complete");
    }

    /// <summary>The captured points in the order the fit expects.</summary>
    /// <remarks>
    /// Joined by name rather than taken in capture order, so the fit's corner order holds
    /// however steps were redone. A missing point leaves the list short, which the fit
    /// refuses by count with an explanation rather than a crash here.
    /// </remarks>
    private Vector2[] FitPoints()
    {
        Spot[] order = _reachMode switch
        {
            ReachMode.FullScreen => [Spot.TopLeft, Spot.TopRight, Spot.BottomRight, Spot.BottomLeft],
            ReachMode.SideToSide => [Spot.MidLeft, Spot.MidRight],
        };

        return [.. order.Join(_corners, spot => spot, captured => captured.Target, (_, captured) => captured.Position)];
    }

    private string WorkingAreaDescription()
    {
        if (_corners.Count == 0)
        {
            return "unknown";
        }

        float width = _corners.Max(c => c.Position.X) - _corners.Min(c => c.Position.X);
        if (_reachMode == ReachMode.SideToSide)
        {
            return $"{width * 100:0} cm across (height not measured)";
        }

        float height = _corners.Max(c => c.Position.Y) - _corners.Min(c => c.Position.Y);
        return $"{width * 100:0} x {height * 100:0} cm";
    }

    private static string SpotName(Spot spot) => spot switch
    {
        Spot.TopLeft => "top-left corner",
        Spot.TopRight => "top-right corner",
        Spot.BottomRight => "bottom-right corner",
        Spot.BottomLeft => "bottom-left corner",
        Spot.MidLeft => "left point",
        Spot.MidRight => "right point",
    };

    /// <summary>The frame borders worth warning about in the current mode.</summary>
    /// <remarks>
    /// In side-to-side mode the top and bottom of the view are expected limits — the mode
    /// exists because the camera cannot see vertical reach — so warning about them would
    /// report the premise as a fault.
    /// </remarks>
    private FrameEdge Relevant(FrameEdge edges) =>
        _reachMode == ReachMode.SideToSide ? edges & (FrameEdge.Left | FrameEdge.Right) : edges;

    /// <summary>
    /// Names every measurement that was taken with the hand at the edge of the camera's view.
    /// </summary>
    /// <remarks>
    /// Nothing assumes the working area sits in the middle of the frame, so a corner or
    /// the reach sweep can run off the side of the view. The numbers look just as
    /// plausible either way; only the edge contact says the camera, not the arm, set them.
    /// </remarks>
    private string? DescribeCameraEdges()
    {
        List<string> lines = [];

        foreach ((Spot target, _, FrameEdge captured) in _corners)
        {
            FrameEdge edges = Relevant(captured);
            if (edges != FrameEdge.None)
            {
                lines.Add($"The {SpotName(target)} was captured at the {SideText(edges)} edge "
                    + "of the camera's view, so it may be off. Redo it, or aim the camera toward your working area.");
            }
        }

        FrameEdge clipped = _reach is null ? FrameEdge.None : Relevant(_reach.ClippedSides);
        if (clipped != FrameEdge.None)
        {
            lines.Add($"Your maximum reach is cut off by the camera on the {SideText(clipped)}: "
                + "there it is the camera's limit, not your arm's.");
        }

        return lines.Count == 0 ? null : string.Join("\n", lines);
    }

    /// <summary>Shows the live mapped pointer over the corner targets.</summary>
    private void BeginTest()
    {
        _mode = Mode.Testing;

        StepLabel.Text = "Testing the mapping";

        if (_testMapping?.HasVertical == false)
        {
            PromptText.Text = "Move your hand side to side and watch the band";
            HintText.Text =
                "Only left-to-right is mapped, so the pointer is a band the height of the screen rather than a dot. "
                + "Reaching toward a marker should put the band on it. Lean toward the camera and back — the band "
                + "should stay where it is. Hold your hand still: the band should stay within about a window's width.";
        }
        else
        {
            PromptText.Text = "Move your hand and watch the dot";
            HintText.Text =
                "Reaching toward a marker should put the dot on it. Lean toward the camera and back — "
                + "the dot should stay where it is, because position is measured in metres rather than pixels.";
        }

        StatusText.Text = string.Empty;
        PrimaryButton.Content = "Save profile";
        SecondaryButton.Content = "Back to results";

        _overlay?.ShowOn(_monitor!);
        _overlay?.SetTarget(null);
        _overlay?.SetCaption(string.Empty);
    }

    private void OnPrimaryClick(object sender, RoutedEventArgs e)
    {
        switch (_mode)
        {
            case Mode.Ready:
                StartCapturing();
                break;

            case Mode.Capturing:
                BeginStep(_stepIndex);
                break;

            case Mode.StepComplete when IsLastPoseStep:
                // Last pose step: fit and apply the thresholds before any grab is asked for.
                if (ApplyThresholds())
                {
                    Advance();
                }

                break;

            case Mode.StepComplete:
                Advance();
                break;

            case Mode.Testing:
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
            case Mode.Ready or Mode.Capturing when Current is IdleStep:
                // Hands resting out of shot: record that, keeping nothing half-captured.
                _recordingIdle = null;
                CompleteStep();
                break;

            case Mode.StepComplete:
                // Drop whatever the step just recorded and take it again. By target, not
                // "the last one added": that holds only while redo is reachable solely from
                // the step that was just completed.
                if (Current is PointStep redone)
                {
                    _corners.RemoveAll(captured => captured.Target == redone.Target);
                }

                if (Current is IdleStep)
                {
                    _idle = null;
                }

                BeginStep(_stepIndex);
                break;

            case Mode.Finished:
                BeginTest();
                break;

            case Mode.Testing:
                _overlay?.SetPointer(ScreenPoint.Nothing);
                Finish();
                break;
        }
    }

    /// <summary>Whether this is the pose step after which the thresholds must be fitted.</summary>
    /// <remarks>
    /// Bounds-checked rather than peeking blindly at the next step, now that the list is
    /// built per mode. Guarding only the peek would be worse than the crash it prevents:
    /// the arm would stop matching, and a trailing pose step would fall through to
    /// <see cref="Finish"/> without the thresholds ever being fitted.
    /// </remarks>
    private bool IsLastPoseStep =>
        Current is PoseStep && (_stepIndex + 1 >= _steps.Length || _steps[_stepIndex + 1] is not PoseStep);

    private void Advance()
    {
        if (_stepIndex == _steps.Length - 1)
        {
            Finish();
        }
        else
        {
            BeginStep(_stepIndex + 1);
        }
    }

    private void OnConfirmationModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_mode == Mode.Capturing && Current is PointStep)
        {
            // Switching mid-capture invalidates what was collected under the old rule.
            _recordingPoint = new PointCapture(Confirmation);
            StatusText.Text = ConfirmationHint();
        }
    }

    private void OnReachModeChanged(object sender, SelectionChangedEventArgs e)
    {
        // Also fires from OnLoaded setting the initial selection, when the steps already
        // match it; and would fire before OnLoaded if anything selected earlier.
        if (_monitor is null || _steps.Length == 0 || ReachModeBox.SelectedIndex < 0)
        {
            return;
        }

        var chosen = (ReachMode)ReachModeBox.SelectedIndex;
        if (chosen == _reachMode)
        {
            return;
        }

        // Only reachable while the selector is shown, which is before the first reach step,
        // so no captured point belongs to the old mode. The mode changes only the tail of the
        // list, so the current index stays valid.
        _reachMode = chosen;
        _steps = BuildSteps(_monitor, _reachMode);
        StepLabel.Text = StepLabelFor(_stepIndex);

        if (Current is not PoseStep)
        {
            UpdateOverlayForStep(Current);
        }
    }

    private void RestartAll()
    {
        _poses.Clear();
        _corners.Clear();
        _idle = null;
        _fittedGesture = null;
        _fittedMapping = null;
        _testMapping = null;
        _reach = null;
        BeginStep(0);
    }

    private void SaveAndClose()
    {
        try
        {
            _fittedGesture?.Save(_gesturePath);

            if (_fittedMapping is not null)
            {
                SpatialProfile profile = SpatialProfile.Load(_spatialPath) ?? new SpatialProfile();
                profile = profile.With(_fittedMapping);

                if (_reach is { Count: > 0 })
                {
                    profile = profile with
                    {
                        MaxReachMin = Point2.From(_reach.Min),
                        MaxReachMax = Point2.From(_reach.Max),
                    };
                }

                profile.Save(_spatialPath);
            }

            Close();
        }
        catch (Exception exception)
        {
            StatusText.Text = $"Could not save: {exception.Message}";
        }
    }

    private void ShowFailure(string title, string? detail)
    {
        _mode = Mode.Failed;
        StepLabel.Text = "Calibration incomplete";
        PromptText.Text = title;
        HintText.Text = detail ?? "Unknown problem.";
        StatusText.Text = string.Empty;
        CaptureProgress.Value = 0;
        ConfirmationPanel.Visibility = Visibility.Collapsed;
        ReachPanel.Visibility = Visibility.Collapsed;
        PrimaryButton.Content = "Start over";
        PrimaryButton.IsEnabled = true;
        SecondaryButton.Visibility = Visibility.Collapsed;
        _overlay?.Hide();
    }

    private void Fail(string message)
    {
        ShowFailure("Cannot start", message);
        PrimaryButton.Content = "Close";
        PrimaryButton.Click -= OnPrimaryClick;
        PrimaryButton.Click += (_, _) => Close();
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

        _overlay?.Close();
        _overlay = null;

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

    private abstract record WizardStep(string Title, string Prompt, string Hint);

    private sealed record PoseStep(string Key, string Title, string Prompt, string Hint)
        : WizardStep(Title, Prompt, Hint);

    private sealed record IdleStep(string Title, string Prompt, string Hint)
        : WizardStep(Title, Prompt, Hint);

    private sealed record PointStep(Spot Target, string Title, string Prompt, string Hint)
        : WizardStep(Title, Prompt, Hint);

    private sealed record SweepStep(string Title, string Prompt, string Hint)
        : WizardStep(Title, Prompt, Hint);
}
