using Freethrow.Core.Config;
using Freethrow.Core.Perception;

namespace Freethrow.Demo.Preview;

/// <summary>Samples gathered while the user held one requested pose.</summary>
/// <param name="Name">Human-readable name of the pose.</param>
/// <param name="Openness">Every openness sample recorded.</param>
/// <param name="ViewAlignment">Every view-alignment sample recorded.</param>
internal sealed record CalibrationPhase(string Name, List<float> Openness, List<float> ViewAlignment)
{
    public bool HasEnoughSamples => Openness.Count >= 15;

    /// <summary>Adds one frame's measurements.</summary>
    public void Add(HandPose pose)
    {
        Openness.Add(HandMetrics.Openness(pose));
        ViewAlignment.Add(HandMetrics.ViewAxisAlignment(pose));
    }
}

/// <summary>
/// Turns recorded poses into grab thresholds.
/// </summary>
/// <remarks>
/// Thresholds are placed in the measured gap between the closed and open clusters
/// rather than at fixed values, because that gap is where a hand actually is or is not
/// gripping, and its position differs from person to person.
/// </remarks>
internal static class GrabCalibration
{
    /// <summary>
    /// Where in the open/closed gap the grab threshold sits, as a fraction from the
    /// closed end. Low, so a grab requires a genuinely closed hand.
    /// </summary>
    private const float GrabFraction = 0.20f;

    /// <summary>
    /// Where in the gap the release threshold sits. Below the midpoint on purpose: a
    /// window dropped early is re-grabbed in a second, while one that will not let go
    /// feels broken.
    /// </summary>
    private const float ReleaseFraction = 0.45f;

    /// <summary>Where between flat and camera-pointing the arming gate sits.</summary>
    private const float AlignmentFraction = 0.40f;

    /// <summary>
    /// Fits a profile, or returns the reason it could not be fitted.
    /// </summary>
    public static (GestureProfile? Profile, string? Problem) Fit(
        CalibrationPhase open,
        CalibrationPhase closed,
        CalibrationPhase pointing)
    {
        if (!open.HasEnoughSamples || !closed.HasEnoughSamples)
        {
            return (null, "Too few frames with a hand in them. Try again in better light.");
        }

        // Use percentiles rather than extremes: a single bad landmark frame should not
        // define where a threshold goes.
        float closedHigh = Percentile(closed.Openness, 0.95f);
        float openLow = Percentile(open.Openness, 0.05f);
        float gap = openLow - closedHigh;

        if (gap <= 0.05f)
        {
            return (null,
                $"Open ({openLow:0.00}) and closed ({closedHigh:0.00}) hands measured almost the same, "
                + "so no reliable threshold exists between them. Make sure the open hand is flat to "
                + "the camera and the fist is fully closed.");
        }

        float maxAlignment = FitAlignment(open, closed, pointing);

        return (new GestureProfile
        {
            GrabOpenness = closedHigh + (gap * GrabFraction),
            ReleaseOpenness = closedHigh + (gap * ReleaseFraction),
            MaxViewAxisAlignment = maxAlignment,
        }, null);
    }

    /// <summary>
    /// Places the arming gate between the alignment seen while facing the camera and the
    /// alignment seen while pointing at it.
    /// </summary>
    private static float FitAlignment(
        CalibrationPhase open,
        CalibrationPhase closed,
        CalibrationPhase pointing)
    {
        List<float> flat = [.. open.ViewAlignment, .. closed.ViewAlignment];
        float flatHigh = Percentile(flat, 0.95f);

        if (!pointing.HasEnoughSamples)
        {
            // Without the pointing sample there is nothing to bound the far side, so keep
            // the default rather than inventing one from half the data.
            return GestureProfile.LoadOptionsOrDefault().MaxViewAxisAlignment;
        }

        float pointingLow = Percentile(pointing.ViewAlignment, 0.05f);
        if (pointingLow - flatHigh <= 0.1f)
        {
            return GestureProfile.LoadOptionsOrDefault().MaxViewAxisAlignment;
        }

        return flatHigh + ((pointingLow - flatHigh) * AlignmentFraction);
    }

    /// <summary>Describes a set of samples for display.</summary>
    public static string Describe(IReadOnlyCollection<float> samples)
    {
        if (samples.Count == 0)
        {
            return "no samples";
        }

        return $"median {Percentile(samples, 0.5f):0.00}  "
            + $"5-95% {Percentile(samples, 0.05f):0.00}-{Percentile(samples, 0.95f):0.00}  "
            + $"n={samples.Count}";
    }

    private static float Percentile(IReadOnlyCollection<float> samples, float fraction)
    {
        float[] sorted = [.. samples];
        Array.Sort(sorted);

        int index = Math.Clamp(
            (int)MathF.Round(fraction * (sorted.Length - 1)),
            0,
            sorted.Length - 1);

        return sorted[index];
    }
}
