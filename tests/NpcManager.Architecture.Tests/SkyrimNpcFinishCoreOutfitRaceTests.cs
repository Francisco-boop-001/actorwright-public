using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
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
    private static async Task TestFinishCoreOutfitRace()
    {
        await using var fixture = await FinishCoreBinaryFixture.CreateAsync();
        var packageMasterRequest = fixture.Proposal.Request! with
        {
            SandboxAuthority = fixture.Proposal.Request!.SandboxAuthority with
            {
                CopiedMaster = null,
                CopiedMasterSha256 = null
            }
        };
        var packageMaster = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
            fixture.SourcePlugin, packageMasterRequest.Source.Plugin!.Value,
            packageMasterRequest.Actor.FormId!.Value, packageMasterRequest.Actor.EditorId!.Value,
            CancellationToken.None, packageMasterRequest);
        Assert(packageMaster.Admitted,
            "A source master already hash-bound inside the source package required a redundant provider binding: " +
            string.Join(" | ", packageMaster.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        var source = SkyrimMod.CreateFromBinary(new ModPath(fixture.PluginKey,
            new FilePath(fixture.SourcePlugin.Value)), SkyrimRelease.SkyrimSE);
        var master = SkyrimMod.CreateFromBinary(new ModPath(ModKey.FromNameAndExtension("Skyrim.esm"),
            new FilePath(fixture.CopiedMaster.Value)), SkyrimRelease.SkyrimSE);
        var raceKey = new FormKey(ModKey.FromNameAndExtension("UBE_AllRace.esp"), 0x5A184);
        var raceMod = new SkyrimMod(raceKey.ModKey, SkyrimRelease.SkyrimSE);
        raceMod.Races.AddNew(raceKey).EditorID = "SyntheticUBE";
        string racePath = Path.Combine(fixture.Root, raceKey.ModKey.ToString());
        WriteCombatFixture(raceMod, racePath);
        source.ModHeader.MasterReferences.Add(new MasterReference { Master = raceKey.ModKey });
        source.Npcs.Single().Race.SetTo(raceKey);
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        var armature = master.ArmorAddons.AddNew(new FormKey(master.ModKey, 0x902));
        armature.EditorID = "VanillaOnlyArmature";
        armature.Race.SetTo(new FormKey(master.ModKey, 0x13746));
        var armor = master.Armors.AddNew(new FormKey(master.ModKey, 0x900));
        armor.EditorID = "ArmorLeather";
        armor.Armature.Add(armature.FormKey);
        var leveled = master.LeveledItems.AddNew(new FormKey(master.ModKey, 0x903));
        leveled.EditorID = "ArmorLeatherVariants";
        leveled.Entries = [new LeveledItemEntry
        {
            Data = new LeveledItemEntryData
            {
                Level = 1,
                Count = 1,
                Reference = new FormLink<IItemGetter>(armor.FormKey)
            }
        }];
        var outfit = master.Outfits.AddNew(new FormKey(master.ModKey, 0x901));
        outfit.EditorID = "ArmorLeatherNoHelmetOutfit";
        outfit.Items = [new FormLink<IOutfitTargetGetter>(armor.FormKey),
            new FormLink<IOutfitTargetGetter>(leveled.FormKey)];
        WriteCombatFixture(master, fixture.CopiedMaster.Value);
        source.Npcs.Single().DefaultOutfit.SetTo(outfit.FormKey);
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        var request = fixture.Proposal.Request! with
        {
            OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
            {
                Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit,
                ExistingOutfit = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x901))
            },
            SandboxAuthority = fixture.Proposal.Request!.SandboxAuthority with
            { CopiedMasterSha256 = OutfitHash(fixture.CopiedMaster.Value) },
            Authorities = fixture.Proposal.Request!.Authorities with
            {
                Providers = [new SkyrimNpcFinishCoreProviderAuthority
                {
                    Plugin = new PluginName(raceKey.ModKey.ToString()), Path = new WorkspacePath(racePath),
                    Sha256 = OutfitHash(racePath), ByteLength = new FileInfo(racePath).Length
                }]
            }
        };
        var inheritedOutfit = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin,
            request.Source.Plugin!.Value, request.Actor.FormId!.Value, request.Actor.EditorId!.Value,
            CancellationToken.None, request with
            {
                OutfitPolicy = request.OutfitPolicy with { ExistingOutfit = null },
                OutfitRacePolicy = SkyrimNpcFinishCoreOutfitRacePolicy.Clone
            });
        Assert(inheritedOutfit.Admitted && inheritedOutfit.OutfitArmaturesToClone.Length == 1,
            "An omitted existingOutfit must preserve and inspect the source NPC default outfit, including LVLI members: " +
            string.Join(" | ", inheritedOutfit.Diagnostics));
        var snapshot = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin,
            request.Source.Plugin!.Value, request.Actor.FormId!.Value, request.Actor.EditorId!.Value,
            CancellationToken.None, request);
        Assert(!snapshot.Admitted && snapshot.Diagnostics.Any(d =>
                d.Code == "finish-core-outfit-armature-race-excluded" &&
                d.Message.Contains("Skyrim.esm|0x00000902", StringComparison.Ordinal) &&
                d.Message.Contains("UBE_AllRace.esp|0x0005A184", StringComparison.Ordinal)),
            "Vanilla-only outfit armature must refuse the UBE actor race with exact identities; observed: " +
            string.Join(" | ", snapshot.Diagnostics));
        request = VerifySourceOutfitOverrides(fixture, source, master, armature, request, raceKey);
        var root = new WorkspacePath(fixture.Root);
        byte[] legacyBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, root);
        Assert(!System.Text.Encoding.UTF8.GetString(legacyBytes).Contains("outfitRacePolicy", StringComparison.Ordinal),
            "Omitted policy must preserve old request wire shape.");
        var cloneRequest = request with
        {
            OutfitRacePolicy = SkyrimNpcFinishCoreOutfitRacePolicy.Clone,
            Source = request.Source with { PluginSha256 = OutfitHash(fixture.SourcePlugin.Value) },
            Authorities = request.Authorities with
            { Providers = [request.Authorities.Providers[0] with { Plugin = new PluginName("ube_allrace.ESP") }] }
        };
        cloneRequest = SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(cloneRequest, root), root);
        Assert(cloneRequest.OutfitRacePolicy == SkyrimNpcFinishCoreOutfitRacePolicy.Clone,
            "Lowercase clone policy failed its published request round trip.");
        var cloneSnapshot = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin,
            cloneRequest.Source.Plugin!.Value, cloneRequest.Actor.FormId!.Value, cloneRequest.Actor.EditorId!.Value,
            CancellationToken.None, cloneRequest);
        Assert(cloneSnapshot.Admitted && cloneSnapshot.OutfitArmaturesToClone.Length == 1 &&
            cloneSnapshot.OutfitArmorsToClone.Length == 1, "Explicit clone failed source admission: " +
            string.Join(" | ", cloneSnapshot.Diagnostics));
        var admitted = new SkyrimNpcFinishCoreSourceReadResult(true, null, request.Source.PackageTreeSha256!.Value,
            cloneSnapshot.PluginSha256, cloneSnapshot.BaseNpc, cloneSnapshot.TargetEditorId, true, "None",
            cloneSnapshot.TypedForbiddenCounts, cloneSnapshot.RawForbiddenCounts, cloneSnapshot.Diagnostics)
        {
            NextFormId = new FormId(cloneSnapshot.NextFormId), Tes4Flags = cloneSnapshot.Tes4Flags,
            MasterOrder = cloneSnapshot.MasterOrder, OccupiedIds = cloneSnapshot.OccupiedIds,
            TargetConfigurationFlags = cloneSnapshot.TargetConfigurationFlags, FactionRanks = cloneSnapshot.FactionRanks,
            CombatStyle = cloneSnapshot.CombatStyle, DefaultOutfit = cloneSnapshot.DefaultOutfit,
            Inventory = cloneSnapshot.Inventory, Relationships = cloneSnapshot.Relationships, AiData = cloneSnapshot.AiData,
            OutfitArmaturesToClone = cloneSnapshot.OutfitArmaturesToClone, OutfitArmorsToClone = cloneSnapshot.OutfitArmorsToClone
        };
        var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService((_, _) => ValueTask.FromResult(admitted), root);
        var analyzed = await service.AnalyzeAsync(cloneRequest, SkyrimNpcFinishCoreDocumentCodec.HashRequest(cloneRequest, root),
            new WorkspacePath(Path.Combine(fixture.Root, "outfit-clone-proposal.json")), CancellationToken.None);
        Assert(analyzed.Proposed && analyzed.Proposal?.Status == SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
            "Clone policy did not produce a reviewed write: " + string.Join(" | ", analyzed.Diagnostics));
        var proposal = analyzed.Proposal!;
        string outputPath = Path.Combine(fixture.Root, "outfit-clone.esp");
        Sha256Hash sourceHash = OutfitHash(fixture.SourcePlugin.Value), masterHash = OutfitHash(fixture.CopiedMaster.Value);
        byte[] outputBytes = new BethesdaSkyrimNpcFinishCoreWriter().Write(fixture.SourcePlugin, proposal,
            fixture.CopiedMaster, CancellationToken.None);
        File.WriteAllBytes(outputPath, outputBytes);
        var output = SkyrimMod.CreateFromBinary(new ModPath(fixture.PluginKey, new FilePath(outputPath)), SkyrimRelease.SkyrimSE);
        var localArmor = output.Armors.Single();
        var localArmature = output.ArmorAddons.Single();
        Assert(output.Npcs.Single().DefaultOutfit.FormKey == output.Outfits.Single().FormKey &&
            output.Outfits.Single().Items!.Select(row => row.FormKey).SequenceEqual([localArmor.FormKey, leveled.FormKey]) &&
            localArmor.Armature.Single().FormKey == localArmature.FormKey &&
            localArmature.FormKey.ModKey == output.ModKey && localArmor.FormKey.ModKey == output.ModKey &&
            localArmature.Race.FormKey == armature.Race.FormKey && localArmature.AdditionalRaces.Single().FormKey == raceKey,
            "Reopened outfit must reach the local ARMO and local ARMA retaining its primary race and appending UBE.");
        var verifier = new BethesdaSkyrimNpcFinishCoreVerifier();
        var verified = verifier.Verify(fixture.SourcePlugin, new WorkspacePath(outputPath), proposal, fixture.CopiedMaster, CancellationToken.None);
        Assert(verified.Verified, "Independent outfit clone verification failed: " + string.Join(" | ", verified.Diagnostics));
        localArmature.AdditionalRaces.Clear();
        WriteCombatFixture(output, outputPath);
        var tampered = verifier.Verify(fixture.SourcePlugin, new WorkspacePath(outputPath), proposal, fixture.CopiedMaster, CancellationToken.None);
        Assert(!tampered.Verified && tampered.Diagnostics.Any(d => d.Code == "finish-core-verify-outfit-armature"),
            "Verifier accepted a cloned ARMA after removing the actor race.");
        localArmature.AdditionalRaces.Add(raceKey);
        localArmor.Armature[0] = new FormLink<IArmorAddonGetter>(armature.FormKey);
        WriteCombatFixture(output, outputPath);
        var wrongLink = verifier.Verify(fixture.SourcePlugin, new WorkspacePath(outputPath), proposal, fixture.CopiedMaster, CancellationToken.None);
        Assert(!wrongLink.Verified && wrongLink.Diagnostics.Any(d => d.Code == "finish-core-verify-outfit-armor"),
            "Verifier accepted an armor link retargeted to the original excluded armature.");
        Assert(sourceHash == OutfitHash(fixture.SourcePlugin.Value) && masterHash == OutfitHash(fixture.CopiedMaster.Value),
            "Clone composition changed source/provider bytes.");

        File.Copy(racePath, Path.Combine(Path.GetDirectoryName(fixture.CopiedMaster.Value)!, raceKey.ModKey.ToString()));
        var outfitService = new OutfitProposalService(new BethesdaPluginReader(),
            new KOnlyWorkspacePolicy(root, new WorkspacePath(root.Value + "-look-only")), root);
        var outfitRequest = new OutfitProposalRequest(GameEdition.SkyrimSpecialEdition, fixture.CopiedMaster,
            new FormId(0x901), OutfitProposalMode.Override, null,
            [new FormReference(new PluginName("Skyrim.esm"), new FormId(0x900))],
            new WorkspacePath(Path.Combine(fixture.Root, "legacy.outfit-proposal.json")));
        Assert((await outfitService.ProposeAsync(outfitRequest, CancellationToken.None)).Written,
            "Legacy outfit proposal without actor race regressed.");
        var excludedProposal = await outfitService.ProposeAsync(outfitRequest with
        {
            ActorRace = new FormReference(new PluginName(raceKey.ModKey.ToString()), new FormId(raceKey.ID)),
            OutputProposal = new WorkspacePath(Path.Combine(fixture.Root, "excluded.outfit-proposal.json"))
        }, CancellationToken.None);
        Assert(!excludedProposal.Written && excludedProposal.Diagnostics.Any(d => d.Code == "finish-core-outfit-armature-race-excluded"),
            "Outfit propose did not enforce supplied actor race: " + string.Join(" | ", excludedProposal.Diagnostics));

        if (!master.ModHeader.MasterReferences.Any(row => row.Master == raceKey.ModKey))
            master.ModHeader.MasterReferences.Add(new MasterReference { Master = raceKey.ModKey });
        armature.AdditionalRaces.Add(raceKey);
        WriteCombatFixture(master, fixture.CopiedMaster.Value);
        request = request with { SandboxAuthority = request.SandboxAuthority with { CopiedMasterSha256 = OutfitHash(fixture.CopiedMaster.Value) } };
        Assert(BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin, request.Source.Plugin!.Value,
            request.Actor.FormId!.Value, request.Actor.EditorId!.Value, CancellationToken.None, request).Admitted,
            "Additional-race admission control failed.");
        armature.AdditionalRaces.Clear();
        armature.Race.SetTo(raceKey);
        WriteCombatFixture(master, fixture.CopiedMaster.Value);
        Assert(!BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin, request.Source.Plugin!.Value,
            request.Actor.FormId!.Value, request.Actor.EditorId!.Value, CancellationToken.None, request).Admitted,
            "Changed copied outfit provider was admitted against stale hash.");
        request = request with { SandboxAuthority = request.SandboxAuthority with { CopiedMasterSha256 = OutfitHash(fixture.CopiedMaster.Value) } };
        Assert(BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin, request.Source.Plugin!.Value,
            request.Actor.FormId!.Value, request.Actor.EditorId!.Value, CancellationToken.None, request).Admitted,
            "Primary-race admission control failed.");
        var malformed = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin, request.Source.Plugin!.Value,
            request.Actor.FormId!.Value, request.Actor.EditorId!.Value, CancellationToken.None, request with
            {
                OutfitPolicy = new() { Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit,
                    ArmorItems = [new FormReference(new PluginName("Skyrim.esm"), new FormId(0x01000900))] }
            });
        Assert(!malformed.Admitted && malformed.Diagnostics.Any(d => d.Message.Contains("24-bit", StringComparison.Ordinal)),
            "Upper owner bits in an armor FormReference aliased a valid local ARMO.");
        Console.WriteLine("PASS Finish Core outfit race admission");
    }

    private static Sha256Hash OutfitHash(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static SkyrimNpcFinishCoreRequest VerifySourceOutfitOverrides(FinishCoreBinaryFixture fixture,
        SkyrimMod source, SkyrimMod master, ArmorAddon armature, SkyrimNpcFinishCoreRequest request, FormKey race)
    {
        SkyrimNpcFinishCorePluginSnapshot Inspect(SkyrimNpcFinishCoreRequest current) =>
            BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin, current.Source.Plugin!.Value,
                current.Actor.FormId!.Value, current.Actor.EditorId!.Value, CancellationToken.None, current);
        var privateArmature = new ArmorAddon(new FormKey(source.ModKey, 0x803), SkyrimRelease.SkyrimSE);
        privateArmature.Race.SetTo(race);
        source.ArmorAddons.Add(privateArmature);
        var armorOverride = (Armor)master.Armors.Single(row => row.FormKey.ID == 0x900).DeepCopy();
        armorOverride.Armature.Clear();
        armorOverride.Armature.Add(privateArmature.FormKey);
        source.Armors.Add(armorOverride);
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        Assert(Inspect(request).Admitted, "Source ARMO override links must win over the owner's excluded armature links.");
        source.Armors.Clear();
        source.ArmorAddons.Clear();
        var winning = (ArmorAddon)armature.DeepCopy();
        winning.AdditionalRaces.Add(race);
        source.ArmorAddons.Add(winning);
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        Assert(Inspect(request).Admitted, "An admitting source ARMA override must win over the excluding owner record.");

        master.ModHeader.MasterReferences.Add(new MasterReference { Master = race.ModKey });
        armature.AdditionalRaces.Add(race);
        WriteCombatFixture(master, fixture.CopiedMaster.Value);
        request = request with { SandboxAuthority = request.SandboxAuthority with { CopiedMasterSha256 = OutfitHash(fixture.CopiedMaster.Value) } };
        winning.AdditionalRaces.Clear();
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        Assert(Inspect(request).Diagnostics.Any(d => d.Code == "finish-core-outfit-armature-race-excluded"),
            "An excluding source ARMA override must win over the admitting owner record.");
        winning.IsDeleted = true;
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        Assert(!Inspect(request).Admitted, "A deleted source ARMA override must not fall back to the owner record.");
        source.ArmorAddons.Clear();
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        armature.AdditionalRaces.Clear();
        master.ModHeader.MasterReferences.Remove(
            master.ModHeader.MasterReferences.Single(row => row.Master == race.ModKey));
        WriteCombatFixture(master, fixture.CopiedMaster.Value);
        request = request with { SandboxAuthority = request.SandboxAuthority with { CopiedMasterSha256 = OutfitHash(fixture.CopiedMaster.Value) } };

        var competing = new SkyrimMod(ModKey.FromNameAndExtension("Competing.esp"), SkyrimRelease.SkyrimSE);
        competing.ModHeader.MasterReferences.Add(new MasterReference { Master = master.ModKey });
        competing.ModHeader.MasterReferences.Add(new MasterReference { Master = race.ModKey });
        winning.IsDeleted = false;
        winning.AdditionalRaces.Add(race);
        competing.ArmorAddons.Add(winning);
        string path = Path.Combine(fixture.Root, "Competing.esp");
        WriteCombatFixture(competing, path);
        var competingRequest = request with { Authorities = request.Authorities with
        {
            Providers = request.Authorities.Providers.Add(new SkyrimNpcFinishCoreProviderAuthority
            {
                Plugin = new PluginName("Competing.esp"), Path = new WorkspacePath(path),
                Sha256 = OutfitHash(path), ByteLength = new FileInfo(path).Length
            })
        } };
        var ambiguous = Inspect(competingRequest);
        Assert(!ambiguous.Admitted && ambiguous.Diagnostics.Any(d => d.Message.Contains("precedence", StringComparison.Ordinal)),
            "A copied ARMA override without declared load precedence must refuse ambiguity.");
        var plannedOrder = source.ModHeader.MasterReferences.Select(row => row.Master.ToString()).ToImmutableArray();
        var unused = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin, request.Source.Plugin!.Value,
            request.Actor.FormId!.Value, request.Actor.EditorId!.Value, CancellationToken.None, competingRequest, plannedOrder);
        Assert(unused.Diagnostics.Any(d => d.Code == "finish-core-outfit-armature-race-excluded"),
            "A copied provider outside the admitted master plan must not become a winning override or ambiguity.");
        var loaded = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(fixture.SourcePlugin, request.Source.Plugin!.Value,
            request.Actor.FormId!.Value, request.Actor.EditorId!.Value, CancellationToken.None, competingRequest,
            plannedOrder.Add(competing.ModKey.ToString()));
        Assert(loaded.Admitted, "An appended provider in the admitted master plan must win by its declared precedence.");
        source.ModHeader.MasterReferences.Add(new MasterReference { Master = competing.ModKey });
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        Assert(Inspect(competingRequest).Admitted, "Declared source master order must choose the later admitting copied override.");
        source.ModHeader.MasterReferences.RemoveAt(source.ModHeader.MasterReferences.Count - 1);
        WriteCombatFixture(source, fixture.SourcePlugin.Value);
        var noRaceProvider = request with { Authorities = request.Authorities with { Providers = [] } };
        var identityOnly = Inspect(noRaceProvider);
        Assert(identityOnly.Diagnostics.Any(d => d.Code == "finish-core-outfit-armature-race-excluded"),
            "Race admission must compare the full race identity without requiring unrelated RACE record fields.");
        return request;
    }
}
