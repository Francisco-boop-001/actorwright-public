using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed class PresetToNpcPipeline(
    IPresetService presetService,
    INpcMutationService mutationService,
    IBodyGenService bodyGenService,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IFaceGeomBuildService? faceGeomBuildService = null,
    IFaceTintBuildService? faceTintBuildService = null,
    IBodySidecarWriteService? bodySidecarWriteService = null,
    IRuntimeScriptDeployService? runtimeScriptDeployService = null) : IPresetToNpcPipeline
{
    private const string ManifestFileName = "npcmanager-package.json";
    private readonly PresetToNpcArtifactWriter artifactWriter =
        new(faceGeomBuildService, faceTintBuildService, bodySidecarWriteService, runtimeScriptDeployService);

    public async ValueTask<PresetToNpcPipelineResult> ExecuteAsync(
        PresetToNpcPipelineRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request).ToBuilder();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return Failed(diagnostics.ToImmutable());

        Sha256Hash sourcePluginHash;
        try { sourcePluginHash = ComputeHash(request.SourcePlugin.Value); }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("pipeline-source-plugin-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Failed(diagnostics.ToImmutable());
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("pipeline-source-plugin-read-denied", DiagnosticSeverity.Error, exception.Message));
            return Failed(diagnostics.ToImmutable());
        }

        var presetResult = await presetService.InspectAsync(
            new PresetParseRequest(request.Format, request.Edition, request.PresetPath), cancellationToken);
        diagnostics.AddRange(presetResult.Diagnostics);
        if (presetResult.Document is null || presetResult.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PresetToNpcPipelineResult(false, presetResult.Document, null, null, null, null, null,
                diagnostics.ToImmutable());

        var preset = presetResult.Document;
        diagnostics.AddRange(ValidateMappedAppearance(preset));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PresetToNpcPipelineResult(false, preset, null, null, null, null, null,
                diagnostics.ToImmutable());
        var weight = ResolveWeight(preset.Appearance.Weight, request.Edition, diagnostics);
        var fallout4BodyMorphs = ResolveFallout4BodyMorphs(preset.Appearance, request.Edition, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new PresetToNpcPipelineResult(false, preset, null, null, null, null, null, diagnostics.ToImmutable());
        if (request.OutputEditorId is null && request.OutputName is null && weight is null && fallout4BodyMorphs is null)
        {
            diagnostics.Add(new Diagnostic("pipeline-no-supported-plugin-change", DiagnosticSeverity.Error,
                "The preset and pipeline request contain no supported NPC plugin change."));
            return new PresetToNpcPipelineResult(false, preset, null, null, null, null, null, diagnostics.ToImmutable());
        }

        var outputPlugin = new WorkspacePath(Path.Combine(request.OutputRoot.Value, request.OutputPlugin.Value));
        var mutationRequest = new NpcMutationRequest(request.Edition, request.SourcePlugin, outputPlugin,
            request.TargetFormId, request.OutputEditorId, request.OutputName, weight, sourcePluginHash, false, null,
            BodyMorphs: fallout4BodyMorphs);
        var proposal = await mutationService.AnalyzeAsync(mutationRequest, cancellationToken);
        diagnostics.AddRange(proposal.Diagnostics);
        if (!proposal.IsApplicable)
            return new PresetToNpcPipelineResult(false, preset, proposal, null, null, null, null, diagnostics.ToImmutable());

        NpcMutationResult? mutation = null;
        BodyGenBuildResult? bodyGen = null;
        var manifestPath = new WorkspacePath(Path.Combine(request.OutputRoot.Value, ManifestFileName));
        var generatedPaths = new List<WorkspacePath>();
        try
        {
            mutation = await mutationService.ApplyAsync(mutationRequest, proposal, cancellationToken);
            diagnostics.AddRange(mutation.Diagnostics);
            if (!mutation.Applied || mutation.OutputHash is null)
                return new PresetToNpcPipelineResult(false, preset, proposal, mutation, null, null, null, diagnostics.ToImmutable());

            if (!preset.Appearance.BodyMorphs.IsEmpty)
            {
                var morphs = preset.Appearance.BodyMorphs
                    .Select(item => new BodyGenMorph(item.Key, item.Value))
                    .ToImmutableArray();
                var bodyGenRequest = new BodyGenTypedBuildRequest(
                    request.Edition, request.OutputPlugin, request.TargetFormId, request.ModName, morphs,
                    request.OutputRoot)
                {
                    EditorId = request.OutputEditorId
                };
                bodyGen = await bodyGenService.BuildTypedAsync(bodyGenRequest, cancellationToken);
                diagnostics.AddRange(bodyGen.Diagnostics);
                if (!bodyGen.Written)
                    return FailedAfterWrite(preset, proposal, mutation, bodyGen, generatedPaths, outputPlugin, manifestPath, diagnostics);
            }

            var artifacts = ImmutableArray.CreateBuilder<PresetToNpcPackageArtifact>();
            var pluginInfo = new FileInfo(outputPlugin.Value);
            artifacts.Add(new PresetToNpcPackageArtifact("plugin", new AssetPath(request.OutputPlugin.Value),
                checked((int)pluginInfo.Length), mutation.OutputHash.Value));
            var optionalArtifacts = await artifactWriter.WriteAsync(request, preset, bodyGen, cancellationToken);
            diagnostics.AddRange(optionalArtifacts.Diagnostics);
            generatedPaths.AddRange(optionalArtifacts.GeneratedPaths);
            if (!optionalArtifacts.Completed)
                return FailedAfterWrite(preset, proposal, mutation, bodyGen, generatedPaths, outputPlugin, manifestPath, diagnostics);
            artifacts.AddRange(optionalArtifacts.Artifacts);

            var manifest = new PresetToNpcPackageManifest(1, request.Edition.ToWireName(), request.Format.ToWireName(),
                RelativeToLab(request.PresetPath), preset.SourceHash, RelativeToLab(request.SourcePlugin), sourcePluginHash,
                request.OutputPlugin.Value, request.TargetFormId, artifacts.ToImmutable());
            var manifestResult = await PackageManifestWriter.WriteAsync(manifest, manifestPath, cancellationToken);
            diagnostics.AddRange(manifestResult.Diagnostics);
            if (!manifestResult.Written || manifestResult.Hash is null)
                return FailedAfterWrite(preset, proposal, mutation, bodyGen, generatedPaths, outputPlugin, manifestPath, diagnostics);

            return new PresetToNpcPipelineResult(true, preset, proposal, mutation, bodyGen, manifestPath,
                manifestResult.Hash, diagnostics.ToImmutable())
            {
                PackageArtifacts = artifacts.ToImmutable()
            };
        }
        catch (OperationCanceledException)
        {
            _ = Rollback(outputPlugin, bodyGen, generatedPaths, manifestPath);
            throw;
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("pipeline-write-failed", DiagnosticSeverity.Error, exception.Message));
            diagnostics.AddRange(Rollback(outputPlugin, bodyGen, generatedPaths, manifestPath));
            return new PresetToNpcPipelineResult(false, preset, proposal, mutation, bodyGen, null, null,
                diagnostics.ToImmutable());
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("pipeline-write-denied", DiagnosticSeverity.Error, exception.Message));
            diagnostics.AddRange(Rollback(outputPlugin, bodyGen, generatedPaths, manifestPath));
            return new PresetToNpcPipelineResult(false, preset, proposal, mutation, bodyGen, null, null,
                diagnostics.ToImmutable());
        }
    }

    private static PresetToNpcPipelineResult FailedAfterWrite(PresetDocument preset, NpcMutationProposal proposal,
        NpcMutationResult mutation, BodyGenBuildResult? bodyGen, IReadOnlyCollection<WorkspacePath> generatedPaths,
        WorkspacePath outputPlugin, WorkspacePath manifest,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(Rollback(outputPlugin, bodyGen, generatedPaths, manifest));
        return new PresetToNpcPipelineResult(false, preset, proposal, mutation, bodyGen, null, null, diagnostics.ToImmutable());
    }

    private ImmutableArray<Diagnostic> ValidateRequest(PresetToNpcPipelineRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if ((request.Format == PresetFormat.LooksMenu && request.Edition != GameEdition.Fallout4) ||
            (request.Format == PresetFormat.RaceMenuJslot && request.Edition != GameEdition.SkyrimSpecialEdition))
            diagnostics.Add(new Diagnostic("pipeline-format-edition-mismatch", DiagnosticSeverity.Error,
                "LooksMenu is bound to Fallout 4 and RaceMenu .jslot is bound to Skyrim SE."));
        if (request.TargetFormId.Value == 0)
            diagnostics.Add(new Diagnostic("pipeline-formid-invalid", DiagnosticSeverity.Error, "NPC FormID must be non-zero."));
        if (request.TargetFormId.Value > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("pipeline-formid-load-order-unresolved", DiagnosticSeverity.Error,
                "Pipeline target FormID must be plugin-local; resolve any load-order byte first."));
        if (!request.PresetPath.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("pipeline-preset-outside-lab", DiagnosticSeverity.Error, "Preset input must remain under the K-only lab root."));
        if (!request.SourcePlugin.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("pipeline-source-plugin-outside-lab", DiagnosticSeverity.Error, "Source plugin must remain under the K-only lab root."));
        if (!request.OutputRoot.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("pipeline-output-outside-lab", DiagnosticSeverity.Error, "Pipeline output must remain under the K-only lab root."));
        if (!Directory.Exists(request.OutputRoot.Value))
            diagnostics.Add(new Diagnostic("pipeline-output-root-missing", DiagnosticSeverity.Error, "Pipeline output root must already exist."));
        if (!File.Exists(request.SourcePlugin.Value))
            diagnostics.Add(new Diagnostic("pipeline-source-plugin-missing", DiagnosticSeverity.Error, "The source plugin does not exist."));
        if (!IsSafeModName(request.ModName))
            diagnostics.Add(new Diagnostic("pipeline-mod-name-invalid", DiagnosticSeverity.Error, "Pipeline mod name must be a safe path segment."));
        if (File.Exists(Path.Combine(request.OutputRoot.Value, request.OutputPlugin.Value)))
            diagnostics.Add(new Diagnostic("pipeline-output-plugin-exists", DiagnosticSeverity.Error, "Pipeline never overwrites an existing plugin."));
        if (File.Exists(Path.Combine(request.OutputRoot.Value, ManifestFileName)))
            diagnostics.Add(new Diagnostic("pipeline-manifest-exists", DiagnosticSeverity.Error, "Pipeline never overwrites an existing package manifest."));
        var sourceExtension = Path.GetExtension(request.SourcePlugin.Value);
        if (!string.Equals(sourceExtension, Path.GetExtension(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("pipeline-plugin-extension-mismatch", DiagnosticSeverity.Error, "Source and output plugin extensions must match."));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));
        AddReparseDiagnostic(diagnostics, request.PresetPath.Value, "preset");
        AddReparseDiagnostic(diagnostics, request.SourcePlugin.Value, "source plugin");
        AddReparseDiagnostic(diagnostics, request.OutputRoot.Value, "output root");
        if (request.FaceGeomManifest is not null)
        {
            var path = request.FaceGeomManifest.Value;
            if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("pipeline-facegeom-manifest-outside-lab", DiagnosticSeverity.Error,
                "FaceGeom manifest must remain under the K-only lab root."));
            AddReparseDiagnostic(diagnostics, path.Value, "FaceGeom manifest");
        }
        if (request.FaceTintManifest is not null)
        {
            var path = request.FaceTintManifest.Value;
            if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("pipeline-facetint-manifest-outside-lab", DiagnosticSeverity.Error,
                "FaceTint manifest must remain under the K-only lab root."));
            AddReparseDiagnostic(diagnostics, path.Value, "FaceTint manifest");
        }
        if (request.RuntimeScriptBuild is not null)
        {
            var path = request.RuntimeScriptBuild.Value;
            if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("pipeline-runtime-script-build-outside-lab", DiagnosticSeverity.Error,
                "Runtime script evidence must remain under the K-only lab root."));
            AddReparseDiagnostic(diagnostics, path.Value, "runtime script evidence");
        }
        if (request.RuntimeScriptPackage is not null)
        {
            var path = request.RuntimeScriptPackage.Value;
            if (!path.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("pipeline-runtime-script-package-outside-lab", DiagnosticSeverity.Error,
                "Runtime script package manifest must remain under the K-only lab root."));
            if (!File.Exists(path.Value)) diagnostics.Add(new Diagnostic("pipeline-runtime-script-package-missing", DiagnosticSeverity.Error,
                "Runtime script package manifest does not exist."));
            AddReparseDiagnostic(diagnostics, path.Value, "runtime script package");
        }
        return diagnostics.ToImmutable();
    }

    private static NpcWeightPatch? ResolveWeight(PresetWeight? weight, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (weight is null) return null;
        if (edition == GameEdition.Fallout4)
        {
            if (weight.Thin is null || weight.Muscular is null || weight.Fat is null)
            {
                diagnostics.Add(new Diagnostic("pipeline-weight-incomplete", DiagnosticSeverity.Error,
                    "Fallout 4 preset weight must contain thin, muscular, and fat values."));
                return null;
            }
            return new NpcWeightPatch(null, weight.Thin, weight.Muscular, weight.Fat);
        }
        if (!float.IsFinite(weight.Value) || weight.Value is < 0 or > 100)
        {
            diagnostics.Add(new Diagnostic("pipeline-weight-invalid", DiagnosticSeverity.Error,
                "Skyrim preset weight must be a finite scalar from 0 through 100."));
            return null;
        }
        return new NpcWeightPatch(weight.Value, null, null, null);
    }

    private static NpcBodyMorphPatch? ResolveFallout4BodyMorphs(PresetAppearance appearance,
        GameEdition edition, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!appearance.Presence.Fallout4BodyMorphs) return null;
        if (edition != GameEdition.Fallout4)
        {
            diagnostics.Add(new Diagnostic("pipeline-body-region-game-unsupported", DiagnosticSeverity.Error,
                "LooksMenu MRSV body-region values are supported only for Fallout 4."));
            return null;
        }
        var values = appearance.Fallout4BodyMorphs ?? Fallout4BodyMorphValues.Zero;
        var patch = new NpcBodyMorphPatch(values.ToDictionary());
        foreach (var value in patch.Values.Values)
        {
            if (!float.IsFinite(value) || value is < -1F or > 1F)
            {
                diagnostics.Add(new Diagnostic("pipeline-body-region-out-of-range", DiagnosticSeverity.Error,
                    "LooksMenu MRSV body-region values must be finite and between -1 and 1."));
                return null;
            }
        }
        return patch;
    }

    private static ImmutableArray<Diagnostic> ValidateMappedAppearance(PresetDocument preset)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var appearance = preset.Appearance;
        if (preset.Edition != GameEdition.SkyrimSpecialEdition) return diagnostics.ToImmutable();

        AddUnsupported(appearance.Presence.Gender, "gender", diagnostics);
        AddUnsupported(appearance.Presence.HeadParts, "headparts", diagnostics);
        AddUnsupported(appearance.Presence.HairColor, "hair-color", diagnostics);
        AddUnsupported(appearance.Presence.Morphs || appearance.Morphs.Count > 0 ||
            appearance.CustomMorphs.Count > 0 || !appearance.SliderMorphs.IsDefaultOrEmpty,
            "face-morphs", diagnostics);
        AddUnsupported(appearance.Presence.Tints || !appearance.Tints.IsDefaultOrEmpty,
            "face-tints", diagnostics);

        var raceMenu = appearance.RaceMenu;
        if (raceMenu is not null)
        {
            AddUnsupported(!string.IsNullOrWhiteSpace(raceMenu.HeadTexture), "head-texture", diagnostics);
            AddUnsupported(!raceMenu.FaceMorphPresets.IsDefaultOrEmpty, "face-morph-presets", diagnostics);
            AddUnsupported(!raceMenu.SculptParts.IsDefaultOrEmpty, "sculpt", diagnostics);
            AddUnsupported(!raceMenu.BodyOverlays.IsDefaultOrEmpty, "overlays", diagnostics);
            AddUnsupported(!raceMenu.NodeTransforms.IsDefaultOrEmpty, "node-transforms", diagnostics);
            AddUnsupported(!raceMenu.SkinOverrides.IsDefaultOrEmpty, "skin-overrides", diagnostics);
        }

        return diagnostics.ToImmutable();
    }

    private static void AddUnsupported(bool present, string field,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!present) return;
        diagnostics.Add(new Diagnostic($"pipeline-skyrim-{field}-not-applied", DiagnosticSeverity.Error,
            $"The Skyrim preset contains {field}, but the preset-to-NPC pipeline does not yet apply it. " +
            "The pipeline refuses to report a completed NPC while appearance data would be dropped."));
    }

    private string RelativeToLab(WorkspacePath path) =>
        new AssetPath(Path.GetRelativePath(labRoot.Value, path.Value)).Value;

    private static Sha256Hash ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool IsSafeModName(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 255 &&
        value is not "." and not ".." && !value.Any(char.IsControl) &&
        !value.Any(character => "<>:\"/\\|?*".Contains(character)) && !value.EndsWith('.') && !value.EndsWith(' ');

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("pipeline-reparse-refused", DiagnosticSeverity.Error,
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception)
            {
                diagnostics.Add(new Diagnostic("pipeline-path-inspection-failed", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            catch (UnauthorizedAccessException exception)
            {
                diagnostics.Add(new Diagnostic("pipeline-path-inspection-denied", DiagnosticSeverity.Error, exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static ImmutableArray<Diagnostic> Rollback(WorkspacePath outputPlugin, BodyGenBuildResult? bodyGen,
        IReadOnlyCollection<WorkspacePath> generatedPaths, WorkspacePath manifest)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        TryDelete(outputPlugin.Value, "plugin", diagnostics);
        if (bodyGen is not null)
            foreach (var file in bodyGen.Files) TryDelete(file.AbsolutePath.Value, "BodyGen artifact", diagnostics);
        foreach (var path in generatedPaths) TryDelete(path.Value, "pipeline artifact", diagnostics);
        TryDelete(manifest.Value, "manifest", diagnostics);
        return diagnostics.ToImmutable();
    }

    private static void TryDelete(string path, string role, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("pipeline-rollback-failed", DiagnosticSeverity.Error,
                $"Could not remove the {role} '{path}': {exception.Message}"));
        }
        catch (UnauthorizedAccessException exception)
        {
            diagnostics.Add(new Diagnostic("pipeline-rollback-denied", DiagnosticSeverity.Error,
                $"Could not remove the {role} '{path}': {exception.Message}"));
        }
    }

    private static PresetToNpcPipelineResult Failed(ImmutableArray<Diagnostic> diagnostics) =>
        new(false, null, null, null, null, null, null, diagnostics);
}
