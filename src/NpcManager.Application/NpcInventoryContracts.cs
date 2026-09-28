using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>An inventory item and its signed 32-bit CNTO count.</summary>
public readonly record struct NpcInventoryEntry(FormReference Item, int Count);

/// <summary>Deterministic inventory-list operations. Replace and incremental operations are mutually exclusive.</summary>
public sealed record NpcInventoryPatch(
    ImmutableArray<NpcInventoryEntry>? Replace,
    ImmutableArray<NpcInventoryEntry> Add,
    ImmutableArray<NpcInventoryEntry> Update,
    ImmutableArray<FormReference> Remove)
{
    public bool IsEmpty => Replace is null && Add.IsDefaultOrEmpty && Update.IsDefaultOrEmpty && Remove.IsDefaultOrEmpty;
}

public sealed record NpcInventorySnapshot(ImmutableArray<NpcInventoryEntry> Items);
