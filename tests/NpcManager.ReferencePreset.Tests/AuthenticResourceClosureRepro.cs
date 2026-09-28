using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Presets;
using NpcManager.Rendering;

namespace NpcManager.ReferencePreset.Tests;

internal static class AuthenticResourceClosureRepro
{
    private static readonly WorkspacePath LabRoot =
        new(@"K:\ExampleWorkspace");

    public static async Task RunAsync(
        string intakeDocumentPath,
        string reviewedDesignDocumentPath,
        string? retainedSnapshotPath = null)
    {
        var policy = new KOnlyWorkspacePolicy(
            LabRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var sessions = new ReferencePresetSessionService(
            policy,
            LabRoot);
        ReferencePresetSessionReadResult intakeRead =
            await ReadAsync(
                sessions,
                intakeDocumentPath,
                ReferencePresetSessionDocumentKind.Intake);
        ReferencePresetSessionReadResult designRead =
            await ReadAsync(
                sessions,
                reviewedDesignDocumentPath,
                ReferencePresetSessionDocumentKind.ReviewedDesign);
        ReferencePresetIntake intake =
            intakeRead.Document?.Intake ??
            throw new InvalidDataException(
                "The authentic intake session lacks its typed intake.");
        ReviewedReferencePresetDesign design =
            designRead.Document?.ReviewedDesign ??
            throw new InvalidDataException(
                "The authentic reviewed-design session lacks its typed design.");
        Sha256Hash designHash =
            designRead.ContentSha256 ??
            throw new InvalidDataException(
                "The authentic reviewed-design session lacks its hash.");

        var assetIndexer = new BethesdaAssetIndexer();
        var contentResolver = new SkyrimAssetContentResolver(
            policy,
            LabRoot);
        var assetPlanner = new SkyrimAssetAuthorityPlanner(
            assetIndexer,
            policy,
            LabRoot);
        var authorityLoader =
            new SkyrimFaceRecordPluginAuthorityLoader(
                policy,
                LabRoot);
        var compatibility =
            new BethesdaRaceMenuPresetCompatibilityEvaluator(
                authorityLoader);
        var sliderParser =
            new RaceMenuSliderCatalogParserCore();
        var catalogLoader =
            new SkyrimRaceMenuCatalogAuthorityLoader(
                assetIndexer,
                assetPlanner,
                contentResolver);
        var resourceService =
            new ReferencePresetResourceSnapshotService(
                new PresetService(policy, LabRoot),
                compatibility,
                new BethesdaReferencePresetCatalogReader(
                    authorityLoader,
                    new BethesdaSkyrimRaceTintAuthorityReader(
                        authorityLoader)),
                assetPlanner,
                new ReferencePresetAssetMaterializer(
                    contentResolver),
                catalogLoader,
                sliderParser,
                new SseSelectedHeadpartNifGeometryReader(),
                new NpcManager.FaceGen.SseTriHeadReader(),
                new InProcessDdsTextureDecoder(
                    LabRoot,
                    InProcessDdsTextureDecodeProfile
                        .ReferencePreview));

        ReferencePresetResourceSnapshotResult result =
            await resourceService.CreateAsync(
                new ReferencePresetResourceSnapshotRequest(
                    intake,
                    design,
                    designHash),
                CancellationToken.None);
        if (!result.Accepted || result.Snapshot is null)
        {
            throw new InvalidDataException(
                "The authentic resource closure was refused: " +
                string.Join(
                    " | ",
                    result.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));
        }

        Console.WriteLine(
            $"AUTHENTIC RESOURCE PASS shapes={result.Snapshot.RenderShapes.Length} textures={result.Snapshot.RenderTextures.Length} assets={result.Snapshot.AssetAuthorities.Length} fingerprint={result.Snapshot.ResourceFingerprint}");

        long decodedTextureBytes = result.Snapshot.RenderTextures.Sum(
            item => (long)item.CanonicalRgba.Length);
        Console.WriteLine(
            $"AUTHENTIC RESOURCE SERIALIZE decodedTextureBytes={decodedTextureBytes}");
        string outputPath =
            retainedSnapshotPath is null
                ? Path.Combine(
                    LabRoot.Value,
                    "projects",
                    "NpcManagerReimplementation",
                    "03-builds",
                    "tests",
                    $"authentic-resource-snapshot-{Guid.NewGuid():N}.json")
                : Path.GetFullPath(
                    retainedSnapshotPath);
        string? outputParent =
            Path.GetDirectoryName(outputPath);
        if (outputParent is null)
            throw new InvalidDataException(
                "The authentic snapshot output has no parent.");
        Directory.CreateDirectory(outputParent);
        var snapshotOutput = new WorkspacePath(outputPath);
        ReferencePresetSessionWriteResult writeResult;
        try
        {
            writeResult = await sessions.WriteAsync(
                new ReferencePresetSessionWriteRequest(
                    snapshotOutput,
                    new ReferencePresetSessionDocument(
                        ReferencePresetSessionDocumentKind
                            .ResourceSnapshot,
                        ResourceSnapshot: result.Snapshot)),
                CancellationToken.None);
        }
        finally
        {
            if (retainedSnapshotPath is null &&
                File.Exists(snapshotOutput.Value))
                File.Delete(snapshotOutput.Value);
        }
        Console.WriteLine(
            $"AUTHENTIC RESOURCE SERIALIZE written={writeResult.Written} diagnostics={string.Join(" | ", writeResult.Diagnostics.Select(item => $"{item.Code}: {item.Message}"))}");
        if (!writeResult.Written ||
            writeResult.ContentSha256 is null)
        {
            throw new InvalidDataException(
                "The authentic resource snapshot did not complete canonical write/readback.");
        }
    }

    public static async Task RunComparisonAsync(
        string intakeDocumentPath,
        string reviewedDesignDocumentPath,
        string resourceSnapshotDocumentPath,
        string comparisonOutputPath)
    {
        var policy = new KOnlyWorkspacePolicy(
            LabRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var sessions = new ReferencePresetSessionService(
            policy,
            LabRoot);
        ReferencePresetSessionReadResult intakeRead =
            await ReadAsync(
                sessions,
                intakeDocumentPath,
                ReferencePresetSessionDocumentKind.Intake);
        ReportMemory("intake-read");
        ReferencePresetSessionReadResult designRead =
            await ReadAsync(
                sessions,
                reviewedDesignDocumentPath,
                ReferencePresetSessionDocumentKind.ReviewedDesign);
        ReportMemory("reviewed-design-read");
        ReferencePresetSessionReadResult resourceRead =
            await ReadAsync(
                sessions,
                resourceSnapshotDocumentPath,
                ReferencePresetSessionDocumentKind.ResourceSnapshot);
        ReportMemory("resource-snapshot-read");

        ReferencePresetIntake intake =
            intakeRead.Document?.Intake ??
            throw new InvalidDataException(
                "The authentic intake session lacks its typed intake.");
        ReviewedReferencePresetDesign design =
            designRead.Document?.ReviewedDesign ??
            throw new InvalidDataException(
                "The authentic reviewed-design session lacks its typed design.");
        ReferencePresetResourceSnapshot snapshot =
            resourceRead.Document?.ResourceSnapshot ??
            throw new InvalidDataException(
                "The authentic resource session lacks its typed snapshot.");
        Sha256Hash designHash =
            designRead.ContentSha256 ??
            throw new InvalidDataException(
                "The authentic reviewed-design session lacks its hash.");

        var renderInputBuilder =
            new ReferencePresetRenderInputBuilder();
        ReferencePresetRenderInputResult render =
            await renderInputBuilder.BuildAsync(
                new ReferencePresetRenderInputRequest(
                    snapshot,
                    design),
                CancellationToken.None);
        RequireAccepted(
            render.Input is not null,
            "render input",
            render.Diagnostics);
        ReportMemory("render-input");

        var planBuilder = new SseFaceMorphPlanBuilder();
        var evaluator = new SseFaceMorphEvaluator();
        RaceMenuTriResponseMatrixBuildResult matrix =
            await new RaceMenuTriResponseMatrixBuilder(
                    planBuilder,
                    evaluator)
                .BuildAsync(
                    new RaceMenuTriResponseMatrixBuildRequest(
                        design,
                        snapshot,
                        render.Input!)
                    {
                        MorphBases = snapshot.MorphBases,
                        ReviewedDesignSha256 = designHash
                    },
                    CancellationToken.None);
        RequireAccepted(
            matrix.Accepted,
            "response matrix",
            matrix.Diagnostics);
        ReportMemory("response-matrix");

        ReferenceRaceMenuPresetSolverResult solved =
            await new ReferenceRaceMenuPresetSolver()
                .SolveAsync(
                    new ReferenceRaceMenuPresetSolverRequest(
                        design,
                        snapshot,
                        matrix)
                    {
                        RenderInput = render.Input,
                        ReviewedDesignSha256 = designHash,
                        ProtectedNeckRingVertexIndices =
                            snapshot
                                .ProtectedNeckRingVertexIndices
                    },
                    CancellationToken.None);
        RequireAccepted(
            solved.Accepted,
            "solver",
            solved.Diagnostics);
        ReportMemory("solver");

        string output = Path.GetFullPath(
            comparisonOutputPath);
        string? parent = Path.GetDirectoryName(output);
        if (parent is null)
            throw new InvalidDataException(
                "The comparison output has no parent.");
        Directory.CreateDirectory(parent);
        if (Directory.Exists(output) ||
            File.Exists(output))
        {
            throw new InvalidDataException(
                "The comparison output must be absent.");
        }
        ReferencePresetComparisonResult comparison =
            await new ReferencePresetComparisonService(
                    policy,
                    LabRoot,
                    planBuilder,
                    evaluator,
                    new ReferencePresetCpuRenderer())
                .RenderAsync(
                    new ReferencePresetComparisonRequest(
                        render.Input!,
                        design,
                        solved,
                        new WorkspacePath(output))
                    {
                        Snapshot = snapshot,
                        Intake = intake
                    },
                    CancellationToken.None);
        RequireAccepted(
            !comparison.Diagnostics.Any(item =>
                item.Severity ==
                DiagnosticSeverity.Error),
            "comparison render",
            comparison.Diagnostics);
        ReportMemory("comparison-render");
        Console.WriteLine(
            $"AUTHENTIC COMPARISON PASS matrix={matrix.MatrixSha256} solver={solved.ResultSha256} artifacts={comparison.Artifacts.Length}");
    }

    public static async Task RunMatrixBenchmarkAsync(
        string intakeDocumentPath,
        string reviewedDesignDocumentPath,
        string resourceSnapshotDocumentPath,
        int channelCount)
    {
        ArgumentOutOfRangeException
            .ThrowIfNegativeOrZero(channelCount);
        var policy = new KOnlyWorkspacePolicy(
            LabRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var sessions = new ReferencePresetSessionService(
            policy,
            LabRoot);
        var stopwatch =
            System.Diagnostics.Stopwatch.StartNew();
        _ = await ReadAsync(
            sessions,
            intakeDocumentPath,
            ReferencePresetSessionDocumentKind.Intake);
        ReferencePresetSessionReadResult designRead =
            await ReadAsync(
                sessions,
                reviewedDesignDocumentPath,
                ReferencePresetSessionDocumentKind.ReviewedDesign);
        ReferencePresetSessionReadResult resourceRead =
            await ReadAsync(
                sessions,
                resourceSnapshotDocumentPath,
                ReferencePresetSessionDocumentKind.ResourceSnapshot);
        Console.WriteLine(
            $"AUTHENTIC MATRIX READ elapsedMs={stopwatch.ElapsedMilliseconds}");
        ReviewedReferencePresetDesign design =
            designRead.Document?.ReviewedDesign ??
            throw new InvalidDataException(
                "The authentic reviewed-design session lacks its typed design.");
        ReferencePresetResourceSnapshot snapshot =
            resourceRead.Document?.ResourceSnapshot ??
            throw new InvalidDataException(
                "The authentic resource session lacks its typed snapshot.");
        Sha256Hash designHash =
            designRead.ContentSha256 ??
            throw new InvalidDataException(
                "The authentic reviewed-design session lacks its hash.");
        int selectedCount = Math.Min(
            channelCount,
            snapshot.MorphChannels.Length);
        snapshot = snapshot with
        {
            MorphChannels = snapshot.MorphChannels
                .Take(selectedCount)
                .ToImmutableArray()
        };
        ReferencePresetRenderInputResult render =
            await new ReferencePresetRenderInputBuilder()
                .BuildAsync(
                    new ReferencePresetRenderInputRequest(
                        snapshot,
                        design),
                    CancellationToken.None);
        RequireAccepted(
            render.Input is not null,
            "render input",
            render.Diagnostics);
        long matrixStarted = stopwatch.ElapsedMilliseconds;
        var planBuilder = new SseFaceMorphPlanBuilder();
        RaceMenuTriResponseMatrixBuildResult matrix =
            await new RaceMenuTriResponseMatrixBuilder(
                    planBuilder,
                    new SseFaceMorphEvaluator())
                .BuildAsync(
                    new RaceMenuTriResponseMatrixBuildRequest(
                        design,
                        snapshot,
                        render.Input!)
                    {
                        MorphBases = snapshot.MorphBases,
                        ReviewedDesignSha256 = designHash
                    },
                    CancellationToken.None);
        RequireAccepted(
            matrix.Accepted,
            "response matrix benchmark",
            matrix.Diagnostics);
        Console.WriteLine(
            $"AUTHENTIC MATRIX PASS channels={selectedCount} responses={matrix.Responses.Length} matrixMs={stopwatch.ElapsedMilliseconds - matrixStarted} totalMs={stopwatch.ElapsedMilliseconds}");
        ReportMemory("matrix-benchmark");
    }

    private static void RequireAccepted(
        bool accepted,
        string stage,
        IEnumerable<Diagnostic> diagnostics)
    {
        if (accepted)
            return;
        throw new InvalidDataException(
            $"The authentic {stage} was refused: " +
            string.Join(
                " | ",
                diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
    }

    private static void ReportMemory(string stage)
    {
        using var process =
            System.Diagnostics.Process.GetCurrentProcess();
        Console.WriteLine(
            $"AUTHENTIC COMPARISON STAGE {stage} privateBytes={process.PrivateMemorySize64} workingSet={process.WorkingSet64} managedBytes={GC.GetTotalMemory(false)}");
    }

    private static async Task<ReferencePresetSessionReadResult>
        ReadAsync(
            ReferencePresetSessionService sessions,
            string path,
            ReferencePresetSessionDocumentKind kind)
    {
        var workspacePath = new WorkspacePath(
            Path.GetFullPath(path));
        Sha256Hash hash;
        await using (FileStream stream = new(
                         workspacePath.Value,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         65_536,
                         FileOptions.Asynchronous |
                         FileOptions.SequentialScan))
        {
            hash = new Sha256Hash(
                Convert.ToHexString(
                    await SHA256.HashDataAsync(stream)));
        }

        ReferencePresetSessionReadResult result =
            await sessions.ReadAsync(
                new ReferencePresetSessionReadRequest(
                    workspacePath,
                    hash,
                    kind),
                CancellationToken.None);
        if (result.Document is null)
        {
            throw new InvalidDataException(
                $"The {kind} session could not be reopened: " +
                string.Join(
                    " | ",
                    result.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")));
        }

        return result;
    }
}
