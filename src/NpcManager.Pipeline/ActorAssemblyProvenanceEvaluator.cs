using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

internal sealed record ActorAssemblyResolvedOwnerEvidence(
    ActorAssemblyMorphOwner Owner,
    ActorAssemblyDocumentDisposition Disposition,
    ActorAssemblyOwnerEvidence? Document,
    BodySlideSliderPresetDocument? OBodyPreset,
    ActorAssemblyRuntimeChannelReview? RuntimeReview,
    Sha256Hash? EvidenceSha256);

internal sealed record ActorAssemblyResolvedOutputEvidence(
    ActorAssemblyMorphableOutfitEntry InventoryEntry,
    ActorAssemblyDocumentDisposition ReceiptDisposition,
    ActorAssemblyOutfitBuildReceipt? Receipt,
    BodySlideSliderPresetDocument? ReceiptPreset,
    BodySlideTriCatalog? TriCatalog,
    Sha256Hash? ReceiptSha256);

internal sealed record ActorAssemblyProvenanceEvaluationRequest(
    ActorAssemblyContract Contract,
    PackageVerificationArtifact Package,
    ActorAssemblyResolvedOwnerEvidence Owner,
    ActorAssemblyOutfitInventory? Inventory,
    ActorAssemblyDocumentDisposition InventoryDisposition,
    Sha256Hash? InventorySha256,
    ImmutableArray<ActorAssemblyResolvedOutputEvidence> Outputs,
    ActorAssemblyReviewedCompositePolicy? CompositePolicy,
    ActorAssemblyDocumentDisposition CompositePolicyDisposition);

internal sealed record ActorAssemblyProvenanceEvaluationResult(
    ImmutableArray<ActorAssemblyCheck> Checks,
    ImmutableArray<Diagnostic> Diagnostics);

internal static class ActorAssemblyProvenanceEvaluator
{
    internal static ActorAssemblyProvenanceEvaluationResult Evaluate(ActorAssemblyProvenanceEvaluationRequest request)
    {
        var checks = ImmutableArray.CreateBuilder<ActorAssemblyCheck>();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var ownerOutcome = EvaluateOwnerEvidence(request, diagnostics);
        checks.Add(Check("MORPH-OWNER-EVIDENCE", ownerOutcome,
            ownerOutcome == ActorAssemblyOutcome.Pass ? "The declared morph-owner evidence is admitted." : "The declared morph-owner evidence is not sufficient to establish ownership."));
        var inventoryOutcome = EvaluateInventoryScope(request, diagnostics);
        checks.Add(Check("OUTFIT-SCOPE", inventoryOutcome,
            inventoryOutcome == ActorAssemblyOutcome.Pass ? "The outfit inventory is admitted." : "The outfit inventory is unavailable or invalid."));

        var provenanceOutcome = EvaluateProvenance(request, diagnostics);
        checks.Add(Check("OUTFIT-PROVENANCE", provenanceOutcome, provenanceOutcome == ActorAssemblyOutcome.Pass ? "Current outfit outputs are covered by admitted receipts." : "Current outfit provenance is incomplete or inconsistent."));
        var collisionOutcome = EvaluateDoubleOwner(request, diagnostics);
        checks.Add(Check("BODY-MORPH-DOUBLE-OWNER", collisionOutcome, collisionOutcome == ActorAssemblyOutcome.Pass ? "No output has two admitted non-neutral morph owners." : "At least one output has conflicting morph ownership or an unprovable interaction."));
        return new(checks.ToImmutable(), diagnostics.ToImmutable());
    }

