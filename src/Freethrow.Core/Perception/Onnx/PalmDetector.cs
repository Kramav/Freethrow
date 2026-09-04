using System.Numerics;
using Freethrow.Core.Capture;
using Freethrow.Core.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Freethrow.Core.Perception.Onnx;

/// <summary>A palm found by <see cref="PalmDetector"/>, in frame pixels.</summary>
/// <param name="Min">Top-left of the palm box.</param>
/// <param name="Max">Bottom-right of the palm box.</param>
/// <param name="Keypoints">
/// Seven palm keypoints. Index 0 is the palm base and index 2 the middle-finger base;
/// the line between them gives the hand's rotation.
/// </param>
/// <param name="Score">Detection confidence in 0..1.</param>
public sealed record PalmDetection(Vector2 Min, Vector2 Max, Vector2[] Keypoints, float Score);

/// <summary>
/// MediaPipe's BlazePalm detector, run through ONNX Runtime.
/// </summary>
/// <remarks>
/// This is the expensive half of hand tracking and is deliberately run as rarely as
/// possible — only to acquire a hand, never to follow one. See
/// <see cref="OnnxHandTracker"/> for the acquire-then-track loop.
/// </remarks>
public sealed class PalmDetector : IDisposable
{
    /// <summary>Model input is 192x192.</summary>
    public const int InputSize = 192;

    private const int AnchorCount = 2016;
    private const int ValuesPerAnchor = 18;
    private const int KeypointCount = 7;

    private readonly InferenceSession _session;
    private readonly string _inputName;
    private readonly string _boxOutputName;
    private readonly string _scoreOutputName;
    private readonly Vector2[] _anchors;
    private readonly float[] _input;
    private readonly NamedOnnxValue[] _inputs;
    private readonly float _scoreThreshold;
    private readonly float _nmsThreshold;

    public PalmDetector(string modelPath, SessionOptions? sessionOptions = null, float scoreThreshold = 0.5f, float nmsThreshold = 0.3f)
    {
        _session = new InferenceSession(modelPath, sessionOptions ?? OnnxSession.CreateDefaultOptions());
        _scoreThreshold = scoreThreshold;
        _nmsThreshold = nmsThreshold;

        _inputName = _session.InputMetadata.Keys.First();

        // The two outputs are distinguished by shape rather than by name: the box tensor
        // carries 18 values per anchor, the score tensor one. Relying on the exported
        // names ("Identity", "Identity_1") would break on any re-export.
        string[] outputs = [.. _session.OutputMetadata.Keys];
        _boxOutputName = outputs.First(name => _session.OutputMetadata[name].Dimensions[^1] == ValuesPerAnchor);
        _scoreOutputName = outputs.First(name => _session.OutputMetadata[name].Dimensions[^1] == 1);

        _anchors = GenerateAnchors();
        _input = new float[InputSize * InputSize * 3];

        var tensor = new DenseTensor<float>(_input.AsMemory(), [1, InputSize, InputSize, 3]);
        _inputs = [NamedOnnxValue.CreateFromTensor(_inputName, tensor)];
    }

