using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record NpcVisualPreviewPhysicalArtifact(
    string Role,
    WorkspacePath Path,
    long Size,
    string Sha256);

public sealed class NpcVisualPreviewArtifactDocument : IDisposable
{
    private readonly FaceGeomHairRegionsPinnedDirectory root;
    private readonly List<FaceGeomHairRegionsPinnedReadFile> retained;
    private readonly ImmutableArray<string> outputFiles;
    private bool disposed;

    internal NpcVisualPreviewArtifactDocument(
        NpcVisualPreviewPersistenceDocument value,
        WorkspacePath path,
        long size,
        string sha256,
        byte[] utf8Json,
        ImmutableArray<NpcVisualPreviewPhysicalArtifact> artifacts,
        ImmutableArray<string> outputFiles,
        FaceGeomHairRegionsPinnedDirectory root,
        List<FaceGeomHairRegionsPinnedReadFile> retained)
    {
        Value = value;
        Path = path;
        Size = size;
        Sha256 = sha256;
        Utf8Json = utf8Json.ToImmutableArray();
        Artifacts = artifacts;
        this.outputFiles = outputFiles;
        this.root = root;
        this.retained = retained;
    }

    public NpcVisualPreviewPersistenceDocument Value { get; }

    public WorkspacePath Path { get; }

    public long Size { get; }

    public string Sha256 { get; }

    public ImmutableArray<byte> Utf8Json { get; }

    public ImmutableArray<NpcVisualPreviewPhysicalArtifact> Artifacts { get; }

    public void Revalidate()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        foreach (FaceGeomHairRegionsPinnedReadFile file in retained)
        {
            NpcVisualPreviewPhysicalArtifact artifact = Artifacts.Single(item =>
                string.Equals(
                    item.Path.Value,
                    file.Path,
                    StringComparison.OrdinalIgnoreCase));
            if (file.Length != artifact.Size ||
                !string.Equals(
                    file.ComputeSha256(),
                    artifact.Sha256,
                    StringComparison.Ordinal))
                throw new InvalidDataException(
                    $"The retained {artifact.Role} changed before workflow publication.");
        }
        string[] current = root.EnumerateTree()
            .Where(item => !item.IsDirectory)
            .Select(item => item.Path.Value)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (!current.SequenceEqual(
                outputFiles,
                StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The retained preview output tree changed before workflow publication.");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        foreach (FaceGeomHairRegionsPinnedReadFile file in
                 retained.AsEnumerable().Reverse())
            file.Dispose();
        root.Dispose();
        disposed = true;
    }
}

public sealed class NpcVisualPreviewArtifactReader
{
    private const long MaximumJsonBytes = 4L * 1024 * 1024;
    private const long MaximumImageBytes = 32L * 1024 * 1024;
    private const long MaximumSourceBytes = 512L * 1024 * 1024;
    private const long MaximumAdmissionBytes = 1024L * 1024 * 1024;
    private const int MaximumFiles = 512;
    private static readonly ImmutableHashSet<string> RootProperties =
        ImmutableHashSet.Create(
            StringComparer.Ordinal,
            "schemaVersion", "sceneSchemaVersion", "label",
            "runtimeAuthority", "source", "views", "contactSheetPath",
            "contactSheetSha256", "renderEvidence", "visualEvidence",
            "diagnostics");
    private static readonly JsonSerializerOptions JsonOptions = CreateOptions();
    private readonly FaceGeomHairRegionsPinnedFileSystem fileSystem;

    public NpcVisualPreviewArtifactReader(WorkspacePath workspaceRoot)
    {
        fileSystem = new FaceGeomHairRegionsPinnedFileSystem(workspaceRoot);
    }

    public NpcVisualPreviewArtifactDocument Load(
        WorkspacePath path,
        string expectedSha256) =>
        Load(path, expectedSha256, CancellationToken.None);

