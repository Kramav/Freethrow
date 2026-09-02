namespace Freethrow.Core.Capture;

/// <summary>
/// Pixel layouts the pipeline understands. Deliberately minimal: capture backends
/// convert whatever the camera produces into one of these before the frame enters
/// the pipeline, so no downstream stage has to know about NV12, YUY2 or MJPG.
/// </summary>
public enum FramePixelFormat
{
    /// <summary>Unset / unrecognised.</summary>
    Unknown = 0,

    /// <summary>8 bits per channel, blue-green-red-alpha order. The colour path.</summary>
    Bgra32,

    /// <summary>Single 8-bit channel. Infrared cameras and the motion gate use this.</summary>
    Gray8,
}
