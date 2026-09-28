using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Read-only typed and raw inspection for the bounded Skyrim simple-follower
/// source. Write and verification passes remain closed until their later
/// transaction stages are implemented.
/// </summary>
public sealed class BethesdaSkyrimFollowerFinishSourceReader :
    ISkyrimFollowerFinishPluginService
{
    private static readonly ImmutableArray<string> ForbiddenSignatures =
        ["PACK", "CELL", "WRLD", "ACHR", "REFR"];

    public ValueTask<SkyrimFollowerFinishPluginSnapshot> InspectAsync(
        SkyrimFollowerFinishRequest request,
        WorkspacePath extractedPlugin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return ValueTask.FromResult(Inspect(
                request,
                extractedPlugin,
                cancellationToken));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                ArgumentException or OverflowException or
                RecordException)
        {
            return ValueTask.FromResult(InvalidSnapshot(
                request,
                extractedPlugin,
                "follower-finish-source-plugin-read-failed",
                exception.Message));
        }
    }

    public ValueTask<SkyrimFollowerFinishPluginWriteResult> WriteAsync(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        WorkspacePath extractedPlugin,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new SkyrimFollowerFinishPluginWriteResult(
                false,
                null,
                null,
                [
                    Error(
                        "follower-finish-write-not-admitted",
                        "The Task 3 source reader is analyze-only and cannot write a plugin.")
                ]));
    }

    public ValueTask<SkyrimFollowerFinishPluginVerification> VerifyAsync(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishProposal proposal,
        WorkspacePath sourcePlugin,
        WorkspacePath outputPlugin,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            new SkyrimFollowerFinishPluginVerification(
                false,
                null,
                null,
                [],
                [],
                [],
                false,
                [
                    Error(
                        "follower-finish-verify-not-admitted",
                        "Post-write follower-finish verification is outside the analyze-only Task 3 stage.")
                ]));
    }

    internal static SkyrimFollowerFinishPluginSnapshot Inspect(
        SkyrimFollowerFinishRequest request,
        WorkspacePath extractedPlugin,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(extractedPlugin.Value) ||
            Directory.Exists(extractedPlugin.Value))
            throw new FileNotFoundException(
                "The extracted follower-finish plugin does not exist.",
                extractedPlugin.Value);

        cancellationToken.ThrowIfCancellationRequested();
        var pluginSha256 = HashFile(extractedPlugin.Value);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(
            extractedPlugin.Value,
            SkyrimRelease.SkyrimSE);
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableArray<ModKey> masterKeys = mod.MasterReferences
            .Select(reference => reference.Master)
            .ToImmutableArray();
        ImmutableArray<PluginName> masters = masterKeys
            .Select(master => new PluginName(master.ToString()))
            .ToImmutableArray();
        IReadOnlyDictionary<(uint FormId, string Signature), string>
            rawDigests = BethesdaRawRecordDigestReader.Read(
                extractedPlugin.Value);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        var ownedRecords = mod.EnumerateMajorRecords()
            .Where(record => record.FormKey.ModKey == mod.ModKey)
            .Select(record => new OwnedRecord(
                record,
                RawFormId(record.FormKey.ID, masters.Length),
                ResolveRawSignature(
                    rawDigests,
                    RawFormId(record.FormKey.ID, masters.Length))))
            .OrderBy(record => record.Record.FormKey.ID)
            .ThenBy(record => record.Signature, StringComparer.Ordinal)
            .ToImmutableArray();
        uint ownerPrefix = checked((uint)masters.Length << 24);
        ImmutableArray<(uint FormId, string Signature)> rawOwnedRecords =
            rawDigests.Keys
                .Where(key =>
                    key.Signature != "TES4" &&
                    (key.FormId & 0xFF00_0000u) == ownerPrefix)
                .OrderBy(key => key.FormId)
                .ThenBy(key => key.Signature, StringComparer.Ordinal)
                .ToImmutableArray();
        ImmutableArray<string> recordInventory = rawOwnedRecords.IsDefaultOrEmpty
            ? ["UNAVAILABLE 0x00000000"]
            : rawOwnedRecords
                .Select(record =>
                    $"{record.Signature} {new FormId(record.FormId & 0x00FF_FFFFu)}")
                .ToImmutableArray();
        Check(
            rawDigests.Count == ownedRecords.Length + 1 &&
            rawOwnedRecords.Length == ownedRecords.Length,
            "follower-finish-source-plugin-raw-inventory",
            "The independent raw inventory does not equal TES4 plus the typed self-owned record inventory.",
            diagnostics);
        Check(
            request.OccupiedLocalFormIds.Length == rawOwnedRecords.Length &&
            request.OccupiedLocalFormIds.Distinct().Count() == request.OccupiedLocalFormIds.Length &&
            rawOwnedRecords
                .Select(record => record.FormId & 0x00FF_FFFFu)
                .ToHashSet()
                .SetEquals(request.OccupiedLocalFormIds.Select(id => id.Value)),
            "follower-finish-source-plugin-owned-form-id-set",
            "The actual self-owned FormID set does not equal the request occupiedLocalFormIds set.",
            diagnostics);

        uint tes4Flags = (uint)mod.ModHeader.Flags;
        Check(
            (tes4Flags & 0x200u) == 0,
            "follower-finish-source-plugin-esl-state",
            "The immutable source plugin must not already carry the ESL flag.",
            diagnostics);
        var nextFormId = new FormId(mod.ModHeader.Stats.NextFormID);
        Check(
            nextFormId == request.Allocation.Package,
            "follower-finish-source-plugin-next-form-id",
            "The source NextFormID must equal the first closed allocation.",
            diagnostics);

        SkyrimFollowerFinishAllocation allocation = request.Allocation;
        Check(
            allocation.Package.Value is >= 0x800 and <= 0xFFC &&
            allocation.Anchor.Value == allocation.Package.Value + 1 &&
            allocation.Actor.Value == allocation.Package.Value + 2 &&
            allocation.NextFormId.Value == allocation.Package.Value + 3 &&
            request.OccupiedLocalFormIds.All(id =>
                id.Value >= 0x800 && id.Value < allocation.Package.Value),
            "follower-finish-source-plugin-allocation",
            "Occupied local IDs must precede the four contiguous allocation IDs inside 0x800..0xFFF.",
            diagnostics);

        INpcGetter? npc = mod.Npcs
            .Where(record =>
                record.FormKey.ModKey == mod.ModKey &&
                record.FormKey.ID == request.NpcFormId.Value)
            .SingleOrDefault();
        IColorRecordGetter? color = mod.Colors
            .Where(record =>
                record.FormKey.ModKey == mod.ModKey &&
                record.FormKey.ID == request.Hair.ColorFormId.Value)
            .SingleOrDefault();
        IRelationshipGetter? relationship = mod.Relationships
            .Where(record =>
                record.FormKey.ModKey == mod.ModKey &&
                record.FormKey.ID == request.RelationshipFormId.Value)
            .SingleOrDefault();

        Check(
            npc is not null,
            "follower-finish-source-plugin-npc",
            "The source plugin does not contain the requested NPC FormID.",
            diagnostics);
        Check(
            color is not null,
            "follower-finish-source-plugin-color",
            "The source plugin does not contain the requested CLFM FormID.",
            diagnostics);
        Check(
            relationship is not null,
            "follower-finish-source-plugin-relationship",
            "The source plugin does not contain the requested RELA FormID.",
            diagnostics);

        ITextureSetGetter[] ownedTextureSets = mod.TextureSets
            .Where(record => record.FormKey.ModKey == mod.ModKey)
            .ToArray();
        IHeadPartGetter[] ownedHeadParts = mod.HeadParts
            .Where(record => record.FormKey.ModKey == mod.ModKey)
            .ToArray();
        IHeadPartGetter[] ownedFaceHeadParts = ownedHeadParts
            .Where(record => record.Type == HeadPart.TypeEnum.Face)
            .ToArray();
        bool faceGraphValid = npc is not null &&
                              ownedTextureSets.Length == 1 &&
                              ownedHeadParts.Length == 1 &&
                              ownedFaceHeadParts.Length == 1 &&
                              npc.HeadTexture.FormKeyNullable ==
                                  ownedTextureSets[0].FormKey &&
                              npc.HeadParts.Count(link => link.FormKey.ModKey == mod.ModKey) == 1 &&
                              npc.HeadParts.Count(link =>
                                  link.FormKey.ModKey == mod.ModKey &&
                                  link.FormKey == ownedFaceHeadParts[0].FormKey) == 1 &&
                              ownedFaceHeadParts[0].TextureSet.FormKeyNullable ==
                                  ownedTextureSets[0].FormKey;
        Check(
            faceGraphValid,
            "follower-finish-source-plugin-face-hdpt",
            "The source NPC must bind exactly one self-owned TXST through one self-owned Face HDPT.",
            diagnostics);
        Check(
            npc is not null &&
            relationship is not null &&
            relationship.Parent.FormKey == npc.FormKey &&
            relationship.Child.FormKey == new FormKey(
                ModKey.FromNameAndExtension("Skyrim.esm"),
                0x7),
            "follower-finish-source-plugin-relationship-endpoints",
            "The source RELA must point from the requested NPC to Skyrim.esm Player.",
            diagnostics);
        bool privateBody = string.Equals(request.ExpectedBodyRoute,
            "private-naked-skin", StringComparison.Ordinal);
        bool skinGraphValid = privateBody
            ? HasPrivateBodyGraph(mod, npc, extractedPlugin.Value, masters.Length)
            : !mod.Armors.Any(record => record.FormKey.ModKey == mod.ModKey) &&
              !mod.ArmorAddons.Any(record => record.FormKey.ModKey == mod.ModKey) &&
              (npc is null || ReadNpcWnam(extractedPlugin.Value,
                  RawFormId(npc.FormKey.ID, masters.Length)) is not { } wnam ||
                  wnam == 0 || (wnam & 0xFF00_0000u) != ownerPrefix);
        Check(
            skinGraphValid,
            "follower-finish-source-plugin-private-skin",
            "Only the declared private-naked-skin route admits self-owned WNAM and one ARMO bound to exactly body, hands, and feet ARMA roles.",
            diagnostics);

        var expectedRoles = new HashSet<(uint FormId, string Signature)>
        {
            (RawFormId(request.NpcFormId.Value, masters.Length), "NPC_"),
            (RawFormId(request.Hair.ColorFormId.Value, masters.Length), "CLFM"),
            (RawFormId(request.RelationshipFormId.Value, masters.Length), "RELA")
        };
        if (ownedTextureSets.Length == 1)
            expectedRoles.Add((RawFormId(ownedTextureSets[0].FormKey.ID, masters.Length), "TXST"));
        if (ownedFaceHeadParts.Length == 1)
            expectedRoles.Add((RawFormId(ownedFaceHeadParts[0].FormKey.ID, masters.Length), "HDPT"));
        if (privateBody && skinGraphValid)
            foreach (OwnedRecord record in ownedRecords.Where(record => record.Signature is "ARMO" or "ARMA"))
                expectedRoles.Add((record.RawFormId, record.Signature));
        string DescribeRoles(IEnumerable<(uint FormId, string Signature)> records) =>
            string.Join(", ", records.OrderBy(record => record.FormId)
                .Select(record => $"{record.Signature} {new FormId(record.FormId & 0x00FF_FFFFu)}"));
        Check(
            rawOwnedRecords.Length == expectedRoles.Count && expectedRoles.SetEquals(rawOwnedRecords),
            "follower-finish-source-plugin-inventory",
            $"Actual inventory: [{string.Join(", ", recordInventory)}]. " +
            $"Expected roles: [{DescribeRoles(expectedRoles)}]. " +
            $"Unexpected: [{DescribeRoles(rawOwnedRecords.Except(expectedRoles))}]. " +
            $"Missing: [{DescribeRoles(expectedRoles.Except(rawOwnedRecords))}].",
            diagnostics);

        FormReference expectedHair = new(
            request.Source.Plugin,
            request.Hair.ColorFormId);
        FormReference actorHair = npc is null ||
                                  npc.HairColor.FormKeyNullable is not { } hair
            ? new FormReference(
                request.Source.Plugin,
                new FormId(0))
            : ToReference(hair);
        bool defaultOutfitNull =
            npc?.DefaultOutfit.FormKeyNullable is null;
        ImmutableArray<NpcFactionEntry> factions = npc is null
            ? []
            : npc.Factions
                .Select(entry => new NpcFactionEntry(
                    ToReference(entry.Faction.FormKey),
                    checked((sbyte)entry.Rank)))
                .ToImmutableArray();
        string relationshipRank = relationship?.Rank.ToString() ??
            "Unavailable";
        byte relationshipRankRawDiscriminator = relationship is null
            ? byte.MaxValue
            : ReadRelationshipRawDiscriminator(
                extractedPlugin.Value,
                RawFormId(
                    request.RelationshipFormId.Value,
                    masters.Length));
        SkyrimPackedRgb hairPackedRgb = color is null
            ? new SkyrimPackedRgb(0)
            : new SkyrimPackedRgb(
                ((uint)color.Color.R << 16) |
                ((uint)color.Color.G << 8) |
                color.Color.B);

        if (npc is not null)
        {
            Check(
                string.Equals(
                    npc.EditorID,
                    request.NpcEditorId.Value,
                    StringComparison.Ordinal),
                "follower-finish-source-plugin-npc-editor-id",
                "The source NPC EditorID does not match the request.",
                diagnostics);
            Check(
                npc.Race.FormKey == ToFormKey(request.ExpectedRace),
                "follower-finish-source-plugin-race",
                "The source NPC race does not match the request.",
                diagnostics);
        }
        Check(
            actorHair == expectedHair,
            "follower-finish-source-plugin-hclf",
            "The source NPC HCLF does not point to the requested output-owned CLFM.",
            diagnostics);
        Check(
            defaultOutfitNull == request.ExpectedDefaultOutfitNull,
            "follower-finish-source-plugin-outfit",
            "The source NPC default outfit does not match the required null state.",
            diagnostics);
        Check(
            factions.SequenceEqual(request.ExpectedFactionRanks),
            "follower-finish-source-plugin-factions",
            "The source NPC follower faction ranks do not match the request.",
            diagnostics);
        Check(
            string.Equals(
                relationshipRank,
                request.ExpectedRelationshipRank,
                StringComparison.Ordinal),
            "follower-finish-source-plugin-relationship-rank",
            "The source typed relationship rank does not match the request.",
            diagnostics);
        Check(
            relationshipRankRawDiscriminator ==
            request.ExpectedRelationshipRankRawDiscriminator,
            "follower-finish-source-plugin-relationship-raw-rank",
            "The source raw RELA rank discriminator does not match the request.",
            diagnostics);
        if (relationship is not null)
        {
            Check(
                relationshipRankRawDiscriminator ==
                checked((byte)(short)relationship.Rank),
                "follower-finish-source-plugin-relationship-typed-raw",
                "Mutagen's typed relationship rank does not agree with the independent raw RELA discriminator.",
                diagnostics);
        }
        Check(
            hairPackedRgb == request.Hair.OldPackedRgb,
            "follower-finish-source-plugin-hair-rgb",
            "The source CLFM packed RGB does not match the request old value.",
            diagnostics);

        ImmutableArray<string> presentForbidden = ownedRecords
            .Select(record => record.Signature)
            .Where(ForbiddenSignatures.Contains)
            .Distinct(StringComparer.Ordinal)
            .ToImmutableArray();
        Check(
            presentForbidden.IsEmpty,
            "follower-finish-source-plugin-forbidden-record",
            "The source plugin already contains a PACK or world/reference record.",
            diagnostics);

        ImmutableArray<string> actorSubrecordDigests =
            ReadNpcSubrecordDigests(
                extractedPlugin.Value,
                RawFormId(request.NpcFormId.Value, masters.Length),
                rawDigests);
        if (!diagnostics.Any(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(new Diagnostic(
                "follower-finish-source-plugin-valid",
                DiagnosticSeverity.Info,
                "Mutagen and the independent raw reader agree on the closed source-plugin inventory."));
        }

        return new SkyrimFollowerFinishPluginSnapshot(
            !diagnostics.Any(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error),
            new PluginName(Path.GetFileName(extractedPlugin.Value)),
            pluginSha256,
            tes4Flags,
            masters,
            nextFormId,
            recordInventory.IsDefaultOrEmpty
                ? ["UNAVAILABLE 0x00000000"]
                : recordInventory,
            actorSubrecordDigests,
            hairPackedRgb,
            actorHair,
            defaultOutfitNull,
            factions,
            relationshipRank,
            relationshipRankRawDiscriminator,
            ForbiddenSignatures,
            diagnostics.ToImmutable());
    }

    private static byte ReadRelationshipRawDiscriminator(
        string path,
        uint rawRelationshipFormId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        ReadOnlyMemory<byte>? payload = FindRecordPayload(
            bytes,
            rawRelationshipFormId,
            "RELA");
        if (payload is null)
            throw new InvalidDataException(
                "The independent raw reader omitted the requested RELA.");
        ReadOnlySpan<byte> span = payload.Value.Span;
        int position = 0;
        uint? extendedSize = null;
        while (position < span.Length)
        {
            if (position + 6 > span.Length)
                throw new InvalidDataException(
                    "The source RELA subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(
                span.Slice(position, 4));
            ushort shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                span.Slice(position + 4, 2));
            position += 6;
            uint size = extendedSize ?? shortSize;
            extendedSize = null;
            if (signature == "XXXX")
            {
                if (shortSize != 4 || position + 4 > span.Length)
                    throw new InvalidDataException(
                        "The source RELA XXXX subrecord is malformed.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    span.Slice(position, 4));
                position += 4;
                continue;
            }
            if (size > int.MaxValue ||
                position + (int)size > span.Length)
                throw new InvalidDataException(
                    "A source RELA subrecord exceeds its record.");
            if (signature == "DATA")
            {
                if (size < 12)
                    throw new InvalidDataException(
                        "The source RELA DATA subrecord has no rank discriminator at offset 8.");
                int discriminator =
                    BinaryPrimitives.ReadInt32LittleEndian(
                        span.Slice(position + 8, 4));
                if (discriminator is < byte.MinValue or > byte.MaxValue)
                    throw new InvalidDataException(
                        "The source RELA DATA rank discriminator does not fit an unsigned byte.");
                return (byte)discriminator;
            }
            position += (int)size;
        }
        throw new InvalidDataException(
            "The source RELA record omitted DATA rank evidence.");
    }

    private static ImmutableArray<string> ReadNpcSubrecordDigests(
        string path,
        uint rawNpcFormId,
        IReadOnlyDictionary<(uint FormId, string Signature), string>
            rawDigests)
    {
        byte[] bytes = File.ReadAllBytes(path);
        ReadOnlyMemory<byte>? payload = FindRecordPayload(
            bytes,
            rawNpcFormId,
            "NPC_");
        if (payload is null)
            return ["UNAVAILABLE=" + new string('0', 64)];

        var rows = ImmutableArray.CreateBuilder<string>();
        string recordDigest = rawDigests.GetValueOrDefault(
            (rawNpcFormId, "NPC_")) ??
            throw new InvalidDataException(
                "The independent raw digest reader omitted the requested NPC.");
        rows.Add($"RECORD={recordDigest}");
        ReadOnlySpan<byte> span = payload.Value.Span;
        int position = 0;
        uint? extendedSize = null;
        int index = 0;
        while (position < span.Length)
        {
            if (position + 6 > span.Length)
                throw new InvalidDataException(
                    "The source NPC subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(
                span.Slice(position, 4));
            ushort shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                span.Slice(position + 4, 2));
            position += 6;
            uint size = extendedSize ?? shortSize;
            extendedSize = null;
            if (signature == "XXXX")
            {
                if (shortSize != 4 || position + 4 > span.Length)
                    throw new InvalidDataException(
                        "The source NPC XXXX subrecord is malformed.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    span.Slice(position, 4));
                position += 4;
                continue;
            }
            if (size > int.MaxValue ||
                position + (int)size > span.Length)
                throw new InvalidDataException(
                    "A source NPC subrecord exceeds its record.");
            string digest = Convert.ToHexString(
                SHA256.HashData(span.Slice(position, (int)size)));
            rows.Add($"{index:D3}:{signature}={digest}");
            index++;
            position += (int)size;
        }
        if (extendedSize is not null)
            throw new InvalidDataException(
                "The source NPC ends with an unresolved XXXX subrecord.");
        return rows.ToImmutable();
    }

    private static bool HasPrivateBodyGraph(
        ISkyrimModGetter mod,
        INpcGetter? npc,
        string path,
        int masterCount)
    {
        if (npc is null)
            return false;

        IArmorGetter[] ownedArmors = mod.Armors
            .Where(record => record.FormKey.ModKey == mod.ModKey)
            .ToArray();
        IArmorAddonGetter[] ownedAddons = mod.ArmorAddons
            .Where(record => record.FormKey.ModKey == mod.ModKey)
            .ToArray();
        if (ownedArmors.Length != 1 || ownedAddons.Length != 3)
            return false;

        IArmorGetter armor = ownedArmors[0];
        FormKey[] armature = armor.Armature
            .Select(link => link.FormKey)
            .ToArray();
        if (armature.Length != 3 ||
            armature.Distinct().Count() != armature.Length ||
            armature.Any(form => form.ModKey != mod.ModKey) ||
            !armature.All(form => ownedAddons.Any(addon =>
                addon.FormKey == form)))
            return false;

        uint[] roleMasks = ownedAddons
            .Where(addon => armature.Contains(addon.FormKey))
            .Select(addon => addon.BodyTemplate is { } body
                ? (uint)body.FirstPersonFlags
                : 0u)
            .ToArray();
        if (roleMasks.Length != 3 ||
            roleMasks.Distinct().Count() != 3 ||
            !roleMasks.All(mask => mask is 0x04u or 0x08u or 0x80u))
            return false;

        uint? wnam = ReadNpcWnam(
            path,
            RawFormId(npc.FormKey.ID, masterCount));
        return wnam == RawFormId(armor.FormKey.ID, masterCount);
    }

    private static uint? ReadNpcWnam(
        string path,
        uint rawNpcFormId)
    {
        ReadOnlyMemory<byte>? payload = FindRecordPayload(
            File.ReadAllBytes(path),
            rawNpcFormId,
            "NPC_");
        if (payload is null)
            return null;

        ReadOnlySpan<byte> span = payload.Value.Span;
        int position = 0;
        uint? extendedSize = null;
        uint? wnam = null;
        while (position < span.Length)
        {
            if (position + 6 > span.Length)
                throw new InvalidDataException(
                    "The source NPC subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(
                span.Slice(position, 4));
            ushort shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                span.Slice(position + 4, 2));
            position += 6;
            uint size = extendedSize ?? shortSize;
            extendedSize = null;
            if (signature == "XXXX")
            {
                if (shortSize != 4 || position + 4 > span.Length)
                    throw new InvalidDataException(
                        "The source NPC XXXX subrecord is malformed.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    span.Slice(position, 4));
                position += 4;
                continue;
            }
            if (size > int.MaxValue ||
                position + (int)size > span.Length)
                throw new InvalidDataException(
                    "A source NPC subrecord exceeds its record.");
            if (signature == "WNAM")
            {
                if (size != 4 || wnam is not null)
                    throw new InvalidDataException(
                        "The source NPC WNAM must occur once with exactly four bytes.");
                wnam = BinaryPrimitives.ReadUInt32LittleEndian(
                    span.Slice(position, 4));
            }
            position += (int)size;
        }
        if (extendedSize is not null)
            throw new InvalidDataException(
                "The source NPC ends with an unresolved XXXX subrecord.");
        return wnam;
    }

    private static ReadOnlyMemory<byte>? FindRecordPayload(
        byte[] bytes,
        uint rawFormId,
        string wantedSignature)
    {
        if (bytes.Length < 24 ||
            !bytes.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException(
                "The source plugin does not start with TES4.");
        int headerEnd = checked(
            24 +
            (int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(4, 4)));
        return Walk(headerEnd, bytes.Length);

        ReadOnlyMemory<byte>? Walk(int start, int end)
        {
            int position = start;
            while (position < end)
            {
                if (position + 24 > end)
                    throw new InvalidDataException(
                        "A source TES4 record header is truncated.");
                string signature = Encoding.ASCII.GetString(
                    bytes,
                    position,
                    4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                if (signature == "GRUP")
                {
                    int groupEnd = checked(position + (int)size);
                    if (groupEnd > end || size < 24)
                        throw new InvalidDataException(
                            "A source TES4 group exceeds its container.");
                    ReadOnlyMemory<byte>? nested = Walk(
                        position + 24,
                        groupEnd);
                    if (nested is not null)
                        return nested;
                    position = groupEnd;
                    continue;
                }
                int recordEnd = checked(position + 24 + (int)size);
                if (recordEnd > end)
                    throw new InvalidDataException(
                        "A source TES4 record exceeds its container.");
                uint formId = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 12, 4));
                if (signature == wantedSignature &&
                    formId == rawFormId)
                    return bytes.AsMemory(position + 24, (int)size);
                position = recordEnd;
            }
            return null;
        }
    }

    private static SkyrimFollowerFinishPluginSnapshot InvalidSnapshot(
        SkyrimFollowerFinishRequest request,
        WorkspacePath pluginPath,
        string code,
        string message)
    {
        Sha256Hash hash = File.Exists(pluginPath.Value)
            ? HashFile(pluginPath.Value)
            : new Sha256Hash(new string('0', 64));
        return new SkyrimFollowerFinishPluginSnapshot(
            false,
            request.Source.Plugin,
            hash,
            0,
            [],
            request.Allocation.Package,
            ["UNAVAILABLE 0x00000000"],
            ["UNAVAILABLE=" + new string('0', 64)],
            request.Hair.OldPackedRgb,
            new FormReference(
                request.Source.Plugin,
                request.Hair.ColorFormId),
            request.ExpectedDefaultOutfitNull,
            request.ExpectedFactionRanks,
            request.ExpectedRelationshipRank,
            request.ExpectedRelationshipRankRawDiscriminator,
            ForbiddenSignatures,
            [Error(code, message)]);
    }

    private static string ResolveRawSignature(
        IReadOnlyDictionary<(uint FormId, string Signature), string>
            rawDigests,
        uint rawFormId)
    {
        string[] signatures = rawDigests.Keys
            .Where(key => key.FormId == rawFormId)
            .Select(key => key.Signature)
            .ToArray();
        if (signatures.Length != 1)
            throw new InvalidDataException(
                $"Raw source inventory found {signatures.Length} signatures for 0x{rawFormId:X8}.");
        return signatures[0];
    }

    private static uint RawFormId(uint localFormId, int masterCount)
    {
        if (masterCount > byte.MaxValue ||
            localFormId > 0x00FF_FFFF)
            throw new InvalidDataException(
                "A source FormID cannot be represented by the ordinary master table.");
        return ((uint)masterCount << 24) | localFormId;
    }

    private static FormReference ToReference(FormKey key) =>
        new(
            new PluginName(key.ModKey.ToString()),
            new FormId(key.ID));

    private static FormKey ToFormKey(FormReference reference) =>
        new(
            ModKey.FromNameAndExtension(reference.Plugin.Value),
            reference.FormId.Value);

    private static Sha256Hash HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void Check(
        bool condition,
        string code,
        string message,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!condition)
            diagnostics.Add(Error(code, message));
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record OwnedRecord(
        IMajorRecordGetter Record,
        uint RawFormId,
        string Signature);
}
