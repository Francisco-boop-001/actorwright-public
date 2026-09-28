using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed partial class RaceMenuNpcFaceTextureBuildService
{
    private const int MaximumAuthorityBytes = 2 * 1024 * 1024;

    private async ValueTask<FaceTextureAuthority?> ReadAuthorityAsync(
        RaceMenuNpcFaceTextureBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var reference = request.Authority;
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, reference.ManifestPath));
        if (HasErrors(diagnostics)) return null;

        if (!WindowsPinnedPath.TryOpenFile(reference.ManifestPath.Value, false,
                out var pinned, out _, out var openError) || pinned is null)
        {
            diagnostics.Add(Error("face-texture-authority-open",
                $"The face-texture authority is not an ordinary identity-pinned file: {openError}"));
            return null;
        }

        using (pinned)
        {
            try
            {
                var bytes = await pinned.ReadAllBytesAsync(MaximumAuthorityBytes, cancellationToken);
                var manifestHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
                if (manifestHash != reference.ExpectedManifestSha256)
                    throw new InvalidDataException(
                        $"Face-texture authority hash {manifestHash} does not match {reference.ExpectedManifestSha256}.");

                using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 16
                });
                RejectDuplicateKeys(document.RootElement);
                var root = document.RootElement;
                RequireShape(root, "face-texture authority", "schemaVersion", "authorityId",
                    "edition", "presetSha256", "privateDiffuseDestination",
                    "fullComposite", "baseDiffuse", "protectedNeck", "tolerances",
                    "layers");
                if (RequiredInt(root, "schemaVersion") != 1)
                    throw new InvalidDataException("Face-texture authority requires schemaVersion 1.");
                if (!string.Equals(RequiredString(root, "edition"), "skyrimse",
                        StringComparison.Ordinal))
                    throw new InvalidDataException("Face-texture authority requires edition 'skyrimse'.");

                var authorityId = RequiredString(root, "authorityId");
                if (authorityId.Length > 128)
                    throw new InvalidDataException("Face-texture authorityId may not exceed 128 characters.");
                var presetHash = new Sha256Hash(RequiredString(root, "presetSha256"));
                if (presetHash != request.Plan.Request.PresetBundle.ExpectedPresetSha256)
                    throw new InvalidDataException(
                        "Face-texture authority does not bind the admitted source preset hash.");
                var privateDiffuseDestination = new AssetPath(
                    RequiredString(root, "privateDiffuseDestination"));
                if (!privateDiffuseDestination.Value.StartsWith("Textures/",
                        StringComparison.OrdinalIgnoreCase) ||
                    !privateDiffuseDestination.Value.EndsWith(".dds",
                        StringComparison.OrdinalIgnoreCase) ||
                    !string.Equals(privateDiffuseDestination.Value,
                        request.PrivateDiffuseDestination.Value,
                        StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(
                        "Face-texture authority does not bind the exact private diffuse package destination.");

                var fullComposite = ReadSource(root.GetProperty("fullComposite"),
                    "full composite", request.AllowedRoot);
                var baseDiffuse = ReadSource(root.GetProperty("baseDiffuse"),
                    "base diffuse", request.AllowedRoot);
                if (!PathEquals(fullComposite.Path,
                        request.Plan.Request.PresetBundle.CharGenFaceTint) ||
                    fullComposite.Sha256 !=
                    request.Plan.Request.PresetBundle.ExpectedCharGenFaceTintSha256 ||
                    fullComposite.Sha256 != request.Plan.CharGenFaceTintSha256)
                {
                    throw new InvalidDataException(
                        "Face-texture full composite must be the admitted raw CharGen FaceTint input.");
                }
                if (fullComposite.Width != request.Width || fullComposite.Height != request.Height ||
                    baseDiffuse.Width != request.Width || baseDiffuse.Height != request.Height)
                    throw new InvalidDataException(
                        "Face-texture input dimensions do not match the requested output dimensions.");

                var neckElement = root.GetProperty("protectedNeck");
                RequireShape(neckElement, "protected-neck authority", "startRowInclusive",
                    "endRowInclusive", "policy");
                if (!string.Equals(RequiredString(neckElement, "policy"),
                        "copy-base-rgba-exact", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        "Protected-neck policy must be 'copy-base-rgba-exact'.");
                var protectedNeck = new ProtectedNeckAuthority(
                    RequiredInt(neckElement, "startRowInclusive"),
                    RequiredInt(neckElement, "endRowInclusive"));
                if (protectedNeck.StartRowInclusive < 0 ||
                    protectedNeck.EndRowInclusive < protectedNeck.StartRowInclusive ||
                    protectedNeck.EndRowInclusive >= request.Height)
                    throw new InvalidDataException(
                        "Protected-neck rows are outside the admitted output raster.");

                var toleranceElement = root.GetProperty("tolerances");
                RequireShape(toleranceElement, "face-texture tolerance authority",
                    "tintModel", "splitShader");
                var tolerances = new FaceTextureToleranceAuthority(
                    ReadRgbErrorTolerance(toleranceElement.GetProperty("tintModel"),
                        "tint-model"),
                    ReadRgbErrorTolerance(toleranceElement.GetProperty("splitShader"),
                        "split-shader"));

                var layers = ReadAndBindLayers(root.GetProperty("layers"), request.Plan,
                    request.AllowedRoot);
                return new FaceTextureAuthority(authorityId, manifestHash, presetHash,
                    privateDiffuseDestination, fullComposite, baseDiffuse, protectedNeck,
                    tolerances, layers);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               JsonException or
                                               InvalidDataException or
                                               ArgumentException or
                                               FormatException or
                                               OverflowException)
            {
                diagnostics.Add(Error("face-texture-authority-invalid", exception.Message));
                return null;
            }
        }
    }

    private static RgbErrorToleranceAuthority ReadRgbErrorTolerance(
        JsonElement element,
        string role)
    {
        RequireShape(element, $"{role} tolerance authority",
            "maximumRgbByteError", "maximumMeanRgbByteError");
        var tolerance = new RgbErrorToleranceAuthority(
            RequiredInt(element, "maximumRgbByteError"),
            RequiredDouble(element, "maximumMeanRgbByteError"));
        if (tolerance.MaximumRgbByteError is < 0 or > 8 ||
            tolerance.MaximumMeanRgbByteError is < 0D or > 1D)
            throw new InvalidDataException(
                $"The {role} tolerance is outside the strict admitted range.");
        return tolerance;
    }

    private static SourceAuthority ReadSource(
        JsonElement element,
        string role,
        WorkspacePath allowedRoot)
    {
        RequireShape(element, role, "sourcePath", "sha256", "width", "height");
        var source = ResolveRelative(allowedRoot, RequiredString(element, "sourcePath"));
        var width = RequiredInt(element, "width");
        var height = RequiredInt(element, "height");
        if (width <= 0 || height <= 0)
            throw new InvalidDataException($"{role} dimensions must be positive.");
        return new SourceAuthority(source,
            new Sha256Hash(RequiredString(element, "sha256")), width, height);
    }

    private static ImmutableArray<LayerAuthority> ReadAndBindLayers(
        JsonElement element,
        RaceMenuNpcAppearancePlan plan,
        WorkspacePath allowedRoot)
    {
        if (element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() != plan.TintDispositions.Length)
            throw new InvalidDataException(
                "Face-texture layers must bind every admitted tint disposition exactly once and in source order.");

        var result = ImmutableArray.CreateBuilder<LayerAuthority>();
        var seen = new HashSet<int>();
        var position = 0;
        foreach (var row in element.EnumerateArray())
        {
            if (row.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each face-texture layer must be an object.");
            var expected = plan.TintDispositions[position++];
            var dispositionWire = RequiredString(row, "disposition");
            var disposition = dispositionWire switch
            {
                "mapped-record" => RaceMenuNpcTintDispositionKind.MappedRecord,
                "baked" => RaceMenuNpcTintDispositionKind.Baked,
                "inactive" => RaceMenuNpcTintDispositionKind.Inactive,
                _ => throw new InvalidDataException(
                    $"Unsupported face-texture disposition '{dispositionWire}'.")
            };
            var isActive = disposition != RaceMenuNpcTintDispositionKind.Inactive;
            RequireShape(row, "face-texture layer", isActive
                ? ["jslotIndex", "disposition", "presetColor", "presetTexture",
                    "provider", "sourcePath", "sourceSha256"]
                : ["jslotIndex", "disposition", "presetColor", "presetTexture"]);
            var index = RequiredInt(row, "jslotIndex");
            var color = RequiredUInt32(row, "presetColor");
            var texture = RequiredString(row, "presetTexture");
            if (!seen.Add(index))
                throw new InvalidDataException($"Face-texture tint row {index} is duplicated.");
            if (index != expected.Source.Index || color != expected.Source.Color ||
                !string.Equals(texture, expected.Source.Texture, StringComparison.Ordinal) ||
                disposition != expected.Kind)
            {
                throw new InvalidDataException(
                    $"Face-texture tint row {index} does not exactly match the admitted ordered preset/disposition.");
            }

            if (!isActive)
            {
                if ((byte)(color >> 24) != 0)
                    throw new InvalidDataException(
                        $"Inactive face-texture tint row {index} has non-zero alpha.");
                result.Add(new LayerAuthority(index, disposition, color, texture,
                    null, null, null));
                continue;
            }

            if ((byte)(color >> 24) == 0)
                throw new InvalidDataException(
                    $"Active face-texture tint row {index} has zero alpha.");
            var provider = RequiredString(row, "provider");
            if (provider.Length > 260)
                throw new InvalidDataException(
                    $"Face-texture provider for tint row {index} may not exceed 260 characters.");
            result.Add(new LayerAuthority(index, disposition, color, texture, provider,
                ResolveRelative(allowedRoot, RequiredString(row, "sourcePath")),
                new Sha256Hash(RequiredString(row, "sourceSha256"))));
        }

        var active = result.Where(item =>
            item.Disposition != RaceMenuNpcTintDispositionKind.Inactive).ToArray();
        if (active.Length > RaceMenuNpcFaceTextureCompositionLimits.MaximumActiveLayers)
            throw new InvalidDataException(
                $"Face-texture composition admits at most " +
                $"{RaceMenuNpcFaceTextureCompositionLimits.MaximumActiveLayers} active tint layers.");
        if (active.Length == 0 || active[0].JslotIndex != 0 ||
            active[0].Disposition != RaceMenuNpcTintDispositionKind.MappedRecord)
            throw new InvalidDataException(
                "Face-texture composition requires mapped .jslot tint row 0 as the first active skin-tone layer.");
        return result.ToImmutable();
    }

    private static WorkspacePath ResolveRelative(WorkspacePath allowedRoot, string value)
    {
        var relative = new AssetPath(value);
        var resolved = new WorkspacePath(Path.Combine(allowedRoot.Value,
            relative.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!resolved.IsUnder(allowedRoot))
            throw new InvalidDataException("Face-texture source path escaped the allowed K-local root.");
        return resolved;
    }

    private static bool PathEquals(WorkspacePath left, WorkspacePath right) =>
        string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

    private static void RequireShape(JsonElement element, string role, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{role} must be an object.");
        var actual = element.EnumerateObject().Select(item => item.Name).ToArray();
        if (actual.Length != fields.Length || fields.Any(field =>
                !actual.Contains(field, StringComparer.Ordinal)))
            throw new InvalidDataException($"{role} has missing or unknown fields.");
    }

    private static void RejectDuplicateKeys(JsonElement element)
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
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } result || result.Any(char.IsControl))
            throw new InvalidDataException($"'{name}' must be a non-empty string.");
        return result;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"'{name}' must be a 32-bit integer.");
        return result;
    }

    private static uint RequiredUInt32(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out var result))
            throw new InvalidDataException($"'{name}' must be an unsigned 32-bit integer.");
        return result;
    }

    private static double RequiredDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) ||
            !double.IsFinite(result))
            throw new InvalidDataException($"'{name}' must be a finite number.");
        return result;
    }
}
