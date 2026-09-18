using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Freethrow.Core.Spatial;
using Freethrow.Desktop.Desktop;

namespace Freethrow.Desktop.Overlay;

/// <summary>
/// A full-screen, click-through overlay that marks where on a monitor the user should
/// reach, and shows where the mapping currently thinks their hand is pointing.
/// </summary>
/// <remarks>
/// <para>
/// Calibration has to put its targets on the screen being calibrated. Describing "the
/// top-left corner of monitor 2" inside a window sitting on monitor 1 asks the user to
/// do the coordinate transform in their head, which is the very thing being measured.
/// </para>
/// <para>
/// The window is positioned in <em>physical</em> pixels through <c>SetWindowPos</c>
/// rather than by setting WPF's Left and Top, which are device-independent units. On a
/// mixed-DPI desktop there is no single scale factor that places a window correctly on
/// every monitor. Drawing then happens in normalised 0..1 coordinates scaled by the
/// element's own size, so the content lands correctly whatever the monitor's DPI.
/// </para>
/// </remarks>
public sealed class CalibrationTargetOverlay : Window
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private static readonly IntPtr HwndTopmost = new(-1);
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    private readonly TargetSurface _surface = new();
    private MonitorInfo? _monitor;

    public CalibrationTargetOverlay()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        Topmost = true;
        Content = _surface;

        SourceInitialized += OnSourceInitialized;
    }

    /// <summary>Shows the overlay covering a given monitor.</summary>
    public void ShowOn(MonitorInfo monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);

        _monitor = monitor;

        // Size in device-independent units as well as forcing the native placement below.
        // MonitorInfo reports physical pixels while WPF lays out in DIPs, so on a scaled
        // display the two differ by the scale factor; stating both keeps the layout size
        // and the window size in agreement rather than relying on one to drive the other.
        double scale = monitor.Dpi / 96.0;
        Left = monitor.Left / scale;
        Top = monitor.Top / scale;
        Width = monitor.Width / scale;
        Height = monitor.Height / scale;

        if (!IsVisible)
        {
            Show();
        }

        Reposition();
    }

    /// <summary>The marks to draw for this calibration. The four corners until told otherwise.</summary>
    public void SetTargets(IReadOnlyList<Spot> spots) => _surface.SetTargets(spots);

    /// <summary>Highlights one of the marks, or none.</summary>
    public void SetTarget(Spot? spot) => _surface.SetTarget(spot);

    /// <summary>Places the live pointer, or hides it with <see cref="ScreenPoint.Nothing"/>.</summary>
    /// <remarks>
    /// A point with no vertical component draws as a full-height band rather than a dot. A
    /// dot would put the hand at a height nothing measured, and the test screen exists to
    /// show what the mapping actually knows.
    /// </remarks>
    public void SetPointer(ScreenPoint point) => _surface.SetPointer(point);

    /// <summary>Sets the caption drawn across the middle of the screen.</summary>
    public void SetCaption(string caption) => _surface.SetCaption(caption);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;

        // Click-through and never focusable: the overlay must not steal input from
        // whatever the user is doing, and must not appear in Alt-Tab.
        int style = GetWindowLong(handle, GwlExStyle);
        SetWindowLong(handle, GwlExStyle, style | WsExTransparent | WsExNoActivate | WsExToolWindow);

        Reposition();
    }

    private void Reposition()
    {
        if (_monitor is not { } monitor)
        {
            return;
        }

        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(
            handle,
            HwndTopmost,
            monitor.Left,
            monitor.Top,
            monitor.Width,
            monitor.Height,
            SwpNoActivate | SwpShowWindow);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr window, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr window, int index, int value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr window,
        IntPtr insertAfter,
        int x,
        int y,
        int width,
        int height,
        uint flags);

    /// <summary>Draws the corner targets, the pointer and the caption.</summary>
    private sealed class TargetSurface : FrameworkElement
    {
        /// <summary>How far in from the screen edge a target sits, as a fraction of the smaller side.</summary>
        private const double InsetFraction = 0.06;

        // Idle markers must stay clearly findable against a busy desktop: at low alpha
        // they vanish into whatever is behind them, and a target you cannot locate is
        // not a target.
        private static readonly Brush IdleBrush = Frozen(Color.FromArgb(0xBB, 0xC8, 0xD2, 0xE0));
        private static readonly Brush ActiveBrush = Frozen(Color.FromArgb(0xFF, 0x4A, 0xDE, 0x80));
        private static readonly Brush PointerBrush = Frozen(Color.FromArgb(0xFF, 0xF2, 0xA6, 0x5A));
        private static readonly Brush BandBrush = Frozen(Color.FromArgb(0x55, 0xF2, 0xA6, 0x5A));
        private static readonly Brush CaptionBrush = Frozen(Color.FromArgb(0xEE, 0xE8, 0xED, 0xF5));
        private static readonly Brush ShadeBrush = Frozen(Color.FromArgb(0x40, 0x00, 0x00, 0x00));

        private static readonly Pen IdlePen = FrozenPen(IdleBrush, 2);
        private static readonly Pen ActivePen = FrozenPen(ActiveBrush, 4);
        private static readonly Pen BandPen = FrozenPen(PointerBrush, 3);

        private IReadOnlyList<Spot> _spots = [Spot.TopLeft, Spot.TopRight, Spot.BottomRight, Spot.BottomLeft];
        private Spot? _target;
        private ScreenPoint _pointer;
        private string _caption = string.Empty;

        public void SetTargets(IReadOnlyList<Spot> spots)
        {
            ArgumentNullException.ThrowIfNull(spots);
            _spots = [.. spots];
            InvalidateVisual();
        }

        public void SetTarget(Spot? spot)
        {
            _target = spot;
            InvalidateVisual();
        }

        public void SetPointer(ScreenPoint point)
        {
            _pointer = point;
            InvalidateVisual();
        }

        public void SetCaption(string caption)
        {
            _caption = caption ?? string.Empty;
            InvalidateVisual();
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            double width = ActualWidth;
            double height = ActualHeight;
            if (width <= 0 || height <= 0)
            {
                return;
            }

            // A wash over the screen so the targets read against whatever is behind them,
            // while leaving the desktop recognisable enough to stay oriented.
            drawingContext.DrawRectangle(ShadeBrush, null, new Rect(0, 0, width, height));

            double inset = Math.Min(width, height) * InsetFraction;

            foreach (Spot spot in _spots)
            {
                DrawTarget(drawingContext, Place(spot, width, height, inset), spot == _target);
            }

            if (_pointer.IsMapped)
            {
                double x = _pointer.X * width;

                if (_pointer.Y is { } y)
                {
                    var position = new Point(x, y * height);
                    drawingContext.DrawEllipse(PointerBrush, null, position, 14, 14);
                    drawingContext.DrawEllipse(null, FrozenPen(CaptionBrush, 2), position, 22, 22);
                }
                else
                {
                    // No height was measured, so the pointer is a column, not a point. A dot
                    // at mid-height would look like a reading and would be believed.
                    drawingContext.DrawRectangle(BandBrush, null, new Rect(x - 10, 0, 20, height));
                    drawingContext.DrawLine(BandPen, new Point(x, 0), new Point(x, height));
                }
            }

            if (_caption.Length > 0)
            {
                DrawCaption(drawingContext, width, height);
            }
        }

        private static Point Place(Spot spot, double width, double height, double inset) => spot switch
        {
            Spot.TopLeft => new(inset, inset),
            Spot.TopRight => new(width - inset, inset),
            Spot.BottomRight => new(width - inset, height - inset),
            Spot.BottomLeft => new(inset, height - inset),
            Spot.MidLeft => new(inset, height / 2),
            Spot.MidRight => new(width - inset, height / 2),
        };

        private static void DrawTarget(DrawingContext drawingContext, Point centre, bool active)
        {
            Pen pen = active ? ActivePen : IdlePen;
            double outer = active ? 46 : 30;

            drawingContext.DrawEllipse(null, pen, centre, outer, outer);
            drawingContext.DrawEllipse(active ? ActiveBrush : IdleBrush, null, centre, 5, 5);

            // Crosshair arms, so the exact point is unambiguous rather than "somewhere in
            // that circle".
            drawingContext.DrawLine(pen, new Point(centre.X - outer - 12, centre.Y), new Point(centre.X - outer + 4, centre.Y));
            drawingContext.DrawLine(pen, new Point(centre.X + outer - 4, centre.Y), new Point(centre.X + outer + 12, centre.Y));
            drawingContext.DrawLine(pen, new Point(centre.X, centre.Y - outer - 12), new Point(centre.X, centre.Y - outer + 4));
            drawingContext.DrawLine(pen, new Point(centre.X, centre.Y + outer - 4), new Point(centre.X, centre.Y + outer + 12));
        }

        private void DrawCaption(DrawingContext drawingContext, double width, double height)
        {
            double boxWidth = width * 0.7;

            var text = new FormattedText(
                _caption,
                System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight,
                new Typeface("Segoe UI"),
                26,
                CaptionBrush,
                VisualTreeHelper.GetDpi(this).PixelsPerDip)
            {
                TextAlignment = TextAlignment.Center,
                MaxTextWidth = boxWidth,
            };

            // DrawText places the text box's top-left at the given point, so centring the
            // text within the box is not enough — the box itself has to be offset by half
            // its width, or it starts at the middle of the screen and runs off the edge.
            var origin = new Point((width - boxWidth) / 2, (height / 2) - (text.Height / 2));

            // A panel behind the text, so it stays readable over any desktop.
            drawingContext.DrawRoundedRectangle(
                ShadeBrush,
                null,
                new Rect(
                    origin.X + ((boxWidth - text.Width) / 2) - 20,
                    origin.Y - 12,
                    text.Width + 40,
                    text.Height + 24),
                8,
                8);

            drawingContext.DrawText(text, origin);
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
}
