using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed record RaceMenuNpcPreflightCommandBinding(
    WorkspacePath SourceRequest,
    Sha256Hash SourceRequestSha256,
    WorkspacePath Preset,
    Sha256Hash ExpectedPresetSha256,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath CompanionRoot,
    WorkspacePath Output,
    WorkspacePath? FaceBakeAuthorityOutput = null);

internal sealed record RaceMenuNpcPreflightCommandBindingResult(
    RaceMenuNpcPreflightCommandBinding? Binding,
    string? ErrorMessage)
{
    public bool IsValid => Binding is not null && ErrorMessage is null;
}

internal static class RaceMenuNpcPreflightCommandBinder
{
    public static RaceMenuNpcPreflightCommandBindingResult Bind(
        ParsedCommand command,
        bool strict)
    {
        RaceMenuJslotNpcBuildCommandBindingResult result =
            RaceMenuJslotNpcBuildCommandBinder.Bind(
                command,
                strict,
                requireReviewedPreflight: false);
        if (!result.IsValid || result.Binding is not
            {
                Mode: RaceMenuJslotNpcBuildCommandMode.Preflight,
                PreflightOutput: { } output
            } binding)
            return Invalid(result.ErrorMessage ??
                "npc create-from-jslot preflight requires --preflight-output.");
        return new RaceMenuNpcPreflightCommandBindingResult(
            new RaceMenuNpcPreflightCommandBinding(
                binding.SourceRequest,
                binding.SourceRequestSha256,
                binding.Preset,
                binding.ExpectedPresetSha256,
                binding.DataRoot,
                binding.PluginOrder,
                binding.CompanionRoot,
                output,
                binding.FaceBakeAuthorityOutput),
            null);
    }

    private static RaceMenuNpcPreflightCommandBindingResult Invalid(
        string message) => new(null, message);
}
