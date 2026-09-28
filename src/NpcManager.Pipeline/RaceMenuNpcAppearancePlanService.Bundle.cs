using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcAppearancePlanService
{
    private async ValueTask<BundleBinding?> ReadBundleAsync(
        RaceMenuNpcBuildRequest request,
        BlankNpcProviderArtifact provider,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var manifest = request.PresetBundle.ManifestPath;
        var (document, manifestHash) = await ReadJsonAsync(
            manifest, request.PresetBundle.ExpectedManifestSha256,
            "bundle-manifest", diagnostics, cancellationToken);
        if (document is null || manifestHash is null) return null;

        using (document)
        {
            try
            {
                return ParseBundle(document.RootElement, request, provider, manifestHash.Value);
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException)
            {
                diagnostics.Add(Error("racemenu-plan-bundle-manifest-invalid", exception.Message));
                return null;
            }
        }
    }

    private BundleBinding ParseBundle(
        JsonElement root,
        RaceMenuNpcBuildRequest request,
        BlankNpcProviderArtifact provider,
        Sha256Hash manifestHash)
    {
        int schemaVersion = RequiredInt(root, "schemaVersion");
        if (schemaVersion == 1)
            RequireShape(root, "RaceMenu preset bundle", "schemaVersion", "bundleId", "edition",
                "preset", "charGen", "providerContext", "recordAuthority", "runtimeRoutes");
        else if (schemaVersion == 3)
            RequireShape(root, "RaceMenu preset bundle", "schemaVersion", "bundleId", "edition",
                "preset", "charGen", "providerAuthority", "recordAuthority", "runtimeRoutes");
        else
            throw new InvalidDataException("RaceMenu preset bundle schemaVersion must be 1 or 3.");
        var bundleId = RequiredString(root, "bundleId");
        if (bundleId.Length > 128)
            throw new InvalidDataException("RaceMenu bundleId may not exceed 128 characters.");
        if (!GameEditionExtensions.TryParseWireName(RequiredString(root, "edition"), out var edition) ||
            edition != request.Edition || edition != GameEdition.SkyrimSpecialEdition)
            throw new InvalidDataException("RaceMenu bundle edition does not match the Skyrim SE request.");

        BindPreset(root.GetProperty("preset"), request.PresetBundle);
        BindCharGen(root.GetProperty("charGen"), request.PresetBundle);
        if (schemaVersion == 1)
            BindProvider(root.GetProperty("providerContext"), request, provider);
        else
            BindProductProvider(root.GetProperty("providerAuthority"), request, provider);
        BindRecordAuthority(root.GetProperty("recordAuthority"),
            request.PresetBundle.RecordAuthority);
        BindRuntimeRoutes(root.GetProperty("runtimeRoutes"), request.PresetBundle.RuntimeRoutes);
        return new BundleBinding(bundleId, manifestHash);
    }

    private void BindRecordAuthority(
        JsonElement element,
        RaceMenuNpcRecordAuthority authority)
    {
        RequireShape(element, "RaceMenu bundle record authority", "manifestPath", "manifestSha256");
        BindPath("record-authority manifest path",
            ResolveRelative(RequiredString(element, "manifestPath"),
                "record-authority manifest path"),
            authority.ManifestPath);
        BindHash("record-authority manifest",
            new Sha256Hash(RequiredString(element, "manifestSha256")),
            authority.ExpectedManifestSha256);
    }

    private void BindPreset(JsonElement element, RaceMenuNpcPresetBundle bundle)
    {
        RequireShape(element, "RaceMenu bundle preset", "path", "sha256");
        BindPath("preset path", ResolveRelative(RequiredString(element, "path"), "preset path"),
            bundle.PresetPath);
        BindHash("preset", new Sha256Hash(RequiredString(element, "sha256")),
            bundle.ExpectedPresetSha256);
    }

    private void BindCharGen(JsonElement element, RaceMenuNpcPresetBundle bundle)
    {
        RequireShape(element, "RaceMenu bundle CharGen", "faceGeomPath", "faceGeomSha256",
            "faceTintPath", "faceTintSha256");
        BindPath("CharGen FaceGeom path",
            ResolveRelative(RequiredString(element, "faceGeomPath"), "CharGen FaceGeom path"),
            bundle.CharGenFaceGeom);
        BindHash("CharGen FaceGeom",
            new Sha256Hash(RequiredString(element, "faceGeomSha256")),
            bundle.ExpectedCharGenFaceGeomSha256);
        BindPath("CharGen FaceTint path",
            ResolveRelative(RequiredString(element, "faceTintPath"), "CharGen FaceTint path"),
            bundle.CharGenFaceTint);
        BindHash("CharGen FaceTint",
            new Sha256Hash(RequiredString(element, "faceTintSha256")),
            bundle.ExpectedCharGenFaceTintSha256);
    }

    private void BindProvider(
        JsonElement element,
        RaceMenuNpcBuildRequest request,
        BlankNpcProviderArtifact provider)
    {
        RequireShape(element, "RaceMenu bundle provider context", "manifestPath", "manifestSha256",
            "dependencyManifestPath", "dependencyManifestSha256");
        BindPath("provider manifest path",
            ResolveRelative(RequiredString(element, "manifestPath"), "provider manifest path"),
            request.ProviderContext.ManifestPath);
        BindHash("provider manifest",
            new Sha256Hash(RequiredString(element, "manifestSha256")),
            provider.ManifestSha256);
        BindPath("dependency manifest path",
            ResolveRelative(RequiredString(element, "dependencyManifestPath"), "dependency manifest path"),
            request.ProviderContext.DependencyManifest);
        BindHash("dependency manifest",
            new Sha256Hash(RequiredString(element, "dependencyManifestSha256")),
            provider.DependencyManifestSha256);
    }

    private static void BindProductProvider(
        JsonElement element,
        RaceMenuNpcBuildRequest request,
        BlankNpcProviderArtifact provider)
    {
        RequireShape(element, "RaceMenu bundle product-provider authority", "kind", "bundleId",
            "registryManifestSha256");
        ProviderResourceAuthoritySet resources =
            request.ProviderContext.ProviderResources ??
            throw new InvalidDataException(
                "The schema-3 preset bundle requires a product-provider request authority.");
        if (provider.ProviderResources is null ||
            !string.Equals(RequiredString(element, "kind"), "product-fixture",
                StringComparison.Ordinal) ||
            !string.Equals(RequiredString(element, "bundleId"), resources.BundleId,
                StringComparison.Ordinal) ||
            new Sha256Hash(RequiredString(element, "registryManifestSha256")) !=
                resources.RegistryManifestSha256 ||
            !string.Equals(provider.ProviderResources.BundleId, resources.BundleId,
                StringComparison.Ordinal) ||
            provider.ProviderResources.RegistryManifestSha256 !=
                resources.RegistryManifestSha256)
        {
            throw new InvalidDataException(
                "The preset bundle product-provider identity does not match the qualified request authority.");
        }
    }

    private void BindRuntimeRoutes(
        JsonElement element,
        RaceMenuNpcRuntimeRouteAuthority? runtimeRoutes)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (runtimeRoutes is not null)
                throw new InvalidDataException("The typed runtime routes are not bound by the preset bundle.");
            return;
        }
        if (runtimeRoutes is null)
            throw new InvalidDataException("The preset bundle binds runtime routes that were not explicitly requested.");
        RequireShape(element, "RaceMenu bundle runtime routes", "manifestPath", "manifestSha256");
        BindPath("runtime-route manifest path",
            ResolveRelative(RequiredString(element, "manifestPath"), "runtime-route manifest path"),
            runtimeRoutes.ManifestPath);
        BindHash("runtime-route manifest",
            new Sha256Hash(RequiredString(element, "manifestSha256")),
            runtimeRoutes.ExpectedManifestSha256);
    }

    private sealed record BundleBinding(string BundleId, Sha256Hash ManifestSha256);
}
