namespace Freethrow.Core.Capture;

/// <summary>
/// A camera the pipeline can open, as reported by an <see cref="ICameraEnumerator"/>.
/// </summary>
/// <param name="Id">Backend-specific source identifier, unique within <paramref name="GroupId"/>.</param>
/// <param name="DisplayName">Human-readable name, suitable for a device picker.</param>
/// <param name="Kind">What the camera senses.</param>
/// <param name="GroupId">
/// Identifier of the physical device the source belongs to. A single webcam module
/// commonly exposes several sources (colour plus infrared) under one group.
/// </param>
/// <param name="GroupName">Human-readable name of that physical device.</param>
public sealed record CameraDeviceInfo(
    string Id,
    string DisplayName,
    CameraKind Kind,
    string GroupId,
    string GroupName)
{
    public override string ToString() => $"{DisplayName} [{Kind}]";
}
