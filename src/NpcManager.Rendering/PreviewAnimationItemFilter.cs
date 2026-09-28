using NpcManager.Application;

namespace NpcManager.Rendering;

internal static class PreviewAnimationItemFilter
{
    internal static bool PassesGender(
        PreviewAnimationListItem item,
        bool targetIsFemale,
        bool filterByGender) =>
        !filterByGender || targetIsFemale || !item.RequiresFemale;

    internal static bool PassesPerspective(
        PreviewAnimationListItem item,
        bool showFirstPerson) =>
        showFirstPerson || !item.IsFirstPersonOnly;

    internal static bool MatchesAll(
        PreviewAnimationListItem item,
        string filter)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        string haystack = string.Join(' ',
            item.ClipName,
            item.Path,
            item.Folder,
            item.Category,
            item.StateAxes,
            string.Join(' ', item.Roles.IsDefault ? [] : item.Roles));
        return filter.Split(' ', StringSplitOptions.RemoveEmptyEntries |
                                  StringSplitOptions.TrimEntries)
            .All(term => haystack.Contains(term,
                StringComparison.OrdinalIgnoreCase));
    }
}
