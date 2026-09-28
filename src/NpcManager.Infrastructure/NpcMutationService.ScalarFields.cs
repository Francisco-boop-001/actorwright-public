using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class NpcMutationService
{
    internal static void ValidateWeight(GameEdition edition, NpcWeightPatch? weight, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (weight is null || weight.IsEmpty) return;
        foreach (var value in new[] { weight.SkyrimValue, weight.Thin, weight.Muscular, weight.Fat }.Where(value => value is not null).Select(value => value!.Value))
            if (float.IsNaN(value) || float.IsInfinity(value) || value is < 0 or > 100)
                diagnostics.Add(new Diagnostic("weight-out-of-range", DiagnosticSeverity.Error, "NPC weight values must be finite numbers from 0 through 100."));
        if (edition == GameEdition.SkyrimSpecialEdition && (weight.Thin is not null || weight.Muscular is not null || weight.Fat is not null))
            diagnostics.Add(new Diagnostic("skyrim-weight-shape-invalid", DiagnosticSeverity.Error, "Skyrim SE accepts one scalar weight, not an FO4 triangle."));
        if (edition == GameEdition.Fallout4 && weight.SkyrimValue is not null)
            diagnostics.Add(new Diagnostic("fo4-weight-shape-invalid", DiagnosticSeverity.Error, "Fallout 4 accepts thin, muscular, and fat triangle values."));
    }

    internal static ImmutableArray<MutationChange> BuildChanges(
        NpcMutationRequest request,
        BethesdaNpcSnapshot snapshot)
    {
        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        if (request.EditorId is { } editorId && !string.Equals(snapshot.EditorId, editorId.Value, StringComparison.Ordinal))
            changes.Add(new MutationChange("EditorID", snapshot.EditorId, editorId.Value));
        if (request.Name is { } name && !string.Equals(snapshot.Name, name.Value, StringComparison.Ordinal))
            changes.Add(new MutationChange("Name", snapshot.Name, name.Value));
        if (request.Names is { } names)
        {
            AddTextChange(changes, "Name", names.FullName, snapshot.Name);
            AddTextChange(changes, "ShortName", names.ShortName, snapshot.ShortName);
        }
        if (request.Sex is { } sex && snapshot.Sex != sex)
            changes.Add(new MutationChange("Sex", FormatSex(snapshot.Sex), FormatSex(sex)));
        if (request.Archetype is { } archetype)
        {
            var current = snapshot.Archetype ?? new NpcArchetypeReferences(null, null, null, null);
            AddReferenceChange(changes, "Race", archetype.Race, current.Race);
            AddReferenceChange(changes, "Voice", archetype.Voice, current.Voice);
            AddReferenceChange(changes, "Class", archetype.Class, current.Class);
            AddReferenceChange(changes, "CombatStyle", archetype.CombatStyle, current.CombatStyle);
        }
        if (request.Weight is { } weight)
        {
            if (weight.SkyrimValue is { } scalar && snapshot.SkyrimWeight != scalar)
                changes.Add(new MutationChange("SkyrimWeight", Format(snapshot.SkyrimWeight), Format(scalar)));
            if (weight.Thin is { } thin && snapshot.Thin != thin)
                changes.Add(new MutationChange("Thin", Format(snapshot.Thin), Format(thin)));
            if (weight.Muscular is { } muscular && snapshot.Muscular != muscular)
                changes.Add(new MutationChange("Muscular", Format(snapshot.Muscular), Format(muscular)));
            if (weight.Fat is { } fat && snapshot.Fat != fat)
                changes.Add(new MutationChange("Fat", Format(snapshot.Fat), Format(fat)));
        }
        AddStatsChanges(changes, request.Stats, snapshot.Stats);
        AddAidtChanges(changes, request.Aidt, snapshot.Aidt);
        AddKeywordChanges(changes, request.KeywordPatch, snapshot.Keywords);
        AddFactionChanges(changes, request.FactionPatch, snapshot.Factions);
        AddInventoryChanges(changes, request.InventoryPatch, snapshot.Inventory);
        AddOutfitChanges(changes, request.OutfitPatch, snapshot.Outfits);
        AddPerkChanges(changes, request.PerkPatch, snapshot.Perks);
        AddActorEffectChanges(changes, request.ActorEffectPatch, snapshot.ActorEffects);
        AddPropertyChanges(changes, request.PropertyPatch, snapshot.Properties);
        AddSkinChange(changes, request.Skin, snapshot.Fallout4Skin);
        AddBodyMorphChanges(changes, request.BodyMorphs, snapshot.Fallout4BodyMorphs);
        return changes.ToImmutable();
    }

    internal static void ValidateEditableNames(
        NpcMutationRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Name is not null && request.Names?.FullName.IsSpecified == true)
            diagnostics.Add(new Diagnostic(
                "npc-name-intent-conflict",
                DiagnosticSeverity.Error,
                "Use either the legacy full-name field or the explicit names patch, not both."));
    }

    private static void AddTextChange(
        ImmutableArray<MutationChange>.Builder changes,
        string field,
        OptionalNpcText requested,
        string? current)
    {
        if (!requested.IsSpecified) return;
        var before = current ?? string.Empty;
        var after = requested.Value ?? string.Empty;
        if (!string.Equals(before, after, StringComparison.Ordinal))
            changes.Add(new MutationChange(field, before, after));
    }

    private static void ValidateBodyMorphs(NpcMutationRequest request, NpcBodyMorphPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch is null || patch.IsEmpty) return;
        if (request.Edition != GameEdition.Fallout4)
        {
            diagnostics.Add(new Diagnostic("skyrim-body-morphs-unsupported", DiagnosticSeverity.Error,
                "Skyrim SE NPC records have no Fallout 4 MRSV body-region morph field."));
            return;
        }
        foreach (var pair in patch.Values)
        {
            if (!Fallout4BodyRegionCatalog.Ordered.Contains(pair.Key))
                diagnostics.Add(new Diagnostic("body-region-unknown", DiagnosticSeverity.Error,
                    $"Unknown Fallout 4 body region '{pair.Key}'."));
            if (!float.IsFinite(pair.Value) || pair.Value is < -1F or > 1F)
                diagnostics.Add(new Diagnostic("body-region-out-of-range", DiagnosticSeverity.Error,
                    $"Body region '{pair.Key}' must be finite and between -1 and 1."));
        }
    }

    private static void AddBodyMorphChanges(ImmutableArray<MutationChange>.Builder changes,
        NpcBodyMorphPatch? patch, Fallout4BodyMorphValues? current)
    {
        if (patch is null || patch.IsEmpty) return;
        var before = current ?? Fallout4BodyMorphValues.Zero;
        var after = before.Apply(patch.Values);
        if (before != after)
            changes.Add(new MutationChange("BodyMorphRegions", Fallout4BodyRegionCatalog.FormatValues(before.ToDictionary()),
                Fallout4BodyRegionCatalog.FormatValues(after.ToDictionary())));
    }

    private static void ValidateSkin(NpcMutationRequest request, NpcSkinPatch? patch,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch is null || patch.IsEmpty) return;
        if (!string.IsNullOrWhiteSpace(patch.PresetSkinId))
            diagnostics.Add(new Diagnostic("preset-skin-unsupported", DiagnosticSeverity.Error,
                "LooksMenu preset-skin IDs require an F4SE template/provider catalog; this release refuses unverified template strings."));
        if (!patch.Fallout4Skin.IsSpecified) return;
        if (request.Edition != GameEdition.Fallout4)
        {
            diagnostics.Add(new Diagnostic("skyrim-skin-unsupported", DiagnosticSeverity.Error,
                "Skyrim SE NPC records have no Fallout 4 WNAM skin field; use the typed RaceMenu skin-override route instead."));
            return;
        }
        if (patch.Fallout4Skin.Value is not { } value) return;
        if (value.FormId.Value == 0)
            diagnostics.Add(new Diagnostic("skin-form-id-invalid", DiagnosticSeverity.Error,
                "A concrete Fallout 4 skin must use a non-null FormID; use --clear-skin for the race-default fallback."));
        if (!string.Equals(value.Plugin.Value, Path.GetFileName(request.InputPlugin.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("skin-plugin-unresolved", DiagnosticSeverity.Error,
                $"Skin record {value} is not in the input plugin; explicit master/load-order resolution is required before mutation."));
    }

    private static void AddSkinChange(ImmutableArray<MutationChange>.Builder changes,
        NpcSkinPatch? patch, FormReference? current)
    {
        if (patch is null || !patch.Fallout4Skin.IsSpecified) return;
        if (patch.Fallout4Skin.Value != current)
            changes.Add(new MutationChange("Skin", FormatReference(current), FormatReference(patch.Fallout4Skin.Value)));
    }

}