    private static ActorAssemblyOutcome EvaluateOwnerEvidence(ActorAssemblyProvenanceEvaluationRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Owner.Disposition is ActorAssemblyDocumentDisposition.HashMismatch or ActorAssemblyDocumentDisposition.Invalid or ActorAssemblyDocumentDisposition.SecurityRefused)
            return ActorAssemblyOutcome.Blocked;
        if (request.Owner.Owner is ActorAssemblyMorphOwner.None or ActorAssemblyMorphOwner.BakedBodySlide)
            return request.Owner.Disposition == ActorAssemblyDocumentDisposition.Loaded ? ActorAssemblyOutcome.Pass : ActorAssemblyOutcome.Unknown;
        if (request.Owner.Disposition is ActorAssemblyDocumentDisposition.Missing or ActorAssemblyDocumentDisposition.Unreadable || request.Owner.Document is null)
            return ActorAssemblyOutcome.Unknown;
        var expectedKind = request.Owner.Owner switch
        {
            ActorAssemblyMorphOwner.OBody => "actor-assembly-obody-evidence",
            ActorAssemblyMorphOwner.BodyGen => "actor-assembly-bodygen-evidence",
            ActorAssemblyMorphOwner.RuntimeScript => "actor-assembly-runtime-script-evidence",
            ActorAssemblyMorphOwner.ReviewedComposite => "actor-assembly-composite-components",
            _ => string.Empty
        };
        if (!string.Equals(request.Owner.Document.ArtifactKind, expectedKind, StringComparison.Ordinal)) return ActorAssemblyOutcome.Blocked;
        if (request.Owner.Document.BaseNpc != request.Contract.BaseNpc) return ActorAssemblyOutcome.Blocked;
        if (request.Owner.Owner == ActorAssemblyMorphOwner.RuntimeScript && request.Owner.RuntimeReview is null) return ActorAssemblyOutcome.Blocked;
        if (request.Owner.Owner == ActorAssemblyMorphOwner.ReviewedComposite)
            return EvaluateCompositePolicy(request, diagnostics);
        return ActorAssemblyOutcome.Pass;
    }

    private static ActorAssemblyOutcome EvaluateInventoryScope(ActorAssemblyProvenanceEvaluationRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.InventoryDisposition is ActorAssemblyDocumentDisposition.HashMismatch or ActorAssemblyDocumentDisposition.Invalid or ActorAssemblyDocumentDisposition.SecurityRefused) return ActorAssemblyOutcome.Blocked;
        if (request.InventoryDisposition is ActorAssemblyDocumentDisposition.Missing or ActorAssemblyDocumentDisposition.Unreadable) return ActorAssemblyOutcome.Unknown;
        var manifestTyped = request.Package.Files.Where(IsTypedOutfitFile).Select(file => file.RelativePath.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var manifestAmbiguous = request.Package.Files.Any(file => IsAmbiguousOutfitFile(file));
        if (request.Inventory is null)
            return manifestTyped.Count == 0 && !manifestAmbiguous ? ActorAssemblyOutcome.Pass : ActorAssemblyOutcome.Unknown;
        if (request.Inventory.PackageManifestSha256 != request.Package.ManifestSha256 || request.Inventory.BaseNpc != request.Contract.BaseNpc) return ActorAssemblyOutcome.Blocked;
        var inventoryPaths = request.Inventory.Entries.SelectMany(entry => entry switch
        {
            ActorAssemblyMorphableOutfitEntry morphable => morphable.NifPackagePaths.Select(path => path.Value).Concat(morphable.TriPackagePath is null ? Enumerable.Empty<string>() : new[] { morphable.TriPackagePath.Value.Value }),
            ActorAssemblyNonMorphableOutfitEntry nonMorphable => new[] { nonMorphable.PackagePath.Value },
            _ => Enumerable.Empty<string>()
        }).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!manifestTyped.SetEquals(inventoryPaths.Where(manifestTyped.Contains))) return ActorAssemblyOutcome.Blocked;
        if (inventoryPaths.Any(path => !request.Package.Files.Any(file => string.Equals(file.RelativePath.Value, path, StringComparison.OrdinalIgnoreCase)))) return ActorAssemblyOutcome.Blocked;
        if (manifestAmbiguous) return ActorAssemblyOutcome.Unknown;
        return ActorAssemblyOutcome.Pass;
    }

    private static ActorAssemblyOutcome EvaluateProvenance(ActorAssemblyProvenanceEvaluationRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.InventoryDisposition is ActorAssemblyDocumentDisposition.HashMismatch or ActorAssemblyDocumentDisposition.Invalid or ActorAssemblyDocumentDisposition.SecurityRefused) return ActorAssemblyOutcome.Blocked;
        if (request.InventoryDisposition is ActorAssemblyDocumentDisposition.Missing or ActorAssemblyDocumentDisposition.Unreadable) return ActorAssemblyOutcome.Unknown;
        if (request.Inventory is null) return request.Package.Files.Any(IsTypedOutfitFile) || request.Package.Files.Any(IsAmbiguousOutfitFile) ? ActorAssemblyOutcome.Unknown : ActorAssemblyOutcome.Pass;
        if (request.Inventory.PackageManifestSha256 != request.Package.ManifestSha256 || request.Inventory.BaseNpc != request.Contract.BaseNpc) return ActorAssemblyOutcome.Blocked;
        if (request.Inventory.Entries.OfType<ActorAssemblyMorphableOutfitEntry>().Any(entry => request.Outputs.All(output => !string.Equals(output.InventoryEntry.OutputId, entry.OutputId, StringComparison.OrdinalIgnoreCase)))) return ActorAssemblyOutcome.Unknown;
        if (request.Outputs.Any(output => output.ReceiptDisposition is ActorAssemblyDocumentDisposition.HashMismatch or ActorAssemblyDocumentDisposition.Invalid or ActorAssemblyDocumentDisposition.SecurityRefused)) return ActorAssemblyOutcome.Blocked;
        if (request.Outputs.Any(output => output.ReceiptDisposition is ActorAssemblyDocumentDisposition.Missing or ActorAssemblyDocumentDisposition.Unreadable || output.Receipt is null)) return ActorAssemblyOutcome.Unknown;
        if (request.Outputs.Any(output => output.TriCatalog is null || output.ReceiptPreset is null)) return ActorAssemblyOutcome.Unknown;
        foreach (var output in request.Outputs)
        {
            var receipt = output.Receipt!;
            var receiptPreset = output.ReceiptPreset!;
            var receiptOutput = receipt.Outputs.SingleOrDefault(item => string.Equals(item.OutputId, output.InventoryEntry.OutputId, StringComparison.OrdinalIgnoreCase));
            if (receiptOutput is null) return ActorAssemblyOutcome.Blocked;
            var inventoryNifs = output.InventoryEntry.NifPackagePaths.Select(path => path.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var receiptNifs = receiptOutput.Nifs.Select(file => file.PackagePath.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!inventoryNifs.SetEquals(receiptNifs)) return ActorAssemblyOutcome.Blocked;
            if ((output.InventoryEntry.TriPackagePath?.Value is string inventoryTri) != (receiptOutput.Tri is not null)) return ActorAssemblyOutcome.Blocked;
            if (output.InventoryEntry.TriPackagePath?.Value is string tri && !string.Equals(tri, receiptOutput.Tri!.PackagePath.Value, StringComparison.OrdinalIgnoreCase)) return ActorAssemblyOutcome.Blocked;
            if (receiptPreset.PresetName != receipt.SourcePreset.PresetName || receiptPreset.SliderSet != receipt.SourcePreset.SliderSet) return ActorAssemblyOutcome.Blocked;
            foreach (var file in receiptOutput.Nifs.Concat(receiptOutput.Tri is null ? Enumerable.Empty<ActorAssemblyOutfitReceiptFile>() : new[] { receiptOutput.Tri }))
            {
                var packageFile = request.Package.Files.SingleOrDefault(candidate => string.Equals(candidate.RelativePath.Value, file.PackagePath.Value, StringComparison.OrdinalIgnoreCase));
                if (packageFile is null || !packageFile.Matches || packageFile.ActualSha256 != file.Sha256) return ActorAssemblyOutcome.Blocked;
            }
            if (output.TriCatalog is not null && receiptOutput.Tri is not null && output.TriCatalog.SourceHash != receiptOutput.Tri.Sha256) return ActorAssemblyOutcome.Blocked;
        }
        return ActorAssemblyOutcome.Pass;
    }

    private static ActorAssemblyOutcome EvaluateDoubleOwner(ActorAssemblyProvenanceEvaluationRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Owner.Disposition is ActorAssemblyDocumentDisposition.HashMismatch or ActorAssemblyDocumentDisposition.Invalid or ActorAssemblyDocumentDisposition.SecurityRefused) return ActorAssemblyOutcome.Blocked;
        if (request.Owner.Owner == ActorAssemblyMorphOwner.ReviewedComposite)
            return EvaluateCompositePolicy(request, diagnostics);
        if (request.Owner.Owner is ActorAssemblyMorphOwner.None)
            return request.Outputs.Any(output => output.ReceiptPreset?.Sliders.Any(row => MathF.Abs(row.Value) > 0F) == true) ? ActorAssemblyOutcome.Blocked : ActorAssemblyOutcome.Pass;
        if (request.Owner.Owner is ActorAssemblyMorphOwner.BakedBodySlide)
        {
            if (request.Inventory is null || request.InventoryDisposition != ActorAssemblyDocumentDisposition.Loaded) return ActorAssemblyOutcome.Unknown;
            if (request.Outputs.Any(output => output.TriCatalog is null || output.ReceiptPreset is null || output.Receipt is null)) return ActorAssemblyOutcome.Unknown;
        }
        else if (request.Owner.Disposition is ActorAssemblyDocumentDisposition.Missing or ActorAssemblyDocumentDisposition.Unreadable || request.Owner.Document is null) return ActorAssemblyOutcome.Unknown;
        var runtimeChannels = RuntimeChannels(request.Owner);
        var anyUnknown = false; var anyBaked = false;
        foreach (var output in request.Outputs)
        {
            if (output.TriCatalog is null || output.ReceiptPreset is null || output.Receipt is null) { anyUnknown = true; continue; }
            var triChannels = output.TriCatalog.SliderNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
            var baked = output.ReceiptPreset.Sliders.Where(row => MathF.Abs(row.Value) > 0F && triChannels.Contains(row.Name)).Select(row => row.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var applied = request.Owner.Document?.AppliedOutputs.FirstOrDefault(item => string.Equals(item.OutputId, output.InventoryEntry.OutputId, StringComparison.OrdinalIgnoreCase));
            if (request.Owner.Owner is ActorAssemblyMorphOwner.OBody or ActorAssemblyMorphOwner.BodyGen or ActorAssemblyMorphOwner.RuntimeScript or ActorAssemblyMorphOwner.ReviewedComposite)
            {
                if (applied is null) { anyUnknown = true; continue; }
                var runtime = runtimeChannels.Where(channel => applied.AppliedChannels.Contains(channel, StringComparer.OrdinalIgnoreCase) && triChannels.Contains(channel)).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (baked.Count > 0 && runtime.Count > 0) return ActorAssemblyOutcome.Blocked;
                if (runtime.Count > 0) anyBaked = true;
                if (baked.Count > 0) anyBaked = true;
            }
            else if (request.Owner.Owner == ActorAssemblyMorphOwner.None && baked.Count > 0) return ActorAssemblyOutcome.Blocked;
            else if (request.Owner.Owner == ActorAssemblyMorphOwner.BakedBodySlide && baked.Count > 0) anyBaked = true;
        }
        if (request.Owner.Owner == ActorAssemblyMorphOwner.BakedBodySlide && request.Outputs.Length > 0 && !anyBaked) return ActorAssemblyOutcome.Blocked;
        if (request.Owner.Owner == ActorAssemblyMorphOwner.BakedBodySlide && request.Outputs.Length == 0) return ActorAssemblyOutcome.Unknown;
        if (anyUnknown) return ActorAssemblyOutcome.Unknown;
        return ActorAssemblyOutcome.Pass;
    }

    private static ImmutableHashSet<string> RuntimeChannels(ActorAssemblyResolvedOwnerEvidence owner)
    {
        if (owner.OBodyPreset is not null) return owner.OBodyPreset.Sliders.Where(row => MathF.Abs(row.Value) > 0F).Select(row => row.Name).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        return owner.Document switch
        {
            ActorAssemblyBodyGenEvidence bodyGen => bodyGen.Channels.Where(channel => MathF.Abs(channel.Value) > 0F).Select(channel => channel.Name).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            ActorAssemblyRuntimeScriptEvidence runtime => runtime.Channels.Where(channel => MathF.Abs(channel.Value) > 0F).Select(channel => channel.Name).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase),
            _ => ImmutableHashSet<string>.Empty.WithComparer(StringComparer.OrdinalIgnoreCase)
        };
    }

    private static ActorAssemblyOutcome DispositionOutcome(ActorAssemblyDocumentDisposition disposition, bool hasDocument) => disposition switch
    {
        ActorAssemblyDocumentDisposition.Loaded when hasDocument => ActorAssemblyOutcome.Pass,
        ActorAssemblyDocumentDisposition.Missing or ActorAssemblyDocumentDisposition.Unreadable => ActorAssemblyOutcome.Unknown,
        _ => ActorAssemblyOutcome.Blocked
    };

    private static ActorAssemblyCheck Check(string code, ActorAssemblyOutcome outcome, string message) =>
        new(code, outcome, message, ImmutableArray<ActorAssemblyCheckEvidence>.Empty);

    private static ActorAssemblyOutcome EvaluateCompositePolicy(ActorAssemblyProvenanceEvaluationRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.CompositePolicyDisposition is ActorAssemblyDocumentDisposition.Missing or ActorAssemblyDocumentDisposition.Unreadable || request.CompositePolicy is null) return ActorAssemblyOutcome.Unknown;
        if (request.CompositePolicyDisposition is ActorAssemblyDocumentDisposition.HashMismatch or ActorAssemblyDocumentDisposition.Invalid or ActorAssemblyDocumentDisposition.SecurityRefused) return ActorAssemblyOutcome.Blocked;
        var policy = request.CompositePolicy;
        if (policy.BaseNpc != request.Contract.BaseNpc || policy.OutfitInventorySha256 != request.InventorySha256) return ActorAssemblyOutcome.Blocked;
        if (request.Owner.Document is not ActorAssemblyCompositeComponentsEvidence components) return ActorAssemblyOutcome.Blocked;
        var owners = components.Components.Select(component => component.Owner).ToHashSet();
        if (!owners.SetEquals(policy.Components.Select(component => component.Owner))) return ActorAssemblyOutcome.Blocked;
        var ownerHashes = components.Components.Where(component => component.Evidence is not null).Select(component => component.Evidence!.Sha256).ToHashSet();
        if (!policy.Components.Where(component => component.EvidenceSha256 is not null).Select(component => component.EvidenceSha256!.Value).ToHashSet().SetEquals(ownerHashes)) return ActorAssemblyOutcome.Blocked;
        var receiptHashes = request.Outputs.Select(output => output.ReceiptSha256).Where(hash => hash is not null).Select(hash => hash!.Value).ToHashSet();
        if (!receiptHashes.SetEquals(policy.ReceiptSha256s)) return ActorAssemblyOutcome.Blocked;
        foreach (var output in policy.Outputs)
        {
            var file = request.Package.Files.SingleOrDefault(candidate => string.Equals(candidate.RelativePath.Value, output.PackagePath.Value, StringComparison.OrdinalIgnoreCase));
            if (file is null || !file.Matches || file.ActualSha256 != output.Sha256) return ActorAssemblyOutcome.Blocked;
        }
        return ActorAssemblyOutcome.Pass;
    }

    private static bool IsTypedOutfitFile(PackageFileVerification file) =>
        string.Equals(file.Kind, "outfit-nif", StringComparison.OrdinalIgnoreCase) || string.Equals(file.Kind, "outfit-tri", StringComparison.OrdinalIgnoreCase);

    private static bool IsAmbiguousOutfitFile(PackageFileVerification file) =>
        (file.RelativePath.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) || file.RelativePath.Value.EndsWith(".tri", StringComparison.OrdinalIgnoreCase)) && !IsTypedOutfitFile(file) &&
        (string.Equals(file.Kind, "generic", StringComparison.OrdinalIgnoreCase) || string.Equals(file.Kind, "unknown", StringComparison.OrdinalIgnoreCase));
}
