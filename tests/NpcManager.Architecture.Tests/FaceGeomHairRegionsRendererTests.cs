using System.Collections.Immutable;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Rendering;
using SkiaSharp;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestFaceGeomHairRegionsRenderer()
    {
        TestHairRegionsRendererPreflightsSixtyFourRegionBudget();
        await TestHairRegionsRendererEnablesPinnedPythonEnvironment();
        await TestHairRegionsRendererSanitizesInheritedPythonEnvironment();
        await TestHairRegionsRendererTypesProcessStartFailure();
        await TestHairRegionsRendererPreservesKillFailureOnCancellation();
        await TestHairRegionsRendererTypesProcessExecutionFailure();
        await TestHairRegionsRendererTypesOutputCaptureFailure();
        await TestHairRegionsRendererCarriesProcessCancellationDetails();
        await TestHairRegionsRendererCarriesExecutionFailureDetails();
        await TestHairRegionsRendererTypesStagingCreateFailure();
        await TestHairRegionsRendererStagesAndValidatesOneImport();
        await TestHairRegionsRendererRefusesStructuralStatusDrift();
        await TestHairRegionsRendererAcceptsClosedTextureBindingModes();
        await TestHairRegionsRendererRefusesInvalidTextureBindingModes();
        await TestHairRegionsRendererRefusesFalseSampledEnvironmentBinding();
        await TestHairRegionsRendererTypesMalformedRuntimePaths();
        await TestHairRegionsRendererRefusesNoncanonicalModuleIdentity();
        await TestHairRegionsRendererBindsNamespacePackageAuthority();
    }

    private static async Task
        TestHairRegionsRendererEnablesPinnedPythonEnvironment()
    {
        var process = new CompletedHairRendererProcess();
        var host = new CapturingHairRendererProcessHost(process);
        var runner =
            new SystemFaceGeomHairRegionsProcessRunner(host);
        string cache = Path.Combine(
            FindGate220ProjectRoot(),
            "03-builds",
            "work",
            "controlled-python-cache");

        await runner.RunAsync(
            new WorkspacePath(
                Path.Combine(
                    FindGate220ProjectRoot(),
                    "03-builds",
                    "work",
                    "controlled-renderer.exe")),
            ["--background"],
            null,
            new WorkspacePath(cache),
            "{\"entry\":\"render_npc_preview_bundle\"}",
            TimeSpan.FromSeconds(1),
            CancellationToken.None);

        Assert(
            host.StartInfo is not null &&
            host.StartInfo.ArgumentList.Count == 2 &&
            host.StartInfo.ArgumentList[0] ==
                "--python-use-system-env" &&
            host.StartInfo.ArgumentList[1] == "--background" &&
            host.StartInfo.RedirectStandardInput,
            "Renderer did not admit the pinned Python environment and embedded-script stdin before Blender parsed its command line.");
    }

    private static async Task
        TestHairRegionsRendererSanitizesInheritedPythonEnvironment()
    {
        string? priorPath =
            Environment.GetEnvironmentVariable("PYTHONPATH");
        string? priorHome =
            Environment.GetEnvironmentVariable("PYTHONHOME");
        try
        {
            Environment.SetEnvironmentVariable(
                "PYTHONPATH",
                @"K:\untrusted-python-path");
            Environment.SetEnvironmentVariable(
                "PYTHONHOME",
                @"K:\untrusted-python-home");
            var host = new CapturingHairRendererProcessHost(
                new CompletedHairRendererProcess());
            var runner =
                new SystemFaceGeomHairRegionsProcessRunner(host);
            string cache = Path.Combine(
                FindGate220ProjectRoot(),
                "03-builds",
                "work",
                "controlled-python-cache");

            await runner.RunAsync(
                new WorkspacePath(
                    Path.Combine(
                        FindGate220ProjectRoot(),
                        "03-builds",
                        "work",
                        "controlled-renderer.exe")),
                ["--background"],
                null,
                new WorkspacePath(cache),
                TimeSpan.FromSeconds(1),
                CancellationToken.None);

            string[] pythonKeys =
                host.StartInfo!.Environment.Keys
                    .Where(key => key.StartsWith(
                        "PYTHON",
                        StringComparison.OrdinalIgnoreCase))
                    .OrderBy(
                        key => key,
                        StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            Assert(
                pythonKeys.SequenceEqual(
                    [
                        "PYTHONDONTWRITEBYTECODE",
                        "PYTHONNOUSERSITE",
                        "PYTHONPYCACHEPREFIX"
                    ],
                    StringComparer.OrdinalIgnoreCase) &&
                host.StartInfo.Environment[
                    "PYTHONNOUSERSITE"] == "1",
                "Renderer admitted inherited Python path or user-site injection while enabling its pinned environment.");
        }
        finally
        {
            Environment.SetEnvironmentVariable(
                "PYTHONPATH",
                priorPath);
            Environment.SetEnvironmentVariable(
                "PYTHONHOME",
                priorHome);
        }
    }

    private static async Task
        TestHairRegionsRendererTypesProcessStartFailure()
    {
        string missingExecutable = Path.Combine(
            FindGate220ProjectRoot(),
            "03-builds",
            "work",
            $"missing-renderer-{Guid.NewGuid():N}.exe");
        var runner =
            new SystemFaceGeomHairRegionsProcessRunner();
        try
        {
            await runner.RunAsync(
                new WorkspacePath(missingExecutable),
                [],
                null,
                null,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
        }
        catch (IOException exception)
        {
            Assert(
                exception.GetType().Name ==
                    "FaceGeomHairRegionsProcessStartException" &&
                exception.InnerException is
                    System.ComponentModel.Win32Exception,
                "Renderer process start failure lost its exact typed Win32 cause.");
            return;
        }

        throw new InvalidOperationException(
            "Missing renderer executable did not fail as a typed process-start operation.");
    }

    private static async Task
        TestHairRegionsRendererPreservesKillFailureOnCancellation()
    {
        var process = new KillFailingHairRendererProcess();
        var runner =
            new SystemFaceGeomHairRegionsProcessRunner(
                new ControlledHairRendererProcessHost(process));
        try
        {
            await runner.RunAsync(
                new WorkspacePath(
                    Path.Combine(
                        FindGate220ProjectRoot(),
                        "03-builds",
                        "work",
                        "controlled-renderer.exe")),
                [],
                null,
                null,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
        }
        catch (
            FaceGeomHairRegionsProcessCanceledException
                exception)
        {
            Assert(
                exception.SurvivingProcessIds.SequenceEqual(
                    [4242]) &&
                exception.TerminationFailure is
                {
                    ProcessId: 4242,
                    InnerException:
                        Win32Exception
                },
                "Cancellation lost the typed process-kill failure or exact surviving PID.");
            return;
        }

        throw new InvalidOperationException(
            "Renderer kill failure did not remain a typed cancellation with exact cleanup details.");
    }

    private static async Task
        TestHairRegionsRendererTypesProcessExecutionFailure()
    {
        var process =
            new ExecutionFailingHairRendererProcess();
        var runner =
            new SystemFaceGeomHairRegionsProcessRunner(
                new ControlledHairRendererProcessHost(
                    process));
        try
        {
            await runner.RunAsync(
                new WorkspacePath(
                    Path.Combine(
                        FindGate220ProjectRoot(),
                        "03-builds",
                        "work",
                        "controlled-renderer.exe")),
                [],
                null,
                null,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
        }
        catch (IOException exception)
        {
            Assert(
                exception.GetType().Name ==
                    "FaceGeomHairRegionsProcessRenderException" &&
                exception.InnerException is IOException &&
                process.KillAttempted &&
                ((FaceGeomHairRegionsProcessRenderException)
                    exception)
                    .SurvivingProcessIds
                    .SequenceEqual([4343]) &&
                ((FaceGeomHairRegionsProcessRenderException)
                    exception)
                    .TerminationFailure is
                    {
                        ProcessId: 4343,
                        InnerException: Win32Exception
                    },
                "Renderer execution failure lost its termination attempt, exact surviving PID, or typed cause.");
            return;
        }

        throw new InvalidOperationException(
            "Renderer execution failure was not translated to the typed render operation.");
    }

    private static async Task
        TestHairRegionsRendererTypesOutputCaptureFailure()
    {
        var process =
            new OutputAccessorFailingHairRendererProcess();
        var runner =
            new SystemFaceGeomHairRegionsProcessRunner(
                new ControlledHairRendererProcessHost(
                    process));
        try
        {
            await runner.RunAsync(
                new WorkspacePath(
                    Path.Combine(
                        FindGate220ProjectRoot(),
                        "03-builds",
                        "work",
                        "controlled-renderer.exe")),
                [],
                null,
                null,
                TimeSpan.FromSeconds(1),
                CancellationToken.None);
        }
        catch (
            FaceGeomHairRegionsProcessRenderException
                exception)
        {
            Assert(
                process.KillAttempted &&
                exception.SurvivingProcessIds
                    .SequenceEqual([4444]) &&
                exception.InnerException is IOException &&
                exception.TerminationFailure is
                {
                    ProcessId: 4444,
                    InnerException: Win32Exception
                },
                "Output getter/read-start failure escaped the bounded termination and evidence boundary.");
            return;
        }

        throw new InvalidOperationException(
            "Output accessor failure was not translated to a typed render operation.");
    }

    private static async Task
        TestHairRegionsRendererCarriesProcessCancellationDetails()
    {
        string scratch =
            CreateHairRendererScratch(
                "process-cancellation-detail");
        try
        {
            HairRendererFixture fixture =
                CreateHairRendererFixture(scratch);
            try
            {
                await fixture.Create(
                        new ProcessCancellingHairRendererRunner())
                    .RenderAsync(
                        fixture.Request,
                        CancellationToken.None);
            }
            catch (
                FaceGeomHairRegionsOperationCanceledException
                    exception)
            {
                Assert(
                    exception.SurvivingProcessIds
                        .SequenceEqual([4545]) &&
                    exception.ProcessTerminationFailure
                        ?.Contains(
                            "Controlled termination denial",
                            StringComparison.Ordinal) == true,
                    "Renderer wrapping lost the exact surviving process or termination detail.");
                return;
            }

            throw new InvalidOperationException(
                "Controlled process cancellation was not carried through the renderer boundary.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task
        TestHairRegionsRendererCarriesExecutionFailureDetails()
    {
        string scratch =
            CreateHairRendererScratch(
                "process-execution-detail");
        try
        {
            HairRendererFixture fixture =
                CreateHairRendererFixture(scratch);
            var process =
                new ExecutionFailingHairRendererProcess();
            try
            {
                await fixture.Create(
                        new SystemFaceGeomHairRegionsProcessRunner(
                            new ControlledHairRendererProcessHost(
                                process)))
                    .RenderAsync(
                        fixture.Request,
                        CancellationToken.None);
            }
            catch (
                FaceGeomHairRegionsOperationalException
                    exception)
            {
                Assert(
                    process.KillAttempted &&
                    exception.SurvivingProcessIds
                        .SequenceEqual([4343]) &&
                    exception.ProcessTerminationFailure
                        ?.Contains(
                            "Controlled execution-failure termination denial",
                            StringComparison.Ordinal) == true &&
                    !Directory.EnumerateFileSystemEntries(
                            fixture.WorkRoot.Value)
                        .Any(),
                    "Renderer operational wrapping lost bounded termination evidence or private staging rollback.");
                return;
            }

            throw new InvalidOperationException(
                "Controlled execution failure did not cross the renderer as typed operational evidence.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task
        TestHairRegionsRendererTypesStagingCreateFailure()
    {
        string scratch =
            CreateHairRendererScratch(
                "staging-create-failure");
        try
        {
            HairRendererFixture fixture =
                CreateHairRendererFixture(scratch);
            var process =
                new ControlledHairRendererProcess();
            var policy =
                new StagingCreateFailingWorkspacePolicy(
                    fixture.WorkRoot);
            try
            {
                FaceGeomHairRegionsRenderResult result =
                    await fixture.Create(
                        process,
                        policy)
                    .RenderAsync(
                        fixture.Request,
                        CancellationToken.None);
                throw new InvalidOperationException(
                    "Controlled staging acquisition failure escaped the typed renderer boundary. " +
                    $"armed={policy.CreateFailureArmed}; rendered={result.Rendered}; " +
                    $"diagnostics={string.Join(",", result.Diagnostics.Select(item => item.Code))}");
            }
            catch (
                FaceGeomHairRegionsOperationalException
                    exception)
            {
                Assert(
                    policy.CreateFailureArmed &&
                    exception.InnerException is IOException &&
                    !exception.SurvivingArtifacts.IsDefault &&
                    exception.SurvivingArtifacts.IsEmpty &&
                    process.BlenderInvocationCount == 0,
                    "Staging acquisition failure did not become typed operational evidence with its exact empty survivor set.");
                return;
            }
        }
        finally
        {
            Directory.Delete(
                scratch,
                recursive: true);
        }
    }

    private static void
        TestHairRegionsRendererPreflightsSixtyFourRegionBudget()
    {
        const long imageLimit = 16L * 1024L * 1024L;
        ImmutableArray<long> sixtyFourRegionBundle =
            Enumerable.Repeat(
                    imageLimit,
                    2 + 64 * 2)
                .ToImmutableArray();
        bool accepted =
            BlenderFaceGeomHairRegionsRenderer
                .FitsRenderedBundleBudget(
                    sixtyFourRegionBundle,
                    out long rejectedAtBytes);
        Assert(
            !accepted &&
            rejectedAtBytes ==
                17L * imageLimit,
            "A maximum-cardinality 64-region bundle was not cumulatively refused before multi-GiB Content allocation.");
        Assert(
            BlenderFaceGeomHairRegionsRenderer
                .FitsRenderedBundleBudget(
                    Enumerable.Repeat(
                            imageLimit,
                            16)
                        .ToImmutableArray(),
                    out long admittedBytes) &&
            admittedBytes ==
                256L * 1024L * 1024L,
            "The exact cumulative render handoff boundary drifted.");
    }

    private static async Task
        TestHairRegionsRendererStagesAndValidatesOneImport()
    {
        string scratch = CreateHairRendererScratch("success");
        try
        {
            HairRendererFixture fixture =
                CreateHairRendererFixture(scratch);
            var process = new ControlledHairRendererProcess();
            var renderer = fixture.Create(process);

            FaceGeomHairRegionsRenderResult result =
                await renderer.RenderAsync(
                    fixture.Request,
                    CancellationToken.None);

            Assert(
                result.Rendered,
                "Hair-region renderer refused valid controlled evidence: " +
                string.Join(
                    ",",
                    result.Diagnostics.Select(item => item.Code)));
            Assert(
                process.BlenderInvocationCount == 1,
                "One preview revision did not start exactly one Blender process.");
            Assert(
                result.FaceGeomImportCount == 1 &&
                result.NifImportInvocationCount == 1,
                "One-import structured authority was not retained.");
            Assert(
                result.Artifacts.Length == 4,
                "One region did not return combined/contact/thumbnail/mask artifacts.");
            Assert(
                result.Artifacts.Count(item =>
                    item.Kind ==
                    FaceGeomHairRegionsPreviewArtifactKind.RegionMask &&
                    item.NonEmptyPixelCount > 0) == 1,
                "The structural role mask was not independently proven nonempty.");
            Assert(
                process.ObservedCandidatePath is not null &&
                !new WorkspacePath(process.ObservedCandidatePath)
                    .IsUnder(fixture.Request.OutputRoot),
                "The exact candidate was staged inside the public output root.");
            Assert(
                process.ObservedRegionStructuralId ==
                    fixture.Request.Regions[0].StructuralId &&
                process.ObservedDuplicateNameOrdinal == 0,
                "The request lost structural or duplicate-name identity.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task
        TestHairRegionsRendererRefusesStructuralStatusDrift()
    {
        string scratch = CreateHairRendererScratch("drift");
        try
        {
            HairRendererFixture fixture =
                CreateHairRendererFixture(scratch);
            var process = new ControlledHairRendererProcess(
                wrongDuplicateOrdinal: true);
            FaceGeomHairRegionsRenderResult result =
                await fixture.Create(process).RenderAsync(
                    fixture.Request,
                    CancellationToken.None);

            Assert(
                !result.Rendered &&
                result.Diagnostics.Any(item =>
                    item.Code ==
                    "facegeom-hair-regions-render-status-mapping"),
                "Renderer accepted status whose duplicate-name ordinal drifted.");
            Assert(
                process.BlenderInvocationCount == 1,
                "Structural refusal unexpectedly launched Blender more than once.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task
        TestHairRegionsRendererAcceptsClosedTextureBindingModes()
    {
        foreach ((string mode, string semantic, bool loadedImage) in
                 new[]
                 {
                     ("SampledImage", "BSShaderTextureSet_Diffuse", true),
                     ("BoundUnmodeled", "BSShaderTextureSet_EnvMap", false)
                 })
        {
            string scratch = CreateHairRendererScratch(
                mode == "SampledImage" ? "tm-s" : "tm-b");
            try
            {
                HairRendererFixture fixture =
                    CreateHairRendererFixture(
                        scratch,
                        includeTexture: true);
                var process = new ControlledHairRendererProcess(
                    textureBindingMode: mode,
                    textureBindingSemantic: semantic,
                    includeLoadedImagePath: loadedImage);
                FaceGeomHairRegionsRenderResult result =
                    await fixture.Create(process).RenderAsync(
                        fixture.Request,
                        CancellationToken.None);

                Assert(
                    result.Rendered &&
                    result.Authority is not null &&
                    result.Authority.Textures.Length == 1 &&
                    result.Authority.Textures[0]
                        .Bindings.Length == 1,
                    $"Renderer refused closed texture binding mode '{mode}': " +
                    string.Join(
                        ",",
                        result.Diagnostics.Select(item =>
                            item.Code)));
            }
            finally
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static async Task
        TestHairRegionsRendererRefusesInvalidTextureBindingModes()
    {
        foreach (
            (
                string suffix,
                string mode,
                string semantic,
                bool loadedImage
            ) in
                 new[]
                 {
                     ("u", "UnknownMode", "BSShaderTextureSet_EnvMap", false),
                     ("bp", "BoundUnmodeled", "BSShaderTextureSet_EnvMap", true),
                     ("sn", "SampledImage", "BSShaderTextureSet_Diffuse", false),
                     ("bf", "BoundUnmodeled", "BSShaderTextureSet_FacegenDetail", false)
                 })
        {
            string scratch = CreateHairRendererScratch(
                $"ti-{suffix}");
            try
            {
                HairRendererFixture fixture =
                    CreateHairRendererFixture(
                        scratch,
                        includeTexture: true);
                var process = new ControlledHairRendererProcess(
                    textureBindingMode: mode,
                    textureBindingSemantic: semantic,
                    includeLoadedImagePath: loadedImage);
                FaceGeomHairRegionsRenderResult result =
                    await fixture.Create(process).RenderAsync(
                        fixture.Request,
                        CancellationToken.None);

                Assert(
                    !result.Rendered &&
                    result.Diagnostics.Any(item =>
                        item.Code ==
                        "facegeom-hair-regions-render-loaded-textures"),
                    $"Renderer accepted invalid texture binding evidence '{mode}' loadedImage={loadedImage}.");
            }
            finally
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static async Task
        TestHairRegionsRendererRefusesFalseSampledEnvironmentBinding()
    {
        string scratch = CreateHairRendererScratch(
            "sampled-env");
        try
        {
            HairRendererFixture fixture =
                CreateHairRendererFixture(
                    scratch,
                    includeTexture: true);
            FaceGeomHairRegionsRenderResult result =
                await fixture.Create(
                        new ControlledHairRendererProcess(
                            textureBindingMode:
                                "SampledImage",
                            textureBindingSemantic:
                                "BSShaderTextureSet_EnvMap",
                            includeLoadedImagePath: true))
                    .RenderAsync(
                        fixture.Request,
                        CancellationToken.None);

            Assert(
                !result.Rendered &&
                result.Diagnostics.Any(item =>
                    item.Code ==
                    "facegeom-hair-regions-render-loaded-textures"),
                "Renderer accepted an unmodeled environment-map semantic as SampledImage authority.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static async Task
        TestHairRegionsRendererTypesMalformedRuntimePaths()
    {
        foreach (
            (
                string Suffix,
                string? PythonCachePrefix,
                string? ModuleCachedPath
            ) value in
                 new[]
                 {
                     ("cache", "bad\0cache", (string?)null),
                     ("module", (string?)null, "bad\0module.pyc")
                 })
        {
            string scratch = CreateHairRendererScratch(
                $"bad-path-{value.Suffix}");
            try
            {
                HairRendererFixture fixture =
                    CreateHairRendererFixture(scratch);
                FaceGeomHairRegionsRenderResult? result = null;
                Exception? escaped = null;
                try
                {
                    result = await fixture.Create(
                            new ControlledHairRendererProcess(
                                statusPythonCachePrefix:
                                    value.PythonCachePrefix,
                                statusModuleCachedPath:
                                    value.ModuleCachedPath))
                        .RenderAsync(
                            fixture.Request,
                            CancellationToken.None);
                }
                catch (Exception exception)
                {
                    escaped = exception;
                }

                Assert(
                    escaped is null &&
                    result is { Rendered: false } &&
                    result.Diagnostics.Any(item =>
                        item.Code ==
                        "facegeom-hair-regions-render-pynifly-runtime"),
                    $"Malformed renderer-status path '{value.Suffix}' escaped the typed diagnostic contract: {escaped?.GetType().Name}.");
            }
            finally
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static async Task
        TestHairRegionsRendererRefusesNoncanonicalModuleIdentity()
    {
        foreach (
            (
                string Suffix,
                string ModuleName,
                string RelativeSource,
                bool NamespaceDirectory,
                bool NormalizeFingerprint
            ) value in
                 new[]
                 {
                     (
                         "alias",
                         "io_scene_nifly",
                         "util/../__init__.py",
                         false,
                         false),
                     (
                         "name",
                         "io_scene_nifly.not_the_root",
                         "__init__.py",
                         false,
                         false),
                     (
                         "backslash",
                         "io_scene_nifly.util",
                         "util\\",
                         true,
                         true)
                 })
        {
            string scratch = CreateHairRendererScratch(
                $"module-{value.Suffix}");
            try
            {
                HairRendererFixture fixture =
                    CreateHairRendererFixture(scratch);
                FaceGeomHairRegionsRenderResult result =
                    await fixture.Create(
                            new ControlledHairRendererProcess(
                                statusModuleName:
                                    value.ModuleName,
                                statusModuleRelativeSource:
                                    value.RelativeSource,
                                statusModuleIsNamespace:
                                    value.NamespaceDirectory,
                                normalizeStatusModuleFingerprintRelativeSource:
                                    value.NormalizeFingerprint))
                        .RenderAsync(
                            fixture.Request,
                            CancellationToken.None);

                Assert(
                    !result.Rendered &&
                    result.Diagnostics.Any(item =>
                        item.Code ==
                        "facegeom-hair-regions-render-pynifly-runtime"),
                    $"Renderer accepted noncanonical PyNifly module identity '{value.ModuleName}' => '{value.RelativeSource}'.");
            }
            finally
            {
                Directory.Delete(scratch, recursive: true);
            }
        }
    }

    private static async Task
        TestHairRegionsRendererBindsNamespacePackageAuthority()
    {
        string scratch = CreateHairRendererScratch("namespace");
        try
        {
            HairRendererFixture fixture =
                CreateHairRendererFixture(scratch);
            FaceGeomHairRegionsRenderResult accepted =
                await fixture.Create(
                        new ControlledHairRendererProcess(
                            includeNamespaceModule: true))
                    .RenderAsync(
                        fixture.Request,
                        CancellationToken.None);
            Assert(
                accepted.Rendered &&
                accepted.Authority is not null &&
                accepted.Authority.PyniflyModules.Length == 2,
                "Renderer refused an exact source-less PyNifly namespace package directory: " +
                string.Join(
                    " | ",
                    accepted.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));

            string driftOutput = Path.Combine(
                scratch,
                "output-drift");
            Directory.CreateDirectory(driftOutput);
            FaceGeomHairRegionsRenderResult drifted =
                await fixture.Create(
                        new ControlledHairRendererProcess(
                            includeNamespaceModule: true,
                            namespaceHashDrift: true))
                    .RenderAsync(
                        fixture.Request with
                        {
                            OutputRoot = new WorkspacePath(
                                driftOutput)
                        },
                        CancellationToken.None);
            Assert(
                !drifted.Rendered &&
                drifted.Diagnostics.Any(item =>
                    item.Code ==
                    "facegeom-hair-regions-render-pynifly-runtime"),
                "Renderer accepted a PyNifly namespace directory whose exact source fingerprint drifted.");
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static HairRendererFixture CreateHairRendererFixture(
        string scratch,
        bool includeTexture = false)
    {
        string tools = Path.Combine(scratch, "tools");
        string profile = Path.Combine(scratch, "profile");
        string work = Path.Combine(scratch, "work");
        string source = Path.Combine(scratch, "source");
        string output = Path.Combine(scratch, "output");
        Directory.CreateDirectory(tools);
        Directory.CreateDirectory(profile);
        string profileAddon = Path.Combine(
            profile,
            "scripts",
            "addons",
            "io_scene_nifly");
        Directory.CreateDirectory(profileAddon);
        WriteTool(profileAddon, "__init__.py", "pynifly-profile");
        string profileUtil = Path.Combine(
            profileAddon,
            "util");
        Directory.CreateDirectory(profileUtil);
        WriteTool(
            profileUtil,
            "settings.py",
            "namespace-source");
        Directory.CreateDirectory(work);
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(output);
        string blender = WriteTool(tools, "blender.exe", "blender");
        string pynifly = WriteTool(
            tools, "io_scene_nifly.zip", "pynifly");
        string script = WriteTool(
            tools, "render_npc_preview_bundle.py", "renderer");
        string texconv = WriteTool(tools, "texconv.exe", "texconv");
        byte[] candidateBytes = [1, 3, 3, 7, 9];
        string candidatePath = Path.Combine(source, "candidate.nif");
        File.WriteAllBytes(candidatePath, candidateBytes);
        var candidate = new NpcVisualAsset(
            NpcVisualAssetRole.FaceGeom,
            new AssetPath(
                "meshes/npcmanager/hair-regions/candidate.nif"),
            "controlled",
            Sha(candidateBytes),
            candidateBytes.LongLength,
            new WorkspacePath(candidatePath),
            false,
            []);
        ImmutableArray<FaceGeomHairTextureAuthority> textures = [];
        if (includeTexture)
        {
            byte[] textureBytes =
                [0x44, 0x44, 0x53, 0x20, 0x11, 0x22];
            string texturePath = Path.Combine(
                source,
                "controlled-hair.dds");
            File.WriteAllBytes(texturePath, textureBytes);
            textures =
            [
                new FaceGeomHairTextureAuthority(
                    new AssetPath(
                        "textures/a.dds"),
                    AssetProviderKind.Loose,
                    "controlled-provider",
                    Sha(textureBytes),
                    textureBytes.LongLength,
                    new WorkspacePath(texturePath))
            ];
        }
        Sha256Hash textureFingerprint =
            FaceGeomHairTextureAuthorityCanonical.Fingerprint(
                textures);
        var sourceGraph = new FaceGeomHairRegionsPreviewSource(
            candidate,
            textures,
            textureFingerprint);
        var region = new FaceGeomHairRegionsRegion(
            "shape:16:shader:20",
            "Duplicate",
            0,
            "BSTriShape",
            16,
            120,
            "BSLightingShaderProperty",
            20,
            "shader:20",
            ["shape:16:shader:20"],
            [],
            21,
            ["textures/actors/hair.dds"],
            "#D6BE83",
            [0U, 0U, 0U],
            140,
            12,
            FaceGeomHairRegionRole.Primary);
        var request = new FaceGeomHairRegionsRenderRequest(
            sourceGraph,
            candidateBytes.ToImmutableArray(),
            [region],
            new WorkspacePath(output));
        return new HairRendererFixture(
            request,
            new WorkspacePath(blender),
            new WorkspacePath(profile),
            new WorkspacePath(pynifly),
            new WorkspacePath(script),
            new WorkspacePath(texconv),
            new WorkspacePath(work),
            Sha(File.ReadAllBytes(blender)),
            Sha(File.ReadAllBytes(pynifly)),
            FingerprintProfile(profile),
            Sha(File.ReadAllBytes(script)),
            Sha(File.ReadAllBytes(texconv)));
    }

    private static string CreateHairRendererScratch(string suffix)
    {
        string parent = Path.Combine(
            FindGate220ProjectRoot(),
            "03-builds",
            "work");
        Directory.CreateDirectory(parent);
        string scratch = Path.Combine(
            parent,
            $"architecture-hair-renderer-{suffix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        return scratch;
    }

    private static string WriteTool(
        string root,
        string name,
        string contents)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, contents);
        return path;
    }

    private static Sha256Hash Sha(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Sha256Hash FingerprintProfile(
        string profile)
    {
        string addonRoot = Path.Combine(
            profile,
            "scripts",
            "addons",
            "io_scene_nifly");
        using IncrementalHash hash =
            IncrementalHash.CreateHash(
                HashAlgorithmName.SHA256);
        foreach (string file in Directory.EnumerateFiles(
                     addonRoot,
                     "*",
                     SearchOption.AllDirectories)
                     .Where(path =>
                         !path.Contains(
                             $"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}",
                             StringComparison.Ordinal) &&
                         !Path.GetExtension(path).Equals(
                             ".pyc",
                             StringComparison.OrdinalIgnoreCase))
                     .OrderBy(
                         path => Path.GetRelativePath(
                                 addonRoot,
                                 path)
                             .Replace('\\', '/'),
                         StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            string relative = Path.GetRelativePath(
                    addonRoot,
                    file)
                .Replace('\\', '/');
            hash.AppendData(System.Text.Encoding.UTF8.GetBytes(
                $"{relative}\0{info.Length}\0"));
            hash.AppendData(SHA256.HashData(
                File.ReadAllBytes(file)));
        }
        return new Sha256Hash(Convert.ToHexString(
            hash.GetHashAndReset()));
    }

    private sealed record HairRendererFixture(
        FaceGeomHairRegionsRenderRequest Request,
        WorkspacePath Blender,
        WorkspacePath Profile,
        WorkspacePath Pynifly,
        WorkspacePath Script,
        WorkspacePath Texconv,
        WorkspacePath WorkRoot,
        Sha256Hash BlenderHash,
        Sha256Hash PyniflyHash,
        Sha256Hash PyniflyProfileHash,
        Sha256Hash ScriptHash,
        Sha256Hash TexconvHash)
    {
        public BlenderFaceGeomHairRegionsRenderer Create(
            IFaceGeomHairRegionsProcessRunner runner,
            IWorkspacePolicy? policy = null) =>
            new(
                Blender,
                Profile,
                Pynifly,
                Script,
                Texconv,
                policy ??
                    new KOnlyWorkspacePolicy(
                        new WorkspacePath(
                            "K:\\ExampleWorkspace"),
                        new WorkspacePath(
                            "F:\\ExampleGame")),
                new WorkspacePath("K:\\ExampleWorkspace"),
                WorkRoot,
                BlenderHash,
                PyniflyHash,
                PyniflyProfileHash,
                ScriptHash,
                TexconvHash,
                runner);
    }

    private sealed class StagingCreateFailingWorkspacePolicy(
        WorkspacePath workRoot) :
        IWorkspacePolicy
    {
        private readonly KOnlyWorkspacePolicy inner =
            new(
                new WorkspacePath(
                    "K:\\ExampleWorkspace"),
                new WorkspacePath(
                    "F:\\ExampleGame"));

        public bool CreateFailureArmed
        {
            get;
            private set;
        }

        public ImmutableArray<Diagnostic> Evaluate(
            WorkspacePath workspaceRoot,
            WorkspacePath outputRoot)
        {
            ImmutableArray<Diagnostic> diagnostics =
                inner.Evaluate(
                    workspaceRoot,
                    outputRoot);
            if (!CreateFailureArmed &&
                outputRoot == workRoot)
            {
                Directory.Delete(
                    workRoot.Value,
                    recursive: true);
                File.WriteAllBytes(
                    workRoot.Value,
                    [71, 72, 73]);
                CreateFailureArmed = true;
            }
            return diagnostics;
        }

        public ImmutableArray<Diagnostic> EvaluateReadRoot(
            WorkspacePath workspaceRoot,
            WorkspacePath readRoot) =>
            inner.EvaluateReadRoot(
                workspaceRoot,
                readRoot);
    }

    private sealed class ControlledHairRendererProcessHost(
        IFaceGeomHairRegionsRunningProcess process) :
        IFaceGeomHairRegionsProcessHost
    {
        public IFaceGeomHairRegionsRunningProcess Start(
            ProcessStartInfo startInfo) =>
            process;
    }

    private sealed class CapturingHairRendererProcessHost(
        IFaceGeomHairRegionsRunningProcess process) :
        IFaceGeomHairRegionsProcessHost
    {
        public ProcessStartInfo? StartInfo
        {
            get;
            private set;
        }

        public IFaceGeomHairRegionsRunningProcess Start(
            ProcessStartInfo startInfo)
        {
            StartInfo = startInfo;
            return process;
        }
    }

    private sealed class CompletedHairRendererProcess :
        IFaceGeomHairRegionsRunningProcess
    {
        private readonly StreamReader standardOutput =
            new(new MemoryStream());
        private readonly StreamReader standardError =
            new(new MemoryStream());

        public int Id => 4141;

        public bool HasExited => true;

        public int ExitCode => 0;

        public StreamReader StandardOutput => standardOutput;

        public StreamReader StandardError => standardError;

        public void Kill(bool entireProcessTree) =>
            throw new InvalidOperationException(
                "A completed process must not be killed.");

        public Task WaitForExitAsync(
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Dispose()
        {
            standardOutput.Dispose();
            standardError.Dispose();
        }
    }

    private sealed class KillFailingHairRendererProcess :
        IFaceGeomHairRegionsRunningProcess
    {
        private readonly StreamReader standardOutput =
            new(new MemoryStream());
        private readonly StreamReader standardError =
            new(new MemoryStream());

        public int Id => 4242;

        public bool HasExited => false;

        public int ExitCode =>
            throw new InvalidOperationException(
                "The controlled process never exits.");

        public StreamReader StandardOutput => standardOutput;

        public StreamReader StandardError => standardError;

        public void Kill(bool entireProcessTree) =>
            throw new Win32Exception(
                5,
                "Controlled access denial.");

        public Task WaitForExitAsync(
            CancellationToken cancellationToken) =>
            Task.FromException(
                new OperationCanceledException(
                    cancellationToken));

        public void Dispose()
        {
            standardOutput.Dispose();
            standardError.Dispose();
        }
    }

    private sealed class ExecutionFailingHairRendererProcess :
        IFaceGeomHairRegionsRunningProcess
    {
        private readonly StreamReader standardOutput =
            new(new MemoryStream());
        private readonly StreamReader standardError =
            new(new MemoryStream());

        public bool KillAttempted
        {
            get;
            private set;
        }

        public int Id => 4343;

        public bool HasExited => false;

        public int ExitCode => 1;

        public StreamReader StandardOutput => standardOutput;

        public StreamReader StandardError => standardError;

        public void Kill(bool entireProcessTree)
        {
            KillAttempted = true;
            throw new Win32Exception(
                5,
                "Controlled execution-failure termination denial.");
        }

        public Task WaitForExitAsync(
            CancellationToken cancellationToken) =>
            Task.FromException(
                new IOException(
                    "Controlled renderer I/O failure."));

        public void Dispose()
        {
            standardOutput.Dispose();
            standardError.Dispose();
        }
    }

    private sealed class OutputAccessorFailingHairRendererProcess :
        IFaceGeomHairRegionsRunningProcess
    {
        public bool KillAttempted
        {
            get;
            private set;
        }

        public int Id => 4444;

        public bool HasExited => false;

        public int ExitCode => 1;

        public StreamReader StandardOutput =>
            throw new IOException(
                "Controlled output-access failure.");

        public StreamReader StandardError =>
            throw new InvalidOperationException(
                "Standard error must not be reached.");

        public void Kill(bool entireProcessTree)
        {
            KillAttempted = true;
            throw new Win32Exception(
                5,
                "Controlled output-failure termination denial.");
        }

        public Task WaitForExitAsync(
            CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public void Dispose()
        {
        }
    }

    private sealed class ProcessCancellingHairRendererRunner :
        IFaceGeomHairRegionsProcessRunner
    {
        public ValueTask<FaceGeomHairRegionsProcessResult>
            RunAsync(
                WorkspacePath executable,
                ImmutableArray<string> arguments,
                WorkspacePath? blenderProfile,
                WorkspacePath? pythonCachePrefix,
                TimeSpan timeout,
                CancellationToken cancellationToken) =>
            throw new FaceGeomHairRegionsProcessCanceledException(
                [4545],
                new FaceGeomHairRegionsProcessTerminationException(
                    4545,
                    new Win32Exception(
                        5,
                        "Controlled termination denial.")),
                new OperationCanceledException());
    }

    private sealed class ControlledHairRendererProcess(
        bool wrongDuplicateOrdinal = false,
        string? textureBindingMode = null,
        string? textureBindingSemantic = null,
        bool includeLoadedImagePath = false,
        bool includeNamespaceModule = false,
        bool namespaceHashDrift = false,
        string? statusPythonCachePrefix = null,
        string? statusModuleCachedPath = null,
        string? statusModuleName = null,
        string? statusModuleRelativeSource = null,
        bool statusModuleIsNamespace = false,
        bool normalizeStatusModuleFingerprintRelativeSource = false) :
        IFaceGeomHairRegionsProcessRunner
    {
        public int BlenderInvocationCount
        {
            get;
            private set;
        }

        public string? ObservedCandidatePath
        {
            get;
            private set;
        }

        public string? ObservedRegionStructuralId
        {
            get;
            private set;
        }

        public int ObservedDuplicateNameOrdinal
        {
            get;
            private set;
        }

        public ValueTask<FaceGeomHairRegionsProcessResult> RunAsync(
            WorkspacePath executable,
            ImmutableArray<string> arguments,
            WorkspacePath? blenderProfile,
            WorkspacePath? pythonCachePrefix,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.Equals(
                    Path.GetFileName(executable.Value),
                    "texconv.exe",
                    StringComparison.OrdinalIgnoreCase))
            {
                int outputIndex = arguments.IndexOf("-o");
                Assert(
                    outputIndex >= 0 &&
                    outputIndex + 1 < arguments.Length,
                    "Controlled Texconv invocation lacked an output directory.");
                string sourcePath = arguments[^1];
                string pngPath = Path.Combine(
                    arguments[outputIndex + 1],
                    Path.GetFileNameWithoutExtension(sourcePath) +
                    ".png");
                WritePng(
                    pngPath,
                    4,
                    4,
                    SKColors.Goldenrod);
                return ValueTask.FromResult(
                    new FaceGeomHairRegionsProcessResult(
                        0,
                        "",
                        "",
                        []));
            }
            BlenderInvocationCount++;
            int requestIndex = arguments.IndexOf("--request");
            int statusIndex = arguments.IndexOf("--status");
            Assert(
                requestIndex >= 0 && statusIndex >= 0,
                "Controlled Blender invocation lacked request/status arguments.");
            string requestPath = arguments[requestIndex + 1];
            string statusPath = arguments[statusIndex + 1];
            using JsonDocument request =
                JsonDocument.Parse(File.ReadAllBytes(requestPath));
            JsonElement root = request.RootElement;
            ObservedCandidatePath =
                root.GetProperty("candidatePath").GetString();
            JsonElement region =
                root.GetProperty("regions")[0];
            ObservedRegionStructuralId =
                region.GetProperty("structuralId").GetString();
            ObservedDuplicateNameOrdinal =
                region.GetProperty("duplicateNameOrdinal").GetInt32();
            string output =
                root.GetProperty("outputRoot").GetString()!;
            string combined = Path.Combine(output, "combined-face.png");
            string thumbnail = Path.Combine(
                output, "region-shape-000016.png");
            string mask = Path.Combine(
                output, "region-shape-000016.mask.png");
            string contact = Path.Combine(output, "contact-sheet.png");
            WritePng(combined, 900, 900, SKColors.Bisque);
            WritePng(thumbnail, 900, 900, SKColors.Goldenrod);
            WritePng(mask, 900, 900, SKColors.White);
            WritePng(contact, 900, 600, SKColors.DarkSlateGray);
            string candidateHash =
                root.GetProperty("candidateSha256").GetString()!;
            string textureHash =
                root.GetProperty(
                    "textureFingerprintSha256").GetString()!;
            WorkspacePath exactProfile =
                blenderProfile ??
                throw new InvalidOperationException(
                    "Controlled Blender profile was absent.");
            WorkspacePath exactCache =
                pythonCachePrefix ??
                throw new InvalidOperationException(
                    "Controlled Python cache was absent.");
            Assert(
                root.GetProperty(
                        "pyniflyProfileRoot")
                    .GetString() ==
                    exactProfile.Value &&
                root.GetProperty(
                        "pythonCachePrefix")
                    .GetString() ==
                    exactCache.Value,
                "Controlled Blender invocation did not use the private source-only profile/cache roots.");
            string moduleSource = Path.Combine(
                exactProfile.Value,
                "scripts",
                "addons",
                "io_scene_nifly",
                "__init__.py");
            string moduleSha =
                Sha(File.ReadAllBytes(
                    moduleSource)).Value;
            string namespaceRelative = "util/";
            string namespaceDirectory = Path.Combine(
                exactProfile.Value,
                "scripts",
                "addons",
                "io_scene_nifly",
                "util");
            string namespaceSha =
                FingerprintSourceDirectory(
                    namespaceDirectory);
            if (namespaceHashDrift)
                namespaceSha = Sha([0x51]).Value;
            var moduleRows = new List<object>
            {
                new
                {
                    moduleName =
                        statusModuleName ??
                        "io_scene_nifly",
                    moduleKind =
                        statusModuleIsNamespace
                            ? "NamespaceDirectory"
                            : "SourceFile",
                    relativeSource =
                        statusModuleRelativeSource ??
                        "__init__.py",
                    sourceSha256 =
                        statusModuleIsNamespace
                            ? namespaceSha
                            : moduleSha,
                    cachedPath =
                        statusModuleIsNamespace
                            ? null
                            : statusModuleCachedPath ??
                              Path.Combine(
                                  exactCache.Value,
                                  "io_scene_nifly",
                                  "__init__.cpython-311.pyc")
                }
            };
            if (includeNamespaceModule)
            {
                moduleRows.Add(new
                {
                    moduleName = "io_scene_nifly.util",
                    moduleKind = "NamespaceDirectory",
                    relativeSource = namespaceRelative,
                    sourceSha256 = namespaceSha,
                    cachedPath = (string?)null
                });
            }
            string moduleFingerprint =
                FingerprintModules(
                    moduleRows,
                    normalizeStatusModuleFingerprintRelativeSource);
            JsonElement[] textureRows =
                root.GetProperty("textures")
                    .EnumerateArray()
                    .ToArray();
            object[] loadedTextures =
                textureRows.Select(texture =>
                {
                    string previewPath =
                        texture.GetProperty(
                            "previewPath").GetString()!;
                    return (object)new
                    {
                        assetPath = texture.GetProperty(
                            "assetPath").GetString(),
                        providerKind = texture.GetProperty(
                            "providerKind").GetString(),
                        provider = texture.GetProperty(
                            "provider").GetString(),
                        sourceSha256 = texture.GetProperty(
                            "sha256").GetString(),
                        sourceBytes = texture.GetProperty(
                            "bytes").GetInt64(),
                        previewSha256 = texture.GetProperty(
                            "previewSha256").GetString(),
                        previewBytes = texture.GetProperty(
                            "previewBytes").GetInt64(),
                        decodeKind = texture.GetProperty(
                            "decodeKind").GetString(),
                        bindingMode =
                            textureBindingMode ??
                            "SampledImage",
                        bindingSemantic =
                            textureBindingSemantic ??
                            "BSShaderTextureSet_Diffuse",
                        objectName = "Duplicate.001",
                        materialName = "Hair",
                        nodeName = "Diffuse_Texture",
                        loadedImagePath =
                            includeLoadedImagePath
                                ? previewPath
                                : null
                    };
                }).ToArray();
            string loadedTextureFingerprint =
                FingerprintLoadedTextures(loadedTextures);
            object[] artifacts =
            [
                Artifact("CombinedFace", null, combined, 900, 900, null),
                Artifact(
                    "RegionThumbnail",
                    ObservedRegionStructuralId,
                    thumbnail,
                    900,
                    900,
                    810000),
                Artifact(
                    "RegionMask",
                    ObservedRegionStructuralId,
                    mask,
                    900,
                    900,
                    810000),
                Artifact("ContactSheet", null, contact, 900, 600, null)
            ];
            object status = new
            {
                schema =
                    "npcmanager-facegeom-hair-regions-render-status/1",
                rendered = true,
                blenderVersion = "controlled",
                renderEngine = "BLENDER_EEVEE_NEXT",
                candidateSha256 = candidateHash,
                textureFingerprintSha256 = textureHash,
                loadedTextureObservationFingerprintSha256 =
                    loadedTextureFingerprint,
                loadedTextures,
                pythonCachePrefix =
                    statusPythonCachePrefix ??
                    exactCache.Value,
                pythonDontWriteBytecode = true,
                pyniflyModuleCount = moduleRows.Count,
                pyniflyModuleSourceFingerprintSha256 =
                    moduleFingerprint,
                pyniflyModules = moduleRows,
                faceGeomImportCount = 1,
                nifImportInvocationCount = 1,
                faceCameraUsedAuthoritativeGeometry = true,
                tintedMaterialCount = 1,
                regionMappings = new[]
                {
                    new
                    {
                        structuralId = ObservedRegionStructuralId,
                        shapeBlockId = 16,
                        shapeBlockType = "BSTriShape",
                        name = "Duplicate",
                        duplicateNameOrdinal =
                            wrongDuplicateOrdinal ? 1 : 0,
                        shaderBlockId = 20,
                        shaderBlockType =
                            "BSLightingShaderProperty",
                        objectName = "Duplicate.001"
                    }
                },
                artifacts
            };
            File.WriteAllBytes(
                statusPath,
                JsonSerializer.SerializeToUtf8Bytes(status));
            return ValueTask.FromResult(
                new FaceGeomHairRegionsProcessResult(
                    0,
                    "",
                    "",
                    []));
        }

        private static string FingerprintLoadedTextures(
            object[] observations)
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
            foreach (object observation in observations)
            {
                JsonElement item = JsonSerializer.SerializeToElement(
                    observation);
                string canonical =
                    $"{item.GetProperty("assetPath").GetString()}\0" +
                    $"{item.GetProperty("providerKind").GetString()}\0" +
                    $"{item.GetProperty("provider").GetString()}\0" +
                    $"{item.GetProperty("sourceSha256").GetString()}\0" +
                    $"{item.GetProperty("sourceBytes").GetInt64()}\0" +
                    $"{item.GetProperty("previewSha256").GetString()}\0" +
                    $"{item.GetProperty("previewBytes").GetInt64()}\0" +
                    $"{item.GetProperty("decodeKind").GetString()}\0" +
                    $"{item.GetProperty("bindingMode").GetString()}\0" +
                    $"{item.GetProperty("bindingSemantic").GetString()}\0" +
                    $"{item.GetProperty("objectName").GetString()}\0" +
                    $"{item.GetProperty("materialName").GetString()}\0" +
                    $"{item.GetProperty("nodeName").GetString()}\0" +
                    $"{(item.GetProperty("loadedImagePath").ValueKind ==
                        JsonValueKind.Null
                            ? ""
                            : item.GetProperty(
                                "loadedImagePath").GetString())}\n";
                hash.AppendData(
                    System.Text.Encoding.UTF8.GetBytes(
                        canonical));
            }
            return Convert.ToHexString(
                hash.GetHashAndReset());
        }

        private static string FingerprintModules(
            IEnumerable<object> modules,
            bool normalizeRelativeSource = false)
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
            foreach (object module in modules)
            {
                JsonElement item =
                    JsonSerializer.SerializeToElement(
                        module);
                string canonical =
                    $"{item.GetProperty("moduleName").GetString()}\0" +
                    $"{item.GetProperty("moduleKind").GetString()}\0" +
                    $"{(normalizeRelativeSource
                        ? item.GetProperty("relativeSource")
                            .GetString()!
                            .Replace('\\', '/')
                        : item.GetProperty("relativeSource")
                            .GetString())}\0" +
                    $"{item.GetProperty("sourceSha256").GetString()}\n";
                hash.AppendData(
                    System.Text.Encoding.UTF8.GetBytes(
                        canonical));
            }
            return Convert.ToHexString(
                hash.GetHashAndReset());
        }

        private static string FingerprintSourceDirectory(
            string directory)
        {
            using IncrementalHash hash =
                IncrementalHash.CreateHash(
                    HashAlgorithmName.SHA256);
            foreach (string file in Directory.EnumerateFiles(
                         directory,
                         "*",
                         SearchOption.AllDirectories)
                         .Where(path =>
                             !path.Contains(
                                 $"{Path.DirectorySeparatorChar}__pycache__{Path.DirectorySeparatorChar}",
                                 StringComparison.Ordinal) &&
                             !Path.GetExtension(path).Equals(
                                 ".pyc",
                                 StringComparison.OrdinalIgnoreCase))
                         .OrderBy(
                             path => Path.GetRelativePath(
                                     directory,
                                     path)
                                 .Replace('\\', '/'),
                             StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(
                        directory,
                        file)
                    .Replace('\\', '/');
                byte[] bytes = File.ReadAllBytes(file);
                string row =
                    $"{relative}\0{bytes.LongLength}\0" +
                    $"{Sha(bytes).Value}\n";
                hash.AppendData(
                    System.Text.Encoding.UTF8.GetBytes(
                        row));
            }
            return Convert.ToHexString(
                hash.GetHashAndReset());
        }

        private static object Artifact(
            string kind,
            string? structuralId,
            string path,
            int width,
            int height,
            long? nonEmptyPixelCount) =>
            new
            {
                kind,
                structuralId,
                path,
                sha256 = Sha(
                    File.ReadAllBytes(path)).Value,
                width,
                height,
                nonEmptyPixelCount
            };

        private static void WritePng(
            string path,
            int width,
            int height,
            SKColor color)
        {
            using SKSurface surface = SKSurface.Create(
                new SKImageInfo(
                    width,
                    height,
                    SKColorType.Rgba8888,
                    SKAlphaType.Premul))
                ?? throw new InvalidOperationException(
                    "Controlled PNG surface allocation failed.");
            surface.Canvas.Clear(color);
            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(
                SKEncodedImageFormat.Png,
                100)
                ?? throw new InvalidOperationException(
                    "Controlled PNG encoding failed.");
            using FileStream stream = new(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            data.SaveTo(stream);
        }
    }
}
