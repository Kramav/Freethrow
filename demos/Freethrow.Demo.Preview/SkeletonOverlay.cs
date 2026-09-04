using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Freethrow.Core.Gestures;
using Freethrow.Core.Perception;

namespace Freethrow.Demo.Preview;

/// <summary>
/// Draws the tracked hand skeleton over the camera preview.
/// </summary>
/// <remarks>
/// Colour carries the gesture state, because a number in a status bar is not something
/// you can read while your hand is in the air. Watching the skeleton change colour as
/// the hand closes is the fastest way to tell whether the grab thresholds are tuned.
/// </remarks>
public sealed class SkeletonOverlay : FrameworkElement
{
    private static readonly Brush HoverBrush = Frozen(Color.FromRgb(0x4C, 0xC9, 0xF0));
    private static readonly Brush GrabBrush = Frozen(Color.FromRgb(0xF2, 0xA6, 0x5A));
    private static readonly Brush BlockedBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush JointBrush = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush BlockedJointBrush = Frozen(Color.FromRgb(0x9C, 0xA3, 0xAF));

    private static readonly Pen HoverPen = FrozenPen(HoverBrush, 2.0);
    private static readonly Pen GrabPen = FrozenPen(GrabBrush, 3.0);
    private static readonly Pen BlockedPen = FrozenPen(BlockedBrush, 1.5);

    private HandPose? _pose;
    private GestureState _state;
    private bool _isArmingBlocked;
    private int _frameWidth;
    private int _frameHeight;

    /// <summary>Sets the hand to draw, or clears it when none is tracked.</summary>
    /// <remarks>
    /// <paramref name="isArmingBlocked"/> renders grey rather than blue. Without it, a
    /// hand the system is deliberately refusing to act on looks identical to one it is
    /// happily watching, and the refusal reads as the tracker being broken.
    /// </remarks>
    public void Show(
        HandPose? pose,
        GestureState state,
        bool isArmingBlocked,
        int frameWidth,
        int frameHeight)
    {
        _pose = pose;
        _state = state;
        _isArmingBlocked = isArmingBlocked;
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (_pose is null || _frameWidth <= 0 || _frameHeight <= 0)
        {
            return;
        }

        // Reproduce the uniform letterbox the Image control applies, so landmarks land
        // on the hand rather than beside it.
        double scale = Math.Min(ActualWidth / _frameWidth, ActualHeight / _frameHeight);
        double offsetX = (ActualWidth - (_frameWidth * scale)) / 2;
        double offsetY = (ActualHeight - (_frameHeight * scale)) / 2;

        bool held = _state == GestureState.Grab;
        Pen pen = held ? GrabPen : _isArmingBlocked ? BlockedPen : HoverPen;
        Brush jointBrush = !held && _isArmingBlocked ? BlockedJointBrush : JointBrush;
        double jointRadius = held ? 3.5 : _isArmingBlocked ? 1.8 : 2.5;

        foreach ((HandLandmark from, HandLandmark to) in HandSkeleton.Bones)
        {
            drawingContext.DrawLine(pen, Map(_pose[from]), Map(_pose[to]));
        }

        for (int i = 0; i < HandPose.LandmarkCount; i++)
        {
            drawingContext.DrawEllipse(jointBrush, null, Map(_pose[(HandLandmark)i]), jointRadius, jointRadius);
        }

        Point Map(Vector2 point) => new(
            offsetX + (point.X * scale),
            offsetY + (point.Y * scale));
    }

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Brush brush, double thickness)
    {
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }
}
