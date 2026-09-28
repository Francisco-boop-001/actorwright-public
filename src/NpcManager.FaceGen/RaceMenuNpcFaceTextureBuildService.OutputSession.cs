using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Holds the output namespace and every created file identity for one schema-5
/// face-texture transaction.
/// </summary>
internal sealed class RaceMenuFaceTextureOutputSession : IDisposable
{
    private readonly string stagingRoot;
    private readonly HashSet<string> allowedOutputs;
    private readonly Dictionary<string, PinnedWindowsDirectory> directoryPins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, PinnedWindowsOutputFile> outputs =
        new(StringComparer.OrdinalIgnoreCase);
    private FaceTextureOutputSessionCloseResult? closeResult;

    private RaceMenuFaceTextureOutputSession(
        string stagingRoot,
        IEnumerable<string> allowedOutputs)
    {
        this.stagingRoot = WindowsPinnedPath.CanonicalPath(stagingRoot);
        this.allowedOutputs = allowedOutputs
            .Select(WindowsPinnedPath.CanonicalPath)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    public static FaceTextureOutputSessionOpenResult Open(
        RaceMenuNpcFaceTextureBuildRequest request,
        WorkspacePath labRoot)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!OperatingSystem.IsWindows())
        {
            diagnostics.Add(Error("face-texture-output-platform",
                "Identity-pinned face-texture output transactions require Windows."));
            return new FaceTextureOutputSessionOpenResult(null, diagnostics.ToImmutable());
        }

        WorkspacePath[] requestedOutputs =
        [
            request.FaceTintOutput,
            request.PrivateDiffuseOutput,
            request.EvidenceOutput
        ];
        var session = new RaceMenuFaceTextureOutputSession(
            request.StagingRoot.Value,
            requestedOutputs.Select(item => item.Value));
        try
        {
            if (!request.StagingRoot.IsUnder(labRoot) ||
                requestedOutputs.Any(output =>
                    !output.IsUnder(request.StagingRoot) || !output.IsUnder(labRoot)))
            {
                diagnostics.Add(Error("face-texture-output-boundary",
                    "The output transaction must stay inside its K-local staging root."));
                return Failed();
            }

            if (!session.TryPinDirectoryTree(request.StagingRoot.Value,
                    "staging root", diagnostics))
                return Failed();
            foreach (string parent in requestedOutputs
                         .Select(output => Path.GetDirectoryName(output.Value))
                         .Where(parent => !string.IsNullOrWhiteSpace(parent))
                         .Cast<string>()
                         .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!session.TryPinDirectoryTree(parent, "output parent", diagnostics))
                    return Failed();
            }

