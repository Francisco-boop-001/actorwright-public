using System.Collections.Frozen;

namespace NpcManager.Application;

public enum DiagnosticClass
{
    Operation,
    Usage,
    Security,
    Validation,
    Verification,
    Cancellation
}

public enum RecoveryAction
{
    RetryUnchanged,
    ChooseFreshOutput,
    Reanalyze,
    ObtainHumanReview,
    RepairEnvironment,
    CorrectInput,
    None
}

public sealed record DiagnosticRecovery(
    RecoveryAction Action,
    string? Option,
    string? ArtifactKind,
    string Constraint,
    bool RetryUnchangedSafe)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? AlternativeCommand { get; init; }
}

public sealed record ProtocolDiagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    DiagnosticClass Class,
    DiagnosticRecovery? Recovery);

public sealed record ProtocolDiagnosticSemantics(
    DiagnosticSeverity Severity,
    DiagnosticClass Class);

public sealed record ProtocolFailureProjection(
    string Code,
    DiagnosticClass Class);

public static class ProtocolV2DiagnosticCodes
{
    private static readonly FrozenSet<string> WorkflowBundlePathFailureCodes =
        new[]
        {
            "workflow-output-exists",
            "workflow-output-overlap",
            "workflow-output-parent-refused",
            "workflow-parent-missing",
            "workflow-path-outside-lab",
            "workflow-path-refused",
            "workflow-reparse-refused"
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> WorkflowBundlePersistenceFailureCodes =
        new[]
        {
            "workflow-promoted-readback-mismatch",
            "workflow-staged-readback-mismatch",
            "workflow-write-failed"
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> FinishVerificationPathFailureCodes =
        new[]
        {
            "finish-verification-output-exists",
            "finish-verification-output-overlap",
            "finish-verification-parent-missing",
            "finish-verification-path-outside-lab",
            "finish-verification-path-refused",
            "finish-verification-reparse-refused"
        }.ToFrozenSet(StringComparer.Ordinal);

    private static readonly FrozenSet<string> FinishVerificationPersistenceFailureCodes =
        new[]
        {
            "finish-verification-promoted-readback-mismatch",
            "finish-verification-staged-readback-mismatch",
            "finish-verification-write-failed"
        }.ToFrozenSet(StringComparer.Ordinal);

    public const string AlternateDataStreamRefused =
        "alternate-data-stream-refused";
    public const string ProtocolAdapterDuplicate =
        "protocol-adapter-duplicate";
    public const string ProtocolAdapterMissing = "protocol-adapter-missing";
    public const string ProtocolAdapterResultInvalid =
        "protocol-adapter-result-invalid";
    public const string OptionConflict = "option-conflict";
    public const string OptionDuplicate = "option-duplicate";
    public const string OptionEnumValue = "option-enum-value";
    public const string OptionFlagValue = "option-flag-value";
    public const string OptionRequired = "option-required";
    public const string OptionSha256Value = "option-sha256-value";
    public const string OptionUnknown = "option-unknown";
    public const string OptionValueRequired = "option-value-required";
    public const string NpcBuildPreflightValidationFailed =
        "npc-build-preflight-validation-failed";
    public const string NpcBuildPreflightInfo =
        "npc-build-preflight-info";
    public const string NpcBuildPreflightWarning =
        "npc-build-preflight-warning";
    public const string NpcBuildValidationFailed =
        "npc-build-validation-failed";
    public const string NpcBuildOperationFailed =
        "npc-build-operation-failed";
    public const string NpcBuildVerificationFailed =
        "npc-build-verification-failed";
    public const string NpcPreviewOperationFailed =
        "npc-preview-operation-failed";
    public const string NpcPreviewValidationFailed =
        "npc-preview-validation-failed";
    public const string NpcPreviewVerificationFailed =
        "npc-preview-verification-failed";
    public const string OutputRootOutsideWorkspace =
        "output-root-outside-workspace";
    public const string PathInspectionFailed = "path-inspection-failed";
    public const string PresetInspectionInfo = "preset-inspection-info";
    public const string PresetInspectionInputHashMismatch =
        "preset-inspection-input-hash-mismatch";
    public const string PresetInspectionOutputExists =
        "preset-inspection-output-exists";
    public const string PresetInspectionPathRefused =
        "preset-inspection-path-refused";
    public const string PresetInspectionCanonicalityFailed =
        "preset-inspection-canonicality-failed";
    public const string PresetInspectionPersistenceFailed =
        "preset-inspection-persistence-failed";
    public const string PresetInspectionValidationFailed =
        "preset-inspection-validation-failed";
    public const string PresetInspectionWarning =
        "preset-inspection-warning";
    public const string PositionalUnexpected = "positional-unexpected";
    public const string ProtectedRootRefused = "protected-root-refused";
    public const string ProtocolCommandLegacy = "protocol-command-legacy";
    public const string ProtocolJsonRequired = "protocol-json-required";
    public const string ProtocolOperationCancelled =
        "protocol-operation-cancelled";
    public const string ProtocolOperationFailed = "protocol-operation-failed";
    public const string ProtocolUnsupported = "protocol-unsupported";
    public const string ReparsePointRefused = "reparse-point-refused";
    public const string ReviewReceiptPathRefused =
        "review-receipt-path-refused";
    public const string ReviewReceiptPersistenceFailed =
        "review-receipt-persistence-failed";
    public const string ReviewReceiptValidationFailed =
        "review-receipt-validation-failed";
    public const string FinishVerificationFailed =
        "finish-verification-failed";
    public const string FinishCoreValidationFailed = "finish-core-validation-failed";
    public const string FinishCoreInfo = "finish-core-info";
    public const string FinishCoreWarning = "finish-core-warning";
    public const string FinishVerificationInfo =
        "finish-verification-info";
    public const string FinishVerificationPathRefused =
        "finish-verification-path-refused";
    public const string FinishVerificationWarning =
        "finish-verification-warning";
    public const string FinishVerificationWriteFailed =
        "finish-verification-write-failed";

    public const string WorkflowBundleValidationFailed =
        "workflow-bundle-validation-failed";

    public const string WorkflowBundlePathRefused =
        "workflow-bundle-path-refused";

    public const string WorkflowBundlePersistenceFailed =
        "workflow-bundle-persistence-failed";
    public const string ReviewedIntakeOutputExists =
        "reviewed-intake-output-exists";
    public const string ReviewedIntakeOutputParentMissing =
        "reviewed-intake-output-parent-missing";
    public const string ReviewedIntakeOutputOverlap =
        "reviewed-intake-output-overlap";
    public const string ReviewedIntakeOutputReuse =
        "reviewed-intake-output-reuse";
    public const string ReviewedIntakePersistenceFailed =
        "reviewed-intake-persistence-failed";
    public const string ReviewedIntakeInfo = "reviewed-intake-info";
    public const string ReviewedIntakeWarning = "reviewed-intake-warning";
    public const string ReviewedIntakeValidationFailed =
        "reviewed-intake-validation-failed";
    public const string SchemaCommandUnknown = "schema-command-unknown";
    public const string SchemaOutputExists = "schema-output-exists";
    public const string SchemaOutputOutsideKDrive =
        "schema-output-outside-k-drive";
    public const string SchemaOutputParentMissing =
        "schema-output-parent-missing";
    public const string SchemaOutputWriteFailed =
        "schema-output-write-failed";
    public const string UnsafePathForm = "unsafe-path-form";
    public const string WorkspaceRootOutsideLab =
        "workspace-root-outside-lab";

    public static FrozenSet<string> All { get; } = new[]
    {
        AlternateDataStreamRefused,
        ProtocolAdapterDuplicate,
        ProtocolAdapterMissing,
        ProtocolAdapterResultInvalid,
        OptionConflict,
        OptionDuplicate,
        OptionEnumValue,
        OptionFlagValue,
        OptionRequired,
        OptionSha256Value,
        OptionUnknown,
        OptionValueRequired,
        NpcBuildPreflightValidationFailed,
        NpcBuildPreflightInfo,
        NpcBuildPreflightWarning,
        NpcBuildValidationFailed,
        NpcBuildOperationFailed,
        NpcBuildVerificationFailed,
        NpcPreviewOperationFailed,
        NpcPreviewValidationFailed,
        NpcPreviewVerificationFailed,
        OutputRootOutsideWorkspace,
        PathInspectionFailed,
        PresetInspectionInfo,
        PresetInspectionInputHashMismatch,
        PresetInspectionOutputExists,
        PresetInspectionPathRefused,
        PresetInspectionCanonicalityFailed,
        PresetInspectionPersistenceFailed,
        PresetInspectionValidationFailed,
        PresetInspectionWarning,
        PositionalUnexpected,
        ProtectedRootRefused,
        ProtocolCommandLegacy,
        ProtocolJsonRequired,
        ProtocolOperationCancelled,
        ProtocolOperationFailed,
        ProtocolUnsupported,
        ReparsePointRefused,
        ReviewReceiptPathRefused,
        ReviewReceiptPersistenceFailed,
        ReviewReceiptValidationFailed,
        FinishVerificationFailed,
        FinishCoreValidationFailed,
        FinishCoreInfo,
        FinishCoreWarning,
        FinishVerificationInfo,
        FinishVerificationPathRefused,
        FinishVerificationWarning,
        FinishVerificationWriteFailed,
        WorkflowBundleValidationFailed,
        WorkflowBundlePathRefused,
        WorkflowBundlePersistenceFailed,
        ReviewedIntakeOutputExists,
        ReviewedIntakeOutputParentMissing,
        ReviewedIntakeOutputOverlap,
        ReviewedIntakeOutputReuse,
        ReviewedIntakePersistenceFailed,
        ReviewedIntakeInfo,
        ReviewedIntakeWarning,
        ReviewedIntakeValidationFailed,
        SchemaCommandUnknown,
        SchemaOutputExists,
        SchemaOutputOutsideKDrive,
        SchemaOutputParentMissing,
        SchemaOutputWriteFailed,
        UnsafePathForm,
        WorkspaceRootOutsideLab
    }.ToFrozenSet(StringComparer.Ordinal);

    public static ProtocolFailureProjection ProjectWorkflowBundleFailure(
        string sourceCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCode);
        if (WorkflowBundlePathFailureCodes.Contains(sourceCode))
            return new ProtocolFailureProjection(
                WorkflowBundlePathRefused,
                DiagnosticClass.Security);
        if (WorkflowBundlePersistenceFailureCodes.Contains(sourceCode))
            return new ProtocolFailureProjection(
                WorkflowBundlePersistenceFailed,
                DiagnosticClass.Operation);
        return new ProtocolFailureProjection(
            WorkflowBundleValidationFailed,
            DiagnosticClass.Validation);
    }

    public static ProtocolFailureProjection ProjectFinishVerificationFailure(
        string sourceCode)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCode);
        if (FinishVerificationPathFailureCodes.Contains(sourceCode))
            return new ProtocolFailureProjection(
                FinishVerificationPathRefused,
                DiagnosticClass.Security);
        if (FinishVerificationPersistenceFailureCodes.Contains(sourceCode))
            return new ProtocolFailureProjection(
                FinishVerificationWriteFailed,
                DiagnosticClass.Operation);
        return new ProtocolFailureProjection(
            FinishVerificationFailed,
            DiagnosticClass.Verification);
    }

    public static bool TryGetAuthoritativeSemantics(
        string code,
        out ProtocolDiagnosticSemantics semantics)
    {
        if (string.Equals(
                code,
                ProtocolV2DiagnosticCodes.WorkflowBundlePathRefused,
                StringComparison.Ordinal))
        {
            semantics = new ProtocolDiagnosticSemantics(
                DiagnosticSeverity.Error,
                DiagnosticClass.Security);
            return true;
        }
        if (string.Equals(
                code,
                ProtocolV2DiagnosticCodes.WorkflowBundlePersistenceFailed,
                StringComparison.Ordinal))
        {
            semantics = new ProtocolDiagnosticSemantics(
                DiagnosticSeverity.Error,
                DiagnosticClass.Operation);
            return true;
        }
        if (string.Equals(
                code,
                ProtocolV2DiagnosticCodes.WorkflowBundleValidationFailed,
                StringComparison.Ordinal))
        {
            semantics = new ProtocolDiagnosticSemantics(
                DiagnosticSeverity.Error,
                DiagnosticClass.Validation);
            return true;
        }
        if (code is NpcBuildPreflightInfo or NpcBuildPreflightWarning or
            FinishVerificationInfo or FinishVerificationWarning or FinishCoreInfo or FinishCoreWarning or
            ReviewedIntakeInfo or ReviewedIntakeWarning or
            PresetInspectionInfo or PresetInspectionWarning)
        {
            semantics = new ProtocolDiagnosticSemantics(
                code is NpcBuildPreflightInfo or FinishVerificationInfo or FinishCoreInfo or ReviewedIntakeInfo or
                    PresetInspectionInfo
                    ? DiagnosticSeverity.Info
                    : DiagnosticSeverity.Warning,
                code is FinishVerificationInfo or FinishVerificationWarning
                    ? DiagnosticClass.Verification
                    : DiagnosticClass.Validation);
            return true;
        }

        DiagnosticClass? diagnosticClass = code switch
        {
            AlternateDataStreamRefused or
            OutputRootOutsideWorkspace or
            PathInspectionFailed or
            PresetInspectionOutputExists or
            PresetInspectionPathRefused or
            FinishVerificationPathRefused or
            ProtectedRootRefused or
            ReparsePointRefused or
            ReviewReceiptPathRefused or
            ReviewedIntakeOutputExists or
            ReviewedIntakeOutputParentMissing or
            ReviewedIntakeOutputOverlap or
            ReviewedIntakeOutputReuse or
            SchemaOutputExists or
            SchemaOutputOutsideKDrive or
            SchemaOutputParentMissing or
            UnsafePathForm or
            WorkspaceRootOutsideLab => DiagnosticClass.Security,

            OptionConflict or
            OptionDuplicate or
            OptionEnumValue or
            OptionFlagValue or
            OptionRequired or
            OptionSha256Value or
            OptionUnknown or
            OptionValueRequired or
            PositionalUnexpected or
            ProtocolCommandLegacy or
            ProtocolJsonRequired or
            ProtocolUnsupported or
            SchemaCommandUnknown => DiagnosticClass.Usage,

            ProtocolAdapterDuplicate or
            ProtocolAdapterMissing or
            ProtocolAdapterResultInvalid or
            ProtocolOperationFailed or
            NpcBuildOperationFailed or
            NpcPreviewOperationFailed or
            FinishVerificationWriteFailed or
            PresetInspectionPersistenceFailed or
            ReviewedIntakePersistenceFailed or
            ReviewReceiptPersistenceFailed or
            SchemaOutputWriteFailed => DiagnosticClass.Operation,

            ProtocolOperationCancelled => DiagnosticClass.Cancellation,

            FinishCoreValidationFailed or NpcBuildPreflightValidationFailed or
            NpcBuildValidationFailed or
            NpcPreviewValidationFailed or
            PresetInspectionInputHashMismatch or
            PresetInspectionValidationFailed or
            ReviewedIntakeValidationFailed => DiagnosticClass.Validation,
            ReviewReceiptValidationFailed => DiagnosticClass.Validation,

            FinishVerificationFailed or
            NpcBuildVerificationFailed or
            NpcPreviewVerificationFailed or
            PresetInspectionCanonicalityFailed =>
                DiagnosticClass.Verification,
            _ => null
        };
        if (diagnosticClass is null)
        {
            semantics = null!;
            return false;
        }

        semantics = new ProtocolDiagnosticSemantics(
            DiagnosticSeverity.Error,
            diagnosticClass.Value);
        return true;
    }
}

