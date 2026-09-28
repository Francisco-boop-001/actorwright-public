using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuNpcFaceGeomBuildService
{
    private static async ValueTask<StagedGeneratedXyz> StageGeneratedXyzAsync(
        ImmutableArray<SkyrimRaceMenuFaceBakeShapeOutput> shapes,
        OwnedFaceGeomWrites owned,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(owned.GeneratedDirectory.Value);
        owned.MarkGeneratedDirectoryCreated();
        if (File.GetAttributes(owned.GeneratedDirectory.Value)
            .HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                "The newly created generated-XYZ directory became a reparse point.");
        }

        var files = ImmutableArray.CreateBuilder<GeneratedXyzFile>(shapes.Length);
        var authorities = ImmutableArray.CreateBuilder<RaceMenuCharGenFaceGeomShapeAuthority>(
            shapes.Length);
        for (int index = 0; index < shapes.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkyrimRaceMenuFaceBakeShapeOutput shape = shapes[index];
            byte[] packed = PackPositions(shape.FinalPositions);
            var packedHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(packed)));
            if (packedHash != shape.FinalPositionSha256)
            {
                throw new InvalidDataException(
                    $"Baked shape '{shape.CarrierShapeName}' final-position hash is not its exact float32-LE XYZ payload.");
            }

            string nameHash = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(shape.CarrierShapeName))).ToLowerInvariant()[..16];
            var path = new WorkspacePath(Path.Combine(owned.GeneratedDirectory.Value,
                $"{index:D3}-{nameHash}.xyz"));
            await using (var stream = new FileStream(path.Value, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 131072,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                owned.MarkGeneratedFileCreated(path);
                await stream.WriteAsync(packed, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            Sha256Hash reopened = await HashBoundedFileAsync(path, packed.LongLength,
                cancellationToken).ConfigureAwait(false);
            if (reopened != packedHash)
            {
                throw new InvalidDataException(
                    $"Generated XYZ '{path.Value}' changed after atomic create-and-reopen verification.");
            }
            files.Add(new GeneratedXyzFile(shape.CarrierShapeName, path, reopened,
                shape.VertexCount));
            authorities.Add(new RaceMenuCharGenFaceGeomGeneratedXyzAuthority(
                shape.CarrierShapeName,
                path,
                reopened,
                shape.VertexCount,
                shape.TopologySha256,
                "Product-owned RaceMenu morph plan evaluated from the selected headpart NIF rest positions."));
        }

        return new StagedGeneratedXyz(files.ToImmutable(), authorities.ToImmutable());
    }

    private static byte[] PackPositions(ImmutableArray<Vector3> positions)
    {
        var bytes = new byte[checked(positions.Length * 3 * sizeof(float))];
        int offset = 0;
        foreach (Vector3 position in positions)
        {
            if (!float.IsFinite(position.X) || !float.IsFinite(position.Y) ||
                !float.IsFinite(position.Z))
            {
                throw new InvalidDataException("Generated XYZ contains a non-finite position.");
            }
            WriteSingle(bytes, ref offset, position.X);
            WriteSingle(bytes, ref offset, position.Y);
            WriteSingle(bytes, ref offset, position.Z);
        }
        return bytes;
    }

    private static void WriteSingle(Span<byte> destination, ref int offset, float value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(destination[offset..],
            BitConverter.SingleToInt32Bits(value));
        offset += sizeof(float);
    }

    private sealed record GeneratedXyzFile(
        string CarrierShapeName,
        WorkspacePath Path,
        Sha256Hash Sha256,
        int VertexCount);

    private sealed record StagedGeneratedXyz(
        ImmutableArray<GeneratedXyzFile> Files,
        ImmutableArray<RaceMenuCharGenFaceGeomShapeAuthority> Authorities);

    private sealed class OwnedFaceGeomWrites
    {
        private readonly List<WorkspacePath> _generatedFiles = [];
        private bool _generatedDirectoryCreated;
        private bool _outputWritten;
        private Sha256Hash? _outputSha256;
        private long? _outputByteLength;
        private bool _committed;
        private bool _rollbackAttempted;

        public OwnedFaceGeomWrites(WorkspacePath generatedDirectory, WorkspacePath outputNif)
        {
            GeneratedDirectory = generatedDirectory;
            OutputNif = outputNif;
        }

        public WorkspacePath GeneratedDirectory { get; }
        public WorkspacePath OutputNif { get; }

        public void MarkGeneratedDirectoryCreated() => _generatedDirectoryCreated = true;
        public void MarkGeneratedFileCreated(WorkspacePath path) => _generatedFiles.Add(path);
        public void MarkOutputWritten(Sha256Hash sha256, long byteLength)
        {
            _outputWritten = true;
            _outputSha256 = sha256;
            _outputByteLength = byteLength;
        }
        public void Commit() => _committed = true;

        public ValueTask RollBackAsync(ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            if (_committed || _rollbackAttempted)
            {
                return ValueTask.CompletedTask;
            }
            _rollbackAttempted = true;

            TryCleanupOutput(diagnostics);
            foreach (WorkspacePath file in _generatedFiles.AsEnumerable().Reverse())
            {
                TryCleanupGeneratedFile(file, diagnostics);
            }
            TryCleanupGeneratedDirectory(diagnostics);
            return ValueTask.CompletedTask;
        }

        private void TryCleanupOutput(ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            if (!_outputWritten) return;
            try
            {
                if (!File.Exists(OutputNif.Value)) return;
                if (File.GetAttributes(OutputNif.Value).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("facegeom-output-cleanup-reparse",
                        "The owned failed FaceGeom output became a reparse point and was left for review."));
                    return;
                }
                var info = new FileInfo(OutputNif.Value);
                if (_outputSha256 is null || _outputByteLength is null ||
                    info.Length != _outputByteLength.Value ||
                    HashFile(OutputNif) != _outputSha256.Value)
                {
                    diagnostics.Add(Error("facegeom-output-cleanup-identity",
                        "The failed FaceGeom output no longer matches the exact file written by this invocation and was left for review."));
                    return;
                }
                File.Delete(OutputNif.Value);
            }
            catch (Exception exception)
            {
                diagnostics.Add(Error("facegeom-output-cleanup-failed", exception.Message));
            }
        }

        private void TryCleanupGeneratedFile(
            WorkspacePath file,
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            try
            {
                if (!File.Exists(file.Value)) return;
                if (!file.IsUnder(GeneratedDirectory) ||
                    File.GetAttributes(file.Value).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("facegeom-xyz-cleanup-reparse",
                        $"Owned generated XYZ '{file.Value}' was unsafe to remove and was left for review."));
                    return;
                }
                File.Delete(file.Value);
            }
            catch (Exception exception)
            {
                diagnostics.Add(Error("facegeom-xyz-cleanup-failed",
                    $"Generated XYZ cleanup failed for '{file.Value}': {exception.Message}"));
            }
        }

        private void TryCleanupGeneratedDirectory(
            ImmutableArray<Diagnostic>.Builder diagnostics)
        {
            if (!_generatedDirectoryCreated) return;
            try
            {
                if (!Directory.Exists(GeneratedDirectory.Value)) return;
                var entries = new DirectoryInfo(GeneratedDirectory.Value)
                    .EnumerateFileSystemInfos().ToArray();
                if (File.GetAttributes(GeneratedDirectory.Value)
                        .HasFlag(FileAttributes.ReparsePoint) || entries.Length != 0)
                {
                    diagnostics.Add(Error("facegeom-xyz-directory-cleanup",
                        "The owned generated-XYZ directory was not empty or became a reparse point and was left for review."));
                    return;
                }
                Directory.Delete(GeneratedDirectory.Value);
            }
            catch (Exception exception)
            {
                diagnostics.Add(Error("facegeom-xyz-directory-cleanup-failed",
                    $"Generated XYZ directory cleanup failed: {exception.Message}"));
            }
        }

        private static Sha256Hash HashFile(WorkspacePath path)
        {
            using var stream = new FileStream(path.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 131072, FileOptions.SequentialScan);
            return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
        }
    }
}
