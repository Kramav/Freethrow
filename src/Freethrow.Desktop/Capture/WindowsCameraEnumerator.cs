using Freethrow.Core.Capture;
using Windows.Media.Capture;
using Windows.Media.Capture.Frames;
using Windows.Media.MediaProperties;

namespace Freethrow.Desktop.Capture;

/// <summary>
/// Discovers cameras through WinRT frame source groups, which is what surfaces
/// infrared sources alongside ordinary colour ones.
/// </summary>
public sealed class WindowsCameraEnumerator : ICameraEnumerator
{
    /// <inheritdoc />
    public async Task<IReadOnlyList<CameraDeviceInfo>> EnumerateAsync(
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<MediaFrameSourceGroup> groups = await MediaFrameSourceGroup.FindAllAsync();
        cancellationToken.ThrowIfCancellationRequested();

        // The same physical sensor is routinely published several times: once inside a
        // combined vendor group that carries colour and infrared together, and again in
        // its own single-source group, sometimes with separate preview and record
        // streams on top. Presenting that raw would offer four "cameras" for two
        // sensors. The USB device path inside the source id is the real identity, so
        // collapse on it.
        List<Candidate> candidates = [];

        foreach (MediaFrameSourceGroup group in groups)
        {
            int videoSourceCount = group.SourceInfos.Count(IsVideoSource);

            foreach (MediaFrameSourceInfo info in group.SourceInfos.Where(IsVideoSource))
            {
                CameraKind kind = MapKind(info.SourceKind);
                if (kind is not CameraKind.Unknown)
                {
                    candidates.Add(new Candidate(group, info, kind, videoSourceCount));
                }
            }
        }

        var devices = new List<CameraDeviceInfo>();

        foreach (IGrouping<(string Path, CameraKind Kind), Candidate> sensor in
                 candidates.GroupBy(c => (Path: DevicePath(c.Info.Id), c.Kind)))
        {
            // Prefer the preview stream (tuned for latency), then the richest group —
            // the combined one can later serve colour and infrared from a single
            // MediaCapture, which is what the IR enhancement will need.
            Candidate best = sensor
                .OrderBy(c => c.Info.MediaStreamType == MediaStreamType.VideoPreview ? 0 : 1)
                .ThenByDescending(c => c.VideoSourceCount)
                .First();

            devices.Add(new CameraDeviceInfo(
                best.Info.Id,
                $"{DescribeSensor(sensor)} ({best.Kind})",
                best.Kind,
                best.Group.Id,
                DescribeSensor(sensor)));
        }

        return devices;
    }

