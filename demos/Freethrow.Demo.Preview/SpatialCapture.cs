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

    /// <summary>Offers one frame. Returns why it was not accepted, or null if it was.</summary>
    public string? Offer(Vector2 metric, GestureState state, bool keyHeld)
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
internal sealed class SweepCapture(int target = 90)
{
    private Vector2 _min = new(float.MaxValue);
    private Vector2 _max = new(float.MinValue);

    public int Count { get; private set; }

    public int Target { get; } = target;

    public bool IsComplete => Count >= Target && Extent.X > 0.05f && Extent.Y > 0.05f;

    public Vector2 Min => _min;

    public Vector2 Max => _max;

    public Vector2 Extent => Count == 0 ? Vector2.Zero : _max - _min;

    public double Progress => Math.Min(1.0, Count / (double)Target);

    public void Offer(Vector2 metric)
    {
        _min = Vector2.Min(_min, metric);
        _max = Vector2.Max(_max, metric);
        Count++;
    }

    public void Reset()
    {
        _min = new Vector2(float.MaxValue);
        _max = new Vector2(float.MinValue);
        Count = 0;
    }
}

/// <summary>Turns captured corners into a stored monitor mapping.</summary>
internal static class SpatialCalibration
{
    /// <summary>
    /// Fits the transform taking the four captured hand positions onto the monitor.
    /// </summary>
    /// <param name="corners">Captured positions in metres: top-left, top-right, bottom-right, bottom-left.</param>
    /// <param name="neutralRest">Where the hand rests, in metres.</param>
    /// <param name="monitor">The monitor being mapped.</param>
    public static (MonitorMapping? Mapping, string? Problem) Fit(
        IReadOnlyList<Vector2> corners,
        Vector2 neutralRest,
        MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(corners);
        ArgumentNullException.ThrowIfNull(monitor);

        // Map onto the unit square rather than pixels, so a resolution change does not
        // invalidate the calibration — only the monitor's shape would.
        Vector2[] destination =
        [
            new(0, 0),
            new(1, 0),
            new(1, 1),
            new(0, 1),
        ];

        (Homography? transform, string? problem) = Homography.TryFit(corners, destination);
        if (transform is null)
        {
            return (null, problem);
        }

        return (new MonitorMapping(
            monitor.DeviceName,
            monitor.Description,
            transform.ToArray(),
            Point2.From(neutralRest),
            [.. corners.Select(Point2.From)],
            monitor.Width,
            monitor.Height,
            DateTimeOffset.UtcNow), null);
    }

    /// <summary>
    /// Describes where the resting hand lands on screen, as a sanity check.
    /// </summary>
    /// <remarks>
    /// If the neutral rest position maps far from the middle of the screen, the envelope
    /// was traced off-centre from where the hand actually lives — reaching the far side
    /// will be a stretch every time. Worth saying so before the profile is saved.
    /// </remarks>
    public static string? DescribeRestPlacement(MonitorMapping mapping)
    {
        Vector2 rest = mapping.ToHomography().Map(mapping.NeutralRest.ToVector());

        if (float.IsNaN(rest.X) || float.IsNaN(rest.Y))
        {
            return "Your resting hand maps outside the screen entirely; consider redoing the corners.";
        }

        float offCentre = Math.Max(Math.Abs(rest.X - 0.5f), Math.Abs(rest.Y - 0.5f));

        return offCentre > 0.35f
            ? $"Your resting hand maps near the edge of the screen ({rest.X:0.00}, {rest.Y:0.00}), so "
              + "reaching the opposite side will be a stretch. Redoing the corners centred on where "
              + "your hand naturally sits would be more comfortable."
            : null;
    }
}
