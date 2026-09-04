using System.Numerics;
using Freethrow.Core.Capture;
using Freethrow.Core.Imaging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Freethrow.Core.Perception.Onnx;

/// <summary>
/// MediaPipe's hand landmark model: 21 points from a cropped, upright hand.
/// </summary>
public sealed class HandLandmarkDetector : IDisposable
{
    /// <summary>Model input is 224x224.</summary>
    public const int InputSize = 224;

    private const int LandmarkValues = HandPose.LandmarkCount * 3;

    private readonly InferenceSession _session;
    private readonly float[] _input;
    private readonly NamedOnnxValue[] _inputs;
    private readonly string[] _outputNames;

    public HandLandmarkDetector(string modelPath, SessionOptions? sessionOptions = null)
    {
        _session = new InferenceSession(modelPath, sessionOptions ?? OnnxSession.CreateDefaultOptions());

        string inputName = _session.InputMetadata.Keys.First();
        _outputNames = [.. _session.OutputMetadata.Keys];

        ValidateOutputs();

        _input = new float[InputSize * InputSize * 3];
        var tensor = new DenseTensor<float>(_input.AsMemory(), [1, InputSize, InputSize, 3]);
        _inputs = [NamedOnnxValue.CreateFromTensor(inputName, tensor)];
    }

    /// <summary>
    /// Runs the model over one crop and returns the landmarks in frame coordinates.
    /// </summary>
    /// <remarks>
    /// The result is returned regardless of confidence; judging whether it is good
    /// enough belongs to the caller, which knows whether it is acquiring a hand or
    /// following one and can apply different bars to each.
    /// </remarks>
    public HandPose Detect(FrameRef frame, RotatedCrop crop)
    {
        ArgumentNullException.ThrowIfNull(frame);

        ImageSampler.SampleRotated(frame, _input, crop);

        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results = _session.Run(_inputs);

        ReadOnlySpan<float> landmarks = Dense(results, _outputNames[0]);
        float confidence = Dense(results, _outputNames[1])[0];
        float handedness = Dense(results, _outputNames[2])[0];
        ReadOnlySpan<float> worldLandmarks = Dense(results, _outputNames[3]);

        var points = new Vector3[HandPose.LandmarkCount];
        var worldPoints = new Vector3[HandPose.LandmarkCount];
        float depthScale = crop.Side / InputSize;

        for (int i = 0; i < HandPose.LandmarkCount; i++)
        {
            int offset = i * 3;

            Vector2 framePoint = crop.ToFrame(new Vector2(landmarks[offset], landmarks[offset + 1]));

            // Z is scaled the same way X and Y are so all three stay in one unit system,
            // but it is a weak signal and nothing downstream should lean on it.
            points[i] = new Vector3(framePoint.X, framePoint.Y, landmarks[offset + 2] * depthScale);

            // World landmarks need the crop rotation undone but nothing else: they are
            // already metric and already hand-relative. Depth passes through untouched,
            // since the crop only ever rotated within the image plane.
            Vector2 worldXy = crop.RotateToFrame(
                new Vector2(worldLandmarks[offset], worldLandmarks[offset + 1]));

            worldPoints[i] = new Vector3(worldXy.X, worldXy.Y, worldLandmarks[offset + 2]);
        }

        return new HandPose(
            points,
            worldPoints,
            handedness >= 0.5f ? Handedness.Right : Handedness.Left,
            confidence);
    }

    public void Dispose() => _session.Dispose();

    /// <summary>
    /// Confirms the model's outputs are in the order this code assumes.
    /// </summary>
    /// <remarks>
    /// Two outputs carry 63 values and two carry one, so shape alone cannot tell
    /// landmarks from world landmarks, or confidence from handedness — only position
    /// can. That makes the ordering a real assumption, and one worth failing loudly on:
    /// a re-exported model with shuffled outputs would otherwise produce landmarks in
    /// metric hand-space, which look like a plausible hand sitting in the wrong place.
    /// </remarks>
    private void ValidateOutputs()
    {
        if (_outputNames.Length < 4)
        {
            throw new InvalidOperationException(
                $"Hand landmark model must expose at least 4 outputs, found {_outputNames.Length}.");
        }

        Expect(0, LandmarkValues, "screen landmarks");
        Expect(1, 1, "presence confidence");
        Expect(2, 1, "handedness");
        Expect(3, LandmarkValues, "world landmarks");

        void Expect(int index, int lastDimension, string role)
        {
            int actual = _session.OutputMetadata[_outputNames[index]].Dimensions[^1];
            if (actual != lastDimension)
            {
                throw new InvalidOperationException(
                    $"Expected output {index} ('{_outputNames[index]}') to be {role} with "
                    + $"final dimension {lastDimension}, but it was {actual}. The model does not "
                    + "match the layout this detector was written for.");
            }
        }
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
}