    public NpcVisualPreviewArtifactDocument Load(
        WorkspacePath path,
        string expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RequireUpperSha256(expectedSha256, "preview bundle SHA-256");
        string? parent = Path.GetDirectoryName(path.Value);
        if (string.IsNullOrWhiteSpace(parent))
            throw new InvalidDataException(
                "The preview bundle has no output root.");
        var previewRoot = new WorkspacePath(parent);
        FaceGeomHairRegionsPinnedDirectory? root = null;
        var retained = new List<FaceGeomHairRegionsPinnedReadFile>();
        var artifacts = new Dictionary<string, NpcVisualPreviewPhysicalArtifact>(
            StringComparer.OrdinalIgnoreCase);
        long retainedBytes = 0;
        try
        {
            root = fileSystem.OpenDirectory(previewRoot, "preview output root");
            byte[] bundleBytes = Open(
                path,
                expectedSha256,
                MaximumJsonBytes,
                "preview bundle");
            NpcVisualPreviewPersistenceDocument value = Parse(bundleBytes);
            ValidateContract(value);

            WorkspacePath hashManifest = new(Path.Combine(
                previewRoot.Value,
                "npc-preview.hashes.sha256"));
            byte[] hashBytes = OpenUnknownHash(
                hashManifest,
                MaximumJsonBytes,
                "preview hash manifest");
            ImmutableDictionary<string, string> rows = ParseHashes(
                hashBytes,
                previewRoot);
            ImmutableArray<FaceGeomHairRegionsPinnedTreeEntry> tree =
                root.EnumerateTree();
            string[] actualFiles = tree.Where(item => !item.IsDirectory)
                .Select(item => item.Path.Value)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            string[] declaredFiles = rows.Keys
                .Append(hashManifest.Value)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (actualFiles.Length > MaximumFiles ||
                !actualFiles.SequenceEqual(
                    declaredFiles,
                    StringComparer.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    "The preview hash manifest does not declare the exact output tree.");
            foreach ((string declaredPath, string hash) in rows)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _ = Open(
                    new WorkspacePath(declaredPath),
                    hash,
                    MaximumSourceBytes,
                    "preview declared output");
            }

            RequireDeclared(rows, path, expectedSha256, "preview bundle");
            RequireDeclared(
                rows,
                value.ContactSheetPath,
                Hash(value.ContactSheetSha256),
                "preview contact sheet");
            RequirePng(Find(value.ContactSheetPath), "preview contact sheet");
            RequireViews(value, rows);
            foreach (NpcVisualPreviewView view in value.Views)
            {
                RequirePng(Find(view.ImagePath),
                    $"preview view '{view.Id}'", view.Width, view.Height);
                RequirePng(Find(view.RoleMaskPath),
                    $"preview role mask '{view.Id}'", view.Width, view.Height);
            }
            ValidateSource(value.Source);
            foreach (NpcVisualAsset asset in value.Source.Assets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] sourceBytes = Open(
                    asset.MaterializedPath,
                    Hash(asset.Sha256),
                    MaximumSourceBytes,
                    $"preview source asset '{asset.AssetPath.Value}'");
                if (sourceBytes.Length != asset.Bytes)
                    throw new InvalidDataException(
                        $"The preview source asset '{asset.AssetPath.Value}' length changed.");
                if (asset.LowWeightMaterializedPath is { } lowPath &&
                    asset.LowWeightSha256 is { } lowHash)
                    _ = Open(
                        lowPath,
                        Hash(lowHash),
                        MaximumSourceBytes,
                        $"preview low-weight asset '{asset.AssetPath.Value}'");
                foreach (NpcVisualTextureSlot slot in asset.Materials
                             .SelectMany(material => material.TextureSlots))
                    _ = Open(
                        slot.MaterializedPath,
                        Hash(slot.Sha256),
                        MaximumSourceBytes,
                        $"preview texture '{slot.AssetPath.Value}'");
            }
            ValidateRenderEvidence(
                value.Source,
                value.RenderEvidence,
                rows);
            ValidateVisualEvidence(value.VisualEvidence);

            var document = new NpcVisualPreviewArtifactDocument(
                value,
                path,
                bundleBytes.Length,
                expectedSha256,
                bundleBytes,
                artifacts.Values
                    .OrderBy(item => item.Path.Value, StringComparer.Ordinal)
                    .ToImmutableArray(),
                actualFiles.ToImmutableArray(),
                root,
                retained);
            root = null;
            retained = [];
            return document;
        }
        catch
        {
            foreach (FaceGeomHairRegionsPinnedReadFile file in
                     retained.AsEnumerable().Reverse())
                file.Dispose();
            root?.Dispose();
            throw;
        }

