using System.Buffers;
using System.Diagnostics;

namespace Freethrow.Core.Capture;

/// <summary>
/// A single captured frame backed by a pooled buffer.
/// </summary>
/// <remarks>
/// <para>
/// Frames are the one thing in this pipeline large enough to matter: a 640x480 BGRA
/// frame is 1.2 MB, and at 30 fps that is 36 MB/s of garbage if allocated naively.
/// Buffers are therefore rented from an <see cref="ArrayPool{T}"/> and returned when
/// the last reference is released.
/// </para>
/// <para>
/// <b>Lifetime contract.</b> A frame handed to a <see cref="ICameraSource.FrameArrived"/>
/// handler is valid only for the duration of that callback. To keep it longer, call
/// <see cref="Retain"/> and <see cref="Dispose"/> when finished. Reading a frame after
/// its buffer has been returned is a use-after-free that will silently show another
/// frame's pixels, so the accessors throw once released rather than tolerating it.
/// </para>
/// </remarks>
public sealed class FrameRef : IDisposable
{
    /// <summary>
    /// The pool frames are rented from unless a caller supplies its own.
    /// </summary>
    /// <remarks>
    /// Deliberately not <see cref="ArrayPool{T}.Shared"/>. The shared pool silently
    /// refuses to pool arrays larger than 1 MB — it allocates a fresh one on rent and
    /// discards it on return — and a single 640x480 BGRA frame is 1.2 MB. Using it here
    /// looks correct, compiles, runs, and quietly allocates a frame-sized array 30 times
    /// a second. This pool is sized to hold real frames, up to 4K.
    /// </remarks>
    public static readonly ArrayPool<byte> DefaultPool =
        ArrayPool<byte>.Create(maxArrayLength: 64 * 1024 * 1024, maxArraysPerBucket: 4);

    private readonly ArrayPool<byte> _pool;
    private byte[]? _data;
    private int _refCount;

    private FrameRef(
        ArrayPool<byte> pool,
        byte[] data,
        int width,
        int height,
        int stride,
        FramePixelFormat format,
        long sequence,
        long captureTimestamp)
    {
        _pool = pool;
        _data = data;
        _refCount = 1;
        Width = width;
        Height = height;
        Stride = stride;
        Format = format;
        Sequence = sequence;
        CaptureTimestamp = captureTimestamp;
    }

    /// <summary>Frame width in pixels.</summary>
    public int Width { get; }

    /// <summary>Frame height in pixels.</summary>
    public int Height { get; }

    /// <summary>Bytes per row. Always tight (<c>Width * bytes-per-pixel</c>).</summary>
    public int Stride { get; }

    /// <summary>Pixel layout of <see cref="Data"/>.</summary>
    public FramePixelFormat Format { get; }

    /// <summary>Monotonic frame counter from the owning source, for detecting gaps.</summary>
    public long Sequence { get; }

    /// <summary><see cref="Stopwatch.GetTimestamp"/> taken when the frame arrived.</summary>
    public long CaptureTimestamp { get; }

    /// <summary>Number of meaningful bytes in <see cref="Data"/>.</summary>
    public int Length => Stride * Height;

    /// <summary>
    /// How long ago this frame was captured. The end-to-end latency budget is measured
    /// against this, so a stage that reads it late reports honestly.
    /// </summary>
    public double AgeMilliseconds =>
        (Stopwatch.GetTimestamp() - CaptureTimestamp) * 1000.0 / Stopwatch.Frequency;

    /// <summary>
    /// The pooled backing array. May be <em>longer</em> than <see cref="Length"/> — pooled
    /// arrays are size-class rounded — so never use <c>Data.Length</c> as the frame size.
    /// </summary>
    public byte[] Data => _data ?? throw new ObjectDisposedException(nameof(FrameRef));

    /// <summary>Writable view over the frame's bytes.</summary>
    public Span<byte> Span => Data.AsSpan(0, Length);

    /// <summary>Read-only view over the frame's bytes.</summary>
    public ReadOnlySpan<byte> Pixels => Data.AsSpan(0, Length);

    /// <summary>Bytes occupied by one pixel in the given format.</summary>
    public static int BytesPerPixel(FramePixelFormat format) => format switch
    {
        FramePixelFormat.Bgra32 => 4,
        FramePixelFormat.Gray8 => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported pixel format."),
    };

    /// <summary>
    /// Rents a frame from the pool. The caller owns the single initial reference and
    /// must dispose it (typically by handing it to subscribers inside a <c>using</c>).
    /// </summary>
    public static FrameRef Rent(
        int width,
        int height,
        FramePixelFormat format,
        long sequence,
        long captureTimestamp,
        ArrayPool<byte>? pool = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(height);

        ArrayPool<byte> effectivePool = pool ?? DefaultPool;
        int stride = width * BytesPerPixel(format);
        byte[] data = effectivePool.Rent(stride * height);
        return new FrameRef(effectivePool, data, width, height, stride, format, sequence, captureTimestamp);
    }

    /// <summary>
    /// Adds a reference so the frame outlives the callback that produced it.
    /// Pair every <c>Retain</c> with a <see cref="Dispose"/>.
    /// </summary>
    public FrameRef Retain()
    {
        // Incrementing from zero means the buffer is already back in the pool; undo the
        // increment so the object stays consistently released rather than half-revived.
        if (Interlocked.Increment(ref _refCount) <= 1)
        {
            Interlocked.Decrement(ref _refCount);
            throw new ObjectDisposedException(nameof(FrameRef));
        }

        return this;
    }

    /// <summary>Releases one reference, returning the buffer to the pool at zero.</summary>
    public void Dispose()
    {
        int remaining = Interlocked.Decrement(ref _refCount);
        if (remaining != 0)
        {
            return;
        }

        byte[]? data = Interlocked.Exchange(ref _data, null);
        if (data is not null)
        {
            _pool.Return(data);
        }
    }
}
