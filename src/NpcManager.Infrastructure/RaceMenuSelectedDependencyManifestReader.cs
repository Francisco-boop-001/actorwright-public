using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Strict reader for the selected external-dependency manifest. The reader
/// consumes only the canonical package evidence file; its evidencePath values
/// are provenance text and are never opened or treated as install authority.
/// </summary>
public sealed class RaceMenuSelectedDependencyManifestReader
    : IRaceMenuSelectedDependencyManifestReader
{
    private const long MaximumManifestBytes = 4L * 1024 * 1024;
    private const long MaximumByteLength = 1L << 40;
    private const int MaximumAssetPathLength = 2048;
    private const int MaximumCollection = 512;
    private static readonly HashSet<string> WindowsReservedDeviceNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "CON", "PRN", "AUX", "NUL",
            "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7",
            "COM8", "COM9",
            "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7",
            "LPT8", "LPT9",
            "CONIN$", "CONOUT$", "CLOCK$"
        };
    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 64
    };

    public async ValueTask<RaceMenuSelectedDependencyManifestReadResult>
        ReadAsync(
            WorkspacePath manifestPath,
            Sha256Hash expectedHash,
            WorkspacePath packageRoot,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            ValidateLocator(manifestPath, packageRoot);
            byte[] bytes = await ReadBytesAsync(
                manifestPath, cancellationToken).ConfigureAwait(false);
            Sha256Hash actualHash = new(Convert.ToHexString(
                SHA256.HashData(bytes)));
            Require(actualHash == expectedHash,
                "Selected dependency manifest hash does not match the expected hash.");

            using JsonDocument document = JsonDocument.Parse(bytes, JsonOptions);
            ValidateDuplicateProperties(document.RootElement, "$");
            RaceMenuSelectedDependencyManifestArtifact artifact = Parse(
                document.RootElement, manifestPath, actualHash, bytes);
            return new RaceMenuSelectedDependencyManifestReadResult(
                artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           JsonException or
                                           KeyNotFoundException or
                                           InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic(
                "racemenu-selected-dependencies-read",
                DiagnosticSeverity.Error,
                exception.Message));
            return new RaceMenuSelectedDependencyManifestReadResult(
                null, diagnostics.ToImmutable());
        }
    }

    private static async ValueTask<byte[]> ReadBytesAsync(
        WorkspacePath manifestPath, CancellationToken cancellationToken)
    {
        var info = new FileInfo(manifestPath.Value);
        Require(info.Exists, "Selected dependency manifest does not exist.");
        Require(info.Length > 0 && info.Length <= MaximumManifestBytes,
            "Selected dependency manifest size is outside the portable bound.");
        var attributes = File.GetAttributes(manifestPath.Value);
        Require(!attributes.HasFlag(FileAttributes.ReparsePoint),
            "Selected dependency manifest cannot be a reparse point.");
        return await File.ReadAllBytesAsync(
            manifestPath.Value, cancellationToken).ConfigureAwait(false);
    }

    private static void ValidateLocator(
        WorkspacePath manifestPath, WorkspacePath packageRoot)
    {
        Require(Directory.Exists(packageRoot.Value),
            "Selected dependency package root does not exist.");
        Require(manifestPath.IsUnder(packageRoot),
            "Selected dependency manifest escaped the package root.");
        string relative = Path.GetRelativePath(
                packageRoot.Value, manifestPath.Value)
            .Replace(Path.DirectorySeparatorChar, '/');
        Require(string.Equals(
                relative,
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath,
                StringComparison.Ordinal),
            "Selected dependency manifest is not at the canonical package locator.");
        Require(!ContainsReparseBetween(packageRoot.Value, manifestPath.Value),
            "Selected dependency manifest path contains a reparse point.");
    }

    private static RaceMenuSelectedDependencyManifestArtifact Parse(
        JsonElement root,
        WorkspacePath manifestPath,
        Sha256Hash manifestHash,
        ReadOnlySpan<byte> originalBytes)
    {
        RequireObject(root, "manifest");
        RequireTopLevelProperties(root);
        Require(RequiredInt(root, "schemaVersion") == 3,
            "Only selected dependency manifest schema 3 is accepted by the strict reader.");
        string dependencyId = RequiredPortable(root, "id", "id");
        Require(dependencyId.StartsWith(
                    "selected-preset-dependencies-",
                    StringComparison.Ordinal) &&
                dependencyId.Length ==
                    "selected-preset-dependencies-".Length + 24 &&
                dependencyId.Skip("selected-preset-dependencies-".Length)
                    .All(character => character is >= '0' and <= '9' or
                                     >= 'a' and <= 'f'),
            "Selected dependency id is not canonical.");
        Sha256Hash presetSha256 = ParseHash(root, "presetSha256");

        int headPartCount = ParseHeadParts(root.GetProperty("headParts"));
        int looseAssetCount = ParseLooseAssets(
            root.GetProperty("looseAssets"),
            out var looseAssets);
        int archiveCount = ParseArchives(
            root.GetProperty("archives"),
            out var archives);
        ParseSidecars(root, out var sidecars);
        var groups = ParseExternalGroups(
            root.GetProperty("externalInstallDependencies"),
            out var groupAssetPaths);
        Require(groups.Length > 0,
            "Schema 3 requires at least one external dependency group.");
        ValidateAssetClosure(
            looseAssets, archives, sidecars, groups);
        ValidateAssetOrdering(looseAssets, archives, sidecars);
        string computedDependencyId = BuildSchema3DependencyId(
            root, looseAssets, archives, sidecars, groups);
        Require(dependencyId == computedDependencyId,
            "Selected dependency id is not bound to the canonical schema-3 content.");

        Require(Canonicalize(root).AsSpan().SequenceEqual(originalBytes),
            "Selected dependency manifest is not in canonical JSON form.");
        return new RaceMenuSelectedDependencyManifestArtifact(
            dependencyId,
            manifestPath,
            manifestHash,
            headPartCount,
            looseAssetCount,
            archiveCount,
            RuntimeAuthority: false)
        {
            SchemaVersion = 3,
            PresetSha256 = presetSha256,
            ExternalInstallDependencies = groups
        };
    }

    private static void RequireTopLevelProperties(JsonElement root)
    {
        var names = root.EnumerateObject().Select(item => item.Name).ToArray();
        var expected = new List<string>
        {
            "schemaVersion", "id", "presetSha256", "headParts", "looseAssets", "archives"
        };
        if (root.TryGetProperty("externalProviderSidecars", out _))
            expected.Add("externalProviderSidecars");
        expected.Add("externalInstallDependencies");
        Require(names.SequenceEqual(expected, StringComparer.Ordinal),
            "Selected dependency manifest properties are not in canonical order.");
    }

    private static int ParseHeadParts(JsonElement array)
    {
        RequireArray(array, "headParts");
        RequireCount(array, "headParts", 1);
        foreach (JsonElement item in array.EnumerateArray())
        {
            RequireProperties(item, [
                "formKey", "type", "pluginEvidence", "pluginSha256"]);
            string formKey = RequiredPortable(item, "formKey", "headPart.formKey");
            int separator = formKey.IndexOf('|');
            Require(separator > 0 && separator == formKey.LastIndexOf('|'),
                "Head-part formKey is not canonical.");
            Require(FormReference.TryParse(formKey, out FormReference reference),
                "Head-part formKey has an invalid form id.");
            Require(formKey ==
                    $"{reference.Plugin.Value}|{reference.FormId.Value:X6}",
                "Head-part formKey is not canonical.");
            string type = RequiredPortable(item, "type", "headPart.type");
            Require(NpcHeadPartTypeExtensions.TryParseWireName(type,
                        out NpcHeadPartType parsedType) &&
                    parsedType.ToWireName() == type,
                "Head-part type is not a supported wire token.");
            _ = ParseAssetPath(item, "pluginEvidence", "headPart.pluginEvidence");
            _ = ParseHash(item, "pluginSha256");
        }

        return array.GetArrayLength();
    }

    private static int ParseLooseAssets(
        JsonElement array,
        out ImmutableArray<LooseAsset> assets)
    {
        RequireArray(array, "looseAssets");
        RequireCount(array, "looseAssets", 0);
        var builder = ImmutableArray.CreateBuilder<LooseAsset>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        foreach (JsonElement item in array.EnumerateArray())
        {
            RequireProperties(item, [
                "gamePath", "provider", "evidencePath", "sha256", "byteLength"]);
            AssetPath gamePath = ParseAssetPath(item, "gamePath", "loose.gamePath");
            Require(RaceMenuSelectedDependencyManifestAssetRules.IsSupported(
                        gamePath),
                "Loose asset route is not supported by the selected dependency contract.");
            Require(paths.Add(gamePath.Value),
                "Loose assets contain a duplicate or case-colliding path.");
            Require(previousPath is null ||
                    StringComparer.OrdinalIgnoreCase.Compare(
                        previousPath, gamePath.Value) < 0,
                "Loose assets are not in canonical path order.");
            previousPath = gamePath.Value;
            string provider = RequiredPortable(item, "provider", "loose.provider");
            AssetPath evidencePath = ParseAssetPath(
                item, "evidencePath", "loose.evidencePath");
            Sha256Hash hash = ParseHash(item, "sha256");
            long byteLength = RequiredPositiveLong(item, "byteLength");
            builder.Add(new LooseAsset(
                gamePath, provider, evidencePath, hash, byteLength));
        }

        assets = builder.ToImmutable();
        return assets.Length;
    }

    private static int ParseArchives(
        JsonElement array,
        out ImmutableArray<ArchiveAsset> archives)
    {
        RequireArray(array, "archives");
        RequireCount(array, "archives", 0);
        var builder = ImmutableArray.CreateBuilder<ArchiveAsset>();
        var providerKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousProviderKey = null;
        foreach (JsonElement item in array.EnumerateArray())
        {
            RequireProperties(item, [
                "provider", "evidencePath", "sha256", "byteLength", "members"]);
            string provider = RequiredPortable(item, "provider", "archive.provider");
            AssetPath evidencePath = ParseAssetPath(
                item, "evidencePath", "archive.evidencePath");
            Sha256Hash archiveHash = ParseHash(item, "sha256");
            long archiveByteLength = RequiredPositiveLong(item, "byteLength");
            string providerKey = provider + "|" + evidencePath.Value + "|" +
                archiveHash.Value;
            Require(providerKeys.Add(providerKey),
                "Archives contain duplicate provider evidence.");
            Require(previousProviderKey is null ||
                    StringComparer.OrdinalIgnoreCase.Compare(
                        previousProviderKey, providerKey) < 0,
                "Archives are not in canonical provider order.");
            previousProviderKey = providerKey;
            JsonElement members = item.GetProperty("members");
            RequireArray(members, "archive.members");
            RequireCount(members, "archive.members", 1);
            var memberBuilder = ImmutableArray.CreateBuilder<ArchiveMember>();
            var memberPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? previousMemberPath = null;
            foreach (JsonElement member in members.EnumerateArray())
            {
                RequireProperties(member, ["gamePath", "sha256", "byteLength"]);
                AssetPath gamePath = ParseAssetPath(
                    member, "gamePath", "archive.member.gamePath");
                Require(RaceMenuSelectedDependencyManifestAssetRules.IsSupported(
                            gamePath),
                    "Archive member route is not supported by the selected dependency contract.");
                Require(paths.Add(gamePath.Value),
                    "Archive members contain duplicate or case-colliding paths.");
                Require(memberPaths.Add(gamePath.Value),
                    "Archive members contain duplicate paths.");
                Require(previousMemberPath is null ||
                        StringComparer.OrdinalIgnoreCase.Compare(
                            previousMemberPath, gamePath.Value) < 0,
                    "Archive members are not in canonical path order.");
                previousMemberPath = gamePath.Value;
                Sha256Hash hash = ParseHash(member, "sha256");
                long length = RequiredPositiveLong(member, "byteLength");
                memberBuilder.Add(new ArchiveMember(gamePath, hash, length));
            }
            builder.Add(new ArchiveAsset(
                provider, evidencePath, archiveHash, archiveByteLength,
                memberBuilder.ToImmutable()));
        }

        archives = builder.ToImmutable();
        return archives.Length;
    }

    private static void ParseSidecars(
        JsonElement root, out ImmutableArray<LooseAsset> sidecars)
    {
        if (!root.TryGetProperty("externalProviderSidecars", out JsonElement array))
        {
            sidecars = [];
            return;
        }

        RequireArray(array, "externalProviderSidecars");
        RequireCount(array, "externalProviderSidecars", 1);
        var builder = ImmutableArray.CreateBuilder<LooseAsset>();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousPath = null;
        foreach (JsonElement item in array.EnumerateArray())
        {
            RequireProperties(item, [
                "gamePath", "provider", "evidencePath", "sha256", "byteLength"]);
            AssetPath gamePath = ParseAssetPath(item, "gamePath", "sidecar.gamePath");
            Require(paths.Add(gamePath.Value),
                "Sidecars contain duplicate or case-colliding paths.");
            Require(previousPath is null ||
                    StringComparer.OrdinalIgnoreCase.Compare(
                        previousPath, gamePath.Value) < 0,
                "Sidecars are not in canonical path order.");
            previousPath = gamePath.Value;
            string provider = RequiredPortable(item, "provider", "sidecar.provider");
            AssetPath evidencePath = ParseAssetPath(
                item, "evidencePath", "sidecar.evidencePath");
            Sha256Hash hash = ParseHash(item, "sha256");
            long byteLength = RequiredPositiveLong(item, "byteLength");
            Require(gamePath.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) &&
                    gamePath.Value.EndsWith(".xml", StringComparison.OrdinalIgnoreCase),
                "External provider sidecars must be meshes-relative XML paths.");
            builder.Add(new LooseAsset(
                gamePath, provider, evidencePath, hash, byteLength));
        }

        sidecars = builder.ToImmutable();
    }

    private static ImmutableArray<
        RaceMenuSelectedDependencyManifestExternalInstallDependency>
        ParseExternalGroups(
            JsonElement array, out ImmutableHashSet<string> groupAssetPaths)
    {
        RequireArray(array, "externalInstallDependencies");
        RequireCount(array, "externalInstallDependencies", 1);
        var builder = ImmutableArray.CreateBuilder<
            RaceMenuSelectedDependencyManifestExternalInstallDependency>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var assetPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? previousId = null;
        foreach (JsonElement item in array.EnumerateArray())
        {
            RequireProperties(item, [
                "descriptorId", "descriptor", "attestation", "outputPlugin", "faceGeom"]);
            string id = RequiredHash(item, "descriptorId");
            Require(ids.Add(id), "External dependency descriptor ids must be unique.");
            if (previousId is not null)
                Require(string.CompareOrdinal(previousId, id) < 0,
                    "External dependency groups must be ordinally sorted by descriptor id.");
            previousId = id;

            ExternalHeadPartDependencyDescriptor descriptor =
                ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(
                    GetRaw(item, "descriptor"));
            Require(descriptor.DescriptorId.Value == id,
                "External dependency descriptor id disagrees with its envelope.");
            ExternalHeadPartFaceGeomExclusionAttestation attestation =
                ExternalHeadPartDependencyDescriptorCodec.ParseAttestation(
                    GetRaw(item, "attestation"));
            Require(attestation.DescriptorId == descriptor.DescriptorId,
                "External dependency attestation targets a different descriptor.");
            Require(descriptor.Assets.Length > 0,
                "External dependency descriptors require at least one asset.");
            Require(descriptor.Assets.All(asset =>
                        RaceMenuSelectedDependencyManifestAssetRules.IsSupported(
                            asset.Path)),
                "External dependency descriptors contain an unsupported asset route.");

            JsonElement output = item.GetProperty("outputPlugin");
            RequireProperties(output, ["plugin", "sha256", "byteLength", "masters", "pnam"]);
            string outputPlugin = RequiredString(output, "plugin");
            ValidatePortableValue(outputPlugin, "outputPlugin.plugin");
            var outputBinding = new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                new PluginName(outputPlugin),
                ParseHash(output, "sha256"),
                RequiredPositiveLong(output, "byteLength"),
                ParsePlugins(output.GetProperty("masters"), "outputPlugin.masters"),
                ParseFormReferences(output.GetProperty("pnam"), "outputPlugin.pnam"));
            ValidateOutputBinding(descriptor, outputBinding);

            JsonElement faceGeom = item.GetProperty("faceGeom");
            RequireProperties(faceGeom, ["path", "sha256", "byteLength"]);
            AssetPath faceGeomPath = ParseAssetPath(faceGeom, "path", "faceGeom.path");
            Require(faceGeomPath.Value.StartsWith("Data/", StringComparison.Ordinal),
                "FaceGeom evidence must be package-relative under Data/.");
            Sha256Hash faceGeomHash = ParseHash(faceGeom, "sha256");
            long faceGeomLength = RequiredPositiveLong(faceGeom, "byteLength");
            Require(attestation.OutputFaceGeomPath == faceGeomPath &&
                    attestation.OutputFaceGeomSha256 == faceGeomHash &&
                    attestation.OutputFaceGeomByteLength == faceGeomLength,
                "FaceGeom evidence disagrees with its attestation.");

            foreach (ExternalHeadPartAssetDependency asset in descriptor.Assets)
            {
                Require(assetPaths.Add(asset.Path.Value),
                    "Descriptor assets contain a duplicate or case-colliding path.");
            }
            builder.Add(new RaceMenuSelectedDependencyManifestExternalInstallDependency(
                descriptor, attestation, outputBinding,
                faceGeomPath, faceGeomHash, faceGeomLength));
        }

        groupAssetPaths = assetPaths.ToImmutableHashSet(
            StringComparer.OrdinalIgnoreCase);
        return builder.ToImmutable();
    }

    private static void ValidateOutputBinding(
        ExternalHeadPartDependencyDescriptor descriptor,
        RaceMenuSelectedDependencyManifestOutputPluginBinding output)
    {
        var masters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginName master in output.Masters)
            Require(masters.Add(master.Value),
                "Output plugin masters must be unique.");
        foreach (PluginName required in descriptor.Members.Select(
                     item => item.RequiredOutputMaster).Distinct())
            Require(masters.Contains(required.Value),
                "Output plugin masters do not cover the descriptor graph.");
        Require(output.PnamBindings.Length == descriptor.Members.Length,
            "Output PNAM binding count does not match the descriptor graph.");
        Require(output.PnamBindings.SequenceEqual(
                    descriptor.Members.Select(item => item.WinningForm)),
            "Output PNAM bindings do not match descriptor member order.");
    }

    private static void ValidateAssetClosure(
        ImmutableArray<LooseAsset> looseAssets,
        ImmutableArray<ArchiveAsset> archives,
        ImmutableArray<LooseAsset> sidecars,
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> groups)
    {
        var descriptorByPath = groups.SelectMany(group => group.Descriptor.Assets)
            .ToDictionary(item => item.Path.Value,
                StringComparer.OrdinalIgnoreCase);
        var observedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (LooseAsset asset in looseAssets)
        {
            Require(observedPaths.Add(asset.GamePath.Value),
                "Selected dependency assets contain a duplicate path across providers.");
            Require(descriptorByPath.TryGetValue(asset.GamePath.Value,
                        out ExternalHeadPartAssetDependency? expected),
                "Loose asset is not declared by an external descriptor.");
            ExternalHeadPartAssetDependency looseExpected = expected!;
            Require(looseExpected.ArchiveMember is null &&
                    looseExpected.Sha256 == asset.Sha256 &&
                    looseExpected.ByteLength == asset.ByteLength &&
                    looseExpected.ByteLength > 0,
                "Loose asset evidence disagrees with its descriptor.");
        }
        foreach (ArchiveAsset archive in archives)
        {
            foreach (ArchiveMember member in archive.Members)
            {
                Require(observedPaths.Add(member.GamePath.Value),
                    "Selected dependency assets contain a duplicate path across providers.");
                Require(descriptorByPath.TryGetValue(member.GamePath.Value,
                            out ExternalHeadPartAssetDependency? expected),
                    "Archive member is not declared by an external descriptor.");
                ExternalHeadPartAssetDependency archiveExpected = expected!;
                Require(archiveExpected.ArchiveMember is { } archiveMember &&
                        string.Equals(
                            archiveMember.ArchivePath.Value,
                            archive.Provider,
                            StringComparison.OrdinalIgnoreCase) &&
                        archiveMember.ArchiveSha256 == archive.ArchiveSha256 &&
                        archiveMember.ArchiveByteLength == archive.ByteLength &&
                        archiveMember.MemberSha256 == member.Sha256 &&
                        archiveMember.MemberByteLength == member.ByteLength,
                    "Archive member evidence disagrees with its descriptor.");
            }
        }
        foreach (string descriptorPath in descriptorByPath.Keys)
            Require(observedPaths.Contains(descriptorPath),
                "External descriptor asset is missing from selected dependency evidence.");

        var requiredSidecars = new Dictionary<string, (Sha256Hash Hash, long Length)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var shape in groups
                     .Where(group => group.Descriptor.Physics.Mode ==
                         ExternalHeadPartPhysicsBindingMode.DirectNifExtraData)
                     .SelectMany(group => group.Descriptor.Physics.Shapes))
        {
            if (requiredSidecars.TryGetValue(shape.XmlPath.Value,
                    out (Sha256Hash Hash, long Length) existing))
            {
                Require(existing.Hash == shape.XmlSha256 &&
                        existing.Length == shape.XmlByteLength,
                    "Direct-NIF physics XML authorities disagree.");
            }
            else
            {
                requiredSidecars.Add(shape.XmlPath.Value,
                    (shape.XmlSha256, shape.XmlByteLength));
            }
        }
        var observedSidecars = sidecars.ToDictionary(
            item => item.GamePath.Value,
            item => (item.Sha256, item.ByteLength),
            StringComparer.OrdinalIgnoreCase);
        Require(observedSidecars.Count == requiredSidecars.Count,
            "External provider sidecars do not exactly close direct-NIF physics authorities.");
        foreach ((string path, (Sha256Hash Hash, long Length) expected) in
                 requiredSidecars)
        {
            Require(observedSidecars.TryGetValue(path,
                        out (Sha256Hash Hash, long Length) actual) &&
                    actual.Hash == expected.Hash && actual.Length == expected.Length,
                "External provider sidecar evidence disagrees with direct-NIF physics XML authority.");
        }
    }

    private static void ValidateAssetOrdering(
        ImmutableArray<LooseAsset> looseAssets,
        ImmutableArray<ArchiveAsset> archives,
        ImmutableArray<LooseAsset> sidecars)
    {
        Require(IsSorted(looseAssets.Select(item => item.GamePath.Value)),
            "Loose assets are not in canonical path order.");
        Require(IsSorted(archives.Select(item =>
                item.Provider + "|" + item.EvidencePath.Value + "|" +
                item.ArchiveSha256.Value)),
            "Archives are not in canonical provider order.");
        Require(archives.All(archive => IsSorted(
                    archive.Members.Select(item => item.GamePath.Value))),
            "Archive members are not in canonical path order.");
        Require(IsSorted(sidecars.Select(item => item.GamePath.Value)),
            "Sidecars are not in canonical path order.");
    }

    private static bool IsSorted(IEnumerable<string> values)
    {
        string? previous = null;
        foreach (string value in values)
        {
            if (previous is not null &&
                StringComparer.OrdinalIgnoreCase.Compare(previous, value) >= 0)
                return false;
            previous = value;
        }
        return true;
    }

    private static string BuildSchema3DependencyId(
        JsonElement root,
        ImmutableArray<LooseAsset> looseAssets,
        ImmutableArray<ArchiveAsset> archives,
        ImmutableArray<LooseAsset> sidecars,
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> groups)
    {
        var dependencyRows = looseAssets.Select(item =>
                $"{item.GamePath.Value}:{item.Provider}:{item.Sha256.Value}:{item.ByteLength}")
            .Concat(archives.SelectMany(archive => archive.Members.Select(member =>
                $"{member.GamePath.Value}:{archive.Provider}:" +
                $"{archive.ArchiveSha256.Value}:{archive.ByteLength}:" +
                $"{member.Sha256.Value}:{member.ByteLength}")))
            .OrderBy(item => item, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item, StringComparer.Ordinal)
            .ToArray();
        string identity = string.Join(
            '|',
            "schema3",
            ParseHash(root, "presetSha256").Value,
            string.Join(';', root.GetProperty("headParts").EnumerateArray()
                .Select(item =>
                {
                    string formKey = RequiredString(item, "formKey");
                    Require(FormReference.TryParse(formKey,
                                out FormReference reference),
                        "Head-part formKey has an invalid form id.");
                    return string.Join(':',
                        $"{reference.Plugin.Value}|{reference.FormId.Value:X6}",
                        RequiredString(item, "type"),
                        ParseAssetPath(
                            item, "pluginEvidence", "headpart.pluginEvidence").Value,
                        ParseHash(item, "pluginSha256").Value);
                })),
            string.Join(';', dependencyRows),
            string.Join(';', sidecars.Select(item =>
                $"{item.GamePath.Value}:{item.Provider}:{item.Sha256.Value}:{item.ByteLength}")),
            string.Join(';', groups.Select(item =>
            {
                string descriptor = Convert.ToHexString(
                    ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                        item.Descriptor));
                string attestation = Convert.ToHexString(
                    ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                        item.Attestation));
                return string.Join(':',
                    descriptor,
                    attestation,
                    item.OutputPlugin.Plugin.Value,
                    item.OutputPlugin.Sha256.Value,
                    item.OutputPlugin.ByteLength,
                    EncodeCanonicalArray(item.OutputPlugin.Masters.Select(
                        master => master.Value)),
                    EncodeCanonicalArray(item.OutputPlugin.PnamBindings.Select(
                        binding => binding.ToString())),
                    item.FaceGeomPath.Value,
                    item.FaceGeomSha256.Value,
                    item.FaceGeomByteLength);
            })));
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "selected-preset-dependencies-" + hash[..24].ToLowerInvariant();
    }

    private static string EncodeCanonicalArray(IEnumerable<string> values) =>
        string.Concat(values.Select(value => value.Length + ":" + value));

    private static ImmutableArray<PluginName> ParsePlugins(
        JsonElement array, string role)
    {
        RequireArray(array, role);
        RequireCount(array, role, 1);
        var values = ImmutableArray.CreateBuilder<PluginName>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            string plugin = RequiredElementString(item, role);
            ValidatePortableValue(plugin, role);
            values.Add(new PluginName(plugin));
        }
        return values.ToImmutable();
    }

    private static ImmutableArray<FormReference> ParseFormReferences(
        JsonElement array, string role)
    {
        RequireArray(array, role);
        RequireCount(array, role, 1);
        var values = ImmutableArray.CreateBuilder<FormReference>();
        foreach (JsonElement item in array.EnumerateArray())
        {
            string text = RequiredElementString(item, role);
            Require(FormReference.TryParse(text, out FormReference reference) &&
                    reference.ToString() == text,
                role + " contains a noncanonical form reference.");
            Require(!ContainsWindowsReservedDeviceSegment(reference.Plugin.Value),
                role + " contains a Windows reserved plugin name.");
            values.Add(reference);
        }
        return values.ToImmutable();
    }

    private static AssetPath ParseAssetPath(
        JsonElement parent, string property, string role) =>
        ParseAssetPath(RequiredString(parent, property), role);

    private static AssetPath ParseAssetPath(string value, string role)
    {
        var path = new AssetPath(value);
        Require(path.Value == value,
            role + " must use canonical slash separators.");
        Require(path.Value.Length <= MaximumAssetPathLength &&
                !path.Value.Any(char.IsControl) &&
                !ContainsWindowsReservedDeviceSegment(path.Value),
            role + " is outside the portable asset-path bounds.");
        return path;
    }

    private static Sha256Hash ParseHash(JsonElement parent, string property) =>
        ParseHash(RequiredString(parent, property), property);

    private static Sha256Hash ParseHash(string value, string role)
    {
        Require(value.Length == 64 && value.All(character =>
                    character is >= '0' and <= '9' or >= 'a' and <= 'f'),
            role + " must be a lowercase SHA-256 hash.");
        return new Sha256Hash(value);
    }

    private static string RequiredHash(JsonElement parent, string property) =>
        ParseHash(parent, property).Value;

    private static string RequiredPortable(
        JsonElement parent, string property, string role)
    {
        string value = RequiredString(parent, property);
        ValidatePortableValue(value, role);
        return value;
    }

    private static void ValidatePortableValue(string value, string role)
    {
        Require(!string.IsNullOrWhiteSpace(value) && value.Length <= 1024 &&
                !value.Any(char.IsControl) && !value.Contains('\\') &&
                !value.Contains('/') && !value.Contains(':') &&
                !value.Contains('\0') &&
                !value.Contains("worktree", StringComparison.OrdinalIgnoreCase) &&
                !ContainsWindowsReservedDeviceSegment(value),
            role + " is not a portable token.");
    }

    private static string RequiredString(JsonElement parent, string property)
    {
        JsonElement element = parent.GetProperty(property);
        Require(element.ValueKind == JsonValueKind.String,
            property + " must be a string.");
        return element.GetString() ?? throw Invalid(property + " is null.");
    }

    private static string RequiredElementString(JsonElement element, string role)
    {
        Require(element.ValueKind == JsonValueKind.String,
            role + " must contain only strings.");
        return element.GetString() ?? throw Invalid(role + " contains null.");
    }

    private static int RequiredInt(JsonElement parent, string property)
    {
        JsonElement element = parent.GetProperty(property);
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt32(out int value)) return value;
        throw Invalid(property + " must be a 32-bit integer.");
    }

    private static long RequiredPositiveLong(JsonElement parent, string property)
    {
        JsonElement element = parent.GetProperty(property);
        if (element.ValueKind == JsonValueKind.Number &&
            element.TryGetInt64(out long value) &&
            value > 0 && value <= MaximumByteLength) return value;
        throw Invalid(property + " must be a positive integer.");
    }

    private static ReadOnlySpan<byte> GetRaw(JsonElement parent, string property)
    {
        string raw = parent.GetProperty(property).GetRawText();
        return System.Text.Encoding.UTF8.GetBytes(raw);
    }

    private static void RequireProperties(
        JsonElement element, IReadOnlyList<string> expected)
    {
        RequireObject(element, "property group");
        var names = element.EnumerateObject().Select(item => item.Name).ToArray();
        Require(names.SequenceEqual(expected, StringComparer.Ordinal),
            "JSON properties are not the expected closed canonical member set.");
    }

    private static void RequireObject(JsonElement element, string role) =>
        Require(element.ValueKind == JsonValueKind.Object,
            role + " must be an object.");

    private static void RequireArray(JsonElement element, string role) =>
        Require(element.ValueKind == JsonValueKind.Array,
            role + " must be an array.");

    private static void RequireCount(
        JsonElement array, string role, int minimum)
    {
        Require(array.GetArrayLength() >= minimum &&
                array.GetArrayLength() <= MaximumCollection,
            role + " count is outside the portable bound.");
    }

    private static void ValidateDuplicateProperties(
        JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                Require(names.Add(property.Name),
                    "Duplicate JSON property at " + path + ".");
                ValidateDuplicateProperties(property.Value,
                    path + "." + property.Name);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (JsonElement child in element.EnumerateArray())
                ValidateDuplicateProperties(child, path + "[" + index++ + "]");
        }
    }

    private static bool ContainsWindowsReservedDeviceSegment(string value)
    {
        foreach (string segment in value.Replace('\\', '/').Split('/'))
        {
            string withoutTrailing = segment.TrimEnd(' ', '.');
            int extensionSeparator = withoutTrailing.IndexOf('.');
            string deviceName = extensionSeparator >= 0
                ? withoutTrailing[..extensionSeparator]
                : withoutTrailing;
            if (WindowsReservedDeviceNames.Contains(deviceName))
                return true;
        }
        return false;
    }

    private static bool ContainsReparseBetween(string root, string path)
    {
        string rootFull = Path.GetFullPath(root).TrimEnd(
            Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string current = Path.GetFullPath(path);
        while (current.Length >= rootFull.Length)
        {
            if ((Directory.Exists(current) || File.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;
            if (string.Equals(current, rootFull,
                    StringComparison.OrdinalIgnoreCase)) return false;
            string? parent = Path.GetDirectoryName(current);
            if (parent is null || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase)) return false;
            current = parent;
        }
        return true;
    }

    private static byte[] Canonicalize(JsonElement root)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(
                   stream,
                   new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.Name == "externalInstallDependencies")
                    WriteCanonicalExternalGroups(writer, property.Value);
                else
                    property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    private static void WriteCanonicalExternalGroups(
        Utf8JsonWriter writer, JsonElement array)
    {
        writer.WriteStartArray();
        foreach (JsonElement group in array.EnumerateArray())
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in group.EnumerateObject())
            {
                writer.WritePropertyName(property.Name);
                if (property.Name is "descriptor" or "attestation")
                {
                    writer.WriteRawValue(
                        property.Value.GetRawText(),
                        skipInputValidation: true);
                }
                else
                    property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw Invalid(message);
    }

    private static InvalidDataException Invalid(string message) =>
        new(message);

    private sealed record LooseAsset(
        AssetPath GamePath,
        string Provider,
        AssetPath EvidencePath,
        Sha256Hash Sha256,
        long ByteLength);

    private sealed record ArchiveAsset(
        string Provider,
        AssetPath EvidencePath,
        Sha256Hash ArchiveSha256,
        long ByteLength,
        ImmutableArray<ArchiveMember> Members);

    private sealed record ArchiveMember(
        AssetPath GamePath,
        Sha256Hash Sha256,
        long ByteLength);
}
