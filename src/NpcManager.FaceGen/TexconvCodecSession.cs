using System.Collections.Immutable;
using System.ComponentModel;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

/// <summary>
/// Owns the immutable inputs and private output tree for one DirectXTex call.
/// </summary>
/// <remarks>
/// The random, exclusively created tree and exact post-run enumeration detect
/// ordinary path substitution and unexpected child injection. A hostile
/// process already running as the same Windows user can observe command-line
/// paths and race an expected-name write; Windows does not provide process-
/// exclusive child creation without a separate token or ACL boundary. Such a
/// file must still pass no-follow identity, size, hash, and DDS validation.
/// </remarks>
internal sealed class TexconvCodecSession : IDisposable
{
    private readonly string diagnosticPrefix;
    private readonly string scratchParent;
    private readonly Dictionary<string, PinnedWindowsDirectory> ancestorPins =
        new(StringComparer.OrdinalIgnoreCase);
    private PinnedWindowsFile? executableFile;
    private PinnedWindowsFile? sourceFile;
    private PinnedWindowsFile? outputFile;
    private PinnedWindowsDirectory? scratchDirectory;
    private PinnedWindowsDirectory? outputDirectoryLease;
    private bool childStarted;
    private bool cleaned;

    private TexconvCodecSession(string diagnosticPrefix, string scratchParent)
    {
        this.diagnosticPrefix = diagnosticPrefix;
        this.scratchParent = scratchParent;
    }

    public string ExecutablePath => executableFile?.Path ?? string.Empty;
    public string SourcePath => sourceFile?.Path ?? string.Empty;
    public string OutputDirectory => outputDirectoryLease?.Path ?? string.Empty;
    public string OutputPath { get; private set; } = string.Empty;
    public byte[] SourceBytes { get; private set; } = [];
    public Sha256Hash SourceSha256 { get; private set; } = new(new string('0', 64));

    public static async ValueTask<TexconvSessionOpenResult> OpenAsync(
        WorkspacePath executablePath,
        WorkspacePath sourcePath,
        WorkspacePath labRoot,
        Sha256Hash expectedExecutableSha256,
        string diagnosticPrefix,
        long maximumSourceBytes,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!OperatingSystem.IsWindows())
        {
            diagnostics.Add(Error(diagnosticPrefix + "-platform",
                "The pinned DirectXTex codec path is supported only on Windows."));
            return new TexconvSessionOpenResult(null, diagnostics.ToImmutable());
        }
        if (!executablePath.IsUnder(labRoot))
        {
            diagnostics.Add(Error(diagnosticPrefix + "-tool-invalid",
                "The pinned DirectXTex executable must be under the K-only lab root."));
            return new TexconvSessionOpenResult(null, diagnostics.ToImmutable());
        }
        if (!sourcePath.IsUnder(labRoot))
        {
            diagnostics.Add(Error(diagnosticPrefix + "-source-invalid",
                "The source DDS must be under the K-only lab root."));
            return new TexconvSessionOpenResult(null, diagnostics.ToImmutable());
        }

        var session = new TexconvCodecSession(
            diagnosticPrefix,
            WindowsPinnedPath.CanonicalPath(labRoot.Value));
        try
        {
            if (!session.TryPinAncestors(executablePath.Value, "tool", diagnostics) ||
                !session.TryPinAncestors(sourcePath.Value, "source", diagnostics) ||
                !TryOpenInput(executablePath.Value, "tool", diagnosticPrefix,
                    out session.executableFile, diagnostics) ||
                !TryOpenInput(sourcePath.Value, "source", diagnosticPrefix,
                    out session.sourceFile, diagnostics))
                return Failed();

            if (await session.executableFile!.HashAsync(cancellationToken) != expectedExecutableSha256)
            {
                diagnostics.Add(Error(diagnosticPrefix + "-tool-hash-mismatch",
                    "The DirectXTex executable hash does not match the pinned manifest."));
                return Failed();
            }

            try
            {
                session.SourceBytes = await session.sourceFile!.ReadAllBytesAsync(
                    maximumSourceBytes, cancellationToken);
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(Error(diagnosticPrefix + "-source-size", exception.Message));
                return Failed();
            }
            session.SourceSha256 = new Sha256Hash(Convert.ToHexString(
                SHA256.HashData(session.SourceBytes)));

            if (!session.TryCreateScratch(diagnostics)) return Failed();
            return new TexconvSessionOpenResult(session, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            session.Cleanup();
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                            ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error(diagnosticPrefix + "-path-pin-failed", exception.Message));
            return Failed();
        }