public static class ProtocolDiagnosticClassifier
{
    private static readonly FrozenSet<string> SecurityCodes =
        new[]
        {
            "actor-assembly-bound-file-outside-lab",
            "actor-assembly-bound-file-reparse",
            "actor-assembly-outside-lab",
            "actor-assembly-plugin-outside-lab",
            "actor-assembly-reparse-refused",
            "actor-assembly-security-refused",
            "actor-assembly-unsafe-path",
            "after-outside-lab",
            "alternate-data-stream-refused",
            "animation-list-manifest-reparse-refused",
            "archive-consistency-archive-reparse",
            "archive-consistency-index-reparse",
            "archive-consistency-plugin-reparse",
            "armor-addon-binary-output-outside-lab",
            "armor-addon-binary-proposal-outside-lab",
            "armor-addon-binary-reparse",
            "armor-addon-binary-source-outside-lab",
            "armor-addon-model-output-outside-lab",
            "armor-addon-model-source-reparse",
            "armor-addon-output-outside-lab",
            "armor-addon-source-reparse",
            "armor-binary-output-outside-lab",
            "armor-binary-proposal-outside-lab",
            "armor-binary-reparse",
            "armor-binary-source-outside-lab",
            "armor-damage-resist-output-outside-lab",
            "armor-damage-resist-source-reparse",
            "armor-proposal-output-outside-lab",
            "armor-proposal-source-reparse",
            "asset-content-provider-outside-root",
            "asset-content-provider-reparse-refused",
            "asset-directory-reparse-refused",
            "asset-index-output-unsafe-path",
            "before-outside-lab",
            "blank-npc-bound-asset-reparse",
            "blank-npc-directory-outside-output",
            "blank-npc-directory-reparse-refused",
            "blank-npc-final-inventory-outside-root",
            "blank-npc-final-tree-reparse",
            "blank-npc-output-parent-outside-lab",
            "blank-npc-rollback-outside-lab",
            "bodygen-input-outside-lab",
            "bodygen-input-read-denied",
            "bodygen-input-reparse-refused",
            "bodygen-input-stat-denied",
            "bodygen-output-outside-lab",
            "bodygen-output-reparse-refused",
            "bodygen-output-write-denied",
            "body-preset-outside-lab",
            "body-sidecar-output-outside-lab",
            "body-sidecar-output-write-denied",
            "body-sidecar-path-outside-root",
            "body-sidecar-reparse",
            "bodyslide-preset-outside-lab",
            "bodyslide-preset-reparse-refused",
            "body-tri-outside-lab",
            "body-tri-path-inspection-denied",
            "body-tri-read-denied",
            "body-tri-reparse-refused",
            "body-weight-outside-lab",
            "body-weight-path-inspection-denied",
            "body-weight-read-denied",
            "body-weight-reparse-refused",
            "changes-action-output-outside-lab",
            "changes-action-session-outside-lab",
            "changes-action-session-reparse",
            "changes-session-outside-lab",
            "changes-session-reparse",
            "chargen-carrier-reparse-refused",
            "data-root-outside-lab",
            "data-root-outside-workspace",
            "direct-chargen-reparse",
            "existing-npc-build-asset-outside-lab",
            "existing-npc-build-outside-lab",
            "existing-npc-edit-input-outside-lab",
            "existing-npc-edit-output-outside-lab",
            "existing-npc-edit-reparse-refused",
            "face-bake-authority-manifest-reparse-refused",
            "facegen-bake-all-output-reparse",
            "facegen-bake-all-root-overlap",
            "facegen-bake-data-root-reparse",
            "facegen-bake-data-root-stat-denied",
            "facegen-batch-manifest-reparse-refused",
            "facegen-batch-output-outside-lab",
            "facegen-correction-output-outside-lab",
            "facegen-deploy-data-outside-lab",
            "facegen-deploy-data-reparse",
            "facegen-deploy-destination-reparse",
            "facegen-deploy-directory-escape",
            "facegen-deploy-directory-reparse",
            "facegen-deploy-manifest-read-denied",
            "facegen-deploy-package-outside-lab",
            "facegen-deploy-package-path-reparse",
            "facegen-deploy-package-reparse",
            "facegen-deploy-path-escape",
            "facegen-deploy-preflight-read-denied",
            "facegen-deploy-root-overlap",
            "facegen-deploy-source-missing-or-reparse",
            "facegen-deploy-write-denied",
            "facegen-manifest-outside-lab",
            "facegen-manifest-read-denied",
            "facegen-manifest-reparse-refused",
            "facegen-options-input-reparse",
            "facegen-options-output-reparse",
            "facegen-pack-anchor-reparse",
            "facegen-pack-output-outside-lab",
            "facegen-pack-path-escape",
            "facegen-pack-root-overlap",
            "facegen-pack-source-outside-data",
            "facegen-pack-source-reparse",
            "facegen-pack-write-denied",
            "facegen-plugin-target-entry-outside-lab",
            "facegen-plugin-target-manifest-reparse-refused",
            "facegen-plugin-target-output-outside-lab",
            "facegeom-allowed-root-reparse",
            "facegeom-authority-manifest-reparse",
            "facegeom-binary-path-denied",
            "facegeom-binary-reparse",
            "facegeom-binary-transport-path-denied",
            "facegeom-binary-transport-reparse",
            "facegeom-bound-output-path-unsafe",
            "facegeom-bound-output-root-outside-lab",
            "facegeom-bound-provider-source-read-denied",
            "facegeom-bound-provider-source-reparse",
            "facegeom-bound-provider-source-stat-denied",
            "facegeom-bound-root-overlap",
            "facegeom-build-manifest-read-denied",
            "facegeom-build-output-outside-lab",
            "facegeom-carrier-reparse",
            "facegeom-chargen-reparse",
            "facegeom-final-root-overlap",
            "facegeom-generated-stage-escape",
            "facegeom-hair-regions-render-reparse",
            "facegeom-hair-regions-render-root-overlap",
            "facegeom-hair-regions-security-refusal",
            "facegeom-hair-regions-selected-source-reparse",
            "facegeom-output-cleanup-reparse",
            "facegeom-output-parent-reparse",
            "facegeom-staging-root-reparse",
            "facegeom-xyz-cleanup-reparse",
            "face-morph-snapshot-reparse",
            "face-pose-input-outside-lab",
            "face-pose-input-reparse",
            "face-pose-read-denied",
            "face-texture-output-reparse",
            "facetint-bound-manifest-read-denied",
            "facetint-bound-output-path-unsafe",
            "facetint-bound-output-root-outside-lab",
            "facetint-bound-provider-source-read-denied",
            "facetint-bound-provider-source-reparse",
            "facetint-bound-provider-source-stat-denied",
            "facetint-bound-root-overlap",
            "facetint-build-output-outside-lab",
            "facetint-dds-output-outside-lab",
            "facetint-provider-root-outside-lab",
            "facetint-provider-root-reparse",
            "facetint-provider-root-stat-denied",
            "finish-core-proposal-outside-project",
            "finish-core-transaction-outside-project",
            "finish-core-transaction-reparse",
            "finish-core-transaction-root-overlap",
            "finish-verification-path-outside-lab",
            "finish-verification-reparse-refused",
            "follower-finish-pair-path-outside",
            "follower-finish-pair-reparse",
            "follower-finish-proposal-security-refused",
            "follower-finish-request-security-refused",
            "follower-finish-source-zip-path-escape",
            "generated-scan-attributes-denied",
            "generated-scan-data-root-reparse",
            "generated-scan-plugin-reparse",
            "generated-scan-sidecar-directory-reparse",
            "generated-scan-sidecar-path-outside-root",
            "generated-scan-sidecar-reparse",
            "headpart-preview-cache-reparse",
            "input-outside-lab",
            "leveled-list-binary-output-outside-lab",
            "leveled-list-binary-proposal-outside-lab",
            "leveled-list-binary-reparse",
            "leveled-list-binary-source-outside-lab",
            "leveled-list-output-outside-lab",
            "leveled-list-resolve-output-outside-lab",
            "leveled-list-resolve-plugin-outside-lab",
            "leveled-list-resolve-plugin-reparse",
            "leveled-list-resolve-source-outside-lab",
            "leveled-list-resolve-source-reparse",
            "leveled-list-source-reparse",
            "load-order-file-outside-lab",
            "load-order-path-inspection-denied",
            "load-order-plugin-inspection-denied",
            "load-order-plugin-reparse-refused",
            "load-order-plugins-outside-lab",
            "load-order-plugins-read-denied",
            "load-order-read-denied",
            "load-order-reparse-refused",
            "material-swap-binary-output-outside-lab",
            "material-swap-binary-proposal-outside-lab",
            "material-swap-binary-reparse",
            "material-swap-binary-source-outside-lab",
            "material-swap-output-outside-lab",
            "material-swap-source-reparse",
            "mesh-preview-cache-escape",
            "mesh-preview-cache-reparse",
            "mesh-preview-provider-escape",
            "npc-appearance-override-outside-lab",
            "npc-appearance-override-reparse",
            "npc-create-alternate-data-stream",
            "npc-create-master-provider-outside-lab",
            "npc-create-output-outside-lab",
            "npc-create-plugin-authority-outside-lab",
            "npc-create-proposal-outside-lab",
            "npc-create-reparse-point",
            "npc-create-rollback-outside-lab",
            "npc-create-rollback-tree-reparse",
            "npc-create-template-outside-lab",
            "npc-create-unsafe-path",
            "npc-override-output-outside-lab",
            "npc-override-proposal-outside-lab",
            "npc-override-reparse-refused",
            "npc-override-source-outside-lab",
            "npc-preview-asset-reparse-refused",
            "npc-preview-comparison-reparse",
            "npc-preview-output-outside-workspace",
            "npc-preview-render-path-inspection-denied",
            "npc-preview-render-reparse-refused",
            "object-template-binary-output-outside-lab",
            "object-template-binary-properties-outside-lab",
            "object-template-binary-proposal-outside-lab",
            "object-template-binary-reparse",
            "object-template-binary-source-outside-lab",
            "object-template-output-outside-lab",
            "object-template-property-output-outside-lab",
            "object-template-property-source-reparse",
            "object-template-source-reparse",
            "outfit-binary-output-outside-lab",
            "outfit-binary-proposal-outside-lab",
            "outfit-binary-reparse",
            "outfit-binary-source-outside-lab",
            "outfit-choice-plugin-reparse-refused",
            "outfit-production-output-outside-lab",
            "outfit-proposal-item-provider-reparse",
            "outfit-proposal-output-outside-lab",
            "outfit-proposal-source-reparse",
            "output-outside-lab",
            "output-root-outside-workspace",
            "package-acceptance-outside-lab",
            "package-archive-output-outside-lab",
            "package-archive-proposal-outside-root",
            "package-archive-proposal-read-denied",
            "package-archive-root-overlap",
            "package-archive-source-outside-lab",
            "package-archive-source-path-escape",
            "package-archive-write-denied",
            "package-artifact-outside-root",
            "package-artifact-reparse",
            "package-build-output-outside-lab",
            "package-build-path-escape",
            "package-build-root-overlap",
            "package-build-source-outside-lab",
            "package-build-write-denied",
            "package-directory-read-denied",
            "package-directory-reparse",
            "package-file-reparse",
            "package-manifest-outside-lab",
            "package-manifest-read-denied",
            "package-manifest-reparse",
            "pipeline-facegeom-manifest-outside-lab",
            "pipeline-facetint-manifest-outside-lab",
            "pipeline-manifest-write-denied",
            "pipeline-output-outside-lab",
            "pipeline-path-inspection-denied",
            "pipeline-preset-outside-lab",
            "pipeline-reparse-refused",
            "pipeline-rollback-denied",
            "pipeline-runtime-script-build-outside-lab",
            "pipeline-runtime-script-package-outside-lab",
            "pipeline-source-plugin-outside-lab",
            "pipeline-source-plugin-read-denied",
            "pipeline-write-denied",
            "plugin-attributes-denied",
            "plugin-deploy-data-outside-lab",
            "plugin-deploy-data-reparse",
            "plugin-deploy-destination-reparse",
            "plugin-deploy-input-outside-lab",
            "plugin-deploy-input-path-reparse",
            "plugin-deploy-input-reparse",
            "plugin-deploy-source-read-denied",
            "plugin-deploy-write-denied",
            "plugin-enumeration-denied",
            "plugin-fingerprint-denied",
            "preset-catalog-outside-lab",
            "preset-input-outside-lab",
            "preset-load-order-attributes-denied",
            "preset-load-order-outside-lab",
            "preset-load-order-reparse-refused",
            "preset-npc-request-security-refused",
            "preset-output-outside-lab",
            "preset-preview-cache-reparse",
            "preset-preview-reparse-refused",
            "preset-read-denied",
            "preview-manifest-reparse-refused",
            "preview-nif-binary-path-denied",
            "preview-nif-binary-reparse",
            "preview-nif-output-outside-lab",
            "preview-nif-scene-reparse-refused",
            "preview-output-outside-lab",
            "preview-render-access-denied",
            "preview-render-path-inspection-denied",
            "preview-render-reparse-refused",
            "preview-reroll-manifest-reparse-refused",
            "preview-reroll-output-outside-lab",
            "proposal-outside-lab",
            "protected-root-refused",
            "provider-migration-target-outside-workspace",
            "provider-migration-target-reparse-ancestor",
            "qualified-carrier-alternate-data-stream-refused",
            "qualified-carrier-evidence-file-outside-package",
            "qualified-carrier-evidence-output-outside-package",
            "qualified-carrier-evidence-package-outside-lab",
            "qualified-carrier-output-outside-lab",
            "qualified-carrier-reparse-refused",
            "qualified-carrier-source-outside-lab",
            "qualified-carrier-verification-outside-lab",
            "racemenu-build-bodygen-retained-outside-data",
            "racemenu-build-bodygen-retained-reparse",
            "racemenu-build-facegeom-carrier-name-unsafe",
            "racemenu-build-staging-cleanup-reparse",
            "racemenu-build-staging-reparse",
            "racemenu-selection-candidate-reparse",
            "racemenu-selection-cleanup-reparse",
            "racemenu-standalone-destination-reparse",
            "record-proposal-output-outside-lab",
            "reference-image-outside-lab",
            "reference-image-reparse",
            "reference-session-input-outside-lab",
            "reparse-point-refused",
            "runtime-report-outside-lab",
            "runtime-script-binary-output-outside-lab",
            "runtime-script-build-write-denied",
            "runtime-script-deploy-data-outside-lab",
            "runtime-script-deploy-data-reparse",
            "runtime-script-deploy-destination-reparse",
            "runtime-script-deploy-manifest-read-denied",
            "runtime-script-deploy-package-outside-lab",
            "runtime-script-deploy-package-path-reparse",
            "runtime-script-deploy-package-reparse",
            "runtime-script-deploy-scripts-reparse",
            "runtime-script-deploy-source-read-denied",
            "runtime-script-deploy-write-denied",
            "runtime-script-output-outside-lab",
            "runtime-script-package-output-outside-lab",
            "runtime-script-package-parent-reparse",
            "runtime-script-package-source-outside-lab",
            "runtime-script-package-source-reparse",
            "runtime-script-package-write-denied",
            "runtime-script-proposal-write-denied",
            "runtime-script-source-outside-lab",
            "runtime-script-source-reparse",
            "runtime-script-vmad-plugin-reparse",
            "save-package-output-outside-lab",
            "save-package-output-parent-reparse",
            "save-package-plugin-output-outside-lab",
            "save-package-plugin-reparse",
            "save-package-plugin-source-outside-lab",
            "save-package-root-overlap",
            "save-package-source-outside-lab",
            "save-package-source-reparse",
            "save-plugin-verify-outside-lab",
            "selective-paste-path-outside-lab",
            "skyrim-bsa-reparse",
            "skyrim-face-record-provider-outside-workspace",
            "skyrim-face-record-provider-reparse",
            "skyrim-native-facegeom-external-rollback-reparse",
            "skyrim-native-tint-output-reparse",
            "skyrim-native-tint-provider-outside-workspace",
            "skyrim-native-tint-provider-reparse",
            "skyrim-record-authority-data-reparse",
            "skyrim-record-authority-staged-reparse",
            "sse-facegeom-carrier-output-reparse",
            "sse-facegeom-carrier-output-stat-denied",
            "sse-transform-input-reparse",
            "sse-transform-output-outside-lab",
            "sse-transform-output-reparse",
            "unsafe-path-form",
            "workspace-root-outside-lab",
        }.ToFrozenSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> RegisteredLegacySecurityCodes =>
        SecurityCodes;

