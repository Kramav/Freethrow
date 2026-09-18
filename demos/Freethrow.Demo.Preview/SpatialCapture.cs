using System.Numerics;
using Freethrow.Core.Config;
using Freethrow.Core.Gestures;
using Freethrow.Core.Spatial;
using Freethrow.Desktop.Desktop;

namespace Freethrow.Demo.Preview;

/// <summary>How a corner position is committed.</summary>
public enum CornerConfirmation
{
    /// <summary>
    /// Close the hand at the corner and hold. Also proves a grab can arm out there,
    /// where the wrist is most rotated and the posture gate most likely to refuse.
    /// </summary>
    GrabAndHold,

    /// <summary>Hold an open hand still at the corner. Easier, but tests no gesture.</summary>
    HoverDwell,

    /// <summary>Hold the hand in place and press space. Most precise, needs the other hand.</summary>
    KeyPress,
}

/// <summary>How much of the screen a calibration maps.</summary>
public enum ReachMode
{
    /// <summary>Four corners, both axes: for a camera that can see the whole working area.</summary>
    FullScreen,

    /// <summary>
    /// Two points, left and right: for a camera that cannot see vertical reach, such as one
    /// in a laptop lid aimed at the face, where reaching for a bottom corner leaves the view
    /// entirely and the step can never complete.
    /// </summary>
    SideToSide,
}

/// <summary>
/// Collects samples for one corner until enough agree.
/// </summary>
/// <remarks>
/// Every mode requires a run of samples rather than a single reading, because a hand at
/// the edge of its reach drifts. The median of the run is used, so a few stray frames
/// cannot pull the corner off position.
/// </remarks>
internal sealed class PointCapture(CornerConfirmation mode, int target = 20)
{
    /// <summary>How far the hand may drift during a hover before the run restarts, in metres.</summary>
    private const float HoverToleranceMetres = 0.04f;

    private readonly List<Vector2> _samples = [];
    private Vector2? _anchor;

    public CornerConfirmation Mode { get; } = mode;

    public int Target { get; } = target;

    public int Count => _samples.Count;

    public bool IsComplete => _samples.Count >= Target;

    public double Progress => Math.Min(1.0, _samples.Count / (double)Target);

    /// <summary>
    /// Every frame border the hand touched during the run that was kept.
    /// </summary>
    /// <remarks>
    /// Not a reason to refuse the corner: the palm centre can still be sound when a
    /// fingertip is off-frame. But a corner at the camera's edge may be where the camera
    /// stopped seeing rather than where the hand stopped, which the results should say.
    /// </remarks>
    public FrameEdge EdgeContact { get; private set; }

    /// <summary>Offers one frame. Returns why it was not accepted, or null if it was.</summary>
    public string? Offer(Vector2 metric, FrameEdge edges, GestureState state, bool keyHeld)
    {
        string? refused = Accept(metric, state, keyHeld);

        if (refused is null)
        {
            EdgeContact |= edges;
        }
        else
        {
            EdgeContact = FrameEdge.None;
        }

        return refused;
    }

    private string? Accept(Vector2 metric, GestureState state, bool keyHeld)
    {
        switch (Mode)
        {
            case CornerConfirmation.GrabAndHold when state != GestureState.Grab:
                _samples.Clear();
                return "close your hand at the target and hold";

            case CornerConfirmation.KeyPress when !keyHeld:
                _samples.Clear();
                return "hold position, then press and hold space";

            case CornerConfirmation.HoverDwell:
                // Restart if the hand wandered: a dwell that tolerated drift would
                // average a smear rather than record a point.
                if (_anchor is { } anchor && Vector2.Distance(anchor, metric) > HoverToleranceMetres)
                {
                    _samples.Clear();
                    _anchor = metric;
                    return "hold still";
                }

                _anchor ??= metric;
                break;
        }

        _samples.Add(metric);
        return null;
    }

    /// <summary>The captured position: the median of the run, per axis.</summary>
    public Vector2 Result => new(
        Median([.. _samples.Select(s => s.X)]),
        Median([.. _samples.Select(s => s.Y)]));

    public void Reset()
    {
        _samples.Clear();
        _anchor = null;
        EdgeContact = FrameEdge.None;
    }

    private static float Median(float[] values)
    {
        if (values.Length == 0)
        {
            return 0;
        }

        Array.Sort(values);
        return values[values.Length / 2];
    }
}

/// <summary>
/// Records the outer bounds of a free sweep of the arm.
/// </summary>
/// <remarks>
/// The maximum-reach envelope only supplies headroom, so movement past a screen edge
/// keeps tracking instead of clamping dead. Bounds are all it needs, which is why this
/// is a sweep rather than four more held corners — and why it is measured once rather
/// than per monitor: it is a property of the arm, not of a screen.
/// </remarks>
internal sealed class SweepCapture(int target = 90, bool requireVertical = true)
{
    private Vector2 _min = new(float.MaxValue);
    private Vector2 _max = new(float.MinValue);

    public int Count { get; private set; }

    public int Target { get; } = target;

    /// <remarks>
    /// Vertical extent is optional because a side-to-side calibration exists for a camera
    /// that cannot see vertical hand travel. Requiring it there stalls this step exactly the
    /// way the bottom corners stalled — the same never-completes failure, one step later.
    /// </remarks>
    public bool IsComplete =>
        Count >= Target && Extent.X > 0.05f && (!requireVertical || Extent.Y > 0.05f);