        TexconvSessionOpenResult Failed()
        {
            diagnostics.AddRange(session.Cleanup().Diagnostics);
            return new TexconvSessionOpenResult(null, diagnostics.ToImmutable());
        }
    }

    public void MarkChildStarted() => childStarted = true;

    public ValueTask<Sha256Hash> HashExecutableAsync(CancellationToken cancellationToken) =>
        executableFile is null
            ? throw new InvalidOperationException("The DirectXTex executable lease is unavailable.")
            : executableFile.HashAsync(cancellationToken);

    public async ValueTask<TexconvOutputReadResult> ReadOutputAsync(
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!TryVerifyTree(expectOutput: true, diagnostics))
            return new TexconvOutputReadResult(null, diagnostics.ToImmutable());
        if (!WindowsPinnedPath.TryOpenFile(OutputPath, deletable: true, out outputFile,
                out var failure, out var error))
        {
            diagnostics.Add(Error(diagnosticPrefix + "-output-invalid",
                failure == PinnedPathFailure.ReparseOrWrongType
                    ? "The DirectXTex output is not an ordinary no-follow file."
                    : $"The DirectXTex output could not be identity-pinned: {error}"));
            return new TexconvOutputReadResult(null, diagnostics.ToImmutable());
        }

        try
        {
            var bytes = await outputFile!.ReadAllBytesAsync(maximumBytes, cancellationToken);
            if (!TryVerifyTree(expectOutput: true, diagnostics))
                return new TexconvOutputReadResult(null, diagnostics.ToImmutable());
            return new TexconvOutputReadResult(bytes, diagnostics.ToImmutable());
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error(diagnosticPrefix + "-output-size", exception.Message));
            return new TexconvOutputReadResult(null, diagnostics.ToImmutable());
        }
    }

    public TexconvCleanupResult Cleanup()
    {
        if (cleaned) return new TexconvCleanupResult(true, []);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            InspectForUnexpectedEntries(diagnostics);
            TryClaimExpectedOutputForCleanup(diagnostics);
            if (outputFile is not null)
            {
                if (!outputFile.TryDelete(out var error))
                    diagnostics.Add(Error(diagnosticPrefix + "-cleanup-output-failed",
                        $"The owned DirectXTex output was left untouched: {error}"));
                outputFile.Dispose();
                outputFile = null;
            }

            TryDeleteOwnedDirectory(ref outputDirectoryLease, "output directory", diagnostics);
            TryDeleteOwnedDirectory(ref scratchDirectory, "scratch directory", diagnostics);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error(diagnosticPrefix + "-cleanup-failed", exception.Message));
        }
        finally
        {
            outputFile?.Dispose();
            outputFile = null;
            sourceFile?.Dispose();
            sourceFile = null;
            executableFile?.Dispose();
            executableFile = null;
            outputDirectoryLease?.Dispose();
            outputDirectoryLease = null;
            scratchDirectory?.Dispose();
            scratchDirectory = null;
            foreach (var directory in ancestorPins.Values.Reverse()) directory.Dispose();
            ancestorPins.Clear();
            cleaned = true;
        }

        var complete = diagnostics.All(item => item.Severity != DiagnosticSeverity.Error);
        return new TexconvCleanupResult(complete, diagnostics.ToImmutable());
    }

    public void Dispose() => Cleanup();

    private bool TryPinAncestors(
        string filePath,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var parent = Path.GetDirectoryName(WindowsPinnedPath.CanonicalPath(filePath));
        if (string.IsNullOrWhiteSpace(parent))
        {
            diagnostics.Add(Error(diagnosticPrefix + "-" + role + "-invalid",
                $"The {role} path has no parent directory."));
            return false;
        }

        foreach (var directory in WindowsPinnedPath.ExpandDirectoryPath(parent))
        {
            if (ancestorPins.ContainsKey(directory)) continue;
            if (!WindowsPinnedPath.TryOpenDirectory(directory, owned: false, out var lease,
                    out var failure, out var error))
            {
                diagnostics.Add(Error(
                    failure == PinnedPathFailure.ReparseOrWrongType
                        ? diagnosticPrefix + "-reparse"
                        : diagnosticPrefix + "-path-pin-failed",
                    $"Could not pin {role} ancestor '{directory}': {error}"));
                return false;
            }
            ancestorPins.Add(directory, lease!);
        }
        return true;
    }

    private bool TryCreateScratch(ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!ancestorPins.ContainsKey(scratchParent))
        {
            diagnostics.Add(Error(diagnosticPrefix + "-scratch-parent-unpinned",
                "The K-local scratch parent was not present in the pinned input ancestry."));
            return false;
        }
        var scratchPath = Path.Combine(scratchParent,
            ".facetint-texconv-" + Guid.NewGuid().ToString("N"));
        if (!TryCreateOwnedDirectory(scratchPath, "scratch", out scratchDirectory, diagnostics))
            return false;
        var outputDirectory = Path.Combine(scratchPath, "output");
        if (!TryCreateOwnedDirectory(outputDirectory, "output", out outputDirectoryLease, diagnostics))
            return false;
        OutputPath = Path.Combine(outputDirectory, Path.GetFileName(SourcePath));
        return TryVerifyTree(expectOutput: false, diagnostics);
    }

    private bool TryCreateOwnedDirectory(
        string path,
        string role,
        out PinnedWindowsDirectory? lease,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        lease = null;
        if (!WindowsPinnedPath.TryCreateDirectoryExclusive(path, out var nativeError))
        {
            diagnostics.Add(Error(
                nativeError == WindowsPinnedPath.ErrorAlreadyExists
                    ? diagnosticPrefix + "-scratch-raced"
                    : diagnosticPrefix + "-scratch-create-failed",
                nativeError == WindowsPinnedPath.ErrorAlreadyExists
                    ? $"The private {role} path appeared before exclusive ownership was acquired."
                    : $"Could not create the private {role} directory: " +
                      new Win32Exception(nativeError).Message));
            return false;
        }
        if (WindowsPinnedPath.TryOpenDirectory(path, owned: true, out lease, out _, out var error))
            return true;
        diagnostics.Add(Error(diagnosticPrefix + "-scratch-pin-failed",
            $"The newly created {role} directory could not be pinned and was left untouched: {error}"));
        return false;
    }

    private bool TryVerifyTree(
        bool expectOutput,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (scratchDirectory is null || outputDirectoryLease is null) return false;
        var scratchEntries = Directory.EnumerateFileSystemEntries(scratchDirectory.Path).ToArray();
        var outputEntries = Directory.EnumerateFileSystemEntries(outputDirectoryLease.Path).ToArray();
        var scratchClean = scratchEntries.Length == 1 &&
            string.Equals(WindowsPinnedPath.CanonicalPath(scratchEntries[0]),
                WindowsPinnedPath.CanonicalPath(outputDirectoryLease.Path), StringComparison.OrdinalIgnoreCase);
        var outputClean = expectOutput
            ? outputEntries.Length == 1 && string.Equals(
                WindowsPinnedPath.CanonicalPath(outputEntries[0]),
                WindowsPinnedPath.CanonicalPath(OutputPath), StringComparison.OrdinalIgnoreCase)
            : outputEntries.Length == 0;
        if (scratchClean && outputClean) return true;

        diagnostics.Add(Error(diagnosticPrefix + "-scratch-contaminated",
            "The private DirectXTex tree contains a missing or undeclared entry."));
        return false;
    }

    private void InspectForUnexpectedEntries(ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (scratchDirectory is null || outputDirectoryLease is null) return;
        var scratchExtras = Directory.EnumerateFileSystemEntries(scratchDirectory.Path)
            .Where(path => !string.Equals(WindowsPinnedPath.CanonicalPath(path),
                WindowsPinnedPath.CanonicalPath(outputDirectoryLease.Path), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        var outputExtras = Directory.EnumerateFileSystemEntries(outputDirectoryLease.Path)
            .Where(path => !string.Equals(WindowsPinnedPath.CanonicalPath(path),
                WindowsPinnedPath.CanonicalPath(OutputPath), StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (scratchExtras.Length > 0 || outputExtras.Length > 0)
            diagnostics.Add(Error(diagnosticPrefix + "-scratch-contaminated",
                "Unexpected entries were detected in the private DirectXTex tree; only known owned entries were cleaned."));
    }

    private void TryClaimExpectedOutputForCleanup(ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (outputFile is not null || !childStarted || string.IsNullOrEmpty(OutputPath) || !File.Exists(OutputPath))
            return;
        if (!WindowsPinnedPath.TryOpenFile(OutputPath, deletable: true, out outputFile,
                out _, out var error))
        {
            diagnostics.Add(Error(diagnosticPrefix + "-cleanup-output-unclaimed",
                $"The expected child output was not an ordinary identity-pinned file and was left untouched: {error}"));
        }
    }

    private void TryDeleteOwnedDirectory(
        ref PinnedWindowsDirectory? directory,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (directory is null) return;
        if (!directory.TryDeleteIfEmpty(out var error))
            diagnostics.Add(Error(diagnosticPrefix + "-cleanup-directory-failed",
                $"The owned {role} was not removed because it was not empty or changed identity: {error}"));
        directory.Dispose();
        directory = null;
    }

    private static bool TryOpenInput(
        string path,
        string role,
        string diagnosticPrefix,
        out PinnedWindowsFile? file,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (WindowsPinnedPath.TryOpenFile(path, deletable: false, out file, out var failure, out var error))
            return true;
        diagnostics.Add(Error(
            failure == PinnedPathFailure.ReparseOrWrongType
                ? diagnosticPrefix + "-reparse"
                : diagnosticPrefix + "-" + role + "-invalid",
            $"The {role} file could not be opened as an immutable ordinary file: {error}"));
        return false;
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}

internal sealed record TexconvSessionOpenResult(
    TexconvCodecSession? Session,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed record TexconvOutputReadResult(
    byte[]? Bytes,
    ImmutableArray<Diagnostic> Diagnostics);

internal sealed record TexconvCleanupResult(
    bool Completed,
    ImmutableArray<Diagnostic> Diagnostics);
