using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reopens a created plugin through Mutagen and a separate bounded TES4 byte
/// walker. Neither a successful write nor one reader alone is accepted.
/// </summary>
public static partial class BethesdaNpcCreationVerifier
{
    private const int RecordHeaderSize = 24;
    private const uint CompressedRecordFlag = 0x0004_0000;
    private const uint MasterPluginFlag = 0x0000_0001;
    private const uint LightPluginFlag = 0x0000_0200;
    private const long MaximumPluginBytes = 16 * 1024 * 1024;

    public static NpcCreationVerificationResult Verify(
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Sha256Hash? proposalHash = null;
        Sha256Hash? outputHash = null;
        RawPluginSnapshot? raw = null;
        ImmutableArray<PluginName> typedMasters = [];
        var typedMajorCount = 0;
        var typedNpcCount = 0;
        var typedOrdinary = false;

        try
        {
            proposalHash = HashFile(request.Proposal.Value);
            if (proposal.ProposalHash is null || proposalHash != proposal.ProposalHash)
            {
                diagnostics.Add(Error("npc-create-proposal-hash-mismatch",
                    "The persisted proposal hash no longer matches the analyzed proposal."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error("npc-create-proposal-read-failed", exception.Message));
        }

        if (!File.Exists(request.Output.Value))
        {
            diagnostics.Add(Error("npc-create-output-missing", "The created plugin does not exist."));
            return BuildResult(false, request, proposal, proposalHash, null, [], null, 0, 0, false,
                diagnostics.ToImmutable());
        }

        try
        {
            var info = new FileInfo(request.Output.Value);
            if (info.Length <= 0 || info.Length > MaximumPluginBytes)
            {
                throw new InvalidDataException("The created plugin is empty or exceeds the bounded verifier size.");
            }

            outputHash = HashFile(request.Output.Value);
            var bytes = File.ReadAllBytes(request.Output.Value);
            raw = ReadRaw(bytes, cancellationToken);
            VerifyRaw(raw, request, proposal, diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or OverflowException)
        {
            diagnostics.Add(Error("npc-create-raw-readback-failed", exception.Message));
        }

        try
        {
            var outputModKey = ModKey.FromNameAndExtension(proposal.OutputPlugin.Value);
            using var overlay = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputModKey, new FilePath(request.Output.Value)),
                SkyrimRelease.SkyrimSE);
            typedMasters = overlay.ModHeader.MasterReferences
                .Select(item => new PluginName(item.Master.ToString()))
                .ToImmutableArray();
            typedMajorCount = overlay.EnumerateMajorRecords().Count();
            typedNpcCount = overlay.Npcs.Count;
            bool expectLight = request.PluginType == BlankNpcPluginType.Espfe;
            bool typedEsp = !overlay.IsMaster &&
                request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase);
            typedOrdinary = typedEsp && !overlay.IsSmallMaster;
            if (typedEsp && overlay.IsSmallMaster != expectLight)
            {
                diagnostics.Add(Error("npc-create-light-flag-mismatch",
                    expectLight
                        ? "The output did not read back as a light-flagged (.esp, TES4 0x200) plugin."
                        : "The output read back with the TES4 light flag that the request did not ask for."));
            }
            if (expectLight)
            {
                uint[] oversized = overlay.EnumerateMajorRecords()
                    .Where(record => record.FormKey.ModKey == outputModKey &&
                                     record.FormKey.ID > BlankNpcOutputPolicy.LightLocalFormIdLimit)
                    .Select(record => record.FormKey.ID).ToArray();
                if (oversized.Length > 0)
                {
                    diagnostics.Add(Error(BlankNpcOutputPolicy.LightBudgetDiagnosticCode,
                        "The light plugin owns local FormIDs above 0xFFF: " +
                        string.Join(", ", oversized.Select(id => $"0x{id:X}")) + "."));
                }
            }

            if (!typedMasters.SequenceEqual(proposal.Masters))
            {
                diagnostics.Add(Error("npc-create-typed-master-mismatch",
                    "Typed read-back did not preserve the proposal's exact master order."));
            }
            var expectedMajorCount = BethesdaNpcCreationAdapter.ExpectedMajorRecordCountFor(
                request.Appearance, request.Traits.Role);
            if (typedMajorCount != expectedMajorCount || typedNpcCount != 1)
            {
                diagnostics.Add(Error("npc-create-record-surface-mismatch",
                    $"The output contains {typedMajorCount} major records and {typedNpcCount} NPC records; expected {expectedMajorCount} total records and exactly one NPC."));
            }
            if (!typedEsp)
            {
                diagnostics.Add(Error("npc-create-not-ordinary-esp",
                    "The output is not an ordinary non-master .esp plugin."));
            }

            var targetKey = new FormKey(outputModKey, proposal.AllocatedFormId.Value);
            var npc = overlay.Npcs.FirstOrDefault(item => item.FormKey == targetKey);
            if (npc is null)
            {
                diagnostics.Add(Error("npc-create-output-ownership-mismatch",
                    $"The output does not own NPC {proposal.AllocatedFormId}."));
            }
            else
            {
                VerifyNpc(npc, request, diagnostics);
                VerifyTypedRole(overlay, npc, request, proposal, diagnostics);
                VerifyTypedAppearance(overlay, npc, request, proposal, diagnostics);
                VerifyTypedRuntimeAppearance(npc, request.RuntimeAppearance, diagnostics);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            InvalidDataException or ArgumentException or KeyNotFoundException or OverflowException or
            NotSupportedException)
        {
            diagnostics.Add(Error("npc-create-typed-readback-failed", exception.Message));
        }

        if (raw is not null && !raw.Masters.SequenceEqual(typedMasters))
        {
            diagnostics.Add(Error("npc-create-reader-disagreement",
                "The raw and typed readers disagree about the output master list."));
        }

        var isValid = !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
        return BuildResult(isValid, request, proposal, proposalHash, outputHash,
            typedMasters.IsDefaultOrEmpty && raw is not null ? raw.Masters : typedMasters,
            raw?.HeaderVersion, typedMajorCount, typedNpcCount,
            typedOrdinary && raw is not null && IsOrdinaryEsp(raw.HeaderFlags),
            diagnostics.ToImmutable(), raw?.NextFormId);
    }

    private static void VerifyNpc(
        INpcGetter npc,
        NpcCreationRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        Check(string.Equals(npc.EditorID, request.Identity.EditorId.Value, StringComparison.Ordinal),
            "npc-create-editor-id-mismatch", "The created NPC EditorID does not match the request.", diagnostics);
        Check(string.Equals(npc.Name?.String, request.Identity.Name.Value, StringComparison.Ordinal),
            "npc-create-name-mismatch", "The created NPC display name does not match the request.", diagnostics);
        Check(npc.FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion,
            "npc-create-form-version-mismatch", "The created NPC FormVersion is not 44.", diagnostics);
        Check(npc.Race.FormKey == ToFormKey(request.References.Race),
            "npc-create-race-mismatch", "The created NPC race link does not match the request.", diagnostics);
        Check(npc.Voice.FormKeyNullable == ToFormKey(request.References.Voice),
            "npc-create-voice-mismatch", "The created NPC voice link does not match the request.", diagnostics);
        Check(npc.Class.FormKey == ToFormKey(request.References.Class),
            "npc-create-class-mismatch", "The created NPC class link does not match the request.", diagnostics);
        Check(npc.CombatStyle.FormKeyNullable == ToFormKey(request.References.CombatStyle),
            "npc-create-combat-style-mismatch", "The created NPC combat-style link does not match the request.", diagnostics);
        FormKey? expectedOutfit = request.Appearance is
            FullyAuthoredSkyrimNpcAppearanceSource
            {
                ExposedOutfitSkinBinding: { } binding
            }
            ? new FormKey(
                ModKey.FromNameAndExtension(
                    Path.GetFileName(request.Output.Value)),
                binding.AllocatedOutfitLocalFormId.Value)
            : request.References.DefaultOutfit is { } outfit
                ? ToFormKey(outfit)
                : null;
        Check(npc.DefaultOutfit.FormKeyNullable == expectedOutfit,
            "npc-create-default-outfit-mismatch", "The created NPC default outfit link does not match the request.", diagnostics);
        var flags = npc.Configuration.Flags;
        Check(flags.HasFlag(NpcConfiguration.Flag.Female) == (request.Traits.Sex == NpcSex.Female),
            "npc-create-sex-mismatch", "The created NPC sex flag does not match the request.", diagnostics);
        Check(flags.HasFlag(NpcConfiguration.Flag.Unique) == request.Traits.IsUnique,
            "npc-create-unique-mismatch", "The created NPC unique flag does not match the request.", diagnostics);
        Check(flags.HasFlag(NpcConfiguration.Flag.Essential) == request.Traits.IsEssential,
            "npc-create-essential-mismatch", "The created NPC essential flag does not match the request.", diagnostics);
        Check(flags.HasFlag(NpcConfiguration.Flag.Protected) == request.Traits.IsProtected,
            "npc-create-protected-mismatch", "The created NPC protected flag does not match the request.", diagnostics);
        Check(flags.HasFlag(NpcConfiguration.Flag.Respawn) == request.Traits.Respawns,
            "npc-create-respawn-mismatch", "The created NPC respawn flag does not match the request.", diagnostics);
        Check(flags.HasFlag(NpcConfiguration.Flag.AutoCalcStats) == request.Traits.AutoCalcStats,
            "npc-create-auto-calc-mismatch", "The created NPC auto-calc flag does not match the request.", diagnostics);
        Check(npc.Configuration.TemplateFlags == 0 && npc.Template.IsNull,
            "npc-create-template-inheritance", "The created NPC still inherits template categories.", diagnostics);

        Check(LevelMatches(npc.Configuration.Level, request.Stats.Level),
            "npc-create-level-mismatch", "The created NPC level does not match the request.", diagnostics);
        Check(npc.Configuration.MagickaOffset == request.Stats.MagickaOffset &&
              npc.Configuration.StaminaOffset == request.Stats.StaminaOffset &&
              npc.Configuration.HealthOffset == request.Stats.HealthOffset,
            "npc-create-stat-offset-mismatch", "The created NPC stat offsets do not match the request.", diagnostics);
        Check(unchecked((ushort)npc.Configuration.CalcMinLevel) == request.Stats.CalcMinLevel &&
              unchecked((ushort)npc.Configuration.CalcMaxLevel) == request.Stats.CalcMaxLevel,
            "npc-create-level-bounds-mismatch", "The created NPC level bounds do not match the request.", diagnostics);
        Check(npc.Configuration.SpeedMultiplier == request.Stats.SpeedMultiplier &&
              npc.Configuration.DispositionBase == request.Stats.DispositionBase &&
              npc.Configuration.BleedoutOverride == request.Stats.BleedoutOverride,
            "npc-create-config-mismatch", "The created NPC configuration values do not match the request.", diagnostics);
        Check(npc.PlayerSkills is not null &&
              npc.PlayerSkills.Health == request.Stats.BaseHealth &&
              npc.PlayerSkills.Magicka == request.Stats.BaseMagicka &&
              npc.PlayerSkills.Stamina == request.Stats.BaseStamina,
            "npc-create-base-stats-mismatch", "The created NPC base actor values do not match the request.", diagnostics);
        Check(Math.Abs(npc.Height - request.Stats.Height) < 0.000001f &&
              Math.Abs(npc.Weight - request.Stats.Weight) < 0.000001f,
            "npc-create-height-weight-mismatch", "The created NPC height or weight does not match the request.", diagnostics);
        Check(npc.NAM5 == request.Stats.FarAwayModelDistance,
            "npc-create-nam5-mismatch", "The created NPC NAM5 value does not match the request.", diagnostics);
        if (request.Appearance is TemplateCarrierNpcAppearanceSource)
        {
            Check(npc.TextureLighting is not null && npc.TintLayers.Count > 0 && npc.HeadParts.Count > 0 &&
                  !npc.HairColor.IsNull && !npc.HeadTexture.IsNull,
                "npc-create-appearance-baseline-missing",
                "The created NPC lost required carrier appearance fields (QNAM, tint, headparts, hair color, or head texture).",
                diagnostics);
        }
    }

    private static void VerifyRaw(
        RawPluginSnapshot raw,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        bool expectLight = request.PluginType == BlankNpcPluginType.Espfe;
        Check((raw.HeaderFlags & MasterPluginFlag) == 0, "npc-create-raw-not-ordinary-esp",
            "The raw TES4 flags identify a master plugin.", diagnostics);
        Check(((raw.HeaderFlags & LightPluginFlag) != 0) == expectLight, "npc-create-raw-light-flag-mismatch",
            expectLight
                ? "The raw TES4 flags do not carry the requested light flag (0x200)."
                : "The raw TES4 flags carry a light flag (0x200) the request did not ask for.", diagnostics);
        if (expectLight)
        {
            uint ownerIndex = checked((uint)proposal.Masters.Length << 24);
            Check(raw.Records.All(record =>
                    (record.FormId & 0xFF000000u) != ownerIndex ||
                    (record.FormId & 0x00FFFFFFu) <= BlankNpcOutputPolicy.LightLocalFormIdLimit),
                BlankNpcOutputPolicy.LightBudgetDiagnosticCode,
                "The raw light plugin owns a local FormID above 0xFFF; FormIDs are never compacted.", diagnostics);
        }
        Check(Math.Abs(raw.HeaderVersion - BethesdaNpcCreationAdapter.HeaderVersion) < 0.0001f,
            "npc-create-header-version-mismatch", "The raw HEDR version is not 1.7.", diagnostics);
        var expectedNextFormId = BethesdaNpcCreationAdapter.ExpectedNextFormIdFor(
            request.Appearance, request.Traits.Role);
        Check(raw.NextFormId.Value == expectedNextFormId,
            "npc-create-next-form-id-mismatch",
            $"The raw HEDR NextFormID is 0x{raw.NextFormId.Value:X8}; expected 0x{expectedNextFormId:X8}.", diagnostics);
        var expectedMajorCount = BethesdaNpcCreationAdapter.ExpectedMajorRecordCountFor(
            request.Appearance, request.Traits.Role);
        var expectedTopGroupCount = BethesdaNpcCreationAdapter.ExpectedTopGroupCountFor(
            request.Appearance, request.Traits.Role);
        var expectedHeaderRecordCount = checked((uint)(expectedMajorCount + expectedTopGroupCount));
        Check(raw.HeaderRecordCount == expectedHeaderRecordCount,
            "npc-create-header-record-count-mismatch",
            $"The raw HEDR record count is {raw.HeaderRecordCount}; expected {expectedHeaderRecordCount} groups and records.", diagnostics);
        Check(raw.Masters.SequenceEqual(proposal.Masters),
            "npc-create-raw-master-mismatch", "The raw TES4 master list does not match the exact proposal order.", diagnostics);
        Check(raw.MajorRecordCount == expectedMajorCount && raw.NpcRecordCount == 1 &&
              HasExpectedRawSurface(raw, request.Appearance, request.Traits.Role),
            "npc-create-raw-record-surface-mismatch",
            "The raw output record and top-level group surface does not match the requested NPC appearance and role.", diagnostics);
        var expectedRawFormId = checked((uint)(proposal.Masters.Length << 24)) | proposal.AllocatedFormId.Value;
        Check(raw.NpcFormId == expectedRawFormId,
            "npc-create-raw-output-ownership-mismatch",
            $"The raw NPC FormID is 0x{raw.NpcFormId:X8}; expected output-owned 0x{expectedRawFormId:X8}.", diagnostics);
        Check(raw.NpcFormVersion == BethesdaNpcCreationAdapter.RecordFormVersion,
            "npc-create-raw-form-version-mismatch", "The raw NPC FormVersion is not 44.", diagnostics);
        Check(raw.HasQnam && raw.Nam5 is not null && raw.Height is not null &&
              (request.Appearance is not TemplateCarrierNpcAppearanceSource || raw.HasSkinTint),
            "npc-create-raw-required-subrecord-missing",
            "The raw NPC is missing QNAM, NAM5, NAM6, or the template carrier's index-zero skin tint.", diagnostics);
        Check(raw.Nam5 == request.Stats.FarAwayModelDistance,
            "npc-create-raw-nam5-mismatch", "The raw NPC NAM5 value does not match the request.", diagnostics);
        Check(raw.Height is { } height && Math.Abs(height - request.Stats.Height) < 0.000001f,
            "npc-create-raw-nam6-mismatch", "The raw NPC NAM6 height does not match the request.", diagnostics);
        VerifyRawRole(raw, request, proposal, diagnostics);
        VerifyRawAppearance(raw, request, proposal, diagnostics);
        VerifyRawRuntimeAppearance(raw, request.RuntimeAppearance, diagnostics);
    }

    private static void VerifyTypedRole(
        ISkyrimModGetter overlay,
        INpcGetter npc,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var potentialFollowerFaction = new FormKey(
            skyrim, BethesdaNpcCreationAdapter.PotentialFollowerFactionLocalFormId);
        var currentFollowerFaction = new FormKey(
            skyrim, BethesdaNpcCreationAdapter.CurrentFollowerFactionLocalFormId);
        var actualFollowerFactions = npc.Factions
            .Where(item => item.Faction.FormKey == potentialFollowerFaction ||
                           item.Faction.FormKey == currentFollowerFaction)
            .Select(item => (item.Faction.FormKey, Rank: (int)item.Rank))
            .ToArray();
        var expectedFollowerFactions = request.Traits.Role == NpcCreationRole.Follower
            ? new[]
            {
                (potentialFollowerFaction, BethesdaNpcCreationAdapter.PotentialFollowerFactionRank),
                (currentFollowerFaction, BethesdaNpcCreationAdapter.CurrentFollowerFactionRank)
            }
            : [];
        Check(actualFollowerFactions.SequenceEqual(expectedFollowerFactions),
            "npc-create-typed-role-factions-mismatch",
            "Typed read-back did not preserve the role's exact PotentialFollowerFaction and CurrentFollowerFaction memberships.",
            diagnostics);

        var relationships = overlay.Relationships.ToArray();
        if (request.Traits.Role != NpcCreationRole.Follower)
        {
            Check(relationships.Length == 0,
                "npc-create-typed-role-relationship-mismatch",
                "Typed read-back found an output-owned relationship for a non-follower role.",
                diagnostics);
            return;
        }

        var outputModKey = ModKey.FromNameAndExtension(proposal.OutputPlugin.Value);
        var expectedRelationshipKey = new FormKey(
            outputModKey,
            BethesdaNpcCreationAdapter.FollowerRelationshipLocalFormIdFor(request.Appearance));
        var relationship = relationships.SingleOrDefault(item => item.FormKey == expectedRelationshipKey);
        Check(relationships.Length == 1 && relationship is not null,
            "npc-create-typed-role-relationship-mismatch",
            "Typed read-back did not find exactly one deterministic output-owned follower relationship.",
            diagnostics);
        if (relationship is null) return;

        Check(relationship.FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(relationship.EditorID,
                  BethesdaNpcCreationAdapter.BuildFollowerRelationshipEditorId(
                      request.Identity.EditorId.Value), StringComparison.Ordinal) &&
              relationship.Parent.FormKey == npc.FormKey &&
              relationship.Child.FormKey == new FormKey(
                  skyrim, BethesdaNpcCreationAdapter.PlayerLocalFormId) &&
              relationship.Rank == Relationship.RankType.Ally &&
              relationship.Unknown == 0 && relationship.Flags == 0 &&
              relationship.AssociationType.FormKey == FormKey.Null,
            "npc-create-typed-role-relationship-mismatch",
            "Typed read-back follower RELA does not exactly bind the NPC to Player at Ally rank with no association or flags.",
            diagnostics);
    }

    private static void VerifyRawRole(
        RawPluginSnapshot raw,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var potentialFollowerFaction = ToRawFormId(new FormReference(
                new PluginName("Skyrim.esm"),
                new FormId(BethesdaNpcCreationAdapter.PotentialFollowerFactionLocalFormId)), proposal);
            var currentFollowerFaction = ToRawFormId(new FormReference(
                new PluginName("Skyrim.esm"),
                new FormId(BethesdaNpcCreationAdapter.CurrentFollowerFactionLocalFormId)), proposal);
            var npcRecord = raw.Records.Single(item => item.Signature == "NPC_");
            var actualFollowerFactions = ReadRawFollowerFactions(
                npcRecord.Body.Span, potentialFollowerFaction, currentFollowerFaction);
            var expectedFollowerFactions = request.Traits.Role == NpcCreationRole.Follower
                ? new[]
                {
                    new RawFollowerFaction(
                        potentialFollowerFaction,
                        BethesdaNpcCreationAdapter.PotentialFollowerFactionRank),
                    new RawFollowerFaction(
                        currentFollowerFaction,
                        BethesdaNpcCreationAdapter.CurrentFollowerFactionRank)
                }
                : [];
            Check(actualFollowerFactions.SequenceEqual(expectedFollowerFactions),
                "npc-create-raw-role-factions-mismatch",
                "Raw SNAM data does not contain the role's exact follower faction memberships and ranks.",
                diagnostics);

            var relationshipRecords = raw.Records
                .Where(item => item.Signature == "RELA")
                .ToArray();
            if (request.Traits.Role != NpcCreationRole.Follower)
            {
                Check(relationshipRecords.Length == 0,
                    "npc-create-raw-role-relationship-mismatch",
                    "Raw read-back found a RELA record for a non-follower role.", diagnostics);
                return;
            }

            if (relationshipRecords.Length != 1)
            {
                diagnostics.Add(Error("npc-create-raw-role-relationship-mismatch",
                    "Raw read-back did not find exactly one follower RELA record."));
                return;
            }

            var relationshipRecord = relationshipRecords[0];
            var relationship = ReadRawFollowerRelationship(relationshipRecord.Body.Span);
            var expectedRelationshipFormId = ToRawOutputFormId(
                new FormId(BethesdaNpcCreationAdapter.FollowerRelationshipLocalFormIdFor(
                    request.Appearance)), proposal);
            var expectedParent = ToRawOutputFormId(proposal.AllocatedFormId, proposal);
            var expectedChild = ToRawFormId(new FormReference(
                new PluginName("Skyrim.esm"),
                new FormId(BethesdaNpcCreationAdapter.PlayerLocalFormId)), proposal);
            Check(relationshipRecord.FormId == expectedRelationshipFormId &&
                  relationshipRecord.FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
                  string.Equals(relationship.EditorId,
                      BethesdaNpcCreationAdapter.BuildFollowerRelationshipEditorId(
                          request.Identity.EditorId.Value), StringComparison.Ordinal) &&
                  relationship.Parent == expectedParent && relationship.Child == expectedChild &&
                  relationship.Rank == (short)Relationship.RankType.Ally &&
                  relationship.Unknown == 0 && relationship.Flags == 0 &&
                  relationship.AssociationType == 0,
                "npc-create-raw-role-relationship-mismatch",
                "Raw RELA EDID/DATA does not exactly bind the output NPC to Player at Ally rank with no association or flags.",
                diagnostics);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("npc-create-raw-role-read-failed", exception.Message));
        }
    }

    private static ImmutableArray<RawFollowerFaction> ReadRawFollowerFactions(
        ReadOnlySpan<byte> payload,
        uint potentialFollowerFaction,
        uint currentFollowerFaction)
    {
        var entries = ImmutableArray.CreateBuilder<RawFollowerFaction>();
        ReadSubrecords(payload, (signature, data) =>
        {
            if (signature != "SNAM") return;
            RequireLength(signature, data, 8);
            var faction = BinaryPrimitives.ReadUInt32LittleEndian(data);
            if (faction != potentialFollowerFaction && faction != currentFollowerFaction) return;
            if (data[5..].ContainsAnyExcept((byte)0))
                throw new InvalidDataException("Raw follower SNAM padding is not zero.");
            entries.Add(new RawFollowerFaction(faction, unchecked((sbyte)data[4])));
        });
        return entries.ToImmutable();
    }

    private static RawFollowerRelationship ReadRawFollowerRelationship(
        ReadOnlySpan<byte> payload)
    {
        string? editorId = null;
        RawFollowerRelationship? relationship = null;
        ReadSubrecords(payload, (signature, data) =>
        {
            if (signature == "EDID")
            {
                editorId = ReadUniqueZString(signature, data, editorId);
                return;
            }
            if (signature != "DATA")
                throw new InvalidDataException($"Raw follower RELA contains unexpected {signature} data.");
            RequireLength(signature, data, 16);
            if (relationship is not null) throw Duplicate(signature);
            relationship = new RawFollowerRelationship(
                string.Empty,
                BinaryPrimitives.ReadUInt32LittleEndian(data),
                BinaryPrimitives.ReadUInt32LittleEndian(data[4..]),
                BinaryPrimitives.ReadInt16LittleEndian(data[8..]),
                data[10],
                data[11],
                BinaryPrimitives.ReadUInt32LittleEndian(data[12..]));
        });
        if (editorId is null || relationship is null)
            throw new InvalidDataException("Raw follower RELA is missing EDID or DATA.");
        return relationship with { EditorId = editorId };
    }

    private static RawPluginSnapshot ReadRaw(byte[] bytes, CancellationToken cancellationToken)
    {
        if (bytes.Length < RecordHeaderSize || !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("The output does not start with a TES4 record.");
        var headerPayloadSize = ReadUInt32(bytes, 4);
        var headerEnd = checked(RecordHeaderSize + (int)headerPayloadSize);
        if (headerEnd > bytes.Length) throw new InvalidDataException("The TES4 header exceeds the file.");
        var headerFlags = ReadUInt32(bytes, 8);
        float? version = null;
        uint? recordCount = null;
        uint? nextFormId = null;
        var masters = ImmutableArray.CreateBuilder<PluginName>();
        ReadSubrecords(bytes.AsSpan(RecordHeaderSize, (int)headerPayloadSize), (signature, data) =>
        {
            if (signature == "HEDR")
            {
                if (data.Length != 12) throw new InvalidDataException("HEDR must contain exactly 12 bytes.");
                version = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));
                recordCount = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
                nextFormId = BinaryPrimitives.ReadUInt32LittleEndian(data[8..]);
            }
            else if (signature == "MAST")
            {
                if (data.IsEmpty || data[^1] != 0) throw new InvalidDataException("A MAST name is not NUL terminated.");
                masters.Add(new PluginName(Encoding.Latin1.GetString(data[..^1])));
            }
        });
        if (version is null || recordCount is null || nextFormId is null)
            throw new InvalidDataException("The TES4 header is missing HEDR.");

        var records = ImmutableArray.CreateBuilder<RawMajorRecord>();
        var topGroups = ImmutableArray.CreateBuilder<RawTopGroup>();
        WalkContainer(bytes, headerEnd, bytes.Length, 0, records, topGroups, cancellationToken);
        var npcs = records.Where(item => item.Signature == "NPC_").ToArray();
        var hasQnam = false;
        var hasSkinTint = false;
        ushort? nam5 = null;
        float? height = null;
        if (npcs.Length == 1)
        {
            ushort? tintIndex = null;
            ReadSubrecords(npcs[0].Body.Span, (signature, data) =>
            {
                if (signature == "QNAM" && data.Length == 12)
                {
                    hasQnam = HasFiniteQnam(data);
                }
                else if (signature == "NAM5" && data.Length == 2)
                    nam5 = BinaryPrimitives.ReadUInt16LittleEndian(data);
                else if (signature == "NAM6" && data.Length == 4)
                    height = BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));
                else if (signature == "TINI" && data.Length >= 2)
                    tintIndex = BinaryPrimitives.ReadUInt16LittleEndian(data);
                else if (signature == "TINC" && data.Length == 4 && tintIndex == 0)
                    hasSkinTint = true;
            });
        }

        return new RawPluginSnapshot(
            version.Value,
            recordCount.Value,
            new FormId(nextFormId.Value),
            masters.ToImmutable(),
            records.Count,
            npcs.Length,
            records.ToImmutable(),
            topGroups.ToImmutable(),
            npcs.Length == 1 ? npcs[0].FormId : 0,
            npcs.Length == 1 ? npcs[0].FormVersion : (ushort)0,
            headerFlags,
            hasQnam,
            hasSkinTint,
            nam5,
            height);
    }

    private static void WalkContainer(
        byte[] bytes,
        int start,
        int end,
        int depth,
        ImmutableArray<RawMajorRecord>.Builder records,
        ImmutableArray<RawTopGroup>.Builder topGroups,
        CancellationToken cancellationToken)
    {
        var position = start;
        while (position < end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position + RecordHeaderSize > end)
                throw new InvalidDataException("A TES4 record header is truncated.");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = ReadUInt32(bytes, position + 4);
            if (signature == "GRUP")
            {
                if (size < RecordHeaderSize) throw new InvalidDataException("A GRUP size is smaller than its header.");
                var groupEnd = checked(position + (int)size);
                if (groupEnd > end) throw new InvalidDataException("A GRUP exceeds its container.");
                if (depth == 0)
                {
                    topGroups.Add(new RawTopGroup(
                        Encoding.ASCII.GetString(bytes, position + 8, 4),
                        ReadUInt32(bytes, position + 12)));
                }
                WalkContainer(bytes, position + RecordHeaderSize, groupEnd, depth + 1,
                    records, topGroups, cancellationToken);
                position = groupEnd;
                continue;
            }

            var recordEnd = checked(position + RecordHeaderSize + (int)size);
            if (recordEnd > end) throw new InvalidDataException("A major record exceeds its container.");
            var flags = ReadUInt32(bytes, position + 8);
            if ((flags & CompressedRecordFlag) != 0)
                throw new InvalidDataException("Compressed records are not accepted by the independent Gate 1 verifier.");
            records.Add(new RawMajorRecord(
                signature,
                ReadUInt32(bytes, position + 12),
                flags,
                ReadUInt32(bytes, position + 16),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 20, 2)),
                BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 22, 2)),
                bytes.AsMemory(position + RecordHeaderSize, (int)size)));
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("A TES4 container has trailing bytes.");
    }

    private static void ReadSubrecords(
        ReadOnlySpan<byte> payload,
        Action<string, ReadOnlySpan<byte>> action)
    {
        var position = 0;
        uint? extendedSize = null;
        while (position < payload.Length)
        {
            if (position + 6 > payload.Length) throw new InvalidDataException("A subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(payload.Slice(position, 4));
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(position + 4, 2));
            position += 6;
            var size = extendedSize ?? shortSize;
            extendedSize = null;
            if (signature == "XXXX")
            {
                if (shortSize != 4 || position + 4 > payload.Length)
                    throw new InvalidDataException("An XXXX subrecord is malformed.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(position, 4));
                position += 4;
                continue;
            }
            if (size > int.MaxValue || position + (int)size > payload.Length)
                throw new InvalidDataException("A subrecord exceeds its major record.");
            action(signature, payload.Slice(position, (int)size));
            position += (int)size;
        }
        if (extendedSize is not null)
            throw new InvalidDataException("An XXXX subrecord has no following payload.");
    }

    private static bool LevelMatches(IANpcLevelGetter? actual, NpcLevelValue expected) =>
        (actual, expected.Mode) switch
        {
            (NpcLevel fixedLevel, NpcLevelMode.Fixed) => fixedLevel.Level == checked((short)expected.Value),
            (PcLevelMult multiplier, NpcLevelMode.Multiplier) =>
                Math.Abs(multiplier.LevelMult - (float)expected.Value) < 0.000001f,
            _ => false
        };

    private static bool HasFiniteQnam(ReadOnlySpan<byte> data)
    {
        for (var offset = 0; offset < data.Length; offset += sizeof(float))
        {
            var value = BitConverter.Int32BitsToSingle(
                BinaryPrimitives.ReadInt32LittleEndian(data.Slice(offset, sizeof(float))));
            if (!float.IsFinite(value)) return false;
        }
        return true;
    }

    private static void Check(
        bool condition,
        string code,
        string message,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!condition) diagnostics.Add(Error(code, message));
    }

    private static bool IsOrdinaryEsp(uint headerFlags) =>
        (headerFlags & (MasterPluginFlag | LightPluginFlag)) == 0;

    private static NpcCreationVerificationResult BuildResult(
        bool isValid,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        Sha256Hash? proposalHash,
        Sha256Hash? outputHash,
        ImmutableArray<PluginName> masters,
        float? headerVersion,
        int majorRecordCount,
        int npcRecordCount,
        bool isOrdinaryEsp,
        ImmutableArray<Diagnostic> diagnostics,
        FormId? nextFormId = null) => new(
            isValid,
            request.Proposal,
            proposalHash,
            request.Output,
            outputHash,
            proposal.AllocatedFormId,
            masters,
            headerVersion,
            nextFormId,
            majorRecordCount,
            npcRecordCount,
            isOrdinaryEsp,
            diagnostics);

    private static Sha256Hash HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static uint ReadUInt32(byte[] bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static FormKey ToFormKey(FormReference reference) => new(
        ModKey.FromNameAndExtension(reference.Plugin.Value),
        reference.FormId.Value);

    private sealed record RawMajorRecord(
        string Signature,
        uint FormId,
        uint Flags,
        uint VersionControl,
        ushort FormVersion,
        ushort Unknown,
        ReadOnlyMemory<byte> Body);

    private sealed record RawTopGroup(string Label, uint GroupType);

    private sealed record RawFollowerFaction(uint Faction, int Rank);

    private sealed record RawFollowerRelationship(
        string EditorId,
        uint Parent,
        uint Child,
        short Rank,
        byte Unknown,
        byte Flags,
        uint AssociationType);

    private sealed record RawPluginSnapshot(
        float HeaderVersion,
        uint HeaderRecordCount,
        FormId NextFormId,
        ImmutableArray<PluginName> Masters,
        int MajorRecordCount,
        int NpcRecordCount,
        ImmutableArray<RawMajorRecord> Records,
        ImmutableArray<RawTopGroup> TopGroups,
        uint NpcFormId,
        ushort NpcFormVersion,
        uint HeaderFlags,
        bool HasQnam,
        bool HasSkinTint,
        ushort? Nam5,
        float? Height);
}
