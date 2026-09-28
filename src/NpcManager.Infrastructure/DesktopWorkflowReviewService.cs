using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class DesktopWorkflowReviewService :
    IDesktopWorkflowReviewService
{
    private const long MaximumJsonBytes = 4L * 1024 * 1024;
    private const long MaximumImageBytes = 32L * 1024 * 1024;
    private const long MaximumPackageFileBytes = 512L * 1024 * 1024;
    private const long MaximumAdmissionBytes = 1024L * 1024 * 1024;
    private const int MaximumRetainedFiles = 2048;

    private static readonly ImmutableHashSet<string> PackageProperties =
        Set("schemaVersion", "edition", "presetFormat", "sourcePreset",
            "sourcePresetSha256", "sourcePlugin", "sourcePluginSha256",
            "outputPlugin", "targetFormId", "artifacts");
    private static readonly ImmutableHashSet<string> PackageArtifactProperties =
        Set("kind", "relativePath", "byteLength", "sha256");
    private static readonly ImmutableHashSet<string> PreviewProperties =
        Set("schemaVersion", "sceneSchemaVersion", "label",
            "runtimeAuthority", "source", "views", "contactSheetPath",
            "contactSheetSha256", "renderEvidence", "visualEvidence",
            "diagnostics");
    private static readonly JsonSerializerOptions PreviewJsonOptions =
        CreatePreviewJsonOptions();

    private readonly AgentWorkflowBundleCodec bundleCodec;
    private readonly AgentReviewReceiptService receiptService;
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem;
    private readonly Action? beforeReceiptPublication;

    public DesktopWorkflowReviewService(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot) : this(
            workspacePolicy,
            labRoot,
            beforeReceiptPublication: null)
    {
    }

    internal DesktopWorkflowReviewService(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot,
        Action? beforeReceiptPublication)
    {
        ArgumentNullException.ThrowIfNull(workspacePolicy);
        bundleCodec = new AgentWorkflowBundleCodec(workspacePolicy, labRoot);
        receiptService = new AgentReviewReceiptService(workspacePolicy, labRoot);
        fileSystem = new FaceGeomHairRegionsPinnedFileSystem(labRoot);
        this.beforeReceiptPublication = beforeReceiptPublication;
    }

    public async ValueTask<DesktopWorkflowReviewLoadResult> LoadAsync(
        DesktopWorkflowLaunchBinding binding,
        CancellationToken cancellationToken) =>
        await Task.Run(
                () => LoadCore(binding, cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<DesktopWorkflowReviewReceiptResult>
        CreateReceiptAsync(
            DesktopWorkflowLaunchBinding binding,
            ReviewOutcome outcome,
            string? reviewerNote,
            WorkspacePath output,
            CancellationToken cancellationToken) =>
        await Task.Run(
                () => CreateReceiptCore(
                    binding,
                    outcome,
                    reviewerNote,
                    output,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

    public async ValueTask<DesktopWorkflowReviewReceiptResult>
        ReopenReceiptAsync(
            DesktopWorkflowLaunchBinding binding,
            WorkspacePath receipt,
            string receiptSha256,
            CancellationToken cancellationToken) =>
        await Task.Run(
                () => ReopenReceiptCore(
                    binding,
                    receipt,
                    receiptSha256,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

    private DesktopWorkflowReviewLoadResult LoadCore(
        DesktopWorkflowLaunchBinding binding,
        CancellationToken cancellationToken)
    {
        try
        {
            using ReviewAdmission admission = Admit(
                binding,
                cancellationToken);
            return new DesktopWorkflowReviewLoadResult(
                true,
                admission.Snapshot,
                []);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return new DesktopWorkflowReviewLoadResult(
                false,
                null,
                [Failure(exception)]);
        }
    }

    private DesktopWorkflowReviewReceiptResult CreateReceiptCore(
        DesktopWorkflowLaunchBinding binding,
        ReviewOutcome outcome,
        string? reviewerNote,
        WorkspacePath output,
        CancellationToken cancellationToken)
    {
        try
        {
            using ReviewAdmission admission = Admit(
                binding,
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            AgentReviewReceiptDocument document =
                receiptService.CreateWithPublicationValidation(
                    admission.Bundle,
                    admission.PackageProposal.Sha256,
                    [admission.PackageProposal, admission.Preview],
                    outcome,
                    reviewerNote,
                    output,
                    () =>
                    {
                        beforeReceiptPublication?.Invoke();
                        admission.Lease.Revalidate(cancellationToken);
                    },
                    () => admission.Lease.Revalidate(
                        CancellationToken.None));
            return Success(document);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return ReceiptFailure(exception);
        }
    }

    private DesktopWorkflowReviewReceiptResult ReopenReceiptCore(
        DesktopWorkflowLaunchBinding binding,
        WorkspacePath receipt,
        string receiptSha256,
        CancellationToken cancellationToken)
    {
        try
        {
            using ReviewAdmission admission = Admit(
                binding,
                cancellationToken);
            AgentReviewReceiptDocument document = receiptService.Load(
                admission.Bundle,
                admission.PackageProposal.Sha256,
                [admission.PackageProposal, admission.Preview],
                receipt,
                receiptSha256);
            admission.Lease.Revalidate(cancellationToken);
            return Success(document);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsExpected(exception))
        {
            return ReceiptFailure(exception);
        }
    }

    private ReviewAdmission Admit(
        DesktopWorkflowLaunchBinding binding,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(binding);
        cancellationToken.ThrowIfCancellationRequested();
        AgentWorkflowBundleDocument bundle = bundleCodec.Load(
            binding.Bundle,
            binding.BundleSha256);
        if (bundle.Bundle.Phase != AgentWorkflowPhase.Review ||
            bundle.Bundle.Artifacts.Length != 2)
            throw new InvalidDataException(
                "Desktop review admits only the exact Review-phase package-and-preview signature.");
        ImmutableDictionary<string, WorkflowArtifactBinding> artifacts =
            bundle.Bundle.Artifacts.ToImmutableDictionary(
                item => item.Kind,
                StringComparer.Ordinal);
        if (!artifacts.TryGetValue(
                WorkflowArtifactKinds.NpcPackageManifest,
                out WorkflowArtifactBinding? package) ||
            !artifacts.TryGetValue(
                WorkflowArtifactKinds.NpcPreviewManifest,
                out WorkflowArtifactBinding? preview) ||
            artifacts.Count != 2)
            throw new InvalidDataException(
                "Desktop review admits exactly one package manifest and one preview manifest.");

        var lease = new ReviewAdmissionLease(fileSystem);
        try
        {
            lease.OpenHashed(
                bundle.Path,
                expectedLength: null,
                bundle.Sha256,
                MaximumJsonBytes,
                "workflow bundle",
                cancellationToken);
            PackageReview packageReview = ReadPackageProposal(
                package,
                bundle.Bundle.Npc,
                lease,
                cancellationToken);
            DesktopWorkflowPreviewPresentation previewPresentation =
                ReadPreview(
                    preview,
                    bundle.Bundle.Npc,
                    packageReview,
                    lease,
                    cancellationToken);
            lease.Revalidate(cancellationToken);

            ImmutableDictionary<string, VerifiedWorkflowArtifact> verified =
                artifacts.Values.ToImmutableDictionary(
                    item => item.Kind,
                    item => new VerifiedWorkflowArtifact(
                        item.Kind,
                        item.Path,
                        item.Size,
                        item.Sha256,
                        true),
                    StringComparer.Ordinal);
            WorkflowEvaluation evaluation = AgentWorkflowService.Evaluate(
                new WorkflowEvaluationInput(
                    bundle.Bundle,
                    verified,
                    null,
                    null,
                    null));
            if (evaluation.Outcome != WorkflowEvaluationOutcome.Blocked ||
                evaluation.Phase != AgentWorkflowPhase.Review ||
                evaluation.Authority.Length !=
                    Enum.GetValues<AgentAuthorityKind>().Length ||
                evaluation.Authority.Select(item => item.Kind)
                    .Distinct().Count() !=
                    Enum.GetValues<AgentAuthorityKind>().Length ||
                evaluation.Diagnostics is not [var prerequisite] ||
                !string.Equals(
                    prerequisite.Code,
                    "workflow-next-action-prerequisites-missing",
                    StringComparison.Ordinal) ||
                prerequisite.Severity != DiagnosticSeverity.Error ||
                prerequisite.Class != DiagnosticClass.Validation ||
                evaluation.NextActions is not [var nextAction] ||
                !string.Equals(
                    nextAction.Command,
                    "gui",
                    StringComparison.Ordinal) ||
                nextAction.RequiredBindings is not
                    [
                        {
                            Option: "--launch",
                            Value: "true",
                            ArtifactSha256: null
                        }
                    ] ||
                !nextAction.MissingPrerequisites.SequenceEqual(
                    ["--executable", "--workflow-bundle",
                     "--workflow-bundle-sha256"],
                    StringComparer.Ordinal) ||
                !nextAction.RequiresHumanAction ||
                !HasExactDesktopReviewAuthority(evaluation.Authority))
                throw new InvalidDataException(
                    "The exact package-and-preview graph is not a coherent Desktop Review transition.");

            ImmutableArray<DesktopWorkflowArtifactPresentation> rows =
                bundle.Bundle.Artifacts.Select(item =>
                    new DesktopWorkflowArtifactPresentation(
                        item.Kind,
                        item.SchemaOrMediaType,
                        item.Path,
                        item.Size,
                        item.Sha256,
                        item.ProducerCommand)).ToImmutableArray();
            var snapshot = new DesktopWorkflowReviewSnapshot(
                bundle.Bundle.Npc.EditorId,
                bundle.Bundle.Npc.DisplayName,
                bundle.Bundle.Game.ToWireName(),
                evaluation.Phase,
                bundle.Path,
                bundle.Sha256,
                rows,
                evaluation.Authority,
                evaluation.Diagnostics,
                evaluation.NextActions,
                packageReview.Presentation,
                previewPresentation,
                AgentReviewContract.FinishAnalyzeScope,
                AgentReviewContract.FinishAnalyzeAuthorityNotice,
                AgentReviewContract.FinishAnalyzeAuthorityNoticeSha256);
            return new ReviewAdmission(
                bundle,
                package,
                preview,
                snapshot,
                lease);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private static PackageReview ReadPackageProposal(
        WorkflowArtifactBinding binding,
        WorkflowNpcIdentity npc,
        ReviewAdmissionLease lease,
        CancellationToken cancellationToken)
    {
        byte[] bytes = lease.OpenBytes(
            binding.Path,
            binding.Size,
            binding.Sha256,
            MaximumJsonBytes,
            "package proposal",
            cancellationToken);
        using JsonDocument document = ParseStrictJson(
            bytes,
            "package proposal");
        JsonElement root = document.RootElement;
        RequireExactProperties(root, PackageProperties, "package proposal");
        RequireNumber(root, "schemaVersion", 1);
        if (RequireString(root, "edition") != "skyrimse" ||
            RequireString(root, "presetFormat") != "racemenu-jslot")
            throw new InvalidDataException(
                "The package proposal must target Skyrim SE and RaceMenu JSlot.");
        _ = RequireString(root, "sourcePreset");
        _ = RequireHash(root, "sourcePresetSha256");
        _ = new PluginName(RequireString(root, "sourcePlugin"));
        _ = RequireHash(root, "sourcePluginSha256");
        var outputPlugin = new PluginName(
            RequireString(root, "outputPlugin"));
        string targetText = RequireString(root, "targetFormId");
        if (!FormId.TryParse(targetText, out FormId targetFormId))
            throw new InvalidDataException(
                "The package proposal target FormID is invalid.");
        RequireNpcIdentity(npc, outputPlugin, targetFormId);

        JsonElement artifacts = RequireArray(root, "artifacts");
        if (artifacts.GetArrayLength() is <= 0 or > MaximumRetainedFiles)
            throw new InvalidDataException(
                $"Desktop review admits 1..{MaximumRetainedFiles} package artifacts.");
        string? parent = Path.GetDirectoryName(binding.Path.Value);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidDataException(
                "The package proposal has no retained package root.");
        var packageRoot = new WorkspacePath(parent);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changes = ImmutableArray.CreateBuilder<string>();
        int outputPluginArtifacts = 0;
        changes.Add($"Output plugin: {outputPlugin.Value}");
        changes.Add($"Target NPC FormID: {targetFormId}");
        foreach (JsonElement artifact in artifacts.EnumerateArray())
        {
            RequireExactProperties(
                artifact,
                PackageArtifactProperties,
                "package artifact");
            string kind = RequireString(artifact, "kind");
            var relative = new AssetPath(
                RequireString(artifact, "relativePath"));
            if (string.Equals(kind, "plugin", StringComparison.OrdinalIgnoreCase))
            {
                outputPluginArtifacts++;
                if (!string.Equals(
                        Path.GetFileName(relative.Value),
                        outputPlugin.Value,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "The package plugin artifact does not match outputPlugin.");
            }
            if (!paths.Add(relative.Value))
                throw new InvalidDataException(
                    "The package proposal repeats an artifact path.");
            long length = RequireInt64(artifact, "byteLength");
            if (length <= 0 || length > MaximumPackageFileBytes)
                throw new InvalidDataException(
                    "A package artifact byte length exceeds the Desktop review bound.");
            string sha256 = RequireHash(artifact, "sha256");
            var physical = new WorkspacePath(Path.Combine(
                packageRoot.Value,
                relative.Value.Replace(
                    '/',
                    Path.DirectorySeparatorChar)));
            if (!physical.IsUnder(packageRoot) || physical == packageRoot)
                throw new UnauthorizedAccessException(
                    "A package artifact escaped its retained package root.");
            lease.OpenHashed(
                physical,
                length,
                sha256,
                MaximumPackageFileBytes,
                $"package artifact '{relative.Value}'",
                cancellationToken);
            changes.Add($"{kind}: {relative.Value} ({length} bytes)");
        }
        if (outputPluginArtifacts != 1)
            throw new InvalidDataException(
                "The package must declare exactly one output plugin artifact.");

        return new PackageReview(
            outputPlugin,
            targetFormId,
            new DesktopWorkflowProposalPresentation(
                binding.Kind,
                binding.Path,
                binding.Sha256,
                "NPC package proposal",
                changes.ToImmutable()));
    }

    private static DesktopWorkflowPreviewPresentation ReadPreview(
        WorkflowArtifactBinding binding,
        WorkflowNpcIdentity npc,
        PackageReview package,
        ReviewAdmissionLease lease,
        CancellationToken cancellationToken)
    {
        byte[] bytes = lease.OpenBytes(
            binding.Path,
            binding.Size,
            binding.Sha256,
            MaximumJsonBytes,
            "preview manifest",
            cancellationToken);
        NpcVisualPreviewPersistenceDocument preview = ParsePreview(bytes);
        if (preview.Source is null ||
            preview.RenderEvidence is null ||
            preview.VisualEvidence is null ||
            !string.Equals(
                preview.SchemaVersion,
                NpcVisualPreviewPersistenceContract.BundleSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                preview.SceneSchemaVersion,
                NpcVisualPreviewPersistenceContract.SceneSchema,
                StringComparison.Ordinal) ||
            !string.Equals(
                preview.Label,
                NpcVisualPreviewPersistenceContract.OffEngineLabel,
                StringComparison.Ordinal) ||
            preview.RuntimeAuthority)
            throw new InvalidDataException(
                "The preview contract, label, or authority boundary is invalid.");
        RequirePreviewIdentity(
            preview.Source.Identity,
            npc,
            package.OutputPlugin,
            package.TargetFormId);
        ValidateSource(preview.Source, lease, cancellationToken);
        ValidateRenderEvidence(
            preview.Source,
            preview.RenderEvidence,
            lease,
            cancellationToken);
        ValidateVisualEvidence(preview.VisualEvidence);
        RequireNoErrorDiagnostics(preview.Diagnostics, "preview bundle");

        string? parent = Path.GetDirectoryName(binding.Path.Value);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidDataException(
                "The preview manifest has no retained preview root.");
        var previewRoot = new WorkspacePath(parent);
        byte[] contact = lease.OpenBytesBelow(
            preview.ContactSheetPath,
            WorkflowHash(preview.ContactSheetSha256),
            previewRoot,
            MaximumImageBytes,
            "preview contact sheet",
            cancellationToken);
        RequirePng(contact, "preview contact sheet");

        if (preview.Views.IsDefault ||
            preview.Views.Length !=
                NpcVisualPreviewPersistenceContract.RequiredViewIds.Length ||
            preview.Views.Any(item => item is null))
            throw new InvalidDataException(
                "The preview must contain the six deterministic views.");
        var projected = ImmutableArray.CreateBuilder<
            DesktopWorkflowPreviewViewPresentation>();
        for (int index = 0; index < preview.Views.Length; index++)
        {
            NpcVisualPreviewView view = preview.Views[index];
            string expectedId =
                NpcVisualPreviewPersistenceContract.RequiredViewIds[index];
            if (!string.Equals(view.Id, expectedId, StringComparison.Ordinal) ||
                view.Width <= 0 ||
                view.Height <= 0)
                throw new InvalidDataException(
                    "The preview view set, order, or dimensions are invalid.");
            byte[] image = lease.OpenBytesBelow(
                view.ImagePath,
                WorkflowHash(view.ImageSha256),
                previewRoot,
                MaximumImageBytes,
                $"preview view '{view.Id}'",
                cancellationToken);
            byte[] mask = lease.OpenBytesBelow(
                view.RoleMaskPath,
                WorkflowHash(view.RoleMaskSha256),
                previewRoot,
                MaximumImageBytes,
                $"preview role mask '{view.Id}'",
                cancellationToken);
            RequirePng(image, $"preview view '{view.Id}'", view.Width, view.Height);
            RequirePng(mask, $"preview role mask '{view.Id}'", view.Width, view.Height);
            projected.Add(new DesktopWorkflowPreviewViewPresentation(
                view.Id,
                view.ImagePath,
                WorkflowHash(view.ImageSha256),
                view.RoleMaskPath,
                WorkflowHash(view.RoleMaskSha256),
                view.Width,
                view.Height));
        }

        return new DesktopWorkflowPreviewPresentation(
            binding.Path,
            binding.Sha256,
            preview.Label,
            preview.ContactSheetPath,
            WorkflowHash(preview.ContactSheetSha256),
            contact.ToImmutableArray(),
            projected.ToImmutable());
    }

    private static NpcVisualPreviewPersistenceDocument ParsePreview(
        byte[] bytes)
    {
        using JsonDocument shape = ParseStrictJson(bytes, "preview manifest");
        RequireExactProperties(
            shape.RootElement,
            PreviewProperties,
            "preview manifest");
        try
        {
            return JsonSerializer.Deserialize<
                    NpcVisualPreviewPersistenceDocument>(
                    bytes,
                    PreviewJsonOptions) ??
                throw new InvalidDataException(
                    "The preview manifest decoded as empty.");
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
                NotSupportedException)
        {
            throw new InvalidDataException(
                "The preview manifest does not match the exact producer contract.",
                exception);
        }
    }

    private static JsonSerializerOptions CreatePreviewJsonOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new SingleValueObjectConverter<PluginName>(
            value => new PluginName(value), value => value.Value));
        options.Converters.Add(new SingleValueObjectConverter<WorkspacePath>(
            value => new WorkspacePath(value), value => value.Value));
        options.Converters.Add(new SingleValueObjectConverter<Sha256Hash>(
            value => new Sha256Hash(value), value => value.Value));
        options.Converters.Add(new SingleValueObjectConverter<AssetPath>(
            value => new AssetPath(value), value => value.Value));
        options.Converters.Add(new FormIdObjectConverter());
        return options;
    }

    private static void ValidateSource(
        NpcVisualSourceGraph source,
        ReviewAdmissionLease lease,
        CancellationToken cancellationToken)
    {
        if (!Enum.IsDefined(source.Route) ||
            !Enum.IsDefined(source.Sex) ||
            !float.IsFinite(source.Weight) ||
            source.Weight is < 0 or > 100 ||
            string.IsNullOrWhiteSpace(source.Race) ||
            source.Assets.IsDefaultOrEmpty ||
            source.Assets.Any(item => item is null) ||
            source.Morphs.IsDefault ||
            source.Morphs.Any(item => item is null) ||
            source.Assets.Count(item =>
                item.Role == NpcVisualAssetRole.FaceGeom) != 1)
            throw new InvalidDataException(
                "The preview source graph is incomplete or invalid.");
        RequireNoErrorDiagnostics(source.Diagnostics, "preview source");
        foreach (NpcVisualAsset asset in source.Assets)
        {
            if (!Enum.IsDefined(asset.Role) ||
                asset.Bytes <= 0 ||
                asset.Bytes > MaximumPackageFileBytes ||
                string.IsNullOrWhiteSpace(asset.Provider) ||
                asset.Materials.IsDefault ||
                asset.Materials.Any(item =>
                    item is null ||
                    item.TextureSlots.IsDefault ||
                    item.TextureSlots.Any(slot => slot is null)))
                throw new InvalidDataException(
                    "The preview source contains an invalid asset binding.");
            lease.OpenHashed(
                asset.MaterializedPath,
                asset.Bytes,
                WorkflowHash(asset.Sha256),
                MaximumPackageFileBytes,
                $"preview source asset '{asset.AssetPath.Value}'",
                cancellationToken);
            bool hasAnyLowWeight =
                asset.LowWeightAssetPath is not null ||
                asset.LowWeightSha256 is not null ||
                asset.LowWeightMaterializedPath is not null;
            bool hasAllLowWeight =
                asset.LowWeightAssetPath is not null &&
                asset.LowWeightSha256 is not null &&
                asset.LowWeightMaterializedPath is not null;
            if (hasAnyLowWeight != hasAllLowWeight)
                throw new InvalidDataException(
                    "A low-weight preview asset binding is incomplete.");
            if (hasAllLowWeight)
                lease.OpenHashed(
                    asset.LowWeightMaterializedPath!.Value,
                    expectedLength: null,
                    WorkflowHash(asset.LowWeightSha256!.Value),
                    MaximumPackageFileBytes,
                    $"low-weight preview asset '{asset.LowWeightAssetPath!.Value.Value}'",
                    cancellationToken);
            foreach (NpcVisualTextureSlot slot in asset.Materials
                         .SelectMany(material => material.TextureSlots))
                lease.OpenHashed(
                    slot.MaterializedPath,
                    expectedLength: null,
                    WorkflowHash(slot.Sha256),
                    MaximumPackageFileBytes,
                    $"preview texture '{slot.AssetPath.Value}'",
                    cancellationToken);
        }
    }

    private static void ValidateRenderEvidence(
        NpcVisualSourceGraph source,
        NpcVisualPreviewRenderEvidence evidence,
        ReviewAdmissionLease lease,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (string.IsNullOrWhiteSpace(evidence.BlenderVersion) ||
            string.IsNullOrWhiteSpace(evidence.RenderEngine) ||
            evidence.FaceGeomImportCount != 1 ||
            !evidence.FaceCameraUsedAuthoritativeGeometry ||
            evidence.ArmatureCount < 0 ||
            evidence.FallbackMaterialCount < 0 ||
            evidence.Skeletons.IsDefault ||
            evidence.Meshes.IsDefaultOrEmpty ||
            evidence.Meshes.Any(item => item is null) ||
            evidence.Materials.IsDefaultOrEmpty ||
            evidence.Materials.Any(item => item is null) ||
            evidence.MaterialApplicationCounts is null ||
            evidence.MaterialApplicationCounts.Count == 0 ||
            evidence.RoleMaskPixelCounts is null ||
            !evidence.RoleMaskPixelCounts.TryGetValue(
                "face-front:FaceGeom",
                out long faceGeomPixels) ||
            faceGeomPixels <= 0 ||
            evidence.RoleMeanLuminance is null)
            throw new InvalidDataException(
                "The preview render evidence is incomplete or invalid.");
        NpcVisualAsset sourceFaceGeom = source.Assets.Single(item =>
            item.Role == NpcVisualAssetRole.FaceGeom);
        if (!evidence.Meshes.Any(item =>
                item.Role == NpcVisualAssetRole.FaceGeom &&
                string.Equals(
                    item.AssetPath.Value,
                    sourceFaceGeom.AssetPath.Value,
                    StringComparison.OrdinalIgnoreCase)) ||
            evidence.Meshes.Any(item =>
                string.IsNullOrWhiteSpace(item.ObjectName) ||
                item.VertexCount <= 0 ||
                item.Materials.IsDefault ||
                item.WorldTransform.Length != 16) ||
            evidence.Materials.Any(item =>
                string.IsNullOrWhiteSpace(item.ObjectName) ||
                string.IsNullOrWhiteSpace(item.MaterialName) ||
                item.TextureBindings is null ||
                item.LoadedImages.IsDefault ||
                string.IsNullOrWhiteSpace(item.BlendMethod)) ||
            evidence.MaterialApplicationCounts.Any(item => item.Value < 0) ||
            evidence.RoleMaskPixelCounts.Any(item => item.Value < 0) ||
            evidence.RoleMeanLuminance.Any(item =>
                !double.IsFinite(item.Value) || item.Value < 0))
            throw new InvalidDataException(
                "The preview render evidence is not coherently bound to its source graph.");
        lease.OpenHashed(
            evidence.StatusPath,
            expectedLength: null,
            WorkflowHash(evidence.StatusSha256),
            MaximumJsonBytes,
            "preview renderer status",
            cancellationToken);
    }

    private static void ValidateVisualEvidence(
        NpcVisualPreviewVisualEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.DetectedFaceCount != 1 ||
            evidence.LandmarkCount != 478 ||
            evidence.SemanticAnchorCount != 31 ||
            !evidence.EyesNoseAndMouthBounded ||
            evidence.Diagnostics.IsDefault)
            throw new InvalidDataException(
                "The preview visual evidence does not match the admitted producer gate.");
        RequireNoErrorDiagnostics(
            evidence.Diagnostics,
            "preview visual evidence");
    }

    private static void RequireNpcIdentity(
        WorkflowNpcIdentity npc,
        PluginName outputPlugin,
        FormId targetFormId)
    {
        if (string.IsNullOrWhiteSpace(npc.Plugin) ||
            string.IsNullOrWhiteSpace(npc.LocalFormId) ||
            !FormId.TryParse(npc.LocalFormId, out FormId workflowForm) ||
            !string.Equals(
                npc.Plugin,
                outputPlugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            workflowForm != targetFormId)
            throw new InvalidDataException(
                "The workflow NPC identity does not match the package output plugin and FormID.");
    }

    private static void RequirePreviewIdentity(
        SkyrimMainWorkspaceIdentity identity,
        WorkflowNpcIdentity npc,
        PluginName outputPlugin,
        FormId targetFormId)
    {
        ArgumentNullException.ThrowIfNull(identity);
        RequireNpcIdentity(npc, outputPlugin, targetFormId);
        if (!string.Equals(
                identity.OwnerPlugin.Value,
                outputPlugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                identity.WinningProvider.Value,
                outputPlugin.Value,
                StringComparison.OrdinalIgnoreCase) ||
            identity.FormId != targetFormId ||
            !string.Equals(identity.Signature, "NPC_", StringComparison.Ordinal))
            throw new InvalidDataException(
                "The preview source identity does not match the exact package and workflow NPC.");
    }

    private static JsonDocument ParseStrictJson(byte[] bytes, string role)
    {
        JsonDocument? document = null;
        try
        {
            document = JsonDocument.Parse(
                bytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 64
                });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    $"The {role} root must be an object.");
            EnsureNoDuplicateProperties(document.RootElement, "$", role);
            return document;
        }
        catch (JsonException exception)
        {
            document?.Dispose();
            throw new InvalidDataException(
                $"The {role} is not strict JSON.",
                exception);
        }
        catch
        {
            document?.Dispose();
            throw;
        }
    }

    private static void EnsureNoDuplicateProperties(
        JsonElement element,
        string path,
        string role)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"The {role} repeats '{path}.{property.Name}'.");
                EnsureNoDuplicateProperties(
                    property.Value,
                    path + "." + property.Name,
                    role);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                EnsureNoDuplicateProperties(
                    item,
                    $"{path}[{index++}]",
                    role);
        }
    }

    private static void RequireExactProperties(
        JsonElement element,
        ImmutableHashSet<string> expected,
        string role)
    {
        if (element.ValueKind != JsonValueKind.Object ||
            !element.EnumerateObject().Select(item => item.Name)
                .ToImmutableHashSet(StringComparer.Ordinal)
                .SetEquals(expected))
            throw new InvalidDataException(
                $"The {role} does not have its exact property set.");
    }

    private static JsonElement RequireArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"'{name}' must be an array.");
        return value;
    }

    private static string RequireString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException(
                $"'{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static void RequireNumber(
        JsonElement root,
        string name,
        int expected)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            !value.TryGetInt32(out int observed) ||
            observed != expected)
            throw new InvalidDataException($"'{name}' must equal {expected}.");
    }

    private static long RequireInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement value) ||
            !value.TryGetInt64(out long observed))
            throw new InvalidDataException($"'{name}' must be an integer.");
        return observed;
    }

    private static string RequireHash(JsonElement root, string name) =>
        NormalizeHash(RequireString(root, name), name);

    private static string NormalizeHash(string value, string role)
    {
        if (value.Length != 64 || value.Any(character =>
                !((character >= '0' && character <= '9') ||
                  (character >= 'A' && character <= 'F'))))
            throw new InvalidDataException(
                $"The {role} must be one uppercase SHA-256.");
        return value;
    }

    private static void RequireNoErrorDiagnostics(
        ImmutableArray<Diagnostic> diagnostics,
        string role)
    {
        if (diagnostics.IsDefault || diagnostics.Any(item =>
                item is null || item.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(
                $"The {role} contains invalid or error diagnostics.");
    }

    private static void RequirePng(
        ReadOnlySpan<byte> bytes,
        string role,
        int? expectedWidth = null,
        int? expectedHeight = null)
    {
        ReadOnlySpan<byte> signature =
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (bytes.Length < 24 ||
            !bytes[..signature.Length].SequenceEqual(signature) ||
            !bytes.Slice(12, 4).SequenceEqual("IHDR"u8))
            throw new InvalidDataException($"The {role} is not a PNG image.");
        uint width = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(16, 4));
        uint height = BinaryPrimitives.ReadUInt32BigEndian(bytes.Slice(20, 4));
        if (width is 0 or > 8192 ||
            height is 0 or > 8192 ||
            (expectedWidth is not null && width != expectedWidth.Value) ||
            (expectedHeight is not null && height != expectedHeight.Value))
            throw new InvalidDataException(
                $"The {role} dimensions are outside the admitted bounds or differ from its manifest.");
    }

    private static ImmutableHashSet<string> Set(params string[] values) =>
        values.ToImmutableHashSet(StringComparer.Ordinal);

    private static bool IsExpected(Exception exception) =>
        exception is ArgumentException or InvalidDataException or IOException or
            UnauthorizedAccessException or InvalidOperationException or
            FormatException;

    private static ProtocolDiagnostic Failure(Exception exception)
    {
        string code = exception switch
        {
            AgentWorkflowCodecException codec => codec.Code,
            AgentReviewReceiptException receipt => receipt.Code,
            UnauthorizedAccessException => "desktop-review-path-refused",
            _ => "desktop-review-invalid"
        };
        return new ProtocolDiagnostic(
            code,
            DiagnosticSeverity.Error,
            exception.Message,
            DiagnosticClass.Validation,
            new DiagnosticRecovery(
                RecoveryAction.CorrectInput,
                null,
                null,
                "Supply the unchanged exact Review-phase package, preview, and transitive evidence.",
                false));
    }

    private static DesktopWorkflowReviewReceiptResult Success(
        AgentReviewReceiptDocument document) => new(
            true,
            document.Receipt.Outcome,
            document.Path,
            document.Sha256,
            document.Artifact,
            []);

    private static DesktopWorkflowReviewReceiptResult ReceiptFailure(
        Exception exception) => new(
            false,
            null,
            null,
            null,
            null,
            [Failure(exception)]);

    private sealed record PackageReview(
        PluginName OutputPlugin,
        FormId TargetFormId,
        DesktopWorkflowProposalPresentation Presentation);

    private sealed class SingleValueObjectConverter<T>(
        Func<string, T> create,
        Func<T, string> project) : JsonConverter<T>
        where T : struct
    {
        public override T Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.PropertyName ||
                !reader.ValueTextEquals("value") ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.String ||
                reader.GetString() is not { } value ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException(
                    $"{typeof(T).Name} must be an exact string value object.");
            return create(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            T value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("value", project(value));
            writer.WriteEndObject();
        }
    }

    private static bool HasExactDesktopReviewAuthority(
        ImmutableArray<WorkflowAuthorityEvidence> authority)
    {
        var expected = ImmutableDictionary.CreateRange(
            new[]
            {
                KeyValuePair.Create(AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Required),
                KeyValuePair.Create(AgentAuthorityKind.SourceProviderIdentity,
                    AgentAuthorityState.Required),
                KeyValuePair.Create(AgentAuthorityKind.DeterministicMaterialization,
                    AgentAuthorityState.Established),
                KeyValuePair.Create(AgentAuthorityKind.IndependentStaticVerification,
                    AgentAuthorityState.Required),
                KeyValuePair.Create(AgentAuthorityKind.OffEnginePreview,
                    AgentAuthorityState.Established),
                KeyValuePair.Create(AgentAuthorityKind.HumanVisualAcceptance,
                    AgentAuthorityState.Required),
                KeyValuePair.Create(AgentAuthorityKind.GameRuntimeVerification,
                    AgentAuthorityState.Required),
                KeyValuePair.Create(AgentAuthorityKind.PromotionApproval,
                    AgentAuthorityState.Required)
            });
        return authority.Length == expected.Count &&
            authority.All(item =>
                expected.TryGetValue(item.Kind, out AgentAuthorityState state) &&
                item.State == state);
    }

    private static string WorkflowHash(Sha256Hash value) =>
        value.Value.ToUpperInvariant();

    private sealed class FormIdObjectConverter : JsonConverter<FormId>
    {
        public override FormId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.PropertyName ||
                !reader.ValueTextEquals("value") ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.Number ||
                !reader.TryGetUInt32(out uint value) ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException(
                    "FormId must be an exact unsigned integer value object.");
            return new FormId(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            FormId value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", value.Value);
            writer.WriteEndObject();
        }
    }

    private sealed record ReviewAdmission(
        AgentWorkflowBundleDocument Bundle,
        WorkflowArtifactBinding PackageProposal,
        WorkflowArtifactBinding Preview,
        DesktopWorkflowReviewSnapshot Snapshot,
        ReviewAdmissionLease Lease) : IDisposable
    {
        public void Dispose() => Lease.Dispose();
    }

    private sealed class ReviewAdmissionLease(
        FaceGeomHairRegionsPinnedFileSystem fileSystem) : IDisposable
    {
        private readonly Dictionary<string, RetainedFile> files = new(
            StringComparer.OrdinalIgnoreCase);
        private long retainedBytes;
        private bool disposed;

        public byte[] OpenBytes(
            WorkspacePath path,
            long expectedLength,
            string expectedSha256,
            long maximumBytes,
            string role,
            CancellationToken cancellationToken) => Open(
                path,
                expectedLength,
                expectedSha256,
                maximumBytes,
                role,
                readBytes: true,
                cancellationToken) ??
            throw new InvalidOperationException(
                $"The retained {role} bytes were unavailable.");

        public byte[] OpenBytesBelow(
            WorkspacePath path,
            string expectedSha256,
            WorkspacePath root,
            long maximumBytes,
            string role,
            CancellationToken cancellationToken)
        {
            if (!path.IsUnder(root) || path == root)
                throw new UnauthorizedAccessException(
                    $"The {role} must remain below its retained root.");
            return Open(
                    path,
                    expectedLength: null,
                    expectedSha256,
                    maximumBytes,
                    role,
                    readBytes: true,
                    cancellationToken) ??
                throw new InvalidOperationException(
                    $"The retained {role} bytes were unavailable.");
        }

        public void OpenHashed(
            WorkspacePath path,
            long? expectedLength,
            string expectedSha256,
            long maximumBytes,
            string role,
            CancellationToken cancellationToken) => _ = Open(
                path,
                expectedLength,
                expectedSha256,
                maximumBytes,
                role,
                readBytes: false,
                cancellationToken);

        public void Revalidate(CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            foreach (RetainedFile retained in files.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (retained.File.Length != retained.Length ||
                    !string.Equals(
                        retained.File.ComputeSha256(),
                        retained.Sha256,
                        StringComparison.Ordinal) ||
                    retained.File.Length != retained.Length)
                    throw new InvalidDataException(
                        $"The retained {retained.Role} changed during review admission.");
            }
        }

        private byte[]? Open(
            WorkspacePath path,
            long? expectedLength,
            string expectedSha256,
            long maximumBytes,
            string role,
            bool readBytes,
            CancellationToken cancellationToken)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            NormalizeHash(expectedSha256, $"{role} SHA-256");
            if (files.TryGetValue(path.Value, out RetainedFile? existing))
            {
                if ((expectedLength is not null &&
                     existing.Length != expectedLength.Value) ||
                    !string.Equals(
                        existing.Sha256,
                        expectedSha256,
                        StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"The retained {role} repeats one path with different authority.");
                if (readBytes && existing.Bytes is null)
                    existing.Bytes = existing.File.ReadExact(maximumBytes);
                return existing.Bytes;
            }
            if (files.Count >= MaximumRetainedFiles)
                throw new InvalidDataException(
                    "The Desktop review retained-file bound was exceeded.");
            FaceGeomHairRegionsPinnedReadFile? source = null;
            try
            {
                source = fileSystem.OpenRead(path, role);
                long length = source.Length;
                if (length <= 0 ||
                    length > maximumBytes ||
                    (expectedLength is not null &&
                     length != expectedLength.Value) ||
                    retainedBytes > MaximumAdmissionBytes - length)
                    throw new InvalidDataException(
                        $"The retained {role} length exceeds its admission bound.");
                byte[]? bytes = readBytes
                    ? source.ReadExact(maximumBytes)
                    : null;
                string observed = readBytes
                    ? Convert.ToHexString(SHA256.HashData(bytes!))
                    : source.ComputeSha256();
                if (!string.Equals(
                        observed,
                        expectedSha256,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        source.ComputeSha256(),
                        expectedSha256,
                        StringComparison.Ordinal) ||
                    source.Length != length)
                    throw new InvalidDataException(
                        $"The retained {role} hash or length changed.");
                var retained = new RetainedFile(
                    source,
                    length,
                    expectedSha256,
                    role,
                    bytes);
                files.Add(path.Value, retained);
                retainedBytes += length;
                source = null;
                return bytes;
            }
            finally
            {
                source?.Dispose();
            }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            foreach (RetainedFile retained in files.Values.Reverse())
                retained.File.Dispose();
            files.Clear();
            disposed = true;
        }

        private sealed class RetainedFile(
            FaceGeomHairRegionsPinnedReadFile file,
            long length,
            string sha256,
            string role,
            byte[]? bytes)
        {
            public FaceGeomHairRegionsPinnedReadFile File { get; } = file;

            public long Length { get; } = length;

            public string Sha256 { get; } = sha256;

            public string Role { get; } = role;

            public byte[]? Bytes { get; set; } = bytes;
        }
    }
}
