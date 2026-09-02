using System.Diagnostics;
using Freethrow.Core.Capture;
using Freethrow.Desktop.Interop;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;

namespace Freethrow.Desktop.Capture;

/// <summary>
/// An <see cref="ICameraSource"/> backed by WinRT <c>MediaFrameReader</c>.
/// </summary>
/// <remarks>
/// WinRT is used in preference to DirectShow or Media Foundation directly because it is
/// the only API that reaches infrared sources uniformly — the same code path serves a
/// colour webcam and a Windows Hello IR camera, which is what makes IR an opt-in
/// enhancement rather than a separate backend.
/// </remarks>
public sealed class MediaFrameCameraSource : ICameraSource
{
    private readonly MediaCapture _capture;
    private readonly MediaFrameSource _source;
    private readonly SemaphoreSlim _stateGate = new(1, 1);
    private readonly string? _preferredOutputSubtype;

    private MediaFrameReader? _reader;
    private long _sequence;
    private long _delivered;
    private long _dropped;
    private string? _lastDropReason;
    private bool _disposed;

    internal MediaFrameCameraSource(
        CameraDeviceInfo device,
        MediaCapture capture,
        MediaFrameSource source,
        string? preferredOutputSubtype,
        CameraFormat activeFormat,
        IReadOnlyList<CameraFormat> supportedFormats)
    {
        Device = device;
        _capture = capture;
        _source = source;
        _preferredOutputSubtype = preferredOutputSubtype;
        ActiveFormat = activeFormat;
        SupportedFormats = supportedFormats;
    }

    /// <inheritdoc />
    public event EventHandler<FrameEventArgs>? FrameArrived;

    /// <inheritdoc />
    public CameraDeviceInfo Device { get; }

    /// <inheritdoc />
    public CameraFormat ActiveFormat { get; private set; }

    /// <inheritdoc />
    public IReadOnlyList<CameraFormat> SupportedFormats { get; }

    /// <inheritdoc />
    public bool IsRunning => _reader is not null;

    /// <inheritdoc />
    public long FramesDelivered => Interlocked.Read(ref _delivered);

    /// <inheritdoc />
    public long FramesDropped => Interlocked.Read(ref _dropped);

