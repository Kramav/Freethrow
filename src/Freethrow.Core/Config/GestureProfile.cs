using System.Text.Json;
using System.Text.Json.Serialization;
using Freethrow.Core.Gestures;

namespace Freethrow.Core.Config;

/// <summary>
/// Grab thresholds fitted to one person's hand.
/// </summary>
/// <remarks>
/// <para>
/// The built-in defaults were derived from a single measured hand, and hands differ:
/// finger length relative to palm length varies enough to shift openness by more than
/// the gap between the grab and release thresholds. A profile replaces guesswork with
/// the user's own measurements.
/// </para>
/// <para>
/// Only the values worth fitting per person live here. Timings and filter constants are
/// properties of the pipeline rather than of a hand, so they stay in code.
/// </para>
/// </remarks>
public sealed record GestureProfile
{
    /// <summary>Openness below which the hand counts as closed.</summary>
    public required float GrabOpenness { get; init; }

    /// <summary>Openness above which a held hand counts as released.</summary>
    public required float ReleaseOpenness { get; init; }

    /// <summary>Largest view-axis alignment that may still start a grab.</summary>
    public required float MaxViewAxisAlignment { get; init; }

    /// <summary>When this profile was measured.</summary>
    public DateTimeOffset CalibratedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>Where profiles are stored unless a path is given.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Freethrow",
        "gesture-profile.json");

    /// <summary>
    /// Reads a profile, or returns <see langword="null"/> if none exists.
    /// </summary>
    /// <remarks>
    /// A corrupt or unreadable profile also returns <see langword="null"/>. Falling back
    /// to working defaults beats refusing to start over a preferences file.
    /// </remarks>
    public static GestureProfile? Load(string? path = null)
    {
        string resolved = path ?? DefaultPath;

        try
        {
            if (!File.Exists(resolved))
            {
                return null;
            }

            return JsonSerializer.Deserialize<GestureProfile>(File.ReadAllText(resolved));
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Returns options from the stored profile, or the defaults.</summary>
    public static GestureOptions LoadOptionsOrDefault(string? path = null) =>
        Load(path)?.ToOptions() ?? GestureOptions.Default;

    /// <summary>Writes this profile, creating the directory if needed.</summary>
    public void Save(string? path = null)
    {
        string resolved = path ?? DefaultPath;

        string? directory = Path.GetDirectoryName(resolved);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(resolved, JsonSerializer.Serialize(this, SerializerOptions));
    }

    /// <summary>Applies this profile over a set of options.</summary>
    public GestureOptions ToOptions(GestureOptions? baseline = null) =>
        (baseline ?? GestureOptions.Default) with
        {
            GrabOpenness = GrabOpenness,
            ReleaseOpenness = ReleaseOpenness,
            MaxViewAxisAlignment = MaxViewAxisAlignment,
        };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };
}
