using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class RaceMenuNpcExecutionRequestFileLoader
{
    private const string LegacyProviderManifestSha256 =
        "EACB7112F3D0177F1C8C1B14604B3F36F413185C52973BC6027059EAE1D7D06E";
    private const string LegacyTemplateSha256 =
        "421C3A902A87F343D50F8791CC4B9CAFB2B71BF3B7D94F7C2F7BE2DB6535BBF0";
    private const string LegacyCarrierSha256 =
        "4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9";

    private RaceMenuNpcExecutionRequestFileLoadResult?
        TryCreateMigrationRequired(
            RaceMenuNpcExecutionRequestFileLoadRequest loadRequest,
            ExecutionRequestDto dto,
            byte[] requestBytes,
            Sha256Hash actualHash,
            long byteLength)
    {
        if (dto.SchemaVersion is not (1 or 2) ||
            dto.ProviderContext is not { } provider ||
            provider.ProductFixtureBundle is not null ||
            !IsRecognizedMissingLegacyProvider(provider))
            return null;
        if (applicationProviderRegistry is null)
            throw new ProductProviderUnavailableException(
                "The recognized legacy provider requires the packaged product-provider registry for migration.");

        PresetBundleDto bundle = Required(dto.PresetBundle, "presetBundle");
        WorkspacePath bundlePath = Resolve(bundle.ManifestPath);
        byte[] bundleBytes = ReadVerified(
            bundlePath,
            Hash(bundle.ManifestSha256),
            "preset-bundle manifest");
        VerifyNonProviderBundleInputs(bundle);
        WorkspacePath migratedRoot =
            loadRequest.PlannedMigratedRequestRoot ??
            new WorkspacePath(Path.Combine(
                Path.GetDirectoryName(loadRequest.RequestFile.Value)!,
                Path.GetFileNameWithoutExtension(
                    loadRequest.RequestFile.Value) + ".provider-migration"));
        if (!applicationProviderRegistry.TryGetDefaultBlankNpcFixture(
                out ProductFixtureBundleReference? product) || product is null)
            throw new ProductProviderUnavailableException(
                "The optional bundled provider is not installed. Use a workspace-provider request built from legally obtained local assets.");
        ApplicationProviderResourceAdmissionResult productAdmission =
            applicationProviderRegistry.Admit(
                product,
                new FormId(0x0000_0800),
                GameEdition.SkyrimSpecialEdition,
                NpcSex.Female);
        if (!productAdmission.Accepted || productAdmission.Authority is null)
            throw new InvalidDataException(
                "Product-provider bundle failed admission: " +
                string.Join(" ", productAdmission.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
        ProviderMigrationPlan plan = new ProviderMigrationDocumentCodec(
            workspaceRoot).CreatePlan(
                loadRequest.RequestFile,
                actualHash,
                requestBytes,
                bundlePath,
                Hash(bundle.ManifestSha256),
                bundleBytes,
                product,
                migratedRoot);
        return new RaceMenuNpcExecutionRequestFileLoadResult(
            RaceMenuNpcExecutionRequestFileLoadStatus.MigrationRequired,
            loadRequest.RequestFile,
            loadRequest.ExpectedSha256,
            actualHash,
            byteLength,
            null,
            [new Diagnostic(
                "legacy-provider-fixture-bytes-unavailable",
                DiagnosticSeverity.Error,
                "The recognized Gate 1 provider declarations reference unavailable exact legacy bytes. Review an explicit migration to the packaged Actorwright fixture.")],
            plan.Review.Value);
    }

    private bool IsRecognizedMissingLegacyProvider(
        ProviderContextDto provider)
    {
        if (!string.Equals(provider.ManifestSha256,
                LegacyProviderManifestSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(provider.TemplateSha256,
                LegacyTemplateSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(provider.FaceGeomSha256,
                LegacyCarrierSha256,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(provider.ManifestPath),
                "gate1-blank-provider-bundle.json",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(provider.TemplatePlugin),
                "EmiCarrierProbe.esp",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(provider.FaceGeomCarrier),
                "00000800.NIF",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetFileName(provider.FaceTintManifest),
                "gate1-qualified-facetint.json",
                StringComparison.OrdinalIgnoreCase))
            return false;
        return !Exists(provider.ManifestPath) ||
            !Exists(provider.TemplatePlugin) ||
            !Exists(provider.FaceGeomCarrier) ||
            !Exists(provider.FaceTintManifest) ||
            !Exists(provider.DependencyManifest) ||
            !Directory.Exists(Resolve(provider.FaceTintProviderRoot).Value);
    }

    private bool Exists(string? relative) =>
        File.Exists(Resolve(relative).Value);

    private void VerifyNonProviderBundleInputs(PresetBundleDto bundle)
    {
        _ = ReadVerified(Resolve(bundle.PresetPath),
            Hash(bundle.PresetSha256), "preset");
        _ = ReadVerified(Resolve(bundle.FaceGeomPath),
            Hash(bundle.FaceGeomSha256), "CharGen FaceGeom");
        _ = ReadVerified(Resolve(bundle.FaceTintPath),
            Hash(bundle.FaceTintSha256), "CharGen FaceTint");
        _ = ReadVerified(Resolve(bundle.RecordAuthorityPath),
            Hash(bundle.RecordAuthoritySha256), "record authority");
        _ = ReadVerified(Resolve(bundle.RuntimeRoutesPath),
            Hash(bundle.RuntimeRoutesSha256), "runtime routes");
    }

    private static byte[] ReadVerified(
        WorkspacePath path,
        Sha256Hash expected,
        string role)
    {
        var info = new FileInfo(path.Value);
        if (!info.Exists || info.Length <= 0 || info.Length > 64L * 1024 * 1024 ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                $"The {role} is absent or not an ordinary file.");
        byte[] bytes = File.ReadAllBytes(path.Value);
        var actual = new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(bytes)));
        if (actual != expected)
            throw new InvalidDataException(
                $"The {role} hash does not match its declaration.");
        return bytes;
    }
}
