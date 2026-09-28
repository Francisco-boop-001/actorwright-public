using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimBodyTransformEditorViewModel : NotifyViewModel
{
    private readonly SkyrimBodyEditorViewModel owner;
    private readonly ImmutableArray<string> catalogNodes;
    private bool loading;

    internal SkyrimBodyTransformEditorViewModel(
        SkyrimBodyEditorViewModel owner,
        ImmutableArray<string> catalogNodes)
    {
        this.owner = owner;
        this.catalogNodes = catalogNodes;
        for (int index = 0; index < 9; index++)
            RotationCells.Add(new SkyrimTransformMatrixCellViewModel(index, index % 4 == 0 ? 1F : 0F));
    }

    public ObservableCollection<SkyrimBodyTransformChoiceRowViewModel> Rows { get; } = [];
    public ObservableCollection<SkyrimTransformMatrixCellViewModel> RotationCells { get; } = [];
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyState => $"No reviewed or preset-carried node is available among {catalogNodes.Length} catalog entries. Enter an explicit node name to author one.";
    public string CatalogStatus => catalogNodes.IsEmpty
        ? "No reviewed skeleton/node catalog · only preset-carried or explicitly typed nodes are available"
        : $"{catalogNodes.Length} reviewed node name(s) · authored nodes are never hidden";

    private SkyrimBodyTransformChoiceRowViewModel? selectedRow;
    public SkyrimBodyTransformChoiceRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (!Set(ref selectedRow, value)) return;
            LoadSelection(value);
            Raise(nameof(CanReset));
        }
    }

    public bool CanReset => SelectedRow?.IsAuthored == true;

    private string nodeName = string.Empty;
    public string NodeName
    {
        get => nodeName;
        set => Set(ref nodeName, value ?? string.Empty);
    }

    private bool firstPerson;
    public bool FirstPerson
    {
        get => firstPerson;
        set => Set(ref firstPerson, value);
    }

    private bool hasScale;
    public bool HasScale { get => hasScale; set => Set(ref hasScale, value); }
    private float scale = 1F;
    public float Scale { get => scale; set => Set(ref scale, value); }
    private bool hasScaleMode;
    public bool HasScaleMode { get => hasScaleMode; set => Set(ref hasScaleMode, value); }
    private int scaleMode;
    public int ScaleMode { get => scaleMode; set => Set(ref scaleMode, value); }
    private bool hasPosition;
    public bool HasPosition { get => hasPosition; set => Set(ref hasPosition, value); }
    private float positionX;
    public float PositionX { get => positionX; set => Set(ref positionX, value); }
    private float positionY;
    public float PositionY { get => positionY; set => Set(ref positionY, value); }
    private float positionZ;
    public float PositionZ { get => positionZ; set => Set(ref positionZ, value); }
    private bool hasRotation;
    public bool HasRotation { get => hasRotation; set => Set(ref hasRotation, value); }

    public bool Apply()
    {
        try
        {
            ImmutableArray<float> position = HasPosition
                ? [PositionX, PositionY, PositionZ]
                : [];
            ImmutableArray<float> rotation = HasRotation
                ? RotationCells.OrderBy(item => item.Index).Select(item => item.Value).ToImmutableArray()
                : [];
            SkyrimBodyEditorDocument changed = SkyrimBodyEditorDocumentRules.UpsertTransform(
                owner.CurrentDocument, NodeName, FirstPerson,
                HasScale ? Scale : null,
                HasScaleMode ? ScaleMode : null,
                position,
                rotation);
            string identity = NodeName.Trim();
            bool fp = FirstPerson;
            owner.Replace(changed, SkyrimBodyEditorSection.Transforms,
                $"{identity} {(fp ? "first-person" : "third-person")} transform updated.");
            SelectedRow = Rows.FirstOrDefault(item => item.FirstPerson == fp &&
                string.Equals(item.Node, identity, StringComparison.OrdinalIgnoreCase));
            return true;
        }
        catch (ArgumentException exception)
        {
            owner.ValidationMessage = exception.Message;
            return false;
        }
        catch (InvalidOperationException exception)
        {
            owner.ValidationMessage = exception.Message;
            return false;
        }
    }

    public void ResetSelected()
    {
        if (SelectedRow is not { IsAuthored: true } row) return;
        owner.Replace(SkyrimBodyEditorDocumentRules.RemoveTransform(
                owner.CurrentDocument, row.Node, row.FirstPerson),
            SkyrimBodyEditorSection.Transforms,
            $"{row.Node} {(row.FirstPerson ? "first-person" : "third-person")} transform removed.");
    }

    internal void Refresh()
    {
        (string Node, bool FirstPerson)? retained = SelectedRow is null
            ? null
            : (SelectedRow.Node, SelectedRow.FirstPerson);
        Rows.Clear();
        foreach (string node in catalogNodes
                     .Concat(owner.CurrentDocument.NodeTransforms.Select(item => item.Node))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(item => item, StringComparer.Ordinal))
        {
            SkyrimNodeTransform? third = owner.CurrentDocument.NodeTransforms.FirstOrDefault(item =>
                !item.FirstPerson && string.Equals(item.Node, node, StringComparison.OrdinalIgnoreCase));
            bool catalogued = catalogNodes.Any(item => string.Equals(item, node, StringComparison.OrdinalIgnoreCase));
            Rows.Add(new SkyrimBodyTransformChoiceRowViewModel(node, false, third is not null, catalogued));
            foreach (SkyrimNodeTransform first in owner.CurrentDocument.NodeTransforms.Where(item =>
                         item.FirstPerson && string.Equals(item.Node, node, StringComparison.OrdinalIgnoreCase)))
                Rows.Add(new SkyrimBodyTransformChoiceRowViewModel(first.Node, true, true, catalogued));
        }
        SelectedRow = retained is null
            ? null
            : Rows.FirstOrDefault(item => item.FirstPerson == retained.Value.FirstPerson &&
                string.Equals(item.Node, retained.Value.Node, StringComparison.OrdinalIgnoreCase));
        Raise(nameof(HasNoRows));
        Raise(nameof(EmptyState));
        Raise(nameof(CatalogStatus));
        Raise(nameof(CanReset));
    }

    private void LoadSelection(SkyrimBodyTransformChoiceRowViewModel? row)
    {
        if (loading || row is null) return;
        loading = true;
        NodeName = row.Node;
        FirstPerson = row.FirstPerson;
        SkyrimNodeTransform? transform = owner.CurrentDocument.NodeTransforms.FirstOrDefault(item =>
            item.FirstPerson == row.FirstPerson &&
            string.Equals(item.Node, row.Node, StringComparison.OrdinalIgnoreCase));
        HasScale = transform?.Scale is not null;
        Scale = transform?.Scale ?? 1F;
        HasScaleMode = transform?.ScaleMode is not null;
        ScaleMode = transform?.ScaleMode ?? 0;
        HasPosition = transform?.Position.Length == 3;
        PositionX = HasPosition ? transform!.Position[0] : 0F;
        PositionY = HasPosition ? transform!.Position[1] : 0F;
        PositionZ = HasPosition ? transform!.Position[2] : 0F;
        HasRotation = transform?.RotationMatrix.Length == 9;
        for (int index = 0; index < RotationCells.Count; index++)
            RotationCells[index].Value = HasRotation
                ? transform!.RotationMatrix[index]
                : index % 4 == 0 ? 1F : 0F;
        loading = false;
        owner.ValidationMessage = string.Empty;
    }
}

public sealed record SkyrimBodyTransformChoiceRowViewModel(
    string Node,
    bool FirstPerson,
    bool IsAuthored,
    bool IsCatalogued)
{
    public string Perspective => FirstPerson ? "First person" : "Third person";
    public string Authority => IsAuthored
        ? IsCatalogued ? "authored · reviewed node" : "authored · preset-carried node"
        : "reviewed node · identity defaults";
}

public sealed class SkyrimTransformMatrixCellViewModel : NotifyViewModel
{
    private float value;

    internal SkyrimTransformMatrixCellViewModel(int index, float value)
    {
        Index = index;
        this.value = value;
    }

    public int Index { get; }
    public string Label => $"M{Index}";
    public float Value { get => value; set => Set(ref this.value, value); }
}
