using Freethrow.Core.Capture;
using Windows.Media.Capture.Frames;

namespace Freethrow.Desktop.Capture;

/// <summary>Translates WinRT frame formats into the pipeline's format record.</summary>
internal static class CameraFormatFactory
{
    /// <summary>Converts a WinRT format, tolerating the non-video formats a group can contain.</summary>
    public static CameraFormat FromMediaFrameFormat(MediaFrameFormat format)
    {
        VideoMediaFrameFormat? video = format.VideoFormat;
        double frameRate = format.FrameRate.Denominator == 0
            ? 0
            : format.FrameRate.Numerator / (double)format.FrameRate.Denominator;

        return new CameraFormat(
            (int)(video?.Width ?? 0),
            (int)(video?.Height ?? 0),
            frameRate,
            format.Subtype);
    }

    /// <summary>
    /// Picks the format closest to what the caller asked for.
    /// </summary>
    /// <remarks>
    /// Resolution dominates the score because it drives both copy cost and inference
    /// cost, and overshooting it buys nothing: hand landmarks at arm's length are
    /// resolved perfectly well at 640x480. Frame rate is weighted second — a camera
    /// that only offers 15 fps makes the whole interaction feel sticky — and MJPG is
    /// penalised last, since decoding it burns CPU the pipeline would rather spend on
    /// inference.
    /// </remarks>
    public static MediaFrameFormat? PickFormat(
        IReadOnlyList<MediaFrameFormat> formats,
        CameraOpenOptions options)
    {
        MediaFrameFormat? best = null;
        double bestScore = double.MaxValue;

        foreach (MediaFrameFormat format in formats)
        {
            VideoMediaFrameFormat? video = format.VideoFormat;
            if (video is null || video.Width == 0 || video.Height == 0)
            {
                continue;
            }

            double frameRate = format.FrameRate.Denominator == 0
                ? 0
                : format.FrameRate.Numerator / (double)format.FrameRate.Denominator;

            double resolutionPenalty =
                Math.Abs((int)video.Width - options.PreferredWidth)
                + Math.Abs((int)video.Height - options.PreferredHeight);

            double frameRatePenalty = Math.Abs(frameRate - options.PreferredFrameRate) * 20;

            double compressionPenalty =
                format.Subtype.Equals("MJPG", StringComparison.OrdinalIgnoreCase) ? 50 : 0;

            double score = resolutionPenalty + frameRatePenalty + compressionPenalty;
            if (score < bestScore)
            {
                bestScore = score;
                best = format;
            }
        }

        return best;
    }
}
