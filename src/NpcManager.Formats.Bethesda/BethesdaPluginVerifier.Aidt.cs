using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaPluginVerifier
{
    private static int AidtOffset(string field) => field switch
    {
        "AIDT:Aggression" => 0, "AIDT:Confidence" => 1, "AIDT:Energy" => 2,
        "AIDT:Morality" => 3, "AIDT:Assistance" => 5,
        _ => throw new InvalidDataException($"Unknown AIDT field '{field}'.")
    };

    private static void VerifyAidtPreservation(PluginVerificationRequest request, ParsedNpc source, ParsedNpc output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var changes = request.ExpectedChanges.Where(item => item.Field.StartsWith("AIDT:", StringComparison.Ordinal)).ToArray();
        if (changes.Length == 0) return;
        if (request.Edition != GameEdition.SkyrimSpecialEdition ||
            !source.Subrecords.TryGetValue("AIDT", out var before) || before.Length != 20 ||
            !output.Subrecords.TryGetValue("AIDT", out var after) || after.Length != 20)
            throw new InvalidDataException("Skyrim AIDT mutation verification requires one existing 20-byte source and output AIDT.");
        var changedOffsets = changes.Select(item => AidtOffset(item.Field)).ToHashSet();
        for (int offset = 0; offset < 20; offset++)
            if (!changedOffsets.Contains(offset) && before[offset] != after[offset])
                diagnostics.Add(new Diagnostic("aidt-preserved-field-drift", DiagnosticSeverity.Error,
                    $"AIDT byte {offset} changed outside the requested fields; Mood, flags and distances are preserved."));
    }
}
