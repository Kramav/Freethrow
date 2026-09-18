using System.Numerics;
using Freethrow.Core.Config;
using Freethrow.Core.Spatial;

namespace Freethrow.Core.Tests;

/// <summary>
/// Tests for choosing and fitting a screen mapping, including the side-to-side mode for a
/// camera that cannot see vertical reach, and for storing which kind was fitted.
/// </summary>
public class ScreenMappingTests
{
    // The reference laptop's display: the aspect ratio the thresholds were reasoned against.
    private const int Width = 1920;
    private const int Height = 1200;

    [Fact]
    public void FourHealthyCornersFitAFullMapping()
    {
        Vector2[] corners = Box(0.40f, 0.30f);

        (ScreenMapping? mapping, string? explanation, string? problem) = MappingFit.Fit(corners, Width, Height);

        Assert.Null(problem);
        Assert.NotNull(explanation);
        Assert.Equal(MappingKind.Homography, mapping!.Kind);
        Assert.True(mapping.HasVertical);

        Vector2[] unitSquare = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        for (int i = 0; i < 4; i++)
        {
            ScreenPoint mapped = mapping.Map(corners[i]);
            Assert.Equal(unitSquare[i].X, mapped.X, tolerance: 0.0001f);
            Assert.Equal(unitSquare[i].Y, mapped.Y!.Value, tolerance: 0.0001f);
        }
    }

    [Fact]
    public void AWideFlatCaptureIsFittedSideToSide()
    {
        // The lid-camera case. 45 x 8 cm is convex, encloses 360 cm² and would solve cleanly
        // as a homography — which is exactly why flatness has to be judged before fitting.
        (ScreenMapping? mapping, string? explanation, _) = MappingFit.Fit(Box(0.45f, 0.08f), Width, Height);

        Assert.Equal(MappingKind.HorizontalOnly, mapping!.Kind);
        Assert.False(mapping.HasVertical);
        Assert.Contains("side to side", explanation);
    }

    [Fact]
    public void ASideToSideMappingReportsNoHeightAtAll()
    {
        (ScreenMapping? mapping, _, _) = MappingFit.Fit(Box(0.45f, 0.08f), Width, Height);

        // Deliberately far apart vertically: height must play no part in the result.
        ScreenPoint left = mapping!.Map(new Vector2(-0.225f, 0.5f));
        ScreenPoint right = mapping.Map(new Vector2(0.225f, -0.5f));

        Assert.True(left.IsMapped);
        Assert.Equal(0f, left.X, tolerance: 0.0001f);
        Assert.Equal(1f, right.X, tolerance: 0.0001f);
        Assert.Null(left.Y);
        Assert.Null(right.Y);
    }

    [Theory]
    [InlineData(0.13f, MappingKind.Homography)]
    [InlineData(0.12f, MappingKind.HorizontalOnly)]
    public void VerticalMustBeAtLeastHalfAScreenShapedReach(float height, MappingKind expected)
    {
        // At 40 cm across on a 16:10 screen, a reach shaped like the screen is 25 cm tall, so
        // the vertical needs 12.5 cm to be no more than twice as sensitive as the horizontal.
        // Both heights clear the 10 cm floor, so this is the ratio deciding, not the floor.
        (ScreenMapping? mapping, _, _) = MappingFit.Fit(Box(0.40f, height), Width, Height);

        Assert.Equal(expected, mapping!.Kind);
    }

    [Fact]
    public void CornersCapturedOutOfOrderAreRefusedNotFittedSideToSide()
    {
        // Top-right and bottom-right swapped. Edge means average top with bottom here, so the
        // vertical span collapses to nothing — and without the convexity check first, this
        // would be quietly fitted side to side and blamed on the camera.
        Vector2[] swapped =
        [
            new(-0.20f, -0.15f),
            new(0.20f, 0.15f),
            new(0.20f, -0.15f),
            new(-0.20f, 0.15f),
        ];

        (ScreenMapping? mapping, _, string? problem) = MappingFit.Fit(swapped, Width, Height);

        Assert.Null(mapping);
        Assert.Contains("order", problem);
    }

    [Fact]
    public void AnEnvelopeTooSmallToUseIsStillRefused()
    {
        // Side to side must not become a way to rescue a hand that barely moved.
        (ScreenMapping? mapping, _, string? problem) = MappingFit.Fit(Box(0.02f, 0.02f), Width, Height);

        Assert.Null(mapping);
        Assert.Contains("too small", problem);
    }

    [Fact]
    public void TwoPointsFitSideToSide()
    {
        (ScreenMapping? mapping, string? explanation, string? problem) =
            MappingFit.Fit([new(-0.20f, 0.03f), new(0.20f, -0.02f)], Width, Height);

        Assert.Null(problem);
        Assert.NotNull(explanation);
        Assert.Equal(MappingKind.HorizontalOnly, mapping!.Kind);
    }

    [Theory]
    [InlineData(0.04f)] // the hand barely moved
    [InlineData(0f)]    // the same point twice: must refuse, not divide by zero
    public void TwoPointsTooCloseAreRefused(float apart)
    {
        (ScreenMapping? mapping, _, string? problem) =
            MappingFit.Fit([new(0f, 0f), new(apart, 0f)], Width, Height);

        Assert.Null(mapping);
        Assert.Contains("too small", problem);
    }

