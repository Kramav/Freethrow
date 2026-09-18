using System.Numerics;

namespace Freethrow.Core.Spatial;

/// <summary>
/// Chooses and fits the best screen mapping the captured reach points can support.
/// </summary>
/// <remarks>
/// <para>
/// The point count is the mode. Four corners (top-left, top-right, bottom-right,
/// bottom-left) fit a full two-axis mapping, or a side-to-side one if they turn out too flat
/// to steer by. Two points (left, then right) fit side-to-side only, for a camera that cannot
/// see vertical reach at all. The requested mode is deliberately not a parameter: it would be
/// a second source of truth that could disagree with the data it describes.
/// </para>
/// <para>
/// Too flat is decided <em>before</em> fitting, not as a fallback after it. A 45 × 8 cm
/// capture is convex, encloses 360 cm² and solves cleanly as a homography — which then
/// amplifies every centimetre of vertical tremor across an eighth of the screen. A fallback
/// that fires only when the fit fails never fires in the case that matters.
/// </para>
/// </remarks>
public static class MappingFit
{
    /// <summary>Smallest span, in metres, an axis can be steered by.</summary>
    /// <remarks>
    /// At most a tenth of a screen per centimetre of hand movement: a tenth of a screen is
    /// about one window, and hover never asks for better than window-level precision, so past
    /// this a hand that merely trembles crosses a window. Resolution-independent on purpose —
    /// a 4K monitor does not make a hand steadier. Derived, not yet measured.
    /// </remarks>
    public const float MinimumSpanMetres = 0.10f;

    /// <summary>How much more sensitive the vertical axis may be than the horizontal.</summary>
    /// <remarks>
    /// Measured in screen pixels per metre of hand movement. Past this, the same tremor that
    /// stays inside one window horizontally crosses two vertically, and the user cannot form
    /// one sense of how far to move. Derived, not yet measured.
    /// </remarks>
    public const float MaxSensitivityRatio = 2.0f;

    // Onto the unit square rather than pixels, so a resolution change does not invalidate
    // the calibration — only the monitor's shape would.
    private static readonly Vector2[] UnitSquare =
    [
        new(0, 0),
        new(1, 0),
        new(1, 1),
        new(0, 1),
    ];

    /// <summary>Fits a mapping from captured reach points.</summary>
    /// <param name="reach">
    /// Hand positions in metres from frame centre: four corners in order top-left, top-right,
    /// bottom-right, bottom-left; or two points, left then right, for side to side.
    /// </param>
    /// <param name="screenWidth">Monitor width in pixels; only the aspect ratio is used.</param>
    /// <param name="screenHeight">Monitor height in pixels.</param>
    /// <returns>
    /// The mapping and a sentence saying what was fitted and why, or the reason nothing could be.
    /// </returns>
    public static (ScreenMapping? Mapping, string? Explanation, string? Problem) Fit(
        IReadOnlyList<Vector2> reach,
        int screenWidth,
        int screenHeight)
    {
        ArgumentNullException.ThrowIfNull(reach);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenWidth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(screenHeight);

        return reach.Count switch
        {
            2 => FitSideToSide(reach[0].X, reach[1].X),
            4 => FitCorners(reach, (float)screenHeight / screenWidth),
            _ => (null, null, "A screen mapping needs four corners, or two points for side to side."),
        };
    }

    private static (ScreenMapping?, string?, string?) FitSideToSide(float left, float right)
    {
        float span = MathF.Abs(right - left);
        if (span < MinimumSpanMetres)
        {
            return (null, null,
                $"The left and right points are only {span * 100:0} cm apart, which is too small "
                + "to map a screen onto. Reach further to each side.");
        }

        return (ScreenMapping.AcrossOnly(left, right),
            $"Side-to-side mapping over {span * 100:0} cm. Height is not measured, and nothing "
            + "downstream will guess at it.",
            null);
    }

    private static (ScreenMapping?, string?, string?) FitCorners(IReadOnlyList<Vector2> corners, float aspect)
    {
        // Edge means pair each corner with its neighbour on the same side of the screen, so
        // they keep the correspondence whichever way metric X runs relative to the screen.
        // A bounding box would not: its minimum X may be the screen's right, and nothing has
        // yet measured which way the camera mirror goes on a real profile.
        float left = (corners[0].X + corners[3].X) / 2;
        float right = (corners[1].X + corners[2].X) / 2;
        float top = (corners[0].Y + corners[1].Y) / 2;
        float bottom = (corners[3].Y + corners[2].Y) / 2;

        float spanX = MathF.Abs(right - left);
        float spanY = MathF.Abs(bottom - top);

        // Flatness is judged on vertical extent, which is sign-safe, and a capture this thin
        // has no height to steer by whatever its shape. So convexity is not asked of it: a few
        // millimetres of noise can flip a turn and make a genuinely flat capture read as one
        // taken out of order.
        float extentY = corners.Max(c => c.Y) - corners.Min(c => c.Y);
        bool flat = extentY < MinimumSpanMetres;

        // Convexity before any span test. Swap the top-right and bottom-right corners and the
        // edge means average top with bottom, collapsing the vertical span to nothing — which
        // would otherwise be reported as the camera's limit when it is the user's order.
        if (!flat && !Homography.IsConvex(corners))
        {
            return (null, null, Homography.OutOfOrderProblem);
        }

        if (spanX < MinimumSpanMetres)
        {
            return (null, null,
                $"The left and right corners are only {spanX * 100:0} cm apart, which is too small "
                + "to map a screen onto. Reach further apart, and check the corners were captured in order.");
        }

        if (flat || !VerticalIsUsable(spanX, spanY, aspect))
        {
            return (ScreenMapping.AcrossOnly(left, right), DescribeTooFlat(spanX, spanY), null);
        }

        (Homography? transform, string? problem) = Homography.TryFit(corners, UnitSquare);
        return transform is null
            ? (null, null, problem)
            : (ScreenMapping.FromHomography(transform),
                $"Full two-axis mapping from four corners, {spanX * 100:0} × {spanY * 100:0} cm.",
                null);
    }

    /// <summary>
    /// Whether the vertical span is enough to steer by, both absolutely and relative to the
    /// horizontal.
    /// </summary>
    /// <remarks>
    /// Rearranged from (height ÷ spanY) ÷ (width ÷ spanX) ≤ ratio. The right-hand side,
    /// aspect × spanX, is the height of a reach shaped like the screen — the one whose two
    /// axes are equally sensitive.
    /// </remarks>
    private static bool VerticalIsUsable(float spanX, float spanY, float aspect) =>
        spanY >= MinimumSpanMetres && spanY >= spanX * aspect / MaxSensitivityRatio;

    private static string DescribeTooFlat(float spanX, float spanY)
    {
        string sensitivity = spanY >= 0.01f
            ? $" One centimetre of hand movement would move the pointer {0.01f / spanY * 100:0}% of the "
                + $"way down the screen but only {0.01f / spanX * 100:0}% of the way across, so height "
                + "would read as tremor rather than aim."
            : string.Empty;

        return $"Your corners span {spanX * 100:0} cm across but only {spanY * 100:0} cm up and down."
            + sensitivity
            + " Fitted side to side only: Freethrow will track how far across the screen you point, "
            + "and will not use height. For both axes, start over with more vertical reach, or aim "
            + "the camera at your working area.";
    }
}