    /// <inheritdoc />
    public string? LastDropReason => Volatile.Read(ref _lastDropReason);

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _stateGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_reader is not null)
            {
                return;
            }

            MediaFrameReader reader = await CreateReaderAsync().ConfigureAwait(false);
            reader.AcquisitionMode = MediaFrameReaderAcquisitionMode.Realtime;
            reader.FrameArrived += OnFrameArrived;

            MediaFrameReaderStartStatus status = await reader.StartAsync();
            if (status != MediaFrameReaderStartStatus.Success)
            {
                reader.FrameArrived -= OnFrameArrived;
                reader.Dispose();
                throw new CameraOpenException(
                    $"Could not start '{Device.DisplayName}': {status}.");
            }

            ActiveFormat = CameraFormatFactory.FromMediaFrameFormat(_source.CurrentFormat);
            _reader = reader;
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync()
    {
        await _stateGate.WaitAsync().ConfigureAwait(false);
        try
        {
            MediaFrameReader? reader = _reader;
            if (reader is null)
            {
                return;
            }

            _reader = null;
            reader.FrameArrived -= OnFrameArrived;
            await reader.StopAsync();
            reader.Dispose();
        }
        finally
        {
            _stateGate.Release();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync().ConfigureAwait(false);
        _capture.Dispose();
        _stateGate.Dispose();
    }

    /// <summary>
    /// Creates the reader, asking the platform to convert into a format the pipeline
    /// understands. Not every source can convert, so a refusal falls back to the native
    /// format and the per-frame copy handles the conversion instead.
    /// </summary>
    private async Task<MediaFrameReader> CreateReaderAsync()
    {
        if (_preferredOutputSubtype is not null)
        {
            try
            {
                return await _capture.CreateFrameReaderAsync(_source, _preferredOutputSubtype);
            }
            catch (Exception)
            {
                // Fall through to the native format.
            }
        }

        return await _capture.CreateFrameReaderAsync(_source);
    }

    private void OnFrameArrived(MediaFrameReader sender, MediaFrameArrivedEventArgs args)
    {
        EventHandler<FrameEventArgs>? handler = FrameArrived;

        using MediaFrameReference? reference = sender.TryAcquireLatestFrame();
        if (reference is null)
        {
            Drop("no frame was available to acquire");
            return;
        }

        VideoMediaFrame? videoFrame = reference.VideoMediaFrame;
        if (videoFrame is null)
        {
            Drop($"frame carried no video content (kind {reference.SourceKind})");
            return;
        }

        SoftwareBitmap? bitmap = videoFrame.SoftwareBitmap;
        if (bitmap is null)
        {
            Drop(videoFrame.Direct3DSurface is not null
                ? "frame arrived on the GPU despite the CPU memory preference"
                : "frame carried neither a software bitmap nor a Direct3D surface");
            return;
        }

        if (handler is null)
        {
            // Nobody is listening; not an error, but it is not a delivered frame either.
            return;
        }

        long sequence = Interlocked.Increment(ref _sequence);
        FrameRef? frame;
        try
        {
            frame = CopyToFrame(bitmap, sequence, ResolveCaptureTimestamp(reference));
        }
        catch (Exception ex)
        {
            Drop($"copy failed: {ex.GetType().Name}: {ex.Message}");
            return;
        }

        if (frame is null)
        {
            Drop("source buffer was smaller than its own plane description");
            return;
        }

        Interlocked.Increment(ref _delivered);
        using (frame)
        {
            handler(this, new FrameEventArgs(frame));
        }
    }

    /// <summary>
    /// Works out when the sensor actually produced the frame.
    /// </summary>
    /// <remarks>
    /// Timestamping on arrival would measure only this method against itself and always
    /// report a fraction of a millisecond, hiding every millisecond spent in the driver
    /// and the capture stack. <c>SystemRelativeTime</c> comes from the same QPC clock as
    /// <see cref="Stopwatch"/>, so the two are directly comparable and the reported
    /// latency becomes sensor-to-handler. Drivers that report some other timebase are
    /// caught by the plausibility check rather than trusted into nonsense figures.
    /// </remarks>
    private static long ResolveCaptureTimestamp(MediaFrameReference reference)
    {
        long now = Stopwatch.GetTimestamp();

        if (reference.SystemRelativeTime is not { } systemRelativeTime)
        {
            return now;
        }

        long candidate = (long)(systemRelativeTime.TotalSeconds * Stopwatch.Frequency);
        long age = now - candidate;

        return age >= 0 && age < Stopwatch.Frequency * 5 ? candidate : now;
    }

    private void Drop(string reason)
    {
        Interlocked.Increment(ref _dropped);
        Volatile.Write(ref _lastDropReason, reason);
    }

    /// <summary>
    /// Copies a locked <c>SoftwareBitmap</c> into a pooled frame, normalising the pixel
    /// format on the way through.
    /// </summary>
    private static unsafe FrameRef? CopyToFrame(SoftwareBitmap bitmap, long sequence, long timestamp)
    {
        SoftwareBitmap? converted = null;
        try
        {
            SoftwareBitmap working = bitmap;
            FramePixelFormat format;

            switch (bitmap.BitmapPixelFormat)
            {
                case BitmapPixelFormat.Bgra8:
                    format = FramePixelFormat.Bgra32;
                    break;

                case BitmapPixelFormat.Gray8:
                    format = FramePixelFormat.Gray8;
                    break;

                case BitmapPixelFormat.Gray16:
                    // Some IR cameras report 16-bit depth of illumination; the extra bits
                    // carry nothing the trackers use, so narrow to 8 during the copy.
                    format = FramePixelFormat.Gray8;
                    break;

                default:
                    // An unusual native format (NV12, YUY2, ...) that the reader would not
                    // convert. This allocates per frame, so it is a fallback, not a path.
                    converted = SoftwareBitmap.Convert(
                        bitmap,
                        BitmapPixelFormat.Bgra8,
                        BitmapAlphaMode.Premultiplied);
                    working = converted;
                    format = FramePixelFormat.Bgra32;
                    break;
            }

            bool narrowFromGray16 = working.BitmapPixelFormat == BitmapPixelFormat.Gray16;

            FrameRef frame = FrameRef.Rent(
                working.PixelWidth,
                working.PixelHeight,
                format,
                sequence,
                timestamp);

            try
            {
                using BitmapBuffer buffer = working.LockBuffer(BitmapBufferAccessMode.Read);
                using IMemoryBufferReference reference = buffer.CreateReference();
                BitmapPlaneDescription plane = buffer.GetPlaneDescription(0);

                MemoryBufferAccess.GetBuffer(reference, out byte* data, out uint capacity);

                long required = plane.StartIndex + ((long)plane.Stride * plane.Height);
                if (required > capacity)
                {
                    frame.Dispose();
                    return null;
                }

                Span<byte> destination = frame.Span;
                int rows = Math.Min(plane.Height, frame.Height);

                if (narrowFromGray16)
                {
                    int columns = Math.Min(plane.Width, frame.Width);
                    for (int y = 0; y < rows; y++)
                    {
                        ushort* sourceRow = (ushort*)(data + plane.StartIndex + ((long)y * plane.Stride));
                        Span<byte> destinationRow = destination.Slice(y * frame.Stride, frame.Stride);
                        for (int x = 0; x < columns; x++)
                        {
                            destinationRow[x] = (byte)(sourceRow[x] >> 8);
                        }
                    }
                }
                else
                {
                    int rowBytes = Math.Min(plane.Stride, frame.Stride);
                    for (int y = 0; y < rows; y++)
                    {
                        var sourceRow = new ReadOnlySpan<byte>(
                            data + plane.StartIndex + ((long)y * plane.Stride),
                            rowBytes);
                        sourceRow.CopyTo(destination.Slice(y * frame.Stride, rowBytes));
                    }
                }

                return frame;
            }
            catch (Exception)
            {
                frame.Dispose();
                throw;
            }
        }
        finally
        {
            converted?.Dispose();
        }
    }
}
