using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

public sealed partial class ProtocolV2FinishCoreAdapter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private WorkflowArtifactBinding BindFile(WorkspacePath path, string kind, string schema,
        string command, string digest, ImmutableArray<string> inputs, ProtocolV2TerminalArtifactLeaseScope terminal,
        string? semantic = null)
    {
        FileStream stream = PinFile(path, terminal);
        return new(kind, schema, path, stream.Length, Convert.ToHexString(SHA256.HashData(stream)), command, digest,
            inputs.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray(), semantic);
    }

    private FileStream PinFile(WorkspacePath path, ProtocolV2TerminalArtifactLeaseScope terminal)
    {
        SkyrimNpcFinishCoreCommandDocumentReader.RequireReadAllowed(workspaceRoot, path);
        if (!Path.IsPathFullyQualified(path.Value) || !path.IsUnder(workspaceRoot) ||
            !File.Exists(path.Value) || File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException("Finish artifacts must be ordinary files beneath the exact workspace.");
        return terminal.Own(new FileStream(path.Value, FileMode.Open, FileAccess.Read, FileShare.Read));
    }

    private static ProtocolArtifact Project(WorkflowArtifactBinding artifact) => new(
        artifact.Kind, artifact.SchemaOrMediaType, artifact.Path.Value, artifact.Size, artifact.Sha256,
        artifact.ProducerCommand, artifact.RequestDigest, artifact.InputArtifactHashes, "hashBound");

    private static ProtocolDiagnostic ProjectDiagnostic(Diagnostic diagnostic) => new(
        diagnostic.Severity switch
        {
            DiagnosticSeverity.Info => ProtocolV2DiagnosticCodes.FinishCoreInfo,
            DiagnosticSeverity.Warning => ProtocolV2DiagnosticCodes.FinishCoreWarning,
            _ => ProtocolV2DiagnosticCodes.FinishCoreValidationFailed
        }, diagnostic.Severity, diagnostic.Code + ": " + diagnostic.Message, DiagnosticClass.Validation,
        diagnostic.Recovery ?? new DiagnosticRecovery(RecoveryAction.CorrectInput, null, null,
            "Correct the bound Finish request and reanalyze before retrying.", false));

    private static ImmutableArray<ProtocolEffect> Effects(bool materialized) =>
        materialized ? [ProtocolEffect.Create(AgentEffectKind.ReadWorkspace, ApplicationEffectStatus.Completed, ApplicationEffectScope.Workspace),
            ProtocolEffect.Create(AgentEffectKind.WriteNewArtifact, ApplicationEffectStatus.Completed, ApplicationEffectScope.KLocalOutput)]
        : [ProtocolEffect.Create(AgentEffectKind.ReadWorkspace, ApplicationEffectStatus.Completed, ApplicationEffectScope.Workspace)];

    private static ImmutableArray<ProtocolAuthority> Authority(bool succeeded) =>
    [
        new(AgentAuthorityKind.InputAdmission, succeeded ? AgentAuthorityState.Established : AgentAuthorityState.Blocked,
            "Success requires typed Finish documents and exact workflow lineage."),
        new(AgentAuthorityKind.SourceProviderIdentity, succeeded ? AgentAuthorityState.Established : AgentAuthorityState.Required,
            "Successful Finish admission binds the exact source and provider authority."),
        new(AgentAuthorityKind.DeterministicMaterialization, succeeded ? AgentAuthorityState.Established : AgentAuthorityState.Required,
            "The successful Finish operation is deterministic, including disposable validation; only published artifact entries identify persistent outputs."),
        new(AgentAuthorityKind.IndependentStaticVerification, AgentAuthorityState.Required, "Run the independent Finish verification command."),
        new(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "Finish does not render a preview."),
        new(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required, "Static Finish and operator receipts do not establish visual acceptance."),
        new(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "No game runtime verification was performed."),
        new(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.NotApplicable, "Finish analyze/apply grant no promotion authority.")
    ];

    private static JsonElement Result(bool apply, SkyrimNpcFinishCoreProposalResult? analysis,
        SkyrimNpcFinishCoreApplyResult? application, SkyrimNpcFinishCoreValidationResult? validation)
    {
        if (!apply)
            return JsonSerializer.SerializeToElement(new
            {
                SchemaId = AgentProtocolSchemaIds.FinishAnalyzeResult,
                SchemaVersion = "1", Proposed = analysis?.Proposed ?? false,
                Status = analysis?.Proposal?.Status ?? SkyrimNpcFinishCoreStatus.Refused,
                ProposalPath = analysis?.ProposalPath?.Value,
                ProposalSha256 = analysis?.ProposalSha256?.Value.ToUpperInvariant(), Validation = validation
            }, JsonOptions);
        return JsonSerializer.SerializeToElement(new
        {
            SchemaId = AgentProtocolSchemaIds.FinishApplyResult,
            SchemaVersion = "1", Applied = application?.Applied ?? false,
            Status = application?.Applied == true ? SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired :
                application?.Diagnostics.Any(item => item.Code == "finish-core-no-changes") == true
                    ? SkyrimNpcFinishCoreStatus.NoChanges : SkyrimNpcFinishCoreStatus.Refused,
            OutputRoot = application?.OutputRoot?.Value, Archive = application?.Archive?.Value,
            Validation = validation
        }, JsonOptions);
    }
}
