using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Cli;

namespace NpcManager.Cli.Tests;

internal static class DiagnosticExitCodeClassifierTests
{
    public static void Run()
    {
        Require(DiagnosticExitCodeClassifier.Classify([]) ==
            CommandExitCode.Success, "empty-success");
        Require(DiagnosticExitCodeClassifier.Classify([
            new Diagnostic("protected-root-refused", DiagnosticSeverity.Warning, "warning")
        ]) == CommandExitCode.Success, "warning-success");

        string[] validationCodes =
        [
            "npc-create-protected-mismatch",
            "facegen-bake-target-plugin-outside-order",
            "face-texture-protected-neck",
            "finish-core-verify-protected-subrecords",
            "protected-appearance",
            "provider-migration-target-exists",
            "PROTECTED-ROOT-REFUSED",
        ];
        foreach (string code in validationCodes)
        {
            Require(DiagnosticExitCodeClassifier.Classify([
                new Diagnostic(code, DiagnosticSeverity.Error, "validation")
            ]) == CommandExitCode.ValidationFailure,
                "validation-code:" + code);
        }

        string[] securityCodes =
        [
            "actor-assembly-security-refused",
            "facegeom-allowed-root-reparse",
            "facegeom-authority-manifest-reparse",
            "facegeom-carrier-reparse",
            "facegeom-chargen-reparse",
            "facegeom-binary-transport-path-denied",
            "facegeom-binary-transport-reparse",
            "facegeom-hair-regions-security-refusal",
            "facegeom-output-parent-reparse",
            "facegeom-staging-root-reparse",
            "follower-finish-proposal-security-refused",
            "follower-finish-request-security-refused",
            "preset-npc-request-security-refused",
            "protected-root-refused",
            "provider-migration-target-outside-workspace",
            "provider-migration-target-reparse-ancestor",
            "finish-core-transaction-outside-project",
            "npc-preview-render-reparse-refused",
            "package-acceptance-outside-lab",
            "runtime-report-outside-lab",
            "skyrim-native-facegeom-external-rollback-reparse",
        ];
        foreach (string code in securityCodes)
        {
            Require(DiagnosticExitCodeClassifier.Classify([
                new Diagnostic(code, DiagnosticSeverity.Error, "security")
            ]) == CommandExitCode.SecurityRefusal,
                "security-code:" + code);
        }

        Require(DiagnosticExitCodeClassifier.Classify([
            new Diagnostic("ordinary-error", DiagnosticSeverity.Error, "validation"),
            new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error, "security")
        ]) == CommandExitCode.SecurityRefusal,
            "mixed-errors-security-wins");
        Require(DiagnosticExitCodeClassifier.KnownSecurityRefusal ==
            CommandExitCode.SecurityRefusal,
            "known-security-refusal");
        Require(DiagnosticExitCodeClassifier.ClassifyFailure([]) ==
            CommandExitCode.ValidationFailure,
            "known-failure-without-diagnostics");
        Require(
            DiagnosticExitCodeClassifier.RegisteredSecurityCodes.Count == 385,
            "release-lineage-security-code-count");
        string vocabulary = string.Join(
            "\n",
            DiagnosticExitCodeClassifier.RegisteredSecurityCodes.OrderBy(
                code => code,
                StringComparer.Ordinal));
        string vocabularySha256 = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(vocabulary)));
        Require(
            vocabularySha256 ==
            "BE166C9FFB9D0D8F732E658916F45C1C537652A3BFDAFAC0D8D91272DFDA7111",
            "release-lineage-security-vocabulary-sha256:" +
            vocabularySha256);
        foreach (string code in
                 DiagnosticExitCodeClassifier.RegisteredSecurityCodes)
        {
            Diagnostic diagnostic = new(
                code,
                DiagnosticSeverity.Error,
                "registered");
            Require(
                ProtocolDiagnosticClassifier.ClassifyLegacy(diagnostic).Class ==
                DiagnosticClass.Security,
                "registered-legacy-security-class:" + code);
            Require(DiagnosticExitCodeClassifier.Classify([
                diagnostic
            ]) == CommandExitCode.SecurityRefusal,
                "registered-security-code:" + code);
        }

        ProtocolDiagnostic ordinary =
            ProtocolDiagnosticClassifier.ClassifyLegacy(
                new Diagnostic(
                    "ordinary-error",
                    DiagnosticSeverity.Error,
                    "ordinary"));
        Require(
            ordinary.Class == DiagnosticClass.Validation &&
            ordinary.Recovery is null,
            "ordinary-legacy-diagnostic-projection");
    }

    private static void Require(bool condition, string name)
    {
        if (!condition)
            throw new InvalidOperationException(name);
    }
}
