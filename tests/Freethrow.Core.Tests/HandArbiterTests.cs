using Freethrow.Core.Gestures;

namespace Freethrow.Core.Tests;

/// <summary>
/// Regression tests for the reported defect: a grab with one hand was ignored because
/// the other had been detected first.
/// </summary>
public class HandArbiterTests
{
    private const int Left = 1;
    private const int Right = 2;

    /// <summary>Pixels per metre for a hand at a typical resting distance.</summary>
    private const float Far = 700f;

    /// <summary>Pixels per metre for a hand reached toward the screen.</summary>
    private const float Near = 900f;

    [Fact]
    public void HandDetectedFirstDoesNotTakeControlWithoutGrabbing()
    {
        // The reported bug. The left hand is present and open; the right one grabs.
        // Control must follow the grab, not the arrival.
        var arbiter = new HandArbiter();

        arbiter.Update([Open(Left, Near)]);
        ArbitrationResult result = arbiter.Update([Open(Left, Near), Grabbing(Right, Far)]);

        Assert.Equal(Right, result.ControllingId);
    }

    [Fact]
    public void FirstHandToGrabKeepsControlWhileTheSecondAlsoGrabs()
    {
        var arbiter = new HandArbiter();

        arbiter.Update([Grabbing(Left, Far), Open(Right, Near)]);
        ArbitrationResult result = arbiter.Update([Grabbing(Left, Far), Grabbing(Right, Near)]);

        // The second hand closing must not steal a drag in progress, even though it is
        // nearer the screen.
        Assert.Equal(Left, result.ControllingId);
    }

    [Fact]
    public void ControlIsReleasedOnlyByTheHandHoldingIt()
    {
        var arbiter = new HandArbiter();

        // Establish the far hand as the holder before the near one closes: with both
        // closing on the same frame the nearer wins the tiebreak, which is a different
        // rule and not the one under test here.
        arbiter.Update([Grabbing(Left, Far), Open(Right, Near)]);
        arbiter.Update([Grabbing(Left, Far), Grabbing(Right, Near)]);

        // The other hand opening changes nothing.
        ArbitrationResult stillHeld = arbiter.Update([Grabbing(Left, Far), Open(Right, Near)]);
        Assert.Equal(Left, stillHeld.ControllingId);

        // The holder opening frees it.
        ArbitrationResult released = arbiter.Update([Open(Left, Far), Open(Right, Near)]);
        Assert.Null(released.ControllingId);
    }

    [Fact]
    public void ControlIsDroppedWhenTheHoldingHandDisappears()
    {
        var arbiter = new HandArbiter();
        arbiter.Update([Grabbing(Left, Far)]);

        ArbitrationResult result = arbiter.Update([Open(Right, Near)]);

        Assert.Null(result.ControllingId);
    }

    [Fact]
    public void ControlCanBeClaimedAgainAfterRelease()
    {
        var arbiter = new HandArbiter();
        arbiter.Update([Grabbing(Left, Far), Open(Right, Near)]);
        arbiter.Update([Open(Left, Far), Open(Right, Near)]);

        ArbitrationResult result = arbiter.Update([Open(Left, Far), Grabbing(Right, Near)]);

        Assert.Equal(Right, result.ControllingId);
    }

    [Fact]
    public void HoverFollowsTheHandNearestTheScreen()
    {
        var arbiter = new HandArbiter();

        ArbitrationResult result = arbiter.Update([Open(Left, Far), Open(Right, Near)]);

        Assert.Equal(Right, result.HoverId);
    }

    [Fact]
    public void HoverDoesNotSwitchBetweenHandsAtSimilarDistance()
    {
        // The anti-chatter case: a few percent of noise must not move the highlight.
        var arbiter = new HandArbiter();
        arbiter.Update([Open(Left, 800f), Open(Right, 780f)]);

        ArbitrationResult result = arbiter.Update([Open(Left, 800f), Open(Right, 815f)]);

        Assert.Equal(Left, result.HoverId);
    }

    [Fact]
    public void HoverSwitchesOnAClearReach()
    {
        var arbiter = new HandArbiter();
        arbiter.Update([Open(Left, 800f), Open(Right, 780f)]);

        ArbitrationResult result = arbiter.Update([Open(Left, 800f), Open(Right, 1000f)]);

        Assert.Equal(Right, result.HoverId);
    }

    [Fact]
    public void TheHoldingHandAlsoDesignates()
    {
        var arbiter = new HandArbiter();

        // The far hand grabs first; hover must follow the drag rather than stay with the
        // nearer, idle hand.
        ArbitrationResult result = arbiter.Update([Grabbing(Left, Far), Open(Right, Near)]);

        Assert.Equal(Left, result.ControllingId);
        Assert.Equal(Left, result.HoverId);
    }

    [Fact]
    public void NoHandsClearsEverything()
    {
        var arbiter = new HandArbiter();
        arbiter.Update([Grabbing(Left, Far)]);

        ArbitrationResult result = arbiter.Update([]);

        Assert.Null(result.ControllingId);
        Assert.Null(result.HoverId);
    }

    private static HandSignal Open(int id, float depth) =>
        new(id, GestureState.Hover, depth, 0.9f);

    private static HandSignal Grabbing(int id, float depth) =>
        new(id, GestureState.Grab, depth, 0.9f);
}
