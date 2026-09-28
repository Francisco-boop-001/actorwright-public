using System.Collections.Immutable;
using System.Collections.ObjectModel;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimFacePartsEditorViewModel : NotifyViewModel
{
    private readonly SkyrimFaceEditorViewModel owner;
    private bool refreshing;

    internal SkyrimFacePartsEditorViewModel(SkyrimFaceEditorViewModel owner)
    {
        this.owner = owner;
        TypeOptions = new ObservableCollection<SkyrimHeadPartTypeOption>(
            Enum.GetValues<NpcHeadPartType>()
                .Select(value => new SkyrimHeadPartTypeOption(value, value.ToWireName())));
        selectedAddType = TypeOptions.First(item => item.Value == NpcHeadPartType.Hair);
    }

    public ObservableCollection<SkyrimFaceHeadPartRowViewModel> HeadParts { get; } = [];
    public ObservableCollection<SkyrimHeadPartTypeOption> TypeOptions { get; }
    public string Summary =>
        $"{owner.CurrentDocument.Parts.OrderedHeadParts.Length} NPC-owned head part(s) · " +
        $"{HeadParts.Count(item => item.IsReadOnly)} derived row(s) · " +
        $"hair {HairColorText} · head texture {HeadTextureText}";
    public bool CanRemove => SelectedHeadPart is { IsReadOnly: false };

    private SkyrimFaceHeadPartRowViewModel? selectedHeadPart;
    public SkyrimFaceHeadPartRowViewModel? SelectedHeadPart
    {
        get => selectedHeadPart;
        set
        {
            if (!Set(ref selectedHeadPart, value)) return;
            Raise(nameof(CanRemove));
        }
    }

    private SkyrimHeadPartTypeOption selectedAddType;
    public SkyrimHeadPartTypeOption SelectedAddType
    {
        get => selectedAddType;
        set => Set(ref selectedAddType, value);
    }

    private string hairColorText = "Preserve source";
    public string HairColorText
    {
        get => hairColorText;
        private set => Set(ref hairColorText, value);
    }

    private string headTextureText = "Race / head-part default";
    public string HeadTextureText
    {
        get => headTextureText;
        private set => Set(ref headTextureText, value);
    }

    private bool isCharGenFacePreset;
    public bool IsCharGenFacePreset
    {
        get => isCharGenFacePreset;
        set
        {
            if (!Set(ref isCharGenFacePreset, value) || refreshing) return;
            owner.Replace(
                SkyrimFaceEditorDocumentRules.SetCharGenFlag(owner.CurrentDocument, value),
                SkyrimFaceEditorSection.FaceParts,
                value ? "CharGen face-preset flag enabled." : "CharGen face-preset flag disabled.");
        }
    }

    public void RemoveSelected()
    {
        if (SelectedHeadPart is not { IsReadOnly: false }) return;
        FormReference selected = SelectedHeadPart.Reference;
        owner.Replace(
            SkyrimFaceEditorDocumentRules.RemoveHeadPart(
                owner.CurrentDocument,
                selected,
                owner.ProvenOrphanedMisc(selected)),
            SkyrimFaceEditorSection.FaceParts,
            $"Removed {selected} and only its proven orphaned standalone Misc children.");
    }

    internal void Refresh()
    {
        refreshing = true;
        try
        {
            (FormReference Reference, SkyrimFaceHeadPartRowKind Kind)? selected =
                SelectedHeadPart is null
                    ? null
                    : (SelectedHeadPart.Reference, SelectedHeadPart.Kind);
            HeadParts.Clear();
            ImmutableArray<NpcHeadPartSelection> owned =
                owner.CurrentDocument.Parts.OrderedHeadParts;
            var overriddenTypes = owned
                .Where(item => item.Type != NpcHeadPartType.Misc)
                .Select(item => item.Type)
                .ToHashSet();
            ImmutableArray<NpcHeadPartSelection> defaults = owner.RaceDefaultHeadParts
                .Where(item => !overriddenTypes.Contains(item.Type))
                .ToImmutableArray();
            ImmutableArray<NpcHeadPartSelection> parents = owned
                .Where(item => item.Type != NpcHeadPartType.Misc)
                .Concat(defaults)
                .ToImmutableArray();
            var claimedExtras = parents
                .Select(item => owner.FindHeadPartCandidate(item.Reference))
                .Where(item => item is not null)
                .Cast<SkyrimHeadPartChoiceCandidate>()
                .SelectMany(item => item.ExtraParts)
                .ToImmutableArray();

            foreach (NpcHeadPartSelection item in owned)
            {
                if (item.Type == NpcHeadPartType.Misc && claimedExtras.Any(extra =>
                        SameReference(extra, item.Reference))) continue;
                AddParentAndExtras(item, SkyrimFaceHeadPartRowKind.NpcOwned);
            }
            foreach (NpcHeadPartSelection item in defaults)
                AddParentAndExtras(item, SkyrimFaceHeadPartRowKind.RaceDefault);
            SelectedHeadPart = selected is null
                ? null
                : HeadParts.FirstOrDefault(item =>
                    SameReference(item.Reference, selected.Value.Reference) &&
                    item.Kind == selected.Value.Kind);
            OptionalFormReference hair = owner.CurrentDocument.Parts.HairColor;
            HairColorText = !hair.IsSpecified
                ? "Preserve source"
                : hair.Value?.ToString() ?? "None / record default";
            HeadTextureText = owner.CurrentDocument.Parts.HeadTexture?.ToString() ??
                              "Race / head-part default";
            IsCharGenFacePreset = owner.CurrentDocument.Parts.IsCharGenFacePreset;
            Raise(nameof(Summary));
            Raise(nameof(CanRemove));
        }
        finally
        {
            refreshing = false;
        }

        void AddParentAndExtras(
            NpcHeadPartSelection selection,
            SkyrimFaceHeadPartRowKind kind)
        {
            HeadParts.Add(new SkyrimFaceHeadPartRowViewModel(selection, kind));
            if (selection.Type == NpcHeadPartType.Misc) return;
            SkyrimHeadPartChoiceCandidate? candidate =
                owner.FindHeadPartCandidate(selection.Reference);
            if (candidate is null) return;
            foreach (FormReference extra in candidate.ExtraParts)
            {
                HeadParts.Add(new SkyrimFaceHeadPartRowViewModel(
                    new NpcHeadPartSelection(extra, NpcHeadPartType.Misc),
                    SkyrimFaceHeadPartRowKind.HnamExtra));
            }
        }
    }

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(
            left.Plugin.Value, right.Plugin.Value, StringComparison.OrdinalIgnoreCase);
}

