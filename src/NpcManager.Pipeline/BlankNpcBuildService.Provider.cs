using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private sealed class PluginNameComparer : IEqualityComparer<PluginName>
    {
        public static PluginNameComparer Instance { get; } = new();

        public bool Equals(PluginName left, PluginName right) =>
            string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(PluginName value) =>
            StringComparer.OrdinalIgnoreCase.GetHashCode(value.Value);
    }

    private static BlankNpcProviderBindingRequest CreateProviderBindingRequest(
        BlankNpcBuildRequest request) =>
        new(
            request.ProviderManifest,
            request.ExpectedProviderManifestSha256,
            request.Edition,
            request.Traits.Sex,
            request.TemplatePlugin,
            request.ExpectedTemplatePluginSha256,
            request.TemplateNpcFormId,
            request.FaceGeomCarrier,
            request.ExpectedFaceGeomCarrierSha256,
            request.FaceTintManifest,
            request.FaceTintProviderRoot,
            request.DependencyManifest)
        {
            ProviderResources = request.ProviderResources
        };

    private static bool ValidateProviderMasters(
        BlankNpcProviderArtifact provider,
        ImmutableArray<PluginName> actual,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var matches = actual.Length >= provider.TemplateMasters.Length &&
                      actual.Take(provider.TemplateMasters.Length)
                          .Zip(provider.TemplateMasters).All(pair =>
                          string.Equals(pair.First.Value, pair.Second.Value,
                              StringComparison.OrdinalIgnoreCase));
        if (!matches)
        {
            diagnostics.Add(Error("blank-npc-provider-master-drift",
                "The creation master list does not preserve the copied template's exact master prefix."));
        }
        return matches;
    }

    private static bool ValidateProviderFaceGeom(
        BlankNpcProviderArtifact provider,
        QualifiedFaceGeomCarrierStructure actual,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var namesMatch = actual.ReachableShapeNames.Length == provider.ExpectedShapeNames.Length &&
                         actual.ReachableShapeNames.Zip(provider.ExpectedShapeNames).All(pair =>
                             string.Equals(pair.First, pair.Second, StringComparison.Ordinal));
        var matches = actual.GraphSha256 == provider.ExpectedFaceGeomGraphSha256 && namesMatch;
        if (!matches)
        {
            diagnostics.Add(Error("blank-npc-provider-facegeom-drift",
                "The qualified FaceGeom graph or shape identity does not match the admitted provider bundle."));
        }
        return matches;
    }

    private static bool ValidateProviderFaceTint(
        BlankNpcProviderArtifact provider,
        FaceTintBuildArtifact? actual,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (actual is null)
        {
            diagnostics.Add(Error("blank-npc-provider-facetint-evidence-missing",
                "The FaceTint build did not return its provider evidence."));
            return false;
        }

        var manifestMatches = new Sha256Hash(actual.InputManifestSha256) ==
                              provider.FaceTintManifestSha256;
        var sourceMatches = actual.ProviderSources is { } sources && sources.Length == 1 &&
                            string.Equals(sources[0].Source, provider.FaceTintSourceAssetPath.Value,
                                StringComparison.OrdinalIgnoreCase) &&
                            new Sha256Hash(sources[0].Sha256) == provider.FaceTintSourceAssetSha256;
        if (!manifestMatches || !sourceMatches)
        {
            diagnostics.Add(Error("blank-npc-provider-facetint-drift",
                "The FaceTint recipe or sampled source does not match the admitted provider bundle."));
            return false;
        }
        return true;
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
