using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static readonly string[] WaveBCreateMasters = ["Skyrim.esm", "ActorwrightBlankNpcProvider.esp", "SyntheticHair.esp"];

    private static async Task RunWaveBFinishAndPlacementAsync(WorkspacePath root, WaveBTranscript transcript)
    {
        var package = Child(root, "planned-npc-output");
        var sourcePlugin = Child(package, "Data", "PackagedNpc.esp");
        var sourceManifest = Child(package, "npcmanager-package.json");
        await AssertWaveBCarrierAsync(root, sourcePlugin, package);
        using (var created = SkyrimMod.CreateFromBinaryOverlay(sourcePlugin.Value, SkyrimRelease.SkyrimSE))
            Require(created.ModHeader.MasterReferences.Select(master => master.Master.ToString()).SequenceEqual(
                    WaveBCreateMasters, StringComparer.OrdinalIgnoreCase),
                "Actual create did not preserve the template prefix and appearance provider order for the custom-owner race.");
        transcript.Note("Independent carrier readback matches the winning hair CLFM #5C5850, eye TXST slot 0, and selected HDPT EditorIDs.");
        var createSnapshot = Child(root, "evidence", "create-before-finish-fixture");
        Directory.CreateDirectory(createSnapshot.Value);
        File.Copy(sourcePlugin.Value, Child(createSnapshot, "PackagedNpc.esp").Value);
        File.Copy(sourceManifest.Value, Child(createSnapshot, "npcmanager-package.json").Value);
        var additional = PrepareFinishMasterAppendFixture(root, sourcePlugin, sourceManifest);
        transcript.Note("Owned synthetic Finish input preparation extends the actual create master prefix to seven and adds retained-link sentinels, then refreshes the source package binding. No local CSTY is seeded. The original create carrier is independently checked before this explicit fixture preparation.");
        var key = ModKey.FromNameAndExtension("PackagedNpc.esp");
        using var source = SkyrimMod.CreateFromBinaryOverlay(sourcePlugin.Value, SkyrimRelease.SkyrimSE);
        INpcGetter npc = source.Npcs.Single();
        Require(source.CombatStyles.Count == 0, "The fixture manually seeded a local combat style.");
        var copiedMaster = Child(root, "authority", "Skyrim.esm");
        SkyrimFinishMasterFixture.AppendCanonicalPackGroupToMaster(
            Child(root, "Data", "Skyrim.esm").Value,
            copiedMaster.Value,
            root.Value);
        PrepareFinishOutfitRaceFixture(copiedMaster, npc.Race.FormKey);
        var request = new SkyrimNpcFinishCoreRequest
        {
            Schema = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
            Source = new()
            {
                PackageRoot = package, PackageManifest = sourceManifest, PackageManifestSha256 = new Sha256Hash(HashFile(sourceManifest)),
                PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(package),
                PluginPath = sourcePlugin, Plugin = new PluginName(key.ToString()), PluginSha256 = new Sha256Hash(HashFile(sourcePlugin))
            },
            Actor = new() { EditorId = new EditorId(npc.EditorID!), FormId = new FormId(npc.FormKey.ID) },
            Authorities = new()
            {
                BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                Providers = [new() { Plugin = new PluginName(key.ToString()), Path = sourcePlugin,
                    Sha256 = new Sha256Hash(HashFile(sourcePlugin)), ByteLength = new FileInfo(sourcePlugin.Value).Length }],
                AdditionalMasters = [additional]
            },
            AiPolicy = new() { Aggression = SkyrimNpcFinishCoreAggression.Unaggressive, Confidence = SkyrimNpcFinishCoreConfidence.Brave,
                Energy = 50, Morality = SkyrimNpcFinishCoreMorality.NoCrime, Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
                Mood = SkyrimNpcFinishCoreMood.Neutral },
            CombatPolicy = new() { SeedLocalStyle = true, Profile = SkyrimNpcFinishCoreCombatProfile.RangedFirst },
            PerkPolicy = [new(new FormReference(new PluginName("Skyrim.esm"), new FormId(0x58F6A)), 2)],
            OutfitPolicy = new() { Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit,
                ExistingOutfit = new FormReference(additional.Plugin, new FormId(0x800)) },
            SandboxAuthority = new() { CopiedMaster = copiedMaster, CopiedMasterSha256 = new Sha256Hash(HashFile(copiedMaster)),
                Template = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x1B217)), TemplateEditorId = "DefaultSandboxEditorLocation512" },
            Output = new() { Root = Child(root, "finished"), Archive = Child(root, "finished.zip"), PluginFileName = key.ToString() }
        };
        var requestPath = Child(root, "evidence", "finish-request.json");
        var rejectedProposal = Child(root, "evidence", "excluded-outfit-proposal.json");
        File.WriteAllBytes(requestPath.Value, SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, root));
        var refused = await transcript.Invoke("vanilla outfit race refusal", "npc", "finish", "analyze", "--json", "--request", requestPath.Value,
            "--request-sha256", HashFile(requestPath), "--proposal", rejectedProposal.Value);
        Require(refused.ExitCode != 0 && refused.Root.ToString().Contains("finish-core-outfit-armature-race-excluded", StringComparison.Ordinal) &&
                !File.Exists(rejectedProposal.Value), "Excluded vanilla ARMA did not produce its typed refusal: " + refused.Root);
        request = request with { OutfitRacePolicy = SkyrimNpcFinishCoreOutfitRacePolicy.Clone };
        byte[] canonical = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, root);
        if (Environment.GetEnvironmentVariable("ACTORWRIGHT_COMPATIBILITY_OBSERVATION_OUTPUT") is not null)
            File.WriteAllBytes(Child(root, "evidence", "finish-request-canonical.json").Value, canonical);
        File.WriteAllBytes(requestPath.Value, ReformatFinishDocument(canonical));
        string requestHash = SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, root).Value;
        Require(!string.Equals(requestHash, HashFile(requestPath), StringComparison.OrdinalIgnoreCase), "Pretty request did not differ physically from canonical bytes.");
        var proposal = Child(root, "evidence", "finish-proposal.json");
        var analyzed = await transcript.Success("Finish analyze clone armature", "npc", "finish", "analyze", "--json", "--request", requestPath.Value,
            "--request-sha256", requestHash, "--proposal", proposal.Value);
        Require(analyzed.Root.GetProperty("status").GetString() == "ReadyForReviewedWrite", "Finish analyze is not ready.");
        var applied = await transcript.Success("Finish apply", "npc", "finish", "apply", "--json", "--request", requestPath.Value,
            "--request-sha256", requestHash, "--proposal", proposal.Value, "--proposal-sha256", HashFile(proposal));
        Require(applied.Root.GetProperty("applied").GetBoolean(), "Finish apply did not publish its result.");
        var finishManifest = Child(root, "finished", "NPCManager", "Evidence", "finish-core-manifest.json");
        var verified = await transcript.Success("Finish verify", "npc", "finish", "verify", "--json", "--manifest", finishManifest.Value,
            "--manifest-sha256", HashFile(finishManifest));
        Require(verified.Root.GetProperty("verified").GetBoolean(), "Independent Finish verification failed.");
        var finishedPlugin = Child(root, "finished", "Data", "PackagedNpc.esp");
        using (var output = SkyrimMod.CreateFromBinaryOverlay(finishedPlugin.Value, SkyrimRelease.SkyrimSE))
        {
            Require(output.ModHeader.MasterReferences.Count == 8 && output.ModHeader.MasterReferences.Take(7).Select(row => row.Master)
                    .SequenceEqual(source.ModHeader.MasterReferences.Select(row => row.Master)), "Finish failed seven-to-eight prefix preservation.");
            var outputRecords = output.EnumerateMajorRecords().ToDictionary(record => record.FormKey);
            foreach (IMajorRecordGetter record in source.EnumerateMajorRecords())
            {
                Require(outputRecords.TryGetValue(record.FormKey, out var retained), "Master reindex lost full record ownership: " + record.FormKey);
                if (record is not INpcGetter)
                    Require(record.EnumerateFormLinks().Select(link => link.FormKey).SequenceEqual(retained!.EnumerateFormLinks().Select(link => link.FormKey)),
                        "Master reindex changed retained full FormLinks: " + record.FormKey);
            }
            INpcGetter finished = output.Npcs.Single();
            Require(output.CombatStyles.Count == 1 && finished.CombatStyle.FormKey == output.CombatStyles.Single().FormKey &&
                    finished.Perks is { Count: 1 } && finished.Perks[0].Perk.FormKey == new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x58F6A) &&
                    finished.Perks[0].Rank == 2, "Finish did not author one local CSTY and requested PRKR rank.");
            Require(finished.HeadTexture.FormKey == npc.HeadTexture.FormKey && finished.SleepingOutfit.FormKey == npc.SleepingOutfit.FormKey &&
                    finished.Keywords!.Select(link => link.FormKey).SequenceEqual(npc.Keywords!.Select(link => link.FormKey)),
                "Master reindex changed protected target NPC links.");
        }
        AssertFinishOutfitRaceOutput(finishedPlugin, npc.Race.FormKey);
        Require(new Sha256Hash(HashFile(sourcePlugin)) == request.Source.PluginSha256 &&
                SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(package) == request.Source.PackageTreeSha256,
            "Finish modified its bound source package.");
        transcript.Note("Independent plugin readback: seven preserved source masters plus the eighth outfit owner; retained full record/FormLink ownership; one local CSTY; PRKR rank 2; output-owned ARMO/ARMA chain admitting the actor race. The pretty request used its canonical hash.");
        await RunWaveBPlacementAsync(root, transcript, finishManifest, finishedPlugin);
        var packageVerification = await transcript.Invoke("final package verify", "package", "verify", "--json", "--manifest",
            Child(root, "finished", "npcmanager-package.json").Value);
        await transcript.Success("create to Finish plugin audit", "plugin", "audit", "--json", "--edition", "skyrimse",
            "--before", sourcePlugin.Value, "--after", finishedPlugin.Value);
        bool claimedVerified = packageVerification.Root.TryGetProperty("verified", out JsonElement packageVerified) &&
            packageVerified.ValueKind == JsonValueKind.True;
        Require(packageVerification.ExitCode != 0 &&
                packageVerification.Root.GetProperty("code").GetString() ==
                    "workflow-human-review-required" &&
                packageVerification.Root.GetProperty("diagnostics").EnumerateArray().Any(diagnostic =>
                    diagnostic.GetProperty("code").GetString() == "workflow-human-review-required" &&
                    diagnostic.GetProperty("severity").GetString() == "error") &&
                !claimedVerified,
            "Workflow-free Wave B package verification did not retain the exact workflow gate: " +
            packageVerification.Root);
        transcript.Note("Workflow-free V1 package verification retained the exact workflow-human-review-required gate and claimed no package verification authority.");
    }

    private static async Task AssertWaveBCarrierAsync(WorkspacePath root, WorkspacePath plugin, WorkspacePath package)
    {
        string carrierPath = Directory.EnumerateFiles(Child(package, "Data", "meshes", "actors", "character", "FaceGenData", "FaceGeom").Value,
            "*.nif", SearchOption.AllDirectories).Single();
        var carrier = new WorkspacePath(carrierPath);
        using var records = SkyrimMod.CreateFromBinaryOverlay(plugin.Value, SkyrimRelease.SkyrimSE);
        INpcGetter npc = records.Npcs.Single();
        Require(npc.Race.FormKey == new FormKey(ModKey.FromNameAndExtension("SyntheticHair.esp"), 0x900),
            "The actual create output did not retain the custom-owner synthetic RACE.");
        IColorRecordGetter color = records.Colors.Single(row => row.FormKey == npc.HairColor.FormKey);
        string expectedColor = $"#{color.Color.R:X2}{color.Color.G:X2}{color.Color.B:X2}";
        var hair = await new FaceGeomHairRegionsAnalyzer(root).AnalyzeAsync(carrier, new Sha256Hash(HashFile(carrier)), null, CancellationToken.None);
        Require(expectedColor == "#5C5850" && hair.Regions.Any(row => row.Name == "SyntheticExternalHair" && row.CurrentColor == expectedColor),
            "Carrier HairTint does not match actual record CLFM.");
        SseNifDocument nif = SseFaceGeomCarrierCodec.Parse(File.ReadAllBytes(carrier.Value));
        SseNifBlock eyes = nif.Blocks.Single(block => block.Name == "SyntheticRecordEyes");
        SseNifBlock shader = nif.Blocks[eyes.References.Single(link => link.Target >= 0 && nif.Blocks[link.Target].Type == "BSLightingShaderProperty").Target];
        SseNifBlock textures = nif.Blocks[shader.References.Single(link => link.Target >= 0 && nif.Blocks[link.Target].Type == "BSShaderTextureSet").Target];
        using var provider = SkyrimMod.CreateFromBinaryOverlay(Child(root, "Data", "ActorwrightBlankNpcProvider.esp").Value, SkyrimRelease.SkyrimSE);
        var headPart = provider.HeadParts.Single(part => part.FormKey.ID == 0x821);
        var txst = provider.TextureSets.Single(row => row.FormKey == headPart.TextureSet.FormKey);
        static string TexturePath(string path)
        {
            string normalized = path.Replace('\\', '/');
            return normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ? normalized : "textures/" + normalized;
        }
        Require(TexturePath(textures.Textures[0]).Equals(TexturePath(txst.Diffuse!.ToString()), StringComparison.OrdinalIgnoreCase),
            "Carrier eye slot differs from actual winning HDPT TXST: " + textures.Textures[0] + " versus " + txst.Diffuse);
        Require(nif.Blocks.Any(block => block.Name == "WaveBPrivateBrow"), "Carrier did not use the product-authored brow EditorID.");
    }

    private static async Task RunWaveBPlacementAsync(WorkspacePath root, WaveBTranscript transcript, WorkspacePath coreManifest, WorkspacePath corePlugin)
    {
        string Rel(WorkspacePath path) => Path.GetRelativePath(root.Value, path.Value).Replace('\\', '/');
        using var npcSource = SkyrimMod.CreateFromBinaryOverlay(corePlugin.Value, SkyrimRelease.SkyrimSE);
        uint npc = npcSource.Npcs.Single().FormKey.ID;
        var cellProvider = Child(root, "authority", "SyntheticCell.esp");
        File.WriteAllBytes(cellProvider.Value, new BethesdaSkyrimInteriorPlacementTopologyWriter().Write(new InteriorPlacementTopologyInput(
            new PluginName("SyntheticCell.esp"), [new PluginName("Skyrim.esm"), new PluginName("PackagedNpc.esp")],
            0x1485, "SyntheticInterior", 0x10, 0x20, 0x01000000 | npc,
            new InteriorPlacementTopologyTransform(1, 2, 3, 0, 0, 0), null)));
        var providers = new List<WorkspacePath>();
        foreach (var master in npcSource.ModHeader.MasterReferences)
        {
            string name = master.Master.ToString();
            var authority = Child(root, "authority", name);
            var data = Child(root, "Data", name);
            if (File.Exists(authority.Value)) providers.Add(authority);
            else if (File.Exists(data.Value)) providers.Add(data);
            else
            {
                Require(name.StartsWith("SourceMaster", StringComparison.Ordinal), "Missing actual master provider: " + name);
                new SkyrimMod(master.Master, SkyrimRelease.SkyrimSE).WriteToBinary(authority.Value);
                providers.Add(authority);
            }
        }
        providers.Add(corePlugin);
        providers.Add(cellProvider);
        var request = new SkyrimInteriorPlacementRequest
        {
            FinishCore = new() { Manifest = Rel(coreManifest), ManifestSha256 = HashFile(coreManifest) },
            LoadOrder = providers.Select((path, index) => new SkyrimInteriorPlacementProvider
                { Plugin = Path.GetFileName(path.Value), Path = Rel(path), Sha256 = HashFile(path), Order = index }).ToImmutableArray(),
            Cell = new() { ProviderPlugin = "SyntheticCell.esp", Owner = "Skyrim.esm|0x00001485", RawFormId = "0x00001485",
                EditorId = "SyntheticInterior", InteriorBlock = 0x10, InteriorSubBlock = 0x20 },
            Transform = new() { Mode = SkyrimInteriorPlacementTransformMode.ExplicitValues, X = 1, Y = 2, Z = 3 },
            Patch = new() { Optional = true }, Output = new() { Root = "placement", Archive = "placement.zip" }
        };
        var requestPath = Child(root, "evidence", "placement-request.json");
        File.WriteAllBytes(requestPath.Value, SkyrimInteriorPlacementDocumentCodec.SerializeRequest(request));
        var proposal = Child(root, "evidence", "placement-proposal.json");
        var analyzed = await transcript.Success("CELL placement analyze", "npc", "placement", "interior", "analyze", "--json", "--request", requestPath.Value,
            "--request-sha256", HashFile(requestPath), "--proposal", proposal.Value);
        await transcript.Success("CELL placement apply", "npc", "placement", "interior", "apply", "--json", "--request", requestPath.Value,
            "--request-sha256", HashFile(requestPath), "--proposal", proposal.Value, "--proposal-sha256", analyzed.Root.GetProperty("proposalSha256").GetString()!);
        var manifest = Child(root, "placement", "interior-placement.manifest.json");
        var verified = await transcript.Success("CELL placement verify", "npc", "placement", "interior", "verify", "--json", "--manifest", manifest.Value,
            "--manifest-sha256", HashFile(manifest));
        Require(verified.Root.GetProperty("verified").GetBoolean(), "Placement verifier did not accept the independent topology.");
        using JsonDocument binding = JsonDocument.Parse(File.ReadAllBytes(manifest.Value));
        string patchName = binding.RootElement.GetProperty("patchPlugin").GetString()!;
        string patchPath = Path.Combine(root.Value, binding.RootElement.GetProperty("patchPath").GetString()!);
        Require(patchName == "PackagedNpc_InteriorPlacement.esp" &&
                (BinaryPrimitives.ReadUInt32LittleEndian(File.ReadAllBytes(patchPath).AsSpan(8, 4)) & 0x200) != 0,
            "Placement patch extension/ESL flag is inconsistent with its ESP master.");
        transcript.Note("Task 20 quest-alias placement has not landed; the admitted CELL mode independently verified an .esp patch with its ESL flag. No pathing/runtime/visual claim.");
    }
}
