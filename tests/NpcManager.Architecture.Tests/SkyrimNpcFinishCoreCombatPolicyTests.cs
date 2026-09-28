using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestFinishCoreCombatAndPerkPolicy()
    {
        await TestFinishCoreCombatStyleOwnerCollision();
        await TestFinishCoreLocalCombatStyleReuse();
        await using var fixture = await FinishCoreBinaryFixture.CreateAsync();
        var root = new WorkspacePath(fixture.Root);
        var masterKey = ModKey.FromNameAndExtension("Skyrim.esm");
        var styleKey = new FormKey(masterKey, 0x3BE1D);
        SkyrimMod master = SkyrimMod.CreateFromBinary(
            new ModPath(masterKey, new FilePath(fixture.CopiedMaster.Value)), SkyrimRelease.SkyrimSE);
        master.CombatStyles.Add(new CombatStyle(styleKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "SeedStyle", OffensiveMult = 0.8f, DefensiveMult = 0.7f,
            EquipmentScoreMultMelee = 2f, EquipmentScoreMultRanged = 3f,
            EquipmentScoreMultMagic = 4f, EquipmentScoreMultStaff = 5f,
            EquipmentScoreMultShout = 6f, EquipmentScoreMultUnarmed = 7f,
            GroupOffensiveMult = 0.31f, AvoidThreatChance = 0.27f,
            CloseRange = new CombatStyleCloseRange { CircleMult = 0.41f, FallbackMult = 0.61f },
            Melee = new CombatStyleMelee { BashMult = 0.37f }
        });
        WriteCombatFixture(master, fixture.CopiedMaster.Value);
        SkyrimMod source = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(fixture.SourcePlugin.Value)), SkyrimRelease.SkyrimSE);
        source.Npcs.Single().CombatStyle.SetTo(styleKey);
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        Assert(source.CombatStyles.Count == 0, "The seed fixture must have no local CSTY.");

        SkyrimNpcFinishCoreRequest? lastRequest = null;
        foreach (string? profile in new string?[] { null, "defensive", "rangedFirst", "meleeFirst" })
        {
            JsonObject node = JsonNode.Parse(SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                fixture.Proposal.Request!, root))!.AsObject();
            node["schema"] = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier;
            node["source"]!["pluginSha256"] = CombatHash(fixture.SourcePlugin.Value).Value;
            node["sandboxAuthority"]!["copiedMasterSha256"] = CombatHash(fixture.CopiedMaster.Value).Value;
            node["aiPolicy"]!["mood"] = "Angry";
            node["combatPolicy"] = new JsonObject { ["seedLocalStyle"] = true };
            if (profile is not null) node["combatPolicy"]!["profile"] = profile;
            node["perkPolicy"] = new JsonArray(
                new JsonObject { ["form"] = "Skyrim.esm|0x00058F6A", ["rank"] = 2 });
            SkyrimNpcFinishCoreRequest request = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                JsonSerializer.SerializeToUtf8Bytes(node), root);
            lastRequest = request;
            if (profile is null)
                VerifyCombatPolicyWireRefusals(node, root);
            // The ordinary source guard must remain closed without the explicit request policy.
            var oldSnapshot = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                fixture.SourcePlugin, request.Source.Plugin!.Value, request.Actor.FormId!.Value,
                request.Actor.EditorId!.Value, CancellationToken.None);
            Assert(!oldSnapshot.Admitted && oldSnapshot.Diagnostics.Any(d => d.Code == "finish-core-source-csty-count"),
                "Legacy source admission unexpectedly accepts master CSTY without seeding.");
            await VerifyCombatPolicyOutput(fixture, request, profile);
        }
        master.CombatStyles.Single().OffensiveMult = 0;
        WriteCombatFixture(master, fixture.CopiedMaster.Value);
        var zeroOffense = lastRequest! with
        {
            CombatPolicy = lastRequest!.CombatPolicy! with { Profile = SkyrimNpcFinishCoreCombatProfile.Defensive },
            SandboxAuthority = lastRequest.SandboxAuthority with { CopiedMasterSha256 = CombatHash(fixture.CopiedMaster.Value) }
        };
        await VerifyCombatPolicyOutput(fixture, zeroOffense, "zero-offense", 0);
        master.CombatStyles.Single().EquipmentScoreMultRanged = float.NaN;
        WriteCombatFixture(master, fixture.CopiedMaster.Value);
        var nonFinite = zeroOffense with
        {
            SandboxAuthority = zeroOffense.SandboxAuthority with { CopiedMasterSha256 = CombatHash(fixture.CopiedMaster.Value) }
        };
        var refused = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin,
            nonFinite.Source.Plugin!.Value, nonFinite.Actor.FormId!.Value, nonFinite.Actor.EditorId!.Value,
            CancellationToken.None, nonFinite);
        Assert(!refused.Admitted && refused.Diagnostics.Any(d => d.Message.Contains("profile inputs must be finite", StringComparison.Ordinal)),
            "Non-finite master combat multipliers were admitted.");
        await TestFinishCorePerkOnlyChange();
    }

    private static async Task TestFinishCoreLocalCombatStyleReuse()
    {
        await using var fixture = await FinishCoreBinaryFixture.CreateAsync();
        SkyrimMod source = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(fixture.SourcePlugin.Value)), SkyrimRelease.SkyrimSE);
        var styleKey = new FormKey(source.ModKey, 0x810);
        source.CombatStyles.Add(new CombatStyle(styleKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "ExistingLocalStyle", OffensiveMult = 0.8f, DefensiveMult = 0.7f,
            EquipmentScoreMultMelee = 2f, EquipmentScoreMultRanged = 3f,
            EquipmentScoreMultMagic = 4f, EquipmentScoreMultStaff = 5f,
            EquipmentScoreMultShout = 6f, EquipmentScoreMultUnarmed = 7f
        });
        source.CombatStyles.Add(new CombatStyle(
            new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x810), SkyrimRelease.SkyrimSE)
        {
            EditorID = "DistinctMasterOverride", OffensiveMult = 0.2f
        });
        source.Npcs.Single().CombatStyle.SetTo(styleKey);
        source.ModHeader.Stats.NextFormID = 0x811;
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        Assert(BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(File.ReadAllBytes(fixture.SourcePlugin.Value))
                .NonTes4Records.Where(row => row.Signature == "CSTY")
                .Select(row => row.RawFormId).Order().SequenceEqual([0x00000810u, 0x01000810u]),
            "The local-CSTY collision fixture did not contain distinct master and self-owned identities.");
        var request = fixture.Proposal.Request! with
        {
            Schema = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
            Source = fixture.Proposal.Request!.Source with { PluginSha256 = CombatHash(fixture.SourcePlugin.Value) },
            CombatPolicy = new() { SeedLocalStyle = true, Profile = SkyrimNpcFinishCoreCombatProfile.RangedFirst }
        };
        var snapshot = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
            fixture.SourcePlugin, request.Source.Plugin!.Value, request.Actor.FormId!.Value,
            request.Actor.EditorId!.Value, CancellationToken.None, request);
        Assert(snapshot.Admitted, "A valid source-owned local CSTY was refused: " + string.Join(" | ", snapshot.Diagnostics));
        var admitted = new SkyrimNpcFinishCoreSourceReadResult(true, null, request.Source.PackageTreeSha256!.Value,
            snapshot.PluginSha256, snapshot.BaseNpc, snapshot.TargetEditorId, true, "None",
            snapshot.TypedForbiddenCounts, snapshot.RawForbiddenCounts, snapshot.Diagnostics)
        {
            NextFormId = new FormId(snapshot.NextFormId), Tes4Flags = snapshot.Tes4Flags,
            MasterOrder = snapshot.MasterOrder, OccupiedIds = snapshot.OccupiedIds,
            TargetConfigurationFlags = snapshot.TargetConfigurationFlags, FactionRanks = snapshot.FactionRanks,
            CombatStyle = snapshot.CombatStyle, DefaultOutfit = snapshot.DefaultOutfit,
            Inventory = snapshot.Inventory, Relationships = snapshot.Relationships, AiData = snapshot.AiData
        };
        var root = new WorkspacePath(fixture.Root);
        var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService((_, _) => ValueTask.FromResult(admitted), root);
        var analyzed = await service.AnalyzeAsync(request, SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, root),
            new WorkspacePath(Path.Combine(fixture.Root, "local-csty.json")), CancellationToken.None);
        Assert(analyzed.Proposed && analyzed.Proposal is { } proposal &&
            !proposal.NewRecords.Any(row => row.StartsWith("CSTY ", StringComparison.Ordinal)) &&
            proposal.ExistingRecordChanges.Any(row => row.StartsWith("CSTY 0x00000810:", StringComparison.Ordinal)),
            "A source-owned CSTY policy update allocated an orphan replacement record.");
        var outputPath = new WorkspacePath(Path.Combine(fixture.Root, "local-csty.esp"));
        File.WriteAllBytes(outputPath.Value, new BethesdaSkyrimNpcFinishCoreWriter().Write(
            fixture.SourcePlugin, analyzed.Proposal!, fixture.CopiedMaster, CancellationToken.None));
        SkyrimMod output = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(outputPath.Value)), SkyrimRelease.SkyrimSE);
        CombatStyle localStyle = output.CombatStyles.Single(row => row.FormKey == styleKey);
        Assert(output.CombatStyles.Count == 2 &&
            output.Npcs.Single().CombatStyle.FormKey == styleKey &&
            localStyle.EquipmentScoreMultRanged == 8f &&
            output.CombatStyles.Single(row => row.FormKey.ModKey == ModKey.FromNameAndExtension("Skyrim.esm")).OffensiveMult == 0.2f,
            "The existing local CSTY was not updated in place without changing the same-local-ID master override.");
        var verified = new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
            fixture.SourcePlugin, outputPath, analyzed.Proposal!, fixture.CopiedMaster, CancellationToken.None);
        Assert(verified.Verified,
            "Independent verification rejected the reused local CSTY: " + string.Join(" | ", verified.Diagnostics));
        SkyrimNpcFinishCoreRequest unrequested = analyzed.Proposal!.Request! with
        {
            CombatPolicy = null
        };
        SkyrimNpcFinishCoreProposal forged = RehashProposal(analyzed.Proposal with
        {
            Request = unrequested,
            RequestSha256 = SkyrimNpcFinishCoreDocumentCodec.HashRequest(unrequested, root),
            ProposalSha256 = null
        }, root);
        var forgedVerification = new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
            fixture.SourcePlugin, outputPath, forged, fixture.CopiedMaster, CancellationToken.None);
        Assert(!forgedVerification.Verified && forgedVerification.Diagnostics.Any(d =>
                d.Code == "finish-core-verify-record-preservation"),
            "A proposal-only CSTY exemption hid an unrequested source-record mutation.");
    }

    private static async Task VerifyCombatPolicyOutput(
        FinishCoreBinaryFixture fixture, SkyrimNpcFinishCoreRequest request, string? profile, float sourceOffense = 0.8f)
    {
        // Source admission and all bytes below are real; this seam supplies only package authority,
        // which is independently covered by the create-to-finish CLI fixture.
        var snapshot = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
            fixture.SourcePlugin, request.Source.Plugin!.Value, request.Actor.FormId!.Value,
            request.Actor.EditorId!.Value, CancellationToken.None, request);
        Assert(snapshot.Admitted, "Opt-in master CSTY source should be admitted: " +
            string.Join(" | ", snapshot.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var admitted = new SkyrimNpcFinishCoreSourceReadResult(
            true, null, request.Source.PackageTreeSha256!.Value, snapshot.PluginSha256,
            snapshot.BaseNpc, snapshot.TargetEditorId, true, "None",
            snapshot.TypedForbiddenCounts, snapshot.RawForbiddenCounts, snapshot.Diagnostics)
        {
            NextFormId = new FormId(snapshot.NextFormId), Tes4Flags = snapshot.Tes4Flags,
            MasterOrder = snapshot.MasterOrder, OccupiedIds = snapshot.OccupiedIds,
            TargetConfigurationFlags = snapshot.TargetConfigurationFlags, FactionRanks = snapshot.FactionRanks,
            CombatStyle = snapshot.CombatStyle, DefaultOutfit = snapshot.DefaultOutfit,
            Inventory = snapshot.Inventory, Relationships = snapshot.Relationships, AiData = snapshot.AiData
        };
        var root = new WorkspacePath(fixture.Root);
        var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(admitted), root);
        var analyzed = await service.AnalyzeAsync(request,
            SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, root),
            new WorkspacePath(Path.Combine(fixture.Root, $"combat-{profile ?? "clone"}.json")), CancellationToken.None);
        Assert(analyzed.Proposed && analyzed.Proposal?.Status == SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
            "Master CSTY did not produce a writable proposal: " + string.Join(" | ", analyzed.Diagnostics));
        var proposal = analyzed.Proposal!;
        string outputPath = Path.Combine(fixture.Root, $"combat-{profile ?? "clone"}.esp");
        byte[] bytes = new BethesdaSkyrimNpcFinishCoreWriter().Write(
            fixture.SourcePlugin, proposal, fixture.CopiedMaster, CancellationToken.None);
        await File.WriteAllBytesAsync(outputPath, bytes);
        SkyrimMod output = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(outputPath)), SkyrimRelease.SkyrimSE);
        CombatStyle style = output.CombatStyles.Single();
        Assert(style.EditorID == "BrigitteBardotNpcManager_CombatStyle" &&
            output.Npcs.Single().CombatStyle.FormKey == style.FormKey && style.FormKey.ModKey == output.ModKey,
            "CSTY clone identity or NPC ZNAM was not local and exact.");
        Assert(output.Npcs.Single().AIData?.Mood == Mood.Angry,
            "The v4 writer did not apply the reviewed AI mood.");
        Assert(style.OffensiveMult == (request.CombatPolicy!.Profile == SkyrimNpcFinishCoreCombatProfile.Defensive ? sourceOffense / 2 : sourceOffense) &&
            style.DefensiveMult == (request.CombatPolicy.Profile == SkyrimNpcFinishCoreCombatProfile.Defensive ? 1f : 0.7f) &&
            style.EquipmentScoreMultRanged == (request.CombatPolicy.Profile == SkyrimNpcFinishCoreCombatProfile.RangedFirst ? 8f : 3f) &&
            style.EquipmentScoreMultMelee == (request.CombatPolicy.Profile == SkyrimNpcFinishCoreCombatProfile.MeleeFirst ? 8f : 2f) &&
            style.GroupOffensiveMult == 0.31f && style.AvoidThreatChance == 0.27f &&
            style.CloseRange is { CircleMult: 0.41f, FallbackMult: 0.61f } && style.Melee is { BashMult: 0.37f },
            "Profile failed its relative preference or did not deep-copy the remaining source style.");
        Assert(output.Npcs.Single().Perks is { Count: 1 } perks && perks[0].Rank == 2 &&
            perks[0].Perk.FormKey == new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x58F6A),
            "Reviewed perk and rank were not written to the NPC.");
        var verifier = new BethesdaSkyrimNpcFinishCoreVerifier();
        var verified = verifier.Verify(fixture.SourcePlugin, new WorkspacePath(outputPath), proposal,
            fixture.CopiedMaster, CancellationToken.None);
        Assert(verified.Verified, "Independent combat/perk verification failed: " + string.Join(" | ", verified.Diagnostics));
        output.Npcs.Single().Perks![0].Rank = 1;
        WriteCombatFixture(output, outputPath);
        var tampered = verifier.Verify(fixture.SourcePlugin, new WorkspacePath(outputPath), proposal,
            fixture.CopiedMaster, CancellationToken.None);
        Assert(!tampered.Verified && tampered.Diagnostics.Any(d => d.Code == "finish-core-verify-perks"),
            "Independent verifier accepted a changed perk rank.");
        output.Npcs.Single().Perks![0].Rank = 2;
        style.Melee!.BashMult = 0.99f;
        WriteCombatFixture(output, outputPath);
        tampered = verifier.Verify(fixture.SourcePlugin, new WorkspacePath(outputPath), proposal,
            fixture.CopiedMaster, CancellationToken.None);
        Assert(!tampered.Verified && tampered.Diagnostics.Any(d => d.Code == "finish-core-verify-csty"),
            "Independent verifier accepted an altered cloned combat field.");
        Assert(tampered.Diagnostics.Any(d => d.Code == "finish-core-verify-csty" &&
                d.Message.Contains("reviewed combat", StringComparison.Ordinal) &&
                !d.Message.Contains("closed defensive contract", StringComparison.Ordinal)),
            "Combat-policy verification reported the legacy defensive contract instead of the reviewed clone/profile.");
    }

    private static async Task TestFinishCoreCombatStyleOwnerCollision()
    {
        await using var fixture = await FinishCoreBinaryFixture.CreateAsync();
        var providerKey = ModKey.FromNameAndExtension("CombatProvider.esp");
        var masterKey = ModKey.FromNameAndExtension("Skyrim.esm");
        var styleKey = new FormKey(providerKey, 0x800);
        var providerPath = new WorkspacePath(Path.Combine(fixture.Root, providerKey.ToString()));
        var provider = new SkyrimMod(providerKey, SkyrimRelease.SkyrimSE);
        provider.ModHeader.MasterReferences.Add(new MasterReference { Master = masterKey });
        provider.CombatStyles.Add(new CombatStyle(styleKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "ProviderOwnedStyle", OffensiveMult = 0.8f
        });
        provider.CombatStyles.Add(new CombatStyle(new FormKey(masterKey, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "DistinctMasterOverride", OffensiveMult = 0.2f
        });
        WriteCombatFixture(provider, providerPath.Value);
        var source = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(fixture.SourcePlugin.Value)), SkyrimRelease.SkyrimSE);
        source.ModHeader.MasterReferences.Add(new MasterReference { Master = providerKey });
        source.Npcs.Single().CombatStyle.SetTo(styleKey);
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        var request = fixture.Proposal.Request! with
        {
            Schema = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
            CombatPolicy = new() { SeedLocalStyle = true },
            Source = fixture.Proposal.Request!.Source with { PluginSha256 = CombatHash(fixture.SourcePlugin.Value) },
            Authorities = fixture.Proposal.Request.Authorities with
            {
                AdditionalMasters = [new(new PluginName(providerKey.ToString()), providerPath,
                    CombatHash(providerPath.Value), new FileInfo(providerPath.Value).Length, 1)]
            }
        };
        var census = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(File.ReadAllBytes(providerPath.Value));
        Assert(census.NonTes4Records.Where(row => row.Signature == "CSTY")
                .Select(row => row.RawFormId).Order().SequenceEqual([0x00000800u, 0x01000800u]),
            "Owner-collision fixture did not contain two distinct complete CSTY identities.");
        var admitted = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin,
            request.Source.Plugin!.Value, request.Actor.FormId!.Value, request.Actor.EditorId!.Value,
            CancellationToken.None, request);
        Assert(admitted.Admitted,
            "Distinct master/provider CSTY identities sharing local ID 0x800 were falsely refused: " +
            string.Join(" | ", admitted.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert(BethesdaSkyrimNpcFinishCoreCombatStyle.ReadTemplate(source, source.Npcs.Single(), request)
                is { FormKey: var clonedKey, OffensiveMult: 0.8f } && clonedKey == styleKey,
            "The owner-collision route selected the master override instead of the provider-owned CSTY.");

        AppendDuplicateRecord(providerPath.Value, "CSTY", 0x800, rawFormId: 0x01000800);
        var duplicateRequest = request with
        {
            Authorities = request.Authorities with
            {
                AdditionalMasters = [request.Authorities.AdditionalMasters.Single() with
                {
                    Sha256 = CombatHash(providerPath.Value), ByteLength = new FileInfo(providerPath.Value).Length
                }]
            }
        };
        var duplicate = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin,
            request.Source.Plugin!.Value, request.Actor.FormId!.Value, request.Actor.EditorId!.Value,
            CancellationToken.None, duplicateRequest);
        Assert(!duplicate.Admitted && duplicate.Diagnostics.Any(d =>
                d.Message.Contains("finish-core-combat-source-count", StringComparison.Ordinal)),
            "A genuinely repeated provider-owned CSTY identity was admitted.");
    }

    private static Sha256Hash CombatHash(string path) => new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static void VerifyCombatPolicyWireRefusals(JsonObject valid, WorkspacePath root)
    {
        var legacyExternal = valid.DeepClone().AsObject();
        legacyExternal["schema"] = SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier;
        bool missingExternalAuthorityRefused = false;
        try { _ = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(legacyExternal), root); }
        catch (InvalidDataException) { missingExternalAuthorityRefused = true; }
        Assert(missingExternalAuthorityRefused,
            "The v3 external-headpart schema silently admitted a policy-only request without external authority.");
        foreach (Action<JsonObject> change in new Action<JsonObject>[]
        {
            node => node["schema"] = SkyrimNpcFinishCoreRequest.SchemaIdentifier,
            node => node["combatPolicy"]!["profile"] = "aggressive",
            node => node["combatPolicy"]!["unexpected"] = true,
            node => node["perkPolicy"]![0]!["rank"] = 0,
            node => node["perkPolicy"]![0]!["rank"] = 256,
            node => node["perkPolicy"]!.AsArray().Add(node["perkPolicy"]![0]!.DeepClone()),
            node => node["authorities"]!["externalHeadParts"] = new JsonObject(),
            node => node["authorities"]!["externalHeadParts"] = null
        })
        {
            var invalid = valid.DeepClone().AsObject();
            change(invalid);
            bool refused = false;
            try { _ = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(invalid), root); }
            catch (InvalidDataException) { refused = true; }
            Assert(refused, "Malformed policy or partial external authority was accepted: " + invalid);
        }
        var clear = valid.DeepClone().AsObject();
        clear["perkPolicy"] = new JsonArray();
        var parsed = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(JsonSerializer.SerializeToUtf8Bytes(clear), root);
        Assert(!parsed.PerkPolicy.IsDefault && parsed.PerkPolicy.IsEmpty, "Explicit empty perks lost clear semantics.");
    }

    private static async Task TestFinishCorePerkOnlyChange()
    {
        await using var fixture = await FinishCoreBinaryFixture.CreateAsync();
        var writer = new BethesdaSkyrimNpcFinishCoreWriter();
        string finishedDirectory = Path.Combine(fixture.Root, "finished");
        Directory.CreateDirectory(finishedDirectory);
        var finished = new WorkspacePath(Path.Combine(finishedDirectory, fixture.Proposal.Request!.Source.Plugin!.Value.Value));
        File.WriteAllBytes(finished.Value, writer.Write(fixture.SourcePlugin, fixture.Proposal, fixture.CopiedMaster, CancellationToken.None));
        var snapshot = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(finished,
            fixture.Proposal.Request.Source.Plugin.Value, fixture.Proposal.Request.Actor.FormId!.Value,
            fixture.Proposal.Request.Actor.EditorId!.Value, CancellationToken.None);
        Assert(snapshot.Admitted && snapshot.CombatStyleMatchesDefensiveContract && snapshot.PackageMatchesFinishCoreContract,
            "Perk-only fixture did not start from a completed defensive output.");
        var request = fixture.Proposal.Request with
        {
            Schema = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
            Source = fixture.Proposal.Request.Source with { PluginPath = finished, PluginSha256 = CombatHash(finished.Value) },
            OutfitPolicy = new() { Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit, ExistingOutfit = snapshot.DefaultOutfit },
            PerkPolicy = [new(new FormReference(new PluginName("Skyrim.esm"), new FormId(0x58F6A)), 2)]
        };
        var admitted = new SkyrimNpcFinishCoreSourceReadResult(true, null, request.Source.PackageTreeSha256!.Value,
            snapshot.PluginSha256, snapshot.BaseNpc, snapshot.TargetEditorId, true, "None",
            snapshot.TypedForbiddenCounts, snapshot.RawForbiddenCounts, snapshot.Diagnostics)
        {
            NextFormId = new FormId(snapshot.NextFormId), Tes4Flags = snapshot.Tes4Flags,
            MasterOrder = snapshot.MasterOrder, OccupiedIds = snapshot.OccupiedIds,
            TargetConfigurationFlags = snapshot.TargetConfigurationFlags, FactionRanks = snapshot.FactionRanks,
            CombatStyle = snapshot.CombatStyle, CombatStyleMatchesDefensiveContract = true,
            DefaultOutfit = snapshot.DefaultOutfit, Relationships = snapshot.Relationships,
            Inventory = snapshot.Inventory, AiData = snapshot.AiData, PackageLinks = snapshot.PackageLinks,
            SemanticSurfaceValues = ["FINISH_CORE_COMPLETE"], Perks = snapshot.Perks
        };
        var root = new WorkspacePath(fixture.Root);
        var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService((_, _) => ValueTask.FromResult(admitted), root);
        var analyzed = await service.AnalyzeAsync(request, SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, root),
            new WorkspacePath(Path.Combine(fixture.Root, "perk-only.json")), CancellationToken.None);
        Assert(analyzed.Proposed && analyzed.Proposal is { NewRecords.Length: 0, Status: SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite },
            "Perk-only update should not allocate records or be dropped as NoChanges: " + string.Join(" | ", analyzed.Diagnostics));
        var output = new WorkspacePath(Path.Combine(fixture.Root, "perk-only.esp"));
        File.WriteAllBytes(output.Value, writer.Write(finished, analyzed.Proposal!, fixture.CopiedMaster, CancellationToken.None));
        var verified = new BethesdaSkyrimNpcFinishCoreVerifier().Verify(finished, output, analyzed.Proposal!, fixture.CopiedMaster, CancellationToken.None);
        Assert(verified.Verified, "Perk-only change without new records did not verify: " + string.Join(" | ", verified.Diagnostics));
    }

    private static void WriteCombatFixture(SkyrimMod mod, string path) => mod.WriteToBinary(new FilePath(path),
        new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck });
}
