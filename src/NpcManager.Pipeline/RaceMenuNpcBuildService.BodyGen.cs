using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcBuildService
{
    private async ValueTask<BodyGenBuildResult> RebindAndVerifyRetainedBodyGenAsync(
        BodyGenBuildResult staged,
        WorkspacePath finalOutputRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var bodyGenDiagnostics = staged.Diagnostics.IsDefault
            ? ImmutableArray.CreateBuilder<Diagnostic>()
            : staged.Diagnostics.ToBuilder();
        var declaredFiles = staged.Files.IsDefault
            ? ImmutableArray<BodyGenFileArtifact>.Empty
            : staged.Files;
        var retained = ImmutableArray.CreateBuilder<BodyGenFileArtifact>(declaredFiles.Length);
        var relativePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var failed = false;
        var finalDataRoot = new WorkspacePath(Path.Combine(finalOutputRoot.Value, "Data"));

        if (declaredFiles.IsEmpty)
        {
            AddFailure("racemenu-build-bodygen-retained-empty",
                "A completed BodyGen transaction did not declare any retained files.");
        }

        foreach (var stagedFile in declaredFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(stagedFile.RelativePath.Value) ||
                stagedFile.ByteLength <= 0)
            {
                AddFailure("racemenu-build-bodygen-retained-invalid",
                    "A retained BodyGen declaration requires a non-empty relative path and positive byte length.");
                continue;
            }
            if (!relativePaths.Add(stagedFile.RelativePath.Value))
            {
                AddFailure("racemenu-build-bodygen-retained-duplicate",
                    $"BodyGen retained path '{stagedFile.RelativePath.Value}' was declared more than once.");
                continue;
            }

            var retainedPath = new WorkspacePath(Path.Combine(
                finalDataRoot.Value,
                stagedFile.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!retainedPath.IsUnder(finalDataRoot) || retainedPath == finalDataRoot)
            {
                AddFailure("racemenu-build-bodygen-retained-outside-data",
                    $"BodyGen retained path '{stagedFile.RelativePath.Value}' escaped the final Data root.");
                continue;
            }

            var pathDiagnostics = WorkspacePolicy.EvaluateReadRoot(
                LaboratoryRoot,
                retainedPath);
            diagnostics.AddRange(pathDiagnostics);
            bodyGenDiagnostics.AddRange(pathDiagnostics);
            if (pathDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            {
                failed = true;
                continue;
            }

            try
            {
                await using var stream = new FileStream(retainedPath.Value,
                    new FileStreamOptions
                    {
                        Mode = FileMode.Open,
                        Access = FileAccess.Read,
                        Share = FileShare.Read,
                        BufferSize = 64 * 1024,
                        Options = FileOptions.Asynchronous | FileOptions.SequentialScan
                    });
                if (!TryVerifyRaceMenuInputFinalPath(stream.SafeFileHandle,
                        retainedPath.Value, out var finalPathError))
                {
                    AddFailure("racemenu-build-bodygen-retained-identity",
                        $"BodyGen retained file '{stagedFile.RelativePath.Value}' could not be identity-pinned to its final package path: {finalPathError}");
                    continue;
                }

                if (File.GetAttributes(retainedPath.Value)
                    .HasFlag(FileAttributes.ReparsePoint))
                {
                    AddFailure("racemenu-build-bodygen-retained-reparse",
                        $"BodyGen retained file '{stagedFile.RelativePath.Value}' is a reparse point.");
                    continue;
                }

                var length = stream.Length;
                if (length != stagedFile.ByteLength)
                {
                    AddFailure("racemenu-build-bodygen-retained-length",
                        $"BodyGen retained file '{stagedFile.RelativePath.Value}' has {length} bytes; expected {stagedFile.ByteLength}.");
                    continue;
                }

                var sha256 = new Sha256Hash(Convert.ToHexString(
                    await SHA256.HashDataAsync(stream, cancellationToken)));
                if (sha256 != stagedFile.Sha256)
                {
                    AddFailure("racemenu-build-bodygen-retained-hash",
                        $"BodyGen retained file '{stagedFile.RelativePath.Value}' has SHA-256 {sha256}; expected {stagedFile.Sha256}.");
                    continue;
                }

                retained.Add(stagedFile with { AbsolutePath = retainedPath });
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               ArgumentException or
                                               NotSupportedException)
            {
                AddFailure("racemenu-build-bodygen-retained-read",
                    $"BodyGen retained file '{stagedFile.RelativePath.Value}' could not be independently reopened: {exception.Message}");
            }
        }

        if (failed || retained.Count != declaredFiles.Length)
        {
            return staged with
            {
                Written = false,
                Files = ImmutableArray<BodyGenFileArtifact>.Empty,
                Diagnostics = bodyGenDiagnostics.ToImmutable()
            };
        }

        var verified = new Diagnostic(
            "racemenu-build-bodygen-retained-verified",
            DiagnosticSeverity.Info,
            $"Independently reopened {retained.Count} retained BodyGen files under the final package Data root after staging cleanup.");
        diagnostics.Add(verified);
        bodyGenDiagnostics.Add(verified);
        return staged with
        {
            Files = retained.ToImmutable(),
            Diagnostics = bodyGenDiagnostics.ToImmutable()
        };

        void AddFailure(string code, string message)
        {
            var diagnostic = Error(code, message);
            diagnostics.Add(diagnostic);
            bodyGenDiagnostics.Add(diagnostic);
            failed = true;
        }
    }
}
