using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimBodyOverlayEditorViewModel : NotifyViewModel
{
    private readonly SkyrimBodyEditorViewModel owner;
    private readonly ImmutableDictionary<BodyOverlayTarget, int> slotLimits;

    internal SkyrimBodyOverlayEditorViewModel(
        SkyrimBodyEditorViewModel owner,
        ImmutableDictionary<BodyOverlayTarget, int> slotLimits)
    {
        this.owner = owner;
        this.slotLimits = slotLimits;
        Targets = new ObservableCollection<SkyrimBodyOverlayTargetOption>(
            Enum.GetValues<BodyOverlayTarget>().Select(value =>
                new SkyrimBodyOverlayTargetOption(value, value.ToString())));
        selectedTarget = Targets[0];
    }

    public ObservableCollection<SkyrimBodyOverlayTargetOption> Targets { get; }
    public ObservableCollection<SkyrimBodyOverlayRowViewModel> Rows { get; } = [];
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyState => $"No Body, Hands, or Feet RaceMenu paint is applied to {owner.Subject}. Hidden Face rows remain preserved.";
    public string SlotStatus => $"{Rows.Count(item => item.Target == SelectedTarget.Value)} of " +
                                $"{slotLimits[SelectedTarget.Value]} reviewed {SelectedTarget.Label} slot(s) used";

    private SkyrimBodyOverlayTargetOption selectedTarget;
    public SkyrimBodyOverlayTargetOption SelectedTarget
    {
        get => selectedTarget;
        set
        {
            if (!Set(ref selectedTarget, value)) return;
            Raise(nameof(SlotStatus));
        }
    }

    private SkyrimBodyOverlayRowViewModel? selectedRow;
    public SkyrimBodyOverlayRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            if (value is not null)
                SelectedTarget = Targets.Single(item => item.Value == value.Target);
            RaiseActions();
        }
    }

    public bool CanRemove => SelectedRow is not null;
    public bool CanMoveUp => AdjacentRow(-1) is not null;
    public bool CanMoveDown => AdjacentRow(1) is not null;

    public void RemoveSelected()
    {
        if (SelectedRow is null) return;
        BodyOverlayTarget target = SelectedRow.Target;
        int slot = SelectedRow.Slot;
        owner.Replace(SkyrimBodyEditorDocumentRules.RemoveBodyOverlay(
                owner.CurrentDocument, target, slot),
            SkyrimBodyEditorSection.BodyOverlays,
            $"{target} overlay slot {slot} removed.");
    }

    public void MoveSelectedUp() => MoveSelected(-1);
    public void MoveSelectedDown() => MoveSelected(1);

    internal void Refresh()
    {
        (BodyOverlayTarget Target, int Slot)? retained = SelectedRow is null
            ? null
            : (SelectedRow.Target, SelectedRow.Slot);
        Rows.Clear();
        foreach (RaceMenuBodyOverlay overlay in
                 SkyrimBodyEditorDocumentRules.BodyOverlaysInDrawOrder(owner.CurrentDocument))
        {
            _ = SkyrimBodyEditorDocumentRules.TryGetBodyOverlayIdentity(
                overlay.Node, out BodyOverlayTarget target, out int slot);
            Rows.Add(new SkyrimBodyOverlayRowViewModel(target, slot, overlay, appearance =>
                owner.Replace(SkyrimBodyEditorDocumentRules.SetBodyOverlayAppearance(
                        owner.CurrentDocument, target, slot,
                        appearance.TintEnabled, appearance.Red, appearance.Green,
                        appearance.Blue, appearance.Opacity),
                    SkyrimBodyEditorSection.BodyOverlays,
                    $"{target} overlay slot {slot} appearance updated.",
                    refreshSection: false)));
        }
        SelectedRow = retained is null
            ? null
            : Rows.FirstOrDefault(item => item.Target == retained.Value.Target &&
                                          item.Slot == retained.Value.Slot);
        Raise(nameof(HasNoRows));
        Raise(nameof(EmptyState));
        Raise(nameof(SlotStatus));
        RaiseActions();
    }

    private void MoveSelected(int delta)
    {
        if (SelectedRow is null || AdjacentRow(delta) is not { } adjacent) return;
        BodyOverlayTarget target = SelectedRow.Target;
        int slot = SelectedRow.Slot;
        int other = adjacent.Slot;
        owner.Replace(SkyrimBodyEditorDocumentRules.MoveBodyOverlay(
                owner.CurrentDocument, target, slot, other),
            SkyrimBodyEditorSection.BodyOverlays,
            $"{target} overlay slots {slot} and {other} swapped.");
        SelectedRow = Rows.FirstOrDefault(item => item.Target == target && item.Slot == other);
    }

    private SkyrimBodyOverlayRowViewModel? AdjacentRow(int delta)
    {
        if (SelectedRow is null) return null;
        int row = Rows.IndexOf(SelectedRow);
        int target = row + delta;
        if (target < 0 || target >= Rows.Count) return null;
        SkyrimBodyOverlayRowViewModel candidate = Rows[target];
        return candidate.Target == SelectedRow.Target ? candidate : null;
    }

    private void RaiseActions()
    {
        Raise(nameof(CanRemove));
        Raise(nameof(CanMoveUp));
        Raise(nameof(CanMoveDown));
    }
}

public sealed record SkyrimBodyOverlayTargetOption(BodyOverlayTarget Value, string Label);

public sealed class SkyrimBodyOverlayRowViewModel : NotifyViewModel
{
    private readonly Action<SkyrimBodyOverlayAppearance> changed;

    internal SkyrimBodyOverlayRowViewModel(
        BodyOverlayTarget target,
        int slot,
        RaceMenuBodyOverlay overlay,
        Action<SkyrimBodyOverlayAppearance> changed)
    {
        Target = target;
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

    public BodyOverlayTarget Target { get; }
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

    private void Publish() => changed(new SkyrimBodyOverlayAppearance(
        TintEnabled, Red, Green, Blue, Opacity));
}

public sealed record SkyrimBodyOverlayAppearance(
    bool TintEnabled,
    float Red,
    float Green,
    float Blue,
    float Opacity);