    /// <summary>
    /// Picks the most human-meaningful name available for a sensor.
    /// </summary>
    /// <remarks>
    /// Combined groups tend to carry a vendor placeholder ("YourCameraGroup"), while the
    /// per-device group names the thing properly ("Integrated IR Webcam"). Fewest
    /// sources therefore means most specific name. The underlying DeviceInformation is
    /// only a fallback: it reports hardware identifiers such as "XPSKI9NVI - Front",
    /// which are accurate and useless in a device picker.
    /// </remarks>
    private static string DescribeSensor(IEnumerable<Candidate> sensor)
    {
        Candidate[] ordered = [.. sensor.OrderBy(c => c.VideoSourceCount)];

        string? groupName = ordered
            .Select(c => c.Group.DisplayName)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));

        if (groupName is not null)
        {
            return groupName;
        }

        return ordered
            .Select(c => c.Info.DeviceInformation?.Name)
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name)) ?? "Unnamed camera";
    }

    /// <summary>
    /// Extracts the underlying device path from a frame source id, which looks like
    /// <c>Source#0@\\?\USB#VID_...</c>. The <c>Source#N</c> prefix varies between groups
    /// for the same hardware; everything after the <c>@</c> does not.
    /// </summary>
    private static string DevicePath(string sourceId)
    {
        int separator = sourceId.IndexOf('@');
        return separator >= 0 && separator + 1 < sourceId.Length
            ? sourceId[(separator + 1)..]
            : sourceId;
    }

    private sealed record Candidate(
        MediaFrameSourceGroup Group,
        MediaFrameSourceInfo Info,
        CameraKind Kind,
        int VideoSourceCount);

    /// <inheritdoc />
    public async Task<ICameraSource> OpenAsync(
        CameraDeviceInfo device,
        CameraOpenOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(device);
        options ??= CameraOpenOptions.Default;

        MediaFrameSourceGroup group;
        try
        {
            group = await MediaFrameSourceGroup.FromIdAsync(device.GroupId);
        }
        catch (Exception ex)
        {
            throw new CameraOpenException($"Camera '{device.GroupName}' is no longer available.", ex);
        }

        cancellationToken.ThrowIfCancellationRequested();

        MediaCapture capture = await InitializeCaptureAsync(group, device).ConfigureAwait(false);
        try
        {
            if (!capture.FrameSources.TryGetValue(device.Id, out MediaFrameSource? source))
            {
                throw new CameraOpenException(
                    $"Camera '{device.DisplayName}' no longer exposes the requested source.");
            }

            IReadOnlyList<CameraFormat> supportedFormats = source.SupportedFormats
                .Select(CameraFormatFactory.FromMediaFrameFormat)
                .ToList();

            MediaFrameFormat? preferred = CameraFormatFactory.PickFormat(source.SupportedFormats, options);
            if (preferred is not null && !ReferenceEquals(preferred, source.CurrentFormat))
            {
                try
                {
                    await source.SetFormatAsync(preferred);
                }
                catch (Exception)
                {
                    // Expected when the camera was opened read-only because another app
                    // holds it. Whatever format it is already running in will do.
                }
            }

            return new MediaFrameCameraSource(
                device,
                capture,
                source,
                PreferredOutputSubtype(device.Kind),
                CameraFormatFactory.FromMediaFrameFormat(source.CurrentFormat),
                supportedFormats);
        }
        catch (Exception)
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Initialises capture, preferring exclusive control so formats can be set, and
    /// falling back to a shared read-only view when another application already holds
    /// the camera.
    /// </summary>
    private static async Task<MediaCapture> InitializeCaptureAsync(
        MediaFrameSourceGroup group,
        CameraDeviceInfo device)
    {
        try
        {
            return await InitializeCaptureAsync(group, MediaCaptureSharingMode.ExclusiveControl)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CameraAccessDeniedException(ex);
        }
        catch (Exception)
        {
            // Someone else owns the camera. Try again without exclusive control before
            // giving up — read-only still delivers frames, just at their format.
        }

        try
        {
            return await InitializeCaptureAsync(group, MediaCaptureSharingMode.SharedReadOnly)
                .ConfigureAwait(false);
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new CameraAccessDeniedException(ex);
        }
        catch (Exception ex)
        {
            throw new CameraOpenException(
                $"Could not open '{device.DisplayName}'. It may be in use by another application.",
                ex);
        }
    }

    private static async Task<MediaCapture> InitializeCaptureAsync(
        MediaFrameSourceGroup group,
        MediaCaptureSharingMode sharingMode)
    {
        // A MediaCapture that failed to initialise cannot be reused, so each attempt
        // gets its own instance.
        var capture = new MediaCapture();
        try
        {
            await capture.InitializeAsync(new MediaCaptureInitializationSettings
            {
                SourceGroup = group,
                SharingMode = sharingMode,
                MemoryPreference = MediaCaptureMemoryPreference.Cpu,
                StreamingCaptureMode = StreamingCaptureMode.Video,
            });

            return capture;
        }
        catch (Exception)
        {
            capture.Dispose();
            throw;
        }
    }

    /// <summary>
    /// The pixel format to ask the frame reader to convert into. Colour sources feed the
    /// hand and face trackers as BGRA; infrared sources are single-channel by nature.
    /// </summary>
    private static string? PreferredOutputSubtype(CameraKind kind) => kind switch
    {
        CameraKind.Color => MediaEncodingSubtypes.Bgra8,
        CameraKind.Infrared => MediaEncodingSubtypes.L8,
        _ => null,
    };

    private static bool IsVideoSource(MediaFrameSourceInfo info) =>
        info.MediaStreamType is MediaStreamType.VideoPreview or MediaStreamType.VideoRecord;

    private static CameraKind MapKind(MediaFrameSourceKind kind) => kind switch
    {
        MediaFrameSourceKind.Color => CameraKind.Color,
        MediaFrameSourceKind.Infrared => CameraKind.Infrared,
        MediaFrameSourceKind.Depth => CameraKind.Depth,
        _ => CameraKind.Unknown,
    };
}
