namespace Freethrow.Core.Capture;

/// <summary>Carries a frame to <see cref="ICameraSource.FrameArrived"/> subscribers.</summary>
public sealed class FrameEventArgs(FrameRef frame) : EventArgs
{
    /// <summary>
    /// The frame. Valid only for the duration of the handler — call
    /// <see cref="FrameRef.Retain"/> to keep it beyond the callback.
    /// </summary>
    public FrameRef Frame { get; } = frame;
}

/// <summary>
/// An open camera delivering frames. Implementations live in the platform layer;
/// the pipeline only ever sees this interface, which is what lets fixtures be
/// replayed from disk in tests and what leaves room for one-camera-per-monitor later.
/// </summary>
public interface ICameraSource : IAsyncDisposable
{
    /// <summary>The device this source was opened from.</summary>
    CameraDeviceInfo Device { get; }

    /// <summary>The format actually negotiated, which may differ from what was requested.</summary>
    CameraFormat ActiveFormat { get; }

    /// <summary>Every format the device offers, for diagnostics and format selection.</summary>
    IReadOnlyList<CameraFormat> SupportedFormats { get; }

    /// <summary>Whether frames are currently flowing.</summary>
    bool IsRunning { get; }

    /// <summary>Frames successfully delivered to subscribers.</summary>
    long FramesDelivered { get; }

    /// <summary>
    /// Frames the backend produced but that never reached subscribers — dropped because
    /// the pipeline fell behind, or unusable. A rising count means the consumer is too slow.
    /// </summary>
    long FramesDropped { get; }

    /// <summary>
    /// Why the most recent frame was dropped, or <see langword="null"/> if none has been.
    /// </summary>
    /// <remarks>
    /// A camera that opens, reports a healthy format and then silently delivers nothing
    /// is the single most confusing failure in this layer, because every visible signal
    /// says success. Carrying the reason turns that into a one-line answer.
    /// </remarks>
    string? LastDropReason { get; }

    /// <summary>
    /// Raised on a background thread for each frame. Handlers must be quick and must not
    /// block; see the lifetime contract on <see cref="FrameRef"/>.
    /// </summary>
    event EventHandler<FrameEventArgs>? FrameArrived;

    /// <summary>Begins streaming.</summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>Stops streaming. The source can be started again.</summary>
    Task StopAsync();
}

/// <summary>Preferences applied when opening a camera. Treated as hints, not requirements.</summary>
public sealed record CameraOpenOptions
{
    /// <summary>
    /// 640x480 at 30 fps is the pipeline default: enough resolution for hand landmarks
    /// at arm's length, small enough to keep inference and copies cheap.
    /// </summary>
    public static CameraOpenOptions Default { get; } = new();

    public int PreferredWidth { get; init; } = 640;

    public int PreferredHeight { get; init; } = 480;

    public double PreferredFrameRate { get; init; } = 30;
}

/// <summary>Discovers cameras and opens them.</summary>
public interface ICameraEnumerator
{
    /// <summary>Lists available camera sources, including infrared ones where present.</summary>
    Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(CancellationToken cancellationToken = default);

    /// <summary>Opens a device. The returned source is not yet streaming.</summary>
    Task<ICameraSource> OpenAsync(
        CameraDeviceInfo device,
        CameraOpenOptions? options = null,
        CancellationToken cancellationToken = default);
}
