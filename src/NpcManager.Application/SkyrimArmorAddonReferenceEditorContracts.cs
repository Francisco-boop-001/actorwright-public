using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record SkyrimArmorAddonRaceEvidence(
    bool Complete,
    FormReference OwningArmorRace,
    FormReference PrimaryRace,
    ImmutableArray<FormReference> AdditionalRaces);

public sealed record SkyrimArmorAddonReferenceCandidate(
    FormReference Reference,
    RecordSignature Signature,
    string DisplayName,
    bool IsDeleted,
    bool IsStale,
    SkyrimArmorAddonRaceEvidence Compatibility,
    ArmorAddonProposalRequest? AuthoredProposal = null);

public sealed record SkyrimArmorAddonReferenceRow(
    FormReference Reference,
    string DisplayName,
    SkyrimArmorAddonRaceEvidence Compatibility,
    ArmorAddonProposalRequest? AuthoredProposal = null);

public sealed record SkyrimArmorAddonDeepEditResult(
    bool Committed,
    SkyrimArmorAddonReferenceRow? Row);

public sealed record SkyrimArmorAddonReferenceTransition(
    bool Accepted,
    bool AutoAccept,
    SkyrimArmorAddonReferenceRow? Row,
    ImmutableArray<Diagnostic> Diagnostics);

public static class SkyrimArmorAddonReferenceEditorRules
{
    private static readonly RecordSignature ArmorAddonSignature = new("ARMA");

