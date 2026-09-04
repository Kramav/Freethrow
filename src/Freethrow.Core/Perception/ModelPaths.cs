namespace Freethrow.Core.Perception;

/// <summary>
/// Locates the ONNX model files.
/// </summary>
/// <remarks>
/// Models are not committed to the repository — they are several megabytes of binary
/// that change independently of the code — so they have to be found at run time. The
/// search order runs from most explicit to most convenient, and the failure message
/// names every place that was tried, because "model not found" with no further detail
/// is a miserable thing to debug on someone else's machine.
/// </remarks>
public static class ModelPaths
{
    /// <summary>Environment variable that overrides the model directory outright.</summary>
    public const string DirectoryVariable = "FREETHROW_MODELS";

    /// <summary>File name of the palm detection model.</summary>
    public const string PalmDetection = "palm_detection.onnx";

    /// <summary>File name of the hand landmark model.</summary>
    public const string HandLandmark = "hand_landmark.onnx";

    /// <summary>Finds a model file, throwing a directed error if it is missing.</summary>
    public static string Resolve(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        var searched = new List<string>();

        foreach (string directory in CandidateDirectories())
        {
            string candidate = Path.Combine(directory, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            searched.Add(directory);
        }

        throw new FileNotFoundException(
            $"Could not find the model '{fileName}'. Run tools\\install.ps1 to download the "
            + $"models, or set {DirectoryVariable} to the directory holding them. Looked in: "
            + string.Join("; ", searched));
    }

    /// <summary>Whether every model the pipeline needs is present.</summary>
    public static bool AreModelsInstalled()
    {
        try
        {
            Resolve(PalmDetection);
            Resolve(HandLandmark);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
    }

    private static IEnumerable<string> CandidateDirectories()
    {
        string? overridden = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (!string.IsNullOrWhiteSpace(overridden))
        {
            yield return overridden;
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Freethrow",
            "models");

        // Walk up from the binary looking for a models directory, which is what makes
        // "clone, build, run" work in a development tree without any further setup.
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        for (int depth = 0; depth < 8 && directory is not null; depth++)
        {
            yield return Path.Combine(directory.FullName, "models");
            directory = directory.Parent;
        }
    }
}
