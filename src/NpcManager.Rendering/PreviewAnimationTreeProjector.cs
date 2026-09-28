using System.Collections.Immutable;
using NpcManager.Application;

namespace NpcManager.Rendering;

internal static class PreviewAnimationTreeProjector
{
    internal static ImmutableArray<PreviewAnimationTreeBranch> Project(
        IEnumerable<PreviewAnimationListItem> items)
    {
        PreviewAnimationListItem[] admitted = items.ToArray();
        IEnumerable<PreviewAnimationListItem> ordinary = admitted
            .Where(item => string.IsNullOrEmpty(item.Category));
        IEnumerable<PreviewAnimationListItem> gestures = admitted
            .Where(item => !string.IsNullOrEmpty(item.Category));
        var groups = ImmutableArray.CreateBuilder<PreviewAnimationTreeBranch>();

        foreach (var roleGroup in ordinary
                     .SelectMany(item => RolesOf(item).Select(role =>
                         (Role: role, Item: item)))
                     .GroupBy(pair => pair.Role,
                         StringComparer.OrdinalIgnoreCase)
                     .OrderBy(group => RoleOrder(group.Key)))
        {
            var root = new MutableBranch("role", RoleDisplay(roleGroup.Key));
            foreach (PreviewAnimationListItem item in roleGroup
                         .Select(pair => pair.Item)
                         .DistinctBy(item => item.Id,
                             StringComparer.OrdinalIgnoreCase)
                         .OrderBy(ClipDisplayName,
                             StringComparer.OrdinalIgnoreCase)
                         .ThenBy(item => item.Path,
                             StringComparer.OrdinalIgnoreCase))
            {
                string folder = string.IsNullOrEmpty(item.Folder)
                    ? "(general)"
                    : item.Folder;
                MutableBranch parent = root;
                foreach (string segment in SplitFolder(folder))
                    parent = parent.GetOrAddChild("folder", segment);
                parent.Leaves.Add(new PreviewAnimationTreeLeaf(
                    LeafLabel(item), item));
            }
            groups.Add(Freeze(root));
        }

        if (gestures.Any())
        {
            var root = new MutableBranch(
                "gestures", "Gestures & Dialogue (IDLE)");
            foreach (var category in gestures
                         .GroupBy(item => item.Category,
                             StringComparer.OrdinalIgnoreCase)
                         .OrderByDescending(group => group.Count())
                         .ThenBy(group => group.Key,
                             StringComparer.OrdinalIgnoreCase))
            {
                MutableBranch branch = root.GetOrAddChild(
                    "category", category.Key);
                foreach (PreviewAnimationListItem item in category
                             .OrderBy(ClipDisplayName,
                                 StringComparer.OrdinalIgnoreCase)
                             .ThenBy(item => item.Path,
                                 StringComparer.OrdinalIgnoreCase))
                {
                    branch.Leaves.Add(new PreviewAnimationTreeLeaf(
                        LeafLabel(item), item));
                }
            }
            groups.Add(Freeze(root));
        }

        return groups.ToImmutable();
    }

    private static PreviewAnimationTreeBranch Freeze(MutableBranch branch)
    {
        ImmutableArray<PreviewAnimationTreeBranch> children = branch.Children.Values
            .OrderBy(child => child.Name, StringComparer.OrdinalIgnoreCase)
            .Select(Freeze)
            .ToImmutableArray();
        ImmutableArray<PreviewAnimationTreeLeaf> leaves = branch.Leaves
            .OrderBy(leaf => leaf.Clip.ClipName,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(leaf => leaf.Clip.Path,
                StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new PreviewAnimationTreeBranch(
            branch.Kind,
            branch.Name,
            children.Sum(child => child.ClipCount) + leaves.Length,
            children,
            leaves);
    }

    private static string[] SplitFolder(string folder) =>
        folder.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries |
                                  StringSplitOptions.TrimEntries);

    private static ImmutableArray<string> RolesOf(
        PreviewAnimationListItem item) =>
        item.Roles.IsDefaultOrEmpty
            ? ImmutableArray.Create("Other")
            : item.Roles;

    private static string ClipDisplayName(PreviewAnimationListItem item) =>
        string.IsNullOrWhiteSpace(item.ClipName)
            ? Path.GetFileNameWithoutExtension(item.Path)
            : item.ClipName;

    private static string LeafLabel(PreviewAnimationListItem item)
    {
        string provenance = item.FromBehaviorGraph ? string.Empty : "Search only · ";
        string additive = item.Additive ? "Additive · " : string.Empty;
        string gender = item.RequiresFemale ? " · Female" : string.Empty;
        return $"{provenance}{additive}{ClipDisplayName(item)}{gender}  —  {item.Path}";
    }

    private static int RoleOrder(string role) => role.ToLowerInvariant() switch
    {
        "core" => 0,
        "mt" => 1,
        "weapon" => 2,
        "furniture" => 3,
        "idle" => 4,
        "pipboy" => 5,
        _ => 9
    };

    private static string RoleDisplay(string role) => role.ToLowerInvariant() switch
    {
        "core" => "Core (death / get-up / swim)",
        "mt" => "Locomotion (MT)",
        "weapon" => "Weapon / combat",
        "furniture" => "Furniture",
        "idle" => "Idle",
        "pipboy" => "Pipboy",
        _ => role
    };

    private sealed class MutableBranch(string kind, string name)
    {
        internal string Kind { get; } = kind;
        internal string Name { get; } = name;
        internal Dictionary<string, MutableBranch> Children { get; } =
            new(StringComparer.OrdinalIgnoreCase);
        internal List<PreviewAnimationTreeLeaf> Leaves { get; } = [];

        internal MutableBranch GetOrAddChild(
            string childKind,
            string childName)
        {
            if (!Children.TryGetValue(childName, out MutableBranch? child))
            {
                child = new MutableBranch(childKind, childName);
                Children.Add(childName, child);
            }
            return child;
        }
    }
}