public sealed record SkyrimHeadPartTypeOption(NpcHeadPartType Value, string Label);

public enum SkyrimFaceHeadPartRowKind
{
    NpcOwned,
    RaceDefault,
    HnamExtra
}

public sealed class SkyrimFaceHeadPartRowViewModel
{
    public SkyrimFaceHeadPartRowViewModel(
        NpcHeadPartSelection selection,
        SkyrimFaceHeadPartRowKind kind)
    {
        Reference = selection.Reference;
        Type = selection.Type;
        RawPnamType = selection.RawPnamType;
        Kind = kind;
    }

    public FormReference Reference { get; }
    public NpcHeadPartType Type { get; }
    public uint? RawPnamType { get; }
    public SkyrimFaceHeadPartRowKind Kind { get; }
    public bool IsReadOnly => Kind != SkyrimFaceHeadPartRowKind.NpcOwned;
    public string TypeText => Kind switch
    {
        SkyrimFaceHeadPartRowKind.NpcOwned => Type.ToWireName(RawPnamType),
        SkyrimFaceHeadPartRowKind.RaceDefault => $"{Type.ToWireName(RawPnamType)} (RACE)",
        SkyrimFaceHeadPartRowKind.HnamExtra => "    HNAM extra",
        _ => Type.ToWireName(RawPnamType)
    };
    public string PluginText => Reference.Plugin.Value;
    public string FormIdText => Reference.FormId.ToString();
}
