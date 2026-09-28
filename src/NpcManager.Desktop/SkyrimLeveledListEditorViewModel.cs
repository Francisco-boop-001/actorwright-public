using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed class SkyrimLeveledListEditorViewModel : NotifyViewModel
{
    private readonly ImmutableArray<EditorId> existingEditorIds;
    private readonly string authorityNotice =
        "This creates an in-memory LVLI header only. No FormID is allocated and no plugin, preview, runtime, or visual authority is produced.";

    public SkyrimLeveledListEditorViewModel(
        ImmutableArray<EditorId> existingEditorIds)
    {
        this.existingEditorIds = existingEditorIds;
        RefreshValidation();
    }

    public SkyrimLeveledListEditorDocument? AcceptedDocument { get; private set; }
    public bool IsAccepted { get; private set; }
    public string EditorIdPreview => SkyrimLeveledListEditorRules.EditorIdPrefix + NameSuffix.Trim();
    public string PackedFlags => $"LVLF 0x{CurrentFlags:X2}";
    public string MaxCountMeaning => MaxCount == 0
        ? "0 means unlimited."
        : $"Limit list evaluation to {MaxCount}.";
    public string AuthorityNotice => authorityNotice;

    private string nameSuffix = string.Empty;
    public string NameSuffix
    {
        get => nameSuffix;
        set
        {
            if (!Set(ref nameSuffix, value ?? string.Empty)) return;
            Raise(nameof(EditorIdPreview));
            RefreshValidation();
        }
    }

    private int chanceNone;
    public int ChanceNone
    {
        get => chanceNone;
        set
        {
            if (!Set(ref chanceNone, value)) return;
            RefreshValidation();
        }
    }

    private int maxCount;
    public int MaxCount
    {
        get => maxCount;
        set
        {
            if (!Set(ref maxCount, value)) return;
            Raise(nameof(MaxCountMeaning));
            RefreshValidation();
        }
    }

    private bool calculateAllLevels;
    public bool CalculateAllLevels
    {
        get => calculateAllLevels;
        set
        {
            if (!Set(ref calculateAllLevels, value)) return;
            Raise(nameof(PackedFlags));
            RefreshValidation();
        }
    }

    private bool calculateEachInCount;
    public bool CalculateEachInCount
    {
        get => calculateEachInCount;
        set
        {
            if (!Set(ref calculateEachInCount, value)) return;
            Raise(nameof(PackedFlags));
            RefreshValidation();
        }
    }

    private bool useAll;
    public bool UseAll
    {
        get => useAll;
        set
        {
            if (!Set(ref useAll, value)) return;
            Raise(nameof(PackedFlags));
            RefreshValidation();
        }
    }

    private bool canAccept;
    public bool CanAccept
    {
        get => canAccept;
        private set => Set(ref canAccept, value);
    }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    private string statusMessage = string.Empty;
    public string StatusMessage
    {
        get => statusMessage;
        private set => Set(ref statusMessage, value);
    }

    public bool TryAccept()
    {
        SkyrimLeveledListEditorResult result = Evaluate();
        if (!result.Accepted || result.Document is null)
        {
            ClearAcceptance();
            ValidationMessage = FirstError(result.Diagnostics);
            return false;
        }

        AcceptedDocument = result.Document;
        IsAccepted = true;
        ValidationMessage = string.Empty;
        StatusMessage =
            "The immutable LVLI header is ready for the outer outfit transaction.";
        Raise(nameof(AcceptedDocument));
        Raise(nameof(IsAccepted));
        return true;
    }

    public void Cancel()
    {
        SkyrimLeveledListEditorRules.Cancel();
        ClearAcceptance();
        ValidationMessage = string.Empty;
        StatusMessage = string.Empty;
    }

    private byte CurrentFlags => (byte)(
        (CalculateAllLevels ? 0x01 : 0) |
        (CalculateEachInCount ? 0x02 : 0) |
        (UseAll ? 0x04 : 0));

    private SkyrimLeveledListEditorResult Evaluate() =>
        SkyrimLeveledListEditorRules.Create(
            GameEdition.SkyrimSpecialEdition,
            NameSuffix,
            ChanceNone,
            MaxCount,
            CalculateAllLevels,
            CalculateEachInCount,
            UseAll,
            existingEditorIds);

    private void RefreshValidation()
    {
        ClearAcceptance();
        SkyrimLeveledListEditorResult result = Evaluate();
        CanAccept = result.Accepted;
        ValidationMessage = result.Accepted
            ? string.Empty
            : FirstError(result.Diagnostics);
        StatusMessage = string.Empty;
    }

    private void ClearAcceptance()
    {
        AcceptedDocument = null;
        IsAccepted = false;
        Raise(nameof(AcceptedDocument));
        Raise(nameof(IsAccepted));
    }

    private static string FirstError(ImmutableArray<Diagnostic> diagnostics) =>
        diagnostics.FirstOrDefault(item => item.Severity == DiagnosticSeverity.Error)?.Message ??
        "The leveled-list document was refused.";
}
