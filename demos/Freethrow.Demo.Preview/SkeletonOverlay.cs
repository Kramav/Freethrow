using System.Numerics;
using System.Windows;
using System.Windows.Media;
using Freethrow.Core.Gestures;
using Freethrow.Core.Perception;

namespace Freethrow.Demo.Preview;

/// <summary>One hand to draw, and what it is doing.</summary>
/// <param name="Pose">Landmarks in frame pixels.</param>
/// <param name="State">Gesture state for this hand.</param>
/// <param name="IsArmingBlocked">Whether the pose is too foreshortened to start a grab.</param>
/// <param name="IsControlling">Whether this hand holds the interaction.</param>
/// <param name="IsHover">Whether this hand is designating a target.</param>
public readonly record struct HandRender(
    HandPose Pose,
    GestureState State,
    bool IsArmingBlocked,
    bool IsControlling,
    bool IsHover);

/// <summary>
/// Draws every tracked hand over the camera preview.
/// </summary>
/// <remarks>
/// Colour carries the hand's role, because a number in a status bar is not something you
/// can read while your hands are in the air. With two hands up it must be obvious at a
/// glance which one the system is listening to — otherwise a hand that is being
/// deliberately ignored looks identical to one that is broken.
/// </remarks>
public sealed class SkeletonOverlay : FrameworkElement
{
    private static readonly Brush HoverBrush = Frozen(Color.FromRgb(0x4C, 0xC9, 0xF0));
    private static readonly Brush GrabBrush = Frozen(Color.FromRgb(0xF2, 0xA6, 0x5A));
    private static readonly Brush BlockedBrush = Frozen(Color.FromRgb(0x6B, 0x72, 0x80));
    private static readonly Brush IdleBrush = Frozen(Color.FromArgb(0x88, 0x8B, 0x94, 0xA6));
    private static readonly Brush JointBrush = Frozen(Color.FromRgb(0xFF, 0xFF, 0xFF));
    private static readonly Brush DimJointBrush = Frozen(Color.FromArgb(0x99, 0xC8, 0xD2, 0xE0));

    private static readonly Pen HoverPen = FrozenPen(HoverBrush, 2.0);
    private static readonly Pen GrabPen = FrozenPen(GrabBrush, 3.5);
    private static readonly Pen BlockedPen = FrozenPen(BlockedBrush, 1.5);
    private static readonly Pen IdlePen = FrozenPen(IdleBrush, 1.5);

    private IReadOnlyList<HandRender> _hands = [];
    private int _frameWidth;
    private int _frameHeight;

    /// <summary>Sets the hands to draw, or clears them when none is tracked.</summary>
    public void Show(IReadOnlyList<HandRender> hands, int frameWidth, int frameHeight)
    {
        _hands = hands ?? [];
        _frameWidth = frameWidth;
        _frameHeight = frameHeight;
        InvalidateVisual();
    }

    /// <summary>Clears the overlay.</summary>
    public void Clear() => Show([], 0, 0);

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (_hands.Count == 0 || _frameWidth <= 0 || _frameHeight <= 0)
        {
            return;
        }

        // Reproduce the uniform letterbox the Image control applies, so landmarks land
        // on the hand rather than beside it.
        double scale = Math.Min(ActualWidth / _frameWidth, ActualHeight / _frameHeight);
        double offsetX = (ActualWidth - (_frameWidth * scale)) / 2;
        double offsetY = (ActualHeight - (_frameHeight * scale)) / 2;

        foreach (HandRender hand in _hands)
        {
            Draw(drawingContext, hand, scale, offsetX, offsetY);
        }
    }

    private static void Draw(
        DrawingContext drawingContext,
        HandRender hand,
        double scale,
        double offsetX,
        double offsetY)
    {
        // Held beats pointing beats blocked beats merely present. A hand that is tracked
        // but not listened to is drawn faintly rather than not at all, so it is clear the
        // system can see it and is choosing not to act on it.
        (Pen pen, Brush joints, double radius) = hand switch
        {
            { IsControlling: true } => (GrabPen, JointBrush, 4.0),
            { IsHover: true, IsArmingBlocked: false } => (HoverPen, JointBrush, 2.5),
            { IsArmingBlocked: true } => (BlockedPen, DimJointBrush, 1.8),
            _ => (IdlePen, DimJointBrush, 1.8),
        };

        foreach ((HandLandmark from, HandLandmark to) in HandSkeleton.Bones)
        {
            drawingContext.DrawLine(pen, Map(hand.Pose[from]), Map(hand.Pose[to]));
        }

        for (int i = 0; i < HandPose.LandmarkCount; i++)
        {
            drawingContext.DrawEllipse(joints, null, Map(hand.Pose[(HandLandmark)i]), radius, radius);
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
