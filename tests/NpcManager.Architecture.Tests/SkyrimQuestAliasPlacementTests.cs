using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.IO.Compression;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Architecture.Tests;

internal static class SkyrimQuestAliasPlacementTests
{
    public static async Task RunAsync()
    {
        string root = Path.Combine(Environment.CurrentDirectory, "artifacts", "task20", "quest-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        ModKey core = ModKey.FromNameAndExtension("QuestCore.esp");
        FormKey marker = new(skyrim, 0x1F88C);
        FormKey npcKey = new(core, 0x800);
        FormKey packageKey = new(core, 0x801);
        var source = new SkyrimMod(skyrim, SkyrimRelease.SkyrimSE);
        source.Statics.Add(new Static(new FormKey(skyrim, 0x900), SkyrimRelease.SkyrimSE) { EditorID = "SyntheticXMarker" });
        var cell = new Cell(new FormKey(skyrim, 0x1485), SkyrimRelease.SkyrimSE)
        {
            EditorID = "SyntheticMarkerCell", Flags = Cell.Flag.IsInteriorCell
        };
        cell.Persistent.Add(new PlacedObject(marker, SkyrimRelease.SkyrimSE)
        {
            EditorID = "SyntheticPersistentMarker",
            MajorRecordFlagsRaw = (int)PlacedObject.DefaultMajorFlag.Persistent,
            Base = new FormLinkNullable<IPlaceableObjectGetter>(new FormKey(skyrim, 0x900))
        });
        source.Cells.AddInteriorCell(cell);
        Write(source, Path.Combine(root, "Skyrim.esm"));
        var coreMod = new SkyrimMod(core, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        coreMod.ModHeader.MasterReferences.Add(new MasterReference { Master = skyrim });
        var package = new Package(packageKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "SyntheticCoreSandbox", ScheduleMonth = -1,
            ScheduleDayOfWeek = Package.DayOfWeek.Any, ScheduleHour = -1, ScheduleMinute = -1
        };
        package.ProcedureTree.Add(new PackageBranch
        {
            BranchType = "Procedure", ProcedureType = "Sandbox", DataInputIndices = [0]
        });
        package.Data[0] = new PackageDataLocation
        {
            Location = new LocationTargetRadius
            {
                Target = new LocationFallback { Type = LocationTargetRadius.LocationType.NearEditorLocation, Data = 0 },
                Radius = 512
            }
        };
        coreMod.Packages.Add(package);
        var npc = new Npc(npcKey, SkyrimRelease.SkyrimSE) { EditorID = "SyntheticCoreNpc" };
        npc.Packages.Add(new FormLink<IPackageGetter>(packageKey));
        coreMod.Npcs.Add(npc);
        Write(coreMod, Path.Combine(root, "QuestCore.esp"));
        byte[] coreBytes = File.ReadAllBytes(Path.Combine(root, "QuestCore.esp"));
        byte[] masterBytes = File.ReadAllBytes(Path.Combine(root, "Skyrim.esm"));
        byte[] finish = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = SkyrimNpcFinishCoreManifest.SchemaIdentifier,
            plugin = "QuestCore.esp", pluginSha256 = Hash(coreBytes), baseNpc = "QuestCore.esp|0x00000800",
            placementIncluded = false, runtimeAuthority = false, visualAuthority = false
        });
        File.WriteAllBytes(Path.Combine(root, "core.manifest.json"), finish);
        byte[] requestBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = SkyrimInteriorPlacementRequest.SchemaIdentifier,
            finishCore = new { manifest = "core.manifest.json", manifestSha256 = Hash(finish) },
            loadOrder = new[]
            {
                new { plugin = "Skyrim.esm", path = "Skyrim.esm", sha256 = Hash(masterBytes), order = 0 },
                new { plugin = "QuestCore.esp", path = "QuestCore.esp", sha256 = Hash(coreBytes), order = 1 }
            },
            placementMode = "quest-alias", marker = "Skyrim.esm|0x0001F88C", sandboxRadius = 512,
            patch = new { optional = true }, output = new { root = "output", archive = "placement.zip" }
        });
        File.WriteAllBytes(Path.Combine(root, "request.json"), requestBytes);
        CheckProviderReplacementLease(root, masterBytes, coreBytes, marker, npcKey);
        SkyrimInteriorPlacementRequest request = SkyrimInteriorPlacementDocumentCodec.ParseRequest(requestBytes, new WorkspacePath(root));
        JsonElement exported = ProtocolV2SchemaService.RenderInline("npc placement interior analyze");
        JsonElement requestSchema = exported.GetProperty("documentSchemas").EnumerateArray()
            .Single(x => x.GetProperty("schemaIdentifier").GetString() == SkyrimInteriorPlacementRequest.SchemaIdentifier).GetProperty("jsonSchema");
        Require(requestSchema.GetProperty("properties").GetProperty("placementMode").GetProperty("const").GetString() == "quest-alias",
            "Command schema export omitted the exact quest-alias mode.");
        byte[] canonical = SkyrimInteriorPlacementDocumentCodec.SerializeRequest(request);
        Require(SkyrimInteriorPlacementDocumentCodec.SerializeRequest(SkyrimInteriorPlacementDocumentCodec.ParseRequest(canonical, new WorkspacePath(root)))
            .AsSpan().SequenceEqual(canonical), "Quest request canonical roundtrip changed its bindings.");
        var service = new SkyrimInteriorPlacementService(new WorkspacePath(root));
        var requestHash = new Sha256Hash(Hash(requestBytes));
        var analyzed = await service.AnalyzeAsync(request, requestHash, new WorkspacePath(Path.Combine(root, "proposal.json")), CancellationToken.None);
        Require(analyzed.Proposed && analyzed.Proposal is not null && analyzed.ProposalSha256 is not null,
            "Quest alias analyze refused: " + string.Join(" | ", analyzed.Diagnostics.Select(x => x.Message)));
        var proposal = SkyrimInteriorPlacementDocumentCodec.ParseProposal(File.ReadAllBytes(Path.Combine(root, "proposal.json")), new WorkspacePath(root));
        var applied = await service.ApplyAsync(request, requestHash, proposal, analyzed.ProposalSha256!.Value, CancellationToken.None);
        Require(applied.Applied && applied.Manifest is not null, "Quest alias apply refused: " + string.Join(" | ", applied.Diagnostics.Select(x => x.Message)));
        var manifestPath = new WorkspacePath(Path.Combine(root, "output", "interior-placement.manifest.json"));
        var verification = await service.VerifyAsync(manifestPath, new Sha256Hash(Hash(File.ReadAllBytes(manifestPath.Value))), CancellationToken.None);
        Require(verification.Verified && verification.Verification is { CellCount: 0, AchrCount: 0, RuntimeAuthority: false },
            "Quest alias verification failed: " + string.Join(" | ", verification.Diagnostics.Select(x => x.Message)));
        string patchPath = Path.Combine(root, applied.Manifest!.PatchPath.Replace('/', Path.DirectorySeparatorChar));
        using var output = SkyrimMod.CreateFromBinaryOverlay(new ModPath(ModKey.FromNameAndExtension(applied.Manifest.PatchPlugin), new FilePath(patchPath)), SkyrimRelease.SkyrimSE);
        IQuestGetter quest = output.Quests.Single();
        Require(output.IsSmallMaster && output.Cells.Count == 0 && output.Worldspaces.Count == 0 && output.Npcs.Count == 0 && output.Packages.Count == 1,
            "Quest alias patch has unexpected records or lost light flag.");
        Require(quest.Flags.HasFlag(Quest.Flag.StartGameEnabled) && quest.VirtualMachineAdapter is null && quest.Aliases.Count == 2,
            "Quest must start game enabled with exactly two script-free aliases.");
        Require(quest.Aliases[0].ID == 0 && quest.Aliases[0].ForcedReference.FormKey == marker && quest.Aliases[1].ID == 1 &&
            quest.Aliases[1].CreateReferenceToObject is { AliasID: 0, Create: CreateReferenceToObject.CreateEnum.At } create && create.Object.FormKey == npcKey,
            "Quest aliases do not force the marker and create the exact core NPC at alias0.");
        IPackageGetter reopenedPackage = output.Packages.Single();
        IPackageDataLocationGetter location = reopenedPackage.Data.Values.OfType<IPackageDataLocationGetter>().Single();
        Require(reopenedPackage.FormKey == packageKey && location.Location?.Radius == 512 &&
            location.Location.Target is ILocationTargetGetter target && target.Link.FormKey == marker,
            "Core sandbox override did not retain its identity and bind NearReference marker/radius.");
        string seqPath = Path.Combine(root, "output", "Seq", Path.GetFileNameWithoutExtension(applied.Manifest.PatchPlugin) + ".seq");
        byte[] seq = File.ReadAllBytes(seqPath);
        Require(seq.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(seq) == ((uint)output.ModHeader.MasterReferences.Count << 24 | quest.FormKey.ID),
            "SEQ must bind the QUST ID through the actual file master ordinal.");
        Require(File.ReadAllBytes(Path.Combine(root, "QuestCore.esp")).AsSpan().SequenceEqual(coreBytes) &&
            File.ReadAllBytes(Path.Combine(root, "Skyrim.esm")).AsSpan().SequenceEqual(masterBytes), "Placement mutated copied sources.");
        output.Dispose();
        await CheckRefusals(root, requestBytes, patchPath, seqPath, manifestPath, service, marker, coreBytes);
        await SkyrimNpcFinishCoreExternalSmpTests.RunQuestAliasPlacementAsync();
        await CheckReviewBoundaries(root, request, proposal, service, marker, File.ReadAllBytes(patchPath), seq);
        CheckMultipleLightMasterEncoding();
        Console.WriteLine("Quest-alias placement transaction passed: " + root);
    }

    private static void CheckProviderReplacementLease(string root, byte[] masterBytes, byte[] coreBytes,
        FormKey marker, FormKey npc)
    {
        string corePath = Path.Combine(root, "QuestCore.esp");
        string replacementPath = Path.Combine(root, "QuestCore.replacement.esp");
        var replacement = SkyrimMod.CreateFromBinary(
            new ModPath(npc.ModKey, new FilePath(corePath)), SkyrimRelease.SkyrimSE);
        replacement.Npcs.Single().EditorID = "UnauthenticatedReplacement";
        Write(replacement, replacementPath);
        Require(Hash(File.ReadAllBytes(replacementPath)) != Hash(coreBytes),
            "Provider replacement fixture did not change the authenticated bytes.");

        bool replacementAttempted = false;
        bool replacementBlocked = false;
        _ = BethesdaSkyrimInteriorPlacementQuestWriter.Bind(
        [
            new(new PluginName("Skyrim.esm"), new WorkspacePath(Path.Combine(root, "Skyrim.esm")), new Sha256Hash(Hash(masterBytes))),
            new(new PluginName("QuestCore.esp"), new WorkspacePath(corePath), new Sha256Hash(Hash(coreBytes)))
        ], new FormReference(new PluginName(npc.ModKey.FileName.String), new FormId(npc.ID)), new Sha256Hash(Hash(coreBytes)),
            new FormReference(new PluginName(marker.ModKey.FileName.String), new FormId(marker.ID)), path =>
        {
            if (!Path.GetFullPath(path.Value).Equals(Path.GetFullPath(corePath), StringComparison.OrdinalIgnoreCase)) return;
            replacementAttempted = true;
            try { File.Move(replacementPath, corePath, overwrite: true); }
            catch (IOException) { replacementBlocked = true; }
            catch (UnauthorizedAccessException) { replacementBlocked = true; }
        });
        Require(replacementAttempted && replacementBlocked,
            "Hash-verified provider path was replaceable before the overlay consumed it.");
    }

    private static void CheckMultipleLightMasterEncoding()
    {
        var cellOwner = new PluginName("CellOwner.esl");
        var core = new PluginName("FinishCore.esl");
        var masters = (ImmutableArray<PluginName>)typeof(SkyrimInteriorPlacementService)
            .GetMethod("BuildMasters", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [cellOwner, core, null])!;
        uint raw = (uint)typeof(SkyrimInteriorPlacementService)
            .GetMethod("EncodeRaw", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(null, [new FormReference(core, new FormId(0x800)), masters])!;
        Require(masters.SequenceEqual([cellOwner, core]) && raw == 0x01000800,
            "Multiple light masters did not retain TES4 master ordinal encoding.");
    }

    private static async Task CheckReviewBoundaries(string root, SkyrimInteriorPlacementRequest request,
        SkyrimInteriorPlacementProposal proposal, SkyrimInteriorPlacementService service, FormKey marker, byte[] plugin, byte[] seq)
    {
        var failures = new List<string>();
        ModKey provider = ModKey.FromNameAndExtension("WinningMarker.esp");
        var mod = new SkyrimMod(provider, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = marker.ModKey });
        var cell = new Cell(new FormKey(marker.ModKey, 0x1485), SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
        cell.Persistent.Add(new PlacedObject(marker, SkyrimRelease.SkyrimSE)
        {
            EditorID = "WinningMarkerOverride", MajorRecordFlagsRaw = (int)PlacedObject.DefaultMajorFlag.Persistent,
            Base = new FormLinkNullable<IPlaceableObjectGetter>(new FormKey(marker.ModKey, 0x900))
        });
        mod.Cells.AddInteriorCell(cell);
        string providerPath = Path.Combine(root, provider.FileName.String);
        Write(mod, providerPath);
        var overridden = request with
        {
            LoadOrder = request.LoadOrder.Add(new() { Plugin = provider.FileName.String, Path = provider.FileName.String,
                Sha256 = Hash(File.ReadAllBytes(providerPath)), Order = request.LoadOrder.Length }),
            Output = new() { Root = "override-output", Archive = "override.zip" }
        };
        var analyzed = await service.AnalyzeAsync(overridden, SkyrimInteriorPlacementDocumentCodec.HashRequest(overridden),
            new WorkspacePath(Path.Combine(root, "override-proposal.json")), CancellationToken.None);
        Require(analyzed.Proposed, "Winning marker fixture refused: " + string.Join(" | ", analyzed.Diagnostics.Select(x => x.Message)));
        if (analyzed.Proposal!.QuestAlias!.MarkerProvider != provider.FileName.String ||
            analyzed.Proposal.MasterOrder[^1] != provider.FileName.String)
            failures.Add("The winning marker provider must be a direct master after its preceding dependencies.");

        // Exercise publication only; format/authority was proven above. A large write holds the real filesystem race window.
        string racedRoot = Path.Combine(root, "raced-output");
        string sentinel = Path.Combine(racedRoot, "concurrent-owner.txt");
        File.WriteAllText(Path.Combine(root, "blocked-archive-parent"), "unrelated parent file");
        var racedRequest = request with { Output = new() { Root = "raced-output", Archive = "blocked-archive-parent/output.zip" } };
        var racedProposal = proposal with { QuestAlias = proposal.QuestAlias! with
            { SeqPath = "raced-output/Seq/" + Path.GetFileNameWithoutExtension(proposal.PatchPlugin) + ".seq" } };
        byte[] slowWrite = new byte[64 * 1024 * 1024];
        plugin.CopyTo(slowWrite, 0);
        using var watcherReady = new ManualResetEventSlim();
        Task competitor = Task.Factory.StartNew(() =>
        {
            watcherReady.Set();
            bool seen = SpinWait.SpinUntil(() => Directory.Exists(racedRoot) ||
                Directory.EnumerateDirectories(root, "raced-output.stage-*").Any(), TimeSpan.FromSeconds(10));
            Require(seen, "Publication race setup did not observe the destination or stage.");
            Directory.CreateDirectory(racedRoot);
            File.WriteAllText(sentinel, "concurrent owner must survive");
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        watcherReady.Wait();
        try
        {
            typeof(SkyrimInteriorPlacementService).GetMethod("ApplyQuestAlias",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.Invoke(service,
                [racedRequest, new Sha256Hash(proposal.RequestSha256), racedProposal, new Sha256Hash(proposal.ProposalSha256!),
                    new QuestAliasPlacementOutput(slowWrite, seq, new Sha256Hash(proposal.QuestAlias!.PackageRecordSha256))]);
            failures.Add("Blocked archive parent unexpectedly admitted publication.");
        }
        catch (System.Reflection.TargetInvocationException exception) when (exception.InnerException is IOException) { }
        await competitor;
        if (!File.Exists(sentinel) || File.ReadAllText(sentinel) != "concurrent owner must survive")
            failures.Add("Failed publication deleted the concurrent owner's destination contents.");
        Require(failures.Count == 0, string.Join(" | ", failures));
        Console.WriteLine("Winning-marker master and concurrent publication ownership controls passed.");
    }

    private static void Write(SkyrimMod mod, string path) => mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
    {
        ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
        MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck
    });

    internal static async Task AssertExternalManifestAsync(string root, string finishPath, SkyrimNpcFinishCoreManifest finish,
        string[] providerPaths)
    {
        byte[] finishBytes = File.ReadAllBytes(finishPath);
        _ = SkyrimNpcFinishCoreDocumentCodec.ParseManifest(finishBytes, new WorkspacePath(root));
        Require(finish.Schema == SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier && finish.ExternalHeadParts is not null,
            "Quest external variation requires a real v2 Finish manifest.");
        ModKey markerProvider = ModKey.FromNameAndExtension("QuestMarker.esm");
        var markerMod = new SkyrimMod(markerProvider, SkyrimRelease.SkyrimSE);
        var markerBase = new FormKey(markerProvider, 0x800);
        markerMod.Statics.Add(new Static(markerBase, SkyrimRelease.SkyrimSE) { EditorID = "SyntheticExternalXMarker" });
        var marker = new FormKey(markerProvider, 0x1F88C);
        var cell = new Cell(new FormKey(markerProvider, 0x1485), SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell };
        cell.Persistent.Add(new PlacedObject(marker, SkyrimRelease.SkyrimSE)
        {
            MajorRecordFlagsRaw = (int)PlacedObject.DefaultMajorFlag.Persistent,
            EditorID = "QuestExternalMarker",
            Base = new FormLinkNullable<IPlaceableObjectGetter>(markerBase)
        });
        markerMod.Cells.AddInteriorCell(cell);
        string markerPath = Path.Combine(root, "QuestMarker.esm");
        Write(markerMod, markerPath);
        var providers = providerPaths.Append(markerPath).Select((path, order) => new SkyrimInteriorPlacementProvider
        {
            Plugin = Path.GetFileName(path), Path = Path.GetRelativePath(root, path).Replace('\\', '/'),
            Sha256 = Hash(File.ReadAllBytes(path)), Order = order
        }).ToImmutableArray();
        var request = new SkyrimInteriorPlacementRequest
        {
            FinishCore = new() { Manifest = Path.GetRelativePath(root, finishPath).Replace('\\', '/'), ManifestSha256 = Hash(finishBytes) },
            LoadOrder = providers, PlacementMode = SkyrimQuestAliasPlacementEvidence.Mode,
            Marker = "QuestMarker.esm|0x0001F88C", SandboxRadius = 512,
            Output = new() { Root = "quest-v2-output", Archive = "quest-v2.zip" }
        };
        var service = new SkyrimInteriorPlacementService(new WorkspacePath(root));
        var requestHash = SkyrimInteriorPlacementDocumentCodec.HashRequest(request);
        var proposal = await service.AnalyzeAsync(request, requestHash, new WorkspacePath(Path.Combine(root, "quest-v2-proposal.json")), CancellationToken.None);
        Require(proposal.Proposed && proposal.Proposal is not null, "Real v2 Finish did not admit quest placement: " + string.Join(" | ", proposal.Diagnostics.Select(x => x.Message)));
        var applied = await service.ApplyAsync(request, requestHash, proposal.Proposal!, proposal.ProposalSha256!.Value, CancellationToken.None);
        Require(applied.Applied, "Real v2 Finish quest apply refused: " + string.Join(" | ", applied.Diagnostics.Select(x => x.Message)));
        var placementManifest = new WorkspacePath(Path.Combine(root, "quest-v2-output", "interior-placement.manifest.json"));
        var verified = await service.VerifyAsync(placementManifest, new Sha256Hash(Hash(File.ReadAllBytes(placementManifest.Value))), CancellationToken.None);
        Require(verified.Verified && !verified.Verification!.RuntimeAuthority,
            "Real v2 Finish quest verify failed: " + string.Join(" | ", verified.Diagnostics.Select(x => x.Message)));
        JsonObject incomplete = JsonNode.Parse(finishBytes)!.AsObject();
        incomplete["externalHeadParts"] = null;
        byte[] incompleteBytes = JsonSerializer.SerializeToUtf8Bytes(incomplete);
        File.WriteAllBytes(Path.Combine(root, "invalid-v2-manifest.json"), incompleteBytes);
        var invalidRequest = request with
        {
            FinishCore = new() { Manifest = "invalid-v2-manifest.json", ManifestSha256 = Hash(incompleteBytes) }
        };
        var refused = await service.AnalyzeAsync(invalidRequest, SkyrimInteriorPlacementDocumentCodec.HashRequest(invalidRequest),
            new WorkspacePath(Path.Combine(root, "invalid-v2-proposal.json")), CancellationToken.None);
        Require(!refused.Proposed && refused.Diagnostics.Any(x => x.Message.Contains("external head-part authority", StringComparison.Ordinal)),
            "Quest placement accepted a v2 manifest with null supplied external authority.");
        Require(File.ReadAllBytes(finishPath).AsSpan().SequenceEqual(finishBytes) && providers.All(provider =>
            new Sha256Hash(Hash(File.ReadAllBytes(Path.Combine(root, provider.Path.Replace('/', Path.DirectorySeparatorChar))))) == new Sha256Hash(provider.Sha256)),
            "Quest v2 variation changed Finish evidence or copied providers.");
        Console.WriteLine("Real product-authored v2 external Finish -> quest-alias analyze/apply/verify passed.");
    }
    private static string Hash(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private static async Task CheckRefusals(string root, byte[] requestBytes, string patchPath, string seqPath,
        WorkspacePath manifestPath, SkyrimInteriorPlacementService service, FormKey marker, byte[] coreBytes)
    {
        JsonObject missing = JsonNode.Parse(requestBytes)!.AsObject();
        missing["marker"] = "Skyrim.esm|0x0000DEAD";
        byte[] missingBytes = JsonSerializer.SerializeToUtf8Bytes(missing);
        var missingRequest = SkyrimInteriorPlacementDocumentCodec.ParseRequest(missingBytes, new WorkspacePath(root));
        var missingResult = await service.AnalyzeAsync(missingRequest, new Sha256Hash(Hash(missingBytes)),
            new WorkspacePath(Path.Combine(root, "missing-marker-proposal.json")), CancellationToken.None);
        Require(!missingResult.Proposed && !File.Exists(Path.Combine(root, "missing-marker-proposal.json")), "Missing marker must refuse before proposal publication.");

        JsonObject mixed = JsonNode.Parse(requestBytes)!.AsObject();
        mixed["cell"] = new JsonObject();
        try
        {
            _ = SkyrimInteriorPlacementDocumentCodec.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(mixed), new WorkspacePath(root));
            throw new InvalidOperationException("Mixed quest/CELL request was accepted.");
        }
        catch (InvalidDataException) { }

        string corePath = Path.Combine(root, "QuestCore.esp");
        var changedCore = SkyrimMod.CreateFromBinary(new ModPath(ModKey.FromNameAndExtension("QuestCore.esp"), new FilePath(corePath)), SkyrimRelease.SkyrimSE);
        changedCore.Npcs.Single().EditorID = "ChangedCoreNpc";
        Write(changedCore, corePath);
        JsonObject changed = JsonNode.Parse(requestBytes)!.AsObject();
        changed["loadOrder"]![1]!["sha256"] = Hash(File.ReadAllBytes(corePath));
        byte[] changedBytes = JsonSerializer.SerializeToUtf8Bytes(changed);
        var changedResult = await service.AnalyzeAsync(SkyrimInteriorPlacementDocumentCodec.ParseRequest(changedBytes, new WorkspacePath(root)),
            new Sha256Hash(Hash(changedBytes)), new WorkspacePath(Path.Combine(root, "changed-core-proposal.json")), CancellationToken.None);
        Require(!changedResult.Proposed && changedResult.Diagnostics.Any(x => x.Message.Contains("Finish Core plugin hash", StringComparison.Ordinal)),
            "Rebound load-order hash must not bypass the retained Finish Core plugin binding.");
        File.WriteAllBytes(corePath, coreBytes);

        byte[] originalPatch = File.ReadAllBytes(patchPath);
        byte[] originalSeq = File.ReadAllBytes(seqPath);
        byte[] originalManifest = File.ReadAllBytes(manifestPath.Value);
        string archivePath = Path.Combine(root, "placement.zip");
        byte[] originalArchive = File.ReadAllBytes(archivePath);
        for (int mutation = 0; mutation < 5; mutation++)
        {
            File.WriteAllBytes(patchPath, originalPatch);
            File.WriteAllBytes(seqPath, originalSeq);
            JsonObject rebound = JsonNode.Parse(originalManifest)!.AsObject();
            if (mutation == 0)
            {
                byte[] wrongSeq = originalSeq.ToArray();
                wrongSeq[0] ^= 1;
                File.WriteAllBytes(seqPath, wrongSeq);
                rebound["questAlias"]!["seqSha256"] = Hash(wrongSeq);
            }
            else
            {
                var patch = SkyrimMod.CreateFromBinary(new ModPath(ModKey.FromNameAndExtension(Path.GetFileName(patchPath)), new FilePath(patchPath)), SkyrimRelease.SkyrimSE);
                if (mutation == 1)
                    patch.Quests.Single().Aliases[0].ForcedReference.SetTo(new FormKey(marker.ModKey, marker.ID + 1));
                else if (mutation == 2)
                    patch.Packages.Single().Data.Values.OfType<PackageDataLocation>().Single().Location!.Radius = 513;
                else if (mutation == 3)
                    patch.Cells.AddInteriorCell(new Cell(new FormKey(patch.ModKey, 0x801), SkyrimRelease.SkyrimSE) { Flags = Cell.Flag.IsInteriorCell });
                else
                    patch.Packages.Single().PackageTemplate.SetTo(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0xDEAD));
                Write(patch, patchPath);
                // Rebind the physical PACK digest too: the verifier must still check its target/radius semantics.
                rebound["questAlias"]!["packageRecordSha256"] = BethesdaRawPluginInventory.Read(patchPath).Single(x => x.Signature == "PACK").Sha256.Value;
            }
            byte[] patchBytes = File.ReadAllBytes(patchPath);
            using (var file = new FileStream(archivePath, FileMode.Create, FileAccess.Write))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
            {
                using (Stream entry = archive.CreateEntry("Data/" + Path.GetFileName(patchPath)).Open()) entry.Write(patchBytes);
                using (Stream entry = archive.CreateEntry("Data/Seq/" + Path.GetFileName(seqPath)).Open()) entry.Write(File.ReadAllBytes(seqPath));
            }
            rebound["patchSha256"] = Hash(patchBytes);
            rebound["archiveSha256"] = Hash(File.ReadAllBytes(archivePath));
            byte[] reboundBytes = JsonSerializer.SerializeToUtf8Bytes(rebound);
            File.WriteAllBytes(manifestPath.Value, reboundBytes);
            var refused = await service.VerifyAsync(manifestPath, new Sha256Hash(Hash(reboundBytes)), CancellationToken.None);
            Require(!refused.Verified, "Quest alias verifier admitted rebound SEQ/alias/PACK/world mutation " + mutation);
        }
        File.WriteAllBytes(patchPath, originalPatch);
        File.WriteAllBytes(seqPath, originalSeq);
        File.WriteAllBytes(manifestPath.Value, originalManifest);
        File.WriteAllBytes(archivePath, originalArchive);
    }
}
