using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Per-NPC transaction that stages native FaceGeom and FaceTint independently,
/// then promotes and reopens the canonical pair as one success boundary.
/// </summary>
public sealed partial class SkyrimFaceGenNpcBakeService(
    ISkyrimNativeFaceGeomBuildService faceGeomService,
    ISkyrimNativeFaceTintPipelineService faceTintService,
    ISseFaceGeomCarrierMaterializationService faceGeomVerifier,
    IFaceTintTextureDecoder faceTintDecoder,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) :
    IFaceGenNpcBakeService, IRaceMenuExternalDescriptorFaceGenNpcBakeService
{
    public ValueTask<FaceGenNpcBakeResult> BakeAsync(
        FaceGenNpcBakeRequest request,
        CancellationToken cancellationToken)
        => BakeCoreAsync(request, null, cancellationToken);

    public ValueTask<FaceGenNpcBakeResult> BakeAsync(
        FaceGenNpcBakeRequest request,
        ExternalHeadPartDependencyDescriptor? expectedDescriptor,
        CancellationToken cancellationToken)
        => BakeCoreAsync(request, expectedDescriptor, cancellationToken);

    private async ValueTask<FaceGenNpcBakeResult> BakeCoreAsync(
        FaceGenNpcBakeRequest request,
        ExternalHeadPartDependencyDescriptor? expectedDescriptor,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Failed(request.Target, diagnostics);

        (AssetPath nifAsset, AssetPath ddsAsset) = CanonicalPaths(request.Target);
        var finalNif = new WorkspacePath(Path.Combine(request.OutputDataRoot.Value,
            nifAsset.Value.Replace('/', Path.DirectorySeparatorChar)));
        var finalDds = new WorkspacePath(Path.Combine(request.OutputDataRoot.Value,
            ddsAsset.Value.Replace('/', Path.DirectorySeparatorChar)));
        bool nifExists = File.Exists(finalNif.Value);
        bool ddsExists = File.Exists(finalDds.Value);
        if (nifExists || ddsExists)
        {
            if (nifExists && ddsExists)
            {
                diagnostics.Add(new Diagnostic("facegen-npc-existing-pair",
                    DiagnosticSeverity.Info,
                    $"Skipped {TargetLabel(request.Target)} because both canonical outputs already exist; existing bytes are not claimed as this run's artifacts."));
                return new FaceGenNpcBakeResult(
                    FaceGenNpcBakeStatus.Skipped, request.Target, null,
                    diagnostics.ToImmutable());
            }
            diagnostics.Add(Error("facegen-npc-existing-half-pair",
                $"Refused {TargetLabel(request.Target)} because only one canonical FaceGen sidecar already exists."));
            return Failed(request.Target, diagnostics);
        }

        bool outputRootCreated = false;
        string stagingRoot = Path.Combine(request.OutputDataRoot.Value,
            ".npcm-facegen-" + Guid.NewGuid().ToString("N"));
        var stageNif = new WorkspacePath(Path.Combine(stagingRoot, "facegeom.nif"));
        var stageDds = new WorkspacePath(Path.Combine(stagingRoot, "facetint.dds"));
        SkyrimNativeFaceGeomBuildArtifact? faceGeom = null;
        SkyrimNativeFaceTintBuildArtifact? faceTint = null;
        bool nifPromoted = false;
        bool ddsPromoted = false;
        try
        {
            RecordAppearanceBinding? binding = await BindRecordAppearanceAsync(
                request, diagnostics, cancellationToken).ConfigureAwait(false);
            if (binding is null) return Failed(request.Target, diagnostics);

            if (!Directory.Exists(request.OutputDataRoot.Value))
            {
                Directory.CreateDirectory(request.OutputDataRoot.Value);
                outputRootCreated = true;
            }
            EnsureOrdinaryDirectory(request.OutputDataRoot.Value, "output Data root");
            Directory.CreateDirectory(stagingRoot);
            EnsureOrdinaryDirectory(stagingRoot, "staging root");

            SkyrimNativeFaceGeomBuildResult geomResult = await faceGeomService.BuildAsync(
                new SkyrimNativeFaceGeomBuildRequest(
                    request.Edition, request.DataRoot, request.PluginOrder,
                    request.Target, ddsAsset, stageNif,
                    request.SidecarOverlay)
                {
                    SkeletonAuthority = request.SkeletonAuthority,
                    EffectiveHairColorPackedRgb =
                        binding.Appearance.HairColorPackedRgb,
                    StagedPluginAuthorities =
                        binding.Authorities,
                    NativeFaceGeomExternalHeadParts =
                        request.NativeFaceGeomExternalHeadParts,
                    ExpectedExternalHeadPartDescriptor = expectedDescriptor
                },
                cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, geomResult.Diagnostics);
            if (!geomResult.Written || !geomResult.Verified ||
                geomResult.Artifact is null || HasErrors(diagnostics))
                return Failed(request.Target, diagnostics);
            faceGeom = geomResult.Artifact;

            SkyrimNativeFaceTintPipelineResult tintResult = await faceTintService.BuildAsync(
                new SkyrimNativeFaceTintPipelineRequest(
                    request.Edition, request.DataRoot, request.PluginOrder,
                    new FormReference(request.Target.OriginatingPlugin,
                        request.Target.FormId),
                    request.Target.Sex, request.Target.Race, stageDds,
                    request.SidecarOverlay?.TintTextureOverrides.IsDefault == false
                        ? request.SidecarOverlay.TintTextureOverrides
                        : [])
                {
                    StagedPluginAuthorities = binding.Authorities
                },
                cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, tintResult.Diagnostics);
            if (!tintResult.Written || tintResult.Artifact is null ||
                HasErrors(diagnostics))
            {
                await VerifyRecordAuthoritiesAsync(request, binding, diagnostics,
                    cancellationToken).ConfigureAwait(false);
                DeleteExact(stageNif, faceGeom.Materialization.OutputSha256,
                    diagnostics);
                return Failed(request.Target, diagnostics);
            }
            faceTint = tintResult.Artifact;

            if (!await VerifyRecordAuthoritiesAsync(request, binding, diagnostics,
                    cancellationToken).ConfigureAwait(false) ||
                !await VerifyRecordCarrierAsync(request.DataRoot, binding, faceGeom, stageNif,
                    diagnostics, cancellationToken).ConfigureAwait(false))
                return Failed(request.Target, diagnostics);

            cancellationToken.ThrowIfCancellationRequested();
            CreateOrdinaryParent(finalNif.Value, request.OutputDataRoot.Value);
            CreateOrdinaryParent(finalDds.Value, request.OutputDataRoot.Value);
            File.Move(stageNif.Value, finalNif.Value, overwrite: false);
            nifPromoted = true;
            File.Move(stageDds.Value, finalDds.Value, overwrite: false);
            ddsPromoted = true;

            SseFaceGeomCarrierMaterializationProposal finalProposal =
                faceGeom.Materialization.Proposal with { OutputNif = finalNif };
            SseFaceGeomCarrierMaterializationVerificationResult nifVerification =
                await faceGeomVerifier.VerifyAsync(
                    finalProposal, CancellationToken.None).ConfigureAwait(false);
            AddDistinct(diagnostics, nifVerification.Diagnostics);
            FaceTintTextureDecodeResult ddsVerification = await faceTintDecoder.DecodeAsync(
                finalDds, CancellationToken.None).ConfigureAwait(false);
            AddDistinct(diagnostics, ddsVerification.Diagnostics);
            if (!nifVerification.Verified ||
                nifVerification.OutputSha256 != faceGeom.Materialization.OutputSha256 ||
                !ddsVerification.Decoded || ddsVerification.SourceSha256 != faceTint.OutputSha256 ||
                ddsVerification.Width != faceTint.Width ||
                ddsVerification.Height != faceTint.Height || HasErrors(diagnostics))
            {
                diagnostics.Add(Error("facegen-npc-final-pair-verification",
                    "The promoted FaceGeom/FaceTint pair did not independently reopen with its staged evidence."));
                DeleteExact(finalDds, faceTint.OutputSha256, diagnostics);
                ddsPromoted = false;
                DeleteExact(finalNif, faceGeom.Materialization.OutputSha256, diagnostics);
                nifPromoted = false;
                return Failed(request.Target, diagnostics);
            }

            var artifact = new FaceGenNpcBakeArtifact(
                request.Target,
                finalNif,
                faceGeom.Materialization.OutputSha256,
                faceGeom.Materialization.OutputByteLength,
                finalDds,
                faceTint.OutputSha256,
                checked((int)new FileInfo(finalDds.Value).Length),
                RuntimeAuthority: false)
            {
                ExternalDependencyAuthorities =
                    faceGeom.ExternalDependencyAuthorities,
                ExternalProviderSidecarAuthorities =
                    faceGeom.ExternalProviderSidecarAuthorities,
                ExternalHeadPartDependencies =
                    faceGeom.ExternalHeadPartDependencies,
                ExternalHeadPartExclusionAttestations =
                    faceGeom.ExternalHeadPartExclusionAttestations
            };
            diagnostics.Add(new Diagnostic("facegen-npc-pair-baked",
                DiagnosticSeverity.Info,
                $"Baked and independently reopened the canonical NIF/DDS pair for {TargetLabel(request.Target)}; runtime authority remains false."));
            return new FaceGenNpcBakeResult(
                FaceGenNpcBakeStatus.Baked, request.Target, artifact,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            if (ddsPromoted && faceTint is not null)
                DeleteExact(finalDds, faceTint.OutputSha256, diagnostics);
            if (nifPromoted && faceGeom is not null)
                DeleteExact(finalNif, faceGeom.Materialization.OutputSha256, diagnostics);
            if (faceTint is not null)
                DeleteExact(stageDds, faceTint.OutputSha256, diagnostics);
            if (faceGeom is not null)
                DeleteExact(stageNif, faceGeom.Materialization.OutputSha256, diagnostics);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           OverflowException)
        {
            diagnostics.Add(Error("facegen-npc-pair-write", exception.Message));
            if (ddsPromoted && faceTint is not null)
                DeleteExact(finalDds, faceTint.OutputSha256, diagnostics);
            if (nifPromoted && faceGeom is not null)
                DeleteExact(finalNif, faceGeom.Materialization.OutputSha256, diagnostics);
            if (faceTint is not null)
                DeleteExact(stageDds, faceTint.OutputSha256, diagnostics);
            if (faceGeom is not null)
                DeleteExact(stageNif, faceGeom.Materialization.OutputSha256, diagnostics);
            return Failed(request.Target, diagnostics);
        }
        finally
        {
            if (faceTint is not null)
                DeleteExact(stageDds, faceTint.OutputSha256, diagnostics);
            if (faceGeom is not null)
                DeleteExact(stageNif, faceGeom.Materialization.OutputSha256, diagnostics);
            TryDeleteDirectory(stagingRoot);
            if (outputRootCreated) TryDeleteDirectory(request.OutputDataRoot.Value);
        }
    }

    private void ValidateRequest(
        FaceGenNpcBakeRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("facegen-npc-edition",
                "The native per-NPC bake supports Skyrim Special Edition only."));
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, request.DataRoot));
        diagnostics.AddRange(workspacePolicy.Evaluate(labRoot, request.OutputDataRoot));
        if (!request.DataRoot.IsUnder(labRoot) || request.DataRoot == labRoot ||
            !Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(Error("facegen-npc-data-root",
                "The copied Data root must be an existing directory below the lab root."));
        if (!request.OutputDataRoot.IsUnder(labRoot) ||
            request.OutputDataRoot == labRoot ||
            request.OutputDataRoot.IsUnder(request.DataRoot) ||
            request.DataRoot.IsUnder(request.OutputDataRoot))
            diagnostics.Add(Error("facegen-npc-output-root",
                "The output Data root must be K-local and disjoint from the copied input Data root."));
        if (request.PluginOrder.IsDefaultOrEmpty || request.Target is null)
            diagnostics.Add(Error("facegen-npc-input-shape",
                "PluginOrder and Target must be explicit."));
    }

    private static (AssetPath Nif, AssetPath Dds) CanonicalPaths(
        FaceGenBakeTarget target)
    {
        string id = target.FormId.Value.ToString("X8");
        return (
            new AssetPath(
                $"meshes/actors/character/FaceGenData/FaceGeom/{target.OriginatingPlugin.Value}/{id}.nif"),
            new AssetPath(
                $"textures/actors/character/FaceGenData/FaceTint/{target.OriginatingPlugin.Value}/{id}.dds"));
    }

    private static void EnsureOrdinaryDirectory(string path, string role)
    {
        var info = new DirectoryInfo(path);
        if (!info.Exists || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException($"The {role} is not an ordinary directory.");
    }

    private static void CreateOrdinaryParent(string path, string outputRoot)
    {
        string parent = Path.GetDirectoryName(path) ??
                        throw new InvalidDataException("Canonical output has no parent directory.");
        Directory.CreateDirectory(parent);
        string current = parent;
        while (true)
        {
            EnsureOrdinaryDirectory(current, "canonical output ancestry");
            if (string.Equals(current, outputRoot,
                    StringComparison.OrdinalIgnoreCase))
                return;
            string? next = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(next) ||
                string.Equals(next, current, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "Canonical output ancestry escaped the output Data root.");
            current = next;
        }
    }

    private static void DeleteExact(
        WorkspacePath path,
        Sha256Hash expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!File.Exists(path.Value)) return;
            Sha256Hash actual;
            using (var stream = File.OpenRead(path.Value))
            {
                actual = new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
            }
            if (actual != expected)
            {
                diagnostics.Add(Error("facegen-npc-cleanup-hash",
                    $"Refused to remove '{path}' because its bytes no longer match this transaction."));
                return;
            }
            File.Delete(path.Value);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(Error("facegen-npc-cleanup-failed",
                $"Could not remove owned output '{path}': {exception.Message}"));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                Directory.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void AddDistinct(
        ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic diagnostic in source)
        {
            if (!target.Any(existing => existing.Code == diagnostic.Code &&
                                        existing.Severity == diagnostic.Severity &&
                                        existing.Message == diagnostic.Message))
                target.Add(diagnostic);
        }
    }

    private static string TargetLabel(FaceGenBakeTarget target) =>
        $"{target.OriginatingPlugin.Value}|{target.FormId}";

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static FaceGenNpcBakeResult Failed(
        FaceGenBakeTarget target,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(FaceGenNpcBakeStatus.Failed, target, null, diagnostics.ToImmutable());
}
