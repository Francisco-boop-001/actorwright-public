using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimFollowerFinishService
{
    private static readonly ImmutableArray<string> RawGroupTreeSurface =
    [
        "TES4",
        "GRUP/NPC_",
        "GRUP/PACK",
        "GRUP/WRLD/CELL/REFR",
        "GRUP/WRLD/CELL/ACHR"
    ];

    public async ValueTask<SkyrimFollowerFinishProposalResult> AnalyzeAsync(
        SkyrimFollowerFinishRequest request,
        Sha256Hash verifiedRequestFileSha256,
        WorkspacePath proposalOutput,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool proposalCreated = false;
        bool proposalRetained = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ImmutableArray<Diagnostic> outputDiagnostics =
                ValidateAnalyzeOutputs(request, proposalOutput);
            if (outputDiagnostics.Any(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error))
                return Refused(outputDiagnostics);

            ImmutableArray<Diagnostic> authorityDiagnostics =
                await VerifyExternalAuthoritiesAsync(
                    request,
                    cancellationToken);
            if (authorityDiagnostics.Any(diagnostic =>
                    diagnostic.Severity == DiagnosticSeverity.Error))
                return Refused(authorityDiagnostics);

            SkyrimFollowerFinishPluginSnapshot sourceSnapshot =
                await inspectSource(request, cancellationToken);
            if (!sourceSnapshot.Valid)
                return Refused(sourceSnapshot.Diagnostics);

            byte[] requestBytes = SerializeRequest(request);
            var proposal = new SkyrimFollowerFinishProposal(
                1,
                request.Operation,
                verifiedRequestFileSha256,
                request,
                sourceSnapshot,
                request.AllowedExistingRecordChanges,
                request.AllowedNewRecords,
                request.Allocation.NextFormId,
                RawGroupTreeSurface,
                request.AllowedPackageFiles,
                false);
            byte[] proposalBytes = SerializeProposal(proposal);
            var proposalSha256 = new Sha256Hash(
                Convert.ToHexString(SHA256.HashData(proposalBytes)));

            await using (var output = new FileStream(
                             proposalOutput.Value,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous |
                             FileOptions.SequentialScan))
            {
                proposalCreated = true;
                await output.WriteAsync(
                    proposalBytes,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            SkyrimFollowerFinishProposalLoadResult reopened =
                await requestLoader.LoadProposalAsync(
                    proposalOutput,
                    proposalSha256,
                    cancellationToken);
            if (!reopened.Loaded ||
                reopened.Proposal is null ||
                reopened.ActualSha256 != proposalSha256 ||
                reopened.ByteLength != proposalBytes.LongLength)
            {
                DeleteOwnedProposal(proposalOutput, proposalCreated);
                proposalCreated = false;
                return Refused(
                    reopened.Diagnostics.Add(
                        new Diagnostic(
                            "follower-finish-proposal-reopen-refused",
                            DiagnosticSeverity.Error,
                            "The persisted proposal did not pass strict hash-bound reopen.")));
            }

            byte[] reopenedBytes = SerializeProposal(reopened.Proposal);
            if (!proposalBytes.AsSpan().SequenceEqual(reopenedBytes) ||
                reopened.Proposal.RequestSha256 !=
                    verifiedRequestFileSha256 ||
                !requestBytes.AsSpan().SequenceEqual(
                    SerializeRequest(reopened.Proposal.Request)))
            {
                DeleteOwnedProposal(proposalOutput, proposalCreated);
                proposalCreated = false;
                return Refused(
                    Refusal(
                        "follower-finish-proposal-canonical-reopen-refused",
                        "The reopened proposal was not byte-identical under canonical serialization."));
            }

            proposalRetained = true;
            return new SkyrimFollowerFinishProposalResult(
                true,
                reopened.Proposal,
                proposalOutput,
                proposalSha256,
                [
                    new Diagnostic(
                        "follower-finish-proposal-persisted",
                        DiagnosticSeverity.Info,
                        "The analyze-only proposal was canonically persisted, hash-bound, and independently reopened.")
                ]);
        }
        catch (OperationCanceledException)
        {
            DeleteOwnedProposal(proposalOutput, proposalCreated);
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            DeleteOwnedProposal(proposalOutput, proposalCreated);
            return Refused(
                Refusal(
                    "follower-finish-proposal-security-refused",
                    exception.Message));
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                ArgumentException or CryptographicException)
        {
            DeleteOwnedProposal(proposalOutput, proposalCreated);
            return Refused(
                Refusal(
                    "follower-finish-proposal-invalid",
                    exception.Message));
        }
        finally
        {
            if (proposalCreated && !proposalRetained)
                DeleteOwnedProposal(proposalOutput, proposalCreated);
        }
    }

    private ImmutableArray<Diagnostic> ValidateAnalyzeOutputs(
        SkyrimFollowerFinishRequest request,
        WorkspacePath proposalOutput)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string? proposalParent =
            Path.GetDirectoryName(proposalOutput.Value);
        if (proposalParent is null || !Directory.Exists(proposalParent))
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-proposal-parent-missing",
                    DiagnosticSeverity.Error,
                    "The proposal parent directory must already exist."));
        }
        else
        {
            diagnostics.AddRange(
                policy.Evaluate(
                    workspaceRoot,
                    new WorkspacePath(proposalParent)));
        }

        if (File.Exists(proposalOutput.Value) ||
            Directory.Exists(proposalOutput.Value))
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-proposal-collision",
                    DiagnosticSeverity.Error,
                    "The proposal output already exists; analyze never overwrites."));
        }

        if (File.Exists(request.OutputRoot.Value) ||
            Directory.Exists(request.OutputRoot.Value) ||
            File.Exists(request.OutputZip.Value) ||
            Directory.Exists(request.OutputZip.Value))
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-output-collision",
                    DiagnosticSeverity.Error,
                    "A requested package or archive output already exists; analyze refuses stale transaction state."));
        }

        var transactionPaths = new List<WorkspacePath>
        {
            request.Source.Zip,
            request.Source.PackageManifest,
            request.OutputRoot,
            request.OutputZip
        };
        if (request.ExternalAuthorities is not null)
        {
            transactionPaths.Add(
                request.ExternalAuthorities.PlacementEvidence.Path);
            transactionPaths.AddRange(
                request.ExternalAuthorities.Providers.Select(provider =>
                    provider.Path));
        }

        if (transactionPaths.Any(path =>
                IsSameOrDescendant(proposalOutput, path) ||
                IsSameOrDescendant(path, proposalOutput)))
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-proposal-path-overlap",
                    DiagnosticSeverity.Error,
                    "The proposal path must be disjoint from source, external-authority, and transaction output paths."));
        }

        for (int first = 0; first < transactionPaths.Count; first++)
        {
            for (int second = first + 1;
                 second < transactionPaths.Count;
                 second++)
            {
                if (IsSameOrDescendant(
                        transactionPaths[first],
                        transactionPaths[second]) ||
                    IsSameOrDescendant(
                        transactionPaths[second],
                        transactionPaths[first]))
                {
                    diagnostics.Add(
                        new Diagnostic(
                            "follower-finish-external-authority-path-overlap",
                            DiagnosticSeverity.Error,
                            "Source, external-authority, and transaction output paths must be pairwise disjoint."));
                    first = transactionPaths.Count;
                    break;
                }
            }
        }

        return diagnostics.ToImmutable();
    }

    private async ValueTask<ImmutableArray<Diagnostic>>
        VerifyExternalAuthoritiesAsync(
            SkyrimFollowerFinishRequest request,
            CancellationToken cancellationToken)
    {
        if (request.ExternalAuthorities is null)
        {
            return Refusal(
                "follower-finish-external-authority-invalid",
                "The follower-finish request must bind external authorities.");
        }

        SkyrimFollowerFinishExternalAuthorities authorities;
        try
        {
            var placement = new SkyrimFollowerFinishFileAuthority(
                request.ExternalAuthorities.PlacementEvidence.Path,
                request.ExternalAuthorities.PlacementEvidence.ByteLength,
                request.ExternalAuthorities.PlacementEvidence.Sha256);
            ImmutableArray<SkyrimFollowerFinishPluginProviderAuthority>
                providers = request.ExternalAuthorities.Providers
                    .Select(provider =>
                        new SkyrimFollowerFinishPluginProviderAuthority(
                            provider.Plugin,
                            provider.Path,
                            provider.ByteLength,
                            provider.Sha256))
                    .ToImmutableArray();
            authorities = new SkyrimFollowerFinishExternalAuthorities(
                placement,
                providers);
            if (!authorities.Providers.SequenceEqual(
                    request.ExternalAuthorities.Providers))
            {
                return Refusal(
                    "follower-finish-external-authority-order",
                    "External provider authority must use canonical plugin/path order.");
            }
        }
        catch (Exception exception) when (
            exception is ArgumentException or InvalidOperationException)
        {
            return Refusal(
                "follower-finish-external-authority-invalid",
                exception.Message);
        }

        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(
            await VerifyExternalAuthorityFileAsync(
                authorities.PlacementEvidence.Path,
                authorities.PlacementEvidence.ByteLength,
                authorities.PlacementEvidence.Sha256,
                "placement evidence",
                cancellationToken));
        foreach (SkyrimFollowerFinishPluginProviderAuthority provider in
                 authorities.Providers)
        {
            diagnostics.AddRange(
                await VerifyExternalAuthorityFileAsync(
                    provider.Path,
                    provider.ByteLength,
                    provider.Sha256,
                    $"provider {provider.Plugin.Value}",
                    cancellationToken));
        }
        return diagnostics.ToImmutable();
    }

    private async ValueTask<ImmutableArray<Diagnostic>>
        VerifyExternalAuthorityFileAsync(
            WorkspacePath path,
            long expectedByteLength,
            Sha256Hash expectedSha256,
            string role,
            CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic> policyDiagnostics =
            policy.EvaluateReadRoot(workspaceRoot, path);
        if (policyDiagnostics.Any(diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error))
            return policyDiagnostics;

        try
        {
            await using var stream = new FileStream(
                path.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan);
            if (stream.Length != expectedByteLength)
            {
                return Refusal(
                    "follower-finish-external-authority-length",
                    $"The {role} byte length {stream.Length} does not match {expectedByteLength}.");
            }

            byte[] digest = await SHA256.HashDataAsync(
                stream,
                cancellationToken);
            var actualSha256 = new Sha256Hash(
                Convert.ToHexString(digest));
            if (actualSha256 != expectedSha256)
            {
                return Refusal(
                    "follower-finish-external-authority-hash",
                    $"The {role} SHA-256 {actualSha256} does not match {expectedSha256}.");
            }

            return
            [
                new Diagnostic(
                    "follower-finish-external-authority-verified",
                    DiagnosticSeverity.Info,
                    $"The {role} was verified from one exact K-local file stream.")
            ];
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or
                DirectoryNotFoundException)
        {
            return Refusal(
                "follower-finish-external-authority-missing",
                $"The {role} file is missing: {exception.Message}");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                CryptographicException)
        {
            return Refusal(
                "follower-finish-external-authority-read",
                $"The {role} file could not be verified: {exception.Message}");
        }
    }

    private static SkyrimFollowerFinishProposalResult Refused(
        ImmutableArray<Diagnostic> diagnostics) =>
        new(false, null, null, null, diagnostics);

    private static bool IsSameOrDescendant(
        WorkspacePath candidate,
        WorkspacePath ancestor)
    {
        string relative = Path.GetRelativePath(
            ancestor.Value,
            candidate.Value);
        return relative is "." ||
               (!relative.StartsWith(
                    ".." + Path.DirectorySeparatorChar,
                    StringComparison.Ordinal) &&
                !Path.IsPathRooted(relative));
    }

    private void DeleteOwnedProposal(
        WorkspacePath proposalOutput,
        bool proposalCreated)
    {
        if (!proposalCreated ||
            !proposalOutput.IsUnder(workspaceRoot) ||
            !File.Exists(proposalOutput.Value))
            return;
        File.Delete(proposalOutput.Value);
    }
}
