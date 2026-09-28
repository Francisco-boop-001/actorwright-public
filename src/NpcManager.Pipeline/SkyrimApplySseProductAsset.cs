using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Loads the immutable Skyrim runtime script shipped inside this assembly,
/// verifies its build/inspection authority, and stages the exact PEX bytes.
/// End users never need Caprica and callers cannot select a different script.
/// </summary>
internal static class SkyrimApplySseProductAsset
{
    private const string ManifestResourceName =
        "NpcManager.Pipeline.Runtime.SkyrimSE.ApplySse.Manifest";
    private const string PexResourceName =
        "NpcManager.Pipeline.Runtime.SkyrimSE.ApplySse.Pex";
    private const string ExpectedManifestSha256 =
        "7706009FBB888C64B4315F6608A6451E0812AEBBEFD8C0AF8871AF769F33F45D";
    private const string ExpectedPexSha256 =
        "993D994391357233AED8E7EDC8E3C1DD2BD7A06616A0E209BB9B3169BDF61332";
    private const string ExpectedPscSha256 =
        "5D4550FA23476D9089675CC56B0616C9541B52EF00195072C084776E5BB612D4";
    private const string ExpectedInspectionSha256 =
        "D7E083714E668B74456304168EDBE4726C3BF3C057B7BB3B939D89E3A7F55B9C";
    private const int ExpectedPexSize = 10_426;
    private const int MaximumManifestBytes = 64 * 1024;

    internal static AssetPath Destination { get; } =
        new("Scripts/NPCM_Manolov_ApplySSE.pex");

    internal static bool IsReservedDestination(AssetPath destination) =>
        string.Equals(destination.Value, Destination.Value,
            StringComparison.OrdinalIgnoreCase);

