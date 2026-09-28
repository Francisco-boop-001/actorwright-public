using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimMainWorkspaceRowViewModel : NotifyViewModel
{
    private SkyrimMainWorkspaceDraft draft;
    private bool isSelected;

    public SkyrimMainWorkspaceRowViewModel(
        SkyrimMainWorkspaceRecord source)
    {
        ArgumentNullException.ThrowIfNull(source);
        Source = source;
        draft = SkyrimMainWorkspaceDraft.Clean(source.Identity);
    }

    public SkyrimMainWorkspaceRecord Source { get; }

    public SkyrimMainWorkspaceIdentity Identity => Source.Identity;

    public string EditorId => Source.EditorId ?? "—";

    public string Name => Source.Name ?? "—";

    public string KindText => Source.Kind ==
        SkyrimMainWorkspaceRecordKind.Npc ? "NPC" : "Leveled NPC";

    public bool IsSelected
    {
        get => isSelected;
        set => Set(ref isSelected, value);
    }

    public bool IsChanged => draft.IsChanged;

    public bool IsDeletePending => draft.IsDeletePending;

    public string StateText
    {
        get
        {
            var states = new List<string>();
            if (Source.IsSourceDeleted)
                states.Add("Deleted in winning source");
            if (Source.IsEmptyLeveledList)
                states.Add("Empty leveled NPC list");
            if (IsChanged)
                states.Add("Changed draft");
            if (IsDeletePending)
                states.Add("Marked for deletion");
            return states.Count == 0 ? "Clean" : string.Join(" · ", states);
        }
    }

    public string IdentityText =>
        $"{Identity.OwnerPlugin.Value}|{Identity.FormId}|" +
        Identity.Signature;

    public string ProviderText =>
        Identity.OwnerPlugin == Identity.WinningProvider
            ? $"Owner and winner: {Identity.OwnerPlugin.Value}"
            : $"Owner: {Identity.OwnerPlugin.Value} · winner: " +
              Identity.WinningProvider.Value;

    public string DetailText
    {
        get
        {
            if (Source.Kind ==
                SkyrimMainWorkspaceRecordKind.LeveledNpc)
                return Source.IsEmptyLeveledList
                    ? "No LVLO member rows; random member and reroll are unavailable."
                    : $"{Source.LeveledNpcEntries.Length} ordered LVLO member row(s).";
            string sex = Source.Sex?.ToString() ?? "Unknown sex";
            string categories = Source.Categories.IsDefaultOrEmpty
                ? "no category"
                : string.Join(
                    ", ",
                    Source.Categories.Select(item =>
                        item.ToString().ToLowerInvariant()));
            return $"{sex} · {categories} · raw " +
                   Source.RawRecordSha256[..Math.Min(
                       12,
                       Source.RawRecordSha256.Length)] + "…";
        }
    }

    internal SkyrimMainWorkspaceDraft Draft => draft;

    internal void ApplyDraft(SkyrimMainWorkspaceDraft value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Identity != Identity)
            throw new ArgumentException(
                "The draft belongs to a different workspace row.",
                nameof(value));
        if (draft == value)
            return;
        draft = value;
        Raise(nameof(IsChanged));
        Raise(nameof(IsDeletePending));
        Raise(nameof(StateText));
    }
}
