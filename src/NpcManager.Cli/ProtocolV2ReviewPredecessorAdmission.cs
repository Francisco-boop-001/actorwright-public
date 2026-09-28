using System.Collections.Immutable;
using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal interface IProtocolV2ReviewPredecessorAdmission
{
    ValueTask<ProtocolV2ReviewPredecessorLease> AdmitAsync(
        ProtocolV2PhysicalFileBinding workflow,
        ProtocolV2PhysicalFileBinding proposal,
        ProtocolV2PhysicalFileBinding preview,
        CancellationToken cancellationToken);
}

internal sealed class ProtocolV2ReviewPredecessorLease : IDisposable
{
    private List<IDisposable>? retained;
    private readonly Action revalidate;

    internal ProtocolV2ReviewPredecessorLease(
        AgentWorkflowBundleTransition workflow,
        WorkflowArtifactBinding proposal,
        WorkflowArtifactBinding preview,
        IEnumerable<IDisposable> retained,
        Action revalidate,
        IEnumerable<WorkspacePath>? protectedRoots = null)
    {
        Workflow = workflow;
        Proposal = proposal;
        Preview = preview;
        this.retained = retained.ToList();
        this.revalidate = revalidate;
        ProtectedRoots = protectedRoots?.ToImmutableArray() ?? [];
    }

    internal AgentWorkflowBundleTransition Workflow { get; }

    internal WorkflowArtifactBinding Proposal { get; }

    internal WorkflowArtifactBinding Preview { get; }

    internal ImmutableArray<WorkflowArtifactBinding> DisplayedArtifacts =>
        [Proposal, Preview];

    internal ImmutableArray<WorkspacePath> ProtectedRoots { get; }

    internal void Revalidate()
    {
        ObjectDisposedException.ThrowIf(retained is null, this);
        revalidate();
    }

    public void Dispose()
    {
        List<IDisposable>? current = Interlocked.Exchange(
            ref retained,
            null);
        if (current is null)
            return;
        for (int index = current.Count - 1; index >= 0; index--)
            current[index].Dispose();
    }
}

