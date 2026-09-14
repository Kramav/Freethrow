using System.Numerics;
using Freethrow.Core.Config;

namespace Freethrow.Core.Spatial;

/// <summary>
/// Where the user's hands sit when they are not using Freethrow, if the camera can see
/// them there.
/// </summary>
/// <remarks>
/// <para>
/// Hands resting on a keyboard or a desk are in shot on some setups and out of it on
/// others. Where they are in shot, they are not reaching for anything, and a hand that
/// merely appears there must not start hovering windows.
/// </para>
/// <para>
/// This is deliberately <em>not</em> the centre of the working area. The working area is
/// the four comfortable-reach corners; the idle zone only marks where a hand is known to
/// be resting rather than intending.
/// </para>
/// </remarks>
/// <param name="Centre">Middle of the zone, in metres from frame centre.</param>
/// <param name="Radius">Radius of the zone, in metres.</param>
public sealed record IdleZone(Point2 Centre, float Radius)
{
    /// <summary>
    /// The smallest radius a fitted zone is given, in metres.
    /// </summary>
    /// <remarks>
    /// A few seconds of capture sees one resting posture. A hand at rest moves between
    /// keyboard, mouse and lap over minutes, so the measured spread alone would draw the
    /// zone too tight to recognise the same person resting a moment later.
    /// </remarks>
    public const float MinimumRadius = 0.05f;

    /// <summary>Fits a zone around positions recorded while the hand was resting.</summary>
    /// <remarks>
    /// The centre is the per-axis median and the radius the 90th-percentile distance from
    /// it, so a few frames caught mid-fidget cannot drag or inflate the zone.
    /// </remarks>
    public static IdleZone Fit(IReadOnlyList<Vector2> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        if (samples.Count == 0)
        {
            throw new ArgumentException("An idle zone needs at least one sample.", nameof(samples));
        }

        var centre = new Vector2(
            Percentile([.. samples.Select(s => s.X)], 0.5f),
            Percentile([.. samples.Select(s => s.Y)], 0.5f));

        float spread = Percentile([.. samples.Select(s => Vector2.Distance(s, centre))], 0.9f);

        return new IdleZone(Point2.From(centre), MathF.Max(spread, MinimumRadius));
    }

    /// <summary>Whether a hand position, in metres from frame centre, lies inside the zone.</summary>
    public bool Contains(Vector2 metric) => Vector2.Distance(Centre.ToVector(), metric) <= Radius;

    private static float Percentile(float[] values, float fraction)
    {
        Array.Sort(values);

        int index = Math.Clamp(
            (int)MathF.Round(fraction * (values.Length - 1)),
            0,
            values.Length - 1);

        return values[index];
    }
}
