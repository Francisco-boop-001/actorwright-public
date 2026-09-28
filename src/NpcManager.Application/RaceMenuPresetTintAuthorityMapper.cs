using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>
/// Maps RaceMenu mask paths to winning RACE TINI rows. Paths absent from the
/// RACE remain baked into the admitted CharGen pair; alpha-zero rows are inert.
/// </summary>
public sealed class RaceMenuPresetTintAuthorityMapper
    : IRaceMenuPresetTintAuthorityMapper
{
    public RaceMenuPresetTintAuthorityPlanResult Map(
        PresetDocument preset,
        SkyrimRaceTintAuthority raceAuthority)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(raceAuthority);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateInputs(preset, raceAuthority, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var raceByPath = raceAuthority.Layers
            .GroupBy(item => item.MaskPath.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var raceIndexCounts = raceAuthority.Layers
            .GroupBy(item => item.Index)
            .ToDictionary(group => group.Key, group => group.Count());
        var mappedIndexes = new HashSet<ushort>();
        var dispositions = ImmutableArray.CreateBuilder<RaceMenuPresetTintAuthorityDisposition>(
            preset.Appearance.Tints.Length);

        foreach (PresetTint source in preset.Appearance.Tints)
        {
            byte alpha = (byte)(source.Color >> 24);
            if (alpha == 0)
            {
                dispositions.Add(new RaceMenuPresetTintAuthorityDisposition(
                    source,
                    RaceMenuPresetTintAuthorityDispositionKind.Inactive,
                    null,
                    null,
                    IsSkinTint: false));
                continue;
            }

            string? normalized = TryNormalizeTexturePath(source.Texture);
            if (normalized is null || !raceByPath.TryGetValue(normalized, out var matches))
            {
                dispositions.Add(new RaceMenuPresetTintAuthorityDisposition(
                    source,
                    RaceMenuPresetTintAuthorityDispositionKind.Baked,
                    null,
                    null,
                    IsSkinTint: false));
                continue;
            }
            if (matches.Length != 1)
            {
                diagnostics.Add(Error("racemenu-tint-race-path-ambiguous",
                    $"Active .jslot tint {source.Index} matches {matches.Length} winning RACE rows for '{normalized}'."));
                continue;
            }

            SkyrimRaceTintLayerAuthority match = matches[0];
            if (raceIndexCounts[match.Index] != 1)
            {
                diagnostics.Add(Error("racemenu-tint-reused-tini-active",
                    $"Active .jslot tint {source.Index} matches '{normalized}', but RACE TINI {match.Index} " +
                    "is reused by more than one mask and cannot be authored unambiguously on an NPC."));
                continue;
            }
            if (!mappedIndexes.Add(match.Index))
            {
                diagnostics.Add(Error("racemenu-tint-tini-duplicate",
                    $"More than one active .jslot tint maps to winning TINI index {match.Index}."));
                continue;
            }
            dispositions.Add(new RaceMenuPresetTintAuthorityDisposition(
                source,
                RaceMenuPresetTintAuthorityDispositionKind.MappedRecord,
                match.Index,
                TiasPresetIndex: -1,
                match.Kind == SkyrimRaceTintMaskKind.SkinTone));
        }

        if (HasErrors(diagnostics)) return Refused(diagnostics);
        ImmutableArray<RaceMenuPresetTintAuthorityDisposition> admitted =
            dispositions.ToImmutable();
        RaceMenuPresetTintAuthorityDisposition[] skinRows = admitted
            .Where(item => item.Kind == RaceMenuPresetTintAuthorityDispositionKind.MappedRecord &&
                           item.IsSkinTint)
            .ToArray();
        if (skinRows.Length != 1)
        {
            diagnostics.Add(Error("racemenu-tint-skin-source",
                $"Preset authority requires exactly one active mapped skin tint; found {skinRows.Length}."));
            return Refused(diagnostics);
        }

        uint color = skinRows[0].Source.Color;
        var qnam = new RaceMenuPresetQnamPlan(
            skinRows[0].Source.Index,
            ((color >> 16) & 0xFF) / 255F,
            ((color >> 8) & 0xFF) / 255F,
            (color & 0xFF) / 255F);
        return new RaceMenuPresetTintAuthorityPlanResult(
            true,
            new RaceMenuPresetTintAuthorityPlan(
                preset, raceAuthority, admitted, qnam),
            diagnostics.ToImmutable());
    }

    private static void ValidateInputs(
        PresetDocument preset,
        SkyrimRaceTintAuthority raceAuthority,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (preset.Format != PresetFormat.RaceMenuJslot ||
            preset.Edition != GameEdition.SkyrimSpecialEdition ||
            !preset.IsValid || preset.Appearance.RaceMenu is null)
        {
            diagnostics.Add(Error("racemenu-tint-preset",
                "Tint authority requires one valid typed Skyrim SE RaceMenu .jslot document."));
        }
        if (preset.Appearance.Tints.IsDefaultOrEmpty ||
            preset.Appearance.Tints.Any(item => item.Index is < 0 or > ushort.MaxValue) ||
            preset.Appearance.Tints.GroupBy(item => item.Index).Any(group => group.Count() != 1))
        {
            diagnostics.Add(Error("racemenu-tint-source-index",
                "The .jslot must contain distinct tint indexes in the unsigned TINI source range."));
        }
        if (!Enum.IsDefined(raceAuthority.Sex) ||
            string.IsNullOrWhiteSpace(raceAuthority.Race.Plugin.Value) ||
            raceAuthority.Race.FormId.Value is 0 or > 0x00FF_FFFF ||
            raceAuthority.Layers.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error("racemenu-tint-race-authority",
                "A complete winning gender-specific RACE tint authority is required."));
        }
        if (raceAuthority.RuntimeAuthority)
        {
            diagnostics.Add(Error("racemenu-tint-runtime-claim",
                "Static RACE tint authority may not claim runtime authority."));
        }
    }

    private static string? TryNormalizeTexturePath(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        string normalized = value.Trim().Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
            normalized = "textures/" + normalized;
        try
        {
            var path = new AssetPath(normalized);
            return path.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase)
                ? path.Value
                : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuPresetTintAuthorityPlanResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
