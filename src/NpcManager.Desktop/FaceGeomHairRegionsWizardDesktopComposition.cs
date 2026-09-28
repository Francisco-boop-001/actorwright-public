using System.Collections.Immutable;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.Desktop;

internal sealed class FaceGeomHairRegionsSelectedSourceLease :
    IDisposable
{
    private Action? release;

    internal FaceGeomHairRegionsSelectedSourceLease(
        FaceGeomHairRegionsSelectedSourceResult result,
        bool retainsArchiveStaging,
        Action? release)
    {
        Result = result ??
            throw new ArgumentNullException(
                nameof(result));
        RetainsArchiveStaging =
            retainsArchiveStaging;
        this.release = release;
    }

    public FaceGeomHairRegionsSelectedSourceResult Result
        { get; }

    public bool RetainsArchiveStaging { get; }

    public void Dispose()
    {
        Action? owned =
            Interlocked.Exchange(
                ref release,
                null);
        owned?.Invoke();
    }
}

/// <summary>
/// Owns the production desktop HairTint services and their disposable K-local
/// staging roots. The caller supplies the already-admitted visual validator;
/// this composition never creates or owns another native inference runtime.
/// </summary>
internal sealed class FaceGeomHairRegionsWizardDesktopComposition :
    IDisposable
{
    internal const int MaximumRetainedSelectedSources = 1;
    internal const int BlenderInteropPathLimit =
        BlenderFaceGeomHairRegionsRenderer
            .LegacyWindowsPathLimit;

    private static readonly Sha256Hash BlenderSha256 = new(
        "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794");
    private static readonly Sha256Hash PyniflySha256 = new(
        "296427A5E30F151223700298A7875DF2C0A61B5F422818FC7002B4F4D8DDAEE7");
    private static readonly Sha256Hash PyniflyProfileSha256 = new(
        "A909978665FBF6927F53F97726D73F14EF69B62E14D1F29DBF5F8FC65089D9A3");
    private static readonly Sha256Hash TexconvSha256 = new(
        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06");

    private readonly object lifetimeSync = new();
    private readonly HashSet<FaceGeomHairRegionsOwnedDirectoryLease>
        retainedSelectedSourceRoots = [];
    private readonly CancellationTokenSource
        lifetimeCancellation = new();
    private readonly ManualResetEventSlim
        selectedSourceOperationsComplete =
            new(initialState: true);
    private readonly FaceGeomHairRegionsOwnedDirectoryLease
        selectedSourceParent;
    private readonly FaceGeomHairRegionsOwnedDirectoryLease
        transactionRoot;
    private readonly FaceGeomHairRegionsOwnedDirectoryLease
        previewWorkRoot;
    private readonly FaceGeomHairRegionsOwnedDirectoryLease
        rendererWorkRoot;
    private readonly FaceGeomHairRegionsOwnedDirectoryLease
        ownedRoot;
    private readonly FaceGeomHairRegionsPreviewService previewService;
    private readonly IFaceGeomHairRegionsSelectedSourceResolver
        selectedSourceResolver;
    private readonly FaceGeomHairRegionsWizardTransaction transaction;
    private readonly IDisposable? nativeLifetime;
    private int activeSelectedSourceOperations;
    private bool disposed;

    private FaceGeomHairRegionsWizardDesktopComposition(
        FaceGeomHairRegionsOwnedDirectoryLease ownedRoot,
        FaceGeomHairRegionsOwnedDirectoryLease rendererWorkRoot,
        FaceGeomHairRegionsOwnedDirectoryLease previewWorkRoot,
        FaceGeomHairRegionsOwnedDirectoryLease transactionRoot,
        FaceGeomHairRegionsOwnedDirectoryLease selectedSourceParent,
        FaceGeomHairRegionsPreviewService previewService,
        IFaceGeomHairRegionsSelectedSourceResolver
            selectedSourceResolver,
        FaceGeomHairRegionsWizardTransaction transaction,
        IDisposable? nativeLifetime)
    {
        this.ownedRoot = ownedRoot;
        this.rendererWorkRoot = rendererWorkRoot;
        this.previewWorkRoot = previewWorkRoot;
        this.transactionRoot = transactionRoot;
        this.selectedSourceParent = selectedSourceParent;
        this.previewService = previewService;
        this.selectedSourceResolver =
            selectedSourceResolver;
        this.transaction = transaction;
        this.nativeLifetime = nativeLifetime;
    }

    public IFaceGeomHairRegionsWizardTransaction Transaction =>
        transaction;

    public static FaceGeomHairRegionsWizardDesktopComposition Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        INpcVisualPreviewVisualValidator admittedVisualValidator,
        Sha256Hash admittedNativeRuntimeManifestSha256,
        IDisposable? nativeLifetime = null)
    {
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(
            admittedVisualValidator);

        WorkspacePath rootPath = new(Path.Combine(
            labRoot.Value,
            ".actorwright",
            "work",
            $"desktop-hair-regions-runtime-{Guid.NewGuid():N}"));
        WorkspacePath rendererRootPath =
            BuildRendererRootPath(
                labRoot,
                Guid.NewGuid());
        FaceGeomHairRegionsOwnedDirectoryLease? root = null;
        FaceGeomHairRegionsOwnedDirectoryLease? rendererRoot = null;
        FaceGeomHairRegionsOwnedDirectoryLease? previewRoot = null;
        FaceGeomHairRegionsOwnedDirectoryLease? transaction = null;
        FaceGeomHairRegionsOwnedDirectoryLease? selectedParent = null;
        FaceGeomHairRegionsPreviewService? service = null;
        try
        {
            root = new FaceGeomHairRegionsOwnedDirectoryLease(
                labRoot,
                rootPath,
                "desktop HairTint runtime");
            rendererRoot =
                new FaceGeomHairRegionsOwnedDirectoryLease(
                labRoot,
                rendererRootPath,
                "desktop HairTint renderer work");
            previewRoot = root.CreateChild(
                Child(rootPath, "preview"),
                "desktop HairTint preview work");
            transaction = root.CreateChild(
                Child(rootPath, "transaction"),
                "desktop HairTint transaction");
            selectedParent = root.CreateChild(
                Child(rootPath, "selected-sources"),
                "desktop HairTint selected-source staging");

            var sourceService =
                new BethesdaFaceGeomHairRegionsPreviewSourceService(
                    policy,
                    labRoot);
            var renderer =
                new BlenderFaceGeomHairRegionsRenderer(
                    new WorkspacePath(Path.Combine(
                        labRoot.Value,
                        "tools",
                        "external",
                        "blender-4.5.1-windows-x64",
                        "blender-4.5.1-windows-x64",
                        "blender.exe")),
                    new WorkspacePath(Path.Combine(
                        labRoot.Value,
                        "tools",
                        "external",
                        "blender-4.5.1-pynifly-profile")),
                    new WorkspacePath(Path.Combine(
                        labRoot.Value,
                        "tools",
                        "external",
                        "_downloads",
                        "io_scene_nifly.zip")),
                    NpcVisualPreviewRendererAuthority.ScriptId,
                    new WorkspacePath(Path.Combine(
                        labRoot.Value,
                        "tools",
                        "external",
                        "directxtex-texconv-2026.5.7",
                        "texconv.exe")),
                    policy,
                    labRoot,
                    rendererRoot.Path,
                    BlenderSha256,
                    PyniflySha256,
                    PyniflyProfileSha256,
                    NpcVisualPreviewRendererAuthority.ScriptSha256,
                    TexconvSha256);
            var documents =
                new FaceGeomHairRegionsDocumentCodec(
                    labRoot);
            service = new FaceGeomHairRegionsPreviewService(
                labRoot,
                previewRoot.Path,
                sourceService,
                renderer,
                admittedVisualValidator,
                documents,
                createOwnedWorkRoot: false);
            var resolver =
                new BethesdaFaceGeomHairRegionsSelectedSourceResolver(
                    policy,
                    labRoot);
            var wizardTransaction =
                new FaceGeomHairRegionsWizardTransaction(
                    labRoot,
                    service,
                    sourceService,
                    transaction.Path,
                    BuildRendererAuthority(
                        admittedNativeRuntimeManifestSha256),
                    NpcVisualPreviewRendererAuthority.ScriptSha256);

            var composition =
                new FaceGeomHairRegionsWizardDesktopComposition(
                    root,
                    rendererRoot,
                    previewRoot,
                    transaction,
                    selectedParent,
                    service,
                    resolver,
                    wizardTransaction,
                    nativeLifetime);
            root = null;
            rendererRoot = null;
            previewRoot = null;
            transaction = null;
            selectedParent = null;
            service = null;
            return composition;
        }
        catch
        {
            service?.Dispose();
            DisposeOwned(
                selectedParent,
                transaction,
                previewRoot,
                rendererRoot,
                root);
            throw;
        }
    }

    public async ValueTask<
        FaceGeomHairRegionsSelectedSourceLease>
        ResolveSelectedSourceAsync(
            ReviewedGameIntake intake,
            SkyrimMainWorkspaceIdentity identity,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(intake);
        ArgumentNullException.ThrowIfNull(identity);
        FaceGeomHairRegionsOwnedDirectoryLease staging;
        CancellationTokenSource linkedCancellation;
        lock (lifetimeSync)
        {
            ObjectDisposedException.ThrowIf(
                disposed,
                this);
            if (retainedSelectedSourceRoots.Count >=
                MaximumRetainedSelectedSources)
            {
                return new FaceGeomHairRegionsSelectedSourceLease(
                    new FaceGeomHairRegionsSelectedSourceResult(
                        false,
                        null,
                        [
                            new Diagnostic(
                                "facegeom-hair-regions-selected-source-lifetime",
                                DiagnosticSeverity.Error,
                                "Close the active HairTint wizard before resolving another archive-backed FaceGeom.")
                        ]),
                    retainsArchiveStaging: false,
                    release: null);
            }
            WorkspacePath stagingPath = Child(
                selectedSourceParent.Path,
                $"source-{Guid.NewGuid():N}");
            staging = selectedSourceParent.CreateChild(
                stagingPath,
                "desktop selected FaceGeom archive staging");
            retainedSelectedSourceRoots.Add(
                staging);
            activeSelectedSourceOperations++;
            selectedSourceOperationsComplete.Reset();
            linkedCancellation =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken,
                        lifetimeCancellation.Token);
        }

        bool retain = false;
        try
        {
            FaceGeomHairRegionsSelectedSourceResult result =
                await selectedSourceResolver.ResolveAsync(
                    new FaceGeomHairRegionsSelectedSourceRequest(
                        intake,
                        identity,
                        staging.Path),
                    linkedCancellation.Token)
                    .ConfigureAwait(false);
            linkedCancellation.Token
                .ThrowIfCancellationRequested();
            if (result.Resolved &&
                result.Source is not null &&
                !result.Diagnostics.Any(item =>
                    item.Severity ==
                    DiagnosticSeverity.Error))
            {
                transaction.AdmitSelectedSource(
                    result.Source,
                    result.Diagnostics);
                retain =
                    result.Source.MaterializedFromArchive;
                WorkspacePath admittedPath =
                    result.Source.Source.Path;
                return new FaceGeomHairRegionsSelectedSourceLease(
                    result,
                    retain,
                    () =>
                    {
                        transaction.RevokeSelectedSource(
                            admittedPath);
                        if (retain)
                        {
                            ReleaseSelectedSourceRoot(
                                staging);
                        }
                    });
            }
            return new FaceGeomHairRegionsSelectedSourceLease(
                result,
                retain,
                release: null);
        }
        finally
        {
            linkedCancellation.Dispose();
            if (!retain)
            {
                ReleaseSelectedSourceRoot(
                    staging);
            }
            lock (lifetimeSync)
            {
                activeSelectedSourceOperations--;
                if (activeSelectedSourceOperations == 0)
                {
                    selectedSourceOperationsComplete.Set();
                }
            }
        }
    }

    public void Dispose()
    {
        List<FaceGeomHairRegionsOwnedDirectoryLease>
            selectedRoots;
        lock (lifetimeSync)
        {
            if (disposed)
            {
                return;
            }
            disposed = true;
        }
        lifetimeCancellation.Cancel();

        selectedSourceOperationsComplete.Wait();
        lock (lifetimeSync)
        {
            selectedRoots =
                [.. retainedSelectedSourceRoots];
            retainedSelectedSourceRoots.Clear();
        }

        List<Exception> failures = [];
        TryDispose(
            previewService,
            failures);
        for (int index =
                 selectedRoots.Count - 1;
             index >= 0;
             index--)
        {
            TryDispose(
                selectedRoots[index],
                failures);
        }
        TryDispose(
            selectedSourceParent,
            failures);
        TryDispose(
            transactionRoot,
            failures);
        TryDispose(
            previewWorkRoot,
            failures);
        TryDispose(
            rendererWorkRoot,
            failures);
        TryDispose(
            ownedRoot,
            failures);
        TryDispose(
            lifetimeCancellation,
            failures);
        TryDispose(
            selectedSourceOperationsComplete,
            failures);
        TryDispose(
            nativeLifetime,
            failures);
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Desktop HairTint runtime cleanup was incomplete.",
                failures);
        }
    }

    private void ReleaseSelectedSourceRoot(
        FaceGeomHairRegionsOwnedDirectoryLease staging)
    {
        bool owns;
        lock (lifetimeSync)
        {
            owns =
                retainedSelectedSourceRoots.Remove(
                    staging);
        }
        if (owns)
        {
            staging.Dispose();
        }
    }

    private static WorkspacePath Child(
        WorkspacePath parent,
        string name) =>
        new(Path.Combine(
            parent.Value,
            name));

    internal static WorkspacePath BuildRendererRootPath(
        WorkspacePath labRoot,
        Guid authority) =>
        new(Path.Combine(
            labRoot.Value,
            ".actorwright",
            "work",
            $"hr-{authority:N}"));

    internal static string ProjectBlenderBoundTexturePath(
        WorkspacePath rendererRoot,
        AssetPath texture) =>
        BlenderFaceGeomHairRegionsRenderer
            .ProjectStagedTexturePath(
                rendererRoot,
                texture);

    private static WorkspacePath ResolveRendererScript() =>
        new(Path.Combine(
            AppContext.BaseDirectory,
            "runtime",
            "rendering",
            "render_npc_preview_bundle.py"));

    private static Sha256Hash BuildRendererAuthority(
        Sha256Hash nativeRuntimeManifestSha256)
    {
        string canonical = string.Join(
            "\n",
            $"blender={BlenderSha256.Value}",
            $"pynifly={PyniflySha256.Value}",
            $"pyniflyProfile={PyniflyProfileSha256.Value}",
            $"renderer={NpcVisualPreviewRendererAuthority.ScriptSha256.Value}",
            $"texconv={TexconvSha256.Value}",
            $"nativeRuntime={nativeRuntimeManifestSha256.Value}");
        return new Sha256Hash(
            Convert.ToHexString(
                SHA256.HashData(
                    Encoding.UTF8.GetBytes(
                        canonical))));
    }

    private static void DisposeOwned(
        params IDisposable?[] owned)
    {
        List<Exception> failures = [];
        foreach (IDisposable? item in owned)
        {
            TryDispose(
                item,
                failures);
        }
        if (failures.Count > 0)
        {
            throw new AggregateException(
                "Desktop HairTint construction failed and owned staging cleanup was incomplete.",
                failures);
        }
    }

    private static void TryDispose(
        IDisposable? owned,
        List<Exception> failures)
    {
        if (owned is null)
        {
            return;
        }
        try
        {
            owned.Dispose();
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException or
                FaceGeomHairRegionsOperationalException)
        {
            failures.Add(
                exception);
        }
    }
}