    [Fact]
    public void TwoPointsKeepTheOrderTheyWereReachedIn()
    {
        // Metric X may run either way relative to the screen, depending on the camera mirror,
        // and nothing has yet measured which on a real profile. The first point reached is the
        // screen's left whatever its sign; sorting would bake a guess into every fit.
        (ScreenMapping? mapping, _, _) = MappingFit.Fit([new(0.20f, 0f), new(-0.20f, 0f)], Width, Height);

        Assert.Equal(0f, mapping!.Map(new Vector2(0.20f, 0f)).X, tolerance: 0.0001f);
        Assert.Equal(1f, mapping.Map(new Vector2(-0.20f, 0f)).X, tolerance: 0.0001f);
    }

    [Fact]
    public void MirroredFlatCornersStillMapLeftToRight()
    {
        // The same guarantee for four corners: a flat capture whose metric X runs right to left.
        Vector2[] mirrored =
        [
            new(0.225f, -0.04f),
            new(-0.225f, -0.04f),
            new(-0.225f, 0.04f),
            new(0.225f, 0.04f),
        ];

        (ScreenMapping? mapping, _, _) = MappingFit.Fit(mirrored, Width, Height);

        Assert.Equal(MappingKind.HorizontalOnly, mapping!.Kind);
        Assert.Equal(0f, mapping.Map(mirrored[0]).X, tolerance: 0.0001f);
        Assert.Equal(1f, mapping.Map(mirrored[1]).X, tolerance: 0.0001f);
    }

    [Fact]
    public void NothingIsMappedByDefault()
    {
        // Every uninitialised field and array element is default(ScreenPoint); it must not
        // read as a hand parked in the screen's top-left corner.
        ScreenPoint point = default;

        Assert.False(point.IsMapped);
        Assert.True(float.IsNaN(point.X));
        Assert.Null(point.Y);
    }

    [Fact]
    public void AProfileWrittenBeforeKindExistedLoadsAsAHomography()
    {
        // A literal file in the old format rather than a round-trip: round-tripping a freshly
        // built object proves nothing about files written before the field existed.
        const string oldFormat = """
            {
              "Monitors": [
                {
                  "DeviceName": "\\\\.\\DISPLAY1",
                  "Description": "Old monitor",
                  "Coefficients": [1, 0, 0, 0, 1, 0, 0, 0],
                  "Idle": null,
                  "Corners": [
                    { "X": -0.2, "Y": -0.15 },
                    { "X": 0.2, "Y": -0.15 },
                    { "X": 0.2, "Y": 0.15 },
                    { "X": -0.2, "Y": 0.15 }
                  ],
                  "Width": 1920,
                  "Height": 1200,
                  "CalibratedAt": "2026-09-04T18:31:24+00:00"
                }
              ],
              "MaxReachMin": null,
              "MaxReachMax": null
            }
            """;

        string path = TempPath();
        try
        {
            File.WriteAllText(path, oldFormat);
            MonitorMapping? restored = SpatialProfile.Load(path)?.Find(@"\\.\DISPLAY1");

            Assert.NotNull(restored);
            Assert.Equal(MappingKind.Homography, restored.Kind);

            ScreenPoint mapped = restored.ToMapping().Map(new Vector2(0.3f, 0.4f));
            Assert.Equal(0.3f, mapped.X, tolerance: 0.0001f);
            Assert.Equal(0.4f, mapped.Y!.Value, tolerance: 0.0001f);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ASideToSideProfileNeverReportsAHeightAfterReloading()
    {
        // Persistence is the boundary a later refactor is most likely to cross, so the
        // no-fabricated-height guarantee is pinned on the far side of it.
        Vector2[] points = [new(-0.20f, 0f), new(0.20f, 0f)];
        (ScreenMapping? fitted, _, _) = MappingFit.Fit(points, Width, Height);

        var mapping = new MonitorMapping(
            @"\\.\DISPLAY1",
            "Laptop",
            fitted!.ToArray(),
            null,
            [.. points.Select(Point2.From)],
            Width,
            Height,
            DateTimeOffset.UtcNow,
            fitted.Kind);

        string path = TempPath();
        try
        {
            new SpatialProfile().With(mapping).Save(path);
            string json = File.ReadAllText(path);
            MonitorMapping? restored = SpatialProfile.Load(path)?.Find(@"\\.\DISPLAY1");

            Assert.Contains("\"HorizontalOnly\"", json);
            Assert.NotNull(restored);
            Assert.Equal(MappingKind.HorizontalOnly, restored.Kind);
            Assert.Equal(2, restored.Corners.Length);

            ScreenPoint mapped = restored.ToMapping().Map(new Vector2(0f, 0.1f));
            Assert.Equal(0.5f, mapped.X, tolerance: 0.0001f);
            Assert.Null(mapped.Y);
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>Four corners of a reach centred on the frame, in capture order: TL, TR, BR, BL.</summary>
    private static Vector2[] Box(float width, float height) =>
    [
        new(-width / 2, -height / 2),
        new(width / 2, -height / 2),
        new(width / 2, height / 2),
        new(-width / 2, height / 2),
    ];

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"freethrow-mapping-{Guid.NewGuid():N}.json");
}
