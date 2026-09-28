using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class SkyrimMainWorkspaceRules
{
    private static readonly ImmutableHashSet<SkyrimMainWorkspaceRoute>
        NpcRoutes = Enum.GetValues<SkyrimMainWorkspaceRoute>()
            .ToImmutableHashSet();

    public static ImmutableArray<SkyrimMainWorkspaceRecord> Filter(
        IEnumerable<SkyrimMainWorkspaceRecord> source,
        SkyrimMainWorkspaceFilter filter)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(filter);
        string search = filter.Search?.Trim() ?? string.Empty;
        ImmutableHashSet<NpcCategory> categories =
            filter.Categories ?? ImmutableHashSet<NpcCategory>.Empty;

        return source.Where(record =>
                KindVisible(record, filter) &&
                (filter.IncludeDeleted || !record.IsSourceDeleted) &&
                (!filter.ChangedOnly ||
                 record.ChangeState != NpcChangeState.Unchanged) &&
                GenderVisible(record, filter.Gender) &&
                CategoryVisible(record, categories) &&
                MatchesSearch(record, search))
            .ToImmutableArray();
    }

    public static ImmutableArray<SkyrimMainWorkspaceIdentity> SelectRange(
        ImmutableArray<SkyrimMainWorkspaceRecord> visible,
        SkyrimMainWorkspaceIdentity anchor,
        SkyrimMainWorkspaceIdentity extent)
    {
        if (visible.IsDefaultOrEmpty) return [];
        int anchorIndex = IndexOf(visible, anchor);
        int extentIndex = IndexOf(visible, extent);
        if (anchorIndex < 0 || extentIndex < 0) return [];
        int first = Math.Min(anchorIndex, extentIndex);
        int count = Math.Abs(anchorIndex - extentIndex) + 1;
        return visible
            .Skip(first)
            .Take(count)
            .Select(item => item.Identity)
            .ToImmutableArray();
    }

    public static SkyrimMainWorkspaceDraft MarkChanged(
        SkyrimMainWorkspaceIdentity identity) =>
        new(identity, true, false);

    public static SkyrimMainWorkspaceDraft MarkChanged(
        SkyrimMainWorkspaceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft with { IsChanged = true };
    }

    public static SkyrimMainWorkspaceDraft MarkDelete(
        SkyrimMainWorkspaceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft with { IsDeletePending = true };
    }

    public static SkyrimMainWorkspaceDraft Restore(
        SkyrimMainWorkspaceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return draft with { IsDeletePending = false };
    }

    public static SkyrimMainWorkspaceDraft Reset(
        SkyrimMainWorkspaceDraft draft)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return SkyrimMainWorkspaceDraft.Clean(draft.Identity);
    }

    public static SkyrimMainWorkspacePreviewProjection ProjectPreview(
        SkyrimMainWorkspacePreviewOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var assets = ImmutableHashSet.CreateBuilder<PreviewAssetCategory>();
        assets.Add(PreviewAssetCategory.Face);
        assets.Add(PreviewAssetCategory.Hair);
        assets.Add(PreviewAssetCategory.Accessory);
        if (options.RenderHeadwear)
            assets.Add(PreviewAssetCategory.Headwear);

        if (options.Mode == SkyrimMainWorkspacePreviewMode.FullCharacter)
        {
            if (options.RenderBody)
                assets.Add(PreviewAssetCategory.Body);
            if (options.RenderUnderarmor)
                assets.Add(PreviewAssetCategory.Underarmor);
            if (options.RenderArmor)
            {
                assets.Add(PreviewAssetCategory.Armor);
                assets.Add(PreviewAssetCategory.Outfit);
            }
            if (options.RenderGore)
                assets.Add(PreviewAssetCategory.Gore);
        }

        var morphs = ImmutableHashSet.CreateBuilder<PreviewMorphCategory>();
        if (options.ApplyBoneMorphs)
            morphs.Add(PreviewMorphCategory.Bone);
        if (options.ApplyVertexMorphs)
            morphs.Add(PreviewMorphCategory.Vertex);
        if (options.ApplyBodyWeight)
            morphs.Add(PreviewMorphCategory.Weight);
        if (options.ApplySculpt)
            morphs.Add(PreviewMorphCategory.Sculpt);
        return new SkyrimMainWorkspacePreviewProjection(
            assets.ToImmutable(), morphs.ToImmutable());
    }

    public static ImmutableHashSet<SkyrimMainWorkspaceRoute> AvailableRoutes(
        SkyrimMainWorkspaceRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.Kind != SkyrimMainWorkspaceRecordKind.Npc ||
            record.IsSourceDeleted)
            return ImmutableHashSet<SkyrimMainWorkspaceRoute>.Empty;
        return NpcRoutes;
    }

    public static string PlaceAtMe(SkyrimMainWorkspaceIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!string.Equals(identity.Signature, "NPC_",
                StringComparison.Ordinal))
            throw new ArgumentException(
                "PlaceAtMe requires an NPC_ identity.", nameof(identity));
        return $"player.placeatme XX{identity.FormId.Value:X6} 1";
    }

    private static bool KindVisible(
        SkyrimMainWorkspaceRecord record,
        SkyrimMainWorkspaceFilter filter) =>
        record.Kind switch
        {
            SkyrimMainWorkspaceRecordKind.Npc => filter.ShowNpcs,
            SkyrimMainWorkspaceRecordKind.LeveledNpc =>
                filter.ShowLeveledNpcs,
            _ => false
        };

    private static bool GenderVisible(
        SkyrimMainWorkspaceRecord record,
        SkyrimMainWorkspaceGender gender) =>
        gender switch
        {
            SkyrimMainWorkspaceGender.Random => true,
            SkyrimMainWorkspaceGender.Male =>
                record.Kind == SkyrimMainWorkspaceRecordKind.Npc &&
                record.Sex == NpcSex.Male,
            SkyrimMainWorkspaceGender.Female =>
                record.Kind == SkyrimMainWorkspaceRecordKind.Npc &&
                record.Sex == NpcSex.Female,
            _ => false
        };

    private static bool CategoryVisible(
        SkyrimMainWorkspaceRecord record,
        ImmutableHashSet<NpcCategory> categories) =>
        record.Kind != SkyrimMainWorkspaceRecordKind.Npc ||
        record.Categories.Any(categories.Contains);

    private static bool MatchesSearch(
        SkyrimMainWorkspaceRecord record,
        string search)
    {
        if (search.Length == 0) return true;
        return Contains(record.EditorId, search) ||
               Contains(record.Name, search) ||
               Contains(record.Identity.FormId.ToString(), search) ||
               Contains(record.Identity.OwnerPlugin.Value, search) ||
               Contains(record.Identity.WinningProvider.Value, search);
    }

    private static bool Contains(string? value, string search) =>
        value?.Contains(search, StringComparison.OrdinalIgnoreCase) == true;

    private static int IndexOf(
        ImmutableArray<SkyrimMainWorkspaceRecord> visible,
        SkyrimMainWorkspaceIdentity identity)
    {
        for (var index = 0; index < visible.Length; index++)
        {
            if (visible[index].Identity == identity) return index;
        }
        return -1;
    }
}
