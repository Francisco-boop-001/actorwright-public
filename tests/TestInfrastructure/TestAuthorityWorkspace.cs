using System;
using System.IO;

namespace NpcManager.TestInfrastructure;

internal static class TestAuthorityWorkspace
{
    private const string EnvironmentVariable =
        "NPCMANAGER_TEST_AUTHORITY_WORKSPACE";

    internal static string ResolveLabRoot(string baseDirectory)
    {
        string? configured =
            Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            return ValidateConfiguredRoot(configured);

        for (DirectoryInfo? current = new(baseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (IsWorkspaceRoot(current.FullName))
                return current.FullName;
        }

        throw new InvalidOperationException(
            "Could not locate the K-local workspace root.");
    }

    internal static string ResolveProjectRoot(string baseDirectory) =>
        Path.Combine(
            ResolveLabRoot(baseDirectory),
            "projects",
            "NpcManagerReimplementation");

    private static string ValidateConfiguredRoot(string configured)
    {
        string fullPath = Path.GetFullPath(configured);
        string? pathRoot = Path.GetPathRoot(fullPath);
        if (!string.Equals(pathRoot, @"K:\",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "The configured authentic test authority must be K-local.");

        var directory = new DirectoryInfo(fullPath);
        if (!directory.Exists ||
            directory.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            !IsWorkspaceRoot(fullPath))
            throw new InvalidOperationException(
                "The configured authentic test authority is not an " +
                "ordinary ExampleWorkspace root.");
        return fullPath;
    }

    private static bool IsWorkspaceRoot(string candidate) =>
        File.Exists(Path.Combine(candidate, "AGENTS.md")) &&
        File.Exists(Path.Combine(candidate, "WORKSPACE_MANIFEST.json")) &&
        (File.Exists(Path.Combine(
            candidate,
            "projects",
            "NpcManagerReimplementation",
             "NpcManager.sln")) ||
         File.Exists(Path.Combine(
             candidate,
             "projects",
             "Emi2FreshBuild",
             "03-builds",
             "feasibility-probes",
             "ck-carrier-root",
             "Data",
             "EmiCarrierProbe.esp")));
}
