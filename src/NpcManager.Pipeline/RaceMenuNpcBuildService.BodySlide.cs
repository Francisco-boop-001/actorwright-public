using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public partial class RaceMenuNpcStandaloneAuthorityReader
{
    private const int MaximumBodySlideAuthorityBytes = 1 * 1024 * 1024;
    private const int MaximumBodySlideXmlBytes = 4 * 1024 * 1024;
    private const long MaximumBodySlideMeshBytes = 256L * 1024 * 1024;

    private async ValueTask<RaceMenuNpcBodySlidePresetAuthority?>
        ReadBodySlidePresetAuthorityAsync(
            RaceMenuNpcBodySlidePresetAuthorityReference reference,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadHashBoundOrdinaryFileAsync(
            reference.ManifestPath,
            reference.ExpectedManifestSha256,
            MaximumBodySlideAuthorityBytes,
            "racemenu-bodyslide-preset-authority",
            "BodySlide preset authority manifest",
            diagnostics,
            cancellationToken);
        if (bytes is null) return null;

        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
        RejectDuplicateKeys(document.RootElement);
        JsonElement root = document.RootElement;
        RequireShape(root, "BodySlide preset authority",
            "schemaVersion", "authorityId", "edition", "sourceKind",
            "presetXmlPath", "presetXmlSha256", "presetName", "sliderSet",
            "groups", "sliderCount", "runtimeAuthority");
        if (RequiredInt(root, "schemaVersion") != 1 ||
            !string.Equals(RequiredString(root, "edition"), "skyrimse",
                StringComparison.Ordinal) ||
            !string.Equals(RequiredString(root, "sourceKind"),
                "bodyslide-sliderpreset-xml",
                StringComparison.Ordinal) ||
            RequiredBodySlideBoolean(root, "runtimeAuthority"))
        {
            throw new InvalidDataException(
                "BodySlide preset authority requires schemaVersion 1, Skyrim SE, sourceKind 'bodyslide-sliderpreset-xml', and runtimeAuthority false.");
        }

        string authorityId = RequiredString(root, "authorityId");
        string presetName = RequiredString(root, "presetName");
        string sliderSet = RequiredString(root, "sliderSet");
        if (authorityId.Length > 128 ||
            string.IsNullOrWhiteSpace(presetName) ||
            string.IsNullOrWhiteSpace(sliderSet) ||
            presetName.Length > 256 ||
            sliderSet.Length > 256)
        {
            throw new InvalidDataException(
                "BodySlide preset authority has an invalid authorityId, presetName, or sliderSet.");
        }

        JsonElement groupRows = root.GetProperty("groups");
        if (groupRows.ValueKind != JsonValueKind.Array ||
            groupRows.GetArrayLength() is <= 0 or > 64)
            throw new InvalidDataException(
                "BodySlide preset authority requires 1-64 groups.");
        var groups = ImmutableArray.CreateBuilder<string>();
        var seenGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (JsonElement row in groupRows.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.String ||
                row.GetString() is not { Length: > 0 } group ||
                string.IsNullOrWhiteSpace(group) ||
                group.Length > 128 ||
                !seenGroups.Add(group))
                throw new InvalidDataException(
                    "BodySlide preset authority groups must be unique non-empty strings.");
            groups.Add(group);
        }

        int sliderCount = RequiredInt(root, "sliderCount");
        if (sliderCount <= 0)
            throw new InvalidDataException(
                "BodySlide preset authority requires a positive sliderCount.");

        var presetXml = ResolveRelative(RequiredString(root, "presetXmlPath"));
        var presetXmlSha256 = new Sha256Hash(
            RequiredString(root, "presetXmlSha256"));
        if (!presetXml.Value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "BodySlide preset authority presetXmlPath must end in .xml.");
        if (await ReadHashBoundOrdinaryFileAsync(
                presetXml,
                presetXmlSha256,
                MaximumBodySlideXmlBytes,
                "racemenu-bodyslide-preset-xml",
                "BodySlide SliderPreset XML",
                diagnostics,
                cancellationToken) is null)
            return null;

        return new RaceMenuNpcBodySlidePresetAuthority(
            reference.ManifestPath,
            reference.ExpectedManifestSha256,
            authorityId,
            presetXml,
            presetXmlSha256,
            presetName,
            sliderSet,
            groups.ToImmutable(),
            sliderCount,
            RuntimeAuthority: false);
    }

    private async ValueTask<RaceMenuNpcBodyMeshAuthority?>
        ReadBodyMeshAuthorityAsync(
            RaceMenuNpcBodySlidePresetAuthorityReference presetReference,
            RaceMenuNpcBodySlidePresetAuthority parsedPreset,
            RaceMenuNpcBodyMeshAuthorityReference meshReference,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadHashBoundOrdinaryFileAsync(
            meshReference.ManifestPath,
            meshReference.ExpectedManifestSha256,
            MaximumBodySlideAuthorityBytes,
            "racemenu-body-mesh-authority",
            "BodySlide body-mesh authority manifest",
            diagnostics,
            cancellationToken);
        if (bytes is null) return null;

        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
        RejectDuplicateKeys(document.RootElement);
        JsonElement root = document.RootElement;
        RequireShape(root, "BodySlide body-mesh authority",
            "schemaVersion", "authorityId", "edition", "sourceKind",
            "bodySlidePresetAuthority", "meshes", "runtimeAuthority");
        if (RequiredInt(root, "schemaVersion") != 1 ||
            !string.Equals(RequiredString(root, "edition"), "skyrimse",
                StringComparison.Ordinal) ||
            !string.Equals(RequiredString(root, "sourceKind"),
                "external-bodyslide-generated-meshes",
                StringComparison.Ordinal) ||
            RequiredBodySlideBoolean(root, "runtimeAuthority"))
        {
            throw new InvalidDataException(
                "BodySlide mesh authority requires schemaVersion 1, Skyrim SE, sourceKind 'external-bodyslide-generated-meshes', and runtimeAuthority false.");
        }

        JsonElement boundPreset = root.GetProperty("bodySlidePresetAuthority");
        RequireShape(boundPreset, "BodySlide body-mesh preset reference",
            "manifestPath", "manifestSha256");
        var declaredPresetReference =
            new RaceMenuNpcBodySlidePresetAuthorityReference(
                ResolveRelative(RequiredString(boundPreset, "manifestPath")),
                new Sha256Hash(RequiredString(boundPreset, "manifestSha256")));
        if (declaredPresetReference != presetReference ||
            declaredPresetReference.ManifestPath != parsedPreset.ManifestPath ||
            declaredPresetReference.ExpectedManifestSha256 !=
            parsedPreset.ManifestSha256)
        {
            throw new InvalidDataException(
                "BodySlide mesh authority is not bound to the exact parsed BodySlide preset authority.");
        }

        JsonElement meshRows = root.GetProperty("meshes");
        if (meshRows.ValueKind != JsonValueKind.Array ||
            meshRows.GetArrayLength() != 6)
            throw new InvalidDataException(
                "BodySlide mesh authority requires exactly six mesh rows: body0, body1, hands0, hands1, feet0, and feet1.");
        var meshes = ImmutableArray.CreateBuilder<RaceMenuNpcBodyMeshAsset>(6);
        var roles = new HashSet<RaceMenuNpcBodyMeshRole>();
        var sourcePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in meshRows.EnumerateArray())
        {
            RequireShape(row, "BodySlide body mesh",
                "role", "sourcePath", "sha256", "destination");
            if (!RaceMenuNpcBodyMeshRoleExtensions.TryParseWireName(
                    RequiredString(row, "role"), out var role) ||
                !roles.Add(role))
                throw new InvalidDataException(
                    "BodySlide mesh roles must be the unique closed set body0/body1/hands0/hands1/feet0/feet1.");
            var source = ResolveRelative(RequiredString(row, "sourcePath"));
            if (!source.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
                !sourcePaths.Add(source.Value))
                throw new InvalidDataException(
                    "BodySlide mesh sources must be unique K-local .nif paths.");
            var destination = CanonicalDataMeshPath(
                RequiredString(row, "destination"),
                "BodySlide mesh authority");
            if (!destinations.Add(destination.Value))
                throw new InvalidDataException(
                    "BodySlide mesh destination paths must be unique.");
            meshes.Add(new RaceMenuNpcBodyMeshAsset(
                role,
                source,
                new Sha256Hash(RequiredString(row, "sha256")),
                destination));
        }

        if (!roles.SetEquals(Enum.GetValues<RaceMenuNpcBodyMeshRole>()))
            throw new InvalidDataException(
                "BodySlide mesh authority is missing one or more required mesh roles.");

        return new RaceMenuNpcBodyMeshAuthority(
            meshReference.ManifestPath,
            meshReference.ExpectedManifestSha256,
            RequiredString(root, "authorityId"),
            declaredPresetReference,
            meshes.ToImmutable(),
            RuntimeAuthority: false);
    }

    private static AssetPath CanonicalDataMeshPath(string value, string role)
    {
        var path = new AssetPath(value);
        if (!path.Value.StartsWith("Meshes/", StringComparison.OrdinalIgnoreCase) ||
            !path.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"{role} mesh paths must be Data-relative Meshes/*.nif paths.");
        return path;
    }

    private static bool RequiredBodySlideBoolean(JsonElement element, string name)
    {
        JsonElement value = element.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"'{name}' must be Boolean.")
        };
    }
}
