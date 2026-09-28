using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using NpcManager.Application;

namespace NpcManager.Presets;

public sealed class ReferenceDescriptionInterpreter
    : IReferenceDescriptionInterpreter
{
    public const int VocabularyVersion = 1;

    private static readonly ImmutableHashSet<string> StopWords =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "a",
            "an",
            "and",
            "with",
            "has",
            "have",
            "the",
            "is",
            "of",
            "face",
            "eyes",
            "eye",
            "brows",
            "brow",
            "nose",
            "lips",
            "lip",
            "chin",
            "jaw",
            "cheeks",
            "cheek",
            "hair",
            "complexion",
            "makeup");

    private static readonly ImmutableHashSet<string> Negations =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "not",
            "no",
            "without");

    public static ImmutableArray<ReferenceDescriptionVocabularyEntry>
        Vocabulary { get; } = CreateVocabulary();

    private static readonly ImmutableArray<VocabularyPattern> Patterns =
        Vocabulary
            .Select((entry, ordinal) =>
                new VocabularyPattern(
                    entry,
                    Tokenize(entry.Phrase),
                    ordinal))
            .OrderByDescending(item => item.Tokens.Length)
            .ThenBy(item => item.Ordinal)
            .ToImmutableArray();

    public ValueTask<ReferenceDescriptionInterpretResult> InterpretAsync(
        ReferenceDescriptionInterpretRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string description = request.Description ?? string.Empty;
        string normalized;
        try
        {
            normalized = description.Normalize(NormalizationForm.FormC);
        }
        catch (ArgumentException)
        {
            diagnostics.Add(Error(
                "reference-description-nfc",
                "The reference description is not valid Unicode NFC text."));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        if (!string.Equals(
                description,
                normalized,
                StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                "reference-description-nfc",
                "The reference description must already be normalized to Unicode NFC."));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        int scalarCount = normalized.EnumerateRunes().Count();
        if (scalarCount >
            ReferencePresetAuthoringRules.MaximumDescriptionScalars)
        {
            diagnostics.Add(Error(
                "reference-description-scalars",
                $"The reference description exceeds {ReferencePresetAuthoringRules.MaximumDescriptionScalars} Unicode scalar values."));
            return ValueTask.FromResult(Rejected(diagnostics));
        }

        ImmutableArray<string> tokens = Tokenize(
            normalized.ToLower(CultureInfo.InvariantCulture));
        var traits =
            ImmutableArray.CreateBuilder<ReferenceDescriptionTrait>();
        var unknowns =
            ImmutableArray.CreateBuilder<ReferenceUnknown>();
        var consumed = new bool[tokens.Length];

        for (var index = 0; index < tokens.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TryMatch(
                    tokens,
                    index,
                    out VocabularyPattern? pattern,
                    out int consumedCount,
                    out bool negated,
                    out int phraseStart))
            {
                for (var tokenIndex = index;
                     tokenIndex < index + consumedCount;
                     tokenIndex++)
                {
                    consumed[tokenIndex] = true;
                }

                string sourcePhrase = string.Join(
                    ' ',
                    tokens.Skip(index).Take(consumedCount));
                ReferenceDescriptionVocabularyEntry entry =
                    pattern!.Entry;
                traits.Add(new ReferenceDescriptionTrait(
                    sourcePhrase,
                    entry.Kind,
                    negated
                        ? -entry.Strength
                        : entry.Strength,
                    entry.Channel,
                    negated ? 0.95 : 1.0,
                    ReferenceTraitReviewState.Proposed,
                    false));
                _ = phraseStart;
                index += consumedCount;
                continue;
            }

            index++;
        }

        for (var index = 0; index < tokens.Length; index++)
        {
            if (consumed[index] ||
                StopWords.Contains(tokens[index]) ||
                Negations.Contains(tokens[index]))
            {
                continue;
            }

            unknowns.Add(new ReferenceUnknown(
                tokens[index],
                "not-in-reference-description-vocabulary-v1",
                ReferenceUnknownReviewState.Unreviewed));
        }

        foreach (IGrouping<
                     ReferenceDescriptionTraitKind,
                     ReferenceDescriptionTrait> group in
                 traits.GroupBy(item => item.Kind))
        {
            bool hasPositive =
                group.Any(item => item.Strength > 0.0);
            bool hasNegative =
                group.Any(item => item.Strength < 0.0);
            if (hasPositive && hasNegative)
            {
                diagnostics.Add(new Diagnostic(
                    "reference-description-competing",
                    DiagnosticSeverity.Warning,
                    $"Competing description phrases target '{group.Key}' and require explicit review."));
            }
        }

        return ValueTask.FromResult(
            new ReferenceDescriptionInterpretResult(
                traits.ToImmutable(),
                unknowns.ToImmutable(),
                diagnostics.ToImmutable()));
    }

    private static bool TryMatch(
        ImmutableArray<string> tokens,
        int index,
        out VocabularyPattern? matched,
        out int consumedCount,
        out bool negated,
        out int phraseStart)
    {
        negated = false;
        phraseStart = index;
        if (Negations.Contains(tokens[index]))
        {
            negated = true;
            phraseStart++;
            if (phraseStart < tokens.Length &&
                tokens[phraseStart] is "a" or "an")
            {
                phraseStart++;
            }
        }

        foreach (VocabularyPattern pattern in Patterns)
        {
            if (phraseStart + pattern.Tokens.Length >
                tokens.Length)
            {
                continue;
            }

            var matches = true;
            for (var offset = 0;
                 offset < pattern.Tokens.Length;
                 offset++)
            {
                if (!string.Equals(
                        tokens[phraseStart + offset],
                        pattern.Tokens[offset],
                        StringComparison.Ordinal))
                {
                    matches = false;
                    break;
                }
            }

            if (!matches)
            {
                continue;
            }

            matched = pattern;
            consumedCount =
                phraseStart - index + pattern.Tokens.Length;
            return true;
        }

        matched = null;
        consumedCount = 0;
        negated = false;
        phraseStart = index;
        return false;
    }

    private static ImmutableArray<string> Tokenize(string value)
    {
        var tokens = ImmutableArray.CreateBuilder<string>();
        var token = new StringBuilder();
        foreach (Rune rune in value.EnumerateRunes())
        {
            UnicodeCategory category =
                Rune.GetUnicodeCategory(rune);
            bool isWord =
                Rune.IsLetterOrDigit(rune) ||
                category is UnicodeCategory.NonSpacingMark or
                    UnicodeCategory.SpacingCombiningMark or
                    UnicodeCategory.EnclosingMark;
            bool isConnector =
                rune.Value is '-' or '\'' or 0x2019;
            if (isWord ||
                (isConnector && token.Length > 0))
            {
                token.Append(rune);
                continue;
            }

            CommitToken(token, tokens);
        }

        CommitToken(token, tokens);
        return tokens.ToImmutable();
    }

    private static void CommitToken(
        StringBuilder token,
        ImmutableArray<string>.Builder tokens)
    {
        if (token.Length == 0)
        {
            return;
        }

        string value = token
            .ToString()
            .Trim('-', '\'', '\u2019');
        token.Clear();
        if (value.Length > 0)
        {
            tokens.Add(value);
        }
    }

    private static ImmutableArray<
        ReferenceDescriptionVocabularyEntry> CreateVocabulary()
    {
        var result = ImmutableArray.CreateBuilder<
            ReferenceDescriptionVocabularyEntry>();

        AddPair(
            result,
            "long face",
            "short face",
            ReferenceDescriptionTraitKind.FaceLength);
        AddPair(
            result,
            "wide face",
            "narrow face",
            ReferenceDescriptionTraitKind.FaceWidth);
        Add(
            result,
            "wide jaw",
            ReferenceDescriptionTraitKind.JawWidth,
            1.0);
        Add(
            result,
            "narrow jaw",
            ReferenceDescriptionTraitKind.JawWidth,
            -1.0);
        Add(
            result,
            "tapered jaw",
            ReferenceDescriptionTraitKind.JawTaper,
            1.0);
        Add(
            result,
            "square jaw",
            ReferenceDescriptionTraitKind.JawShape,
            1.0);
        AddPair(
            result,
            "wide chin",
            "pointed chin",
            ReferenceDescriptionTraitKind.ChinWidth);
        AddPair(
            result,
            "long chin",
            "short chin",
            ReferenceDescriptionTraitKind.ChinHeight);
        AddPair(
            result,
            "projecting chin",
            "receding chin",
            ReferenceDescriptionTraitKind.ChinProjection);
        AddPair(
            result,
            "wide cheeks",
            "narrow cheeks",
            ReferenceDescriptionTraitKind.CheekWidth);
        Add(
            result,
            "high cheeks",
            ReferenceDescriptionTraitKind.CheekProminence,
            0.8);
        Add(
            result,
            "prominent cheeks",
            ReferenceDescriptionTraitKind.CheekProminence,
            1.0);
        AddPair(
            result,
            "large eyes",
            "small eyes",
            ReferenceDescriptionTraitKind.EyeSize);
        AddPair(
            result,
            "wide-set eyes",
            "close-set eyes",
            ReferenceDescriptionTraitKind.EyeSpacing);
        AddPair(
            result,
            "wide set eyes",
            "close set eyes",
            ReferenceDescriptionTraitKind.EyeSpacing);
        AddPair(
            result,
            "upturned eyes",
            "downturned eyes",
            ReferenceDescriptionTraitKind.EyeCant);
        Add(
            result,
            "deep-set eyes",
            ReferenceDescriptionTraitKind.EyeDepth,
            1.0);
        Add(
            result,
            "deep set eyes",
            ReferenceDescriptionTraitKind.EyeDepth,
            1.0);
        AddPair(
            result,
            "high brows",
            "low brows",
            ReferenceDescriptionTraitKind.BrowHeight);
        AddPair(
            result,
            "arched brows",
            "straight brows",
            ReferenceDescriptionTraitKind.BrowArch);
        AddPair(
            result,
            "thick brows",
            "thin brows",
            ReferenceDescriptionTraitKind.BrowThickness);
        AddPair(
            result,
            "wide bridge",
            "narrow bridge",
            ReferenceDescriptionTraitKind.NoseBridgeWidth);
        AddPair(
            result,
            "high bridge",
            "low bridge",
            ReferenceDescriptionTraitKind.NoseBridgeHeight);
        Add(
            result,
            "sloped bridge",
            ReferenceDescriptionTraitKind.NoseBridgeSlope,
            1.0);
        AddPair(
            result,
            "long nose",
            "short nose",
            ReferenceDescriptionTraitKind.NoseLength);
        AddPair(
            result,
            "wide nose",
            "narrow nose",
            ReferenceDescriptionTraitKind.NoseWingWidth);
        AddPair(
            result,
            "upturned nose",
            "downturned nose",
            ReferenceDescriptionTraitKind.NoseTipDirection);
        AddPair(
            result,
            "wide lips",
            "narrow lips",
            ReferenceDescriptionTraitKind.LipWidth);
        AddPair(
            result,
            "full lips",
            "thin lips",
            ReferenceDescriptionTraitKind.LipFullness);

        foreach (string phrase in new[]
                 {
                     "pale complexion",
                     "warm complexion",
                     "cool complexion",
                     "dark complexion",
                     "freckled complexion"
                 })
        {
            Add(
                result,
                phrase,
                ReferenceDescriptionTraitKind.Complexion,
                1.0,
                ReferenceTraitChannel.Color);
        }

        foreach (string phrase in new[]
                 {
                     "black hair",
                     "brown hair",
                     "brunette hair",
                     "blonde hair",
                     "red hair",
                     "auburn hair",
                     "silver hair",
                     "white hair"
                 })
        {
            Add(
                result,
                phrase,
                ReferenceDescriptionTraitKind.HairColor,
                1.0,
                ReferenceTraitChannel.Color);
        }

        foreach (string phrase in new[]
                 {
                     "blue eyes",
                     "green eyes",
                     "brown eyes",
                     "hazel eyes",
                     "gray eyes",
                     "grey eyes",
                     "amber eyes"
                 })
        {
            Add(
                result,
                phrase,
                ReferenceDescriptionTraitKind.EyeColor,
                1.0,
                ReferenceTraitChannel.Color);
        }

        foreach (string phrase in new[]
                 {
                     "smoky makeup",
                     "winged eyeliner",
                     "dark eyeliner",
                     "red lipstick",
                     "dark lipstick",
                     "light makeup"
                 })
        {
            Add(
                result,
                phrase,
                ReferenceDescriptionTraitKind.MakeupIntent,
                1.0,
                ReferenceTraitChannel.Tint);
        }

        foreach (string phrase in new[]
                 {
                     "long hair",
                     "short hair",
                     "side-swept hair",
                     "side swept hair",
                     "updo hair",
                     "ponytail hair"
                 })
        {
            Add(
                result,
                phrase,
                ReferenceDescriptionTraitKind.HeadpartPreference,
                1.0,
                ReferenceTraitChannel.Headpart);
        }

        return result.ToImmutable();
    }

    private static void AddPair(
        ImmutableArray<ReferenceDescriptionVocabularyEntry>.Builder result,
        string positivePhrase,
        string negativePhrase,
        ReferenceDescriptionTraitKind kind)
    {
        Add(result, positivePhrase, kind, 1.0);
        Add(result, negativePhrase, kind, -1.0);
    }

    private static void Add(
        ImmutableArray<ReferenceDescriptionVocabularyEntry>.Builder result,
        string phrase,
        ReferenceDescriptionTraitKind kind,
        double strength,
        ReferenceTraitChannel channel =
            ReferenceTraitChannel.Geometry)
    {
        result.Add(new ReferenceDescriptionVocabularyEntry(
            phrase,
            kind,
            strength,
            channel));
    }

    private static ReferenceDescriptionInterpretResult Rejected(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            ImmutableArray<ReferenceDescriptionTrait>.Empty,
            ImmutableArray<ReferenceUnknown>.Empty,
            diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record VocabularyPattern(
        ReferenceDescriptionVocabularyEntry Entry,
        ImmutableArray<string> Tokens,
        int Ordinal);
}