    public static bool TryGetAuthoritativeSemantics(
        string code,
        out ProtocolDiagnosticSemantics semantics)
    {
        if (ProtocolV2DiagnosticCodes.TryGetAuthoritativeSemantics(
                code,
                out semantics))
            return true;
        if (SecurityCodes.Contains(code))
        {
            semantics = new ProtocolDiagnosticSemantics(
                DiagnosticSeverity.Error,
                DiagnosticClass.Security);
            return true;
        }

        semantics = null!;
        return false;
    }

    public static ProtocolDiagnostic ClassifyLegacy(Diagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        return new ProtocolDiagnostic(
            diagnostic.Code,
            diagnostic.Severity,
            diagnostic.Message,
            SecurityCodes.Contains(diagnostic.Code)
                ? DiagnosticClass.Security
                : DiagnosticClass.Validation,
            null);
    }
}

public static class ProtocolExitCodeMapper
{
    public static ProtocolOutcome MapOutcome(CommandExitCode exitCode) =>
        exitCode switch
        {
            CommandExitCode.Success => ProtocolOutcome.Succeeded,
            CommandExitCode.SecurityRefusal => ProtocolOutcome.Refused,
            CommandExitCode.Cancelled => ProtocolOutcome.Cancelled,
            _ => ProtocolOutcome.Failed
        };

    public static CommandExitCode Map(
        IEnumerable<ProtocolDiagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(diagnostics);

        var hasUsage = false;
        var hasCancellation = false;
        var hasOperation = false;
        var hasValidation = false;
        foreach (ProtocolDiagnostic diagnostic in diagnostics)
        {
            if (diagnostic.Severity != DiagnosticSeverity.Error)
                continue;

            switch (diagnostic.Class)
            {
                case DiagnosticClass.Security:
                    return CommandExitCode.SecurityRefusal;
                case DiagnosticClass.Usage:
                    hasUsage = true;
                    break;
                case DiagnosticClass.Cancellation:
                    hasCancellation = true;
                    break;
                case DiagnosticClass.Operation:
                    hasOperation = true;
                    break;
                case DiagnosticClass.Validation:
                case DiagnosticClass.Verification:
                    hasValidation = true;
                    break;
                default:
                    hasOperation = true;
                    break;
            }
        }

        if (hasUsage)
            return CommandExitCode.UsageError;
        if (hasCancellation)
            return CommandExitCode.Cancelled;
        if (hasOperation)
            return CommandExitCode.GeneralFailure;
        if (hasValidation)
            return CommandExitCode.ValidationFailure;
        return CommandExitCode.Success;
    }
}
