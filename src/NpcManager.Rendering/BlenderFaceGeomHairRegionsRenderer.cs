using System.Collections.Immutable;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using SkiaSharp;
using DesktopFailureOperationId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopFailureOperationId;
using DesktopProcessIdentityId = NpcManager.Application.ActorwrightObservabilityEventSource.DesktopProcessIdentityId;

[assembly: InternalsVisibleTo("NpcManager.Architecture.Tests")]
[assembly: InternalsVisibleTo("NpcManager.Desktop.Smoke")]
[assembly: InternalsVisibleTo("NpcManager.Desktop")]
[assembly: InternalsVisibleTo("Actorwright.Desktop")]

namespace NpcManager.Rendering;

/// <summary>
/// One-process, one-import renderer for the exact proposed FaceGeom bytes.
/// Its disposable staging root is separate from the closed public output.
/// </summary>
public sealed partial class BlenderFaceGeomHairRegionsRenderer :
    IFaceGeomHairRegionsRenderer
{
    internal const int LegacyWindowsPathLimit = 260;
    private const string StagingDirectoryPrefix = "r-";
    private const string RequestSchema =
        "npcmanager-facegeom-hair-regions-render-request/1";
    private const string StatusSchema =
        "npcmanager-facegeom-hair-regions-render-status/1";
    private const long MaximumCandidateBytes =
        128L * 1024L * 1024L;
    private const long MaximumStatusBytes =
        4L * 1024L * 1024L;
    private const long MaximumArtifactBytes =
        64L * 1024L * 1024L;
    private const long MaximumRenderedImageBytes =
        16L * 1024L * 1024L;
    private const long MaximumRenderedBundleBytes =
        256L * 1024L * 1024L;
    private const long MaximumTextureBytes =
        128L * 1024L * 1024L;
    private readonly WorkspacePath blenderPath;
    private readonly WorkspacePath profileRoot;
    private readonly WorkspacePath pyniflyArchivePath;
    private readonly EmbeddedBlenderScript rendererScript;
    private readonly WorkspacePath texconvPath;
    private readonly IWorkspacePolicy policy;
    private readonly WorkspacePath labRoot;
    private readonly WorkspacePath workRoot;
    private readonly Sha256Hash expectedBlenderSha256;
    private readonly Sha256Hash expectedPyniflySha256;
    private readonly Sha256Hash expectedPyniflyProfileSha256;
    private readonly Sha256Hash expectedScriptSha256;
    private readonly Sha256Hash expectedTexconvSha256;
    private readonly IFaceGeomHairRegionsProcessRunner processRunner;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        UnmappedMemberHandling =
            JsonUnmappedMemberHandling.Disallow
    };

    public BlenderFaceGeomHairRegionsRenderer(
        WorkspacePath blenderPath,
        WorkspacePath profileRoot,
        WorkspacePath pyniflyArchivePath,
        EmbeddedBlenderScriptId scriptId,
        WorkspacePath texconvPath,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        WorkspacePath workRoot,
        Sha256Hash expectedBlenderSha256,
        Sha256Hash expectedPyniflySha256,
        Sha256Hash expectedPyniflyProfileSha256,
        Sha256Hash expectedScriptSha256,
        Sha256Hash expectedTexconvSha256)
        : this(
            blenderPath,
            profileRoot,
            pyniflyArchivePath,
            scriptId,
            texconvPath,
            policy,
            labRoot,
            workRoot,
            expectedBlenderSha256,
            expectedPyniflySha256,
            expectedPyniflyProfileSha256,
            expectedScriptSha256,
            expectedTexconvSha256,
            new SystemFaceGeomHairRegionsProcessRunner())
    {
    }

    internal BlenderFaceGeomHairRegionsRenderer(
        WorkspacePath blenderPath,
        WorkspacePath profileRoot,
        WorkspacePath pyniflyArchivePath,
        EmbeddedBlenderScriptId scriptId,
        WorkspacePath texconvPath,
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        WorkspacePath workRoot,
        Sha256Hash expectedBlenderSha256,
        Sha256Hash expectedPyniflySha256,
        Sha256Hash expectedPyniflyProfileSha256,
        Sha256Hash expectedScriptSha256,
        Sha256Hash expectedTexconvSha256,
        IFaceGeomHairRegionsProcessRunner processRunner)
    {
        this.blenderPath = blenderPath;
        this.profileRoot = profileRoot;
        this.pyniflyArchivePath = pyniflyArchivePath;
        rendererScript = EmbeddedBlenderScriptBundle.Load(
            scriptId.Value);
        this.texconvPath = texconvPath;
        this.policy = policy ??
            throw new ArgumentNullException(nameof(policy));
        this.labRoot = labRoot;
        this.workRoot = workRoot;
        this.expectedBlenderSha256 = expectedBlenderSha256;
        this.expectedPyniflySha256 = expectedPyniflySha256;
        this.expectedPyniflyProfileSha256 =
            expectedPyniflyProfileSha256;
        this.expectedScriptSha256 = expectedScriptSha256;
        this.expectedTexconvSha256 = expectedTexconvSha256;
        this.processRunner = processRunner ??
            throw new ArgumentNullException(nameof(processRunner));
    }

    public async ValueTask<FaceGeomHairRegionsRenderResult>
        RenderAsync(
            FaceGeomHairRegionsRenderRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        string stagingText = Path.Combine(
            workRoot.Value,
            $"{StagingDirectoryPrefix}{Guid.NewGuid():N}");
        var staging = new WorkspacePath(stagingText);
        ProfileSnapshot? openingProfile = null;
        FaceGeomHairRegionsRenderResult? result = null;
        OperationCanceledException? cancellation = null;
        Exception? operationalFailure = null;
        ImmutableArray<WorkspacePath> survivors = [];
        try
        {
            await ValidateRequestAsync(
                request,
                diagnostics,
                cancellationToken);
            if (!HasErrors(diagnostics))
                openingProfile =
                    await SnapshotFullProfileAsync(
                        diagnostics,
                        "before-render",
                        cancellationToken);
            if (openingProfile is not null &&
                !HasErrors(diagnostics))
            {
                Directory.CreateDirectory(staging.Value);
                result = await RenderCoreAsync(
                    request,
                    staging,
                    openingProfile,
                    diagnostics,
                    cancellationToken);
            }
        }
        catch (OperationCanceledException exception)
        {
            cancellation = exception;
        }
        catch (Exception exception) when (
            exception is
                IOException or
                UnauthorizedAccessException or
                InvalidDataException or
                System.ComponentModel.Win32Exception or
                System.Security.SecurityException)
        {
            operationalFailure = exception;
        }
        finally
        {
            if (openingProfile is not null)
            {
                ProfileSnapshot? closingProfile =
                    await SnapshotFullProfileAsync(
                        diagnostics,
                        "after-render",
                        CancellationToken.None);
                if (closingProfile is null ||
                    closingProfile != openingProfile)
                    diagnostics.Add(Error(
                        "facegeom-hair-regions-pynifly-original-profile-drift",
                        "The complete original Blender/PyNifly profile inventory changed during the private render session."));
            }
            survivors = CleanupStaging(staging);
        }
        if (cancellation is not null)
        {
            ImmutableArray<WorkspacePath> allSurvivors =
                survivors.AddRange(
                    EnumerateOutputSurvivors(
                        request.OutputRoot));
            FaceGeomHairRegionsProcessCanceledException?
                processCancellation =
                    cancellation as
                        FaceGeomHairRegionsProcessCanceledException;
            string? terminationFailure =
                processCancellation?.TerminationFailure is
                    { } failure
                    ? $"{failure.GetType().Name}: " +
                      $"{failure.Message} " +
                      $"Inner {failure.InnerException?.GetType().Name}: " +
                      failure.InnerException?.Message
                    : null;
            throw new FaceGeomHairRegionsOperationCanceledException(
                "Hair-region rendering was canceled after process-tree termination; surviving artifacts are attached.",
                allSurvivors
                    .Distinct()
                    .ToImmutableArray(),
                cancellation,
                processCancellation?.SurvivingProcessIds ??
                    [],
                terminationFailure);
        }
        if (operationalFailure is not null)
        {
            ImmutableArray<WorkspacePath> allSurvivors =
                survivors.AddRange(
                        EnumerateOutputSurvivors(
                            request.OutputRoot))
                    .Distinct()
                    .ToImmutableArray();
            FaceGeomHairRegionsProcessRenderException?
                processFailure =
                    operationalFailure as
                        FaceGeomHairRegionsProcessRenderException;
            string? terminationFailure =
                processFailure?.TerminationFailure is
                    { } failure
                    ? $"{failure.GetType().Name}: " +
                      $"{failure.Message} " +
                      $"Inner {failure.InnerException?.GetType().Name}: " +
                      failure.InnerException?.Message
                    : null;
            throw new FaceGeomHairRegionsOperationalException(
                "Hair-region rendering failed during an expected operational boundary; surviving artifacts and process evidence are attached.",
                allSurvivors,
                operationalFailure,
                processFailure?.SurvivingProcessIds ??
                    [],
                terminationFailure);
        }
        if (!survivors.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-cleanup-survivor",
                "Transient renderer cleanup left: " +
                string.Join(
                    ", ",
                    survivors.Select(item => item.Value))));
            result = Refused(diagnostics);
        }
        if (HasErrors(diagnostics))
            result = Refused(diagnostics);
        return result ?? Refused(diagnostics);
    }

    private async ValueTask<FaceGeomHairRegionsRenderResult>
        RenderCoreAsync(
            FaceGeomHairRegionsRenderRequest request,
            WorkspacePath staging,
            ProfileSnapshot openingProfile,
            ImmutableArray<Diagnostic>.Builder diagnostics,
            CancellationToken cancellationToken)
    {
        string dataRootText =
            Path.Combine(staging.Value, "assets", "Data");
        Directory.CreateDirectory(dataRootText);
        var dataRoot = new WorkspacePath(dataRootText);
        var renderOutputRoot = new WorkspacePath(
            Path.Combine(staging.Value, "render-output"));
        Directory.CreateDirectory(renderOutputRoot.Value);
        var privateProfileRoot = new WorkspacePath(
            Path.Combine(staging.Value, "pynifly-profile"));
        await CopyPrivatePyniflyProfileAsync(
            privateProfileRoot,
            diagnostics,
            cancellationToken);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);
        var pythonCacheRoot = new WorkspacePath(
            Path.Combine(staging.Value, "python-cache"));
        Directory.CreateDirectory(pythonCacheRoot.Value);
        ValidateEmptyPythonCache(
            pythonCacheRoot,
            diagnostics,
            "before-render");
        if (HasErrors(diagnostics))
            return Refused(diagnostics);
        var candidatePath = new WorkspacePath(
            Path.Combine(staging.Value, "candidate.nif"));
        await WriteNewAsync(
            candidatePath,
            request.CandidateBytes.AsMemory(),
            cancellationToken);

        var stagedTextures =
            ImmutableArray.CreateBuilder<HairTextureRequestRow>();
        foreach (FaceGeomHairTextureAuthority texture in
                 request.Source.Textures)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stagedTextureText =
                BuildStagedTexturePath(
                    staging,
                    texture.AssetPath);
            if (stagedTextureText.Length >=
                LegacyWindowsPathLimit)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-path-budget",
                    $"Blender-bound texture path is {stagedTextureText.Length} characters; the deterministic Windows renderer requires fewer than {LegacyWindowsPathLimit}: {texture.AssetPath.Value}"));
                return Refused(diagnostics);
            }
            string? parent = Path.GetDirectoryName(
                stagedTextureText);
            if (parent is null)
                throw new InvalidDataException(
                    "Texture staging parent is absent.");
            Directory.CreateDirectory(parent);
            var stagedTexture =
                new WorkspacePath(stagedTextureText);
            byte[] textureBytes =
                await ReadBoundedAsync(
                    texture.MaterializedPath,
                    MaximumTextureBytes,
                    cancellationToken);
            await WriteNewAsync(
                stagedTexture,
                textureBytes,
                cancellationToken);
            DecodedTexture? decoded =
                await DecodeTextureAsync(
                stagedTexture,
                diagnostics,
                cancellationToken);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);
            if (decoded is null)
                return Refused(diagnostics);
            stagedTextures.Add(new HairTextureRequestRow(
                texture.AssetPath.Value,
                texture.ProviderKind.ToString(),
                texture.Provider,
                texture.Sha256.Value,
                texture.Bytes,
                stagedTexture.Value,
                decoded.Path.Value,
                decoded.Sha256.Value,
                decoded.Bytes,
                "texconv-dds-to-png"));
        }

        var requestPath = new WorkspacePath(
            Path.Combine(staging.Value, "request.json"));
        var statusPath = new WorkspacePath(
            Path.Combine(staging.Value, "status.json"));
        object payload = new
        {
            schema = RequestSchema,
            root = staging.Value,
            dataRoot = dataRoot.Value,
            outputRoot = renderOutputRoot.Value,
            candidatePath = candidatePath.Value,
            candidateSha256 =
                request.Source.Candidate.Sha256.Value,
            textureFingerprintSha256 =
                request.Source.TextureFingerprintSha256.Value,
            textures = stagedTextures.ToImmutable(),
            pyniflyProfileRoot =
                privateProfileRoot.Value,
            pythonCachePrefix =
                pythonCacheRoot.Value,
            width = 900,
            height = 900,
            regions = request.Regions
                .OrderBy(item => item.ShapeBlockId)
                .Select(item => new
                {
                    structuralId = item.StructuralId,
                    name = item.Name,
                    duplicateNameOrdinal =
                        item.DuplicateNameOrdinal,
                    shapeBlockType = item.ShapeBlockType,
                    shapeBlockId = item.ShapeBlockId,
                    shaderBlockType = item.ShaderBlockType,
                    shaderBlockId = item.ShaderBlockId,
                    currentColor = item.CurrentColor
                })
                .ToImmutableArray()
        };
        await ValidateStagedTexturesAsync(
            stagedTextures.ToImmutable(),
            diagnostics,
            "before-render",
            cancellationToken);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);
        await WriteNewAsync(
            requestPath,
            JsonSerializer.SerializeToUtf8Bytes(
                payload,
                JsonOptions),
            cancellationToken);

        await VerifyAuthoritiesAsync(
            request,
            diagnostics,
            "changed-before-render",
            cancellationToken);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        EmbeddedBlenderInvocation invocation =
            EmbeddedBlenderInvocationFactory.Create(
                rendererScript.Id,
                [
                    "--request",
                    requestPath.Value,
                    "--status",
                    statusPath.Value
                ]);
        FaceGeomHairRegionsProcessResult process =
            await processRunner.RunAsync(
                blenderPath,
                invocation.Arguments,
                privateProfileRoot,
                pythonCacheRoot,
                invocation.StandardInput,
                TimeSpan.FromMinutes(12),
                cancellationToken);
        if (process.ExitCode != 0)
            ActorwrightObservabilityEventSource.Log.RecordDesktopProcessFailure(
                Activity.Current,
                DesktopFailureOperationId.FaceGeomHairRegions,
                DesktopProcessIdentityId.Blender,
                expectedBlenderSha256.Value,
                process.ExitCode,
                "facegeom-hair-regions-render-process-failed");
        ValidateEmptyPythonCache(
            pythonCacheRoot,
            diagnostics,
            "after-render");
        await ValidateStagedTexturesAsync(
            stagedTextures.ToImmutable(),
            diagnostics,
            "after-render",
            CancellationToken.None);
        AddProcessDiagnostics(
            process,
            "Blender",
            diagnostics);
        if (HasErrors(diagnostics))
        {
            HairRenderStatus? failedStatus =
                await ReadStatusAsync(
                    statusPath,
                    diagnostics,
                    cancellationToken);
            if (failedStatus is not null &&
                !string.IsNullOrWhiteSpace(
                    failedStatus.Error))
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-status-failed",
                    string.Join(
                        Environment.NewLine,
                        new[]
                        {
                            failedStatus.Error,
                            failedStatus.Traceback
                        }.Where(value =>
                            !string.IsNullOrWhiteSpace(
                                value)))));
            return Refused(diagnostics);
        }

        HairRenderStatus? status = await ReadStatusAsync(
            statusPath,
            diagnostics,
            cancellationToken);
        if (status is null || !status.Rendered)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-status-failed",
                status?.Error ??
                "Blender did not return successful structured evidence."));
            return Refused(diagnostics);
        }

        ValidateStatusAuthority(
            status,
            request,
            stagedTextures.ToImmutable(),
            privateProfileRoot,
            pythonCacheRoot,
            diagnostics);
        ImmutableArray<FaceGeomHairRegionsPreviewArtifact>
            artifacts = await ValidateArtifactsAsync(
                status,
                request,
                renderOutputRoot,
                diagnostics,
                cancellationToken);
        await VerifyAuthoritiesAsync(
            request,
            diagnostics,
            "changed-after-render",
            cancellationToken);
        if (HasErrors(diagnostics))
        {
            CleanupOwnedArtifacts(status, request.OutputRoot);
            return Refused(diagnostics);
        }

        diagnostics.Add(new Diagnostic(
            "facegeom-hair-regions-render-created",
            DiagnosticSeverity.Info,
            "Rendered one exact staged FaceGeom import into a closed off-engine HairTint artifact set."));
        return new FaceGeomHairRegionsRenderResult(
            true,
            artifacts,
            expectedScriptSha256,
            request.Source.TextureFingerprintSha256,
            status.FaceGeomImportCount,
            status.NifImportInvocationCount,
            diagnostics.ToImmutable(),
            BuildRenderAuthority(
                status,
                stagedTextures.ToImmutable(),
                openingProfile));
    }

    internal static string ProjectStagedTexturePath(
        WorkspacePath workRoot,
        AssetPath assetPath) =>
        BuildStagedTexturePath(
            new WorkspacePath(
                Path.Combine(
                    workRoot.Value,
                    $"{StagingDirectoryPrefix}{new string('0', 32)}")),
            assetPath);

    private static string BuildStagedTexturePath(
        WorkspacePath staging,
        AssetPath assetPath) =>
        Path.Combine(
            staging.Value,
            "assets",
            "Data",
            assetPath.Value.Replace(
                '/',
                Path.DirectorySeparatorChar));

    private async ValueTask ValidateRequestAsync(
        FaceGeomHairRegionsRenderRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (request.CandidateBytes.IsDefaultOrEmpty ||
            request.CandidateBytes.Length <= 0 ||
            request.CandidateBytes.Length >
                MaximumCandidateBytes ||
            request.CandidateBytes.Length !=
                request.Source.Candidate.Bytes ||
            Hash(request.CandidateBytes.AsSpan()) !=
                request.Source.Candidate.Sha256)
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-candidate",
                "The in-memory candidate differs from its exact source authority."));
        if (request.Source.Candidate.Role !=
                NpcVisualAssetRole.FaceGeom ||
            request.Regions.IsDefaultOrEmpty ||
            request.Regions.Length > 64)
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-source",
                "One FaceGeom candidate and 1 through 64 structural regions are required."));
        if (request.Regions
            .Select(item => item.StructuralId)
            .Distinct(StringComparer.Ordinal)
            .Count() != request.Regions.Length ||
            request.Regions
                .Select(item => item.ShapeBlockId)
                .Distinct()
                .Count() != request.Regions.Length)
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-regions",
                "Structural region identities must be unique."));

        ValidateDirectory(
            request.OutputRoot,
            "public output root",
            diagnostics,
            write: true);
        ValidateDirectory(
            workRoot,
            "disposable renderer work root",
            diagnostics,
            write: true);
        if (request.OutputRoot.IsUnder(workRoot) ||
            workRoot.IsUnder(request.OutputRoot))
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-root-overlap",
                "Public output and disposable work roots must not overlap."));
        if (Directory.Exists(request.OutputRoot.Value) &&
            Directory.EnumerateFileSystemEntries(
                request.OutputRoot.Value).Any())
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-output-not-empty",
                "The public renderer output root must be fresh and empty."));

        foreach ((WorkspacePath path, bool directory, string role) in
                 new[]
                 {
                     (blenderPath, false, "Blender"),
                     (profileRoot, true, "Blender profile"),
                     (pyniflyArchivePath, false, "PyNifly archive"),
                     (texconvPath, false, "Texconv")
                 })
        {
            if (directory)
                ValidateDirectory(
                    path,
                    role,
                    diagnostics,
                    write: false);
            else
                ValidateFilePath(
                    path,
                    role,
                    diagnostics);
        }
        if (rendererScript.Sha256 != expectedScriptSha256)
            diagnostics.Add(Error(
                "facegeom-hair-regions-script-embedded-hash",
                "The embedded renderer script does not match its admitted SHA-256. " +
                $"Expected {expectedScriptSha256.Value}; actual {rendererScript.Sha256.Value}."));
        ValidateFilePath(
            request.Source.Candidate.MaterializedPath,
            "candidate",
            diagnostics);
        foreach (FaceGeomHairTextureAuthority texture in
                 request.Source.Textures)
            ValidateFilePath(
                texture.MaterializedPath,
                $"texture {texture.AssetPath.Value}",
                diagnostics);
        if (HasErrors(diagnostics))
            return;

        byte[] candidateOnDisk = await ReadBoundedAsync(
            request.Source.Candidate.MaterializedPath,
            MaximumCandidateBytes,
            cancellationToken);
        if (candidateOnDisk.LongLength !=
                request.Source.Candidate.Bytes ||
            Hash(candidateOnDisk) !=
                request.Source.Candidate.Sha256 ||
            !candidateOnDisk.AsSpan().SequenceEqual(
                request.CandidateBytes.AsSpan()))
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-candidate-stale",
                "The materialized candidate differs from the in-memory proposal bytes."));
        foreach (FaceGeomHairTextureAuthority texture in
                 request.Source.Textures)
        {
            byte[] bytes = await ReadBoundedAsync(
                texture.MaterializedPath,
                MaximumTextureBytes,
                cancellationToken);
            if (bytes.LongLength != texture.Bytes ||
                Hash(bytes) != texture.Sha256)
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-texture-stale",
                    $"Texture '{texture.AssetPath.Value}' differs from its provider authority."));
        }
        await VerifyAuthoritiesAsync(
            request,
            diagnostics,
            "initial",
            cancellationToken);
    }

    private async ValueTask<DecodedTexture?> DecodeTextureAsync(
        WorkspacePath stagedTexture,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        string directory =
            Path.GetDirectoryName(stagedTexture.Value)!;
        FaceGeomHairRegionsProcessResult result =
            await processRunner.RunAsync(
                texconvPath,
                [
                    "-y",
                    "-ft",
                    "png",
                    "-o",
                    directory,
                    stagedTexture.Value
                ],
                null,
                null,
                TimeSpan.FromMinutes(2),
                cancellationToken);
        if (result.ExitCode != 0)
            ActorwrightObservabilityEventSource.Log.RecordDesktopProcessFailure(
                Activity.Current,
                DesktopFailureOperationId.FaceGeomHairRegions,
                DesktopProcessIdentityId.Texconv,
                expectedTexconvSha256.Value,
                result.ExitCode,
                "facegeom-hair-regions-render-process-failed");
        AddProcessDiagnostics(
            result,
            "Texconv",
            diagnostics);
        if (HasErrors(diagnostics))
            return null;
        string png = Path.ChangeExtension(
            stagedTexture.Value,
            ".png");
        if (!File.Exists(png))
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-texture-decode",
                "Pinned Texconv did not create the expected adjacent PNG."));
            return null;
        }
        byte[] previewBytes = await ReadBoundedAsync(
            new WorkspacePath(png),
            MaximumTextureBytes,
            cancellationToken);
        using SKBitmap? bitmap = SKBitmap.Decode(previewBytes);
        if (bitmap is null ||
            bitmap.Width <= 0 ||
            bitmap.Height <= 0)
        {
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-texture-decode",
                "Pinned Texconv output was not a decodable PNG."));
            return null;
        }
        return new DecodedTexture(
            new WorkspacePath(png),
            Hash(previewBytes),
            previewBytes.LongLength);
    }

    private static async ValueTask ValidateStagedTexturesAsync(
        ImmutableArray<HairTextureRequestRow> textures,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string phase,
        CancellationToken cancellationToken)
    {
        foreach (HairTextureRequestRow texture in textures)
        {
            try
            {
                byte[] source = await ReadBoundedAsync(
                    new WorkspacePath(texture.SourcePath),
                    MaximumTextureBytes,
                    cancellationToken);
                byte[] preview = await ReadBoundedAsync(
                    new WorkspacePath(texture.PreviewPath),
                    MaximumTextureBytes,
                    cancellationToken);
                if (source.LongLength != texture.Bytes ||
                    Hash(source).Value != texture.Sha256 ||
                    preview.LongLength !=
                        texture.PreviewBytes ||
                    Hash(preview).Value !=
                        texture.PreviewSha256)
                    throw new InvalidDataException(
                        $"Staged texture '{texture.AssetPath}' changed.");
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    InvalidDataException)
            {
                diagnostics.Add(Error(
                    $"facegeom-hair-regions-render-staged-texture-{phase}",
                    exception.Message));
            }
        }
    }

    private void ValidateFilePath(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!path.IsUnder(labRoot) ||
            !File.Exists(path.Value) ||
            Directory.Exists(path.Value))
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-path",
                $"{role} must be an existing K-local ordinary file."));
        else
            diagnostics.AddRange(
                policy.EvaluateReadRoot(
                    labRoot,
                    path));
        AddReparseDiagnostic(
            path.Value,
            role,
            diagnostics);
    }

    private void ValidateDirectory(
        WorkspacePath path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        bool write)
    {
        if (!path.IsUnder(labRoot) ||
            !Directory.Exists(path.Value))
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-path",
                $"{role} must be an existing K-local directory."));
        else
            diagnostics.AddRange(
                write
                    ? policy.Evaluate(
                        labRoot,
                        path)
                    : policy.EvaluateReadRoot(
                        labRoot,
                        path));
        AddReparseDiagnostic(
            path.Value,
            role,
            diagnostics);
    }

    private static void AddReparseDiagnostic(
        string path,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) ||
                     Directory.Exists(current)) &&
                    File.GetAttributes(current)
                        .HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error(
                        "facegeom-hair-regions-render-reparse",
                        $"{role} traverses a reparse point."));
                    return;
                }
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException)
            {
                diagnostics.Add(Error(
                    "facegeom-hair-regions-render-path-inspection",
                    exception.Message));
                return;
            }
            string? parent =
                Directory.GetParent(current)?.FullName;
            if (string.Equals(
                    parent,
                    current,
                    StringComparison.OrdinalIgnoreCase))
                break;
            current = parent ?? "";
        }
    }

    private static async ValueTask VerifyHashAsync(
        WorkspacePath path,
        Sha256Hash expected,
        string code,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            if (await HashFileAsync(
                    path,
                    cancellationToken) != expected)
                diagnostics.Add(Error(
                    code,
                    $"'{path.Value}' differs from its pinned SHA-256."));
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            diagnostics.Add(Error(
                code,
                exception.Message));
        }
    }

    private static void AddProcessDiagnostics(
        FaceGeomHairRegionsProcessResult process,
        string role,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!process.SurvivingProcessIds.IsDefaultOrEmpty)
            diagnostics.Add(Error(
                "facegeom-hair-regions-render-process-survivor",
                $"{role} retained process IDs: " +
                string.Join(
                    ",",
                    process.SurvivingProcessIds)));
        if (process.ExitCode == 0)
            return;
        string detail = string.Join(
            " ",
            new[]
            {
                process.StandardOutput,
                process.StandardError
            }.Where(value =>
                !string.IsNullOrWhiteSpace(value)));
        if (detail.Length > 8192)
            detail =
                detail[..2048] +
                "\n...[process output truncated]...\n" +
                detail[^4096..];
        diagnostics.Add(Error(
            "facegeom-hair-regions-render-process-failed",
            $"{role} exited {process.ExitCode}: {detail}"));
    }

    private static void CleanupOwnedArtifacts(
        HairRenderStatus status,
        WorkspacePath outputRoot)
    {
        foreach (HairArtifact artifact in
                 status.Artifacts ?? [])
        {
            try
            {
                string path = Path.GetFullPath(
                    artifact.Path ?? "");
                if (!string.Equals(
                        Path.GetDirectoryName(path),
                        outputRoot.Value,
                        StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(path))
                    continue;
                byte[] bytes = File.ReadAllBytes(path);
                if (string.Equals(
                        Hash(bytes).Value,
                        artifact.Sha256,
                        StringComparison.OrdinalIgnoreCase))
                    File.Delete(path);
            }
            catch (Exception exception) when (
                exception is IOException or
                    UnauthorizedAccessException or
                    ArgumentException)
            {
            }
        }
    }

    private static ImmutableArray<WorkspacePath>
        CleanupStaging(WorkspacePath staging)
    {
        try
        {
            if (Directory.Exists(staging.Value))
                Directory.Delete(
                    staging.Value,
                    recursive: true);
            return [];
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return [staging];
        }
    }

    private static ImmutableArray<WorkspacePath>
        EnumerateOutputSurvivors(WorkspacePath outputRoot)
    {
        try
        {
            return Directory.Exists(outputRoot.Value)
                ? Directory.EnumerateFiles(
                        outputRoot.Value,
                        "*",
                        SearchOption.TopDirectoryOnly)
                    .Select(path =>
                        new WorkspacePath(
                            Path.GetFullPath(path)))
                    .ToImmutableArray()
                : [];
        }
        catch (Exception exception) when (
            exception is IOException or
                UnauthorizedAccessException)
        {
            return [outputRoot];
        }
    }

    private static async ValueTask<byte[]> ReadBoundedAsync(
        WorkspacePath path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        if (stream.Length is <= 0 ||
            stream.Length > maximumBytes ||
            stream.Length > int.MaxValue)
            throw new InvalidDataException(
                $"'{path.Value}' is outside its bounded size.");
        byte[] bytes = new byte[checked((int)stream.Length)];
        await stream.ReadExactlyAsync(
            bytes,
            cancellationToken);
        return bytes;
    }

    private static async ValueTask WriteNewAsync(
        WorkspacePath path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        await stream.WriteAsync(
            bytes,
            cancellationToken);
        await stream.FlushAsync(
            cancellationToken);
    }

    private static async ValueTask WriteNewAllowEmptyAsync(
        WorkspacePath path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        if (!bytes.IsEmpty)
            await stream.WriteAsync(
                bytes,
                cancellationToken);
        await stream.FlushAsync(
            cancellationToken);
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous |
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(
                stream,
                cancellationToken)));
    }

    private static Sha256Hash Hash(
        ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(
            SHA256.HashData(bytes)));

    private static bool HasErrors(
        IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item =>
            item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(
        string code,
        string message) =>
        new(
            code,
            DiagnosticSeverity.Error,
            message);

    private static FaceGeomHairRegionsRenderResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            false,
            [],
            null,
            null,
            0,
            0,
            diagnostics.ToImmutable());

}