    internal static async ValueTask<BlankNpcTransitivePackageAsset> MaterializeAsync(
        WorkspacePath stagingRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await LoadAndValidateAsync(cancellationToken);
        var root = new DirectoryInfo(stagingRoot.Value);
        if (!root.Exists || root.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                "The product runtime script requires a present, ordinary service-owned staging root.");

        var data = Directory.CreateDirectory(Path.Combine(stagingRoot.Value, "Data"));
        var scripts = Directory.CreateDirectory(Path.Combine(data.FullName, "Scripts"));
        if (data.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            scripts.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                "The product runtime script staging ancestry may not contain reparse points.");

        var destinationPath = new WorkspacePath(Path.Combine(
            stagingRoot.Value,
            "Data",
            Destination.Value.Replace('/', Path.DirectorySeparatorChar)));
        if (!destinationPath.IsUnder(stagingRoot) ||
            File.Exists(destinationPath.Value) ||
            Directory.Exists(destinationPath.Value))
            throw new InvalidDataException(
                "The reserved product runtime script destination was not fresh and contained.");

        var temporaryPath = destinationPath.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(payload, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            var stagedHash = await HashFileAsync(temporaryPath, cancellationToken);
            if (stagedHash != new Sha256Hash(ExpectedPexSha256))
                throw new InvalidDataException(
                    "The staged product runtime script did not retain its embedded SHA-256.");

            File.Move(temporaryPath, destinationPath.Value, overwrite: false);
            var finalInfo = new FileInfo(destinationPath.Value);
            if (!finalInfo.Exists || finalInfo.Length != ExpectedPexSize ||
                finalInfo.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    "The atomically staged product runtime script is not the expected ordinary file.");

            return new BlankNpcTransitivePackageAsset(
                destinationPath,
                stagedHash,
                Destination);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static async ValueTask<byte[]> LoadAndValidateAsync(
        CancellationToken cancellationToken)
    {
        var assembly = typeof(SkyrimApplySseProductAsset).Assembly;
        var manifestBytes = await ReadResourceAsync(
            assembly, ManifestResourceName, MaximumManifestBytes, cancellationToken);
        var manifestHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(manifestBytes)));
        if (manifestHash != new Sha256Hash(ExpectedManifestSha256))
            throw new InvalidDataException(
                "The embedded product runtime asset manifest did not match the compiled authority hash.");

        ProductAssetManifest manifest;
        try
        {
            using var document = JsonDocument.Parse(manifestBytes, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 8
            });
            RejectDuplicateKeys(document.RootElement);
            manifest = ParseManifest(document.RootElement);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "The embedded product runtime asset manifest is invalid JSON.", exception);
        }

        var pex = await ReadResourceAsync(
            assembly, PexResourceName, ExpectedPexSize, cancellationToken);
        var pexHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(pex)));
        if (pex.Length != manifest.PexSize || pexHash != manifest.PexSha256 ||
            pexHash != new Sha256Hash(ExpectedPexSha256))
            throw new InvalidDataException(
                "The embedded product runtime PEX did not match its strict manifest.");
        return pex;
    }

    private static ProductAssetManifest ParseManifest(JsonElement root)
    {
        RequireShape(root, "product runtime asset manifest", "schemaVersion", "productAssetId",
            "edition", "scriptName", "dataRelativeDestination", "pex", "source",
            "inspection", "runtimeAuthority");
        var pex = root.GetProperty("pex");
        RequireShape(pex, "product runtime PEX", "sha256", "size");
        var source = root.GetProperty("source");
        RequireShape(source, "product runtime source", "pscSha256");
        var inspection = root.GetProperty("inspection");
        RequireShape(inspection, "product runtime inspection", "sha256",
            "totalPropertyCount", "writablePropertyCount", "readOnlyPropertyCount");

        var pexSha256 = new Sha256Hash(RequiredString(pex, "sha256"));
        var pexSize = RequiredInt(pex, "size");
        var pscSha256 = new Sha256Hash(RequiredString(source, "pscSha256"));
        var inspectionSha256 = new Sha256Hash(RequiredString(inspection, "sha256"));
        var totalProperties = RequiredInt(inspection, "totalPropertyCount");
        var writableProperties = RequiredInt(inspection, "writablePropertyCount");
        var readOnlyProperties = RequiredInt(inspection, "readOnlyPropertyCount");

        if (RequiredInt(root, "schemaVersion") != 1 ||
            !string.Equals(RequiredString(root, "productAssetId"),
                "skyrimse-npcm-manolov-apply-sse-v1", StringComparison.Ordinal) ||
            !string.Equals(RequiredString(root, "edition"), "skyrimse",
                StringComparison.Ordinal) ||
            !string.Equals(RequiredString(root, "scriptName"),
                "NPCM_Manolov_ApplySSE", StringComparison.Ordinal) ||
            !string.Equals(RequiredString(root, "dataRelativeDestination"),
                Destination.Value, StringComparison.Ordinal) ||
            pexSha256 != new Sha256Hash(ExpectedPexSha256) ||
            pexSize != ExpectedPexSize ||
            pscSha256 != new Sha256Hash(ExpectedPscSha256) ||
            inspectionSha256 != new Sha256Hash(ExpectedInspectionSha256) ||
            totalProperties != 43 || writableProperties != 36 || readOnlyProperties != 7 ||
            totalProperties != writableProperties + readOnlyProperties ||
            RequiredBoolean(root, "runtimeAuthority"))
            throw new InvalidDataException(
                "The embedded product runtime asset manifest fields do not match the pinned 36-property build authority.");

        return new ProductAssetManifest(pexSha256, pexSize);
    }

    private static async ValueTask<byte[]> ReadResourceAsync(
        Assembly assembly,
        string resourceName,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = assembly.GetManifestResourceStream(resourceName) ??
                                 throw new InvalidDataException(
                                     $"Required embedded product resource '{resourceName}' is absent.");
        if (!stream.CanSeek || stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException(
                $"Embedded product resource '{resourceName}' has an invalid size.");
        var bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(bytes, cancellationToken);
        if (stream.ReadByte() != -1)
            throw new InvalidDataException(
                $"Embedded product resource '{resourceName}' exceeded its declared size.");
        return bytes;
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)));
    }

    private static void RequireShape(JsonElement element, string role, params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{role} must be an object.");
        var actual = element.EnumerateObject().Select(item => item.Name).ToArray();
        if (actual.Length != fields.Length || fields.Any(field =>
                !actual.Contains(field, StringComparer.Ordinal)))
            throw new InvalidDataException($"{role} has missing or unknown fields.");
    }

    private static void RejectDuplicateKeys(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate JSON property '{property.Name}' is not accepted.");
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) RejectDuplicateKeys(item);
        }
    }

    private static string RequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            value.GetString() is not { Length: > 0 } result ||
            result.Contains('\0'))
            throw new InvalidDataException($"'{name}' must be a non-empty string.");
        return result;
    }

    private static int RequiredInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || !value.TryGetInt32(out var result))
            throw new InvalidDataException($"'{name}' must be an Int32.");
        return result;
    }

    private static bool RequiredBoolean(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"'{name}' must be a Boolean.");
        return value.GetBoolean();
    }

    private sealed record ProductAssetManifest(Sha256Hash PexSha256, int PexSize);
}
