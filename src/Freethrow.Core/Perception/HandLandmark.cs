namespace Freethrow.Core.Perception;

/// <summary>
/// The 21 hand landmarks, in MediaPipe's canonical order. The numeric values are the
/// model's output indices and must not be reordered.
/// </summary>
public enum HandLandmark
{
    Wrist = 0,

    ThumbCmc = 1,
    ThumbMcp = 2,
    ThumbIp = 3,
    ThumbTip = 4,

    IndexMcp = 5,
    IndexPip = 6,
    IndexDip = 7,
    IndexTip = 8,

    MiddleMcp = 9,
    MiddlePip = 10,
    MiddleDip = 11,
    MiddleTip = 12,

    RingMcp = 13,
    RingPip = 14,
    RingDip = 15,
    RingTip = 16,

    PinkyMcp = 17,
    PinkyPip = 18,
    PinkyDip = 19,
    PinkyTip = 20,
}

/// <summary>Which hand the model believes it saw.</summary>
public enum Handedness
{
    Unknown = 0,
    Left,
    Right,
}

/// <summary>Landmark connectivity, for drawing a skeleton.</summary>
public static class HandSkeleton
{
    /// <summary>Bone pairs linking the 21 landmarks into a hand.</summary>
    public static readonly (HandLandmark From, HandLandmark To)[] Bones =
    [
        // Thumb.
        (HandLandmark.Wrist, HandLandmark.ThumbCmc),
        (HandLandmark.ThumbCmc, HandLandmark.ThumbMcp),
        (HandLandmark.ThumbMcp, HandLandmark.ThumbIp),
        (HandLandmark.ThumbIp, HandLandmark.ThumbTip),

        // Index.
        (HandLandmark.Wrist, HandLandmark.IndexMcp),
        (HandLandmark.IndexMcp, HandLandmark.IndexPip),
        (HandLandmark.IndexPip, HandLandmark.IndexDip),
        (HandLandmark.IndexDip, HandLandmark.IndexTip),

        // Middle.
        (HandLandmark.IndexMcp, HandLandmark.MiddleMcp),
        (HandLandmark.MiddleMcp, HandLandmark.MiddlePip),
        (HandLandmark.MiddlePip, HandLandmark.MiddleDip),
        (HandLandmark.MiddleDip, HandLandmark.MiddleTip),

        // Ring.
        (HandLandmark.MiddleMcp, HandLandmark.RingMcp),
        (HandLandmark.RingMcp, HandLandmark.RingPip),
        (HandLandmark.RingPip, HandLandmark.RingDip),
        (HandLandmark.RingDip, HandLandmark.RingTip),

        // Pinky.
        (HandLandmark.RingMcp, HandLandmark.PinkyMcp),
        (HandLandmark.PinkyMcp, HandLandmark.PinkyPip),
        (HandLandmark.PinkyPip, HandLandmark.PinkyDip),
        (HandLandmark.PinkyDip, HandLandmark.PinkyTip),

        // Palm closure.
        (HandLandmark.Wrist, HandLandmark.PinkyMcp),
    ];
}
