using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reads one closed, hash-bound Gate 1 provider bundle and proves that every
/// requested component belongs to that same provider identity. It also parses
/// and rehashes the dependency evidence rather than treating it as opaque JSON.
/// </summary>
public sealed partial class BlankNpcProviderService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IAssetIndexer? providerAssetIndexer = null) : IBlankNpcProviderService
{
    private const long MaximumManifestBytes = 1_048_576;
    private readonly IAssetIndexer assetIndexer = providerAssetIndexer ?? new BethesdaAssetIndexer();

    public async ValueTask<BlankNpcProviderBindingRequest> SelectWorkspaceManifestAsync(
        WorkspacePath manifestPath,
        GameEdition edition,
        NpcSex sex,
        CancellationToken cancellationToken)
    {
        var selectionDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        selectionDiagnostics.AddRange(policy.EvaluateReadRoot(labRoot, manifestPath));
        if (HasErrors(selectionDiagnostics))
            throw new InvalidDataException(string.Join(" | ",
                selectionDiagnostics.Select(item => item.Message)));

        try
        {
            byte[] manifestBytes = await ReadBoundedAsync(manifestPath, cancellationToken);
            using JsonDocument document = JsonDocument.Parse(manifestBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 32
                });
            JsonElement root = document.RootElement;
            RequireShape(root, "provider manifest", "schemaVersion", "providerId", "edition", "sex",
                "template", "faceGeom", "faceTint", "dependencies");
            JsonElement template = root.GetProperty("template");
            JsonElement faceGeom = root.GetProperty("faceGeom");
            JsonElement faceTint = root.GetProperty("faceTint");
            JsonElement dependencies = root.GetProperty("dependencies");
            RequireShape(template, "provider template", "path", "sha256", "npcFormId", "masters");
            RequireShape(faceGeom, "provider FaceGeom", "path", "sha256", "graphSha256", "shapeNames");
            RequireShape(faceTint, "provider FaceTint", "manifestPath", "manifestSha256", "providerRoot",
                "sourceAssetPath", "sourceAssetSha256");
            RequireShape(dependencies, "provider dependencies", "manifestPath", "manifestSha256",
                "dependencyId", "headPartCount", "looseAssetCount", "archiveCount");

            WorkspacePath templatePath = ResolveRelative(
                RequiredString(template, "path"), "template path", selectionDiagnostics);
            WorkspacePath faceGeomPath = ResolveRelative(
                RequiredString(faceGeom, "path"), "FaceGeom path", selectionDiagnostics);
            WorkspacePath faceTintManifest = ResolveRelative(
                RequiredString(faceTint, "manifestPath"), "FaceTint manifest path", selectionDiagnostics);
            WorkspacePath providerRoot = ResolveRelative(
                RequiredString(faceTint, "providerRoot"), "FaceTint provider root", selectionDiagnostics);
            WorkspacePath dependencyManifest = ResolveRelative(
                RequiredString(dependencies, "manifestPath"), "dependency manifest path", selectionDiagnostics);
            if (HasErrors(selectionDiagnostics))
                throw new InvalidDataException(string.Join(" | ",
                    selectionDiagnostics.Select(item => item.Message)));
            if (!FormId.TryParse(RequiredString(template, "npcFormId"), out FormId templateNpc))
                throw new InvalidDataException("Provider template NPC FormID is invalid.");

            var request = new BlankNpcProviderBindingRequest(
                manifestPath,
                Hash(manifestBytes),
                edition,
                sex,
                templatePath,
                new Sha256Hash(RequiredString(template, "sha256")),
                templateNpc,
                faceGeomPath,
                new Sha256Hash(RequiredString(faceGeom, "sha256")),
                faceTintManifest,
                providerRoot,
                dependencyManifest);
            BlankNpcProviderBindingResult result = await QualifyAsync(request, cancellationToken);
            if (!result.Qualified || result.Artifact is null)
                throw new InvalidDataException(string.Join(" | ",
                    result.Diagnostics.Select(item => $"{item.Code}: {item.Message}")));
            return request;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or ArgumentException or InvalidDataException or
                                           OverflowException)
        {
            throw new InvalidDataException(
                $"Provider manifest was refused: {exception.Message}", exception);
        }
    }

    public async ValueTask<BlankNpcProviderBindingResult> QualifyAsync(
        BlankNpcProviderBindingRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (request.ProviderResources is not null)
            return await QualifyApplicationAsync(request,
                cancellationToken).ConfigureAwait(false);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.ManifestPath));
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        try
        {
            var manifestBytes = await ReadBoundedAsync(request.ManifestPath, cancellationToken);
            var manifestHash = Hash(manifestBytes);
            if (manifestHash != request.ExpectedManifestSha256)
            {
                diagnostics.Add(Error("blank-provider-manifest-hash-mismatch",
                    $"Provider manifest hash {manifestHash} does not match {request.ExpectedManifestSha256}."));
                return Refused(diagnostics);
            }

            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
            var root = document.RootElement;
            RequireShape(root, "provider manifest", "schemaVersion", "providerId", "edition", "sex",
                "template", "faceGeom", "faceTint", "dependencies");
            if (RequiredInt(root, "schemaVersion") != 1)
                throw new InvalidDataException("Provider schemaVersion must be 1.");
            var providerId = RequiredString(root, "providerId");
            if (string.IsNullOrWhiteSpace(providerId) || providerId.Length > 128)
                throw new InvalidDataException("Provider ID must contain 1-128 characters.");
            if (!GameEditionExtensions.TryParseWireName(RequiredString(root, "edition"), out var edition) ||
                edition != GameEdition.SkyrimSpecialEdition || edition != request.Edition)
                throw new InvalidDataException("Provider edition does not match the Skyrim SE request.");
            var sex = RequiredString(root, "sex").ToLowerInvariant() switch
            {
                "male" => NpcSex.Male,
                "female" => NpcSex.Female,
                _ => throw new InvalidDataException("Provider sex must be 'male' or 'female'.")
            };
            if (sex != request.Sex)
                throw new InvalidDataException("Provider sex does not match the requested NPC sex.");

            var template = root.GetProperty("template");
            RequireShape(template, "provider template", "path", "sha256", "npcFormId", "masters");
            var templatePath = ResolveRelative(RequiredString(template, "path"), "template path", diagnostics);
            var templateHash = new Sha256Hash(RequiredString(template, "sha256"));
            if (!FormId.TryParse(RequiredString(template, "npcFormId"), out var templateNpc))
                throw new InvalidDataException("Provider template NPC FormID is invalid.");
            var masters = ReadUniqueStrings(template.GetProperty("masters"), "provider masters")
                .Select(value => new PluginName(value)).ToImmutableArray();
            if (masters.IsDefaultOrEmpty || !string.Equals(masters[0].Value, "Skyrim.esm", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Provider masters must begin with Skyrim.esm.");

            var faceGeom = root.GetProperty("faceGeom");
            RequireShape(faceGeom, "provider FaceGeom", "path", "sha256", "graphSha256", "shapeNames");
            var faceGeomPath = ResolveRelative(RequiredString(faceGeom, "path"), "FaceGeom path", diagnostics);
            var faceGeomHash = new Sha256Hash(RequiredString(faceGeom, "sha256"));
            var graphHash = new Sha256Hash(RequiredString(faceGeom, "graphSha256"));
            var shapeNames = ReadUniqueStrings(faceGeom.GetProperty("shapeNames"), "FaceGeom shape names");
            if (shapeNames.Length != 7)
                throw new InvalidDataException("The admitted Gate 1 provider must bind exactly seven FaceGeom shapes.");

            var faceTint = root.GetProperty("faceTint");
            RequireShape(faceTint, "provider FaceTint", "manifestPath", "manifestSha256", "providerRoot",
                "sourceAssetPath", "sourceAssetSha256");
            var faceTintManifest = ResolveRelative(RequiredString(faceTint, "manifestPath"),
                "FaceTint manifest path", diagnostics);
            var faceTintManifestHash = new Sha256Hash(RequiredString(faceTint, "manifestSha256"));
            var providerRoot = ResolveRelative(RequiredString(faceTint, "providerRoot"),
                "FaceTint provider root", diagnostics);
            var sourceAsset = new AssetPath(RequiredString(faceTint, "sourceAssetPath"));
            var sourceAssetHash = new Sha256Hash(RequiredString(faceTint, "sourceAssetSha256"));
            var sourceAssetFile = new WorkspacePath(Path.Combine(providerRoot.Value,
                sourceAsset.Value.Replace('/', Path.DirectorySeparatorChar)));
            diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, sourceAssetFile));

            var dependencies = root.GetProperty("dependencies");
            RequireShape(dependencies, "provider dependencies", "manifestPath", "manifestSha256",
                "dependencyId", "headPartCount", "looseAssetCount", "archiveCount");
            var dependencyManifest = ResolveRelative(RequiredString(dependencies, "manifestPath"),
                "dependency manifest path", diagnostics);
            var dependencyHash = new Sha256Hash(RequiredString(dependencies, "manifestSha256"));
            var dependencyId = RequiredString(dependencies, "dependencyId");
            var expectedHeadParts = RequiredNonNegativeInt(dependencies, "headPartCount");
            var expectedLooseAssets = RequiredNonNegativeInt(dependencies, "looseAssetCount");
            var expectedArchives = RequiredNonNegativeInt(dependencies, "archiveCount");

            Bind("template plugin", templatePath, request.TemplatePlugin, diagnostics);
            Bind("template hash", templateHash, request.ExpectedTemplatePluginSha256, diagnostics);
            if (templateNpc != request.TemplateNpcFormId)
                diagnostics.Add(Error("blank-provider-template-formid-mismatch",
                    "The requested template NPC is not bound by the provider manifest."));
            Bind("FaceGeom carrier", faceGeomPath, request.FaceGeomCarrier, diagnostics);
            Bind("FaceGeom hash", faceGeomHash, request.ExpectedFaceGeomCarrierSha256, diagnostics);
            Bind("FaceTint manifest", faceTintManifest, request.FaceTintManifest, diagnostics);
            Bind("FaceTint provider root", providerRoot, request.FaceTintProviderRoot, diagnostics);
            Bind("dependency manifest", dependencyManifest, request.DependencyManifest, diagnostics);

            await VerifyFileAsync(templatePath, templateHash, "template plugin", diagnostics, cancellationToken);
            await VerifyFileAsync(faceGeomPath, faceGeomHash, "FaceGeom carrier", diagnostics, cancellationToken);
            await VerifyFileAsync(faceTintManifest, faceTintManifestHash, "FaceTint manifest", diagnostics, cancellationToken);
            await VerifyFileAsync(sourceAssetFile, sourceAssetHash, "FaceTint provider source", diagnostics, cancellationToken);
            await VerifyFileAsync(dependencyManifest, dependencyHash, "dependency manifest", diagnostics, cancellationToken);
            if (!Directory.Exists(providerRoot.Value))
                diagnostics.Add(Error("blank-provider-root-missing", "The bound FaceTint provider root is missing."));

            if (!HasErrors(diagnostics))
                ValidateTemplateMasters(templatePath, templateNpc, masters);
            await ValidateFaceTintBindingAsync(faceTintManifest, sourceAsset, cancellationToken);
            await ValidateDependencyManifestAsync(dependencyManifest, dependencyId, expectedHeadParts,
                expectedLooseAssets, expectedArchives, masters, diagnostics, cancellationToken);
            if (HasErrors(diagnostics)) return Refused(diagnostics);

            return new BlankNpcProviderBindingResult(true,
                new BlankNpcProviderArtifact(
                    "1", "blank-npc-provider-binding", providerId, request.ManifestPath, manifestHash,
                    edition, sex, masters, graphHash, shapeNames, sourceAsset, sourceAssetHash,
                    faceTintManifestHash, dependencyId, dependencyHash, expectedHeadParts,
                    expectedLooseAssets, expectedArchives),
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           JsonException or ArgumentException or InvalidDataException or
                                           OverflowException)
        {
            diagnostics.Add(Error("blank-provider-invalid", exception.Message));
            return Refused(diagnostics);
        }
    }

    private static void ValidateTemplateMasters(
        WorkspacePath templatePath,
        FormId templateNpc,
        ImmutableArray<PluginName> declaredMasters)
    {
        var actualMasters = BethesdaNpcCreationAdapter.ReadTemplate(templatePath, templateNpc).Masters;
        if (actualMasters.Length != declaredMasters.Length ||
            !actualMasters.Zip(declaredMasters).All(pair =>
                string.Equals(pair.First.Value, pair.Second.Value,
                    StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "Provider template masters must exactly match the copied template plugin header; " +
                "source-only preset dependencies belong to separate source provenance.");
        }
    }

    private async ValueTask ValidateDependencyManifestAsync(
        WorkspacePath path,
        string expectedId,
        int expectedHeadParts,
        int expectedLooseAssets,
        int expectedArchives,
        ImmutableArray<PluginName> expectedMasters,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(path, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        var root = document.RootElement;
        RequireShape(root, "dependency manifest", "schemaVersion", "id", "headParts", "looseAssets", "archives");
        if (RequiredInt(root, "schemaVersion") != 1 ||
            !string.Equals(RequiredString(root, "id"), expectedId, StringComparison.Ordinal))
            throw new InvalidDataException("Dependency schema or identity does not match the provider bundle.");

        var headParts = RequiredArray(root, "headParts");
        var looseAssets = RequiredArray(root, "looseAssets");
        var archives = RequiredArray(root, "archives");
        if (headParts.GetArrayLength() != expectedHeadParts ||
            looseAssets.GetArrayLength() != expectedLooseAssets ||
            archives.GetArrayLength() != expectedArchives)
            throw new InvalidDataException("Dependency inventory counts do not match the provider bundle.");

        var boundKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in headParts.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireShape(item, "dependency headpart", "formKey", "type", "pluginEvidence", "pluginSha256");
            var formKey = RequiredString(item, "formKey");
            if (!boundKeys.Add("form:" + formKey))
                throw new InvalidDataException($"Duplicate dependency FormKey '{formKey}'.");
            var separator = formKey.LastIndexOf('|');
            if (separator <= 0 || separator == formKey.Length - 1 ||
                !FormId.TryParse("0x" + formKey[(separator + 1)..], out _))
                throw new InvalidDataException($"Dependency FormKey '{formKey}' is invalid.");
            var plugin = formKey[..separator];
            if (!expectedMasters.Any(master => string.Equals(master.Value, plugin, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidDataException($"Dependency headpart plugin '{plugin}' is absent from provider masters.");
            _ = RequiredString(item, "type");
            _ = await VerifyEvidenceAsync(item, "pluginEvidence", "pluginSha256", "headpart provider",
                diagnostics, cancellationToken);
        }

        foreach (var item in looseAssets.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireShape(item, "loose dependency", "gamePath", "provider", "evidencePath", "sha256");
            var gamePath = new AssetPath(RequiredString(item, "gamePath"));
            if (!boundKeys.Add("asset:" + gamePath.Value))
                throw new InvalidDataException($"Duplicate dependency asset '{gamePath.Value}'.");
            _ = RequiredString(item, "provider");
            _ = await VerifyEvidenceAsync(item, "evidencePath", "sha256", "loose provider",
                diagnostics, cancellationToken);
        }

        foreach (var item in archives.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequireShape(item, "archive dependency", "provider", "evidencePath", "sha256", "members");
            _ = RequiredString(item, "provider");
            var archivePath = await VerifyEvidenceAsync(item, "evidencePath", "sha256", "archive provider",
                diagnostics, cancellationToken);
            var members = ReadUniqueStrings(item.GetProperty("members"), "archive members");
            if (members.IsDefaultOrEmpty)
                throw new InvalidDataException("A dependency archive must declare at least one member.");
            foreach (var member in members)
            {
                var asset = new AssetPath(member);
                if (!boundKeys.Add("asset:" + asset.Value))
                    throw new InvalidDataException($"Duplicate dependency asset '{asset.Value}'.");
            }
            await VerifyArchiveMembersAsync(archivePath, members, diagnostics, cancellationToken);
        }
    }

    private async ValueTask<WorkspacePath> VerifyEvidenceAsync(
        JsonElement item,
        string pathProperty,
        string hashProperty,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var path = ResolveRelative(RequiredString(item, pathProperty), role, diagnostics);
        var hash = new Sha256Hash(RequiredString(item, hashProperty));
        await VerifyFileAsync(path, hash, role, diagnostics, cancellationToken);
        return path;
    }

    private async ValueTask VerifyArchiveMembersAsync(
        WorkspacePath archivePath,
        ImmutableArray<string> members,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(archivePath.Value)) return;
        var directory = Path.GetDirectoryName(archivePath.Value);
        if (string.IsNullOrWhiteSpace(directory))
            throw new InvalidDataException("The dependency archive has no containing directory.");

        var index = await assetIndexer.IndexAsync(new AssetIndexRequest(
            GameEdition.SkyrimSpecialEdition, new WorkspacePath(directory)), cancellationToken);
        foreach (var diagnostic in index.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error))
        {
            diagnostics.Add(Error("blank-provider-archive-read-failed", diagnostic.Message));
        }

        var archiveName = Path.GetFileName(archivePath.Value);
        var available = index.Providers
            .Where(item => item.Kind == AssetProviderKind.Archive &&
                           string.Equals(item.Source, archiveName, StringComparison.OrdinalIgnoreCase))
            .Select(item => item.Path.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var member in members)
        {
            var normalized = new AssetPath(member).Value;
            if (!available.Contains(normalized))
                diagnostics.Add(Error("blank-provider-archive-member-missing",
                    $"The bound archive does not contain declared member '{normalized}'."));
        }
    }

    private static async ValueTask ValidateFaceTintBindingAsync(
        WorkspacePath manifest,
        AssetPath expectedSource,
        CancellationToken cancellationToken)
    {
        var bytes = await ReadBoundedAsync(manifest, cancellationToken);
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 32
        });
        var root = document.RootElement;
        var layers = RequiredArray(root, "layers");
        if (layers.GetArrayLength() != 1)
            throw new InvalidDataException("The admitted Gate 1 FaceTint recipe must contain exactly one bound layer.");
        var source = new AssetPath(RequiredString(layers[0], "source"));
        if (source != expectedSource)
            throw new InvalidDataException("FaceTint recipe source does not match the provider bundle.");
    }

    private async ValueTask VerifyFileAsync(
        WorkspacePath path,
        Sha256Hash expected,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, path));
        if (!File.Exists(path.Value))
        {
            diagnostics.Add(Error("blank-provider-file-missing", $"The bound {role} is missing."));
            return;
        }
        await using var stream = File.OpenRead(path.Value);
        var actual = new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
        if (actual != expected)
            diagnostics.Add(Error("blank-provider-file-hash-mismatch",
                $"The bound {role} hash {actual} does not match {expected}."));
    }

    private WorkspacePath ResolveRelative(
        string value,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) || value.Contains(':'))
            throw new InvalidDataException($"The {role} must be a relative workspace path.");
        var path = new WorkspacePath(Path.Combine(labRoot.Value,
            value.Replace('/', Path.DirectorySeparatorChar)));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, path));
        return path;
    }

    private static void Bind(
        string role,
        WorkspacePath expected,
        WorkspacePath actual,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!string.Equals(expected.Value, actual.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("blank-provider-component-mismatch",
                $"The requested {role} is not the path bound by the provider manifest."));
    }

    private static void Bind(
        string role,
        Sha256Hash expected,
        Sha256Hash actual,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (expected != actual)
            diagnostics.Add(Error("blank-provider-component-mismatch",
                $"The requested {role} is not the hash bound by the provider manifest."));
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumManifestBytes)
            throw new InvalidDataException($"Manifest '{path.Value}' is missing, empty, or exceeds 1 MiB.");
        return await File.ReadAllBytesAsync(path.Value, cancellationToken);
    }

    private static void RequireShape(JsonElement element, string role, params string[] required)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The {role} must be an object.");
        var allowed = required.ToHashSet(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
                throw new InvalidDataException($"The {role} contains duplicate property '{property.Name}'.");
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException($"The {role} contains unsupported property '{property.Name}'.");
        }
        var missing = required.Where(name => !seen.Contains(name)).ToArray();
        if (missing.Length > 0)
            throw new InvalidDataException($"The {role} is missing {string.Join(", ", missing)}.");
    }

    private static JsonElement RequiredArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"'{name}' must be an array.");
        return value;
    }

    private static ImmutableArray<string> ReadUniqueStrings(JsonElement array, string role)
    {
        if (array.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"The {role} must be an array.");
        var values = array.EnumerateArray().Select(item =>
            item.ValueKind == JsonValueKind.String ? item.GetString()! :
                throw new InvalidDataException($"The {role} must contain strings."))
            .ToImmutableArray();
        if (values.Any(string.IsNullOrWhiteSpace) ||
            values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
            throw new InvalidDataException($"The {role} must contain unique non-empty values.");
        return values;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"'{name}' must be a non-empty string.");
        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out var result))
            throw new InvalidDataException($"'{name}' must be an integer.");
        return result;
    }

    private static int RequiredNonNegativeInt(JsonElement element, string name)
    {
        var value = RequiredInt(element, name);
        if (value < 0) throw new InvalidDataException($"'{name}' cannot be negative.");
        return value;
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static BlankNpcProviderBindingResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
