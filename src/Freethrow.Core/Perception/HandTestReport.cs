using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Freethrow.Core.Perception;

/// <summary>One scripted phase of the hand test: what the user is asked to do, and for how long.</summary>
/// <param name="Key">Stable identifier, used to line a phase up with the same phase of an older run.</param>
/// <param name="Title">Short name for the results table.</param>
/// <param name="Prompt">The instruction.</param>
/// <param name="Hint">Why this phase exists, or how to do it.</param>
/// <param name="Expected">The shape the hand is asked to make — the label every frame is judged against.</param>
/// <param name="Moving">Whether the hand is asked to move, which is when a held grab is most at risk.</param>
/// <param name="Seconds">How long to record, from the first frame with a hand.</param>
/// <param name="Distance">
/// Which distance this phase is held at, or null for one that moves. An open and a closed
/// phase at the same distance are compared with each other, which is what says how much
/// closing the hand costs, separately from how much the distance does.
/// </param>
public sealed record HandTestPhase(
    string Key,
    string Title,
    string Prompt,
    string Hint,
    HandShape Expected,
    bool Moving,
    double Seconds,
    string? Distance);

/// <summary>What one phase measured, in the form that is saved and compared.</summary>
/// <param name="Tracked">Share of frames with a hand tracked, 0 to 1.</param>
/// <param name="ConfidenceP10">Tenth-percentile landmark confidence, or null with no hand-frames.</param>
/// <param name="ReadAsExpected">Share of hand-frames whose measured shape matched the instruction.</param>
/// <param name="Holding">Share of frames holding a grab: wanted with a fist, a false grab with an open hand.</param>
/// <param name="ViewWidthCm">How wide the camera sees at the hand, median, in centimetres.</param>
public sealed record HandTestPhaseResult(
    string Key,
    string Title,
    HandShape Expected,
    bool Moving,
    string? Distance,
    int Frames,
    double Tracked,
    float? ConfidenceP10,
    float? ConfidenceMedian,
    double ReadAsExpected,
    double Holding,
    int GrabsLost,
    int GrabsDroppedByTracker,
    int GrabsPastGrace,
    float? ViewWidthCm)
{
    public static HandTestPhaseResult From(HandTestPhase phase, TrackingQuality quality)
    {
        ArgumentNullException.ThrowIfNull(phase);
        ArgumentNullException.ThrowIfNull(quality);

        ConfidenceSample confidence = quality.AllConfidence();
        ConfidenceSample widths = quality.ViewWidths();

        return new HandTestPhaseResult(
            phase.Key,
            phase.Title,
            phase.Expected,
            phase.Moving,
            phase.Distance,
            quality.Frames,
            Share(quality.FramesWithHand, quality.Frames),
            confidence.IsEmpty ? null : confidence.Percentile(0.1f),
            confidence.IsEmpty ? null : confidence.Median,
            Share(quality.Confidence(phase.Expected).Count, quality.HandFrames),
            Share(quality.FramesHolding, quality.Frames),
            quality.GrabsLost,
            quality.GrabsDroppedByTracker,
            quality.GrabsPastGrace,
            widths.IsEmpty ? null : widths.Median * 100);
    }

    private static double Share(int part, int whole) => whole == 0 ? 0 : part / (double)whole;
}

/// <summary>A whole run, saved so the next one has something to be compared with.</summary>
public sealed record HandTestRun(
    DateTimeOffset CreatedAt,
    string Machine,
    string Camera,
    string Format,
    float GrabOpenness,
    float ReleaseOpenness,
    float TrackingGate,
    float GestureGate,
    float CalibrationGate,
    List<HandTestPhaseResult> Phases);

/// <summary>One check on a run.</summary>
/// <param name="Verdict"><c>PASS</c>, <c>FAIL</c>, or <c>NOTE</c> for something that is not a fault but changes what to do.</param>
/// <param name="Summary">What was checked, and for a failure, which phases and their values.</param>
/// <param name="Detail">What a failure means for a grab, written once however many phases failed.</param>
public sealed record HandTestCheck(string Verdict, string Summary, string? Detail = null);

/// <summary>Saves, loads, checks and prints hand-test runs.</summary>
/// <remarks>
/// The checks exist because a table of confidences on its own is not an answer: each is
/// judged against the gate in the pipeline that acts on it, so a number arrives already
/// saying what it will do to a grab. The targets follow from those gates, not from data,
/// and the printout says so.
/// </remarks>
public static class HandTestReport
{
    /// <summary>A held pose should be tracked in nearly every frame; one in twenty missing already flickers.</summary>
    private const double MinimumTracked = 0.95;

    /// <summary>Below this, the grab thresholds disagree with the hand often enough to be felt.</summary>
    private const double MinimumReadAsExpected = 0.90;

