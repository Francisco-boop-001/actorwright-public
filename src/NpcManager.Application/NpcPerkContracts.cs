using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>An NPC PERK FormID and unsigned 8-bit rank.</summary>
public readonly record struct NpcPerkEntry(FormReference Perk, byte Rank);

/// <summary>Deterministic perk-list operations. Replacement and incremental operations are mutually exclusive.</summary>
public sealed record NpcPerkPatch(
    ImmutableArray<NpcPerkEntry>? Replace,
    ImmutableArray<NpcPerkEntry> Add,
    ImmutableArray<NpcPerkEntry> Update,
    ImmutableArray<FormReference> Remove)
{
    public bool IsEmpty => Replace is null && Add.IsDefaultOrEmpty && Update.IsDefaultOrEmpty && Remove.IsDefaultOrEmpty;
}

public sealed record NpcPerkSnapshot(ImmutableArray<NpcPerkEntry> Perks);
