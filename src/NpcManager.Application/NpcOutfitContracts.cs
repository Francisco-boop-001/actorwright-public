namespace NpcManager.Application;

/// <summary>Explicitly specified default and sleeping outfit links; an empty optional clears the field.</summary>
public sealed record NpcOutfitPatch(
    OptionalFormReference DefaultOutfit,
    OptionalFormReference SleepingOutfit)
{
    public bool IsEmpty => !DefaultOutfit.IsSpecified && !SleepingOutfit.IsSpecified;
}

public sealed record NpcOutfitSnapshot(
    NpcManager.Domain.FormReference? DefaultOutfit,
    NpcManager.Domain.FormReference? SleepingOutfit);
