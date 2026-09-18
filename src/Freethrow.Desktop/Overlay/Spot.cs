namespace Freethrow.Desktop.Overlay;

/// <summary>A place on a screen that a calibration step can ask the user to reach for.</summary>
/// <remarks>
/// One vocabulary shared by the overlay, which draws the mark, and the wizard, which names
/// the step and orders the captured points. Two enums that had to agree would be one more
/// thing to drift. Switches over it carry no discard arm, so adding a spot is a build error
/// everywhere it has to be handled.
/// </remarks>
public enum Spot
{
    TopLeft,
    TopRight,
    BottomRight,
    BottomLeft,

    /// <summary>Halfway down the left edge, for a side-to-side calibration.</summary>
    MidLeft,

    /// <summary>Halfway down the right edge, for a side-to-side calibration.</summary>
    MidRight,
}