    /// <summary>
    /// Finds palms in a frame, best first.
    /// </summary>
    /// <param name="frame">Frame to search.</param>
    /// <param name="maxResults">
    /// How many distinct palms to return. More than one is worth having even when only
    /// a single hand will be tracked — see <see cref="OnnxHandTracker"/>, which picks
    /// between them by landmark quality rather than trusting this score.
    /// </param>
    public IReadOnlyList<PalmDetection> Detect(FrameRef frame, int maxResults = 2)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxResults);

        LetterboxTransform transform = ImageSampler.LetterboxToTensor(frame, _input, InputSize);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(_inputs);

        ReadOnlySpan<float> boxes = Dense(results, _boxOutputName);
        ReadOnlySpan<float> scores = Dense(results, _scoreOutputName);

        List<PalmDetection> candidates = Decode(boxes, scores, transform);
        return candidates.Count == 0 ? [] : SuppressOverlaps(candidates, maxResults);
    }

    public void Dispose() => _session.Dispose();

    private List<PalmDetection> Decode(
        ReadOnlySpan<float> boxes,
        ReadOnlySpan<float> scores,
        LetterboxTransform transform)
    {
        var candidates = new List<PalmDetection>();

        for (int anchorIndex = 0; anchorIndex < AnchorCount; anchorIndex++)
        {
            float score = Sigmoid(scores[anchorIndex]);
            if (score < _scoreThreshold)
            {
                continue;
            }

            Vector2 anchor = _anchors[anchorIndex];
            int offset = anchorIndex * ValuesPerAnchor;

            // Deltas are in model pixels; dividing by the input size puts them in the
            // same normalised space as the anchors.
            var center = new Vector2(
                (boxes[offset] / InputSize) + anchor.X,
                (boxes[offset + 1] / InputSize) + anchor.Y);

            var size = new Vector2(
                boxes[offset + 2] / InputSize,
                boxes[offset + 3] / InputSize);

            var keypoints = new Vector2[KeypointCount];
            for (int k = 0; k < KeypointCount; k++)
            {
                int keypointOffset = offset + 4 + (k * 2);
                var normalised = new Vector2(
                    (boxes[keypointOffset] / InputSize) + anchor.X,
                    (boxes[keypointOffset + 1] / InputSize) + anchor.Y);

                keypoints[k] = transform.NormalisedToFrame(normalised, InputSize);
            }

            candidates.Add(new PalmDetection(
                transform.NormalisedToFrame(center - (size / 2), InputSize),
                transform.NormalisedToFrame(center + (size / 2), InputSize),
                keypoints,
                score));
        }

        return candidates;
    }

    /// <summary>
    /// Collapses overlapping anchor hits into distinct palms, best-scoring first.
    /// </summary>
    /// <remarks>
    /// A single palm lights up several neighbouring anchors, so the boxes that agree
    /// with each winner are averaged into it rather than discarded. That steadies the
    /// crop against the frame-to-frame wobble any one anchor shows, and a steadier crop
    /// means steadier landmarks.
    /// </remarks>
    private List<PalmDetection> SuppressOverlaps(List<PalmDetection> candidates, int maxResults)
    {
        candidates.Sort((left, right) => right.Score.CompareTo(left.Score));

        var kept = new List<PalmDetection>(maxResults);
        var absorbed = new bool[candidates.Count];

        for (int i = 0; i < candidates.Count && kept.Count < maxResults; i++)
        {
            if (absorbed[i])
            {
                continue;
            }

            PalmDetection best = candidates[i];
            Vector2 minSum = best.Min;
            Vector2 maxSum = best.Max;
            int overlapping = 1;

            for (int j = i + 1; j < candidates.Count; j++)
            {
                if (absorbed[j] || IntersectionOverUnion(best, candidates[j]) <= _nmsThreshold)
                {
                    continue;
                }

                absorbed[j] = true;
                minSum += candidates[j].Min;
                maxSum += candidates[j].Max;
                overlapping++;
            }

            kept.Add(best with
            {
                Min = minSum / overlapping,
                Max = maxSum / overlapping,
            });
        }

        return kept;
    }

    private static float IntersectionOverUnion(PalmDetection left, PalmDetection right)
    {
        float x1 = MathF.Max(left.Min.X, right.Min.X);
        float y1 = MathF.Max(left.Min.Y, right.Min.Y);
        float x2 = MathF.Min(left.Max.X, right.Max.X);
        float y2 = MathF.Min(left.Max.Y, right.Max.Y);

        float intersection = MathF.Max(0, x2 - x1) * MathF.Max(0, y2 - y1);
        if (intersection <= 0)
        {
            return 0;
        }

        float leftArea = (left.Max.X - left.Min.X) * (left.Max.Y - left.Min.Y);
        float rightArea = (right.Max.X - right.Min.X) * (right.Max.Y - right.Min.Y);

        return intersection / (leftArea + rightArea - intersection);
    }

    /// <summary>
    /// Generates the SSD anchor grid the model was trained against.
    /// </summary>
    /// <remarks>
    /// Two feature maps: a 24x24 grid at stride 8 with two anchors per cell, then a
    /// 12x12 grid at stride 16 with six, giving 1152 + 864 = 2016. Because the model
    /// uses fixed anchor sizes, only the centres matter, so the whole anchor set reduces
    /// to a list of points. OpenCV's reference implementation ships these as a 90 KB
    /// literal; generating them keeps the derivation visible and the file small.
    /// </remarks>
    private static Vector2[] GenerateAnchors()
    {
        var anchors = new Vector2[AnchorCount];
        int index = 0;

        foreach ((int stride, int anchorsPerCell) in new[] { (8, 2), (16, 6) })
        {
            int gridSize = InputSize / stride;

            for (int y = 0; y < gridSize; y++)
            {
                for (int x = 0; x < gridSize; x++)
                {
                    var center = new Vector2((x + 0.5f) / gridSize, (y + 0.5f) / gridSize);

                    for (int repeat = 0; repeat < anchorsPerCell; repeat++)
                    {
                        anchors[index++] = center;
                    }
                }
            }
        }

        return anchors;
    }

    private static ReadOnlySpan<float> Dense(
        IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results,
        string name)
    {
        foreach (DisposableNamedOnnxValue value in results)
        {
            if (value.Name == name)
            {
                return ((DenseTensor<float>)value.AsTensor<float>()).Buffer.Span;
            }
        }

        throw new InvalidOperationException($"Model produced no output named '{name}'.");
    }

    private static float Sigmoid(float value) =>
        1f / (1f + MathF.Exp(-Math.Clamp(value, -100f, 100f)));
}
