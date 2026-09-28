using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Desktop;

public sealed record ExistingNpcIdentityValues(
    EditorId? EditorId,
    string? FullName,
    string? ShortName,
    NpcArchetypeReferences Archetype);

public sealed record ExistingNpcIdentityPatches(
    EditorId? EditorId,
    NpcEditableNamesPatch? Names,
    NpcArchetypePatch? Archetype)
{
    public bool IsEmpty => EditorId is null &&
                           (Names is null || Names.IsEmpty) &&
                           (Archetype is null || Archetype.IsEmpty);
}

public sealed class ExistingNpcIdentityState
{
    private ExistingNpcIdentityValues? baseline;
    private ExistingNpcIdentityValues? current;
    private ExistingNpcIdentityPatches? initial;
    private FormChoiceSearchResult? catalog;

    public bool IsLoaded => baseline is not null && current is not null;
    public bool HasChanges => !BuildPatches().IsEmpty;
    public string Summary => current is null
        ? "Load the selected NPC before editing identity and archetype."
        : $"{Display(current.FullName, "Unnamed")} · " +
          $"{Display(current.Archetype.Race?.ToString(), "No race")} · " +
          $"{Display(current.Archetype.Class?.ToString(), "No class")}";

    public event EventHandler? Changed;

    public void InitializePatch(
        EditorId? editorId,
        NpcName? legacyName,
        NpcEditableNamesPatch? names,
        NpcArchetypePatch? archetype)
    {
        var normalizedNames = names;
        if (legacyName is { } name && names?.FullName.IsSpecified != true)
            normalizedNames = new NpcEditableNamesPatch(
                OptionalNpcText.Set(name.Value), names?.ShortName ?? default);
        initial = new ExistingNpcIdentityPatches(editorId, normalizedNames, archetype);
    }

