using System.Numerics;
using Freethrow.Core.Spatial;

namespace Freethrow.Core.Tests;

/// <summary>
/// Tests for detecting a hand cut off by the edge of the camera's view.
/// </summary>
/// <remarks>
/// A synthetic hand at the default scale spans from its wrist at the offset to its
/// fingertips about 157 px to the right, all on one row. At 640×480 the 2% margin is
/// 12.8 px horizontally and 9.6 px vertically.
/// </remarks>
public class FrameFitTests
{
    private const int Width = 640;
    private const int Height = 480;

    [Fact]
    public void CentredHandTouchesNoEdge()
    {
        Assert.Equal(FrameEdge.None, Edges(Vector2.Zero));
    }

    [Theory]
    [InlineData(-0.35f, 0f, FrameEdge.Left)]
    [InlineData(0.20f, 0f, FrameEdge.Right)]
    [InlineData(0f, -0.25f, FrameEdge.Top)]
    [InlineData(0f, 0.25f, FrameEdge.Bottom)]
    public void HandPastOneBorderReportsExactlyThatBorder(float x, float y, FrameEdge expected)
    {
        Assert.Equal(expected, Edges(new Vector2(x, y)));
    }

    [Fact]
    public void HandInACornerReportsBothBorders()
    {
        Assert.Equal(FrameEdge.Left | FrameEdge.Top, Edges(new Vector2(-0.35f, -0.25f)));
    }

    [Fact]
    public void HandInsideTheMarginCountsAsTouching()
    {
        // Wrist at x = 10 px: still inside the frame, but too close to trust the fingers
        // on that side.
        Assert.Equal(FrameEdge.Left, Edges(new Vector2(-0.31f, 0)));
    }

    [Fact]
    public void HandJustClearOfTheMarginDoesNot()
    {
        // Wrist at x = 20 px, clear of the 12.8 px margin.
        Assert.Equal(FrameEdge.None, Edges(new Vector2(-0.30f, 0)));
    }

    private static FrameEdge Edges(Vector2 offsetMetres) =>
        FrameFit.Edges(SyntheticHand.Create(1.8f, offsetMetres: offsetMetres), Width, Height);
}
