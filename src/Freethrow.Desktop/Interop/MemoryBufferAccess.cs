using System.Runtime.InteropServices;
using Windows.Foundation;
using WinRT;

namespace Freethrow.Desktop.Interop;

/// <summary>
/// Reaches the raw bytes behind a WinRT <see cref="IMemoryBufferReference"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is the only way to read a locked <c>SoftwareBitmap</c> without copying it
/// through an intermediate buffer first. At 30 fps a redundant 1.2 MB copy per frame is
/// 36 MB/s of pure waste, which is what earns the unsafe code its place here.
/// </para>
/// <para>
/// The obvious implementation — declare <c>IMemoryBufferByteAccess</c> with
/// <c>[ComImport]</c> and cast the reference to it — is what every pre-.NET 5 sample
/// shows, and it throws <c>InvalidCastException</c> here. Under CsWinRT a projected
/// object is not a classic runtime-callable wrapper, so the cast has no COM identity to
/// work with. Querying the interface explicitly and calling through its vtable works on
/// any projection, which is why the pointer arithmetic below exists.
/// </para>
/// </remarks>
internal static unsafe class MemoryBufferAccess
{
    /// <summary>IID of <c>IMemoryBufferByteAccess</c>.</summary>
    private static readonly Guid ByteAccessIid = new("5B0D3235-4DBA-4D44-865E-8F1D0E4FD04D");

    /// <summary>Slot of <c>GetBuffer</c> in the vtable, after IUnknown's three entries.</summary>
    private const int GetBufferSlot = 3;

    /// <summary>
    /// Returns a pointer to the reference's bytes.
    /// </summary>
    /// <remarks>
    /// The pointer stays valid only while <paramref name="reference"/> is alive; the
    /// caller must not let it outlive the enclosing <c>using</c>.
    /// </remarks>
    public static void GetBuffer(IMemoryBufferReference reference, out byte* data, out uint capacity)
    {
        ArgumentNullException.ThrowIfNull(reference);

        IntPtr inspectable = MarshalInspectable<object>.FromManaged(reference);
        if (inspectable == IntPtr.Zero)
        {
            throw new InvalidOperationException("Buffer reference had no native COM identity.");
        }

        try
        {
            Guid iid = ByteAccessIid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(inspectable, ref iid, out IntPtr byteAccess));

            try
            {
                byte* buffer;
                uint size;

                var getBuffer = (delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, int>)
                    (*(void***)byteAccess)[GetBufferSlot];

                Marshal.ThrowExceptionForHR(getBuffer(byteAccess, &buffer, &size));

                data = buffer;
                capacity = size;
            }
            finally
            {
                Marshal.Release(byteAccess);
            }
        }
        finally
        {
            Marshal.Release(inspectable);
        }
    }
}
