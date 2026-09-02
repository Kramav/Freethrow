namespace Freethrow.Core.Capture;

/// <summary>A capture format offered by a camera.</summary>
/// <param name="Width">Frame width in pixels.</param>
/// <param name="Height">Frame height in pixels.</param>
/// <param name="FrameRate">Nominal frames per second.</param>
/// <param name="Subtype">Backend-specific encoding name (for example <c>NV12</c>, <c>MJPG</c>, <c>L8</c>).</param>
public readonly record struct CameraFormat(int Width, int Height, double FrameRate, string Subtype)
{
    /// <summary>Total pixels per frame — used when scoring candidate formats.</summary>
    public int PixelCount => Width * Height;

    public override string ToString() => $"{Width}x{Height} @ {FrameRate:0.#}fps ({Subtype})";
}
