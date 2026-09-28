using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using NpcManager.Application;
using NpcManager.Cli;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Cli.Tests;

internal sealed class Preview254ExternalSmpFinishCliScenario :
    IPreview254ExternalSmpCliScenario
{
    public string Selector => "--test-finish-core-external-smp-cli";

    public async ValueTask RunAsync(CancellationToken cancellationToken) =>
        await FinishCoreExternalSmpCliTests.RunAsync(cancellationToken);
}

internal static class FinishCoreExternalSmpCliTests
{
    private static readonly JsonSerializerOptions MutationJsonOptions = new()
    {
        WriteIndented = true
    };

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var fixture = new FinishExternalSmpFixture();
        await AssertConcreteSourceAdmission(fixture, cancellationToken);
        await AssertConcreteFinishService(fixture, cancellationToken);
        await AssertConcreteMutationRefusals(fixture, cancellationToken);
        var service = new RecordingInstallContextService(fixture);

        await AssertContextFreeAnalyzeAndContextfulDispatch(
            fixture, service, cancellationToken);
        await AssertInvalidAndApplyRefusal(
            fixture, service, cancellationToken);
        await AssertContextfulApplyAndFingerprintRefusal(
            fixture, service, cancellationToken);
        await AssertVerifyDispatchAndContextFreeSemantics(
            fixture, service, cancellationToken);
        AssertBinderContract();
    }

    private static async Task AssertConcreteSourceAdmission(
        FinishExternalSmpFixture fixture,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreSourceReadResult result = await fixture.CreateSourceReader().InspectAsync(
            fixture.Request,
            cancellationToken);
        Assert(
            result.Admitted,
            "The authentic external SMP CLI fixture was refused by the concrete " +
            "Finish Core source reader: " +
            string.Join(" | ", result.Diagnostics.Select(item =>
                item.Code + ":" + item.Message)));
    }

    private static async Task AssertConcreteFinishService(
        FinishExternalSmpFixture fixture,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreService service = fixture.CreateConcreteService();
        SkyrimNpcFinishCoreProposalResult contextFree =
            await service.AnalyzeAsync(
                fixture.Request,
                fixture.RequestHash,
                new WorkspacePath(Path.Combine(
                    fixture.Root, "concrete-context-free-proposal.json")),
                cancellationToken);
        Assert(
            contextFree.Proposed && contextFree.Proposal is not null &&
            contextFree.Proposal.Status ==
                SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired &&
            contextFree.Proposal.ExternalHeadParts?.Verification
                .CurrentInstallDependencyState == ExternalInstallDependencyState.DeclaredUnverified,
            "Concrete context-free Finish Core analyze did not preserve the install gate: " +
            FormatDiagnostics(contextFree.Diagnostics));

        SkyrimNpcFinishCoreProposalResult contextful =
            await service.AnalyzeWithInstallContextAsync(
                fixture.Request,
                fixture.RequestHash,
                new WorkspacePath(Path.Combine(
                    fixture.Root, "concrete-contextful-proposal.json")),
                fixture.CreateInstallContext(),
                cancellationToken);
        Assert(
            contextful.Proposed && contextful.Proposal is not null &&
            contextful.Proposal.Status == SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite &&
            contextful.Proposal.ExternalHeadParts?.Verification
                .CurrentInstallDependencyState == ExternalInstallDependencyState.Verified &&
            contextful.Proposal.ExternalHeadParts.Verification.InstallReady &&
            contextful.Proposal.ExternalHeadParts.ContextFingerprint is not null,
            "Concrete contextful Finish Core analyze did not establish install authority: " +
            FormatDiagnostics(contextful.Diagnostics));
    }

    private static async Task AssertConcreteMutationRefusals(
        FinishExternalSmpFixture fixture,
        CancellationToken cancellationToken)
    {
        byte[] validManifest = await File.ReadAllBytesAsync(
            fixture.SourcePackageManifestPath.Value, cancellationToken);
        try
        {
            JsonObject incomplete = JsonNode.Parse(validManifest)?.AsObject() ??
                throw new InvalidDataException("The authentic fixture manifest was not an object.");
            incomplete.Remove("edition");
            await File.WriteAllBytesAsync(
                fixture.SourcePackageManifestPath.Value,
                JsonSerializer.SerializeToUtf8Bytes(incomplete, MutationJsonOptions),
                cancellationToken);
            SkyrimNpcFinishCoreRequest mutated = fixture.Request with
            {
                Source = fixture.Request.Source with
                {
                    PackageManifestSha256 = Hash(File.ReadAllBytes(
                        fixture.SourcePackageManifestPath.Value)),
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader
                        .ComputePackageTreeSha256(fixture.PackageRoot)
                }
            };
            SkyrimNpcFinishCoreSourceReadResult result =
                await fixture.CreateSourceReader().InspectAsync(mutated, cancellationToken);
            Assert(
                !result.Admitted && result.Diagnostics.Any(item =>
                    item.Code == "package-manifest-field" &&
                    item.Message.Contains("edition", StringComparison.Ordinal)),
                "An incomplete canonical package manifest was not refused for the missing field: " +
                FormatDiagnostics(result.Diagnostics));
        }
        finally
        {
            await File.WriteAllBytesAsync(
                fixture.SourcePackageManifestPath.Value,
                validManifest,
                cancellationToken);
        }

        SkyrimNpcFinishCoreRequest emptyProviders = fixture.Request with
        {
            Authorities = fixture.Request.Authorities with
            {
                Providers = []
            }
        };
        SkyrimNpcFinishCoreSourceReadResult emptyProviderResult =
            await fixture.CreateSourceReader().InspectAsync(
                emptyProviders, cancellationToken);
        Assert(
            !emptyProviderResult.Admitted && emptyProviderResult.Diagnostics.Any(item =>
                item.Code == "finish-core-authority-provider-empty"),
            "An empty ordinary provider authority list was not refused by the concrete reader: " +
            FormatDiagnostics(emptyProviderResult.Diagnostics));

        var verifier = new BethesdaExternalHeadPartInstallVerifier();
        ExternalHeadPartInstallVerificationResult disabled = await verifier.VerifyAsync(
            fixture.CreateVerificationRequest(
                new ExternalHeadPartInstallVerificationContext(
                    fixture.DataRoot,
                    [
                        new PluginName("Skyrim.esm"),
                        fixture.TargetRace.Plugin,
                        fixture.OutputPlugin
                    ],
                    fixture.TargetRace)),
            cancellationToken);
        Assert(
            disabled.Artifact is { InstallReady: false } &&
            disabled.Diagnostics.Any(item =>
                item.Code == ExternalHeadPartDiagnosticCodes.ProviderDisabled),
            "A disabled external provider was not refused by the strict verifier: " +
            FormatDiagnostics(disabled.Diagnostics));

        byte[] providerBytes = await File.ReadAllBytesAsync(
            fixture.ProviderPluginPath.Value, cancellationToken);
        try
        {
            File.Delete(fixture.ProviderPluginPath.Value);
            ExternalHeadPartInstallVerificationResult missing =
                await verifier.VerifyAsync(
                    fixture.CreateVerificationRequest(
                        fixture.CreateInstallContext()),
                    cancellationToken);
            Assert(
                missing.Artifact is { InstallReady: false } &&
                missing.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.ProviderMissing),
                "An omitted external provider was not refused by the strict verifier: " +
                FormatDiagnostics(missing.Diagnostics));
        }
        finally
        {
            await File.WriteAllBytesAsync(
                fixture.ProviderPluginPath.Value,
                providerBytes,
                cancellationToken);
        }
    }

    private static string FormatDiagnostics(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => item.Code + ":" + item.Message));

    private static async Task AssertContextFreeAnalyzeAndContextfulDispatch(
        FinishExternalSmpFixture fixture,
        RecordingInstallContextService service,
        CancellationToken cancellationToken)
    {
        string contextFreeProposal = Path.Combine(
            fixture.Root, "proposal-context-free.json");
        (CommandExitCode exit, string output, string error) =
            await RunHandler(
                fixture,
                service,
                [
                    "npc", "finish", "analyze",
                    "--request", fixture.RequestPath.Value,
                    "--request-sha256", fixture.RequestHash.Value,
                    "--proposal", contextFreeProposal,
                    "--json"
                ],
                cancellationToken);
        Assert(exit == CommandExitCode.Success &&
               service.AnalyzeCalls == 1 &&
               service.ContextAnalyzeCalls == 0 &&
               output.Contains(
                   SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired.ToString(),
                   StringComparison.Ordinal) &&
               !output.Contains(fixture.DataRoot.Value, StringComparison.OrdinalIgnoreCase),
            "Context-free external analyze did not preserve the nonterminal install gate: " +
            $"exit={exit} analyze={service.AnalyzeCalls} contextAnalyze={service.ContextAnalyzeCalls} " +
            $"out={output} err={error}");

        service.Reset();
        string contextfulProposal = Path.Combine(
            fixture.Root, "proposal-contextful.json");
        (exit, output, error) = await RunHandler(
            fixture,
            service,
            [
                "npc", "finish", "analyze",
                "--request", fixture.RequestPath.Value,
                "--request-sha256", fixture.RequestHash.Value,
                "--proposal", contextfulProposal,
                "--data-root", fixture.DataRoot.Value,
                "--plugins", fixture.EnabledPlugins,
                "--json"
            ],
            cancellationToken);
        Assert(exit == CommandExitCode.Success &&
               service.AnalyzeCalls == 0 &&
               service.ContextAnalyzeCalls == 1 &&
               service.ContextAnalyzeContext is { } context &&
               context.DataRoot == fixture.DataRoot &&
               context.EnabledPluginOrder.SequenceEqual(fixture.EnabledPluginOrder) &&
               context.TargetRace == fixture.TargetRace &&
               output.Contains(
                   SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite.ToString(),
                   StringComparison.Ordinal) &&
               !output.Contains(fixture.DataRoot.Value, StringComparison.OrdinalIgnoreCase),
            "External analyze did not dispatch the exact ephemeral contextful service: " +
            $"exit={exit} analyze={service.AnalyzeCalls} contextAnalyze={service.ContextAnalyzeCalls} " +
            $"out={output} err={error}");
    }

    private static async Task AssertInvalidAndApplyRefusal(
        FinishExternalSmpFixture fixture,
        RecordingInstallContextService service,
        CancellationToken cancellationToken)
    {
        service.Reset();
        (CommandExitCode exit, _, string error) = await RunHandler(
            fixture,
            service,
            [
                "npc", "finish", "analyze",
                "--request", fixture.RequestPath.Value,
                "--request-sha256", fixture.RequestHash.Value,
                "--proposal", Path.Combine(fixture.Root, "partial.json"),
                "--data-root", fixture.DataRoot.Value
            ],
            cancellationToken);
        Assert(exit == CommandExitCode.UsageError &&
               service.TotalOperationCalls == 0 &&
               error.Contains("--plugins", StringComparison.Ordinal),
            "Partial external context was not refused before service dispatch: " +
            $"exit={exit} calls={service.TotalOperationCalls} err={error}");

        service.Reset();
        (exit, _, error) = await RunHandler(
            fixture,
            service,
            [
                "npc", "finish", "analyze",
                "--request", fixture.RequestPath.Value,
                "--request-sha256", fixture.RequestHash.Value,
                "--proposal", Path.Combine(fixture.Root, "outside.json"),
                "--data-root", @"C:\outside",
                "--plugins", fixture.EnabledPlugins
            ],
            cancellationToken);
        Assert(exit == CommandExitCode.UsageError &&
               service.TotalOperationCalls == 0 &&
               error.Contains("K-local", StringComparison.OrdinalIgnoreCase),
            "A non-K external context was not refused before service dispatch: " +
            $"exit={exit} calls={service.TotalOperationCalls} err={error}");

        service.Reset();
        (exit, string output, _) = await RunHandler(
            fixture,
            service,
            [
                "npc", "finish", "apply",
                "--request", fixture.RequestPath.Value,
                "--request-sha256", fixture.RequestHash.Value,
                "--proposal", fixture.ReadyProposalPath.Value,
                "--proposal-sha256", fixture.ReadyProposalHash.Value,
                "--json"
            ],
            cancellationToken);
        Assert(exit == CommandExitCode.ValidationFailure &&
               service.ApplyCalls == 0 &&
               service.ContextApplyCalls == 0 &&
               output.Contains(ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                   StringComparison.Ordinal),
            "External apply without fresh context was not refused before dispatch: " +
            $"exit={exit} apply={service.ApplyCalls} contextApply={service.ContextApplyCalls} out={output}");
    }

    private static async Task AssertContextfulApplyAndFingerprintRefusal(
        FinishExternalSmpFixture fixture,
        RecordingInstallContextService service,
        CancellationToken cancellationToken)
    {
        service.Reset();
        service.ApplyResult = fixture.AppliedResult;
        (CommandExitCode exit, string output, string error) = await RunHandler(
            fixture,
            service,
            [
                "npc", "finish", "apply",
                "--request", fixture.RequestPath.Value,
                "--request-sha256", fixture.RequestHash.Value,
                "--proposal", fixture.ReadyProposalPath.Value,
                "--proposal-sha256", fixture.ReadyProposalHash.Value,
                "--data-root", fixture.DataRoot.Value,
                "--plugins", fixture.EnabledPlugins,
                "--json"
            ],
            cancellationToken);
        Assert(exit == CommandExitCode.Success &&
               service.ApplyCalls == 0 &&
               service.ContextApplyCalls == 1 &&
               service.ContextApplyContext is { } context &&
               context.TargetRace == fixture.TargetRace &&
               output.Contains("applied", StringComparison.Ordinal) &&
               !output.Contains(fixture.DataRoot.Value, StringComparison.OrdinalIgnoreCase),
            "External apply did not dispatch through the contextful service: " +
            $"exit={exit} apply={service.ApplyCalls} contextApply={service.ContextApplyCalls} " +
            $"out={output} err={error}");

        service.Reset();
        service.ApplyResult = fixture.FingerprintMismatchApplyResult;
        (exit, output, error) = await RunHandler(
            fixture,
            service,
            [
                "npc", "finish", "apply",
                "--request", fixture.RequestPath.Value,
                "--request-sha256", fixture.RequestHash.Value,
                "--proposal", fixture.ReadyProposalPath.Value,
                "--proposal-sha256", fixture.ReadyProposalHash.Value,
                "--data-root", fixture.DataRoot.Value,
                "--plugins", fixture.EnabledPlugins,
                "--json"
            ],
            cancellationToken);
        Assert(exit == CommandExitCode.ValidationFailure &&
               service.ApplyCalls == 0 &&
               service.ContextApplyCalls == 1 &&
               output.Contains(ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                   StringComparison.Ordinal) &&
               !error.Contains(fixture.DataRoot.Value, StringComparison.OrdinalIgnoreCase),
            "A proposal fingerprint refusal did not remain a typed contextful apply failure: " +
            $"exit={exit} apply={service.ApplyCalls} contextApply={service.ContextApplyCalls} " +
            $"out={output} err={error}");
    }

    private static async Task AssertVerifyDispatchAndContextFreeSemantics(
        FinishExternalSmpFixture fixture,
        RecordingInstallContextService service,
        CancellationToken cancellationToken)
    {
        service.Reset();
        service.VerifyResult = fixture.ContextFreeVerificationResult;
        (CommandExitCode exit, string output, string error) = await RunHandler(
            fixture,
            service,
            [
                "npc", "finish", "verify",
                "--manifest", fixture.ManifestPath.Value,
                "--manifest-sha256", fixture.ManifestHash.Value,
                "--json"
            ],
            cancellationToken);
        Assert(exit == CommandExitCode.Success &&
               service.VerifyCalls == 1 &&
               service.ContextVerifyCalls == 0 &&
               output.Contains("declaredUnverified", StringComparison.Ordinal) &&
               !output.Contains(fixture.DataRoot.Value, StringComparison.OrdinalIgnoreCase),
            "Context-free external verify did not preserve current install uncertainty: " +
            $"exit={exit} verify={service.VerifyCalls} contextVerify={service.ContextVerifyCalls} " +
            $"out={output} err={error}");

        service.Reset();
        service.VerifyResult = fixture.ContextfulVerificationResult;
        (exit, output, error) = await RunHandler(
            fixture,
            service,
            [
                "npc", "finish", "verify",
                "--manifest", fixture.ManifestPath.Value,
                "--manifest-sha256", fixture.ManifestHash.Value,
                "--data-root", fixture.DataRoot.Value,
                "--plugins", fixture.EnabledPlugins,
                "--json"
            ],
            cancellationToken);
        Assert(exit == CommandExitCode.Success &&
               service.VerifyCalls == 0 &&
               service.ContextVerifyCalls == 1 &&
               service.ContextVerifyContext is { } context &&
               context.TargetRace == fixture.TargetRace &&
               output.Contains("verified", StringComparison.Ordinal) &&
               !output.Contains(fixture.DataRoot.Value, StringComparison.OrdinalIgnoreCase),
            "External verify did not dispatch through the contextful service: " +
            $"exit={exit} verify={service.VerifyCalls} contextVerify={service.ContextVerifyCalls} " +
            $"out={output} err={error}");
    }

    private static void AssertBinderContract()
    {
        ExternalHeadPartInstallContextBindingResult absent =
            ExternalHeadPartInstallContextBinder.Bind(
                CommandLine.Parse(["npc", "finish", "verify"]));
        Assert(absent.IsValid && !absent.IsSpecified &&
               absent.EnabledPluginOrder.IsEmpty,
            "Absent external context was not accepted for context-free operations.");

        ExternalHeadPartInstallContextBindingResult duplicate =
            ExternalHeadPartInstallContextBinder.Bind(
                CommandLine.Parse([
                    "npc", "finish", "verify",
                    "--data-root", @"K:\Actorwright\Data",
                    "--plugins", "Skyrim.esm,skyrim.esm"
                ]));
        Assert(!duplicate.IsValid && duplicate.ErrorMessage is not null,
            "Duplicate enabled plugins were accepted by the external context binder.");
    }

    private static async Task<(CommandExitCode Exit, string Output, string Error)>
        RunHandler(
            FinishExternalSmpFixture fixture,
            RecordingInstallContextService service,
            string[] args,
            CancellationToken cancellationToken)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var handler = new SkyrimNpcFinishCoreCommandHandler(
            service, fixture.Workspace, output, error);
        CommandExitCode exit = await handler.RunAsync(
            CommandLine.Parse(args), cancellationToken);
        return (exit, output.ToString(), error.ToString());
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