        byte[] Open(
            WorkspacePath artifactPath,
            string hash,
            long maximum,
            string role)
        {
            RequireUpperSha256(hash, $"{role} SHA-256");
            if (artifacts.TryGetValue(
                    FaceGeomHairRegionsPinnedFileSystem.Canonical(
                        artifactPath.Value),
                    out NpcVisualPreviewPhysicalArtifact? existing))
            {
                if (!string.Equals(
                        existing.Sha256,
                        hash,
                        StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"The {role} repeats one path with different hashes.");
                return retained.Single(file => string.Equals(
                        file.Path,
                        existing.Path.Value,
                        StringComparison.OrdinalIgnoreCase))
                    .ReadExact(maximum);
            }
            FaceGeomHairRegionsPinnedReadFile file = fileSystem.OpenRead(
                artifactPath,
                role);
            try
            {
                byte[] bytes = file.ReadExact(maximum);
                if (retainedBytes > MaximumAdmissionBytes - bytes.Length)
                    throw new InvalidDataException(
                        "The preview retained-file byte bound was exceeded.");
                string observed = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(observed, hash, StringComparison.Ordinal) ||
                    !string.Equals(
                        file.ComputeSha256(),
                        hash,
                        StringComparison.Ordinal) ||
                    file.Length != bytes.Length)
                    throw new InvalidDataException(
                        $"The {role} changed during its pinned read.");
                retained.Add(file);
                retainedBytes += bytes.Length;
                artifacts.Add(file.Path, new NpcVisualPreviewPhysicalArtifact(
                    role,
                    new WorkspacePath(file.Path),
                    bytes.Length,
                    observed));
                return bytes;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        byte[] OpenUnknownHash(
            WorkspacePath artifactPath,
            long maximum,
            string role)
        {
            FaceGeomHairRegionsPinnedReadFile file = fileSystem.OpenRead(
                artifactPath,
                role);
            try
            {
                byte[] bytes = file.ReadExact(maximum);
                if (retainedBytes > MaximumAdmissionBytes - bytes.Length)
                    throw new InvalidDataException(
                        "The preview retained-file byte bound was exceeded.");
                string observed = Convert.ToHexString(SHA256.HashData(bytes));
                if (!string.Equals(
                        file.ComputeSha256(),
                        observed,
                        StringComparison.Ordinal) ||
                    file.Length != bytes.Length)
                    throw new InvalidDataException(
                        $"The {role} changed during its pinned read.");
                retained.Add(file);
                retainedBytes += bytes.Length;
                artifacts.Add(file.Path, new NpcVisualPreviewPhysicalArtifact(
                    role,
                    new WorkspacePath(file.Path),
                    bytes.Length,
                    observed));
                return bytes;
            }
            catch
            {
                file.Dispose();
                throw;
            }
        }

        byte[] Find(WorkspacePath artifactPath)
        {
            NpcVisualPreviewPhysicalArtifact artifact = artifacts[
                FaceGeomHairRegionsPinnedFileSystem.Canonical(
                    artifactPath.Value)];
            return retained.Single(file => string.Equals(
                    file.Path,
                    artifact.Path.Value,
                    StringComparison.OrdinalIgnoreCase))
                .ReadExact(MaximumImageBytes);
        }
    }

    private static NpcVisualPreviewPersistenceDocument Parse(byte[] bytes)
    {
        using JsonDocument shape = JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        if (shape.RootElement.ValueKind != JsonValueKind.Object ||
            !shape.RootElement.EnumerateObject().Select(item => item.Name)
                .ToImmutableHashSet(StringComparer.Ordinal)
                .SetEquals(RootProperties))
            throw new InvalidDataException(
                "The preview bundle property set is not exact.");
        RequireNoDuplicates(shape.RootElement, "preview bundle");
        try
        {
            NpcVisualPreviewPersistenceDocument value =
                JsonSerializer.Deserialize<NpcVisualPreviewPersistenceDocument>(
                    bytes,
                    JsonOptions) ??
                throw new InvalidDataException(
                    "The preview bundle decoded as empty.");
            byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(
                value,
                JsonOptions);
            if (!bytes.AsSpan().SequenceEqual(canonical))
                throw new InvalidDataException(
                    "The preview bundle bytes are not canonical producer JSON.");
            return value;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or
                NotSupportedException)
        {
            throw new InvalidDataException(
                "The preview bundle does not match the exact producer contract.",
                exception);
        }
    }

    private static void ValidateContract(
        NpcVisualPreviewPersistenceDocument value)
    {
        if (value.Source is null || value.RenderEvidence is null ||
            value.VisualEvidence is null ||
            value.SchemaVersion !=
                NpcVisualPreviewPersistenceContract.BundleSchema ||
            value.SceneSchemaVersion !=
                NpcVisualPreviewPersistenceContract.SceneSchema ||
            value.Label !=
                NpcVisualPreviewPersistenceContract.OffEngineLabel ||
            value.RuntimeAuthority ||
            value.Diagnostics.IsDefault ||
            value.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(
                "The preview bundle contract or authority is invalid.");
    }

    private static void RequireViews(
        NpcVisualPreviewPersistenceDocument value,
        ImmutableDictionary<string, string> rows)
    {
        if (value.Views.IsDefault || value.Views.Length !=
                NpcVisualPreviewPersistenceContract.RequiredViewIds.Length)
            throw new InvalidDataException(
                "The preview must contain exactly six views.");
        for (int index = 0; index < value.Views.Length; index++)
        {
            NpcVisualPreviewView view = value.Views[index];
            if (view.Id !=
                    NpcVisualPreviewPersistenceContract.RequiredViewIds[index] ||
                view.Width != 900 || view.Height != 900)
                throw new InvalidDataException(
                    "The preview view order or dimensions changed.");
            RequireDeclared(rows, view.ImagePath, Hash(view.ImageSha256),
                $"preview view '{view.Id}'");
            RequireDeclared(rows, view.RoleMaskPath,
                Hash(view.RoleMaskSha256),
                $"preview role mask '{view.Id}'");
        }
    }

    private static void ValidateSource(NpcVisualSourceGraph source)
    {
        if (!Enum.IsDefined(source.Route) || !Enum.IsDefined(source.Sex) ||
            !float.IsFinite(source.Weight) || source.Weight is < 0 or > 100 ||
            source.Assets.IsDefaultOrEmpty ||
            source.Assets.Count(item =>
                item.Role == NpcVisualAssetRole.FaceGeom) != 1 ||
            source.Assets.Count(item =>
                item.Role == NpcVisualAssetRole.FaceTint) != 1 ||
            source.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(
                "The preview source graph is incomplete.");
        foreach (NpcVisualAsset asset in source.Assets)
        {
            if (!Enum.IsDefined(asset.Role) || asset.Bytes <= 0 ||
                asset.Bytes > MaximumSourceBytes ||
                string.IsNullOrWhiteSpace(asset.Provider) ||
                asset.Materials.IsDefault ||
                asset.Materials.Any(material =>
                    material is null || material.TextureSlots.IsDefault) ||
                (asset.LowWeightMaterializedPath is null) !=
                    (asset.LowWeightSha256 is null) ||
                (asset.LowWeightMaterializedPath is null) !=
                    (asset.LowWeightAssetPath is null))
                throw new InvalidDataException(
                    "A preview source asset is invalid.");
        }
    }

    private static void ValidateRenderEvidence(
        NpcVisualSourceGraph source,
        NpcVisualPreviewRenderEvidence evidence,
        ImmutableDictionary<string, string> rows)
    {
        if (string.IsNullOrWhiteSpace(evidence.BlenderVersion) ||
            string.IsNullOrWhiteSpace(evidence.RenderEngine) ||
            evidence.FaceGeomImportCount != 1 ||
            !evidence.FaceCameraUsedAuthoritativeGeometry ||
            evidence.FallbackMaterialCount != 0 ||
            evidence.MaterialApplicationCounts is null ||
            evidence.MaterialApplicationCounts.Count == 0 ||
            evidence.RoleMaskPixelCounts is null ||
            !evidence.RoleMaskPixelCounts.TryGetValue(
                "face-front:FaceGeom", out long pixels) || pixels <= 0 ||
            evidence.Meshes.IsDefaultOrEmpty ||
            evidence.Materials.IsDefaultOrEmpty ||
            !evidence.Meshes.Any(mesh =>
                mesh.Role == NpcVisualAssetRole.FaceGeom &&
                string.Equals(
                    mesh.AssetPath.Value,
                    source.Assets.Single(asset =>
                        asset.Role == NpcVisualAssetRole.FaceGeom)
                        .AssetPath.Value,
                    StringComparison.OrdinalIgnoreCase)) ||
            evidence.Meshes.Any(mesh =>
                string.IsNullOrWhiteSpace(mesh.ObjectName) ||
                mesh.VertexCount <= 0 || mesh.WorldTransform.Length != 16) ||
            evidence.Materials.Any(material =>
                string.IsNullOrWhiteSpace(material.ObjectName) ||
                string.IsNullOrWhiteSpace(material.MaterialName) ||
                material.TextureBindings is null ||
                material.LoadedImages.IsDefault ||
                string.IsNullOrWhiteSpace(material.BlendMethod)))
            throw new InvalidDataException(
                "The preview render evidence is incomplete.");
        RequireDeclared(
            rows,
            evidence.StatusPath,
            Hash(evidence.StatusSha256),
            "preview renderer status");
    }

    private static void ValidateVisualEvidence(
        NpcVisualPreviewVisualEvidence evidence)
    {
        if (evidence.DetectedFaceCount is < 0 or > 16 ||
            !double.IsFinite(evidence.DetectorScore) ||
            evidence.DetectorScore is < 0 or > 1 ||
            evidence.LandmarkCount is < 0 or > 478 ||
            evidence.SemanticAnchorCount is < 0 or > 31 ||
            evidence.Diagnostics.Any(item =>
                item.Severity == DiagnosticSeverity.Error))
            throw new InvalidDataException(
                "The preview visual evidence is invalid.");
    }

    private static ImmutableDictionary<string, string> ParseHashes(
        byte[] bytes,
        WorkspacePath root)
    {
        string text = new UTF8Encoding(false, true).GetString(bytes);
        if (text.Contains('\r') ||
            !text.EndsWith('\n'))
            throw new InvalidDataException(
                "The preview hash manifest is not canonical LF UTF-8.");
        var rows = ImmutableDictionary.CreateBuilder<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (string line in text.Split('\n',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.Length <= 66 || line[64] != ' ' || line[65] != ' ')
                throw new InvalidDataException(
                    "A preview hash-manifest row is malformed.");
            string lower = line[..64];
            if (!lower.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'))
                throw new InvalidDataException(
                    "A preview hash-manifest digest is not lowercase SHA-256.");
            var relative = new AssetPath(line[66..]);
            var path = new WorkspacePath(Path.GetFullPath(Path.Combine(
                root.Value,
                relative.Value.Replace('/', Path.DirectorySeparatorChar))));
            if (!path.IsUnder(root) || path == root ||
                !rows.TryAdd(path.Value, lower.ToUpperInvariant()))
                throw new InvalidDataException(
                    "A preview hash-manifest path is duplicate or escaped.");
        }
        return rows.ToImmutable();
    }

    private static void RequireDeclared(
        ImmutableDictionary<string, string> rows,
        WorkspacePath path,
        string hash,
        string role)
    {
        if (!rows.TryGetValue(
                FaceGeomHairRegionsPinnedFileSystem.Canonical(path.Value),
                out string? declared) ||
            !string.Equals(declared, hash, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"The {role} is missing or stale in the hash manifest.");
    }

    private static void RequirePng(
        byte[] bytes,
        string role,
        int? expectedWidth = null,
        int? expectedHeight = null)
    {
        ReadOnlySpan<byte> signature =
            [137, 80, 78, 71, 13, 10, 26, 10];
        if (bytes.Length < signature.Length ||
            !bytes.AsSpan(0, signature.Length).SequenceEqual(signature))
            throw new InvalidDataException(
                $"The {role} is not a PNG image.");
        if (expectedWidth is not null && expectedHeight is not null &&
            (bytes.Length < 24 ||
             !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8) ||
             BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) !=
                checked((uint)expectedWidth.Value) ||
             BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) !=
                checked((uint)expectedHeight.Value)))
            throw new InvalidDataException(
                $"The {role} PNG dimensions differ from its contract.");
    }

    private static void RequireNoDuplicates(JsonElement value, string path)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"The {path} repeats property '{property.Name}'.");
                RequireNoDuplicates(property.Value, $"{path}.{property.Name}");
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (JsonElement item in value.EnumerateArray())
                RequireNoDuplicates(item, $"{path}[{index++}]");
        }
    }

    private static string Hash(Sha256Hash hash) =>
        hash.Value.ToUpperInvariant();

    private static void RequireUpperSha256(string value, string role)
    {
        if (value.Length != 64 || value.Any(character =>
                character is not (>= '0' and <= '9' or >= 'A' and <= 'F')))
            throw new InvalidDataException(
                $"The {role} is not uppercase SHA-256.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = false,
            MaxDepth = 64,
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
            RespectRequiredConstructorParameters = true
        };
        options.Converters.Add(new SingleValueConverter<PluginName>(
            value => new PluginName(value), value => value.Value));
        options.Converters.Add(new SingleValueConverter<WorkspacePath>(
            value => new WorkspacePath(value), value => value.Value));
        options.Converters.Add(new SingleValueConverter<Sha256Hash>(
            value => new Sha256Hash(value), value => value.Value));
        options.Converters.Add(new SingleValueConverter<AssetPath>(
            value => new AssetPath(value), value => value.Value));
        options.Converters.Add(new FormIdConverter());
        NpcVisualPreviewJson.AddCanonicalDictionaryConverters(options);
        return options;
    }

    private sealed class SingleValueConverter<T>(
        Func<string, T> parse,
        Func<T, string> format) : JsonConverter<T>
    {
        public override T Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.PropertyName ||
                reader.GetString() != "value" ||
                !reader.Read() ||
                reader.TokenType != JsonTokenType.String)
                throw new JsonException("Expected exact { value: string } object.");
            string value = reader.GetString()!;
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Single-value object contains extra data.");
            return parse(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            T value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteString("value", format(value));
            writer.WriteEndObject();
        }
    }

    private sealed class FormIdConverter : JsonConverter<FormId>
    {
        public override FormId Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartObject ||
                !reader.Read() || reader.TokenType != JsonTokenType.PropertyName ||
                reader.GetString() != "value" || !reader.Read() ||
                reader.TokenType != JsonTokenType.Number ||
                !reader.TryGetUInt32(out uint value) || !reader.Read() ||
                reader.TokenType != JsonTokenType.EndObject)
                throw new JsonException("Expected exact { value: uint } object.");
            return new FormId(value);
        }

        public override void Write(
            Utf8JsonWriter writer,
            FormId value,
            JsonSerializerOptions options)
        {
            writer.WriteStartObject();
            writer.WriteNumber("value", value.Value);
            writer.WriteEndObject();
        }
    }
}
