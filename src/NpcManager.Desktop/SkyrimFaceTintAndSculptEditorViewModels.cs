using System.Collections.ObjectModel;
using NpcManager.Application;

namespace NpcManager.Desktop;

public sealed class SkyrimFaceTintEditorViewModel : NotifyViewModel
{
    private readonly SkyrimFaceEditorViewModel owner;
    private readonly string emptyState;

    internal SkyrimFaceTintEditorViewModel(SkyrimFaceEditorViewModel owner)
    {
        this.owner = owner;
        emptyState = $"{owner.RaceEditorId} declares no tint layers for {owner.Sex.ToString().ToLowerInvariant()}.";
    }

    public ObservableCollection<SkyrimFaceTintRowViewModel> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyState => emptyState;

    internal void Refresh()
    {
        Rows.Clear();
        foreach (SkyrimFaceEditorTintLayer layer in owner.CurrentDocument.Tints)
        {
            Rows.Add(new SkyrimFaceTintRowViewModel(layer, changed =>
                owner.Replace(
                    SkyrimFaceEditorDocumentRules.SetTint(owner.CurrentDocument, changed),
                    SkyrimFaceEditorSection.Tints,
                    $"Tint layer {changed.Value.Index} updated.",
                    refreshSection: false)));
        }
        Raise(nameof(HasRows));
        Raise(nameof(HasNoRows));
        Raise(nameof(EmptyState));
    }
}

public sealed class SkyrimFaceTintRowViewModel : NotifyViewModel
{
    private readonly Action<SkyrimFaceEditorTintLayer> changed;
    private SkyrimFaceEditorTintLayer layer;

    internal SkyrimFaceTintRowViewModel(
        SkyrimFaceEditorTintLayer layer,
        Action<SkyrimFaceEditorTintLayer> changed)
    {
        this.layer = layer;
        this.changed = changed;
    }

    public ushort Index => layer.Value.Index;
    public string Label => $"Tint {Index}";
    public string MaskPath => layer.MaskOverride?.Value ??
                              layer.RaceDefaultMask?.Value ?? "No mask path";
    public string MaskAuthority => layer.MaskOverride is null
        ? "Race default mask"
        : "RaceMenu custom mask override";

    public bool IsAuthored
    {
        get => layer.IsAuthored;
        set => Update(layer with { IsAuthored = value });
    }

    public int Red
    {
        get => layer.Value.Red;
        set => UpdateValue(layer.Value with { Red = (byte)Math.Clamp(value, 0, 255) });
    }

    public int Green
    {
        get => layer.Value.Green;
        set => UpdateValue(layer.Value with { Green = (byte)Math.Clamp(value, 0, 255) });
    }

    public int Blue
    {
        get => layer.Value.Blue;
        set => UpdateValue(layer.Value with { Blue = (byte)Math.Clamp(value, 0, 255) });
    }

    public int Alpha
    {
        get => layer.Value.Alpha;
        set => UpdateValue(layer.Value with { Alpha = (byte)Math.Clamp(value, 0, 255) });
    }

    public int Coverage
    {
        get => checked((int)layer.Value.Coverage);
        set => UpdateValue(layer.Value with { Coverage = (uint)Math.Clamp(value, 0, 100) });
    }

    private void UpdateValue(SkyrimFaceTintLayer value) =>
        Update(layer with { Value = value, IsAuthored = true });

    private void Update(SkyrimFaceEditorTintLayer value)
    {
        if (value == layer) return;
        layer = value;
        Raise(nameof(IsAuthored));
        Raise(nameof(Red));
        Raise(nameof(Green));
        Raise(nameof(Blue));
        Raise(nameof(Alpha));
        Raise(nameof(Coverage));
        Raise(nameof(MaskPath));
        Raise(nameof(MaskAuthority));
        changed(value);
    }
}

public sealed class SkyrimFaceSculptEditorViewModel : NotifyViewModel
{
    private readonly SkyrimFaceEditorViewModel owner;
    private readonly string emptyState;
    private readonly string authorityNotice;

    internal SkyrimFaceSculptEditorViewModel(SkyrimFaceEditorViewModel owner)
    {
        this.owner = owner;
        emptyState = $"{owner.RaceEditorId} carries no RaceMenu sculpt blocks.";
        authorityNotice =
            $"Read-only {owner.Sex.ToString().ToLowerInvariant()} sculpt metadata · vertex, FaceGen, " +
            "and runtime authority remain with the admitted preset/build pipeline";
    }

    public ObservableCollection<SkyrimFaceSculptRowViewModel> Rows { get; } = [];
    public bool HasRows => Rows.Count > 0;
    public bool HasNoRows => Rows.Count == 0;
    public string EmptyState => emptyState;
    public string AuthorityNotice => authorityNotice;

    internal void Refresh()
    {
        Rows.Clear();
        foreach (RaceMenuSculptPart part in owner.CurrentDocument.SculptParts)
            Rows.Add(new SkyrimFaceSculptRowViewModel(part));
        Raise(nameof(HasRows));
        Raise(nameof(HasNoRows));
        Raise(nameof(EmptyState));
    }
}

public sealed class SkyrimFaceSculptRowViewModel
{
    internal SkyrimFaceSculptRowViewModel(RaceMenuSculptPart part)
    {
        Host = part.Host;
        VertexCount = part.VertexCount;
        DeltaCount = part.Vertices.Length;
        HasVertexCount = part.HasVertexCount;
        HasData = part.HasData;
    }

    public string Host { get; }
    public long VertexCount { get; }
    public int DeltaCount { get; }
    public bool HasVertexCount { get; }
    public bool HasData { get; }
}
