namespace NpcManager.ReferencePreset.Tests;

internal static class Program
{
    private const string Selector_ReferenceCameraNeighbors = "--test-reference-camera-neighbors";
    private const string Selector_PresetArrayBudget = "--test-preset-array-budget";
    private const string Selector_ReferenceAuthoringSchemas = "--test-reference-authoring-schemas";
    private const string Selector_ReferenceIntakeTemplate = "--test-reference-intake-template";
    private const string Selector_ReferenceCanonicalFlow = "--test-reference-canonical-flow";
    private const string Selector_ReferenceSessionCanonical = "--test-reference-session-canonical";
    private const string Selector_NativeLoaderProbe = "--native-loader-probe";
    private const string Selector_NativeLoaderOnly = "--native-loader-only";
    private const string Selector_AuthenticResourceRepro = "--authentic-resource-repro";
    private const string Selector_AuthenticResourceOutput = "--authentic-resource-output";
    private const string Selector_AuthenticComparisonRepro = "--authentic-comparison-repro";
    private const string Selector_AuthenticMatrixBenchmark = "--authentic-matrix-benchmark";
    private const string Selector_SessionOnly = "--session-only";
    private const string Selector_ResourceOnly = "--resource-only";
    private const string Selector_TextureProfileOnly = "--texture-profile-only";
    private const string Selector_PlacementOnly = "--placement-only";
    private const string Selector_ShapeRoutingOnly = "--shape-routing-only";
    private const string Selector_DeterminismEvidence = "--determinism-evidence";
    private const string Selector_NativeInferenceEvidence = "--native-inference-evidence";

    public static async Task<int> Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        if (args is [Selector_ReferenceCameraNeighbors])
        {
            await ReferenceMeshAnchorBindingTests.TestDeterministicReviewedRaycast();
            await ReferencePresetRenderingTests.TestCpuRasterizerControls();
            await ReferencePresetSolverTests.TestRealFaceMathResponseMatrix();
            await ReferencePresetSolverTests.TestFixedSolverAndBoundedSculpt();
            Console.WriteLine("PASS reference camera renderer/binder/solver neighbors");
            return 0;
        }
        if (args is [Selector_PresetArrayBudget]) { await PresetArrayBudgetTests.RunAsync(); Console.WriteLine("PASS preset path-aware array budget"); return 0; }
        if (args is [Selector_ReferenceAuthoringSchemas])
        {
            await ReferenceAuthoringSchemaTests.RunAsync();
            Console.WriteLine("PASS reference authoring schemas");
            return 0;
        }
        if (args is [Selector_ReferenceIntakeTemplate])
        {
            await ReferencePresetTransactionTests.TestIntakeTemplateAsync();
            Console.WriteLine("PASS reference intake template");
            return 0;
        }
        if (args is [Selector_ReferenceCanonicalFlow])
        {
            await ReferencePresetTransactionTests.TestCanonicalReferenceFlowAsync();
            Console.WriteLine("PASS reference canonical flow");
            return 0;
        }
        if (args is [Selector_ReferenceSessionCanonical])
        {
            await ReferencePresetSessionTests.TestCanonicalSessionWriteRead();
            Console.WriteLine("PASS reference session canonical read/write");
            return 0;
        }
        if (args.Length >= 3 &&
            args[0] == Selector_NativeLoaderProbe)
        {
            return NativeRuntimeLoaderTests.RunProbe(args);
        }

        if (args.Length == 1 &&
            args[0] == Selector_NativeLoaderOnly)
        {
            await NativeRuntimeLoaderTests
                .TestAdmittedClosureAndRefusals();
            Console.WriteLine("RESULT PASS native-loader");
            return 0;
        }

        if (args.Length == 3 &&
            args[0] == Selector_AuthenticResourceRepro)
        {
            await AuthenticResourceClosureRepro.RunAsync(
                args[1],
                args[2]);
            Console.WriteLine(
                "RESULT PASS authentic-resource-repro");
            return 0;
        }

        if (args.Length == 4 &&
            args[0] == Selector_AuthenticResourceOutput)
        {
            await AuthenticResourceClosureRepro.RunAsync(
                args[1],
                args[2],
                args[3]);
            Console.WriteLine(
                "RESULT PASS authentic-resource-output");
            return 0;
        }

