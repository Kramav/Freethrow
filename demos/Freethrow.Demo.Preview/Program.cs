using System.Windows;
using Freethrow.Core.Capture;
using Freethrow.Core.Diagnostics;
using Freethrow.Desktop.Capture;

namespace Freethrow.Demo.Preview;

/// <summary>
/// Entry point. With no arguments this opens the preview window; with
/// <c>--list</c> or <c>--probe</c> it answers on the console instead, which is how
/// capture gets verified on a machine before any UI exists.
/// </summary>
internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
                return application.Run(new MainWindow());
            }

            // Camera work runs on the thread pool (MTA) rather than this STA thread:
            // MediaCapture is happier there, and blocking an STA thread on COM async
            // work is a reliable way to deadlock.
            return args[0].ToLowerInvariant() switch
            {
                "--list" or "-l" => Task.Run(ListDevicesAsync).GetAwaiter().GetResult(),
                "--probe" or "-p" => Task.Run(() => ProbeAsync(args)).GetAwaiter().GetResult(),
                "--help" or "-h" or "/?" => PrintUsage(0),
                _ => PrintUnknownArgument(args[0]),
            };
        }
        catch (CameraException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 2;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    /// <summary>Prints every camera source, infrared included.</summary>
    private static async Task<int> ListDevicesAsync()
    {
        IReadOnlyList<CameraDeviceInfo> devices = await new WindowsCameraEnumerator().EnumerateAsync();

        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No cameras found.");
            return 2;
        }

        Console.WriteLine($"{devices.Count} camera source(s):");
        for (int i = 0; i < devices.Count; i++)
        {
            CameraDeviceInfo device = devices[i];
            Console.WriteLine($"  [{i}] {device.GroupName}");
            Console.WriteLine($"      kind  : {device.Kind}");
            Console.WriteLine($"      id    : {device.Id}");
        }

        return 0;
    }

    /// <summary>
    /// Opens a camera, streams for a few seconds and reports what actually happened:
    /// negotiated format, frame rate, dropped frames, latency, and bytes allocated per
    /// frame. That last number is the one that shows whether frame pooling is working.
    /// </summary>
    private static async Task<int> ProbeAsync(string[] args)
    {
        int requestedIndex = args.Length > 1 && int.TryParse(args[1], out int parsedIndex) ? parsedIndex : -1;
        double seconds = args.Length > 2 && double.TryParse(args[2], out double parsedSeconds) ? parsedSeconds : 3;

        var enumerator = new WindowsCameraEnumerator();
        IReadOnlyList<CameraDeviceInfo> devices = await enumerator.EnumerateAsync();

        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No cameras found.");
            return 2;
        }

        if (requestedIndex >= devices.Count)
        {
            Console.Error.WriteLine($"No camera at index {requestedIndex}; {devices.Count} available.");
            return 2;
        }

        CameraDeviceInfo device = requestedIndex >= 0
            ? devices[requestedIndex]
            : devices.FirstOrDefault(d => d.Kind == CameraKind.Color) ?? devices[0];

        await using ICameraSource source = await enumerator.OpenAsync(device);

        Console.WriteLine($"device  : {device.GroupName} ({device.Kind})");
        Console.WriteLine($"format  : {source.ActiveFormat}");
        Console.WriteLine($"offers  : {source.SupportedFormats.Count} format(s)");
        Console.WriteLine($"probing : {seconds:0.#}s ...");

        var frameRate = new RateCounter();
        var latency = new MovingAverage();
        int frames = 0;
        string description = "(no frames)";

        void OnFrameArrived(object? sender, FrameEventArgs e)
        {
            frameRate.Tick();
            latency.Add(e.Frame.AgeMilliseconds);
            Interlocked.Increment(ref frames);
            description = $"{e.Frame.Width}x{e.Frame.Height} {e.Frame.Format}";
        }

        source.FrameArrived += OnFrameArrived;
        await source.StartAsync();

        long allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        long allocatedAfter = GC.GetTotalAllocatedBytes(precise: true);

        await source.StopAsync();
        source.FrameArrived -= OnFrameArrived;

        int captured = Volatile.Read(ref frames);
        Console.WriteLine();
        Console.WriteLine($"frames    : {captured} delivered, {source.FramesDropped} dropped");
        if (source.LastDropReason is { } dropReason)
        {
            Console.WriteLine($"last drop : {dropReason}");
        }

        Console.WriteLine($"rate      : {captured / seconds:0.0} fps average over the probe, "
            + $"{frameRate.PerSecond:0.0} fps at the end");
        Console.WriteLine($"latency   : {latency.Value:0.0} ms mean, {latency.Max:0.0} ms worst");
        Console.WriteLine($"frame     : {description}");
        Console.WriteLine($"allocated : {(allocatedAfter - allocatedBefore) / 1024.0:0} KB total, "
            + $"{(captured > 0 ? (allocatedAfter - allocatedBefore) / (double)captured : 0):0} B/frame");
        Console.WriteLine($"working set: {Environment.WorkingSet / (1024.0 * 1024.0):0} MB");

        if (captured == 0)
        {
            Console.Error.WriteLine();
            Console.Error.WriteLine("No frames arrived. The camera opened but delivered nothing.");
            return 3;
        }

        return 0;
    }

    private static int PrintUsage(int exitCode)
    {
        Console.WriteLine("Freethrow preview");
        Console.WriteLine();
        Console.WriteLine("  (no arguments)              open the preview window");
        Console.WriteLine("  --list                      list camera sources, infrared included");
        Console.WriteLine("  --probe [index] [seconds]   stream briefly and report capture health");
        Console.WriteLine();
        Console.WriteLine("Index comes from --list. Without one, the first colour camera is used.");
        return exitCode;
    }

    private static int PrintUnknownArgument(string argument)
    {
        Console.Error.WriteLine($"Unrecognised argument '{argument}'.");
        Console.Error.WriteLine();
        PrintUsage(0);
        return 64;
    }
}
