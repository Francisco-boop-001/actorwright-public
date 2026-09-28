using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed record TypedFormIdPickerOptions(
    string Title,
    string Purpose,
    ImmutableArray<RecordSignature> AllowedSignatures,
    FormReference? CurrentReference,
    bool AllowNull,
    Func<FormChoiceCandidate, bool>? CompatibilityFilter = null,
    Func<FormChoiceCandidate, bool>? DeleteOrRevert = null);

public sealed class TypedFormIdPickerViewModel : NotifyViewModel
{
    private readonly List<TypedFormIdPickerRowViewModel> allRows;
    private readonly Func<FormChoiceCandidate, bool>? deleteOrRevert;

    public TypedFormIdPickerViewModel(
        FormChoiceSearchResult search,
        TypedFormIdPickerOptions options)
    {
        ArgumentNullException.ThrowIfNull(search);
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowedSignatures.IsDefaultOrEmpty)
            throw new ArgumentException(
                "At least one typed record signature is required.", nameof(options));

        Title = options.Title;
        Purpose = options.Purpose;
        AllowedSignatures = string.Join(" / ",
            options.AllowedSignatures.Select(item => item.Value));
        CompatibilityFilterAvailable = options.CompatibilityFilter is not null;
        deleteOrRevert = options.DeleteOrRevert;
        Diagnostics = search.Diagnostics;

        var allowed = options.AllowedSignatures.ToImmutableHashSet();
        allRows = search.Candidates
            .Where(candidate => allowed.Contains(candidate.Signature) && !candidate.IsDeleted)
            .Select(candidate => TypedFormIdPickerRowViewModel.From(
                candidate,
                options.CompatibilityFilter?.Invoke(candidate) ?? true))
            .ToList();
        if (options.CurrentReference is { } current &&
            allRows.All(row => row.Reference != current))
        {
            allRows.Add(TypedFormIdPickerRowViewModel.Current(current));
        }
        allRows.Sort(TypedFormIdPickerRowViewModel.Compare);
        if (options.AllowNull)
            allRows.Insert(0, TypedFormIdPickerRowViewModel.Null());

        Refresh();
        SelectedRow = options.CurrentReference is { } selected
            ? VisibleRows.FirstOrDefault(row => row.Reference == selected)
            : options.AllowNull
                ? VisibleRows.FirstOrDefault(row => row.IsNull)
                : null;
    }

    public string Title { get; }
    public string Purpose { get; }
    public string AllowedSignatures { get; }
    public bool CompatibilityFilterAvailable { get; }
    public bool DeleteOrRevertAvailable => deleteOrRevert is not null;
    public ImmutableArray<Diagnostic> Diagnostics { get; }
    public ObservableCollection<TypedFormIdPickerRowViewModel> VisibleRows { get; } = [];
    public bool HasNoVisibleRows => VisibleRows.Count == 0;
    public bool CanAccept => SelectedRow is { IsDeleted: false };
    public bool CanDeleteOrRevert =>
        deleteOrRevert is not null && SelectedRow?.Candidate is not null;
    public string EmptyState => allRows.Count == 0
        ? $"No non-deleted {AllowedSignatures} records are available in the reviewed copied plugin closure."
        : "No typed records match the current search and compatibility filters.";
    public string VisibleCount => $"{VisibleRows.Count} of {allRows.Count} typed row(s) visible";
    public string SelectionTitle => SelectedRow?.DisplayName ?? "No record selected";
    public string SelectionReference => SelectedRow is null
        ? "Select one row deliberately."
        : SelectedRow.IsNull
            ? "NULL / zero FormID"
            : SelectedRow.Reference?.ToString() ?? "Unavailable reference";
    public string SelectionProvenance => SelectedRow?.ProvenanceText ??
        "The picker returns no value until a row is selected.";
    public bool IsAccepted { get; private set; }
    public FormReference? AcceptedReference { get; private set; }
    public FormId? AcceptedFormId { get; private set; }

    private string filterText = string.Empty;
    public string FilterText
    {
        get => filterText;
        set
        {
            if (Set(ref filterText, value ?? string.Empty)) Refresh();
        }
    }

    private bool showAll;
    public bool ShowAll
    {
        get => showAll;
        set
        {
            if (Set(ref showAll, value)) Refresh();
        }
    }

    private TypedFormIdPickerRowViewModel? selectedRow;
    public TypedFormIdPickerRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            ValidationMessage = string.Empty;
            Raise(nameof(CanAccept));
            Raise(nameof(CanDeleteOrRevert));
            Raise(nameof(SelectionTitle));
            Raise(nameof(SelectionReference));
            Raise(nameof(SelectionProvenance));
        }
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    public bool TryAccept()
    {
        ClearAccepted();
        if (SelectedRow is null)
        {
            ValidationMessage = "Select a typed record or the explicit NULL row first.";
            return false;
        }
        if (SelectedRow.IsDeleted)
        {
            ValidationMessage = "A deleted record cannot be selected.";
            return false;
        }

        IsAccepted = true;
        AcceptedReference = SelectedRow.Reference;
        AcceptedFormId = SelectedRow.Reference?.FormId ?? new FormId(0);
        ValidationMessage = string.Empty;
        return true;
    }

    public bool TryDeleteOrRevertSelected()
    {
        if (deleteOrRevert is null || SelectedRow?.Candidate is not { } candidate)
        {
            ValidationMessage = "Select a caller-owned draft that can be deleted or reverted.";
            return false;
        }
        if (!deleteOrRevert(candidate))
        {
            ValidationMessage = "Delete or Revert was cancelled; the typed row was preserved.";
            return false;
        }

        var removed = SelectedRow;
        allRows.Remove(removed);
        SelectedRow = null;
        ClearAccepted();
        Refresh();
        ValidationMessage = "The caller confirmed Delete or Revert for this picker session.";
        return true;
    }

    public void Cancel()
    {
        ClearAccepted();
        ValidationMessage = string.Empty;
    }

    private void Refresh()
    {
        var retained = SelectedRow;
        var query = FilterText.Trim();
        var rows = allRows.Where(row =>
            row.IsNull ||
            ((ShowAll || row.IsCompatible || !CompatibilityFilterAvailable) &&
             Matches(row, query)));
        VisibleRows.Clear();
        foreach (var row in rows) VisibleRows.Add(row);
        if (retained is not null && !VisibleRows.Contains(retained))
            SelectedRow = null;
        Raise(nameof(HasNoVisibleRows));
        Raise(nameof(EmptyState));
        Raise(nameof(VisibleCount));
    }

    private void ClearAccepted()
    {
        IsAccepted = false;
        AcceptedReference = null;
        AcceptedFormId = null;
    }

    private static bool Matches(TypedFormIdPickerRowViewModel row, string query) =>
        query.Length == 0 || row.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.EditorId.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.FormIdText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.PluginText.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        row.SignatureText.Contains(query, StringComparison.OrdinalIgnoreCase);
}

