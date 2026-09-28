using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public partial class RaceMenuNpcStandaloneAuthorityReader
{
    private const int MaximumFinalOutputAuthorityBytes = 1 * 1024 * 1024;
    private const int MaximumFinalOutputEvidenceRows = 16;

    private async ValueTask<RaceMenuNpcFinalOutputAuthority?> ReadFinalOutputAuthorityAsync(
        WorkspacePath manifestPath,
        Sha256Hash expectedManifestSha256,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(WorkspacePolicy.EvaluateReadRoot(
            LaboratoryRoot,
            manifestPath));
        if (HasErrors(diagnostics)) return null;

        try
        {
            var info = new FileInfo(manifestPath.Value);
            if (!info.Exists || info.Length <= 0 ||
                info.Length > MaximumFinalOutputAuthorityBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    "The final-output authority must be an ordinary non-empty K-local file no larger than 1 MiB.");

            var bytes = await File.ReadAllBytesAsync(manifestPath.Value, cancellationToken);
            var manifestSha256 = new Sha256Hash(
                Convert.ToHexString(SHA256.HashData(bytes)));
            if (manifestSha256 != expectedManifestSha256)
                throw new InvalidDataException(
                    $"Final-output authority hash {manifestSha256} does not match {expectedManifestSha256}.");

            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
            RejectDuplicateKeys(document.RootElement);
            var root = document.RootElement;
            RequireShape(root, "final-output authority", "schemaVersion", "authorityId",
                "edition", "sourceKind", "presetSha256", "charGenFaceGeomSha256",
                "charGenFaceTintSha256", "faceGeom", "faceTint", "evidence",
                "runtimeAuthority");
            if (RequiredInt(root, "schemaVersion") != 1 ||
                !string.Equals(RequiredString(root, "edition"), "skyrimse",
                    StringComparison.Ordinal) ||
                !string.Equals(RequiredString(root, "sourceKind"),
                    "admitted-final-oracle", StringComparison.Ordinal))
                throw new InvalidDataException(
                    "Final-output authority requires schemaVersion 1, Skyrim SE, and sourceKind 'admitted-final-oracle'.");

            var authorityId = RequiredString(root, "authorityId");
            if (authorityId.Length > 128)
                throw new InvalidDataException(
                    "Final-output authorityId may not exceed 128 characters.");

            var faceGeom = root.GetProperty("faceGeom");
            RequireShape(faceGeom, "final FaceGeom authority", "path", "sha256");
            var faceTint = root.GetProperty("faceTint");
            RequireShape(faceTint, "final FaceTint authority", "path", "sha256",
                "width", "height");
            var width = RequiredInt(faceTint, "width");
            var height = RequiredInt(faceTint, "height");
            if (width <= 0 || height <= 0 || width > 16_384 || height > 16_384)
                throw new InvalidDataException(
                    "Final FaceTint dimensions must be positive and no larger than 16384.");

            var evidenceElement = root.GetProperty("evidence");
            if (evidenceElement.ValueKind != JsonValueKind.Array ||
                evidenceElement.GetArrayLength() is 0 or > MaximumFinalOutputEvidenceRows)
                throw new InvalidDataException(
                    $"Final-output evidence must contain 1..{MaximumFinalOutputEvidenceRows} rows.");
            var evidence = ImmutableArray.CreateBuilder<RaceMenuNpcFinalOutputEvidence>();
            var evidencePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var row in evidenceElement.EnumerateArray())
            {
                RequireShape(row, "final-output evidence", "path", "sha256");
                var path = ResolveRelative(RequiredString(row, "path"));
                if (!evidencePaths.Add(path.Value))
                    throw new InvalidDataException(
                        $"Final-output evidence path '{path.Value}' occurs more than once.");
                evidence.Add(new RaceMenuNpcFinalOutputEvidence(
                    path,
                    new Sha256Hash(RequiredString(row, "sha256"))));
            }

            var runtimeAuthority = RequiredBoolean(root, "runtimeAuthority");
            if (runtimeAuthority)
                throw new InvalidDataException(
                    "A rebound final-output oracle cannot claim runtime authority for the new package.");

            return new RaceMenuNpcFinalOutputAuthority(
                manifestPath,
                manifestSha256,
                authorityId,
                new Sha256Hash(RequiredString(root, "presetSha256")),
                new Sha256Hash(RequiredString(root, "charGenFaceGeomSha256")),
                new Sha256Hash(RequiredString(root, "charGenFaceTintSha256")),
                ResolveRelative(RequiredString(faceGeom, "path")),
                new Sha256Hash(RequiredString(faceGeom, "sha256")),
                ResolveRelative(RequiredString(faceTint, "path")),
                new Sha256Hash(RequiredString(faceTint, "sha256")),
                width,
                height,
                evidence.ToImmutable(),
                runtimeAuthority);
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
            diagnostics.Add(Error("racemenu-final-output-authority-invalid",
                exception.Message));
            return null;
        }
    }

    protected static bool ValidateFinalOutputAuthorityBinding(
        RaceMenuNpcBuildRequest request,
        RaceMenuNpcStandaloneAssets assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (assets.FinalOutputAuthority is not { } authority) return true;

        var bundle = request.PresetBundle;
        if (authority.PresetSha256 != bundle.ExpectedPresetSha256 ||
            authority.CharGenFaceGeomSha256 != bundle.ExpectedCharGenFaceGeomSha256 ||
            authority.CharGenFaceTintSha256 != bundle.ExpectedCharGenFaceTintSha256)
            diagnostics.Add(Error("racemenu-final-output-authority-character-drift",
                "The final-output oracle is not bound to the exact admitted preset and original CharGen pair."));
        if (authority.FaceTintWidth != assets.FaceTintWidth ||
            authority.FaceTintHeight != assets.FaceTintHeight)
            diagnostics.Add(Error("racemenu-final-output-authority-dimensions",
                "The final-output oracle FaceTint dimensions disagree with the standalone asset declaration."));
        if (authority.RuntimeAuthority)
            diagnostics.Add(Error("racemenu-final-output-authority-runtime",
                "The rebound final-output oracle may not claim runtime authority for this new package."));
        return !HasErrors(diagnostics);
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"'{name}' must be a Boolean.");
        return value.GetBoolean();
    }
}
