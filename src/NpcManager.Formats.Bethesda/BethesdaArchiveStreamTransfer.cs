using System.Buffers;
using System.Security.Cryptography;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

internal readonly record struct BethesdaArchiveStreamTransferResult(
    long ByteLength,
    Sha256Hash Sha256);

internal sealed class BsaMemberLengthMismatchException
    : IOException
{
    public const string DiagnosticCode =
        "skyrim-bsa-member-length-mismatch";

    public BsaMemberLengthMismatchException(
        long declaredLength,
        long actualLength)
        : base(
            $"The BSA member stream length was {actualLength} bytes but its archive record declared {declaredLength} bytes.")
    {
        DeclaredLength = declaredLength;
        ActualLength = actualLength;
    }

    public long DeclaredLength { get; }

    public long ActualLength { get; }
}

internal static class BethesdaArchiveStreamTransfer
{
    private const int BufferSize = 64 * 1024;

    public static async ValueTask<BethesdaArchiveStreamTransferResult>
        TransferAsync(
            Stream source,
            Stream? destination,
            long declaredLength,
            long maximumLength,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!source.CanRead)
        {
            throw new ArgumentException(
                "The BSA member source stream is not readable.",
                nameof(source));
        }

        if (destination is not null && !destination.CanWrite)
        {
            throw new ArgumentException(
                "The BSA member destination stream is not writable.",
                nameof(destination));
        }

        if (declaredLength <= 0 ||
            maximumLength <= 0 ||
            declaredLength > maximumLength)
        {
            throw new InvalidDataException(
                "The declared BSA member length is empty or exceeds the admitted maximum.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long transferred = 0;
            while (transferred < declaredLength)
            {
                cancellationToken.ThrowIfCancellationRequested();
                int requested = checked((int)Math.Min(
                    BufferSize,
                    declaredLength - transferred));
                int read = await source.ReadAsync(
                        buffer.AsMemory(0, requested),
                        cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (read == 0)
                {
                    throw new BsaMemberLengthMismatchException(
                        declaredLength,
                        transferred);
                }

                hash.AppendData(buffer, 0, read);
                if (destination is not null)
                {
                    await destination.WriteAsync(
                            buffer.AsMemory(0, read),
                            cancellationToken)
                        .ConfigureAwait(false);
                }

                transferred = checked(transferred + read);
            }

            int extra = await source.ReadAsync(
                    buffer.AsMemory(0, 1),
                    cancellationToken)
                .ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            if (extra != 0)
            {
                throw new BsaMemberLengthMismatchException(
                    declaredLength,
                    checked(transferred + extra));
            }

            return new BethesdaArchiveStreamTransferResult(
                transferred,
                new Sha256Hash(Convert.ToHexString(
                    hash.GetHashAndReset())));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
