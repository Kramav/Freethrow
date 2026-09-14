using System.Numerics;
using Freethrow.Core.Config;
using Freethrow.Core.Spatial;

namespace Freethrow.Core.Tests;

/// <summary>
/// Tests for the idle zone: where resting hands sit, and that it survives storage.
/// </summary>
public class IdleZoneTests
{
    [Fact]
    public void CentreIgnoresAFewStrayFrames()
    {
        // Twenty frames resting low and to the left, plus three caught mid-fidget far away.
        // The median must stay with the resting hand; a mean would be dragged toward the
        // strays.
        List<Vector2> samples = [.. Enumerable.Repeat(new Vector2(-0.10f, 0.20f), 20)];
        samples.AddRange(Enumerable.Repeat(new Vector2(0.30f, -0.20f), 3));

        IdleZone zone = IdleZone.Fit(samples);

        Assert.Equal(-0.10f, zone.Centre.X, tolerance: 0.0001f);
        Assert.Equal(0.20f, zone.Centre.Y, tolerance: 0.0001f);
    }

    [Fact]
    public void PerfectlyStillHandStillGetsTheMinimumRadius()
    {
        IdleZone zone = IdleZone.Fit([.. Enumerable.Repeat(new Vector2(0.05f, 0.18f), 30)]);

        Assert.Equal(IdleZone.MinimumRadius, zone.Radius);
    }

    [Fact]
    public void SpreadWiderThanTheMinimumSetsTheRadius()
    {
        // Samples on a 12 cm circle around the centre, plus the centre itself.
        List<Vector2> samples = [Vector2.Zero];
        for (int i = 0; i < 40; i++)
        {
            float angle = i * MathF.Tau / 40;
            samples.Add(new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * 0.12f);
        }

        IdleZone zone = IdleZone.Fit(samples);

        Assert.Equal(0.12f, zone.Radius, tolerance: 0.01f);
    }

    [Fact]
    public void ContainsTheCentreButNotBeyondTheRadius()
    {
        var zone = new IdleZone(new Point2(0.1f, 0.2f), 0.06f);

        Assert.True(zone.Contains(new Vector2(0.1f, 0.2f)));
        Assert.True(zone.Contains(new Vector2(0.15f, 0.2f)));
        Assert.False(zone.Contains(new Vector2(0.17f, 0.2f)));
    }

    [Fact]
    public void FittingNoSamplesIsRefused()
    {
        Assert.Throws<ArgumentException>(() => IdleZone.Fit([]));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IdleZoneSurvivesARoundTripThroughTheProfile(bool inFrame)
    {
        // JSON has bitten this profile once already: Vector2 silently serialised as zeros.
        // A nullable record nested in a positional record is exactly the shape that can
        // read back wrong without failing, so check both of its states.
        IdleZone? idle = inFrame ? new IdleZone(new Point2(-0.04f, 0.21f), 0.07f) : null;

        var mapping = new MonitorMapping(
            @"\\.\DISPLAY1",
            "Test monitor",
            [1, 0, 0, 0, 1, 0, 0, 0],
            idle,
            [new(-0.2f, -0.15f), new(0.2f, -0.15f), new(0.2f, 0.15f), new(-0.2f, 0.15f)],
            1920,
            1200,
            DateTimeOffset.UtcNow);

        string path = Path.Combine(Path.GetTempPath(), $"freethrow-idle-{Guid.NewGuid():N}.json");

        try
        {
            new SpatialProfile().With(mapping).Save(path);
            MonitorMapping? restored = SpatialProfile.Load(path)?.Find(@"\\.\DISPLAY1");

            Assert.NotNull(restored);
            Assert.Equal(idle, restored.Idle);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
