using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Presets;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferenceDescriptionTests
{
    public static async Task TestVocabularyAndUnknowns()
    {
        var interpreter = new ReferenceDescriptionInterpreter();
        ReferenceDescriptionInterpretResult normalized =
            await interpreter.InterpretAsync(
                Request(
                    "LONG face, wide-set eyes, thick brows and arched brows; " +
                    "not a wide nose. Sophia Loren, moonlit."),
                CancellationToken.None);

        RequireTrait(
            normalized,
            ReferenceDescriptionTraitKind.FaceLength,
            1.0);
        RequireTrait(
            normalized,
            ReferenceDescriptionTraitKind.EyeSpacing,
            1.0);
        RequireTrait(
            normalized,
            ReferenceDescriptionTraitKind.BrowThickness,
            1.0);
        RequireTrait(
            normalized,
            ReferenceDescriptionTraitKind.BrowArch,
            1.0);
        RequireTrait(
            normalized,
            ReferenceDescriptionTraitKind.NoseWingWidth,
            -1.0);
        Require(
            normalized.Unknowns.Any(item =>
                item.Text.Contains(
                    "sophia",
                    StringComparison.OrdinalIgnoreCase)) &&
            normalized.Unknowns.Any(item =>
                item.Text.Contains(
                    "moonlit",
                    StringComparison.OrdinalIgnoreCase)),
            "reference-description-unknowns-visible");

        ReferenceDescriptionInterpretResult competing =
            await interpreter.InterpretAsync(
                Request("wide face and narrow face"),
                CancellationToken.None);
        Require(
            competing.Traits.Count(item =>
                item.Kind ==
                ReferenceDescriptionTraitKind.FaceWidth) == 2,
            "reference-description-competing-traits");
        RequireCode(
            competing,
            "reference-description-competing");
        Require(
            competing.Traits
                .Where(item =>
                    item.Kind ==
                    ReferenceDescriptionTraitKind.FaceWidth)
                .All(item => !item.ConflictAcknowledged),
            "reference-description-conflict-unacknowledged");
    }

    public static async Task TestUnicodeLimits()
    {
        var interpreter = new ReferenceDescriptionInterpreter();
        string exact = string.Concat(
            Enumerable.Repeat("\U0001F642", 4_096));
        ReferenceDescriptionInterpretResult admitted =
            await interpreter.InterpretAsync(
                Request(exact),
                CancellationToken.None);
        Require(
            !HasError(
                admitted,
                "reference-description-scalars"),
            "reference-description-4096-scalars");

        ReferenceDescriptionInterpretResult refused =
            await interpreter.InterpretAsync(
                Request(exact + "\U0001F642"),
                CancellationToken.None);
        RequireCode(refused, "reference-description-scalars");

        ReferenceDescriptionInterpretResult nonNfc =
            await interpreter.InterpretAsync(
                Request("Cafe\u0301"),
                CancellationToken.None);
        RequireCode(nonNfc, "reference-description-nfc");
    }

    private static ReferenceDescriptionInterpretRequest Request(
        string description) =>
        new(description, new Sha256Hash(new string('a', 64)));

    private static void RequireTrait(
        ReferenceDescriptionInterpretResult result,
        ReferenceDescriptionTraitKind kind,
        double strength)
    {
        Require(
            result.Traits.Any(item =>
                item.Kind == kind &&
                Math.Abs(item.Strength - strength) < 0.000001),
            $"reference-description-trait:{kind}:{strength}");
    }

    private static bool HasError(
        ReferenceDescriptionInterpretResult result,
        string code) =>
        result.Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error &&
            string.Equals(
                item.Code,
                code,
                StringComparison.Ordinal));

    private static void RequireCode(
        ReferenceDescriptionInterpretResult result,
        string code)
    {
        Require(
            result.Diagnostics.Any(item =>
                string.Equals(
                    item.Code,
                    code,
                    StringComparison.Ordinal)),
            $"missing-diagnostic:{code}");
    }

    private static void Require(bool condition, string diagnostic)
    {
        if (!condition)
        {
            throw new InvalidOperationException(diagnostic);
        }
    }
}
