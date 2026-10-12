using MediaRescue.Core;
using MediaRescue.Windows.Native;

namespace MediaRescue.Windows;

/// <summary>
/// Opens files of a disc for random-access, timeout-guarded reads (overlapped I/O + CancelIoEx).
/// Uses FILE_FLAG_NO_BUFFERING with sector aligned reads when possible and falls back to buffered I/O otherwise
/// (for example VCD .DAT files whose size is not a multiple of the sector size).
/// </summary>
public sealed class DiscReader : IDiscReader
{
    public DiscReader() { }

    public IDiscFile Open(string driveLetter, string relativePath)
    {
        var letter = DeviceIo.NormalizeLetter(driveLetter)
            ?? throw new DiscReadException(ReadErrorKind.Other, Win32Errors.ERROR_PATH_NOT_FOUND, "Letra de unidade inválida: " + driveLetter);
        var rel = (relativePath ?? "").Replace('/', '\\').TrimStart('\\');
        var full = @"\\?\" + letter + @"\" + rel;
        return DiscFile.Open(letter, full);
    }

    internal sealed class DiscFile : IDiscFile
    {
        private const int MaxChunk = 1 << 20;
        private const int Sector = 2048;

        private readonly string _letter;
        private readonly string _path;
        private OverlappedHandle _io;
        private bool _unbuffered;
        private bool _disposed;
        private readonly SemaphoreSlim _switchLock = new(1, 1);

        public long Length { get; }
        public int SectorSize => _unbuffered ? Sector : 1;

        private DiscFile(string letter, string path, OverlappedHandle io, bool unbuffered, long length)
        {
            _letter = letter;
            _path = path;
            _io = io;
            _unbuffered = unbuffered;
            Length = length;
        }

        public static DiscFile Open(string letter, string path)
        {
            var io = OverlappedHandle.Open(path, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_ALL,
                NativeMethods.FILE_FLAG_NO_BUFFERING, out int error);
            bool unbuffered = io != null;
            if (io == null)
            {
                // E-29 / alignment: fall back to buffered access with the same sharing mode.
                io = OverlappedHandle.Open(path, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_ALL, 0, out int error2);
                if (io == null)
                    throw OpenFailure(letter, error2 != 0 ? error2 : error);
            }
            long length = io.GetLength();
            if (length < 0)
            {
                io.Dispose();
                throw new DiscReadException(ReadErrorKind.Other, 0, "Não foi possível obter o tamanho do arquivo no disco.");
            }
            return new DiscFile(letter, path, io, unbuffered, length);
        }

        private static DiscReadException OpenFailure(string letter, int error)
        {
            // File/path not found after the medium disappeared is a "device gone" situation, not a missing file.
            if (error is Win32Errors.ERROR_FILE_NOT_FOUND or Win32Errors.ERROR_PATH_NOT_FOUND)
            {
                if (DeviceIo.GetVolumeInfo(letter, out int volErr) == null)
                    return Win32Errors.ToReadException(volErr == 0 ? Win32Errors.ERROR_NOT_READY : volErr);
                return new DiscReadException(ReadErrorKind.Other, error, $"Arquivo não encontrado no disco. Código {error}.");
            }
            return Win32Errors.ToReadException(error);
        }

        public async Task<int> ReadAsync(long offset, Memory<byte> buffer, TimeSpan timeout, CancellationToken ct)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
            if (buffer.Length == 0 || offset >= Length) return 0;

            int wanted = (int)Math.Min(buffer.Length, Length - offset);
            int done = 0;
            while (done < wanted)
            {
                ct.ThrowIfCancellationRequested();
                long pos = offset + done;
                int got;
                try
                {
                    got = await ReadChunkAsync(pos, wanted - done, buffer.Slice(done, wanted - done), timeout, ct).ConfigureAwait(false);
                }
                catch (DiscReadException ex) when (_unbuffered && ex.Win32Code == Win32Errors.ERROR_INVALID_PARAMETER)
                {
                    // Alignment not accepted by this file: switch this handle to buffered I/O and retry.
                    await SwitchToBufferedAsync().ConfigureAwait(false);
                    continue;
                }
                if (got <= 0) break; // unexpected EOF
                done += got;
            }
            return done;
        }

        private Task<int> ReadChunkAsync(long pos, int remaining, Memory<byte> dest, TimeSpan timeout, CancellationToken ct)
        {
            if (!_unbuffered)
            {
                int len = Math.Min(remaining, MaxChunk);
                return _io.ReadAsync(pos, len, 0, dest.Slice(0, len), timeout, ct);
            }
            long start = pos & ~(long)(Sector - 1);
            int skip = (int)(pos - start);
            int need = skip + remaining;
            int alignedLen = Math.Min(MaxChunk, (need + Sector - 1) & ~(Sector - 1));
            int want = Math.Min(remaining, alignedLen - skip);
            return _io.ReadAsync(start, alignedLen, skip, dest.Slice(0, want), timeout, ct);
        }

        private async Task SwitchToBufferedAsync()
        {
            await _switchLock.WaitAsync().ConfigureAwait(false);
            try
            {
                if (!_unbuffered) return;
                var io = OverlappedHandle.Open(_path, NativeMethods.GENERIC_READ, NativeMethods.FILE_SHARE_ALL, 0, out int error);
                if (io == null) throw OpenFailure(_letter, error);
                var old = _io;
                _io = io;
                _unbuffered = false;
                old.Dispose();
            }
            finally
            {
                _switchLock.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _io.Dispose();
        }
    }
}
