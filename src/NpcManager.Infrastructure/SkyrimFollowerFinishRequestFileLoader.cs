using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Strict, bounded, hash-first loader shared by follower-finish CLI and
/// desktop callers. It accepts only ordinary files and request paths below
/// the configured K-local workspace root.
/// </summary>
public sealed class SkyrimFollowerFinishRequestFileLoader :
    ISkyrimFollowerFinishRequestFileLoader
{
    private const int MaximumInputBytes = 1024 * 1024;
    private readonly WorkspacePath workspaceRoot;
    private readonly Func<string, FileAttributes?> readPathAttributes;

    public SkyrimFollowerFinishRequestFileLoader(
        WorkspacePath workspaceRoot)
        : this(workspaceRoot, ReadPathAttributes)
    {
    }

    internal SkyrimFollowerFinishRequestFileLoader(
        WorkspacePath workspaceRoot,
        Func<string, FileAttributes?> readPathAttributes)
    {
        this.workspaceRoot = workspaceRoot;
        this.readPathAttributes = readPathAttributes ??
            throw new ArgumentNullException(nameof(readPathAttributes));
    }

    public async ValueTask<SkyrimFollowerFinishRequestLoadResult>
        LoadRequestAsync(
            WorkspacePath path,
            Sha256Hash expectedSha256,
            CancellationToken cancellationToken,
            SkyrimFollowerFinishDocumentLoadMode mode =
                SkyrimFollowerFinishDocumentLoadMode.PreWrite)
    {
        Sha256Hash? actualSha256 = null;
        long? byteLength = null;
        try
        {
            BoundFile bound = await ReadBoundFileAsync(
                path,
                expectedSha256,
                cancellationToken);
            actualSha256 = bound.Sha256;
            byteLength = bound.Bytes.LongLength;
            using JsonDocument document = ParseDocument(bound.Bytes);
            RejectDuplicateProperties(document.RootElement);
            SkyrimFollowerFinishRequest request =
                ReadRequest(document.RootElement);
            ValidateRequestFileSystem(
                request,
                path,
                "request",
                mode);
            return new SkyrimFollowerFinishRequestLoadResult(
                true,
                path,
                expectedSha256,
                actualSha256,
                byteLength,
                request,
                [
                    new Diagnostic(
                        "follower-finish-request-loaded",
                        DiagnosticSeverity.Info,
                        "The schema-1 follower-finish request was hash-bound and parsed without ambiguity.")
                ]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return new SkyrimFollowerFinishRequestLoadResult(
                false,
                path,
                expectedSha256,
                actualSha256,
                byteLength,
                null,
                [
                    new Diagnostic(
                        "follower-finish-request-security-refused",
                        DiagnosticSeverity.Error,
                        exception.Message)
                ]);
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            return new SkyrimFollowerFinishRequestLoadResult(
                false,
                path,
                expectedSha256,
                actualSha256,
                byteLength,
                null,
                [
                    new Diagnostic(
                        "follower-finish-request-invalid",
                        DiagnosticSeverity.Error,
                        exception.Message)
                ]);
        }
    }

    public async ValueTask<SkyrimFollowerFinishProposalLoadResult>
        LoadProposalAsync(
            WorkspacePath path,
            Sha256Hash expectedSha256,
            CancellationToken cancellationToken,
            SkyrimFollowerFinishDocumentLoadMode mode =
                SkyrimFollowerFinishDocumentLoadMode.PreWrite)
    {
        Sha256Hash? actualSha256 = null;
        long? byteLength = null;
        try
        {
            BoundFile bound = await ReadBoundFileAsync(
                path,
                expectedSha256,
                cancellationToken);
            actualSha256 = bound.Sha256;
            byteLength = bound.Bytes.LongLength;
            using JsonDocument document = ParseDocument(bound.Bytes);
            RejectDuplicateProperties(document.RootElement);
            SkyrimFollowerFinishProposal proposal =
                ReadProposal(document.RootElement);
            ValidateRequestFileSystem(
                proposal.Request,
                path,
                "proposal",
                mode);
            return new SkyrimFollowerFinishProposalLoadResult(
                true,
                path,
                expectedSha256,
                actualSha256,
                byteLength,
                proposal,
                [
                    new Diagnostic(
                        "follower-finish-proposal-loaded",
                        DiagnosticSeverity.Info,
                        "The schema-1 follower-finish proposal was hash-bound and parsed without ambiguity.")
                ]);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (UnauthorizedAccessException exception)
        {
            return new SkyrimFollowerFinishProposalLoadResult(
                false,
                path,
                expectedSha256,
                actualSha256,
                byteLength,
                null,
                [
                    new Diagnostic(
                        "follower-finish-proposal-security-refused",
                        DiagnosticSeverity.Error,
                        exception.Message)
                ]);
        }
        catch (Exception exception) when (IsValidationException(exception))
        {
            return new SkyrimFollowerFinishProposalLoadResult(
                false,
                path,
                expectedSha256,
                actualSha256,
                byteLength,
                null,
                [
                    new Diagnostic(
                        "follower-finish-proposal-invalid",
                        DiagnosticSeverity.Error,
                        exception.Message)
                ]);
        }
    }

    private async ValueTask<BoundFile> ReadBoundFileAsync(
        WorkspacePath path,
        Sha256Hash expectedSha256,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateKPath(path, "input JSON");
        if (!File.Exists(path.Value) || Directory.Exists(path.Value))
            throw new InvalidDataException(
                "Follower-finish input must be an existing ordinary file.");
        if (TraversesReparsePoint(path))
            throw new UnauthorizedAccessException(
                "Follower-finish input may not traverse a reparse point.");

        byte[] bytes;
        await using (var stream = new FileStream(
                         path.Value,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         64 * 1024,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        {
            if (stream.Length is <= 0 or > MaximumInputBytes)
                throw new InvalidDataException(
                    "Follower-finish input must contain 1 byte to 1 MiB.");
            bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, cancellationToken);
        }

        var actualSha256 = new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(bytes)));
        if (actualSha256 != expectedSha256)
            throw new InvalidDataException(
                $"Follower-finish input hash {actualSha256} does not match {expectedSha256}.");
        return new BoundFile(bytes, actualSha256);
    }

    private static JsonDocument ParseDocument(byte[] bytes) =>
        JsonDocument.Parse(
            bytes,
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });

    private SkyrimFollowerFinishRequest ReadRequest(JsonElement element)
    {
        RequireObject(
            element,
            "request",
            "schemaVersion",
            "operation",
            "source",
            "externalAuthorities",
            "npcEditorId",
            "npcFormId",
            "occupiedLocalFormIds",
            "expectedRace",
            "expectedBodyRoute",
            "expectedDefaultOutfitNull",
            "expectedFactionRanks",
            "relationshipFormId",
            "expectedRelationshipRank",
            "expectedRelationshipRankRawDiscriminator",
            "hair",
            "setEslFlag",
            "compactFormIds",
            "sandbox",
            "placement",
            "allocation",
            "allowedNewRecords",
            "allowedExistingRecordChanges",
            "allowedPackageFiles",
            "outputRoot",
            "outputZip",
            "narrative");

        return ReadRequestWithOccupied(
            element,
            ReadFormIdArray(
                Required(element, "occupiedLocalFormIds"),
                "occupiedLocalFormIds"));
    }

    private SkyrimFollowerFinishRequest ReadRequestWithOccupied(
        JsonElement element,
        ImmutableArray<FormId> occupied) =>
        new(
            ReadInt(element, "schemaVersion"),
            ReadString(element, "operation"),
            ReadSource(Required(element, "source")),
            ReadEditorId(element, "npcEditorId"),
            ReadFormId(element, "npcFormId"),
            occupied,
            ReadFormReference(element, "expectedRace"),
            ReadString(element, "expectedBodyRoute"),
            ReadBool(element, "expectedDefaultOutfitNull"),
            ReadFactionRanks(
                Required(element, "expectedFactionRanks"),
                "expectedFactionRanks"),
            ReadFormId(element, "relationshipFormId"),
            ReadString(element, "expectedRelationshipRank"),
            ReadByte(
                element,
                "expectedRelationshipRankRawDiscriminator"),
            ReadHair(Required(element, "hair")),
            ReadBool(element, "setEslFlag"),
            ReadBool(element, "compactFormIds"),
            ReadSandbox(Required(element, "sandbox")),
            ReadPlacement(Required(element, "placement")),
            ReadAllocation(Required(element, "allocation")),
            ReadStringArray(
                Required(element, "allowedNewRecords"),
                "allowedNewRecords"),
            ReadStringArray(
                Required(element, "allowedExistingRecordChanges"),
                "allowedExistingRecordChanges"),
            ReadPackageFiles(
                Required(element, "allowedPackageFiles"),
                "allowedPackageFiles"),
            ReadWorkspacePath(element, "outputRoot"),
            ReadWorkspacePath(element, "outputZip"),
            ReadString(element, "narrative"),
            ReadExternalAuthorities(
                Required(element, "externalAuthorities")));

    private SkyrimFollowerFinishSourceAuthority ReadSource(
        JsonElement element)
    {
        RequireObject(
            element,
            "source",
            "zip",
            "zipByteLength",
            "zipSha256",
            "packageManifest",
            "packageManifestSha256",
            "plugin",
            "pluginSha256",
            "faceGeomSha256",
            "faceTintSha256");
        PluginName plugin = ReadPluginName(element, "plugin");
        if (!plugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "source.plugin must retain an .esp filename.");
        return new SkyrimFollowerFinishSourceAuthority(
            ReadWorkspacePath(element, "zip"),
            ReadLong(element, "zipByteLength"),
            ReadHash(element, "zipSha256"),
            ReadWorkspacePath(element, "packageManifest"),
            ReadHash(element, "packageManifestSha256"),
            plugin,
            ReadHash(element, "pluginSha256"),
            ReadHash(element, "faceGeomSha256"),
            ReadHash(element, "faceTintSha256"));
    }

    private SkyrimFollowerFinishExternalAuthorities
        ReadExternalAuthorities(JsonElement element)
    {
        RequireObject(
            element,
            "externalAuthorities",
            "placementEvidence",
            "providers");
        return new SkyrimFollowerFinishExternalAuthorities(
            ReadExternalFileAuthority(
                Required(element, "placementEvidence"),
                "externalAuthorities.placementEvidence"),
            ReadPluginProviderAuthorities(
                Required(element, "providers")));
    }

    private SkyrimFollowerFinishFileAuthority ReadExternalFileAuthority(
        JsonElement element,
        string role)
    {
        RequireObject(
            element,
            role,
            "path",
            "byteLength",
            "sha256");
        return new SkyrimFollowerFinishFileAuthority(
            ReadWorkspacePath(element, "path"),
            ReadLong(element, "byteLength"),
            ReadHash(element, "sha256"));
    }

    private ImmutableArray<SkyrimFollowerFinishPluginProviderAuthority>
        ReadPluginProviderAuthorities(JsonElement element)
    {
        const string role = "externalAuthorities.providers";
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{role} must be an array.");
        var builder = ImmutableArray.CreateBuilder<
            SkyrimFollowerFinishPluginProviderAuthority>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            RequireObject(
                item,
                role,
                "plugin",
                "path",
                "byteLength",
                "sha256");
            builder.Add(
                new SkyrimFollowerFinishPluginProviderAuthority(
                    ReadPluginName(item, "plugin"),
                    ReadWorkspacePath(item, "path"),
                    ReadLong(item, "byteLength"),
                    ReadHash(item, "sha256")));
        }
        return builder.ToImmutable();
    }

    private static SkyrimFollowerFinishHairChange ReadHair(
        JsonElement element)
    {
        RequireObject(
            element,
            "hair",
            "colorFormId",
            "oldPackedRgb",
            "newPackedRgb");
        return new SkyrimFollowerFinishHairChange(
            ReadFormId(element, "colorFormId"),
            new SkyrimPackedRgb(ReadUInt(element, "oldPackedRgb")),
            new SkyrimPackedRgb(ReadUInt(element, "newPackedRgb")));
    }

    private static SkyrimFollowerFinishSandbox ReadSandbox(
        JsonElement element)
    {
        RequireObject(
            element,
            "sandbox",
            "procedure",
            "radius",
            "schedule",
            "target",
            "condition");
        return new SkyrimFollowerFinishSandbox(
            ReadString(element, "procedure"),
            ReadInt(element, "radius"),
            ReadString(element, "schedule"),
            ReadFormReference(element, "target"),
            ReadString(element, "condition"));
    }

    private static SkyrimFollowerFinishPlacement ReadPlacement(
        JsonElement element)
    {
        RequireObject(
            element,
            "placement",
            "worldspace",
            "cell",
            "markerBase",
            "actor",
            "anchor");
        return new SkyrimFollowerFinishPlacement(
            ReadFormReference(element, "worldspace"),
            ReadFormReference(element, "cell"),
            ReadFormReference(element, "markerBase"),
            ReadTransform(Required(element, "actor"), "placement.actor"),
            ReadTransform(Required(element, "anchor"), "placement.anchor"));
    }

    private static SkyrimExteriorTransform ReadTransform(
        JsonElement element,
        string role)
    {
        RequireObject(
            element,
            role,
            "x",
            "y",
            "z",
            "rotationX",
            "rotationY",
            "rotationZ");
        return new SkyrimExteriorTransform(
            ReadDouble(element, "x"),
            ReadDouble(element, "y"),
            ReadDouble(element, "z"),
            ReadDouble(element, "rotationX"),
            ReadDouble(element, "rotationY"),
            ReadDouble(element, "rotationZ"));
    }

    private static SkyrimFollowerFinishAllocation ReadAllocation(
        JsonElement element)
    {
        RequireObject(
            element,
            "allocation",
            "package",
            "anchor",
            "actor",
            "nextFormId");
        return new SkyrimFollowerFinishAllocation(
            ReadFormId(element, "package"),
            ReadFormId(element, "anchor"),
            ReadFormId(element, "actor"),
            ReadFormId(element, "nextFormId"));
    }

    private SkyrimFollowerFinishProposal ReadProposal(JsonElement element)
    {
        RequireObject(
            element,
            "proposal",
            "schemaVersion",
            "operation",
            "requestSha256",
            "request",
            "sourceSnapshot",
            "existingRecordChanges",
            "newRecords",
            "nextFormId",
            "rawGroupTreeSurface",
            "allowedPackageFiles",
            "runtimeAuthority");
        return new SkyrimFollowerFinishProposal(
            ReadInt(element, "schemaVersion"),
            ReadString(element, "operation"),
            ReadHash(element, "requestSha256"),
            ReadRequest(Required(element, "request")),
            ReadSnapshot(Required(element, "sourceSnapshot")),
            ReadStringArray(
                Required(element, "existingRecordChanges"),
                "existingRecordChanges"),
            ReadStringArray(
                Required(element, "newRecords"),
                "newRecords"),
            ReadFormId(element, "nextFormId"),
            ReadStringArray(
                Required(element, "rawGroupTreeSurface"),
                "rawGroupTreeSurface"),
            ReadPackageFiles(
                Required(element, "allowedPackageFiles"),
                "allowedPackageFiles"),
            ReadBool(element, "runtimeAuthority"));
    }

    private static SkyrimFollowerFinishPluginSnapshot ReadSnapshot(
        JsonElement element)
    {
        RequireObject(
            element,
            "sourceSnapshot",
            "valid",
            "plugin",
            "pluginSha256",
            "tes4Flags",
            "masters",
            "nextFormId",
            "recordInventory",
            "actorSubrecordDigests",
            "hairPackedRgb",
            "actorHairColor",
            "defaultOutfitNull",
            "factionRanks",
            "relationshipRank",
            "relationshipRankRawDiscriminator",
            "absentSignatures",
            "diagnostics");
        return new SkyrimFollowerFinishPluginSnapshot(
            ReadBool(element, "valid"),
            ReadPluginName(element, "plugin"),
            ReadHash(element, "pluginSha256"),
            ReadUInt(element, "tes4Flags"),
            ReadPluginNameArray(
                Required(element, "masters"),
                "sourceSnapshot.masters"),
            ReadFormId(element, "nextFormId"),
            ReadStringArray(
                Required(element, "recordInventory"),
                "sourceSnapshot.recordInventory"),
            ReadStringArray(
                Required(element, "actorSubrecordDigests"),
                "sourceSnapshot.actorSubrecordDigests"),
            new SkyrimPackedRgb(ReadUInt(element, "hairPackedRgb")),
            ReadFormReference(element, "actorHairColor"),
            ReadBool(element, "defaultOutfitNull"),
            ReadFactionRanks(
                Required(element, "factionRanks"),
                "sourceSnapshot.factionRanks"),
            ReadString(element, "relationshipRank"),
            ReadByte(
                element,
                "relationshipRankRawDiscriminator"),
            ReadStringArray(
                Required(element, "absentSignatures"),
                "sourceSnapshot.absentSignatures"),
            ReadDiagnostics(
                Required(element, "diagnostics"),
                "sourceSnapshot.diagnostics"));
    }

    private void ValidateRequestFileSystem(
        SkyrimFollowerFinishRequest request,
        WorkspacePath documentPath,
        string documentKind,
        SkyrimFollowerFinishDocumentLoadMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new InvalidDataException(
                "The follower-finish document load mode is unsupported.");
        if (request.ExternalAuthorities is null)
            throw new InvalidDataException(
                "The follower-finish request must bind external authorities.");
        ValidateDocumentPathDisjoint(
            request,
            documentPath,
            documentKind);
        ValidateKPath(request.Source.Zip, "source ZIP");
        if (!File.Exists(request.Source.Zip.Value) ||
            Directory.Exists(request.Source.Zip.Value))
            throw new InvalidDataException(
                "The source ZIP must be an existing ordinary K-local file.");
        if (TraversesReparsePoint(request.Source.Zip))
            throw new UnauthorizedAccessException(
                "The source ZIP path may not traverse a reparse point.");
        if (new FileInfo(request.Source.Zip.Value).Length !=
            request.Source.ZipByteLength)
            throw new InvalidDataException(
                "The source ZIP byte length does not match the request.");

        ValidateKPath(
            request.Source.PackageManifest,
            "source package manifest");
        if (!File.Exists(request.Source.PackageManifest.Value) ||
            Directory.Exists(request.Source.PackageManifest.Value))
            throw new InvalidDataException(
                "The source package manifest must be an existing ordinary K-local file.");
        if (TraversesReparsePoint(request.Source.PackageManifest))
            throw new UnauthorizedAccessException(
                "The source package manifest path may not traverse a reparse point.");

        ValidateExternalAuthorityFile(
            request.ExternalAuthorities.PlacementEvidence.Path,
            "placement-evidence authority");
        foreach (SkyrimFollowerFinishPluginProviderAuthority provider in
                 request.ExternalAuthorities.Providers)
        {
            ValidateExternalAuthorityFile(
                provider.Path,
                $"provider authority {provider.Plugin.Value}");
        }

        ValidateOutputPath(request.OutputRoot, "output root", mode);
        ValidateOutputPath(request.OutputZip, "output ZIP", mode);
        if (!request.OutputZip.Value.EndsWith(
                ".zip",
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The follower-finish archive output must end in .zip.");
    }

    private static void ValidateDocumentPathDisjoint(
        SkyrimFollowerFinishRequest request,
        WorkspacePath documentPath,
        string documentKind)
    {
        var nestedPaths = new List<(WorkspacePath Path, string Role)>
        {
            (request.Source.Zip, "source ZIP"),
            (request.Source.PackageManifest, "source package manifest"),
            (request.OutputRoot, "output root"),
            (request.OutputZip, "output ZIP"),
            (
                request.ExternalAuthorities.PlacementEvidence.Path,
                "placement-evidence authority")
        };
        nestedPaths.AddRange(
            request.ExternalAuthorities.Providers.Select(provider =>
                (
                    provider.Path,
                    $"provider authority {provider.Plugin.Value}")));

        foreach ((WorkspacePath nestedPath, string role) in nestedPaths)
        {
            if (IsSameOrDescendant(documentPath, nestedPath) ||
                IsSameOrDescendant(nestedPath, documentPath))
                throw new InvalidDataException(
                    $"The loaded {documentKind} document path must be pairwise disjoint from {role}; same, ancestor, and descendant aliases are refused.");
        }
    }

    private static bool IsSameOrDescendant(
        WorkspacePath candidate,
        WorkspacePath ancestor)
    {
        string candidateValue = Path.TrimEndingDirectorySeparator(
            candidate.Value);
        string ancestorValue = Path.TrimEndingDirectorySeparator(
            ancestor.Value);
        if (string.Equals(
                candidateValue,
                ancestorValue,
                StringComparison.OrdinalIgnoreCase))
            return true;
        if (!candidateValue.StartsWith(
                ancestorValue,
                StringComparison.OrdinalIgnoreCase) ||
            candidateValue.Length <= ancestorValue.Length)
            return false;
        char boundary = candidateValue[ancestorValue.Length];
        return boundary == Path.DirectorySeparatorChar ||
               boundary == Path.AltDirectorySeparatorChar;
    }

    private void ValidateExternalAuthorityFile(
        WorkspacePath path,
        string role)
    {
        ValidateKPath(path, role);
        if (!File.Exists(path.Value) || Directory.Exists(path.Value))
            throw new InvalidDataException(
                $"The {role} must be an existing ordinary K-local file.");
        if (TraversesReparsePoint(path))
            throw new UnauthorizedAccessException(
                $"The {role} path may not traverse a reparse point.");
    }

    private void ValidateOutputPath(
        WorkspacePath path,
        string role,
        SkyrimFollowerFinishDocumentLoadMode mode)
    {
        ValidateKPath(path, role);
        if (mode == SkyrimFollowerFinishDocumentLoadMode.PreWrite &&
            (File.Exists(path.Value) || Directory.Exists(path.Value)))
            throw new InvalidDataException(
                $"The {role} already exists; follower finish never overwrites.");
        if (TraversesReparsePoint(path))
            throw new UnauthorizedAccessException(
                $"The {role} path may not traverse a reparse point.");
    }

    private void ValidateKPath(
        WorkspacePath path,
        string role)
    {
        string root = Path.GetPathRoot(path.Value) ?? string.Empty;
        if (!path.IsUnder(workspaceRoot) ||
            !string.Equals(root, @"K:\", StringComparison.OrdinalIgnoreCase) ||
            path.Value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            path.Value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            path.Value.StartsWith(@"\\", StringComparison.Ordinal) ||
            path.Value.IndexOf(':', 2) >= 0)
            throw new UnauthorizedAccessException(
                $"The {role} must be an ordinary path below the K-only workspace root.");
    }

    private bool TraversesReparsePoint(WorkspacePath path)
    {
        string current = path.Value;
        while (true)
        {
            FileAttributes? attributes = readPathAttributes(current);
            if (attributes.HasValue &&
                attributes.Value.HasFlag(FileAttributes.ReparsePoint))
                return true;
            if (string.Equals(
                    current,
                    workspaceRoot.Value,
                    StringComparison.OrdinalIgnoreCase))
                return false;
            string? parent = Directory.GetParent(current)?.FullName;
            if (parent is null ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return true;
            current = parent;
        }
    }

    private static FileAttributes? ReadPathAttributes(string path)
    {
        return File.Exists(path) || Directory.Exists(path)
            ? File.GetAttributes(path)
            : null;
    }

    private WorkspacePath ReadWorkspacePath(
        JsonElement element,
        string propertyName)
    {
        string value = ReadString(element, propertyName);
        string root = Path.GetPathRoot(value) ?? string.Empty;
        if (!Path.IsPathFullyQualified(value) ||
            !string.Equals(root, @"K:\", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith(@"\\?\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            value.StartsWith(@"\\", StringComparison.Ordinal) ||
            value.IndexOf(':', 2) >= 0 ||
            value.Contains('\0'))
            throw new UnauthorizedAccessException(
                $"{propertyName} must be an ordinary absolute K-local path.");
        var path = new WorkspacePath(value);
        if (!path.IsUnder(workspaceRoot))
            throw new UnauthorizedAccessException(
                $"{propertyName} must remain below the configured workspace root.");
        return path;
    }

    private static ImmutableArray<AssetPath> ReadPackageFiles(
        JsonElement element,
        string role)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{role} must be an array.");
        var builder = ImmutableArray.CreateBuilder<AssetPath>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(
                    $"{role} entries must be strings.");
            string value = item.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value) ||
                value.Contains('\\') ||
                value.StartsWith('/') ||
                value.Contains(':') ||
                value.Split('/').Any(segment =>
                    segment is "" or "." or ".."))
                throw new InvalidDataException(
                    $"{role} entries must be forward-slash relative, traversal-free, and non-ADS.");
            builder.Add(new AssetPath(value));
        }
        ImmutableArray<AssetPath> files = builder.ToImmutable();
        if (files.IsDefaultOrEmpty)
            throw new InvalidDataException(
                $"{role} must be a non-empty array.");
        if (files
                .Select(path => path.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() != files.Length)
            throw new InvalidDataException(
                $"{role} must be case-insensitively unique.");
        return files;
    }

    private static ImmutableArray<FormId> ReadFormIdArray(
        JsonElement element,
        string role)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{role} must be an array.");
        var builder = ImmutableArray.CreateBuilder<FormId>();
        foreach (JsonElement item in element.EnumerateArray())
            builder.Add(ReadFormIdValue(item, role));
        return builder.ToImmutable();
    }

    private static ImmutableArray<string> ReadStringArray(
        JsonElement element,
        string role)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{role} must be an array.");
        var builder = ImmutableArray.CreateBuilder<string>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException(
                    $"{role} entries must be non-empty strings.");
            builder.Add(item.GetString()!);
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<PluginName> ReadPluginNameArray(
        JsonElement element,
        string role)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{role} must be an array.");
        var builder = ImmutableArray.CreateBuilder<PluginName>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new InvalidDataException(
                    $"{role} entries must be plugin strings.");
            builder.Add(ParsePluginName(item.GetString(), role));
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<NpcFactionEntry> ReadFactionRanks(
        JsonElement element,
        string role)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{role} must be an array.");
        var builder = ImmutableArray.CreateBuilder<NpcFactionEntry>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            RequireObject(item, role, "faction", "rank");
            builder.Add(new NpcFactionEntry(
                ReadFormReference(item, "faction"),
                ReadSByte(item, "rank")));
        }
        return builder.ToImmutable();
    }

    private static ImmutableArray<Diagnostic> ReadDiagnostics(
        JsonElement element,
        string role)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{role} must be an array.");
        var builder = ImmutableArray.CreateBuilder<Diagnostic>();
        foreach (JsonElement item in element.EnumerateArray())
        {
            RequireObject(item, role, "code", "severity", "message");
            DiagnosticSeverity severity =
                ReadString(item, "severity") switch
                {
                    "info" => DiagnosticSeverity.Info,
                    "warning" => DiagnosticSeverity.Warning,
                    "error" => DiagnosticSeverity.Error,
                    _ => throw new InvalidDataException(
                        $"{role}.severity is unsupported.")
                };
            builder.Add(new Diagnostic(
                ReadString(item, "code"),
                severity,
                ReadString(item, "message")));
        }
        return builder.ToImmutable();
    }

    private static EditorId ReadEditorId(
        JsonElement element,
        string propertyName)
    {
        string value = ReadString(element, propertyName);
        if (value.Length > 64 ||
            !char.IsAsciiLetter(value[0]) ||
            value.Any(character =>
                !char.IsAsciiLetterOrDigit(character) && character != '_'))
            throw new InvalidDataException(
                $"{propertyName} is not a valid ASCII EditorID.");
        return new EditorId(value);
    }

    private static Sha256Hash ReadHash(
        JsonElement element,
        string propertyName)
    {
        string value = ReadString(element, propertyName);
        if (value.Length != 64 ||
            value.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException(
                $"{propertyName} must be exactly 64 hexadecimal characters.");
        return new Sha256Hash(value);
    }

    private static PluginName ReadPluginName(
        JsonElement element,
        string propertyName) =>
        ParsePluginName(ReadString(element, propertyName), propertyName);

    private static PluginName ParsePluginName(
        string? value,
        string role)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 255 ||
            value.Any(char.IsControl) ||
            value.Contains('/') ||
            value.Contains('\\') ||
            value.Contains(':') ||
            !(value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) ||
              value.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) ||
              value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(
                $"{role} must be one safe plugin filename.");
        return new PluginName(value);
    }

    private static FormId ReadFormId(
        JsonElement element,
        string propertyName) =>
        ReadFormIdValue(Required(element, propertyName), propertyName);

    private static FormId ReadFormIdValue(
        JsonElement element,
        string role)
    {
        if (element.ValueKind != JsonValueKind.String)
            throw new InvalidDataException(
                $"{role} must be a hexadecimal FormID string.");
        string value = element.GetString() ?? string.Empty;
        if (value.Length is < 3 or > 10 ||
            !value.StartsWith("0x", StringComparison.Ordinal) ||
            value[2..].Any(character => !Uri.IsHexDigit(character)) ||
            !FormId.TryParse(value, out FormId formId))
            throw new InvalidDataException(
                $"{role} must be an exact 0x-prefixed FormID.");
        return formId;
    }

    private static FormReference ReadFormReference(
        JsonElement element,
        string propertyName)
    {
        string value = ReadString(element, propertyName);
        int separator = value.IndexOf('|');
        if (separator <= 0 ||
            separator != value.LastIndexOf('|') ||
            separator == value.Length - 1)
            throw new InvalidDataException(
                $"{propertyName} must be Plugin|0xFormID.");
        PluginName plugin = ParsePluginName(
            value[..separator],
            propertyName);
        using JsonDocument formDocument = JsonDocument.Parse(
            JsonSerializer.Serialize(value[(separator + 1)..]));
        FormId formId = ReadFormIdValue(
            formDocument.RootElement,
            propertyName);
        return new FormReference(plugin, formId);
    }

    private static string ReadString(
        JsonElement element,
        string propertyName)
    {
        JsonElement value = Required(element, propertyName);
        if (value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()) ||
            value.GetString()!.Contains('\0'))
            throw new InvalidDataException(
                $"{propertyName} must be a non-empty string.");
        return value.GetString()!;
    }

    private static bool ReadBool(
        JsonElement element,
        string propertyName)
    {
        JsonElement value = Required(element, propertyName);
        if (value.ValueKind is not (
                JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException(
                $"{propertyName} must be a Boolean.");
        return value.GetBoolean();
    }

    private static int ReadInt(
        JsonElement element,
        string propertyName)
    {
        JsonElement value = Required(element, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt32(out int result))
            throw new InvalidDataException(
                $"{propertyName} must be a 32-bit integer.");
        return result;
    }

    private static long ReadLong(
        JsonElement element,
        string propertyName)
    {
        JsonElement value = Required(element, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetInt64(out long result))
            throw new InvalidDataException(
                $"{propertyName} must be a 64-bit integer.");
        return result;
    }

    private static uint ReadUInt(
        JsonElement element,
        string propertyName)
    {
        JsonElement value = Required(element, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetUInt32(out uint result))
            throw new InvalidDataException(
                $"{propertyName} must be an unsigned 32-bit integer.");
        return result;
    }

    private static sbyte ReadSByte(
        JsonElement element,
        string propertyName)
    {
        int value = ReadInt(element, propertyName);
        if (value is < sbyte.MinValue or > sbyte.MaxValue)
            throw new InvalidDataException(
                $"{propertyName} must fit a signed byte.");
        return (sbyte)value;
    }

    private static byte ReadByte(
        JsonElement element,
        string propertyName)
    {
        uint value = ReadUInt(element, propertyName);
        if (value > byte.MaxValue)
            throw new InvalidDataException(
                $"{propertyName} must fit an unsigned byte.");
        return (byte)value;
    }

    private static double ReadDouble(
        JsonElement element,
        string propertyName)
    {
        JsonElement value = Required(element, propertyName);
        if (value.ValueKind != JsonValueKind.Number ||
            !value.TryGetDouble(out double result) ||
            !double.IsFinite(result))
            throw new InvalidDataException(
                $"{propertyName} must be a finite number.");
        return result;
    }

    private static JsonElement Required(
        JsonElement element,
        string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out JsonElement value))
            throw new InvalidDataException(
                $"Required JSON property '{propertyName}' is absent.");
        return value;
    }

    private static void RequireObject(
        JsonElement element,
        string role,
        params string[] allowedProperties)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"{role} must be an object.");
        var allowed = allowedProperties.ToHashSet(StringComparer.Ordinal);
        foreach (JsonProperty property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                throw new InvalidDataException(
                    $"Unknown JSON property '{role}.{property.Name}'.");
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                    throw new InvalidDataException(
                        $"Duplicate JSON property '{property.Name}' is not accepted.");
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }

    private static bool IsValidationException(Exception exception) =>
        exception is IOException or JsonException or InvalidDataException or
            ArgumentException or FormatException or OverflowException;

    private sealed record BoundFile(byte[] Bytes, Sha256Hash Sha256);
}
