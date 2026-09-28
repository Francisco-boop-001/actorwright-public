using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Writes runtime-route artifacts and the closed preset bundle into one owned
/// candidate directory. Every input is rehashed immediately before output.
/// </summary>
public sealed class RaceMenuPresetBundleAuthorityWriter(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot)
    : IRaceMenuPresetBundleAuthorityWriter
{
    private const long MaximumInputBytes = 512L * 1024 * 1024;
    private const int MaximumJsonBytes = 1 * 1024 * 1024;

    public async ValueTask<RaceMenuPresetBundleAuthorityWriteResult> WriteAsync(
        RaceMenuPresetBundleAuthorityWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        await VerifyInputAsync(request.Companion.Preset,
            request.Companion.PresetSha256, "preset", diagnostics, cancellationToken);
        await VerifyInputAsync(request.Companion.FaceGeom,
            request.Companion.FaceGeomSha256, "CharGen FaceGeom", diagnostics, cancellationToken);
        await VerifyInputAsync(request.Companion.FaceTint,
            request.Companion.FaceTintSha256, "CharGen FaceTint", diagnostics, cancellationToken);
        ProviderResourceAuthoritySet? productResources =
            request.ProviderContext.ProviderResources;
        if (productResources is null)
        {
            await VerifyInputAsync(request.ProviderContext.ManifestPath,
                request.ProviderContext.ExpectedManifestSha256, "provider manifest", diagnostics,
                cancellationToken);
        }
        else
        {
            foreach (ProviderResourceAuthority resource in productResources.Files)
                await VerifyProviderResourceAsync(resource, diagnostics, cancellationToken);
        }
        await VerifyInputAsync(request.RecordAuthority.ManifestPath,
            request.RecordAuthority.ManifestSha256, "record authority", diagnostics,
            cancellationToken);
        Sha256Hash? dependencyHash = productResources is null
            ? await HashInputAsync(
                request.ProviderContext.DependencyManifest,
                "dependency manifest",
                diagnostics,
                cancellationToken)
            : productResources.DependencyManifest.ExpectedSha256;
        if (HasErrors(diagnostics) || dependencyHash is null) return Refused(diagnostics);

        string bundleId = BuildBundleId(request);
        string routeId = bundleId.Replace("selection-bundle-", "selection-routes-",
            StringComparison.Ordinal);
        ImmutableArray<RaceMenuNpcAppearanceField> runtimeFields = RuntimeFields(request.Preset);
        var created = ImmutableArray.CreateBuilder<WorkspacePath>();
        var routes = ImmutableArray.CreateBuilder<RouteArtifact>(runtimeFields.Length);
        try
        {
            foreach (RaceMenuNpcAppearanceField field in runtimeFields)
            {
                var path = new WorkspacePath(Path.Combine(
                    request.CandidateRoot.Value,
                    $"runtime-{field.ToWireName()}.json"));
                byte[] bytes = SerializeRuntimeArtifact(field);
                Sha256Hash hash = await WriteNewAsync(path, bytes, cancellationToken);
                created.Add(path);
                routes.Add(new RouteArtifact(field, path, hash));
            }

            var routeManifest = new WorkspacePath(Path.Combine(
                request.CandidateRoot.Value, "runtime-routes.json"));
            byte[] routeBytes = SerializeRuntimeRoutes(routeId, bundleId, routes.ToImmutable());
            Sha256Hash routeHash = await WriteNewAsync(
                routeManifest, routeBytes, cancellationToken);
            created.Add(routeManifest);
            var runtimeAuthority = new RaceMenuNpcRuntimeRouteAuthority(
                routeManifest, routeHash);

            var bundleManifest = new WorkspacePath(Path.Combine(
                request.CandidateRoot.Value, "bundle.json"));
            byte[] bundleBytes = SerializeBundle(
                bundleId, request, dependencyHash.Value, runtimeAuthority);
            Sha256Hash bundleHash = await WriteNewAsync(
                bundleManifest, bundleBytes, cancellationToken);
            created.Add(bundleManifest);
            var bundle = new RaceMenuNpcPresetBundle(
                bundleManifest,
                bundleHash,
                request.Companion.Preset,
                request.Companion.PresetSha256,
                request.Companion.FaceGeom,
                request.Companion.FaceGeomSha256,
                request.Companion.FaceTint,
                request.Companion.FaceTintSha256,
                request.RecordAuthority.Authority,
                runtimeAuthority);
            return new RaceMenuPresetBundleAuthorityWriteResult(
                true,
                new RaceMenuPresetBundleAuthorityArtifact(
                    bundleId, bundle, runtimeAuthority, created.ToImmutable()),
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            Cleanup(created);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or
                                           NotSupportedException)
        {
            Cleanup(created);
            diagnostics.Add(Error("racemenu-bundle-authority-write", exception.Message));
            return Refused(diagnostics);
        }
    }

    private byte[] SerializeBundle(
        string bundleId,
        RaceMenuPresetBundleAuthorityWriteRequest request,
        Sha256Hash dependencyHash,
        RaceMenuNpcRuntimeRouteAuthority runtimeAuthority)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = Writer(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion",
                request.ProviderContext.ProviderResources is null ? 1 : 3);
            writer.WriteString("bundleId", bundleId);
            writer.WriteString("edition", "skyrimse");
            writer.WriteStartObject("preset");
            writer.WriteString("path", Relative(request.Companion.Preset));
            writer.WriteString("sha256", request.Companion.PresetSha256.Value);
            writer.WriteEndObject();
            writer.WriteStartObject("charGen");
            writer.WriteString("faceGeomPath", Relative(request.Companion.FaceGeom));
            writer.WriteString("faceGeomSha256", request.Companion.FaceGeomSha256.Value);
            writer.WriteString("faceTintPath", Relative(request.Companion.FaceTint));
            writer.WriteString("faceTintSha256", request.Companion.FaceTintSha256.Value);
            writer.WriteEndObject();
            if (request.ProviderContext.ProviderResources is { } resources)
            {
                writer.WriteStartObject("providerAuthority");
                writer.WriteString("kind", "product-fixture");
                writer.WriteString("bundleId", resources.BundleId);
                writer.WriteString("registryManifestSha256",
                    resources.RegistryManifestSha256.Value);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteStartObject("providerContext");
                writer.WriteString("manifestPath", Relative(request.ProviderContext.ManifestPath));
                writer.WriteString("manifestSha256", request.ProviderContext.ExpectedManifestSha256.Value);
                writer.WriteString("dependencyManifestPath",
                    Relative(request.ProviderContext.DependencyManifest));
                writer.WriteString("dependencyManifestSha256", dependencyHash.Value);
                writer.WriteEndObject();
            }
            writer.WriteStartObject("recordAuthority");
            writer.WriteString("manifestPath", Relative(request.RecordAuthority.ManifestPath));
            writer.WriteString("manifestSha256", request.RecordAuthority.ManifestSha256.Value);
            writer.WriteEndObject();
            writer.WriteStartObject("runtimeRoutes");
            writer.WriteString("manifestPath", Relative(runtimeAuthority.ManifestPath));
            writer.WriteString("manifestSha256", runtimeAuthority.ExpectedManifestSha256.Value);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private byte[] SerializeRuntimeRoutes(
        string routeId,
        string bundleId,
        ImmutableArray<RouteArtifact> routes)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = Writer(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("routeId", routeId);
            writer.WriteString("bundleId", bundleId);
            writer.WriteStartArray("routes");
            foreach (RouteArtifact route in routes)
            {
                writer.WriteStartObject();
                writer.WriteString("field", route.Field.ToWireName());
                writer.WriteString("classification", "runtime-declared");
                writer.WriteString("artifactPath", Relative(route.Path));
                writer.WriteString("artifactSha256", route.Sha256.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static byte[] SerializeRuntimeArtifact(RaceMenuNpcAppearanceField field)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = Writer(buffer))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", 1);
            writer.WriteString("field", field.ToWireName());
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static Utf8JsonWriter Writer(IBufferWriter<byte> buffer) =>
        new(buffer, new JsonWriterOptions { Indented = true, SkipValidation = false });

    private async ValueTask<Sha256Hash> WriteNewAsync(
        WorkspacePath path,
        byte[] bytes,
        CancellationToken cancellationToken)
    {
        if (bytes.Length <= 0 || bytes.Length > MaximumJsonBytes ||
            !path.IsUnder(labRoot) || File.Exists(path.Value) || Directory.Exists(path.Value))
            throw new InvalidDataException("Generated authority destination or size is invalid.");
        await using (var stream = new FileStream(
            path.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        byte[] reopened = await File.ReadAllBytesAsync(path.Value, cancellationToken)
            .ConfigureAwait(false);
        if (!reopened.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException("Generated authority did not reopen byte-for-byte.");
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(reopened)));
    }

    private async ValueTask VerifyInputAsync(
        WorkspacePath path,
        Sha256Hash expected,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        Sha256Hash? actual = await HashInputAsync(
            path, role, diagnostics, cancellationToken);
        if (actual is not null && actual.Value != expected)
            diagnostics.Add(Error("racemenu-bundle-authority-hash",
                $"{role} hash {actual.Value} does not match {expected}."));
    }

    private async ValueTask<Sha256Hash?> HashInputAsync(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, path));
        if (HasErrors(diagnostics)) return null;
        try
        {
            if (HasReparsePath(path.Value))
                throw new InvalidDataException($"{role} traverses a reparse point.");
            var info = new FileInfo(path.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumInputBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    $"{role} must be one ordinary non-empty K-local file no larger than {MaximumInputBytes} bytes.");
            await using var stream = new FileStream(path.Value, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-bundle-authority-input",
                $"{role} could not be admitted: {exception.Message}"));
            return null;
        }
    }

    private static async ValueTask VerifyProviderResourceAsync(
        ProviderResourceAuthority resource,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            if (resource is not ApplicationProviderResourceAuthority application ||
                application.IsDirectory)
                throw new InvalidDataException(
                    $"Product-provider role '{resource.Role}' is not one application-owned file.");
            var info = new FileInfo(application.Path.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumInputBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    $"Product-provider role '{resource.Role}' is absent or not an ordinary file.");
            await using var stream = new FileStream(application.Path.Value, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
            if (actual != application.ExpectedSha256)
                throw new InvalidDataException(
                    $"Product-provider role '{resource.Role}' changed after registry admission.");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-bundle-authority-product-input",
                exception.Message));
        }
    }

    private void ValidateRequest(
        RaceMenuPresetBundleAuthorityWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Preset.Format != PresetFormat.RaceMenuJslot ||
            request.Preset.Edition != GameEdition.SkyrimSpecialEdition ||
            !request.Preset.IsValid ||
            request.Preset.SourceHash != request.Companion.PresetSha256)
        {
            diagnostics.Add(Error("racemenu-bundle-authority-preset",
                "Bundle authority requires the same valid typed preset and companion export."));
        }
        if (!HasSameStemCompanions(request.Companion))
        {
            diagnostics.Add(Error("racemenu-bundle-authority-companions",
                "Preset authority requires same-directory, same-stem .jslot, .nif, and .dds companions."));
        }
        if (!IsOwnedCandidateRoot(request, diagnostics))
        {
            diagnostics.Add(Error("racemenu-bundle-authority-root",
                "Candidate root must be an ordinary K-local directory containing only its record-authority manifest."));
        }
        if (!request.RecordAuthority.ManifestPath.IsUnder(request.CandidateRoot) ||
            request.RecordAuthority.Authority.ManifestPath !=
            request.RecordAuthority.ManifestPath ||
            request.RecordAuthority.Authority.ExpectedManifestSha256 !=
            request.RecordAuthority.ManifestSha256)
        {
            diagnostics.Add(Error("racemenu-bundle-authority-record",
                "Record authority must belong to the candidate root and retain one exact path/hash identity."));
        }
    }

    private bool IsOwnedCandidateRoot(
        RaceMenuPresetBundleAuthorityWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!request.CandidateRoot.IsUnder(labRoot) ||
            request.CandidateRoot == labRoot ||
            !Directory.Exists(request.CandidateRoot.Value)) return false;
        try
        {
            if (HasReparsePath(request.CandidateRoot.Value)) return false;
            string[] entries = Directory.GetFileSystemEntries(request.CandidateRoot.Value);
            return entries.Length == 1 && string.Equals(
                Path.GetFullPath(entries[0]),
                request.RecordAuthority.ManifestPath.Value,
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-bundle-authority-root-inspection",
                $"Candidate root could not be inspected: {exception.Message}"));
            return false;
        }
    }

    private static bool HasSameStemCompanions(RaceMenuPresetCompanionExport companion)
    {
        string preset = companion.Preset.Value;
        string faceGeom = companion.FaceGeom.Value;
        string faceTint = companion.FaceTint.Value;
        return string.Equals(Path.GetExtension(preset), ".jslot", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetExtension(faceGeom), ".nif", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetExtension(faceTint), ".dds", StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetDirectoryName(preset), Path.GetDirectoryName(faceGeom),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetDirectoryName(preset), Path.GetDirectoryName(faceTint),
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetFileNameWithoutExtension(preset),
                   Path.GetFileNameWithoutExtension(faceGeom), StringComparison.OrdinalIgnoreCase) &&
               string.Equals(Path.GetFileNameWithoutExtension(preset),
                   Path.GetFileNameWithoutExtension(faceTint), StringComparison.OrdinalIgnoreCase);
    }

    private bool HasReparsePath(string path)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            if (string.Equals(current, labRoot.Value, StringComparison.OrdinalIgnoreCase)) return false;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase)) return true;
            current = parent;
        }
    }

    private static ImmutableArray<RaceMenuNpcAppearanceField> RuntimeFields(
        PresetDocument preset)
    {
        var result = ImmutableArray.CreateBuilder<RaceMenuNpcAppearanceField>();
        PresetAppearance appearance = preset.Appearance;
        if (appearance.Overlays.Length > 0 ||
            (appearance.RaceMenu?.BodyOverlays.Length ?? 0) > 0)
            result.Add(RaceMenuNpcAppearanceField.Overlays);
        if ((appearance.RaceMenu?.NodeTransforms.Length ?? 0) > 0)
            result.Add(RaceMenuNpcAppearanceField.NodeTransforms);
        if (!string.IsNullOrWhiteSpace(appearance.Skin) ||
            (appearance.RaceMenu?.SkinOverrides.Length ?? 0) > 0)
            result.Add(RaceMenuNpcAppearanceField.SkinOverrides);
        return result.ToImmutable();
    }

    private static string BuildBundleId(RaceMenuPresetBundleAuthorityWriteRequest request)
    {
        string identity = string.Join('|',
            request.Companion.PresetSha256.Value,
            request.Companion.FaceGeomSha256.Value,
            request.Companion.FaceTintSha256.Value,
            request.RecordAuthority.ManifestSha256.Value,
            request.ProviderContext.ProviderResources?.BundleId ?? "workspace-provider",
            request.ProviderContext.ProviderResources?.RegistryManifestSha256.Value ??
                request.ProviderContext.ExpectedManifestSha256.Value);
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "selection-bundle-" + hash[..24].ToLowerInvariant();
    }

    private string Relative(WorkspacePath path)
    {
        if (!path.IsUnder(labRoot) || path == labRoot)
            throw new InvalidDataException("Bundle authority path escaped the K-local lab root.");
        return new AssetPath(Path.GetRelativePath(labRoot.Value, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')).Value;
    }

    private void Cleanup(ImmutableArray<WorkspacePath>.Builder created)
    {
        for (var index = created.Count - 1; index >= 0; index--)
        {
            WorkspacePath path = created[index];
            try
            {
                if (path.IsUnder(labRoot) && File.Exists(path.Value) &&
                    !File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
                    File.Delete(path.Value);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The candidate root remains visibly incomplete and can be
                // cleaned by its owning transaction after inspection.
            }
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuPresetBundleAuthorityWriteResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record RouteArtifact(
        RaceMenuNpcAppearanceField Field,
        WorkspacePath Path,
        Sha256Hash Sha256);
}
