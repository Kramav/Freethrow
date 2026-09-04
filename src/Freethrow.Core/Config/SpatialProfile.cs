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
/// <param name="Coefficients">The eight homography coefficients.</param>
/// <param name="NeutralRest">Where the hand sits at rest, in metres from frame centre.</param>
/// <param name="Corners">The four captured corners, kept for redisplay and diagnosis.</param>
/// <param name="Width">Monitor width in pixels when calibrated.</param>
/// <param name="Height">Monitor height in pixels when calibrated.</param>
/// <param name="CalibratedAt">When this mapping was measured.</param>
public sealed record MonitorMapping(
    string DeviceName,
    string Description,
    double[] Coefficients,
    Point2 NeutralRest,
    Point2[] Corners,
    int Width,
    int Height,
    DateTimeOffset CalibratedAt)
{
    /// <summary>Rebuilds the transform from stored coefficients.</summary>
    public Homography ToHomography() => Homography.FromArray(Coefficients);
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
    public Point2? MaxReachMin { get; init; }

    /// <summary>Bottom-right of the maximum reach envelope.</summary>
    public Point2? MaxReachMax { get; init; }

    /// <summary>Where profiles are stored unless a path is given.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Freethrow",
        "spatial-profile.json");

    /// <summary>Reads a profile, or <see langword="null"/> if none exists or it is unreadable.</summary>
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
