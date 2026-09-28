using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimRaceMenuPaintPickerViewModel : NotifyViewModel
{
    private readonly ImmutableArray<SkyrimRaceMenuPaintChoiceRowViewModel> catalog;

    public SkyrimRaceMenuPaintPickerViewModel(
        SkyrimRaceMenuPaintChoiceResult result,
        SkyrimRaceMenuPaintCategory category,
        string? currentPath,
        bool allowNone)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (!result.Accepted)
            throw new ArgumentException("The paint picker requires an accepted catalog result.", nameof(result));
        if (result.Candidates.Any(item => item is null || item.Sources.IsDefaultOrEmpty ||
                                          item.TextureSlots.IsDefault))
            throw new ArgumentException(
                "Every paint choice must retain registration evidence and initialized texture slots.",
                nameof(result));
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category));
        Category = category;
        Title = $"Choose {category.ToWireName()} paint";
        Context = result.Summary is null
            ? "Compiled RaceMenu registration catalog"
            : $"{result.Summary.CandidateCount} registered choice(s) · " +
              $"{result.Summary.PaintScriptCount} winning paint script(s) · " +
              $"catalog {result.Summary.CatalogSha256.Value[..12]}…";

        var rows = ImmutableArray.CreateBuilder<SkyrimRaceMenuPaintChoiceRowViewModel>();
        if (allowNone) rows.Add(SkyrimRaceMenuPaintChoiceRowViewModel.Clear());
        rows.AddRange(result.Candidates.Select(SkyrimRaceMenuPaintChoiceRowViewModel.From));
        catalog = rows.ToImmutable();
        RefreshVisibleRows();

        if (allowNone && string.IsNullOrWhiteSpace(currentPath))
        {
            SelectedRow = catalog.First(item => item.IsClear);
        }
        else if (!string.IsNullOrWhiteSpace(currentPath))
        {
            SelectedRow = catalog.FirstOrDefault(item =>
                !item.IsClear && PathsEquivalent(item.RegisteredPath, currentPath));
        }
    }

    public SkyrimRaceMenuPaintCategory Category { get; }
    public string Title { get; }
    public string Context { get; }
    public ObservableCollection<SkyrimRaceMenuPaintChoiceRowViewModel> VisibleRows { get; } = [];
    public bool IsAccepted { get; private set; }
    public string? AcceptedPath { get; private set; }
    public SkyrimRaceMenuPaintChoiceCandidate? AcceptedEntry { get; private set; }
    public bool HasNoVisibleRows => VisibleRows.Count == 0;
    public string VisibleCount => $"{VisibleRows.Count} visible choice(s)";
    public string EmptyState =>
        $"No registered {Category.ToWireName()} paint matches this filter.";
    public bool CanAccept => SelectedRow is not null;
    public string SelectionTitle => SelectedRow?.DisplayName ?? "Nothing selected";
    public string SelectionPath => SelectedRow?.RegisteredPath ?? "Select one catalog row.";
    public string SelectionEvidence => SelectedRow?.Evidence ??
        "Only compiled-Papyrus registrations from the reviewed copied Data root can be selected.";
    public string AuthorityNotice =>
        $"{Category.ToWireName()} registration evidence only · texture rendering and Skyrim runtime authority false";

    private string filterText = string.Empty;
    public string FilterText
    {
        get => filterText;
        set
        {
            if (Set(ref filterText, value ?? string.Empty)) RefreshVisibleRows();
        }
    }

    private SkyrimRaceMenuPaintChoiceRowViewModel? selectedRow;
    public SkyrimRaceMenuPaintChoiceRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            ClearAccepted();
            ValidationMessage = string.Empty;
            Raise(nameof(CanAccept));
            Raise(nameof(SelectionTitle));
            Raise(nameof(SelectionPath));
            Raise(nameof(SelectionEvidence));
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
            ValidationMessage = "Select one registered paint or the explicit clear row first.";
            return false;
        }
        IsAccepted = true;
        AcceptedPath = SelectedRow.IsClear ? string.Empty : SelectedRow.RegisteredPath;
        AcceptedEntry = SelectedRow.Candidate;
        ValidationMessage = string.Empty;
        return true;
    }

    public void Cancel()
    {
        ClearAccepted();
        ValidationMessage = string.Empty;
    }

    internal static bool PathsEquivalent(string left, string right) =>
        string.Equals(NormalizePath(left), NormalizePath(right),
            StringComparison.OrdinalIgnoreCase);

    private void RefreshVisibleRows()
    {
        SkyrimRaceMenuPaintChoiceRowViewModel? retained = SelectedRow;
        string query = FilterText.Trim();
        VisibleRows.Clear();
        foreach (SkyrimRaceMenuPaintChoiceRowViewModel row in catalog.Where(item =>
                     item.IsClear ||
                     query.Length == 0 ||
                     item.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                     item.RegisteredPath.Contains(query, StringComparison.OrdinalIgnoreCase)))
        {
            VisibleRows.Add(row);
        }
        if (retained is not null && !VisibleRows.Contains(retained)) SelectedRow = null;
        Raise(nameof(HasNoVisibleRows));
        Raise(nameof(VisibleCount));
        Raise(nameof(EmptyState));
    }

    private void ClearAccepted()
    {
        IsAccepted = false;
        AcceptedPath = null;
        AcceptedEntry = null;
    }

    private static string NormalizePath(string value)
    {
        string normalized = value.Trim().Replace('\\', '/');
        return normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
            ? normalized["textures/".Length..]
            : normalized;
    }
}

public sealed class SkyrimRaceMenuPaintChoiceRowViewModel
{
    private SkyrimRaceMenuPaintChoiceRowViewModel(
        SkyrimRaceMenuPaintChoiceCandidate? candidate,
        bool isClear,
        string displayName,
        string registeredPath,
        string evidence)
    {
        Candidate = candidate;
        IsClear = isClear;
        DisplayName = displayName;
        RegisteredPath = registeredPath;
        Evidence = evidence;
    }

    public SkyrimRaceMenuPaintChoiceCandidate? Candidate { get; }
    public bool IsClear { get; }
    public string DisplayName { get; }
    public string RegisteredPath { get; }
    public string Evidence { get; }

    internal static SkyrimRaceMenuPaintChoiceRowViewModel Clear() =>
        new(null, true, "(None — clear)", string.Empty,
            "Explicit clear · returns an empty path and no typed paint entry");

    internal static SkyrimRaceMenuPaintChoiceRowViewModel From(
        SkyrimRaceMenuPaintChoiceCandidate candidate)
    {
        SkyrimRaceMenuPaintRegistrationSource source = candidate.Sources[0];
        string evidence =
            $"{source.ProviderKind} · {source.ScriptPath.Value} · " +
            $"PEX {source.PexSha256.Value[..12]}…" +
            (candidate.Sources.Length == 1
                ? string.Empty
                : $" · {candidate.Sources.Length} registration sources");
        return new SkyrimRaceMenuPaintChoiceRowViewModel(
            candidate,
            false,
            candidate.DisplayName,
            candidate.RegisteredPath.Value,
            evidence);
    }
}