internal sealed class RecordingInstallContextService(FinishExternalSmpFixture fixture) :
    ISkyrimNpcFinishCoreInstallContextService
{
    public int AnalyzeCalls { get; private set; }
    public int ContextAnalyzeCalls { get; private set; }
    public int ApplyCalls { get; private set; }
    public int ContextApplyCalls { get; private set; }
    public int VerifyCalls { get; private set; }
    public int ContextVerifyCalls { get; private set; }

    public ExternalHeadPartInstallVerificationContext? ContextAnalyzeContext { get; private set; }
    public ExternalHeadPartInstallVerificationContext? ContextApplyContext { get; private set; }
    public ExternalHeadPartInstallVerificationContext? ContextVerifyContext { get; private set; }

    public SkyrimNpcFinishCoreApplyResult ApplyResult { get; set; } =
        fixture.AppliedResult;

    public SkyrimNpcFinishCoreVerificationResult VerifyResult { get; set; } =
        fixture.ContextFreeVerificationResult;

    public int TotalOperationCalls =>
        AnalyzeCalls + ContextAnalyzeCalls + ApplyCalls + ContextApplyCalls +
        VerifyCalls + ContextVerifyCalls;

    public ValueTask<SkyrimNpcFinishCoreProposalResult> AnalyzeAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AnalyzeCalls++;
        return ValueTask.FromResult(fixture.ContextFreeProposalResult);
    }

    public ValueTask<SkyrimNpcFinishCoreProposalResult>
        AnalyzeWithInstallContextAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContextAnalyzeCalls++;
        ContextAnalyzeContext = installContext;
        return ValueTask.FromResult(fixture.ContextfulProposalResult);
    }

    public ValueTask<SkyrimNpcFinishCoreApplyResult> ApplyAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ApplyCalls++;
        return ValueTask.FromResult(fixture.ContextFreeApplyResult);
    }

    public ValueTask<SkyrimNpcFinishCoreApplyResult>
        ApplyWithInstallContextAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            SkyrimNpcFinishCoreProposal proposal,
            Sha256Hash proposalSha256,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContextApplyCalls++;
        ContextApplyContext = installContext;
        return ValueTask.FromResult(ApplyResult);
    }

    public ValueTask<SkyrimNpcFinishCoreVerificationResult> VerifyAsync(
        WorkspacePath manifestPath,
        Sha256Hash manifestSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        VerifyCalls++;
        return ValueTask.FromResult(VerifyResult);
    }

    public ValueTask<SkyrimNpcFinishCoreVerificationResult>
        VerifyWithInstallContextAsync(
            WorkspacePath manifestPath,
            Sha256Hash manifestSha256,
            ExternalHeadPartInstallVerificationContext installContext,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ContextVerifyCalls++;
        ContextVerifyContext = installContext;
        return ValueTask.FromResult(VerifyResult);
    }

    public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateAnalyzeAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        WorkspacePath proposalPath,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public ValueTask<SkyrimNpcFinishCoreValidationResult> ValidateApplyAsync(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreProposal proposal,
        Sha256Hash proposalSha256,
        CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public void Reset()
    {
        AnalyzeCalls = 0;
        ContextAnalyzeCalls = 0;
        ApplyCalls = 0;
        ContextApplyCalls = 0;
        VerifyCalls = 0;
        ContextVerifyCalls = 0;
        ContextAnalyzeContext = null;
        ContextApplyContext = null;
        ContextVerifyContext = null;
    }
}

