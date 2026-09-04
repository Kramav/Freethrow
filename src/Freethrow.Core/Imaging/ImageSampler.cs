using System.Numerics;
using Freethrow.Core.Capture;

namespace Freethrow.Core.Imaging;

/// <summary>
/// Maps between a letterboxed model input and the frame it came from.
/// </summary>
/// <param name="Scale">Frame pixels per model pixel, applied uniformly to both axes.</param>
/// <param name="OffsetX">Left padding, in model pixels.</param>
/// <param name="OffsetY">Top padding, in model pixels.</param>
public readonly record struct LetterboxTransform(float Scale, float OffsetX, float OffsetY)
{
    /// <summary>Converts a point in model pixels back to frame pixels.</summary>
    public Vector2 ToFrame(Vector2 modelPoint) => new(
        (modelPoint.X - OffsetX) / Scale,
        (modelPoint.Y - OffsetY) / Scale);

    /// <summary>Converts a point in normalised model space (0..1) back to frame pixels.</summary>
    public Vector2 NormalisedToFrame(Vector2 normalised, int modelSize) =>
        ToFrame(normalised * modelSize);
}

/// <summary>
/// A square region of a frame, rotated about its centre, sampled into a model input.
/// </summary>
/// <param name="Center">Centre of the region, in frame pixels.</param>
/// <param name="Side">Side length of the region, in frame pixels.</param>
/// <param name="Rotation">
/// Rotation in radians. Positive values rotate the sampled content counter-clockwise on
/// screen, matching the convention the landmark model was trained with.
/// </param>
/// <param name="Size">Side length of the model input, in pixels.</param>
public readonly record struct RotatedCrop(Vector2 Center, float Side, float Rotation, int Size)
{
    /// <summary>
    /// Converts a point in crop pixels back to frame pixels.
    /// </summary>
    /// <remarks>
    /// This is the exact inverse of the mapping <see cref="ImageSampler.SampleRotated"/>
    /// uses to fill the crop, and both are defined here rather than derived separately.
    /// A rotated crop whose forward and inverse transforms disagree by a sign or a half
    /// pixel produces landmarks that track the hand but sit slightly beside it, which is
    /// maddening to debug from the output alone.
    /// </remarks>
    public Vector2 ToFrame(Vector2 cropPoint)
    {
        float step = Side / Size;
        float localX = (cropPoint.X - (Size / 2f)) * step;
        float localY = (cropPoint.Y - (Size / 2f)) * step;

        float cos = MathF.Cos(Rotation);
        float sin = MathF.Sin(Rotation);

        return new Vector2(
            Center.X + (cos * localX) - (sin * localY),
            Center.Y + (sin * localX) + (cos * localY));
    }
}

/// <summary>
/// Resamples camera frames into the float tensors the models expect.
/// </summary>
/// <remarks>
/// Both entry points write NHWC RGB in 0..1, which is what the MediaPipe-derived models
/// were converted with. Writing straight from the frame into the tensor avoids the
/// intermediate images a crop-rotate-resize chain would allocate every frame.
/// </remarks>
public static class ImageSampler
{
    private const int Channels = 3;

    /// <summary>
    /// Fits the whole frame into a square tensor, preserving aspect ratio and padding
    /// the remainder with black.
    /// </summary>
    /// <returns>The transform needed to map detections back to frame coordinates.</returns>
    public static LetterboxTransform LetterboxToTensor(FrameRef frame, Span<float> destination, int size)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(size);
        EnsureCapacity(destination, size);

        float scale = MathF.Min((float)size / frame.Width, (float)size / frame.Height);
        float offsetX = (size - (frame.Width * scale)) / 2f;
        float offsetY = (size - (frame.Height * scale)) / 2f;

        var view = new FrameView(frame);
        int index = 0;

        for (int v = 0; v < size; v++)
        {
            float sourceY = (v - offsetY) / scale;

            for (int u = 0; u < size; u++)
            {
                float sourceX = (u - offsetX) / scale;
                view.SampleBilinear(sourceX, sourceY, out float r, out float g, out float b);

                destination[index++] = r;
                destination[index++] = g;
                destination[index++] = b;
            }
        }

