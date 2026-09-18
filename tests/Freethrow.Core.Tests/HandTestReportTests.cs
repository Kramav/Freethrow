using Freethrow.Core.Perception;

namespace Freethrow.Core.Tests;

/// <summary>
/// Tests for the hand-test report: that each check fires on the failure it names and stays
/// quiet otherwise, and that runs are compared phase by phase and survive storage.
/// </summary>
/// <remarks>
/// Assertions look for phase titles and key phrases rather than formatted numbers, so they
/// hold whatever decimal separator the machine's culture uses.
/// </remarks>
public class HandTestReportTests
{
    [Fact]
    public void AHealthyRunPassesEveryCheck()
    {
        HandTestRun run = Run(Open("open"), Fist("fist"), Carry());

        IReadOnlyList<HandTestCheck> checks = HandTestReport.Checks(run);

        Assert.NotEmpty(checks);
        Assert.All(checks, check => Assert.Equal("PASS", check.Verdict));
    }

    [Fact]
    public void AFistUnderTheGrabGateFails()
    {
        HandTestRun run = Run(Open("open"), Fist("fist", p10: 0.55f));

        IReadOnlyList<HandTestCheck> checks = HandTestReport.Checks(run);

        Assert.Contains(checks, check => check.Verdict == "FAIL" && Text(check).Contains("drops in and out"));
    }

    [Fact]
    public void AFistBetweenTheGatesIsANoteAboutCalibrationNotAFailure()
    {
        // Above the 0.60 a grab needs, below the 0.70 calibration needs: grabs work, but
        // grab-confirmed calibration corners will stall.
        HandTestRun run = Run(Open("open"), Fist("fist", p10: 0.65f));

        IReadOnlyList<HandTestCheck> checks = HandTestReport.Checks(run);

        Assert.DoesNotContain(checks, check => check.Verdict == "FAIL");
        Assert.Contains(checks, check => check.Verdict == "NOTE" && Text(check).Contains("hovering"));
    }

    [Fact]
    public void CarryingThatLosesAGrabFailsAndSaysHow()
    {
        HandTestRun run = Run(Carry(lost: 1, dropped: 1));

        IReadOnlyList<HandTestCheck> checks = HandTestReport.Checks(run);

        Assert.Contains(checks, check => check.Verdict == "FAIL" && Text(check).Contains("dropped by the tracker"));
    }

    [Fact]
    public void AnOpenHandThatGrabsFails()
    {
        HandTestRun run = Run(Open("open", holding: 0.05));

        IReadOnlyList<HandTestCheck> checks = HandTestReport.Checks(run);

        Assert.Contains(checks, check => check.Verdict == "FAIL" && Text(check).Contains("with an open hand"));
    }

    [Fact]
    public void AHandLostInAFifthOfFramesFailsTracking()
    {
        HandTestRun run = Run(Open("open", tracked: 0.8));

        IReadOnlyList<HandTestCheck> checks = HandTestReport.Checks(run);

        Assert.Contains(checks, check => check.Verdict == "FAIL" && Text(check).Contains("open 80%"));
    }

    [Fact]
    public void APhaseWithNoFramesIsNotJudged()
    {
        // A phase that recorded nothing has no evidence either way; failing it would report
        // a tracking fault for a phase that never ran.
        HandTestRun run = Run(Open("open", tracked: 0, frames: 0));

        Assert.All(HandTestReport.Checks(run), check => Assert.Equal("PASS", check.Verdict));
    }

    [Fact]
    public void ComparisonLinesPhasesUpByKey()
    {
        HandTestRun previous = Run(Open("open", tracked: 0.9));
        HandTestRun now = Run(Open("open"), Fist("fist"));

        string report = HandTestReport.Format(now, previous);

        Assert.Contains("Compared with", report);
        Assert.Contains("100%, 90%", report);   // the open phase, now then
        Assert.Contains("100%, —", report);     // the fist phase, absent before
    }

    [Fact]
    public void FistIsSetAgainstTheOpenHandAtTheSameDistance()
    {
        // The comparison the whole test is built around: what closing the hand costs,
        // at each distance, separately from what the distance itself costs.
        HandTestRun run = Run(
            Open("open", distance: "where you use it"),
            Fist("fist", distance: "where you use it"),
            Open("open-near", distance: "closer"),
            Fist("fist-near", p10: 0.5f, distance: "closer"),
            Carry());

        string report = HandTestReport.Format(run, previous: null);

        Assert.Contains("Fist against open hand", report);
        Assert.Contains("where you use it", report);
        Assert.Contains("closer", report);
    }

    [Fact]
    public void SavedRunsRoundTripAndTheLatestIsFound()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"freethrow-hand-tests-{Guid.NewGuid():N}");

        try
        {
            HandTestRun older = Run(Open("open", tracked: 0.9)) with { CreatedAt = new DateTimeOffset(2026, 9, 1, 10, 0, 0, TimeSpan.Zero) };
            HandTestRun newer = Run(Fist("fist")) with { CreatedAt = new DateTimeOffset(2026, 9, 2, 10, 0, 0, TimeSpan.Zero) };

            HandTestReport.Save(older, folder);
            string path = HandTestReport.Save(newer, folder);

            HandTestRun? latest = HandTestReport.LoadLatest(folder);

            Assert.NotNull(latest);
            Assert.Equal("fist", Assert.Single(latest.Phases).Key);
            Assert.Equal(HandShape.Closed, latest.Phases[0].Expected);
            Assert.Contains("\"Closed\"", File.ReadAllText(path));
        }
        finally
        {
            if (Directory.Exists(folder))
            {
                Directory.Delete(folder, recursive: true);
            }
        }
    }

    private static HandTestRun Run(params HandTestPhaseResult[] phases) => new(
        DateTimeOffset.Now,
        "TEST-PC",
        "Test camera",
        "640x480",
        GrabOpenness: 1.04f,
        ReleaseOpenness: 1.245f,
        TrackingGate: 0.5f,
        GestureGate: 0.6f,
        CalibrationGate: 0.7f,
        [.. phases]);

    private static HandTestPhaseResult Open(
        string key, double tracked = 1.0, double holding = 0, int frames = 170, string? distance = "here") =>
        Phase(key, HandShape.Open, moving: false, tracked, p10: 0.9f, holding, lost: 0, dropped: 0, frames, distance);

    private static HandTestPhaseResult Fist(string key, float p10 = 0.8f, string? distance = "here") =>
        Phase(key, HandShape.Closed, moving: false, tracked: 1.0, p10, holding: 1.0, lost: 0, dropped: 0, distance: distance);

    private static HandTestPhaseResult Carry(int lost = 0, int dropped = 0) =>
        Phase("carry", HandShape.Closed, moving: true, tracked: 1.0, p10: 0.8f, holding: 0.98, lost, dropped, distance: null);

    private static string Text(HandTestCheck check) => $"{check.Summary} {check.Detail}";

    private static HandTestPhaseResult Phase(
        string key,
        HandShape expected,
        bool moving,
        double tracked,
        float p10,
        double holding,
        int lost,
        int dropped,
        int frames = 170,
        string? distance = null) => new(
            key,
            key,
            expected,
            moving,
            distance,
            frames,
            tracked,
            p10,
            ConfidenceMedian: p10 + 0.05f,
            ReadAsExpected: 1.0,
            holding,
            lost,
            dropped,
            GrabsPastGrace: lost - dropped,
            ViewWidthCm: 40);
}
