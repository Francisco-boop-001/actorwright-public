using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimNpcFinishCoreBinary()
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        await TestFinishCoreMasterReindex();
        await TestFinishCoreCombatAndPerkPolicy();
        await using var fixture = await FinishCoreBinaryFixture.CreateAsync();
        var writer = new BethesdaSkyrimNpcFinishCoreWriter();
        byte[] changedMaster = await File.ReadAllBytesAsync(
            fixture.CopiedMaster.Value);
        changedMaster[^1] ^= 0x01;
        string changedMasterPath = PerRowMasterPath(
            fixture.Root, "changed-master-hash");
        await File.WriteAllBytesAsync(changedMasterPath, changedMaster);
        SkyrimNpcFinishCoreRequest changedMasterRequest =
            fixture.Proposal.Request! with
            {
                SandboxAuthority = fixture.Proposal.Request.SandboxAuthority with
                {
                    CopiedMaster = new WorkspacePath(changedMasterPath)
                }
            };
        SkyrimNpcFinishCoreProposal changedMasterProposal = RehashProposal(
            fixture.Proposal with
            {
                Request = changedMasterRequest,
                RequestSha256 = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                    changedMasterRequest,
                    new WorkspacePath(fixture.Root)),
                ProposalSha256 = null
            },
            new WorkspacePath(fixture.Root));
        try
        {
            _ = writer.Write(
                fixture.SourcePlugin,
                changedMasterProposal,
                new WorkspacePath(changedMasterPath),
                CancellationToken.None);
            throw new InvalidOperationException(
                "Finish Core writer accepted copied-master bytes outside the request hash binding.");
        }
        catch (InvalidDataException exception)
        {
            Assert(
                exception.Message.Contains(
                    "follower-finish-core-template-file-hash",
                    StringComparison.Ordinal),
                "Copied-master hash refusal lost its stable diagnostic identity: " +
                exception.Message);
        }
        byte[] first = writer.Write(
            fixture.SourcePlugin,
            fixture.Proposal,
            fixture.CopiedMaster,
            CancellationToken.None);
        ImmutableArray<RawRecord> validMajorRecords = ReadRecords(first);
        int validTopLevelGroupCount = CountTopLevelGroups(first);
        uint validTes4RecordCount = ReadTes4RecordCount(first);
        int validRecordCensus = checked(
            validMajorRecords.Length + validTopLevelGroupCount);
        Assert(
            validRecordCensus == 10 &&
            validTes4RecordCount == (uint)validRecordCensus,
            $"The valid Finish Core fixture has an unexpected TES4 census: " +
            $"hedr={validTes4RecordCount}, major={validMajorRecords.Length}, " +
            $"topGroups={validTopLevelGroupCount}, expected=10.");
        byte[] second = writer.Write(
            fixture.SourcePlugin,
            fixture.Proposal,
            fixture.CopiedMaster,
            CancellationToken.None);
        Assert(first.AsSpan().SequenceEqual(second),
            "Repeated Finish Core writes were not byte-identical.");

        SkyrimNpcFinishCoreRequest preserveAiRequest = fixture.Proposal.Request! with
        {
            Schema = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
            Authorities = fixture.Proposal.Request!.Authorities with
            {
                AdditionalMasters = ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>.Empty
            },
            AiPolicy = null
        };
        SkyrimNpcFinishCoreProposal preserveAiProposal = fixture.Proposal with
        {
            Schema = SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
            Request = preserveAiRequest,
            RequestSha256 = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                preserveAiRequest, new WorkspacePath(fixture.Root)),
            ProposalSha256 = null
        };
        preserveAiProposal = RehashProposal(
            preserveAiProposal, new WorkspacePath(fixture.Root));
        byte[] preserveAiProposalBytes =
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                preserveAiProposal, new WorkspacePath(fixture.Root));
        preserveAiProposal = SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
            preserveAiProposalBytes, new WorkspacePath(fixture.Root));
        SkyrimNpcFinishCoreRequest reopenedPreserveAiRequest =
            preserveAiProposal.Request ??
            throw new InvalidOperationException(
                "The serialized v1 source-AI preservation proposal omitted its request.");
        Assert(
            preserveAiProposal.Schema == SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier &&
            reopenedPreserveAiRequest.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
            reopenedPreserveAiRequest.Authorities.AdditionalMasters.IsEmpty &&
            reopenedPreserveAiRequest.AiPolicy is null,
            "The v1 source-AI preservation fixture retained v2-only authority or mood members.");
        byte[] preservedAiBytes = writer.Write(
            fixture.SourcePlugin,
            preserveAiProposal,
            fixture.CopiedMaster,
            CancellationToken.None);
        string preservedAiPath = Path.Combine(fixture.Root, "preserved-ai.esp");
        await File.WriteAllBytesAsync(preservedAiPath, preservedAiBytes);
        SkyrimMod preservedAi = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(preservedAiPath)),
            SkyrimRelease.SkyrimSE);
        SkyrimMod originalAi = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(fixture.SourcePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        Assert(
            Equals(
                originalAi.Npcs.Single(x => x.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value).AIData,
                preservedAi.Npcs.Single(x => x.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value).AIData),
            "Finish Core changed AIDT when the optional aiPolicy was omitted.");

        string outputPath = Path.Combine(fixture.Root, "BrigitteBardotNpcManager.esp");
        await File.WriteAllBytesAsync(outputPath, first);
        SkyrimMod output = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(outputPath)),
            SkyrimRelease.SkyrimSE);
        SkyrimMod source = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(fixture.SourcePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        Assert(output.ModKey == source.ModKey &&
               output.ModHeader.Flags == source.ModHeader.Flags &&
               output.ModHeader.Stats.NextFormID == fixture.Proposal.NextFormId.Value,
            "TES4 plugin identity, flags, or NextFormID changed outside the proposal.");
        Assert(output.ModHeader.MasterReferences.Select(x => x.Master.ToString())
                   .SequenceEqual(fixture.Proposal.MasterOrder),
            "The output master order differs from the proposal.");

        Npc sourceNpc = source.Npcs.Single(x => x.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value);
        Npc outputNpc = output.Npcs.Single(x => x.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value);
        Assert(((uint)sourceNpc.Configuration.Flags & 0x00000820u) == 0,
            "The binary source fixture already carries the ACBS mask under test.");
        Assert(outputNpc.AIData is
            {
                Aggression: Aggression.Unaggressive,
                Confidence: Confidence.Brave,
                EnergyLevel: 50,
                Responsibility: Responsibility.NoCrime,
                Assistance: Assistance.HelpsFriendsAndAllies
            },
            "Finish Core did not author the exact reviewed AIDT policy.");
        Assert((uint)outputNpc.Configuration.Flags ==
                   ((uint)sourceNpc.Configuration.Flags | 0x00000820u),
            "Finish Core did not apply ACBS by OR-ing the reviewed mask over source flags.");
        Assert(outputNpc.Factions.Select(x => (x.Faction.FormKey, x.Rank))
                   .SequenceEqual(sourceNpc.Factions.Select(x => (x.Faction.FormKey, x.Rank))),
            "Existing faction order or ranks were not preserved.");
        Relationship[] outputRelationships = output.Relationships
            .Where(x => x.Parent.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value)
            .ToArray();
        Assert(outputRelationships.Length == 1 &&
               outputRelationships[0].Child.FormKey == new FormKey(
                   ModKey.FromNameAndExtension("Skyrim.esm"), 7) &&
               outputRelationships[0].Rank == Relationship.RankType.Ally &&
               outputRelationships[0].Flags == 0,
            "Finish Core duplicated or changed the actor-to-player relationship: " +
            string.Join(", ", outputRelationships.Select(x =>
                $"{x.Parent.FormKey}->{x.Child.FormKey} rank={x.Rank} flags={x.Flags}")));

        CombatStyle style = output.CombatStyles.Single(x =>
            x.FormKey.ModKey == output.ModKey && x.FormKey.ID == 0x805);
        Assert(style.OffensiveMult == 0f && style.DefensiveMult == 1f &&
               style.GroupOffensiveMult == 0f &&
               style.EquipmentScoreMultMelee == 0f &&
               style.EquipmentScoreMultMagic == 0f &&
               style.EquipmentScoreMultRanged == 0f &&
               style.EquipmentScoreMultShout == 0f &&
               style.EquipmentScoreMultUnarmed == 0f &&
               style.EquipmentScoreMultStaff == 0f &&
               style.AvoidThreatChance == 1f &&
               style.CloseRange is { FallbackMult: 1f } &&
               style.Flight is { HoverChance: 0f, DiveBombChance: 0f,
                   GroundAttackChance: 0f, PerchAttackChance: 0f,
                   FlyingAttackChance: 0f } &&
               outputNpc.CombatStyle.FormKeyNullable == style.FormKey,
            "The derived CSTY is not the closed defensive-only contract.");

        Outfit outfit = output.Outfits.Single(x =>
            x.FormKey.ModKey == output.ModKey && x.FormKey.ID == 0x806);
        Assert(outputNpc.DefaultOutfit.FormKeyNullable == outfit.FormKey &&
               (outfit.Items ?? []).Select(x => x.FormKey).SequenceEqual(
                   fixture.Proposal.Request!.OutfitPolicy.ArmorItems.Select(ToFinishCoreFormKey)),
            "The private OTFT did not retain the exact reviewed item order.");

        Package package = output.Packages.Single(x =>
            x.FormKey.ModKey == output.ModKey && x.FormKey.ID == 0x807);
        PackageDataLocation location = package.Data.Values
            .OfType<PackageDataLocation>().Single();
        Assert(location.Location is LocationTargetRadius { Radius: 512 } &&
               package.Conditions.Count == 1 &&
               package.VirtualMachineAdapter is null &&
               package.OwnerQuest.FormKeyNullable is null &&
               outputNpc.Packages.Select(x => x.FormKey).SequenceEqual([package.FormKey]),
            "The cloned sandbox PACK widened its admitted surface.");

        BethesdaSkyrimNpcFinishCoreVerification verification =
            new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
                fixture.SourcePlugin,
                new WorkspacePath(outputPath),
                fixture.Proposal,
                fixture.CopiedMaster,
                CancellationToken.None);
        Assert(verification.Verified &&
               verification.TypedForbiddenCounts.Values.All(x => x == 0) &&
               verification.RawForbiddenCounts.Values.All(x => x == 0),
            "The independent typed/raw verifier rejected the admitted binary: " +
            string.Join(" | ", verification.Diagnostics.Select(x => x.Code + ":" + x.Message)));

        string[] malformedNewRecords = fixture.Proposal.NewRecords
            .Select(value => value.StartsWith("CSTY ", StringComparison.Ordinal)
                ? "CSTY zzzz"
                : value)
            .ToArray();
        Assert(
            fixture.Proposal.NewRecords.Count(value =>
                value.StartsWith("CSTY ", StringComparison.Ordinal)) == 1 &&
            malformedNewRecords.Count(value =>
                value.StartsWith("CSTY ", StringComparison.Ordinal)) == 1 &&
            malformedNewRecords.Contains("CSTY zzzz", StringComparer.Ordinal),
            "The malformed CSTY allocation regression did not preserve one canonical replacement.");
        SkyrimNpcFinishCoreProposal malformedAllocationProposal = RehashProposal(
            fixture.Proposal with
            {
                NewRecords = malformedNewRecords.ToImmutableArray(),
                ProposalSha256 = null
            },
            new WorkspacePath(fixture.Root));
        string malformedAllocationOutputPath =
            Path.Combine(fixture.Root, "malformed-csty-allocation.esp");
        await File.WriteAllBytesAsync(malformedAllocationOutputPath, first);
        BethesdaSkyrimNpcFinishCoreVerification malformedAllocationVerification =
            new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
                fixture.SourcePlugin,
                new WorkspacePath(malformedAllocationOutputPath),
                malformedAllocationProposal,
                fixture.CopiedMaster,
                CancellationToken.None);
        Diagnostic? malformedAllocationDiagnostic =
            malformedAllocationVerification.Diagnostics.FirstOrDefault(item =>
                item.Code == "finish-core-verify-proposal-allocation-invalid");
        Assert(
            !malformedAllocationVerification.Verified &&
            malformedAllocationDiagnostic is not null &&
            malformedAllocationDiagnostic.Message.Contains(
                "CSTY allocation", StringComparison.Ordinal) &&
            malformedAllocationDiagnostic.Message.Contains(
                "zzzz", StringComparison.Ordinal) &&
            !malformedAllocationVerification.Diagnostics.Any(item =>
                item.Code == "finish-core-verify-exception"),
            "Malformed CSTY allocation did not produce the stable refusal diagnostic: " +
            string.Join(" | ", malformedAllocationVerification.Diagnostics.Select(
                item => item.Code + ":" + item.Message)));

        await TestFinishCoreOutputRecordClosureAsync(
            fixture, first, validTes4RecordCount);

        SkyrimNpcFinishCorePluginSnapshot admitted =
            BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                new WorkspacePath(outputPath),
                new PluginName(fixture.PluginKey.FileName.ToString()),
                FinishCoreBinaryFixture.ActorFormId,
                new EditorId("BrigitteBardotNpcManager"),
                CancellationToken.None);
        Assert(admitted.Admitted && admitted.PackageMatchesFinishCoreContract,
            "The source reader did not recognize the exact closed Finish Core package contract: " +
            string.Join(" | ", admitted.Diagnostics.Select(row => row.Code + ":" + row.Message)));

        AssertProtectedRecordsIdentical(
            await File.ReadAllBytesAsync(fixture.SourcePlugin.Value), first,
            FinishCoreBinaryFixture.ActorFormId.Value);

        byte[] hostile = first.ToArray();
        int npcOffset = FindRecord(hostile, "NPC_", FinishCoreBinaryFixture.ActorFormId.Value);
        int edid = FindSubrecord(hostile, npcOffset, "EDID");
        hostile[edid + 6] ^= 0x01;
        string hostilePath = Path.Combine(fixture.Root, "hostile.esp");
        await File.WriteAllBytesAsync(hostilePath, hostile);
        BethesdaSkyrimNpcFinishCoreVerification hostileVerification =
            new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
                fixture.SourcePlugin,
                new WorkspacePath(hostilePath),
                fixture.Proposal,
                fixture.CopiedMaster,
                CancellationToken.None);
        Assert(!hostileVerification.Verified,
            "The verifier accepted a protected NPC subrecord mutation.");

        byte[] typedInvalid = first.ToArray();
        int typedInvalidNpc = FindRecord(
            typedInvalid,
            "NPC_",
            FinishCoreBinaryFixture.ActorFormId.Value);
        uint typedInvalidFlags = BinaryPrimitives.ReadUInt32LittleEndian(
            typedInvalid.AsSpan(typedInvalidNpc + 8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            typedInvalid.AsSpan(typedInvalidNpc + 8, 4),
            typedInvalidFlags | 0x0004_0000u);
        string typedInvalidPath = Path.Combine(
            fixture.Root,
            "typed-invalid.esp");
        await File.WriteAllBytesAsync(typedInvalidPath, typedInvalid);
        BethesdaSkyrimNpcFinishCoreVerification typedInvalidVerification =
            new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
                fixture.SourcePlugin,
                new WorkspacePath(typedInvalidPath),
                fixture.Proposal,
                fixture.CopiedMaster,
                CancellationToken.None);
        Assert(
            !typedInvalidVerification.Verified &&
            typedInvalidVerification.Diagnostics.Any(item =>
                item.Code == "finish-core-verify-exception"),
            "A Mutagen typed-record failure escaped or lost its verifier diagnostic classification: " +
            string.Join(" | ", typedInvalidVerification.Diagnostics.Select(
                item => item.Code + ":" + item.Message)));

        byte[] hostileAi = first.ToArray();
        int hostileAiNpc = FindRecord(
            hostileAi, "NPC_", FinishCoreBinaryFixture.ActorFormId.Value);
        int aidt = FindSubrecord(hostileAi, hostileAiNpc, "AIDT");
        hostileAi[aidt + 11] = 0;
        string hostileAiPath = Path.Combine(fixture.Root, "hostile-ai.esp");
        await File.WriteAllBytesAsync(hostileAiPath, hostileAi);
        BethesdaSkyrimNpcFinishCoreVerification hostileAiVerification =
            new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
                fixture.SourcePlugin,
                new WorkspacePath(hostileAiPath),
                fixture.Proposal,
                fixture.CopiedMaster,
                CancellationToken.None);
        Assert(
            !hostileAiVerification.Verified &&
            hostileAiVerification.Diagnostics.Any(item =>
                item.Code == "finish-core-verify-aidt"),
            "The verifier accepted an output whose raw AIDT assistance drifted from the reviewed policy.");

        await TestFinishCoreMoodRoundTripsAsync(fixture, writer);
        await TestFinishCoreSourceCountFirstAsync(fixture, first);
        await TestFinishCoreExactOneDiagnosticsAsync(fixture, first, writer);
    }

    private static async Task TestFinishCoreOutputRecordClosureAsync(
        FinishCoreBinaryFixture fixture,
        byte[] validOutputBytes,
        uint expectedTes4RecordCount)
    {
        const uint unexpectedRecordId = 0x801;
        string path = PerRowPluginPath(
            fixture.Root, "output-record-closure");
        await File.WriteAllBytesAsync(path, validOutputBytes);
        SkyrimMod output = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(path)),
            SkyrimRelease.SkyrimSE);
        ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        output.Relationships.Add(new Relationship(
            new FormKey(fixture.PluginKey, unexpectedRecordId),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "BrigitteBardotNpcManagerUnexpectedRELA",
            Parent = new FormLink<INpcGetter>(
                new FormKey(fixture.PluginKey, 0x802)),
            Child = new FormLink<INpcGetter>(
                new FormKey(skyrim, 0x7)),
            Rank = Relationship.RankType.Ally,
            Unknown = 0,
            Flags = 0,
            AssociationType = new FormLink<IAssociationTypeGetter>(FormKey.Null)
        });
        WriteFinishCoreBinaryPlugin(output, path);
        byte[] mutated = File.ReadAllBytes(path);
        SetTes4RecordCount(mutated, expectedTes4RecordCount);
        await File.WriteAllBytesAsync(path, mutated);

        ImmutableArray<RawRecord> raw = ReadRecords(mutated);
        RawRecord[] extraRows = raw
            .Where(row => row.Signature == "RELA" &&
                         row.LocalFormId == unexpectedRecordId)
            .ToArray();
        byte selfFileLocalIndex = ReadTes4SelfFileLocalIndex(mutated);
        int actualCensus = checked(raw.Length + CountTopLevelGroups(mutated));
        Assert(
            ReadTes4RecordCount(mutated) == expectedTes4RecordCount &&
            actualCensus == checked((int)expectedTes4RecordCount + 1) &&
            raw.Count(row => row.Signature == "RELA") == 2 &&
            extraRows.Length == 1 &&
            BinaryPrimitives.ReadUInt32LittleEndian(
                extraRows[0].Bytes.AsSpan(12, 4)) ==
                (((uint)selfFileLocalIndex << 24) | unexpectedRecordId),
            "The output-closure fixture did not preserve the extra RELA raw key " +
            "and the expected HEDR-versus-census mismatch.");

        SkyrimMod reopened = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(path)),
            SkyrimRelease.SkyrimSE);
        Relationship[] typedExtra = reopened.Relationships
            .Where(row => row.FormKey.ModKey == fixture.PluginKey &&
                          row.FormKey.ID == unexpectedRecordId)
            .ToArray();
        Assert(
            Path.GetFileName(path).Equals(
                fixture.PluginKey.FileName.ToString(), StringComparison.OrdinalIgnoreCase) &&
            reopened.ModKey == fixture.PluginKey &&
            typedExtra.Length == 1,
            "The output-closure fixture did not independently reopen the unique self-owned RELA.");

        BethesdaSkyrimNpcFinishCoreVerification verification =
            new BethesdaSkyrimNpcFinishCoreVerifier().Verify(
                fixture.SourcePlugin,
                new WorkspacePath(path),
                fixture.Proposal,
                fixture.CopiedMaster,
                CancellationToken.None);
        Diagnostic? closureDiagnostic = verification.Diagnostics.FirstOrDefault(item =>
            item.Code == "finish-core-verify-record-closure");
        Assert(
            !verification.Verified &&
            closureDiagnostic is not null &&
            closureDiagnostic.Message.Contains(
                "RELA 0x00000801", StringComparison.Ordinal) &&
            closureDiagnostic.Message.Contains(
                "census", StringComparison.OrdinalIgnoreCase) &&
            !verification.Diagnostics.Any(item =>
                item.Code == "finish-core-verify-exception"),
            "The output closure did not produce the stable unexpected-record/census refusal: " +
            string.Join(" | ", verification.Diagnostics.Select(
                item => item.Code + ":" + item.Message)));
    }

    private static async Task TestFinishCoreSourceCountFirstAsync(
        FinishCoreBinaryFixture fixture,
        byte[] validOutputBytes)
    {
        foreach ((string name, string signature, string code) in new[]
        {
            ("source-csty-duplicate", "CSTY", "finish-core-source-csty-count"),
            ("source-pack-duplicate", "PACK", "finish-core-source-pack-count")
        })
        {
            string path = PerRowPluginPath(fixture.Root, name);
            await File.WriteAllBytesAsync(path, validOutputBytes);
            uint localFormId = GetAllocationId(fixture.Proposal, signature);
            RawDuplicateAppendEvidence evidence = AppendDuplicateRecord(
                path, signature, localFormId);
            AssertRawDuplicateEvidence(
                path, fixture.PluginKey, signature, localFormId, name, evidence);
            byte[] duplicateBytes = await File.ReadAllBytesAsync(path);
            int rawCount = ReadRecords(duplicateBytes).Count(row =>
                row.Signature == signature && row.LocalFormId == localFormId);
            Assert(
                rawCount == 2 &&
                evidence.Tes4RecordCountAfter ==
                    checked(evidence.Tes4RecordCountBefore + 1),
                $"Source count-first fixture '{name}' did not preserve raw count=2.");

            SkyrimNpcFinishCorePluginSnapshot snapshot =
                BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                    new WorkspacePath(path),
                    new PluginName(fixture.PluginKey.FileName.ToString()),
                    FinishCoreBinaryFixture.ActorFormId,
                    new EditorId("BrigitteBardotNpcManager"),
                    CancellationToken.None);
            AssertStableExactOneDiagnostic(
                snapshot.Diagnostics, code, signature, "observed=at least 2", name);
            Assert(
                snapshot.Diagnostics.All(item =>
                    !item.Message.Contains("RecordCollision", StringComparison.Ordinal) &&
                    !item.Code.Contains("exception", StringComparison.OrdinalIgnoreCase)),
                $"Source count-first fixture '{name}' escaped through a collision/exception diagnostic: " +
                string.Join(" | ", snapshot.Diagnostics.Select(
                    item => item.Code + ":" + item.Message)));
        }
    }

    private static async Task TestFinishCoreMoodRoundTripsAsync(
        FinishCoreBinaryFixture fixture,
        BethesdaSkyrimNpcFinishCoreWriter writer)
    {
        BethesdaSkyrimNpcFinishCoreVerifier verifier =
            new();
        SkyrimNpcFinishCoreMood[] moods =
            Enum.GetValues<SkyrimNpcFinishCoreMood>();
        for (int index = 0; index < moods.Length; index++)
        {
            SkyrimNpcFinishCoreMood sourceMood = moods[index];
            SkyrimNpcFinishCoreMood requestedMood =
                moods[(index + 1) % moods.Length];
            Mutagen.Bethesda.Skyrim.Mood sourceMutagenMood =
                ToMutagenMood(sourceMood);
            Mutagen.Bethesda.Skyrim.Mood requestedMutagenMood =
                ToMutagenMood(requestedMood);
            string sourcePath = PerRowPluginPath(
                fixture.Root, "mood-source-" + sourceMood);
            FinishCoreBinaryFixture.WriteSourcePlugin(sourcePath, sourceMutagenMood);
            SkyrimNpcFinishCorePluginSnapshot sourceSnapshot =
                BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                    new WorkspacePath(sourcePath),
                    new PluginName(fixture.PluginKey.FileName.ToString()),
                    FinishCoreBinaryFixture.ActorFormId,
                    new EditorId("BrigitteBardotNpcManager"),
                    CancellationToken.None);
            Assert(
                sourceSnapshot.Admitted && sourceSnapshot.AiData?.Mood == sourceMood,
                $"Source readback did not preserve mood '{sourceMood}' through the explicit Mutagen mapping: " +
                string.Join(" | ", sourceSnapshot.Diagnostics.Select(item => item.Code + ":" + item.Message)));

            SkyrimNpcFinishCoreRequest request = fixture.Proposal.Request! with
            {
                Source = fixture.Proposal.Request.Source with
                {
                    PluginPath = new WorkspacePath(sourcePath),
                    PluginSha256 = HashBinaryFile(sourcePath)
                },
                AiPolicy = fixture.Proposal.Request.AiPolicy! with
                {
                    Mood = requestedMood
                }
            };
            Sha256Hash requestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                request, new WorkspacePath(fixture.Root));
            SkyrimNpcFinishCoreProposal proposal = fixture.Proposal with
            {
                Request = request,
                RequestSha256 = requestSha,
                ProposalSha256 = null
            };
            proposal = RehashProposal(
                proposal, new WorkspacePath(fixture.Root));
            byte[] outputBytes = writer.Write(
                new WorkspacePath(sourcePath),
                proposal,
                fixture.CopiedMaster,
                CancellationToken.None);
            string outputPath = PerRowPluginPath(
                fixture.Root, "mood-output-" + requestedMood);
            await File.WriteAllBytesAsync(outputPath, outputBytes);
            SkyrimMod output = SkyrimMod.CreateFromBinary(
                new ModPath(fixture.PluginKey, new FilePath(outputPath)),
                SkyrimRelease.SkyrimSE);
            Npc outputNpc = output.Npcs.Single(row =>
                row.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value);
            Assert(
                outputNpc.AIData?.Mood == requestedMutagenMood,
                $"Writer/readback did not apply Finish Core mood '{requestedMood}' (Mutagen '{requestedMutagenMood}').");
            SkyrimNpcFinishCorePluginSnapshot outputSnapshot =
                BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                    new WorkspacePath(outputPath),
                    new PluginName(fixture.PluginKey.FileName.ToString()),
                    FinishCoreBinaryFixture.ActorFormId,
                    new EditorId("BrigitteBardotNpcManager"),
                    CancellationToken.None);
            Assert(
                outputSnapshot.Admitted && outputSnapshot.AiData?.Mood == requestedMood,
                $"Independent output readback did not preserve mood '{requestedMood}'.");
            BethesdaSkyrimNpcFinishCoreVerification verification = verifier.Verify(
                new WorkspacePath(sourcePath),
                new WorkspacePath(outputPath),
                proposal,
                fixture.CopiedMaster,
                CancellationToken.None);
            Assert(
                verification.Verified && verification.Diagnostics.All(item =>
                    item.Code != "finish-core-verify-exception"),
                $"Verifier did not accept mood '{requestedMood}': " +
                string.Join(" | ", verification.Diagnostics.Select(item => item.Code + ":" + item.Message)));
        }

        SkyrimNpcFinishCoreRequest undefinedRequest = fixture.Proposal.Request! with
        {
            AiPolicy = fixture.Proposal.Request.AiPolicy! with
            {
                Mood = (SkyrimNpcFinishCoreMood)999
            }
        };
        SkyrimNpcFinishCoreProposal undefinedProposal = fixture.Proposal with
        {
            Request = undefinedRequest,
            RequestSha256 = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                undefinedRequest, new WorkspacePath(fixture.Root)),
            ProposalSha256 = null
        };
        undefinedProposal = RehashProposal(
            undefinedProposal, new WorkspacePath(fixture.Root));
        try
        {
            _ = writer.Write(
                fixture.SourcePlugin,
                undefinedProposal,
                fixture.CopiedMaster,
                CancellationToken.None);
            throw new InvalidOperationException(
                "Writer accepted an undefined Finish Core mood enum value.");
        }
        catch (InvalidDataException exception)
        {
            Assert(
                exception.Message.Contains("finish-core-writer-mood", StringComparison.Ordinal) &&
                exception.Message.Contains("999", StringComparison.Ordinal),
                "Undefined Finish Core mood refusal omitted the exact mood-specific identity: " +
                exception.Message);
        }

        string legacySourcePath = PerRowPluginPath(
            fixture.Root, "legacy-mood-source");
        FinishCoreBinaryFixture.WriteSourcePlugin(
            legacySourcePath, Mutagen.Bethesda.Skyrim.Mood.Surprised);
        SkyrimNpcFinishCoreRequest legacyRequest = fixture.Proposal.Request! with
        {
            Schema = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
            Authorities = fixture.Proposal.Request!.Authorities with
            {
                AdditionalMasters = ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>.Empty
            },
            Source = fixture.Proposal.Request!.Source with
            {
                PluginPath = new WorkspacePath(legacySourcePath),
                PluginSha256 = HashBinaryFile(legacySourcePath)
            },
            AiPolicy = fixture.Proposal.Request!.AiPolicy! with { Mood = null }
        };
        SkyrimNpcFinishCoreProposal legacyProposal = fixture.Proposal with
        {
            Schema = SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
            Request = legacyRequest,
            RequestSha256 = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                legacyRequest, new WorkspacePath(fixture.Root)),
            ProposalSha256 = null
        };
        legacyProposal = RehashProposal(
            legacyProposal, new WorkspacePath(fixture.Root));
        byte[] legacyProposalBytes =
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                legacyProposal, new WorkspacePath(fixture.Root));
        legacyProposal = SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
            legacyProposalBytes, new WorkspacePath(fixture.Root));
        SkyrimNpcFinishCoreRequest reopenedLegacyRequest =
            legacyProposal.Request ??
            throw new InvalidOperationException(
                "The serialized v1 mood-preservation proposal omitted its request.");
        Assert(
            legacyProposal.Schema == SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier &&
            reopenedLegacyRequest.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
            reopenedLegacyRequest.Authorities.AdditionalMasters.IsEmpty &&
            reopenedLegacyRequest.AiPolicy?.Mood is null &&
            SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                legacyProposalBytes) == legacyProposal.ProposalSha256,
            "The v1 mood-preservation fixture retained v2-only authority or mood members.");
        byte[] legacyBytes = writer.Write(
            new WorkspacePath(legacySourcePath),
            legacyProposal,
            fixture.CopiedMaster,
            CancellationToken.None);
        string legacyOutputPath = PerRowPluginPath(
            fixture.Root, "legacy-mood-output");
        await File.WriteAllBytesAsync(legacyOutputPath, legacyBytes);
        SkyrimMod legacyOutput = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(legacyOutputPath)),
            SkyrimRelease.SkyrimSE);
        Assert(
            legacyOutput.Npcs.Single(row =>
                row.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value).AIData?.Mood ==
                Mutagen.Bethesda.Skyrim.Mood.Surprised,
            "A v1 null mood did not preserve the source Mutagen Surprised value.");
        BethesdaSkyrimNpcFinishCoreVerification legacyVerification = verifier.Verify(
            new WorkspacePath(legacySourcePath),
            new WorkspacePath(legacyOutputPath),
            legacyProposal,
            fixture.CopiedMaster,
            CancellationToken.None);
        Assert(
            legacyVerification.Verified,
            "Verifier treated the v1 null mood as a required output comparison: " +
            string.Join(" | ", legacyVerification.Diagnostics.Select(item => item.Code + ":" + item.Message)));

        SkyrimMod legacyMutation = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(legacyOutputPath)),
            SkyrimRelease.SkyrimSE);
        Npc legacyMutationNpc = legacyMutation.Npcs.Single(row =>
            row.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value);
        AIData legacyBefore = legacyMutationNpc.AIData ??
            throw new InvalidDataException("The v1 mood fixture omitted AIDT before mutation.");
        Aggression legacyAggression = legacyBefore.Aggression;
        Confidence legacyConfidence = legacyBefore.Confidence;
        int legacyEnergy = legacyBefore.EnergyLevel;
        Responsibility legacyResponsibility = legacyBefore.Responsibility;
        Assistance legacyAssistance = legacyBefore.Assistance;
        Mutagen.Bethesda.Skyrim.Mood legacyMood = legacyBefore.Mood;
        legacyBefore.Mood = Mutagen.Bethesda.Skyrim.Mood.Angry;
        string legacyMutatedPath = PerRowPluginPath(
            fixture.Root, "legacy-mood-mutated");
        WriteFinishCoreBinaryPlugin(legacyMutation, legacyMutatedPath);
        SkyrimMod legacyMutated = SkyrimMod.CreateFromBinary(
            new ModPath(fixture.PluginKey, new FilePath(legacyMutatedPath)),
            SkyrimRelease.SkyrimSE);
        AIData mutatedAi = legacyMutated.Npcs.Single(row =>
                row.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value).AIData ??
            throw new InvalidDataException("The v1 mood mutation omitted AIDT after mutation.");
        Assert(
            legacyMood == Mutagen.Bethesda.Skyrim.Mood.Surprised &&
            legacyMood != mutatedAi.Mood &&
            mutatedAi.Mood == Mutagen.Bethesda.Skyrim.Mood.Angry &&
            mutatedAi.Aggression == legacyAggression &&
            mutatedAi.Confidence == legacyConfidence &&
            mutatedAi.EnergyLevel == legacyEnergy &&
            mutatedAi.Responsibility == legacyResponsibility &&
            mutatedAi.Assistance == legacyAssistance,
            "The v1 mood mutation did not preserve the five legacy AIDT fields while changing Mood.");
        BethesdaSkyrimNpcFinishCoreVerification legacyMoodMutationVerification =
            verifier.Verify(
                new WorkspacePath(legacySourcePath),
                new WorkspacePath(legacyMutatedPath),
                legacyProposal,
                fixture.CopiedMaster,
                CancellationToken.None);
        Diagnostic? legacyMoodDiagnostic =
            legacyMoodMutationVerification.Diagnostics.FirstOrDefault(item =>
                item.Code == "finish-core-verify-aidt");
        Assert(
            !legacyMoodMutationVerification.Verified &&
            legacyMoodDiagnostic is not null &&
            legacyMoodDiagnostic.Message.Contains("Mood", StringComparison.Ordinal) &&
            !legacyMoodMutationVerification.Diagnostics.Any(item =>
                item.Code == "finish-core-verify-exception"),
            "The v1 mood mutation did not produce the stable AI refusal with Mood evidence: " +
            string.Join(" | ", legacyMoodMutationVerification.Diagnostics.Select(
                item => item.Code + ":" + item.Message)));
    }

    private static async Task TestFinishCoreExactOneDiagnosticsAsync(
        FinishCoreBinaryFixture fixture,
        byte[] validOutputBytes,
        BethesdaSkyrimNpcFinishCoreWriter writer)
    {
        string validOutputPath = PerRowPluginPath(
            fixture.Root, "exact-one-valid");
        await File.WriteAllBytesAsync(validOutputPath, validOutputBytes);

        var sourceCases = new[]
        {
            (
                Name: "source-npc-missing",
                Observed: "observed=0",
                Mutate: (Action<SkyrimMod>)(mod => mod.Npcs.Clear())),
            (
                Name: "source-npc-duplicate",
                Observed: "observed=at least 2",
                Mutate: (Action<SkyrimMod>)(_ => { }))
        };
        foreach ((string name, string observed, Action<SkyrimMod> mutate) in sourceCases)
        {
            bool sameFormKeyDuplicate = name == "source-npc-duplicate";
            SkyrimMod source = SkyrimMod.CreateFromBinary(
                new ModPath(fixture.PluginKey, new FilePath(fixture.SourcePlugin.Value)),
                SkyrimRelease.SkyrimSE);
            mutate(source);
            string path = PerRowPluginPath(fixture.Root, name);
            WriteFinishCoreBinaryPlugin(source, path);
            RawDuplicateAppendEvidence? duplicateAppendEvidence = null;
            if (name.Contains("duplicate", StringComparison.Ordinal))
                duplicateAppendEvidence = AppendDuplicateRecord(
                    path, "NPC_", FinishCoreBinaryFixture.ActorFormId.Value);
            int rawSourceNpcCount = ReadRecords(File.ReadAllBytes(path))
                .Count(row => row.Signature == "NPC_" &&
                              row.LocalFormId == FinishCoreBinaryFixture.ActorFormId.Value);
            Assert(rawSourceNpcCount ==
                       (name.Contains("missing", StringComparison.Ordinal) ? 0 : 2),
                $"Source exact-one fixture '{name}' did not preserve the intended raw NPC count.");
            if (sameFormKeyDuplicate)
            {
                AssertRawDuplicateEvidence(
                    path,
                    fixture.PluginKey,
                    "NPC_",
                    FinishCoreBinaryFixture.ActorFormId.Value,
                    name,
                    duplicateAppendEvidence!);
            }
            else
            {
                SkyrimMod reopenedSource = SkyrimMod.CreateFromBinary(
                    new ModPath(fixture.PluginKey, new FilePath(path)),
                    SkyrimRelease.SkyrimSE);
                Assert(
                    Path.GetFileName(path).Equals(
                        fixture.PluginKey.FileName.ToString(),
                        StringComparison.OrdinalIgnoreCase) &&
                    reopenedSource.ModKey == fixture.PluginKey &&
                    !reopenedSource.Npcs.Any(row =>
                        row.FormKey.ModKey == fixture.PluginKey &&
                        row.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value),
                    $"Source exact-one fixture '{name}' did not independently reopen with the intended NPC count/self key.");
            }
            SkyrimNpcFinishCorePluginSnapshot snapshot =
                BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                    new WorkspacePath(path),
                    new PluginName(fixture.PluginKey.FileName.ToString()),
                    FinishCoreBinaryFixture.ActorFormId,
                    new EditorId("BrigitteBardotNpcManager"),
                    CancellationToken.None);
            AssertStableExactOneDiagnostic(
                snapshot.Diagnostics,
                "finish-core-source-npc-count",
                "NPC_ 0x00000800",
                observed,
                name);
        }

        var verifierCases = new[]
        {
            (
                Name: "output-npc-missing",
                Code: "finish-core-verify-output-npc-count",
                Identity: "NPC_ 0x00000800",
                Mutate: (Action<SkyrimMod>)(mod => mod.Npcs.Clear())),
            (
                Name: "output-npc-duplicate",
                Code: "finish-core-verify-output-npc-count",
                Identity: "NPC_ 0x00000800",
                Mutate: (Action<SkyrimMod>)(_ => { })),
            (
                Name: "csty-missing",
                Code: "finish-core-verify-csty-count",
                Identity: "CSTY",
                Mutate: (Action<SkyrimMod>)(mod => mod.CombatStyles.Clear())),
            (
                Name: "csty-duplicate",
                Code: "finish-core-verify-csty-count",
                Identity: "CSTY",
                Mutate: (Action<SkyrimMod>)(_ => { })),
            (
                Name: "otft-missing",
                Code: "finish-core-verify-otft-count",
                Identity: "OTFT",
                Mutate: (Action<SkyrimMod>)(mod => mod.Outfits.Clear())),
            (
                Name: "otft-duplicate",
                Code: "finish-core-verify-otft-count",
                Identity: "OTFT",
                Mutate: (Action<SkyrimMod>)(_ => { })),
            (
                Name: "pack-missing",
                Code: "finish-core-verify-pack-count",
                Identity: "PACK",
                Mutate: (Action<SkyrimMod>)(mod => mod.Packages.Clear())),
            (
                Name: "pack-duplicate",
                Code: "finish-core-verify-pack-count",
                Identity: "PACK",
                Mutate: (Action<SkyrimMod>)(_ => { })),
            (
                Name: "pack-location-missing",
                Code: "finish-core-verify-pack-location-count",
                Identity: "PACK location",
                Mutate: (Action<SkyrimMod>)(mod =>
                    mod.Packages.Single().Data.Clear())),
            (
                Name: "pack-location-duplicate",
                Code: "finish-core-verify-pack-location-count",
                Identity: "PACK location",
                Mutate: (Action<SkyrimMod>)(mod =>
                    AddDistinctPackageLocation(mod.Packages.Single(), mod))),
            (
                Name: "pack-condition-missing",
                Code: "finish-core-verify-pack-condition-count",
                Identity: "PACK condition",
                Mutate: (Action<SkyrimMod>)(mod =>
                    mod.Packages.Single().Conditions.Clear())),
            (
                Name: "pack-condition-duplicate",
                Code: "finish-core-verify-pack-condition-count",
                Identity: "PACK condition",
                Mutate: (Action<SkyrimMod>)(mod =>
                    mod.Packages.Single().Conditions.Add(
                        mod.Packages.Single().Conditions.Single().DeepCopy())))
        };
        BethesdaSkyrimNpcFinishCoreVerifier verifier = new();
        foreach ((string name, string code, string identity, Action<SkyrimMod> mutate)
                 in verifierCases)
        {
            bool sameFormKeyDuplicate = name is
                "output-npc-duplicate" or
                "csty-duplicate" or
                "otft-duplicate" or
                "pack-duplicate";
            SkyrimMod output = SkyrimMod.CreateFromBinary(
                new ModPath(fixture.PluginKey, new FilePath(validOutputPath)),
                SkyrimRelease.SkyrimSE);
            if (name.StartsWith("pack-location-", StringComparison.Ordinal))
            {
                Package[] initialPackages = output.Packages
                    .Where(row => row.FormKey.ModKey == fixture.PluginKey &&
                                  row.FormKey.ID == GetAllocationId(
                                      fixture.Proposal, "PACK"))
                    .ToArray();
                Assert(initialPackages.Length == 1,
                    $"PACK location fixture '{name}' did not start with exactly one package.");
                AssertPackageLocations(initialPackages[0], 1, name);
            }
            mutate(output);
            string path = PerRowPluginPath(fixture.Root, name);
            WriteFinishCoreBinaryPlugin(output, path);
            RawDuplicateAppendEvidence? duplicateAppendEvidence = null;
            if (name.Contains("duplicate", StringComparison.Ordinal) &&
                !name.Contains("location", StringComparison.Ordinal) &&
                !name.Contains("condition", StringComparison.Ordinal))
            {
                string signature = name.StartsWith("output-npc-", StringComparison.Ordinal)
                    ? "NPC_"
                    : name.StartsWith("csty-", StringComparison.Ordinal)
                        ? "CSTY"
                        : name.StartsWith("otft-", StringComparison.Ordinal)
                            ? "OTFT"
                            : "PACK";
                uint formId = signature == "NPC_"
                    ? FinishCoreBinaryFixture.ActorFormId.Value
                    : GetAllocationId(fixture.Proposal, signature);
                duplicateAppendEvidence = AppendDuplicateRecord(path, signature, formId);
            }
            if (sameFormKeyDuplicate)
            {
                string signature = name.StartsWith("output-npc-", StringComparison.Ordinal)
                    ? "NPC_"
                    : name.StartsWith("csty-", StringComparison.Ordinal)
                        ? "CSTY"
                        : name.StartsWith("otft-", StringComparison.Ordinal)
                            ? "OTFT"
                            : "PACK";
                uint formId = signature == "NPC_"
                    ? FinishCoreBinaryFixture.ActorFormId.Value
                    : GetAllocationId(fixture.Proposal, signature);
                AssertRawDuplicateEvidence(
                    path,
                    fixture.PluginKey,
                    signature,
                    formId,
                    name,
                    duplicateAppendEvidence!);
            }
            else
            {
                SkyrimMod reopenedOutput = SkyrimMod.CreateFromBinary(
                    new ModPath(fixture.PluginKey, new FilePath(path)),
                    SkyrimRelease.SkyrimSE);
                Assert(
                    Path.GetFileName(path).Equals(
                        fixture.PluginKey.FileName.ToString(),
                        StringComparison.OrdinalIgnoreCase) &&
                    reopenedOutput.ModKey == fixture.PluginKey,
                    $"Output exact-one fixture '{name}' lost the canonical plugin basename/self key.");
                AssertReopenedOutputCase(reopenedOutput, path, fixture, name);
            }
            BethesdaSkyrimNpcFinishCoreVerification verification = verifier.Verify(
                fixture.SourcePlugin,
                new WorkspacePath(path),
                fixture.Proposal,
                fixture.CopiedMaster,
                CancellationToken.None);
            AssertStableExactOneDiagnostic(
                verification.Diagnostics,
                code,
                identity,
                name.Contains("missing", StringComparison.Ordinal)
                    ? "observed=0"
                    : "observed=at least 2",
                name);
        }

        var templateCases = new[]
        {
            (
                Name: "template-pack-missing",
                Observed: "observed=0",
                Mutate: (Action<SkyrimMod>)(mod => mod.Packages.Clear())),
            (
                Name: "template-pack-duplicate",
                Observed: "observed=at least 2",
                Mutate: (Action<SkyrimMod>)(_ => { }))
        };
        foreach ((string name, string observed, Action<SkyrimMod> mutate) in templateCases)
        {
            string templatePath = PerRowMasterPath(fixture.Root, name);
            SkyrimMod template = SkyrimMod.CreateFromBinary(
                new ModPath(
                    ModKey.FromNameAndExtension("Skyrim.esm"),
                    new FilePath(fixture.CopiedMaster.Value)),
                SkyrimRelease.SkyrimSE);
            mutate(template);
            WriteFinishCoreBinaryPlugin(template, templatePath);
            RawDuplicateAppendEvidence? duplicateAppendEvidence = null;
            if (name.Contains("duplicate", StringComparison.Ordinal))
                duplicateAppendEvidence = AppendDuplicateRecord(templatePath, "PACK", 0x1B217);
            int rawTemplatePackCount = ReadRecords(File.ReadAllBytes(templatePath))
                .Count(row => row.Signature == "PACK" && row.LocalFormId == 0x1B217);
            Assert(rawTemplatePackCount ==
                       (name.Contains("missing", StringComparison.Ordinal) ? 0 : 2),
                $"Template exact-one fixture '{name}' did not preserve the intended raw PACK count.");
            if (name == "template-pack-duplicate")
            {
                AssertRawDuplicateEvidence(
                    templatePath,
                    ModKey.FromNameAndExtension("Skyrim.esm"),
                    "PACK",
                    0x1B217,
                    name,
                    duplicateAppendEvidence!);
            }
            else
            {
                SkyrimMod reopenedTemplate = SkyrimMod.CreateFromBinary(
                    new ModPath(
                        ModKey.FromNameAndExtension("Skyrim.esm"),
                        new FilePath(templatePath)),
                    SkyrimRelease.SkyrimSE);
                Assert(
                    Path.GetFileName(templatePath).Equals("Skyrim.esm", StringComparison.OrdinalIgnoreCase) &&
                    reopenedTemplate.ModKey == ModKey.FromNameAndExtension("Skyrim.esm") &&
                    !reopenedTemplate.Packages.Any(row =>
                        row.FormKey.ModKey == ModKey.FromNameAndExtension("Skyrim.esm") &&
                        row.FormKey.ID == 0x1B217),
                    $"Template exact-one fixture '{name}' did not independently reopen with the intended PACK count/self key.");
            }
            SkyrimNpcFinishCoreRequest templateRequest =
                fixture.Proposal.Request! with
                {
                    SandboxAuthority = fixture.Proposal.Request.SandboxAuthority with
                    {
                        CopiedMaster = new WorkspacePath(templatePath),
                        CopiedMasterSha256 = HashBinaryFile(templatePath)
                    }
                };
            SkyrimNpcFinishCoreProposal templateProposal = RehashProposal(
                fixture.Proposal with
                {
                    Request = templateRequest,
                    RequestSha256 = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                        templateRequest,
                        new WorkspacePath(fixture.Root)),
                    ProposalSha256 = null
                },
                new WorkspacePath(fixture.Root));
            AssertStableWriterExactOne(
                () => writer.Write(
                    fixture.SourcePlugin,
                    templateProposal,
                    new WorkspacePath(templatePath),
                    CancellationToken.None),
                "finish-core-writer-pack-template-count",
                "PACK 0x0001B217",
                observed,
                name);
        }

    }

    private static RawDuplicateAppendEvidence AppendDuplicateRecord(
        string path,
        string signature,
        uint localFormId,
        uint? rawFormId = null)
    {
        byte[] bytes = File.ReadAllBytes(path);
        ImmutableArray<RawRecord> beforeRecords = ReadRecords(bytes);
        uint beforeTes4RecordCount = ReadTes4RecordCount(bytes);
        int beforeTopLevelGroupCount = CountTopLevelGroups(bytes);
        RawRecord record = ReadRecords(bytes)
            .FirstOrDefault(row => row.Signature == signature &&
                                   row.LocalFormId == localFormId &&
                                   (rawFormId is null || BinaryPrimitives.ReadUInt32LittleEndian(
                                       row.Bytes.AsSpan(12, 4)) == rawFormId))
            ?? throw new InvalidDataException(
                $"Cannot duplicate missing {signature} 0x{localFormId:X8}.");
        (int groupStart, int groupLength) = FindTopLevelGroup(bytes, signature);
        int groupEnd = checked(groupStart + groupLength);
        byte[] rebuilt = new byte[checked(bytes.Length + record.Bytes.Length)];
        bytes.AsSpan(0, groupEnd).CopyTo(rebuilt);
        record.Bytes.CopyTo(rebuilt.AsSpan(groupEnd));
        bytes.AsSpan(groupEnd).CopyTo(
            rebuilt.AsSpan(checked(groupEnd + record.Bytes.Length)));
        BinaryPrimitives.WriteUInt32LittleEndian(
            rebuilt.AsSpan(groupStart + 4, 4),
            checked((uint)(groupLength + record.Bytes.Length)));
        IncrementTes4RecordCount(rebuilt);
        uint afterTes4RecordCount = ReadTes4RecordCount(rebuilt);
        int afterMajorRecordCount = ReadRecords(rebuilt).Length;
        int afterTopLevelGroupCount = CountTopLevelGroups(rebuilt);
        File.WriteAllBytes(path, rebuilt);
        return new RawDuplicateAppendEvidence(
            beforeTes4RecordCount,
            afterTes4RecordCount,
            beforeRecords.Length,
            afterMajorRecordCount,
            beforeTopLevelGroupCount,
            afterTopLevelGroupCount);
    }

    private static (int Start, int Length) FindTopLevelGroup(
        byte[] bytes,
        string signature)
    {
        int position = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        while (position < bytes.Length)
        {
            string actual = Encoding.ASCII.GetString(bytes, position, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 4, 4));
            int length = actual == "GRUP"
                ? checked((int)size)
                : checked(24 + (int)size);
            if (actual == "GRUP" &&
                Encoding.ASCII.GetString(bytes, position + 8, 4) == signature)
                return (position, length);
            position = checked(position + length);
        }
        throw new InvalidDataException(
            $"The plugin has no top-level {signature} group.");
    }

    private static int CountTopLevelGroups(byte[] bytes)
    {
        int position = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        int count = 0;
        while (position < bytes.Length)
        {
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 4, 4));
            int length = signature == "GRUP"
                ? checked((int)size)
                : checked(24 + (int)size);
            if (signature == "GRUP")
                count = checked(count + 1);
            position = checked(position + length);
        }
        return count;
    }

    private static void IncrementTes4RecordCount(byte[] bytes)
    {
        int position = 24;
        int end = checked(position + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        while (position < end)
        {
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            if (signature == "HEDR")
            {
                if (size < 12)
                    throw new InvalidDataException("The plugin HEDR is too short.");
                uint count = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 10, 4));
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(position + 10, 4), checked(count + 1));
                return;
            }
            position = checked(position + 6 + size);
        }
        throw new InvalidDataException("The plugin HEDR subrecord is missing.");
    }

    private static void SetTes4RecordCount(byte[] bytes, uint recordCount)
    {
        int position = 24;
        int end = checked(position + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        while (position < end)
        {
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            if (signature == "HEDR")
            {
                if (size < 12)
                    throw new InvalidDataException("The plugin HEDR is too short.");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(position + 10, 4), recordCount);
                return;
            }
            position = checked(position + 6 + size);
        }
        throw new InvalidDataException("The plugin HEDR subrecord is missing.");
    }

    private static void AssertRawDuplicateEvidence(
        string path,
        ModKey expectedModKey,
        string signature,
        uint localFormId,
        string name,
        RawDuplicateAppendEvidence appendEvidence)
    {
        byte[] bytes = File.ReadAllBytes(path);
        ImmutableArray<RawRecord> records = ReadRecords(bytes);
        ImmutableArray<RawRecord> matches = records
            .Where(row => row.Signature == signature && row.LocalFormId == localFormId)
            .ToImmutableArray();
        byte selfFileLocalIndex = ReadTes4SelfFileLocalIndex(bytes);
        Assert(
            Path.GetFileName(path).Equals(
                expectedModKey.FileName.ToString(),
                StringComparison.OrdinalIgnoreCase),
            $"Raw exact-one fixture '{name}' lost the canonical plugin basename/self key.");
        Assert(
            matches.Length == 2 && matches.All(row =>
            {
                uint formId = BinaryPrimitives.ReadUInt32LittleEndian(
                    row.Bytes.AsSpan(12, 4));
                return (formId & 0xFF00_0000u) ==
                           ((uint)selfFileLocalIndex << 24) &&
                       (formId & 0x00FF_FFFFu) == localFormId;
            }),
            $"Raw exact-one fixture '{name}' did not preserve exactly two self-owned " +
            $"{signature} 0x{localFormId:X8} records.");
        Assert(
            appendEvidence.Tes4RecordCountAfter ==
                checked(appendEvidence.Tes4RecordCountBefore + 1) &&
            appendEvidence.MajorRecordCountAfter ==
                checked(appendEvidence.MajorRecordCountBefore + 1) &&
            appendEvidence.TopLevelGroupCountAfter ==
                appendEvidence.TopLevelGroupCountBefore,
            $"Raw exact-one fixture '{name}' did not preserve the exact duplicate " +
            "append evidence (HEDR +1, major records +1, top-level groups unchanged).");
    }

    private static byte ReadTes4SelfFileLocalIndex(byte[] bytes)
    {
        int position = 24;
        int end = checked(position + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        int declaredMasterCount = 0;
        while (position < end)
        {
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            if (signature == "MAST")
            {
                if (size == 0)
                    throw new InvalidDataException("The plugin MAST subrecord is empty.");
                declaredMasterCount = checked(declaredMasterCount + 1);
            }
            position = checked(position + 6 + size);
        }
        return checked((byte)declaredMasterCount);
    }

    private static uint ReadTes4RecordCount(byte[] bytes)
    {
        int position = 24;
        int end = checked(position + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        while (position < end)
        {
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            if (signature == "HEDR")
            {
                if (size < 12)
                    throw new InvalidDataException("The plugin HEDR is too short.");
                return BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 10, 4));
            }
            position = checked(position + 6 + size);
        }
        throw new InvalidDataException("The plugin HEDR subrecord is missing.");
    }

    private static void AssertReopenedOutputCase(
        SkyrimMod output,
        string path,
        FinishCoreBinaryFixture fixture,
        string name)
    {
        int expected = name.Contains("missing", StringComparison.Ordinal) ? 0 : 2;
        ImmutableArray<RawRecord> raw = ReadRecords(File.ReadAllBytes(path));
        if (name.StartsWith("output-npc-", StringComparison.Ordinal))
        {
            int count = output.Npcs.Count(row =>
                row.FormKey.ModKey == fixture.PluginKey &&
                row.FormKey.ID == FinishCoreBinaryFixture.ActorFormId.Value);
            int rawCount = raw.Count(row => row.Signature == "NPC_" &&
                row.LocalFormId == FinishCoreBinaryFixture.ActorFormId.Value);
            Assert(count == expected,
                $"Output NPC fixture '{name}' reopened with {count}, expected {expected}.");
            Assert(rawCount == expected,
                $"Output NPC fixture '{name}' raw-reopened with {rawCount}, expected {expected}.");
            return;
        }

        if (name.StartsWith("csty-", StringComparison.Ordinal))
        {
            uint id = GetAllocationId(fixture.Proposal, "CSTY");
            int count = output.CombatStyles.Count(row =>
                row.FormKey.ModKey == fixture.PluginKey && row.FormKey.ID == id);
            int rawCount = raw.Count(row => row.Signature == "CSTY" &&
                row.LocalFormId == id);
            Assert(count == expected,
                $"CSTY fixture '{name}' reopened with {count}, expected {expected}.");
            Assert(rawCount == expected,
                $"CSTY fixture '{name}' raw-reopened with {rawCount}, expected {expected}.");
            return;
        }

        if (name.StartsWith("otft-", StringComparison.Ordinal))
        {
            uint id = GetAllocationId(fixture.Proposal, "OTFT");
            int count = output.Outfits.Count(row =>
                row.FormKey.ModKey == fixture.PluginKey && row.FormKey.ID == id);
            int rawCount = raw.Count(row => row.Signature == "OTFT" &&
                row.LocalFormId == id);
            Assert(count == expected,
                $"OTFT fixture '{name}' reopened with {count}, expected {expected}.");
            Assert(rawCount == expected,
                $"OTFT fixture '{name}' raw-reopened with {rawCount}, expected {expected}.");
            return;
        }

        if (name.StartsWith("pack-location-", StringComparison.Ordinal))
        {
            Package[] packages = output.Packages
                .Where(row => row.FormKey.ModKey == fixture.PluginKey &&
                              row.FormKey.ID == GetAllocationId(
                                  fixture.Proposal, "PACK"))
                .ToArray();
            Assert(packages.Length == 1,
                $"PACK location fixture '{name}' lost its self-owned PACK record.");
            AssertPackageLocations(packages[0], expected, name);
            return;
        }

        if (name.StartsWith("pack-condition-", StringComparison.Ordinal))
        {
            Package[] packages = output.Packages
                .Where(row => row.FormKey.ModKey == fixture.PluginKey &&
                              row.FormKey.ID == GetAllocationId(
                                  fixture.Proposal, "PACK"))
                .ToArray();
            Assert(packages.Length == 1,
                $"PACK condition fixture '{name}' lost its self-owned PACK record.");
            Assert(packages[0].Conditions.Count == expected,
                $"PACK condition fixture '{name}' reopened with {packages[0].Conditions.Count}, expected {expected}.");
            return;
        }

        if (name.StartsWith("pack-", StringComparison.Ordinal))
        {
            uint id = GetAllocationId(fixture.Proposal, "PACK");
            int count = output.Packages.Count(row =>
                row.FormKey.ModKey == fixture.PluginKey && row.FormKey.ID == id);
            int rawCount = raw.Count(row => row.Signature == "PACK" &&
                row.LocalFormId == id);
            Assert(count == expected,
                $"PACK fixture '{name}' reopened with {count}, expected {expected}.");
            Assert(rawCount == expected,
                $"PACK fixture '{name}' raw-reopened with {rawCount}, expected {expected}.");
            return;
        }

        throw new InvalidOperationException(
            $"No independent reopen assertion exists for output fixture '{name}'.");
    }

    private static void AssertPackageLocations(
        Package package,
        int expected,
        string name)
    {
        PackageDataLocation[] locations = package.Data.Values
            .OfType<PackageDataLocation>()
            .ToArray();
        sbyte[] keys = package.Data
            .Where(pair => pair.Value is PackageDataLocation)
            .Select(pair => pair.Key)
            .ToArray();
        Assert(
            locations.Length == expected &&
            keys.Length == expected &&
            keys.Distinct().Count() == expected &&
            locations.All(location => location.Location is LocationTargetRadius
            {
                Radius: > 0
            }),
            $"PACK location fixture '{name}' did not reopen with {expected} distinct valid PackageDataLocation keys.");
    }

    private static uint GetAllocationId(
        SkyrimNpcFinishCoreProposal proposal,
        string signature)
    {
        string[] rows = proposal.NewRecords
            .Where(value => value.StartsWith(signature + " ", StringComparison.Ordinal))
            .ToArray();
        Assert(rows.Length == 1,
            $"The test proposal did not contain exactly one {signature} allocation.");
        string value = rows[0].AsSpan(signature.Length + 3).ToString();
        if (value.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            value = value[2..];
        return uint.Parse(value.AsSpan(),
            System.Globalization.NumberStyles.AllowHexSpecifier,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static void AssertStableExactOneDiagnostic(
        IEnumerable<Diagnostic> diagnostics,
        string code,
        string identity,
        string observed,
        string name)
    {
        Diagnostic? diagnostic = diagnostics.FirstOrDefault(item => item.Code == code);
        Assert(
            diagnostic is not null &&
            diagnostic.Message.Contains(identity, StringComparison.Ordinal) &&
            diagnostic.Message.Contains("expected=1", StringComparison.Ordinal) &&
            diagnostic.Message.Contains(observed, StringComparison.Ordinal) &&
            !diagnostics.Any(item => item.Code == "finish-core-verify-exception"),
            $"Exact-one case '{name}' did not produce stable code/identity/expected/observed evidence: " +
            string.Join(" | ", diagnostics.Select(item => item.Code + ":" + item.Message)));
    }

    private static void AssertStableWriterExactOne(
        Action action,
        string code,
        string identity,
        string observed,
        string name)
    {
        try
        {
            action();
        }
        catch (InvalidDataException exception)
        {
            Assert(
                exception.Message.Contains(code, StringComparison.Ordinal) &&
                exception.Message.Contains(identity, StringComparison.Ordinal) &&
                exception.Message.Contains("expected=1", StringComparison.Ordinal) &&
                exception.Message.Contains(observed, StringComparison.Ordinal),
                $"Writer exact-one case '{name}' omitted stable evidence: {exception.Message}");
            return;
        }
        throw new InvalidOperationException(
            $"Writer exact-one case '{name}' was accepted without a refusal.");
    }

    private static void AddDistinctPackageLocation(Package package, SkyrimMod mod)
    {
        PackageDataLocation existing = package.Data.Values
            .OfType<PackageDataLocation>()
            .Single();
        PackageDataLocation duplicate = existing.DeepCopy();
        switch (duplicate.Location.Target)
        {
            case LocationTarget target:
                target.Link.SetTo(new FormKey(mod.ModKey, 0x00000900));
                break;
            case LocationFallback fallback:
                fallback.Data++;
                break;
            default:
                throw new InvalidDataException(
                    "The test fixture's duplicate PACK locations are not mutable targets.");
        }
        package.Data[1] = duplicate;
    }

    internal static void WriteFinishCoreBinaryPlugin(SkyrimMod mod, string path) =>
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });

    private static string PerRowPluginPath(string root, string row)
    {
        string directory = Path.Combine(root, row);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "BrigitteBardotNpcManager.esp");
    }

    private static string PerRowMasterPath(string root, string row)
    {
        string directory = Path.Combine(root, row);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "Skyrim.esm");
    }

    private static Sha256Hash HashBinaryFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static Mutagen.Bethesda.Skyrim.Mood ToMutagenMood(
        SkyrimNpcFinishCoreMood mood) =>
        mood switch
        {
            SkyrimNpcFinishCoreMood.Neutral => Mutagen.Bethesda.Skyrim.Mood.Neutral,
            SkyrimNpcFinishCoreMood.Angry => Mutagen.Bethesda.Skyrim.Mood.Angry,
            SkyrimNpcFinishCoreMood.Fear => Mutagen.Bethesda.Skyrim.Mood.Fear,
            SkyrimNpcFinishCoreMood.Happy => Mutagen.Bethesda.Skyrim.Mood.Happy,
            SkyrimNpcFinishCoreMood.Sad => Mutagen.Bethesda.Skyrim.Mood.Sad,
            SkyrimNpcFinishCoreMood.Surprise => Mutagen.Bethesda.Skyrim.Mood.Surprised,
            SkyrimNpcFinishCoreMood.Puzzled => Mutagen.Bethesda.Skyrim.Mood.Puzzled,
            SkyrimNpcFinishCoreMood.Disgusted => Mutagen.Bethesda.Skyrim.Mood.Disgusted,
            _ => throw new InvalidDataException(
                $"Unsupported Finish Core mood '{mood}'.")
        };

    private static FormKey ToFinishCoreFormKey(FormReference value) =>
        new(ModKey.FromNameAndExtension(value.Plugin.Value), value.FormId.Value);

    private static SkyrimNpcFinishCoreProposal RehashProposal(
        SkyrimNpcFinishCoreProposal proposal,
        WorkspacePath projectRoot)
    {
        byte[] withoutSelf = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal with { ProposalSha256 = null }, projectRoot);
        return proposal with
        {
            ProposalSha256 = SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                withoutSelf)
        };
    }

    private static void AssertProtectedRecordsIdentical(
        byte[] source,
        byte[] output,
        uint actorFormId)
    {
        Dictionary<string, byte[]> before = ReadRecords(source)
            .Where(row => row.Signature != "TES4" &&
                          !(row.Signature == "NPC_" && row.LocalFormId == actorFormId))
            .ToDictionary(row => row.Signature + ":" + row.LocalFormId.ToString("X8"), row => row.Bytes);
        Dictionary<string, byte[]> after = ReadRecords(output)
            .Where(row => row.Signature != "TES4" &&
                          !(row.Signature == "NPC_" && row.LocalFormId == actorFormId))
            .ToDictionary(row => row.Signature + ":" + row.LocalFormId.ToString("X8"), row => row.Bytes);
        foreach ((string key, byte[] bytes) in before)
            Assert(after.TryGetValue(key, out byte[]? actual) && bytes.AsSpan().SequenceEqual(actual),
                $"Source record {key} was not byte-preserved.");
    }

    private static int FindRecord(byte[] bytes, string signature, uint localFormId)
    {
        foreach (RawRecord row in ReadRecords(bytes))
            if (row.Signature == signature && row.LocalFormId == localFormId)
                return row.Offset;
        throw new InvalidDataException($"Missing {signature} 0x{localFormId:X8}.");
    }

    private static int FindSubrecord(byte[] bytes, int recordOffset, string signature)
    {
        int position = recordOffset + 24;
        int end = position + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(recordOffset + 4, 4)));
        while (position < end)
        {
            string actual = Encoding.ASCII.GetString(bytes, position, 4);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2));
            if (actual == signature)
                return position;
            position += 6 + size;
        }
        throw new InvalidDataException($"Missing {signature} subrecord.");
    }

    private sealed record RawDuplicateAppendEvidence(
        uint Tes4RecordCountBefore,
        uint Tes4RecordCountAfter,
        int MajorRecordCountBefore,
        int MajorRecordCountAfter,
        int TopLevelGroupCountBefore,
        int TopLevelGroupCountAfter);

    private sealed record RawRecord(
        string Signature,
        uint LocalFormId,
        int Offset,
        byte[] Bytes);

    private static ImmutableArray<RawRecord> ReadRecords(byte[] bytes)
    {
        var rows = ImmutableArray.CreateBuilder<RawRecord>();
        int first = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        Walk(first, bytes.Length);
        return rows.ToImmutable();

        void Walk(int start, int end)
        {
            int position = start;
            while (position < end)
            {
                string signature = Encoding.ASCII.GetString(bytes, position, 4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
                if (signature == "GRUP")
                {
                    Walk(position + 24, checked(position + (int)size));
                    position = checked(position + (int)size);
                    continue;
                }
                int length = checked(24 + (int)size);
                uint formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4));
                rows.Add(new RawRecord(signature, formId & 0x00FF_FFFFu, position,
                    bytes.AsSpan(position, length).ToArray()));
                position += length;
            }
        }
    }

    internal sealed class FinishCoreBinaryFixture : IAsyncDisposable
    {
        private FinishCoreBinaryFixture(
            string root,
            WorkspacePath sourcePlugin,
            WorkspacePath copiedMaster,
            SkyrimNpcFinishCoreProposal proposal)
        {
            Root = root;
            SourcePlugin = sourcePlugin;
            CopiedMaster = copiedMaster;
            Proposal = proposal;
        }

        internal string Root { get; }
        internal WorkspacePath SourcePlugin { get; }
        internal WorkspacePath CopiedMaster { get; }
        internal SkyrimNpcFinishCoreProposal Proposal { get; }
        internal ModKey PluginKey => ModKey.FromNameAndExtension(SourcePlugin.Value);
        internal static FormId ActorFormId => new(0x800);

        internal static async Task<FinishCoreBinaryFixture> CreateAsync()
        {
            SkyrimFinishMasterFixture.EnsureAvailable();
            string root = Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "finish-core-binary-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string sourceDirectory = Path.Combine(root, "source");
            string dataDirectory = Path.Combine(sourceDirectory, "Data");
            Directory.CreateDirectory(dataDirectory);
            string sourcePath = Path.Combine(dataDirectory, "BrigitteBardotNpcManager.esp");
            WriteSourcePlugin(sourcePath);
            string faceGeom = Path.Combine(
                dataDirectory, "meshes", "actors", "character", "FaceGenData", "FaceGeom",
                "BrigitteBardotNpcManager.esp", "00000800.nif");
            string faceTint = Path.Combine(
                dataDirectory, "textures", "actors", "character", "FaceGenData", "FaceTint",
                "BrigitteBardotNpcManager.esp", "00000800.dds");
            Directory.CreateDirectory(Path.GetDirectoryName(faceGeom)!);
            Directory.CreateDirectory(Path.GetDirectoryName(faceTint)!);
            await File.WriteAllBytesAsync(faceGeom, [0x4E, 0x49, 0x46]);
            await File.WriteAllBytesAsync(faceTint, [0x44, 0x44, 0x53]);
            string masterPath = Path.Combine(sourceDirectory, "Skyrim.esm");
            SkyrimFollowerFinishCoreFixture.WriteCanonicalFollowerFinishTemplateMaster(masterPath);
            var outfitMaster = SkyrimMod.CreateFromBinary(new ModPath(ModKey.FromNameAndExtension("Skyrim.esm"),
                new FilePath(masterPath)), SkyrimRelease.SkyrimSE);
            outfitMaster.Races.Add(new Race(new FormKey(outfitMaster.ModKey, 0x13746), SkyrimRelease.SkyrimSE));
            outfitMaster.Armors.Add(new Armor(new FormKey(outfitMaster.ModKey, 0x800), SkyrimRelease.SkyrimSE));
            outfitMaster.Armors.Add(new Armor(new FormKey(outfitMaster.ModKey, 0x801), SkyrimRelease.SkyrimSE));
            WriteCombatFixture(outfitMaster, masterPath);
            var plugin = new PluginName("BrigitteBardotNpcManager.esp");
            var source = BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                new WorkspacePath(sourcePath), plugin, FinishCoreBinaryFixture.ActorFormId,
                new EditorId("BrigitteBardotNpcManager"), CancellationToken.None);
            Assert(source.Admitted, "The real Finish Core source fixture was refused.");
            string manifestPath = Path.Combine(sourceDirectory, "npcmanager-package.json");
            await File.WriteAllTextAsync(manifestPath, System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1, edition = "SkyrimSe", presetFormat = "RaceMenu",
                sourcePreset = "synthetic.jslot", sourcePresetSha256 = Hash("synthetic-preset").Value,
                sourcePlugin = plugin.Value, sourcePluginSha256 = source.PluginSha256.Value,
                outputPlugin = plugin.Value, targetFormId = ActorFormId.ToString(),
                artifacts = Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories)
                    .Order(StringComparer.Ordinal).Select(path => new
                    {
                        kind = path == sourcePath ? "plugin" : "synthetic-input",
                        relativePath = Path.GetRelativePath(sourceDirectory, path).Replace('\\', '/'),
                        byteLength = new FileInfo(path).Length,
                        sha256 = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                    }).ToArray()
            }));
            var request = new SkyrimNpcFinishCoreRequest
            {
                Source = new SkyrimNpcFinishCoreSource
                {
                    PackageRoot = new WorkspacePath(sourceDirectory),
                    PackageManifest = new WorkspacePath(Path.Combine(sourceDirectory, "npcmanager-package.json")),
                    PackageManifestSha256 = new Sha256Hash(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(manifestPath)))),
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader
                        .ComputePackageTreeSha256(new WorkspacePath(sourceDirectory)),
                    PluginPath = new WorkspacePath(sourcePath),
                    Plugin = plugin,
                    PluginSha256 = source.PluginSha256
                },
                Actor = new SkyrimNpcFinishCoreActor
                {
                    EditorId = new EditorId("BrigitteBardotNpcManager"),
                    FormId = new FormId(0x800)
                },
                Authorities = new SkyrimNpcFinishCoreAuthorities
                {
                    BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba
                },
                AiPolicy = new SkyrimNpcFinishCoreAiPolicy
                {
                    Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                    Confidence = SkyrimNpcFinishCoreConfidence.Brave,
                    Energy = 50,
                    Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                    Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
                    Mood = SkyrimNpcFinishCoreMood.Neutral
                },
                OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
                {
                    Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit,
                    ArmorItems = [
                        new FormReference(new PluginName("Skyrim.esm"), new FormId(0x00000800)),
                        new FormReference(new PluginName("Skyrim.esm"), new FormId(0x00000801))
                    ]
                },
                InventoryPolicy = new SkyrimNpcFinishCoreInventoryPolicyDocument
                {
                    Policy = SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory
                },
                SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
                {
                    Template = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                    TemplateEditorId = "DefaultSandboxEditorLocation512",
                    CopiedMaster = new WorkspacePath(masterPath),
                    CopiedMasterSha256 = HashFile(masterPath)
                },
                Output = new SkyrimNpcFinishCoreOutput
                {
                    Root = new WorkspacePath(Path.Combine(root, "output")),
                    Archive = new WorkspacePath(Path.Combine(root, "output.zip")),
                    PluginFileName = plugin.Value
                }
            };
            var sourceResult = new SkyrimNpcFinishCoreSourceReadResult(
                true, null, request.Source.PackageTreeSha256!.Value,
                source.PluginSha256, source.BaseNpc, source.TargetEditorId,
                true, "None", source.TypedForbiddenCounts,
                source.RawForbiddenCounts, source.Diagnostics)
            {
                NextFormId = new FormId(source.NextFormId),
                Tes4Flags = source.Tes4Flags,
                MasterOrder = source.MasterOrder,
                OccupiedIds = source.OccupiedIds,
                TargetConfigurationFlags = source.TargetConfigurationFlags,
                FactionRanks = source.FactionRanks,
                CombatStyle = source.CombatStyle,
                CombatStyleMatchesDefensiveContract = source.CombatStyleMatchesDefensiveContract,
                DefaultOutfit = source.DefaultOutfit,
                Inventory = source.Inventory,
                PackageLinks = source.PackageLinks,
                Relationships = source.Relationships,
                AiData = source.AiData,
                SemanticSurfaceValues = []
            };
            var service = new NpcManager.Pipeline.SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(sourceResult),
                new WorkspacePath(root));
            WorkspacePath proposalPath = new(Path.Combine(root, "proposal.json"));
            Sha256Hash requestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                request, new WorkspacePath(root));
            SkyrimNpcFinishCoreProposalResult result = await service.AnalyzeAsync(
                request, requestSha, proposalPath, CancellationToken.None);
            Assert(result.Proposed && result.Proposal is not null,
                "The real Finish Core source did not produce a proposal: " +
                string.Join(" | ", result.Diagnostics.Select(x => x.Code + ":" + x.Message)));
            Assert(result.Proposal!.NewRecords.SequenceEqual(["CSTY 0x00000805", "OTFT 0x00000806", "PACK 0x00000807"]),
                "The proposal did not reuse the admitted factions/relationship or allocate deterministic records.");
            return new FinishCoreBinaryFixture(
                root, new WorkspacePath(sourcePath), new WorkspacePath(masterPath), result.Proposal!);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }

        private static Sha256Hash Hash(string value) =>
            new(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))));

        private static Sha256Hash HashFile(string path) =>
            new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

        internal static void WriteSourcePlugin(string path) =>
            WriteSourcePlugin(path, Mutagen.Bethesda.Skyrim.Mood.Neutral);

        internal static void WriteSourcePlugin(
            string path,
            Mutagen.Bethesda.Skyrim.Mood mood)
        {
            ModKey plugin = ModKey.FromNameAndExtension("BrigitteBardotNpcManager.esp");
            ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
            var mod = new SkyrimMod(plugin, SkyrimRelease.SkyrimSE)
            {
                IsSmallMaster = true
            };
            mod.ModHeader.MasterReferences.Add(new MasterReference { Master = skyrim });
            mod.ModHeader.Stats.NextFormID = 0x805;
            var npcKey = new FormKey(plugin, 0x800);
            var npc = new Npc(npcKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "BrigitteBardotNpcManager",
                Name = "Fixture follower",
                Race = new FormLink<IRaceGetter>(new FormKey(skyrim, 0x13746)),
                Configuration = new NpcConfiguration
                {
                    Flags = (NpcConfiguration.Flag)(
                        (uint)NpcConfiguration.Flag.Female | 0x00000001u),
                    HealthOffset = 10
                },
                AIData = new AIData
                {
                    Aggression = Aggression.Unaggressive,
                    Confidence = Confidence.Cowardly,
                    EnergyLevel = 10,
                    Responsibility = Responsibility.AnyCrime,
                    Assistance = Assistance.HelpsNobody,
                    Mood = mood
                },
                Weight = 50f
            };
            npc.Factions.Add(new RankPlacement
            {
                Faction = new FormLink<IFactionGetter>(new FormKey(skyrim, 0x5C84D)),
                Rank = 0
            });
            npc.Factions.Add(new RankPlacement
            {
                Faction = new FormLink<IFactionGetter>(new FormKey(skyrim, 0x5C84E)),
                Rank = -1
            });
            mod.Npcs.Add(npc);
            mod.Relationships.Add(new Relationship(
                new FormKey(plugin, 0x804), SkyrimRelease.SkyrimSE)
            {
                EditorID = "BrigitteBardotNpcManagerPlayerAllyRELA",
                Parent = new FormLink<INpcGetter>(npcKey),
                Child = new FormLink<INpcGetter>(new FormKey(skyrim, 0x7)),
                Rank = Relationship.RankType.Ally,
                Unknown = 0,
                Flags = 0,
                AssociationType = new FormLink<IAssociationTypeGetter>(FormKey.Null)
            });
            mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
        }
    }
}