        return new LetterboxTransform(scale, offsetX, offsetY);
    }

    /// <summary>
    /// Samples a rotated square region of the frame into a square tensor.
    /// </summary>
    /// <remarks>
    /// Rotating the hand upright before landmark detection is not cosmetic: the model
    /// was trained on upright hands and its accuracy falls away sharply as the hand
    /// tilts, so the rotation is what keeps landmarks stable when the wrist turns.
    /// </remarks>
    public static void SampleRotated(FrameRef frame, Span<float> destination, RotatedCrop crop)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(crop.Size);
        EnsureCapacity(destination, crop.Size);

        int size = crop.Size;
        float step = crop.Side / size;
        float half = size / 2f;
        float cos = MathF.Cos(crop.Rotation);
        float sin = MathF.Sin(crop.Rotation);

        var view = new FrameView(frame);
        int index = 0;

        for (int v = 0; v < size; v++)
        {
            float localY = (v - half) * step;

            for (int u = 0; u < size; u++)
            {
                float localX = (u - half) * step;

                float sourceX = crop.Center.X + (cos * localX) - (sin * localY);
                float sourceY = crop.Center.Y + (sin * localX) + (cos * localY);

                view.SampleBilinear(sourceX, sourceY, out float r, out float g, out float b);

                destination[index++] = r;
                destination[index++] = g;
                destination[index++] = b;
            }
        }
    }

    private static void EnsureCapacity(Span<float> destination, int size)
    {
        int required = size * size * Channels;
        if (destination.Length < required)
        {
            throw new ArgumentException(
                $"Destination needs {required} floats for a {size}x{size} tensor, got {destination.Length}.",
                nameof(destination));
        }
    }

    /// <summary>Bilinear read access over a frame's pixels, normalising format differences.</summary>
    private readonly ref struct FrameView
    {
        private readonly ReadOnlySpan<byte> _pixels;
        private readonly int _width;
        private readonly int _height;
        private readonly int _stride;
        private readonly bool _isGray;

        public FrameView(FrameRef frame)
        {
            _pixels = frame.Pixels;
            _width = frame.Width;
            _height = frame.Height;
            _stride = frame.Stride;
            _isGray = frame.Format switch
            {
                FramePixelFormat.Gray8 => true,
                FramePixelFormat.Bgra32 => false,
                _ => throw new NotSupportedException($"Cannot sample {frame.Format} frames."),
            };
        }

        /// <summary>
        /// Samples a point, returning black outside the frame. Black rather than clamped
        /// edge pixels because that is what the models were trained against: MediaPipe
        /// pads with a constant border, and smeared edge pixels would read as texture.
        /// </summary>
        public void SampleBilinear(float x, float y, out float r, out float g, out float b)
        {
            if (x < 0 || y < 0 || x > _width - 1 || y > _height - 1)
            {
                r = g = b = 0;
                return;
            }

            int x0 = (int)x;
            int y0 = (int)y;
            int x1 = Math.Min(x0 + 1, _width - 1);
            int y1 = Math.Min(y0 + 1, _height - 1);

            float fx = x - x0;
            float fy = y - y0;

            ReadPixel(x0, y0, out float r00, out float g00, out float b00);
            ReadPixel(x1, y0, out float r10, out float g10, out float b10);
            ReadPixel(x0, y1, out float r01, out float g01, out float b01);
            ReadPixel(x1, y1, out float r11, out float g11, out float b11);

            r = Blend(r00, r10, r01, r11, fx, fy);
            g = Blend(g00, g10, g01, g11, fx, fy);
            b = Blend(b00, b10, b01, b11, fx, fy);
        }

        private static float Blend(float v00, float v10, float v01, float v11, float fx, float fy)
        {
            float top = v00 + ((v10 - v00) * fx);
            float bottom = v01 + ((v11 - v01) * fx);
            return top + ((bottom - top) * fy);
        }

        private void ReadPixel(int x, int y, out float r, out float g, out float b)
        {
            if (_isGray)
            {
                float value = _pixels[(y * _stride) + x] / 255f;
                r = g = b = value;
                return;
            }

            int offset = (y * _stride) + (x * 4);
            b = _pixels[offset] / 255f;
            g = _pixels[offset + 1] / 255f;
            r = _pixels[offset + 2] / 255f;
        }
    }
}
