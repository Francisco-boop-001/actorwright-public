using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Resolves the canonical FaceGen filenames used by the pinned upstream packer
/// against an explicit copied-Data load order and asset index. This is deliberately
/// read-only: provider evidence is not a live loadability or runtime authority.
/// </summary>
public sealed class FaceGenProviderResolutionService(
    IGameInventoryService inventoryService,
    IAssetIndexer assetIndexer,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IFaceTintProviderBindingReader faceTintBindingReader) : IFaceGenProviderResolutionService
{
    public async ValueTask<FaceGenProviderResolutionResult> ResolveAsync(
        FaceGenProviderResolutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.DataRoot));
        if (request.PluginOrder.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic("facegen-provider-plugin-order-required", DiagnosticSeverity.Error,
                "FaceGen provider resolution requires an explicit --plugins load order; filename order is not authoritative."));
        }
        else if (request.PluginOrder.Select(item => item.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(new Diagnostic("facegen-provider-duplicate-plugin", DiagnosticSeverity.Error,
                "FaceGen provider resolution requires a load order without duplicate plugin names."));
        }
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        var inventory = await inventoryService.ReadAsync(new GameInventoryRequest(
            request.Edition, request.DataRoot, null, request.NpcFormId, false,
            request.PluginOrder), cancellationToken);
        diagnostics.AddRange(inventory.Diagnostics);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        var matches = inventory.Npcs.Where(item => item.FormId == request.NpcFormId).ToImmutableArray();
        var provenance = matches.Length == 1 ? matches[0].Provenance : null;
        if (provenance is null || provenance.OverrideChain.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic("facegen-provider-npc-not-unique", DiagnosticSeverity.Error,
                $"NPC {request.NpcFormId} did not resolve to exactly one explicit-load-order provenance chain."));
            return Refused(diagnostics);
        }

        var index = await assetIndexer.IndexAsync(new AssetIndexRequest(request.Edition, request.DataRoot), cancellationToken);
        diagnostics.AddRange(index.Diagnostics);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        var npc = matches[0];
        var origin = provenance.OverrideChain[0];
        var localFormId = FaceGenProviderPathRules.ToFaceGenLocalFormId(request.NpcFormId);
        var artifacts = BuildArtifacts(request.Edition, origin, localFormId, request.IncludeSharedNeutralDetail,
            index.Providers, diagnostics);
        FaceTintRaceBinding? faceTintBinding = null;
        if (request.Edition == GameEdition.Fallout4)
        {
            if (npc.Metadata?.Sex is not { } sex)
            {
                diagnostics.Add(new Diagnostic("facetint-provider-npc-sex-missing", DiagnosticSeverity.Error,
                    $"FO4 NPC {request.NpcFormId} has no typed sex metadata for RACE tint binding."));
            }
            else
            {
                var bindingResult = await faceTintBindingReader.ReadAsync(
                    new FaceTintProviderBindingRequest(request.Edition, request.DataRoot,
                        request.NpcFormId, npc.Plugin, sex, request.PluginOrder), cancellationToken);
                diagnostics.AddRange(bindingResult.Diagnostics);
                faceTintBinding = bindingResult.Binding;
            }
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var artifact = new FaceGenProviderResolutionArtifact(
            "3", "facegen-provider-resolution", request.Edition.ToWireName(), request.NpcFormId.ToString(),
            $"0x{localFormId:X8}", origin.Value, npc.Plugin.Value, provenance.OverrideChain, artifacts,
            new FaceGenProviderNpcContext(npc.Metadata?.Sex, npc.Metadata?.RaceFormId,
                npc.Metadata?.HeadPartFormIds ?? ImmutableArray<FormId>.Empty),
            faceTintBinding);
        return new FaceGenProviderResolutionResult(true, artifact, diagnostics.ToImmutable());
    }

    private static ImmutableArray<FaceGenProviderArtifact> BuildArtifacts(
        GameEdition edition,
        PluginName origin,
        uint localFormId,
        bool includeSharedNeutralDetail,
        ImmutableArray<AssetProvider> providers,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var hex = localFormId.ToString("X8");
        var specs = new List<(FaceGenProviderArtifactKind Kind, string Path, FaceGenProviderRequiredness Requiredness)>();
        var geom = $"Meshes/Actors/Character/FaceGenData/FaceGeom/{origin.Value}/{hex}.nif";
        specs.Add((FaceGenProviderArtifactKind.FaceGeom, geom, FaceGenProviderRequiredness.Required));
        if (edition == GameEdition.Fallout4)
        {
            var root = $"Textures/Actors/Character/FaceCustomization/{origin.Value}/{hex}";
            specs.Add((FaceGenProviderArtifactKind.FaceCustomizationDiffuse, root + "_d.dds", FaceGenProviderRequiredness.Required));
            specs.Add((FaceGenProviderArtifactKind.FaceCustomizationNormal, root + "_msn.dds", FaceGenProviderRequiredness.Required));
            specs.Add((FaceGenProviderArtifactKind.FaceCustomizationSpecular, root + "_s.dds", FaceGenProviderRequiredness.Required));
        }
        else
        {
            var tintRoot = $"Textures/Actors/Character/FaceGenData/FaceTint/{origin.Value}/";
            specs.Add((FaceGenProviderArtifactKind.FaceTint, tintRoot + hex + ".dds", FaceGenProviderRequiredness.Required));
            if (includeSharedNeutralDetail)
                specs.Add((FaceGenProviderArtifactKind.FaceDetailNeutral, tintRoot + "facedetailneutral.dds", FaceGenProviderRequiredness.Required));
            specs.Add((FaceGenProviderArtifactKind.FaceDiffuse,
                $"Textures/Actors/Character/FaceGenData/FaceDiffuse/{origin.Value}/{hex}.dds", FaceGenProviderRequiredness.Optional));
            specs.Add((FaceGenProviderArtifactKind.FaceNormal,
                $"Textures/Actors/Character/FaceGenData/FaceNormal/{origin.Value}/{hex}.dds", FaceGenProviderRequiredness.Optional));
        }

        var byPath = providers.GroupBy(item => item.Path.Value, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToImmutableArray(), StringComparer.OrdinalIgnoreCase);
        var result = ImmutableArray.CreateBuilder<FaceGenProviderArtifact>(specs.Count);
        foreach (var spec in specs)
        {
            AssetPath path;
            try { path = new AssetPath(spec.Path); }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("facegen-provider-path-invalid", DiagnosticSeverity.Error,
                    $"Canonical FaceGen path '{spec.Path}' is invalid: {exception.Message}"));
                continue;
            }
            var evidence = byPath.TryGetValue(path.Value, out var matches)
                ? matches.Select(provider => new FaceGenProviderEvidence(provider.Kind, provider.Source,
                    provider.Size, new Sha256Hash(provider.Sha256))).ToImmutableArray()
                : ImmutableArray<FaceGenProviderEvidence>.Empty;
            if (evidence.IsDefaultOrEmpty)
            {
                diagnostics.Add(new Diagnostic("facegen-provider-missing", spec.Requiredness == FaceGenProviderRequiredness.Required
                    ? DiagnosticSeverity.Warning : DiagnosticSeverity.Info,
                    $"No copied asset provider was found for canonical FaceGen path '{path.Value}'."));
            }
            result.Add(new FaceGenProviderArtifact(spec.Kind, path, spec.Requiredness, evidence));
        }
        return result.ToImmutable();
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static FaceGenProviderResolutionResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
