using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimMeshPickerCatalog()
    {
        WorkspacePath dataRoot = new(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\01-source-copies\\mesh-picker\\Data");
        AssetChoiceCandidate body = MeshCandidate(
            "meshes/armor/travel/body.nif",
            [
                MeshProvider(AssetProviderKind.Loose, "loose", 120, 'a'),
                MeshProvider(AssetProviderKind.Archive, "Travel.bsa", 120, 'b')
            ]);
        AssetChoiceCandidate helmet = MeshCandidate(
            "meshes/armor/travel/helmet.nif",
            [MeshProvider(AssetProviderKind.Archive, "Travel.bsa", 80, 'c')]);
        var search = new AssetChoiceSearchResult(
            GameEdition.SkyrimSpecialEdition,
            AssetChoiceKind.Mesh,
            [helmet, body],
            []);

        SkyrimMeshPickerCatalogResult catalog = SkyrimMeshPickerRules.BuildCatalog(
            new SkyrimMeshPickerCatalogRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                search,
                "armor\\travel\\body.nif"));
        Assert(catalog.Accepted && catalog.Candidates.Length == 2 &&
               catalog.Candidates[0].IndexedPath.Value ==
               "meshes/armor/travel/body.nif" &&
               catalog.Candidates[0].RelativePath.Value ==
               "armor/travel/body.nif" &&
               catalog.Candidates[0].SelectedProvider.Kind ==
               AssetProviderKind.Loose &&
               catalog.InitialCandidate == catalog.Candidates[0],
            "The mesh catalog lost ordering, prefix repair, provider precedence, or exact preselection.");
        Assert(SkyrimMeshPickerRules.Filter(catalog, "helmet")
                   .Single() == catalog.Candidates[1],
            "The cached mesh catalog filter did not retain the exact row.");

        SkyrimMeshPickerSelection accepted =
            SkyrimMeshPickerRules.Use(catalog.Candidates[0]);
        Assert(accepted.Accepted &&
               accepted.RelativePath?.Value == "armor/travel/body.nif" &&
               accepted.Candidate == catalog.Candidates[0],
            "Mesh acceptance did not return the exact prefix-free candidate.");
        Assert(!SkyrimMeshPickerRules.Cancel().Accepted,
            "Mesh-picker Cancel returned an accepted path.");

        Assert(!SkyrimMeshPickerRules.BuildCatalog(
                new SkyrimMeshPickerCatalogRequest(
                    GameEdition.Fallout4,
                    dataRoot,
                    search,
                    null)).Accepted,
            "The Skyrim mesh picker accepted a Fallout request.");
        Assert(!CatalogWithCandidate(dataRoot,
            MeshCandidate("textures/armor/not-a-mesh.nif",
                [MeshProvider(AssetProviderKind.Loose, "loose", 10, 'd')]))
            .Accepted,
            "A .nif outside the meshes namespace was accepted.");
        Assert(!CatalogWithCandidate(dataRoot,
            MeshCandidate("meshes/armor/not-a-nif.tri",
                [MeshProvider(AssetProviderKind.Loose, "loose", 10, 'e')]))
            .Accepted,
            "A non-NIF asset was accepted.");
        Assert(!CatalogWithCandidate(dataRoot,
            MeshCandidate("meshes/armor/default-provider.nif",
                ImmutableArray<AssetChoiceProviderEvidence>.Empty))
            .Accepted,
            "A mesh without exact provider evidence was accepted.");
        Assert(!SkyrimMeshPickerRules.BuildCatalog(
                new SkyrimMeshPickerCatalogRequest(
                    GameEdition.SkyrimSpecialEdition,
                    dataRoot,
                    new AssetChoiceSearchResult(
                        GameEdition.SkyrimSpecialEdition,
                        AssetChoiceKind.Mesh,
                        [body, body],
                        []),
                    null)).Accepted,
            "Duplicate case-insensitive mesh paths were accepted.");
        return Task.CompletedTask;
    }

    private static async Task TestSkyrimMeshPickerPreview()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "mesh-picker-preview-" + Guid.NewGuid().ToString("N"));
        string dataPath = Path.Combine(root, "Data");
        string cachePath = Path.Combine(root, "cache");
        Directory.CreateDirectory(dataPath);
        try
        {
            byte[] content = [1, 2, 3, 4, 5];
            string contentHash = Convert.ToHexString(SHA256.HashData(content));
            var provider = new AssetChoiceProviderEvidence(
                AssetProviderKind.Loose,
                "loose",
                content.Length,
                new Sha256Hash(contentHash));
            var candidate = new SkyrimMeshPickerCandidate(
                new AssetPath("meshes/armor/travel/body.nif"),
                new AssetPath("armor/travel/body.nif"),
                provider,
                [provider]);
            var resolver = new ControlledMeshContentResolver(content);
            var renderer = new ControlledMeshPreviewRenderer();
            WorkspacePath labRoot = new("K:\\ExampleWorkspace");
            var service = new SkyrimMeshPreviewService(
                resolver,
                renderer,
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                labRoot,
                new WorkspacePath(cachePath));
            var request = new SkyrimMeshPreviewRequest(
                new WorkspacePath(dataPath),
                candidate,
                128,
                96);

            SkyrimMeshPreviewResult first = await service.RenderAsync(
                request,
                CancellationToken.None);
            SkyrimMeshPreviewResult second = await service.RenderAsync(
                request,
                CancellationToken.None);
            Assert(first.Rendered && first.Image is
            { Width: 128, Height: 96, MeshCount: 1 },
                "The first exact mesh preview did not render at the requested dimensions.");
            Assert(File.Exists(first.Image!.Path),
                "The exact mesh preview image was not retained in the K-local cache.");
            Assert(second.Rendered && second.Image is not null &&
                   string.Equals(second.Image.Sha256, first.Image.Sha256,
                       StringComparison.OrdinalIgnoreCase),
                "The deterministic mesh preview cache did not return the exact image hash.");
            Assert(renderer.CallCount == 1 && resolver.CallCount == 2,
                "The exact mesh content was not rebound before one-image cache reuse.");
            Assert(resolver.LastRequest?.Authorities.Single().AssetPath.Value ==
                   candidate.IndexedPath.Value,
                "The content resolver did not receive the selected canonical mesh path.");
            string staged = Path.Combine(
                cachePath,
                "content",
                contentHash,
                "meshes",
                "armor",
                "travel",
                "body.nif");
            Assert(File.Exists(staged) &&
                   File.ReadAllBytes(staged).SequenceEqual(content),
                "The exact resolved NIF content was not staged under its content hash.");
            byte[] tamperedImage = await File.ReadAllBytesAsync(
                first.Image.Path);
            tamperedImage[8] ^= 0x01;
            await File.WriteAllBytesAsync(first.Image.Path, tamperedImage);
            SkyrimMeshPreviewResult tampered = await service.RenderAsync(
                request,
                CancellationToken.None);
            Assert(!tampered.Rendered && tampered.Image is null &&
                   tampered.Diagnostics.Any(item =>
                       item.Code == "mesh-preview-cache-invalid") &&
                   renderer.CallCount == 1,
                "A hash-mismatched mesh preview cache image was trusted or silently replaced.");

            byte[] archiveBytes = [9, 8, 7, 6];
            string archivePath = Path.Combine(dataPath, "Travel.bsa");
            await File.WriteAllBytesAsync(archivePath, archiveBytes);
            byte[] archiveContent = [6, 7, 8];
            string archiveContentHash = Convert.ToHexString(
                SHA256.HashData(archiveContent));
            var archiveProvider = new AssetChoiceProviderEvidence(
                AssetProviderKind.Archive,
                "Travel.bsa",
                archiveContent.Length,
                new Sha256Hash(archiveContentHash));
            var archiveCandidate = new SkyrimMeshPickerCandidate(
                new AssetPath("meshes/armor/travel/archive.nif"),
                new AssetPath("armor/travel/archive.nif"),
                archiveProvider,
                [archiveProvider]);
            var archiveResolver = new ControlledMeshContentResolver(
                archiveContent);
            var archiveRenderer = new ControlledMeshPreviewRenderer();
            var archiveService = new SkyrimMeshPreviewService(
                archiveResolver,
                archiveRenderer,
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                labRoot,
                new WorkspacePath(cachePath));
            SkyrimMeshPreviewResult archivePreview =
                await archiveService.RenderAsync(
                    request with { Candidate = archiveCandidate },
                    CancellationToken.None);
            SkyrimAssetContentAuthority archiveAuthority =
                archiveResolver.LastRequest!.Authorities.Single();
            Assert(archivePreview.Rendered,
                "The BSA-bound mesh preview was refused: " +
                string.Join("; ", archivePreview.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));
            Assert(archiveAuthority.Kind ==
                   SkyrimAssetContentProviderKind.Bsa,
                "The BSA provider was flattened to loose content.");
            Assert(string.Equals(
                    archiveAuthority.ProviderPath.Value,
                    archivePath,
                    StringComparison.OrdinalIgnoreCase),
                "The BSA preview resolved a different provider path.");
            Assert(string.Equals(
                    archiveAuthority.ProviderSha256.Value,
                    Convert.ToHexString(SHA256.HashData(archiveBytes)),
                    StringComparison.OrdinalIgnoreCase),
                "The BSA preview did not bind the archive SHA-256.");
            Assert(archiveAuthority.ContentSha256.Value ==
                   archiveProvider.Sha256.Value,
                "The BSA preview did not independently bind the member SHA-256.");

            var badProvider = provider with
            {
                Sha256 = new Sha256Hash(new string('f', 64))
            };
            SkyrimMeshPreviewResult refused = await service.RenderAsync(
                request with
                {
                    Candidate = candidate with
                    {
                        SelectedProvider = badProvider,
                        Providers = [badProvider]
                    }
                },
                CancellationToken.None);
            Assert(!refused.Rendered && refused.Image is null &&
                   refused.Diagnostics.Any(item =>
                       item.Code == "mesh-preview-content-binding"),
                "A provider/content hash mismatch was accepted by the preview cache.");

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await AssertThrowsAsync<OperationCanceledException>(() =>
                service.RenderAsync(request, cancellation.Token).AsTask());
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static SkyrimMeshPickerCatalogResult CatalogWithCandidate(
        WorkspacePath dataRoot,
        AssetChoiceCandidate candidate) =>
        SkyrimMeshPickerRules.BuildCatalog(new SkyrimMeshPickerCatalogRequest(
            GameEdition.SkyrimSpecialEdition,
            dataRoot,
            new AssetChoiceSearchResult(
                GameEdition.SkyrimSpecialEdition,
                AssetChoiceKind.Mesh,
                [candidate],
                []),
            null));

    private static AssetChoiceCandidate MeshCandidate(
        string path,
        ImmutableArray<AssetChoiceProviderEvidence> providers) =>
        new(
            AssetChoiceKind.Mesh,
            new AssetPath(path),
            new AssetChoiceProvider(
                providers.IsDefaultOrEmpty
                    ? AssetChoiceProviderStatus.Missing
                    : AssetChoiceProviderStatus.Resolved,
                providers),
            null,
            null,
            null,
            null,
            null,
            null);

    private static AssetChoiceProviderEvidence MeshProvider(
        AssetProviderKind kind,
        string source,
        long size,
        char hashCharacter) =>
        new(kind, source, size, new Sha256Hash(new string(hashCharacter, 64)));

    private sealed class ControlledMeshContentResolver(byte[] content)
        : ISkyrimAssetContentResolver
    {
        public int CallCount { get; private set; }
        public SkyrimAssetContentResolutionRequest? LastRequest { get; private set; }

        public ValueTask<SkyrimAssetContentResolutionResult> ResolveAsync(
            SkyrimAssetContentResolutionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            SkyrimAssetContentAuthority authority = request.Authorities.Single();
            var resolved = new ResolvedSkyrimAssetContent(
                authority.ProviderId,
                authority.Kind,
                authority.ProviderPath,
                authority.ProviderSha256,
                authority.AssetPath,
                content.Length,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(content))),
                content.ToImmutableArray());
            return ValueTask.FromResult(new SkyrimAssetContentResolutionResult(
                true,
                [resolved],
                []));
        }
    }

    private sealed class ControlledMeshPreviewRenderer : IPreviewImageRenderer
    {
        public int CallCount { get; private set; }

        public async ValueTask<PreviewImageRenderResult> RenderAsync(
            PreviewImageRenderRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            string staged = Path.Combine(
                request.AssetRoot.Value,
                request.Assets.Single().Path.Replace(
                    '/',
                    Path.DirectorySeparatorChar));
            if (!File.Exists(staged))
                return new PreviewImageRenderResult(false, null,
                    [new Diagnostic("controlled-mesh-missing",
                        DiagnosticSeverity.Error,
                        "The staged mesh was missing.")]);
            byte[] png = MeshPreviewPngHeader(request.Width, request.Height);
            await File.WriteAllBytesAsync(
                request.OutputPath.Value,
                png,
                cancellationToken);
            string hash = Convert.ToHexString(SHA256.HashData(png));
            return new PreviewImageRenderResult(
                true,
                new PreviewRenderedImage(
                    request.OutputPath.Value,
                    hash,
                    request.Width,
                    request.Height,
                    1),
                []);
        }
    }

    private static byte[] MeshPreviewPngHeader(int width, int height)
    {
        var bytes = new byte[24];
        new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }
            .CopyTo(bytes, 0);
        "IHDR"u8.CopyTo(bytes.AsSpan(12, 4));
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16, 4), width);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(20, 4), height);
        return bytes;
    }
}
