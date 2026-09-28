using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class NpcCreationService
{
    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));

    private static PluginName ParseOutputPlugin(
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try { return new PluginName(Path.GetFileName(output.Value)); }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("npc-create-output-plugin-name", exception.Message));
            return new PluginName("Refused.esp");
        }
    }

    private static Sha256Hash HashIfPresent(
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!File.Exists(path.Value)) return ZeroHash;
        try { return HashFile(path.Value); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error("npc-create-hash-failed", $"{path.Value}: {exception.Message}"));
            return ZeroHash;
        }
    }

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private static void AddUnsafePathDiagnostic(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (path.StartsWith("\\\\?\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\.\\", StringComparison.Ordinal) ||
            path.StartsWith("\\\\", StringComparison.Ordinal))
            diagnostics.Add(Error("npc-create-unsafe-path", $"The {role} uses a device or UNC path."));
        if (path.IndexOf(':', 2) >= 0)
            diagnostics.Add(Error("npc-create-alternate-data-stream", $"The {role} contains an alternate-data-stream delimiter."));
    }

    private static void AddReparseDiagnostic(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("npc-create-reparse-point",
                        $"The {role} traverses the reparse point '{current}'."));
                    return;
                }
            }
            catch (FileNotFoundException)
            {
                // An output leaf may not exist yet; inspect every existing parent.
            }
            catch (DirectoryNotFoundException)
            {
                // An output leaf may not exist yet; inspect every existing parent.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-create-path-inspection-failed",
                    $"The {role} could not be inspected at '{current}': {exception.Message}"));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return;
            current = parent;
        }
    }

    private static PathAncestrySnapshot CaptureExistingAncestry(
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string role)
    {
        var entries = ImmutableArray.CreateBuilder<PathAncestryEntry>();
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                var attributes = File.GetAttributes(current);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("npc-create-reparse-point",
                        $"The {role} traverses the reparse point '{current}'."));
                    break;
                }
                entries.Add(new PathAncestryEntry(
                    current,
                    attributes,
                    File.GetCreationTimeUtc(current)));
            }
            catch (FileNotFoundException)
            {
                // Keep walking to capture the nearest existing parent.
            }
            catch (DirectoryNotFoundException)
            {
                // Keep walking to capture the nearest existing parent.
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-create-path-snapshot-failed",
                    $"The {role} ancestry could not be captured at '{current}': {exception.Message}"));
                break;
            }

            var parent = Directory.GetParent(current)?.FullName;
            if (parent is null || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent;
        }
        return new PathAncestrySnapshot(entries.ToImmutable());
    }

    private bool ValidateCleanupPath(
        string path,
        PathAncestrySnapshot ancestry,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string role)
    {
        WorkspacePath candidate;
        try
        {
            candidate = new WorkspacePath(path);
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("npc-create-rollback-path-invalid",
                $"The {role} rollback path is invalid: {exception.Message}"));
            return false;
        }

        if (!candidate.IsUnder(labRoot) || candidate == labRoot)
        {
            diagnostics.Add(Error("npc-create-rollback-outside-lab",
                $"Refused to remove the {role} outside the owned K-local output path."));
            return false;
        }

        var policyDiagnostics = policy.Evaluate(labRoot, candidate);
        diagnostics.AddRange(policyDiagnostics);
        if (policyDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return false;

        foreach (var entry in ancestry.Entries)
        {
            try
            {
                var currentAttributes = File.GetAttributes(entry.Path);
                var currentCreationTime = File.GetCreationTimeUtc(entry.Path);
                if (currentAttributes != entry.Attributes || currentCreationTime != entry.CreationTimeUtc)
                {
                    diagnostics.Add(Error("npc-create-rollback-ancestry-changed",
                        $"Refused to remove the {role} because ancestor '{entry.Path}' changed after admission."));
                    return false;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-create-rollback-ancestry-unavailable",
                    $"Refused to remove the {role} because ancestor '{entry.Path}' could not be rechecked: {exception.Message}"));
                return false;
            }
        }
        return true;
    }

    private RollbackOutcome RollbackApply(
        NpcCreationRequest request,
        string temporaryDirectory,
        bool promoted,
        PathAncestrySnapshot outputParentAncestry)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (promoted && File.Exists(request.Output.Value) &&
            ValidateCleanupPath(request.Output.Value, outputParentAncestry, diagnostics, "promoted plugin"))
        {
            try
            {
                File.Delete(request.Output.Value);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-create-rollback-plugin-failed",
                    $"Could not remove the promoted plugin '{request.Output.Value}': {exception.Message}"));
            }
        }

        diagnostics.AddRange(CleanupTemporaryDirectory(temporaryDirectory, outputParentAncestry));

        Sha256Hash? remainingOutputHash = null;
        if (File.Exists(request.Output.Value))
        {
            try { remainingOutputHash = HashFile(request.Output.Value); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-create-rollback-output-hash-failed",
                    $"The remaining promoted plugin could not be hashed: {exception.Message}"));
            }
            diagnostics.Add(Error("npc-create-rollback-incomplete",
                $"Creation failed, but the game-facing plugin remains at '{request.Output.Value}'."));
        }
        return new RollbackOutcome(
            remainingOutputHash,
            !File.Exists(request.Output.Value) && !Directory.Exists(temporaryDirectory) && !File.Exists(temporaryDirectory),
            diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> CleanupTemporaryDirectory(
        string temporaryDirectory,
        PathAncestrySnapshot outputParentAncestry)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (Directory.Exists(temporaryDirectory) &&
            ValidateCleanupPath(temporaryDirectory, outputParentAncestry, diagnostics, "temporary directory") &&
            ValidateTreeContainsNoReparsePoints(temporaryDirectory, diagnostics))
        {
            try
            {
                Directory.Delete(temporaryDirectory, recursive: true);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-create-rollback-temporary-failed",
                    $"Could not remove the temporary directory '{temporaryDirectory}': {exception.Message}"));
            }
        }
        if (Directory.Exists(temporaryDirectory) || File.Exists(temporaryDirectory))
        {
            diagnostics.Add(Error("npc-create-rollback-incomplete",
                $"The temporary write root remains at '{temporaryDirectory}'."));
        }
        return diagnostics.ToImmutable();
    }

    private static bool ValidateTreeContainsNoReparsePoints(
        string root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            IEnumerable<string> children;
            try
            {
                children = Directory.EnumerateFileSystemEntries(directory).ToArray();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-create-rollback-tree-inspection-failed",
                    $"Refused recursive cleanup because '{directory}' could not be enumerated: {exception.Message}"));
                return false;
            }

            foreach (var child in children)
            {
                try
                {
                    var attributes = File.GetAttributes(child);
                    if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add(Error("npc-create-rollback-tree-reparse",
                            $"Refused recursive cleanup because the owned tree contains reparse point '{child}'."));
                        return false;
                    }
                    if (attributes.HasFlag(FileAttributes.Directory)) pending.Push(child);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    diagnostics.Add(Error("npc-create-rollback-tree-inspection-failed",
                        $"Refused recursive cleanup because '{child}' could not be inspected: {exception.Message}"));
                    return false;
                }
            }
        }
        return true;
    }

    private static NpcCreationResult Refused(
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        NpcCreationVerificationResult? verification,
        ImmutableArray<Diagnostic> diagnostics,
        Sha256Hash? remainingOutputHash = null) => new(
            false,
            request.Proposal,
            proposal.ProposalHash,
            request.Output,
            remainingOutputHash,
            proposal.AllocatedFormId,
            proposal.Masters,
            verification,
            diagnostics);

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDeleteTransient(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record PathAncestryEntry(
        string Path,
        FileAttributes Attributes,
        DateTime CreationTimeUtc);

    private sealed record PathAncestrySnapshot(ImmutableArray<PathAncestryEntry> Entries);

    private sealed record RollbackOutcome(
        Sha256Hash? RemainingOutputHash,
        bool Complete,
        ImmutableArray<Diagnostic> Diagnostics);
}
