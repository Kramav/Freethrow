using System.Numerics;
using System.Text.Json;
using Freethrow.Core.Spatial;

namespace Freethrow.Core.Config;

/// <summary>A 2D point with named properties, so it survives JSON round-tripping.</summary>
/// <remarks>
/// <see cref="Vector2"/> exposes X and Y as fields rather than properties, which the JSON
/// serializer skips by default — a stored profile would silently read back as all zeros.
/// </remarks>
public readonly record struct Point2(float X, float Y)
{
    public Vector2 ToVector() => new(X, Y);

    public static Point2 From(Vector2 value) => new(value.X, value.Y);
}

/// <summary>The hand-to-screen mapping fitted for one monitor.</summary>
/// <param name="DeviceName">Stable display key, such as <c>\\.\DISPLAY1</c>.</param>
/// <param name="Description">Human-readable name at the time of calibration.</param>
/// <param name="Coefficients">
/// The eight mapping coefficients, laid out as <see cref="ScreenMapping.ToArray"/> describes
/// for every <see cref="MappingKind"/>.
/// </param>
/// <param name="Idle">
/// Where the hands rest when not in use, or <see langword="null"/> if they rest out of the
/// camera's view. Not the centre of the working area: that is defined by the corners.
/// </param>
/// <param name="Corners">
/// The captured reach points in capture order, kept for redisplay and diagnosis: four
/// corners (top-left, top-right, bottom-right, bottom-left) from a four-corner calibration,
/// or two (left, right) from a side-to-side one. Not fixed-length, unlike
/// <paramref name="Coefficients"/>.
/// </param>
/// <param name="Width">Monitor width in pixels when calibrated.</param>
/// <param name="Height">Monitor height in pixels when calibrated.</param>
/// <param name="CalibratedAt">When this mapping was measured.</param>
/// <param name="Kind">
/// Which kind of fit <paramref name="Coefficients"/> holds. Last and defaulted so that a
/// profile written before it existed — which is always a homography — binds to the right
/// kind with no version field. An unknown future value fails the whole load: see
/// <see cref="SpatialProfile.Load"/>.
/// </param>
public sealed record MonitorMapping(
    string DeviceName,
    string Description,
    double[] Coefficients,
    IdleZone? Idle,
    Point2[] Corners,
    int Width,
    int Height,
    DateTimeOffset CalibratedAt,
    MappingKind Kind = MappingKind.Homography)
{
    /// <summary>Rebuilds the mapping from stored coefficients.</summary>
    /// <remarks>
    /// There is deliberately no way to get a bare <see cref="Homography"/> from here. Built
    /// from horizontal-only coefficients it would map every hand to a height of zero — a
    /// fabricated reading indistinguishable from a hand held at the top of the screen.
    /// </remarks>
    public ScreenMapping ToMapping() => ScreenMapping.FromArray(Kind, Coefficients);
}

/// <summary>
/// Per-monitor hand-to-screen mappings, plus the global reach envelope.
/// </summary>
/// <remarks>
/// Monitors are calibrated separately because turning toward a side display also turns
/// the body, moving where the hand naturally sits. The maximum reach envelope is not
/// per-monitor: it is a property of the person's arm, so it is measured once and used
/// everywhere as headroom, letting movement past a screen edge keep tracking instead of
/// clamping dead.
/// </remarks>
public sealed record SpatialProfile
{
    /// <summary>Mappings, one per calibrated monitor.</summary>
    public List<MonitorMapping> Monitors { get; init; } = [];

    /// <summary>Top-left of the maximum reach envelope, in metres from frame centre.</summary>
    /// <remarks>
    /// After a side-to-side calibration the vertical bounds here are where the camera
    /// stopped seeing, not where the arm stopped — the sweep never required height. Nothing
    /// reads them yet; when something does, it must check the monitor's
    /// <see cref="MonitorMapping.Kind"/> before trusting Y.
    /// </remarks>
    public Point2? MaxReachMin { get; init; }

    /// <summary>Bottom-right of the maximum reach envelope.</summary>
    public Point2? MaxReachMax { get; init; }

    /// <summary>Where profiles are stored unless a path is given.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Freethrow",
        "spatial-profile.json");

    /// <summary>Reads a profile, or <see langword="null"/> if none exists or it is unreadable.</summary>
    /// <remarks>
    /// All or nothing: one monitor entry that fails to parse discards every monitor and the
    /// reach envelope with it, and reads exactly like never having calibrated. That is the
    /// real fragility behind "old profiles keep loading" — new fields must default so that
    /// older files still parse.
    /// </remarks>
    public static SpatialProfile? Load(string? path = null)
    {
        string resolved = path ?? DefaultPath;

        try
        {
            return File.Exists(resolved)
                ? JsonSerializer.Deserialize<SpatialProfile>(File.ReadAllText(resolved))
                : null;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Writes this profile, creating the directory if needed.</summary>
    public void Save(string? path = null)
    {
        string resolved = path ?? DefaultPath;

        string? directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(
            resolved,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }

    /// <summary>The mapping for a display, or <see langword="null"/> if it has none.</summary>
    public MonitorMapping? Find(string deviceName) =>
        Monitors.FirstOrDefault(monitor =>
            string.Equals(monitor.DeviceName, deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Returns this profile with <paramref name="mapping"/> added or replacing its predecessor.</summary>
    public SpatialProfile With(MonitorMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        List<MonitorMapping> updated =
        [
            .. Monitors.Where(existing =>
                !string.Equals(existing.DeviceName, mapping.DeviceName, StringComparison.OrdinalIgnoreCase)),
            mapping,
        ];

        return this with { Monitors = updated };
    }
}
