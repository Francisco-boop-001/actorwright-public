using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class NpcMutationService
{
    private static void ValidateAidt(NpcMutationRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Aidt is not { } aidt) return;
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("aidt-edition-unsupported", DiagnosticSeverity.Error, "AIDT patching requires Skyrim SE."));
        if (aidt.IsEmpty || aidt.Aggression is { } aggression && !Enum.IsDefined(aggression) ||
            aidt.Confidence is { } confidence && !Enum.IsDefined(confidence) ||
            aidt.Morality is { } morality && !Enum.IsDefined(morality) ||
            aidt.Assistance is { } assistance && !Enum.IsDefined(assistance))
            diagnostics.Add(new Diagnostic("aidt-invalid", DiagnosticSeverity.Error, "AIDT requires at least one defined aggression, confidence, morality, assistance or byte energy value."));
    }

    private static ImmutableArray<Diagnostic> ValidateAidtSource(NpcMutationRequest request, BethesdaNpcSnapshot snapshot)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition || request.Aidt is null && request.FactionPatch is null) return [];
        if (request.Aidt is not null && snapshot.Aidt is null)
            return [new Diagnostic("aidt-source-missing", DiagnosticSeverity.Error, "The source NPC has no AIDT; explicit patching preserves existing AI data rather than inventing untouched fields.")];
        var factions = snapshot.Factions?.Factions ?? [];
        if (request.FactionPatch is { } patch) factions = ApplyFactionOperations(factions, patch);
        var assistance = request.Aidt?.Assistance ?? snapshot.Aidt?.Assistance;
        if (assistance == SkyrimNpcFinishCoreAssistance.HelpsNobody && factions.Any(entry =>
                entry.Faction.Plugin == new PluginName("Skyrim.esm") && entry.Faction.FormId.Value is 0x5C84D or 0x5C84E))
            return [new Diagnostic("aidt-follower-assistance", DiagnosticSeverity.Error,
                "Effective PotentialFollowerFaction/CurrentFollowerFaction membership cannot use HelpsNobody; select helpsAllies or helpsFriendsAndAllies.")];
        return [];
    }

    private static void AddAidtChanges(ImmutableArray<MutationChange>.Builder changes, NpcAidtPatch? patch, NpcAidtPatch? source)
    {
        if (patch is null || source is null) return;
        var before = source.ToExpectations().ToDictionary(item => item.Field, item => item.After, StringComparer.Ordinal);
        foreach (var value in patch.ToExpectations())
            if (!string.Equals(before[value.Field], value.After, StringComparison.Ordinal))
                changes.Add(value with { Before = before[value.Field] });
    }
}
