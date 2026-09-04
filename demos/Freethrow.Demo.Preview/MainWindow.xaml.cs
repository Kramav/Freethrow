using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Freethrow.Core.Capture;
using Freethrow.Core.Config;
using Freethrow.Core.Diagnostics;
using Freethrow.Core.Gestures;
using Freethrow.Core.Perception;
using Freethrow.Core.Perception.Onnx;
using Freethrow.Desktop.Capture;

namespace Freethrow.Demo.Preview;

/// <summary>
/// Live camera preview with capture telemetry.
/// </summary>
/// <remarks>
/// The frame path here is deliberately a single-slot mailbox rather than a queue.
/// Frames arrive faster than WPF composes, and a queue would grow without bound while
/// showing progressively staler images — the worst of both. Keeping only the newest
/// frame means the preview is always current and the pooled buffers go straight back.
/// </remarks>
public partial class MainWindow : Window
{
    private readonly WindowsCameraEnumerator _enumerator = new();
    private readonly DispatcherTimer _statusTimer;
    private readonly RateCounter _captureRate = new();
    private readonly RateCounter _renderRate = new();
    private readonly MovingAverage _latency = new();
    private readonly object _pendingGate = new();

    private ICameraSource? _source;
    private IHandTracker? _tracker;
    private HandTrackingWorker? _worker;
    private FrameRef? _pendingFrame;
    private WriteableBitmap? _bitmap;
    private bool _isStarting;
    private string? _trackingUnavailable;

