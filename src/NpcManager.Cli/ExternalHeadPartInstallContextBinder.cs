using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Cli;

internal sealed record ExternalHeadPartInstallContextBindingResult(
    bool IsSpecified,
    WorkspacePath? DataRoot,
    ImmutableArray<PluginName> EnabledPluginOrder,
    string? ErrorMessage)
{
    public bool IsValid => ErrorMessage is null;

    public ExternalHeadPartInstallVerificationContext CreateContext(
        FormReference targetRace)
    {
        if (!IsValid || !IsSpecified || DataRoot is not { } dataRoot ||
            EnabledPluginOrder.IsDefaultOrEmpty)
            throw new InvalidOperationException(
                "An installed external Finish context is not complete.");

        return new ExternalHeadPartInstallVerificationContext(
            dataRoot,
            EnabledPluginOrder,
            targetRace);
    }
}

/// <summary>
/// Binds the two ephemeral external-provider options shared by Finish Core
/// commands.  The resulting root/order are never part of a Finish document.
/// </summary>
internal static class ExternalHeadPartInstallContextBinder
{
    private const int MaximumPlugins = 64;

    public static ExternalHeadPartInstallContextBindingResult Bind(
        ParsedCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);

        bool hasDataRoot = command.Options.TryGetValue(
            "data-root", out string? dataRootValue);
        bool hasPlugins = command.Options.TryGetValue(
            "plugins", out string? pluginsValue);
        if (!hasDataRoot && !hasPlugins)
            return new(false, null, [], null);

        if (command.DuplicateOptions.Any(option =>
                option.Equals("data-root", StringComparison.OrdinalIgnoreCase) ||
                option.Equals("plugins", StringComparison.OrdinalIgnoreCase)))
            return Invalid("Finish Core external install options may not be repeated.");

        if (!hasDataRoot || string.IsNullOrWhiteSpace(dataRootValue) ||
            string.Equals(dataRootValue, "true", StringComparison.OrdinalIgnoreCase))
            return Invalid(
                "Finish Core external install context requires --data-root <absolute K-local path>.");

        if (!IsAbsoluteKLocalPath(dataRootValue!))
            return Invalid("--data-root must be an absolute K-local path.");

        if (!hasPlugins || string.IsNullOrWhiteSpace(pluginsValue) ||
            string.Equals(pluginsValue, "true", StringComparison.OrdinalIgnoreCase))
            return Invalid(
                "Finish Core external install context requires --plugins <complete enabled order>.");

        string[] rawPlugins = pluginsValue!.Split(',', StringSplitOptions.None);
        if (rawPlugins.Length > MaximumPlugins)
            return Invalid(
                $"--plugins must contain no more than {MaximumPlugins} enabled plugins.");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var plugins = ImmutableArray.CreateBuilder<PluginName>(rawPlugins.Length);
        try
        {
            foreach (string rawPlugin in rawPlugins)
            {
                string value = rawPlugin.Trim();
                if (value.Length == 0)
                    return Invalid("--plugins must not contain empty entries.");

                PluginName plugin = new(value);
                if (!seen.Add(plugin.Value))
                    return Invalid(
                        $"--plugins contains duplicate plugin '{plugin.Value}'.");
                plugins.Add(plugin);
            }
        }
        catch (ArgumentException exception)
        {
            return Invalid(exception.Message);
        }

        if (plugins.Count == 0)
            return Invalid("--plugins must contain at least one plugin.");

