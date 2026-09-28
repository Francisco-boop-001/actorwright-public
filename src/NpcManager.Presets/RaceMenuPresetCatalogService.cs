using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>
/// Loads a bounded flat RaceMenu preset directory once. Invalid files are
/// omitted with named diagnostics; filtering and selection can then operate on
/// the immutable result without rescanning or reparsing.
/// </summary>
public sealed class RaceMenuPresetCatalogService(
    IPresetService presetService,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IRaceMenuPresetCompatibilityEvaluator? compatibilityEvaluator = null)
    : IRaceMenuPresetCatalogService
{
    private const int MaxPresetFiles = 4096;

    public async ValueTask<RaceMenuPresetCatalogResult> LoadAsync(
        RaceMenuPresetCatalogRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ValidateDirectory(request.PresetDirectory).ToBuilder();
        if (HasErrors(diagnostics))
            return new RaceMenuPresetCatalogResult([], diagnostics.ToImmutable());

        string[] files;
        try
        {
            files = Directory.EnumerateFiles(request.PresetDirectory.Value, "*",
                    SearchOption.TopDirectoryOnly)
                .Where(path => string.Equals(Path.GetExtension(path), ".jslot",
                    StringComparison.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(path => path, StringComparer.Ordinal)
                .Take(MaxPresetFiles + 1)
                .ToArray();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("preset-catalog-read-failed",
                DiagnosticSeverity.Error, exception.Message));
            return new RaceMenuPresetCatalogResult([], diagnostics.ToImmutable());
        }

        if (files.Length > MaxPresetFiles)
        {
            diagnostics.Add(new Diagnostic("preset-catalog-file-limit",
                DiagnosticSeverity.Error,
                $"Preset catalogs may contain at most {MaxPresetFiles} .jslot files."));
            return new RaceMenuPresetCatalogResult([], diagnostics.ToImmutable());
        }

        var entries = ImmutableArray.CreateBuilder<RaceMenuPresetCatalogEntry>(files.Length);
        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var fileDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            AddReparseDiagnostic(fileDiagnostics, file, "preset file");
            if (HasErrors(fileDiagnostics))
            {
                diagnostics.AddRange(fileDiagnostics);
                continue;
            }
            var sourcePath = new WorkspacePath(file);
            PresetParseResult parsed = await presetService.InspectAsync(
                new PresetParseRequest(PresetFormat.RaceMenuJslot,
                    GameEdition.SkyrimSpecialEdition, sourcePath), cancellationToken);
            if (parsed.Document is not { IsValid: true } document)
            {
                diagnostics.Add(new Diagnostic("preset-catalog-entry-omitted",
                    DiagnosticSeverity.Warning,
                    $"Omitted '{Path.GetFileName(file)}': {DescribeErrors(parsed.Diagnostics)}"));
                continue;
            }

            RaceMenuPresetCompatibilityResult compatibility =
                await EvaluateCompatibilityAsync(document, request.Target,
                    cancellationToken);
            entries.Add(new RaceMenuPresetCatalogEntry(
                Path.GetFileNameWithoutExtension(file),
                sourcePath,
                document.SourceHash,
                document,
                Summarize(document.Appearance),
                compatibility.Kind,
                parsed.Diagnostics.AddRange(compatibility.Diagnostics)));
        }

        if (files.Length == 0)
        {
            diagnostics.Add(new Diagnostic("preset-catalog-empty",
                DiagnosticSeverity.Info,
                "No RaceMenu .jslot presets were found in the selected folder."));
        }

        return new RaceMenuPresetCatalogResult(entries.ToImmutable(),
            diagnostics.ToImmutable());
    }

    private ValueTask<RaceMenuPresetCompatibilityResult> EvaluateCompatibilityAsync(
        PresetDocument document,
        RaceMenuPresetTarget? target,
        CancellationToken cancellationToken)
    {
        if (target is null || compatibilityEvaluator is null)
        {
            return ValueTask.FromResult(new RaceMenuPresetCompatibilityResult(
                RaceMenuPresetCompatibilityKind.Unavailable, []));
        }

        return compatibilityEvaluator.EvaluateAsync(document, target,
            cancellationToken);
    }

    private ImmutableArray<Diagnostic> ValidateDirectory(WorkspacePath directory)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!directory.IsUnder(labRoot) || directory == labRoot)
        {
            diagnostics.Add(new Diagnostic("preset-catalog-outside-lab",
                DiagnosticSeverity.Error,
                "The preset folder must remain below the K-only workspace root."));
            return diagnostics.ToImmutable();
        }
        if (!Directory.Exists(directory.Value))
        {
            diagnostics.Add(new Diagnostic("preset-catalog-missing",
                DiagnosticSeverity.Error, "The selected preset folder does not exist."));
            return diagnostics.ToImmutable();
        }

        diagnostics.AddRange(policy.Evaluate(labRoot, directory));
        AddReparseDiagnostic(diagnostics, directory.Value, "preset folder");
        return diagnostics.ToImmutable();
    }

    private static RaceMenuPresetSummary Summarize(PresetAppearance appearance)
    {
        RaceMenuPresetData? raceMenu = appearance.RaceMenu;
        return new RaceMenuPresetSummary(
            appearance.HeadParts.Length,
            appearance.Tints.Length,
            raceMenu?.FaceMorphPresets.Length ?? 0,
            appearance.SliderMorphs.Length,
            appearance.OrderedCustomMorphs.IsDefault
                ? appearance.CustomMorphs.Count
                : appearance.OrderedCustomMorphs.Length,
            raceMenu?.SculptParts.Length ?? 0,
            appearance.Weight?.Value,
            raceMenu?.BodyMorphsKeyed.Count ?? appearance.BodyMorphs.Count,
            raceMenu?.BodyOverlays.Length ?? 0,
            raceMenu?.NodeTransforms.Length ?? 0,
            raceMenu?.SkinOverrides.Length ?? 0);
    }

    private static string DescribeErrors(IEnumerable<Diagnostic> diagnostics)
    {
        string[] errors = diagnostics
            .Where(item => item.Severity == DiagnosticSeverity.Error)
            .Select(item => item.Code)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        return errors.Length == 0 ? "the preset was not admitted" :
            string.Join(", ", errors);
    }

    private static void AddReparseDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("reparse-point-refused",
                        DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(new Diagnostic("path-inspection-failed",
                    DiagnosticSeverity.Error,
                    $"The {role} could not be inspected: {exception.Message}"));
                return;
            }

            string? parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
}