    public MainWindow()
    {
        InitializeComponent();

        _statusTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250),
        };
        _statusTimer.Tick += (_, _) => UpdateStatus();

        Loaded += OnLoaded;
        Closing += OnClosing;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            IReadOnlyList<CameraDeviceInfo> devices = await _enumerator.EnumerateAsync();
            DeviceSelector.ItemsSource = devices;

            if (devices.Count == 0)
            {
                StatusText.Text = "No cameras found.";
                StartStopButton.IsEnabled = false;
                return;
            }

            // Default to a colour camera; infrared is an enhancement, not the primary sensor.
            CameraDeviceInfo preferred =
                devices.FirstOrDefault(d => d.Kind == CameraKind.Color) ?? devices[0];
            DeviceSelector.SelectedItem = preferred;

            // A preview tool that opens to a black rectangle and waits to be told to
            // preview is just a chore. Start streaming immediately.
            await StartCaptureAsync(preferred);
        }
        catch (Exception ex)
        {
            StatusText.Text = Describe(ex);
        }
    }

    private async void OnStartStopClick(object sender, RoutedEventArgs e)
    {
        if (_isStarting)
        {
            return;
        }

        if (_source is not null)
        {
            await StopAsync();
            return;
        }

        if (DeviceSelector.SelectedItem is CameraDeviceInfo device)
        {
            await StartCaptureAsync(device);
        }
    }

    private async Task StartCaptureAsync(CameraDeviceInfo device)
    {
        _isStarting = true;
        StartStopButton.IsEnabled = false;
        DeviceSelector.IsEnabled = false;

        try
        {
            StartTracking();

            ICameraSource source = await _enumerator.OpenAsync(device);
            source.FrameArrived += OnFrameArrived;
            await source.StartAsync();

            _source = source;
            _captureRate.Reset();
            _renderRate.Reset();
            _latency.Reset();

            CompositionTarget.Rendering += OnRendering;
            _statusTimer.Start();

            StartStopButton.Content = "Stop";
        }
        catch (Exception ex)
        {
            StatusText.Text = Describe(ex);
            DeviceSelector.IsEnabled = true;
        }
        finally
        {
            _isStarting = false;
            StartStopButton.IsEnabled = true;
        }
    }

    /// <summary>
    /// Brings up hand tracking, or explains why it is unavailable.
    /// </summary>
    /// <remarks>
    /// Missing models degrade the preview to plain video rather than preventing it from
    /// opening: capture is worth checking on a machine even when the models have not
    /// been downloaded yet.
    /// </remarks>
    private void StartTracking()
    {
        try
        {
            _tracker = OnnxHandTracker.Create();

            // Prefer thresholds fitted to this person's hand; the built-in defaults come
            // from a single measured hand and hands vary more than the gap between the
            // grab and release thresholds.
            _worker = new HandTrackingWorker(_tracker, GestureProfile.LoadOptionsOrDefault());

            _trackingUnavailable = null;
        }
        catch (FileNotFoundException ex)
        {
            _trackingUnavailable = ex.Message;
        }
        catch (Exception ex)
        {
            _trackingUnavailable = $"Hand tracking failed to start: {ex.Message}";
        }
    }

    private void OnMirrorChanged(object sender, RoutedEventArgs e)
    {
        // Fires during XAML parsing when IsChecked="True" is applied, which happens
        // before the transform further down the tree has been assigned to its field.
        if (MirrorTransform is null)
        {
            return;
        }

        // Cameras face the user, so an unmirrored preview reverses every movement and
        // makes reaching toward a target feel wrong. Mirrored is the honest default.
        MirrorTransform.ScaleX = MirrorToggle.IsChecked == true ? -1 : 1;
    }

    /// <summary>
    /// Runs on a capture thread. Retains the newest frame and releases whatever it
    /// displaced, so at most one frame is ever held outside the pool.
    /// </summary>
    private void OnFrameArrived(object? sender, FrameEventArgs e)
    {
        _captureRate.Tick();
        _latency.Add(e.Frame.AgeMilliseconds);

        // The worker retains the frame itself if it wants it, and drops it if it is
        // still busy — so a slow tracker never slows down capture or display.
        _worker?.Submit(e.Frame);

        FrameRef retained = e.Frame.Retain();
        FrameRef? displaced;

        lock (_pendingGate)
        {
            displaced = _pendingFrame;
            _pendingFrame = retained;
        }

        displaced?.Dispose();
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        FrameRef? frame;
        lock (_pendingGate)
        {
            frame = _pendingFrame;
            _pendingFrame = null;
        }

        if (frame is null)
        {
            return;
        }

        using (frame)
        {
            try
            {
                Blit(frame);
                _renderRate.Tick();
            }
            catch (Exception ex)
            {
                StatusText.Text = Describe(ex);
            }
        }

        // Drawn from the latest available result rather than in step with inference:
        // tracking runs slower than display, and the skeleton should simply persist on
        // the frames between results instead of flickering.
        HandTrackingResult? result = _worker?.Latest;

        if (result is null)
        {
            Skeleton.Clear();
            return;
        }

        Skeleton.Show(
            [.. result.Hands.Select(hand => new HandRender(
                hand.Pose,
                hand.Gesture.State,
                hand.Gesture.IsArmingBlocked,
                hand.Id == result.ControllingId,
                hand.Id == result.HoverId))],
            result.FrameWidth,
            result.FrameHeight);
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

    private void UpdateStatus()
    {
        ICameraSource? source = _source;
        if (source is null)
        {
            return;
        }

        string capture =
            $"{source.ActiveFormat}  capture {_captureRate.PerSecond,5:0.0} fps  "
            + $"display {_renderRate.PerSecond,5:0.0} fps  "
            + $"latency {_latency.Value,4:0.0} ms  "
            + $"dropped {source.FramesDropped}  "
            + $"{Environment.WorkingSet / (1024.0 * 1024.0):0} MB";

        if (_trackingUnavailable is { } unavailable)
        {
            StatusText.Text = $"{capture}\n{unavailable}";
            return;
        }

        if (_worker is not { } worker || _tracker is not { } tracker)
        {
            StatusText.Text = capture;
            return;
        }

        HandTrackingResult? result = worker.Latest;

        string perHand = result is null || result.Hands.Count == 0
            ? "no hands"
            : string.Join("   ", result.Hands.Select(hand => Describe(hand, result)));

        StatusText.Text = capture + "\n"
            + perHand + "\n"
            + $"hands {result?.Hands.Count ?? 0}  "
            + $"tracked {worker.HandRate * 100,5:0.0}%  "
            + $"inference {worker.InferenceMilliseconds,4:0.0} ms  "
            + $"runs {tracker.DetectionRuns} detect / {tracker.TrackingRuns} track";

        static string Describe(TrackedHand hand, HandTrackingResult result)
        {
            // The role matters more than the raw numbers when two hands are up: it says
            // which one the system is actually listening to.
            string role = hand.Id == result.ControllingId ? "HOLDING"
                : hand.Id == result.HoverId ? "pointing"
                : hand.Gesture.IsArmingBlocked ? "blocked"
                : "idle";

            return $"[{hand.Id} {hand.Pose.Handedness,-5} {role,-8} "
                + $"open {hand.Gesture.Openness,4:0.00} "
                + $"view {hand.Gesture.ViewAlignment,4:0.00} "
                + $"near {hand.DepthProxy,5:0}{(hand.Gesture.IsCoasting ? " coasting" : string.Empty)}]";
        }
    }

    private async Task StopAsync()
    {
        CompositionTarget.Rendering -= OnRendering;
        _statusTimer.Stop();

        ICameraSource? source = _source;
        _source = null;

        if (source is not null)
        {
            source.FrameArrived -= OnFrameArrived;
            await source.DisposeAsync();
        }

        // Stop the worker before the tracker it borrows, or the last in-flight frame
        // runs inference against a disposed ONNX session.
        _worker?.Dispose();
        _worker = null;
        _tracker?.Dispose();
        _tracker = null;
        Skeleton.Clear();

        FrameRef? pending;
        lock (_pendingGate)
        {
            pending = _pendingFrame;
            _pendingFrame = null;
        }

        pending?.Dispose();

        StartStopButton.Content = "Start";
        DeviceSelector.IsEnabled = true;
        StatusText.Text = "Stopped.";
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_source is null)
        {
            return;
        }

        // Let the camera shut down cleanly before the window goes away, otherwise the
        // capture thread can outlive the dispatcher and fault on the way out.
        e.Cancel = true;
        await StopAsync();
        Close();
    }

    private static string Describe(Exception exception) =>
        exception is CameraException ? exception.Message : exception.ToString();
}
