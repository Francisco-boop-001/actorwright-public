using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal enum RaceMenuJslotNpcBuildCommandMode
{
    Preflight,
    Build
}

internal sealed record RaceMenuJslotNpcBuildCommandBinding(
    RaceMenuJslotNpcBuildCommandMode Mode,
    WorkspacePath SourceRequest,
    Sha256Hash SourceRequestSha256,
    WorkspacePath Preset,
    Sha256Hash ExpectedPresetSha256,
    WorkspacePath DataRoot,
    ImmutableArray<PluginName> PluginOrder,
    WorkspacePath CompanionRoot,
    WorkspacePath? PreflightOutput,
    NpcBuildPreflightReviewAuthority? ReviewedPreflight,
    WorkspacePath? FaceBakeAuthorityOutput = null);

internal enum RaceMenuJslotNpcBuildCommandBindingFailure
{
    Usage,
    InvalidValue
}

internal sealed record RaceMenuJslotNpcBuildCommandBindingResult(
    RaceMenuJslotNpcBuildCommandBinding? Binding,
    string? ErrorMessage,
    RaceMenuJslotNpcBuildCommandBindingFailure Failure =
        RaceMenuJslotNpcBuildCommandBindingFailure.Usage)
{
    public bool IsValid => Binding is not null && ErrorMessage is null;
}

internal static class RaceMenuJslotNpcBuildCommandBinder
{
    private static readonly string[] CommonOptions =
    [
        "request", "request-sha256", "preset", "preset-sha256",
        "data-root", "plugins", "companion-root"
    ];

    private static readonly string[] ModeOptions =
    [
        "preflight-output", "reviewed-preflight",
        "reviewed-preflight-sha256", "face-bake-authority-output"
    ];

    private static readonly string[] WorkflowOptions =
    [
        "workflow-bundle", "workflow-bundle-sha256", "workflow-output"
    ];

    public static RaceMenuJslotNpcBuildCommandBindingResult Bind(
        ParsedCommand command,
        bool strict,
        bool requireReviewedPreflight)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (strict && (command.Positionals.Length != 2 ||
            !command.Positionals.SequenceEqual(
                ["npc", "create-from-jslot"],
                StringComparer.OrdinalIgnoreCase)))
            return Invalid(
                "npc create-from-jslot accepts no extra positional arguments.");
        if (strict && command.DuplicateOptions.Length > 0)
            return Invalid(
                $"Duplicate option '--{command.DuplicateOptions[0]}' is not allowed.");
        if (strict)
        {
            IEnumerable<string> allowed = CommonOptions.Concat(ModeOptions)
                .Concat(WorkflowOptions);
            string? unknown = command.Options.Keys.FirstOrDefault(option =>
                !allowed.Contains(option, StringComparer.OrdinalIgnoreCase));
            if (unknown is not null)
                return Invalid($"Unknown option '--{unknown}'.");
        }

        string? missing = CommonOptions.FirstOrDefault(option =>
            !command.Options.TryGetValue(option, out string? value) ||
            string.IsNullOrWhiteSpace(value));
        if (missing is not null)
            return Invalid($"npc create-from-jslot requires --{missing}.");

        bool writesPreflight = command.Options.TryGetValue(
            "preflight-output", out string? preflightValue);
        bool derivesFaceBake = command.Options.TryGetValue("face-bake-authority-output", out string? faceBakeValue);
        if (derivesFaceBake && (!writesPreflight || string.IsNullOrWhiteSpace(faceBakeValue)))
            return Invalid("--face-bake-authority-output requires a nonempty fresh path and --preflight-output.");
        bool hasReviewedPath = command.Options.TryGetValue(
            "reviewed-preflight", out string? reviewedValue);
        bool hasReviewedHash = command.Options.TryGetValue(
            "reviewed-preflight-sha256", out string? reviewedHash);
        if (writesPreflight && string.IsNullOrWhiteSpace(preflightValue) ||
            strict &&
            (hasReviewedPath && string.IsNullOrWhiteSpace(reviewedValue) ||
             hasReviewedHash && string.IsNullOrWhiteSpace(reviewedHash)))
            return Invalid(
                "Preflight and reviewed-preflight options require nonempty values.");
        if (writesPreflight && (hasReviewedPath || hasReviewedHash) ||
            hasReviewedPath != hasReviewedHash)
            return Invalid(
                "Use either --preflight-output or the complete --reviewed-preflight/--reviewed-preflight-sha256 pair.");
        if (requireReviewedPreflight &&
            (writesPreflight || !hasReviewedPath || !hasReviewedHash))
            return Invalid(
                "Protocol-v2 NPC build requires the complete reviewed-preflight path/hash pair and no --preflight-output.");

        string requestValue = command.Options["request"];
        string requestPath = requestValue.StartsWith('@')
            ? requestValue[1..]
            : requestValue;
        string requestHash = command.Options["request-sha256"];
        string presetHash = command.Options["preset-sha256"];
        if (strict && (!IsUpperSha256(requestHash) ||
                       !IsUpperSha256(presetHash) ||
                       hasReviewedHash && !IsUpperSha256(reviewedHash!)))
            return Invalid(
                "Protocol-v2 NPC build requires uppercase 64-character SHA-256 bindings.");

        try
        {
            ImmutableArray<PluginName> plugins = command.Options["plugins"]
                .Split(',', StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                .Select(value => new PluginName(value))
                .ToImmutableArray();
            if (strict && (plugins.IsDefaultOrEmpty ||
                plugins.Select(item => item.Value)
                    .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                plugins.Length))
                return Invalid(
                    "--plugins must contain distinct comma-separated plugin names.");

            NpcBuildPreflightReviewAuthority? reviewed = hasReviewedPath
                ? new NpcBuildPreflightReviewAuthority(
                    new WorkspacePath(reviewedValue!),
                    new Sha256Hash(reviewedHash!))
                : null;
            return new RaceMenuJslotNpcBuildCommandBindingResult(
                new RaceMenuJslotNpcBuildCommandBinding(
                    writesPreflight
                        ? RaceMenuJslotNpcBuildCommandMode.Preflight
                        : RaceMenuJslotNpcBuildCommandMode.Build,
                    new WorkspacePath(requestPath),
                    new Sha256Hash(requestHash),
                    new WorkspacePath(command.Options["preset"]),
                    new Sha256Hash(presetHash),
                    new WorkspacePath(command.Options["data-root"]),
                    plugins,
                    new WorkspacePath(command.Options["companion-root"]),
                    writesPreflight
                        ? new WorkspacePath(preflightValue!)
                        : null,
                    reviewed,
                    derivesFaceBake ? new WorkspacePath(faceBakeValue!) : null),
                null);
        }
        catch (ArgumentException exception)
        {
            return Invalid(
                exception.Message,
                strict || writesPreflight
                    ? RaceMenuJslotNpcBuildCommandBindingFailure.Usage
                    : RaceMenuJslotNpcBuildCommandBindingFailure.InvalidValue);
        }
    }

    private static bool IsUpperSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static RaceMenuJslotNpcBuildCommandBindingResult Invalid(
        string message,
        RaceMenuJslotNpcBuildCommandBindingFailure failure =
            RaceMenuJslotNpcBuildCommandBindingFailure.Usage) =>
        new(null, message, failure);
}
