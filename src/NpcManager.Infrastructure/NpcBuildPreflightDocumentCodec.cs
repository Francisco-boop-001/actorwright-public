using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class NpcBuildPreflightDocumentCodec :
    INpcBuildPreflightDocumentCodec
{
    private const int MaximumBytes = 8 * 1024 * 1024;
    private const string Role = "NPC build preflight document";
    private readonly FaceGeomHairRegionsWorkspaceBoundary boundary;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters =
        {
            new WorkspacePathJsonConverter(),
            new Sha256HashJsonConverter(),
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    public NpcBuildPreflightDocumentCodec(WorkspacePath workspaceRoot)
    {
        boundary = new FaceGeomHairRegionsWorkspaceBoundary(workspaceRoot);
    }

    internal NpcBuildPreflightDocumentCodec(
        WorkspacePath workspaceRoot,
        IFaceGeomHairRegionsPinnedFileSystemHooks hooks)
    {
        boundary = new FaceGeomHairRegionsWorkspaceBoundary(
            workspaceRoot,
            hooks);
    }

    public NpcBuildPreflightDocument Encode(
        NpcBuildPreflightArtifact artifact)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        Validate(artifact);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(
            artifact, JsonOptions);
        return new NpcBuildPreflightDocument(
            artifact,
            bytes.ToImmutableArray(),
            Hash(bytes));
    }

    public async ValueTask<NpcBuildPreflightDocument> WriteNewAsync(
        NpcBuildPreflightDocument document,
        WorkspacePath output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(document);
        cancellationToken.ThrowIfCancellationRequested();
        boundary.RequireNewFile(output, Role);
        string parent = Path.GetDirectoryName(output.Value) ??
            throw new InvalidDataException(
                "The preflight output has no parent directory.");
        var temporary = new WorkspacePath(Path.Combine(
            parent,
            $".npc-build-preflight-{Guid.NewGuid():N}.tmp"));
        FaceGeomHairRegionsOwnedFile? owned = null;
        bool promoted = false;
        try
        {
            owned = boundary.CreateOwnedFile(
                temporary,
                output,
                Role);
            byte[] staged = await owned.WriteAndReadbackAsync(
                document.Utf8Json.AsMemory(),
                MaximumBytes,
                cancellationToken);
            if (!staged.AsSpan().SequenceEqual(document.Utf8Json.AsSpan()) ||
                Hash(staged) != document.Sha256)
                throw new InvalidDataException(
                    "The staged preflight document failed exact readback.");
            _ = ParseExact(staged, output);

            cancellationToken.ThrowIfCancellationRequested();
            owned.PromoteNoOverwrite();
            promoted = true;

            byte[] retained = await owned.ReadbackExactAsync(
                MaximumBytes,
                CancellationToken.None);
            if (!retained.AsSpan().SequenceEqual(document.Utf8Json.AsSpan()) ||
                Hash(retained) != document.Sha256)
                throw new InvalidDataException(
                    "The promoted preflight document changed during retained readback.");
            _ = ParseExact(retained, output);
            owned.Dispose();
            owned = null;

            NpcBuildPreflightDocument reopened = await ReadExactAsync(
                output, document.Sha256, CancellationToken.None);
            if (!reopened.Utf8Json.AsSpan().SequenceEqual(
                    document.Utf8Json.AsSpan()))
                throw new InvalidDataException(
                    "The promoted preflight document changed during publication.");
            return reopened with { Path = output };
        }
        catch (OperationCanceledException exception) when (!promoted)
        {
            CleanupOrThrow(owned, exception);
            throw;
        }
        catch (Exception exception) when (!promoted &&
                                          exception is ArgumentException or
                                              InvalidDataException or
                                              IOException or
                                              UnauthorizedAccessException or
                                              JsonException or
                                              NotSupportedException)
        {
            CleanupOrThrow(owned, exception);
            throw;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    public async ValueTask<NpcBuildPreflightDocument> ReadExactAsync(
        WorkspacePath path,
        Sha256Hash expectedSha256,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await boundary.ReadExactFileAsync(
            path,
            MaximumBytes,
            Role,
            cancellationToken);
        if (Hash(bytes) != expectedSha256)
            throw new InvalidDataException(
                "The reviewed preflight source hash does not match.");
        return ParseExact(bytes, path);
    }

    private NpcBuildPreflightDocument ParseExact(
        byte[] bytes,
        WorkspacePath path)
    {
        using JsonDocument parsed = JsonDocument.Parse(bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 64
            });
        EnsureNoDuplicates(parsed.RootElement, "$");
        NpcBuildPreflightArtifact artifact =
            JsonSerializer.Deserialize<NpcBuildPreflightArtifact>(
                bytes, JsonOptions) ??
            throw new InvalidDataException(
                "The reviewed preflight document is empty.");
        Validate(artifact);
        NpcBuildPreflightDocument canonical = Encode(artifact);
        if (!canonical.Utf8Json.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException(
                "The reviewed preflight document is not canonical Actorwright JSON.");
        return canonical with { Path = path };
    }

    private static void CleanupOrThrow(
        FaceGeomHairRegionsOwnedFile? owned,
        Exception primary)
    {
        if (owned is null)
            return;
        if (!owned.TryDelete(out string? cleanupFailure))
            throw new IOException(
                "The NPC build preflight transaction failed and its exact owned temporary could not be removed: " +
                cleanupFailure,
                primary);
    }

    private static void Validate(NpcBuildPreflightArtifact artifact)
    {
        if (!string.Equals(artifact.Schema,
                NpcBuildPreflightSchemas.Artifact, StringComparison.Ordinal) ||
            artifact.DerivationVersion !=
                NpcBuildPreflightSchemas.DerivationVersion ||
            artifact.RuntimeAuthority ||
            artifact.Authorities.IsDefault || artifact.HeadParts.IsDefault ||
            artifact.Appearance.IsDefault ||
            artifact.FinalDependencyClosure.IsDefault ||
            artifact.RequiredGates.IsDefault ||
            artifact.OptionalPreview.IsDefault ||
            artifact.PlannedOutputs.IsDefault ||
            (artifact.DependencyClosure is { } closure &&
             (closure.IsDefault || closure.Any(item =>
                 item is null || string.IsNullOrWhiteSpace(item.Kind) ||
                 string.IsNullOrWhiteSpace(item.Path) ||
                 item.Status is not ("present" or "missing" or "nonCanonicalPath") ||
                 item.Disposition is not (null or "platformProvided" or "optionalUnavailable")) ||
              (artifact.ReadyForBuild && closure.Any(item => item.Status != "present" &&
                  item.Disposition is not ("platformProvided" or "optionalUnavailable"))))) ||
            artifact.ReadyForBuild != artifact.RequiredGates.All(item =>
                item.Required && item.Passed) ||
            artifact.PreviewReady != artifact.OptionalPreview.All(item =>
                !item.Required && item.Passed))
            throw new InvalidDataException(
                "The NPC build preflight artifact is internally inconsistent.");
    }

    private static void EnsureNoDuplicates(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate property '{path}.{property.Name}'.");
                EnsureNoDuplicates(property.Value,
                    $"{path}.{property.Name}");
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            int index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                EnsureNoDuplicates(item, $"{path}[{index++}]");
        }
    }

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private sealed class WorkspacePathJsonConverter :
        JsonConverter<WorkspacePath>
    {
        public override WorkspacePath Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String &&
            reader.GetString() is { } value
                ? new WorkspacePath(value)
                : throw new JsonException(
                    "Workspace paths must be absolute strings.");

        public override void Write(
            Utf8JsonWriter writer,
            WorkspacePath value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }

    private sealed class Sha256HashJsonConverter :
        JsonConverter<Sha256Hash>
    {
        public override Sha256Hash Read(
            ref Utf8JsonReader reader,
            Type typeToConvert,
            JsonSerializerOptions options) =>
            reader.TokenType == JsonTokenType.String &&
            reader.GetString() is { } value
                ? new Sha256Hash(value)
                : throw new JsonException(
                    "SHA-256 values must be strings.");

        public override void Write(
            Utf8JsonWriter writer,
            Sha256Hash value,
            JsonSerializerOptions options) =>
            writer.WriteStringValue(value.Value);
    }
}
