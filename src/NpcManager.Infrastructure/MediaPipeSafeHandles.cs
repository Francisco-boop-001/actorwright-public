using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace NpcManager.Infrastructure;

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MediaPipeImageFreeDelegate(nint image);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate int MediaPipeTaskCloseDelegate(
    nint task,
    out nint errorMessage);

[UnmanagedFunctionPointer(CallingConvention.Cdecl)]
internal delegate void MediaPipeErrorFreeDelegate(nint errorMessage);

internal sealed class MediaPipeImageSafeHandle
    : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly MediaPipeImageFreeDelegate _release;

    public MediaPipeImageSafeHandle(
        nint image,
        MediaPipeImageFreeDelegate release)
        : base(true)
    {
        _release = release ??
            throw new ArgumentNullException(nameof(release));
        SetHandle(image);
    }

    protected override bool ReleaseHandle()
    {
        _release(handle);
        return true;
    }
}

internal sealed class MediaPipeTaskSafeHandle
    : SafeHandleZeroOrMinusOneIsInvalid
{
    private readonly MediaPipeTaskCloseDelegate _close;
    private readonly MediaPipeErrorFreeDelegate _freeError;

    public MediaPipeTaskSafeHandle(
        nint task,
        MediaPipeTaskCloseDelegate close,
        MediaPipeErrorFreeDelegate freeError)
        : base(true)
    {
        _close = close ??
            throw new ArgumentNullException(nameof(close));
        _freeError = freeError ??
            throw new ArgumentNullException(nameof(freeError));
        SetHandle(task);
    }

    protected override bool ReleaseHandle()
    {
        nint errorMessage = nint.Zero;
        int status = _close(handle, out errorMessage);
        if (errorMessage != nint.Zero)
        {
            _freeError(errorMessage);
        }

        return status == 0;
    }
}

internal sealed class Utf8CoTaskMemSafeHandle
    : SafeHandleZeroOrMinusOneIsInvalid
{
    public Utf8CoTaskMemSafeHandle(string value)
        : base(true)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        SetHandle(Marshal.StringToCoTaskMemUTF8(value));
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeCoTaskMem(handle);
        return true;
    }
}
