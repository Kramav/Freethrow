namespace Freethrow.Core.Capture;

/// <summary>
/// What a camera actually senses. Infrared is the opt-in enhancement: it works in
/// darkness and gives a cleaner eye signal, but is never required.
/// </summary>
public enum CameraKind
{
    Unknown = 0,
    Color,
    Infrared,
    Depth,
}
