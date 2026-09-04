using Microsoft.ML.OnnxRuntime;

namespace Freethrow.Core.Perception.Onnx;

/// <summary>Shared ONNX Runtime session configuration.</summary>
public static class OnnxSession
{
    /// <summary>
    /// Session options tuned for a background service rather than a benchmark.
    /// </summary>
    /// <remarks>
    /// Thread counts are capped deliberately. ONNX Runtime defaults to one intra-op
    /// thread per core, which minimises single-inference latency but leaves the machine
    /// briefly unresponsive thirty times a second — unacceptable for something meant to
    /// run all day behind whatever the user is actually doing. Two threads capture most
    /// of the speedup at a fraction of the disruption.
    /// </remarks>
    public static SessionOptions CreateDefaultOptions() => new()
    {
        GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
        ExecutionMode = ExecutionMode.ORT_SEQUENTIAL,
        IntraOpNumThreads = 2,
        InterOpNumThreads = 1,
        EnableMemoryPattern = true,
    };
}
