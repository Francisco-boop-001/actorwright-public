using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimNpcFinishCoreService :
    ISkyrimNpcFinishCoreInstallContextService
{
    private readonly Func<
        SkyrimNpcFinishCoreRequest,
        CancellationToken,
        ValueTask<SkyrimNpcFinishCoreSourceReadResult>> inspectSource;
    private readonly WorkspacePath projectRoot;
    private readonly IExternalHeadPartInstallVerifier?
        externalInstallVerifier;
    private readonly IExternalHeadPartPromotedOutputVerifier?
        externalPromotedOutputVerifier;
    private readonly Func<string, bool> reparseAncestorProbe;
    private readonly Func<string, bool> reparseDescendantProbe;
    private readonly Func<string, bool>? createOwnedDirectory;
    private readonly ISkyrimNpcFinishCoreOwnedLeaseFactory ownedLeaseFactory;

    public SkyrimNpcFinishCoreService(
        Func<
            SkyrimNpcFinishCoreRequest,
            CancellationToken,
            ValueTask<SkyrimNpcFinishCoreSourceReadResult>> inspectSource,
        WorkspacePath projectRoot,
        IExternalHeadPartInstallVerifier? externalInstallVerifier = null,
        IExternalHeadPartPromotedOutputVerifier? externalPromotedOutputVerifier = null)
    {
        this.inspectSource = inspectSource ??
            throw new ArgumentNullException(nameof(inspectSource));
        this.projectRoot = projectRoot;
        this.externalInstallVerifier = externalInstallVerifier;
        this.externalPromotedOutputVerifier = externalPromotedOutputVerifier ??
            externalInstallVerifier as IExternalHeadPartPromotedOutputVerifier;
        reparseAncestorProbe = HasReparseAncestor;
        reparseDescendantProbe = HasDescendantReparsePoint;
        createOwnedDirectory = null;
        ownedLeaseFactory = new FaceGeomSkyrimNpcFinishCoreOwnedLeaseFactory();
    }

    // Portable transaction tests can inject a stateful boundary probe when a
    // Windows reparse point cannot be created in the test filesystem.
    internal SkyrimNpcFinishCoreService(
        Func<
            SkyrimNpcFinishCoreRequest,
            CancellationToken,
            ValueTask<SkyrimNpcFinishCoreSourceReadResult>> inspectSource,
        WorkspacePath projectRoot,
        Func<string, bool> reparseAncestorProbe,
        Func<string, bool>? reparseDescendantProbe = null,
        IExternalHeadPartInstallVerifier? externalInstallVerifier = null,
        IExternalHeadPartPromotedOutputVerifier? externalPromotedOutputVerifier = null,
        Func<string, bool>? createOwnedDirectory = null,
        ISkyrimNpcFinishCoreOwnedLeaseFactory? ownedLeaseFactory = null)
        : this(
            inspectSource,
            projectRoot,
            externalInstallVerifier,
            externalPromotedOutputVerifier)
    {
        this.reparseAncestorProbe = reparseAncestorProbe ??
            throw new ArgumentNullException(nameof(reparseAncestorProbe));
        this.reparseDescendantProbe = reparseDescendantProbe ?? HasDescendantReparsePoint;
        this.createOwnedDirectory = createOwnedDirectory;
        this.ownedLeaseFactory = ownedLeaseFactory ??
            new FaceGeomSkyrimNpcFinishCoreOwnedLeaseFactory();
    }

    public async ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsExternalRequest(request))
            return await AnalyzeExternalAsync(
                request,
                requestSha256,
                proposalPath,
                installContext: null,
                requireCurrentAuthority: false,
                cancellationToken);
        ImmutableArray<Diagnostic> pathDiagnostics = ValidateProposalPath(
            proposalPath,
            projectRoot);
        if (HasErrors(pathDiagnostics))
            return new(false, null, null, null, pathDiagnostics);
        if (File.Exists(proposalPath.Value) || Directory.Exists(proposalPath.Value))
            return Refused("finish-core-proposal-exists",
                "Analyze refuses to overwrite an existing proposal path.");

        SkyrimNpcFinishCoreSourceReadResult source = await inspectSource(
            request,
            cancellationToken);
        if (!source.Admitted)
            return new(false, null, null, null, source.Diagnostics);

        ImmutableArray<Diagnostic> validation = ValidateSourceAndRequest(
            request,
            source);
        if (HasErrors(validation))
            return new(false, null, null, null, validation);

        cancellationToken.ThrowIfCancellationRequested();
        SkyrimNpcFinishCoreMasterPlan masterPlan = BuildMasterPlan(request, source);
        if (!masterPlan.Admitted)
            return new(false, null, null, null, masterPlan.Diagnostics);

        SkyrimNpcFinishCoreProposal proposal;
        try
        {
            proposal = DeriveProposal(
                request,
                requestSha256,
                source,
                masterPlan);
        }
        catch (SkyrimNpcFinishCoreIdOverflowException)
        {
            return Refused(
                "finish-core-id-overflow",
                "The source FormID space cannot allocate the required Finish Core records.");
        }
        return await WriteProposalAsync(
            proposal,
            proposalPath,
            projectRoot,
            cancellationToken);
    }

    public ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsExternalRequest(request))
            return ApplyExternalAsync(
                request,
                requestSha256,
                proposal,
                proposalSha256,
                installContext: null,
                cancellationToken);
        return ApplyTransactionAsync(
            request, requestSha256, proposal, proposalSha256, cancellationToken);
    }

    public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (IsExternalManifest(manifestPath))
            return VerifyExternalAsync(
                manifestPath,
                manifestSha256,
                installContext: null,
                requireCurrentAuthority: false,
                cancellationToken);
        return VerifyTransactionAsync(manifestPath, manifestSha256, cancellationToken);
    }

    public async ValueTask<SkyrimNpcFinishCoreProposalResult>
        AnalyzeWithInstallContextAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(installContext);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsExternalRequest(request))
            return await AnalyzeExternalAsync(
                request,
                requestSha256,
                proposalPath,
                installContext,
                requireCurrentAuthority: true,
                cancellationToken);
        return await AnalyzeLegacyAsync(
                request,
                requestSha256,
                proposalPath,
                cancellationToken);
    }

    public async ValueTask<SkyrimNpcFinishCoreApplyResult>
        ApplyWithInstallContextAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalSha256,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(proposal);
        ArgumentNullException.ThrowIfNull(installContext);
        cancellationToken.ThrowIfCancellationRequested();
        if (IsExternalRequest(request))
            return await ApplyExternalAsync(
                request,
                requestSha256,
                proposal,
                proposalSha256,
                installContext,
                cancellationToken);
        return await ApplyTransactionAsync(
                request,
                requestSha256,
                proposal,
                proposalSha256,
                cancellationToken);
    }

    public ValueTask<SkyrimNpcFinishCoreVerificationResult>
        VerifyWithInstallContextAsync(
            WorkspacePath manifestPath,
            Sha256Hash manifestSha256,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(installContext);
        cancellationToken.ThrowIfCancellationRequested();
        return IsExternalManifest(manifestPath)
            ? VerifyExternalAsync(
                manifestPath,
                manifestSha256,
                installContext,
                requireCurrentAuthority: true,
                cancellationToken)
            : VerifyTransactionAsync(manifestPath, manifestSha256, cancellationToken);
    }

    private ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeLegacyAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken) =>
        AnalyzeAsync(request, requestSha256, proposalPath, cancellationToken);

    private static bool IsExternalRequest(
        SkyrimNpcFinishCoreRequest request) =>
        request.Authorities.ExternalHeadParts is not null;

    private static bool IsExternalManifest(WorkspacePath manifestPath)
    {
        if (!File.Exists(manifestPath.Value))
            return false;
        try
        {
            using FileStream stream = File.OpenRead(manifestPath.Value);
            using var document = System.Text.Json.JsonDocument.Parse(stream);
            return document.RootElement.TryGetProperty("schema", out var schema) &&
                   schema.ValueKind == System.Text.Json.JsonValueKind.String &&
                   schema.GetString() == SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier;
        }
        catch (IOException)
        {
            return false;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimNpcFinishCoreProposalResult Refused(
        string code,
        string message) =>
        new(false, null, null, null,
            [new Diagnostic(code, DiagnosticSeverity.Error, message)]);
}
