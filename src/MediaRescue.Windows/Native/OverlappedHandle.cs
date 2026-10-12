using System.Runtime.InteropServices;
using System.Threading;
using MediaRescue.Core;
using Microsoft.Win32.SafeHandles;

namespace MediaRescue.Windows.Native;

/// <summary>
/// A file/device handle opened with FILE_FLAG_OVERLAPPED and bound to the thread pool. Reads have a hard timeout
/// enforced with CancelIoEx: the caller always gets control back at the deadline even when the driver never
/// completes the request. The native buffer and OVERLAPPED of an abandoned read stay alive until the kernel
/// finally completes (or cancels) it, so a late completion can never write into freed memory.
/// </summary>
internal sealed class OverlappedHandle : IDisposable
{
    private readonly SafeFileHandle _handle;
    private readonly ThreadPoolBoundHandle _bound;
    private int _pending;
    private int _disposeRequested;
    private int _released;

    public SafeFileHandle Handle => _handle;

    private OverlappedHandle(SafeFileHandle handle, ThreadPoolBoundHandle bound)
    {
        _handle = handle;
        _bound = bound;
    }

    /// <summary>Opens an overlapped handle; returns null and the Win32 error on failure.</summary>
    public static OverlappedHandle? Open(string path, uint access, uint share, uint extraFlags, out int error)
    {
        var h = NativeMethods.CreateFile(path, access, share, IntPtr.Zero, NativeMethods.OPEN_EXISTING,
            NativeMethods.FILE_FLAG_OVERLAPPED | extraFlags, IntPtr.Zero);
        if (h.IsInvalid)
        {
            error = Marshal.GetLastWin32Error();
            h.Dispose();
            return null;
        }
        return Wrap(h, out error);
    }

    /// <summary>Wraps an already opened overlapped handle (also used by tests with named pipes).</summary>
    public static OverlappedHandle? Wrap(SafeFileHandle h, out int error)
    {
        try
        {
            var bound = ThreadPoolBoundHandle.BindHandle(h);
            error = 0;
            return new OverlappedHandle(h, bound);
        }
        catch (Exception)
        {
            error = Win32Errors.ERROR_INVALID_PARAMETER;
            h.Dispose();
            return null;
        }
    }

    public long GetLength() => NativeMethods.GetFileSizeEx(_handle, out var size) ? size : -1;

    private sealed unsafe class Op
    {
        public readonly TaskCompletionSource Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public NativeOverlapped* Overlapped;
        public void* Buffer;
        public readonly object Gate = new();
        public bool Completed;
        public bool Abandoned;
        public uint Error;
        public uint Bytes;
    }

    /// <summary>
    /// Reads <paramref name="length"/> bytes at <paramref name="offset"/> into a private aligned native buffer, then copies
    /// the bytes after <paramref name="skip"/> into <paramref name="destination"/>. Returns the number of bytes copied.
    /// Throws <see cref="DiscReadException"/> on I/O error or timeout (BadSector / 121), <see cref="OperationCanceledException"/>
    /// when cancelled. A hung request never blocks the caller beyond <paramref name="timeout"/>.
    /// </summary>
    public async Task<int> ReadAsync(long offset, int length, int skip, Memory<byte> destination, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (Volatile.Read(ref _disposeRequested) != 0) throw new ObjectDisposedException(nameof(OverlappedHandle));
        if (length <= 0) return 0;

        var op = Begin(offset, length, out int startError);
        if (op == null)
        {
            // Failed synchronously: the kernel will not queue a completion packet.
            if (startError == Win32Errors.ERROR_HANDLE_EOF) return 0;
            throw Win32Errors.ToReadException(startError);
        }

        using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var delay = Task.Delay(timeout, delayCts.Token);
        var finished = await Task.WhenAny(op.Completion.Task, delay).ConfigureAwait(false);
        if (finished != op.Completion.Task)
        {
            lock (op.Gate)
            {
                if (!op.Completed)
                {
                    // Abandon: the completion callback will release the buffer whenever the kernel is done with it.
                    op.Abandoned = true;
                    Cancel(op);
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    throw Win32Errors.Timeout(timeout);
                }
            }
        }
        delayCts.Cancel();

        // Completed (possibly racing with the timer): we own the op now.
        try
        {
            uint err = op.Error;
            if (err != 0)
            {
                if (err == Win32Errors.ERROR_HANDLE_EOF) return 0;
                if (err == Win32Errors.ERROR_OPERATION_ABORTED && ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                throw Win32Errors.ToReadException((int)err);
            }
            return CopyOut(op, skip, destination);
        }
        finally
        {
            FreeOp(op);
        }
    }

    private unsafe Op? Begin(long offset, int length, out int error)
    {
        error = 0;
        var op = new Op();
        op.Buffer = NativeMemory.AlignedAlloc((nuint)length, 4096);
        try
        {
            op.Overlapped = _bound.AllocateNativeOverlapped(OnComplete, op, null);
        }
        catch
        {
            NativeMemory.AlignedFree(op.Buffer);
            throw;
        }
        op.Overlapped->OffsetLow = unchecked((int)(offset & 0xFFFFFFFF));
        op.Overlapped->OffsetHigh = (int)(offset >> 32);

        Interlocked.Increment(ref _pending);
        try
        {
            if (NativeMethods.ReadFile(_handle, op.Buffer, (uint)length, IntPtr.Zero, op.Overlapped)) return op;
            error = Marshal.GetLastWin32Error();
            if (error == Win32Errors.ERROR_IO_PENDING) { error = 0; return op; }
        }
        catch
        {
            FreeOp(op);
            throw;
        }
        FreeOp(op);
        return null;
    }

    private unsafe void Cancel(Op op) => NativeMethods.CancelIoEx(_handle, op.Overlapped);

    private static unsafe int CopyOut(Op op, int skip, Memory<byte> destination)
    {
        int bytes = (int)op.Bytes;
        if (bytes <= skip) return 0;
        int n = Math.Min(bytes - skip, destination.Length);
        new ReadOnlySpan<byte>((byte*)op.Buffer + skip, n).CopyTo(destination.Span);
        return n;
    }

    private unsafe void OnComplete(uint errorCode, uint numBytes, NativeOverlapped* overlapped)
    {
        var op = (Op)ThreadPoolBoundHandle.GetNativeOverlappedState(overlapped)!;
        bool release;
        lock (op.Gate)
        {
            op.Completed = true;
            op.Error = errorCode;
            op.Bytes = numBytes;
            release = op.Abandoned;
        }
        if (release) FreeOp(op);
        else op.Completion.TrySetResult();
    }

    private unsafe void FreeOp(Op op)
    {
        _bound.FreeNativeOverlapped(op.Overlapped);
        NativeMemory.AlignedFree(op.Buffer);
        if (Interlocked.Decrement(ref _pending) == 0 && Volatile.Read(ref _disposeRequested) != 0)
            Release();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeRequested, 1) != 0) return;
        if (Volatile.Read(ref _pending) == 0) { Release(); return; }
        // A request is still in flight (hung drive): cancel it and let the last completion close the handle.
        CancelAll();
        if (Volatile.Read(ref _pending) == 0) Release();
    }

    private unsafe void CancelAll()
    {
        try { NativeMethods.CancelIoEx(_handle, null); } catch { }
    }

    private void Release()
    {
        if (Interlocked.Exchange(ref _released, 1) != 0) return;
        // Closing a handle with a wedged driver can block; never do it on the caller's thread.
        Task.Run(() =>
        {
            try { _bound.Dispose(); } catch { }
            try { _handle.Dispose(); } catch { }
        });
    }
}


