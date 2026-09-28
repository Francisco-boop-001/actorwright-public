using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public enum ExistingNpcOutfitEditMode
{
    Keep,
    Set,
    Clear
}

public sealed record ExistingNpcCollectionValues(
    ImmutableArray<FormReference> Keywords,
    ImmutableArray<NpcFactionEntry> Factions,
    ImmutableArray<NpcInventoryEntry> Inventory,
    FormReference? DefaultOutfit,
    FormReference? SleepingOutfit,
    ImmutableArray<NpcPerkEntry> Perks,
    ImmutableArray<FormReference> ActorEffects);

public sealed record ExistingNpcCollectionPatchSet(
    NpcKeywordPatch? Keywords,
    NpcFactionPatch? Factions,
    NpcInventoryPatch? Inventory,
    NpcOutfitPatch? Outfits,
    NpcPerkPatch? Perks,
    NpcActorEffectPatch? ActorEffects)
{
    public bool IsEmpty => Keywords is null && Factions is null && Inventory is null &&
                           Outfits is null && Perks is null && ActorEffects is null;
}

/// <summary>
/// Holds only the committed, typed collection state for the existing-NPC
/// editor. Modal sessions receive copies; cancellation therefore cannot mutate
/// the reviewed request.
/// </summary>
public sealed class ExistingNpcCollectionState
{
    private ExistingNpcCollectionValues? baseline;
    private ExistingNpcCollectionValues? current;

    public bool IsLoaded => baseline is not null && current is not null;

    public bool HasChanges => BuildPatches() is { IsEmpty: false };

    public string Summary => current is null
        ? "Load the selected NPC before editing its lists."
        : $"{current.Keywords.Length} keywords · {current.Factions.Length} factions · " +
          $"{current.Inventory.Length} inventory rows · {current.Perks.Length} perks · " +
          $"{current.ActorEffects.Length} actor effects";

    public event EventHandler? Changed;

