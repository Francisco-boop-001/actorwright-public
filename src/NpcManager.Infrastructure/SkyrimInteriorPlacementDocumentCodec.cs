using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Strict wire codec for the closed interior-placement documents.  Paths stay
/// project-relative on the wire and proposal hashes exclude only their own
/// self-reference.
/// </summary>
public static class SkyrimInteriorPlacementDocumentCodec
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        WriteIndented = false,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 32
    };

    public static SkyrimInteriorPlacementRequest ParseRequest(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot)
    {
        JsonElement root = ParseRoot(bytes);
        bool questAlias = IsQuestAlias(root);
        if (questAlias)
            RequireExactMembers(root, "request", "schema", "finishCore", "loadOrder", "placementMode", "marker", "sandboxRadius", "patch", "output");
        else
            RequireExactMembers(root, "request", "schema", "finishCore", "loadOrder", "cell", "transform", "location", "patch", "output");
        SkyrimInteriorPlacementRequest request = Deserialize<SkyrimInteriorPlacementRequest>(root);
        if (!string.Equals(request.Schema, SkyrimInteriorPlacementRequest.SchemaIdentifier, StringComparison.Ordinal))
            throw new InvalidDataException($"Interior placement request schema must be '{SkyrimInteriorPlacementRequest.SchemaIdentifier}'.");
        ValidatePath(request.FinishCore.Manifest, projectRoot, "finishCore.manifest");
        foreach (SkyrimInteriorPlacementProvider provider in request.LoadOrder)
            ValidatePath(provider.Path, projectRoot, "loadOrder.path");
        ValidatePath(request.Output.Root, projectRoot, "output.root");
        ValidatePath(request.Output.Archive, projectRoot, "output.archive");
        ValidateString(request.FinishCore.ManifestSha256, "finishCore.manifestSha256");
        if (questAlias)
        {
            ValidateReference(request.Marker, "marker");
            if (request.SandboxRadius is null or 0)
                throw new InvalidDataException("Quest-alias sandboxRadius must be a positive uint32 radius.");
        }
        else
        {
            ValidateString(request.Cell.ProviderPlugin, "cell.providerPlugin");
            ValidateString(request.Cell.Owner, "cell.owner");
            ValidateString(request.Cell.RawFormId, "cell.rawFormId");
            ValidateString(request.Cell.EditorId, "cell.editorId");
        }
        return request;
    }

    public static SkyrimInteriorPlacementProposal ParseProposal(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot)
    {
        JsonElement root = ParseRoot(bytes);
        RequireModeMembers(root, "proposal", projectRoot, "schema", "requestSha256", "proposalSha256", "status", "patchPlugin", "masterOrder", "cellOwner", "cellRawFormId", "npcOwner", "npcRawFormId", "locationOwner", "locationRawFormId", "cellEditorId", "interiorBlock", "interiorSubBlock", "x", "y", "z", "rotationX", "rotationY", "rotationZ", "conflictContained", "conflictFree", "pathingAuthority", "runtimeAuthority", "corePlugin", "coreNpc", "providerChain", "diagnostics");
        SkyrimInteriorPlacementProposal proposal = Deserialize<SkyrimInteriorPlacementProposal>(root);
        if (!string.Equals(proposal.Schema, SkyrimInteriorPlacementProposal.SchemaIdentifier, StringComparison.Ordinal))
            throw new InvalidDataException("Interior placement proposal schema is not v1.");
        _ = projectRoot;
        return proposal;
    }

    public static SkyrimInteriorPlacementManifest ParseManifest(
        ReadOnlySpan<byte> bytes,
        WorkspacePath projectRoot)
    {
        JsonElement root = ParseRoot(bytes);
        RequireModeMembers(root, "manifest", projectRoot, "schema", "patchPlugin", "patchPath", "patchSha256", "archive", "archiveSha256", "requestSha256", "proposalSha256", "finishCoreManifestSha256", "corePlugin", "coreNpc", "cell", "cellRawFormId", "npcRawFormId", "interiorBlock", "interiorSubBlock", "locationRawFormId", "masterOrder", "placedReference", "conflictContained", "conflictFree", "pathingAuthority", "runtimeAuthority", "visualAuthority", "status");
        SkyrimInteriorPlacementManifest manifest = Deserialize<SkyrimInteriorPlacementManifest>(root);
        if (!string.Equals(manifest.Schema, SkyrimInteriorPlacementManifest.SchemaIdentifier, StringComparison.Ordinal))
            throw new InvalidDataException("Interior placement manifest schema is not v1.");
        ValidatePath(manifest.PatchPath, projectRoot, "manifest.patchPath");
        ValidatePath(manifest.Archive, projectRoot, "manifest.archive");
        return manifest;
    }

    public static SkyrimInteriorPlacementVerification ParseVerification(
        ReadOnlySpan<byte> bytes)
    {
        JsonElement root = ParseRoot(bytes);
        RequireModeMembers(root, "verification", null, "schema", "verified", "status", "patchPlugin", "tes4Count", "cellCount", "achrCount", "hasOnlyEdidCell", "hasPersistentActor", "conflictContained", "runtimeAuthority", "diagnostics");
        SkyrimInteriorPlacementVerification verification = Deserialize<SkyrimInteriorPlacementVerification>(root);
        if (!string.Equals(verification.Schema, SkyrimInteriorPlacementVerification.SchemaIdentifier, StringComparison.Ordinal))
            throw new InvalidDataException("Interior placement verification schema is not v1.");
        return verification;
    }

    public static byte[] SerializeRequest(SkyrimInteriorPlacementRequest request)
    {
        if (request.PlacementMode != SkyrimQuestAliasPlacementEvidence.Mode)
            return Canonicalize(JsonSerializer.SerializeToUtf8Bytes(request, SerializerOptions));
        JsonObject root = JsonSerializer.SerializeToNode(request, SerializerOptions)!.AsObject();
        root.Remove("cell");
        root.Remove("transform");
        root.Remove("location");
        return Canonicalize(JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions));
    }

    public static byte[] SerializeProposal(SkyrimInteriorPlacementProposal proposal) =>
        Canonicalize(JsonSerializer.SerializeToUtf8Bytes(proposal, SerializerOptions));

    public static byte[] SerializeManifest(SkyrimInteriorPlacementManifest manifest) =>
        Canonicalize(JsonSerializer.SerializeToUtf8Bytes(manifest, SerializerOptions));

    public static byte[] SerializeVerification(SkyrimInteriorPlacementVerification verification) =>
        Canonicalize(JsonSerializer.SerializeToUtf8Bytes(verification, SerializerOptions));

    public static Sha256Hash HashRequest(SkyrimInteriorPlacementRequest request) =>
        new(Convert.ToHexString(SHA256.HashData(SerializeRequest(request))));

    public static Sha256Hash HashProposalWithoutSelf(ReadOnlySpan<byte> bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), DocumentOptions);
        JsonNode? node = JsonNode.Parse(document.RootElement.GetRawText());
        if (node is not JsonObject root)
            throw new InvalidDataException("Interior placement proposal must be an object.");
        root.Remove("proposalSha256");
        return new(Convert.ToHexString(SHA256.HashData(Canonicalize(JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions)))));
    }

    public static byte[] RemoveProposalHash(ReadOnlySpan<byte> bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), DocumentOptions);
        JsonNode? node = JsonNode.Parse(document.RootElement.GetRawText());
        if (node is not JsonObject root)
            throw new InvalidDataException("Interior placement proposal must be an object.");
        root.Remove("proposalSha256");
        return Canonicalize(JsonSerializer.SerializeToUtf8Bytes(root, SerializerOptions));
    }

    private static T Deserialize<T>(JsonElement root) =>
        JsonSerializer.Deserialize<T>(root.GetRawText(), SerializerOptions)
        ?? throw new InvalidDataException("Interior placement JSON document was null.");

    private static JsonElement ParseRoot(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(Encoding.UTF8.GetPreamble()))
            throw new InvalidDataException("Interior placement JSON must be UTF-8 without a BOM.");
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), DocumentOptions);
        RejectDuplicateProperties(document.RootElement);
        return document.RootElement.Clone();
    }

    private static void RequireExactMembers(JsonElement element, string context, params string[] expected)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"Interior placement {context} must be an object.");
        HashSet<string> allowed = expected.ToHashSet(StringComparer.Ordinal);
        string[] actual = element.EnumerateObject().Select(property => property.Name).ToArray();
        string[] unknown = actual.Where(name => !allowed.Contains(name)).OrderBy(name => name, StringComparer.Ordinal).ToArray();
        string[] missing = expected.Where(name => !actual.Contains(name, StringComparer.Ordinal)).ToArray();
        if (unknown.Length != 0 || missing.Length != 0)
            throw new InvalidDataException($"Interior placement {context} members are closed; unknown=[{string.Join(',', unknown)}], missing=[{string.Join(',', missing)}].");
    }

    private static void ValidatePath(string value, WorkspacePath projectRoot, string name)
    {
        if (string.IsNullOrWhiteSpace(value) || value.StartsWith('/') || Path.IsPathRooted(value) || value.Contains('\\') || value.Split('/').Any(part => part is "" or "." or ".."))
            throw new InvalidDataException($"Interior placement path '{name}' is not canonical.");
        WorkspacePath resolved = new(Path.GetFullPath(Path.Combine(projectRoot.Value, value.Replace('/', Path.DirectorySeparatorChar))));
        if (!resolved.IsUnder(projectRoot))
            throw new InvalidDataException($"Interior placement path '{name}' escapes the project root.");
    }

    private static void ValidateString(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"Interior placement member '{name}' must be non-empty.");
    }

    private static bool IsQuestAlias(JsonElement root)
    {
        if (!root.TryGetProperty("placementMode", out JsonElement mode))
            return false;
        if (mode.ValueKind != JsonValueKind.String || mode.GetString() != SkyrimQuestAliasPlacementEvidence.Mode)
            throw new InvalidDataException("The only explicit placementMode is 'quest-alias'; omit it for CELL/ACHR placement.");
        return true;
    }

    private static void RequireModeMembers(JsonElement root, string context, WorkspacePath? projectRoot, params string[] legacy)
    {
        bool questAlias = IsQuestAlias(root);
        RequireExactMembers(root, context, questAlias ? [.. legacy, "placementMode", "questAlias"] : legacy);
        if (!questAlias) return;
        JsonElement evidence = root.GetProperty("questAlias");
        RequireExactMembers(evidence, "questAlias", "marker", "markerProvider", "sandboxPackage", "sandboxRadius", "quest", "questRawFormId", "packageRecordSha256", "seqPath", "seqSha256", "questCount", "aliasCount", "packageCount", "startGameEnabled");
        foreach (string field in new[] { "marker", "sandboxPackage", "quest" })
            ValidateReference(evidence.GetProperty(field).GetString(), "questAlias." + field);
        _ = new PluginName(evidence.GetProperty("markerProvider").GetString()!);
        _ = new Sha256Hash(evidence.GetProperty("packageRecordSha256").GetString()!);
        _ = new Sha256Hash(evidence.GetProperty("seqSha256").GetString()!);
        if (evidence.GetProperty("questRawFormId").GetString() is not string questRaw ||
            !FormId.TryParse(questRaw, out FormId questId) || questId.Value == 0 ||
            evidence.GetProperty("sandboxRadius").GetUInt32() == 0 || evidence.GetProperty("questCount").GetInt32() != 1 ||
            evidence.GetProperty("aliasCount").GetInt32() != 2 || evidence.GetProperty("packageCount").GetInt32() != 1 ||
            !evidence.GetProperty("startGameEnabled").GetBoolean())
            throw new InvalidDataException("Quest-alias evidence must bind one start-game quest, two aliases, one package and a positive radius.");
        if (projectRoot is { } boundRoot)
            ValidatePath(evidence.GetProperty("seqPath").GetString()!, boundRoot, "questAlias.seqPath");
    }

    private static void ValidateReference(string? value, string field)
    {
        if (value is null || !FormReference.TryParse(value, out FormReference reference) || reference.ToString() != value)
            throw new InvalidDataException($"{field} must be a canonical plugin|FormID reference.");
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException($"Interior placement JSON contains duplicate member '{property.Name}'.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement child in element.EnumerateArray())
                RejectDuplicateProperties(child);
        }
    }

    private static byte[] Canonicalize(ReadOnlySpan<byte> bytes)
    {
        using JsonDocument document = JsonDocument.Parse(bytes.ToArray(), DocumentOptions);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
            WriteCanonical(document.RootElement, writer);
        return stream.ToArray();
    }

    private static void WriteCanonical(JsonElement element, Utf8JsonWriter writer)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (JsonProperty property in element.EnumerateObject().OrderBy(item => item.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(property.Value, writer);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (JsonElement child in element.EnumerateArray())
                    WriteCanonical(child, writer);
                writer.WriteEndArray();
                break;
            default:
                element.WriteTo(writer);
                break;
        }
    }
}
