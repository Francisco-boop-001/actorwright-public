using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Writes the hash-bound external NIF/TRI/DDS contract observed by the
/// Manager-owned selected-preset transaction. It never copies provider assets.
/// </summary>
public sealed class RaceMenuSelectedDependencyManifestWriter(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : IRaceMenuSelectedDependencyManifestWriter
{
    private const int MaximumManifestBytes = 1 * 1024 * 1024;

    public async ValueTask<RaceMenuSelectedDependencyManifestWriteResult> WriteAsync(
        RaceMenuSelectedDependencyManifestWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency>
            externalInstallDependencies;
        try
        {
            externalInstallDependencies = NormalizeExternalDependencies(
                request, diagnostics);
        }
        catch (Exception exception) when (exception is ArgumentException or
                                           InvalidDataException or
                                           NotSupportedException or
                                           JsonException)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-external",
                exception.Message));
            return Refused(diagnostics);
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        request = request with
        {
            ExternalInstallDependencies = externalInstallDependencies
        };

        NormalizedAssets normalized;
        try
        {
            normalized = NormalizeAssets(request, diagnostics);
        }
        catch (ArgumentException exception)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-path", exception.Message));
            return Refused(diagnostics);
        }
        if (normalized.Dependencies.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-empty",
                "The selected preset must retain at least one exact external NIF, TRI, or DDS provider."));
        }
        if (!externalInstallDependencies.IsDefaultOrEmpty &&
            normalized.Dependencies.Any(item =>
                item.Kind == AssetProviderKind.Archive &&
                (item.ContentLength <= 0 || item.ContentLength > (1L << 40))))
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-archive-length",
                "Schema 3 asset and provider evidence require positive portable byte lengths."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        normalized = await VerifyInputsAsync(
            request, normalized, diagnostics, cancellationToken).ConfigureAwait(false);
        if (!externalInstallDependencies.IsDefaultOrEmpty &&
            normalized.Dependencies.Any(item =>
                item.ContentLength <= 0 || item.ContentLength > (1L << 40) ||
                item.ProviderByteLength <= 0 ||
                item.ProviderByteLength > (1L << 40)))
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-archive-length",
                "Schema 3 asset and provider evidence require positive portable byte lengths."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        string dependencyId = BuildDependencyId(request, normalized);
        byte[] bytes = Serialize(
            dependencyId, request, normalized, out int schemaVersion);
        if (bytes.Length is <= 0 or > MaximumManifestBytes)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-size",
                $"Generated dependency evidence must be 1-{MaximumManifestBytes} bytes."));
            return Refused(diagnostics);
        }

        try
        {
            Sha256Hash hash = await WriteAndReopenAsync(
                request.Destination,
                bytes,
                dependencyId,
                request.PresetSha256,
                schemaVersion,
                request.RecordDraft.HeadParts.Length,
                normalized.Dependencies.Count(item =>
                    item.Kind == AssetProviderKind.Loose),
                normalized.Dependencies.Where(item =>
                    item.Kind == AssetProviderKind.Archive)
                    .Select(item => ProviderKey(item))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Count(),
                normalized.ProviderSidecars.Length,
                externalInstallDependencies.Length,
                cancellationToken).ConfigureAwait(false);
            if (schemaVersion == 3)
            {
                RaceMenuSelectedDependencyManifestReadResult readback =
                    await new RaceMenuSelectedDependencyManifestReader().ReadAsync(
                        request.Destination,
                        hash,
                        PackageRootFor(request.Destination),
                        cancellationToken).ConfigureAwait(false);
                if (readback.Artifact is null)
                    throw new InvalidDataException(string.Join(
                        " | ", readback.Diagnostics.Select(item => item.Message)));
                CompareSchema3Readback(
                    readback.Artifact,
                    dependencyId,
                    hash,
                    request.PresetSha256,
                    request.RecordDraft.HeadParts.Length,
                    normalized.Dependencies.Count(item =>
                        item.Kind == AssetProviderKind.Loose),
                    normalized.Dependencies.Where(item =>
                        item.Kind == AssetProviderKind.Archive)
                        .Select(item => ProviderKey(item))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count(),
                    externalInstallDependencies);
            }
            int looseCount = normalized.Dependencies.Count(item =>
                item.Kind == AssetProviderKind.Loose);
            int archiveCount = normalized.Dependencies
                .Where(item => item.Kind == AssetProviderKind.Archive)
                .Select(ProviderKey)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count();
            return new RaceMenuSelectedDependencyManifestWriteResult(
                true,
                new RaceMenuSelectedDependencyManifestArtifact(
                    dependencyId,
                    request.Destination,
                    hash,
                    request.RecordDraft.HeadParts.Length,
                    looseCount,
                    archiveCount,
                    RuntimeAuthority: false)
                {
                    SchemaVersion = schemaVersion,
                    PresetSha256 = request.PresetSha256,
                    ExternalInstallDependencies = externalInstallDependencies
                },
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDeleteOwnedOutput(request.Destination);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           JsonException)
        {
            TryDeleteOwnedOutput(request.Destination);
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-write", exception.Message));
            return Refused(diagnostics);
        }
    }

    private void ValidateRequest(
            RaceMenuSelectedDependencyManifestWriteRequest request,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.PresetSha256 != request.RecordDraft.Preset.SourceHash ||
            request.RecordDraft.RuntimeAuthority ||
            request.RecordDraft.HeadParts.IsDefaultOrEmpty ||
            request.FaceGenDependencies.IsDefault ||
            request.ExternalTextures.IsDefault)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-input",
                "Dependency evidence requires one matching static preset draft and initialized provider arrays."));
        }
        string? parent = Path.GetDirectoryName(request.Destination.Value);
        diagnostics.AddRange(workspacePolicy.Evaluate(
            labRoot, request.Destination));
        if (!request.Destination.IsUnder(labRoot) ||
            !string.Equals(Path.GetExtension(request.Destination.Value), ".json",
                StringComparison.OrdinalIgnoreCase) ||
            parent is null || !Directory.Exists(parent) ||
            File.Exists(request.Destination.Value) ||
            Directory.Exists(request.Destination.Value))
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-destination",
                "Destination must be one absent JSON file under an existing K-local transaction directory."));
        }
        if (!request.ExternalInstallDependencies.IsDefaultOrEmpty &&
            parent is not null)
        {
            try
            {
                string packageRoot = PackageRootFor(request.Destination).Value;
                string relative = Path.GetRelativePath(
                        packageRoot, request.Destination.Value)
                    .Replace(Path.DirectorySeparatorChar, '/');
                if (!Directory.Exists(packageRoot) ||
                    !string.Equals(
                        relative,
                        RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath,
                        StringComparison.Ordinal))
                {
                    diagnostics.Add(Error(
                        "racemenu-selected-dependencies-locator",
                        "Schema 3 evidence must use the canonical package locator."));
                }
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(Error(
                    "racemenu-selected-dependencies-locator",
                    exception.Message));
            }
        }
    }

    private static NormalizedAssets NormalizeAssets(
        RaceMenuSelectedDependencyManifestWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var byPath = new Dictionary<string, DependencyAsset>(
            StringComparer.OrdinalIgnoreCase);
        var sidecarsByPath = new Dictionary<string, DependencyAsset>(
            StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimAssetAuthority authority in request.FaceGenDependencies)
        {
            AddAsset(byPath, new DependencyAsset(
                authority.AssetPath,
                authority.ProviderId,
                authority.ProviderKind,
                authority.ProviderPath,
                authority.ProviderSha256,
                authority.ContentSha256,
                authority.ContentLength,
                0), diagnostics);
        }
        foreach (SkyrimAssetAuthority authority in
                 request.ExternalProviderSidecars)
        {
            AddSidecar(sidecarsByPath, new DependencyAsset(
                authority.AssetPath,
                authority.ProviderId,
                authority.ProviderKind,
                 authority.ProviderPath,
                 authority.ProviderSha256,
                 authority.ContentSha256,
                 authority.ContentLength,
                 0), diagnostics);
        }
        foreach (RaceMenuNpcExternalTextureAuthority authority in
                 request.ExternalTextures)
        {
            AssetProviderKind kind = string.Equals(
                authority.Provider, "loose", StringComparison.OrdinalIgnoreCase)
                ? AssetProviderKind.Loose
                : AssetProviderKind.Archive;
            var candidate = new DependencyAsset(
                authority.DataRelativePath,
                authority.Provider,
                kind,
                authority.Source,
                authority.ExpectedSourceSha256,
                authority.ExpectedMemberSha256,
                0,
                0);
            if (kind == AssetProviderKind.Archive)
            {
                candidate = BindArchiveExternalTextureAuthority(
                    authority,
                    candidate,
                    request.ExternalInstallDependencies,
                    diagnostics);
            }
            AddAsset(byPath, candidate, diagnostics);
        }
        return new NormalizedAssets(
            byPath.Values
                .OrderBy(item => item.GamePath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray(),
            sidecarsByPath.Values
                .OrderBy(item => item.GamePath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray());
    }

    private static DependencyAsset BindArchiveExternalTextureAuthority(
        RaceMenuNpcExternalTextureAuthority authority,
        DependencyAsset candidate,
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency>
            externalInstallDependencies,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        // External texture authorities carry the provider/member hashes but no
        // member length. Schema 3 already binds that fact in the descriptor's
        // archive-member authority; require the exact binding here, then the
        // normal verification pass reopens and hashes the provider file.
        ExternalHeadPartAssetDependency[] matches =
            externalInstallDependencies
                .SelectMany(item => item.Descriptor.Assets)
                .Where(item => string.Equals(
                    item.Path.Value,
                    authority.DataRelativePath.Value,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
        if (matches.Length == 0)
            return candidate;

        ExternalHeadPartAssetDependency[] archiveMatches = matches
            .Where(item => item.ArchiveMember is not null)
            .Where(item =>
            {
                ExternalHeadPartArchiveMemberAuthority archive =
                    item.ArchiveMember!;
                return string.Equals(
                           archive.ArchivePath.Value,
                           authority.Provider,
                           StringComparison.OrdinalIgnoreCase) &&
                       archive.ArchiveSha256 == authority.ExpectedSourceSha256 &&
                       archive.MemberPath == item.Path &&
                       archive.MemberSha256 == authority.ExpectedMemberSha256 &&
                       item.Sha256 == authority.ExpectedMemberSha256;
            })
            .ToArray();
        if (archiveMatches.Length != matches.Length ||
            archiveMatches.Length == 0)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-archive-authority",
                $"Archive texture {authority.DataRelativePath} does not exactly match its descriptor archive authority."));
            return candidate;
        }

        ExternalHeadPartAssetDependency first = archiveMatches[0];
        ExternalHeadPartArchiveMemberAuthority firstArchive =
            first.ArchiveMember!;
        if (archiveMatches.Any(item =>
                item.ByteLength != first.ByteLength ||
                item.ArchiveMember!.ArchiveByteLength !=
                    firstArchive.ArchiveByteLength ||
                item.ArchiveMember.MemberByteLength !=
                    firstArchive.MemberByteLength))
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-archive-authority",
                $"Archive texture {authority.DataRelativePath} has conflicting descriptor member lengths."));
            return candidate;
        }

        if (first.ByteLength <= 0 || firstArchive.ArchiveByteLength <= 0 ||
            firstArchive.MemberByteLength <= 0 ||
            first.ByteLength != firstArchive.MemberByteLength)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-archive-authority",
                $"Archive texture {authority.DataRelativePath} lacks a positive, consistent descriptor member length."));
            return candidate;
        }

        return candidate with
        {
            ContentLength = first.ByteLength,
            ProviderByteLength = firstArchive.ArchiveByteLength
        };
    }

    private static ImmutableArray<
        RaceMenuSelectedDependencyManifestExternalInstallDependency>
        NormalizeExternalDependencies(
            RaceMenuSelectedDependencyManifestWriteRequest request,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var byDescriptor = new Dictionary<string,
            RaceMenuSelectedDependencyManifestExternalInstallDependency>(
            StringComparer.Ordinal);
        foreach (RaceMenuSelectedDependencyManifestExternalInstallDependency
                     group in request.ExternalInstallDependencies)
        {
            try
            {
                _ = ExternalHeadPartDependencyDescriptorCodec
                    .SerializeDescriptor(group.Descriptor);
                _ = ExternalHeadPartDependencyDescriptorCodec
                    .SerializeAttestation(group.Attestation);
                RequireExternal(group.Descriptor.Assets.Length > 0,
                    "External dependency descriptors require at least one supported asset.");
                RequireExternal(group.Descriptor.Assets.All(asset =>
                        RaceMenuSelectedDependencyManifestAssetRules.IsSupported(
                            asset.Path)),
                    "External dependency descriptors contain an unsupported asset route.");
                RequireExternal(group.Descriptor.DescriptorId ==
                                group.Attestation.DescriptorId,
                    "External dependency attestation does not bind its descriptor.");
                RequireExternal(group.FaceGeomPath ==
                                group.Attestation.OutputFaceGeomPath &&
                                group.FaceGeomSha256 ==
                                group.Attestation.OutputFaceGeomSha256 &&
                                group.FaceGeomByteLength ==
                                group.Attestation.OutputFaceGeomByteLength,
                    "External dependency FaceGeom evidence disagrees with its attestation.");
                RequireExternal(group.FaceGeomPath.Value.StartsWith(
                                    "Data/", StringComparison.Ordinal),
                    "External dependency FaceGeom evidence must be package-relative under Data/.");
                ValidateOutputPluginBinding(group);
                RequireExternal(byDescriptor.TryAdd(
                                    group.Descriptor.DescriptorId.Value, group),
                    "Duplicate external dependency descriptor ID.");
            }
            catch (Exception exception) when (exception is ArgumentException or
                                               InvalidDataException or
                                               NotSupportedException or
                                               JsonException)
            {
                diagnostics.Add(Error(
                    "racemenu-selected-dependencies-external",
                    exception.Message));
            }
        }

        return byDescriptor.Values
            .OrderBy(item => item.Descriptor.DescriptorId.Value,
                StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static void ValidateOutputPluginBinding(
        RaceMenuSelectedDependencyManifestExternalInstallDependency group)
    {
        var output = group.OutputPlugin;
        _ = new PluginName(output.Plugin.Value);
        RequireExternal(output.ByteLength > 0 && output.ByteLength <= (1L << 40),
            "External output plugin byte length must be positive.");
        RequireExternal(output.Masters.Length is > 0 and <= 512 &&
                        output.PnamBindings.Length is > 0 and <= 512,
            "External output plugin bindings exceed the portable bound.");
        RequireExternal(output.Sha256.Value.Length == 64,
            "External output plugin hash must be SHA-256.");
        var masters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (PluginName master in output.Masters)
            RequireExternal(masters.Add(master.Value),
                "External output plugin masters must be unique.");
        var requiredMasters = group.Descriptor.Members
            .Select(item => item.RequiredOutputMaster.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        RequireExternal(requiredMasters.All(item => masters.Contains(item)),
            "External output plugin masters do not cover the descriptor graph.");
        RequireExternal(output.PnamBindings.Length ==
                        group.Descriptor.Members.Length,
            "External output PNAM binding count does not match the descriptor graph.");
        RequireExternal(output.PnamBindings.SequenceEqual(
                            group.Descriptor.Members.Select(item => item.WinningForm)),
            "External output PNAM bindings do not match descriptor member order.");
    }

    private static void RequireExternal(bool condition, string message)
    {
        if (!condition) throw new InvalidDataException(message);
    }

    private static void AddAsset(
        Dictionary<string, DependencyAsset> assets,
        DependencyAsset candidate,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string path = candidate.GamePath.Value;
        if (!RaceMenuSelectedDependencyManifestAssetRules.IsSupported(
                candidate.GamePath))
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-asset",
                $"Selected dependency '{path}' is not a canonical NIF, TRI, or DDS route."));
            return;
        }
        if (!assets.TryGetValue(path, out DependencyAsset? existing))
        {
            assets.Add(path, candidate);
            return;
        }
        bool sameIdentity = existing.Kind == candidate.Kind &&
            string.Equals(existing.Provider, candidate.Provider,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                existing.EvidencePath.Value,
                candidate.EvidencePath.Value,
                StringComparison.OrdinalIgnoreCase) &&
            existing.ProviderSha256 == candidate.ProviderSha256 &&
            existing.ContentSha256 == candidate.ContentSha256;
        if (sameIdentity &&
            CompatibleUninitializedLength(
                existing.ContentLength, candidate.ContentLength) &&
            CompatibleUninitializedLength(
                existing.ProviderByteLength, candidate.ProviderByteLength))
        {
            assets[path] = existing with
            {
                ContentLength = MergeLength(
                    existing.ContentLength, candidate.ContentLength),
                ProviderByteLength = MergeLength(
                    existing.ProviderByteLength, candidate.ProviderByteLength)
            };
            return;
        }
        if (existing.Kind != candidate.Kind ||
            !string.Equals(existing.Provider, candidate.Provider,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                existing.EvidencePath.Value,
                candidate.EvidencePath.Value,
                StringComparison.OrdinalIgnoreCase) ||
            existing.ProviderSha256 != candidate.ProviderSha256 ||
            existing.ContentSha256 != candidate.ContentSha256 ||
            existing.ContentLength != candidate.ContentLength ||
            existing.ProviderByteLength != candidate.ProviderByteLength)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-conflict",
                $"Selected dependency '{path}' has conflicting winning-provider evidence."));
        }
    }

    private static bool CompatibleUninitializedLength(
        long existing,
        long candidate) =>
        existing == candidate || existing == 0 || candidate == 0;

    private static long MergeLength(long existing, long candidate) =>
        existing == 0 ? candidate : existing;

    private static void AddSidecar(
        Dictionary<string, DependencyAsset> sidecars,
        DependencyAsset candidate,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string path = candidate.GamePath.Value;
        if (candidate.Kind != AssetProviderKind.Loose ||
            !path.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) ||
            !path.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) ||
            path.Contains('\\') || path.Contains("..", StringComparison.Ordinal) ||
            path.Contains(':'))
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-sidecar",
                $"External provider sidecar '{path}' must be a canonical loose meshes-relative XML route."));
            return;
        }
        if (!sidecars.TryGetValue(path, out DependencyAsset? existing))
        {
            sidecars.Add(path, candidate);
            return;
        }
        if (!string.Equals(existing.EvidencePath.Value,
                candidate.EvidencePath.Value, StringComparison.OrdinalIgnoreCase) ||
            existing.ProviderSha256 != candidate.ProviderSha256 ||
            existing.ContentSha256 != candidate.ContentSha256 ||
            existing.ContentLength != candidate.ContentLength ||
            existing.ProviderByteLength != candidate.ProviderByteLength)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-sidecar-conflict",
                $"External provider sidecar '{path}' has conflicting winning-provider evidence."));
        }
    }

    private async ValueTask<NormalizedAssets> VerifyInputsAsync(
        RaceMenuSelectedDependencyManifestWriteRequest request,
        NormalizedAssets normalized,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        foreach (RaceMenuPresetHeadPartAuthority headPart in
                 request.RecordDraft.HeadParts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _ = await VerifyFileAsync(
                headPart.Binding.ProviderPlugin,
                headPart.Binding.ProviderPluginSha256,
                $"headpart provider {headPart.Binding.Reference}",
                diagnostics, cancellationToken).ConfigureAwait(false);
            if (HasErrors(diagnostics)) return normalized;
        }

        var dependencies = ImmutableArray.CreateBuilder<DependencyAsset>();
        foreach (DependencyAsset asset in normalized.Dependencies)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DependencyAsset updatedAsset = asset;
            ProviderObservation? observation = await VerifyFileAsync(
                asset.EvidencePath,
                asset.ProviderSha256,
                $"asset provider {asset.GamePath}",
                diagnostics, cancellationToken).ConfigureAwait(false);
            if (HasErrors(diagnostics) || observation is null) return normalized;
            if (asset.Kind == AssetProviderKind.Loose)
            {
                if (observation.Hash != asset.ContentSha256)
                {
                    diagnostics.Add(Error(
                        "racemenu-selected-dependencies-content-hash",
                        $"Loose asset {asset.GamePath} content hash does not match its source."));
                    return normalized;
                }
                if (asset.ContentLength > 0 &&
                    asset.ContentLength != observation.Length)
                {
                    diagnostics.Add(Error(
                        "racemenu-selected-dependencies-content-length",
                        $"Loose asset {asset.GamePath} content length does not match its source."));
                    return normalized;
                }
                updatedAsset = asset with { ContentLength = observation.Length };
            }
            else if (updatedAsset.ContentLength <= 0)
            {
                diagnostics.Add(Error(
                    "racemenu-selected-dependencies-archive-length",
                    $"Archive member {asset.GamePath} lacks an authoritative member length."));
                return normalized;
            }
            dependencies.Add(updatedAsset with {
                ProviderByteLength = observation.Length
            });
        }

        var sidecars = ImmutableArray.CreateBuilder<DependencyAsset>();
        foreach (DependencyAsset sidecar in normalized.ProviderSidecars)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ProviderObservation? observation = await VerifyFileAsync(
                sidecar.EvidencePath,
                sidecar.ProviderSha256,
                $"external provider sidecar {sidecar.GamePath}",
                diagnostics, cancellationToken).ConfigureAwait(false);
            if (HasErrors(diagnostics) || observation is null) return normalized;
            if (observation.Hash != sidecar.ContentSha256 ||
                observation.Length != sidecar.ContentLength)
            {
                diagnostics.Add(Error(
                    "racemenu-selected-dependencies-sidecar-content",
                    $"External provider sidecar {sidecar.GamePath} metadata does not match its source."));
                return normalized;
            }
            sidecars.Add(sidecar with { ProviderByteLength = observation.Length });
        }

        return new NormalizedAssets(
            dependencies.ToImmutable(), sidecars.ToImmutable());
    }

    private async ValueTask<ProviderObservation?> VerifyFileAsync(
        WorkspacePath path,
        Sha256Hash expected,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, path));
        if (HasErrors(diagnostics) || ContainsReparseBetween(labRoot.Value, path.Value))
        {
            if (!HasErrors(diagnostics))
                diagnostics.Add(Error(
                    "racemenu-selected-dependencies-provider-file",
                    $"The {role} path contains a reparse point."));
            return null;
        }
        try
        {
            await using var stream = new FileStream(
                path.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = stream.Length;
            if (length <= 0 || length > (1L << 40))
            {
                diagnostics.Add(Error(
                    "racemenu-selected-dependencies-provider-file",
                    $"The {role} is not an ordinary non-empty K-local file."));
                return null;
            }
            FileAttributes attributes = File.GetAttributes(path.Value);
            if (attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error(
                    "racemenu-selected-dependencies-provider-file",
                    $"The {role} is not an ordinary non-empty K-local file."));
                return null;
            }
            Sha256Hash actual = new(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken)
                    .ConfigureAwait(false)));
            if (actual != expected)
            {
                diagnostics.Add(Error(
                    "racemenu-selected-dependencies-provider-hash",
                    $"The {role} hash {actual} does not match {expected}."));
                return null;
            }
            return new ProviderObservation(actual, length);
        }
        catch (FileNotFoundException)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-provider-file",
                $"The {role} is not an ordinary non-empty K-local file."));
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            diagnostics.Add(Error(
                "racemenu-selected-dependencies-provider-file",
                $"The {role} is not an ordinary non-empty K-local file."));
            return null;
        }
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

    private byte[] Serialize(
        string dependencyId,
        RaceMenuSelectedDependencyManifestWriteRequest request,
        NormalizedAssets normalized,
        out int schemaVersion)
    {
        schemaVersion = request.ExternalInstallDependencies.IsDefaultOrEmpty
            ? normalized.ProviderSidecars.IsDefaultOrEmpty ? 1 : 2
            : 3;
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(
                   buffer,
                   new JsonWriterOptions
                   {
                       Indented = true,
                       SkipValidation = false
                   }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", schemaVersion);
            writer.WriteString("id", dependencyId);
            if (schemaVersion == 3)
                writer.WriteString("presetSha256", request.PresetSha256.Value);
            writer.WriteStartArray("headParts");
            foreach (RaceMenuPresetHeadPartAuthority headPart in
                     request.RecordDraft.HeadParts)
            {
                writer.WriteStartObject();
                writer.WriteString(
                    "formKey",
                    $"{headPart.Binding.Reference.Plugin.Value}|{headPart.Binding.Reference.FormId.Value:X6}");
                writer.WriteString(
                    "type",
                    headPart.Binding.HeadPartType?.ToWireName() ?? "misc");
                writer.WriteString(
                    "pluginEvidence",
                    Relative(headPart.Binding.ProviderPlugin));
                writer.WriteString(
                    "pluginSha256",
                    headPart.Binding.ProviderPluginSha256.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("looseAssets");
            foreach (DependencyAsset asset in normalized.Dependencies.Where(item =>
                         item.Kind == AssetProviderKind.Loose))
            {
                writer.WriteStartObject();
                writer.WriteString("gamePath", asset.GamePath.Value);
                writer.WriteString("provider", asset.Provider);
                writer.WriteString("evidencePath", Relative(asset.EvidencePath));
                writer.WriteString("sha256", asset.ContentSha256.Value);
                if (schemaVersion == 3)
                    writer.WriteNumber("byteLength", asset.ContentLength);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            writer.WriteStartArray("archives");
            foreach (IGrouping<string, DependencyAsset> group in normalized.Dependencies
                         .Where(item => item.Kind == AssetProviderKind.Archive)
                         .GroupBy(ProviderKey, StringComparer.OrdinalIgnoreCase)
                         .OrderBy(item => item.Key, StringComparer.OrdinalIgnoreCase))
            {
                DependencyAsset provider = group.First();
                writer.WriteStartObject();
                writer.WriteString("provider", provider.Provider);
                writer.WriteString(
                    "evidencePath", Relative(provider.EvidencePath));
                writer.WriteString("sha256", provider.ProviderSha256.Value);
                if (schemaVersion == 3)
                    writer.WriteNumber("byteLength", provider.ProviderByteLength);
                writer.WriteStartArray("members");
                foreach (DependencyAsset member in group.OrderBy(
                             item => item.GamePath.Value,
                             StringComparer.OrdinalIgnoreCase))
                {
                    if (schemaVersion == 3)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("gamePath", member.GamePath.Value);
                        writer.WriteString("sha256", member.ContentSha256.Value);
                        writer.WriteNumber("byteLength", member.ContentLength);
                        writer.WriteEndObject();
                    }
                    else
                        writer.WriteStringValue(member.GamePath.Value);
                }
                writer.WriteEndArray();
                writer.WriteEndObject();
            }
            writer.WriteEndArray();

            if (schemaVersion is 2 or 3 &&
                !normalized.ProviderSidecars.IsDefaultOrEmpty)
            {
                writer.WriteStartArray("externalProviderSidecars");
                foreach (DependencyAsset sidecar in normalized.ProviderSidecars)
                {
                    writer.WriteStartObject();
                    writer.WriteString("gamePath", sidecar.GamePath.Value);
                    writer.WriteString("provider", sidecar.Provider);
                    writer.WriteString("evidencePath", Relative(sidecar.EvidencePath));
                    writer.WriteString("sha256", sidecar.ContentSha256.Value);
                    if (schemaVersion == 3)
                        writer.WriteNumber("byteLength", sidecar.ContentLength);
                    writer.WriteEndObject();
                }
                writer.WriteEndArray();
            }
            if (schemaVersion == 3)
            {
                writer.WriteStartArray("externalInstallDependencies");
                foreach (RaceMenuSelectedDependencyManifestExternalInstallDependency
                             group in request.ExternalInstallDependencies)
                    WriteExternalInstallDependency(writer, group);
                writer.WriteEndArray();
            }
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteExternalInstallDependency(
        Utf8JsonWriter writer,
        RaceMenuSelectedDependencyManifestExternalInstallDependency group)
    {
        writer.WriteStartObject();
        writer.WriteString("descriptorId", group.Descriptor.DescriptorId.Value);
        writer.WritePropertyName("descriptor");
        writer.WriteRawValue(
            Encoding.UTF8.GetString(
                ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                    group.Descriptor)),
            skipInputValidation: true);
        writer.WritePropertyName("attestation");
        writer.WriteRawValue(
            Encoding.UTF8.GetString(
                ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                    group.Attestation)),
            skipInputValidation: true);
        writer.WriteStartObject("outputPlugin");
        writer.WriteString("plugin", group.OutputPlugin.Plugin.Value);
        writer.WriteString("sha256", group.OutputPlugin.Sha256.Value);
        writer.WriteNumber("byteLength", group.OutputPlugin.ByteLength);
        writer.WriteStartArray("masters");
        foreach (PluginName master in group.OutputPlugin.Masters)
            writer.WriteStringValue(master.Value);
        writer.WriteEndArray();
        writer.WriteStartArray("pnam");
        foreach (FormReference reference in group.OutputPlugin.PnamBindings)
            writer.WriteStringValue(reference.ToString());
        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.WriteStartObject("faceGeom");
        writer.WriteString("path", group.FaceGeomPath.Value);
        writer.WriteString("sha256", group.FaceGeomSha256.Value);
        writer.WriteNumber("byteLength", group.FaceGeomByteLength);
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private string BuildDependencyId(
        RaceMenuSelectedDependencyManifestWriteRequest request,
        NormalizedAssets normalized)
    {
        if (!request.ExternalInstallDependencies.IsDefaultOrEmpty)
            return BuildSchema3DependencyId(request, normalized);

        string identity = string.Join(
            '|',
            request.PresetSha256.Value,
            string.Join(
                ';',
                request.RecordDraft.HeadParts.Select(item =>
                    $"{item.Binding.Reference}:{item.Binding.ProviderPluginSha256.Value}")),
             string.Join(
                 ';',
                 normalized.Dependencies.Select(item =>
                     $"{item.GamePath.Value}:{item.ContentSha256.Value}")),
             string.Join(
                 ';',
                 normalized.ProviderSidecars.Select(item =>
                     $"{item.GamePath.Value}:{item.ContentSha256.Value}")));
        string hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "selected-preset-dependencies-" + hash[..24].ToLowerInvariant();
    }

    private string BuildSchema3DependencyId(
        RaceMenuSelectedDependencyManifestWriteRequest request,
        NormalizedAssets normalized)
    {
        string identity = string.Join(
            '|',
            "schema3",
            request.PresetSha256.Value,
            string.Join(
                ';',
                request.RecordDraft.HeadParts.Select(item =>
                    string.Join(':',
                        $"{item.Binding.Reference.Plugin.Value}|{item.Binding.Reference.FormId.Value:X6}",
                        item.Binding.HeadPartType?.ToWireName() ?? "misc",
                        Relative(item.Binding.ProviderPlugin),
                        item.Binding.ProviderPluginSha256.Value))),
            string.Join(
                ';',
                normalized.Dependencies
                    .OrderBy(item => item.GamePath.Value,
                        StringComparer.OrdinalIgnoreCase)
                    .ThenBy(item => item.GamePath.Value,
                        StringComparer.Ordinal)
                    .Select(item =>
                        item.Kind == AssetProviderKind.Archive
                            ? $"{item.GamePath.Value}:{item.Provider}:" +
                              $"{item.ProviderSha256.Value}:{item.ProviderByteLength}:" +
                              $"{item.ContentSha256.Value}:{item.ContentLength}"
                            : $"{item.GamePath.Value}:{item.Provider}:" +
                              $"{item.ContentSha256.Value}:{item.ContentLength}")),
            string.Join(
                ';',
                normalized.ProviderSidecars.Select(item =>
                    $"{item.GamePath.Value}:{item.Provider}:{item.ContentSha256.Value}:{item.ContentLength}")),
            string.Join(
                ';',
                request.ExternalInstallDependencies
                    .OrderBy(item => item.Descriptor.DescriptorId.Value,
                        StringComparer.Ordinal)
                    .Select(item =>
                    {
                        string descriptor = Convert.ToHexString(
                            ExternalHeadPartDependencyDescriptorCodec
                                .SerializeDescriptor(item.Descriptor));
                        string attestation = Convert.ToHexString(
                            ExternalHeadPartDependencyDescriptorCodec
                                .SerializeAttestation(item.Attestation));
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

    private static async ValueTask<Sha256Hash> WriteAndReopenAsync(
        WorkspacePath destination,
        byte[] bytes,
        string dependencyId,
        Sha256Hash presetSha256,
        int schemaVersion,
        int headPartCount,
        int looseAssetCount,
        int archiveCount,
        int sidecarCount,
        int externalDependencyCount,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(
                         destination.Value,
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        byte[] reopened = await File.ReadAllBytesAsync(
            destination.Value, cancellationToken).ConfigureAwait(false);
        if (!reopened.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException(
                "Selected dependency evidence did not reopen byte-for-byte.");
        using JsonDocument document = JsonDocument.Parse(reopened);
        JsonElement root = document.RootElement;
        var expectedProperties = schemaVersion == 1
            ? new HashSet<string>(StringComparer.Ordinal)
            {
                "schemaVersion", "id", "headParts", "looseAssets", "archives"
            }
            : schemaVersion == 2
                ? new HashSet<string>(StringComparer.Ordinal)
                {
                    "schemaVersion", "id", "headParts", "looseAssets", "archives",
                    "externalProviderSidecars"
                }
                : new HashSet<string>(StringComparer.Ordinal)
                {
                    "schemaVersion", "id", "presetSha256", "headParts", "looseAssets", "archives",
                    "externalInstallDependencies"
                };
        if (schemaVersion == 3 && sidecarCount > 0)
            expectedProperties.Add("externalProviderSidecars");
        if (!root.EnumerateObject().Select(item => item.Name).ToHashSet(
                StringComparer.Ordinal).SetEquals(expectedProperties) ||
            root.GetProperty("schemaVersion").GetInt32() != schemaVersion ||
            !string.Equals(
                root.GetProperty("id").GetString(),
                dependencyId,
                StringComparison.Ordinal) ||
            schemaVersion == 3 &&
                !string.Equals(
                    root.GetProperty("presetSha256").GetString(),
                    presetSha256.Value,
                    StringComparison.Ordinal) ||
            root.GetProperty("headParts").GetArrayLength() != headPartCount ||
            root.GetProperty("looseAssets").GetArrayLength() != looseAssetCount ||
            root.GetProperty("archives").GetArrayLength() != archiveCount ||
            schemaVersion == 2 && (sidecarCount <= 0 ||
                root.GetProperty("externalProviderSidecars").GetArrayLength() !=
                sidecarCount) ||
            schemaVersion == 3 &&
                root.GetProperty("externalInstallDependencies").GetArrayLength() !=
                externalDependencyCount ||
            schemaVersion == 3 && sidecarCount > 0 &&
                root.GetProperty("externalProviderSidecars").GetArrayLength() !=
                sidecarCount)
        {
            throw new InvalidDataException(
                "Selected dependency evidence changed during JSON readback.");
        }
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(reopened)));
    }

    private string Relative(WorkspacePath path)
    {
        if (!path.IsUnder(labRoot) || path == labRoot)
            throw new InvalidDataException(
                "Selected dependency evidence path escaped the K-local lab root.");
        return new AssetPath(Path.GetRelativePath(labRoot.Value, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')).Value;
    }

    private static string ProviderKey(DependencyAsset asset) =>
        $"{asset.Provider}|{asset.EvidencePath.Value}|{asset.ProviderSha256.Value}";

    private static string EncodeCanonicalArray(IEnumerable<string> values) =>
        string.Concat(values.Select(value => value.Length + ":" + value));

    private static WorkspacePath PackageRootFor(WorkspacePath destination)
    {
        string? evidenceDirectory = Path.GetDirectoryName(destination.Value);
        if (evidenceDirectory is null)
            throw new ArgumentException(
                "Schema 3 evidence destination has no containing directory.",
                nameof(destination));
        return new WorkspacePath(Path.GetFullPath(Path.Combine(
            evidenceDirectory, "..", "..", "..")));
    }

    private static void CompareSchema3Readback(
        RaceMenuSelectedDependencyManifestArtifact actual,
        string dependencyId,
        Sha256Hash manifestHash,
        Sha256Hash presetSha256,
        int headPartCount,
        int looseAssetCount,
        int archiveCount,
        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> expected)
    {
        if (actual.SchemaVersion != 3 || actual.DependencyId != dependencyId ||
            actual.ManifestSha256 != manifestHash ||
            actual.PresetSha256 != presetSha256 ||
            actual.HeadPartCount != headPartCount ||
            actual.LooseAssetCount != looseAssetCount ||
            actual.ArchiveCount != archiveCount ||
            actual.ExternalInstallDependencies.Length != expected.Length)
        {
            throw new InvalidDataException(
                "Selected dependency schema 3 readback changed its semantic summary.");
        }
        for (int index = 0; index < expected.Length; index++)
        {
            RaceMenuSelectedDependencyManifestExternalInstallDependency left =
                expected[index];
            RaceMenuSelectedDependencyManifestExternalInstallDependency right =
                actual.ExternalInstallDependencies[index];
            if (!ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                        left.Descriptor).AsSpan().SequenceEqual(
                    ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                        right.Descriptor)) ||
                !ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                        left.Attestation).AsSpan().SequenceEqual(
                    ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(
                        right.Attestation)) ||
                left.OutputPlugin.Plugin != right.OutputPlugin.Plugin ||
                left.OutputPlugin.Sha256 != right.OutputPlugin.Sha256 ||
                left.OutputPlugin.ByteLength != right.OutputPlugin.ByteLength ||
                !left.OutputPlugin.Masters.SequenceEqual(
                    right.OutputPlugin.Masters) ||
                !left.OutputPlugin.PnamBindings.SequenceEqual(
                    right.OutputPlugin.PnamBindings) ||
                left.FaceGeomPath != right.FaceGeomPath ||
                left.FaceGeomSha256 != right.FaceGeomSha256 ||
                left.FaceGeomByteLength != right.FaceGeomByteLength)
            {
                throw new InvalidDataException(
                    "Selected dependency schema 3 readback changed semantic groups.");
            }
        }
    }

    private void TryDeleteOwnedOutput(WorkspacePath path)
    {
        try
        {
            if (path.IsUnder(labRoot) && File.Exists(path.Value) &&
                !File.GetAttributes(path.Value)
                    .HasFlag(FileAttributes.ReparsePoint))
                File.Delete(path.Value);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            // The owning selection transaction can quarantine the candidate.
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuSelectedDependencyManifestWriteResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record DependencyAsset(
        AssetPath GamePath,
        string Provider,
        AssetProviderKind Kind,
        WorkspacePath EvidencePath,
        Sha256Hash ProviderSha256,
        Sha256Hash ContentSha256,
        long ContentLength,
        long ProviderByteLength);

    private sealed record NormalizedAssets(
        ImmutableArray<DependencyAsset> Dependencies,
        ImmutableArray<DependencyAsset> ProviderSidecars);

    private sealed record ProviderObservation(Sha256Hash Hash, long Length);
}
