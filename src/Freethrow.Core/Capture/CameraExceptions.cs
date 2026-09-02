namespace Freethrow.Core.Capture;

/// <summary>Base type for camera failures the user can plausibly act on.</summary>
public class CameraException : Exception
{
    public CameraException(string message)
        : base(message)
    {
    }

    public CameraException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>
/// The operating system refused camera access.
/// </summary>
/// <remarks>
/// On Windows this is nearly always the per-machine privacy toggle rather than a bug:
/// Settings &gt; Privacy &amp; security &gt; Camera, with "Let desktop apps access your
/// camera" switched off. Surfacing that plainly saves a long debugging detour, so the
/// message carries the fix rather than the HRESULT.
/// </remarks>
public sealed class CameraAccessDeniedException : CameraException
{
    public CameraAccessDeniedException(Exception innerException)
        : base(
            "Camera access was denied. Open Settings > Privacy & security > Camera and make sure "
            + "both 'Camera access' and 'Let desktop apps access your camera' are turned on.",
            innerException)
    {
    }
}

/// <summary>The camera could not be opened or configured.</summary>
public sealed class CameraOpenException : CameraException
{
    public CameraOpenException(string message)
        : base(message)
    {
    }

    public CameraOpenException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
