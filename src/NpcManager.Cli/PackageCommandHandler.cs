using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal sealed class PackageCommandHandler(
    IPackageBuildService buildService,
    IPackageInspectService inspectService,
    IPackageVerifyService verifyService,
    IPackageArchiveService archiveService,
    WorkspacePath workspaceRoot,
    TextWriter output,
    TextWriter error,
    ISkyrimNpcFinishCoreService? finishCoreService = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly ImmutableHashSet<string> VerifyOptionNames =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase,
            "manifest", "strict-install-dependencies", "data-root", "plugins",
            "workflow-bundle", "workflow-bundle-sha256");
    private static readonly ImmutableHashSet<string> RuntimeVerifyOptionNames =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "archive", "payload", "include-runtime-preset");
    private static readonly ImmutableHashSet<string> ArchiveOptionNames =
        ImmutableHashSet.Create(StringComparer.OrdinalIgnoreCase, "package", "source-root", "output", "payload", "include-runtime-preset",
            "workflow-bundle", "workflow-bundle-sha256");

    internal async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command, CancellationToken cancellationToken)
    {
        return command.Name switch
        {
            "package build" => await RunBuildAsync(command, cancellationToken),
            "package inspect" => await RunInspectAsync(command, cancellationToken),
            "package verify" => await RunVerifyAsync(command, cancellationToken),
            "package archive" => await RunArchiveAsync(command, cancellationToken),
            _ => Usage(command.Json, $"Unknown package command '{command.Name}'.")
        };
    }

    private async ValueTask<CommandExitCode> RunBuildAsync(
        ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!command.Options.TryGetValue("source-root", out var sourceRoot) ||
            !command.Options.TryGetValue("output-root", out var outputRoot))
            return Usage(command.Json, "package build requires --source-root and --output-root.");
        try
        {
            var result = await buildService.BuildAsync(new PackageBuildRequest(
                new WorkspacePath(sourceRoot), new WorkspacePath(outputRoot)), cancellationToken);
            Write(command.Json, new BuildResponse(result.Written, result.Artifact, result.Diagnostics),
                result.Written ? "package build: PASS" : "package build: REFUSED");
            return Exit(result.Written, result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private async ValueTask<CommandExitCode> RunInspectAsync(
        ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!command.Options.TryGetValue("manifest", out var manifest))
            return Usage(command.Json, "package inspect requires --manifest.");
        try
        {
            var result = await inspectService.InspectAsync(
                new PackageInspectRequest(new WorkspacePath(manifest)), cancellationToken);
            Write(command.Json, new InspectResponse(result.Valid, result.Identity, result.Diagnostics),
                result.Valid ? "package inspect: PASS" : "package inspect: REFUSED");
            return Exit(result.Valid, result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private async ValueTask<CommandExitCode> RunVerifyAsync(
        ParsedCommand command, CancellationToken cancellationToken)
    {
        if (command.Options.TryGetValue("archive", out var archive))
        {
            if (command.Options.Keys.Any(key => !RuntimeVerifyOptionNames.Contains(key)) || !command.DuplicateOptions.IsDefaultOrEmpty)
                return Usage(command.Json, "Archive verification accepts only --archive, --payload runtime-only and optional --include-runtime-preset, each once; manifest/install inputs are separate.");
            if (!TryArchivePayload(command, out var payload, out var preset, out string errorMessage) || payload != PackageArchivePayload.RuntimeOnly)
                return Usage(command.Json, string.IsNullOrEmpty(errorMessage) ? "Archive verification requires --payload runtime-only." : errorMessage);
            try
            {
                var result = await archiveService.VerifyRuntimeAsync(new(new WorkspacePath(archive), preset), cancellationToken);
                Write(command.Json, result, result.Verified ? "package verify: PASS (runtime ZIP layout and hash inventory only)" : "package verify: REFUSED");
                return Exit(result.Verified, result.Diagnostics);
            }
            catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
        }
        if (!command.Options.TryGetValue("manifest", out var manifest))
            return Usage(command.Json, "package verify requires --manifest.");

        if (!TryParseVerifyOptions(command, out var strict, out var dataRoot,
                out var enabledPlugins, out var optionError))
            return Usage(command.Json, optionError);

        try
        {
            var manifestPath = new WorkspacePath(manifest);
            FinishPackageManifestReadLease? finishManifestLease;
            if (RequireReviewedFinishPackage(
                    command,
                    new WorkspacePath(Path.GetDirectoryName(manifestPath.Value)!),
                    out finishManifestLease) is { } gateRefusal)
                return gateRefusal;
            using (finishManifestLease)
            {
                if (await RequireFullFinishVerificationAsync(
                        command,
                        finishManifestLease,
                        cancellationToken) is { } finishRefusal)
                    return finishRefusal;
                var ordinary = await verifyService.VerifyAsync(
                    new PackageVerifyRequest(manifestPath), cancellationToken);
                if (RequireCurrentFinishManifest(command, finishManifestLease) is { } changedRefusal)
                    return changedRefusal;

                if (!strict)
                {
                    WriteVerify(command.Json, ordinary,
                        HumanVerify(ordinary, strict: false, strictSatisfied: ordinary.Verified));
                    return Exit(ordinary.Verified, ordinary.Diagnostics);
                }

                if (!ordinary.Verified)
                {
                    WriteVerify(command.Json, ordinary,
                        HumanVerify(ordinary, strict: true, strictSatisfied: false));
                    return Exit(false, ordinary.Diagnostics);
                }

                // Ordinary packages have no external authority to establish. Keep
                // the strict option useful for them without inventing a race.
                if (ordinary.ExternalInstallDependencyVerification is null)
                {
                    WriteVerify(command.Json, ordinary,
                        HumanVerify(ordinary, strict: true, strictSatisfied: true));
                    return Exit(true, ordinary.Diagnostics);
                }

                var externalEvidence = ordinary.ExternalInstallDependencyVerification;
                if (!HasCompleteExternalEvidence(ordinary, externalEvidence))
                {
                    var refused = ordinary with
                    {
                        Diagnostics = ordinary.Diagnostics.Add(TargetRaceUnavailable(
                            "The verified package did not expose complete external descriptor evidence."))
                    };
                    WriteVerify(command.Json, refused,
                        HumanVerify(refused, strict: true, strictSatisfied: false));
                    return Exit(false, refused.Diagnostics);
                }

                if (!TryValidateLoadOrderShape(dataRoot!.Value, enabledPlugins,
                        externalEvidence, out var loadOrderError))
                    return Usage(command.Json, loadOrderError!);

                if (!TryDeriveTargetRace(ordinary, out var targetRace,
                        out var derivationDiagnostic))
                {
                    var diagnostics = ordinary.Diagnostics.Add(derivationDiagnostic!);
                    var refused = ordinary with { Diagnostics = diagnostics };
                    WriteVerify(command.Json, refused,
                        HumanVerify(refused, strict: true, strictSatisfied: false));
                    return Exit(false, diagnostics);
                }

                var strictResult = await verifyService.VerifyAsync(
                    new PackageVerifyRequest(manifestPath)
                    {
                        InstallContext = new ExternalHeadPartInstallVerificationContext(
                            dataRoot!.Value, enabledPlugins, targetRace),
                        RequireInstallDependencyAuthority = true
                    },
                    cancellationToken);
                if (RequireCurrentFinishManifest(command, finishManifestLease) is { } strictChangedRefusal)
                    return strictChangedRefusal;
                var installReady = strictResult.Verified &&
                    strictResult.ExternalInstallDependencyVerification is
                    {
                        CurrentInstallDependencyState: ExternalInstallDependencyState.Verified,
                        InstallReady: true,
                        InstallDependencyAuthority: true
                    };
                WriteVerify(command.Json, strictResult,
                    HumanVerify(strictResult, strict: true,
                        strictSatisfied: installReady));
                return Exit(installReady, strictResult.Diagnostics);
            }
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private static bool TryParseVerifyOptions(
        ParsedCommand command,
        out bool strict,
        out WorkspacePath? dataRoot,
        out ImmutableArray<PluginName> enabledPlugins,
        out string errorMessage)
    {
        strict = false;
        dataRoot = null;
        enabledPlugins = [];
        errorMessage = string.Empty;

        if (command.Options.Keys.Any(key => !VerifyOptionNames.Contains(key)))
        {
            string unsupported = command.Options.Keys
                .Where(key => !VerifyOptionNames.Contains(key))
                .OrderBy(key => key, StringComparer.OrdinalIgnoreCase)
                .First();
            errorMessage = $"package verify does not accept --{unsupported}.";
            return false;
        }

        if (command.DuplicateOptions.Any(key => VerifyOptionNames.Contains(key)))
        {
            errorMessage = "package verify options may not be repeated.";
            return false;
        }

        if (command.Options.TryGetValue("strict-install-dependencies",
                out var strictValue))
        {
            if (command.ValuelessOptions.Contains(
                    "strict-install-dependencies", StringComparer.OrdinalIgnoreCase) ||
                !bool.TryParse(strictValue, out strict))
            {
                errorMessage = "--strict-install-dependencies must be true or false.";
                return false;
            }
        }

        bool hasDataRoot = command.Options.TryGetValue("data-root", out var dataRootValue);
        bool hasPlugins = command.Options.TryGetValue("plugins", out var pluginsValue);
        if (!strict)
        {
            if (hasDataRoot || hasPlugins)
            {
                errorMessage =
                    "--data-root and --plugins require --strict-install-dependencies true.";
                return false;
            }

            return true;
        }

        if (!hasDataRoot || string.IsNullOrWhiteSpace(dataRootValue))
        {
            errorMessage =
                "strict package verify requires --data-root <absolute K-local path>.";
            return false;
        }

        if (!IsAbsoluteKLocalPath(dataRootValue!))
        {
            errorMessage = "--data-root must be an absolute K-local path.";
            return false;
        }

        if (!hasPlugins || string.IsNullOrWhiteSpace(pluginsValue))
        {
            errorMessage =
                "strict package verify requires --plugins <complete enabled order>.";
            return false;
        }

        try
        {
            dataRoot = new WorkspacePath(dataRootValue!);
            var pluginValues = pluginsValue!.Split(',', StringSplitOptions.None);
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var builder = ImmutableArray.CreateBuilder<PluginName>(pluginValues.Length);
            foreach (var rawValue in pluginValues)
            {
                var value = rawValue.Trim();
                if (value.Length == 0)
                {
                    errorMessage = "--plugins must not contain empty entries.";
                    return false;
                }

                var plugin = new PluginName(value);
                if (!seen.Add(plugin.Value))
                {
                    errorMessage = $"--plugins contains duplicate plugin '{plugin.Value}'.";
                    return false;
                }

                builder.Add(plugin);
            }

            enabledPlugins = builder.ToImmutable();
            if (enabledPlugins.IsDefaultOrEmpty)
            {
                errorMessage = "--plugins must contain at least one plugin.";
                return false;
            }

            return true;
        }
        catch (ArgumentException exception)
        {
            errorMessage = exception.Message;
            return false;
        }
    }

    private static bool IsAbsoluteKLocalPath(string value)
    {
        try
        {
            if (!Path.IsPathFullyQualified(value)) return false;
            var root = Path.GetPathRoot(Path.GetFullPath(value));
            return string.Equals(root, "K:\\", StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    private static bool TryDeriveTargetRace(
        PackageVerifyResult ordinary,
        out FormReference targetRace,
        out Diagnostic? diagnostic)
    {
        targetRace = default;
        diagnostic = null;
        var artifact = ordinary.Artifact;
        if (artifact is null)
        {
            diagnostic = TargetRaceUnavailable(
                "The verified package did not expose a manifest artifact.");
            return false;
        }

        var outputRows = artifact.Files
            .Where(row => string.Equals(
                Path.GetFileName(row.RelativePath.Value),
                artifact.OutputPlugin,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (outputRows.Length != 1)
        {
            diagnostic = TargetRaceUnavailable(
                "The verified package must contain exactly one output-plugin artifact.");
            return false;
        }

        var outputRow = outputRows[0];
        if (!string.Equals(outputRow.Kind, "plugin", StringComparison.OrdinalIgnoreCase) ||
            !outputRow.Matches)
        {
            diagnostic = TargetRaceUnavailable(
                "The unique output-plugin artifact is not a verified plugin row.");
            return false;
        }

        if (!GameEditionExtensions.TryParseWireName(artifact.Edition, out var edition))
        {
            diagnostic = TargetRaceUnavailable(
                "The package edition is not supported for target-race derivation.");
            return false;
        }

        try
        {
            var packageRoot = Path.GetDirectoryName(artifact.ManifestPath.Value);
            if (string.IsNullOrWhiteSpace(packageRoot))
            {
                diagnostic = TargetRaceUnavailable(
                    "The package manifest has no resolvable package root.");
                return false;
            }

            var outputPath = Path.GetFullPath(Path.Combine(
                packageRoot,
                outputRow.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
            if (!IsUnder(outputPath, packageRoot))
            {
                diagnostic = TargetRaceUnavailable(
                    "The verified output-plugin row escaped the package root.");
                return false;
            }

            var snapshot = BethesdaNpcFaceAdapter.Read(
                edition,
                new WorkspacePath(outputPath),
                artifact.TargetFormId);
            if (snapshot.Race is not { } race ||
                race.FormId.Value == 0 ||
                string.IsNullOrWhiteSpace(race.Plugin.Value))
            {
                diagnostic = TargetRaceUnavailable(
                    "The exact manifest target NPC has no persisted race.");
                return false;
            }

            targetRace = race;
            return true;
        }
        catch (ArgumentException exception)
        {
            diagnostic = TargetRaceUnavailable(
                "The verified output plugin target race was invalid: " +
                exception.Message);
            return false;
        }
        catch (Exception)
        {
            diagnostic = TargetRaceUnavailable(
                "The verified output plugin did not contain a readable manifest target race.");
            return false;
        }
    }

    private static bool HasCompleteExternalEvidence(
        PackageVerifyResult ordinary,
        ExternalHeadPartInstallVerificationArtifact external)
    {
        if (!string.Equals(external.SchemaIdentifier,
                ExternalHeadPartSchemaIdentifiers.InstallVerification,
                StringComparison.Ordinal) ||
            !external.PackageIntegrity || !external.DescriptorClosureValid)
            return false;

        // A non-null external projection is only usable when the baseline
        // result is entirely error-free. PackageVerifyService can preserve a
        // projection alongside malformed external evidence, so do not let a
        // prefix allow-list accidentally promote that projection to a race.
        return !ordinary.Diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);
    }

    private static bool TryValidateLoadOrderShape(
        WorkspacePath dataRoot,
        ImmutableArray<PluginName> enabledPlugins,
        ExternalHeadPartInstallVerificationArtifact external,
        out string? error)
    {
        error = null;
        const int maximumPlugins = 64;
        if (enabledPlugins.Length > maximumPlugins)
        {
            error = "--plugins must contain no more than 64 enabled plugins.";
            return false;
        }

        var order = enabledPlugins
            .Select((plugin, index) => (plugin.Value, index))
            .ToDictionary(item => item.Value, item => item.index,
                StringComparer.OrdinalIgnoreCase);
        foreach (var provider in external.ProviderObservations)
        {
            if (!order.TryGetValue(provider.ProviderPlugin.Value, out var providerIndex))
            {
                error = $"--plugins is missing verified provider '{provider.ProviderPlugin.Value}'.";
                return false;
            }

            var providerPath = Path.Combine(dataRoot.Value, provider.ProviderPlugin.Value);
            if (!File.Exists(providerPath))
            {
                error = $"--plugins is incomplete: verified provider '{provider.ProviderPlugin.Value}' is not present under --data-root.";
                return false;
            }

            try
            {
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    providerPath, SkyrimRelease.SkyrimSE);
                foreach (var master in mod.ModHeader.MasterReferences)
                {
                    var masterName = master.Master.ToString();
                    if (!order.TryGetValue(masterName, out var masterIndex))
                    {
                        error = $"--plugins is incomplete: provider '{provider.ProviderPlugin.Value}' requires missing master '{masterName}'.";
                        return false;
                    }

                    if (masterIndex >= providerIndex)
                    {
                        error = $"--plugins must place master '{masterName}' before dependent provider '{provider.ProviderPlugin.Value}'.";
                        return false;
                    }
                }
            }
            catch (Exception exception)
            {
                error = $"--plugins could not read verified provider '{provider.ProviderPlugin.Value}': {exception.Message}";
                return false;
            }
        }

        return true;
    }

    private static bool IsUnder(string path, string root) =>
        new WorkspacePath(path).IsUnder(new WorkspacePath(root));

    private static Diagnostic TargetRaceUnavailable(string message) =>
        new(ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
            DiagnosticSeverity.Error, message);

    private void WriteVerify(bool json, PackageVerifyResult result, string human) =>
        Write(json,
            new VerifyResponse(
                result.Verified,
                result.Artifact,
                result.Diagnostics,
                result.ExternalInstallDependencyVerification),
            human);

    private static string HumanVerify(
        PackageVerifyResult result,
        bool strict,
        bool strictSatisfied)
    {
        if (!result.Verified)
            return "package verify: REFUSED (package bytes REFUSED)";

        if (result.ExternalInstallDependencyVerification is null)
        {
            return strict
                ? "package verify: PASS (package bytes PASS; install dependencies NOT REQUIRED)"
                : "package verify: PASS";
        }

        return strict && strictSatisfied
            ? "package verify: PASS (package bytes PASS; install dependencies VERIFIED)"
            : $"package verify: {(strict ? "REFUSED" : "PASS")} " +
                "(package bytes PASS; install dependencies REQUIRED)";
    }

    private async ValueTask<CommandExitCode> RunArchiveAsync(
        ParsedCommand command, CancellationToken cancellationToken)
    {
        if (command.Options.Keys.Any(key => !ArchiveOptionNames.Contains(key)) || !command.DuplicateOptions.IsDefaultOrEmpty)
            return Usage(command.Json, "Package archive options must be recognized and supplied once.");
        command.Options.TryGetValue("source-root", out var sourceRoot);
        if (command.Options.TryGetValue("package", out var alias))
        {
            if (sourceRoot is not null && !string.Equals(sourceRoot, alias, StringComparison.OrdinalIgnoreCase))
                return Usage(command.Json, "--package and --source-root must name the same source when both are supplied.");
            sourceRoot ??= alias;
        }
        if (sourceRoot is null ||
            !command.Options.TryGetValue("output", out var outputArchive))
            return Usage(command.Json, "package archive requires --source-root (or --package) and --output.");
        if (!TryArchivePayload(command, out var payload, out var preset, out string errorMessage))
            return Usage(command.Json, errorMessage);
        try
        {
            FinishPackageManifestReadLease? finishManifestLease;
            if (RequireReviewedFinishPackage(
                    command,
                    new WorkspacePath(sourceRoot),
                    out finishManifestLease) is { } gateRefusal)
                return gateRefusal;
            using (finishManifestLease)
            {
                if (await RequireFullFinishVerificationAsync(
                        command,
                        finishManifestLease,
                        cancellationToken) is { } finishRefusal)
                    return finishRefusal;
                var result = await archiveService.ArchiveAsync(
                    new PackageArchiveRequest(
                        new WorkspacePath(sourceRoot),
                        new WorkspacePath(outputArchive), payload, preset),
                    cancellationToken);
                if (RequireCurrentFinishManifest(command, finishManifestLease) is { } changedRefusal)
                    return changedRefusal;
                Write(
                    command.Json,
                    new ArchiveResponse(result.Written, result.Artifact, result.Diagnostics),
                    result.Written ? "package archive: PASS" : "package archive: REFUSED");
                return Exit(result.Written, result.Diagnostics);
            }
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private static bool TryArchivePayload(ParsedCommand command, out PackageArchivePayload payload,
        out AssetPath? preset, out string errorMessage)
    {
        payload = PackageArchivePayload.Full;
        preset = null;
        errorMessage = string.Empty;
        if (command.Options.TryGetValue("payload", out string? value))
        {
            if (value == "runtime-only") payload = PackageArchivePayload.RuntimeOnly;
            else if (value != "full") { errorMessage = "--payload must be full or runtime-only."; return false; }
        }
        if (command.Options.TryGetValue("include-runtime-preset", out string? selected))
        {
            if (payload != PackageArchivePayload.RuntimeOnly)
            { errorMessage = "--include-runtime-preset requires --payload runtime-only."; return false; }
            try
            {
                preset = new AssetPath(selected);
                if (preset.Value.Value != selected)
                    throw new ArgumentException("Runtime preset must be a canonical Data-relative path with forward slashes and no Data/ prefix.");
            }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        return true;
    }

    private CommandExitCode? RequireReviewedFinishPackage(
        ParsedCommand command,
        WorkspacePath packageRoot,
        out FinishPackageManifestReadLease? manifestLease)
    {
        manifestLease = null;
        string[] finishManifests =
        [
            Path.Combine(packageRoot.Value, "NPCManager", "Evidence", "finish-core-manifest.json"),
            Path.Combine(packageRoot.Value, "Data", "NPCManager", "Evidence", "finish-core-manifest.json")
        ];
        string[] presentFinishManifests = finishManifests.Where(File.Exists).ToArray();
        if (presentFinishManifests.Length == 0) return null;
        if (presentFinishManifests.Length != 1)
            return Refused(command.Json, "workflow-human-review-required",
                "Finish packaging requires exactly one package-owned finish-core-manifest.json.");
        string finishManifest = Path.GetFullPath(presentFinishManifests[0]);
        if (!command.Options.TryGetValue("workflow-bundle", out string? workflow) ||
            !command.Options.TryGetValue("workflow-bundle-sha256", out string? workflowSha256))
            return Refused(command.Json, "workflow-human-review-required",
                "Finish package archive and verification require the exact --workflow-bundle and --workflow-bundle-sha256; any supplied review receipt is validated against that workflow.");
        try
        {
            var policy = new KOnlyWorkspacePolicy(
                workspaceRoot,
                ActorwrightWorkspace.ResolveProtectedRoot(workspaceRoot));
            manifestLease = new FinishPackageManifestReadLease(
                workspaceRoot,
                new WorkspacePath(finishManifest));
            byte[] manifestBytes = manifestLease.InitialBytes.ToArray();
            SkyrimNpcFinishCoreManifest parsedManifest =
                SkyrimNpcFinishCoreDocumentCodec.ParseManifest(
                    manifestBytes,
                    workspaceRoot);
            if (!SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                    parsedManifest,
                    workspaceRoot).AsSpan().SequenceEqual(manifestBytes))
                throw new AgentWorkflowCodecException(
                    "workflow-human-review-required",
                    "The Finish package manifest must be canonical Actorwright JSON.");
            var lifecycle = new AgentWorkflowBundleTransitionService(new AgentWorkflowBundleCodec(policy, workspaceRoot));
            var receipts = new AgentReviewReceiptService(policy, workspaceRoot);
            AgentWorkflowBundleTransition transition = lifecycle.LoadForFinishCommand(
                new WorkspacePath(workflow),
                workflowSha256,
                "npc finish verify",
                document => LoadOptionalReceipt(document, receipts)!);
            AgentWorkflowBundleDocument document = transition.Document;
            WorkflowArtifactBinding[] manifestBindings = document.Bundle.Artifacts
                .Where(item => item.Kind == WorkflowArtifactKinds.NpcFinishCoreManifest).Take(2).ToArray();
            if (manifestBindings.Length != 1)
                throw new AgentWorkflowCodecException("workflow-human-review-required",
                    "The Finish workflow must contain exactly one bound Finish manifest.");
            WorkflowArtifactBinding manifestBinding = manifestBindings[0];
            string manifestSha256 = Convert.ToHexString(SHA256.HashData(manifestBytes));
            if (!string.Equals(Path.GetFullPath(manifestBinding.Path.Value), finishManifest,
                    StringComparison.OrdinalIgnoreCase) ||
                manifestBinding.Size != manifestBytes.Length ||
                !string.Equals(manifestBinding.Sha256, manifestSha256,
                    StringComparison.OrdinalIgnoreCase))
                throw new AgentWorkflowCodecException("workflow-human-review-required",
                    "The Finish workflow is not bound to this package's exact Finish manifest.");
            WorkflowArtifactBinding[] requests = document.Bundle.Artifacts
                .Where(item => item.Kind == WorkflowArtifactKinds.NpcFinishCoreRequest).Take(2).ToArray();
            WorkflowArtifactBinding[] proposals = document.Bundle.Artifacts
                .Where(item => item.Kind == WorkflowArtifactKinds.NpcFinishCoreProposal).Take(2).ToArray();
            if (requests.Length != 1 || proposals.Length != 1 ||
                parsedManifest.RequestSha256 is null ||
                parsedManifest.ProposalSha256 is null ||
                requests[0].InputArtifactHashes.Length != 1 ||
                proposals[0].SemanticSha256 is null ||
                !string.Equals(parsedManifest.RequestSha256.Value.Value, requests[0].InputArtifactHashes[0],
                    StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parsedManifest.ProposalSha256.Value.Value, proposals[0].SemanticSha256,
                    StringComparison.OrdinalIgnoreCase) ||
                !manifestBinding.InputArtifactHashes.SequenceEqual(
                    new[] { requests[0].Sha256, proposals[0].Sha256 }.Order(StringComparer.Ordinal),
                    StringComparer.Ordinal))
                throw new AgentWorkflowCodecException("workflow-human-review-required",
                    "The Finish package manifest does not bind the workflow's exact request and proposal.");
            return null;
        }
        catch (Exception exception) when (exception is AgentWorkflowCodecException or
            AgentReviewReceiptException or ArgumentException or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            manifestLease?.Dispose();
            manifestLease = null;
            return Refused(command.Json, "workflow-human-review-required", exception.Message);
        }
    }

    private static AgentReviewReceiptDocument? LoadOptionalReceipt(
        AgentWorkflowBundleDocument document,
        AgentReviewReceiptService receipts)
    {
        WorkflowArtifactBinding[] bindings = document.Bundle.Artifacts
            .Where(item => item.Kind == WorkflowArtifactKinds.ReviewReceipt).Take(2).ToArray();
        if (bindings.Length == 0) return null;
        if (bindings.Length != 1)
            throw new AgentWorkflowCodecException("workflow-human-review-required",
                "The Finish workflow must contain at most one bound review receipt.");
        AgentReviewReceiptDocument receipt = receipts.LoadForSuccessor(
            document,
            bindings[0].Path,
            bindings[0].Sha256);
        WorkflowArtifactBinding[] proposals = document.Bundle.Artifacts
            .Where(item => item.Kind == WorkflowArtifactKinds.NpcFinishCoreProposal).Take(2).ToArray();
        if (proposals.Length != 1 ||
            !string.Equals(receipt.Receipt.ProposalSha256, proposals[0].Sha256,
                StringComparison.Ordinal))
            throw new AgentWorkflowCodecException("workflow-human-review-required",
                "The accepted receipt does not bind the workflow's exact Finish proposal.");
        return receipt;
    }

    private CommandExitCode? RequireCurrentFinishManifest(
        ParsedCommand command,
        FinishPackageManifestReadLease? manifestLease)
    {
        if (manifestLease is null) return null;
        try
        {
            return manifestLease.HasExactReadback()
                ? null
                : Refused(command.Json, "workflow-human-review-required",
                    "The Finish package manifest changed during package verification or publication.");
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or
            UnauthorizedAccessException or NotSupportedException)
        {
            return Refused(command.Json, "workflow-human-review-required", exception.Message);
        }
    }

    private async ValueTask<CommandExitCode?> RequireFullFinishVerificationAsync(
        ParsedCommand command,
        FinishPackageManifestReadLease? manifestLease,
        CancellationToken cancellationToken)
    {
        if (manifestLease is null) return null;
        if (finishCoreService is null)
            return Refused(command.Json, "workflow-human-review-required",
                "Full Finish package verification is unavailable.");
        try
        {
            string sha256 = Convert.ToHexString(SHA256.HashData(
                manifestLease.InitialBytes.Span));
            SkyrimNpcFinishCoreVerificationResult verified =
                await finishCoreService.VerifyAsync(
                    manifestLease.Manifest,
                    new Sha256Hash(sha256),
                    cancellationToken);
            if (!verified.Verified || verified.Verification?.Verified != true)
            {
                string detail = verified.Diagnostics.FirstOrDefault(
                    diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)?.Message ??
                    "The full Finish verifier did not establish package authority.";
                return Refused(command.Json, "workflow-human-review-required", detail);
            }
            return RequireCurrentFinishManifest(command, manifestLease);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException or
            IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return Refused(command.Json, "workflow-human-review-required", exception.Message);
        }
    }

    private CommandExitCode Refused(bool json, string code, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic(code, DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse(code, diagnostics), JsonOptions));
        else error.WriteLine($"ERROR {code}: {message}");
        return DiagnosticExitCodeClassifier.ClassifyFailure(diagnostics);
    }

    private void Write<T>(bool json, T response, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", diagnostics), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private static CommandExitCode Exit(bool success, ImmutableArray<Diagnostic> diagnostics)
    {
        if (success) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.ClassifyFailure(diagnostics);
    }

    private sealed record BuildResponse(bool Written, PackageBuildArtifact? Artifact,
        ImmutableArray<Diagnostic> Diagnostics);
    private sealed record InspectResponse(bool Valid, PackageManifestIdentity? Identity,
        ImmutableArray<Diagnostic> Diagnostics);
    private sealed record VerifyResponse(
        bool Verified,
        PackageVerificationArtifact? Artifact,
        ImmutableArray<Diagnostic> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ExternalHeadPartInstallVerificationArtifact?
            ExternalInstallDependencyVerification);
    private sealed record ArchiveResponse(bool Written, PackageArchiveArtifact? Artifact,
        ImmutableArray<Diagnostic> Diagnostics);
    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
