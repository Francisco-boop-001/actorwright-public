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

public sealed record BethesdaNpcSnapshot(
    string? EditorId,
    string? Name,
    float? SkyrimWeight,
    float? Thin,
    float? Muscular,
    float? Fat,
    NpcSex? Sex = null,
    NpcArchetypeReferences? Archetype = null,
    NpcStatsSnapshot? Stats = null,
    NpcKeywordSnapshot? Keywords = null,
    NpcFactionSnapshot? Factions = null,
    NpcInventorySnapshot? Inventory = null,
    NpcOutfitSnapshot? Outfits = null,
    NpcPerkSnapshot? Perks = null,
    NpcActorEffectSnapshot? ActorEffects = null,
    NpcPropertySnapshot? Properties = null,
    FormReference? Fallout4Skin = null,
    Fallout4BodyMorphValues? Fallout4BodyMorphs = null,
    string? ShortName = null,
    NpcAidtPatch? Aidt = null);

public static partial class BethesdaNpcMutationAdapter
{
    public static ImmutableArray<Diagnostic> ValidateSkyrimArchetypeTargets(
        WorkspacePath sourcePlugin,
        NpcArchetypePatch? patch)
    {
        if (patch is null || patch.IsEmpty) return [];
        var requested = new[]
        {
            (Field: "Race", Signature: "RACE", Value: patch.Race),
            (Field: "Voice", Signature: "VTYP", Value: patch.Voice),
            (Field: "Class", Signature: "CLAS", Value: patch.Class),
            (Field: "CombatStyle", Signature: "CSTY", Value: patch.CombatStyle)
        }.Where(item => item.Value.IsSpecified && item.Value.Value is not null)
         .Select(item => (item.Field, item.Signature, Reference: item.Value.Value!.Value))
         .ToImmutableArray();
        if (requested.IsEmpty) return [];

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var dataRoot = Path.GetDirectoryName(sourcePlugin.Value)!;
        foreach (var group in requested.GroupBy(item => item.Reference.Plugin))
        {
            var path = Path.Combine(dataRoot, group.Key.Value);
            if (!File.Exists(path))
            {
                diagnostics.Add(new Diagnostic(
                    "archetype-provider-missing",
                    DiagnosticSeverity.Error,
                    $"Typed archetype validation requires copied provider '{group.Key}' beside the source plugin."));
                continue;
            }
            try
            {
                var key = ToModKey(path);
                using var overlay = SkyrimMod.CreateFromBinaryOverlay(
                    new ModPath(key, new FilePath(path)), SkyrimRelease.SkyrimSE);
                foreach (var item in group)
                {
                    var formKey = new FormKey(key, item.Reference.FormId.Value);
                    IMajorRecordGetter? record = item.Signature switch
                    {
                        "RACE" => overlay.Races.FirstOrDefault(value => value.FormKey == formKey),
                        "VTYP" => overlay.VoiceTypes.FirstOrDefault(value => value.FormKey == formKey),
                        "CLAS" => overlay.Classes.FirstOrDefault(value => value.FormKey == formKey),
                        "CSTY" => overlay.CombatStyles.FirstOrDefault(value => value.FormKey == formKey),
                        _ => null
                    };
                    if (record is null || !MatchesSkyrimArchetypeSignature(record, item.Signature))
                    {
                        diagnostics.Add(new Diagnostic(
                            "archetype-record-type-mismatch",
                            DiagnosticSeverity.Error,
                            $"{item.Field} reference {item.Reference} is not a typed {item.Signature} record in its copied provider."));
                    }
                }
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic(
                    "archetype-provider-read-failed",
                    DiagnosticSeverity.Error,
                    $"Typed archetype provider '{group.Key}' could not be read: {exception.Message}"));
            }
        }
        return diagnostics.ToImmutable();
    }

    private static bool MatchesSkyrimArchetypeSignature(
        IMajorRecordGetter record,
        string signature) => signature switch
        {
            "RACE" => record is Mutagen.Bethesda.Skyrim.IRaceGetter,
            "VTYP" => record is Mutagen.Bethesda.Skyrim.IVoiceTypeGetter,
            "CLAS" => record is Mutagen.Bethesda.Skyrim.IClassGetter,
            "CSTY" => record is Mutagen.Bethesda.Skyrim.ICombatStyleGetter,
            _ => false
        };

    public static ImmutableHashSet<string> ReadReferencePluginClosure(
        GameEdition edition,
        WorkspacePath pluginPath)
    {
        var path = pluginPath.Value;
        var modKey = ToModKey(path);
        return edition switch
        {
            GameEdition.SkyrimSpecialEdition => ReadSkyrimReferencePluginClosure(path, modKey),
            GameEdition.Fallout4 => ReadFallout4ReferencePluginClosure(path, modKey),
            _ => throw new ArgumentOutOfRangeException(nameof(edition), edition,
                "Unsupported game edition.")
        };
    }

    public static BethesdaNpcSnapshot Read(GameEdition edition, WorkspacePath pluginPath, FormId formId)
    {
        var path = pluginPath.Value;
        var modKey = ToModKey(path);
        return edition switch
        {
            GameEdition.SkyrimSpecialEdition => ReadSkyrim(path, modKey, formId),
            GameEdition.Fallout4 => ReadFallout4(path, modKey, formId),
            _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unsupported game edition.")
        };
    }

    public static void Write(NpcMutationRequest request, WorkspacePath destination)
    {
        var sourcePath = request.InputPlugin.Value;
        var modKey = request.WholeSkin is null ? ToModKey(sourcePath) :
            ModKey.FromNameAndExtension(Path.GetFileName(sourcePath));
        switch (request.Edition)
        {
            case GameEdition.SkyrimSpecialEdition:
                WriteSkyrim(sourcePath, destination.Value, modKey, request);
                break;
            case GameEdition.Fallout4:
                WriteFallout4(sourcePath, destination.Value, modKey, request);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(request), request.Edition, "Unsupported game edition.");
        }
    }

    private static BethesdaNpcSnapshot ReadSkyrim(string path, ModKey modKey, FormId formId)
    {
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(path)), SkyrimRelease.SkyrimSE);
        var npc = overlay.Npcs[new FormKey(modKey, formId.Value)];
        var sex = npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? NpcSex.Female : NpcSex.Male;
        var archetype = new NpcArchetypeReferences(
            ToReference(npc.Race.FormKey),
            ToReference(npc.Voice.FormKeyNullable),
            ToReference(npc.Class.FormKey),
            ToReference(npc.CombatStyle.FormKeyNullable));
        return new BethesdaNpcSnapshot(npc.EditorID, npc.Name?.String, npc.Weight, null, null, null, sex, archetype,
            ReadSkyrimStats(npc), ReadSkyrimKeywords(npc), ReadSkyrimFactions(npc), ReadSkyrimInventory(npc), ReadSkyrimOutfits(npc), ReadSkyrimPerks(npc), ReadSkyrimActorEffects(npc),
            ShortName: npc.ShortName?.String,
            Aidt: npc.AIData is { } ai ? new NpcAidtPatch(
                (SkyrimNpcFinishCoreAggression)(int)ai.Aggression,
                (SkyrimNpcFinishCoreConfidence)(int)ai.Confidence,
                (SkyrimNpcFinishCoreMorality)(int)ai.Responsibility,
                (SkyrimNpcFinishCoreAssistance)(int)ai.Assistance, ai.EnergyLevel) : null);
    }

    private static ImmutableHashSet<string> ReadSkyrimReferencePluginClosure(
        string path,
        ModKey modKey)
    {
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, new FilePath(path)), SkyrimRelease.SkyrimSE);
        return overlay.ModHeader.MasterReferences
            .Select(item => item.Master.FileName.String)
            .Append(modKey.FileName.String)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static BethesdaNpcSnapshot ReadFallout4(string path, ModKey modKey, FormId formId)
    {
        using var overlay = Fallout4Mod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(path)), Fallout4Release.Fallout4);
        var npc = overlay.Npcs[new FormKey(modKey, formId.Value)];
        var weight = npc.Weight;
        var sex = npc.Flags.HasFlag(Mutagen.Bethesda.Fallout4.Npc.Flag.Female) ? NpcSex.Female : NpcSex.Male;
        var archetype = new NpcArchetypeReferences(
            ToReference(npc.Race.FormKey),
            ToReference(npc.Voice.FormKeyNullable),
            ToReference(npc.Class.FormKeyNullable),
            ToReference(npc.CombatStyle.FormKeyNullable));
        return new BethesdaNpcSnapshot(npc.EditorID, npc.Name?.String, null, weight?.Thin, weight?.Muscular, weight?.Fat, sex, archetype,
            ReadFallout4Stats(npc), ReadFallout4Keywords(npc), ReadFallout4Factions(npc), ReadFallout4Inventory(npc), ReadFallout4Outfits(npc), ReadFallout4Perks(npc), ReadFallout4ActorEffects(npc), ReadFallout4Properties(npc), ToReference(npc.Skin.FormKeyNullable), ReadFallout4BodyMorphs(npc));
    }

    private static ImmutableHashSet<string> ReadFallout4ReferencePluginClosure(
        string path,
        ModKey modKey)
    {
        using var overlay = Fallout4Mod.CreateFromBinaryOverlay(
            new ModPath(modKey, new FilePath(path)), Fallout4Release.Fallout4);
        return overlay.ModHeader.MasterReferences
            .Select(item => item.Master.FileName.String)
            .Append(modKey.FileName.String)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static void WriteSkyrim(string sourcePath, string destinationPath, ModKey modKey, NpcMutationRequest request)
    {
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(sourcePath)), SkyrimRelease.SkyrimSE);
        var mutable = (SkyrimMod)overlay.DeepCopy();
        var npc = mutable.Npcs[new FormKey(modKey, request.TargetFormId.Value)];
        ApplySkyrimMutation(npc, request);
        NpcWholeSkinPlan? skin = request.WholeSkin is not null ? PlanWholeSkin(request) : null;
        if (skin is not null) ApplyWholeSkin(mutable, npc, skin);
        WriteMod(mutable, destinationPath);
        if (skin is not null) PreserveWholeSkinSource(sourcePath, destinationPath, request.TargetFormId, skin);
    }

    internal static void ApplySkyrimMutation(
        Mutagen.Bethesda.Skyrim.Npc npc,
        NpcMutationRequest request)
    {
        if (request.EditorId is { } editorId) npc.EditorID = editorId.Value;
        if (request.Name is { } name) npc.Name = name.Value;
        if (request.Names is { } names)
        {
            if (names.FullName.IsSpecified)
                npc.Name = string.IsNullOrEmpty(names.FullName.Value) ? null : names.FullName.Value;
            if (names.ShortName.IsSpecified)
                npc.ShortName = string.IsNullOrEmpty(names.ShortName.Value) ? null : names.ShortName.Value;
        }
        if (request.Sex is { } sex)
            npc.Configuration.Flags = SetFemaleFlag(npc.Configuration.Flags, sex == NpcSex.Female);
        ApplySkyrimArchetype(npc, request.Archetype);
        ApplySkyrimStats(npc, request.Stats);
        ApplySkyrimKeywords(npc, request.KeywordPatch);
        ApplySkyrimFactions(npc, request.FactionPatch);
        if (request.Aidt is { } aidt)
        {
            var ai = npc.AIData ?? throw new InvalidDataException("The source NPC has no AIDT to patch.");
            if (aidt.Aggression is { } aggression) ai.Aggression = (Aggression)(int)aggression;
            if (aidt.Confidence is { } confidence) ai.Confidence = (Confidence)(int)confidence;
            if (aidt.Morality is { } morality) ai.Responsibility = (Responsibility)(int)morality;
            if (aidt.Assistance is { } assistance) ai.Assistance = (Assistance)(int)assistance;
            if (aidt.Energy is { } energy) ai.EnergyLevel = energy;
        }
        ApplySkyrimInventory(npc, request.InventoryPatch);
        ApplySkyrimOutfits(npc, request.OutfitPatch);
        ApplySkyrimPerks(npc, request.PerkPatch);
        ApplySkyrimActorEffects(npc, request.ActorEffectPatch);
        if (request.Skin is { } skin && !skin.IsEmpty)
            throw new InvalidOperationException("Skyrim NPC records do not expose Fallout 4 WNAM skin routing.");
        if (request.BodyMorphs is { } bodyMorphs && !bodyMorphs.IsEmpty)
            throw new InvalidOperationException("Skyrim NPC records do not expose Fallout 4 MRSV body-region morphs.");
        if (request.Weight is { } weight && weight.SkyrimValue is { } scalar) npc.Weight = scalar;
    }

    private static void WriteFallout4(string sourcePath, string destinationPath, ModKey modKey, NpcMutationRequest request)
    {
        using var overlay = Fallout4Mod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(sourcePath)), Fallout4Release.Fallout4);
        var mutable = (Fallout4Mod)overlay.DeepCopy();
        var npc = mutable.Npcs[new FormKey(modKey, request.TargetFormId.Value)];
        if (request.EditorId is { } editorId) npc.EditorID = editorId.Value;
        if (request.Name is { } name) npc.Name = name.Value;
        if (request.Sex is { } sex)
            npc.Flags = SetFemaleFlag(npc.Flags, sex == NpcSex.Female);
        ApplyFallout4Archetype(npc, request.Archetype);
        ApplyFallout4Stats(npc, request.Stats);
        ApplyFallout4Keywords(npc, request.KeywordPatch);
        ApplyFallout4Factions(npc, request.FactionPatch);
        ApplyFallout4Inventory(npc, request.InventoryPatch);
        ApplyFallout4Outfits(npc, request.OutfitPatch);
        ApplyFallout4Perks(npc, request.PerkPatch);
        ApplyFallout4ActorEffects(npc, request.ActorEffectPatch);
        ApplyFallout4Properties(npc, request.PropertyPatch);
        ApplyFallout4Skin(npc, request.Skin);
        ApplyFallout4BodyMorphs(npc, request.BodyMorphs);
        if (request.Weight is { } weight)
        {
            var current = npc.Weight ?? new NpcWeight();
            if (weight.Thin is { } thin) current.Thin = thin;
            if (weight.Muscular is { } muscular) current.Muscular = muscular;
            if (weight.Fat is { } fat) current.Fat = fat;
            npc.Weight = current;
        }
        WriteMod(mutable, destinationPath);
    }

    private static void WriteMod(IModGetter mod, string destinationPath) =>
        mod.WriteToBinary(new FilePath(destinationPath), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck
        });

    private static ModKey ToModKey(string path) =>
        new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);

}
