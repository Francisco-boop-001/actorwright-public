using System.Collections.Immutable;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;


namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaNpcMutationAdapter
{
    private static FormReference? ToReference(FormKey? formKey) =>
        formKey is { } key && !key.IsNull
            ? new FormReference(new PluginName(key.ModKey.ToString()), new FormId(key.ID))
            : null;

    private static FormKey ToFormKey(FormReference reference) =>
        new(ModKey.FromNameAndExtension(reference.Plugin.Value), reference.FormId.Value);

    private static void ApplySkyrimArchetype(Mutagen.Bethesda.Skyrim.Npc npc, NpcArchetypePatch? patch)
    {
        if (patch is null) return;
        if (patch.Race.IsSpecified && patch.Race.Value is { } race)
            npc.Race = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IRaceGetter>(ToFormKey(race));
        if (patch.Voice.IsSpecified)
            npc.Voice = patch.Voice.Value is { } voice
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.IVoiceTypeGetter>(ToFormKey(voice))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.IVoiceTypeGetter>();
        if (patch.Class.IsSpecified && patch.Class.Value is { } npcClass)
            npc.Class = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Skyrim.IClassGetter>(ToFormKey(npcClass));
        if (patch.CombatStyle.IsSpecified)
            npc.CombatStyle = patch.CombatStyle.Value is { } combatStyle
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.ICombatStyleGetter>(ToFormKey(combatStyle))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Skyrim.ICombatStyleGetter>();
    }

    private static void ApplyFallout4Archetype(Mutagen.Bethesda.Fallout4.Npc npc, NpcArchetypePatch? patch)
    {
        if (patch is null) return;
        if (patch.Race.IsSpecified && patch.Race.Value is { } race)
            npc.Race = new Mutagen.Bethesda.Plugins.FormLink<Mutagen.Bethesda.Fallout4.IRaceGetter>(ToFormKey(race));
        if (patch.Voice.IsSpecified)
            npc.Voice = patch.Voice.Value is { } voice
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IVoiceTypeGetter>(ToFormKey(voice))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IVoiceTypeGetter>();
        if (patch.Class.IsSpecified)
            npc.Class = patch.Class.Value is { } npcClass
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IClassGetter>(ToFormKey(npcClass))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IClassGetter>();
        if (patch.CombatStyle.IsSpecified)
            npc.CombatStyle = patch.CombatStyle.Value is { } combatStyle
                ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.ICombatStyleGetter>(ToFormKey(combatStyle))
                : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.ICombatStyleGetter>();
    }

    private static void ApplyFallout4Skin(Mutagen.Bethesda.Fallout4.Npc npc, NpcSkinPatch? patch)
    {
        if (patch is null || !patch.Fallout4Skin.IsSpecified) return;
        npc.Skin = patch.Fallout4Skin.Value is { } skin
            ? new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IArmorGetter>(ToFormKey(skin))
            : new Mutagen.Bethesda.Plugins.FormLinkNullable<Mutagen.Bethesda.Fallout4.IArmorGetter>();
    }

    private static Fallout4BodyMorphValues ReadFallout4BodyMorphs(Mutagen.Bethesda.Fallout4.INpcGetter npc)
    {
        var values = npc.BodyMorphRegionValues ?? new Mutagen.Bethesda.Fallout4.NpcBodyMorphRegionValues();
        return new Fallout4BodyMorphValues(values.Head, values.UpperTorso, values.Arms, values.LowerTorso, values.Legs);
    }

    private static void ApplyFallout4BodyMorphs(Mutagen.Bethesda.Fallout4.Npc npc, NpcBodyMorphPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        var current = npc.BodyMorphRegionValues ?? new Mutagen.Bethesda.Fallout4.NpcBodyMorphRegionValues();
        var values = new Fallout4BodyMorphValues(current.Head, current.UpperTorso, current.Arms, current.LowerTorso, current.Legs)
            .Apply(patch.Values);
        npc.BodyMorphRegionValues = new Mutagen.Bethesda.Fallout4.NpcBodyMorphRegionValues
        {
            Head = values.Head,
            UpperTorso = values.UpperTorso,
            Arms = values.Arms,
            LowerTorso = values.LowerTorso,
            Legs = values.Legs
        };
    }

}
