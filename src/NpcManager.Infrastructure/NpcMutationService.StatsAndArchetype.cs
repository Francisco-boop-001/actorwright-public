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
    internal static void ValidateStats(
        GameEdition edition,
        NpcStatsPatch? stats,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (stats is null || stats.IsEmpty) return;
        if (stats.Level is { } level && !level.TryGetRaw(out _))
            diagnostics.Add(new Diagnostic("level-out-of-range", DiagnosticSeverity.Error,
                "Level must be an integer from 0 through 65535, or a multiplier from 0 through 65.535 with at most three decimals."));
        if (edition == GameEdition.Fallout4 && (stats.MagickaOffset is not null || stats.StaminaOffset is not null ||
            stats.HealthOffset is not null || stats.SpeedMultiplier is not null || stats.Height is not null || stats.PlayerSkills is not null))
            diagnostics.Add(new Diagnostic("fo4-stat-field-unsupported", DiagnosticSeverity.Error,
                "The requested statistics are Skyrim-only."));
        if (edition == GameEdition.SkyrimSpecialEdition && stats.XpValueOffset is not null)
            diagnostics.Add(new Diagnostic("skyrim-stat-field-unsupported", DiagnosticSeverity.Error,
                "XpValueOffset is Fallout 4-only."));
        if (stats.Height is { } height && (float.IsNaN(height) || float.IsInfinity(height) || height is < 0.1f or > 10f))
            diagnostics.Add(new Diagnostic("height-out-of-range", DiagnosticSeverity.Error, "Height must be finite and between 0.1 and 10."));
        if (stats.PlayerSkills?.FarAwayModelDistance is { } farAwayModelDistance &&
            (!float.IsFinite(farAwayModelDistance) || farAwayModelDistance is < -1_000_000f or > 1_000_000f))
        {
            diagnostics.Add(new Diagnostic("far-away-model-distance-out-of-range", DiagnosticSeverity.Error,
                "Far-away model distance must be finite and between -1000000 and 1000000."));
        }
        ValidateFlags(edition, stats.Flags, diagnostics);
    }

    private static void ValidateFlags(GameEdition edition, NpcFlagPatch? patch, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch is null) return;
        var set = patch.Set.ToHashSet();
        var clear = patch.Clear.ToHashSet();
        if (set.Count != patch.Set.Length || clear.Count != patch.Clear.Length)
            diagnostics.Add(new Diagnostic("flag-duplicate", DiagnosticSeverity.Error, "A flag may appear only once in each set or clear list."));
        if (set.Overlaps(clear)) diagnostics.Add(new Diagnostic("flag-conflict", DiagnosticSeverity.Error, "A flag cannot be set and cleared in the same mutation."));
        foreach (var flag in set.Concat(clear))
            if (!NpcFlagExtensions.TryGetMask(edition, flag, out _))
                diagnostics.Add(new Diagnostic("flag-edition-unsupported", DiagnosticSeverity.Error,
                    $"Flag {flag} is not supported for {edition.ToWireName()}"));
    }

    private static void AddStatsChanges(ImmutableArray<MutationChange>.Builder changes,
        NpcStatsPatch? patch, NpcStatsSnapshot? current)
    {
        if (patch is null) return;
        var before = current ?? new NpcStatsSnapshot(new NpcLevelValue(NpcLevelMode.Fixed, 0), 0, 0, 0, 0, 0, 0, 0, 0, 0, null, null, []);
        if (patch.Level is { } level && level != before.Level)
        {
            changes.Add(new MutationChange("Level", before.Level.ToString(), level.ToString()));
            var oldMultiplier = before.Level.Mode == NpcLevelMode.Multiplier;
            var newMultiplier = level.Mode == NpcLevelMode.Multiplier;
            if (oldMultiplier != newMultiplier)
                changes.Add(new MutationChange("Flag:pc-level-mult", oldMultiplier.ToString().ToLowerInvariant(), newMultiplier.ToString().ToLowerInvariant()));
        }
        AddNumericChange(changes, "XpValueOffset", before.XpValueOffset, patch.XpValueOffset);
        AddNumericChange(changes, "MagickaOffset", before.MagickaOffset, patch.MagickaOffset);
        AddNumericChange(changes, "StaminaOffset", before.StaminaOffset, patch.StaminaOffset);
        AddNumericChange(changes, "HealthOffset", before.HealthOffset, patch.HealthOffset);
        AddNumericChange(changes, "CalcMinLevel", before.CalcMinLevel, patch.CalcMinLevel);
        AddNumericChange(changes, "CalcMaxLevel", before.CalcMaxLevel, patch.CalcMaxLevel);
        AddNumericChange(changes, "SpeedMultiplier", before.SpeedMultiplier, patch.SpeedMultiplier);
        AddNumericChange(changes, "DispositionBase", before.DispositionBase, patch.DispositionBase);
        AddNumericChange(changes, "BleedoutOverride", before.BleedoutOverride, patch.BleedoutOverride);
        AddNumericChange(changes, "Height", before.Height, patch.Height);
        if (patch.PlayerSkills is { } skills)
        {
            var existing = before.PlayerSkills ?? new NpcPlayerSkillsSnapshot(0, 0, 0, ImmutableDictionary<NpcSkill, byte>.Empty, ImmutableDictionary<NpcSkill, byte>.Empty);
            AddNumericChange(changes, "PlayerHealth", existing.Health, skills.Health);
            AddNumericChange(changes, "PlayerMagicka", existing.Magicka, skills.Magicka);
            AddNumericChange(changes, "PlayerStamina", existing.Stamina, skills.Stamina);
            AddSkillChanges(changes, "SkillValue", existing.Values, skills.Values);
            AddSkillChanges(changes, "SkillOffset", existing.Offsets, skills.Offsets);
            AddNumericChange(changes, "FarAwayModelDistance", existing.FarAwayModelDistance,
                skills.FarAwayModelDistance);
            AddNumericChange(changes, "GearedUpWeapons", existing.GearedUpWeapons,
                skills.GearedUpWeapons);
        }
        if (patch.Flags is { } flags)
        {
            foreach (var flag in flags.Set) if (!before.Flags.Contains(flag)) changes.Add(new MutationChange($"Flag:{flag.ToWireName()}", "false", "true"));
            foreach (var flag in flags.Clear) if (before.Flags.Contains(flag)) changes.Add(new MutationChange($"Flag:{flag.ToWireName()}", "true", "false"));
        }
    }

    private static void AddNumericChange<T>(ImmutableArray<MutationChange>.Builder changes, string field, T before, T? requested)
        where T : struct
    {
        if (requested is { } value && !EqualityComparer<T>.Default.Equals(before, value))
            changes.Add(new MutationChange(field, Convert.ToString(before, CultureInfo.InvariantCulture), Convert.ToString(value, CultureInfo.InvariantCulture)));
    }

    private static void AddNumericChange(ImmutableArray<MutationChange>.Builder changes, string field, float? before, float? requested)
    {
        if (requested is { } value && before != value)
            changes.Add(new MutationChange(field, Format(before), Format(value)));
    }

    private static void AddSkillChanges(ImmutableArray<MutationChange>.Builder changes, string prefix,
        ImmutableDictionary<NpcSkill, byte> before, ImmutableDictionary<NpcSkill, byte>? requested)
    {
        if (requested is null) return;
        foreach (var (skill, value) in requested)
        {
            before.TryGetValue(skill, out var current);
            if (current != value) changes.Add(new MutationChange($"{prefix}:{skill.ToWireName()}", current.ToString(CultureInfo.InvariantCulture), value.ToString(CultureInfo.InvariantCulture)));
        }
    }

    private static string? Format(float? value) => value?.ToString("R", CultureInfo.InvariantCulture);

    private static string? FormatSex(NpcSex? value) => value?.ToString().ToLowerInvariant();

    private static string FormatReference(FormReference? value) => value?.ToString() ?? "none";

    private static void AddReferenceChange(ImmutableArray<MutationChange>.Builder changes, string field,
        OptionalFormReference requested, FormReference? current)
    {
        if (!requested.IsSpecified || requested.Value == current) return;
        changes.Add(new MutationChange(field, FormatReference(current), FormatReference(requested.Value)));
    }

    internal static void ValidateArchetype(
        NpcArchetypePatch? patch,
        ImmutableHashSet<string> referencePlugins,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (patch is null) return;
        ValidateRequiredReference(patch.Race, "race", diagnostics);
        ValidateRequiredReference(patch.Class, "class", diagnostics);
        foreach (var reference in new[] { patch.Race.Value, patch.Voice.Value, patch.Class.Value, patch.CombatStyle.Value }
                     .Where(reference => reference is not null).Select(reference => reference!.Value))
        {
            if (reference.FormId.Value == 0)
                diagnostics.Add(new Diagnostic("reference-form-id-invalid", DiagnosticSeverity.Error,
                    $"{reference} uses the null FormID; use none for an optional reference."));
            if (!referencePlugins.Contains(reference.Plugin.Value))
                diagnostics.Add(new Diagnostic("reference-plugin-unresolved", DiagnosticSeverity.Error,
                    $"Reference plugin {reference.Plugin} is outside the source plugin's declared master closure."));
        }
    }

    private static void ValidateRequiredReference(OptionalFormReference reference, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference.IsSpecified && reference.Value is null)
            diagnostics.Add(new Diagnostic("reference-required", DiagnosticSeverity.Error,
                $"{field} cannot be cleared because the game NPC field is non-nullable."));
    }

}
