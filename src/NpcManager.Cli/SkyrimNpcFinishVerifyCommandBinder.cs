using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed record SkyrimNpcFinishVerifyCommandBinding(
    WorkspacePath Manifest,
    Sha256Hash ManifestSha256,
    string ManifestSha256Wire,
    WorkspacePath? VerificationOutput);

internal sealed record SkyrimNpcFinishVerifyCommandBindingResult(
    SkyrimNpcFinishVerifyCommandBinding? Binding,
    string? ErrorMessage)
{
    public bool IsValid => Binding is not null && ErrorMessage is null;
}

internal static class SkyrimNpcFinishVerifyCommandBinder
{
    private static readonly string[] V1Options =
    [
        "manifest", "manifest-sha256", "data-root", "plugins"
    ];

    private static readonly string[] V2Options =
    [
        "manifest", "manifest-sha256", "verification-output",
        "workflow-bundle", "workflow-bundle-sha256", "workflow-output",
        "data-root", "plugins"
    ];

    private static readonly string[] V1RequiredOptions =
    [
        "manifest", "manifest-sha256"
    ];

    private static readonly string[] V2RequiredOptions =
    [
        "manifest", "manifest-sha256", "verification-output",
        "workflow-bundle", "workflow-bundle-sha256", "workflow-output"
    ];

    public static SkyrimNpcFinishVerifyCommandBindingResult Bind(
        ParsedCommand command,
        bool protocolV2)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (protocolV2 && (command.Positionals.Length != 3 ||
            !command.Positionals.SequenceEqual(
                ["npc", "finish", "verify"],
                StringComparer.OrdinalIgnoreCase)))
            return Invalid(
                "npc finish verify accepts no positional arguments beyond the command name.");
        if (command.DuplicateOptions.Length != 0)
            return Invalid("Finish Core options may not be repeated.");

        string[] allowed = protocolV2 ? V2Options : V1Options;
        string? unknown = command.Options.Keys.FirstOrDefault(option =>
            !allowed.Contains(option, StringComparer.OrdinalIgnoreCase));
        if (unknown is not null)
            return Invalid($"Finish Core does not support --{unknown}.");

        string[] required = protocolV2 ? V2RequiredOptions : V1RequiredOptions;
        string? missing = required.FirstOrDefault(option =>
            !command.Options.TryGetValue(option, out string? value) ||
            string.IsNullOrWhiteSpace(value) || value == "true");
        if (missing is not null)
            return Invalid($"Finish Core requires --{missing}.");

        string manifestSha256 = command.Options["manifest-sha256"];
        if (protocolV2 && !IsUpperSha256(manifestSha256))
            return Invalid(
                "Protocol-v2 Finish Verify requires an uppercase 64-character --manifest-sha256.");

        return new SkyrimNpcFinishVerifyCommandBindingResult(
            new SkyrimNpcFinishVerifyCommandBinding(
                new WorkspacePath(command.Options["manifest"]),
                new Sha256Hash(manifestSha256),
                manifestSha256,
                protocolV2
                    ? new WorkspacePath(command.Options["verification-output"])
                    : null),
            null);
    }

    private static bool IsUpperSha256(string value) =>
        value.Length == 64 && value.All(character =>
            character is >= '0' and <= '9' or >= 'A' and <= 'F');

    private static SkyrimNpcFinishVerifyCommandBindingResult Invalid(
        string message) => new(null, message);
}