internal sealed class ProtocolV2ReviewPredecessorAdmission(
    WorkspacePath workspaceRoot,
    AgentWorkflowBundleTransitionService workflowLifecycle,
    IPackageVerifyService packageVerifier,
    INpcStaticBuildPackageAdmission packageAdmission,
    NpcVisualPreviewArtifactReader previewReader,
    SkyrimNpcFinishCoreWorkflowDocumentStore finishDocuments) :
    IProtocolV2ReviewPredecessorAdmission
{
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem = new(
        workspaceRoot);

    internal static ProtocolV2ReviewPredecessorAdmission CreateDefault(
        WorkspacePath workspaceRoot,
        AgentWorkflowBundleTransitionService workflowLifecycle)
    {
        var policy = new KOnlyWorkspacePolicy(
            workspaceRoot,
            ActorwrightWorkspace.ResolveProtectedRoot(workspaceRoot));
        var manifestReader = new PackageManifestReader(policy, workspaceRoot);
        return new ProtocolV2ReviewPredecessorAdmission(
            workspaceRoot,
            workflowLifecycle,
            new PackageVerifyService(manifestReader),
            new NpcStaticBuildPackageAdmission(
                workspaceRoot,
                manifestReader),
            new NpcVisualPreviewArtifactReader(workspaceRoot),
            new SkyrimNpcFinishCoreWorkflowDocumentStore(workspaceRoot));
    }

    public async ValueTask<ProtocolV2ReviewPredecessorLease> AdmitAsync(
        ProtocolV2PhysicalFileBinding workflow,
        ProtocolV2PhysicalFileBinding proposal,
        ProtocolV2PhysicalFileBinding preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(preview);
        cancellationToken.ThrowIfCancellationRequested();
        AgentWorkflowBundleTransition transition =
            workflowLifecycle.LoadForFinishCommand(
                workflow.Path,
                workflow.Sha256,
                "gui");
        FaceGeomHairRegionsPinnedReadFile workflowLease =
            fileSystem.OpenRead(workflow.Path, "review workflow bundle");
        var owned = new List<IDisposable> { workflowLease };
        try
        {
            RequireRetainedFile(
                workflowLease,
                transition.Document.Size,
                transition.Document.Sha256,
                "workflow bundle");
            string[] signature = transition.Document.Bundle.Artifacts
                .Select(item => item.Kind)
                .ToArray();
            if (signature.SequenceEqual(
                    [
                        WorkflowArtifactKinds.NpcPackageManifest,
                        WorkflowArtifactKinds.NpcPreviewManifest
                    ],
                    StringComparer.Ordinal))
                return await AdmitPackageReviewAsync(
                    transition,
                    proposal,
                    preview,
                    workflowLease,
                    owned,
                    cancellationToken).ConfigureAwait(false);
            if (signature.SequenceEqual(
                    [
                        WorkflowArtifactKinds.NpcFinishCoreRequest,
                        WorkflowArtifactKinds.NpcFinishCoreProposal,
                        WorkflowArtifactKinds.NpcPreviewManifest
                    ],
                    StringComparer.Ordinal) || AgentWorkflowService.IsFinishReviewContinuation(transition.Document.Bundle, reviewed: false))
                return await AdmitFinishReviewAsync(
                    transition,
                    proposal,
                    preview,
                    workflowLease,
                    owned,
                    cancellationToken).ConfigureAwait(false);
            throw new InvalidDataException(
                "GUI review requires exactly package/preview or request/Finish-proposal/preview evidence.");
        }
        catch
        {
            Dispose(owned);
            throw;
        }
    }

    private async ValueTask<ProtocolV2ReviewPredecessorLease>
        AdmitPackageReviewAsync(
        AgentWorkflowBundleTransition transition,
        ProtocolV2PhysicalFileBinding proposalOption,
        ProtocolV2PhysicalFileBinding previewOption,
        FaceGeomHairRegionsPinnedReadFile workflowLease,
        List<IDisposable> owned,
        CancellationToken cancellationToken)
    {
        WorkflowArtifactBinding package = RequireOptionBinding(
            transition,
            WorkflowArtifactKinds.NpcPackageManifest,
            proposalOption);
        WorkflowArtifactBinding preview = RequireOptionBinding(
            transition,
            WorkflowArtifactKinds.NpcPreviewManifest,
            previewOption);
        PackageVerifyResult verification = await packageVerifier.VerifyAsync(
            new PackageVerifyRequest(proposalOption.Path),
            cancellationToken).ConfigureAwait(false);
        if (!verification.Verified || verification.Artifact is not
            { RuntimeProof: false } artifact)
            throw new InvalidDataException(
                verification.Diagnostics.IsDefaultOrEmpty
                    ? "The review package failed independent verification."
                    : string.Join(" | ", verification.Diagnostics.Select(
                        item => $"{item.Code}: {item.Message}")));
        NpcStaticBuildPackageLease packageLease =
            await packageAdmission.AdmitPreviewAsync(
                artifact,
                package,
                transition.Document.Bundle.Npc,
                cancellationToken).ConfigureAwait(false);
        owned.Add(packageLease);
        NpcVisualPreviewArtifactDocument previewDocument = previewReader.Load(
            previewOption.Path,
            previewOption.Sha256,
            cancellationToken);
        owned.Add(previewDocument);
        ProtocolV2PreviewBindingAdmission.RequireReviewBinding(
            previewDocument,
            preview,
            package,
            packageLease.Identity,
            transition.Document.Bundle.Npc);
        void Revalidate()
        {
            RequireRetainedFile(
                workflowLease,
                transition.Document.Size,
                transition.Document.Sha256,
                "workflow bundle");
            packageLease.Revalidate();
            previewDocument.Revalidate();
            ProtocolV2PreviewBindingAdmission.RequireReviewBinding(
                previewDocument,
                preview,
                package,
                packageLease.Identity,
                transition.Document.Bundle.Npc);
        }
        Revalidate();
        var result = new ProtocolV2ReviewPredecessorLease(
            transition,
            package,
            preview,
            owned,
            Revalidate,
            [packageLease.PackageRoot]);
        owned.Clear();
        return result;
    }

    private async ValueTask<ProtocolV2ReviewPredecessorLease>
        AdmitFinishReviewAsync(
        AgentWorkflowBundleTransition transition,
        ProtocolV2PhysicalFileBinding proposalOption,
        ProtocolV2PhysicalFileBinding previewOption,
        FaceGeomHairRegionsPinnedReadFile workflowLease,
        List<IDisposable> owned,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        WorkflowArtifactBinding request = transition.Document.Bundle.Artifacts
            .Single(item => item.Kind ==
                WorkflowArtifactKinds.NpcFinishCoreRequest);
        WorkflowArtifactBinding proposal = RequireOptionBinding(
            transition,
            WorkflowArtifactKinds.NpcFinishCoreProposal,
            proposalOption);
        WorkflowArtifactBinding preview = RequireOptionBinding(
            transition,
            WorkflowArtifactKinds.NpcPreviewManifest,
            previewOption);
        SkyrimNpcFinishCoreWorkflowDocumentLease<
            SkyrimNpcFinishCoreRequestDocument> requestLease =
                finishDocuments.LoadRequestRetained(
                    request.Path,
                    request.Sha256);
        owned.Add(requestLease);
        SkyrimNpcFinishCoreWorkflowDocumentLease<
            SkyrimNpcFinishCoreProposalDocument> proposalLease =
                finishDocuments.LoadProposalForReviewRetained(
                    proposalOption.Path,
                    proposalOption.Sha256);
        owned.Add(proposalLease);
        if (requestLease.Document.Value.Source.PackageManifest is not
            { } packageManifest)
            throw new InvalidDataException(
                "The Finish request source package manifest is missing.");
        if (AgentWorkflowService.IsFinishReviewContinuation(transition.Document.Bundle, reviewed: false))
        {
            WorkflowArtifactBinding package = transition.Document.Bundle.Artifacts.Single(item => item.Kind == WorkflowArtifactKinds.NpcPackageManifest);
            if (!string.Equals(package.Path.Value, packageManifest.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(package.Sha256, requestLease.Document.Value.Source.PackageManifestSha256?.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The Finish review source package must retain the exact request binding.");
        }
        PackageVerifyResult verification = await packageVerifier.VerifyAsync(
            new PackageVerifyRequest(packageManifest),
            cancellationToken).ConfigureAwait(false);
        if (!verification.Verified || verification.Artifact is not
            { RuntimeProof: false } artifact)
            throw new InvalidDataException(
                verification.Diagnostics.IsDefaultOrEmpty
                    ? "The Finish source package failed independent verification."
                    : string.Join(" | ", verification.Diagnostics.Select(
                        item => $"{item.Code}: {item.Message}")));
        NpcStaticBuildPackageLease packageLease =
            await packageAdmission.AdmitFinishAsync(
                artifact,
                requestLease.Document,
                transition.Document.Bundle.Npc,
                cancellationToken).ConfigureAwait(false);
        owned.Add(packageLease);
        NpcVisualPreviewArtifactDocument previewDocument = previewReader.Load(
            previewOption.Path,
            previewOption.Sha256,
            cancellationToken);
        owned.Add(previewDocument);
        RequireFinishBinding(
            request,
            proposal,
            requestLease.Document,
            proposalLease.Document,
            transition.Document.Bundle.Npc);
        ProtocolV2PreviewBindingAdmission.RequireFinishReviewBinding(
            previewDocument,
            preview,
            requestLease.Document,
            packageLease.Identity,
            transition.Document.Bundle.Npc);
        void Revalidate()
        {
            RequireRetainedFile(
                workflowLease,
                transition.Document.Size,
                transition.Document.Sha256,
                "workflow bundle");
            requestLease.Revalidate(
                requestLease.Document.Sha256,
                requestLease.Document.Size);
            proposalLease.Revalidate(
                proposalLease.Document.Sha256,
                proposalLease.Document.Size);
            packageLease.Revalidate();
            previewDocument.Revalidate();
            RequireFinishBinding(
                request,
                proposal,
                requestLease.Document,
                proposalLease.Document,
                transition.Document.Bundle.Npc);
            ProtocolV2PreviewBindingAdmission.RequireFinishReviewBinding(
                previewDocument,
                preview,
                requestLease.Document,
                packageLease.Identity,
                transition.Document.Bundle.Npc);
        }
        Revalidate();
        var result = new ProtocolV2ReviewPredecessorLease(
            transition,
            proposal,
            preview,
            owned,
            Revalidate,
            [packageLease.PackageRoot]);
        owned.Clear();
        return result;
    }

    private void RequireFinishBinding(
        WorkflowArtifactBinding requestBinding,
        WorkflowArtifactBinding proposalBinding,
        SkyrimNpcFinishCoreRequestDocument request,
        SkyrimNpcFinishCoreProposalDocument proposal,
        WorkflowNpcIdentity npc)
    {
        if (!ExactDocumentBinding(requestBinding, request.Path, request.Size,
                request.Sha256) ||
            !ExactDocumentBinding(proposalBinding, proposal.Path, proposal.Size,
                proposal.Sha256) ||
            !string.Equals(
                proposalBinding.SemanticSha256,
                proposal.SemanticSha256.ToUpperInvariant(),
                StringComparison.Ordinal) ||
            !string.Equals(
                proposal.Value.RequestSha256?.Value,
                request.Sha256,
                StringComparison.OrdinalIgnoreCase) ||
            proposal.Value.Request is null ||
            request.Value.Actor.EditorId is not { } requestEditorId ||
            request.Value.Actor.FormId is not { } requestFormId ||
            npc.Plugin is null ||
            npc.LocalFormId is null ||
            !string.Equals(
                requestEditorId.Value,
                npc.EditorId,
                StringComparison.Ordinal) ||
            !string.Equals(
                request.Value.Output.PluginFileName,
                npc.Plugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                requestFormId.Value.ToString(
                    "X8",
                    CultureInfo.InvariantCulture),
                NormalizeLocalFormId(npc.LocalFormId),
                StringComparison.OrdinalIgnoreCase) ||
            !SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                    proposal.Value.Request,
                    workspaceRoot)
                .AsSpan().SequenceEqual(request.Utf8Json.AsSpan()))
            throw new InvalidDataException(
                "The Finish proposal does not bind the exact physical and nested request authority.");
    }

    private static string NormalizeLocalFormId(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;

    private static bool ExactDocumentBinding(
        WorkflowArtifactBinding binding,
        WorkspacePath path,
        long size,
        string sha256) =>
        string.Equals(
            binding.Path.Value,
            path.Value,
            StringComparison.OrdinalIgnoreCase) &&
        binding.Size == size &&
        string.Equals(binding.Sha256, sha256, StringComparison.Ordinal);

    private static WorkflowArtifactBinding RequireOptionBinding(
        AgentWorkflowBundleTransition transition,
        string kind,
        ProtocolV2PhysicalFileBinding option)
    {
        WorkflowArtifactBinding binding = transition.Document.Bundle.Artifacts
            .Single(item => item.Kind == kind);
        if (!string.Equals(
                binding.Path.Value,
                option.Path.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                binding.Sha256,
                option.Sha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The '{kind}' option differs from the exact workflow binding.");
        return binding;
    }

    private static void RequireRetainedFile(
        FaceGeomHairRegionsPinnedReadFile retained,
        long expectedSize,
        string expectedSha256,
        string role)
    {
        if (retained.Length != expectedSize ||
            !string.Equals(
                retained.ComputeSha256(),
                expectedSha256,
                StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The retained {role} changed after admission.");
    }

    private static void Dispose(IEnumerable<IDisposable> owned)
    {
        foreach (IDisposable item in owned.Reverse())
            item.Dispose();
    }
}

internal static class ProtocolV2PreviewBindingAdmission
{
    internal static void RequireExactPhysical(
        NpcVisualPreviewArtifactDocument preview,
        WorkflowArtifactBinding binding)
    {
        if (!string.Equals(
                binding.Kind,
                WorkflowArtifactKinds.NpcPreviewManifest,
                StringComparison.Ordinal) ||
            !string.Equals(
                preview.Path.Value,
                binding.Path.Value,
                StringComparison.OrdinalIgnoreCase) ||
            preview.Size != binding.Size ||
            !string.Equals(
                preview.Sha256,
                binding.Sha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                binding.SchemaOrMediaType,
                NpcVisualPreviewPersistenceContract.BundleSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                binding.ProducerCommand,
                "preview npc",
                StringComparison.Ordinal))
            throw new InvalidDataException(
                "The preview document differs from its exact workflow binding.");
    }

    internal static void RequireReviewBinding(
        NpcVisualPreviewArtifactDocument preview,
        WorkflowArtifactBinding previewBinding,
        WorkflowArtifactBinding packageBinding,
        PackageManifestIdentity package,
        WorkflowNpcIdentity npc)
    {
        RequireExactPhysical(preview, previewBinding);
        if (!previewBinding.InputArtifactHashes.Contains(
                packageBinding.Sha256,
                StringComparer.Ordinal) ||
            !string.Equals(
                package.ManifestPath.Value,
                packageBinding.Path.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                package.ManifestSha256.Value.ToUpperInvariant(),
                packageBinding.Sha256,
                StringComparison.Ordinal) ||
            !string.Equals(
                package.OutputPlugin,
                npc.Plugin,
                StringComparison.OrdinalIgnoreCase) ||
            npc.LocalFormId is null ||
            !string.Equals(
                package.TargetFormId.Value.ToString(
                    "X8",
                    CultureInfo.InvariantCulture),
                NormalizeLocalFormId(npc.LocalFormId),
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                preview.Value.Source.Identity.OwnerPlugin.Value,
                package.OutputPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                preview.Value.Source.Identity.WinningProvider.Value,
                package.OutputPlugin,
                StringComparison.OrdinalIgnoreCase) ||
            preview.Value.Source.Identity.FormId != package.TargetFormId)
            throw new InvalidDataException(
                "The preview identity and provenance differ from the exact package workflow lineage.");
        RequirePackageAsset(
            preview,
            package,
            NpcVisualAssetRole.FaceGeom,
            "facegeom");
        RequirePackageAsset(
            preview,
            package,
            NpcVisualAssetRole.FaceTint,
            "facetint");
    }

    internal static void RequireFinishReviewBinding(
        NpcVisualPreviewArtifactDocument preview,
        WorkflowArtifactBinding previewBinding,
        SkyrimNpcFinishCoreRequestDocument request,
        PackageManifestIdentity package,
        WorkflowNpcIdentity npc)
    {
        RequireExactPhysical(preview, previewBinding);
        string plugin = request.Value.Output.PluginFileName;
        if (request.Value.Actor.EditorId is not { } requestEditorId ||
            request.Value.Actor.FormId is not { } formId ||
            request.Value.Source.PackageManifestSha256 is not { } packageSha ||
            request.Value.Source.Plugin is not { } sourcePlugin ||
            request.Value.Source.PluginSha256 is null ||
            npc.Plugin is null || npc.LocalFormId is null)
            throw new InvalidDataException(
                "The Finish request/workflow NPC identity is incomplete.");
        string localFormId = formId.Value.ToString(
            "X8",
            CultureInfo.InvariantCulture);
        NpcVisualSourceGraph source = preview.Value.Source;
        if (!previewBinding.InputArtifactHashes.Contains(
                packageSha.Value.ToUpperInvariant(),
                StringComparer.Ordinal) ||
            !string.Equals(
                sourcePlugin.Value,
                plugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                requestEditorId.Value,
                npc.EditorId,
                StringComparison.Ordinal) ||
            !string.Equals(plugin, npc.Plugin, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                NormalizeLocalFormId(npc.LocalFormId),
                localFormId,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                source.Identity.OwnerPlugin.Value,
                plugin,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                source.Identity.WinningProvider.Value,
                plugin,
                StringComparison.OrdinalIgnoreCase) ||
            source.Identity.FormId != formId)
            throw new InvalidDataException(
                "The Finish preview identity and inputs differ from the exact workflow request/proposal lineage.");
        RequirePackageAsset(
            preview,
            package,
            NpcVisualAssetRole.FaceGeom,
            "facegeom");
        RequirePackageAsset(
            preview,
            package,
            NpcVisualAssetRole.FaceTint,
            "facetint");
    }

    private static void RequirePackageAsset(
        NpcVisualPreviewArtifactDocument preview,
        PackageManifestIdentity package,
        NpcVisualAssetRole role,
        string packageKind)
    {
        NpcVisualAsset[] assets = preview.Value.Source.Assets.Where(item => item.Role == role)
            .ToArray();
        PackageManifestFile[] files = package.Files.Where(item => string.Equals(
                item.Kind,
                packageKind,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (assets.Length != 1 || files.Length != 1)
            throw new InvalidDataException(
                $"The preview must bind exactly one package {packageKind} asset.");
        NpcVisualAsset asset = assets[0];
        PackageManifestFile file = files[0];
        string relative = file.RelativePath.Value.StartsWith(
            "Data/",
            StringComparison.OrdinalIgnoreCase)
            ? file.RelativePath.Value[5..]
            : file.RelativePath.Value;
        string packageRoot = Path.GetDirectoryName(package.ManifestPath.Value) ??
            throw new InvalidDataException(
                "The package manifest has no parent directory.");
        string materialized = Path.GetFullPath(Path.Combine(
            packageRoot,
            file.RelativePath.Value.Replace(
                '/', Path.DirectorySeparatorChar)));
        string previewRoot = Path.GetDirectoryName(preview.Path.Value) ??
            throw new InvalidDataException("The preview document has no parent directory.");
        string copied = Path.GetFullPath(Path.Combine(
            previewRoot, "assets", "Data",
            relative.Replace('/', Path.DirectorySeparatorChar)));
        bool directPackage = string.Equals(
                asset.Provider, package.OutputPlugin, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(asset.MaterializedPath.Value, materialized, StringComparison.OrdinalIgnoreCase);
        bool copiedPackage = string.Equals(
                asset.Provider, $"package-overlay:{Path.GetFileName(packageRoot)}", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(asset.MaterializedPath.Value, copied, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(
                asset.AssetPath.Value,
                relative,
                StringComparison.OrdinalIgnoreCase) ||
            !(directPackage || copiedPackage) ||
            asset.Bytes != file.ByteLength ||
            !string.Equals(
                asset.Sha256.Value,
                file.Sha256.Value,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"The preview {packageKind} differs from the retained package artifact.");
    }

    private static string NormalizeLocalFormId(string value) =>
        value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value[2..]
            : value;
}
