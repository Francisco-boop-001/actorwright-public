using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Cli;

internal sealed class RaceMenuJslotNpcBuildCommandHandler(
    IRaceMenuJslotNpcBuildService service,
    WorkspacePath workspaceRoot,
    TextWriter output,
    TextWriter error,
    IRaceMenuNpcExecutionRequestFileLoader? requestFileLoader = null,
    IProviderMigrationService? providerMigrationService = null,
    INpcBuildPreflightService? preflightService = null)
{
    private readonly INpcBuildPreflightService? buildPreflightService =
        preflightService;
    private readonly IRaceMenuNpcExecutionRequestFileLoader requestLoader =
        requestFileLoader ?? new RaceMenuNpcExecutionRequestFileLoader(
            workspaceRoot,
            new ApplicationProviderResourceRegistry(
                new ApplicationResourcePath(AppContext.BaseDirectory)));
    private readonly IProviderMigrationService migrationService =
        providerMigrationService ?? new ProviderMigrationService(
            workspaceRoot,
            new ApplicationProviderResourceRegistry(
                new ApplicationResourcePath(AppContext.BaseDirectory)));
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] canonical = ["reviewed-provider-migration",
            "reviewed-provider-migration-sha256"];
        string[] alias = ["provider-migration",
            "provider-migration-sha256"];
        bool writesReview = command.Options.ContainsKey("provider-migration-output");
        bool hasCanonical = canonical.Any(command.Options.ContainsKey);
        bool hasAlias = alias.Any(command.Options.ContainsKey);
        bool acceptsReview = hasCanonical || hasAlias;
        if (writesReview || acceptsReview)
            return await RunProviderMigrationAsync(command, writesReview,
                hasCanonical, hasAlias, cancellationToken).ConfigureAwait(false);

        RaceMenuJslotNpcBuildCommandBindingResult bindingResult =
            RaceMenuJslotNpcBuildCommandBinder.Bind(
                command,
                strict: false,
                requireReviewedPreflight: false);
        if (!bindingResult.IsValid)
        {
            bool invalidValue = bindingResult.Failure ==
                RaceMenuJslotNpcBuildCommandBindingFailure.InvalidValue;
            return Failure(
                command.Json,
                invalidValue
                    ? "jslot-npc-request-invalid"
                    : string.Equals(
                    bindingResult.ErrorMessage,
                    "Use either --preflight-output or the complete --reviewed-preflight/--reviewed-preflight-sha256 pair.",
                    StringComparison.Ordinal)
                    ? "jslot-npc-preflight-usage"
                    : "usage-error",
                bindingResult.ErrorMessage ??
                "NPC build inputs are invalid.",
                invalidValue
                    ? CommandExitCode.ValidationFailure
                    : CommandExitCode.UsageError);
        }
        RaceMenuJslotNpcBuildCommandBinding binding = bindingResult.Binding!;

        try
        {
            RaceMenuNpcExecutionRequestFileLoadResult loaded =
                await requestLoader.LoadAsync(
                    new RaceMenuNpcExecutionRequestFileLoadRequest(
                        binding.SourceRequest,
                        binding.SourceRequestSha256),
                    cancellationToken).ConfigureAwait(false);
            if (!loaded.Loaded || loaded.Request is null)
            {
                bool security = loaded.Status ==
                                RaceMenuNpcExecutionRequestFileLoadStatus.SecurityRefused;
                bool migration = loaded.Status ==
                    RaceMenuNpcExecutionRequestFileLoadStatus
                        .MigrationRequired;
                return Failure(command.Json,
                    security ? "jslot-npc-request-security-refused" :
                    migration ? "jslot-npc-provider-migration-required" :
                    "jslot-npc-request-invalid",
                    string.Join(" | ", loaded.Diagnostics.Select(item => item.Message)),
                    DiagnosticExitCodeClassifier.ClassifyFailure(
                        loaded.Diagnostics));
            }

            var request = new RaceMenuJslotNpcBuildRequest(
                loaded.Request,
                binding.Preset,
                binding.ExpectedPresetSha256,
                binding.DataRoot,
                binding.PluginOrder,
                binding.CompanionRoot)
            {
                SourceRequest = binding.SourceRequest,
                SourceRequestSha256 = binding.SourceRequestSha256,
                ReviewedPreflight = binding.ReviewedPreflight
            };
            if (binding.Mode == RaceMenuJslotNpcBuildCommandMode.Preflight)
            {
                if (buildPreflightService is null)
                    return Failure(command.Json,
                        "jslot-npc-preflight-unavailable",
                        "NPC build preflight is not configured.",
                        CommandExitCode.ValidationFailure);
                NpcBuildPreflightResult preflight =
                    await buildPreflightService.CreateAsync(
                        new NpcBuildPreflightRequest(
                            loaded.Request,
                            binding.SourceRequest,
                            binding.SourceRequestSha256,
                            binding.Preset,
                            binding.ExpectedPresetSha256,
                            binding.DataRoot,
                            binding.PluginOrder,
                            binding.CompanionRoot,
                            binding.PreflightOutput!.Value,
                            binding.FaceBakeAuthorityOutput),
                        cancellationToken).ConfigureAwait(false);
                return WritePreflight(command.Json, preflight);
            }
            RaceMenuJslotNpcBuildResult result = await service.ExecuteAsync(
                request, null, cancellationToken).ConfigureAwait(false);

            BlankNpcBuildArtifact? artifact = result.Execution?.Build?.Artifact;
            RaceMenuPresetCompanionExport? companion =
                result.CompanionBuild?.Companion;
            var defaults = artifact is null ? (Fields: (ImmutableArray<string>?)null, Diagnostics: ImmutableArray<Diagnostic>.Empty) :
                NpcInheritedDefaultsAudit.Read(GameEdition.SkyrimSpecialEdition, workspaceRoot, artifact.Plugin,
                    new FormReference(new PluginName(Path.GetFileName(artifact.Plugin.Value)), artifact.AllocatedFormId), cancellationToken,
                    artifact.PluginSha256);
            var response = new BuildResponse(
                result.Completed,
                result.Preset?.SourceHash.Value,
                result.Target?.AuthorityId,
                result.CompanionBuild?.TemporaryNpc?.Verification?.IsValid ?? false,
                companion?.Preset.Value,
                companion?.PresetSha256.Value,
                companion?.FaceGeom.Value,
                companion?.FaceGeomSha256.Value,
                companion?.FaceTint.Value,
                companion?.FaceTintSha256.Value,
                result.CompanionBuild?.FaceGen?.Artifact?.RuntimeAuthority ?? false,
                artifact?.AllocatedFormId.ToString(),
                artifact?.Plugin.Value,
                artifact?.PluginSha256.Value,
                artifact?.Manifest.Value,
                artifact?.ManifestSha256.Value,
                artifact?.RuntimeAuthority ?? false,
                result.Diagnostics.AddRange(defaults.Diagnostics),
                result.Execution?.ExternalInstallPrepublication is
                    { } externalInstallPrepublication
                    ? ExternalInstallPrepublicationProtocolProjection.Serialize(
                        externalInstallPrepublication)
                    : null,
                defaults.Fields);
            if (command.Json)
            {
                output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
            }
            else if (result.Completed)
            {
                output.WriteLine("npc create-from-jslot: VERIFIED STATIC BUILD");
                output.WriteLine($"Preset -> {response.PresetSha256}");
                output.WriteLine($"Manager NIF -> {response.CompanionFaceGeom}");
                output.WriteLine($"Manager DDS -> {response.CompanionFaceTint}");
                output.WriteLine($"NPC {response.NpcFormId} -> {response.Plugin}");
                output.WriteLine($"Package -> {response.Manifest}");
                output.WriteLine("Runtime authority -> false (requires in-game proof)");
            }
            else
            {
                output.WriteLine("npc create-from-jslot: REFUSED");
                foreach (Diagnostic diagnostic in result.Diagnostics
                             .Where(item => item.Severity != DiagnosticSeverity.Info))
                    error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
            }
            return result.Completed
                ? CommandExitCode.Success
                : ExitCode(result.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            return Failure(command.Json, "cancelled",
                "JSlot NPC creation was cancelled.", CommandExitCode.Cancelled);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           FormatException or
                                           IOException or
                                           UnauthorizedAccessException)
        {
            return Failure(command.Json, "jslot-npc-request-invalid",
                exception.Message, CommandExitCode.ValidationFailure);
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            return Failure(command.Json, "jslot-npc-unexpected-failure",
                $"{exception.GetType().Name}: {exception.Message}",
                CommandExitCode.ValidationFailure);
        }
    }

    private async ValueTask<CommandExitCode> RunProviderMigrationAsync(
        ParsedCommand command,
        bool writesReview,
        bool hasCanonical,
        bool hasAlias,
        CancellationToken cancellationToken)
    {
        string[] common = ["request", "request-sha256",
            "migrated-request-root"];
        string[] canonical = ["reviewed-provider-migration",
            "reviewed-provider-migration-sha256"];
        string[] alias = ["provider-migration", "provider-migration-sha256"];
        bool acceptsReview = hasCanonical || hasAlias;
        string[] mode = writesReview
            ? ["provider-migration-output"]
            : hasCanonical ? canonical : alias;
        if (writesReview == acceptsReview || hasCanonical && hasAlias ||
            common.Concat(mode).Any(key =>
                !command.Options.TryGetValue(key, out string? value) ||
                string.IsNullOrWhiteSpace(value)) ||
            command.DuplicateOptions.Intersect(common.Concat(canonical)
                .Concat(alias), StringComparer.OrdinalIgnoreCase).Any() ||
            command.Options.Keys.Except(common.Concat(mode),
                StringComparer.Ordinal).Any())
            return Failure(command.Json,
                "jslot-npc-provider-migration-usage",
                "Provider migration requires exactly --request, --request-sha256, --migrated-request-root, and either --provider-migration-output or the reviewed migration path/hash pair.",
                CommandExitCode.UsageError);
        try
        {
            string requestText = command.Options["request"];
            var sourceRequest = new WorkspacePath(
                requestText.StartsWith('@') ? requestText[1..] :
                    requestText);
            var sourceHash = new Sha256Hash(
                command.Options["request-sha256"]);
            var migratedRoot = new WorkspacePath(
                command.Options["migrated-request-root"]);
            ProviderMigrationResult result;
            if (writesReview)
            {
                RaceMenuNpcExecutionRequestFileLoadResult loaded =
                    await requestLoader.LoadAsync(
                        new RaceMenuNpcExecutionRequestFileLoadRequest(
                            sourceRequest, sourceHash)
                        {
                            PlannedMigratedRequestRoot = migratedRoot
                        }, cancellationToken).ConfigureAwait(false);
                if (loaded.Status !=
                        RaceMenuNpcExecutionRequestFileLoadStatus
                            .MigrationRequired ||
                    loaded.MigrationReview is null)
                    return Failure(command.Json,
                        "jslot-npc-provider-migration-not-required",
                        string.Join(" | ", loaded.Diagnostics.Select(item =>
                            item.Message)),
                        DiagnosticExitCodeClassifier.ClassifyFailure(
                            loaded.Diagnostics));
                result = await migrationService.WriteReviewAsync(
                    new ProviderMigrationReviewWriteRequest(
                        loaded.MigrationReview,
                        new WorkspacePath(command.Options[
                            "provider-migration-output"])),
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                result = await migrationService.AcceptAsync(
                    new ProviderMigrationAcceptanceRequest(
                        sourceRequest,
                        sourceHash,
                        new WorkspacePath(command.Options[
                            mode[0]]),
                        new Sha256Hash(command.Options[
                            mode[1]]),
                        migratedRoot),
                    cancellationToken).ConfigureAwait(false);
            }
            string? reviewPath = writesReview
                ? command.Options["provider-migration-output"]
                : command.Options[mode[0]];
            string? acceptanceCommand = writesReview && result.Completed
                ? $"actorwright npc create-from-jslot --request {QuoteBoundPath(sourceRequest.Value)} " +
                  $"--request-sha256 {sourceHash.Value} --migrated-request-root {QuoteBoundPath(migratedRoot.Value)} " +
                  $"--reviewed-provider-migration {QuoteBoundPath(reviewPath)} " +
                  $"--reviewed-provider-migration-sha256 {result.Review!.Sha256.Value}"
                : null;
            if (command.Json)
                output.WriteLine(JsonSerializer.Serialize(
                    new MigrationResponse(
                        result.Completed,
                        reviewPath,
                        result.Review?.Sha256.Value,
                        acceptanceCommand,
                        result.Outputs.Select(item => item.Value)
                            .ToImmutableArray(),
                        result.Diagnostics),
                    JsonOptions));
            else if (result.Completed)
            {
                output.WriteLine(writesReview
                    ? "npc create-from-jslot: PROVIDER MIGRATION REVIEW WRITTEN"
                    : "npc create-from-jslot: REVIEWED PROVIDER MIGRATION COMPLETE");
                if (writesReview)
                {
                    output.WriteLine($"Review -> {reviewPath}");
                    output.WriteLine($"SHA-256 -> {result.Review!.Sha256.Value}");
                    output.WriteLine($"Next -> {acceptanceCommand}");
                }
            }
            else
                foreach (Diagnostic diagnostic in result.Diagnostics)
                    error.WriteLine(
                        $"{diagnostic.Code}: {diagnostic.Message}");
            return result.Completed ? CommandExitCode.Success :
                ExitCode(result.Diagnostics);
        }
        catch (OperationCanceledException)
        {
            return Failure(command.Json, "cancelled",
                "Provider migration was cancelled.",
                CommandExitCode.Cancelled);
        }
        catch (Exception exception) when (exception is ArgumentException or
            FormatException or IOException or UnauthorizedAccessException)
        {
            return Failure(command.Json,
                "jslot-npc-provider-migration-invalid",
                exception.Message,
                CommandExitCode.ValidationFailure);
        }
    }

    private CommandExitCode WritePreflight(
        bool json,
        NpcBuildPreflightResult result)
    {
        var response = new PreflightResponse(
            result.Created,
            result.ReadyForBuild,
            result.Document?.Value.PreviewReady ?? false,
            result.Document?.Path?.Value,
            result.Document?.Sha256.Value,
            result.Document?.Value.RequiredGates ?? [],
            result.Document?.Value.OptionalPreview ?? [],
            result.Document?.Value.DependencyClosure ?? [],
            result.Diagnostics,
            result.FaceBakeAuthority is { } derived ? new(derived.Path.Value, derived.Sha256.Value) : null);
        if (json)
            output.WriteLine(JsonSerializer.Serialize(response, JsonOptions));
        else if (result.Created)
        {
            output.WriteLine(result.ReadyForBuild
                ? "npc create-from-jslot: PREFLIGHT READY"
                : "npc create-from-jslot: PREFLIGHT REVIEW REQUIRED");
            output.WriteLine($"Preflight -> {response.Path}");
            output.WriteLine($"SHA-256 -> {response.Sha256}");
            output.WriteLine(response.PreviewReady
                ? "Preview -> ready"
                : "Preview -> unavailable (build gates are separate)");
        }
        else
            foreach (Diagnostic diagnostic in result.Diagnostics)
                error.WriteLine($"{diagnostic.Code}: {diagnostic.Message}");
        return result.Created && result.ReadyForBuild
            ? CommandExitCode.Success
            : ExitCode(result.Diagnostics);
    }

    private CommandExitCode Failure(
        bool json,
        string code,
        string message,
        CommandExitCode exitCode)
    {
        if (json)
            error.WriteLine(JsonSerializer.Serialize(
                new ErrorResponse(code, message), JsonOptions));
        else
            error.WriteLine($"ERROR {code}: {message}");
        return exitCode;
    }

    private static CommandExitCode ExitCode(
        ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticExitCodeClassifier.ClassifyFailure(diagnostics);

    private static string QuoteBoundPath(string value)
    {
        var quoted = new StringBuilder(value.Length + 2).Append('"');
        var backslashes = 0;
        foreach (char character in value)
        {
            if (character == '\\')
            {
                backslashes++;
                continue;
            }
            if (character == '"')
            {
                quoted.Append('\\', (backslashes * 2) + 1).Append('"');
                backslashes = 0;
                continue;
            }
            quoted.Append('\\', backslashes).Append(character);
            backslashes = 0;
        }
        return quoted.Append('\\', backslashes * 2).Append('"').ToString();
    }

    private sealed record BuildResponse(
        bool Completed,
        string? PresetSha256,
        string? TargetAuthorityId,
        bool TemporaryNpcVerified,
        string? CompanionPreset,
        string? CompanionPresetSha256,
        string? CompanionFaceGeom,
        string? CompanionFaceGeomSha256,
        string? CompanionFaceTint,
        string? CompanionFaceTintSha256,
        bool CompanionRuntimeAuthority,
        string? NpcFormId,
        string? Plugin,
        string? PluginSha256,
        string? Manifest,
        string? ManifestSha256,
        bool RuntimeAuthority,
        ImmutableArray<Diagnostic> Diagnostics,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        JsonElement?
            ExternalInstallPrepublication,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        ImmutableArray<string>? InheritedDefaults = null);

    private sealed record ErrorResponse(string Code, string Message);

    private sealed record MigrationResponse(
        bool Completed,
        string? ReviewPath,
        string? ReviewSha256,
        string? AcceptanceCommand,
        ImmutableArray<string> Outputs,
        ImmutableArray<Diagnostic> Diagnostics);

    private sealed record DerivedFaceBakeResponse(string Path, string Sha256);

    private sealed record PreflightResponse(
        bool Created,
        bool ReadyForBuild,
        bool PreviewReady,
        string? Path,
        string? Sha256,
        ImmutableArray<NpcBuildPreflightGate> RequiredGates,
        ImmutableArray<NpcBuildPreflightGate> OptionalPreview,
        ImmutableArray<NpcBuildPreflightDependency> DependencyClosure,
        ImmutableArray<Diagnostic> Diagnostics,
        [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        DerivedFaceBakeResponse? FaceBakeAuthority = null);
}
