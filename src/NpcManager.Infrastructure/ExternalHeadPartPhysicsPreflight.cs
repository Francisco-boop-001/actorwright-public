using System.Collections.Immutable;
using System.Xml;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class ExternalHeadPartPhysicsBindingResolver
{
    /// <summary>Observes every declared dependency without producing binding authority.</summary>
    public ValueTask<(ImmutableArray<NpcBuildPreflightDependency> Dependencies,
        ImmutableArray<Diagnostic> Diagnostics)> ObservePreflightDependenciesAsync(
        WorkspacePath dataRoot,
        ImmutableArray<(AssetPath Path, ExternalHeadPartProviderNifReadResult Read)> models,
        CancellationToken cancellationToken) =>
        _xmlReader.ObservePreflightDependenciesAsync(dataRoot, models, cancellationToken);
}

internal sealed partial class ExternalHeadPartPhysicsXmlReader
{
    internal async ValueTask<(ImmutableArray<NpcBuildPreflightDependency> Dependencies,
        ImmutableArray<Diagnostic> Diagnostics)> ObservePreflightDependenciesAsync(
        WorkspacePath dataRoot,
        ImmutableArray<(AssetPath Path, ExternalHeadPartProviderNifReadResult Read)> models,
        CancellationToken cancellationToken)
    {
        var rows = new Dictionary<string, NpcBuildPreflightDependency>(StringComparer.OrdinalIgnoreCase);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (models.IsDefaultOrEmpty) return ([], []);
        AddReadRootDiagnostics(dataRoot, diagnostics);
        if (HasErrors(diagnostics)) return ([], diagnostics.ToImmutable());
        var xmlPaths = new HashSet<AssetPath>();
        bool direct = models.Any(model => !model.Read.PhysicsObjectLocators.IsDefaultOrEmpty);
        bool needsMap = models.Any(model => model.Read.PhysicsObjectLocators.IsDefaultOrEmpty);
        ExternalHeadPartDefaultBbpMap? map = null;
        if (needsMap && (direct || File.Exists(Path.Combine(dataRoot.Value, DefaultBbpRelativePath))))
        {
            ExternalHeadPartDefaultBbpReadResult read = await ReadDefaultBbpAsync(dataRoot, cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(read.Diagnostics);
            map = read.Mapping;
            rows[DefaultBbpRelativePath] = new("physics", DefaultBbpRelativePath, read.Accepted ? "present" : "missing");
        }
        foreach (var model in models)
        {
            foreach (string locator in model.Read.PhysicsObjectLocators)
            {
                if (ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(locator, PhysicsXmlPrefix, ".xml", out var path))
                    xmlPaths.Add(path);
                else
                {
                    rows[locator] = new("physics", locator, "nonCanonicalPath");
                    diagnostics.Add(Error("external-headpart-physics-xml-path", $"Model '{model.Path}' declares unsafe locator '{locator}'."));
                }
            }
            if (!model.Read.PhysicsObjectLocators.IsEmpty || map is null) continue;
            foreach (string shape in model.Read.ShapeNames)
            {
                ExternalHeadPartDefaultBbpEntry? entry = map.Entries.SingleOrDefault(item => item.ShapeName == shape);
                if (entry is not null) xmlPaths.Add(entry.XmlPath);
                else diagnostics.Add(Error(ExternalHeadPartDiagnosticCodes.PhysicsMissing,
                    $"'{DefaultBbpRelativePath}' has no physics binding for shape '{shape}' in '{model.Path}'."));
            }
        }
        foreach (AssetPath path in xmlPaths.OrderBy(item => item.Value, StringComparer.Ordinal))
        {
            byte[]? bytes = await Read(path, "physics", MaximumXmlBytes).ConfigureAwait(false);
            if (bytes is null) continue;
            try
            {
                ParsedPhysicsXml parsed = ParsePhysicsXml(path, bytes);
                foreach (AssetPath collider in parsed.ColliderPaths)
                    _ = await Read(collider, "mesh", MaximumAssetBytes).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is InvalidDataException or XmlException or ArgumentException)
            {
                diagnostics.Add(Error("external-headpart-physics-xml-refused", $"Physics XML '{path}' was refused: {exception.Message}"));
            }
        }
        return (rows.Values.OrderBy(item => item.Path, StringComparer.Ordinal).ToImmutableArray(), diagnostics.ToImmutable());

        async ValueTask<byte[]?> Read(AssetPath path, string kind, int maximumBytes)
        {
            try
            {
                ExternalHeadPartPhysicsFileReadEvidence evidence = await ReadOrdinaryFileAsync(
                    dataRoot, path, maximumBytes, cancellationToken).ConfigureAwait(false);
                rows[path.Value] = new(kind, path.Value, "present");
                return evidence.Bytes;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException)
            {
                rows[path.Value] = new(kind, path.Value, "missing");
                diagnostics.Add(Error("external-headpart-physics-asset-missing", $"Physics dependency '{path}' is unavailable: {exception.Message}"));
                return null;
            }
        }
    }
}