    public void Load(NpcOverrideSourceSnapshot snapshot)
    {
        baseline = From(snapshot);
        current = baseline;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear()
    {
        if (!IsLoaded) return;
        baseline = null;
        current = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ExistingNpcCollectionEditorViewModel CreateEditor()
    {
        if (baseline is null || current is null)
            throw new InvalidOperationException("Load a hash-bound NPC snapshot before editing collections.");
        return new ExistingNpcCollectionEditorViewModel(baseline, current);
    }

    public void Commit(ExistingNpcCollectionEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (editor.AcceptedValues is not { } values)
            throw new InvalidOperationException("Only a validated accepted editor session can be committed.");
        current = values;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ExistingNpcCollectionPatchSet BuildPatches()
    {
        if (baseline is null || current is null)
            return new ExistingNpcCollectionPatchSet(null, null, null, null, null, null);

        var keywords = baseline.Keywords.SequenceEqual(current.Keywords)
            ? null
            : new NpcKeywordPatch(new NpcKeywordListPatch(current.Keywords, [], []));
        var factions = baseline.Factions.SequenceEqual(current.Factions)
            ? null
            : new NpcFactionPatch(current.Factions, [], [], []);
        var inventory = baseline.Inventory.SequenceEqual(current.Inventory)
            ? null
            : new NpcInventoryPatch(current.Inventory, [], [], []);
        var outfits = baseline.DefaultOutfit == current.DefaultOutfit &&
                      baseline.SleepingOutfit == current.SleepingOutfit
            ? null
            : new NpcOutfitPatch(
                baseline.DefaultOutfit == current.DefaultOutfit
                    ? default
                    : ToOptional(current.DefaultOutfit),
                baseline.SleepingOutfit == current.SleepingOutfit
                    ? default
                    : ToOptional(current.SleepingOutfit));
        var perks = baseline.Perks.SequenceEqual(current.Perks)
            ? null
            : new NpcPerkPatch(current.Perks, [], [], []);
        var actorEffects = baseline.ActorEffects.SequenceEqual(current.ActorEffects)
            ? null
            : new NpcActorEffectPatch(current.ActorEffects, [], []);
        return new ExistingNpcCollectionPatchSet(
            keywords, factions, inventory, outfits, perks, actorEffects);
    }

    private static ExistingNpcCollectionValues From(NpcOverrideSourceSnapshot snapshot) =>
        new(
            snapshot.Keywords.Keywords,
            snapshot.Factions.Factions,
            snapshot.Inventory.Items,
            snapshot.Outfits.DefaultOutfit,
            snapshot.Outfits.SleepingOutfit,
            snapshot.Perks.Perks,
            snapshot.ActorEffects.ActorEffects);

    private static OptionalFormReference ToOptional(FormReference? reference) =>
        reference is { } value
            ? OptionalFormReference.Set(value)
            : OptionalFormReference.Clear();
}

public sealed class ExistingNpcCollectionEditorViewModel : NotifyViewModel
{
    private readonly ExistingNpcCollectionValues baseline;

    public ExistingNpcCollectionEditorViewModel(
        ExistingNpcCollectionValues baseline,
        ExistingNpcCollectionValues current)
    {
        this.baseline = baseline;
        Keywords = new ObservableCollection<NpcReferenceRowViewModel>(
            current.Keywords.Select(reference => new NpcReferenceRowViewModel(reference.ToString())));
        Factions = new ObservableCollection<NpcReferenceValueRowViewModel>(
            current.Factions.Select(entry => new NpcReferenceValueRowViewModel(
                entry.Faction.ToString(), entry.Rank.ToString(CultureInfo.InvariantCulture))));
        Inventory = new ObservableCollection<NpcReferenceValueRowViewModel>(
            current.Inventory.Select(entry => new NpcReferenceValueRowViewModel(
                entry.Item.ToString(), entry.Count.ToString(CultureInfo.InvariantCulture))));
        Perks = new ObservableCollection<NpcReferenceValueRowViewModel>(
            current.Perks.Select(entry => new NpcReferenceValueRowViewModel(
                entry.Perk.ToString(), entry.Rank.ToString(CultureInfo.InvariantCulture))));
        ActorEffects = new ObservableCollection<NpcReferenceRowViewModel>(
            current.ActorEffects.Select(reference => new NpcReferenceRowViewModel(reference.ToString())));

        DefaultOutfitMode = ResolveOutfitMode(baseline.DefaultOutfit, current.DefaultOutfit);
        DefaultOutfitReference = current.DefaultOutfit?.ToString() ?? string.Empty;
        SleepingOutfitMode = ResolveOutfitMode(baseline.SleepingOutfit, current.SleepingOutfit);
        SleepingOutfitReference = current.SleepingOutfit?.ToString() ?? string.Empty;

        AddKeywordCommand = AddReferenceCommand(Keywords);
        RemoveKeywordCommand = RemoveReferenceCommand(Keywords);
        AddFactionCommand = AddValueCommand(Factions, "0");
        RemoveFactionCommand = RemoveValueCommand(Factions);
        AddInventoryCommand = AddValueCommand(Inventory, "1");
        RemoveInventoryCommand = RemoveValueCommand(Inventory);
        AddPerkCommand = AddValueCommand(Perks, "1");
        RemovePerkCommand = RemoveValueCommand(Perks);
        AddActorEffectCommand = AddReferenceCommand(ActorEffects);
        RemoveActorEffectCommand = RemoveReferenceCommand(ActorEffects);
    }

    public ObservableCollection<NpcReferenceRowViewModel> Keywords { get; }
    public ObservableCollection<NpcReferenceValueRowViewModel> Factions { get; }
    public ObservableCollection<NpcReferenceValueRowViewModel> Inventory { get; }
    public ObservableCollection<NpcReferenceValueRowViewModel> Perks { get; }
    public ObservableCollection<NpcReferenceRowViewModel> ActorEffects { get; }

    public IReadOnlyList<ExistingNpcOutfitEditMode> OutfitModes { get; } =
        Enum.GetValues<ExistingNpcOutfitEditMode>();

    public ICommand AddKeywordCommand { get; }
    public ICommand RemoveKeywordCommand { get; }
    public ICommand AddFactionCommand { get; }
    public ICommand RemoveFactionCommand { get; }
    public ICommand AddInventoryCommand { get; }
    public ICommand RemoveInventoryCommand { get; }
    public ICommand AddPerkCommand { get; }
    public ICommand RemovePerkCommand { get; }
    public ICommand AddActorEffectCommand { get; }
    public ICommand RemoveActorEffectCommand { get; }

    private ExistingNpcOutfitEditMode defaultOutfitMode;
    public ExistingNpcOutfitEditMode DefaultOutfitMode
    {
        get => defaultOutfitMode;
        set => Set(ref defaultOutfitMode, value);
    }

    private string defaultOutfitReference = string.Empty;
    public string DefaultOutfitReference
    {
        get => defaultOutfitReference;
        set => Set(ref defaultOutfitReference, value);
    }

    private ExistingNpcOutfitEditMode sleepingOutfitMode;
    public ExistingNpcOutfitEditMode SleepingOutfitMode
    {
        get => sleepingOutfitMode;
        set => Set(ref sleepingOutfitMode, value);
    }

    private string sleepingOutfitReference = string.Empty;
    public string SleepingOutfitReference
    {
        get => sleepingOutfitReference;
        set => Set(ref sleepingOutfitReference, value);
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    public ExistingNpcCollectionValues? AcceptedValues { get; private set; }

    public bool TryAccept()
    {
        AcceptedValues = null;
        if (!TryReferences(Keywords, "Keywords", out var keywords, out var error) ||
            !TryFactions(out var factions, out error) ||
            !TryInventory(out var inventory, out error) ||
            !TryPerks(out var perks, out error) ||
            !TryReferences(ActorEffects, "Actor effects", out var actorEffects, out error) ||
            !TryOutfit(DefaultOutfitMode, DefaultOutfitReference,
                baseline.DefaultOutfit, "Default outfit", out var defaultOutfit, out error) ||
            !TryOutfit(SleepingOutfitMode, SleepingOutfitReference,
                baseline.SleepingOutfit, "Sleeping outfit", out var sleepingOutfit, out error))
        {
            ValidationMessage = error;
            return false;
        }

        ValidationMessage = string.Empty;
        AcceptedValues = new ExistingNpcCollectionValues(
            keywords, factions, inventory, defaultOutfit, sleepingOutfit, perks, actorEffects);
        return true;
    }

    private bool TryFactions(
        out ImmutableArray<NpcFactionEntry> values,
        out string error)
    {
        var builder = ImmutableArray.CreateBuilder<NpcFactionEntry>();
        var seen = new HashSet<FormReference>();
        foreach (var row in Factions)
        {
            if (!TryReference(row.Reference, "Faction", out var reference, out error) ||
                !sbyte.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank))
            {
                if (string.IsNullOrEmpty(error)) error = "Faction rank must be between -128 and 127.";
                values = [];
                return false;
            }
            if (!seen.Add(reference))
            {
                values = [];
                error = $"Faction {reference} occurs more than once.";
                return false;
            }
            builder.Add(new NpcFactionEntry(reference, rank));
        }
        values = builder.ToImmutable();
        error = string.Empty;
        return true;
    }

    private bool TryInventory(
        out ImmutableArray<NpcInventoryEntry> values,
        out string error)
    {
        var builder = ImmutableArray.CreateBuilder<NpcInventoryEntry>();
        var seen = new HashSet<FormReference>();
        foreach (var row in Inventory)
        {
            if (!TryReference(row.Reference, "Inventory item", out var reference, out error) ||
                !int.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var count))
            {
                if (string.IsNullOrEmpty(error)) error = "Inventory count must be a signed 32-bit integer.";
                values = [];
                return false;
            }
            if (!seen.Add(reference))
            {
                values = [];
                error = $"Inventory item {reference} occurs more than once; combine its count first.";
                return false;
            }
            builder.Add(new NpcInventoryEntry(reference, count));
        }
        values = builder.ToImmutable();
        error = string.Empty;
        return true;
    }

    private bool TryPerks(
        out ImmutableArray<NpcPerkEntry> values,
        out string error)
    {
        var builder = ImmutableArray.CreateBuilder<NpcPerkEntry>();
        var seen = new HashSet<FormReference>();
        foreach (var row in Perks)
        {
            if (!TryReference(row.Reference, "Perk", out var reference, out error) ||
                !byte.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rank))
            {
                if (string.IsNullOrEmpty(error)) error = "Perk rank must be between 0 and 255.";
                values = [];
                return false;
            }
            if (!seen.Add(reference))
            {
                values = [];
                error = $"Perk {reference} occurs more than once.";
                return false;
            }
            builder.Add(new NpcPerkEntry(reference, rank));
        }
        values = builder.ToImmutable();
        error = string.Empty;
        return true;
    }

    private static bool TryReferences(
        IEnumerable<NpcReferenceRowViewModel> rows,
        string label,
        out ImmutableArray<FormReference> values,
        out string error)
    {
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        var seen = new HashSet<FormReference>();
        foreach (var row in rows)
        {
            if (!TryReference(row.Reference, label, out var reference, out error))
            {
                values = [];
                return false;
            }
            if (!seen.Add(reference))
            {
                values = [];
                error = $"{label} contains duplicate reference {reference}.";
                return false;
            }
            builder.Add(reference);
        }
        values = builder.ToImmutable();
        error = string.Empty;
        return true;
    }

    private static bool TryOutfit(
        ExistingNpcOutfitEditMode mode,
        string text,
        FormReference? baselineValue,
        string label,
        out FormReference? value,
        out string error)
    {
        switch (mode)
        {
            case ExistingNpcOutfitEditMode.Keep:
                value = baselineValue;
                error = string.Empty;
                return true;
            case ExistingNpcOutfitEditMode.Clear:
                value = null;
                error = string.Empty;
                return true;
            case ExistingNpcOutfitEditMode.Set:
                if (TryReference(text, label, out var reference, out error))
                {
                    value = reference;
                    return true;
                }
                value = null;
                return false;
            default:
                value = null;
                error = $"{label} mode is unsupported.";
                return false;
        }
    }

    private static bool TryReference(
        string text,
        string label,
        out FormReference reference,
        out string error)
    {
        if (FormReference.TryParse(text.Trim(), out reference) && reference.FormId.Value != 0)
        {
            error = string.Empty;
            return true;
        }
        error = $"{label} must use Plugin.esp|0xXXXXXXXX with a nonzero FormID.";
        return false;
    }

    private static ExistingNpcOutfitEditMode ResolveOutfitMode(
        FormReference? baselineValue,
        FormReference? currentValue) =>
        baselineValue == currentValue
            ? ExistingNpcOutfitEditMode.Keep
            : currentValue is null
                ? ExistingNpcOutfitEditMode.Clear
                : ExistingNpcOutfitEditMode.Set;

    private static RelayCommand AddReferenceCommand(
        ObservableCollection<NpcReferenceRowViewModel> rows) =>
        new RelayCommand(_ => rows.Add(new NpcReferenceRowViewModel(string.Empty)));

    private static RelayCommand RemoveReferenceCommand(
        ObservableCollection<NpcReferenceRowViewModel> rows) =>
        new RelayCommand(row =>
        {
            if (row is NpcReferenceRowViewModel value) rows.Remove(value);
        });

    private static RelayCommand AddValueCommand(
        ObservableCollection<NpcReferenceValueRowViewModel> rows,
        string defaultValue) =>
        new RelayCommand(_ => rows.Add(new NpcReferenceValueRowViewModel(string.Empty, defaultValue)));

    private static RelayCommand RemoveValueCommand(
        ObservableCollection<NpcReferenceValueRowViewModel> rows) =>
        new RelayCommand(row =>
        {
            if (row is NpcReferenceValueRowViewModel value) rows.Remove(value);
        });
}

public sealed class NpcReferenceRowViewModel(string reference) : NotifyViewModel
{
    private string reference = reference;
    public string Reference { get => reference; set => Set(ref reference, value); }
}

public sealed class NpcReferenceValueRowViewModel(string reference, string value) : NotifyViewModel
{
    private string reference = reference;
    private string value = value;
    public string Reference { get => reference; set => Set(ref reference, value); }
    public string Value { get => value; set => Set(ref this.value, value); }
}

public abstract class NotifyViewModel : INotifyPropertyChanged
{
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(propertyName);
        return true;
    }

    protected void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public event PropertyChangedEventHandler? PropertyChanged;
}

internal sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => execute(parameter);
}