            if (!session.TryVerifyDirectoryPins(diagnostics)) return Failed();
            foreach (WorkspacePath output in requestedOutputs)
            {
                if (File.Exists(output.Value) || Directory.Exists(output.Value))
                {
                    diagnostics.Add(Error("face-texture-output-raced",
                        $"The new output path appeared before exclusive creation: '{output.Value}'."));
                }
            }
            if (HasErrors(diagnostics)) return Failed();
            return new FaceTextureOutputSessionOpenResult(session, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                            ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error("face-texture-output-pin-failed", exception.Message));
            return Failed();
        }

        FaceTextureOutputSessionOpenResult Failed()
        {
            session.ReleasePinsAndOutputs();
            return new FaceTextureOutputSessionOpenResult(null, diagnostics.ToImmutable());
        }
    }

    public async ValueTask<bool> TryWriteNewAsync(
        WorkspacePath destination,
        ReadOnlyMemory<byte> bytes,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(closeResult is not null, this);
        string canonical = WindowsPinnedPath.CanonicalPath(destination.Value);
        if (!allowedOutputs.Contains(canonical) ||
            !WindowsPinnedPath.IsSameOrUnder(canonical, stagingRoot))
        {
            diagnostics.Add(Error("face-texture-output-unowned",
                $"The transaction refused undeclared output '{destination.Value}'."));
            return false;
        }
        if (outputs.ContainsKey(canonical))
        {
            diagnostics.Add(Error("face-texture-output-duplicate-write",
                $"The transaction refused a second write to '{destination.Value}'."));
            return false;
        }
        if (!TryVerifyDirectoryPins(diagnostics)) return false;

        if (!WindowsPinnedPath.TryCreateOutputFileExclusive(canonical, out var output,
                out var failure, out var error) || output is null)
        {
            diagnostics.Add(Error(
                failure == PinnedPathFailure.ReparseOrWrongType
                    ? "face-texture-output-reparse"
                    : "face-texture-output-create-failed",
                $"The declared output could not be exclusively created and pinned: {error}"));
            return false;
        }
        outputs.Add(canonical, output);

        PinnedOutputWriteResult write = await output.WriteAndSealAsync(bytes, cancellationToken);
        if (!write.Completed)
        {
            diagnostics.Add(Error("face-texture-output-seal-failed",
                $"The output identity could not be durably sealed: {write.Error}"));
            return false;
        }
        return TryVerifyDirectoryPins(diagnostics);
    }

    public async ValueTask<byte[]> ReadOwnedBytesAsync(
        WorkspacePath source,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(closeResult is not null, this);
        string canonical = WindowsPinnedPath.CanonicalPath(source.Value);
        if (!outputs.TryGetValue(canonical, out var output))
            throw new InvalidOperationException(
                $"The output transaction does not own '{source.Value}'.");
        return await output.ReadAllBytesAsync(maximumBytes, cancellationToken);
    }

    public FaceTextureOutputSessionCloseResult Cleanup()
    {
        if (closeResult is not null) return closeResult;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach ((string path, PinnedWindowsOutputFile output) in outputs.Reverse())
        {
            if (!output.TryDelete(out var error))
            {
                diagnostics.Add(Error("face-texture-output-cleanup-failed",
                    $"Failed to delete owned output identity '{path}': {error}"));
            }
            if (File.Exists(path) || Directory.Exists(path))
            {
                diagnostics.Add(Error("face-texture-output-cleanup-incomplete",
                    $"The output name '{path}' remains occupied after identity-bound cleanup; no path-based deletion was attempted."));
            }
        }

        ReleasePinsAndOutputs();
        closeResult = new FaceTextureOutputSessionCloseResult(
            !HasErrors(diagnostics), diagnostics.ToImmutable());
        return closeResult;
    }

    public FaceTextureOutputSessionCloseResult Retain()
    {
        if (closeResult is not null) return closeResult;
        ReleasePinsAndOutputs();
        closeResult = new FaceTextureOutputSessionCloseResult(true, []);
        return closeResult;
    }

    public void Dispose() => Cleanup();

    private bool TryPinDirectoryTree(
        string directory,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (string ancestor in WindowsPinnedPath.ExpandDirectoryPath(directory))
        {
            if (directoryPins.ContainsKey(ancestor)) continue;
            if (!WindowsPinnedPath.TryOpenDirectory(ancestor, owned: false,
                    out var lease, out var failure, out var error) || lease is null)
            {
                diagnostics.Add(Error(
                    failure == PinnedPathFailure.ReparseOrWrongType
                        ? "face-texture-output-reparse"
                        : "face-texture-output-pin-failed",
                    $"Could not identity-pin {role} ancestor '{ancestor}': {error}"));
                return false;
            }
            directoryPins.Add(ancestor, lease);
        }
        return true;
    }

    private bool TryVerifyDirectoryPins(ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        foreach (PinnedWindowsDirectory directory in directoryPins.Values)
        {
            if (directory.TryVerifyIdentityAndPath(out var error)) continue;
            diagnostics.Add(Error("face-texture-output-identity-drift",
                $"Pinned output ancestor '{directory.Path}' changed identity: {error}"));
            return false;
        }
        return true;
    }

    private void ReleasePinsAndOutputs()
    {
        foreach (PinnedWindowsOutputFile output in outputs.Values.Reverse()) output.Dispose();
        outputs.Clear();
        foreach (PinnedWindowsDirectory directory in directoryPins.Values.Reverse()) directory.Dispose();
        directoryPins.Clear();
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}

internal sealed record FaceTextureOutputSessionOpenResult(
    RaceMenuFaceTextureOutputSession? Session,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed record FaceTextureOutputSessionCloseResult(
    bool Completed,
    ImmutableArray<Diagnostic> Diagnostics);
