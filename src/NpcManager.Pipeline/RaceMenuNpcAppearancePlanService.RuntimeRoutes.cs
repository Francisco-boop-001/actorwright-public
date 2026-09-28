using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcAppearancePlanService
{
    private async ValueTask<ImmutableDictionary<RaceMenuNpcAppearanceField, RuntimeRouteBinding>>
        ReadRuntimeRoutesAsync(
            RaceMenuNpcRuntimeRouteAuthority? authority,
            string bundleId,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        if (authority is null)
            return ImmutableDictionary<RaceMenuNpcAppearanceField, RuntimeRouteBinding>.Empty;

        var (document, _) = await ReadJsonAsync(authority.ManifestPath,
            authority.ExpectedManifestSha256, "runtime-route-manifest", diagnostics,
            cancellationToken);
        if (document is null)
            return ImmutableDictionary<RaceMenuNpcAppearanceField, RuntimeRouteBinding>.Empty;

        using (document)
        {
            try
            {
                return await ParseRuntimeRoutesAsync(document.RootElement, bundleId,
                    diagnostics, cancellationToken);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                diagnostics.Add(Error("racemenu-plan-runtime-route-manifest-invalid", exception.Message));
                return ImmutableDictionary<RaceMenuNpcAppearanceField, RuntimeRouteBinding>.Empty;
            }
        }
    }

    private async ValueTask<ImmutableDictionary<RaceMenuNpcAppearanceField, RuntimeRouteBinding>>
        ParseRuntimeRoutesAsync(
            JsonElement root,
            string bundleId,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        RequireShape(root, "runtime route manifest", "schemaVersion", "routeId", "bundleId", "routes");
        if (RequiredInt(root, "schemaVersion") != 1)
            throw new InvalidDataException("Runtime route manifest schemaVersion must be 1.");
        var routeId = RequiredString(root, "routeId");
        if (routeId.Length > 128)
            throw new InvalidDataException("Runtime routeId may not exceed 128 characters.");
        if (!string.Equals(RequiredString(root, "bundleId"), bundleId, StringComparison.Ordinal))
            throw new InvalidDataException("Runtime route manifest bundleId does not match the admitted bundle.");

        var routes = root.GetProperty("routes");
        if (routes.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Runtime route manifest routes must be an array.");

        var result = ImmutableDictionary.CreateBuilder<RaceMenuNpcAppearanceField, RuntimeRouteBinding>();
        foreach (var route in routes.EnumerateArray())
        {
            RequireShape(route, "runtime route", "field", "classification",
                "artifactPath", "artifactSha256");
            var fieldName = RequiredString(route, "field");
            if (!RaceMenuNpcAppearanceFieldExtensions.TryParseRuntimeWireName(fieldName, out var field))
                throw new InvalidDataException($"Runtime route field '{fieldName}' is not supported.");
            if (!string.Equals(RequiredString(route, "classification"), "runtime-declared",
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"Runtime route '{fieldName}' must use classification 'runtime-declared'.");
            }
            if (result.ContainsKey(field))
                throw new InvalidDataException($"Runtime route '{fieldName}' is duplicated.");

            var artifact = ResolveRelative(RequiredString(route, "artifactPath"),
                $"runtime route '{fieldName}' artifact");
            var hash = new Sha256Hash(RequiredString(route, "artifactSha256"));
            var (artifactDocument, _) = await ReadJsonAsync(artifact, hash,
                "runtime-route-artifact", diagnostics, cancellationToken);
            if (artifactDocument is null) continue;
            using (artifactDocument)
            {
                var artifactRoot = artifactDocument.RootElement;
                RequireShape(artifactRoot, $"runtime route '{fieldName}' artifact",
                    "schemaVersion", "field");
                if (RequiredInt(artifactRoot, "schemaVersion") != 1 ||
                    !string.Equals(RequiredString(artifactRoot, "field"), fieldName,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Runtime route artifact for '{fieldName}' does not bind schema 1 and the same field.");
                }
                result.Add(field, new RuntimeRouteBinding(artifact, hash));
            }
        }
        return result.ToImmutable();
    }

    private sealed record RuntimeRouteBinding(
        WorkspacePath ArtifactPath,
        Sha256Hash ArtifactSha256);
}
