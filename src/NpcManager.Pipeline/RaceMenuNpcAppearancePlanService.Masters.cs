using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcAppearancePlanService
{
    private static ImmutableArray<NpcCreationPluginAuthority> BuildPluginAuthorities(
        RaceMenuNpcBuildRequest request,
        RecordAuthorityBinding authority,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = ImmutableArray.CreateBuilder<NpcCreationPluginAuthority>();
        var byPlugin = new Dictionary<string, NpcCreationPluginAuthority>(
            StringComparer.OrdinalIgnoreCase);

        void Add(NpcCreationPluginAuthority candidate, string source)
        {
            if (!byPlugin.TryGetValue(candidate.Plugin.Value, out var existing))
            {
                byPlugin.Add(candidate.Plugin.Value, candidate);
                result.Add(candidate);
                return;
            }

            if (!string.Equals(existing.PluginPath.Value, candidate.PluginPath.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                existing.ExpectedSha256 != candidate.ExpectedSha256)
            {
                diagnostics.Add(Error("racemenu-plan-plugin-authority-conflict",
                    $"{source} resolves plugin '{candidate.Plugin}' through conflicting paths or hashes."));
            }
        }

        foreach (var candidate in request.PluginAuthorities.IsDefault
                     ? []
                     : request.PluginAuthorities)
        {
            Add(candidate, "The selected plugin order");
        }

        foreach (var binding in authority.FormBindings.Prepend(authority.RaceBinding))
        {
            Add(new NpcCreationPluginAuthority(
                    binding.ProviderPluginName,
                    binding.ProviderPlugin,
                    binding.ProviderPluginSha256),
                "Record authority");
        }

        return result.ToImmutable();
    }

    private static ImmutableArray<PluginName> BuildSourceDependencies(
        PresetAppearance appearance)
    {
        var dependencies = ImmutableArray.CreateBuilder<PluginName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(PluginName plugin)
        {
            if (seen.Add(plugin.Value)) dependencies.Add(plugin);
        }

        if (appearance.RaceMenu is { } raceMenu)
        {
            foreach (var plugin in raceMenu.ModNames) Add(plugin);
            foreach (var mod in raceMenu.Mods) Add(mod.Name);
            if (!string.IsNullOrWhiteSpace(raceMenu.HeadTexture))
            {
                var identifier = PresetIdentifier.Parse(raceMenu.HeadTexture);
                if (identifier.Plugin is { } plugin) Add(plugin);
            }
        }

        foreach (var headPart in appearance.HeadParts)
        {
            if (headPart.Identifier.Plugin is { } plugin) Add(plugin);
        }

        return dependencies.ToImmutable();
    }

    private static ImmutableArray<PluginName> MergeSourceDependencies(
        ImmutableArray<PluginName> presetDependencies,
        RecordAuthorityBinding authority)
    {
        var dependencies = presetDependencies.ToBuilder();
        var seen = dependencies.Select(item => item.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var source in authority.FormBindings
                     .Select(binding => binding.SourceReference.Plugin))
        {
            if (seen.Add(source.Value)) dependencies.Add(source);
        }
        return dependencies.ToImmutable();
    }

    private static ImmutableArray<PluginName> BuildRequiredOutputMasters(
        ImmutableArray<PluginName> templateMasters,
        RaceMenuNpcBuildRequest request,
        RecordAuthorityBinding authority,
        ResolvedReferences resolved,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var masters = ImmutableArray.CreateBuilder<PluginName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(PluginName plugin, string role)
        {
            if (string.Equals(plugin.Value, request.OutputPlugin.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("racemenu-plan-output-self-master",
                    $"The {role} resolves to output plugin '{request.OutputPlugin}', " +
                    "which cannot be its own master."));
                return;
            }
            if (seen.Add(plugin.Value)) masters.Add(plugin);
        }

        foreach (var master in templateMasters) Add(master, "template master");
        foreach (var headPart in resolved.HeadParts)
            Add(headPart.Binding.Reference.Plugin, "mapped head-part provider");
        if (resolved.HairColor is RaceMenuNpcExternalHairColorAuthority externalHair)
            Add(externalHair.Binding.Reference.Plugin, "external hair-color provider");
        if (resolved.HeadTexture is { } headTexture)
            Add(headTexture.Reference.Plugin, "external head-texture provider");

        Add(request.References.Race.Plugin, "race provider");
        Add(request.References.Voice.Plugin, "voice provider");
        Add(request.References.Class.Plugin, "class provider");
        Add(request.References.CombatStyle.Plugin, "combat-style provider");
        if (request.References.DefaultOutfit is { } outfit)
            Add(outfit.Plugin, "default-outfit provider");
        Add(authority.RaceBinding.Reference.Plugin, "record-authority race provider");

        if (masters.Count > byte.MaxValue)
        {
            diagnostics.Add(Error("racemenu-plan-output-master-limit",
                "The required output master union exceeds Skyrim's 255-master bound."));
        }
        return masters.ToImmutable();
    }
}
