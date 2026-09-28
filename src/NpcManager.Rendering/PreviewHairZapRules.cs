using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Rendering;

/// <summary>Resolves the pinned game's hair partition coverage without guessing.</summary>
internal static class PreviewHairZapRules
{
    // FO4 slot 32 is the FaceGen head partition. Headwear that covers it
    // culls the face mesh in the upstream preview/export path; it is not a
    // hair partition and therefore never contributes to Parts.
    private static readonly ImmutableHashSet<int> FalloutHeadwearSlots = [30, 31, 32];
    private static readonly ImmutableHashSet<int> SkyrimHairSlots = [31, 41, 131, 141];

    internal static PreviewHairZapPlan? Resolve(
        GameEdition edition,
        bool renderHeadwear,
        ImmutableHashSet<int>? coveredSlots,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!renderHeadwear && coveredSlots is null)
            return null;

        var slots = coveredSlots ?? ImmutableHashSet<int>.Empty;
        var accepted = edition == GameEdition.Fallout4 ? FalloutHeadwearSlots : SkyrimHairSlots;
        var invalid = slots.Where(slot => !accepted.Contains(slot)).Order().ToImmutableArray();
        if (!invalid.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic("preview-hair-zap-slot-invalid", DiagnosticSeverity.Error,
                $"{edition.ToWireName()} hair coverage slots are limited to {string.Join(", ", accepted.Order())}; received {string.Join(", ", invalid)}."));
            return null;
        }

        if (renderHeadwear && slots.IsEmpty)
        {
            diagnostics.Add(new Diagnostic("preview-hair-zap-slots-required", DiagnosticSeverity.Error,
                "Headwear rendering requires at least one explicit covered hair partition slot."));
            return null;
        }

        var normalized = slots.Order().ToImmutableArray();
        var top = renderHeadwear && (edition == GameEdition.Fallout4
            ? slots.Contains(30)
            : slots.Contains(31) || slots.Contains(131));
        var @long = renderHeadwear && (edition == GameEdition.Fallout4
            ? slots.Contains(31)
            : slots.Contains(41) || slots.Contains(141));
        var faceGenHead = renderHeadwear && edition == GameEdition.Fallout4 && slots.Contains(32);
        return new PreviewHairZapPlan(edition.ToWireName(), renderHeadwear, normalized, top, @long, faceGenHead);
    }
}