    public Vector2 Min => _min;

    public Vector2 Max => _max;

    public Vector2 Extent => Count == 0 ? Vector2.Zero : _max - _min;

    public double Progress => Math.Min(1.0, Count / (double)Target);

    /// <summary>
    /// Sides where the envelope's bound was set by a hand at the edge of the camera's view.
    /// </summary>
    /// <remarks>
    /// A sweep that runs off the side of the frame records the camera's limit on that
    /// side, not the arm's, and looks exactly like a genuine reach in the numbers. Only
    /// the frame that set a bound decides it: touching an edge somewhere mid-sweep says
    /// nothing about the extremes.
    /// </remarks>
    public FrameEdge ClippedSides { get; private set; }

    public void Offer(Vector2 metric, FrameEdge edges)
    {
        // Frame and metric axes point the same way (metric is pixels minus the frame
        // centre, divided by a positive scale), so a new minimum X is a leftward extreme
        // in camera terms.
        if (metric.X < _min.X)
        {
            ClippedSides = Flag(ClippedSides, FrameEdge.Left, edges);
        }

        if (metric.X > _max.X)
        {
            ClippedSides = Flag(ClippedSides, FrameEdge.Right, edges);
        }

        if (metric.Y < _min.Y)
        {
            ClippedSides = Flag(ClippedSides, FrameEdge.Top, edges);
        }

        if (metric.Y > _max.Y)
        {
            ClippedSides = Flag(ClippedSides, FrameEdge.Bottom, edges);
        }

        _min = Vector2.Min(_min, metric);
        _max = Vector2.Max(_max, metric);
        Count++;
    }

    public void Reset()
    {
        _min = new Vector2(float.MaxValue);
        _max = new Vector2(float.MinValue);
        Count = 0;
        ClippedSides = FrameEdge.None;
    }

    /// <summary>Sets or clears <paramref name="side"/> according to the frame that just extended it.</summary>
    private static FrameEdge Flag(FrameEdge current, FrameEdge side, FrameEdge touched) =>
        (touched & side) != 0 ? current | side : current & ~side;
}

/// <summary>
/// Records where the hands sit at rest, with no stillness or gesture required.
/// </summary>
/// <remarks>
/// Not a <see cref="PointCapture"/>: that restarts whenever the hand drifts, and a resting
/// hand fidgets. Resting is the one thing the user is not asked to do precisely.
/// </remarks>
internal sealed class IdleCapture(int target = 60)
{
    private readonly List<Vector2> _samples = [];

    public int Count => _samples.Count;

    public int Target { get; } = target;

    public bool IsComplete => _samples.Count >= Target;

    public double Progress => Math.Min(1.0, _samples.Count / (double)Target);

    public void Offer(Vector2 metric) => _samples.Add(metric);

    public IdleZone Result => IdleZone.Fit(_samples);
}

/// <summary>Turns captured reach points into a stored monitor mapping.</summary>
internal static class SpatialCalibration
{
    /// <summary>
    /// Fits the best mapping the captured reach points support, for one monitor.
    /// </summary>
    /// <param name="reach">
    /// Captured positions in metres: four corners (top-left, top-right, bottom-right,
    /// bottom-left), or two points (left, right) for side to side.
    /// </param>
    /// <param name="idle">Where the hands rest, or null if they rest out of frame.</param>
    /// <param name="monitor">The monitor being mapped.</param>
    /// <returns>The mapping and what was fitted, or the reason none could be.</returns>
    public static (MonitorMapping? Mapping, string? Explanation, string? Problem) Fit(
        IReadOnlyList<Vector2> reach,
        IdleZone? idle,
        MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(reach);
        ArgumentNullException.ThrowIfNull(monitor);

        (ScreenMapping? fitted, string? explanation, string? problem) =
            MappingFit.Fit(reach, monitor.Width, monitor.Height);

        if (fitted is null)
        {
            return (null, null, problem);
        }

        return (new MonitorMapping(
            monitor.DeviceName,
            monitor.Description,
            fitted.ToArray(),
            idle,
            [.. reach.Select(Point2.From)],
            monitor.Width,
            monitor.Height,
            DateTimeOffset.UtcNow,
            fitted.Kind), explanation, null);
    }

    /// <summary>
    /// Describes where the resting hands are, for the results screen.
    /// </summary>
    /// <remarks>
    /// Informational only. The resting hand is not expected to sit anywhere in particular
    /// relative to the screen — out of frame, below the working area, or even behind it
    /// are all normal — so there is nothing here to warn about.
    /// </remarks>
    public static string DescribeIdle(MonitorMapping mapping)
    {
        if (mapping.Idle is not { } idle)
        {
            return "Hands rest out of the camera's view.";
        }

        ScreenPoint onScreen = mapping.ToMapping().Map(idle.Centre.ToVector());
        string where = !onScreen.IsMapped
            ? "off the mapped area"
            : onScreen.Y is { } y
                ? $"at ({onScreen.X:0.00}, {y:0.00}) on the screen's 0–1 scale"
                : $"{onScreen.X:0.00} of the way across the screen (height is not measured)";

        return $"Hands rest {where}, within {idle.Radius * 100:0} cm. "
            + "Once windows can be moved (M2), a hand that appears there will not hover until it leaves.";
    }
}
