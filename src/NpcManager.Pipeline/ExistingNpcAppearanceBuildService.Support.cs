using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class ExistingNpcAppearanceBuildService
{
    private const int MaximumTransitiveAssets = 4_096;
    private const long MaximumInputAssetBytes = 512L * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private ImmutableArray<Diagnostic> Validate(
        ExistingNpcAppearanceBuildRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("existing-npc-build-edition",
                "Existing-NPC appearance packages support Skyrim SE/AE only."));
        if (!request.SourcePlugin.IsUnder(labRoot) ||
            !request.OutputRoot.IsUnder(labRoot) ||
            !request.FaceGeomSource.SourceNif.IsUnder(labRoot) ||
            !request.FaceTintSource.SourceDds.IsUnder(labRoot))
        {
            diagnostics.Add(Error("existing-npc-build-outside-lab",
                "Source, FaceGen, and output paths must remain under the K-only workspace."));
        }
        if (!File.Exists(request.SourcePlugin.Value))
            diagnostics.Add(Error("existing-npc-build-source-missing",
                "The source plugin does not exist."));
        if (request.TargetFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("existing-npc-build-form-id",
                "The source-owned NPC requires a nonzero plugin-local 24-bit FormID."));
        if (Directory.Exists(request.OutputRoot.Value) ||
            File.Exists(request.OutputRoot.Value))
        {
            diagnostics.Add(Error("existing-npc-build-output-exists",
                "The package output root must not already exist."));
        }
        if (string.Equals(
                request.OutputPlugin.Value,
                Path.GetFileName(request.SourcePlugin.Value),
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("existing-npc-build-plugin-name-collision",
                "The override ESP must have a different filename from the source plugin."));
        }
        if (!request.OutputPlugin.Value.EndsWith(
                ".esp",
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("existing-npc-build-output-extension",
                "The existing-NPC override must be an ordinary .esp."));
        }
        if (request.FaceTintSource.Width <= 0 ||
            request.FaceTintSource.Height <= 0)
        {
            diagnostics.Add(Error("existing-npc-build-facetint-dimensions",
                "FaceTint dimensions must be positive."));
        }

        var parent = Directory.GetParent(request.OutputRoot.Value)?.FullName;
        if (string.IsNullOrWhiteSpace(parent) || !Directory.Exists(parent))
            diagnostics.Add(Error("existing-npc-build-output-parent",
                "The package output parent must already exist."));
        diagnostics.AddRange(policy.Evaluate(labRoot, request.OutputRoot));

        var sourceOwner = Path.GetFileName(request.SourcePlugin.Value);
        try
        {
            _ = new PluginName(sourceOwner);
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error("existing-npc-build-source-name", exception.Message));
        }

        ValidateOrdinaryInput(
            request.FaceGeomSource.SourceNif,
            "FaceGeom source",
            diagnostics);
        ValidateOrdinaryInput(
            request.FaceTintSource.SourceDds,
            "FaceTint source",
            diagnostics);
        if (request.TransitivePackageAssets.IsDefault ||
            request.TransitivePackageAssets.Length > MaximumTransitiveAssets)
        {
            diagnostics.Add(Error("existing-npc-build-asset-count",
                $"Transitive package assets must be initialized and contain at most {MaximumTransitiveAssets} files."));
            return diagnostics.ToImmutable();
        }
        if (request.BodyMorphs.IsDefault)
        {
            diagnostics.Add(Error("existing-npc-build-body-morphs-default",
                "The BodyGen morph collection must be initialized, even when empty."));
        }

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var asset in request.TransitivePackageAssets)
        {
            if (!asset.Source.IsUnder(labRoot))
            {
                diagnostics.Add(Error("existing-npc-build-asset-outside-lab",
                    $"Transitive source '{asset.Source.Value}' is outside the K-only workspace."));
            }
            ValidateOrdinaryInput(asset.Source, "transitive source", diagnostics);
            if (string.IsNullOrWhiteSpace(asset.Destination.Value) ||
                Path.IsPathRooted(asset.Destination.Value) ||
                !destinations.Add(asset.Destination.Value))
            {
                diagnostics.Add(Error("existing-npc-build-asset-destination",
                    $"Transitive destination '{asset.Destination.Value}' is unsafe or duplicated."));
            }
        }
        return diagnostics.ToImmutable();
    }

    private void ValidateOrdinaryInput(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, path));
        try
        {
            var info = new FileInfo(path.Value);
            if (!info.Exists || info.Length <= 0 ||
                info.Length > MaximumInputAssetBytes ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("existing-npc-build-input-file",
                    $"The {role} must be an ordinary non-empty K-local file no larger than 512 MiB."));
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("existing-npc-build-input-read",
                $"The {role} could not be inspected safely: {exception.Message}"));
        }
    }

    private static BuildPaths CreatePaths(
        ExistingNpcAppearanceBuildRequest request)
    {
        var sourceOwner = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var localFormId = request.TargetFormId.Value.ToString("X8",
            System.Globalization.CultureInfo.InvariantCulture);
        var data = Path.Combine(request.OutputRoot.Value, "Data");
        var evidence = Path.Combine(request.OutputRoot.Value, "evidence");
        var plugin = new WorkspacePath(Path.Combine(data, request.OutputPlugin.Value));
        var faceGeom = new WorkspacePath(Path.Combine(
            data,
            "meshes",
            "actors",
            "character",
            "FaceGenData",
            "FaceGeom",
            sourceOwner.Value,
            localFormId + ".nif"));
        var faceTint = new WorkspacePath(Path.Combine(
            data,
            "textures",
            "actors",
            "character",
            "FaceGenData",
            "FaceTint",
            sourceOwner.Value,
            localFormId + ".dds"));
        var transitive = request.TransitivePackageAssets
            .OrderBy(item => item.Destination.Value, StringComparer.OrdinalIgnoreCase)
            .Select((item, index) => new TransitivePath(
                $"transitive-package-asset-{index:D4}",
                item,
                new WorkspacePath(Path.Combine(
                    data,
                    item.Destination.Value.Replace(
                        '/', Path.DirectorySeparatorChar)))))
            .ToImmutableArray();

        var reservedDataPaths = new[] { plugin, faceGeom, faceTint }
            .Select(item => Path.GetFullPath(item.Value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!request.BodyMorphs.IsDefaultOrEmpty)
        {
            reservedDataPaths.Add(Path.GetFullPath(Path.Combine(
                data,
                "meshes",
                "actors",
                "character",
                "BodyGenData",
                sourceOwner.Value,
                "templates.ini")));
            reservedDataPaths.Add(Path.GetFullPath(Path.Combine(
                data,
                "meshes",
                "actors",
                "character",
                "BodyGenData",
                sourceOwner.Value,
                "morphs.ini")));
        }
        if (transitive.Any(item =>
                !item.Destination.IsUnder(new WorkspacePath(data)) ||
                reservedDataPaths.Contains(Path.GetFullPath(item.Destination.Value))))
        {
            throw new InvalidDataException(
                "A transitive package destination escaped Data or collides with a product-owned artifact.");
        }

        var bodyGenDirectories = request.BodyMorphs.IsDefaultOrEmpty
            ? Enumerable.Empty<string>()
            :
            [
                Path.Combine(
                    data,
                    "meshes",
                    "actors",
                    "character",
                    "BodyGenData",
                    sourceOwner.Value)
            ];
        var requiredDirectories = new[]
            {
                data,
                evidence,
                Path.GetDirectoryName(faceGeom.Value)!,
                Path.GetDirectoryName(faceTint.Value)!
            }
            .Concat(bodyGenDirectories)
            .Concat(transitive.Select(item =>
                Path.GetDirectoryName(item.Destination.Value)!))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        return new BuildPaths(
            sourceOwner,
            plugin,
            faceGeom,
            faceTint,
            new AssetPath(
                $"textures/actors/character/FaceGenData/FaceTint/{sourceOwner.Value}/{localFormId}.dds"),
            new WorkspacePath(Path.Combine(
                evidence, "npc-appearance-override-proposal.json")),
            new WorkspacePath(Path.Combine(
                evidence, "facegeom-carrier-materialization.json")),
            new WorkspacePath(Path.Combine(
                evidence, "facetint-exact-source.json")),
            new WorkspacePath(Path.Combine(
                evidence, "runtime-test-instructions.json")),
            new WorkspacePath(Path.Combine(
                request.OutputRoot.Value, "npcmanager-package.json")),
            transitive,
            requiredDirectories);
    }

    private static async ValueTask<Sha256Hash> WriteJsonAsync<T>(
        WorkspacePath destination,
        T value,
        CancellationToken cancellationToken)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination.Value, overwrite: false);
            return new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async ValueTask CopyHashBoundAsync(
        WorkspacePath source,
        Sha256Hash expected,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        var actual = await HashFileAsync(source, cancellationToken);
        if (actual != expected)
            throw new InvalidDataException(
                $"Source '{source.Value}' has SHA-256 {actual}; expected {expected}.");
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using var input = new FileStream(
                source.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await using (var output = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(output, cancellationToken);
                await output.FlushAsync(cancellationToken);
                output.Flush(flushToDisk: true);
            }
            if (await HashFileAsync(new WorkspacePath(temporary), cancellationToken) != expected)
                throw new InvalidDataException(
                    $"Source '{source.Value}' changed while it was materialized.");
            File.Move(temporary, destination.Value, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static async ValueTask<ImmutableArray<PresetToNpcPackageArtifact>>
        BuildInventoryAsync(
            BuildPaths paths,
            BodyGenBuildResult? bodyGen,
            CancellationToken cancellationToken)
    {
        var fixedPaths = new[]
        {
            ("plugin", paths.Plugin),
            ("facegeom", paths.FaceGeom),
            ("facetint", paths.FaceTint),
            ("npc-appearance-override-proposal", paths.Proposal),
            ("facegeom-carrier-materialization", paths.FaceGeomEvidence),
            ("facetint-exact-source", paths.FaceTintEvidence),
            ("runtime-kit", paths.RuntimeKit)
        };
        var artifacts = fixedPaths
            .Concat(paths.TransitiveAssets.Select(item =>
                (item.Kind, item.Destination)))
            .Concat(bodyGen is { Written: true }
                ? bodyGen.Files.Select((item, index) =>
                    ($"bodygen-{index:D2}", item.AbsolutePath))
                : []);
        var rows = ImmutableArray.CreateBuilder<PresetToNpcPackageArtifact>();
        var packageRoot = new WorkspacePath(Path.GetDirectoryName(paths.Manifest.Value)!);
        foreach (var (kind, path) in artifacts)
        {
            var info = new FileInfo(path.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > int.MaxValue)
                throw new IOException(
                    $"Package artifact '{path.Value}' is missing, empty, or too large.");
            rows.Add(new PresetToNpcPackageArtifact(
                kind,
                new AssetPath(Path.GetRelativePath(packageRoot.Value, path.Value)),
                checked((int)info.Length),
                await HashFileAsync(path, cancellationToken)));
        }
        return rows.ToImmutable();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private sealed record BuildPaths(
        PluginName SourceOwner,
        WorkspacePath Plugin,
        WorkspacePath FaceGeom,
        WorkspacePath FaceTint,
        AssetPath FaceTintAssetPath,
        WorkspacePath Proposal,
        WorkspacePath FaceGeomEvidence,
        WorkspacePath FaceTintEvidence,
        WorkspacePath RuntimeKit,
        WorkspacePath Manifest,
        ImmutableArray<TransitivePath> TransitiveAssets,
        ImmutableArray<string> RequiredDirectories);

    private sealed record TransitivePath(
        string Kind,
        BlankNpcTransitivePackageAsset Source,
        WorkspacePath Destination);

    private sealed record ExactFaceTintEvidence(
        string SchemaVersion,
        string ArtifactKind,
        string SourceSha256,
        string OutputDds,
        string OutputSha256,
        int Width,
        int Height,
        bool ByteExact,
        bool RuntimeAuthority);
}
