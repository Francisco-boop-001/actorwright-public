using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Rendering;

namespace NpcManager.Infrastructure;

public static class ApplicationResourceExtractionProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static int Run(TextWriter output, TextWriter error)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        try
        {
            var resourceBase = new ApplicationResourcePath(
                AppContext.BaseDirectory);
            ApplicationResourceRuntimeAdmissionResult admission =
                new ApplicationResourceRuntimeLocator(resourceBase)
                    .AdmitReferencePresetRuntime();
            if (!admission.Accepted || admission.Authority is null)
            {
                error.WriteLine(JsonSerializer.Serialize(
                    new
                    {
                        schemaVersion = 1,
                        accepted = false,
                        diagnostics = admission.Diagnostics
                    },
                    JsonOptions));
                return 4;
            }

            var entries = ImmutableArray.CreateBuilder<ProbeEntry>();
            ApplicationResourceRuntimeAuthority runtime =
                admission.Authority;
            entries.Add(new ProbeEntry(
                runtime.Manifest.Role,
                Path.GetRelativePath(
                        resourceBase.Value,
                        runtime.Manifest.Path.Value)
                    .Replace('\\', '/'),
                runtime.Manifest.ByteLength,
                runtime.Manifest.Sha256.Value,
                "content"));
            foreach (ApplicationResourceAuthority asset in runtime.Assets)
                entries.Add(new ProbeEntry(
                    asset.Role,
                    Path.GetRelativePath(
                            resourceBase.Value,
                            asset.Path.Value)
                        .Replace('\\', '/'),
                    asset.ByteLength,
                    asset.Sha256.Value,
                    "content"));

            string profileManifest = Path.Combine(
                resourceBase.Value,
                "runtime",
                "rendering",
                "npc-preview-profile-manifest.json");
            AddFile(
                entries,
                resourceBase,
                "npc-preview-profile-manifest",
                profileManifest);
            AddScript(
                entries,
                "npc-preview-render-script",
                "render_npc_preview_bundle");

            AddProductProviderClosure(entries, resourceBase);

            output.WriteLine(JsonSerializer.Serialize(
                new ProbeDocument(
                    1,
                    true,
                    resourceBase.Value,
                    runtime.ManifestSha256.Value,
                    entries.ToImmutable()),
                JsonOptions));
            return 0;
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                InvalidOperationException or
                ArgumentException)
        {
            error.WriteLine(JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    accepted = false,
                    diagnostics = new[]
                    {
                        new
                        {
                            code = "application-resource-probe-failed",
                            severity = "error",
                            message = exception.Message
                        }
                    }
                },
                JsonOptions));
            return 4;
        }
    }

    private static void AddFile(
        ImmutableArray<ProbeEntry>.Builder entries,
        ApplicationResourcePath resourceBase,
        string role,
        string path)
    {
        if (!File.Exists(path) ||
            Directory.Exists(path) ||
            (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"Application resource is missing or not ordinary: '{path}'.");
        var info = new FileInfo(path);
        using FileStream stream = File.Open(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read);
        string hash = Convert.ToHexString(SHA256.HashData(stream));
        entries.Add(new ProbeEntry(
            role,
            Path.GetRelativePath(resourceBase.Value, path)
                .Replace('\\', '/'),
            info.Length,
            hash,
            "content"));
    }

    private static void AddScript(
        ImmutableArray<ProbeEntry>.Builder entries,
        string role,
        string id)
    {
        EmbeddedBlenderScript script =
            EmbeddedBlenderScriptBundle.Load(id);
        entries.Add(new ProbeEntry(
            role,
            $"assembly://NpcManager.Rendering/{script.EntryFileName}",
            script.SourceBytes.Length,
            script.Sha256.Value,
            "managed-resource"));
    }

    private static void AddProductProviderClosure(
        ImmutableArray<ProbeEntry>.Builder entries,
        ApplicationResourcePath resourceBase)
    {
        var registry = new ApplicationProviderResourceRegistry(resourceBase);
        if (!registry.TryGetDefaultBlankNpcFixture(out
                ProductFixtureBundleReference? reference) || reference is null)
            return;
        ApplicationProviderResourceAdmissionResult admission = registry.Admit(
            reference,
            new FormId(0x0000_0800),
            GameEdition.SkyrimSpecialEdition,
            NpcSex.Female);
        if (!admission.Accepted || admission.Authority is null)
            throw new InvalidDataException(
                "The packaged product-provider closure did not pass registry admission: " +
                string.Join(" | ", admission.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));

        string root = Path.Combine(resourceBase.Value, "runtime",
            "product-fixtures", reference.BundleId);
        string registryPath = Path.Combine(root, "registry.json");
        AddFile(entries, resourceBase, "product-provider-registry",
            registryPath);
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(registryPath));
        foreach (JsonElement row in document.RootElement.GetProperty("assets")
                     .EnumerateArray())
        {
            string role = row.GetProperty("role").GetString() ??
                throw new InvalidDataException(
                    "The product-provider registry contains an empty role.");
            string relative = row.GetProperty("path").GetString() ??
                throw new InvalidDataException(
                    "The product-provider registry contains an empty path.");
            string probeRole = string.Equals(
                    role, "provider-manifest", StringComparison.Ordinal)
                ? "product-provider-manifest"
                : "product-provider-" + role;
            AddFile(entries, resourceBase, probeRole,
                Path.Combine(root,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    private sealed record ProbeDocument(
        int SchemaVersion,
        bool Accepted,
        string ResourceBase,
        string RuntimeManifestSha256,
        ImmutableArray<ProbeEntry> Entries);

    private sealed record ProbeEntry(
        string Role,
        string Path,
        long Length,
        string Sha256,
        string Storage);
}
