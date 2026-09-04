using System.Runtime.InteropServices;

namespace Freethrow.Desktop.Desktop;

/// <summary>One display, in virtual-desktop pixel coordinates.</summary>
/// <param name="DeviceName">
/// The stable key, such as <c>\\.\DISPLAY1</c>. Calibration is stored against this
/// rather than an index or a bounds rectangle, both of which change the moment a
/// monitor is unplugged, rearranged, or has its resolution altered.
/// </param>
/// <param name="Description">Human-readable adapter description, for pickers.</param>
/// <param name="Left">Left edge in virtual-desktop pixels; may be negative.</param>
/// <param name="Top">Top edge in virtual-desktop pixels; may be negative.</param>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
/// <param name="Dpi">Effective DPI, 96 being unscaled.</param>
/// <param name="IsPrimary">Whether this is the primary display.</param>
public sealed record MonitorInfo(
    string DeviceName,
    string Description,
    int Left,
    int Top,
    int Width,
    int Height,
    uint Dpi,
    bool IsPrimary)
{
    /// <summary>Centre of the monitor in virtual-desktop pixels.</summary>
    public (int X, int Y) Center => (Left + (Width / 2), Top + (Height / 2));

    /// <summary>Scale factor relative to unscaled 96 DPI.</summary>
    public double ScaleFactor => Dpi / 96.0;

    public override string ToString() =>
        $"{Description} ({Width}x{Height} @ {Dpi} DPI{(IsPrimary ? ", primary" : string.Empty)})";
}

/// <summary>
/// Enumerates the displays attached to the machine.
/// </summary>
/// <remarks>
/// Reported in physical pixels rather than WPF device-independent units. Calibration and
/// window placement both work in the virtual desktop's own coordinate system, and
/// converting to DIPs on a mixed-DPI setup means picking a scale factor that is only
/// correct for one monitor.
/// </remarks>
public static class MonitorTopology
{
    private const int MonitorInfoPrimary = 0x00000001;
    private const int EffectiveDpi = 0;
    private const int DefaultDpi = 96;

    /// <summary>Lists every attached display, primary first.</summary>
    public static IReadOnlyList<MonitorInfo> Enumerate()
    {
        var monitors = new List<MonitorInfo>();

        bool Callback(IntPtr monitor, IntPtr _, ref Rect __, IntPtr ___)
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoW(monitor, ref info))
            {
                return true;
            }

            monitors.Add(new MonitorInfo(
                info.Device,
                DescribeAdapter(info.Device),
                info.Monitor.Left,
                info.Monitor.Top,
                info.Monitor.Right - info.Monitor.Left,
                info.Monitor.Bottom - info.Monitor.Top,
                ResolveDpi(monitor),
                (info.Flags & MonitorInfoPrimary) != 0));

            return true;
        }

        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Callback, IntPtr.Zero);

        return [.. monitors.OrderByDescending(m => m.IsPrimary).ThenBy(m => m.Left)];
    }

    /// <summary>The primary display, or <see langword="null"/> if none reported one.</summary>
    public static MonitorInfo? Primary() => Enumerate().FirstOrDefault(m => m.IsPrimary);

    /// <summary>
    /// Finds the monitor whose centre is nearest the given one, for falling back when a
    /// display has no calibration of its own.
    /// </summary>
    public static MonitorInfo? NearestTo(MonitorInfo target, IEnumerable<MonitorInfo> candidates)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(candidates);

        (int targetX, int targetY) = target.Center;

        return candidates
            .Where(candidate => candidate.DeviceName != target.DeviceName)
            .OrderBy(candidate =>
            {
                (int x, int y) = candidate.Center;
                double dx = x - targetX;
                double dy = y - targetY;
                return (dx * dx) + (dy * dy);
            })
            .FirstOrDefault();
    }

    /// <summary>
    /// Reads a monitor's effective DPI, falling back to 96 on systems or drivers that
    /// decline to report it.
    /// </summary>
    private static uint ResolveDpi(IntPtr monitor)
    {
        try
        {
            if (GetDpiForMonitor(monitor, EffectiveDpi, out uint dpiX, out _) == 0 && dpiX > 0)
            {
                return dpiX;
            }
        }
        catch (DllNotFoundException)
        {
            // shcore.dll predates Windows 8.1; unscaled is the right assumption there.
        }

        return DefaultDpi;
    }

    /// <summary>Looks up the adapter description behind a device name.</summary>
    private static string DescribeAdapter(string deviceName)
    {
        var device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };

        for (uint index = 0; EnumDisplayDevicesW(null, index, ref device, 0); index++)
        {
            if (device.DeviceName == deviceName && !string.IsNullOrWhiteSpace(device.DeviceString))
            {
                return device.DeviceString;
            }

            device = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>() };
        }

        return deviceName;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, ref Rect clip, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr clip,
        MonitorEnumProc callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MonitorInfoEx info);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(
        string? device,
        uint deviceIndex,
        ref DisplayDevice displayDevice,
        uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public uint StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceId;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }
}
