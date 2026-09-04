using Freethrow.Core.Gestures;
using Freethrow.Core.Perception;

namespace Freethrow.Core.Tests;

/// <summary>
/// Regression tests for the two defects found in use: grabs firing on a hand that was
/// merely angled away from the camera, and grabs that would not let go.
/// </summary>
public class GestureRecognizerTests
{
    /// <summary>Openness of a relaxed, flat hand, measured from a real frame.</summary>
    private const float OpenHand = 1.80f;

    /// <summary>Openness of a closed fist.</summary>
    private const float ClosedHand = 1.10f;

    /// <summary>
    /// The value that used to stick: a half-open hand landing between the old grab and
    /// release thresholds, satisfying neither.
    /// </summary>
    private const float HalfOpenHand = 1.65f;

    [Fact]
    public void ClosingTheHandStartsAGrab()
    {
        var driver = new Driver();

        GestureUpdate update = driver.Hold(ClosedHand, seconds: 0.5);

        Assert.Equal(GestureState.Grab, update.State);
        Assert.True(update.GrabStarted || driver.State == GestureState.Grab);
    }

    [Fact]
    public void HalfOpenHandReleasesInsteadOfStickingInTheDeadBand()
    {
        // The original defect: grab was 1.55 and release 1.75, so a hand at 1.65
        // satisfied neither condition and stayed held indefinitely.
        var driver = new Driver();
        driver.Hold(ClosedHand, seconds: 0.5);
        Assert.Equal(GestureState.Grab, driver.State);

        GestureUpdate update = driver.Hold(HalfOpenHand, seconds: 0.5);

        Assert.Equal(GestureState.Hover, update.State);
    }

    [Fact]
    public void ReleaseIsPromptOnceTheHandOpens()
    {
        var driver = new Driver();
        driver.Hold(ClosedHand, seconds: 0.5);

        // Assert on the frame the transition actually occurs, not on some later frame
        // by which the flags have already cleared.
        (GestureUpdate release, double seconds) = driver.RunUntil(
            OpenHand,
            update => update.GrabEnded,
            limitSeconds: 0.5);

        Assert.True(release.GrabEnded, "The hand opened but the grab never ended.");
        Assert.False(release.GrabAborted);
        Assert.True(
            seconds <= 0.15,
            $"Release took {seconds * 1000:0} ms; a deliberate open should register within 150 ms.");
    }

    [Fact]
    public void SingleNoisyFrameDoesNotRestartTheReleaseWindow()
    {
        // Previously a lone contrary frame reset the counter to zero, so a release
        // needed two consecutive clean frames and noise could defer it indefinitely.
        var driver = new Driver();
        driver.Hold(ClosedHand, seconds: 0.5);

        bool released = false;
        for (int frame = 0; frame < 10 && !released; frame++)
        {
            // Every third frame reads closed, mimicking landmark noise while opening.
            float openness = frame % 3 == 2 ? ClosedHand : OpenHand;
            released = driver.Step(openness).State == GestureState.Hover;
        }

        Assert.True(released, "A hand opening through intermittent noise never released.");
    }

    [Fact]
    public void ForeshortenedHandDoesNotArmAGrab()
    {
        // The second defect: an open hand angled toward the camera projects like a fist.
        // A real such pose measured 0.78; hands flat to the camera read 0.11 to 0.21.
        var driver = new Driver();

        GestureUpdate update = driver.Hold(ClosedHand, seconds: 1.0, viewAlignment: 0.78f);

        Assert.Equal(GestureState.Hover, update.State);
        Assert.True(update.IsArmingBlocked);
    }

    [Fact]
    public void FlatHandStillArmsNormally()
    {
        var driver = new Driver();

        GestureUpdate update = driver.Hold(ClosedHand, seconds: 0.5, viewAlignment: 0.21f);

        Assert.Equal(GestureState.Grab, update.State);
        Assert.False(update.IsArmingBlocked);
    }

    [Fact]
    public void RotatingTheWristDoesNotDropAHeldGrab()
    {
        // Orientation gates arming only. A wrist turns throughout a drag, and releasing
        // then would be a worse failure than the false grabs the gate prevents.
        var driver = new Driver();
        driver.Hold(ClosedHand, seconds: 0.5);
        Assert.Equal(GestureState.Grab, driver.State);

        GestureUpdate update = driver.Hold(ClosedHand, seconds: 0.5, viewAlignment: 0.95f);

        Assert.Equal(GestureState.Grab, update.State);
    }

    [Fact]
    public void BriefTrackingLossCoastsRatherThanDropping()
    {
        var driver = new Driver();
        driver.Hold(ClosedHand, seconds: 0.5);

        GestureUpdate update = driver.StepMissing();

        Assert.Equal(GestureState.Grab, update.State);
        Assert.True(update.IsCoasting);
    }

    [Fact]
    public void SustainedTrackingLossAbortsTheGrab()
    {
        var driver = new Driver();
        driver.Hold(ClosedHand, seconds: 0.5);

        GestureUpdate update = default;
        for (int frame = 0; frame < 30; frame++)
        {
            update = driver.StepMissing();
        }

        Assert.Equal(GestureState.NoHand, update.State);
    }

    [Fact]
    public void OpennessIsUnaffectedByRotation()
    {
        // The property that makes the whole fix work: the same hand shape must measure
        // the same however it is turned. This is what the 2D measure could not do.
        float flat = HandMetrics.Openness(SyntheticHand.Create(1.8f, viewAlignment: 0f));
        float angled = HandMetrics.Openness(SyntheticHand.Create(1.8f, viewAlignment: 0.9f));

        Assert.Equal(flat, angled, tolerance: 0.001f);
    }

    /// <summary>
    /// Drives a recognizer on a continuous clock.
    /// </summary>
    /// <remarks>
    /// The clock has to persist across calls: the debounce is measured in seconds, so
    /// restarting time for each phase of a test would send timestamps backwards and the
    /// recognizer would correctly refuse to advance.
    /// </remarks>
    private sealed class Driver
    {
        private const double FrameInterval = 1.0 / 30;

        private readonly GestureRecognizer _recognizer = new();
        private double _time;

        public GestureState State => _recognizer.State;

        /// <summary>Feeds one frame of a given pose.</summary>
        public GestureUpdate Step(float openness, float viewAlignment = 0f)
        {
            _time += FrameInterval;
            return _recognizer.Update(SyntheticHand.Create(openness, viewAlignment), _time);
        }

        /// <summary>Feeds one frame in which no hand was found.</summary>
        public GestureUpdate StepMissing()
        {
            _time += FrameInterval;
            return _recognizer.Update(null, _time);
        }

        /// <summary>
        /// Feeds a steady pose until <paramref name="until"/> holds, reporting how long
        /// that took.
        /// </summary>
        public (GestureUpdate Update, double Seconds) RunUntil(
            float openness,
            Func<GestureUpdate, bool> until,
            double limitSeconds,
            float viewAlignment = 0f)
        {
            var update = default(GestureUpdate);
            double elapsed = 0;

            while (elapsed < limitSeconds)
            {
                update = Step(openness, viewAlignment);
                elapsed += FrameInterval;

                if (until(update))
                {
                    break;
                }
            }

            return (update, elapsed);
        }

        /// <summary>Holds a steady pose for a span of time.</summary>
        public GestureUpdate Hold(float openness, double seconds, float viewAlignment = 0f)
        {
            var update = default(GestureUpdate);
            int frames = (int)(seconds / FrameInterval);

            for (int frame = 0; frame < frames; frame++)
            {
                update = Step(openness, viewAlignment);
            }

            return update;
        }
    }
}
