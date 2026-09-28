using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PresetCommandHandler(
    IPresetService service,
    IPresetFormResolver resolver,
    IRaceMenuPresetCatalogService? catalogService,
    ISkyrimFaceRecordPluginAuthorityLoader? authorityLoader,
    TextWriter output,
    TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        return command.Name switch
        {
            "preset inspect" => await InspectAsync(command, cancellationToken),
            "preset catalog" => await CatalogAsync(command, cancellationToken),
            "preset export" => await ExportAsync(command, cancellationToken),
            "preset diff" => await DiffAsync(command, cancellationToken),
            "preset resolve" => await ResolveAsync(command, cancellationToken),
            "appearance copy" => await CopyAsync(command, cancellationToken),
            _ => Usage(command.Json, $"Unknown preset command '{command.Name}'.")
        };
    }

    private async ValueTask<CommandExitCode> CatalogAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (catalogService is null)
            return Usage(command.Json,
                "preset catalog is unavailable in this runner configuration.");
        if (!command.Options.TryGetValue("directory", out var directoryText))
            return Usage(command.Json, "preset catalog requires --directory.");

        try
        {
            var directory = new WorkspacePath(directoryText);
            RaceMenuPresetTargetBuild? targetBuild =
                await TryBuildCatalogTargetAsync(command, cancellationToken);
            if (targetBuild is { Accepted: false })
            {
                WriteError(command.Json, "preset-catalog-target-invalid",
                    targetBuild.Diagnostics);
                return DiagnosticExitCodeClassifier.ClassifyFailure(
                    targetBuild.Diagnostics);
            }

            RaceMenuPresetCatalogResult result = await catalogService.LoadAsync(
                new RaceMenuPresetCatalogRequest(directory, targetBuild?.Target),
                cancellationToken);
            string filter = command.Options.TryGetValue("filter", out var filterText)
                ? filterText.Trim()
                : string.Empty;
            bool compatibleOnly = command.Options.ContainsKey("compatible-only");
            var diagnostics = result.Diagnostics.ToBuilder();
            if (compatibleOnly && targetBuild is null)
            {
                diagnostics.Add(new Diagnostic("preset-catalog-target-required",
                    DiagnosticSeverity.Error,
                    "--compatible-only requires --data-root, --plugins, --race, and --sex."));
            }
            if (compatibleOnly && result.Entries.Any(item =>
                    item.Compatibility == RaceMenuPresetCompatibilityKind.Unavailable))
            {
                diagnostics.Add(new Diagnostic("preset-catalog-compatibility-unavailable",
                    DiagnosticSeverity.Error,
                    "Compatible-only filtering is unavailable because at least one preset lacks complete static race authority."));
            }
            ImmutableArray<RaceMenuPresetCatalogEntry> entries = result.Entries
                .Where(item => filter.Length == 0 || item.DisplayName.Contains(filter,
                    StringComparison.OrdinalIgnoreCase))
                .Where(item => !compatibleOnly ||
                               item.Compatibility == RaceMenuPresetCompatibilityKind.Compatible)
                .ToImmutableArray();
            int omitted = result.Diagnostics.Count(item =>
                item.Code == "preset-catalog-entry-omitted");
            var response = new CatalogResponse(
                result.Accepted && !diagnostics.Any(item =>
                    item.Severity == DiagnosticSeverity.Error),
                directory.Value,
                filter,
                compatibleOnly,
                targetBuild?.Target?.AuthorityId,
                entries.Select(CatalogEntryResponse.From).ToImmutableArray(),
                omitted,
                diagnostics.ToImmutable());
            Write(response, command.Json,
                $"preset catalog: {entries.Length} admitted, {omitted} omitted");
            return Exit(response.Diagnostics);
        }
        catch (ArgumentException exception)
        {
            return Usage(command.Json, exception.Message);
        }
    }

    private async ValueTask<RaceMenuPresetTargetBuild?> TryBuildCatalogTargetAsync(
        ParsedCommand command,
        CancellationToken cancellationToken)
    {
        string[] optionNames = ["data-root", "plugins", "race", "sex"];
        bool any = optionNames.Any(command.Options.ContainsKey);
        if (!any) return null;
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (authorityLoader is null)
        {
            diagnostics.Add(new Diagnostic("preset-catalog-authority-unavailable",
                DiagnosticSeverity.Error,
                "This runner has no Skyrim plugin-authority loader."));
            return new RaceMenuPresetTargetBuild(null, diagnostics.ToImmutable());
        }
        if (optionNames.Any(name => !command.Options.ContainsKey(name)))
        {
            diagnostics.Add(new Diagnostic("preset-catalog-target-options",
                DiagnosticSeverity.Error,
                "Race compatibility requires --data-root, --plugins, --race, and --sex together."));
            return new RaceMenuPresetTargetBuild(null, diagnostics.ToImmutable());
        }

        if (!FormReference.TryParse(command.Options["race"], out FormReference race))
        {
            diagnostics.Add(new Diagnostic("preset-catalog-race-invalid",
                DiagnosticSeverity.Error,
                "--race must be Plugin|0xFormID."));
        }
        NpcSex? sex = command.Options["sex"].Trim().ToLowerInvariant() switch
        {
            "female" => NpcSex.Female,
            "male" => NpcSex.Male,
            _ => null
        };
        if (sex is null)
        {
            diagnostics.Add(new Diagnostic("preset-catalog-sex-invalid",
                DiagnosticSeverity.Error,
                "--sex must be female or male."));
        }

        WorkspacePath dataRoot;
        ImmutableArray<PluginName> plugins;
        try
        {
            dataRoot = new WorkspacePath(command.Options["data-root"]);
            plugins = command.Options["plugins"]
                .Split(',', StringSplitOptions.RemoveEmptyEntries |
                            StringSplitOptions.TrimEntries)
                .Select(item => new PluginName(item))
                .ToImmutableArray();
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(new Diagnostic("preset-catalog-target-invalid",
                DiagnosticSeverity.Error, exception.Message));
            return new RaceMenuPresetTargetBuild(null, diagnostics.ToImmutable());
        }
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new RaceMenuPresetTargetBuild(null, diagnostics.ToImmutable());

        SkyrimFaceRecordPluginAuthorityResult authority =
            await authorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition, dataRoot, plugins),
                cancellationToken);
        diagnostics.AddRange(authority.Diagnostics);
        if (!authority.Accepted)
            return new RaceMenuPresetTargetBuild(null, diagnostics.ToImmutable());

        string authorityId = "cli:" + string.Join(':',
            authority.Authorities.Select(item => item.ExpectedSha256.Value));
        return new RaceMenuPresetTargetBuild(
            new RaceMenuPresetTarget(authorityId, race, sex!.Value, dataRoot,
                authority.Authorities),
            diagnostics.ToImmutable());
    }

    private async ValueTask<CommandExitCode> InspectAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!PresetCommandBinder.TryBind(command, "input", out var request, out var errorMessage)) return Usage(command.Json, errorMessage);
        var result = await service.InspectAsync(request, cancellationToken);
        if (result.Document is null)
        {
            WriteError(command.Json, "preset-invalid", result.Diagnostics);
            return DiagnosticExitCodeClassifier.ClassifyFailure(
                result.Diagnostics);
        }
        var document = result.Document;
        var response = new InspectResponse(document.Format.ToWireName(), document.Edition.ToWireName(), document.SourceHash.Value,
            document.IsValid, document.Appearance, document.Diagnostics);
        Write(response, command.Json, $"preset: {response.Format} {response.Edition} valid={response.IsValid} diagnostics={response.Diagnostics.Length}");
        return Exit(document.Diagnostics);
    }

    private async ValueTask<CommandExitCode> ExportAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!PresetCommandBinder.TryBind(command, "input", out var source, out var errorMessage)) return Usage(command.Json, errorMessage);
        if (!command.Options.TryGetValue("output", out var outputPath)) return Usage(command.Json, "preset export requires --output.");
        try
        {
            var result = await service.ExportAsync(new PresetExportRequest(source.Format, source.Edition, source.SourcePath, new WorkspacePath(outputPath)), cancellationToken);
            var response = new ExportResponse(result.Written, result.Source.Format.ToWireName(), result.Source.SourceHash.Value,
                result.OutputHash?.Value, result.Diagnostics);
            Write(response, command.Json, result.Written ? $"preset export: PASS {outputPath}" : "preset export: REFUSED");
            return result.Written
                ? Exit(result.Diagnostics)
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private async ValueTask<CommandExitCode> DiffAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!PresetCommandBinder.TryBindFormatEdition(command, out var format, out var edition, out var errorMessage)) return Usage(command.Json, errorMessage);
        if (!command.Options.TryGetValue("left", out var leftPath) || !command.Options.TryGetValue("right", out var rightPath)) return Usage(command.Json, "preset diff requires --left and --right.");
        try
        {
            var result = await service.DiffAsync(new PresetDiffRequest(
                new PresetParseRequest(format, edition, new WorkspacePath(leftPath)),
                new PresetParseRequest(format, edition, new WorkspacePath(rightPath))), cancellationToken);
            var response = new DiffResponse(result.Differences.Length == 0, result.Differences, result.Diagnostics);
            Write(response, command.Json, $"preset diff: {response.Differences.Length} differences");
            return Exit(result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private async ValueTask<CommandExitCode> ResolveAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!command.Options.TryGetValue("identifier", out var identifier) || !command.Options.TryGetValue("load-order", out var loadOrder))
            return Usage(command.Json, "preset resolve requires --identifier Plugin|FormID and --load-order <json>.");
        try
        {
            var result = await resolver.ResolveAsync(new PresetResolutionRequest(PresetIdentifier.Parse(identifier), new WorkspacePath(loadOrder))
            {
                DataRoot = command.Options.TryGetValue("data-root", out var dataRoot) ? new WorkspacePath(dataRoot) : null
            }, cancellationToken);
            var response = new ResolveResponse(result.Identifier.Raw, result.ResolvedFormId?.ToString(), result.IsResolved, result.Diagnostics);
            Write(response, command.Json, result.IsResolved ? $"preset resolve: {response.ResolvedFormId}" : "preset resolve: REFUSED");
            return result.IsResolved
                ? Exit(result.Diagnostics)
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private async ValueTask<CommandExitCode> CopyAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (service is not IPresetCopyService copyService)
            return Usage(command.Json, "appearance copy is unavailable in this runner configuration.");
        if (!PresetCommandBinder.TryBindFormatEdition(command, out var format, out var edition, out var errorMessage)) return Usage(command.Json, errorMessage);
        if (!command.Options.TryGetValue("from", out var sourcePath) ||
            !command.Options.TryGetValue("to", out var targetPath) ||
            !command.Options.TryGetValue("output", out var outputPath) ||
            !command.Options.TryGetValue("sections", out var sectionText))
            return Usage(command.Json, "appearance copy requires --from, --to, --output, and --sections.");
        if (!TryParseSections(sectionText, edition, out var sections, out errorMessage)) return Usage(command.Json, errorMessage);
        try
        {
            var result = await copyService.CopyAsync(new PresetCopyRequest(
                new PresetParseRequest(format, edition, new WorkspacePath(sourcePath)),
                new PresetParseRequest(format, edition, new WorkspacePath(targetPath)), sections,
                new WorkspacePath(outputPath)), cancellationToken);
            var response = new CopyResponse(result.Written, format.ToWireName(), edition.ToWireName(),
                sections.Select(item => item.ToWireName()).ToImmutableArray(),
                result.Source.SourceHash.Value, result.Target.SourceHash.Value, result.OutputHash?.Value,
                result.Diagnostics);
            Write(response, command.Json, result.Written ? $"appearance copy: PASS {outputPath}" : "appearance copy: REFUSED");
            return result.Written
                ? Exit(result.Diagnostics)
                : DiagnosticExitCodeClassifier.ClassifyFailure(
                    result.Diagnostics);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private static bool TryParseSections(string raw, GameEdition edition, out ImmutableArray<PresetCopySection> sections, out string errorMessage)
    {
        if (string.Equals(raw.Trim(), "all", StringComparison.OrdinalIgnoreCase))
        {
            sections = edition switch
            {
                GameEdition.Fallout4 => ImmutableArray.Create(
                    PresetCopySection.BodyWeight,
                    PresetCopySection.BodyRegions,
                    PresetCopySection.BodySliders,
                    PresetCopySection.Overlays,
                    PresetCopySection.LmSkinTemplate,
                    PresetCopySection.FaceParts,
                    PresetCopySection.HairColor,
                    PresetCopySection.FaceTints,
                    PresetCopySection.FaceVertexMorphs,
                    PresetCopySection.FaceBoneRegions),
                GameEdition.SkyrimSpecialEdition => ImmutableArray.Create(
                    PresetCopySection.BodyWeight,
                    PresetCopySection.BodySliders,
                    PresetCopySection.Overlays,
                    PresetCopySection.FaceParts,
                    PresetCopySection.HairColor,
                    PresetCopySection.FaceTints,
                    PresetCopySection.FaceVertexMorphs,
                    PresetCopySection.Sculpt),
                _ => ImmutableArray<PresetCopySection>.Empty
            };
            errorMessage = string.Empty;
            return true;
        }
        var parsed = ImmutableArray.CreateBuilder<PresetCopySection>();
        foreach (var value in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!PresetCopySectionExtensions.TryParseWireName(value, out var section))
            {
                sections = ImmutableArray<PresetCopySection>.Empty;
                errorMessage = $"Unknown appearance copy section '{value}'.";
                return false;
            }
            parsed.Add(section);
        }
        sections = parsed.ToImmutable();
        errorMessage = string.Empty;
        return true;
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(human);
    }

    private void WriteError(bool json, string code, ImmutableArray<Diagnostic> diagnostics)
    {
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse(code, diagnostics), JsonOptions));
        else foreach (var diagnostic in diagnostics) error.WriteLine($"{diagnostic.Severity.ToString().ToUpperInvariant()} {diagnostic.Code}: {diagnostic.Message}");
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        WriteError(json, "usage-error", diagnostics); return CommandExitCode.UsageError;
    }

    private static CommandExitCode Exit(
        ImmutableArray<Diagnostic> diagnostics) =>
        DiagnosticExitCodeClassifier.Classify(diagnostics);

    private sealed record InspectResponse(string Format, string Edition, string SourceSha256, bool IsValid,
        PresetAppearance Appearance, ImmutableArray<Diagnostic> Diagnostics);
    private sealed record CatalogEntryResponse(
        string DisplayName,
        string SourcePath,
        string SourceSha256,
        RaceMenuPresetSummary Summary,
        RaceMenuPresetCompatibilityKind Compatibility,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        internal static CatalogEntryResponse From(RaceMenuPresetCatalogEntry entry) =>
            new(entry.DisplayName, entry.SourcePath.Value, entry.SourceSha256.Value,
                entry.Summary, entry.Compatibility, entry.Diagnostics);
    }
    private sealed record CatalogResponse(
        bool Accepted,
        string Directory,
        string Filter,
        bool CompatibleOnly,
        string? TargetAuthorityId,
        ImmutableArray<CatalogEntryResponse> Entries,
        int Omitted,
        ImmutableArray<Diagnostic> Diagnostics);
    private sealed record RaceMenuPresetTargetBuild(
        RaceMenuPresetTarget? Target,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public bool Accepted => Target is not null &&
                                !Diagnostics.Any(item =>
                                    item.Severity == DiagnosticSeverity.Error);
    }
    private sealed record ExportResponse(bool Written, string Format, string SourceSha256, string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);
    private sealed record DiffResponse(bool Equal, ImmutableArray<PresetDifference> Differences, ImmutableArray<Diagnostic> Diagnostics);
    private sealed record ResolveResponse(string Identifier, string? ResolvedFormId, bool IsResolved, ImmutableArray<Diagnostic> Diagnostics);
    private sealed record CopyResponse(bool Written, string Format, string Edition, ImmutableArray<string> Sections,
        string SourceSha256, string TargetSha256, string? OutputSha256, ImmutableArray<Diagnostic> Diagnostics);
    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
