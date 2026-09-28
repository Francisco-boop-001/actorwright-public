using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Routes ordinary FaceGeom bake requests to the existing Blender exporter and
/// keeps finished-head transport wholly inside the independent Bethesda NIF
/// boundary. Transport never invokes Blender or PyNifly.
/// </summary>
public sealed class FaceGeomBinaryBuildRouter(
    IFaceGeomBinaryBuildService bakeService,
    INifGeometryReadbackService geometryReadback,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IFaceGeomBinaryBuildService
{
    private const long MaximumNifBytes = 512L * 1024 * 1024;

    public async ValueTask<FaceGeomBinaryBuildResult> BuildAsync(
        FaceGeomBinaryBuildRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(bakeService);
        ArgumentNullException.ThrowIfNull(geometryReadback);
        cancellationToken.ThrowIfCancellationRequested();

        return request.Operation switch
        {
            FaceGeomBinaryOperation.Bake =>
                await bakeService.BuildAsync(request, cancellationToken).ConfigureAwait(false),
            FaceGeomBinaryOperation.Transport =>
                await BuildTransportAsync(request, cancellationToken).ConfigureAwait(false),
            _ => Refused(new Diagnostic("facegeom-binary-operation-unsupported",
                DiagnosticSeverity.Error, "Unsupported FaceGeom binary operation."))
        };
    }

    private async ValueTask<FaceGeomBinaryBuildResult> BuildTransportAsync(
        FaceGeomBinaryBuildRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateTransportRequest(request);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        return request.TransportProfile switch
        {
            FaceGeomTransportProfile.CompleteCarrier =>
                await BuildCompleteCarrierAsync(request, diagnostics, cancellationToken)
                    .ConfigureAwait(false),
            FaceGeomTransportProfile.GeometryIntoCarrier =>
                await BuildGeometryIntoCarrierAsync(request, diagnostics, cancellationToken)
                    .ConfigureAwait(false),
            _ => Refused(diagnostics.AddAndReturn(new Diagnostic(
                "facegeom-binary-transport-profile-required",
                DiagnosticSeverity.Error,
                "Transport requires complete-carrier or geometry-into-carrier.")))
        };
    }

    private async ValueTask<FaceGeomBinaryBuildResult> BuildCompleteCarrierAsync(
        FaceGeomBinaryBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        byte[] sourceBytes;
        Sha256Hash sourceHash;
        try
        {
            sourceBytes = await File.ReadAllBytesAsync(request.SourceNif.Value,
                cancellationToken).ConfigureAwait(false);
            sourceHash = Hash(sourceBytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-read",
                DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-access",
                DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }

        if (request.SourceSha256 is not { } expectedSource || expectedSource != sourceHash)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-hash-mismatch",
                DiagnosticSeverity.Error,
                $"Finished-head source hash {sourceHash} does not match the requested source hash."));
            return Refused(diagnostics);
        }

        NifGeometryReadbackDocument? source = await ReadbackAsync(
            request, request.SourceNif, sourceHash, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (source is null) return Refused(diagnostics);
        if (!QualifyCarrier(sourceBytes, QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete,
                diagnostics))
            return Refused(diagnostics);

        string temporaryPath = TemporaryPath(request.OutputPath.Value, "complete");
        try
        {
            await WriteNewFileAsync(temporaryPath, sourceBytes, cancellationToken)
                .ConfigureAwait(false);
            Sha256Hash outputHash = Hash(await File.ReadAllBytesAsync(
                temporaryPath, cancellationToken).ConfigureAwait(false));
            NifGeometryReadbackDocument? output = await ReadbackAsync(
                request, new WorkspacePath(temporaryPath), outputHash, diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (output is null) return Refused(diagnostics);
            byte[] outputBytes = await File.ReadAllBytesAsync(temporaryPath, cancellationToken)
                .ConfigureAwait(false);
            if (!sourceBytes.AsSpan().SequenceEqual(outputBytes) ||
                source.ByteLength != output.ByteLength ||
                source.GraphSha256 != output.GraphSha256 ||
                source.AggregateGeometrySha256 != output.AggregateGeometrySha256 ||
                !source.Shapes.SequenceEqual(output.Shapes))
            {
                diagnostics.Add(new Diagnostic(
                    "facegeom-binary-transport-readback-mismatch",
                    DiagnosticSeverity.Error,
                    "Complete-carrier output changed bytes or independently parsed geometry evidence."));
                return Refused(diagnostics);
            }
            if (!QualifyCarrier(outputBytes,
                    QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete,
                    diagnostics))
                return Refused(diagnostics);

            if (!ValidatePromotionPaths(request.OutputPath.Value, temporaryPath, diagnostics))
                return Refused(diagnostics);
            File.Move(temporaryPath, request.OutputPath.Value, overwrite: false);
            var artifact = new FaceGeomBinaryBuildArtifact(
                "1", "facegeom-binary-sandbox-build", request.Edition.ToWireName(),
                request.OutputPath.Value, outputHash.Value, output.ByteLength,
                output.Shapes.Sum(shape => shape.VertexCount), request.SourceSha256!.Value.Value,
                [], request.Morphs, source.AggregateGeometrySha256.Value,
                output.AggregateGeometrySha256.Value, "nif-transport-complete-carrier",
                RuntimeAuthority: false);
            return new FaceGeomBinaryBuildResult(true, artifact, outputHash,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-io",
                DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-access",
                DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async ValueTask<FaceGeomBinaryBuildResult> BuildGeometryIntoCarrierAsync(
        FaceGeomBinaryBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        WorkspacePath carrierPath = request.CarrierPath!.Value;
        byte[] sourceBytes;
        byte[] carrierBytes;
        Sha256Hash sourceHash;
        Sha256Hash carrierHash;
        try
        {
            sourceBytes = await File.ReadAllBytesAsync(request.SourceNif.Value,
                cancellationToken).ConfigureAwait(false);
            carrierBytes = await File.ReadAllBytesAsync(carrierPath.Value,
                cancellationToken).ConfigureAwait(false);
            sourceHash = Hash(sourceBytes);
            carrierHash = Hash(carrierBytes);
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-read",
                DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-access",
                DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }

        if (request.SourceSha256 is not { } expectedSource || expectedSource != sourceHash)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-hash-mismatch",
                DiagnosticSeverity.Error,
                $"Finished-head source hash {sourceHash} does not match the requested source hash."));
        }
        if (request.CarrierSha256 is not { } expectedCarrier || expectedCarrier != carrierHash)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-carrier-hash-mismatch",
                DiagnosticSeverity.Error,
                $"Finished-head carrier hash {carrierHash} does not match the requested carrier hash."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        if (!QualifyCarrier(carrierBytes,
                QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete,
                diagnostics))
            return Refused(diagnostics);

        NifGeometryReadbackDocument? source = await ReadbackAsync(
            request, request.SourceNif, sourceHash, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (source is null) return Refused(diagnostics);
        NifGeometryReadbackDocument? carrier = await ReadbackAsync(
            request, carrierPath, carrierHash, diagnostics, cancellationToken)
            .ConfigureAwait(false);
        if (carrier is null) return Refused(diagnostics);
        NifGeometryShapeReadback sourceShape = SelectShape(source, request.ShapeName!, diagnostics);
        NifGeometryShapeReadback carrierShape = SelectShape(carrier, request.ShapeName!, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        if (sourceShape.VertexCount != carrierShape.VertexCount ||
            sourceShape.VertexDescriptor != carrierShape.VertexDescriptor ||
            sourceShape.VertexStride != carrierShape.VertexStride ||
            sourceShape.VertexPayloadLength != carrierShape.VertexPayloadLength ||
            sourceShape.TriangleTopologySha256 != carrierShape.TriangleTopologySha256)
        {
            diagnostics.Add(new Diagnostic(
                "facegeom-binary-transport-geometry-mismatch",
                DiagnosticSeverity.Error,
                $"Source and carrier shape '{request.ShapeName}' differ in count, descriptor, stride, payload length, or topology."));
            return Refused(diagnostics);
        }

        byte[] outputBytes = carrierBytes.ToArray();
        sourceBytes.AsSpan(checked((int)sourceShape.VertexPayloadOffset),
                sourceShape.VertexPayloadLength)
            .CopyTo(outputBytes.AsSpan(checked((int)carrierShape.VertexPayloadOffset),
                carrierShape.VertexPayloadLength));
        string temporaryPath = TemporaryPath(request.OutputPath.Value, "geometry");
        try
        {
            await WriteNewFileAsync(temporaryPath, outputBytes, cancellationToken)
                .ConfigureAwait(false);
            Sha256Hash outputHash = Hash(await File.ReadAllBytesAsync(
                temporaryPath, cancellationToken).ConfigureAwait(false));
            NifGeometryReadbackDocument? output = await ReadbackAsync(
                request, new WorkspacePath(temporaryPath), outputHash, diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (output is null) return Refused(diagnostics);
            byte[] reopenedOutputBytes = await File.ReadAllBytesAsync(
                temporaryPath, cancellationToken).ConfigureAwait(false);
            bool reopenedHashMatches = Hash(reopenedOutputBytes) == outputHash;
            if (!QualifyCarrier(reopenedOutputBytes,
                    QualifiedFaceGeomCarrierProfile.RaceMenuExportedComplete,
                    diagnostics))
                return Refused(diagnostics);
            if (!reopenedHashMatches)
            {
                diagnostics.Add(new Diagnostic(
                    "facegeom-binary-transport-output-hash-mismatch",
                    DiagnosticSeverity.Error,
                    "The exact temporary output bytes changed after independent readback."));
            }

            NifGeometryShapeReadback outputShape = SelectShape(output, request.ShapeName!, diagnostics);
            if (outputShape.VertexPayloadSha256 != sourceShape.VertexPayloadSha256 ||
                output.GraphSha256 != carrier.GraphSha256 ||
                !carrier.Shapes.Select(shape => shape.Name).SequenceEqual(
                    output.Shapes.Select(shape => shape.Name)))
            {
                diagnostics.Add(new Diagnostic(
                    "facegeom-binary-transport-output-geometry-mismatch",
                    DiagnosticSeverity.Error,
                    "The independently reopened vertex-array transport output does not match source geometry and carrier graph evidence."));
                return Refused(diagnostics);
            }

            int carrierOffset = checked((int)carrierShape.VertexPayloadOffset);
            int carrierLength = carrierShape.VertexPayloadLength;
            int suffixOffset = checked(carrierOffset + carrierLength);
            if (!carrierBytes.AsSpan(0, carrierOffset)
                    .SequenceEqual(reopenedOutputBytes.AsSpan(0, carrierOffset)) ||
                !carrierBytes.AsSpan(suffixOffset)
                    .SequenceEqual(reopenedOutputBytes.AsSpan(suffixOffset)))
            {
                diagnostics.Add(new Diagnostic(
                    "facegeom-binary-transport-authorized-range",
                    DiagnosticSeverity.Error,
                    "Geometry-into-carrier changed bytes outside the selected contiguous vertex payload."));
            }

            if (HasErrors(diagnostics) ||
                !outputBytes.AsSpan(checked((int)carrierShape.VertexPayloadOffset),
                    carrierShape.VertexPayloadLength)
                    .SequenceEqual(reopenedOutputBytes.AsSpan(
                        checked((int)carrierShape.VertexPayloadOffset),
                        carrierShape.VertexPayloadLength)))
            {
                if (!HasErrors(diagnostics))
                    diagnostics.Add(new Diagnostic(
                        "facegeom-binary-transport-authorized-range",
                        DiagnosticSeverity.Error,
                        "The exact temporary output vertex payload changed after readback."));
                return Refused(diagnostics);
            }

            if (!ValidatePromotionPaths(request.OutputPath.Value, temporaryPath, diagnostics))
                return Refused(diagnostics);
            File.Move(temporaryPath, request.OutputPath.Value, overwrite: false);
            var artifact = new FaceGeomBinaryBuildArtifact(
                "1", "facegeom-binary-sandbox-build", request.Edition.ToWireName(),
                request.OutputPath.Value, outputHash.Value, output.ByteLength,
                outputShape.VertexCount, request.SourceSha256!.Value.Value,
                [], request.Morphs, sourceShape.VertexPayloadSha256.Value,
                outputShape.VertexPayloadSha256.Value, "nif-transport-vertex-array",
                RuntimeAuthority: false, CarrierVertexSha256: carrierShape.VertexPayloadSha256.Value);
            return new FaceGeomBinaryBuildResult(true, artifact, outputHash,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-io",
                DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-access",
                DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        finally
        {
            TryDelete(temporaryPath);
        }
    }

    private async ValueTask<NifGeometryReadbackDocument?> ReadbackAsync(
        FaceGeomBinaryBuildRequest request,
        WorkspacePath path,
        Sha256Hash expectedHash,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        NifGeometryReadbackResult result = await geometryReadback.ReadAsync(
            new NifGeometryReadbackRequest(request.Edition, path, expectedHash),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(result.Diagnostics);
        return result.Accepted ? result.Document : null;
    }

    private static NifGeometryShapeReadback SelectShape(
        NifGeometryReadbackDocument document,
        string requestedName,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        NifGeometryShapeReadback[] matches = document.Shapes
            .Where(shape => string.Equals(shape.Name, requestedName, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length != 1)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-shape-missing",
                DiagnosticSeverity.Error,
                $"Exactly one recognized NIF shape named '{requestedName}' is required; found {matches.Length}."));
            return new NifGeometryShapeReadback(requestedName, string.Empty, -1, 0, 0, 0, 0,
                new Sha256Hash(new string('0', 64)), new Sha256Hash(new string('0', 64)));
        }
        return matches[0];
    }

    private static bool QualifyCarrier(
        byte[] bytes,
        QualifiedFaceGeomCarrierProfile profile,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            SseNifDocument document = SseFaceGeomCarrierCodec.Parse(bytes);
            QualifiedFaceGeomCarrierStructure structure =
                SseFaceGeomCarrierCodec.BuildStructure(document);
            SseFaceGeomCarrierCodec.Qualify(document, structure, profile, diagnostics);
            return !HasErrors(diagnostics);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-carrier-invalid",
                DiagnosticSeverity.Error, exception.Message));
            return false;
        }
        catch (OverflowException exception)
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-carrier-overflow",
                DiagnosticSeverity.Error, exception.Message));
            return false;
        }
    }

    private ImmutableArray<Diagnostic>.Builder ValidateTransportRequest(
        FaceGeomBinaryBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-edition",
                DiagnosticSeverity.Error,
                "Finished-head transport is supported only for Skyrim SE."));
        if (!request.AssetRoot.IsUnder(labRoot) || !Directory.Exists(request.AssetRoot.Value))
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-root",
                DiagnosticSeverity.Error,
                "FaceGeom transport asset roots must be existing K-local directories."));
        else
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.AssetRoot));
        ValidateSourcePath(request, diagnostics);
        ValidateOutputPath(request, diagnostics);
        ValidateMorphs(request.Morphs, diagnostics);
        if (request.SourceSha256 is null)
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-hash-required",
                DiagnosticSeverity.Error, "Transport requires an exact --source-sha256 binding."));
        if (request.TransportProfile is null)
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-profile-required",
                DiagnosticSeverity.Error,
                "Transport requires complete-carrier or geometry-into-carrier."));

        if (request.TransportProfile == FaceGeomTransportProfile.GeometryIntoCarrier)
        {
            if (request.CarrierPath is not { } carrier)
                diagnostics.Add(new Diagnostic("facegeom-binary-transport-carrier-required",
                    DiagnosticSeverity.Error, "Geometry-into-carrier requires --carrier."));
            else
                ValidateCarrierPath(request, carrier, diagnostics);
            if (request.CarrierSha256 is null)
                diagnostics.Add(new Diagnostic("facegeom-binary-transport-carrier-hash-required",
                    DiagnosticSeverity.Error, "Geometry-into-carrier requires --carrier-sha256."));
            if (string.IsNullOrWhiteSpace(request.ShapeName))
                diagnostics.Add(new Diagnostic("facegeom-binary-transport-shape-required",
                    DiagnosticSeverity.Error, "Geometry-into-carrier requires an exact --shape name."));
        }
        else if (request.CarrierPath is not null || request.CarrierSha256 is not null ||
                 !string.IsNullOrWhiteSpace(request.ShapeName))
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-carrier-unexpected",
                DiagnosticSeverity.Error,
                "Complete-carrier transport does not accept carrier or shape bindings."));
        }
        return diagnostics;
    }

    private static void ValidateSourcePath(
        FaceGeomBinaryBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        AddReparseDiagnostic(diagnostics, request.SourceNif.Value, "transport source");
        if (!request.SourceNif.IsUnder(request.AssetRoot) ||
            !File.Exists(request.SourceNif.Value) ||
            !request.SourceNif.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-invalid",
                DiagnosticSeverity.Error,
                "Transport source must be an existing .nif under the asset root."));
            return;
        }
        if (new FileInfo(request.SourceNif.Value).Length is <= 0 or > MaximumNifBytes)
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-source-size",
                DiagnosticSeverity.Error, "Transport source NIF is outside the accepted size bound."));
    }

    private static void ValidateCarrierPath(
        FaceGeomBinaryBuildRequest request,
        WorkspacePath carrier,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        AddReparseDiagnostic(diagnostics, carrier.Value, "transport carrier");
        if (!carrier.IsUnder(request.AssetRoot) || !File.Exists(carrier.Value) ||
            !carrier.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-carrier-invalid",
                DiagnosticSeverity.Error,
                "Transport carrier must be an existing .nif under the asset root."));
            return;
        }
        if (new FileInfo(carrier.Value).Length is <= 0 or > MaximumNifBytes)
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-carrier-size",
                DiagnosticSeverity.Error, "Transport carrier NIF is outside the accepted size bound."));
    }

    private void ValidateOutputPath(
        FaceGeomBinaryBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        AddReparseDiagnostic(diagnostics, request.OutputPath.Value, "transport output");
        if (!request.OutputPath.IsUnder(labRoot) ||
            !request.OutputPath.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-output-invalid",
                DiagnosticSeverity.Error, "Transport output must be a new .nif under K."));
            return;
        }
        string? parent = Path.GetDirectoryName(request.OutputPath.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic("facegeom-binary-transport-output-parent",
                DiagnosticSeverity.Error, "Transport output directory must already exist."));
        else
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (PathExists(request.OutputPath.Value))
            diagnostics.Add(new Diagnostic("facegeom-binary-output-exists",
                DiagnosticSeverity.Error, "FaceGeom outputs never overwrite existing files."));
    }

    private static void ValidateMorphs(
        ImmutableArray<FaceGeomBinaryMorph> morphs,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (FaceGeomBinaryMorph morph in morphs)
        {
            if (string.IsNullOrWhiteSpace(morph.Name) || morph.Name.Length > 255 ||
                !float.IsFinite(morph.Value) || morph.Value is < -1f or > 1f)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-morph-invalid",
                    DiagnosticSeverity.Error,
                    "FaceGeom morph names must be non-empty and values must be finite in -1..1."));
                continue;
            }
            if (!names.Add(morph.Name))
                diagnostics.Add(new Diagnostic("facegeom-binary-morph-duplicate",
                    DiagnosticSeverity.Error,
                    $"FaceGeom morph '{morph.Name}' occurs more than once."));
            if (morph.Value != 0f)
                diagnostics.Add(new Diagnostic("facegeom-binary-transport-morph-nonzero",
                    DiagnosticSeverity.Error,
                    "Finished-head transport rejects non-zero morph values."));
        }
    }

    private static async ValueTask WriteNewFileAsync(
        string path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 64 * 1024, useAsync: true);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string TemporaryPath(string outputPath, string label) =>
        Path.Combine(Path.GetDirectoryName(outputPath)!,
            $".facegeom-transport-{label}-{Guid.NewGuid():N}.nif");

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool ValidatePromotionPaths(
        string outputPath,
        string temporaryPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        int before = diagnostics.Count;
        AddReparseDiagnostic(diagnostics, outputPath, "transport output promotion");
        AddReparseDiagnostic(diagnostics, temporaryPath, "transport temporary output");
        if (PathExists(outputPath))
            diagnostics.Add(new Diagnostic("facegeom-binary-output-exists",
                DiagnosticSeverity.Error, "FaceGeom outputs never overwrite existing files."));
        return diagnostics.Count == before;
    }

    private static bool PathExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
        catch (IOException) { return true; }
        catch (UnauthorizedAccessException) { return true; }
    }

    private static void AddReparseDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        string role)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("facegeom-binary-transport-reparse",
                        DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-transport-path-inspection",
                    DiagnosticSeverity.Error, exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("facegeom-binary-transport-path-denied",
                    DiagnosticSeverity.Error, exception.Message));
                return;
            }
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase))
                break;
            current = parent;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceGeomBinaryBuildResult Refused(
        params Diagnostic[] diagnostics) =>
        new(false, null, null, diagnostics.ToImmutableArray());

    private static FaceGeomBinaryBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, null, diagnostics.ToImmutable());

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

internal static class FaceGeomTransportDiagnosticBuilderExtensions
{
    internal static ImmutableArray<Diagnostic>.Builder AddAndReturn(
        this ImmutableArray<Diagnostic>.Builder builder,
        Diagnostic diagnostic)
    {
        builder.Add(diagnostic);
        return builder;
    }
}
