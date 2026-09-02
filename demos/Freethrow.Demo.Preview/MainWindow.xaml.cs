using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Freethrow.Core.Capture;
using Freethrow.Core.Diagnostics;
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
    private FrameRef? _pendingFrame;
    private WriteableBitmap? _bitmap;
    private bool _isStarting;

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

    private void OnMirrorChanged(object sender, RoutedEventArgs e)
    {
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

        StatusText.Text =
            $"{source.ActiveFormat}   capture {_captureRate.PerSecond,5:0.0} fps   "
            + $"display {_renderRate.PerSecond,5:0.0} fps   "
            + $"latency {_latency.Value,5:0.0} ms (max {_latency.Max:0.0})   "
            + $"delivered {source.FramesDelivered}   dropped {source.FramesDropped}   "
            + $"working set {Environment.WorkingSet / (1024.0 * 1024.0):0} MB";
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