public sealed class TypedFormIdPickerRowViewModel
{
    private TypedFormIdPickerRowViewModel(
        FormChoiceCandidate? candidate,
        FormReference? reference,
        string displayName,
        string editorId,
        string formIdText,
        string pluginText,
        string signatureText,
        string provenanceText,
        bool isNull,
        bool isCompatible,
        bool isDeleted)
    {
        Candidate = candidate;
        Reference = reference;
        DisplayName = displayName;
        EditorId = editorId;
        FormIdText = formIdText;
        PluginText = pluginText;
        SignatureText = signatureText;
        ProvenanceText = provenanceText;
        IsNull = isNull;
        IsCompatible = isCompatible;
        IsDeleted = isDeleted;
    }

    public FormChoiceCandidate? Candidate { get; }
    public FormReference? Reference { get; }
    public string DisplayName { get; }
    public string EditorId { get; }
    public string FormIdText { get; }
    public string PluginText { get; }
    public string SignatureText { get; }
    public string ProvenanceText { get; }
    public bool IsNull { get; }
    public bool IsCompatible { get; }
    public bool IsDeleted { get; }

    internal static TypedFormIdPickerRowViewModel From(
        FormChoiceCandidate candidate,
        bool isCompatible)
    {
        var reference = new FormReference(candidate.Provenance.SourcePlugin, candidate.FormId);
        var name = candidate.Name ?? candidate.EditorId ?? "Unnamed record";
        var chain = candidate.Provenance.OverrideChain.IsDefaultOrEmpty
            ? candidate.Plugin.Value
            : string.Join(" -> ", candidate.Provenance.OverrideChain.Select(item => item.Value));
        return new TypedFormIdPickerRowViewModel(
            candidate,
            reference,
            name,
            candidate.EditorId ?? string.Empty,
            candidate.FormId.ToString(),
            candidate.Provenance.SourcePlugin.Value,
            candidate.Signature.Value,
            $"{candidate.Provenance.Kind} authority · winning provider {candidate.Plugin.Value} · chain {chain}",
            false,
            isCompatible,
            candidate.IsDeleted);
    }

    internal static TypedFormIdPickerRowViewModel Current(FormReference current) => new(
        null,
        current,
        "Current value (not present in the copied catalog)",
        string.Empty,
        current.FormId.ToString(),
        current.Plugin.Value,
        "CURRENT",
        "Preservation-only current value; choosing a different record requires typed catalog authority.",
        false,
        true,
        false);

    internal static TypedFormIdPickerRowViewModel Null() => new(
        null,
        null,
        "None / NULL",
        string.Empty,
        string.Empty,
        string.Empty,
        "NULL",
        "Explicit zero FormID.",
        true,
        true,
        false);

    internal static int Compare(
        TypedFormIdPickerRowViewModel left,
        TypedFormIdPickerRowViewModel right)
    {
        var signature = string.Compare(
            left.SignatureText, right.SignatureText, StringComparison.Ordinal);
        if (signature != 0) return signature;
        var name = string.Compare(
            left.DisplayName, right.DisplayName, StringComparison.OrdinalIgnoreCase);
        if (name != 0) return name;
        return string.Compare(left.FormIdText, right.FormIdText, StringComparison.Ordinal);
    }
}
