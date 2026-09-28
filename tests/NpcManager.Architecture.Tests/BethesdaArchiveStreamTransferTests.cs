using System.Security.Cryptography;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestBethesdaArchiveStreamTransfer()
    {
        var invalidZero = new TrackingReadStream([1]);
        await AssertThrowsAsync<InvalidDataException>(() =>
            BethesdaArchiveStreamTransfer.TransferAsync(
                    invalidZero,
                    Stream.Null,
                    0,
                    512L * 1024 * 1024,
                    CancellationToken.None)
                .AsTask());
        Assert(
            invalidZero.ReadCallCount == 0,
            "A zero declared BSA member length reached the source stream.");

        var invalidLarge = new TrackingReadStream([1]);
        await AssertThrowsAsync<InvalidDataException>(() =>
            BethesdaArchiveStreamTransfer.TransferAsync(
                    invalidLarge,
                    Stream.Null,
                    (512L * 1024 * 1024) + 1,
                    512L * 1024 * 1024,
                    CancellationToken.None)
                .AsTask());
        Assert(
            invalidLarge.ReadCallCount == 0,
            "An oversized declared BSA member length reached the source stream.");

        byte[] content = Enumerable.Range(0, (64 * 1024 * 3) + 17)
            .Select(index => unchecked((byte)(index * 31)))
            .ToArray();
        var tracked = new TrackingReadStream(content);
        await using var destination = new MemoryStream();
        BethesdaArchiveStreamTransferResult transferred =
            await BethesdaArchiveStreamTransfer.TransferAsync(
                tracked,
                destination,
                content.LongLength,
                512L * 1024 * 1024,
                CancellationToken.None);
        Assert(
            tracked.MaximumRequestedBytes <= 64 * 1024 &&
            tracked.ReadCallCount >= 4 &&
            transferred.ByteLength == content.LongLength &&
            transferred.Sha256 == new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(content))) &&
            destination.ToArray().SequenceEqual(content),
            "BSA stream transfer was not exact, bounded to 64 KiB, and incrementally hashed.");

        var shortSource = new TrackingReadStream(content[..^1]);
        BsaMemberLengthMismatchException shortError =
            await CaptureLengthMismatchAsync(
                shortSource,
                content.LongLength);
        Assert(
            BsaMemberLengthMismatchException.DiagnosticCode ==
                "skyrim-bsa-member-length-mismatch" &&
            shortError.ActualLength == content.LongLength - 1,
            "A short BSA member stream did not produce the exact length diagnostic.");

        byte[] overlongBytes = [.. content, 0xA5];
        var overlongSource = new TrackingReadStream(overlongBytes);
        BsaMemberLengthMismatchException overlongError =
            await CaptureLengthMismatchAsync(
                overlongSource,
                content.LongLength);
        Assert(
            BsaMemberLengthMismatchException.DiagnosticCode ==
                "skyrim-bsa-member-length-mismatch" &&
            overlongError.ActualLength == content.LongLength + 1 &&
            overlongSource.MaximumRequestedBytes <= 64 * 1024,
            "An overlong BSA member stream did not produce the exact bounded length diagnostic.");

        using var cancellation = new CancellationTokenSource();
        var cancellingSource = new TrackingReadStream(
            content,
            cancellation);
        await using var cancelledDestination = new MemoryStream();
        await AssertThrowsAsync<OperationCanceledException>(() =>
            BethesdaArchiveStreamTransfer.TransferAsync(
                    cancellingSource,
                    cancelledDestination,
                    content.LongLength,
                    512L * 1024 * 1024,
                    cancellation.Token)
                .AsTask());
        Assert(
            cancelledDestination.Length == 0,
            "Cancelled BSA stream transfer committed bytes after cancellation.");

        string projectRoot = FindGate220ProjectRoot();
        string serviceSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "NpcManager.Formats.Bethesda",
            "BethesdaSkyrimBsaService.cs"));
        string helperSource = File.ReadAllText(Path.Combine(
            projectRoot,
            "src",
            "NpcManager.Formats.Bethesda",
            "BethesdaArchiveStreamTransfer.cs"));
        Assert(
            !serviceSource.Contains(
                "entry.GetBytes()",
                StringComparison.Ordinal) &&
            serviceSource.Contains(
                "entry.AsStream()",
                StringComparison.Ordinal) &&
            helperSource.Contains(
                "ArrayPool<byte>",
                StringComparison.Ordinal) &&
            helperSource.Contains(
                "IncrementalHash",
                StringComparison.Ordinal),
            "The production BSA path still contains a whole-member allocation or lacks the pooled incremental transfer.");
    }

    private static async Task<BsaMemberLengthMismatchException>
        CaptureLengthMismatchAsync(
            Stream source,
            long declaredLength)
    {
        try
        {
            await BethesdaArchiveStreamTransfer.TransferAsync(
                source,
                Stream.Null,
                declaredLength,
                512L * 1024 * 1024,
                CancellationToken.None);
        }
        catch (BsaMemberLengthMismatchException exception)
        {
            return exception;
        }

        throw new InvalidOperationException(
            "The mismatched BSA stream length was accepted.");
    }

    private static string FindGate220ProjectRoot()
    {
        for (DirectoryInfo? current =
                 new(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(
                    current.FullName,
                    "Actorwright.sln")))
            {
                return current.FullName;
            }
        }

        throw new InvalidOperationException(
            "The Actorwright project root was not found.");
    }

    private sealed class TrackingReadStream : Stream
    {
        private readonly byte[] _content;
        private readonly CancellationTokenSource? _cancelAfterRead;
        private int _position;

        public TrackingReadStream(
            byte[] content,
            CancellationTokenSource? cancelAfterRead = null)
        {
            _content = content;
            _cancelAfterRead = cancelAfterRead;
        }

        public int ReadCallCount { get; private set; }

        public int MaximumRequestedBytes { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => _content.LongLength;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReadCallCount++;
            MaximumRequestedBytes = Math.Max(
                MaximumRequestedBytes,
                buffer.Length);
            int count = Math.Min(
                buffer.Length,
                _content.Length - _position);
            if (count > 0)
            {
                _content.AsMemory(_position, count)
                    .CopyTo(buffer);
                _position += count;
                _cancelAfterRead?.Cancel();
            }

            return ValueTask.FromResult(count);
        }

        public override int Read(
            byte[] buffer,
            int offset,
            int count) =>
            throw new InvalidOperationException(
                "The BSA transfer used synchronous reads.");

        public override void Flush()
        {
        }

        public override long Seek(
            long offset,
            SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }
}
