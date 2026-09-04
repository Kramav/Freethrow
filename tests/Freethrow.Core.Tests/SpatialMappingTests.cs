using System.Numerics;
using Freethrow.Core.Spatial;

namespace Freethrow.Core.Tests;

/// <summary>
/// Tests for the hand-to-screen mapping: the metric conversion that makes it
/// depth-independent, and the homography that carries it onto a monitor.
/// </summary>
public class SpatialMappingTests
{
    /// <summary>Corners in the order calibration captures them: TL, TR, BR, BL.</summary>
    private static readonly Vector2[] UnitSquare =
    [
        new(0, 0),
        new(1, 0),
        new(1, 1),
        new(0, 1),
    ];

    [Fact]
    public void MetricPositionIsUnchangedByDistanceFromCamera()
    {
        // The property the whole mapping rests on. The same hand in the same place, seen
        // from two distances, must resolve to the same metric position — otherwise
        // leaning toward the screen would silently drag the mapping with it.
        var offset = new Vector2(0.10f, -0.05f);

        Vector2 near = HandSpace.ToMetric(
            SyntheticHand.Create(1.8f, pixelsPerMetre: 1200f, offsetMetres: offset), 640, 480);

        Vector2 far = HandSpace.ToMetric(
            SyntheticHand.Create(1.8f, pixelsPerMetre: 600f, offsetMetres: offset), 640, 480);

        Assert.Equal(near.X, far.X, tolerance: 0.001f);
        Assert.Equal(near.Y, far.Y, tolerance: 0.001f);
    }

    [Fact]
    public void MetricPositionTracksLateralMovement()
    {
        Vector2 centre = HandSpace.ToMetric(SyntheticHand.Create(1.8f), 640, 480);

        Vector2 moved = HandSpace.ToMetric(
            SyntheticHand.Create(1.8f, offsetMetres: new Vector2(0.2f, 0)), 640, 480);

        Assert.Equal(0.2f, moved.X - centre.X, tolerance: 0.001f);
        Assert.Equal(0f, moved.Y - centre.Y, tolerance: 0.001f);
    }

    [Fact]
    public void CornersMapExactlyOntoTheDestination()
    {
        Vector2[] hand = Envelope();

        (Homography? transform, string? problem) = Homography.TryFit(hand, UnitSquare);

        Assert.Null(problem);
        Assert.NotNull(transform);

        for (int i = 0; i < 4; i++)
        {
            Vector2 mapped = transform.Map(hand[i]);
            Assert.Equal(UnitSquare[i].X, mapped.X, tolerance: 0.0001f);
            Assert.Equal(UnitSquare[i].Y, mapped.Y, tolerance: 0.0001f);
        }
    }

    [Fact]
    public void CentreOfASymmetricEnvelopeMapsToCentreOfScreen()
    {
        (Homography? transform, _) = Homography.TryFit(Envelope(), UnitSquare);

        Vector2 mapped = transform!.Map(Vector2.Zero);

        Assert.Equal(0.5f, mapped.X, tolerance: 0.001f);
        Assert.Equal(0.5f, mapped.Y, tolerance: 0.001f);
    }

    [Fact]
    public void KeystonedEnvelopeStillMapsCornersExactly()
    {
        // A trapezoid: the far edge of the reach covers fewer pixels than the near edge,
        // which is exactly what a camera looking across the movement plane produces. An
        // affine fit cannot satisfy all four corners here; a homography can.
        Vector2[] keystoned =
        [
            new(-0.14f, -0.15f),
            new(0.14f, -0.15f),
            new(0.22f, 0.15f),
            new(-0.22f, 0.15f),
        ];

        (Homography? transform, string? problem) = Homography.TryFit(keystoned, UnitSquare);

        Assert.Null(problem);

        for (int i = 0; i < 4; i++)
        {
            Vector2 mapped = transform!.Map(keystoned[i]);
            Assert.Equal(UnitSquare[i].X, mapped.X, tolerance: 0.0001f);
            Assert.Equal(UnitSquare[i].Y, mapped.Y, tolerance: 0.0001f);
        }
    }

    [Fact]
    public void CollinearCornersAreRejected()
    {
        Vector2[] collinear =
        [
            new(-0.2f, 0),
            new(-0.1f, 0),
            new(0.1f, 0),
            new(0.2f, 0),
        ];

        (Homography? transform, string? problem) = Homography.TryFit(collinear, UnitSquare);

        Assert.Null(transform);
        Assert.NotNull(problem);
    }

    [Fact]
    public void EnvelopeTooSmallToBeUsefulIsRejected()
    {
        // Two centimetres square: the hand barely moved, and a mapping fitted to it would
        // throw the cursor across the screen on the slightest twitch.
        Vector2[] tiny =
        [
            new(-0.01f, -0.01f),
            new(0.01f, -0.01f),
            new(0.01f, 0.01f),
            new(-0.01f, 0.01f),
        ];

        (Homography? transform, string? problem) = Homography.TryFit(tiny, UnitSquare);

        Assert.Null(transform);
        Assert.Contains("too small", problem);
    }

    [Fact]
    public void CornersCapturedOutOfOrderAreRejected()
    {
        // Swapping two corners produces a bow-tie. Fitting it yields a transform that
        // folds over itself, so it is refused with an explanation instead.
        Vector2[] bowTie =
        [
            new(-0.2f, -0.15f),
            new(0.2f, -0.15f),
            new(-0.2f, 0.15f),
            new(0.2f, 0.15f),
        ];

        (Homography? transform, string? problem) = Homography.TryFit(bowTie, UnitSquare);

        Assert.Null(transform);
        Assert.NotNull(problem);
    }

    [Fact]
    public void CoefficientsSurviveARoundTripThroughStorage()
    {
        (Homography? original, _) = Homography.TryFit(Envelope(), UnitSquare);

        Homography restored = Homography.FromArray(original!.ToArray());

        var probe = new Vector2(0.05f, -0.03f);
        Assert.Equal(original.Map(probe).X, restored.Map(probe).X, tolerance: 0.0001f);
        Assert.Equal(original.Map(probe).Y, restored.Map(probe).Y, tolerance: 0.0001f);
    }

    /// <summary>A plausible comfortable reach envelope: 40 cm wide, 30 cm tall.</summary>
    private static Vector2[] Envelope() =>
    [
        new(-0.20f, -0.15f),
        new(0.20f, -0.15f),
        new(0.20f, 0.15f),
        new(-0.20f, 0.15f),
    ];
}