internal sealed class FinishExternalSmpFixture : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    internal FinishExternalSmpFixture()
    {
        Root = Path.Combine(
            Environment.CurrentDirectory,
            "artifacts",
            "finish-core-external-smp-cli-" + Guid.NewGuid().ToString("N"));
        Workspace = new WorkspacePath(Root);
        DataRoot = new WorkspacePath(Path.Combine(Root, "Data"));
        string packageRoot = Path.Combine(Root, "package");
        PackageRoot = new WorkspacePath(packageRoot);
        ArchivePath = new WorkspacePath(Path.Combine(Root, "package.zip"));
        RequestPath = new WorkspacePath(Path.Combine(Root, "request.json"));
        ReadyProposalPath = new WorkspacePath(Path.Combine(Root, "ready-proposal.json"));
        ManifestPath = new WorkspacePath(Path.Combine(Root, "manifest.json"));
        SourcePackageManifestPath = new WorkspacePath(
            Path.Combine(packageRoot, "manifest.json"));
        Directory.CreateDirectory(DataRoot.Value);
        Directory.CreateDirectory(Path.Combine(packageRoot, "Data", "NPCManager", "Evidence"));
        Directory.CreateDirectory(Path.Combine(packageRoot, "Data"));

        SourcePlugin = new PluginName("Output.esp");
        OutputPlugin = SourcePlugin;
        SkyrimMaster = new PluginName("Skyrim.esm");
        RacePlugin = new PluginName("Race.esp");
        ProviderPlugin = new PluginName("OrchidAdornment.esp");
        TargetRace = new FormReference(RacePlugin, new FormId(0x900));
        SkyrimMasterPath = new WorkspacePath(
            Path.Combine(DataRoot.Value, SkyrimMaster.Value));
        RacePluginPath = new WorkspacePath(
            Path.Combine(DataRoot.Value, RacePlugin.Value));
        ProviderPluginPath = new WorkspacePath(
            Path.Combine(DataRoot.Value, ProviderPlugin.Value));
        ProviderModelPath = new WorkspacePath(Path.Combine(
            DataRoot.Value, "meshes", "actors", "character", "hair", "orchid-root.nif"));
        ProviderPhysicsPath = new WorkspacePath(Path.Combine(
            DataRoot.Value, "SKSE", "Plugins", "hdtSkinnedMeshConfigs", "orchid-root.xml"));
        ProviderDefaultBbpPath = new WorkspacePath(Path.Combine(
            DataRoot.Value, "SKSE", "Plugins", "hdtSkinnedMeshConfigs", "defaultBBPs.xml"));
        ProviderTriPath = new WorkspacePath(Path.Combine(
            DataRoot.Value, "meshes", "actors", "character", "hair", "orchid-root.tri"));
        ProviderColliderPath = new WorkspacePath(Path.Combine(
            DataRoot.Value, "meshes", "actors", "character", "hair", "orchid-collider.nif"));
        FaceGeomPath = new AssetPath(
            "Data/NPCManager/FaceGeom/orchid-root.nif");
        SourcePresetPath = new AssetPath(
            "Data/NPCManager/Evidence/orchid-root.jslot");
        WriteSkyrimMaster(SkyrimMasterPath.Value, SkyrimMaster);
        WriteRacePlugin(RacePluginPath.Value, RacePlugin, SkyrimMaster);
        WriteProviderPlugin(ProviderPluginPath.Value, ProviderPlugin);
        WriteNpcPlugin(
            Path.Combine(packageRoot, "Data", OutputPlugin.Value),
            OutputPlugin,
            TargetRace,
            ProviderPlugin);
        File.Copy(
            SkyrimMasterPath.Value,
            Path.Combine(packageRoot, "Data", SkyrimMaster.Value),
            overwrite: true);
        File.Copy(
            RacePluginPath.Value,
            Path.Combine(packageRoot, "Data", RacePlugin.Value),
            overwrite: true);
        File.Copy(
            Path.Combine(packageRoot, "Data", OutputPlugin.Value),
            Path.Combine(DataRoot.Value, OutputPlugin.Value),
            overwrite: true);
        Directory.CreateDirectory(Path.GetDirectoryName(ProviderModelPath.Value)!);
        File.WriteAllBytes(
            ProviderModelPath.Value,
            BuildProviderNifForTests(
                null,
                "meshes/actors/character/hair/orchid-root.tri"));
        File.WriteAllBytes(ProviderTriPath.Value, "orchid-provider-tri"u8.ToArray());
        Directory.CreateDirectory(Path.GetDirectoryName(ProviderPhysicsPath.Value)!);
        File.WriteAllBytes(
            ProviderPhysicsPath.Value,
            Encoding.UTF8.GetBytes(BuildPhysicsXml(
                new AssetPath("meshes/actors/character/hair/orchid-collider.nif"))));
        File.WriteAllText(
            ProviderDefaultBbpPath.Value,
            "<defaultBBPs>" +
            "<map shape=\"HairPhysicsShape\" file=\"SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml\" />" +
            "<map shape=\"HairCollisionShape\" file=\"SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml\" />" +
            "</defaultBBPs>");
        File.WriteAllBytes(ProviderColliderPath.Value, "orchid-provider-collider"u8.ToArray());
        string faceGeomPath = Path.Combine(
            packageRoot, FaceGeomPath.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(faceGeomPath)!);
        File.WriteAllBytes(faceGeomPath, "orchid-facegeom-nif"u8.ToArray());
        File.WriteAllBytes(
            Path.Combine(packageRoot, SourcePresetPath.Value.Replace('/', Path.DirectorySeparatorChar)),
            "orchid-root-racemenu-preset"u8.ToArray());

        Descriptor = CreateDescriptor();
        Attestation = CreateAttestation(Descriptor);
        SelectedManifestPath = new AssetPath(
            SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath);
        SelectedManifestBytes = CreateSelectedManifest();
        string selectedPath = Path.Combine(
            packageRoot,
            SelectedManifestPath.Value.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(selectedPath)!);
        File.WriteAllBytes(selectedPath, SelectedManifestBytes);
        PromotedBindingPath = new AssetPath(
            SkyrimNpcFinishCoreDocumentCodec.CanonicalPromotedOutputBindingPath);
        byte[] promotedBindingBytes = CreatePromotedOutputBinding();
        string promotedBindingPath = Path.Combine(
            packageRoot,
            PromotedBindingPath.Value.Replace('/', Path.DirectorySeparatorChar));
        File.WriteAllBytes(promotedBindingPath, promotedBindingBytes);
        File.WriteAllBytes(
            SourcePackageManifestPath.Value,
            CreateSourcePackageManifest());
        PackageManifestHash = Hash(File.ReadAllBytes(SourcePackageManifestPath.Value));
        PackageTreeHash = SkyrimNpcFinishCoreSourcePackageReader
            .ComputePackageTreeSha256(PackageRoot);
        WriteArchive();

        Fingerprint = CreateFingerprint();
        DeclaredArtifact = CreateArtifact(
            ExternalInstallDependencyState.DeclaredUnverified,
            null,
            null,
            ExternalHeadPartDiagnosticCodes.InstallContextAbsent);
        VerifiedArtifact = CreateArtifact(
            ExternalInstallDependencyState.Verified,
            Fingerprint,
            true,
            null);
        Request = CreateRequest();
        RequestBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            Request, Workspace);
        File.WriteAllBytes(RequestPath.Value, RequestBytes);
        RequestHash = Hash(RequestBytes);

        ContextFreeProposalResult = WriteProposal(
            "context-free-proposal.json",
            DeclaredArtifact,
            SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired,
            null);
        ContextfulProposalResult = WriteProposal(
            "contextful-proposal.json",
            VerifiedArtifact,
            SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
            Fingerprint);
        ReadyProposalPath = ContextfulProposalResult.ProposalPath!.Value;
        ReadyProposalHash = ContextfulProposalResult.ProposalSha256!.Value;

        Manifest = CreateManifest();
        byte[] manifestBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
            Manifest, Workspace);
        File.WriteAllBytes(ManifestPath.Value, manifestBytes);
        ManifestHash = Hash(manifestBytes);

        ContextFreeVerificationResult = VerificationResult(
            DeclaredArtifact,
            SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired,
            verified: true);
        ContextfulVerificationResult = VerificationResult(
            VerifiedArtifact with
            {
                HistoricalSnapshotValid = true,
                VerifiedInstallSnapshot = new ExternalHeadPartVerifiedInstallSnapshot(
                    Hash(SelectedManifestBytes), [Descriptor.DescriptorId], Fingerprint)
            },
            SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired,
            verified: true);
        ContextFreeApplyResult = new(
            false, null, null, null,
            [new Diagnostic(
                ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                DiagnosticSeverity.Error,
                "External apply requires a fresh context.")]);
        AppliedResult = new(
            true, Manifest, PackageRoot, ArchivePath, []);
        FingerprintMismatchApplyResult = new(
            false, null, null, null,
            [new Diagnostic(
                ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                DiagnosticSeverity.Error,
                "The supplied context differs from the proposal fingerprint.")]);
    }

    internal string Root { get; }
    internal WorkspacePath Workspace { get; }
    internal WorkspacePath DataRoot { get; }
    internal WorkspacePath PackageRoot { get; }
    internal WorkspacePath ArchivePath { get; }
    internal WorkspacePath RequestPath { get; }
    internal WorkspacePath ReadyProposalPath { get; private set; }
    internal WorkspacePath ManifestPath { get; }
    internal WorkspacePath SourcePackageManifestPath { get; }
    internal Sha256Hash PackageManifestHash { get; }
    internal Sha256Hash PackageTreeHash { get; }
    internal PluginName SourcePlugin { get; }
    internal PluginName OutputPlugin { get; }
    internal PluginName SkyrimMaster { get; }
    internal PluginName RacePlugin { get; }
    internal PluginName ProviderPlugin { get; }
    internal WorkspacePath SkyrimMasterPath { get; }
    internal WorkspacePath RacePluginPath { get; }
    internal WorkspacePath ProviderPluginPath { get; }
    internal WorkspacePath ProviderModelPath { get; }
    internal WorkspacePath ProviderPhysicsPath { get; }
    internal WorkspacePath ProviderDefaultBbpPath { get; }
    internal WorkspacePath ProviderTriPath { get; }
    internal WorkspacePath ProviderColliderPath { get; }
    internal AssetPath FaceGeomPath { get; }
    internal AssetPath SourcePresetPath { get; }
    internal FormReference TargetRace { get; }
    internal ImmutableArray<PluginName> EnabledPluginOrder =>
        [SkyrimMaster, TargetRace.Plugin, ProviderPlugin];
    internal string EnabledPlugins => string.Join(',',
        EnabledPluginOrder.Select(plugin => plugin.Value));
    internal ExternalHeadPartDependencyDescriptor Descriptor { get; }
    internal ExternalHeadPartFaceGeomExclusionAttestation Attestation { get; }
    internal AssetPath SelectedManifestPath { get; }
    internal byte[] SelectedManifestBytes { get; }
    internal AssetPath PromotedBindingPath { get; }
    internal ExternalHeadPartInstallContextFingerprint Fingerprint { get; }
    internal SkyrimNpcFinishCoreRequest Request { get; }
    internal byte[] RequestBytes { get; }
    internal Sha256Hash RequestHash { get; }
    internal SkyrimNpcFinishCoreProposalResult ContextFreeProposalResult { get; }
    internal SkyrimNpcFinishCoreProposalResult ContextfulProposalResult { get; }
    internal Sha256Hash ReadyProposalHash { get; }
    internal SkyrimNpcFinishCoreManifest Manifest { get; }
    internal Sha256Hash ManifestHash { get; }
    internal ExternalHeadPartInstallVerificationArtifact DeclaredArtifact { get; }
    internal ExternalHeadPartInstallVerificationArtifact VerifiedArtifact { get; }
    internal SkyrimNpcFinishCoreVerificationResult ContextFreeVerificationResult { get; }
    internal SkyrimNpcFinishCoreVerificationResult ContextfulVerificationResult { get; }
    internal SkyrimNpcFinishCoreApplyResult ContextFreeApplyResult { get; }
    internal SkyrimNpcFinishCoreApplyResult AppliedResult { get; }
    internal SkyrimNpcFinishCoreApplyResult FingerprintMismatchApplyResult { get; }

    internal SkyrimNpcFinishCoreSourcePackageReader CreateSourceReader()
    {
        var policy = new KOnlyWorkspacePolicy(
            Workspace,
            new WorkspacePath(Path.Combine(Root, "protected-live")));
        var manifestReader = new PackageManifestReader(policy, Workspace);
        var packageVerifier = new PackageVerifyService(manifestReader);
        return new SkyrimNpcFinishCoreSourcePackageReader(
            Workspace,
            policy,
            manifestReader,
            packageVerifier,
            new BethesdaSkyrimNpcFinishCoreSourceReader());
    }

    internal SkyrimNpcFinishCoreService CreateConcreteService() =>
        new(
            CreateSourceReader().InspectAsync,
            Workspace,
            new BethesdaExternalHeadPartInstallVerifier());

    internal ExternalHeadPartInstallVerificationContext CreateInstallContext() =>
        new(DataRoot, EnabledPluginOrder, TargetRace);

    internal ExternalHeadPartInstallVerificationRequest CreateVerificationRequest(
        ExternalHeadPartInstallVerificationContext context) =>
        new(
            PackageRoot,
            new WorkspacePath(Path.Combine(
                PackageRoot.Value, "Data", OutputPlugin.Value)),
            OutputPlugin,
            HashFile(Path.Combine(PackageRoot.Value, "Data", OutputPlugin.Value)),
            new WorkspacePath(Path.Combine(
                PackageRoot.Value,
                SelectedManifestPath.Value.Replace('/', Path.DirectorySeparatorChar))),
            Hash(SelectedManifestBytes),
            context,
            RequireCurrentAuthority: true,
            TargetActorFormId: Request.Actor.FormId);

    private byte[] CreateSourcePackageManifest()
    {
        string[] paths = Directory.EnumerateFiles(
                PackageRoot.Value, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(PackageRoot.Value, path)
                .Replace(Path.DirectorySeparatorChar, '/'))
            .Where(path => !string.Equals(path, "manifest.json",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var artifacts = paths.Select(relative =>
        {
            string physical = Path.Combine(
                PackageRoot.Value,
                relative.Replace('/', Path.DirectorySeparatorChar));
            byte[] bytes = File.ReadAllBytes(physical);
            return new
            {
                kind = string.Equals(relative,
                    SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath,
                    StringComparison.Ordinal)
                    ? "external-headpart-dependencies"
                    : string.Equals(relative, "Data/" + OutputPlugin.Value,
                        StringComparison.Ordinal)
                        ? "plugin"
                        : relative.StartsWith("Data/", StringComparison.Ordinal)
                            ? "provider"
                            : "asset",
                relativePath = relative,
                byteLength = bytes.LongLength,
                sha256 = Hash(bytes).Value
            };
        }).ToArray();
        string presetPath = Path.Combine(
            PackageRoot.Value,
            SourcePresetPath.Value.Replace('/', Path.DirectorySeparatorChar));
        var manifest = new
        {
            schemaVersion = 1,
            edition = GameEdition.SkyrimSpecialEdition.ToString(),
            presetFormat = PresetFormat.RaceMenuJslot.ToString(),
            sourcePreset = SourcePresetPath.Value,
            sourcePresetSha256 = HashFile(presetPath).Value,
            sourcePlugin = OutputPlugin.Value,
            sourcePluginSha256 = HashFile(Path.Combine(
                PackageRoot.Value, "Data", OutputPlugin.Value)).Value,
            outputPlugin = OutputPlugin.Value,
            targetFormId = "0x00000800",
            artifacts
        };
        return JsonSerializer.SerializeToUtf8Bytes(manifest, JsonOptions);
    }

    public void Dispose()
    {
        if (Directory.Exists(Root))
            Directory.Delete(Root, recursive: true);
    }

    private SkyrimNpcFinishCoreRequest CreateRequest()
    {
        JsonObject node = new()
        {
            ["schema"] = SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
            ["source"] = new JsonObject
            {
                ["packageRoot"] = "package",
                ["packageManifest"] = "package/manifest.json",
                ["packageManifestSha256"] = PackageManifestHash.Value,
                ["packageTreeSha256"] = PackageTreeHash.Value,
                ["pluginPath"] = "package/Data/" + SourcePlugin.Value,
                ["plugin"] = SourcePlugin.Value,
                ["pluginSha256"] = HashFile(
                    Path.Combine(PackageRoot.Value, "Data", SourcePlugin.Value)).Value
            },
            ["actor"] = new JsonObject
            {
                ["editorId"] = "ExternalFixtureNpc",
                ["formId"] = "0x00000800"
            },
            ["authorities"] = new JsonObject
            {
                ["bodyRoute"] = "Cbbe3Ba",
                ["providers"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["plugin"] = SkyrimMaster.Value,
                        ["path"] = "package/Data/" + SkyrimMaster.Value,
                        ["sha256"] = HashFile(SkyrimMasterPath.Value).Value,
                        ["byteLength"] = new FileInfo(SkyrimMasterPath.Value).Length
                    }
                },
                ["additionalMasters"] = new JsonArray(),
                ["actorAssemblySha256"] = null,
                ["bodyOwnerSha256"] = null,
                ["protectedAppearanceTreeSha256"] = null,
                ["externalHeadParts"] = new JsonObject
                {
                    ["selectedManifestPath"] = SelectedManifestPath.Value,
                    ["selectedManifestSha256"] = Hash(SelectedManifestBytes).Value,
                    ["bindings"] = new JsonArray
                    {
                        new JsonObject
                        {
                            ["descriptorId"] = Descriptor.DescriptorId.Value,
                            ["faceGeomExclusionAttestationSha256"] =
                                Attestation.AttestationSha256.Value
                        }
                    }
                }
            },
            ["followerPolicy"] = new JsonObject
            {
                ["recruitable"] = true,
                ["defensiveOnly"] = true,
                ["potentialFollowerFaction"] = "Skyrim.esm|0x0005C84D",
                ["currentFollowerFaction"] = "Skyrim.esm|0x0005C84E",
                ["relationshipRank"] = "Ally"
            },
            ["aiPolicy"] = new JsonObject
            {
                ["aggression"] = "Unaggressive",
                ["confidence"] = "Brave",
                ["energy"] = 50,
                ["morality"] = "NoCrime",
                ["assistance"] = "HelpsFriendsAndAllies",
                ["mood"] = "Neutral"
            },
            ["outfitPolicy"] = new JsonObject
            {
                ["policy"] = "PrivateOutfit",
                ["existingOutfit"] = null,
                ["armorItems"] = new JsonArray
                {
                    SkyrimMaster.Value + "|0x00000800"
                }
            },
            ["inventoryPolicy"] = new JsonObject
            {
                ["policy"] = "PreserveInventory",
                ["expectedSourceItems"] = new JsonArray(),
                ["desiredItems"] = new JsonArray()
            },
            ["sandboxAuthority"] = new JsonObject
            {
                ["copiedMaster"] = "package/Data/" + SkyrimMaster.Value,
                ["copiedMasterSha256"] = HashFile(Path.Combine(PackageRoot.Value, "Data", SkyrimMaster.Value)).Value,
                ["template"] = "Skyrim.esm|0x0001B217",
                ["templateEditorId"] = "DefaultSandboxEditorLocation512",
                ["rawRecordDigest"] = new string('D', 64)
            },
            ["output"] = new JsonObject
            {
                ["root"] = "package",
                ["archive"] = "package.zip",
                ["pluginFileName"] = OutputPlugin.Value
            }
        };
        return SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
            JsonSerializer.SerializeToUtf8Bytes(node, JsonOptions), Workspace);
    }

    private SkyrimNpcFinishCoreProposalResult WriteProposal(
        string fileName,
        ExternalHeadPartInstallVerificationArtifact artifact,
        SkyrimNpcFinishCoreStatus status,
        ExternalHeadPartInstallContextFingerprint? fingerprint)
    {
        var authority = new SkyrimNpcFinishCoreExternalHeadPartAuthority(
            SelectedManifestPath,
            Hash(SelectedManifestBytes),
            [new SkyrimNpcFinishCoreExternalHeadPartBinding(
                Descriptor.DescriptorId,
                Attestation.AttestationSha256)]);
        var proposal = new SkyrimNpcFinishCoreProposal
        {
            Schema = SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier,
            RequestSha256 = RequestHash,
            Request = Request,
            Status = status,
            ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartProposalAuthority(
                authority, artifact, fingerprint)
        };
        byte[] withoutHash = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal, Workspace);
        Sha256Hash proposalHash = SkyrimNpcFinishCoreDocumentCodec
            .HashProposalWithoutSelf(withoutHash);
        proposal = proposal with { ProposalSha256 = proposalHash };
        byte[] bytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            proposal, Workspace);
        WorkspacePath path = new(Path.Combine(Root, fileName));
        File.WriteAllBytes(path.Value, bytes);
        return new(true, proposal, path, proposalHash, []);
    }

    private SkyrimNpcFinishCoreManifest CreateManifest()
    {
        var snapshot = new ExternalHeadPartVerifiedInstallSnapshot(
            Hash(SelectedManifestBytes), [Descriptor.DescriptorId], Fingerprint);
        return new SkyrimNpcFinishCoreManifest
        {
            Schema = SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier,
            Plugin = OutputPlugin,
            PluginSha256 = HashFile(Path.Combine(
                PackageRoot.Value, "Data", OutputPlugin.Value)),
            BaseNpc = new FormReference(OutputPlugin, new FormId(0x800)),
            RequestSha256 = RequestHash,
            ProposalSha256 = ContextfulProposalResult.ProposalSha256,
            PackageRoot = PackageRoot,
            Archive = ArchivePath,
            ArchiveSha256 = HashFile(ArchivePath.Value),
            SourcePackageTreeSha256 = PackageTreeHash,
            PackageTreeSha256 = PackageTreeHash,
            RuntimeIdentity = new SkyrimNpcFinishCoreRuntimeIdentity
            {
                BaseNpc = new FormReference(OutputPlugin, new FormId(0x800)),
                PlacementIncluded = false
            },
            Evidence = new SkyrimNpcFinishCoreManifestEvidence
            {
                Files = [new SkyrimNpcFinishCoreEvidenceEntry
                {
                    Path = new AssetPath("Data/" + OutputPlugin.Value),
                    ByteLength = new FileInfo(Path.Combine(
                        PackageRoot.Value, "Data", OutputPlugin.Value)).Length,
                    Sha256 = HashFile(Path.Combine(
                        PackageRoot.Value, "Data", OutputPlugin.Value))
                }],
                PackageTreeSha256 = PackageTreeHash,
                SourcePackageTreeSha256 = PackageTreeHash
            },
            ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartManifestAuthority(
                SelectedManifestPath,
                Hash(SelectedManifestBytes),
                [Descriptor],
                [Attestation],
                snapshot)
            {
                PromotedOutputBindingPath = PromotedBindingPath,
                PromotedOutputBindingSha256 = HashFile(Path.Combine(
                    PackageRoot.Value,
                    PromotedBindingPath.Value.Replace('/', Path.DirectorySeparatorChar)))
            }
        };
    }

    private ExternalHeadPartInstallVerificationArtifact CreateArtifact(
        ExternalInstallDependencyState state,
        ExternalHeadPartInstallContextFingerprint? fingerprint,
        bool? enabled,
        string? diagnosticCode)
    {
        var observation = new ExternalHeadPartInstallProviderObservation(
            Descriptor.Provider.Plugin,
            Descriptor.Provider.PluginSha256,
            state == ExternalInstallDependencyState.Verified
                ? Descriptor.Provider.PluginSha256
                : null,
            enabled);
        ImmutableArray<ExternalHeadPartInstallPrerequisite> prerequisites =
            diagnosticCode is null
                ? []
                : [new ExternalHeadPartInstallPrerequisite(
                    diagnosticCode,
                    Descriptor.Provider.Plugin.Value,
                    Descriptor.Provider.PluginSha256,
                    null,
                    enabled,
                    diagnosticCode == ExternalHeadPartDiagnosticCodes.InstallContextAbsent
                        ? "Supply --data-root and --plugins and retry."
                        : "Repair the provider installation and retry.")];
        return new ExternalHeadPartInstallVerificationArtifact(
            ExternalHeadPartSchemaIdentifiers.InstallVerification,
            true,
            true,
            null,
            [Descriptor.DescriptorId],
            null,
            state,
            state == ExternalInstallDependencyState.Verified,
            state == ExternalInstallDependencyState.Verified,
            false,
            false,
            [observation],
            prerequisites);
    }

    private SkyrimNpcFinishCoreVerificationResult VerificationResult(
        ExternalHeadPartInstallVerificationArtifact artifact,
        SkyrimNpcFinishCoreStatus status,
        bool verified) => new(
        verified,
        new SkyrimNpcFinishCoreVerification
        {
            Schema = SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier,
            Status = status,
            Verified = verified,
            PlacementIncluded = false,
            RuntimeAuthority = false,
            VisualAuthority = false,
            PluginSha256 = HashFile(Path.Combine(
                PackageRoot.Value, "Data", OutputPlugin.Value)),
            PackageTreeSha256 = PackageTreeHash,
            SourcePackageTreeSha256 = PackageTreeHash,
            RuntimeIdentity = new SkyrimNpcFinishCoreRuntimeIdentity
            {
                BaseNpc = new FormReference(OutputPlugin, new FormId(0x800)),
                PlacementIncluded = false
            },
            ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartVerification(
                artifact)
        },
        []);

    private ExternalHeadPartDependencyDescriptor CreateDescriptor()
    {
        FormReference rootForm = new(ProviderPlugin, new FormId(0x800));
        var policy = new KOnlyWorkspacePolicy(
            Workspace,
            new WorkspacePath(Path.Combine(Root, "protected-live")));
        var authorities = EnabledPluginOrder.Select(plugin =>
        {
            WorkspacePath path = new(Path.Combine(DataRoot.Value, plugin.Value));
            return new SkyrimFaceRecordPluginAuthority(
                plugin, path, HashFile(path.Value));
        }).ToImmutableArray();
        SkyrimFaceRecordRouteResult route =
            new BethesdaSkyrimFaceRecordRouteResolver(policy, Workspace)
                .ResolveAsync(
                    new SkyrimFaceRecordRouteRequest(
                        GameEdition.SkyrimSpecialEdition,
                        TargetRace,
                        NpcSex.Female,
                        [new SkyrimFaceRecordHeadPartSelection(rootForm, [])],
                        authorities),
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        if (!route.Accepted || route.Route is null ||
            route.Route.HeadPartGraph.Length != 1)
            throw new InvalidDataException(
                "The authentic external SMP route fixture was refused: " +
                string.Join(" | ", route.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));
        SkyrimFaceHeadPartGraphRoute routeMember = route.Route.HeadPartGraph[0];
        FormReference winningForm = routeMember.WinningForm;
        PluginName provider = routeMember.WinningPlugin;
        AssetPath model = routeMember.ModelNif ??
            throw new InvalidDataException("The authentic provider route omitted its NIF model.");
        Sha256Hash providerHash = HashFile(ProviderPluginPath.Value);
        Sha256Hash modelHash = HashFile(ProviderModelPath.Value);
        Sha256Hash physicsHash = HashFile(ProviderPhysicsPath.Value);
        var member = new ExternalHeadPartRecordDependency(
            routeMember.OriginForm,
            routeMember.RequiredOutputMaster,
            winningForm,
            provider,
            routeMember.WinningPluginSha256,
            routeMember.WinningPluginByteLength,
            routeMember.WinningRecordSha256,
            routeMember.EditorId,
            routeMember.DeclaredType,
            routeMember.EffectiveType,
            model,
            routeMember.TriRoutes,
            routeMember.HnamEdges,
            routeMember.Parent,
            routeMember.Depth,
            routeMember.RouteOrder,
            routeMember.AppliesToSex,
            routeMember.ValidRace);
        var descriptor = new ExternalHeadPartDependencyDescriptor(
            ExternalHeadPartSchemaIdentifiers.Descriptor,
            Hash("orchid-root-source-form"u8),
            ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
            rootForm,
            rootForm,
            NpcHeadPartType.Hair,
            Hash("orchid-headpart-graph"u8),
            new ExternalHeadPartProviderIdentity(
                provider,
                providerHash,
                new FileInfo(ProviderPluginPath.Value).Length,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired),
            [member],
            new ExternalHeadPartPhysicsBinding(
                ExternalHeadPartPhysicsBindingMode.DefaultBbpMap,
                [
                    new ExternalHeadPartPhysicsShapeBinding(
                        rootForm,
                        model,
                        "HairPhysicsShape",
                        new AssetPath("SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml"),
                        physicsHash,
                        new FileInfo(ProviderPhysicsPath.Value).Length),
                    new ExternalHeadPartPhysicsShapeBinding(
                        rootForm,
                        model,
                        "HairCollisionShape",
                        new AssetPath("SKSE/Plugins/hdtSkinnedMeshConfigs/orchid-root.xml"),
                        physicsHash,
                        new FileInfo(ProviderPhysicsPath.Value).Length)
                ],
                new ExternalHeadPartPhysicsMappingAuthority(
                    new AssetPath("SKSE/Plugins/hdtSkinnedMeshConfigs/defaultBBPs.xml"),
                    HashFile(ProviderDefaultBbpPath.Value),
                    new FileInfo(ProviderDefaultBbpPath.Value).Length)),
            [new ExternalHeadPartAssetDependency(
                model,
                 modelHash,
                 new FileInfo(ProviderModelPath.Value).Length,
                 provider,
                 providerHash,
                 null)],
            [new ExternalHeadPartRuntimePrerequisite(
                "skse-plugin", "FSMP present", "SKSE/Plugins/hdtSMP64.dll")]);
        return descriptor with
        {
            DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                .ComputeDescriptorId(descriptor)
        };
    }

    private ExternalHeadPartFaceGeomExclusionAttestation CreateAttestation(
        ExternalHeadPartDependencyDescriptor descriptor)
    {
        string faceGeomPath = Path.Combine(
            PackageRoot.Value,
            FaceGeomPath.Value.Replace('/', Path.DirectorySeparatorChar));
        var attestation = new ExternalHeadPartFaceGeomExclusionAttestation(
            ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
            Hash("orchid-facegeom-attestation"u8),
            descriptor.DescriptorId,
            FaceGeomPath,
            HashFile(faceGeomPath),
            new FileInfo(faceGeomPath).Length,
            [],
            [
                new ExternalHeadPartExcludedShapeEvidence(
                    descriptor.Members[0].ModelNif!.Value,
                    "HairPhysicsShape"),
                new ExternalHeadPartExcludedShapeEvidence(
                    descriptor.Members[0].ModelNif!.Value,
                    "HairCollisionShape")
            ],
            [new ExternalHeadPartExcludedMetadataEvidence(
                "physics-locator", "HDT Skinned Mesh Physics Object")],
            "preview254-exclusion-v1");
        return attestation with
        {
            AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                .ComputeAttestationHash(attestation)
        };
    }

    private ExternalHeadPartInstallContextFingerprint CreateFingerprint()
    {
        var observations = ImmutableArray.Create(
            new ExternalHeadPartInstallObservation(
                "plugin", Descriptor.Provider.Plugin.Value,
                Descriptor.Provider.PluginSha256,
                Descriptor.Provider.PluginByteLength, 0),
            new ExternalHeadPartInstallObservation(
                "record", "OrchidAdornment.esp|0x00000800",
                Descriptor.Members[0].WinningRecordSha256,
                Descriptor.Provider.PluginByteLength,
                1));
        return new ExternalHeadPartInstallContextFingerprint(
            ExternalHeadPartDependencyDescriptorCodec
                .ComputeInstallContextFingerprintHash(observations),
            observations);
    }

    private byte[] CreateSelectedManifest()
    {
        Sha256Hash presetHash = Hash("orchid-fixture-preset"u8);
        RaceMenuNpcFormBinding binding = new(
            new RecordSignature("HDPT"),
            Descriptor.RootSourceForm,
            Descriptor.RootWinningForm,
            ProviderPlugin,
            ProviderPluginPath,
            Descriptor.Provider.PluginSha256,
            NpcHeadPartType.Hair);
        var draft = new RaceMenuPresetRecordAuthorityDraft(
            "finish-core-external-smp-cli",
            new PresetDocument(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                null!,
                presetHash,
                []),
            null!,
            null!,
            [new RaceMenuPresetHeadPartAuthority(null!, binding)],
            null!,
            null!,
            0,
            new FormId(0x800),
            null!,
            RuntimeAuthority: false);
        Sha256Hash modelHash = HashFile(ProviderModelPath.Value);
        var model = new SkyrimAssetAuthority(
            ProviderPlugin.Value,
            AssetProviderKind.Loose,
            ProviderModelPath,
            modelHash,
            Descriptor.Assets[0].Path,
            new FileInfo(ProviderModelPath.Value).Length,
            modelHash);
        string outputPluginPath = Path.Combine(
            PackageRoot.Value, "Data", OutputPlugin.Value);
        Sha256Hash outputHash = HashFile(outputPluginPath);
        var group = new RaceMenuSelectedDependencyManifestExternalInstallDependency(
            Descriptor,
            Attestation,
            new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                OutputPlugin,
                outputHash,
                new FileInfo(outputPluginPath).Length,
                [SkyrimMaster, TargetRace.Plugin, ProviderPlugin],
                [new FormReference(ProviderPlugin, new FormId(0x800))]),
            Attestation.OutputFaceGeomPath,
            Attestation.OutputFaceGeomSha256,
            Attestation.OutputFaceGeomByteLength);
        WorkspacePath destination = new(Path.Combine(
            PackageRoot.Value,
            SelectedManifestPath.Value.Replace('/', Path.DirectorySeparatorChar)));
        RaceMenuSelectedDependencyManifestWriteResult result =
            new RaceMenuSelectedDependencyManifestWriter(
                    new KOnlyWorkspacePolicy(
                        Workspace,
                        new WorkspacePath(Path.Combine(Root, "protected-live"))),
                    Workspace)
                .WriteAsync(
                    new RaceMenuSelectedDependencyManifestWriteRequest(
                        presetHash,
                        draft,
                        [model],
                        [],
                        destination)
                    {
                        ExternalInstallDependencies = [group]
                    },
                    CancellationToken.None)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        if (!result.Written || result.Artifact?.SchemaVersion != 3)
            throw new InvalidOperationException(
                "The focused external SMP fixture could not write an authentic schema-3 selected manifest: " +
                string.Join(" | ", result.Diagnostics.Select(item => item.Message)));
        return File.ReadAllBytes(destination.Value);
    }

    private byte[] CreatePromotedOutputBinding()
    {
        string outputPluginPath = Path.Combine(
            PackageRoot.Value, "Data", OutputPlugin.Value);
        Sha256Hash outputHash = HashFile(outputPluginPath);
        long outputLength = new FileInfo(outputPluginPath).Length;
        ImmutableArray<PluginName> masterOrder =
            [TargetRace.Plugin, ProviderPlugin];
        ImmutableArray<FormReference> pnam =
            [new FormReference(ProviderPlugin, new FormId(0x800))];
        var group = new SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding(
            Descriptor.DescriptorId,
            Attestation.AttestationSha256,
            OutputPlugin,
            outputHash,
            outputLength,
            masterOrder,
            pnam);
        return SkyrimNpcFinishCorePromotedOutputBindingCodec.Serialize(
            new SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding(
                SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding
                    .SchemaIdentifierValue,
                SelectedManifestPath,
                Hash(SelectedManifestBytes),
                [group],
                OutputPlugin,
                outputHash,
                outputLength,
                masterOrder,
                pnam));
    }

    private static string BuildPhysicsXml(AssetPath collider) =>
        "<hdtSmp><bones><bone name=\"NPC Head [Head]\" /></bones>" +
        $"<colliders><collider path=\"{collider.Value}\" /></colliders></hdtSmp>";

    private void WriteArchive()
    {
        if (File.Exists(ArchivePath.Value))
            File.Delete(ArchivePath.Value);
        ZipFile.CreateFromDirectory(
            PackageRoot.Value,
            ArchivePath.Value,
            CompressionLevel.NoCompression,
            includeBaseDirectory: false);
    }

    private static void WriteSkyrimMaster(string path, PluginName plugin)
    {
        var key = ModKey.FromNameAndExtension(plugin.Value);
        var master = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        master.Armors.Add(new Armor(new FormKey(key, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ExternalFixtureArmor"
        });
        master.Races.Add(new Race(
            new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ExternalFixtureBaseRace",
            HeadData = new GenderedItem<HeadData?>(new HeadData(), new HeadData())
        });
        WritePlugin(master, path);
    }

    private static void WriteRacePlugin(
        string path,
        PluginName plugin,
        PluginName masterPlugin)
    {
        var key = ModKey.FromNameAndExtension(plugin.Value);
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension(masterPlugin.Value)
        });
        mod.Races.Add(new Race(
            new FormKey(key, 0x900), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ExternalFixtureRace",
            HeadData = new GenderedItem<HeadData?>(new HeadData(), new HeadData())
        });
        WritePlugin(mod, path);
    }

    private static void WritePlugin(SkyrimMod mod, string path) =>
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });

    private static void WriteProviderPlugin(string path, PluginName plugin)
    {
        var provider = new SkyrimMod(
            ModKey.FromNameAndExtension(plugin.Value), SkyrimRelease.SkyrimSE);
        provider.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("Skyrim.esm")
        });
        provider.HeadParts.Add(new HeadPart(
            new FormKey(ModKey.FromNameAndExtension(plugin.Value), 0x800),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "OrchidRootHair",
            Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
            Type = HeadPart.TypeEnum.Hair,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(
                    "meshes/actors/character/hair/orchid-root.nif")
            }
        });
        provider.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
    }

    private static void WriteNpcPlugin(
        string path,
        PluginName plugin,
        FormReference race,
        PluginName? externalProvider = null)
    {
        var outputKey = ModKey.FromNameAndExtension(plugin.Value);
        var mod = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension("Skyrim.esm")
        });
        mod.ModHeader.MasterReferences.Add(new MasterReference
        {
            Master = ModKey.FromNameAndExtension(race.Plugin.Value)
        });
        if (externalProvider is { } provider)
            mod.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(provider.Value)
            });
        var npc = new Npc(
            new FormKey(outputKey, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ExternalFixtureNpc",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            },
            Race = new FormLink<IRaceGetter>(new FormKey(
                ModKey.FromNameAndExtension(race.Plugin.Value), race.FormId.Value))
        };
        if (externalProvider is { } providerName)
            npc.HeadParts.Add(new FormLink<IHeadPartGetter>(new FormKey(
                ModKey.FromNameAndExtension(providerName.Value), 0x800)));
        mod.Npcs.Add(npc);
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
    }

    private static byte[] BuildProviderNifForTests(
        string? directPhysicsXml,
        string triPath)
    {
        var strings = new List<string>
        {
            "Root",
            "HairPhysicsShape",
            "HairCollisionShape",
            "BODYTRI",
            triPath
        };
        if (directPhysicsXml is not null)
        {
            strings.Add("HDT Skinned Mesh Physics Object");
            strings.Add(directPhysicsXml);
        }
        const int rootIndex = 0;
        const int physicsShapeIndex = 1;
        const int collisionShapeIndex = 2;
        const int metadataIndex = 3;
        ImmutableArray<int> rootExtras = directPhysicsXml is null
            ? []
            : [metadataIndex + 2];
        var blocks = new List<(ushort Type, byte[] Data)>
        {
            (0, BuildNodeBlock(
                NameIndex(strings, "Root"),
                rootExtras,
                [physicsShapeIndex, collisionShapeIndex])),
            (1, BuildShapeBlock(
                NameIndex(strings, "HairPhysicsShape"), metadataIndex)),
            (1, BuildShapeBlock(
                NameIndex(strings, "HairCollisionShape"), metadataIndex + 1)),
            (2, BuildMetadataBlock(
                NameIndex(strings, "BODYTRI"), NameIndex(strings, triPath))),
            (2, BuildMetadataBlock(
                NameIndex(strings, "BODYTRI"), NameIndex(strings, triPath)))
        };
        if (directPhysicsXml is not null)
            blocks.Add((2, BuildMetadataBlock(
                NameIndex(strings, "HDT Skinned Mesh Physics Object"),
                NameIndex(strings, directPhysicsXml))));

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes(
            "Gamebryo File Format, Version 20.2.0.7\n"));
        writer.Write(0x14020007U);
        writer.Write((byte)1);
        writer.Write(12U);
        writer.Write((uint)blocks.Count);
        writer.Write(100U);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((ushort)3);
        WriteSizedAscii(writer, "NiNode");
        WriteSizedAscii(writer, "BSTriShape");
        WriteSizedAscii(writer, "NiStringExtraData");
        foreach ((ushort type, _) in blocks)
            writer.Write(type);
        foreach ((_, byte[] data) in blocks)
            writer.Write((uint)data.Length);
        writer.Write((uint)strings.Count);
        writer.Write((uint)strings.Max(item => item.Length));
        foreach (string value in strings)
            WriteSizedAscii(writer, value);
        writer.Write(0U);
        foreach ((_, byte[] data) in blocks)
            writer.Write(data);
        writer.Write(1U);
        writer.Write(rootIndex);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildNodeBlock(
        int nameIndex,
        ImmutableArray<int> extras,
        ImmutableArray<int> children)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((uint)nameIndex);
        writer.Write((uint)extras.Length);
        foreach (int extra in extras)
            writer.Write(extra);
        writer.Write(-1);
        writer.Write(new byte[56]);
        writer.Write(-1);
        writer.Write((uint)children.Length);
        foreach (int child in children)
            writer.Write(child);
        writer.Write(0U);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildShapeBlock(int nameIndex, int metadataIndex)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((uint)nameIndex);
        writer.Write(1U);
        writer.Write(metadataIndex);
        writer.Write(-1);
        writer.Write(new byte[56]);
        writer.Write(-1);
        writer.Write(new byte[16]);
        writer.Write(-1);
        writer.Write(-1);
        writer.Write(-1);
        writer.Write(new byte[16]);
        writer.Flush();
        return stream.ToArray();
    }

    private static byte[] BuildMetadataBlock(int nameIndex, int targetIndex)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write((uint)nameIndex);
        writer.Write((uint)targetIndex);
        writer.Flush();
        return stream.ToArray();
    }

    private static int NameIndex(List<string> strings, string value)
    {
        for (var index = 0; index < strings.Count; index++)
            if (string.Equals(strings[index], value, StringComparison.Ordinal))
                return index;
        throw new InvalidOperationException(
            $"Fixture string was not found: {value}");
    }

    private static void WriteSizedAscii(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static Sha256Hash HashFile(string path) =>
        Hash(File.ReadAllBytes(path));

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
