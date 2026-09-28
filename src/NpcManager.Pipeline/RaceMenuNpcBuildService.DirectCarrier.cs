using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcBuildService
{
    private const long MaximumDirectCarrierBytes = 64L * 1024 * 1024;

    private async ValueTask<WorkspacePath?> MaterializeDirectCarrierAsync(
        RaceMenuDirectFaceGeomCarrierSelection selection,
        WorkspacePath stagingRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!selection.RequiresOwnedCopy)
            return selection.SourceNif;

        byte[]? bytes = await ReadHashBoundOrdinaryFileAsync(
            selection.SourceNif,
            selection.SourceSha256,
            MaximumDirectCarrierBytes,
            "racemenu-build-manager-carrier",
            "Manager-owned complete CharGen carrier",
            diagnostics,
            cancellationToken);
        if (bytes is null || HasErrors(diagnostics))
            return null;

        var destination = new WorkspacePath(Path.Combine(
            stagingRoot.Value,
            "manager-qualified-complete-carrier.nif"));
        diagnostics.AddRange(WorkspacePolicy.Evaluate(LaboratoryRoot, destination));
        if (!destination.IsUnder(stagingRoot) ||
            File.Exists(destination.Value) ||
            Directory.Exists(destination.Value) ||
            HasErrors(diagnostics))
        {
            diagnostics.Add(Error(
                "racemenu-build-manager-carrier-output",
                "The owned complete-carrier staging destination is invalid or already exists."));
            return null;
        }

        await using (var stream = new FileStream(
            destination.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            stream.Flush(flushToDisk: true);
        }

        Sha256Hash outputHash;
        await using (var stream = new FileStream(
            destination.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            outputHash = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false)));
        }
        if (outputHash != selection.SourceSha256)
        {
            diagnostics.Add(Error(
                "racemenu-build-manager-carrier-copy-hash",
                $"The owned complete-carrier copy hash {outputHash} does not match {selection.SourceSha256}."));
            return null;
        }

        diagnostics.Add(new Diagnostic(
            "racemenu-build-manager-carrier-selected",
            DiagnosticSeverity.Info,
            "The Manager's independently reopened complete CharGen NIF is the direct-build carrier; the unrelated template-provider carrier remains template authority only."));
        return destination;
    }
}
