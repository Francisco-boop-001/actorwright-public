using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Resolves a plugin through an explicitly reviewed authority when one exists,
/// otherwise through the legacy copied-Data root beside the template carrier.
/// Hash and workspace validation remain the caller's responsibility.
/// </summary>
public static class BethesdaNpcCreationProviderResolver
{
    public static string Resolve(
        WorkspacePath dataRoot,
        PluginName plugin,
        ImmutableArray<NpcCreationPluginAuthority> authorities)
    {
        if (authorities.IsDefaultOrEmpty)
            return Path.Combine(dataRoot.Value, plugin.Value);

        NpcCreationPluginAuthority? match = null;
        foreach (var authority in authorities)
        {
            if (!string.Equals(authority.Plugin.Value, plugin.Value,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (match is not null)
                throw new InvalidDataException(
                    $"Plugin authority '{plugin}' is declared more than once.");
            match = authority;
        }

        return match?.PluginPath.Value ?? Path.Combine(dataRoot.Value, plugin.Value);
    }
}