        if (args.Length == 5 &&
            args[0] == Selector_AuthenticComparisonRepro)
        {
            await AuthenticResourceClosureRepro
                .RunComparisonAsync(
                    args[1],
                    args[2],
                    args[3],
                    args[4]);
            Console.WriteLine(
                "RESULT PASS authentic-comparison-repro");
            return 0;
        }

        if (args.Length == 5 &&
            args[0] == Selector_AuthenticMatrixBenchmark)
        {
            await AuthenticResourceClosureRepro
                .RunMatrixBenchmarkAsync(
                    args[1],
                    args[2],
                    args[3],
                    int.Parse(
                        args[4],
                        System.Globalization.CultureInfo
                            .InvariantCulture));
            Console.WriteLine(
                "RESULT PASS authentic-matrix-benchmark");
            return 0;
        }

        if (args.Length == 1 &&
            args[0] == Selector_SessionOnly)
        {
            await ReferencePresetSessionTests
                .TestCanonicalSessionWriteRead();
            Console.WriteLine("RESULT PASS session");
            return 0;
        }

        if (args.Length == 1 &&
            args[0] == Selector_ResourceOnly)
        {
            await ReferenceResourceSnapshotTests
                .TestCompleteResourceSnapshot();
            Console.WriteLine("RESULT PASS resource");
            return 0;
        }

        if (args.Length == 1 &&
            args[0] == Selector_TextureProfileOnly)
        {
            await ReferencePresetRenderingTests
                .TestAuthenticFourKTextureProfile();
            Console.WriteLine("RESULT PASS texture-profile");
            return 0;
        }

        if (args.Length == 1 &&
            args[0] == Selector_PlacementOnly)
        {
            await ReferencePresetRenderingTests
                .TestAuthenticSkinnedHeadpartPlacement();
            Console.WriteLine("RESULT PASS placement");
            return 0;
        }

        if (args.Length == 1 &&
            args[0] == Selector_ShapeRoutingOnly)
        {
            await ReferencePresetRenderingTests
                .TestShapeScopedSolvedMorphRouting();
            Console.WriteLine(
                "RESULT PASS shape-routing-only");
            return 0;
        }

        if (args.Length == 1 &&
            args[0] == Selector_DeterminismEvidence)
        {
            await ReferencePresetSolverTests
                .TestAuthenticHighPolyHeadResponseMatrix();
            await ReferencePresetSolverTests
                .TestFixedSolverAndBoundedSculpt();
            await ReferencePresetRenderingTests
                .TestAuthenticRenderAndComparison();
            await ReferencePresetWriterTests
                .TestTransactionalCanonicalWriter();
            Console.WriteLine(
                $"DETERMINISM matrix={ReferencePresetSolverTests.AuthenticMatrixSha256} solver={ReferencePresetSolverTests.SolverResultSha256} sculpt={ReferencePresetSolverTests.SculptResultSha256} render={ReferencePresetRenderingTests.AuthenticRenderSha256} comparisons={ReferencePresetRenderingTests.ComparisonSetSha256} preset={ReferencePresetWriterTests.CanonicalPresetSha256}");
            return 0;
        }

