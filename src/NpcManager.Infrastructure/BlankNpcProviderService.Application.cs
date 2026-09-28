using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class BlankNpcProviderService
{
    private static async ValueTask<BlankNpcProviderBindingResult>
        QualifyApplicationAsync(
            BlankNpcProviderBindingRequest request,
            CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            ProviderResourceAuthoritySet resources =
                request.ProviderResources ?? throw new InvalidDataException(
                    "The application provider authority is absent.");
            if (resources.Files.Any(item => item is not
                    ApplicationProviderResourceAuthority) ||
                resources.FaceTintProviderRoot is not
                    ApplicationProviderResourceAuthority
                    {
                        IsDirectory: true
                    })
                throw new InvalidDataException(
                    "The application provider authority contains a workspace resource or invalid root.");
            foreach (ProviderResourceAuthority resource in resources.Files)
                await VerifyApplicationResourceAsync(resource,
                    cancellationToken).ConfigureAwait(false);

            byte[] manifestBytes = await ReadApplicationAsync(
                resources.Manifest, cancellationToken).ConfigureAwait(false);
            Sha256Hash manifestHash = Hash(manifestBytes);
            if (manifestHash != request.ExpectedManifestSha256)
                throw new InvalidDataException(
                    "The product provider manifest hash does not match the request.");
            using JsonDocument document = JsonDocument.Parse(manifestBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            JsonElement root = document.RootElement;
            RequireShape(root, "provider manifest", "schemaVersion",
                "providerId", "edition", "sex", "template", "faceGeom",
                "faceTint", "dependencies");
            if (RequiredInt(root, "schemaVersion") != 1 ||
                !string.Equals(RequiredString(root, "edition"), "skyrimse",
                    StringComparison.Ordinal) ||
                !string.Equals(RequiredString(root, "sex"), "female",
                    StringComparison.Ordinal) ||
                request.Edition != GameEdition.SkyrimSpecialEdition ||
                request.Sex != NpcSex.Female ||
                resources.Edition != request.Edition ||
                resources.Sex != request.Sex)
                throw new InvalidDataException(
                    "The product provider edition or sex does not match the request.");
            string providerId = RequiredString(root, "providerId");

            JsonElement template = root.GetProperty("template");
            RequireShape(template, "provider template", "path", "sha256",
                "npcFormId", "masters");
            BindApplicationPath(template, "path", resources.TemplatePlugin);
            BindApplicationHash(template, "sha256", resources.TemplatePlugin);
            if (!FormId.TryParse(RequiredString(template, "npcFormId"),
                    out FormId formId) ||
                formId != request.TemplateNpcFormId ||
                formId != resources.TemplateNpcFormId)
                throw new InvalidDataException(
                    "The product provider template FormID does not match the request.");
            ImmutableArray<PluginName> masters = ReadUniqueStrings(
                    template.GetProperty("masters"), "provider masters")
                .Select(value => new PluginName(value)).ToImmutableArray();
            ApplicationResourcePath templatePath = ApplicationPath(
                resources.TemplatePlugin);
            BethesdaNpcCreationTemplateSnapshot templateSnapshot =
                BethesdaNpcCreationAdapter.ReadTemplate(templatePath, formId);
            if (!templateSnapshot.Masters.SequenceEqual(masters))
                throw new InvalidDataException(
                    "The product template master list drifted.");

            JsonElement faceGeom = root.GetProperty("faceGeom");
            RequireShape(faceGeom, "provider FaceGeom", "path", "sha256",
                "graphSha256", "shapeNames");
            BindApplicationPath(faceGeom, "path", resources.FaceGeomCarrier);
            BindApplicationHash(faceGeom, "sha256",
                resources.FaceGeomCarrier);
            byte[] faceGeomBytes = await ReadApplicationAsync(
                resources.FaceGeomCarrier, cancellationToken)
                .ConfigureAwait(false);
            SseNifDocument nif = SseFaceGeomCarrierCodec.Parse(faceGeomBytes);
            QualifiedFaceGeomCarrierStructure structure =
                SseFaceGeomCarrierCodec.BuildStructure(nif);
            SseFaceGeomCarrierCodec.Qualify(nif, structure,
                QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete,
                diagnostics);
            ImmutableArray<string> shapeNames = ReadUniqueStrings(
                faceGeom.GetProperty("shapeNames"),
                "FaceGeom shape names");
            if (structure.GraphSha256 != new Sha256Hash(
                    RequiredString(faceGeom, "graphSha256")) ||
                !structure.ReachableShapeNames.SequenceEqual(shapeNames,
                    StringComparer.Ordinal))
                throw new InvalidDataException(
                    "The product provider FaceGeom structure drifted.");

            JsonElement faceTint = root.GetProperty("faceTint");
            RequireShape(faceTint, "provider FaceTint", "manifestPath",
                "manifestSha256", "providerRoot", "sourceAssetPath",
                "sourceAssetSha256");
            BindApplicationPath(faceTint, "manifestPath",
                resources.FaceTintManifest);
            BindApplicationHash(faceTint, "manifestSha256",
                resources.FaceTintManifest);
            BindApplicationPath(faceTint, "providerRoot",
                resources.FaceTintProviderRoot);
            var sourceAsset = new AssetPath(RequiredString(faceTint,
                "sourceAssetPath"));
            if (resources.FaceTintSource.ExpectedSha256 != new Sha256Hash(
                    RequiredString(faceTint, "sourceAssetSha256")))
                throw new InvalidDataException(
                    "The product provider FaceTint source hash drifted.");
            byte[] faceTintManifestBytes = await ReadApplicationAsync(
                resources.FaceTintManifest, cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument tintDocument = JsonDocument.Parse(
                faceTintManifestBytes);
            JsonElement layers = tintDocument.RootElement.GetProperty("layers");
            if (layers.GetArrayLength() != 1 ||
                !string.Equals(layers[0].GetProperty("source").GetString(),
                    sourceAsset.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The product FaceTint manifest source drifted.");

            JsonElement dependencies = root.GetProperty("dependencies");
            RequireShape(dependencies, "provider dependencies",
                "manifestPath", "manifestSha256", "dependencyId",
                "headPartCount", "looseAssetCount", "archiveCount");
            BindApplicationPath(dependencies, "manifestPath",
                resources.DependencyManifest);
            BindApplicationHash(dependencies, "manifestSha256",
                resources.DependencyManifest);
            int headParts = RequiredNonNegativeInt(dependencies,
                "headPartCount");
            int looseAssets = RequiredNonNegativeInt(dependencies,
                "looseAssetCount");
            int archives = RequiredNonNegativeInt(dependencies,
                "archiveCount");
            string dependencyId = RequiredString(dependencies,
                "dependencyId");
            byte[] dependencyBytes = await ReadApplicationAsync(
                resources.DependencyManifest, cancellationToken)
                .ConfigureAwait(false);
            using JsonDocument dependencyDocument = JsonDocument.Parse(
                dependencyBytes);
            JsonElement dependencyRoot = dependencyDocument.RootElement;
            RequireShape(dependencyRoot, "dependency manifest",
                "schemaVersion", "id", "headParts", "looseAssets",
                "archives");
            if (RequiredInt(dependencyRoot, "schemaVersion") != 1 ||
                !string.Equals(RequiredString(dependencyRoot, "id"),
                    dependencyId, StringComparison.Ordinal) ||
                dependencyRoot.GetProperty("headParts").GetArrayLength() !=
                    headParts ||
                dependencyRoot.GetProperty("looseAssets").GetArrayLength() !=
                    looseAssets ||
                dependencyRoot.GetProperty("archives").GetArrayLength() !=
                    archives || headParts != 0 || looseAssets != 0 ||
                archives != 0)
                throw new InvalidDataException(
                    "The bounded product dependency manifest drifted.");
            if (HasErrors(diagnostics)) return Refused(diagnostics);

            return new BlankNpcProviderBindingResult(
                true,
                new BlankNpcProviderArtifact(
                    "1",
                    "blank-npc-provider-binding",
                    providerId,
                    default,
                    manifestHash,
                    request.Edition,
                    request.Sex,
                    masters,
                    structure.GraphSha256,
                    structure.ReachableShapeNames,
                    sourceAsset,
                    resources.FaceTintSource.ExpectedSha256,
                    resources.FaceTintManifest.ExpectedSha256,
                    dependencyId,
                    resources.DependencyManifest.ExpectedSha256,
                    headParts,
                    looseAssets,
                    archives)
                {
                    ProviderResources = resources
                },
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or JsonException or
            InvalidDataException or ArgumentException or OverflowException)
        {
            diagnostics.Add(Error("blank-provider-invalid",
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private static async ValueTask VerifyApplicationResourceAsync(
        ProviderResourceAuthority resource,
        CancellationToken cancellationToken)
    {
        if (resource.IsDirectory)
            throw new InvalidDataException(
                "Application file verification received a directory.");
        byte[] bytes = await ReadApplicationAsync(resource,
            cancellationToken).ConfigureAwait(false);
        if (Hash(bytes) != resource.ExpectedSha256)
            throw new InvalidDataException(
                $"Application provider role '{resource.Role}' drifted.");
    }

    private static async ValueTask<byte[]> ReadApplicationAsync(
        ProviderResourceAuthority resource,
        CancellationToken cancellationToken)
    {
        if (resource is not ApplicationProviderResourceAuthority application ||
            resource.IsDirectory)
            throw new InvalidDataException(
                $"Application provider role '{resource.Role}' is not one ordinary file.");
        var info = new FileInfo(resource.PhysicalPath);
        if (!info.Exists || info.Length <= 0 ||
            info.Length > 64L * 1024 * 1024 ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                $"Application provider role '{resource.Role}' is not one ordinary file.");
        if (ApplicationResourceRuntimeLocator.HasReparseAncestor(
                info.FullName, FindBundleRoot(application)))
            throw new InvalidDataException(
                $"Application provider role '{resource.Role}' crosses a reparse point.");
        return await File.ReadAllBytesAsync(info.FullName, cancellationToken)
            .ConfigureAwait(false);
    }

    private static ApplicationResourcePath ApplicationPath(
        ProviderResourceAuthority authority) =>
        authority is ApplicationProviderResourceAuthority application
            ? application.Path
            : throw new InvalidDataException(
                $"Provider role '{authority.Role}' is not application-owned.");

    private static void BindApplicationHash(
        JsonElement element,
        string property,
        ProviderResourceAuthority authority)
    {
        if (new Sha256Hash(RequiredString(element, property)) !=
            authority.ExpectedSha256)
            throw new InvalidDataException(
                $"Provider role '{authority.Role}' hash does not match its manifest.");
    }

    private static void BindApplicationPath(
        JsonElement element,
        string property,
        ProviderResourceAuthority authority)
    {
        string declared = new AssetPath(RequiredString(element,
            property)).Value;
        ApplicationResourcePath manifest = ApplicationPath(authority);
        string bundleRoot = authority is
            ApplicationProviderResourceAuthority application
            ? FindBundleRoot(application)
            : throw new InvalidDataException(
                "Application provider path binding received a workspace resource.");
        string actual = Path.GetRelativePath(bundleRoot, manifest.Value)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(declared, actual,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Provider role '{authority.Role}' path does not match its registry authority.");
    }

    private static string FindBundleRoot(
        ApplicationProviderResourceAuthority authority)
    {
        string marker = Path.Combine("runtime", "product-fixtures",
            authority.BundleId);
        int index = authority.Path.Value.LastIndexOf(marker,
            StringComparison.OrdinalIgnoreCase);
        if (index < 0)
            throw new InvalidDataException(
                "Application provider path has no authenticated bundle root.");
        return authority.Path.Value[..(index + marker.Length)];
    }
}
