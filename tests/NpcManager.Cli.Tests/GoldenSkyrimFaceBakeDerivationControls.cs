using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static async Task VerifyFaceBakeDerivationControlsAsync(WorkspacePath root, WorkspacePath preset)
    {
        WorkspacePath sourceRoot = root;
        root = Child(sourceRoot, "derivation-controls");
        Directory.CreateDirectory(Child(root, "npc-preflight").Value);
        foreach (string source in Directory.GetFiles(Child(sourceRoot, "Data").Value, "*", SearchOption.AllDirectories))
        {
            string destination = Path.Combine(root.Value, Path.GetRelativePath(sourceRoot.Value, source));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        File.Copy(preset.Value, Child(root, "npc-preflight", "fixture.jslot").Value);
        preset = Child(root, "npc-preflight", "fixture.jslot");
        File.Copy(Child(sourceRoot, "npc-preflight", "face.nif").Value, Child(root, "npc-preflight", "face.nif").Value);
        // Only named synthetic provider files are reused; its unrelated Finish
        // documents are neither loaded nor treated as derivation evidence.
        using var external = new FinishExternalSmpFixture();
        foreach (WorkspacePath source in new[] { external.ProviderPluginPath, external.ProviderModelPath,
                     external.ProviderPhysicsPath, external.ProviderDefaultBbpPath, external.ProviderTriPath,
                     external.ProviderColliderPath })
        {
            string destination = Path.Combine(Child(root, "Data").Value,
                Path.GetRelativePath(external.DataRoot.Value, source.Value));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source.Value, destination);
        }
        var providerPath = Child(root, "Data", "ActorwrightBlankNpcProvider.esp");
        var provider = SkyrimMod.CreateFromBinary(providerPath.Value, SkyrimRelease.SkyrimSE);
        provider.HeadParts.Add(new HeadPart(new FormKey(provider.ModKey, 0x880), SkyrimRelease.SkyrimSE)
        {
            EditorID = "DerivationRecordOnly", Flags = HeadPart.Flag.Female, Type = HeadPart.TypeEnum.Misc
        });
        provider.WriteToBinary(providerPath.Value);
        var jslot = JsonNode.Parse(File.ReadAllBytes(preset.Value))!;
        jslot["headParts"]!.AsArray().Add(new JsonObject
        {
            ["formIdentifier"] = "ActorwrightBlankNpcProvider.esp|0x00000880", ["type"] = 0
        });
        jslot["headParts"]!.AsArray().Add(new JsonObject
        {
            ["formIdentifier"] = "OrchidAdornment.esp|0x00000800", ["type"] = 3
        });
        File.WriteAllText(preset.Value, jslot.ToJsonString(), new UTF8Encoding(false));

        var policy = new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-protected"));
        var indexer = new BatchScopedAssetIndexer(new BethesdaAssetIndexer());
        var planner = new SkyrimAssetAuthorityPlanner(indexer, policy, root);
        var content = new SkyrimAssetContentResolver(policy, root);
        var loader = new SkyrimFaceBakeAuthorityLoader(policy, root, content);
        SkyrimFaceBakeAuthorityDerivationService CreateService(ISkyrimFaceBakeAuthorityLoader readback) => new(new PresetService(policy, root),
            new SkyrimFaceRecordPluginAuthorityLoader(policy, root), new BethesdaSkyrimFaceRecordRouteResolver(policy, root),
            planner, content, new SkyrimRaceMenuCatalogAuthorityLoader(indexer, planner, content),
            new RaceMenuSliderCatalogParserCore(), new SseSelectedHeadpartNifGeometryReader(),
            new SkyrimFaceBakeCarrierGeometryReader(), readback, policy,
            new ExternalHeadPartDependencyDiscoveryService(planner, content, new ExternalHeadPartPhysicsBindingResolver(policy, root)),
            new ExternalHeadPartFaceGeomExclusionVerifier());
        var service = CreateService(loader);
        var carrier = Child(root, "npc-preflight", "face.nif");
        var output = Child(root, "npc-preflight", "derived-record-only-smp.json");
        var request = new SkyrimFaceBakeAuthorityDerivationRequest(root, preset, new Sha256Hash(HashFile(preset)),
            Child(root, "Data"), [new("Skyrim.esm"), new("ActorwrightBlankNpcProvider.esp"), new("OrchidAdornment.esp")],
            new(new("Skyrim.esm"), new(0x13746)), NpcSex.Female,
            new WorkspaceProviderResourceAuthority("facegeom-carrier", carrier, new Sha256Hash(HashFile(carrier))), output);
        var derived = await service.DeriveAsync(request, CancellationToken.None);
        Require(derived.Accepted && derived.Artifact is not null,
            "Actual model-less/SMP derivation refused: " + string.Join("; ", derived.Diagnostics.Select(item => item.Code + ": " + item.Message)));
        var loaded = await loader.LoadAsync(new(root, output, derived.Artifact!.Sha256), CancellationToken.None);
        Require(loaded.Loaded && loaded.Authority is { CarrierShapes.Length: 7 } authority &&
                authority.RecordOnlyMappedHeadParts.Select(part => part.ToString()).SequenceEqual(
                    ["ActorwrightBlankNpcProvider.esp|0x00000880", "OrchidAdornment.esp|0x00000800"]) &&
                authority.Assets.All(asset => !asset.AssetPath.Value.Contains("orchid", StringComparison.OrdinalIgnoreCase)),
            "Model-less or independently excluded SMP records became ordinary geometry assets.");

        var failedOutput = Child(root, "npc-preflight", "forced-readback-refusal.json");
        var failures = new List<string>();
        var failedPublication = await CreateService(new RefuseAfterRealAuthorityReadback(loader)).DeriveAsync(
            request with { Output = failedOutput }, CancellationToken.None);
        if (failedPublication.Accepted || File.Exists(failedOutput.Value) ||
            Directory.GetFiles(Path.GetDirectoryName(failedOutput.Value)!, "forced-readback-refusal.json.tmp-*").Length != 0)
            failures.Add("Failed independent readback left a final authority or owned temporary file.");

        byte[] dummyBytes = File.ReadAllBytes(Child(root, "Data", "meshes", "Actorwright", "Derivation", "Head1.nif").Value);
        var dummyDocument = SseFaceGeomCarrierCodec.Parse(dummyBytes);
        var dummyShape = dummyDocument.Blocks.Single(block => block.Type == "BSDynamicTriShape");
        byte[] oldName = Encoding.ASCII.GetBytes(dummyShape.Name!);
        byte[] newName = Encoding.ASCII.GetBytes("DummyLensX");
        int nameOffset = dummyBytes.AsSpan().IndexOf(oldName);
        Require(oldName.Length == newName.Length && nameOffset >= 0,
            "Owned dummy source must retain the exact ten-byte model name slot.");
        newName.CopyTo(dummyBytes.AsSpan(nameOffset));
        BinaryPrimitives.WriteInt32LittleEndian(dummyBytes.AsSpan(dummyShape.References.Single(row => row.Kind == "shader").Offset, 4), -1);
        var dummyPath = new AssetPath("meshes/Actorwright/Derivation/LensDummy.nif");
        File.WriteAllBytes(Path.Combine(Child(root, "Data").Value, dummyPath.Value), dummyBytes);
        var dummyHash = new Sha256Hash(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(dummyBytes)));
        var dummyIntake = new SseSelectedHeadpartNifGeometryReader().Read(new(dummyPath, dummyHash, dummyBytes.ToImmutableArray()));
        Require(dummyIntake.Accepted && dummyIntake.Document!.Shapes.Single().IsShaderlessDummy,
            "Actual shaderless dummy was not independently admitted by the existing typed classifier.");
        provider.HeadParts.Add(new HeadPart(new FormKey(provider.ModKey, 0x881), SkyrimRelease.SkyrimSE)
        {
            EditorID = "DerivationLensDummy", Flags = HeadPart.Flag.Female | (HeadPart.Flag)8,
            Type = HeadPart.TypeEnum.Eyes, Model = new Model { File = dummyPath.Value[7..] }
        });
        provider.WriteToBinary(providerPath.Value);
        jslot["headParts"]!.AsArray().Add(new JsonObject
        {
            ["formIdentifier"] = "ActorwrightBlankNpcProvider.esp|0x00000881", ["type"] = 2
        });
        File.WriteAllText(preset.Value, jslot.ToJsonString(), new UTF8Encoding(false));
        request = request with { ExpectedPresetSha256 = new Sha256Hash(HashFile(preset)), Output = Child(root, "npc-preflight", "derived-dummy.json") };
        var dummyDerived = await service.DeriveAsync(request, CancellationToken.None);
        if (!dummyDerived.Accepted || dummyDerived.Artifact is null)
            failures.Add("Typed shaderless dummy must be explicitly omitted from carrier mapping: " + string.Join("; ", dummyDerived.Diagnostics.Select(item => item.Message)));
        else
        {
            var dummyLoaded = await loader.LoadAsync(new(root, request.Output, dummyDerived.Artifact.Sha256), CancellationToken.None);
            if (!dummyLoaded.Loaded || dummyLoaded.Authority is not { CarrierShapes.Length: 7 } dummyAuthority ||
                !dummyAuthority.RecordOnlyMappedHeadParts.Any(part => part.FormId.Value == 0x881) ||
                dummyAuthority.Assets.Any(asset => asset.AssetPath == dummyPath))
                failures.Add("Dummy omission lost its explicit record-only disposition or retained an unused geometry asset.");
        }

        provider.HeadParts.Single(part => part.FormKey.ID == 0x802).ExtraParts.Add(new FormKey(provider.ModKey, 0x881));
        provider.WriteToBinary(providerPath.Value);
        jslot["headParts"]!.AsArray().RemoveAt(jslot["headParts"]!.AsArray().Count - 1);
        File.WriteAllText(preset.Value, jslot.ToJsonString(), new UTF8Encoding(false));
        request = request with { ExpectedPresetSha256 = new Sha256Hash(HashFile(preset)) };
        var childOutput = Child(root, "npc-preflight", "unsupported-dummy-child.json");
        var child = await service.DeriveAsync(request with { Output = childOutput }, CancellationToken.None);
        if (child.Accepted || File.Exists(childOutput.Value) ||
            !child.Diagnostics.Any(item => item.Code == "face-bake-derivation-dummy-hnam-unsupported"))
            failures.Add("Unrepresentable default/HNAM dummy child did not refuse with its precise diagnostic before publication.");
        provider.HeadParts.Single(part => part.FormKey.ID == 0x802).ExtraParts.Remove(new FormKey(provider.ModKey, 0x881));

        provider.HeadParts.Single(part => part.FormKey.ID == 0x802).EditorID = "NoCarrierMatch";
        provider.WriteToBinary(providerPath.Value);
        var ambiguousOutput = Child(root, "npc-preflight", "ambiguous-authority.json");
        var ambiguous = await service.DeriveAsync(request with { Output = ambiguousOutput }, CancellationToken.None);
        Require(!ambiguous.Accepted && !File.Exists(ambiguousOutput.Value) &&
                ambiguous.Diagnostics.Any(item => item.Code == "face-bake-derivation-refused" && item.Message.Contains("unique unused carrier mapping", StringComparison.Ordinal)),
            "Ambiguous same-topology carrier candidates were guessed instead of refused.");
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private sealed class RefuseAfterRealAuthorityReadback(ISkyrimFaceBakeAuthorityLoader actual) : ISkyrimFaceBakeAuthorityLoader
    {
        public async ValueTask<SkyrimFaceBakeAuthorityLoadResult> LoadAsync(
            SkyrimFaceBakeAuthorityLoadRequest request, CancellationToken cancellationToken)
        {
            var loaded = await actual.LoadAsync(request, cancellationToken);
            Require(loaded.Loaded, "The controlled failure must follow a successful real loader readback.");
            return new(SkyrimFaceBakeAuthorityLoadStatus.ContentRefused, null, loaded.ActualManifestSha256,
                [new("test-forced-authority-readback-refusal", DiagnosticSeverity.Error, "Controlled refusal after real readback.")]);
        }
    }
}
