using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Independent raw census plus typed reopening of the emitted QUST/PACK and exact SEQ.</summary>
public static class BethesdaSkyrimInteriorPlacementQuestVerifier
{
    public static void Verify(byte[] bytes, byte[] seq, PluginName patch, ImmutableArray<PluginName> masters,
        FormReference npc, SkyrimQuestAliasPlacementEvidence evidence)
    {
        var census = BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(bytes);
        Require(census.Records.Count(x => x.Signature == "TES4") == 1 && census.NonTes4Records.Length == 2 &&
            census.NonTes4Records.Count(x => x.Signature == "QUST") == 1 && census.NonTes4Records.Count(x => x.Signature == "PACK") == 1,
            "Quest-alias output must contain exactly TES4, one QUST and one PACK; world/other records are forbidden.");
        Require(census.Groups.Length == 2 && census.Groups.All(x => x.Depth == 1 && x.Type == 0 && x.Label is "QUST" or "PACK") &&
            census.Groups.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count() == 2,
            "Quest-alias output contains unexpected group topology.");
        var questRaw = census.NonTes4Records.Single(x => x.Signature == "QUST");
        var packageRaw = census.NonTes4Records.Single(x => x.Signature == "PACK");
        Require(questRaw.GroupLabel == "QUST" && packageRaw.GroupLabel == "PACK" &&
            census.NonTes4Records.All(x => (x.Flags & (0x40000u | 0x20u)) == 0), "Quest-alias output record group, compression or deleted flags differ.");
        foreach (var record in census.NonTes4Records)
            Require(BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(bytes, record).All(x => x.Signature != "VMAD"),
                "Quest-alias output cannot contain a VMAD.");
        var header = census.Records.Single(x => x.Signature == "TES4");
        var headerFields = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(bytes, header);
        var hedr = headerFields.Single(x => x.Signature == "HEDR");
        Require(hedr.Bytes.Length == 18 && (header.Flags & 0x200) != 0 &&
            BinaryPrimitives.ReadUInt32LittleEndian(hedr.Bytes.AsSpan(10, 4)) == census.NonTes4Records.Length + census.Groups.Length &&
            BinaryPrimitives.ReadUInt32LittleEndian(hedr.Bytes.AsSpan(14, 4)) == 0x801 &&
            headerFields.Where(x => x.Signature == "MAST").Select(x => Encoding.UTF8.GetString(x.Bytes.AsSpan(6)).TrimEnd('\0'))
                .SequenceEqual(masters.Select(x => x.Value), StringComparer.Ordinal), "Quest-alias TES4 flags/count/masters/next ID differ.");
        Require(!patch.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase) ||
            masters.All(x => !x.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)), "Quest-alias ESP masters require the light-flagged ESP output extension.");
        uint expectedQuest = ((uint)masters.Length << 24) | 0x800u;
        Require(FormId.TryParse(evidence.QuestRawFormId, out FormId encoded) && encoded.Value == expectedQuest &&
            questRaw.RawFormId == expectedQuest && evidence.Quest == $"{patch.Value}|0x00000800" &&
            seq.Length == 4 && BinaryPrimitives.ReadUInt32LittleEndian(seq) == expectedQuest &&
            Hash(seq) == new Sha256Hash(evidence.SeqSha256), "Quest-alias SEQ/QUST do not bind the actual file master ordinal.");
        Require(Hash(packageRaw.Bytes) == new Sha256Hash(evidence.PackageRecordSha256), "Quest-alias PACK record bytes differ from the reviewed binding.");
        using var stream = new MemoryStream(bytes, writable: false);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(stream, SkyrimRelease.SkyrimSE, ModKey.FromNameAndExtension(patch.Value));
        IQuestGetter quest = mod.Quests.Single();
        Require(quest.Flags == (Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce) && quest.NextAliasID == 2 &&
            quest.VirtualMachineAdapter is null && quest.Aliases.Count == 2 && quest.DialogConditions.Count == 0 &&
            quest.EventConditions.Count == 0 && quest.Stages.Count == 0 && quest.Objectives.Count == 0,
            "Quest-alias quest is not the reviewed script-free start-game/run-once quest.");
        IQuestAliasGetter markerAlias = quest.Aliases[0];
        IQuestAliasGetter npcAlias = quest.Aliases[1];
        Require(markerAlias.ID == 0 && npcAlias.ID == 1 &&
            markerAlias.Type == QuestAlias.TypeEnum.Reference && npcAlias.Type == QuestAlias.TypeEnum.Reference &&
            markerAlias.ForcedReference.FormKey == Key(evidence.Marker) && markerAlias.CreateReferenceToObject is null &&
            npcAlias.ForcedReference.FormKey.IsNull && npcAlias.CreateReferenceToObject is
                { AliasID: 0, Create: CreateReferenceToObject.CreateEnum.At } create && create.Object.FormKey == Key(npc.ToString()) &&
            quest.Aliases.All(alias => alias.Flags is null && alias.Conditions.Count == 0 && alias.PackageData.Count == 0 &&
                alias.UniqueActor.FormKey.IsNull && alias.External is null && alias.Location is null &&
                alias.FindMatchingRefNearAlias is null && alias.FindMatchingRefFromEvent is null &&
                alias.AliasIDToForceIntoWhenFilled is null && alias.SpecificLocation.FormKey.IsNull),
            "Quest-alias fill modes do not force the marker and create the exact NPC at alias0.");
        IPackageGetter package = mod.Packages.Single();
        IPackageDataLocationGetter[] locations = package.Data.Values.OfType<IPackageDataLocationGetter>().ToArray();
        bool sandboxProcedure = package.ProcedureTree.Count == 0
            ? package.PackageTemplate.FormKey == Key(BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission.CanonicalSandboxProcedureTemplateForm.ToString())
            : package.ProcedureTree.Count == 1 && package.PackageTemplate.FormKey.IsNull && package.ProcedureTree[0].ProcedureType == "Sandbox";
        Require(package.FormKey == Key(evidence.SandboxPackage) && locations.Length == 1 &&
            locations[0].Location is { Target: ILocationTargetGetter target } location &&
            target.Link.FormKey == Key(evidence.Marker) && location.Radius == evidence.SandboxRadius && evidence.SandboxRadius > 0 &&
            sandboxProcedure &&
            package.OwnerQuest.FormKeyNullable is null && package.VirtualMachineAdapter is null,
            "Quest-alias PACK override does not bind the NPC sandbox to the reviewed marker and radius.");
    }

    private static FormKey Key(string value)
    {
        Require(FormReference.TryParse(value, out FormReference reference) && reference.ToString() == value, "Quest-alias identity is not canonical.");
        return new(ModKey.FromNameAndExtension(reference.Plugin.Value), reference.FormId.Value);
    }

    private static Sha256Hash Hash(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }
}
