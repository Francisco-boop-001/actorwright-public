using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

/// <summary>Ordered, duplicate-free FormID list operations used by NPC KWDA/APPR fields.</summary>
public sealed record NpcKeywordListPatch(
    ImmutableArray<FormReference>? Replace,
    ImmutableArray<FormReference> Add,
    ImmutableArray<FormReference> Remove)
{
    public bool IsEmpty => Replace is null && Add.IsDefaultOrEmpty && Remove.IsDefaultOrEmpty;
}

public sealed record NpcKeywordPatch(
    NpcKeywordListPatch Keywords,
    NpcKeywordListPatch? AttachParentSlots = null)
{
    public bool IsEmpty => Keywords.IsEmpty && (AttachParentSlots is null || AttachParentSlots.IsEmpty);
}

public sealed record NpcKeywordSnapshot(
    ImmutableArray<FormReference> Keywords,
    ImmutableArray<FormReference> AttachParentSlots);
