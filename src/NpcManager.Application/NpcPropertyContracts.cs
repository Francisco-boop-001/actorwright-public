using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>One Fallout 4 PRPS entry: an AVIF actor-value reference and IEEE-754 single value.</summary>
public readonly record struct NpcPropertyEntry(FormReference ActorValue, float Value);

/// <summary>Deterministic PRPS list operations. Properties are Fallout 4-only in the pinned upstream editor.</summary>
public sealed record NpcPropertyPatch(
    ImmutableArray<NpcPropertyEntry>? Replace,
    ImmutableArray<NpcPropertyEntry> Add,
    ImmutableArray<NpcPropertyEntry> Update,
    ImmutableArray<FormReference> Remove)
{
    public bool IsEmpty => Replace is null && Add.IsDefaultOrEmpty && Update.IsDefaultOrEmpty && Remove.IsDefaultOrEmpty;
}

public sealed record NpcPropertySnapshot(ImmutableArray<NpcPropertyEntry> Properties);
