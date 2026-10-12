using System.Diagnostics;
using System.IO.Pipes;
using MediaRescue.Core;
using MediaRescue.Windows.Native;

namespace MediaRescue.Integration.Tests;

/// <summary>E-12 and the Win32 error classification of RF-05, without any disc: a named pipe never answers a read.</summary>
public sealed class OverlappedIoTests
{
    private static async Task<(NamedPipeServerStream Server, OverlappedHandle Client)> HangingPipe()
    {
        var name = "mr-hang-" + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 65536, 65536);
        var h = NativeMethods.CreateFile($@"\\.\pipe\{name}", NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE, 0, IntPtr.Zero,
            NativeMethods.OPEN_EXISTING, NativeMethods.FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        h.IsInvalid.Should().BeFalse();
        var client = OverlappedHandle.Wrap(h, out int err);
        client.Should().NotBeNull($"binding failed with {err}");
        await server.WaitForConnectionAsync();
        return (server, client!);
    }

    [Fact]
    public async Task E12_HungRead_ReturnsAtTheTimeout_AsBadSector121()
    {
        var (server, client) = await HangingPipe();
        using (server)
        using (client)
        {
            var buf = new byte[4096];
            var sw = Stopwatch.StartNew();
            var act = async () => await client.ReadAsync(0, buf.Length, 0, buf, TimeSpan.FromMilliseconds(600), CancellationToken.None);
            var ex = (await act.Should().ThrowAsync<DiscReadException>()).Which;
            sw.Stop();
            ex.Kind.Should().Be(ReadErrorKind.BadSector);
            ex.Win32Code.Should().Be(121);
            sw.Elapsed.Should().BeGreaterThan(TimeSpan.FromMilliseconds(500)).And.BeLessThan(TimeSpan.FromSeconds(3), "the caller must never be blocked past the timeout");
        }
    }

    [Fact]
    public async Task E12_HungRead_CancelReturnsQuickly()
    {
        var (server, client) = await HangingPipe();
        using (server)
        using (client)
        {
            using var cts = new CancellationTokenSource();
            var buf = new byte[4096];
            var task = client.ReadAsync(0, buf.Length, 0, buf, TimeSpan.FromSeconds(30), cts.Token);
            await Task.Delay(200);
            var sw = Stopwatch.StartNew();
            cts.Cancel();
            var act = async () => await task;
            await act.Should().ThrowAsync<OperationCanceledException>();
            sw.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(2), "Cancelar must work in <= 5 s from the user's point of view");
        }
    }

    [Fact]
    public async Task E12_LateCompletionAfterTimeout_DoesNotCorruptMemoryOrCrash_AndHandleStaysUsable()
    {
        var (server, client) = await HangingPipe();
        using (server)
        {
            var buf = new byte[1024];
            await Assert.ThrowsAsync<DiscReadException>(() => client.ReadAsync(0, buf.Length, 0, buf, TimeSpan.FromMilliseconds(200), CancellationToken.None));
            // The data arrives after the caller gave up: the abandoned request is released by its late completion.
            await server.WriteAsync(new byte[] { 1, 2, 3, 4 });
            await Task.Delay(300);
            GC.Collect(); GC.WaitForPendingFinalizers();

            // A fresh read on the same handle still works.
            await server.WriteAsync(new byte[] { 9, 8, 7 });
            var fresh = new byte[16];
            int n = await client.ReadAsync(0, 16, 0, fresh, TimeSpan.FromSeconds(5), CancellationToken.None);
            n.Should().BeGreaterThan(0);
            client.Dispose();
        }
    }

    [Fact]
    public async Task OverlappedRead_WithDataAvailable_CopiesBytesAfterSkip()
    {
        var (server, client) = await HangingPipe();
        using (server)
        using (client)
        {
            await server.WriteAsync(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
            var buf = new byte[4];
            int n = await client.ReadAsync(0, 8, 2, buf, TimeSpan.FromSeconds(5), CancellationToken.None);
            n.Should().Be(4);
            buf.Should().Equal(3, 4, 5, 6);
        }
    }

    // ---------------------------------------------------------------- RF-05 error table

    [Theory]
    [InlineData(23, ReadErrorKind.BadSector)]    // ERROR_CRC
    [InlineData(27, ReadErrorKind.BadSector)]    // ERROR_SECTOR_NOT_FOUND
    [InlineData(1117, ReadErrorKind.BadSector)]  // ERROR_IO_DEVICE
    [InlineData(121, ReadErrorKind.BadSector)]   // ERROR_SEM_TIMEOUT
    [InlineData(21, ReadErrorKind.DeviceGone)]   // ERROR_NOT_READY
    [InlineData(1112, ReadErrorKind.DeviceGone)] // ERROR_NO_MEDIA_IN_DRIVE
    [InlineData(1110, ReadErrorKind.DeviceGone)] // ERROR_MEDIA_CHANGED
    [InlineData(1167, ReadErrorKind.DeviceGone)] // ERROR_DEVICE_NOT_CONNECTED
    [InlineData(6, ReadErrorKind.DeviceGone)]    // ERROR_INVALID_HANDLE after removal
    [InlineData(5, ReadErrorKind.Other)]
    [InlineData(32, ReadErrorKind.Other)]
    [InlineData(87, ReadErrorKind.Other)]
    [InlineData(0, ReadErrorKind.Other)]
    public void E03_E04_E10_Win32ErrorsAreMappedAsListedInTheSpec(int code, ReadErrorKind expected)
    {
        Win32Errors.ClassifyRead(code).Should().Be(expected);
        var ex = Win32Errors.ToReadException(code);
        ex.Kind.Should().Be(expected);
        ex.Win32Code.Should().Be(code);
        ex.Message.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void E29_SharingViolation_HasAnExplanatoryMessage()
    {
        var ex = Win32Errors.ToReadException(32);
        ex.Kind.Should().Be(ReadErrorKind.Other);
        ex.Message.Should().Contain("em uso");
    }
}
