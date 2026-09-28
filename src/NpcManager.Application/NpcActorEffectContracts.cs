using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Ordered, duplicate-free SPLO spell-reference list operations.</summary>
public sealed record NpcActorEffectPatch(
    ImmutableArray<FormReference>? Replace,
    ImmutableArray<FormReference> Add,
    ImmutableArray<FormReference> Remove)
{
    public bool IsEmpty => Replace is null && Add.IsDefaultOrEmpty && Remove.IsDefaultOrEmpty;
}

public sealed record NpcActorEffectSnapshot(ImmutableArray<FormReference> ActorEffects);
