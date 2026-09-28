using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestActorAssemblyProvenance()
    {
        var hash = new Sha256Hash(new string('A', 64));
        var plugin = new PluginName("AnnaFieldMedicFollower.esp");
        var actor = new ActorAssemblyActorIdentity(plugin, new FormId(0x800));
        var contract = new ActorAssemblyContract(1, "npc-assembly-preflight", GameEdition.SkyrimSpecialEdition,
            new ActorAssemblyBoundFile(new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\manifest.json")), hash), actor,
            new ActorAssemblyPlacement(ActorAssemblyPlacementMode.None, null),
            new ActorAssemblyBodyMorph(ActorAssemblyMorphOwner.OBody, new ActorAssemblyEvidenceReference(ActorAssemblyEvidenceStatus.Available, new ActorAssemblyBoundFile(new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\owner.json")), hash), null)),
            new ActorAssemblyOutfitScope(ActorAssemblyOutfitScopeStatus.Complete, new ActorAssemblyBoundFile(new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\inventory.json")), hash), null), null);
        var preset = new BodySlideSliderPresetDocument(hash, "Anna", "UBE", ImmutableArray<string>.Empty,
            ImmutableArray.Create(new BodySlideSliderPresetRow("Breasts", "", 45F)));
        var tri = new BodySlideTriCatalog(hash, ImmutableArray.Create(new BodySlideTriShape("Anna", ImmutableArray.Create(new BodySlideTriMorph("Breasts", BodySlideTriMorphType.Position, ImmutableArray.Create(new BodySlideTriOffset(0, 1, 1, 1)))))));
        var ownerDocument = new ActorAssemblyOBodyEvidence(1, "actor-assembly-obody-evidence", GameEdition.SkyrimSpecialEdition, actor,
            new ActorAssemblyBoundFile(new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\preset.xml")), hash),
            ImmutableArray.Create(new ActorAssemblyBoundFile(new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\assignment.json")), hash)),
            ImmutableArray.Create(new ActorAssemblyAppliedOutput("anna-main-outfit", ImmutableArray.Create("Breasts"))));
        var inventoryEntry = new ActorAssemblyMorphableOutfitEntry("anna-main-outfit", ImmutableArray.Create(new AssetPath("Data/anna_0.nif")), new AssetPath("Data/anna.tri"), new ActorAssemblyEvidenceReference(ActorAssemblyEvidenceStatus.Available, new ActorAssemblyBoundFile(new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\receipt.json")), hash), null));
        var inventory = new ActorAssemblyOutfitInventory(1, "actor-assembly-outfit-inventory", GameEdition.SkyrimSpecialEdition, hash, actor, ImmutableArray.Create<ActorAssemblyOutfitEntry>(inventoryEntry));
        var receipt = new ActorAssemblyOutfitBuildReceipt(1, "actor-assembly-outfit-build-receipt", GameEdition.SkyrimSpecialEdition,
            new ActorAssemblyTool("BodySlide", "5.7", new ActorAssemblyBoundFile(new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\bs.exe")), hash)),
            new ActorAssemblyReceiptPreset(new ActorAssemblyBoundFile(new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\preset.xml")), hash), "Anna", "UBE"),
            ImmutableArray.Create(new ActorAssemblyOutfitReceiptOutput("anna-main-outfit", ImmutableArray.Create(new ActorAssemblyOutfitReceiptFile(new AssetPath("Data/anna_0.nif"), hash)), new ActorAssemblyOutfitReceiptFile(new AssetPath("Data/anna.tri"), hash))));
        var package = new PackageVerificationArtifact("1", "package", "skyrimse", "jslot", plugin.Value, actor.FormId, new WorkspacePath(Path.GetFullPath("K:\\ExampleWorkspace\\manifest.json")), hash, ImmutableArray<PackageFileVerification>.Empty, true, true, false);
        var request = new ActorAssemblyProvenanceEvaluationRequest(contract, package,
            new ActorAssemblyResolvedOwnerEvidence(ActorAssemblyMorphOwner.OBody, ActorAssemblyDocumentDisposition.Loaded,
                ownerDocument, preset, null, hash), inventory, ActorAssemblyDocumentDisposition.Loaded, hash,
            ImmutableArray.Create(new ActorAssemblyResolvedOutputEvidence(inventoryEntry, ActorAssemblyDocumentDisposition.Loaded, receipt, preset, tri, hash)), null, ActorAssemblyDocumentDisposition.Missing);
        var result = ActorAssemblyProvenanceEvaluator.Evaluate(request);
        Assert(result.Checks.Any(check => check.Code == "BODY-MORPH-DOUBLE-OWNER" && check.Outcome == ActorAssemblyOutcome.Blocked), "A baked/runtime morph collision was not blocked.");
        return Task.CompletedTask;
    }
}
