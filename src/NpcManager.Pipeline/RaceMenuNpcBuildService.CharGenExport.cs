using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public partial class RaceMenuNpcStandaloneAuthorityReader
{
    private const int MaximumExternalCharGenAuthorityBytes = 1 * 1024 * 1024;
    private const int MaximumExternalPresetBytes = 16 * 1024 * 1024;
    private const int MaximumExternalFaceGeomBytes = 64 * 1024 * 1024;
    private const int MaximumExternalFaceTintBytes = 16 * 1024 * 1024;

    private async ValueTask<RaceMenuNpcExternalCharGenExportAuthority?>
        ReadExternalCharGenExportAuthorityAsync(
            WorkspacePath manifestPath,
            Sha256Hash expectedManifestSha256,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        byte[]? bytes = await ReadHashBoundOrdinaryFileAsync(
            manifestPath,
            expectedManifestSha256,
            MaximumExternalCharGenAuthorityBytes,
            "racemenu-external-chargen-authority",
            "external RaceMenu CharGen export authority manifest",
            diagnostics,
            cancellationToken);
        if (bytes is null) return null;

        using JsonDocument document = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
        RejectDuplicateKeys(document.RootElement);
        JsonElement root = document.RootElement;
        RequireShape(
            root,
            "external RaceMenu CharGen export authority",
            "schemaVersion", "authorityId", "edition", "sourceKind",
            "presetPath", "presetSha256", "faceGeomPath", "faceGeomSha256",
            "faceTintPath", "faceTintSha256", "race", "sex",
            "userConfirmedVisualMatch", "runtimeAuthority");

        bool userConfirmed = RequiredExternalExportBoolean(
            root, "userConfirmedVisualMatch");
        bool runtimeAuthority = RequiredExternalExportBoolean(
            root, "runtimeAuthority");
        if (RequiredInt(root, "schemaVersion") != 1 ||
            !string.Equals(
                RequiredString(root, "edition"),
                "skyrimse",
                StringComparison.Ordinal) ||
            !string.Equals(
                RequiredString(root, "sourceKind"),
                "racemenu-chargen-export",
                StringComparison.Ordinal) ||
            !userConfirmed ||
            runtimeAuthority)
        {
            throw new InvalidDataException(
                "External CharGen export authority requires schemaVersion 1, Skyrim SE, sourceKind 'racemenu-chargen-export', userConfirmedVisualMatch true, and runtimeAuthority false.");
        }

        string authorityId = RequiredString(root, "authorityId");
        if (authorityId.Length > 128)
            throw new InvalidDataException(
                "External CharGen export authorityId may not exceed 128 characters.");
        if (!FormReference.TryParse(
                RequiredString(root, "race"),
                out FormReference race))
            throw new InvalidDataException(
                "External CharGen export race must be a plugin-qualified FormID.");
        NpcSex sex = RequiredString(root, "sex") switch
        {
            "female" => NpcSex.Female,
            "male" => NpcSex.Male,
            _ => throw new InvalidDataException(
                "External CharGen export sex must be 'female' or 'male'.")
        };

        var preset = ResolveRelative(RequiredString(root, "presetPath"));
        var presetSha256 = new Sha256Hash(
            RequiredString(root, "presetSha256"));
        var faceGeom = ResolveRelative(RequiredString(root, "faceGeomPath"));
        var faceGeomSha256 = new Sha256Hash(
            RequiredString(root, "faceGeomSha256"));
        var faceTint = ResolveRelative(RequiredString(root, "faceTintPath"));
        var faceTintSha256 = new Sha256Hash(
            RequiredString(root, "faceTintSha256"));
        if (!preset.Value.EndsWith(".jslot", StringComparison.OrdinalIgnoreCase) ||
            !faceGeom.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase) ||
            !faceTint.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "External CharGen export authority requires .jslot, .nif, and .dds source paths.");
        }

        if (await ReadHashBoundOrdinaryFileAsync(
                preset,
                presetSha256,
                MaximumExternalPresetBytes,
                "racemenu-external-chargen-preset",
                "visually confirmed RaceMenu preset",
                diagnostics,
                cancellationToken) is null ||
            await ReadHashBoundOrdinaryFileAsync(
                faceGeom,
                faceGeomSha256,
                MaximumExternalFaceGeomBytes,
                "racemenu-external-chargen-facegeom",
                "RaceMenu-exported FaceGeom",
                diagnostics,
                cancellationToken) is null ||
            await ReadHashBoundOrdinaryFileAsync(
                faceTint,
                faceTintSha256,
                MaximumExternalFaceTintBytes,
                "racemenu-external-chargen-facetint",
                "RaceMenu-exported FaceTint",
                diagnostics,
                cancellationToken) is null)
        {
            return null;
        }

        return new RaceMenuNpcExternalCharGenExportAuthority(
            manifestPath,
            expectedManifestSha256,
            authorityId,
            preset,
            presetSha256,
            faceGeom,
            faceGeomSha256,
            faceTint,
            faceTintSha256,
            race,
            sex,
            UserConfirmedVisualMatch: true,
            RuntimeAuthority: false);
    }

    internal static bool ValidateExternalCharGenExportBinding(
        RaceMenuNpcBuildRequest request,
        RaceMenuNpcStandaloneAssets assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (assets.ExternalCharGenExportAuthority is not { } authority)
            return true;

        RaceMenuNpcPresetBundle bundle = request.PresetBundle;
        if (authority.PresetSha256 != bundle.ExpectedPresetSha256 ||
            authority.FaceGeomSha256 !=
                bundle.ExpectedCharGenFaceGeomSha256 ||
            authority.FaceTintSha256 != bundle.ExpectedCharGenFaceTintSha256 ||
            authority.Race != request.References.Race ||
            authority.Sex != request.Traits.Sex ||
            !authority.UserConfirmedVisualMatch ||
            authority.RuntimeAuthority)
        {
            diagnostics.Add(Error(
                "racemenu-external-chargen-character-drift",
                "The external CharGen export authority is not bound to the exact preset, selected FaceGeom and FaceTint, race, sex, user confirmation, and non-runtime limits."));
            return false;
        }
        return true;
    }

    private static bool RequiredExternalExportBoolean(
        JsonElement element,
        string name)
    {
        JsonElement value = element.GetProperty(name);
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => throw new InvalidDataException(
                $"{name} must be a JSON boolean.")
        };
    }
}
