using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Assets;

/// <summary>
/// Strict loader for one explicitly named Gate 2 face-bake authority. It reads
/// no provider content itself; all NIF, TRI, and catalog bytes are materialized
/// exclusively through <see cref="ISkyrimAssetContentResolver"/>.
/// </summary>
public sealed partial class SkyrimFaceBakeAuthorityLoader(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot,
    ISkyrimAssetContentResolver contentResolver) : ISkyrimFaceBakeAuthorityLoader
{
    private const long MaximumManifestBytes = 1024 * 1024;
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32
    };

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    internal static byte[] SerializeDerived(SkyrimFaceBakeAuthorityDocumentDto document, WorkspacePath allowedRoot)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (ValidateAndMap(allowedRoot, document, diagnostics) is null || HasErrors(diagnostics))
            throw new InvalidDataException(string.Join("; ", diagnostics.Select(item => item.Message)));
        return JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions);
    }

    public async ValueTask<SkyrimFaceBakeAuthorityLoadResult> LoadAsync(
        SkyrimFaceBakeAuthorityLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        ImmutableArray<Diagnostic>.Builder diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, request.AllowedRoot));
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(request.AllowedRoot, request.ManifestPath));
        if (HasErrors(diagnostics))
        {
            return Refused(SkyrimFaceBakeAuthorityLoadStatus.SecurityRefused, null, diagnostics);
        }

        if (!ValidateManifestPath(request.AllowedRoot, request.ManifestPath, diagnostics))
        {
            return Refused(SkyrimFaceBakeAuthorityLoadStatus.SecurityRefused, null, diagnostics);
        }

        ManifestRead? manifestRead = await ReadManifestAsync(
            request.ManifestPath, diagnostics, cancellationToken);
        if (manifestRead is null)
        {
            return Refused(SkyrimFaceBakeAuthorityLoadStatus.ValidationRefused, null, diagnostics);
        }

        if (manifestRead.Sha256 != request.ExpectedManifestSha256)
        {
            diagnostics.Add(Error("face-bake-authority-manifest-hash-mismatch",
                $"Authority manifest hash {manifestRead.Sha256} does not match {request.ExpectedManifestSha256}."));
            return Refused(SkyrimFaceBakeAuthorityLoadStatus.ValidationRefused,
                manifestRead.Sha256, diagnostics);
        }

        ParsedAuthority? parsed = ParseManifest(request.AllowedRoot,
            manifestRead.Content, diagnostics);
        if (parsed is null || HasErrors(diagnostics))
        {
            return Refused(SkyrimFaceBakeAuthorityLoadStatus.ValidationRefused,
                manifestRead.Sha256, diagnostics);
        }

        SkyrimAssetContentResolutionResult content = await contentResolver.ResolveAsync(
            new SkyrimAssetContentResolutionRequest(request.AllowedRoot,
                parsed.Assets.Select(item => item.Authority).ToImmutableArray()),
            cancellationToken);
        diagnostics.AddRange(content.Diagnostics);
        if (!content.Resolved || HasErrors(diagnostics))
        {
            return Refused(SkyrimFaceBakeAuthorityLoadStatus.ContentRefused,
                manifestRead.Sha256, diagnostics);
        }

        SkyrimFaceBakeAuthority? authority = Materialize(
            parsed, content.Assets, manifestRead.Sha256, diagnostics);
        if (authority is null || HasErrors(diagnostics))
        {
            return Refused(SkyrimFaceBakeAuthorityLoadStatus.ContentRefused,
                manifestRead.Sha256, diagnostics);
        }

        return new SkyrimFaceBakeAuthorityLoadResult(
            SkyrimFaceBakeAuthorityLoadStatus.Loaded,
            authority,
            manifestRead.Sha256,
            diagnostics.ToImmutable());
    }

    private static bool ValidateManifestPath(
        WorkspacePath allowedRoot,
        WorkspacePath manifestPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!manifestPath.IsUnder(allowedRoot) ||
            HasAlternateDataStream(manifestPath.Value) ||
            manifestPath.Value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            manifestPath.Value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            manifestPath.Value.StartsWith(@"\\", StringComparison.Ordinal))
        {
            diagnostics.Add(Error("face-bake-authority-manifest-path-refused",
                "Authority manifest must be an ordinary K-local path under the declared root."));
            return false;
        }

        try
        {
            string current = manifestPath.Value;
            while (true)
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    IsReparsePoint(File.GetAttributes(current)))
                {
                    diagnostics.Add(Error("face-bake-authority-manifest-reparse-refused",
                        "Authority manifest path traverses a reparse point."));
                    return false;
                }

                if (string.Equals(current, allowedRoot.Value, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                string? parent = Path.GetDirectoryName(current);
                if (string.IsNullOrWhiteSpace(parent) ||
                    string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                {
                    diagnostics.Add(Error("face-bake-authority-manifest-path-refused",
                        "Authority manifest could not be proven inside the declared root."));
                    return false;
                }
                current = parent;
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add(Error("face-bake-authority-manifest-path-refused",
                $"Authority manifest path could not be qualified: {exception.Message}"));
            return false;
        }
    }

    private static async ValueTask<ManifestRead?> ReadManifestAsync(
        WorkspacePath manifestPath,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(manifestPath.Value))
        {
            diagnostics.Add(Error("face-bake-authority-manifest-missing",
                "Authority manifest does not exist."));
            return null;
        }

        try
        {
            await using FileStream stream = new(manifestPath.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length <= 0 || stream.Length > MaximumManifestBytes)
            {
                diagnostics.Add(Error("face-bake-authority-manifest-size-invalid",
                    $"Authority manifest must contain 1 to {MaximumManifestBytes} bytes."));
                return null;
            }

            byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
            await stream.ReadExactlyAsync(bytes, cancellationToken);
            if (stream.Length != bytes.LongLength)
            {
                diagnostics.Add(Error("face-bake-authority-manifest-changed",
                    "Authority manifest changed while it was being read."));
                return null;
            }

            Sha256Hash hash = new(Convert.ToHexString(SHA256.HashData(bytes)));
            return new ManifestRead(ImmutableCollectionsMarshal.AsImmutableArray(bytes), hash);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or OverflowException)
        {
            diagnostics.Add(Error("face-bake-authority-manifest-read-failed",
                $"Authority manifest could not be read: {exception.Message}"));
            return null;
        }
    }

    private static ParsedAuthority? ParseManifest(
        WorkspacePath allowedRoot,
        ImmutableArray<byte> bytes,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(bytes.AsMemory(), DocumentOptions);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(Error("face-bake-authority-json-root-invalid",
                    "Authority manifest root must be a JSON object."));
                return null;
            }

            ValidateNoDuplicateProperties(document.RootElement, "$", diagnostics);
            if (HasErrors(diagnostics))
            {
                return null;
            }

            SkyrimFaceBakeAuthorityDocumentDto? dto =
                JsonSerializer.Deserialize<SkyrimFaceBakeAuthorityDocumentDto>(
                    bytes.AsSpan(), SerializerOptions);
            if (dto is null)
            {
                diagnostics.Add(Error("face-bake-authority-json-empty",
                    "Authority manifest did not contain a document."));
                return null;
            }

            return ValidateAndMap(allowedRoot, dto, diagnostics);
        }
        catch (JsonException exception)
        {
            diagnostics.Add(Error("face-bake-authority-json-invalid",
                $"Authority manifest JSON was refused: {exception.Message}"));
            return null;
        }
    }

    private static void ValidateNoDuplicateProperties(
        JsonElement element,
        string path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    diagnostics.Add(Error("face-bake-authority-json-property-duplicate",
                        $"Duplicate JSON property '{property.Name}' at {path}."));
                }
                ValidateNoDuplicateProperties(property.Value, $"{path}.{property.Name}", diagnostics);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
            {
                ValidateNoDuplicateProperties(item, $"{path}[{index}]", diagnostics);
                index++;
            }
        }
    }

    private static bool HasAlternateDataStream(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    private static bool IsReparsePoint(FileAttributes attributes) =>
        attributes.HasFlag(FileAttributes.ReparsePoint);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimFaceBakeAuthorityLoadResult Refused(
        SkyrimFaceBakeAuthorityLoadStatus status,
        Sha256Hash? actualManifestSha256,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(status, null, actualManifestSha256, diagnostics.ToImmutable());

    private sealed record ManifestRead(ImmutableArray<byte> Content, Sha256Hash Sha256);
}
