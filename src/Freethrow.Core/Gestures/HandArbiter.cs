namespace Freethrow.Core.Gestures;

/// <summary>What one tracked hand is doing this frame.</summary>
/// <param name="Id">Stable track identifier.</param>
/// <param name="State">Gesture state for this hand.</param>
/// <param name="DepthProxy">
/// Pixels per metre for this hand, from <c>HandSpace.PixelsPerMetre</c>. Larger means
/// nearer the camera, and so nearer the monitor.
/// </param>
/// <param name="Confidence">Landmark confidence, used only to break ties.</param>
public readonly record struct HandSignal(int Id, GestureState State, float DepthProxy, float Confidence);

/// <summary>Which hand does what, this frame.</summary>
/// <param name="ControllingId">The hand holding the interaction, or null if none is.</param>
/// <param name="HoverId">The hand designating a target, or null if no hand is present.</param>
public readonly record struct ArbitrationResult(int? ControllingId, int? HoverId);

/// <summary>Tuning for <see cref="HandArbiter"/>.</summary>
public sealed record ArbiterOptions
{
    public static ArbiterOptions Default { get; } = new();

    /// <summary>
    /// How much nearer a hand must be, as a fraction, before it takes over hovering.
    /// </summary>
    /// <remarks>
    /// Without a margin, two hands held at roughly the same distance would trade the
    /// highlight back and forth on measurement noise alone — the same chatter the grab
    /// thresholds and the window dwell timer already exist to prevent. Eight percent is
    /// comfortably above the frame-to-frame noise in the world-scale estimate while still
    /// well below the difference between a reaching hand and a resting one.
    /// </remarks>
    public float HoverSwitchMargin { get; init; } = 0.08f;
}

/// <summary>
/// Decides which of several tracked hands is in control, and which is pointing.
/// </summary>
/// <remarks>
/// <para>
/// Exists because being <em>detected</em> first used to decide control: whichever hand
/// the tracker happened to lock onto owned the interaction, and a deliberate grab with
/// the other hand did nothing. Detection order is an accident of where the palm detector
/// looked; it should carry no authority at all.
/// </para>
/// <para>
/// So control is claimed by grabbing, not by arriving. The first hand to close takes the
/// interaction and keeps it until it opens, and a grab from the other hand is ignored for
/// as long as that lasts — mid-drag is the worst possible moment to change which hand is
/// driving.
/// </para>
/// <para>
/// Pure logic over states and depths, with no dependency on models or frames, so every
/// rule here is exercised by synthetic sequences in tests.
/// </para>
/// </remarks>
public sealed class HandArbiter(ArbiterOptions? options = null)
{
    private readonly ArbiterOptions _options = options ?? ArbiterOptions.Default;

    private int? _controllingId;
    private int? _hoverId;

    /// <summary>The hand currently holding the interaction.</summary>
    public int? ControllingId => _controllingId;

    /// <summary>The hand currently designating a target.</summary>
    public int? HoverId => _hoverId;

    /// <summary>Resolves control and hover for this frame.</summary>
    public ArbitrationResult Update(IReadOnlyList<HandSignal> hands)
    {
        ArgumentNullException.ThrowIfNull(hands);

        if (hands.Count == 0)
        {
            Reset();
            return new ArbitrationResult(null, null);
        }

        _controllingId = ResolveControl(hands);
        _hoverId = ResolveHover(hands);

        return new ArbitrationResult(_controllingId, _hoverId);
    }

    /// <summary>Forgets which hand was in control.</summary>
    public void Reset()
    {
        _controllingId = null;
        _hoverId = null;
    }

    private int? ResolveControl(IReadOnlyList<HandSignal> hands)
    {
        if (_controllingId is { } holder)
        {
            // Keep control with the holding hand for exactly as long as it stays closed.
            // Nothing the other hand does can take it away, because handing the drag over
            // mid-move would drop whatever is being carried.
            foreach (HandSignal hand in hands)
            {
                if (hand.Id == holder)
                {
                    return hand.State == GestureState.Grab ? holder : null;
                }
            }

            // The holding hand vanished from tracking entirely.
            return null;
        }

        // No one is holding: the first hand to close claims it. When two close on the very
        // same frame, the nearer one wins, matching how hover is chosen.
        HandSignal? claimant = null;
        foreach (HandSignal hand in hands)
        {
            if (hand.State != GestureState.Grab)
            {
                continue;
            }

            if (claimant is not { } best || hand.DepthProxy > best.DepthProxy)
            {
                claimant = hand;
            }
        }

        return claimant?.Id;
    }

    private int? ResolveHover(IReadOnlyList<HandSignal> hands)
    {
        // While a hand is holding something, it is also the one designating: asking which
        // window another hand is pointing at makes no sense mid-drag.
        if (_controllingId is { } holder)
        {
            return holder;
        }

        HandSignal? incumbent = Find(hands, _hoverId);
        HandSignal nearest = hands[0];

        foreach (HandSignal hand in hands)
        {
            if (hand.DepthProxy > nearest.DepthProxy)
            {
                nearest = hand;
            }
        }

        if (incumbent is not { } current)
        {
            return nearest.Id;
        }

        // Switch only on a clear win, so noise cannot flip the highlight between two hands
        // held at similar distance.
        return nearest.DepthProxy > current.DepthProxy * (1 + _options.HoverSwitchMargin)
            ? nearest.Id
            : current.Id;
    }

    private static HandSignal? Find(IReadOnlyList<HandSignal> hands, int? id)
    {
        if (id is not { } wanted)
        {
            return null;
        }

        foreach (HandSignal hand in hands)
        {
            if (hand.Id == wanted)
            {
                return hand;
            }
        }

        return null;
    }
}
