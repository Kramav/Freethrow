using System.Numerics;
using System.Text.Json.Serialization;

namespace Freethrow.Core.Spatial;

/// <summary>What kind of fit a stored mapping is.</summary>
/// <remarks>
/// Stored by name so a profile on disk says what it is. Integer values still read, so no
/// stored form of this field can fail to load.
/// </remarks>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MappingKind
{
    /// <summary>
    /// Four sound corners: a full projective fit, keystone corrected. Zero deliberately, so
    /// a profile written before this enum existed reads back as what it in fact is — no
    /// version field and no migration hook needed.
    /// </summary>
    Homography = 0,

    /// <summary>
    /// Left-to-right only. The camera could not see enough vertical hand movement to steer
    /// by, so height is absent rather than guessed.
    /// </summary>
    HorizontalOnly = 1,
}

/// <summary>
/// A fitted hand-to-screen mapping: metres from frame centre in, 0–1 screen position out.
/// </summary>
/// <remarks>
/// This is what consumers hold, rather than a <see cref="Homography"/>. A homography always
/// produces a Y, so handing one out for a mapping without a vertical axis would fabricate
/// exactly the height this type exists to withhold.
/// </remarks>
public sealed class ScreenMapping
{
    private readonly double[] _coefficients;
    private readonly Homography? _projective;

    private ScreenMapping(MappingKind kind, double[] coefficients)
    {
        Kind = kind;
        _coefficients = coefficients;
        _projective = kind == MappingKind.Homography ? Homography.FromArray(coefficients) : null;
    }

    /// <summary>Which kind of fit this is.</summary>
    public MappingKind Kind { get; }

    /// <summary>
    /// Whether this mapping has a vertical axis at all. Fixed at calibration time, so a
    /// caller can decide once how to present it rather than per frame.
    /// </summary>
    public bool HasVertical => Kind == MappingKind.Homography;

    /// <summary>Maps a hand position, in metres from frame centre, onto the screen.</summary>
    public ScreenPoint Map(Vector2 metric) => Kind switch
    {
        // Affine in X only: no projective divide, so no horizon to guard. There is no
        // vertical row to evaluate, and inventing one is the failure this type prevents.
        MappingKind.HorizontalOnly => ScreenPoint.AcrossOnly(
            (float)((_coefficients[0] * metric.X) + _coefficients[2])),

        MappingKind.Homography => FromProjective(_projective!.Map(metric)),
    };

    /// <summary>The eight coefficients, for persistence.</summary>
    /// <remarks>
    /// Always eight whatever the kind, laid out as <see cref="Homography.ToArray"/> lays them
    /// out; a horizontal-only mapping uses slots 0 and 2 (<c>u = h11·x + h13</c>) and zeroes
    /// the rest. One layout for every kind is what keeps the stored shape — and so every old
    /// profile — unchanged.
    /// </remarks>
    public double[] ToArray() => (double[])_coefficients.Clone();

    /// <summary>Rebuilds a mapping from persisted coefficients.</summary>
    public static ScreenMapping FromArray(MappingKind kind, double[] coefficients)
    {
        ArgumentNullException.ThrowIfNull(coefficients);

        if (coefficients.Length != 8)
        {
            throw new ArgumentException("A screen mapping has exactly eight coefficients.", nameof(coefficients));
        }

        if (!Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown mapping kind.");
        }

        return new ScreenMapping(kind, (double[])coefficients.Clone());
    }

    /// <summary>Wraps a fitted projective transform.</summary>
    internal static ScreenMapping FromHomography(Homography transform) =>
        new(MappingKind.Homography, transform.ToArray());

    /// <summary>A left-to-right mapping taking <paramref name="left"/> to 0 and <paramref name="right"/> to 1.</summary>
    /// <remarks>
    /// Takes the two ends in the order the user reached them, not sorted. Which way metric X
    /// runs relative to the screen depends on the camera mirror, and nothing has measured
    /// that on a real profile yet; sorting would bake an unverified sign into every fit.
    /// </remarks>
    internal static ScreenMapping AcrossOnly(float left, float right)
    {
        double scale = 1.0 / (right - left);
        return new ScreenMapping(MappingKind.HorizontalOnly, [scale, 0, -left * scale, 0, 0, 0, 0, 0]);
    }

    private static ScreenPoint FromProjective(Vector2 mapped) =>
        float.IsNaN(mapped.X) || float.IsNaN(mapped.Y)
            ? ScreenPoint.Nothing
            : ScreenPoint.At(mapped.X, mapped.Y);
}
