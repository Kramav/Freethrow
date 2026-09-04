using System.Diagnostics;

namespace Freethrow.Core.Capture;

/// <summary>
/// Reads and writes single frames as uncompressed files.
/// </summary>
/// <remarks>
/// <para>
/// Deliberately not PNG or JPEG. This format exists so a frame can cross between the
/// C# pipeline and an external reference implementation with no codec on either side
/// and no chance that compression, colour management or chroma subsampling alters a
/// pixel. Comparing two trackers is only meaningful when both saw byte-identical input.
/// </para>
/// <para>
/// It is also the fixture format for headless tests: recorded frames replayed through
/// the pipeline exercise perception and gestures with no camera attached.
/// </para>
/// <para>
/// Layout: magic, version, width, height, format, stride, then <c>stride * height</c>
/// bytes of pixels. All integers little-endian.
/// </para>
/// </remarks>
public static class RawFrameFile
{
    /// <summary>Conventional file extension.</summary>
    public const string Extension = ".ftraw";

    private const uint Magic = 0x57415246; // "FRAW"
    private const int Version = 1;

    /// <summary>Writes a frame to disk.</summary>
    public static void Save(FrameRef frame, string path)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write(Magic);
        writer.Write(Version);
        writer.Write(frame.Width);
        writer.Write(frame.Height);
        writer.Write((int)frame.Format);
        writer.Write(frame.Stride);
        writer.Write(frame.Pixels);
    }

    /// <summary>Reads a frame from disk.</summary>
    public static FrameRef Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var reader = new BinaryReader(stream);

        uint magic = reader.ReadUInt32();
        if (magic != Magic)
        {
            throw new InvalidDataException($"'{path}' is not a Freethrow raw frame.");
        }

        int version = reader.ReadInt32();
        if (version != Version)
        {
            throw new InvalidDataException(
                $"'{path}' is version {version}; this build reads version {Version}.");
        }

        int width = reader.ReadInt32();
        int height = reader.ReadInt32();
        var format = (FramePixelFormat)reader.ReadInt32();
        int stride = reader.ReadInt32();

        FrameRef frame = FrameRef.Rent(width, height, format, sequence: 0, Stopwatch.GetTimestamp());
        try
        {
            if (stride != frame.Stride)
            {
                throw new InvalidDataException(
                    $"'{path}' has stride {stride}; a {width}x{height} {format} frame needs {frame.Stride}.");
            }

            int read = reader.Read(frame.Span);
            if (read != frame.Length)
            {
                throw new InvalidDataException(
                    $"'{path}' ended early: expected {frame.Length} pixel bytes, read {read}.");
            }

            return frame;
        }
        catch (Exception)
        {
            frame.Dispose();
            throw;
        }
    }
}
