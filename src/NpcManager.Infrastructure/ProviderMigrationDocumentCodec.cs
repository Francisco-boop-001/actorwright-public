using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed record ProviderMigrationPlan(
    ProviderMigrationReviewDocument Review,
    ImmutableDictionary<string, ImmutableArray<byte>> Files);

public sealed class ProviderMigrationDocumentCodec(
    WorkspacePath workspaceRoot)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public ProviderMigrationPlan CreatePlan(
        WorkspacePath sourceRequest,
        Sha256Hash sourceRequestSha256,
        ReadOnlySpan<byte> sourceRequestBytes,
        WorkspacePath sourceBundle,
        Sha256Hash sourceBundleSha256,
        ReadOnlySpan<byte> sourceBundleBytes,
        ProductFixtureBundleReference productFixture,
        WorkspacePath migratedRoot)
    {
        if (!migratedRoot.IsUnder(workspaceRoot) ||
            migratedRoot == workspaceRoot)
            throw new InvalidDataException(
                "The migrated request root must be below the admitted workspace.");

        JsonObject request = ParseObject(sourceRequestBytes, "source request");
        JsonObject bundle = ParseObject(sourceBundleBytes, "source preset bundle");
        JsonObject provider = request["providerContext"]?.AsObject() ??
            throw new InvalidDataException(
                "The legacy request has no providerContext object.");
        var declared = provider
            .Where(item => item.Key != "productFixtureBundle")
            .Select(item => new ProviderMigrationDeclaredField(
                "providerContext." + item.Key,
                item.Value?.ToJsonString() ?? "null"))
            .OrderBy(item => item.Field, StringComparer.Ordinal)
            .ToImmutableArray();
        string fingerprint = Hash(Encoding.UTF8.GetBytes(string.Join('\n',
            declared.Select(item => $"{item.Field}={item.DeclaredValue}")))).Value;

        JsonObject migratedBundle = bundle.DeepClone().AsObject();
        migratedBundle["schemaVersion"] = 3;
        migratedBundle.Remove("providerContext");
        migratedBundle["providerAuthority"] = new JsonObject
        {
            ["kind"] = "product-fixture",
            ["bundleId"] = productFixture.BundleId,
            ["registryManifestSha256"] =
                productFixture.RegistryManifestSha256.Value
        };
        byte[] bundleBytes = Serialize(migratedBundle);
        Sha256Hash bundleHash = Hash(bundleBytes);

        JsonObject migratedRequest = request.DeepClone().AsObject();
        migratedRequest["schemaVersion"] = 3;
        bool removedExistingNpcTarget =
            migratedRequest.Remove("existingNpcTarget");
        JsonObject migratedProvider =
            migratedRequest["providerContext"]!.AsObject();
        string? templateForm = migratedProvider["templateNpcFormId"]?
            .GetValue<string>();
        migratedProvider.Clear();
        migratedProvider["templateNpcFormId"] = templateForm;
        migratedProvider["productFixtureBundle"] = new JsonObject
        {
            ["bundleId"] = productFixture.BundleId,
            ["registryManifestSha256"] =
                productFixture.RegistryManifestSha256.Value
        };
        JsonObject presetBundle =
            migratedRequest["presetBundle"]?.AsObject() ??
            throw new InvalidDataException(
                "The legacy request has no presetBundle object.");
        var migratedBundlePath = new WorkspacePath(Path.Combine(
            migratedRoot.Value, "preset-bundle.json"));
        presetBundle["manifestPath"] = Relative(migratedBundlePath);
        presetBundle["manifestSha256"] = bundleHash.Value;
        byte[] requestBytes = Serialize(migratedRequest);
        Sha256Hash requestHash = Hash(requestBytes);

        JsonObject receipt = new()
        {
            ["schema"] = ProviderMigrationSchemas.Receipt,
            ["sourceRequestSha256"] = sourceRequestSha256.Value,
            ["sourcePresetBundleSha256"] = sourceBundleSha256.Value,
            ["bundleId"] = productFixture.BundleId,
            ["registryManifestSha256"] =
                productFixture.RegistryManifestSha256.Value,
            ["npcRequestSha256"] = requestHash.Value,
            ["presetBundleSha256"] = bundleHash.Value,
            ["buildAuthority"] = false,
            ["runtimeAuthority"] = false
        };
        byte[] receiptBytes = Serialize(receipt);
        Sha256Hash receiptHash = Hash(receiptBytes);

        var files = ImmutableDictionary.CreateBuilder<string,
            ImmutableArray<byte>>(StringComparer.Ordinal);
        files.Add("npc-request.json", requestBytes.ToImmutableArray());
        files.Add("preset-bundle.json", bundleBytes.ToImmutableArray());
        files.Add("migration-receipt.json", receiptBytes.ToImmutableArray());
        ImmutableArray<ProviderMigrationPlannedFile> planned =
        [
            new("npc-request.json", requestHash, requestBytes.LongLength),
            new("preset-bundle.json", bundleHash, bundleBytes.LongLength),
            new("migration-receipt.json", receiptHash,
                receiptBytes.LongLength)
        ];
        var changesBuilder =
            ImmutableArray.CreateBuilder<ProviderMigrationChange>(
                removedExistingNpcTarget ? 5 : 4);
        changesBuilder.Add(new(
            "schemaVersion", request["schemaVersion"]?.ToJsonString() ??
                "null", "3"));
        if (removedExistingNpcTarget)
        {
            changesBuilder.Add(new(
                "existingNpcTarget",
                request["existingNpcTarget"]?.ToJsonString() ?? "null",
                "omitted for schemaVersion 3 product-fixture request"));
        }
        changesBuilder.Add(new("providerContext", provider.ToJsonString(),
            migratedProvider.ToJsonString()));
        changesBuilder.Add(new("presetBundle.manifestPath",
            sourceBundle.Value, Relative(migratedBundlePath)));
        changesBuilder.Add(new("presetBundle.manifestSha256",
            sourceBundleSha256.Value, bundleHash.Value));
        ImmutableArray<ProviderMigrationChange> changes =
            changesBuilder.ToImmutable();
        var artifact = new ProviderMigrationReviewArtifact(
            ProviderMigrationSchemas.Review,
            sourceRequest,
            sourceRequestSha256,
            sourceBundle,
            sourceBundleSha256,
            declared,
            fingerprint,
            productFixture,
            changes,
            migratedRoot,
            planned,
            $"{BuildInfo.ProductName}/{BuildInfo.ProductVersion}/{BuildInfo.SourceLine}",
            BuildAuthority: false,
            RuntimeAuthority: false);
        byte[] reviewBytes = JsonSerializer.SerializeToUtf8Bytes(
            artifact, JsonOptions);
        var review = new ProviderMigrationReviewDocument(
            artifact,
            reviewBytes.ToImmutableArray(),
            Hash(reviewBytes));
        return new ProviderMigrationPlan(review, files.ToImmutable());
    }

    public static ProviderMigrationReviewDocument DecodeReview(
        ReadOnlySpan<byte> bytes,
        Sha256Hash expectedSha256)
    {
        byte[] source = bytes.ToArray();
        if (Hash(source) != expectedSha256)
            throw new InvalidDataException(
                "The reviewed provider migration hash does not match its bytes.");
        using JsonDocument parsed = JsonDocument.Parse(source);
        RejectDuplicates(parsed.RootElement);
        ProviderMigrationReviewArtifact value =
            JsonSerializer.Deserialize<ProviderMigrationReviewArtifact>(
                source, JsonOptions) ??
            throw new InvalidDataException(
                "The provider migration review deserialized to null.");
        return new ProviderMigrationReviewDocument(
            value, source.ToImmutableArray(), expectedSha256);
    }

    private string Relative(WorkspacePath path) =>
        new AssetPath(Path.GetRelativePath(workspaceRoot.Value, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')).Value;

    private static JsonObject ParseObject(
        ReadOnlySpan<byte> bytes,
        string role)
    {
        JsonNode? node = JsonNode.Parse(bytes);
        return node as JsonObject ?? throw new InvalidDataException(
            $"The {role} must contain one JSON object.");
    }

    private static byte[] Serialize(JsonNode node) =>
        Encoding.UTF8.GetBytes(node.ToJsonString(JsonOptions));

    private static void RejectDuplicates(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate JSON property '{property.Name}'.");
                RejectDuplicates(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in element.EnumerateArray())
                RejectDuplicates(item);
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
