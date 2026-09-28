using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed class ApplicationProviderResourceRegistry(
    ApplicationResourcePath resourceBase) :
    IApplicationProviderResourceRegistry
{
    private static readonly string[] RequiredRoles =
    [
        "provenance",
        "provider-manifest",
        "template-plugin",
        "facegeom-carrier",
        "facetint-manifest",
        "facetint-source",
        "dependency-manifest"
    ];

    public bool TryGetDefaultBlankNpcFixture(
        out ProductFixtureBundleReference? reference)
    {
        FileAttributes baseAttributes = File.GetAttributes(
            resourceBase.Value);
        if (!baseAttributes.HasFlag(FileAttributes.Directory) ||
            baseAttributes.HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                "The application resource base is not an ordinary directory.");

        string root = resourceBase.Value;
        foreach (string segment in new[]
                 { "runtime", "product-fixtures", "blank-npc-v1" })
        {
            root = Path.Combine(root, segment);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(root);
            }
            catch (Exception exception) when (exception is
                FileNotFoundException or DirectoryNotFoundException)
            {
                reference = null;
                return false;
            }
            if (!attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidDataException(
                    "The optional product-provider root is not an ordinary directory.");
        }

        string registryPath = Path.Combine(root, "registry.json");
        reference = new ProductFixtureBundleReference(
            "blank-npc-v1",
            Hash(ReadOrdinary(registryPath, root)));
        return true;
    }

    public ApplicationProviderResourceAdmissionResult Admit(
        ProductFixtureBundleReference reference,
        FormId templateNpcFormId,
        GameEdition edition,
        NpcSex sex)
    {
        try
        {
            string rootPath = Path.Combine(
                resourceBase.Value,
                "runtime",
                "product-fixtures",
                reference.BundleId);
            var root = new ApplicationResourcePath(rootPath);
            if (!root.IsUnder(resourceBase) ||
                !Directory.Exists(root.Value) ||
                ApplicationResourceRuntimeLocator.HasReparseAncestor(root.Value, resourceBase.Value))
                throw new InvalidDataException(
                    "The product-provider root is absent, escaped, or crosses a reparse point.");

            string registryPath = Path.Combine(root.Value, "registry.json");
            byte[] registryBytes = ReadOrdinary(registryPath, root.Value);
            Sha256Hash registryHash = Hash(registryBytes);
            if (registryHash != reference.RegistryManifestSha256)
                throw new InvalidDataException(
                    "The product-provider registry hash does not match the request.");

            using JsonDocument document = JsonDocument.Parse(
                registryBytes,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = 24
                });
            EnsureNoDuplicates(document.RootElement, "$");
            JsonElement json = document.RootElement;
            RequireShape(json, "registry", "schemaVersion", "bundleId",
                "edition", "sex", "templateNpcFormId", "dataRootSha256",
                "assets");
            if (json.GetProperty("schemaVersion").GetInt32() != 1 ||
                !string.Equals(json.GetProperty("bundleId").GetString(),
                    reference.BundleId, StringComparison.Ordinal) ||
                !string.Equals(json.GetProperty("edition").GetString(),
                    edition.ToWireName(), StringComparison.Ordinal) ||
                !string.Equals(json.GetProperty("sex").GetString(),
                    sex == NpcSex.Female ? "female" : "male",
                    StringComparison.Ordinal) ||
                !FormId.TryParse(
                    json.GetProperty("templateNpcFormId").GetString() ?? "",
                    out FormId registryForm) ||
                registryForm != templateNpcFormId)
                throw new InvalidDataException(
                    "The product-provider registry identity does not match the request.");

            JsonElement rows = json.GetProperty("assets");
            if (rows.ValueKind != JsonValueKind.Array ||
                rows.GetArrayLength() != RequiredRoles.Length)
                throw new InvalidDataException(
                    "The product-provider registry asset inventory is not exact.");
            var assets = ImmutableDictionary.CreateBuilder<string,
                ApplicationProviderResourceAuthority>(StringComparer.Ordinal);
            for (var index = 0; index < RequiredRoles.Length; index++)
            {
                JsonElement row = rows[index];
                RequireShape(row, $"asset[{index}]", "role", "path",
                    "length", "sha256");
                string role = row.GetProperty("role").GetString() ?? "";
                string relative = row.GetProperty("path").GetString() ?? "";
                if (!string.Equals(role, RequiredRoles[index],
                        StringComparison.Ordinal) ||
                    Path.IsPathRooted(relative) || relative.Contains(':') ||
                    relative.Contains("..", StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Product-provider asset row {index} is not admitted.");
                string path = Path.GetFullPath(Path.Combine(root.Value,
                    relative.Replace('/', Path.DirectorySeparatorChar)));
                if (!new ApplicationResourcePath(path).IsUnder(root))
                    throw new InvalidDataException(
                        $"Product-provider asset '{relative}' escaped its bundle.");
                byte[] bytes = ReadOrdinary(path, root.Value);
                var expected = new Sha256Hash(
                    row.GetProperty("sha256").GetString() ?? "");
                if (bytes.LongLength != row.GetProperty("length").GetInt64() ||
                    Hash(bytes) != expected)
                    throw new InvalidDataException(
                        $"Product-provider asset '{relative}' drifted.");
                assets.Add(role, new ApplicationProviderResourceAuthority(
                    reference.BundleId,
                    role,
                    new ApplicationResourcePath(path),
                    expected,
                    registryHash));
            }

            string dataRootPath = Path.Combine(root.Value, "Data");
            Sha256Hash dataRootHash = HashDirectory(dataRootPath);
            if (dataRootHash != new Sha256Hash(
                    json.GetProperty("dataRootSha256").GetString() ?? ""))
                throw new InvalidDataException(
                    "The product-provider Data inventory drifted.");

            string[] actual = EnumerateOrdinaryFiles(root.Value,
                    "The product-provider bundle contains a reparse point.")
                .Select(path => Path.GetRelativePath(root.Value, path)
                    .Replace(Path.DirectorySeparatorChar, '/'))
                .Order(StringComparer.Ordinal)
                .ToArray();
            string[] expectedFiles = rows.EnumerateArray()
                .Select(row => row.GetProperty("path").GetString()!)
                .Append("registry.json")
                .Order(StringComparer.Ordinal)
                .ToArray();
            if (!actual.SequenceEqual(expectedFiles, StringComparer.Ordinal))
                throw new InvalidDataException(
                    "The product-provider bundle contains undeclared files.");

            ApplicationProviderResourceAuthority Require(string role) =>
                assets.TryGetValue(role, out var value)
                    ? value
                    : throw new InvalidDataException(
                        $"The product-provider role '{role}' is absent.");
            var providerRoot = new ApplicationProviderResourceAuthority(
                reference.BundleId,
                "facetint-provider-root",
                new ApplicationResourcePath(dataRootPath),
                dataRootHash,
                registryHash,
                Directory: true);
            return new ApplicationProviderResourceAdmissionResult(
                new ProviderResourceAuthoritySet(
                    reference.BundleId,
                    registryHash,
                    Require("provider-manifest"),
                    Require("template-plugin"),
                    Require("facegeom-carrier"),
                    Require("facetint-manifest"),
                    providerRoot,
                    Require("facetint-source"),
                    Require("dependency-manifest"),
                    templateNpcFormId,
                    edition,
                    sex),
                []);
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            JsonException or KeyNotFoundException or ArgumentException or
            InvalidOperationException or OverflowException)
        {
            return new ApplicationProviderResourceAdmissionResult(
                null,
                [new Diagnostic(
                    "product-provider-unavailable",
                    DiagnosticSeverity.Error,
                    exception.Message)]);
        }
    }

    private static byte[] ReadOrdinary(string path, string boundary)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > 64L * 1024 * 1024 ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint) ||
            ApplicationResourceRuntimeLocator.HasReparseAncestor(path, boundary))
            throw new InvalidDataException(
                $"Product-provider file '{path}' is absent or not ordinary.");
        return File.ReadAllBytes(path);
    }

    private static Sha256Hash HashDirectory(string root)
    {
        if (!Directory.Exists(root) ||
            File.GetAttributes(root).HasFlag(FileAttributes.ReparsePoint))
            throw new InvalidDataException(
                "The product-provider Data root is absent or not ordinary.");
        var text = new StringBuilder();
        foreach (string path in EnumerateOrdinaryFiles(root,
                     "The product-provider Data root contains a reparse point.")
                 .Order(StringComparer.Ordinal))
        {
            if (ApplicationResourceRuntimeLocator.HasReparseAncestor(path, root))
                throw new InvalidDataException(
                    "The product-provider Data root contains a reparse point.");
            string relative = Path.GetRelativePath(root, path)
                .Replace(Path.DirectorySeparatorChar, '/');
            text.Append(relative).Append('|')
                .Append(Hash(File.ReadAllBytes(path)).Value).Append('\n');
        }
        return Hash(Encoding.UTF8.GetBytes(text.ToString()));
    }

    private static string[] EnumerateOrdinaryFiles(
        string root,
        string reparseMessage)
    {
        var files = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string directory = pending.Pop();
            if (ApplicationResourceRuntimeLocator.HasReparseAncestor(
                    directory, root))
                throw new InvalidDataException(reparseMessage);
            foreach (string entry in Directory.EnumerateFileSystemEntries(
                         directory))
            {
                FileAttributes attributes = File.GetAttributes(entry);
                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    throw new InvalidDataException(reparseMessage);
                if (attributes.HasFlag(FileAttributes.Directory))
                    pending.Push(entry);
                else
                    files.Add(entry);
            }
        }
        return files.ToArray();
    }

    private static void RequireShape(
        JsonElement element,
        string role,
        params string[] fields)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The {role} must be an object.");
        var expected = fields.ToHashSet(StringComparer.Ordinal);
        var actual = element.EnumerateObject().Select(item => item.Name)
            .ToHashSet(StringComparer.Ordinal);
        if (!actual.SetEquals(expected))
            throw new InvalidDataException(
                $"The {role} fields are not exact.");
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
            var index = 0;
            foreach (JsonElement item in element.EnumerateArray())
                EnsureNoDuplicates(item, $"{path}[{index++}]");
        }
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
