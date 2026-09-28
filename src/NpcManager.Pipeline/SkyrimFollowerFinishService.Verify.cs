using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimFollowerFinishService
{
    private partial async ValueTask<
        SkyrimFollowerFinishVerificationResult>
        VerifyTransactionAsync(
            SkyrimFollowerFinishRequest request,
            Sha256Hash verifiedRequestFileSha256,
            SkyrimFollowerFinishProposal proposal,
            Sha256Hash expectedProposalSha256,
            WorkspacePath manifest,
            CancellationToken cancellationToken)
    {
        if (pluginService is null)
        {
            return new SkyrimFollowerFinishVerificationResult(
                false,
                null,
                null,
                Refusal(
                    "follower-finish-plugin-service-unavailable",
                    "This follower-finish service was constructed for analysis only; transaction verification is unavailable."));
        }

        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        string verificationScratch = string.Empty;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateFollowerFinishApprovalAsync(
                request,
                verifiedRequestFileSha256,
                proposal,
                expectedProposalSha256,
                inspectApprovedSource: false,
                cancellationToken);
            string expectedManifest = Path.GetFullPath(
                Path.Combine(
                    request.OutputRoot.Value,
                    "npcmanager-package.json"));
            if (!Directory.Exists(
                    request.OutputRoot.Value) ||
                !request.OutputRoot.IsUnder(workspaceRoot) ||
                !string.Equals(
                    Path.GetFullPath(manifest.Value),
                    expectedManifest,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    "Final verification requires the exact promoted root manifest.");
            }
            ThrowOnFollowerFinishErrors(
                policy.EvaluateReadRoot(
                    workspaceRoot,
                    manifest));

            FollowerFinishSourcePlan sourcePlan =
                await ReadFollowerFinishSourcePlanAsync(
                    request,
                    cancellationToken);
            verificationScratch =
                request.OutputRoot.Value +
                ".verify-" +
                Guid.NewGuid().ToString("N");
            Directory.CreateDirectory(verificationScratch);
            var verificationScratchRoot =
                new WorkspacePath(verificationScratch);
            var extractedPlugin = new WorkspacePath(
                Path.Combine(
                    verificationScratch,
                    ".transaction",
                    request.Source.Plugin.Value));
            ImmutableArray<FollowerFinishMappedArtifact>
                sourceArtifacts =
                    await ExtractFollowerFinishSourceAsync(
                        request,
                        sourcePlan,
                        verificationScratchRoot,
                        extractedPlugin,
                        cancellationToken);
            string verificationEvidence = Path.Combine(
                request.OutputRoot.Value,
                FollowerFinishEvidenceDefinitions[2].Path.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            if (!File.Exists(verificationEvidence))
                throw new InvalidDataException(
                    "The plugin verification evidence is missing.");
            SkyrimFollowerFinishPluginVerification
                persistedPluginVerification =
                    ReadFollowerFinishPluginVerification(
                        verificationEvidence);
            var promotedPlugin = new WorkspacePath(
                Path.Combine(
                    request.OutputRoot.Value,
                    "Data",
                    request.Source.Plugin.Value));
            SkyrimFollowerFinishPluginVerification
                pluginVerification =
                    await pluginService.VerifyAsync(
                        request,
                        proposal,
                        extractedPlugin,
                        promotedPlugin,
                        cancellationToken);
            diagnostics.AddRange(
                pluginVerification.Diagnostics);
            if (!FollowerFinishPluginVerificationsEqual(
                    persistedPluginVerification,
                    pluginVerification))
            {
                diagnostics.Add(
                    new Diagnostic(
                        "follower-finish-fresh-plugin-verification",
                        DiagnosticSeverity.Error,
                        "Fresh promoted-plugin verification differs from the persisted staging evidence."));
            }

            PackageVerifyResult packageVerification =
                await packageVerifier.VerifyAsync(
                    new PackageVerifyRequest(manifest),
                    cancellationToken);
            diagnostics.AddRange(
                packageVerification.Diagnostics);
            ImmutableArray<Diagnostic> closureDiagnostics =
                await VerifyFollowerFinishPackageClosureAsync(
                    request,
                    proposal,
                    expectedProposalSha256,
                    sourcePlan,
                    sourceArtifacts,
                    request.OutputRoot,
                    packageVerification,
                    pluginVerification,
                    cancellationToken);
            diagnostics.AddRange(closureDiagnostics);
            if (diagnostics.Any(diagnostic =>
                    diagnostic.Severity ==
                    DiagnosticSeverity.Error))
            {
                return new SkyrimFollowerFinishVerificationResult(
                    false,
                    pluginVerification,
                    packageVerification,
                    diagnostics.ToImmutable());
            }

            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-final-static-pass",
                    DiagnosticSeverity.Info,
                    "The promoted package passed fresh static verification; runtime authority remains false."));
            return new SkyrimFollowerFinishVerificationResult(
                true,
                pluginVerification,
                packageVerification,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or
                InvalidDataException or
                UnauthorizedAccessException or
                ArgumentException or
                CryptographicException or
                InvalidOperationException)
        {
            diagnostics.Add(
                new Diagnostic(
                    "follower-finish-verification-failed",
                    DiagnosticSeverity.Error,
                    exception.Message));
            return new SkyrimFollowerFinishVerificationResult(
                false,
                null,
                null,
                diagnostics.ToImmutable());
        }
        finally
        {
            if (!string.IsNullOrEmpty(verificationScratch) &&
                Directory.Exists(verificationScratch))
            {
                try
                {
                    Directory.Delete(
                        verificationScratch,
                        recursive: true);
                }
                catch (Exception exception) when (
                    exception is IOException or
                        UnauthorizedAccessException)
                {
                    diagnostics.Add(
                        new Diagnostic(
                            "follower-finish-verification-cleanup-failed",
                            DiagnosticSeverity.Error,
                            exception.Message));
                }
            }
        }
    }

    private static bool FollowerFinishPluginVerificationsEqual(
        SkyrimFollowerFinishPluginVerification persisted,
        SkyrimFollowerFinishPluginVerification fresh) =>
        persisted.Verified == fresh.Verified &&
        persisted.SourceSha256 == fresh.SourceSha256 &&
        persisted.OutputSha256 == fresh.OutputSha256 &&
        persisted.ExistingRecordChanges.SequenceEqual(
            fresh.ExistingRecordChanges) &&
        persisted.NewRecords.SequenceEqual(
            fresh.NewRecords) &&
        persisted.RawGroupTreeSurface.SequenceEqual(
            fresh.RawGroupTreeSurface) &&
        persisted.RuntimeAuthority == fresh.RuntimeAuthority;
}