    public void Load(NpcOverrideSourceSnapshot snapshot)
    {
        baseline = new ExistingNpcIdentityValues(
            snapshot.EditorId,
            snapshot.Name?.Value,
            snapshot.ShortName,
            snapshot.Archetype);
        current = Apply(baseline, initial);
        initial = null;
        catalog = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(bool discardInitialPatch = false)
    {
        if (discardInitialPatch) initial = null;
        if (!IsLoaded) return;
        baseline = null;
        current = null;
        catalog = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async ValueTask<ExistingNpcIdentityEditorViewModel> CreateEditorAsync(
        IFormChoiceService formChoices,
        WorkspacePath sourcePlugin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(formChoices);
        if (current is null)
            throw new InvalidOperationException(
                "Load a hash-bound NPC snapshot before editing identity and archetype.");
        if (catalog is { } cached)
            return new ExistingNpcIdentityEditorViewModel(current, cached);

        FormChoiceSearchResult search;
        try
        {
            var dataRoot = new WorkspacePath(
                Path.GetDirectoryName(sourcePlugin.Value) ?? sourcePlugin.Value);
            var pluginOrder = BethesdaNpcOverrideAdapter.ReadRequiredMasters(sourcePlugin);
            search = await formChoices.SearchAsync(
                new FormChoiceSearchRequest(
                    GameEdition.SkyrimSpecialEdition,
                    dataRoot,
                    [new RecordSignature("RACE"), new RecordSignature("VTYP"),
                        new RecordSignature("CLAS"), new RecordSignature("CSTY")],
                    null,
                    null,
                    true,
                    pluginOrder),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            search = new FormChoiceSearchResult(
                GameEdition.SkyrimSpecialEdition,
                true,
                [],
                [new Diagnostic("identity-catalog-unavailable", DiagnosticSeverity.Error,
                    exception.Message)]);
        }
        catalog = search;
        return new ExistingNpcIdentityEditorViewModel(current, search);
    }

    public void Commit(ExistingNpcIdentityEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (editor.AcceptedValues is not { } values)
            throw new InvalidOperationException("Only a validated identity editor can be committed.");
        current = values;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ExistingNpcIdentityPatches BuildPatches()
    {
        if (baseline is null || current is null)
            return new ExistingNpcIdentityPatches(null, null, null);
        var editorId = baseline.EditorId == current.EditorId ? null : current.EditorId;
        var full = TextPatch(baseline.FullName, current.FullName);
        var shortName = TextPatch(baseline.ShortName, current.ShortName);
        var names = full.IsSpecified || shortName.IsSpecified
            ? new NpcEditableNamesPatch(full, shortName)
            : null;
        var race = ReferencePatch(baseline.Archetype.Race, current.Archetype.Race);
        var voice = ReferencePatch(baseline.Archetype.Voice, current.Archetype.Voice);
        var npcClass = ReferencePatch(baseline.Archetype.Class, current.Archetype.Class);
        var combatStyle = ReferencePatch(
            baseline.Archetype.CombatStyle, current.Archetype.CombatStyle);
        var archetype = race.IsSpecified || voice.IsSpecified ||
                        npcClass.IsSpecified || combatStyle.IsSpecified
            ? new NpcArchetypePatch(race, voice, npcClass, combatStyle)
            : null;
        return new ExistingNpcIdentityPatches(editorId, names, archetype);
    }

    private static ExistingNpcIdentityValues Apply(
        ExistingNpcIdentityValues source,
        ExistingNpcIdentityPatches? patch)
    {
        if (patch is null || patch.IsEmpty) return source;
        var names = patch.Names;
        var archetype = patch.Archetype;
        return source with
        {
            EditorId = patch.EditorId ?? source.EditorId,
            FullName = names?.FullName.IsSpecified == true
                ? Normalize(names.FullName.Value)
                : source.FullName,
            ShortName = names?.ShortName.IsSpecified == true
                ? Normalize(names.ShortName.Value)
                : source.ShortName,
            Archetype = new NpcArchetypeReferences(
                Applied(source.Archetype.Race, archetype?.Race),
                Applied(source.Archetype.Voice, archetype?.Voice),
                Applied(source.Archetype.Class, archetype?.Class),
                Applied(source.Archetype.CombatStyle, archetype?.CombatStyle))
        };
    }

    private static FormReference? Applied(
        FormReference? source,
        OptionalFormReference? patch) =>
        patch?.IsSpecified == true ? patch.Value.Value : source;

    private static OptionalNpcText TextPatch(string? before, string? after)
    {
        if (string.Equals(Normalize(before), Normalize(after), StringComparison.Ordinal))
            return default;
        return string.IsNullOrEmpty(after)
            ? OptionalNpcText.Clear()
            : OptionalNpcText.Set(after);
    }

    private static OptionalFormReference ReferencePatch(
        FormReference? before,
        FormReference? after) => before == after
        ? default
        : after is { } value
            ? OptionalFormReference.Set(value)
            : OptionalFormReference.Clear();

    private static string? Normalize(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;

    private static string Display(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value;
}

public sealed record ExistingNpcArchetypeChoice(
    FormReference? Reference,
    string Label,
    bool IsAuthoritative,
    bool IsDeleted);

public enum ExistingNpcArchetypeField
{
    Race,
    Voice,
    Class,
    CombatStyle
}

public sealed class ExistingNpcIdentityEditorViewModel : NotifyViewModel
{
    private readonly ExistingNpcIdentityValues original;
    private readonly FormChoiceSearchResult search;

    public ExistingNpcIdentityEditorViewModel(
        ExistingNpcIdentityValues current,
        FormChoiceSearchResult search)
    {
        original = current;
        this.search = search;
        EditorId = current.EditorId?.Value ?? string.Empty;
        FullName = current.FullName ?? string.Empty;
        ShortName = current.ShortName ?? string.Empty;
        Races = Choices(search, "RACE", current.Archetype.Race, allowNull: false);
        Voices = Choices(search, "VTYP", current.Archetype.Voice, allowNull: true);
        Classes = Choices(search, "CLAS", current.Archetype.Class, allowNull: false);
        CombatStyles = Choices(search, "CSTY", current.Archetype.CombatStyle, allowNull: true);
        Race = Select(Races, current.Archetype.Race);
        Voice = Select(Voices, current.Archetype.Voice);
        NpcClass = Select(Classes, current.Archetype.Class);
        CombatStyle = Select(CombatStyles, current.Archetype.CombatStyle);
        CatalogMessage = search.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)
            ? "Some copied master files are unavailable. Existing references can be preserved, " +
              "but a new archetype choice requires a typed candidate from the complete copied Data root."
            : $"{search.Candidates.Count(item => !item.IsDeleted)} typed winning choices loaded from the source master closure.";
    }

    public ObservableCollection<ExistingNpcArchetypeChoice> Races { get; }
    public ObservableCollection<ExistingNpcArchetypeChoice> Voices { get; }
    public ObservableCollection<ExistingNpcArchetypeChoice> Classes { get; }
    public ObservableCollection<ExistingNpcArchetypeChoice> CombatStyles { get; }
    public string CatalogMessage { get; }
    public ExistingNpcIdentityValues? AcceptedValues { get; private set; }

    private string editorId = string.Empty;
    public string EditorId { get => editorId; set => Set(ref editorId, value); }

    private string fullName = string.Empty;
    public string FullName { get => fullName; set => Set(ref fullName, value); }

    private string shortName = string.Empty;
    public string ShortName { get => shortName; set => Set(ref shortName, value); }

    private ExistingNpcArchetypeChoice? race;
    public ExistingNpcArchetypeChoice? Race { get => race; set => Set(ref race, value); }

    private ExistingNpcArchetypeChoice? voice;
    public ExistingNpcArchetypeChoice? Voice { get => voice; set => Set(ref voice, value); }

    private ExistingNpcArchetypeChoice? npcClass;
    public ExistingNpcArchetypeChoice? NpcClass { get => npcClass; set => Set(ref npcClass, value); }

    private ExistingNpcArchetypeChoice? combatStyle;
    public ExistingNpcArchetypeChoice? CombatStyle { get => combatStyle; set => Set(ref combatStyle, value); }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    public bool TryAccept()
    {
        AcceptedValues = null;
        try
        {
            if (string.IsNullOrWhiteSpace(EditorId))
                throw new ArgumentException("EditorID is required.");
            var typedEditorId = new EditorId(EditorId);
            _ = OptionalNpcText.Set(FullName);
            _ = OptionalNpcText.Set(ShortName);
            if (Race?.Reference is null)
                throw new ArgumentException("Race is required for a Skyrim NPC.");
            if (NpcClass?.Reference is null)
                throw new ArgumentException("Class is required for a Skyrim NPC.");
            ValidateChoice(Race, original.Archetype.Race, "Race");
            ValidateChoice(Voice, original.Archetype.Voice, "Voice Type");
            ValidateChoice(NpcClass, original.Archetype.Class, "Class");
            ValidateChoice(CombatStyle, original.Archetype.CombatStyle, "Combat Style");
            AcceptedValues = new ExistingNpcIdentityValues(
                typedEditorId,
                Normalize(FullName),
                Normalize(ShortName),
                new NpcArchetypeReferences(
                    Race.Reference,
                    Voice?.Reference,
                    NpcClass.Reference,
                    CombatStyle?.Reference));
            ValidationMessage = string.Empty;
            return true;
        }
        catch (ArgumentException exception)
        {
            ValidationMessage = exception.Message;
            return false;
        }
    }

    public TypedFormIdPickerViewModel CreatePicker(ExistingNpcArchetypeField field)
    {
        var (signature, title, purpose, current, allowNull) = field switch
        {
            ExistingNpcArchetypeField.Race =>
                ("RACE", "Choose the NPC race",
                    "Select one non-deleted Race from the reviewed copied plugin closure.",
                    Race?.Reference, false),
            ExistingNpcArchetypeField.Voice =>
                ("VTYP", "Choose the NPC voice type",
                    "Select one Voice Type or the explicit NULL row.",
                    Voice?.Reference, true),
            ExistingNpcArchetypeField.Class =>
                ("CLAS", "Choose the NPC class",
                    "Select one non-deleted Class from the reviewed copied plugin closure.",
                    NpcClass?.Reference, false),
            ExistingNpcArchetypeField.CombatStyle =>
                ("CSTY", "Choose the NPC combat style",
                    "Select one Combat Style or the explicit NULL row.",
                    CombatStyle?.Reference, true),
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
        return new TypedFormIdPickerViewModel(
            search,
            new TypedFormIdPickerOptions(
                title,
                purpose,
                [new RecordSignature(signature)],
                current,
                allowNull));
    }

    public void ApplyPicker(
        ExistingNpcArchetypeField field,
        TypedFormIdPickerViewModel picker)
    {
        ArgumentNullException.ThrowIfNull(picker);
        if (!picker.IsAccepted)
            throw new InvalidOperationException(
                "Only an accepted typed record picker result can be applied.");
        var choices = field switch
        {
            ExistingNpcArchetypeField.Race => Races,
            ExistingNpcArchetypeField.Voice => Voices,
            ExistingNpcArchetypeField.Class => Classes,
            ExistingNpcArchetypeField.CombatStyle => CombatStyles,
            _ => throw new ArgumentOutOfRangeException(nameof(field), field, null)
        };
        var choice = choices.SingleOrDefault(item =>
            item.Reference == picker.AcceptedReference);
        if (choice is null)
            throw new InvalidOperationException(
                "The accepted typed reference is not part of this editor's immutable catalog.");
        switch (field)
        {
            case ExistingNpcArchetypeField.Race:
                Race = choice;
                break;
            case ExistingNpcArchetypeField.Voice:
                Voice = choice;
                break;
            case ExistingNpcArchetypeField.Class:
                NpcClass = choice;
                break;
            case ExistingNpcArchetypeField.CombatStyle:
                CombatStyle = choice;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(field), field, null);
        }
    }

    private static void ValidateChoice(
        ExistingNpcArchetypeChoice? choice,
        FormReference? originalReference,
        string field)
    {
        var reference = choice?.Reference;
        if (reference == originalReference) return;
        if (choice is null || !choice.IsAuthoritative || choice.IsDeleted)
            throw new ArgumentException(
                $"{field} must be selected from a non-deleted typed record in the copied source closure.");
    }

    private static ObservableCollection<ExistingNpcArchetypeChoice> Choices(
        FormChoiceSearchResult search,
        string signature,
        FormReference? current,
        bool allowNull)
    {
        var values = search.Candidates
            .Where(item => item.Signature.Value == signature && !item.IsDeleted)
            .Select(item => new ExistingNpcArchetypeChoice(
                new FormReference(item.Provenance.SourcePlugin, item.FormId),
                Label(item),
                true,
                false))
            .ToList();
        if (allowNull)
            values.Insert(0, new ExistingNpcArchetypeChoice(null, "None", true, false));
        if (current is not null && values.All(item => item.Reference != current))
            values.Insert(allowNull ? 1 : 0, new ExistingNpcArchetypeChoice(
                current,
                $"Current · {current}",
                false,
                false));
        return new ObservableCollection<ExistingNpcArchetypeChoice>(values);
    }

    private static ExistingNpcArchetypeChoice? Select(
        IEnumerable<ExistingNpcArchetypeChoice> choices,
        FormReference? reference) => choices.FirstOrDefault(item => item.Reference == reference);

    private static string Label(FormChoiceCandidate item)
    {
        var name = item.Name ?? item.EditorId ?? "Unnamed record";
        return $"{name} · {item.Provenance.SourcePlugin}|{item.FormId}";
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrEmpty(value) ? null : value;
}
