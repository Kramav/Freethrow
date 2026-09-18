using System.Numerics;
using Freethrow.Core.Gestures;
using Freethrow.Core.Perception;

namespace Freethrow.Core.Tests;

/// <summary>
/// Tests for the tracking-quality recorder behind <c>--track</c> and <c>--hand-test</c>:
/// that each way a grab can end is attributed correctly, and confidence is filed by the
/// shape the hand actually made.
/// </summary>
public class TrackingQualityTests
{
    // The one real gesture profile measured so far, so shapes classify as they do live.
    private const float GrabOpenness = 1.04f;
    private const float ReleaseOpenness = 1.245f;

    [Fact]
    public void AGrabWhoseHandVanishesIsCountedAsDroppedByTheTracker()
    {
        // The case that reports nothing by itself: the worker discards a vanished hand's
        // recognizer on the same frame, so no GrabEnded ever arrives for it.
        TrackingQuality quality = New();

        quality.Add(Frame(Hand(1, 1.0f, 0.9f, GestureState.Grab, GestureState.Hover)));
        quality.Add(Frame());

        Assert.Equal(1, quality.GrabsStarted);
        Assert.Equal(1, quality.GrabsDroppedByTracker);
        Assert.Equal(0, quality.GrabsPastGrace);
        Assert.Equal(0, quality.GrabsReleased);
        Assert.Equal(1, quality.GrabsLost);
    }

    [Fact]
    public void AGrabThatCoastsOutIsCountedPastTheGrace()
    {
        // Still tracked, but under the recognizer's confidence gate for longer than it
        // will hold a grab: it ends as NoHand while the hand is still in the result.
        TrackingQuality quality = New();

        quality.Add(Frame(Hand(1, 1.0f, 0.9f, GestureState.Grab, GestureState.Hover)));
        quality.Add(Frame(Hand(1, 1.0f, 0.55f, GestureState.NoHand, GestureState.Grab)));

        Assert.Equal(1, quality.GrabsPastGrace);
        Assert.Equal(0, quality.GrabsDroppedByTracker);
        Assert.Equal(1, quality.GrabsLost);
    }

    [Fact]
    public void OpeningTheHandIsAReleaseNotALoss()
    {
        TrackingQuality quality = New();

        quality.Add(Frame(Hand(1, 1.0f, 0.9f, GestureState.Grab, GestureState.Hover)));
        quality.Add(Frame(Hand(1, 1.7f, 0.9f, GestureState.Hover, GestureState.Grab)));
        quality.Add(Frame());

        Assert.Equal(1, quality.GrabsReleased);
        Assert.Equal(0, quality.GrabsLost);
    }

    [Fact]
    public void ConfidenceIsFiledByTheShapeTheHandActuallyMade()
    {
        TrackingQuality quality = New();

        quality.Add(Frame(Hand(1, 1.00f, 0.55f, GestureState.Grab, GestureState.Grab)));
        quality.Add(Frame(Hand(1, 1.15f, 0.80f, GestureState.Hover, GestureState.Hover)));
        quality.Add(Frame(Hand(1, 1.70f, 0.95f, GestureState.Hover, GestureState.Hover)));

        Assert.Equal(0.55f, quality.Confidence(HandShape.Closed).Median);
        Assert.Equal(0.80f, quality.Confidence(HandShape.InBetween).Median);
        Assert.Equal(0.95f, quality.Confidence(HandShape.Open).Median);
        Assert.Equal(3, quality.HandFrames);
    }

    [Fact]
    public void FramesAreCountedWithAndWithoutAHand()
    {
        TrackingQuality quality = New();

        quality.Add(Frame(Hand(1, 1.0f, 0.9f, GestureState.Grab, GestureState.Hover)));
        quality.Add(Frame(Hand(1, 1.0f, 0.9f, GestureState.Grab, GestureState.Grab)));
        quality.Add(Frame());
        quality.Add(Frame(Hand(2, 1.7f, 0.9f, GestureState.Hover, GestureState.NoHand)));

        Assert.Equal(4, quality.Frames);
        Assert.Equal(3, quality.FramesWithHand);
        Assert.Equal(2, quality.FramesHolding);
    }

    [Fact]
    public void ViewWidthIsTheFrameWidthInMetresAtTheHand()
    {
        // 640 pixels at 1600 pixels per metre: the camera sees 40 cm across at that depth.
        TrackingQuality quality = New();

        quality.Add(Frame(Hand(1, 1.7f, 0.9f, GestureState.Hover, GestureState.Hover, pixelsPerMetre: 1600f)));

        Assert.Equal(0.40f, quality.ViewWidths().Median, tolerance: 0.0001f);
    }

    [Fact]
    public void ShareUnderAGateCountsOnlyValuesStrictlyBelowIt()
    {
        var sample = new ConfidenceSample([0.5f, 0.6f, 0.7f, 0.8f]);

        Assert.Equal(0.25, sample.ShareUnder(0.6f));
        Assert.Equal(0.5f, sample.Percentile(0f));
        Assert.Equal(0.8f, sample.Percentile(1f));
    }

    private static TrackingQuality New() => new(GrabOpenness, ReleaseOpenness);

    private static HandTrackingResult Frame(params TrackedHand[] hands) => new(hands, null, null, 0, 640, 480);

    private static TrackedHand Hand(
        int id,
        float openness,
        float confidence,
        GestureState state,
        GestureState previous,
        float pixelsPerMetre = 1000f)
    {
        HandPose pose = SyntheticHand.Create(openness, confidence: confidence, pixelsPerMetre: pixelsPerMetre);
        var gesture = new GestureUpdate(state, previous, Vector2.Zero, openness, confidence, false, 0, false);
        return new TrackedHand(id, pose, gesture, pixelsPerMetre);
    }
}
