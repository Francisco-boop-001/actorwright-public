using System.Collections.ObjectModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimFaceOverlayEditorViewModel : NotifyViewModel
{
    private readonly SkyrimFaceEditorViewModel owner;
    private readonly string emptyState;

    internal SkyrimFaceOverlayEditorViewModel(
        SkyrimFaceEditorViewModel owner,
        int slotLimit)
    {
        this.owner = owner;
        SlotLimit = slotLimit;
        emptyState = $"No RaceMenu Face [OvlN] paint is applied to {owner.RaceEditorId}.";
    }

    public int SlotLimit { get; }
    public ObservableCollection<SkyrimFaceOverlayRowViewModel> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyState => emptyState;
    public string SlotStatus => $"{Rows.Count} of {SlotLimit} reviewed face slot(s) used";

    private SkyrimFaceOverlayRowViewModel? selectedRow;
    public SkyrimFaceOverlayRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            Raise(nameof(CanRemove));
            Raise(nameof(CanMoveUp));
            Raise(nameof(CanMoveDown));
        }
    }

    public bool CanRemove => SelectedRow is not null;
    public bool CanMoveUp => SelectedRow is not null && Rows.IndexOf(SelectedRow) > 0;
    public bool CanMoveDown => SelectedRow is not null &&
                               Rows.IndexOf(SelectedRow) < Rows.Count - 1;

    public void RemoveSelected()
    {
        if (SelectedRow is null) return;
        int slot = SelectedRow.Slot;
        owner.Replace(
            SkyrimFaceEditorDocumentRules.RemoveFaceOverlay(owner.CurrentDocument, slot),
            SkyrimFaceEditorSection.FaceOverlays,
            $"Face overlay slot {slot} removed.");
    }

    public void MoveSelectedUp() => MoveSelected(-1);
    public void MoveSelectedDown() => MoveSelected(1);

    internal void Refresh()
    {
        int? selected = SelectedRow?.Slot;
        Rows.Clear();
        foreach (RaceMenuBodyOverlay overlay in
                 SkyrimFaceEditorDocumentRules.FaceOverlaysInDrawOrder(owner.CurrentDocument))
        {
            _ = SkyrimFaceEditorDocumentRules.TryGetFaceOverlaySlot(overlay.Node, out int slot);
            Rows.Add(new SkyrimFaceOverlayRowViewModel(slot, overlay, changed =>
                owner.Replace(
                    SkyrimFaceEditorDocumentRules.SetFaceOverlayAppearance(
                        owner.CurrentDocument,
                        slot,
                        changed.TintEnabled,
                        changed.Red,
                        changed.Green,
                        changed.Blue,
                        changed.Opacity),
                    SkyrimFaceEditorSection.FaceOverlays,
                    $"Face overlay slot {slot} appearance updated.",
                    refreshSection: false)));
        }
        SelectedRow = selected is null
            ? null
            : Rows.FirstOrDefault(item => item.Slot == selected.Value);
        Raise(nameof(HasRows));
        Raise(nameof(HasNoRows));
        Raise(nameof(EmptyState));
        Raise(nameof(SlotStatus));
        Raise(nameof(CanRemove));
        Raise(nameof(CanMoveUp));
        Raise(nameof(CanMoveDown));
    }

    private void MoveSelected(int delta)
    {
        if (SelectedRow is null) return;
        int row = Rows.IndexOf(SelectedRow);
        int target = row + delta;
        if (target < 0 || target >= Rows.Count) return;
        int slot = SelectedRow.Slot;
        int adjacent = Rows[target].Slot;
        owner.Replace(
            SkyrimFaceEditorDocumentRules.MoveFaceOverlay(
                owner.CurrentDocument, slot, adjacent),
            SkyrimFaceEditorSection.FaceOverlays,
            $"Face overlay slots {slot} and {adjacent} swapped.");
        SelectedRow = Rows.FirstOrDefault(item => item.Slot == adjacent);
    }
}

public sealed class SkyrimFaceOverlayRowViewModel : NotifyViewModel
{
    private readonly Action<SkyrimFaceOverlayAppearance> changed;

    internal SkyrimFaceOverlayRowViewModel(
        int slot,
        RaceMenuBodyOverlay overlay,
        Action<SkyrimFaceOverlayAppearance> changed)
    {
        Slot = slot;
        Node = overlay.Node;
        Diffuse = overlay.Diffuse ?? string.Empty;
        Normal = overlay.Normal ?? string.Empty;
        tintEnabled = overlay.Tint.Length >= 3;
        red = tintEnabled ? overlay.Tint[0] : 1F;
        green = tintEnabled ? overlay.Tint[1] : 1F;
        blue = tintEnabled ? overlay.Tint[2] : 1F;
        opacity = overlay.Alpha ?? 1F;
        this.changed = changed;
    }

    public int Slot { get; }
    public string Node { get; }
    public string Diffuse { get; }
    public string Normal { get; }
    public string Label => $"{Node} · {System.IO.Path.GetFileName(Diffuse)}";

    private bool tintEnabled;
    public bool TintEnabled
    {
        get => tintEnabled;
        set { if (Set(ref tintEnabled, value)) Publish(); }
    }

    private float red;
    public float Red
    {
        get => red;
        set { if (Set(ref red, Math.Clamp(value, 0F, 1F))) Publish(); }
    }

    private float green;
    public float Green
    {
        get => green;
        set { if (Set(ref green, Math.Clamp(value, 0F, 1F))) Publish(); }
    }

    private float blue;
    public float Blue
    {
        get => blue;
        set { if (Set(ref blue, Math.Clamp(value, 0F, 1F))) Publish(); }
    }

    private float opacity;
    public float Opacity
    {
        get => opacity;
        set { if (Set(ref opacity, Math.Clamp(value, 0F, 1F))) Publish(); }
    }

    private void Publish() => changed(new SkyrimFaceOverlayAppearance(
        TintEnabled, Red, Green, Blue, Opacity));
}

public sealed record SkyrimFaceOverlayAppearance(
    bool TintEnabled,
    float Red,
    float Green,
    float Blue,
    float Opacity);
