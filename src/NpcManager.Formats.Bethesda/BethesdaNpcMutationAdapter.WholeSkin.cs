using System.Buffers.Binary;
using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaNpcMutationAdapter
{
    private static readonly string[] WholeSkinTextureNames = ["Head", "Body", "Hands"];
    public static NpcWholeSkinPlan PlanWholeSkin(NpcMutationRequest request)
    {
        NpcWholeSkinPatch patch = request.WholeSkin ?? throw new InvalidDataException("wholeSkin is absent.");
        var sourceKey = ModKey.FromNameAndExtension(Path.GetFileName(request.InputPlugin.Value));
        var providers = new List<(NpcCreationPluginAuthority Authority, ISkyrimModDisposableGetter Mod)>();
        try
        {
            foreach (var authority in patch.PluginAuthorities)
                providers.Add((authority, SkyrimMod.CreateFromBinaryOverlay(authority.PluginPath.Value, SkyrimRelease.SkyrimSE)));
            var source = providers.Single(row => row.Mod.ModKey == sourceKey).Mod;
            INpcGetter npc = source.Npcs.Single(row => row.FormKey == new FormKey(sourceKey, request.TargetFormId.Value));
            if (!npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) || request.Sex == NpcSex.Male)
                throw new InvalidDataException("wholeSkin supports female Skyrim SE NPCs only.");
            if (request.Archetype?.Race is { IsSpecified: true } requestedRace &&
                (requestedRace.Value is not { } raceReference ||
                 npc.Race.FormKey != new FormKey(ModKey.FromNameAndExtension(raceReference.Plugin.Value), raceReference.FormId.Value)))
                throw new InvalidDataException("wholeSkin preserves the source race; author a race change separately before binding skin authority.");
            var closure = source.ModHeader.MasterReferences.Select(row => row.Master).Append(sourceKey).ToHashSet();
            (T Record, NpcCreationPluginAuthority Provider) Find<T>(FormKey key, Func<ISkyrimModGetter, IEnumerable<T>> records)
                where T : class, ISkyrimMajorRecordGetter
            {
                foreach (var provider in providers.AsEnumerable().Reverse())
                {
                    T? record = records(provider.Mod).SingleOrDefault(row => row.FormKey == key);
                    if (record is null) continue;
                    if (record.IsDeleted) throw new InvalidDataException($"wholeSkin winning {key} is deleted.");
                    if (record.EnumerateFormLinks().Any(link => !link.FormKey.IsNull && !closure.Contains(link.FormKey.ModKey)))
                        throw new InvalidDataException($"wholeSkin source {key} retains a reference outside the input master table.");
                    return (record, provider.Authority);
                }
                throw new InvalidDataException($"wholeSkin source record {key} is missing from the copied provider order.");
            }
            RaceMenuNpcFormBinding Binding<T>((T Record, NpcCreationPluginAuthority Provider) item, string signature)
                where T : class, ISkyrimMajorRecordGetter
            {
                var reference = new FormReference(new PluginName(item.Record.FormKey.ModKey.FileName.String), new FormId(item.Record.FormKey.ID));
                return new(new RecordSignature(signature), reference, reference, item.Provider.Plugin,
                    item.Provider.PluginPath, item.Provider.ExpectedSha256, signature == "HDPT" ? NpcHeadPartType.Face : null);
            }
            var race = Find(npc.Race.FormKey, m => m.Races);
            FormKey armorKey = npc.WornArmor.FormKey.IsNull ? race.Record.Skin.FormKey : npc.WornArmor.FormKey;
            var armor = Find(armorKey, m => m.Armors);
            if (armor.Record.Armature.Count != 3 || armor.Record.Armature.Select(row => row.FormKey).Distinct().Count() != 3)
                throw new InvalidDataException("wholeSkin requires exactly three distinct body/hands/feet source ARMAs.");
            var addons = armor.Record.Armature.Select(row => Find(row.FormKey, m => m.ArmorAddons)).ToArray();
            RaceMenuNpcFormBinding? faceBinding = null;
            if (!patch.PreservesHead)
            {
                var faces = npc.HeadParts.Select(row => Find(row.FormKey, m => m.HeadParts)).Where(row => row.Record.Type == HeadPart.TypeEnum.Face).ToArray();
                if (faces.Length != 1 || string.IsNullOrWhiteSpace(faces[0].Record.Model?.File.ToString()))
                    throw new InvalidDataException("wholeSkin requires one selected face HDPT with a retained model.");
                faceBinding = Binding(faces[0], "HDPT");
            }
            int allocatedCount = patch.PreservesHead ? 6 : 8;
            uint maximum = source.EnumerateMajorRecords().Where(row => row.FormKey.ModKey == sourceKey).Select(row => row.FormKey.ID).DefaultIfEmpty(0x7FFU).Max();
            uint first = Math.Max(0x800U, Math.Max(checked(maximum + 1), source.ModHeader.Stats.NextFormID));
            if (first > 0x0100_0000U - (uint)allocatedCount ||
                (((uint)source.ModHeader.Flags & 0x200) != 0 &&
                 first + (uint)allocatedCount - 1 > 0xFFF))
                throw new InvalidDataException("wholeSkin allocation exceeds the input plugin's local FormID space.");
            var regions = ImmutableArray.CreateBuilder<OutputOwnedSkyrimNpcNakedSkinRegionBinding>();
            foreach ((SkyrimNpcSkinRegion region, BipedObjectFlag flag) in new[] { (SkyrimNpcSkinRegion.Body, BipedObjectFlag.Body), (SkyrimNpcSkinRegion.Hands, BipedObjectFlag.Hands), (SkyrimNpcSkinRegion.Feet, BipedObjectFlag.Feet) })
            {
                var matches = addons.Where(row => row.Record.BodyTemplate is { } body && (body.FirstPersonFlags & flag) != 0 &&
                    (row.Record.Race.FormKey == npc.Race.FormKey || (!race.Record.ArmorRace.IsNull && row.Record.Race.FormKey == race.Record.ArmorRace.FormKey) ||
                     row.Record.AdditionalRaces.Any(link => link.FormKey == npc.Race.FormKey || (!race.Record.ArmorRace.IsNull && link.FormKey == race.Record.ArmorRace.FormKey)))).ToArray();
                if (matches.Length != 1 || string.IsNullOrWhiteSpace(matches[0].Record.WorldModel?.Female?.File.ToString()))
                    throw new InvalidDataException($"wholeSkin requires exactly one compatible female {region} ARMA with a model.");
                uint armorAddonOffset = patch.PreservesHead ? 2U : 4U;
                uint bodyTextureOffset = patch.PreservesHead ? 0U : 1U;
                uint handsTextureOffset = patch.PreservesHead ? 1U : 2U;
                regions.Add(new(region, new FormId(first + armorAddonOffset + (uint)regions.Count), Binding(matches[0], "ARMA"), null,
                    new AssetPath(matches[0].Record.WorldModel!.Female!.File.ToString()))
                { AllocatedFemaleTextureSetLocalFormId = new FormId(first + (region == SkyrimNpcSkinRegion.Hands ? handsTextureOffset : bodyTextureOffset)) });
            }
            if (regions.Select(row => row.SourceArmorAddon.Reference).Distinct().Count() != 3)
                throw new InvalidDataException("wholeSkin body/hands/feet must use three distinct source ARMAs.");
            return new(patch, new FormId(first), faceBinding,
                new(new FormId(first + (patch.PreservesHead ? 5U : 7U)), Binding(armor, "ARMO"), regions.ToImmutable()));
        }
        finally { foreach (var provider in providers) provider.Mod.Dispose(); }
    }

    private static void ApplyWholeSkin(SkyrimMod mod, Npc npc, NpcWholeSkinPlan plan)
    {
        uint first = plan.FirstAllocatedLocalFormId.Value;
        var sets = plan.Patch.PreservesHead
            ? new[] { plan.Patch.Body, plan.Patch.Hands }
            : new[] { plan.Patch.Head, plan.Patch.Body, plan.Patch.Hands };
        string[] names = plan.Patch.PreservesHead ? ["Body", "Hands"] : WholeSkinTextureNames;
        for (int index = 0; index < sets.Length; index++)
            BethesdaNpcCreationAdapter.AddOutputOwnedTextureSet(mod, new FormKey(mod.ModKey, first + (uint)index),
                npc.EditorID + "Private" + names[index],
                sets[index].ToDictionary(row => row.Key, row => row.Value.Path, StringComparer.Ordinal),
                !plan.Patch.PreservesHead && index == 0 ? TextureSet.Flag.FaceGenTextures | TextureSet.Flag.HasModelSpaceNormalMap : 0);
        npc.WornArmor.SetTo(BethesdaNpcCreationAdapter.AddOutputOwnedNakedSkinBinding(mod, mod.ModKey,
            npc.EditorID!, plan.Skin, plan.Patch.PluginAuthorities));
        if (!plan.Patch.PreservesHead)
        {
            RaceMenuNpcFormBinding faceBinding = plan.FaceHeadPart ?? throw new InvalidDataException("wholeSkin replacement plan lacks its face HDPT.");
            using var provider = SkyrimMod.CreateFromBinaryOverlay(faceBinding.ProviderPlugin.Value, SkyrimRelease.SkyrimSE);
            var oldKey = new FormKey(ModKey.FromNameAndExtension(faceBinding.Reference.Plugin.Value), faceBinding.Reference.FormId.Value);
            var face = provider.HeadParts.Single(row => row.FormKey == oldKey);
            FormKey headTexture = new(mod.ModKey, first);
            var owned = BethesdaNpcCreationAdapter.AddOutputOwnedFaceHeadPart(mod, new FormKey(mod.ModKey, first + 3), face,
                npc.EditorID + "PrivateFace", headTexture);
            npc.HeadTexture.SetTo(headTexture);
            int position = npc.HeadParts.FindIndex(row => row.FormKey == oldKey);
            npc.HeadParts[position] = new FormLink<IHeadPartGetter>(owned);
        }
        mod.ModHeader.Stats.NextFormID = first + (uint)plan.AllocatedRecordCount;
    }

    private static void PreserveWholeSkinSource(string sourcePath, string destination, FormId target, NpcWholeSkinPlan plan)
    {
        byte[] sourceBytes = File.ReadAllBytes(sourcePath);
        byte[] scratchBytes = File.ReadAllBytes(destination);
        var source = BethesdaSkyrimNpcFinishCoreRaw.Read(sourceBytes, target.Value);
        var scratch = BethesdaSkyrimNpcFinishCoreRaw.Read(scratchBytes, target.Value);
        if (!source.Tes4.Masters.SequenceEqual(scratch.Tes4.Masters, StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("wholeSkin may not change the input master table.");
        byte[] tes4 = source.Tes4.Record.Bytes.ToArray();
        var hedr = source.Tes4.Subrecords.Single(row => row.Signature == "HEDR");
        int offset = hedr.Offset - source.Tes4.Record.Offset;
        BinaryPrimitives.WriteUInt32LittleEndian(tes4.AsSpan(offset + 10), checked(source.Tes4.RecordCount + (uint)(plan.AllocatedRecordCount * 2)));
        BinaryPrimitives.WriteUInt32LittleEndian(tes4.AsSpan(offset + 14), plan.FirstAllocatedLocalFormId.Value + (uint)plan.AllocatedRecordCount);
        byte[] targetBytes = plan.Patch.PreservesHead
            ? ReplaceWholeSkinWnam(sourceBytes, source.Target!, scratchBytes, scratch.Target!)
            : scratch.Target!.Bytes;
        byte[] retained = BethesdaSkyrimNpcFinishCoreRaw.RewriteTree(sourceBytes, sourceBytes.Length,
            new Dictionary<int, byte[]> { [source.Tes4.Record.Offset] = tes4, [source.Target!.Offset] = targetBytes });
        uint owner = (uint)source.Tes4.Masters.Length << 24;
        for (uint id = plan.FirstAllocatedLocalFormId.Value; id < plan.FirstAllocatedLocalFormId.Value + (uint)plan.AllocatedRecordCount; id++)
        {
            var record = scratch.Records.Single(row => row.RawFormId == (owner | id));
            retained = retained.Concat(BethesdaSkyrimNpcFinishCoreRaw.BuildGroup(record.Signature, record.Bytes)).ToArray();
        }
        File.WriteAllBytes(destination, retained);
    }

    private static byte[] ReplaceWholeSkinWnam(byte[] sourceBytes, BethesdaSkyrimNpcFinishCoreRaw.RawRecord sourceTarget,
        byte[] scratchBytes, BethesdaSkyrimNpcFinishCoreRaw.RawRecord scratchTarget)
    {
        if ((sourceTarget.Flags & 0x0004_0000U) != 0 || (scratchTarget.Flags & 0x0004_0000U) != 0)
            throw new InvalidDataException("wholeSkin preserve mode refuses compressed NPC_ records.");
        var sourceFields = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(sourceBytes, sourceTarget);
        var sourceWnam = sourceFields.SingleOrDefault(row => row.Signature == "WNAM");
        var scratchWnam = BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(scratchBytes, scratchTarget).Single(row => row.Signature == "WNAM");
        if (sourceWnam is null)
        {
            var sourceRnam = sourceFields.Single(row => row.Signature == "RNAM");
            if (sourceRnam.Length != 10 || scratchWnam.Length != 10)
                throw new InvalidDataException("wholeSkin preserve mode requires four-byte RNAM and WNAM links.");
            int insertOffset = sourceRnam.Offset - sourceTarget.Offset + sourceRnam.Length;
            byte[] inserted = [.. sourceTarget.Bytes.AsSpan(0, insertOffset), .. scratchWnam.Bytes, .. sourceTarget.Bytes.AsSpan(insertOffset)];
            BinaryPrimitives.WriteUInt32LittleEndian(inserted.AsSpan(4),
                checked((uint)(sourceTarget.PayloadLength + scratchWnam.Length)));
            return inserted;
        }
        if (sourceWnam.Length != scratchWnam.Length)
            throw new InvalidDataException("wholeSkin preserve mode cannot change the WNAM subrecord shape.");
        byte[] result = sourceTarget.Bytes.ToArray();
        scratchWnam.Bytes.CopyTo(result, sourceWnam.Offset - sourceTarget.Offset);
        return result;
    }
}
