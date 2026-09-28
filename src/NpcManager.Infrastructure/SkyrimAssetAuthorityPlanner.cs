using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Shared copied-Data winner planner for Skyrim FaceGen NIF, TRI, DDS, and
/// bounded RaceMenu catalog inputs. It never guesses order between competing
/// archive providers.
/// </summary>
public sealed class SkyrimAssetAuthorityPlanner(
    IAssetIndexer assetIndexer,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : ISkyrimAssetAuthorityPlanner
{
    private const int MaximumAssets = 512;
    private const long MaximumArchiveBytes = 8L * 1024 * 1024 * 1024;

    public async ValueTask<SkyrimAssetAuthorityPlanResult> PlanAsync(
        SkyrimAssetAuthorityPlanRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, request.DataRoot));
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        AssetIndex index;
        try
        {
            index = await assetIndexer.IndexAsync(
                new AssetIndexRequest(request.Edition, request.DataRoot),
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("skyrim-asset-authority-index-failed", exception.Message));
            return Refused(diagnostics);
        }
        diagnostics.AddRange(index.Diagnostics);
        if (index.Edition != request.Edition)
        {
            diagnostics.Add(Error("skyrim-asset-authority-index-edition",
                "The asset index returned a different game edition."));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var byPath = index.Providers.GroupBy(item => item.Path.Value,
                StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        var archiveHashes = new Dictionary<string, Sha256Hash>(
            StringComparer.OrdinalIgnoreCase);
        ImmutableArray<AssetPath> optionalAssets = request.OptionalAssets.IsDefault
            ? []
            : request.OptionalAssets;
        HashSet<string> optionalPaths = optionalAssets
            .Select(item => item.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<AssetPath> requestedAssets =
            request.RequiredAssets.AddRange(optionalAssets);
        var authorities = ImmutableArray.CreateBuilder<SkyrimAssetAuthority>(
            requestedAssets.Length);
        var unavailableOptional = ImmutableArray.CreateBuilder<AssetPath>();
        foreach (AssetPath asset in requestedAssets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!byPath.TryGetValue(asset.Value, out AssetProvider[]? candidates))
            {
                if (optionalPaths.Contains(asset.Value))
                {
                    unavailableOptional.Add(asset);
                }
                else
                {
                    diagnostics.Add(Error("skyrim-asset-authority-missing",
                        $"Copied Data contains no provider for '{asset}'."));
                }
                continue;
            }

            AssetProvider[] loose = candidates
                .Where(item => item.Kind == AssetProviderKind.Loose).ToArray();
            if (loose.Length > 1)
            {
                diagnostics.Add(Error("skyrim-asset-authority-loose-ambiguous",
                    $"Copied Data reports more than one loose provider for '{asset}'."));
                continue;
            }
            if (loose.Length == 1)
            {
                AssetProvider provider = loose[0];
                string physical = Path.Combine(request.DataRoot.Value,
                    asset.Value.Replace('/', Path.DirectorySeparatorChar));
                authorities.Add(new SkyrimAssetAuthority(
                    "loose", AssetProviderKind.Loose,
                    new WorkspacePath(physical), new Sha256Hash(provider.Sha256),
                    asset, provider.Size, new Sha256Hash(provider.Sha256)));
                continue;
            }

            IGrouping<string, AssetProvider>[] archiveGroups = candidates
                .Where(item => item.Kind == AssetProviderKind.Archive)
                .GroupBy(item => item.Source, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (archiveGroups.Length != 1 || archiveGroups[0].Count() != 1)
            {
                diagnostics.Add(Error("skyrim-asset-authority-archive-ambiguous",
                    $"Asset '{asset}' has an ambiguous archive provider set; declare an explicit winner instead of guessing copied archive priority."));
                continue;
            }

            AssetProvider archive = archiveGroups[0].Single();
            if (!IsSafeArchiveName(archive.Source))
            {
                diagnostics.Add(Error("skyrim-asset-authority-archive-name",
                    $"Archive provider name for '{asset}' is unsafe."));
                continue;
            }
            string archivePath = Path.Combine(request.DataRoot.Value, archive.Source);
            if (!archiveHashes.TryGetValue(archivePath, out Sha256Hash archiveHash))
            {
                try
                {
                    archiveHash = await HashArchiveAsync(
                        archivePath, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or
                                                   UnauthorizedAccessException or
                                                   InvalidDataException or
                                                   ArgumentException or
                                                   NotSupportedException)
                {
                    diagnostics.Add(Error("skyrim-asset-authority-archive-read",
                        exception.Message));
                    continue;
                }
                archiveHashes.Add(archivePath, archiveHash);
            }
            authorities.Add(new SkyrimAssetAuthority(
                archive.Source, AssetProviderKind.Archive,
                new WorkspacePath(archivePath), archiveHash,
                asset, archive.Size, new Sha256Hash(archive.Sha256)));
        }

        return HasErrors(diagnostics)
            ? Refused(diagnostics)
            : new SkyrimAssetAuthorityPlanResult(
                true, authorities.ToImmutable(), diagnostics.ToImmutable(),
                unavailableOptional.ToImmutable());
    }

    private static void ValidateRequest(
        SkyrimAssetAuthorityPlanRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("skyrim-asset-authority-edition",
                "FaceGen asset planning supports Skyrim Special Edition only."));
        }
        if (string.IsNullOrWhiteSpace(request.DataRoot.Value) ||
            !Directory.Exists(request.DataRoot.Value))
        {
            diagnostics.Add(Error("skyrim-asset-authority-data-root",
                "The copied Data root must already exist."));
        }
        ImmutableArray<AssetPath> optionalAssets = request.OptionalAssets.IsDefault
            ? []
            : request.OptionalAssets;
        int totalAssets = request.RequiredAssets.IsDefault
            ? optionalAssets.Length
            : request.RequiredAssets.Length + optionalAssets.Length;
        if (request.RequiredAssets.IsDefault || totalAssets < 1 ||
            totalAssets > MaximumAssets)
        {
            diagnostics.Add(Error("skyrim-asset-authority-count",
                $"RequiredAssets plus OptionalAssets must explicitly contain 1-{MaximumAssets} paths."));
            return;
        }
        ImmutableArray<AssetPath> allAssets =
            request.RequiredAssets.AddRange(optionalAssets);
        if (allAssets.Any(item => !IsSupportedAsset(item)) ||
            allAssets.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            allAssets.Length)
        {
            diagnostics.Add(Error("skyrim-asset-authority-shape",
                "RequiredAssets and OptionalAssets must be mutually distinct canonical FaceGen NIF/TRI/DDS or RaceMenu catalog paths."));
        }
    }

    private static bool IsSupportedAsset(AssetPath asset)
    {
        if (string.IsNullOrWhiteSpace(asset.Value) || asset.Value.Contains('\\')) return false;
        bool meshBinary = asset.Value.StartsWith("meshes/",
                              StringComparison.OrdinalIgnoreCase) &&
                          (asset.Value.EndsWith(".nif",
                               StringComparison.OrdinalIgnoreCase) ||
                           asset.Value.EndsWith(".tri",
                               StringComparison.OrdinalIgnoreCase));
        bool texture = asset.Value.StartsWith("textures/",
                           StringComparison.OrdinalIgnoreCase) &&
                       asset.Value.EndsWith(".dds",
                           StringComparison.OrdinalIgnoreCase);
        bool raceMenuCatalog = asset.Value.StartsWith(
                                   "meshes/actors/character/FaceGenMorphs/",
                                   StringComparison.OrdinalIgnoreCase) &&
                               (asset.Value.EndsWith(".ini",
                                    StringComparison.OrdinalIgnoreCase) ||
                                asset.Value.EndsWith(".slider",
                                    StringComparison.OrdinalIgnoreCase));
        return meshBinary || texture || raceMenuCatalog;
    }

    private static async ValueTask<Sha256Hash> HashArchiveAsync(
        string path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length <= 0 || info.Length > MaximumArchiveBytes ||
            info.Attributes.HasFlag(FileAttributes.Directory) ||
            info.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            throw new InvalidDataException(
                $"Archive '{Path.GetFileName(path)}' is not an admitted ordinary BSA file.");
        }
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 256 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
    }

    private static bool IsSafeArchiveName(string value) =>
        !string.IsNullOrWhiteSpace(value) && value == Path.GetFileName(value) &&
        value.EndsWith(".bsa", StringComparison.OrdinalIgnoreCase) &&
        !value.Any(char.IsControl) && !value.Contains(':');

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimAssetAuthorityPlanResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], diagnostics.ToImmutable());
}
