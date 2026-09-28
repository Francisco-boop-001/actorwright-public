using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed class ActorAssemblyPreflightService(
    IActorAssemblyContractLoader contractLoader,
    IActorAssemblyEvidenceLoader evidenceLoader,
    IPackageVerifyService packageVerifyService,
    IActorAssemblyIdentityReader identityReader,
    IBodySlideSliderPresetInspectionService presetInspectionService,
    IBodySlideTriInspectionService triInspectionService,
    WorkspacePath? labRoot = null)
    : IActorAssemblyPreflightService
{
    private readonly WorkspacePath _labRoot = labRoot ?? ActorwrightWorkspace.ResolveRoot();

    public async ValueTask<ActorAssemblyPreflightExecutionResult> PreflightAsync(
        ActorAssemblyPreflightRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var contract = await contractLoader.LoadAsync(request, cancellationToken);
        if (contract.Document is null)
            return Error(false, contract.SecurityRefusal, contract.Diagnostics);
        if (contract.SecurityRefusal)
            return Error(true, true, contract.Diagnostics);

        var package = await packageVerifyService.VerifyAsync(new PackageVerifyRequest(contract.Document.PackageManifest.Path), cancellationToken);
        if (HasSecurityDiagnostic(package.Diagnostics)) return Error(true, true, package.Diagnostics);
        var packageArtifact = package.Artifact;
        if (packageArtifact is not null && packageArtifact.ManifestSha256 != contract.Document.PackageManifest.Sha256)
            packageArtifact = packageArtifact with { NoUndeclaredFiles = false };
        var packageOutcome = packageArtifact is not null && package.Verified &&
            packageArtifact.ManifestSha256 == contract.Document.PackageManifest.Sha256 &&
            string.Equals(packageArtifact.OutputPlugin, contract.Document.BaseNpc.Plugin.Value, StringComparison.OrdinalIgnoreCase) &&
            packageArtifact.TargetFormId == contract.Document.BaseNpc.FormId
            ? ActorAssemblyOutcome.Pass : ActorAssemblyOutcome.Blocked;
        if (packageArtifact is null)
            return ResultForFailure(request, contract.Document, packageOutcome, package.Diagnostics);

        var pluginFiles = packageArtifact.Files.Where(file => string.Equals(file.Kind, "plugin", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (pluginFiles.Length != 1)
            return ResultForFailure(request, contract.Document, ActorAssemblyOutcome.Blocked,
                package.Diagnostics.Add(new Diagnostic("actor-assembly-plugin-entry-count", DiagnosticSeverity.Error, "The verified package manifest must contain exactly one plugin file.")));
        var pluginFile = pluginFiles[0];
        if (!string.Equals(Path.GetFileName(pluginFile.RelativePath.Value), packageArtifact.OutputPlugin, StringComparison.OrdinalIgnoreCase))
            return ResultForFailure(request, contract.Document, ActorAssemblyOutcome.Blocked,
                package.Diagnostics.Add(new Diagnostic("actor-assembly-plugin-entry-mismatch", DiagnosticSeverity.Error, "The plugin file basename does not match the manifest outputPlugin.")));
        var pluginPath = new WorkspacePath(Path.Combine(Path.GetDirectoryName(packageArtifact.ManifestPath.Value)!, pluginFile.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
        var identity = await identityReader.ReadAsync(new ActorAssemblyIdentityReadRequest(pluginPath, contract.Document.BaseNpc, contract.Document.Placement), cancellationToken);

        var owner = await ResolveOwnerAsync(contract.Document, cancellationToken);
        if (owner.SecurityRefusal) return Error(true, true, owner.Diagnostics);
        var inventoryLoad = contract.Document.OutfitScope.Status == ActorAssemblyOutfitScopeStatus.Complete && contract.Document.OutfitScope.Inventory is not null
            ? await evidenceLoader.LoadOutfitInventoryAsync(contract.Document.OutfitScope.Inventory, cancellationToken)
            : new ActorAssemblyDocumentLoadResult<ActorAssemblyOutfitInventory>(
                contract.Document.OutfitScope.Status == ActorAssemblyOutfitScopeStatus.NotApplicable ? ActorAssemblyDocumentDisposition.Loaded : ActorAssemblyDocumentDisposition.Missing,
                null, null, false, ImmutableArray<Diagnostic>.Empty);
        if (inventoryLoad.SecurityRefusal) return Error(true, true, inventoryLoad.Diagnostics);
        var outputResolution = await ResolveOutputsAsync(contract.Document, packageArtifact, inventoryLoad, cancellationToken);
        if (outputResolution.SecurityRefusal) return Error(true, true, outputResolution.Diagnostics);
        var policyLoad = contract.Document.ReviewedCompositePolicy?.Status == ActorAssemblyEvidenceStatus.Available && contract.Document.ReviewedCompositePolicy.File is not null
            ? await evidenceLoader.LoadCompositePolicyAsync(contract.Document.ReviewedCompositePolicy.File, cancellationToken)
            : new ActorAssemblyDocumentLoadResult<ActorAssemblyReviewedCompositePolicy>(ActorAssemblyDocumentDisposition.Missing, null, null, false, ImmutableArray<Diagnostic>.Empty);
        if (policyLoad.SecurityRefusal) return Error(true, true, policyLoad.Diagnostics);
        var evaluation = ActorAssemblyProvenanceEvaluator.Evaluate(new ActorAssemblyProvenanceEvaluationRequest(
            contract.Document, packageArtifact, owner.Resolved, inventoryLoad.Document, inventoryLoad.Disposition,
            inventoryLoad.ActualSha256, outputResolution.Outputs, policyLoad.Document, policyLoad.Disposition));

        var checks = ImmutableArray.CreateBuilder<ActorAssemblyCheck>();
        checks.Add(Check("PACKAGE-INTEGRITY", packageOutcome, packageOutcome == ActorAssemblyOutcome.Pass ? "The package manifest and output identity are verified." : "Package verification or target identity failed."));
        checks.Add(Check("BASE-NPC-IDENTITY", identity.BaseNpc.Outcome, "Typed and raw base NPC identity evidence."));
        checks.Add(Check("PLACED-REFERENCE-IDENTITY", identity.PlacedReference?.Outcome ?? ActorAssemblyOutcome.NotApplicable, identity.PlacedReference is null ? "Placement mode has no persistent reference." : "Typed and raw placed-reference identity evidence."));
        checks.AddRange(evaluation.Checks);
        var outcome = Aggregate(checks);
        var artifact = new ActorAssemblyPreflightArtifact(1, "actor-assembly-preflight-result", outcome,
            request.ContractSha256, packageArtifact.ManifestSha256, identity.BaseNpc, identity.PlacedReference,
            identity.PlacedReference is null ? "baseNpc" : "placedReference", checks.ToImmutable());
        return new(true, false, artifact, null);
    }

    private async ValueTask<ResolvedOwner> ResolveOwnerAsync(ActorAssemblyContract contract, CancellationToken cancellationToken)
    {
        var reference = contract.BodyMorph.Evidence;
        if (reference.File is null)
        {
            var ownerDisposition = contract.BodyMorph.Owner is ActorAssemblyMorphOwner.None or ActorAssemblyMorphOwner.BakedBodySlide
                ? ActorAssemblyDocumentDisposition.Loaded
                : ActorAssemblyDocumentDisposition.Missing;
            return new(new ActorAssemblyResolvedOwnerEvidence(contract.BodyMorph.Owner, ownerDisposition, null, null, null, null), false, ImmutableArray<Diagnostic>.Empty);
        }
        var loaded = await evidenceLoader.LoadOwnerEvidenceAsync(reference.File, cancellationToken);
        if (loaded.Document is null) return new(new ActorAssemblyResolvedOwnerEvidence(contract.BodyMorph.Owner, loaded.Disposition, null, null, null, loaded.ActualSha256), loaded.SecurityRefusal, loaded.Diagnostics);
        var nested = await VerifyOwnerFilesAsync(loaded.Document, cancellationToken);
        if (nested.SecurityRefusal)
            return new(new ActorAssemblyResolvedOwnerEvidence(contract.BodyMorph.Owner, nested.Disposition, loaded.Document, null, null, loaded.ActualSha256), true, nested.Diagnostics);
        BodySlideSliderPresetDocument? preset = null;
        ActorAssemblyRuntimeChannelReview? review = null;
        if (loaded.Document is ActorAssemblyOBodyEvidence obody)
        {
            var result = await presetInspectionService.InspectAsync(new BodySlideSliderPresetInspectionRequest(GameEdition.SkyrimSpecialEdition, obody.Preset.Path), cancellationToken);
            if (result.IsValid && result.SourceSha256 is not null && result.SourceSha256.Value == obody.Preset.Sha256 &&
                result.PresetName is not null && result.SliderSet is not null)
                preset = new BodySlideSliderPresetDocument(result.SourceSha256.Value, result.PresetName ?? string.Empty, result.SliderSet ?? string.Empty, result.Groups, result.Sliders);
            else if (result.SourceSha256 is not null && result.SourceSha256.Value != obody.Preset.Sha256)
                nested = new ActorAssemblyBoundFileValidationResult(ActorAssemblyDocumentDisposition.HashMismatch, false,
                    ImmutableArray.Create(new Diagnostic("actor-assembly-preset-hash-mismatch", DiagnosticSeverity.Error, "The parsed OBody preset hash does not match its binding.")));
            else if (!result.IsValid)
                nested = new ActorAssemblyBoundFileValidationResult(ActorAssemblyDocumentDisposition.Invalid, false, result.Diagnostics);
        }
        else if (loaded.Document is ActorAssemblyRuntimeScriptEvidence runtime)
        {
            var reviewLoad = await evidenceLoader.LoadRuntimeReviewAsync(runtime.SemanticReview, cancellationToken);
            if (reviewLoad.SecurityRefusal)
                return new(new ActorAssemblyResolvedOwnerEvidence(contract.BodyMorph.Owner, ActorAssemblyDocumentDisposition.SecurityRefused, loaded.Document, null, null, loaded.ActualSha256), true, reviewLoad.Diagnostics);
            review = reviewLoad.Document;
            if (review is not null && (review.ScriptSha256 != runtime.Pex.Sha256 ||
                !review.Channels.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(runtime.Channels.Select(channel => channel.Name))))
                nested = new ActorAssemblyBoundFileValidationResult(ActorAssemblyDocumentDisposition.HashMismatch, false,
                    ImmutableArray.Create(new Diagnostic("actor-assembly-runtime-review-mismatch", DiagnosticSeverity.Error, "Runtime review PEX hash or channel set does not match runtime evidence.")));
        }
        var disposition = nested.Disposition != ActorAssemblyDocumentDisposition.Loaded ? nested.Disposition : loaded.Disposition;
        return new(new ActorAssemblyResolvedOwnerEvidence(contract.BodyMorph.Owner, disposition, loaded.Document, preset, review, loaded.ActualSha256), false, nested.Diagnostics);
    }

    private async ValueTask<ResolvedOutputs> ResolveOutputsAsync(
        ActorAssemblyContract contract, PackageVerificationArtifact package, ActorAssemblyDocumentLoadResult<ActorAssemblyOutfitInventory> inventory, CancellationToken cancellationToken)
    {
        if (inventory.Document is null) return new(ImmutableArray<ActorAssemblyResolvedOutputEvidence>.Empty, false, ImmutableArray<Diagnostic>.Empty);
        var outputs = ImmutableArray.CreateBuilder<ActorAssemblyResolvedOutputEvidence>();
        foreach (var entry in inventory.Document.Entries.OfType<ActorAssemblyMorphableOutfitEntry>())
        {
            if (entry.Receipt.File is null) { outputs.Add(new(entry, ActorAssemblyDocumentDisposition.Missing, null, null, null, null)); continue; }
            var receiptLoad = await evidenceLoader.LoadReceiptAsync(entry.Receipt.File, cancellationToken);
            if (receiptLoad.SecurityRefusal) return new(outputs.ToImmutable(), true, receiptLoad.Diagnostics);
            BodySlideSliderPresetDocument? preset = null;
            BodySlideTriCatalog? tri = null;
            var receiptDisposition = receiptLoad.Disposition;
            if (receiptLoad.Document is not null)
            {
                var nested = await VerifyReceiptFilesAsync(receiptLoad.Document, cancellationToken);
                if (nested.SecurityRefusal) return new(outputs.ToImmutable(), true, nested.Diagnostics);
                if (nested.Disposition != ActorAssemblyDocumentDisposition.Loaded) receiptDisposition = nested.Disposition;
                var presetResult = await presetInspectionService.InspectAsync(new BodySlideSliderPresetInspectionRequest(GameEdition.SkyrimSpecialEdition, receiptLoad.Document.SourcePreset.File.Path), cancellationToken);
                if (presetResult.IsValid && presetResult.SourceSha256 is not null && presetResult.SourceSha256.Value == receiptLoad.Document.SourcePreset.File.Sha256)
                    preset = new BodySlideSliderPresetDocument(presetResult.SourceSha256.Value, presetResult.PresetName ?? string.Empty, presetResult.SliderSet ?? string.Empty, presetResult.Groups, presetResult.Sliders);
                else if (presetResult.SourceSha256 is not null && presetResult.SourceSha256.Value != receiptLoad.Document.SourcePreset.File.Sha256)
                    receiptDisposition = ActorAssemblyDocumentDisposition.HashMismatch;
                else if (!presetResult.IsValid)
                    receiptDisposition = ActorAssemblyDocumentDisposition.Invalid;
                if (entry.TriPackagePath is not null)
                {
                    var triPath = new WorkspacePath(Path.Combine(Path.GetDirectoryName(package.ManifestPath.Value)!, entry.TriPackagePath.Value.Value.Replace('/', Path.DirectorySeparatorChar)));
                    var triResult = await triInspectionService.InspectAsync(new BodySlideTriInspectionRequest(GameEdition.SkyrimSpecialEdition, triPath), cancellationToken);
                    tri = triResult.Catalog;
                }
            }
            outputs.Add(new(entry, receiptDisposition, receiptLoad.Document, preset, tri, receiptLoad.ActualSha256));
        }
        return new(outputs.ToImmutable(), false, ImmutableArray<Diagnostic>.Empty);
    }

    private static ActorAssemblyPreflightExecutionResult ResultForFailure(ActorAssemblyPreflightRequest request, ActorAssemblyContract contract, ActorAssemblyOutcome outcome, ImmutableArray<Diagnostic> diagnostics)
    {
        var unknown = new ActorAssemblyBaseNpcEvidence(contract.BaseNpc.Plugin, contract.BaseNpc.FormId,
            new ActorAssemblyRecordObservation(ActorAssemblyObservationStatus.Unknown, null, null, "Identity was not reached."),
            new ActorAssemblyRecordObservation(ActorAssemblyObservationStatus.Unknown, null, null, "Identity was not reached."), outcome);
        var checks = ImmutableArray.Create(Check("PACKAGE-INTEGRITY", outcome, "Package verification or target identity failed."));
        return new(true, false, new ActorAssemblyPreflightArtifact(1, "actor-assembly-preflight-result", outcome, request.ContractSha256, contract.PackageManifest.Sha256, unknown, null, "baseNpc", checks), null);
    }

    private static ActorAssemblyPreflightExecutionResult Error(bool admitted, bool security, ImmutableArray<Diagnostic> diagnostics) =>
        new(admitted, security, null, new ActorAssemblyPreflightErrorArtifact(1, "actor-assembly-preflight-error", admitted, diagnostics));

    private static ActorAssemblyCheck Check(string code, ActorAssemblyOutcome outcome, string message) => new(code, outcome, message, ImmutableArray<ActorAssemblyCheckEvidence>.Empty);
    private static ActorAssemblyOutcome Aggregate(IEnumerable<ActorAssemblyCheck> checks) => checks.Any(check => check.Outcome == ActorAssemblyOutcome.Blocked) ? ActorAssemblyOutcome.Blocked : checks.Any(check => check.Outcome == ActorAssemblyOutcome.Unknown) ? ActorAssemblyOutcome.Unknown : checks.Any(check => check.Outcome == ActorAssemblyOutcome.Pass) ? ActorAssemblyOutcome.Pass : ActorAssemblyOutcome.NotApplicable;
    private static bool HasSecurityDiagnostic(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            ProtocolDiagnosticClassifier.ClassifyLegacy(item).Class ==
            DiagnosticClass.Security);

    private async ValueTask<ActorAssemblyBoundFileValidationResult> VerifyOwnerFilesAsync(ActorAssemblyOwnerEvidence document, CancellationToken cancellationToken)
    {
        var files = document switch
        {
            ActorAssemblyOBodyEvidence obody => new[] { obody.Preset }.Concat(obody.Assignments),
            ActorAssemblyBodyGenEvidence bodygen => new[] { bodygen.Assignment, bodygen.Templates, bodygen.Morphs },
            ActorAssemblyRuntimeScriptEvidence runtime => new[] { runtime.Plugin, runtime.Pex, runtime.Vmad, runtime.SemanticReview },
            ActorAssemblyCompositeComponentsEvidence composite => composite.Components.Where(component => component.Evidence is not null).Select(component => component.Evidence!),
            _ => Enumerable.Empty<ActorAssemblyBoundFile>()
        };
        return await VerifyBoundFilesAsync(files, cancellationToken);
    }

    private async ValueTask<ActorAssemblyBoundFileValidationResult> VerifyReceiptFilesAsync(ActorAssemblyOutfitBuildReceipt receipt, CancellationToken cancellationToken) =>
        await VerifyBoundFilesAsync(new[] { receipt.Tool.Executable, receipt.SourcePreset.File }, cancellationToken);

    private async ValueTask<ActorAssemblyBoundFileValidationResult> VerifyBoundFilesAsync(IEnumerable<ActorAssemblyBoundFile> files, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var disposition = ActorAssemblyDocumentDisposition.Loaded;
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!file.Path.IsUnder(_labRoot))
            {
                diagnostics.Add(new Diagnostic("actor-assembly-bound-file-outside-lab", DiagnosticSeverity.Error, $"Bound evidence file '{file.Path.Value}' is outside the K-only lab root."));
                return new(ActorAssemblyDocumentDisposition.SecurityRefused, true, diagnostics.ToImmutable());
            }
            var current = Path.GetFullPath(file.Path.Value);
            while (!string.IsNullOrEmpty(current))
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("actor-assembly-bound-file-reparse", DiagnosticSeverity.Error, $"Bound evidence file '{file.Path.Value}' traverses a reparse point."));
                    return new(ActorAssemblyDocumentDisposition.SecurityRefused, true, diagnostics.ToImmutable());
                }
                var parent = Directory.GetParent(current)?.FullName;
                if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
                current = parent;
            }
            if (!File.Exists(file.Path.Value))
            {
                disposition = PreferDisposition(disposition, ActorAssemblyDocumentDisposition.Missing);
                diagnostics.Add(new Diagnostic("actor-assembly-bound-file-missing", DiagnosticSeverity.Error, $"Bound evidence file '{file.Path.Value}' is missing."));
                continue;
            }
            try
            {
                var bytes = await File.ReadAllBytesAsync(file.Path.Value, cancellationToken);
                var actual = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
                if (actual != file.Sha256)
                {
                    disposition = PreferDisposition(disposition, ActorAssemblyDocumentDisposition.HashMismatch);
                    diagnostics.Add(new Diagnostic("actor-assembly-bound-file-hash-mismatch", DiagnosticSeverity.Error, $"Bound evidence file '{file.Path.Value}' has hash '{actual.Value.ToUpperInvariant()}', not the declared binding."));
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (UnauthorizedAccessException exception)
            {
                disposition = PreferDisposition(disposition, ActorAssemblyDocumentDisposition.Unreadable);
                diagnostics.Add(new Diagnostic("actor-assembly-bound-file-unreadable", DiagnosticSeverity.Error, exception.Message));
            }
            catch (IOException exception)
            {
                disposition = PreferDisposition(disposition, ActorAssemblyDocumentDisposition.Unreadable);
                diagnostics.Add(new Diagnostic("actor-assembly-bound-file-unreadable", DiagnosticSeverity.Error, exception.Message));
            }
        }
        return new(disposition, false, diagnostics.ToImmutable());
    }

    private static ActorAssemblyDocumentDisposition PreferDisposition(ActorAssemblyDocumentDisposition current, ActorAssemblyDocumentDisposition candidate)
    {
        static int Rank(ActorAssemblyDocumentDisposition value) => value switch
        {
            ActorAssemblyDocumentDisposition.HashMismatch => 4,
            ActorAssemblyDocumentDisposition.Invalid => 4,
            ActorAssemblyDocumentDisposition.Unreadable => 3,
            ActorAssemblyDocumentDisposition.Missing => 2,
            _ => 1
        };
        return Rank(candidate) > Rank(current) ? candidate : current;
    }

    private sealed record ActorAssemblyBoundFileValidationResult(
        ActorAssemblyDocumentDisposition Disposition,
        bool SecurityRefusal,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ResolvedOwner(
        ActorAssemblyResolvedOwnerEvidence Resolved,
        bool SecurityRefusal,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ResolvedOutputs(
        ImmutableArray<ActorAssemblyResolvedOutputEvidence> Outputs,
        bool SecurityRefusal,
        ImmutableArray<Diagnostic> Diagnostics);
}
