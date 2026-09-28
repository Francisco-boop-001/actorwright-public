using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public sealed record NpcWholeSkinTexture(AssetPath Path, Sha256Hash Sha256);

public enum NpcWholeSkinHeadPolicy
{
    Replace,
    Preserve
}

/// <summary>Exact copied texture/provider authority for a female-only private skin patch.</summary>
public sealed record NpcWholeSkinPatch(
    WorkspacePath DataRoot,
    ImmutableArray<NpcCreationPluginAuthority> PluginAuthorities,
    ImmutableDictionary<string, NpcWholeSkinTexture> Head,
    ImmutableDictionary<string, NpcWholeSkinTexture> Body,
    ImmutableDictionary<string, NpcWholeSkinTexture> Hands,
    string DocumentJson,
    Sha256Hash DocumentSha256)
{
    public NpcWholeSkinHeadPolicy HeadPolicy { get; init; } = NpcWholeSkinHeadPolicy.Replace;
    public ImmutableArray<NpcWholeSkinTexture> PreservedAssets { get; init; } = [];
    public bool PreservesHead => HeadPolicy == NpcWholeSkinHeadPolicy.Preserve;
}

public sealed record NpcWholeSkinPlan(
    NpcWholeSkinPatch Patch,
    FormId FirstAllocatedLocalFormId,
    RaceMenuNpcFormBinding? FaceHeadPart,
    OutputOwnedSkyrimNpcNakedSkinBinding Skin)
{
    public int AllocatedRecordCount => Patch.PreservesHead ? 6 : 8;
    public ImmutableArray<string> ChangedNpcSubrecords => Patch.PreservesHead ? ["WNAM"] : ["WNAM", "FTST", "PNAM"];
}
