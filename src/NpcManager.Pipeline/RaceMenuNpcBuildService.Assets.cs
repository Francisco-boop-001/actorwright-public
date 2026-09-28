using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public partial class RaceMenuNpcStandaloneAuthorityReader :
    IRaceMenuNpcStandaloneAuthorityReader
{
    private readonly IAssetIndexer assetIndexer;
    protected IWorkspacePolicy WorkspacePolicy { get; }
    protected WorkspacePath LaboratoryRoot { get; }

    public RaceMenuNpcStandaloneAuthorityReader(
        IAssetIndexer assetIndexer,
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        this.assetIndexer = assetIndexer ??
            throw new ArgumentNullException(nameof(assetIndexer));
        WorkspacePolicy = policy ??
            throw new ArgumentNullException(nameof(policy));
        LaboratoryRoot = labRoot;
    }

    private const int MaximumStandaloneManifestBytes = 1 * 1024 * 1024;
    private static readonly string[] Schema7StandaloneRequiredFields =
    [
        "schemaVersion", "assetSetId", "edition", "nam9Authority",
        "faceTint", "privateHeadTextures", "packageAssets",
        "overlayDecisions", "externalTextureAuthorities",
        "bodySlidePresetAuthority", "bodyMeshAuthority",
        "finalOutputAuthority"
    ];
    private static readonly string[] Schema8StandaloneRequiredFields =
    [
        "schemaVersion", "assetSetId", "edition", "nam9Authority",
        "faceTint", "privateHeadTextures", "packageAssets",
        "overlayDecisions", "externalTextureAuthorities",
        "externalHeadPartDependencies",
        "externalHeadPartFaceGeomExclusionAttestations",
        "bodySlidePresetAuthority", "bodyMeshAuthority",
        "finalOutputAuthority"
    ];

    public async ValueTask<RaceMenuNpcStandaloneAuthorityReadResult> ReadAsync(
        RaceMenuNpcStandaloneAssetAuthority authority,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(authority);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        RaceMenuNpcStandaloneAssets? assets = await ReadStandaloneAssetsAsync(
            authority, diagnostics, cancellationToken).ConfigureAwait(false);
        return new RaceMenuNpcStandaloneAuthorityReadResult(
            assets is not null && !HasErrors(diagnostics),
            assets,
            diagnostics.ToImmutable());
    }

    protected async ValueTask<RaceMenuNpcStandaloneAssets?> ReadStandaloneAssetsAsync(
        RaceMenuNpcStandaloneAssetAuthority authority,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(WorkspacePolicy.EvaluateReadRoot(
            LaboratoryRoot,
            authority.ManifestPath));
        if (HasErrors(diagnostics)) return null;
        try
        {
            var info = new FileInfo(authority.ManifestPath.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumStandaloneManifestBytes ||
                File.GetAttributes(authority.ManifestPath.Value).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("racemenu-assets-manifest-file",
                    "The standalone-asset manifest must be an ordinary non-empty K-local file no larger than 1 MiB."));
                return null;
            }
            var bytes = await File.ReadAllBytesAsync(authority.ManifestPath.Value, cancellationToken);
            var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
            if (hash != authority.ExpectedManifestSha256)
            {
                diagnostics.Add(Error("racemenu-assets-manifest-hash",
                    $"Standalone-asset manifest hash {hash} does not match {authority.ExpectedManifestSha256}."));
                return null;
            }

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 16
            });
            RejectDuplicateKeys(document.RootElement);
            var root = document.RootElement;
            RejectReservedPackageAssetSubstitution(root);
            var schemaVersion = RequiredInt(root, "schemaVersion");
            switch (schemaVersion)
            {
                case 3:
                    RequireShape(root, "standalone assets", "schemaVersion", "assetSetId",
                        "edition", "nam9Authority", "faceTint", "privateHeadTextures",
                        "packageAssets", "overlayDecisions", "externalTextureAuthorities",
                        "finalOutputAuthority");
                    break;
                case 4:
                    RequireShape(root, "standalone assets", "schemaVersion", "assetSetId",
                        "edition", "nam9Authority", "faceTint", "privateHeadTextures",
                        "packageAssets", "overlayDecisions", "externalTextureAuthorities",
                        "faceBakeAuthority", "finalOutputAuthority");
                    break;
                case 5:
                    RequireShape(root, "standalone assets", "schemaVersion", "assetSetId",
                        "edition", "nam9Authority", "faceTint", "privateHeadTextures",
                        "packageAssets", "overlayDecisions", "externalTextureAuthorities",
                        "faceBakeAuthority", "faceTextureBakeAuthority",
                        "finalOutputAuthority");
                    break;
                case 6:
                    RequireShape(root, "standalone assets", "schemaVersion", "assetSetId",
                        "edition", "nam9Authority", "faceTint", "privateHeadTextures",
                        "packageAssets", "overlayDecisions", "externalTextureAuthorities",
                        "finalOutputAuthority");
                    break;
                case 7:
                    RequireShapeWithOptional(root, "standalone assets",
                        Schema7StandaloneRequiredFields,
                        "externalCharGenExportAuthority",
                        "nativeFaceGeomExternalHeadParts");
                    break;
                case 8:
                    RequireShape(root, "standalone assets",
                        Schema8StandaloneRequiredFields);
                    break;
                default:
                    throw new InvalidDataException(
                        "Standalone assets require schemaVersion 3, 4, 5, 6, 7, or 8.");
            }
            if (!string.Equals(RequiredString(root, "edition"), "skyrimse",
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Standalone assets require edition 'skyrimse'.");
            var assetSetId = RequiredString(root, "assetSetId");
            if (assetSetId.Length > 128)
                throw new InvalidDataException("Standalone assetSetId may not exceed 128 characters.");

            var nam9 = root.GetProperty("nam9Authority");
            RequireShape(nam9, "NAM9 authority", "pluginPath", "pluginSha256", "npcFormId",
                "trailingValue");
            if (!FormId.TryParse(RequiredString(nam9, "npcFormId"), out var nam9Npc))
                throw new InvalidDataException("NAM9 authority npcFormId is invalid.");
            var trailing = RequiredSingle(nam9, "trailingValue");
            var nam9Authority = new RaceMenuNpcNam9TrailingAuthority(
                ResolveRelative(RequiredString(nam9, "pluginPath")),
                new Sha256Hash(RequiredString(nam9, "pluginSha256")), nam9Npc, trailing);

            var faceTint = root.GetProperty("faceTint");
            RequireShape(faceTint, "FaceTint dimensions", "width", "height");
            var width = RequiredInt(faceTint, "width");
            var height = RequiredInt(faceTint, "height");
            if (width <= 0 || height <= 0)
                throw new InvalidDataException("FaceTint width and height must be positive.");
            if (schemaVersion == 5 &&
                (width > RaceMenuNpcFaceTextureCompositionLimits.MaximumAxisPixels ||
                 height > RaceMenuNpcFaceTextureCompositionLimits.MaximumAxisPixels ||
                 (long)width * height >
                    RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels))
            {
                throw new InvalidDataException(
                    $"SchemaVersion 5 FaceTint dimensions exceed the product composition " +
                    $"budget of {RaceMenuNpcFaceTextureCompositionLimits.MaximumPixels} pixels.");
            }

            var textures = root.GetProperty("privateHeadTextures");
            RequireShape(textures, "private head textures", "diffuse", "normalOrGloss",
                "glowOrDetailMap", "height", "backlightMaskOrSpecular",
                "environmentMaskOrSubsurfaceTint", "environment", "multilayer");
            var privateTextures = new SkyrimPrivateHeadTexturePaths(
                RequiredAssetPath(textures, "diffuse"),
                RequiredAssetPath(textures, "normalOrGloss"),
                RequiredAssetPath(textures, "glowOrDetailMap"),
                RequiredAssetPath(textures, "height"),
                RequiredAssetPath(textures, "backlightMaskOrSpecular"),
                NullableAssetPath(textures, "environmentMaskOrSubsurfaceTint"),
                NullableAssetPath(textures, "environment"),
                NullableAssetPath(textures, "multilayer"));

            var packageRows = root.GetProperty("packageAssets");
            if (packageRows.ValueKind != JsonValueKind.Array ||
                (schemaVersion == 8 && packageRows.GetArrayLength() != 0) ||
                (schemaVersion is not (6 or 7 or 8) && packageRows.GetArrayLength() == 0))
                throw new InvalidDataException(
                    schemaVersion == 8
                        ? "SchemaVersion 8 requires packageAssets to be an empty array."
                        : "packageAssets must be an array and may be empty only for direct CharGen schemaVersion 6 or 7.");
            var packageAssets = ImmutableArray.CreateBuilder<BlankNpcTransitivePackageAsset>();
            foreach (var row in packageRows.EnumerateArray())
            {
                RequireShape(row, "package asset", "sourcePath", "sha256", "destination");
                string originalDestination = RequiredString(row, "destination");
                var destination = NormalizePackageDestination(originalDestination);
                if (!string.Equals(originalDestination, destination.Value, StringComparison.Ordinal))
                    diagnostics.Add(new Diagnostic("racemenu-package-destination-normalized", DiagnosticSeverity.Info,
                        $"Package destination '{originalDestination}' admitted as '{destination.Value}'. The source manifest bytes are unchanged."));
                if (SkyrimApplySseProductAsset.IsReservedDestination(destination))
                    throw new InvalidDataException(
                        $"Package destination '{destination.Value}' is product-owned and callers may not supply or replace it.");
                packageAssets.Add(new BlankNpcTransitivePackageAsset(
                    ResolveRelative(RequiredString(row, "sourcePath")),
                    new Sha256Hash(RequiredString(row, "sha256")),
                    destination));
            }

            RaceMenuNpcOverlayDecisionSet? overlayDecisions = null;
            var decisionAuthority = root.GetProperty("overlayDecisions");
            if (decisionAuthority.ValueKind != JsonValueKind.Null)
            {
                RequireShape(decisionAuthority, "overlay-decision authority", "manifestPath",
                    "manifestSha256");
                overlayDecisions = await ReadOverlayDecisionsAsync(
                    ResolveRelative(RequiredString(decisionAuthority, "manifestPath")),
                    new Sha256Hash(RequiredString(decisionAuthority, "manifestSha256")),
                    diagnostics, cancellationToken);
                if (overlayDecisions is null) return null;
            }

            var externalRows = root.GetProperty("externalTextureAuthorities");
            if (externalRows.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("externalTextureAuthorities must be an array.");
            var externalAuthorities = ImmutableArray.CreateBuilder<RaceMenuNpcExternalTextureAuthority>();
            var externalPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in externalRows.EnumerateArray())
            {
                RequireShape(row, "external texture authority", "dataRelativePath", "provider",
                    "sourcePath", "sourceSha256", "memberSha256");
                var dataRelativePath = CanonicalDataTexturePath(
                    RequiredString(row, "dataRelativePath"), "external texture authority");
                if (!externalPaths.Add(dataRelativePath.Value))
                    throw new InvalidDataException(
                        $"External texture authority '{dataRelativePath.Value}' occurs more than once.");
                var provider = RequiredString(row, "provider");
                if (provider.Length > 260)
                    throw new InvalidDataException("External texture provider may not exceed 260 characters.");
                externalAuthorities.Add(new RaceMenuNpcExternalTextureAuthority(
                    dataRelativePath,
                    provider,
                    ResolveRelative(RequiredString(row, "sourcePath")),
                    new Sha256Hash(RequiredString(row, "sourceSha256")),
                    new Sha256Hash(RequiredString(row, "memberSha256"))));
            }

            var externalHeadPartDependencies =
                ImmutableArray.CreateBuilder<ExternalHeadPartDependencyDescriptor>();
            var externalHeadPartExclusionAttestations =
                ImmutableArray.CreateBuilder<ExternalHeadPartFaceGeomExclusionAttestation>();
            if (schemaVersion == 8)
            {
                var descriptorRows = root.GetProperty(
                    "externalHeadPartDependencies");
                var attestationRows = root.GetProperty(
                    "externalHeadPartFaceGeomExclusionAttestations");
                if (descriptorRows.ValueKind != JsonValueKind.Array ||
                    descriptorRows.GetArrayLength() == 0 ||
                    attestationRows.ValueKind != JsonValueKind.Array ||
                    attestationRows.GetArrayLength() == 0 ||
                    descriptorRows.GetArrayLength() != attestationRows.GetArrayLength())
                    throw new InvalidDataException(
                        "SchemaVersion 8 requires non-empty descriptor and matching FaceGeom exclusion-attestation arrays.");

                var descriptorIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement row in descriptorRows.EnumerateArray())
                {
                    ExternalHeadPartDependencyDescriptor descriptor =
                        ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(
                            Encoding.UTF8.GetBytes(row.GetRawText()));
                    if (!descriptorIds.Add(descriptor.DescriptorId.Value))
                        throw new InvalidDataException(
                            $"SchemaVersion 8 descriptor '{descriptor.DescriptorId}' occurs more than once.");
                    externalHeadPartDependencies.Add(descriptor);
                }

                var attestationIds = new HashSet<string>(StringComparer.Ordinal);
                foreach (JsonElement row in attestationRows.EnumerateArray())
                {
                    ExternalHeadPartFaceGeomExclusionAttestation attestation =
                        ExternalHeadPartDependencyDescriptorCodec.ParseAttestation(
                            Encoding.UTF8.GetBytes(row.GetRawText()));
                    if (!attestationIds.Add(attestation.DescriptorId.Value))
                        throw new InvalidDataException(
                            $"SchemaVersion 8 attestation for descriptor '{attestation.DescriptorId}' occurs more than once.");
                    if (!descriptorIds.Contains(attestation.DescriptorId.Value))
                        throw new InvalidDataException(
                            $"SchemaVersion 8 attestation '{attestation.DescriptorId}' has no matching descriptor.");
                    externalHeadPartExclusionAttestations.Add(attestation);
                }
                if (descriptorIds.Any(id => !attestationIds.Contains(id)))
                    throw new InvalidDataException(
                        "SchemaVersion 8 requires exactly one FaceGeom exclusion attestation per descriptor.");
            }

            var nativeExternalRows =
                ImmutableArray.CreateBuilder<SkyrimNativeFaceGeomExternalHeadPart>();
            if (root.TryGetProperty(
                    "nativeFaceGeomExternalHeadParts",
                    out JsonElement nativeExternalElement))
            {
                if (schemaVersion != 7 ||
                    nativeExternalElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException(
                        "nativeFaceGeomExternalHeadParts is supported only as a schemaVersion 7 array.");
                for (var index = 0;
                     index < nativeExternalElement.GetArrayLength();
                     index++)
                {
                    JsonElement row = nativeExternalElement[index];
                    RequireShape(
                        row,
                        "native FaceGeom external headpart",
                        "order",
                        "sourceFormIdentifier",
                        "providerFormKey",
                        "disposition");
                    int order = RequiredInt(row, "order");
                    if (order != index)
                        throw new InvalidDataException(
                            "nativeFaceGeomExternalHeadParts order values must be contiguous from zero.");
                    string sourceFormIdentifier =
                        RequiredString(row, "sourceFormIdentifier");
                    string providerFormKey =
                        RequiredString(row, "providerFormKey");
                    string disposition =
                        RequiredString(row, "disposition");
                    if (!string.Equals(
                            sourceFormIdentifier,
                            SkyrimNativeFaceGeomExternalHeadPartAuthority
                                .DintSourceFormIdentifier,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            providerFormKey,
                            SkyrimNativeFaceGeomExternalHeadPartAuthority
                                .DintProviderFormKey,
                            StringComparison.Ordinal) ||
                        !string.Equals(
                            disposition,
                            SkyrimNativeFaceGeomExternalHeadPartAuthority
                                .RecordOnlyExternalDisposition,
                            StringComparison.Ordinal))
                    {
                        throw new InvalidDataException(
                            "nativeFaceGeomExternalHeadParts contains an unadmitted external-provider row.");
                    }
                    nativeExternalRows.Add(
                        new SkyrimNativeFaceGeomExternalHeadPart(
                            order,
                            sourceFormIdentifier,
                            providerFormKey,
                            disposition));
                }
            }

            RaceMenuNpcFinalOutputAuthority? finalOutputAuthority = null;
            var finalAuthorityElement = root.GetProperty("finalOutputAuthority");
            if (finalAuthorityElement.ValueKind != JsonValueKind.Null)
            {
                RequireShape(finalAuthorityElement, "final-output authority reference",
                    "manifestPath", "manifestSha256");
                finalOutputAuthority = await ReadFinalOutputAuthorityAsync(
                    ResolveRelative(RequiredString(finalAuthorityElement, "manifestPath")),
                    new Sha256Hash(RequiredString(finalAuthorityElement, "manifestSha256")),
                    diagnostics,
                    cancellationToken);
                if (finalOutputAuthority is null) return null;
            }

            RaceMenuNpcFaceBakeAuthorityReference? faceBakeAuthority = null;
            if (schemaVersion is 4 or 5)
            {
                var faceBakeElement = root.GetProperty("faceBakeAuthority");
                if (faceBakeElement.ValueKind == JsonValueKind.Null)
                    throw new InvalidDataException(
                        $"SchemaVersion {schemaVersion} requires an exact faceBakeAuthority reference.");
                RequireShape(faceBakeElement, "face-bake authority reference",
                    "manifestPath", "manifestSha256");
                faceBakeAuthority = new RaceMenuNpcFaceBakeAuthorityReference(
                    ResolveRelative(RequiredString(faceBakeElement, "manifestPath")),
                    new Sha256Hash(RequiredString(faceBakeElement, "manifestSha256")));
                if (finalOutputAuthority is not null)
                    throw new InvalidDataException(
                        $"SchemaVersion {schemaVersion} requires finalOutputAuthority to be null.");
            }
            else if (schemaVersion == 3 && finalOutputAuthority is null)
            {
                throw new InvalidDataException(
                    "SchemaVersion 3 is retained only for an exact admitted final-output oracle.");
            }
            else if (schemaVersion == 6 && finalOutputAuthority is not null)
            {
                throw new InvalidDataException(
                    "SchemaVersion 6 uses only the exact selected CharGen bundle and forbids a final-output oracle.");
            }
            else if (schemaVersion == 7 && finalOutputAuthority is not null)
            {
                throw new InvalidDataException(
                    "SchemaVersion 7 uses exact selected CharGen plus external BodySlide mesh authority and forbids a final-output oracle.");
            }
            else if (schemaVersion == 8 && finalOutputAuthority is not null)
            {
                throw new InvalidDataException(
                    "SchemaVersion 8 uses only Manager-owned FaceGeom carrier evidence and forbids a final-output oracle.");
            }

            RaceMenuNpcFaceTextureBakeAuthorityReference? faceTextureBakeAuthority = null;
            if (schemaVersion == 5)
            {
                var faceTextureElement = root.GetProperty("faceTextureBakeAuthority");
                if (faceTextureElement.ValueKind == JsonValueKind.Null)
                    throw new InvalidDataException(
                        "SchemaVersion 5 requires an exact faceTextureBakeAuthority reference.");
                RequireShape(faceTextureElement, "face-texture-bake authority reference",
                    "manifestPath", "manifestSha256");
                faceTextureBakeAuthority = new RaceMenuNpcFaceTextureBakeAuthorityReference(
                    ResolveRelative(RequiredString(faceTextureElement, "manifestPath")),
                    new Sha256Hash(RequiredString(faceTextureElement, "manifestSha256")));
                var generatedDestination = ToPackageTexturePath(privateTextures.Diffuse.Value);
                if (packageAssets.Any(item => string.Equals(item.Destination.Value,
                        generatedDestination, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException(
                        "SchemaVersion 5 private diffuse is product-owned and may not be supplied in packageAssets.");
            }

            RaceMenuNpcBodySlidePresetAuthority? bodySlidePresetAuthority = null;
            RaceMenuNpcBodyMeshAuthority? bodyMeshAuthority = null;
            RaceMenuNpcExternalCharGenExportAuthority?
                externalCharGenExportAuthority = null;
            if (schemaVersion is 7 or 8)
            {
                var bodySlideElement =
                    root.GetProperty("bodySlidePresetAuthority");
                var bodyMeshElement = root.GetProperty("bodyMeshAuthority");
                if (bodySlideElement.ValueKind == JsonValueKind.Null ||
                    bodyMeshElement.ValueKind == JsonValueKind.Null)
                {
                    if (schemaVersion == 7 ||
                        bodySlideElement.ValueKind != JsonValueKind.Null ||
                        bodyMeshElement.ValueKind != JsonValueKind.Null)
                    {
                        diagnostics.Add(Error(
                            schemaVersion == 7
                                ? "racemenu-assets-schema7-body-mesh-authority-required"
                                : "racemenu-assets-schema8-body-authority-pair",
                            schemaVersion == 7
                                ? "SchemaVersion 7 requires bodySlidePresetAuthority and bodyMeshAuthority with body0/body1/hands0/hands1/feet0/feet1 mesh rows."
                                : "SchemaVersion 8 requires bodySlidePresetAuthority and bodyMeshAuthority together when either BodySlide authority is present."));
                        return null;
                    }
                }
                if (bodySlideElement.ValueKind != JsonValueKind.Null &&
                    bodyMeshElement.ValueKind != JsonValueKind.Null)
                {
                    RequireShape(bodySlideElement,
                        "BodySlide preset authority reference",
                        "manifestPath", "manifestSha256");
                    var bodySlideReference =
                        new RaceMenuNpcBodySlidePresetAuthorityReference(
                            ResolveRelative(RequiredString(
                                bodySlideElement, "manifestPath")),
                            new Sha256Hash(RequiredString(
                                bodySlideElement, "manifestSha256")));
                    bodySlidePresetAuthority =
                        await ReadBodySlidePresetAuthorityAsync(
                            bodySlideReference,
                            diagnostics,
                            cancellationToken);
                    if (bodySlidePresetAuthority is null) return null;

                    RequireShape(bodyMeshElement,
                        "BodySlide mesh authority reference",
                        "manifestPath", "manifestSha256");
                    var bodyMeshReference =
                        new RaceMenuNpcBodyMeshAuthorityReference(
                            ResolveRelative(RequiredString(
                                bodyMeshElement, "manifestPath")),
                            new Sha256Hash(RequiredString(
                                bodyMeshElement, "manifestSha256")));
                    bodyMeshAuthority =
                        await ReadBodyMeshAuthorityAsync(
                            bodySlideReference,
                            bodySlidePresetAuthority,
                            bodyMeshReference,
                            diagnostics,
                            cancellationToken);
                    if (bodyMeshAuthority is null) return null;
                }

                if (schemaVersion == 7 && root.TryGetProperty(
                        "externalCharGenExportAuthority",
                        out JsonElement externalCharGenElement) &&
                    externalCharGenElement.ValueKind != JsonValueKind.Null)
                {
                    RequireShape(
                        externalCharGenElement,
                        "external CharGen export authority reference",
                        "manifestPath", "manifestSha256");
                    externalCharGenExportAuthority =
                        await ReadExternalCharGenExportAuthorityAsync(
                            ResolveRelative(RequiredString(
                                externalCharGenElement, "manifestPath")),
                            new Sha256Hash(RequiredString(
                                externalCharGenElement, "manifestSha256")),
                            diagnostics,
                            cancellationToken);
                    if (externalCharGenExportAuthority is null) return null;
                }
            }

            var result = new RaceMenuNpcStandaloneAssets(assetSetId, nam9Authority,
                privateTextures, width, height, packageAssets.ToImmutable(), overlayDecisions,
                externalAuthorities.ToImmutable(), finalOutputAuthority)
            {
                SchemaVersion = schemaVersion,
                FaceBakeAuthority = faceBakeAuthority,
                FaceTextureBakeAuthority = faceTextureBakeAuthority,
                BodySlidePresetAuthority = bodySlidePresetAuthority,
                BodyMeshAuthority = bodyMeshAuthority,
                ExternalCharGenExportAuthority =
                    externalCharGenExportAuthority,
                ExternalHeadPartDependencies =
                    externalHeadPartDependencies.ToImmutable(),
                ExternalHeadPartExclusionAttestations =
                    externalHeadPartExclusionAttestations.ToImmutable(),
                NativeFaceGeomExternalHeadParts =
                    nativeExternalRows.ToImmutable()
            };
            if (!await VerifyStandaloneSourcesAsync(result, diagnostics, cancellationToken))
                return null;
            if (!await VerifyExternalTextureAuthoritiesAsync(
                    result.ExternalTextureAuthorities, diagnostics, cancellationToken))
                return null;
            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or InvalidDataException or ArgumentException or
                                           FormatException or OverflowException)
        {
            diagnostics.Add(Error("racemenu-assets-manifest-invalid", exception.Message));
            return null;
        }
    }

    private async ValueTask<bool> VerifyStandaloneSourcesAsync(
        RaceMenuNpcStandaloneAssets assets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var rows = assets.PackageAssets
            .Select(item => (item.Source, item.ExpectedSha256, item.Destination.Value))
            .Append((assets.Nam9Authority.Plugin,
                assets.Nam9Authority.ExpectedPluginSha256, "NAM9 authority"))
            .Concat(assets.ExternalTextureAuthorities.Select(item =>
                (item.Source, item.ExpectedSourceSha256,
                    $"external provider for {item.DataRelativePath.Value}")))
            .Concat(assets.FaceBakeAuthority is { } faceBake
                ? new[]
                {
                    (faceBake.ManifestPath, faceBake.ExpectedManifestSha256,
                        "face-bake authority manifest")
                }
                : [])
            .Concat(assets.FaceTextureBakeAuthority is { } faceTextureBake
                ? new[]
                {
                    (faceTextureBake.ManifestPath,
                        faceTextureBake.ExpectedManifestSha256,
                        "face-texture-bake authority manifest")
                }
                : [])
            .Concat(assets.BodySlidePresetAuthority is { } bodySlide
                ? new[]
                {
                    (bodySlide.ManifestPath, bodySlide.ManifestSha256,
                        "BodySlide preset authority manifest"),
                    (bodySlide.PresetXml, bodySlide.PresetXmlSha256,
                        "BodySlide SliderPreset XML")
                }
                : [])
            .Concat(assets.BodyMeshAuthority is { } bodyMeshes
                ? new[]
                    {
                        (bodyMeshes.ManifestPath, bodyMeshes.ManifestSha256,
                            "BodySlide body-mesh authority manifest")
                    }.Concat(bodyMeshes.Meshes.Select(item =>
                        (item.Source, item.ExpectedSha256,
                            $"BodySlide mesh {item.Role.ToWireName()}")))
                : [])
            .Concat(assets.FinalOutputAuthority is { } final
                ? new[]
                    {
                        (final.FaceGeom, final.FaceGeomSha256, "final FaceGeom oracle"),
                        (final.FaceTint, final.FaceTintSha256, "final FaceTint oracle")
                    }.Concat(final.Evidence.Select((item, index) =>
                        (item.Path, item.ExpectedSha256,
                            $"final-output evidence {index}")))
                : []);
        foreach (var (source, expected, role) in rows)
        {
            diagnostics.AddRange(WorkspacePolicy.EvaluateReadRoot(
                LaboratoryRoot,
                source));
            if (HasErrors(diagnostics)) return false;
            var info = new FileInfo(source.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > int.MaxValue ||
                File.GetAttributes(source.Value).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("racemenu-assets-source-file",
                    $"Asset source '{role}' is not an ordinary non-empty K-local file."));
                return false;
            }
            await using var stream = new FileStream(source.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)));
            if (actual != expected)
            {
                diagnostics.Add(Error("racemenu-assets-source-hash",
                    $"Asset source '{role}' hash {actual} does not match {expected}."));
                return false;
            }
        }
        return true;
    }

    protected static ImmutableArray<BlankNpcTransitivePackageAsset> BuildEvidenceAssets(
        RaceMenuNpcExecutionRequest request,
        RaceMenuNpcAppearancePlan plan,
        RaceMenuNpcStandaloneAssets assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = assets.PackageAssets.ToBuilder();
        AddEvidence(result, request.Build.PresetBundle.ManifestPath,
            request.Build.PresetBundle.ExpectedManifestSha256,
            "NPCManager/Evidence/racemenu-bundle.json");
        AddEvidence(result, request.Build.PresetBundle.PresetPath,
            request.Build.PresetBundle.ExpectedPresetSha256,
            "NPCManager/Evidence/source-preset.jslot");
        AddEvidence(result, request.Build.PresetBundle.RecordAuthority.ManifestPath,
            request.Build.PresetBundle.RecordAuthority.ExpectedManifestSha256,
            "NPCManager/Evidence/record-authority.json");
        if (request.Build.WholeSkinAuthority is { } wholeSkin)
            AddEvidence(result, wholeSkin.ManifestPath,
                wholeSkin.ExpectedManifestSha256,
                "NPCManager/Evidence/whole-skin-authority.json");
        if (request.Build.PresetBundle.RuntimeRoutes is { } runtime)
            AddEvidence(result, runtime.ManifestPath, runtime.ExpectedManifestSha256,
                "NPCManager/Evidence/runtime-routes.json");
        AddEvidence(result, request.AssetAuthority.ManifestPath,
            request.AssetAuthority.ExpectedManifestSha256,
            "NPCManager/Evidence/standalone-assets.json");
        if (assets.OverlayDecisions is { } decisions)
            AddEvidence(result, decisions.ManifestPath, decisions.ManifestSha256,
                "NPCManager/Evidence/overlay-decisions.json");
        if (assets.FinalOutputAuthority is { } final)
        {
            AddEvidence(result, final.ManifestPath, final.ManifestSha256,
                "NPCManager/Evidence/final-output-authority.json");
            for (var index = 0; index < final.Evidence.Length; index++)
                AddEvidence(result, final.Evidence[index].Path,
                    final.Evidence[index].ExpectedSha256,
                    $"NPCManager/Evidence/final-output-evidence-{index + 1}.json");
        }
        if (assets.FaceBakeAuthority is { } faceBake)
            AddEvidence(result, faceBake.ManifestPath,
                faceBake.ExpectedManifestSha256,
                "NPCManager/Evidence/FaceGeom/face-bake-authority.json");
        if (assets.FaceTextureBakeAuthority is { } faceTextureBake)
            AddEvidence(result, faceTextureBake.ManifestPath,
                faceTextureBake.ExpectedManifestSha256,
                "NPCManager/Evidence/FaceTextures/face-texture-authority.json");
        if (assets.BodySlidePresetAuthority is { } bodySlide)
        {
            AddEvidence(result, bodySlide.ManifestPath,
                bodySlide.ManifestSha256,
                "NPCManager/Evidence/BodySlide/body-slide-preset-authority.json");
            AddEvidence(result, bodySlide.PresetXml,
                bodySlide.PresetXmlSha256,
                "NPCManager/Evidence/BodySlide/source-sliderpreset.xml");
        }
        if (assets.BodyMeshAuthority is { } bodyMesh)
        {
            AddEvidence(result, bodyMesh.ManifestPath,
                bodyMesh.ManifestSha256,
                "NPCManager/Evidence/BodySlide/body-mesh-authority.json");
            result.AddRange(bodyMesh.Meshes.Select(item =>
                new BlankNpcTransitivePackageAsset(
                    item.Source,
                    item.ExpectedSha256,
                    item.Destination)));
        }
        if (request.SelectedDependencyManifest is { } selectedDependencies)
            AddEvidence(result, selectedDependencies.ManifestPath,
                selectedDependencies.ExpectedManifestSha256,
                "NPCManager/Evidence/selected-preset-dependencies.json",
                assets.SchemaVersion == 8
                    ? BlankNpcTransitivePackageAssetKinds.ExternalHeadPartDependencies
                    : null);
        if (assets.SchemaVersion == 8 &&
            !RaceMenuJslotProbeToken.IsValid(request.JslotOutputBindingProbe) &&
            result.Count(item => string.Equals(
                item.Kind,
                BlankNpcTransitivePackageAssetKinds.ExternalHeadPartDependencies,
                StringComparison.Ordinal)) != 1)
        {
            diagnostics.Add(Error(
                "racemenu-build-schema8-selected-dependency-manifest",
                "SchemaVersion 8 requires exactly one external-headpart-dependencies package row."));
        }
        if (plan.BundleManifestSha256 != request.Build.PresetBundle.ExpectedManifestSha256 ||
            plan.RecordAuthoritySha256 !=
            request.Build.PresetBundle.RecordAuthority.ExpectedManifestSha256)
            diagnostics.Add(Error("racemenu-build-plan-authority-drift",
                "The admitted plan no longer matches the package evidence authorities."));
        return result.ToImmutable();
    }

    private static void AddEvidence(
        ImmutableArray<BlankNpcTransitivePackageAsset>.Builder rows,
        WorkspacePath source,
        Sha256Hash hash,
        string destination,
        string? kind = null) =>
        rows.Add(new BlankNpcTransitivePackageAsset(
            source, hash, new AssetPath(destination), kind));

    protected static bool ValidateStandaloneAssetCoverage(
        RaceMenuNpcStandaloneAssets assets,
        SkyrimNpcRuntimeAppearancePayload runtime,
        IEnumerable<BlankNpcTransitivePackageAsset> packageAssets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var destinations = packageAssets.Select(item => item.Destination.Value)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var requiredHeadTextures = new[]
        {
            assets.PrivateHeadTextures.NormalOrGloss,
            assets.PrivateHeadTextures.GlowOrDetailMap,
            assets.PrivateHeadTextures.BacklightMaskOrSpecular
        }.AsEnumerable();
        if (assets.SchemaVersion != 5)
            requiredHeadTextures = requiredHeadTextures.Prepend(
                assets.PrivateHeadTextures.Diffuse);
        if (assets.SchemaVersion is 6 or 7 or 8)
        {
            requiredHeadTextures = requiredHeadTextures
                .Append(assets.PrivateHeadTextures.Height)
                .Concat(new[]
                {
                    assets.PrivateHeadTextures.EnvironmentMaskOrSubsurfaceTint,
                    assets.PrivateHeadTextures.Environment,
                    assets.PrivateHeadTextures.Multilayer
                }.Where(item => item is not null).Select(item => item!.Value));
        }
        var required = requiredHeadTextures.Select(item => item.Value)
         .Concat(runtime.Overlays.SelectMany(item => new[] { item.Diffuse, item.Normal }))
         .Concat(runtime.SkinOverrides.SelectMany(item => new[] { item.Diffuse, item.Normal }))
         .Where(item => !string.IsNullOrWhiteSpace(item))
         .Select(ToPackageTexturePath)
         .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var external = assets.ExternalTextureAuthorities
            .Select(item => item.DataRelativePath.Value)
            .ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var normalized in required)
        {
            if (!destinations.Contains(normalized) && !external.Contains(normalized))
                diagnostics.Add(Error("racemenu-assets-required-file-missing",
                    $"Neither standalone package assets nor a verified external provider supply required texture '{normalized}'."));
        }
        foreach (var declared in external.Where(item => !required.Contains(item)))
            diagnostics.Add(Error("racemenu-assets-external-authority-unused",
                $"External texture authority '{declared}' is not required by the mapped runtime appearance."));
        if (assets.BodyMeshAuthority is { } bodyMesh)
        {
            foreach (var mesh in bodyMesh.Meshes)
            {
                if (!destinations.Contains(mesh.Destination.Value))
                    diagnostics.Add(Error("racemenu-assets-body-mesh-missing",
                        $"The external BodySlide mesh '{mesh.Role.ToWireName()}' is not present in the transitive package inventory."));
            }
        }
        return !HasErrors(diagnostics);
    }

    protected static string ToPackageTexturePath(string gameTexturePath)
    {
        var normalized = new AssetPath(gameTexturePath).Value;
        return normalized.StartsWith("Textures/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : new AssetPath($"Textures/{normalized}").Value;
    }

    protected static bool DestinationsAreUnique(
        IEnumerable<BlankNpcTransitivePackageAsset> assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in assets)
        {
            if (!seen.Add(asset.Destination.Value))
                diagnostics.Add(Error("racemenu-assets-destination-duplicate",
                    $"Package destination '{asset.Destination.Value}' occurs more than once."));
        }
        return !HasErrors(diagnostics);
    }

    protected static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    protected static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    protected WorkspacePath? CreateStagingRoot(
        WorkspacePath outputRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var parent = Path.GetDirectoryName(outputRoot.Value);
        if (parent is null || !Directory.Exists(parent))
        {
            diagnostics.Add(Error("racemenu-build-output-parent",
                "The final output parent must exist before staging product-owned runtime assets."));
            return null;
        }
        var stage = new WorkspacePath(Path.Combine(parent,
            ".racemenu-stage-" + Guid.NewGuid().ToString("N")));
        diagnostics.AddRange(WorkspacePolicy.Evaluate(LaboratoryRoot, stage));
        if (HasErrors(diagnostics)) return null;
        Directory.CreateDirectory(stage.Value);
        if (File.GetAttributes(stage.Value).HasFlag(FileAttributes.ReparsePoint))
        {
            diagnostics.Add(Error("racemenu-build-staging-reparse",
                "The newly created BodyGen staging root became a reparse point."));
            return null;
        }
        return stage;
    }

    protected void CleanupOwnedStagingRoot(
        WorkspacePath stage,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            if (!Directory.Exists(stage.Value)) return;
            if (!stage.IsUnder(LaboratoryRoot) ||
                File.GetAttributes(stage.Value).HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("racemenu-build-staging-cleanup-reparse",
                    $"Owned staging root '{stage.Value}' is outside the lab or is a reparse point and was left for review."));
                return;
            }
            var pending = new Stack<string>();
            pending.Push(stage.Value);
            while (pending.Count > 0)
            {
                var directory = pending.Pop();
                foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos())
                {
                    if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        diagnostics.Add(Error("racemenu-build-staging-cleanup-reparse",
                            $"Owned staging root '{stage.Value}' contains a reparse point and was left for review."));
                        return;
                    }
                    if (entry is DirectoryInfo child) pending.Push(child.FullName);
                }
            }
            Directory.Delete(stage.Value, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(Error("racemenu-build-staging-cleanup", exception.Message));
        }
    }

    protected WorkspacePath ResolveRelative(string value)
    {
        var relative = new AssetPath(value);
        var path = new WorkspacePath(Path.Combine(LaboratoryRoot.Value,
            relative.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!path.IsUnder(LaboratoryRoot))
            throw new InvalidDataException("Standalone manifest path escaped the workspace root.");
        return path;
    }

    protected static void RequireShape(JsonElement element, string role, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{role} must be an object.");
        var actual = element.EnumerateObject().Select(item => item.Name).ToArray();
        if (actual.Length != fields.Length || fields.Any(field =>
                !actual.Contains(field, StringComparer.Ordinal)))
            throw new InvalidDataException($"{role} has missing or unknown fields.");
    }

    private static void RequireShapeWithOptional(
        JsonElement element,
        string role,
        IReadOnlyCollection<string> required,
        params string[] optional)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{role} must be an object.");
        var actual = element.EnumerateObject()
            .Select(item => item.Name)
            .ToArray();
        if (required.Any(field => !actual.Contains(
                field, StringComparer.Ordinal)) ||
            actual.Any(field =>
                !required.Contains(field, StringComparer.Ordinal) &&
                !optional.Contains(field, StringComparer.Ordinal)))
            throw new InvalidDataException($"{role} has missing or unknown fields.");
    }

    protected static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate JSON property '{property.Name}' is not accepted.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private static void RejectReservedPackageAssetSubstitution(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("packageAssets", out var rows) ||
            rows.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var row in rows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object ||
                !row.TryGetProperty("destination", out var destination) ||
                destination.ValueKind != JsonValueKind.String ||
                destination.GetString() is not { Length: > 0 } value)
            {
                continue;
            }

            if (SkyrimApplySseProductAsset.IsReservedDestination(NormalizePackageDestination(value)))
                throw new InvalidDataException(
                    $"Package destination '{value}' is product-owned and callers may not supply or replace it.");
        }
    }

    private static AssetPath NormalizePackageDestination(string value)
    {
        string normalized = value.Trim().Replace('\\', '/').ToLowerInvariant();
        if (normalized.StartsWith('/')) normalized = normalized[1..];
        try { return new AssetPath(normalized); }
        catch (ArgumentException exception)
        {
            throw new InvalidDataException($"Package destination '{value}' must be a relative forward-slash asset path after normalizing one leading separator.", exception);
        }
    }

    protected static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } result || result.Contains('\0'))
            throw new InvalidDataException($"'{name}' must be a non-empty string.");
        return result;
    }

    protected static int RequiredInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"'{name}' must be an Int32.");
        return result;
    }

    private static float RequiredSingle(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetSingle(out var result) ||
            !float.IsFinite(result))
            throw new InvalidDataException($"'{name}' must be a finite Single.");
        return result;
    }

    private static AssetPath RequiredAssetPath(JsonElement element, string name) =>
        new(RequiredString(element, name));

    private static AssetPath? NullableAssetPath(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value))
            throw new InvalidDataException($"'{name}' is required and may be null.");
        return value.ValueKind switch
        {
            JsonValueKind.Null => null,
            JsonValueKind.String when value.GetString() is { Length: > 0 } text => new AssetPath(text),
            _ => throw new InvalidDataException($"'{name}' must be null or a non-empty asset path.")
        };
    }
}
