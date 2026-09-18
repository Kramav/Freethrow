using System.Diagnostics;
using System.IO;
using System.Numerics;
using System.Windows;
using Freethrow.Core.Capture;
using Freethrow.Core.Config;
using Freethrow.Core.Diagnostics;
using Freethrow.Core.Gestures;
using Freethrow.Core.Perception;
using Freethrow.Core.Perception.Onnx;
using Freethrow.Core.Spatial;
using Freethrow.Desktop.Capture;
using Freethrow.Desktop.Desktop;
using Freethrow.Desktop.Overlay;

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
                "--monitors" or "-m" => ListMonitors(),
                "--overlay" => ShowOverlay(args),
                "--formats" or "-f" => Task.Run(() => ListFormatsAsync(args)).GetAwaiter().GetResult(),
                "--probe" or "-p" => Task.Run(() => ProbeAsync(args)).GetAwaiter().GetResult(),
                "--snap" or "-s" => Task.Run(() => SnapAsync(args)).GetAwaiter().GetResult(),
                "--landmarks" => RunLandmarks(args),
                "--track" or "-t" => Task.Run(() => TrackAsync(args)).GetAwaiter().GetResult(),
                // Runs on this STA thread rather than the pool: it opens a window.
                "--calibrate-grab" or "-c" => RunCalibration(args),
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
    /// Prints every format a camera offers, and marks the one the scorer picked.
    /// </summary>
    /// <remarks>
    /// Exists because "offers : 30 format(s)" is not an answer when a camera delivers
    /// half its nominal rate: the question is whether a better format was on the table
    /// and the scorer passed it over, or whether the device simply has nothing faster.
    /// </remarks>
    private static async Task<int> ListFormatsAsync(string[] args)
    {
        int requestedIndex = args.Length > 1 && int.TryParse(args[1], out int parsedIndex) ? parsedIndex : -1;

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

        Console.WriteLine($"device : {device.GroupName} ({device.Kind})");
        Console.WriteLine($"active : {source.ActiveFormat}");
        Console.WriteLine();

        foreach (CameraFormat format in source.SupportedFormats
            .OrderByDescending(f => f.PixelCount)
            .ThenByDescending(f => f.FrameRate))
        {
            string marker = format == source.ActiveFormat ? " <- active" : string.Empty;
            Console.WriteLine($"  {format}{marker}");
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
        string? subtype = args.Length > 3 ? args[3] : null;

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

        await using ICameraSource source = await enumerator.OpenAsync(
            device,
            new CameraOpenOptions { PreferredSubtype = subtype });

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

        // Read before stopping: PerSecond decays an open window, and StopAsync takes long
        // enough that the settled rate was being divided by the shutdown as well as the
        // stream -- reporting a camera as slower than it is, which is the one direction a
        // capture diagnostic must never be wrong in.
        double endRate = frameRate.PerSecond;

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
            + $"{endRate:0.0} fps at the end");
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

    /// <summary>
    /// Captures a single frame to an uncompressed file, so the exact pixels the pipeline
    /// saw can be replayed or handed to a reference implementation for comparison.
    /// </summary>
    private static async Task<int> SnapAsync(string[] args)
    {
        string path = args.Length > 1 ? args[1] : "frame" + RawFrameFile.Extension;
        int requestedIndex = args.Length > 2 && int.TryParse(args[2], out int parsed) ? parsed : -1;

        var enumerator = new WindowsCameraEnumerator();
        IReadOnlyList<CameraDeviceInfo> devices = await enumerator.EnumerateAsync();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No cameras found.");
            return 2;
        }

        CameraDeviceInfo device = requestedIndex >= 0 && requestedIndex < devices.Count
            ? devices[requestedIndex]
            : devices.FirstOrDefault(d => d.Kind == CameraKind.Color) ?? devices[0];

        await using ICameraSource source = await enumerator.OpenAsync(device);

        var arrived = new TaskCompletionSource<FrameRef>(TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrameArrived(object? sender, FrameEventArgs e)
        {
            // Skip the first few frames: cameras open with auto-exposure still settling,
            // and a black or blown-out frame is a poor thing to test a tracker against.
            if (e.Frame.Sequence >= 10)
            {
                arrived.TrySetResult(e.Frame.Retain());
            }
        }

        source.FrameArrived += OnFrameArrived;
        await source.StartAsync();

        Task completed = await Task.WhenAny(arrived.Task, Task.Delay(TimeSpan.FromSeconds(10)));
        await source.StopAsync();
        source.FrameArrived -= OnFrameArrived;

        if (completed != arrived.Task)
        {
            Console.Error.WriteLine("Timed out waiting for a frame.");
            return 3;
        }

        using FrameRef frame = await arrived.Task;
        RawFrameFile.Save(frame, path);

        Console.WriteLine($"saved {frame.Width}x{frame.Height} {frame.Format} to {Path.GetFullPath(path)}");
        return 0;
    }

    /// <summary>
    /// Runs the tracker over a saved frame and prints every landmark, so the output can
    /// be diffed against a reference implementation given the same input.
    /// </summary>
    private static int RunLandmarks(string[] args)
    {
        if (args.Length < 2)
        {
            Console.Error.WriteLine("Usage: --landmarks <file" + RawFrameFile.Extension + ">");
            return 64;
        }

        using FrameRef frame = RawFrameFile.Load(args[1]);
        using IHandTracker tracker = OnnxHandTracker.Create();

        var stopwatch = Stopwatch.StartNew();
        IReadOnlyList<TrackedHandPose> hands = tracker.Track(frame);
        stopwatch.Stop();

        Console.WriteLine($"frame     : {frame.Width}x{frame.Height} {frame.Format}");
        Console.WriteLine($"inference : {stopwatch.Elapsed.TotalMilliseconds:0.0} ms "
            + $"({tracker.DetectionRuns} detection, {tracker.TrackingRuns} tracking)");
        Console.WriteLine($"hands     : {hands.Count}");

        if (hands.Count == 0)
        {
            Console.WriteLine("result    : no hand detected");
            return 3;
        }

        foreach ((int id, HandPose found) in hands)
        {
            Console.WriteLine();
            Console.WriteLine($"hand {id}    : {found.Handedness}, confidence {found.Confidence:0.000}, "
                + $"openness {HandMetrics.Openness(found):0.000}, "
                + $"palm ({HandMetrics.PalmCenter(found).X:0}, {HandMetrics.PalmCenter(found).Y:0})");
        }

        Console.WriteLine();

        // Full detail for the best hand only; the reference comparison diffs one hand.
        HandPose pose = hands.OrderByDescending(hand => hand.Pose.Confidence).First().Pose;

        Console.WriteLine($"handedness: {pose.Handedness}");
        Console.WriteLine($"confidence: {pose.Confidence:0.000}");
        Console.WriteLine($"scale     : {HandMetrics.Scale(pose):0.00} px screen, "
            + $"{HandMetrics.WorldScale(pose):0.0000} world");
        Console.WriteLine($"openness  : {HandMetrics.Openness(pose):0.000} world, "
            + $"{HandMetrics.ProjectedOpenness(pose):0.000} projected");
        Console.WriteLine($"view align: {HandMetrics.ViewAxisAlignment(pose):0.000} "
            + "(0 = flat to camera, 1 = pointing at it)");
        Console.WriteLine($"palm      : {HandMetrics.PalmCenter(pose).X:0.0}, {HandMetrics.PalmCenter(pose).Y:0.0}");
        Console.WriteLine("landmarks : screen x y z | world x y z");

        for (int i = 0; i < HandPose.LandmarkCount; i++)
        {
            Vector3 point = pose.Landmarks[i];
            Vector3 world = pose.WorldLandmarks[i];
            Console.WriteLine(
                $"  {i,2} {(HandLandmark)i,-10} {point.X,8:0.00} {point.Y,8:0.00} {point.Z,7:0.00} | "
                + $"{world.X,8:0.0000} {world.Y,8:0.0000} {world.Z,8:0.0000}");
        }

        return 0;
    }

    /// <summary>
    /// Tracks a hand from the live camera and reports how the perception budget is
    /// actually being spent.
    /// </summary>
    private static async Task<int> TrackAsync(string[] args)
    {
        double seconds = args.Length > 1 && double.TryParse(args[1], out double parsed) ? parsed : 10;
        int requestedIndex = args.Length > 2 && int.TryParse(args[2], out int index) ? index : -1;

        var enumerator = new WindowsCameraEnumerator();
        IReadOnlyList<CameraDeviceInfo> devices = await enumerator.EnumerateAsync();
        if (devices.Count == 0)
        {
            Console.Error.WriteLine("No cameras found.");
            return 2;
        }

        CameraDeviceInfo device = requestedIndex >= 0 && requestedIndex < devices.Count
            ? devices[requestedIndex]
            : devices.FirstOrDefault(d => d.Kind == CameraKind.Color) ?? devices[0];

        using IHandTracker tracker = OnnxHandTracker.Create();
        GestureOptions gestureOptions = GestureProfile.LoadOptionsOrDefault();

        // Drives the same worker the preview does, so this reports the real multi-hand
        // path including arbitration rather than a simplified stand-in.
        using var worker = new HandTrackingWorker(tracker, gestureOptions);

        int grabs = 0;
        int twoHandFrames = 0;

        // Collected on the worker thread, read once capture has stopped. Confidence is split
        // by hand shape because a fist hides its fingers and scores lower than an open hand,
        // and every gate that acts on confidence was set without measuring that difference.
        var statsGate = new object();
        var confidenceByShape = new Dictionary<string, List<float>>
        {
            ["closed"] = [],
            ["in between"] = [],
            ["open"] = [],
        };
        var viewWidths = new List<float>();
        var holding = new HashSet<int>();
        int released = 0;
        int droppedByTracker = 0;
        int pastGrace = 0;

        void OnResult(object? sender, HandTrackingResult result)
        {
            lock (statsGate)
            {
                foreach (TrackedHand hand in result.Hands)
                {
                    if (hand.Gesture.GrabStarted)
                    {
                        grabs++;
                    }

                    if (hand.Gesture.GrabAborted)
                    {
                        pastGrace++;
                    }
                    else if (hand.Gesture.GrabEnded)
                    {
                        released++;
                    }

                    // Raw, not the recognizer's smoothed openness: that stops updating below
                    // its confidence gate, which is exactly the frames being measured.
                    float openness = HandMetrics.Openness(hand.Pose);
                    string shape = openness <= gestureOptions.GrabOpenness ? "closed"
                        : openness >= gestureOptions.ReleaseOpenness ? "open"
                        : "in between";
                    confidenceByShape[shape].Add(hand.Pose.Confidence);

                    if (hand.DepthProxy > 0)
                    {
                        viewWidths.Add(result.FrameWidth / hand.DepthProxy);
                    }
                }

                // A hand the tracker drops takes its recognizer with it on the same frame, so
                // its grab never reports as aborted — it simply stops appearing. Counted here,
                // or that way of losing a grab would be invisible.
                droppedByTracker += holding.Count(id => !result.Hands.Any(hand => hand.Id == id));

                holding.Clear();
                foreach (TrackedHand hand in result.Hands)
                {
                    if (hand.Gesture.State == GestureState.Grab)
                    {
                        holding.Add(hand.Id);
                    }
                }

                if (result.Hands.Count > 1)
                {
                    twoHandFrames++;
                }
            }
        }

        await using ICameraSource source = await enumerator.OpenAsync(device);

        void OnFrameArrived(object? sender, FrameEventArgs e) => worker.Submit(e.Frame);

        worker.ResultAvailable += OnResult;
        source.FrameArrived += OnFrameArrived;
        await source.StartAsync();

        Console.WriteLine($"device : {device.GroupName}");
        Console.WriteLine($"format : {source.ActiveFormat}");
        Console.WriteLine($"grab   : below {gestureOptions.GrabOpenness:0.00}, "
            + $"release above {gestureOptions.ReleaseOpenness:0.00}, "
            + $"blocked above view {gestureOptions.MaxViewAxisAlignment:0.00}"
            + (GestureProfile.Load() is null ? "  (defaults)" : "  (your profile)"));
        Console.WriteLine();
        Console.WriteLine($"Tracking for {seconds:0.#}s. Raise both hands, then grab with each in turn.");
        Console.WriteLine();

        var reportUntil = Stopwatch.StartNew();
        while (reportUntil.Elapsed.TotalSeconds < seconds)
        {
            await Task.Delay(500);

            HandTrackingResult? latest = worker.Latest;
            string line = latest is null || latest.Hands.Count == 0
                ? "  no hands                                             "
                : "  " + string.Join("  ", latest.Hands.Select(hand =>
                    $"[{hand.Id} {(hand.Id == latest.ControllingId ? "HOLD" : hand.Id == latest.HoverId ? "point" : "idle")}"
                    + $" open {hand.Gesture.Openness:0.00} conf {hand.Pose.Confidence:0.00}"
                    + $" sees {(hand.DepthProxy > 0 ? latest.FrameWidth / hand.DepthProxy * 100 : 0):0} cm]"));

            // Overwrite in place on a console, but write plain lines when redirected —
            // carriage returns turn a captured log into one unreadable smear.
            if (Console.IsOutputRedirected)
            {
                Console.WriteLine(line);
            }
            else
            {
                Console.Write('\r' + line.PadRight(Math.Max(0, Console.WindowWidth - 1)));
            }
        }

        await source.StopAsync();
        source.FrameArrived -= OnFrameArrived;
        worker.ResultAvailable -= OnResult;

        Console.WriteLine();
        Console.WriteLine();
        Console.WriteLine($"frames     : {worker.FramesProcessed} processed, {worker.FramesWithHand} with a hand");
        Console.WriteLine($"two hands  : {twoHandFrames} frames");
        Console.WriteLine($"inference  : {worker.InferenceMilliseconds:0.0} ms mean, "
            + $"{worker.WorstInferenceMilliseconds:0.0} ms worst");
        Console.WriteLine($"model runs : {tracker.DetectionRuns} detection, {tracker.TrackingRuns} tracking");
        lock (statsGate)
        {
            Console.WriteLine($"grabs      : {grabs} started, {released} released, "
                + $"{droppedByTracker + pastGrace} lost ({droppedByTracker} dropped by the tracker, "
                + $"{pastGrace} past the {gestureOptions.TrackingLossGraceSeconds:0.00} s grace)");

            Console.WriteLine("confidence : by hand shape; frames the tracker dropped are not counted");
            foreach ((string shape, List<float> values) in confidenceByShape)
            {
                Console.WriteLine($"  {shape,-10}  {DescribeConfidence(values, gestureOptions.MinConfidence)}");
            }

            if (viewWidths.Count > 0)
            {
                float[] widths = [.. viewWidths.Order()];
                Console.WriteLine($"view width : the camera sees {Percentile(widths, 0.5f) * 100:0} cm across "
                    + $"at your hand (middle 80%: {Percentile(widths, 0.1f) * 100:0}–{Percentile(widths, 0.9f) * 100:0} cm)");
            }
        }

        // Frames with no hand at all legitimately run the detector every time — there is
        // nothing else to do. Only detections beyond those indicate a tracking loop that
        // is losing its grip, so subtract them before judging.
        long unavoidable = worker.FramesProcessed - worker.FramesWithHand;
        long whileTracking = Math.Max(0, tracker.DetectionRuns - unavoidable);

        Console.WriteLine($"detections : {unavoidable} unavoidable (no hand in frame), "
            + $"{whileTracking} while tracking");

        if (worker.FramesWithHand > 30 && whileTracking > worker.FramesWithHand / 2)
        {
            Console.WriteLine();
            Console.WriteLine("Note: the detector ran often even while a hand was tracked, so the tracking "
                + "loop is losing its grip rather than following. Expect higher CPU use than intended.");
        }

        return 0;
    }

    /// <summary>
    /// Summarises confidence for one hand shape against the two gates that act on it.
    /// </summary>
    private static string DescribeConfidence(List<float> values, float gestureGate)
    {
        if (values.Count == 0)
        {
            return "no frames";
        }

        float[] sorted = [.. values.Order()];
        return $"{sorted.Length,5} frames  median {Percentile(sorted, 0.5f):0.00}  min {sorted[0]:0.00}  "
            + $"under {gestureGate:0.00} (grab ignores it) {Share(sorted, gestureGate):0%}  "
            + $"under {CalibrationWindow.MinimumConfidence:0.00} (calibration refuses it) "
            + $"{Share(sorted, CalibrationWindow.MinimumConfidence):0%}";
    }

    private static float Percentile(float[] sorted, float fraction) =>
        sorted[(int)MathF.Round(fraction * (sorted.Length - 1))];

    private static double Share(float[] sorted, float threshold) =>
        sorted.Count(value => value < threshold) / (double)sorted.Length;

    /// <summary>
    /// Lists the attached displays and whether each has a spatial calibration.
    /// </summary>
    private static int ListMonitors()
    {
        IReadOnlyList<MonitorInfo> monitors = MonitorTopology.Enumerate();

        if (monitors.Count == 0)
        {
            Console.Error.WriteLine("No monitors reported.");
            return 2;
        }

        SpatialProfile? profile = SpatialProfile.Load();

        Console.WriteLine($"{monitors.Count} monitor(s):");
        foreach (MonitorInfo monitor in monitors)
        {
            MonitorMapping? mapping = profile?.Find(monitor.DeviceName);

            Console.WriteLine($"  {monitor.DeviceName}  {monitor.Description}");
            Console.WriteLine($"    bounds : {monitor.Width}x{monitor.Height} at ({monitor.Left}, {monitor.Top})");
            Console.WriteLine($"    dpi    : {monitor.Dpi} (scale {monitor.ScaleFactor:0.##}x)"
                + (monitor.IsPrimary ? "  primary" : string.Empty));
            Console.WriteLine($"    mapping: {(mapping is null
                ? "not calibrated"
                : $"calibrated {mapping.CalibratedAt.LocalDateTime:yyyy-MM-dd HH:mm}")}");

            if (mapping is not null)
            {
                Console.WriteLine($"    kind   : {mapping.Kind switch
                {
                    MappingKind.Homography => "full screen (both axes)",
                    MappingKind.HorizontalOnly => "side to side only (height not measured)",
                }}");

                Console.WriteLine($"    idle   : {(mapping.Idle is { } idle
                    ? $"({idle.Centre.X * 100:0}, {idle.Centre.Y * 100:0}) cm from frame centre, radius {idle.Radius * 100:0} cm"
                    : "out of frame")}");
            }
        }

        if (profile is { MaxReachMin: { } min, MaxReachMax: { } max })
        {
            Console.WriteLine();
            Console.WriteLine($"reach envelope: {(max.X - min.X) * 100:0} x {(max.Y - min.Y) * 100:0} cm");
        }

        return 0;
    }

    /// <summary>
    /// Shows the calibration target overlay on each monitor in turn, then exits.
    /// </summary>
    /// <remarks>
    /// Overlay placement is the most fragile platform code here: the window is positioned
    /// in physical pixels while WPF draws in device-independent units, and a mixed-DPI
    /// desktop has no single scale factor that satisfies both. This makes that verifiable
    /// on its own, without walking through a whole calibration to reach it.
    /// </remarks>
    private static int ShowOverlay(string[] args)
    {
        double seconds = args.Length > 1 && double.TryParse(args[1], out double parsed) ? parsed : 4;

        IReadOnlyList<MonitorInfo> monitors = MonitorTopology.Enumerate();
        if (monitors.Count == 0)
        {
            Console.Error.WriteLine("No monitors reported.");
            return 2;
        }

        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var overlay = new CalibrationTargetOverlay();

        int index = 0;
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds),
        };

        void ShowNext()
        {
            if (index >= monitors.Count)
            {
                timer.Stop();
                overlay.Close();
                application.Shutdown();
                return;
            }

            MonitorInfo monitor = monitors[index++];
            Console.WriteLine($"showing on {monitor.DeviceName} ({monitor.Width}x{monitor.Height} "
                + $"at {monitor.Left},{monitor.Top}, {monitor.Dpi} DPI)");

            // Every spot a calibration can ask for, so the side-to-side marks are checked for
            // placement the same way the corners are.
            Spot[] spots = Enum.GetValues<Spot>();

            overlay.ShowOn(monitor);
            overlay.SetTargets(spots);
            overlay.SetTarget(spots[(index - 1) % spots.Length]);
            overlay.SetPointer(ScreenPoint.At(0.5f, 0.5f));
            overlay.SetCaption($"{monitor.Description}\nmarkers should sit just inside each corner "
                + "and halfway down each side");

            overlay.Dispatcher.BeginInvoke(
                System.Windows.Threading.DispatcherPriority.Loaded,
                () =>
                {
                    DpiScale dpi = System.Windows.Media.VisualTreeHelper.GetDpi(overlay);
                    Console.WriteLine($"  wpf dpi scale : {dpi.DpiScaleX:0.###}");
                    Console.WriteLine($"  window size   : {overlay.ActualWidth:0} x {overlay.ActualHeight:0} DIP");
                    Console.WriteLine($"  expected      : {monitor.Width / (monitor.Dpi / 96.0):0} x "
                        + $"{monitor.Height / (monitor.Dpi / 96.0):0} DIP");
                });
        }

        timer.Tick += (_, _) => ShowNext();
        application.Startup += (_, _) =>
        {
            ShowNext();
            timer.Start();
        };

        return application.Run();
    }

    /// <summary>
    /// Opens the calibration window, which fits grab thresholds to the user's own hand.
    /// </summary>
    /// <remarks>
    /// The built-in defaults came from one measured hand. Hands differ by more than the
    /// gap between the grab and release thresholds, which is exactly how a grab ends up
    /// neither committing nor letting go.
    /// </remarks>
    private static int RunCalibration(string[] args)
    {
        // The flag may sit anywhere after the command. Taking args[1] as the path, as this
        // once did, would read "--calibrate-grab --sideways" as a profile saved to a file
        // called "--sideways".
        string[] rest = args[1..];
        ReachMode? reach = rest.Contains("--sideways") ? ReachMode.SideToSide : null;
        string? profilePath = rest.FirstOrDefault(arg => !arg.StartsWith("--", StringComparison.Ordinal));

        var application = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        return application.Run(new CalibrationWindow(profilePath, reach: reach));
    }

    private static int PrintUsage(int exitCode)
    {
        Console.WriteLine("Freethrow preview");
        Console.WriteLine();
        Console.WriteLine("  (no arguments)              open the preview window");
        Console.WriteLine("  --list                      list camera sources, infrared included");
        Console.WriteLine("  --formats [index]           list every format a camera offers");
        Console.WriteLine("  --probe [index] [seconds] [subtype]");
        Console.WriteLine("                              stream briefly and report capture health");
        Console.WriteLine("  --snap [path] [index]       save one frame uncompressed, for replay");
        Console.WriteLine("  --landmarks <path>          run the tracker over a saved frame");
        Console.WriteLine("  --track [seconds] [index]   track a hand live and report the cost");
        Console.WriteLine("  --calibrate-grab [--sideways] [path]");
        Console.WriteLine("                              fit grab thresholds and the screen mapping to");
        Console.WriteLine("                              your own hand; --sideways maps left-to-right only,");
        Console.WriteLine("                              for a camera that cannot see vertical reach");
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