    /// <summary>Carrying a window, the grab must hold nearly throughout.</summary>
    private const double MinimumHolding = 0.90;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Where runs are kept unless a folder is given.</summary>
    public static string DefaultFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Freethrow",
        "hand-tests");

    public static string ShapeName(HandShape shape) => shape switch
    {
        HandShape.Closed => "closed",
        HandShape.InBetween => "in between",
        HandShape.Open => "open",
    };

    /// <summary>Writes a run, named by its time so the newest sorts last.</summary>
    public static string Save(HandTestRun run, string? folder = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        string resolved = folder ?? DefaultFolder;
        Directory.CreateDirectory(resolved);
        string path = Path.Combine(resolved, $"hand-test-{run.CreatedAt.LocalDateTime:yyyyMMdd-HHmmss}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(run, Json));
        return path;
    }

    public static HandTestRun? Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<HandTestRun>(File.ReadAllText(path), Json);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The most recent run saved, whatever camera it used.</summary>
    public static HandTestRun? LoadLatest(string? folder = null)
    {
        string resolved = folder ?? DefaultFolder;
        if (!Directory.Exists(resolved))
        {
            return null;
        }

        return Directory.GetFiles(resolved, "hand-test-*.json")
            .OrderByDescending(path => path, StringComparer.Ordinal)
            .Select(Load)
            .FirstOrDefault(run => run is not null);
    }

    /// <summary>
    /// The full printout: the run, fist against open hand at each distance, the checks, and
    /// how it compares with <paramref name="previous"/>.
    /// </summary>
    public static string Format(HandTestRun run, HandTestRun? previous)
    {
        ArgumentNullException.ThrowIfNull(run);

        var text = new StringBuilder();

        text.AppendLine($"Hand tracking test  {run.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm}  {run.Machine}  {run.Camera}  {run.Format}");
        text.AppendLine($"Grab below {run.GrabOpenness:0.00}, release above {run.ReleaseOpenness:0.00}. "
            + $"The tracker keeps a hand above {run.TrackingGate:0.00}, a grab needs {run.GestureGate:0.00}, "
            + $"calibration needs {run.CalibrationGate:0.00}.");
        text.AppendLine();

        text.AppendLine($"{"phase",-24}{"tracked",8}{"conf 10%",10}{"median",8}{"reads as asked",18}{"grab held",11}{"lost",6}{"camera sees",13}");
        foreach (HandTestPhaseResult phase in run.Phases)
        {
            string readsAs = $"{ShapeName(phase.Expected)} {phase.ReadAsExpected:0%}";

            text.AppendLine(
                $"{phase.Title,-24}{phase.Tracked,8:0%}{Number(phase.ConfidenceP10),10}{Number(phase.ConfidenceMedian),8}"
                + $"{readsAs,18}{phase.Holding,11:0%}{phase.GrabsLost,6}{Centimetres(phase.ViewWidthCm),13}");
        }

        AppendFistAgainstOpen(text, run);

        text.AppendLine();
        text.AppendLine("Checks — targets follow from the gates above; none has been validated against data yet");
        foreach (HandTestCheck check in Checks(run))
        {
            text.AppendLine($"  {check.Verdict,-5} {check.Summary}");
            if (check.Detail is not null)
            {
                text.AppendLine($"        {check.Detail}");
            }
        }

        if (previous is not null)
        {
            AppendComparison(text, run, previous);
        }

        return text.ToString();
    }

    /// <summary>
    /// Every check. A check is one line however many phases fail it, naming each with its
    /// value, so the explanation is read once and the pattern across distances is visible.
    /// </summary>
    public static IReadOnlyList<HandTestCheck> Checks(HandTestRun run)
    {
        ArgumentNullException.ThrowIfNull(run);

        // A phase with no frames has no evidence either way; judging it would report a
        // tracking fault for a phase that never ran.
        List<HandTestPhaseResult> phases = [.. run.Phases.Where(phase => phase.Frames > 0)];
        List<HandTestPhaseResult> fists = [.. phases.Where(phase => phase.Expected == HandShape.Closed)];

        List<HandTestCheck> checks =
        [
            Check(
                "held poses are tracked in at least 95% of frames",
                phases.Where(phase => phase.Tracked < MinimumTracked)
                    .Select(phase => $"{phase.Title} {phase.Tracked:0%}"),
                "tracked in",
                "A held hand should not drop. If it was near the edge of the view, or very close to the "
                + "camera, that is the likely cause."),

            Check(
                $"a fist stays above the {run.GestureGate:0.00} a grab needs",
                fists.Where(phase => phase.ConfidenceP10 < run.GestureGate)
                    .Select(phase => $"{phase.Title} {Number(phase.ConfidenceP10)}"),
                "one frame in ten under",
                $"Below {run.GestureGate:0.00} a frame does not count toward a grab, so a held grab drops "
                + "in and out."),

            Check(
                "carrying never loses the grab",
                phases.Where(phase => phase.Moving && (phase.GrabsLost > 0 || phase.Holding < MinimumHolding))
                    .Select(phase => $"{phase.Title}, {phase.GrabsLost} lost ({phase.GrabsDroppedByTracker} dropped "
                        + $"by the tracker, {phase.GrabsPastGrace} past the grace), held {phase.Holding:0%}"),
                null,
                "A carried window would be dropped. A grab dropped by the tracker ends at once, without "
                + "the grace a coasting grab gets."),

            Check(
                "every pose reads as the shape asked for, 90% of the time",
                phases.Where(phase => phase.ReadAsExpected < MinimumReadAsExpected)
                    .Select(phase => $"{phase.Title} {phase.ReadAsExpected:0%}"),
                "read as asked in",
                "Failing at one distance only points at the distance; failing everywhere points at the grab "
                + "thresholds, so recalibrate."),

            Check(
                "an open hand never grabs",
                phases.Where(phase => phase.Expected == HandShape.Open && phase.Holding > 0)
                    .Select(phase => $"{phase.Title} {phase.Holding:0%}"),
                "grabbing in",
                "A grab with an open hand is a window picked up by accident."),
        ];

        // Not a tracking failure, but it decides how calibration has to be run on this setup.
        List<string> underCalibration =
        [
            .. fists.Where(phase => phase.ConfidenceP10 < run.CalibrationGate)
                .Select(phase => $"{phase.Title} {Number(phase.ConfidenceP10)}"),
        ];

        if (underCalibration.Count > 0)
        {
            checks.Add(new HandTestCheck(
                "NOTE",
                $"a fist is under the {run.CalibrationGate:0.00} calibration needs, one frame in ten: "
                + string.Join("; ", underCalibration),
                "Calibration will stall on grab-confirmed corners. Confirm them by hovering instead."));
        }

        return checks;
    }

    /// <summary>
    /// Fist against open hand at each distance: how much closing the hand costs, and whether
    /// the cost grows as the hand nears the camera.
    /// </summary>
    private static void AppendFistAgainstOpen(StringBuilder text, HandTestRun run)
    {
        var pairs = run.Phases
            .Where(phase => phase.Distance is not null && !phase.Moving)
            .GroupBy(phase => phase.Distance!)
            .Select(group => (
                Distance: group.Key,
                Open: group.FirstOrDefault(phase => phase.Expected == HandShape.Open),
                Fist: group.FirstOrDefault(phase => phase.Expected == HandShape.Closed)))
            .Where(pair => pair.Open?.ConfidenceMedian is not null && pair.Fist?.ConfidenceMedian is not null)
            .ToList();

        if (pairs.Count == 0)
        {
            return;
        }

        text.AppendLine();
        text.AppendLine("Fist against open hand at the same distance — median confidence");
        text.AppendLine($"{"distance",-24}{"fist",8}{"open",8}{"cost",8}{"camera sees",13}");

        foreach ((string distance, HandTestPhaseResult? open, HandTestPhaseResult? fist) in pairs)
        {
            float cost = fist!.ConfidenceMedian!.Value - open!.ConfidenceMedian!.Value;
            text.AppendLine(
                $"{distance,-24}{Number(fist.ConfidenceMedian),8}{Number(open.ConfidenceMedian),8}"
                + $"{cost,8:+0.00;-0.00;0.00}{Centimetres(fist.ViewWidthCm),13}");
        }
    }

    private static void AppendComparison(StringBuilder text, HandTestRun run, HandTestRun previous)
    {
        text.AppendLine();
        text.AppendLine($"Compared with {previous.CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm} on {previous.Machine} "
            + $"({previous.Camera}) — each column is now, then");
        text.AppendLine($"{"phase",-24}{"tracked",16}{"conf 10%",16}{"reads as asked",18}{"lost",10}");

        foreach (HandTestPhaseResult now in run.Phases)
        {
            HandTestPhaseResult? then = previous.Phases.FirstOrDefault(phase => phase.Key == now.Key);

            string tracked = Pair($"{now.Tracked:0%}", then is null ? null : $"{then.Tracked:0%}");
            string confidence = Pair(Number(now.ConfidenceP10), then is null ? null : Number(then.ConfidenceP10));
            string readsAs = Pair($"{now.ReadAsExpected:0%}", then is null ? null : $"{then.ReadAsExpected:0%}");
            string lost = Pair($"{now.GrabsLost}", then is null ? null : $"{then.GrabsLost}");

            text.AppendLine($"{now.Title,-24}{tracked,16}{confidence,16}{readsAs,18}{lost,10}");
        }
    }

    private static HandTestCheck Check(string passing, IEnumerable<string> failures, string? verb, string detail)
    {
        List<string> failed = [.. failures];
        if (failed.Count == 0)
        {
            return new HandTestCheck("PASS", passing);
        }

        string list = string.Join("; ", failed);
        return new HandTestCheck("FAIL", verb is null ? $"{passing}: {list}" : $"{passing} — {verb}: {list}", detail);
    }

    private static string Number(float? value) => value is { } number ? $"{number:0.00}" : "—";

    private static string Centimetres(float? value) => value is { } number ? $"{number:0} cm" : "—";

    private static string Pair(string now, string? then) => $"{now}, {then ?? "—"}";
}