        if (args.Length == 2 &&
            args[0] == Selector_NativeInferenceEvidence)
        {
            await MediaPipeInferenceTests
                .WriteElviraEvidenceAsync(args[1]);
            Console.WriteLine(
                "RESULT PASS native-inference-evidence");
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Reference runtime manifest binds every native/model/decoder byte",
                DependencyAdmissionTests.TestReferenceRuntimeManifest),
            ("Native runtime loader binds and releases the admitted closure",
                NativeRuntimeLoaderTests.TestAdmittedClosureAndRefusals),
            ("MediaPipe runtime exposes the exact admitted C surface",
                DependencyAdmissionTests.TestMediaPipeExports),
            ("Skia decodes PNG JPEG and WebP deterministically",
                ReferenceImageDecoderTests.TestSkiaFormatAdmission),
            ("Face inference refuses zero multiple and low-score faces",
                ReferenceImageDecoderTests.TestFaceCountAndConfidencePolicy),
            ("MediaPipe C layouts and runtime authority are exact",
                MediaPipeNativeApiTests.TestInteropLayoutAndRuntimeAuthority),
            ("MediaPipe refuses the authentic multi-face Sofia Fergar contact sheet",
                MediaPipeNativeApiTests.TestAuthenticSofiaFergarContactSheetRefusal),
            ("MediaPipe inference is deterministic on three real views",
                MediaPipeInferenceTests.TestElviraNativeDeterminism),
            ("MediaPipe evidence refuses an existing output before inference",
                MediaPipeInferenceTests.TestEvidenceNoOverwrite),
            ("Reference descriptions preserve traits conflicts and unknowns",
                ReferenceDescriptionTests.TestVocabularyAndUnknowns),
            ("Reference description limits use Unicode scalar values",
                ReferenceDescriptionTests.TestUnicodeLimits),
            ("MediaPipe landmarks project to the exact 31 semantic anchors",
                ReferenceLandmarkProjectionTests.TestExactAnchorMap),
            ("Anchor visibility and confirmation follow view confidence",
                ReferenceLandmarkProjectionTests.TestVisibilityAndConfidence),
            ("Authentic copied Skyrim catalog binds reviewed providers",
                ReferenceResourceSnapshotTests.TestAuthenticCatalogSelection),
            ("Complete resource snapshot closes real NIF TRI and baseline inputs",
                ReferenceResourceSnapshotTests.TestCompleteResourceSnapshot),
            ("Real selected-head NIF builds exact deterministic render input",
                ReferenceResourceSnapshotTests.TestRealNifRenderInput),
            ("Reference intake enforces image description and path limits",
                ReferencePresetRulesTests.TestReferenceIntakeRules),
            ("Reference review cannot skip confidence or unknown decisions",
                ReferencePresetRulesTests.TestReferenceReviewRules),
            ("Reference mesh bindings are topology and camera bound",
                ReferencePresetRulesTests.TestReferenceMeshBindingRules),
            ("Reviewed render clicks bind exact deterministic head triangles",
                ReferenceMeshAnchorBindingTests.TestDeterministicReviewedRaycast),
            ("Real face math builds exact morph response columns",
                ReferencePresetSolverTests.TestRealFaceMathResponseMatrix),
            ("Authentic High Poly Head NAM9 pairs build exact responses",
                ReferencePresetSolverTests.TestAuthenticHighPolyHeadResponseMatrix),
            ("Fixed solver is deterministic and sculpt remains bounded",
                ReferencePresetSolverTests.TestFixedSolverAndBoundedSculpt),
            ("CPU reference rasterizer is authority-bound and deterministic",
                ReferencePresetRenderingTests.TestCpuRasterizerControls),
            ("Reference preview admits exact authentic 4096 COR textures without widening FaceTint",
                ReferencePresetRenderingTests.TestAuthenticFourKTextureProfile),
            ("Authentic skinned COR hair is placed in the selected head coordinate space",
                ReferencePresetRenderingTests.TestAuthenticSkinnedHeadpartPlacement),
            ("Authentic HPH and DDS comparison artifacts are deterministic",
                ReferencePresetRenderingTests.TestAuthenticRenderAndComparison),
            ("Solved custom morphs route only to headpart shapes that contain their TRI channel",
                ReferencePresetRenderingTests.TestShapeScopedSolvedMorphRouting),
            ("Reference JSlot writer is canonical transactional and preserving",
                ReferencePresetWriterTests.TestTransactionalCanonicalWriter),
            ("Reference session documents are canonical hash-bound and atomic",
                ReferencePresetSessionTests.TestCanonicalSessionWriteRead),
            ("Reference authoring transaction is proposal-first hash-bound and exact",
                ReferencePresetTransactionTests.TestHashBoundTransactionalWorkflow),
            ("Reference authoring transaction rejects undeclared output and rolls back cancellation",
                ReferencePresetTransactionTests.TestUndeclaredAndCancelledRollback),
            ("Reference authority transitions are monotonic and hash bound",
                ReferencePresetRulesTests.TestReferenceAuthorityTransitions),
            ("Reference sculpt eligibility preserves single-view depth and neck ring",
                ReferencePresetRulesTests.TestReferenceSculptRules)
        };

        var passed = 0;
        foreach ((string name, Func<Task> run) in tests)
        {
            try
            {
                await run();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                return 1;
            }
        }

        Console.WriteLine($"RESULT PASS {passed}/{tests.Length}");
        return 0;
    }
}