        return new(
            true,
            new WorkspacePath(dataRootValue!),
            plugins.ToImmutable(),
            null);
    }

    public static bool TryReadTargetRace(
        SkyrimNpcFinishCoreRequest request,
        out FormReference targetRace,
        out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(request);
        targetRace = default;
        errorMessage = string.Empty;
        if (request.Source.PluginPath is not { } pluginPath ||
            request.Source.PluginSha256 is not { } pluginSha256 ||
            request.Actor.FormId is not { } actorFormId)
        {
            errorMessage =
                "External Finish Core context requires the hash-bound source plugin and actor FormID.";
            return false;
        }

        return TryReadTargetRace(
            pluginPath,
            pluginSha256,
            actorFormId,
            out targetRace,
            out errorMessage);
    }

    public static bool TryReadTargetRace(
        SkyrimNpcFinishCoreManifest manifest,
        out FormReference targetRace,
        out string errorMessage)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        targetRace = default;
        errorMessage = string.Empty;
        if (manifest.PackageRoot is not { } packageRoot ||
            manifest.Plugin is not { } plugin ||
            manifest.PluginSha256 is not { } pluginSha ||
            manifest.BaseNpc is not { } baseNpc)
        {
            errorMessage =
                "External Finish Core verification requires the hash-bound package plugin and base NPC.";
            return false;
        }
        if (!string.Equals(
                baseNpc.Plugin.Value,
                plugin.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            errorMessage =
                "The external Finish Core base NPC must be owned by the manifest output plugin.";
            return false;
        }

        string[] candidatePluginPaths =
        [
            Path.Combine(packageRoot.Value, "Data", plugin.Value),
            Path.Combine(packageRoot.Value, plugin.Value)
        ];
        string[] existingPluginPaths = candidatePluginPaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(File.Exists)
            .ToArray();
        if (existingPluginPaths.Length != 1)
        {
            errorMessage =
                "The external Finish package must contain exactly one conventional Data/output-plugin path.";
            return false;
        }

        string pluginPath;
        try
        {
            pluginPath = Path.GetFullPath(existingPluginPaths[0]);
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }

        WorkspacePath path = new(pluginPath);
        if (!path.IsUnder(packageRoot))
        {
            errorMessage =
                "The external Finish Core output plugin escaped the package root.";
            return false;
        }

        return TryReadTargetRace(
            path,
            pluginSha,
            baseNpc.FormId,
            out targetRace,
            out errorMessage);
    }

    private static bool TryReadTargetRace(
        WorkspacePath pluginPath,
        Sha256Hash expectedPluginSha256,
        FormId actorFormId,
        out FormReference targetRace,
        out string errorMessage)
    {
        targetRace = default;
        errorMessage = string.Empty;
        try
        {
            if (!Path.IsPathFullyQualified(pluginPath.Value) ||
                !File.Exists(pluginPath.Value) ||
                HasReparsePath(pluginPath.Value))
            {
                errorMessage =
                    "The hash-bound Finish Core output plugin is missing or not an ordinary file.";
                return false;
            }

            FileAttributes attributes = File.GetAttributes(pluginPath.Value);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Device))
            {
                errorMessage =
                    "The hash-bound Finish Core output plugin must not be a reparse point or device.";
                return false;
            }

            byte[] before = File.ReadAllBytes(pluginPath.Value);
            if (HashBytes(before) != expectedPluginSha256)
            {
                errorMessage =
                    "The hash-bound Finish Core output plugin hash drifted.";
                return false;
            }

            NpcFaceSnapshot snapshot = BethesdaNpcFaceAdapter.Read(
                GameEdition.SkyrimSpecialEdition,
                pluginPath,
                actorFormId);
            byte[] after = File.ReadAllBytes(pluginPath.Value);
            if (!before.AsSpan().SequenceEqual(after) ||
                HashBytes(after) != expectedPluginSha256)
            {
                errorMessage =
                    "The hash-bound Finish Core output plugin changed while its target race was read.";
                return false;
            }

            if (snapshot.Race is not { } race || race.FormId.Value == 0 ||
                string.IsNullOrWhiteSpace(race.Plugin.Value))
            {
                errorMessage =
                    "The exact Finish Core target NPC has no persisted race.";
                return false;
            }

            targetRace = race;
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                ArgumentException or InvalidOperationException or
                KeyNotFoundException or UnauthorizedAccessException or
                NotSupportedException)
        {
            errorMessage =
                "The exact Finish Core target NPC race could not be read: " +
                exception.Message;
            return false;
        }
    }

    private static bool HasReparsePath(string path)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                break;
            current = parent ?? string.Empty;
        }
        return false;
    }

    private static bool IsAbsoluteKLocalPath(string value)
    {
        try
        {
            if (!Path.IsPathFullyQualified(value))
                return false;
            string? root = Path.GetPathRoot(Path.GetFullPath(value));
            return string.Equals(root, "K:\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static Sha256Hash HashBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static ExternalHeadPartInstallContextBindingResult Invalid(
        string message) => new(true, null, [], message);
}
