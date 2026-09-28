using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>A faction membership and its signed SNAM rank.</summary>
public readonly record struct NpcFactionEntry(FormReference Faction, sbyte Rank);

/// <summary>Deterministic faction-list operations. Replace is mutually exclusive with incremental operations.</summary>
public sealed record NpcFactionPatch(
    ImmutableArray<NpcFactionEntry>? Replace,
    ImmutableArray<NpcFactionEntry> Add,
    ImmutableArray<NpcFactionEntry> Update,
    ImmutableArray<FormReference> Remove)
{
    public bool IsEmpty => Replace is null && Add.IsDefaultOrEmpty && Update.IsDefaultOrEmpty && Remove.IsDefaultOrEmpty;
}

public sealed record NpcFactionSnapshot(ImmutableArray<NpcFactionEntry> Factions);
