namespace Freethrow.Core.Spatial;

/// <summary>
/// Where on a screen a hand points, on the 0–1 scale, with a vertical axis that may not
/// exist.
/// </summary>
/// <remarks>
/// <para>
/// A mapping fitted from a camera that cannot see vertical hand movement has no height to
/// report. Returning 0, or 0.5, would be a reading the caller cannot tell from a measured
/// one — the window layer would place a window at the top of the screen and look correct
/// while being uninformed. So the absence is carried in the type, not encoded as a value.
/// </para>
/// <para>
/// Not a NaN in <c>Y</c> either: the existing unmappable-point guard tests only
/// <c>float.IsNaN(X)</c>, so <c>(0.42, NaN)</c> would sail through it and hand a NaN
/// coordinate to the renderer.
/// </para>
/// <para>
/// Stored as a positive "was mapped" flag rather than a negative one, so
/// <c>default(ScreenPoint)</c> — which every uninitialised field and array element is —
/// reads as nothing rather than as a hand parked in the screen's top-left corner.
/// </para>
/// </remarks>
public readonly record struct ScreenPoint
{
    private readonly bool _mapped;
    private readonly bool _hasVertical;
    private readonly float _x;
    private readonly float _y;

    private ScreenPoint(bool mapped, bool hasVertical, float x, float y)
    {
        _mapped = mapped;
        _hasVertical = hasVertical;
        _x = x;
        _y = y;
    }

    /// <summary>Nothing was mapped: no hand, or a point on the transform's horizon.</summary>
    public static ScreenPoint Nothing => default;

    /// <summary>A position on both axes.</summary>
    public static ScreenPoint At(float x, float y) => new(true, true, x, y);

    /// <summary>A horizontal position from a mapping that never measured height.</summary>
    public static ScreenPoint AcrossOnly(float x) => new(true, false, x, 0);

    /// <summary>Whether this is a position at all.</summary>
    public bool IsMapped => _mapped;

    /// <summary>
    /// Horizontal position, or NaN when nothing was mapped — the same convention
    /// <see cref="Homography.Map"/> uses, so code that forgets <see cref="IsMapped"/>
    /// degrades the way it already did.
    /// </summary>
    public float X => _mapped ? _x : float.NaN;

    /// <summary>
    /// Vertical position, or <see langword="null"/> when nothing was mapped or the mapping
    /// has no vertical axis — never a stand-in value.
    /// </summary>
    public float? Y => _mapped && _hasVertical ? _y : null;
}
