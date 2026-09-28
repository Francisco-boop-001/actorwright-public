using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.ReferencePreset.Tests;

/// <summary>
/// WI-NPC-2: real High Poly Head sculpt payloads (Rachel: 11,255 rows at
/// $.morphs.sculpt[0].data) must parse, while every other JSON array keeps
/// the generic 8,192 anti-DoS bound and the refusal names path and counts.
/// </summary>
internal static class PresetArrayBudgetTests
{
    private const string SculptPath = "$.morphs.sculpt[0].data";

    public static async Task RunAsync()
    {
        string root = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "tests",
            "preset-array-budget-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var workspace = new WorkspacePath(root);
            var service = new PresetService(
                new KOnlyWorkspacePolicy(workspace, new WorkspacePath(Path.Combine(root, "protected"))),
                workspace);

            PresetParseResult realistic = await Inspect(service, root, "realistic.jslot", WriteSculptPreset(9_000));
            Require(realistic.Document is not null &&
                    !realistic.Diagnostics.Any(item => item.Code == "preset-array-limit"),
                "A 9,000-row High Poly Head sculpt payload must parse under the sculpt budget: " +
                Format(realistic.Diagnostics));
            Require(realistic.Document!.Appearance.RaceMenu?.SculptParts is [{ Vertices.Length: 9_000 }],
                "The admitted sculpt payload lost rows on the way to the typed document.");

            PresetParseResult mixedCase = await Inspect(service, root, "mixed-case.jslot",
                WriteSculptPreset(9_000).Replace("\"morphs\"", "\"Morphs\"").Replace("\"sculpt\"", "\"Sculpt\"").Replace("\"data\"", "\"Data\""));
            Require(mixedCase.Document?.Appearance.RaceMenu?.SculptParts is [{ Vertices.Length: 9_000 }] &&
                    !mixedCase.Diagnostics.Any(item => item.Code == "preset-array-limit"),
                "Structural sculpt admission must match case-insensitive preset parsing: " + Format(mixedCase.Diagnostics));

            PresetParseResult oversized = await Inspect(service, root, "oversized.jslot", WriteSculptPreset(70_000));
            Diagnostic? limit = oversized.Diagnostics.SingleOrDefault(item => item.Code == "preset-array-limit");
            Require(oversized.Document is null && limit is { Severity: DiagnosticSeverity.Error } &&
                    limit.Message.Contains(SculptPath, StringComparison.Ordinal) &&
                    limit.Message.Contains("70000", StringComparison.Ordinal) &&
                    limit.Message.Contains("65536", StringComparison.Ordinal) &&
                    limit.Message.Contains("omit sculpt", StringComparison.OrdinalIgnoreCase),
                "A 70,000-row sculpt payload must be refused with the path, the actual count and the allowed count: " +
                Format(oversized.Diagnostics));

            PresetParseResult sculptBlocks = await Inspect(service, root, "sculpt-blocks.jslot",
                WriteSculptBlockArray(8_193));
            limit = sculptBlocks.Diagnostics.SingleOrDefault(item => item.Code == "preset-array-limit");
            Require(sculptBlocks.Document is null && limit is { Severity: DiagnosticSeverity.Error } &&
                    limit.Message.Contains("$.morphs.sculpt", StringComparison.Ordinal) &&
                    limit.Message.Contains("8193", StringComparison.Ordinal) &&
                    limit.Message.Contains("8192", StringComparison.Ordinal),
                "Only sculpt row data may use the larger budget; the sculpt block array must keep 8,192: " +
                Format(sculptBlocks.Diagnostics));

            foreach ((string name, string json) in new[]
                     {
                         ("top-level-lookalike.jslot", WriteLookalikeArray(9_000, nested: false)),
                         ("nested-lookalike.jslot", WriteLookalikeArray(9_000, nested: true))
                     })
            {
                PresetParseResult lookalike = await Inspect(service, root, name, json);
                limit = lookalike.Diagnostics.SingleOrDefault(item => item.Code == "preset-array-limit");
                Require(lookalike.Document is null && limit is { Severity: DiagnosticSeverity.Error } &&
                        limit.Message.Contains("9000", StringComparison.Ordinal) &&
                        limit.Message.Contains("8192", StringComparison.Ordinal) &&
                        !limit.Message.Contains("omit sculpt", StringComparison.OrdinalIgnoreCase),
                    "A property name that resembles the sculpt display path must keep the generic budget: " +
                    Format(lookalike.Diagnostics));
            }

            PresetParseResult unrelated = await Inspect(service, root, "unrelated.jslot", WriteTintPreset(8_193));
            limit = unrelated.Diagnostics.SingleOrDefault(item => item.Code == "preset-array-limit");
            Require(unrelated.Document is null && limit is { Severity: DiagnosticSeverity.Error } &&
                    limit.Message.Contains("$.tintInfo", StringComparison.Ordinal) &&
                    limit.Message.Contains("8193", StringComparison.Ordinal) &&
                    limit.Message.Contains("8192", StringComparison.Ordinal),
                "Arrays outside $.morphs must keep the 8,192 bound and report path and counts: " +
                Format(unrelated.Diagnostics));

            PresetParseResult duplicate = await Inspect(service, root, "duplicate.jslot",
                "{\"version\":{\"formatVersion\":4},\"actor\":{\"weight\":50},\"actor\":{\"weight\":51}}");
            Require(duplicate.Document is null &&
                    duplicate.Diagnostics.Any(item => item.Code == "preset-duplicate-key"),
                "The path-aware budget must not weaken the duplicate-key check: " + Format(duplicate.Diagnostics));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static async Task<PresetParseResult> Inspect(PresetService service, string root, string name, string json)
    {
        string path = Path.Combine(root, name);
        await File.WriteAllTextAsync(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return await service.InspectAsync(
            new PresetParseRequest(PresetFormat.RaceMenuJslot, GameEdition.SkyrimSpecialEdition, new WorkspacePath(path)),
            CancellationToken.None);
    }

    private static string WriteSculptPreset(int rows)
    {
        var builder = new StringBuilder(rows * 16 + 256);
        builder.Append("{\"version\":{\"formatVersion\":4,\"runtimeVersion\":17039360,\"signature\":1397442898,\"skseVersion\":131840},")
            .Append("\"actor\":{\"weight\":50},\"morphs\":{\"sculptDivisor\":10000,\"sculpt\":[{\"host\":\"FemaleHead.nif\",\"vertices\":")
            .Append(rows).Append(",\"data\":[");
        for (int index = 0; index < rows; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append('[').Append(index).Append(",1,-2,3]");
        }
        return builder.Append("]}]}}").ToString();
    }

    private static string WriteTintPreset(int entries)
    {
        var builder = new StringBuilder(entries * 4 + 128);
        builder.Append("{\"version\":{\"formatVersion\":4},\"actor\":{\"weight\":50},\"tintInfo\":[");
        for (int index = 0; index < entries; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append('0');
        }
        return builder.Append("]}").ToString();
    }

    private static string WriteSculptBlockArray(int entries)
    {
        var builder = new StringBuilder(entries * 3 + 128);
        builder.Append("{\"version\":{\"formatVersion\":4},\"actor\":{\"weight\":50},\"morphs\":{\"sculpt\":[");
        for (int index = 0; index < entries; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append("{}");
        }
        return builder.Append("]}}").ToString();
    }

    private static string WriteLookalikeArray(int entries, bool nested)
    {
        var builder = new StringBuilder(entries * 2 + 160);
        builder.Append("{\"version\":{\"formatVersion\":4},\"actor\":{\"weight\":50},");
        builder.Append(nested ? "\"morphs\":{\"sculpt[0]\":{\"data\":[" : "\"morphs.sculpt[0].data\":[");
        for (int index = 0; index < entries; index++)
        {
            if (index > 0) builder.Append(',');
            builder.Append('0');
        }
        return builder.Append(nested ? "]}}}" : "]}").ToString();
    }

    private static string Format(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Severity} {item.Code}: {item.Message}"));

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
