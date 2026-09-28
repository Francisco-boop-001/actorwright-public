using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestActorAssemblyPreflightService()
    {
        var root = new WorkspacePath("K:\\ExampleWorkspace");
        var hash = new Sha256Hash(new string('B', 64));
        var actor = new ActorAssemblyActorIdentity(new PluginName("AnnaFieldMedicFollower.esp"), new FormId(0x800));
        var contract = new ActorAssemblyContract(1, "npc-assembly-preflight", GameEdition.SkyrimSpecialEdition,
            new ActorAssemblyBoundFile(new WorkspacePath("K:\\ExampleWorkspace\\manifest.json"), hash), actor,
            new ActorAssemblyPlacement(ActorAssemblyPlacementMode.None, null),
            new ActorAssemblyBodyMorph(ActorAssemblyMorphOwner.None, new ActorAssemblyEvidenceReference(ActorAssemblyEvidenceStatus.NotApplicable, null, "baked owner not claimed")),
            new ActorAssemblyOutfitScope(ActorAssemblyOutfitScopeStatus.NotApplicable, null, "fixture has no outfit scope"), null);
        var plugin = new PackageFileVerification("plugin", new AssetPath("Data/AnnaFieldMedicFollower.esp"), 1, 1, hash, hash, true);
        var package = new PackageVerificationArtifact("1", "package", "skyrimse", "jslot", actor.Plugin.Value, actor.FormId, root, hash, ImmutableArray.Create(plugin), true, true, false);
        ActorAssemblyPreflightService CreateService(
            ImmutableArray<Diagnostic> packageDiagnostics) =>
            new(new ContractFake(contract), new EvidenceFake(),
                new PackageFake(package, packageDiagnostics),
                new IdentityFake(actor), new PresetFake(), new TriFake());

        var service = CreateService(ImmutableArray<Diagnostic>.Empty);
        var request = new ActorAssemblyPreflightRequest(
            new WorkspacePath("K:\\ExampleWorkspace\\contract.json"), hash);
        var result = await service.PreflightAsync(request, CancellationToken.None);
        var artifact = result.Artifact ?? throw new InvalidOperationException("The admitted preflight artifact was not returned.");
        Assert(artifact.Checks.Select(check => check.Code).SequenceEqual(["PACKAGE-INTEGRITY", "BASE-NPC-IDENTITY", "PLACED-REFERENCE-IDENTITY", "MORPH-OWNER-EVIDENCE", "OUTFIT-SCOPE", "OUTFIT-PROVENANCE", "BODY-MORPH-DOUBLE-OWNER"]), "Actor Assembly check order changed.");
        Assert(artifact.NoWrite && !artifact.RuntimeAuthority, "Actor Assembly static authority flags changed.");

        var unregisteredSecurityShapedWarning = new Diagnostic(
            "plugin-outside-warning-extra", DiagnosticSeverity.Warning,
            "This warning code is not a registered security refusal.");
        var ordinaryWarning = await CreateService(
            [unregisteredSecurityShapedWarning])
            .PreflightAsync(request, CancellationToken.None);
        Assert(ordinaryWarning.Artifact is not null &&
               !ordinaryWarning.SecurityRefusal,
            "Unregistered security-shaped package warning was refused.");

        var registeredSecurityWarning = new Diagnostic(
            "actor-assembly-plugin-outside-lab", DiagnosticSeverity.Warning,
            "The registered security code remains a refusal at warning severity.");
        var securityWarning = await CreateService([registeredSecurityWarning])
            .PreflightAsync(request, CancellationToken.None);
        Assert(securityWarning.Artifact is null &&
               securityWarning.SecurityRefusal,
            "Registered package security code lost refusal semantics.");
    }

    private sealed class ContractFake(ActorAssemblyContract contract) : IActorAssemblyContractLoader
    { public ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyContract>> LoadAsync(ActorAssemblyPreflightRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new ActorAssemblyDocumentLoadResult<ActorAssemblyContract>(ActorAssemblyDocumentDisposition.Loaded, contract, request.ContractSha256, false, ImmutableArray<Diagnostic>.Empty)); }
    private sealed class EvidenceFake : IActorAssemblyEvidenceLoader
    {
        public ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOwnerEvidence>> LoadOwnerEvidenceAsync(ActorAssemblyBoundFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOutfitInventory>> LoadOutfitInventoryAsync(ActorAssemblyBoundFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyOutfitBuildReceipt>> LoadReceiptAsync(ActorAssemblyBoundFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyRuntimeChannelReview>> LoadRuntimeReviewAsync(ActorAssemblyBoundFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask<ActorAssemblyDocumentLoadResult<ActorAssemblyReviewedCompositePolicy>> LoadCompositePolicyAsync(ActorAssemblyBoundFile file, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
    private sealed class PackageFake(PackageVerificationArtifact package, ImmutableArray<Diagnostic> diagnostics) : IPackageVerifyService { public ValueTask<PackageVerifyResult> VerifyAsync(PackageVerifyRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new PackageVerifyResult(true, package, diagnostics)); }
    private sealed class IdentityFake(ActorAssemblyActorIdentity actor) : IActorAssemblyIdentityReader { public ValueTask<ActorAssemblyIdentityReadResult> ReadAsync(ActorAssemblyIdentityReadRequest request, CancellationToken cancellationToken) => ValueTask.FromResult(new ActorAssemblyIdentityReadResult(new ActorAssemblyBaseNpcEvidence(actor.Plugin, actor.FormId, new ActorAssemblyRecordObservation(ActorAssemblyObservationStatus.Found, "NPC_", actor.FormId, null), new ActorAssemblyRecordObservation(ActorAssemblyObservationStatus.Found, "NPC_", actor.FormId, null), ActorAssemblyOutcome.Pass), null, "baseNpc", ImmutableArray<Diagnostic>.Empty)); }
    private sealed class PresetFake : IBodySlideSliderPresetInspectionService { public ValueTask<BodySlideSliderPresetInspectionResult> InspectAsync(BodySlideSliderPresetInspectionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException(); }
    private sealed class TriFake : IBodySlideTriInspectionService { public ValueTask<BodySlideTriInspectionResult> InspectAsync(BodySlideTriInspectionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException(); }
}