    public static SkyrimArmorAddonReferenceTransition Choose(
        GameEdition edition,
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceCandidate? candidate)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("armor-addon-reference-edition",
                "The Skyrim ARMA reference editor accepts Skyrim Special Edition only."));
        if (candidate is null)
            diagnostics.Add(Error("armor-addon-reference-candidate-missing",
                "Choose one reviewed race-compatible ARMA."));
        else
            ValidateCandidate(owningArmorRace, candidate, diagnostics);
        if (HasErrors(diagnostics) || candidate is null)
            return Refused(diagnostics);
        return new(true, false,
            new SkyrimArmorAddonReferenceRow(
                candidate.Reference, candidate.DisplayName,
                candidate.Compatibility,
                candidate.AuthoredProposal),
            diagnostics.ToImmutable());
    }

    public static SkyrimArmorAddonReferenceTransition Accept(
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceRow? row)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (row is null)
            diagnostics.Add(Error("armor-addon-reference-row-missing",
                "Choose one reviewed race-compatible ARMA before using it."));
        else
            ValidateRow(owningArmorRace, row, diagnostics);
        return HasErrors(diagnostics) || row is null
            ? Refused(diagnostics)
            : new(true, false, row with { }, diagnostics.ToImmutable());
    }

    public static SkyrimArmorAddonReferenceTransition ApplyDeepEdit(
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceRow? current,
        SkyrimArmorAddonDeepEditResult? child)
    {
        if (current is null)
            return new(false, false, null,
            [
                Error("armor-addon-reference-row-missing",
                    "Choose one reviewed race-compatible ARMA before deep editing it.")
            ]);
        if (child is null)
            return new(false, false, null,
            [
                Error("armor-addon-reference-child-missing",
                    "The ARMA deep editor did not return a typed result.")
            ]);
        if (!child.Committed)
            return new(false, false, null,
            [
                Error("armor-addon-reference-child-cancelled",
                    "The ARMA deep edit was cancelled; the current working row was preserved.")
            ]);
        if (child.Row is null)
            return new(false, false, null,
            [
                Error("armor-addon-reference-child-missing",
                    "A committed ARMA deep edit did not return a typed row.")
            ]);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRow(owningArmorRace, child.Row, diagnostics);
        return HasErrors(diagnostics)
            ? Refused(diagnostics)
            : new(true, true, child.Row with { }, diagnostics.ToImmutable());
    }

    public static SkyrimArmorAddonReferenceTransition Cancel() =>
        new(false, false, null, []);

    public static ImmutableArray<Diagnostic> ValidateRow(
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRow(owningArmorRace, row, diagnostics);
        return diagnostics.ToImmutable();
    }

    private static void ValidateCandidate(
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceCandidate candidate,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateReference(candidate.Reference, "ARMA candidate", diagnostics);
        if (candidate.Signature != ArmorAddonSignature)
            diagnostics.Add(Error("armor-addon-reference-signature",
                "The selected record must be an ARMA."));
        if (candidate.IsDeleted)
            diagnostics.Add(Error("armor-addon-reference-deleted",
                "A deleted ARMA cannot be selected."));
        if (candidate.IsStale)
            diagnostics.Add(Error("armor-addon-reference-stale",
                "The ARMA candidate is stale against the reviewed catalog."));
        ValidateLabel(candidate.DisplayName, diagnostics);
        ValidateCompatibility(owningArmorRace, candidate.Compatibility, diagnostics);
        if (candidate.AuthoredProposal is not null)
            ValidateProposal(new SkyrimArmorAddonReferenceRow(
                candidate.Reference,
                candidate.DisplayName,
                candidate.Compatibility,
                candidate.AuthoredProposal), diagnostics);
    }

    private static void ValidateCompatibility(
        FormReference expectedOwningRace,
        SkyrimArmorAddonRaceEvidence? evidence,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (evidence is null)
        {
            diagnostics.Add(Error("armor-addon-reference-race-evidence",
                "Complete primary and additional-race evidence is required."));
            return;
        }
        if (!evidence.Complete || evidence.AdditionalRaces.IsDefault)
        {
            diagnostics.Add(Error("armor-addon-reference-race-evidence",
                "Complete primary and additional-race evidence is required."));
            return;
        }
        ValidateReference(evidence.OwningArmorRace, "owning armor race", diagnostics);
        ValidateReference(evidence.PrimaryRace, "ARMA primary race", diagnostics);
        foreach (FormReference race in evidence.AdditionalRaces)
            ValidateReference(race, "ARMA additional race", diagnostics);
        if (evidence.AdditionalRaces.Select(ReferenceKey)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            evidence.AdditionalRaces.Length)
            diagnostics.Add(Error("armor-addon-reference-race-duplicate",
                "ARMA additional-race evidence may not contain duplicates."));
        if (!SameReference(expectedOwningRace, evidence.OwningArmorRace))
            diagnostics.Add(Error("armor-addon-reference-race-context",
                "The ARMA compatibility evidence belongs to a different owning armor race."));
        if (!SameReference(evidence.OwningArmorRace, evidence.PrimaryRace) &&
            !evidence.AdditionalRaces.Any(race =>
                SameReference(evidence.OwningArmorRace, race)))
            diagnostics.Add(Error("armor-addon-reference-race-incompatible",
                "The ARMA does not support the owning armor race."));
    }

    private static void ValidateRow(
        FormReference owningArmorRace,
        SkyrimArmorAddonReferenceRow row,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateReference(row.Reference, "ARMA row", diagnostics);
        ValidateLabel(row.DisplayName, diagnostics);
        ValidateCompatibility(owningArmorRace, row.Compatibility, diagnostics);
        ValidateProposal(row, diagnostics);
    }

    private static void ValidateProposal(
        SkyrimArmorAddonReferenceRow row,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (row.AuthoredProposal is not { } proposal) return;
        if (proposal.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("armor-addon-reference-proposal-edition",
                "A nested authored ARMA proposal must target Skyrim Special Edition."));
        FormId expected = proposal.Mode == ArmorAddonProposalMode.New
            ? proposal.TargetFormId ?? default
            : proposal.SourceFormId;
        if (expected != row.Reference.FormId)
            diagnostics.Add(Error("armor-addon-reference-proposal-form",
                "The nested ARMA proposal identity does not match the accepted row."));
        if (proposal.Mode == ArmorAddonProposalMode.Override &&
            !string.Equals(Path.GetFileName(proposal.SourcePlugin.Value),
                row.Reference.Plugin.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-addon-reference-proposal-plugin",
                "An override proposal must retain the qualified source plugin owner."));
    }

    private static void ValidateLabel(
        string? value,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 512 ||
            value.Any(char.IsControl))
            diagnostics.Add(Error("armor-addon-reference-label",
                "ARMA labels must contain 1-512 visible characters."));
    }

    private static void ValidateReference(
        FormReference reference,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference.FormId.Value is > 0 and <= 0x00FF_FFFF &&
            !string.IsNullOrWhiteSpace(reference.Plugin.Value)) return;
        diagnostics.Add(Error("armor-addon-reference-invalid",
            $"The {role} requires a provider and a nonzero plugin-local 24-bit FormID."));
    }

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static string ReferenceKey(FormReference reference) =>
        $"{reference.Plugin.Value}|{reference.FormId.Value:X6}";

    private static SkyrimArmorAddonReferenceTransition Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, diagnostics.ToImmutable());

    private static bool HasErrors(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
