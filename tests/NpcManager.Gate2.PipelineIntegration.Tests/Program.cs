using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Gate2.PipelineIntegration.Tests;

internal static partial class Program
{
    private const string Selector_RaceMenuStandaloneSchema8ChildProbe = "--test-racemenu-standalone-schema8-child-probe";
    private const string Selector_RaceMenuStandaloneSchema3To7Goldens = "--test-racemenu-standalone-schema3-7-goldens";

    private const string Schema3CanonicalGoldenBase64 =
        "eyJzY2hlbWFWZXJzaW9uIjozLCJhc3NldFNldElkIjoiZm9jdXNlZC1zY2hlbWEtMyIsImVkaXRpb24iOiJza3lyaW1zZSIsIm5hbTlBdXRob3JpdHkiOnsicGx1Z2luUGF0aCI6ImZpeHR1cmVzL3NjaGVtYS0zL25hbTktc291cmNlLmVzcCIsInBsdWdpblNoYTI1NiI6IjlmNjRhNzQ3ZTFiOTdmMTMxZmFiYjZiNDQ3Mjk2YzliNmYwMjAxZTc5ZmIzYzUzNTZlNmM3N2U4OWI2YTgwNmEiLCJucGNGb3JtSWQiOiIweDAwMDAwODAwIiwidHJhaWxpbmdWYWx1ZSI6MH0sImZhY2VUaW50Ijp7IndpZHRoIjoxLCJoZWlnaHQiOjF9LCJwcml2YXRlSGVhZFRleHR1cmVzIjp7ImRpZmZ1c2UiOiJBY3RvcnMvQ2hhcmFjdGVyL1Rlc3QvZmVtYWxlaGVhZC5kZHMiLCJub3JtYWxPckdsb3NzIjoiQWN0b3JzL0NoYXJhY3Rlci9UZXN0L2ZlbWFsZWhlYWRfbXNuLmRkcyIsImdsb3dPckRldGFpbE1hcCI6IkFjdG9ycy9DaGFyYWN0ZXIvVGVzdC9mZW1hbGVoZWFkX3NrLmRkcyIsImhlaWdodCI6IkFjdG9ycy9DaGFyYWN0ZXIvTWFsZS9CbGFua0RldGFpbG1hcC5kZHMiLCJiYWNrbGlnaHRNYXNrT3JTcGVjdWxhciI6IkFjdG9ycy9DaGFyYWN0ZXIvVGVzdC9mZW1hbGVoZWFkX3MuZGRzIiwiZW52aXJvbm1lbnRNYXNrT3JTdWJzdXJmYWNlVGludCI6bnVsbCwiZW52aXJvbm1lbnQiOm51bGwsIm11bHRpbGF5ZXIiOm51bGx9LCJwYWNrYWdlQXNzZXRzIjpbeyJzb3VyY2VQYXRoIjoiZml4dHVyZXMvc2NoZW1hLTMvcGFja2FnZS1zb3VyY2UuZGRzIiwic2hhMjU2IjoiNTVlNTUwOWY4MDUyOTk4Mjk0MjY2ZWU1YjUwY2I1OTI5MzgxOTFmYjVkNjdmNzNjYWMyZTYwYjAyNzZiMWJkZCIsImRlc3RpbmF0aW9uIjoiVGV4dHVyZXMvVGVzdC9wYWNrYWdlLXNvdXJjZS5kZHMifV0sIm92ZXJsYXlEZWNpc2lvbnMiOm51bGwsImV4dGVybmFsVGV4dHVyZUF1dGhvcml0aWVzIjpbXSwiZmluYWxPdXRwdXRBdXRob3JpdHkiOnsibWFuaWZlc3RQYXRoIjoiZml4dHVyZXMvc2NoZW1hLTMvZmluYWwtb3V0cHV0LWF1dGhvcml0eS5qc29uIiwibWFuaWZlc3RTaGEyNTYiOiI3ZDQ0NjMxMTMzMWRkZWYxYTAyYTM4YmFkOGJlYmNlOTIyZDJkZGExZDFkM2QxNjkzYjQwOWEzNGNmMGY0OTk0In19";
    private const string Schema6CanonicalGoldenBase64 =
        "eyJzY2hlbWFWZXJzaW9uIjo2LCJhc3NldFNldElkIjoiZm9jdXNlZC1zY2hlbWEtNiIsImVkaXRpb24iOiJza3lyaW1zZSIsIm5hbTlBdXRob3JpdHkiOnsicGx1Z2luUGF0aCI6ImZpeHR1cmVzL3NjaGVtYS02L25hbTktc291cmNlLmVzcCIsInBsdWdpblNoYTI1NiI6IjlmNjRhNzQ3ZTFiOTdmMTMxZmFiYjZiNDQ3Mjk2YzliNmYwMjAxZTc5ZmIzYzUzNTZlNmM3N2U4OWI2YTgwNmEiLCJucGNGb3JtSWQiOiIweDAwMDAwODAwIiwidHJhaWxpbmdWYWx1ZSI6MH0sImZhY2VUaW50Ijp7IndpZHRoIjoxLCJoZWlnaHQiOjF9LCJwcml2YXRlSGVhZFRleHR1cmVzIjp7ImRpZmZ1c2UiOiJBY3RvcnMvQ2hhcmFjdGVyL1Rlc3QvZmVtYWxlaGVhZC5kZHMiLCJub3JtYWxPckdsb3NzIjoiQWN0b3JzL0NoYXJhY3Rlci9UZXN0L2ZlbWFsZWhlYWRfbXNuLmRkcyIsImdsb3dPckRldGFpbE1hcCI6IkFjdG9ycy9DaGFyYWN0ZXIvVGVzdC9mZW1hbGVoZWFkX3NrLmRkcyIsImhlaWdodCI6IkFjdG9ycy9DaGFyYWN0ZXIvTWFsZS9CbGFua0RldGFpbG1hcC5kZHMiLCJiYWNrbGlnaHRNYXNrT3JTcGVjdWxhciI6IkFjdG9ycy9DaGFyYWN0ZXIvVGVzdC9mZW1hbGVoZWFkX3MuZGRzIiwiZW52aXJvbm1lbnRNYXNrT3JTdWJzdXJmYWNlVGludCI6bnVsbCwiZW52aXJvbm1lbnQiOm51bGwsIm11bHRpbGF5ZXIiOm51bGx9LCJwYWNrYWdlQXNzZXRzIjpbXSwib3ZlcmxheURlY2lzaW9ucyI6bnVsbCwiZXh0ZXJuYWxUZXh0dXJlQXV0aG9yaXRpZXMiOltdLCJmaW5hbE91dHB1dEF1dGhvcml0eSI6bnVsbH0=";

    private const string FixedSchema4CanonicalGoldenJson = """
{"schemaVersion":4,"assetSetId":"focused-schema-4","edition":"skyrimse","nam9Authority":{"pluginPath":"fixtures/schema-4/nam9-source.esp","pluginSha256":"9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a","npcFormId":"0x00000800","trailingValue":0},"faceTint":{"width":1,"height":1},"privateHeadTextures":{"diffuse":"Actors/Character/Test/femalehead.dds","normalOrGloss":"Actors/Character/Test/femalehead_msn.dds","glowOrDetailMap":"Actors/Character/Test/femalehead_sk.dds","height":"Actors/Character/Male/BlankDetailmap.dds","backlightMaskOrSpecular":"Actors/Character/Test/femalehead_s.dds","environmentMaskOrSubsurfaceTint":null,"environment":null,"multilayer":null},"packageAssets":[{"sourcePath":"fixtures/schema-4/package-source.dds","sha256":"55e5509f8052998294266ee5b50cb592938191fb5d67f73cac2e60b0276b1bdd","destination":"Textures/Test/package-source.dds"}],"overlayDecisions":null,"externalTextureAuthorities":[],"faceBakeAuthority":{"manifestPath":"fixtures/schema-4/face-bake-authority.json","manifestSha256":"c46951567f3d5bc6cac964a46690c3ba89446ed66de4aff35fc1e63f5e0881fd"},"finalOutputAuthority":null}
""";

    public static async Task<int> Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        if (args is [Selector_RaceMenuStandaloneSchema8ChildProbe])
        {
            Console.WriteLine($"PROBE_READY PID={Environment.ProcessId}");
            await Task.Delay(Timeout.InfiniteTimeSpan);
            return 0;
        }
        if (args is [Selector_RaceMenuStandaloneSchema3To7Goldens])
        {
            await TestSchema3To7CanonicalGoldensAsync();
            Console.WriteLine(
                "PASS --test-racemenu-standalone-schema3-7-goldens");
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("schema v3 retains the bounded final-output oracle", TestSchema3OracleAsync),
            ("schema v4 admits only the generic face-bake authority", TestSchema4AuthorityAsync),
            ("schema v4 refuses a finished-output oracle", TestSchema4OracleRefusalAsync),
            ("schema v6 admits only the exact selected CharGen bundle",
                TestSchema6DirectCharGenAsync),
            ("schema v7 admits external BodySlide preset and mesh authority",
                TestSchema7ExternalBodySlideAuthorityAsync),
            ("schema v7 private texture coverage includes the required height slot",
                TestSchema7PrivateHeightTextureCoverageAsync),
            ("schema v5 real request completes with durable post-staging evidence",
                TestSchema5RealPipelineAsync),
            ("schema v5 can create an intentionally naked NPC",
                TestSchema5NoDefaultOutfitPipelineAsync),
            ("schema v5 BodySlide opt-out retains no BodyGen artifacts",
                TestSchema5BodySlideOmissionAsync),
            ("preset build refuses missing whole-skin authority before output",
                TestMissingWholeSkinAuthorityAsync),
            ("preset build refuses a non-skin authority manifest before output",
                TestInvalidWholeSkinAuthorityAsync),
            ("schema v5 existing NPC retains source ownership and origin-keyed assets",
                TestSchema5ExistingNpcPipelineAsync),
            ("schema v5 missing services refuse before output or staging",
                TestSchema5DependencyPreflightAsync),
            ("schema v5 postcheck failure quarantines the requested output",
                TestSchema5PostcheckQuarantineAsync)
        };

        var passed = 0;
        foreach (var test in tests)
        {
            try
            {
                await test.Run();
                Console.WriteLine($"PASS {test.Name}");
                passed++;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {test.Name}: {exception.Message}");
            }
        }

        Console.WriteLine($"RESULT {(passed == tests.Length ? "PASS" : "FAIL")} {passed}/{tests.Length}");
        return passed == tests.Length ? 0 : 1;
    }

    private const string FixedSchema7CanonicalGoldenJson = """
{"schemaVersion":7,"assetSetId":"focused-schema-7","edition":"skyrimse","nam9Authority":{"pluginPath":"fixtures/schema-7/nam9-source.esp","pluginSha256":"9f64a747e1b97f131fabb6b447296c9b6f0201e79fb3c5356e6c77e89b6a806a","npcFormId":"0x00000800","trailingValue":0},"faceTint":{"width":1,"height":1},"privateHeadTextures":{"diffuse":"Actors/Character/Test/femalehead.dds","normalOrGloss":"Actors/Character/Test/femalehead_msn.dds","glowOrDetailMap":"Actors/Character/Test/femalehead_sk.dds","height":"Actors/Character/Male/BlankDetailmap.dds","backlightMaskOrSpecular":"Actors/Character/Test/femalehead_s.dds","environmentMaskOrSubsurfaceTint":null,"environment":null,"multilayer":null},"packageAssets":[],"overlayDecisions":null,"externalTextureAuthorities":[],"finalOutputAuthority":null,"bodySlidePresetAuthority":{"manifestPath":"fixtures/schema-7/body-slide-preset-authority.json","manifestSha256":"a3b64625255eba93711cb88622cc70e489c856a163050b35546fa26c1795a2f7"},"bodyMeshAuthority":{"manifestPath":"fixtures/schema-7/body-mesh-authority.json","manifestSha256":"bf586daee874fb909c7e1805451ccb9922d53241657bd90b218356893f9690b4"}}
""";

    private static async Task TestSchema3To7CanonicalGoldensAsync()
    {
        var cases = new (int Schema, bool FaceBake, bool FinalOracle,
            bool Package, bool BodySlide, bool BodyMesh, string Name)[]
        {
            (3, false, true, true, false, false, "schema3"),
            (4, true, false, true, false, false, "schema4"),
            (6, false, false, false, false, false, "schema6"),
            (7, false, false, false, true, true, "schema7")
        };
        foreach (var item in cases)
        {
            using var fixture = new ManifestFixture(
                $"canonical-{item.Name}");
            WorkspacePath manifest = fixture.WriteStandaloneManifest(
                item.Schema,
                item.FaceBake,
                item.FinalOracle,
                item.Package,
                item.BodySlide,
                item.BodyMesh);
            byte[] before = fixture.CanonicalBytes(manifest, item.Schema);
            string expectedText = item.Schema == 7
                ? FixedSchema7CanonicalGoldenJson.TrimEnd()
                : item.Schema == 4
                    ? FixedSchema4CanonicalGoldenJson.TrimEnd()
                    : Encoding.UTF8.GetString(Convert.FromBase64String(item.Schema switch
                {
                    3 => Schema3CanonicalGoldenBase64,
                    6 => Schema6CanonicalGoldenBase64,
                    _ => throw new InvalidDataException(
                        $"No fixed canonical golden for schema {item.Schema}.")
                }));
            if (item.Schema == 3)
                expectedText = expectedText.Replace(
                    "7d446311331ddef1a02a38bad8bebce922d2dda1d1d3d1693b409a34cf0f4994",
                    "832f609a07489b4d1368736da466be785b682993d12df421d858de06a3a79a37",
                    StringComparison.Ordinal);
            byte[] expected = Encoding.UTF8.GetBytes(expectedText);
            if (!before.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidOperationException(
                    $"Schema {item.Schema} canonical writer bytes changed.");
            }
            switch (item.Schema)
            {
                case 3:
                    await TestSchema3OracleAsync();
                    break;
                case 4:
                    await TestSchema4AuthorityAsync();
                    break;
                case 6:
                    await TestSchema6DirectCharGenAsync();
                    break;
                case 7:
                    await TestSchema7ExternalBodySlideAuthorityAsync();
                    break;
                default:
                    throw new InvalidDataException(
                        $"No authoritative legacy helper for schema {item.Schema}.");
            }
            byte[] after = fixture.CanonicalBytes(manifest, item.Schema);
            Assert(before.AsSpan().SequenceEqual(after),
                $"Schema {item.Schema} canonical bytes changed around its authoritative helper.");
            Assert(before.Length > 0,
                $"Schema {item.Schema} canonical writer bytes were empty.");
        }
    }

    private static async Task TestSchema3OracleAsync()
    {
        using var fixture = new ManifestFixture("schema3");
        var plan = new RejectingPlanService();
        var result = await ExecuteAsync(
            fixture,
            plan,
            fixture.WriteStandaloneManifest(schemaVersion: 3, includeFaceBake: false,
                includeFinalOracle: true));

        Assert(plan.CallCount == 1, "Schema v3 did not reach the plan boundary.");
        Assert(result.Assets is
        {
            SchemaVersion: 3, FinalOutputAuthority: not null,
            FaceBakeAuthority: null
        },
            "Schema v3 did not retain the bounded final-output oracle contract.");
    }

    private static async Task TestSchema4AuthorityAsync()
    {
        using var fixture = new ManifestFixture("schema4");
        var plan = new RejectingPlanService();
        var result = await ExecuteAsync(
            fixture,
            plan,
            fixture.WriteStandaloneManifest(schemaVersion: 4, includeFaceBake: true,
                includeFinalOracle: false));

        Assert(plan.CallCount == 1, "Schema v4 did not reach the plan boundary.");
        Assert(result.Assets is
        {
            SchemaVersion: 4, FinalOutputAuthority: null,
            FaceBakeAuthority: not null
        } assets &&
               assets.FaceBakeAuthority.ManifestPath == fixture.FaceBakeManifest &&
               assets.FaceBakeAuthority.ExpectedManifestSha256 ==
               HashFile(fixture.FaceBakeManifest.Value),
            "Schema v4 did not retain the exact generic face-bake authority.");
    }

    private static async Task TestSchema4OracleRefusalAsync()
    {
        using var fixture = new ManifestFixture("schema4-oracle");
        var plan = new RejectingPlanService();
        var result = await ExecuteAsync(
            fixture,
            plan,
            fixture.WriteStandaloneManifest(schemaVersion: 4, includeFaceBake: true,
                includeFinalOracle: true));

        Assert(plan.CallCount == 0,
            "Schema v4 reached planning after declaring a forbidden final-output oracle.");
        Assert(!result.Completed && result.Assets is null &&
               result.Diagnostics.Any(item =>
                   item.Code == "racemenu-assets-manifest-invalid" &&
                   item.Message.Contains("requires finalOutputAuthority to be null",
                       StringComparison.Ordinal)),
            "Schema v4 did not fail closed on a finished-output oracle.");
    }

    private static async Task TestSchema6DirectCharGenAsync()
    {
        using var fixture = new ManifestFixture("schema6-direct");
        var plan = new RejectingPlanService();
        var result = await ExecuteAsync(
            fixture,
            plan,
            fixture.WriteStandaloneManifest(schemaVersion: 6, includeFaceBake: false,
                includeFinalOracle: false, includePackageAsset: false));

        Assert(plan.CallCount == 1, "Schema v6 did not reach the plan boundary.");
        Assert(result.Assets is
        {
            SchemaVersion: 6,
            FinalOutputAuthority: null,
            FaceBakeAuthority: null,
            FaceTextureBakeAuthority: null,
            PackageAssets.Length: 0
        }, "Schema v6 did not retain the direct CharGen-only authority contract.");
    }

    private static async Task TestSchema7ExternalBodySlideAuthorityAsync()
    {
        using var fixture = new ManifestFixture("schema7-bodyslide");
        var plan = new RejectingPlanService();
        var result = await ExecuteAsync(
            fixture,
            plan,
            fixture.WriteStandaloneManifest(
                schemaVersion: 7,
                includeFaceBake: false,
                includeFinalOracle: false,
                includePackageAsset: false,
                includeBodySlideAuthority: true,
                includeBodyMeshAuthority: true));

        Assert(plan.CallCount == 1, "Schema v7 did not reach the plan boundary.");
        Assert(result.Assets is
        {
            SchemaVersion: 7,
            FinalOutputAuthority: null,
            BodySlidePresetAuthority:
            {
                PresetName: "[DevonixS] - Zenithar's Masterpiece",
                SliderSet: "UBE SE 2.0 Release Body",
                SliderCount: 2
            },
            BodyMeshAuthority: { Meshes.Length: 6 },
            PackageAssets.Length: 0
        } assets &&
               assets.BodySlidePresetAuthority.Groups.Contains(
                   "UBE Female",
                   StringComparer.Ordinal) &&
               assets.BodyMeshAuthority.Meshes.Any(item =>
                   item.Role == RaceMenuNpcBodyMeshRole.Body1 &&
                   item.Destination.Value ==
                   "Meshes/Actors/Character/Chel/body_1.nif"),
            "Schema v7 did not retain the exact BodySlide preset and body mesh authority contract.");

        var refusedPlan = new RejectingPlanService();
        var refused = await ExecuteAsync(
            fixture,
            refusedPlan,
            fixture.WriteStandaloneManifest(
                schemaVersion: 7,
                includeFaceBake: false,
                includeFinalOracle: false,
                includePackageAsset: false,
                includeBodySlideAuthority: true,
                includeBodyMeshAuthority: false));
        Assert(refusedPlan.CallCount == 0 &&
               !refused.Completed &&
               refused.Diagnostics.Any(item =>
                   item.Code ==
                   "racemenu-assets-schema7-body-mesh-authority-required"),
            "Schema v7 without a body/hands/feet mesh authority did not fail closed before planning.");
    }

    private static Task TestSchema7PrivateHeightTextureCoverageAsync()
    {
        var textures = new SkyrimPrivateHeadTexturePaths(
            new AssetPath("Actors/Character/Test/femalehead.dds"),
            new AssetPath("Actors/Character/Test/femalehead_msn.dds"),
            new AssetPath("Actors/Character/Test/femalehead_sk.dds"),
            new AssetPath("Actors/Character/Male/BlankDetailmap.dds"),
            new AssetPath("Actors/Character/Test/femalehead_s.dds"));
        var hash = new Sha256Hash(new string('A', 64));
        ImmutableArray<RaceMenuNpcExternalTextureAuthority> external =
        [
            External("Textures/Actors/Character/Test/femalehead.dds"),
            External("Textures/Actors/Character/Test/femalehead_msn.dds"),
            External("Textures/Actors/Character/Test/femalehead_sk.dds"),
            External("Textures/Actors/Character/Male/BlankDetailmap.dds"),
            External("Textures/Actors/Character/Test/femalehead_s.dds")
        ];
        var assets = new RaceMenuNpcStandaloneAssets(
            "schema7-height-coverage",
            new RaceMenuNpcNam9TrailingAuthority(
                new WorkspacePath(@"K:\ExampleWorkspace\tests\nam9.esp"),
                hash,
                new FormId(0x00000800),
                0F),
            textures,
            1,
            1,
            ImmutableArray<BlankNpcTransitivePackageAsset>.Empty,
            null,
            external,
            null)
        {
            SchemaVersion = 7
        };
        var runtime = new SkyrimNpcRuntimeAppearancePayload(
            true,
            ImmutableArray<SkyrimNpcRuntimeOverlay>.Empty,
            ImmutableArray<SkyrimNpcRuntimeSkinOverride>.Empty,
            ImmutableArray<SkyrimNpcRuntimeNodeTransform>.Empty,
            ImmutableArray<SkyrimNpcRuntimeSourceDisposition>.Empty);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var method = typeof(RaceMenuNpcBuildService).GetMethod(
            "ValidateStandaloneAssetCoverage",
            System.Reflection.BindingFlags.NonPublic |
            System.Reflection.BindingFlags.Static) ??
            throw new InvalidOperationException(
                "The standalone coverage boundary was not found.");
        var accepted = (bool)(method.Invoke(
            null,
            [assets, runtime,
                ImmutableArray<BlankNpcTransitivePackageAsset>.Empty,
                diagnostics]) ??
            throw new InvalidOperationException(
                "The standalone coverage boundary returned null."));

        Assert(accepted &&
               diagnostics.All(item =>
                   item.Code != "racemenu-assets-external-authority-unused"),
            "Schema v7 rejected its required private height texture as an unused external authority.");
        return Task.CompletedTask;

        RaceMenuNpcExternalTextureAuthority External(string path) =>
            new(
                new AssetPath(path),
                "loose",
                new WorkspacePath(@"K:\ExampleWorkspace\tests\texture.dds"),
                hash,
                hash);
    }

    private static async Task<RaceMenuNpcExecutionResult> ExecuteAsync(
        ManifestFixture fixture,
        RejectingPlanService plan,
        WorkspacePath standaloneManifest)
    {
        var policy = new KOnlyWorkspacePolicy(
            fixture.LabRoot,
            new WorkspacePath(@"F:\ExampleGame"));
        var service = new RaceMenuNpcBuildService(
            plan,
            null!,
            null!,
            null!,
            new NoOpAssetIndexer(),
            policy,
            fixture.LabRoot);
        return await service.ExecuteAsync(
            new RaceMenuNpcExecutionRequest(
                null!,
                new RaceMenuNpcStandaloneAssetAuthority(
                    standaloneManifest,
                    HashFile(standaloneManifest.Value))),
            null,
            CancellationToken.None);
    }

    private static Sha256Hash HashFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed class RejectingPlanService : IRaceMenuNpcAppearancePlanService
    {
        public int CallCount { get; private set; }

        public ValueTask<RaceMenuNpcAppearancePlanResult> AnalyzeAsync(
            RaceMenuNpcBuildRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            return ValueTask.FromResult(new RaceMenuNpcAppearancePlanResult(
                false,
                null,
                ImmutableArray.Create(new Diagnostic(
                    "test-plan-stop",
                    DiagnosticSeverity.Error,
                    "The focused manifest test stops after the standalone authority seam."))));
        }
    }

    private sealed class NoOpAssetIndexer : IAssetIndexer
    {
        public ValueTask<AssetIndex> IndexAsync(
            AssetIndexRequest request,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(new AssetIndex(
                request.Edition,
                [],
                []));
    }

    private sealed class ManifestFixture : IDisposable
    {
        private static readonly string[] BodySlideGroups = ["UBE", "UBE Female"];
        private readonly string _testRoot;
        private readonly WorkspacePath _nam9;
        private readonly WorkspacePath _packageAsset;
        private readonly WorkspacePath _finalFaceGeom;
        private readonly WorkspacePath _finalFaceTint;
        private readonly WorkspacePath _finalEvidence;
        private readonly WorkspacePath _finalManifest;
        private readonly WorkspacePath _bodySlideXml;
        private readonly WorkspacePath _bodySlidePresetManifest;
        private readonly WorkspacePath _bodyMeshManifest;
        private readonly ImmutableArray<WorkspacePath> _bodyMeshes;

        public ManifestFixture(string name)
        {
            LabRoot = FindLabRoot();
            _testRoot = Path.Combine(
                LabRoot.Value,
                "artifacts",
                "racemenu-gate2-pipeline-fixtures");
            WorkRoot = new WorkspacePath(Path.Combine(
                _testRoot,
                $".work-{name}-{Environment.ProcessId}-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(WorkRoot.Value);

            _nam9 = WriteBytes("nam9-source.esp", [1, 2, 3, 4]);
            _packageAsset = WriteBytes("package-source.dds", [5, 6, 7, 8]);
            FaceBakeManifest = WriteJson("face-bake-authority.json", new { authority = "generic" });
            _finalFaceGeom = WriteBytes("accepted-facegeom.nif", [9, 10, 11, 12]);
            _finalFaceTint = WriteBytes("accepted-facetint.dds", [13, 14, 15, 16]);
            _finalEvidence = WriteJson("accepted-evidence.json", new { qualified = true });
            _finalManifest = WriteFinalOutputAuthority();
            _bodySlideXml = WriteText("zenithar-masterpiece.xml", """
                <SliderPresets>
                  <Preset name="[DevonixS] - Zenithar's Masterpiece" set="UBE SE 2.0 Release Body">
                    <Group name="UBE" />
                    <Group name="UBE Female" />
                    <SetSlider name="Breasts" size="small" value="25" />
                    <SetSlider name="Breasts" size="big" value="75.5" />
                  </Preset>
                </SliderPresets>
                """);
            _bodySlidePresetManifest = WriteBodySlidePresetAuthority();
            _bodyMeshes =
            [
                WriteBytes("body_0.nif", [20, 0]),
                WriteBytes("body_1.nif", [20, 1]),
                WriteBytes("hands_0.nif", [21, 0]),
                WriteBytes("hands_1.nif", [21, 1]),
                WriteBytes("feet_0.nif", [22, 0]),
                WriteBytes("feet_1.nif", [22, 1])
            ];
            _bodyMeshManifest = WriteBodyMeshAuthority();
        }

        public WorkspacePath LabRoot { get; }
        public WorkspacePath WorkRoot { get; }
        public WorkspacePath FaceBakeManifest { get; }

        public WorkspacePath WriteStandaloneManifest(
            int schemaVersion,
            bool includeFaceBake,
            bool includeFinalOracle,
            bool includePackageAsset = true,
            bool includeBodySlideAuthority = false,
            bool includeBodyMeshAuthority = false)
        {
            var root = new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["schemaVersion"] = schemaVersion,
                ["assetSetId"] = $"focused-schema-{schemaVersion}",
                ["edition"] = "skyrimse",
                ["nam9Authority"] = new
                {
                    pluginPath = Relative(_nam9),
                    pluginSha256 = HashFile(_nam9.Value).Value,
                    npcFormId = "0x00000800",
                    trailingValue = 0F
                },
                ["faceTint"] = new { width = 1, height = 1 },
                ["privateHeadTextures"] = new
                {
                    diffuse = "Actors/Character/Test/femalehead.dds",
                    normalOrGloss = "Actors/Character/Test/femalehead_msn.dds",
                    glowOrDetailMap = "Actors/Character/Test/femalehead_sk.dds",
                    height = "Actors/Character/Male/BlankDetailmap.dds",
                    backlightMaskOrSpecular = "Actors/Character/Test/femalehead_s.dds",
                    environmentMaskOrSubsurfaceTint = (string?)null,
                    environment = (string?)null,
                    multilayer = (string?)null
                },
                ["packageAssets"] = includePackageAsset
                    ? new object[]
                    {
                        new
                        {
                            sourcePath = Relative(_packageAsset),
                            sha256 = HashFile(_packageAsset.Value).Value,
                            destination = "Textures/Test/package-source.dds"
                        }
                    }
                    : Array.Empty<object>(),
                ["overlayDecisions"] = null,
                ["externalTextureAuthorities"] = Array.Empty<object>()
            };
            if (includeFaceBake)
            {
                root["faceBakeAuthority"] = new
                {
                    manifestPath = Relative(FaceBakeManifest),
                    manifestSha256 = HashFile(FaceBakeManifest.Value).Value
                };
            }
            root["finalOutputAuthority"] = includeFinalOracle
                ? new
                {
                    manifestPath = Relative(_finalManifest),
                    manifestSha256 = HashFile(_finalManifest.Value).Value
                }
                : null;
            if (schemaVersion == 7)
            {
                root["bodySlidePresetAuthority"] = includeBodySlideAuthority
                    ? new
                    {
                        manifestPath = Relative(_bodySlidePresetManifest),
                        manifestSha256 = HashFile(_bodySlidePresetManifest.Value).Value
                    }
                    : null;
                root["bodyMeshAuthority"] = includeBodyMeshAuthority
                    ? new
                    {
                        manifestPath = Relative(_bodyMeshManifest),
                        manifestSha256 = HashFile(_bodyMeshManifest.Value).Value
                    }
                    : null;
            }
            return WriteJson($"standalone-v{schemaVersion}.json", root);
        }

        public byte[] CanonicalBytes(WorkspacePath manifest, int schemaVersion)
        {
            string workRoot = Path.GetRelativePath(
                LabRoot.Value, WorkRoot.Value)
                .Replace(Path.DirectorySeparatorChar, '/');
            string normalized = File.ReadAllText(manifest.Value)
                .Replace(workRoot, $"fixtures/schema-{schemaVersion}",
                    StringComparison.Ordinal);
            if (schemaVersion == 3)
            {
                normalized = ReplaceNestedHash(
                    normalized,
                    _finalManifest,
                    workRoot,
                    $"fixtures/schema-{schemaVersion}");
            }
            else if (schemaVersion == 7)
            {
                string presetHash = HashFile(_bodySlidePresetManifest.Value).Value;
                string normalizedPreset = File.ReadAllText(
                    _bodySlidePresetManifest.Value)
                    .Replace(workRoot, $"fixtures/schema-{schemaVersion}",
                        StringComparison.Ordinal);
                string stablePresetHash = HashText(normalizedPreset);
                normalized = normalized.Replace(presetHash, stablePresetHash,
                    StringComparison.Ordinal);

                string meshHash = HashFile(_bodyMeshManifest.Value).Value;
                string normalizedMesh = File.ReadAllText(_bodyMeshManifest.Value)
                    .Replace(workRoot, $"fixtures/schema-{schemaVersion}",
                        StringComparison.Ordinal)
                    .Replace(presetHash, stablePresetHash,
                        StringComparison.Ordinal);
                string stableMeshHash = HashText(normalizedMesh);
                normalized = normalized.Replace(meshHash, stableMeshHash,
                    StringComparison.Ordinal);
            }
            return Encoding.UTF8.GetBytes(normalized);
        }

        private static string ReplaceNestedHash(
            string outer,
            WorkspacePath nested,
            string workRoot,
            string stableRoot)
        {
            string originalHash = HashFile(nested.Value).Value;
            string stableText = File.ReadAllText(nested.Value)
                .Replace(workRoot, stableRoot, StringComparison.Ordinal);
            return outer.Replace(originalHash, HashText(stableText),
                StringComparison.Ordinal);
        }

        private static string HashText(string text) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))
                .ToLowerInvariant();

        public void Dispose()
        {
            var owned = Path.GetFullPath(WorkRoot.Value);
            var root = Path.GetFullPath(_testRoot) + Path.DirectorySeparatorChar;
            if (!owned.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Refusing to clean a non-owned test path.");
            if (Directory.Exists(owned)) Directory.Delete(owned, recursive: true);
        }

        private WorkspacePath WriteFinalOutputAuthority() =>
            WriteJson("final-output-authority.json", new
            {
                schemaVersion = 1,
                authorityId = "historical-oracle",
                edition = "skyrimse",
                sourceKind = "admitted-final-oracle",
                presetSha256 = new string('1', 64),
                charGenFaceGeomSha256 = new string('2', 64),
                charGenFaceTintSha256 = new string('3', 64),
                faceGeom = new
                {
                    path = Relative(_finalFaceGeom),
                    sha256 = HashFile(_finalFaceGeom.Value).Value
                },
                faceTint = new
                {
                    path = Relative(_finalFaceTint),
                    sha256 = HashFile(_finalFaceTint.Value).Value,
                    width = 1,
                    height = 1
                },
                evidence = new[]
                {
                    new
                    {
                        path = Relative(_finalEvidence),
                        sha256 = HashFile(_finalEvidence.Value).Value
                    }
                },
                runtimeAuthority = false
            });

        private WorkspacePath WriteBodySlidePresetAuthority() =>
            WriteJson("body-slide-preset-authority.json", new
            {
                schemaVersion = 1,
                authorityId = "zenithar-masterpiece-fixture",
                edition = "skyrimse",
                sourceKind = "bodyslide-sliderpreset-xml",
                presetXmlPath = Relative(_bodySlideXml),
                presetXmlSha256 = HashFile(_bodySlideXml.Value).Value,
                presetName = "[DevonixS] - Zenithar's Masterpiece",
                sliderSet = "UBE SE 2.0 Release Body",
                groups = BodySlideGroups,
                sliderCount = 2,
                runtimeAuthority = false
            });

        private WorkspacePath WriteBodyMeshAuthority()
        {
            var roles = new[]
            {
                ("body0", "Meshes/Actors/Character/Chel/body_0.nif"),
                ("body1", "Meshes/Actors/Character/Chel/body_1.nif"),
                ("hands0", "Meshes/Actors/Character/Chel/hands_0.nif"),
                ("hands1", "Meshes/Actors/Character/Chel/hands_1.nif"),
                ("feet0", "Meshes/Actors/Character/Chel/feet_0.nif"),
                ("feet1", "Meshes/Actors/Character/Chel/feet_1.nif")
            };
            var meshes = roles.Select((row, index) => new
            {
                role = row.Item1,
                sourcePath = Relative(_bodyMeshes[index]),
                sha256 = HashFile(_bodyMeshes[index].Value).Value,
                destination = row.Item2
            }).ToArray();
            return WriteJson("body-mesh-authority.json", new
            {
                schemaVersion = 1,
                authorityId = "zenithar-generated-meshes-fixture",
                edition = "skyrimse",
                sourceKind = "external-bodyslide-generated-meshes",
                bodySlidePresetAuthority = new
                {
                    manifestPath = Relative(_bodySlidePresetManifest),
                    manifestSha256 =
                        HashFile(_bodySlidePresetManifest.Value).Value
                },
                meshes,
                runtimeAuthority = false
            });
        }

        private WorkspacePath WriteBytes(string name, byte[] bytes)
        {
            var path = new WorkspacePath(Path.Combine(WorkRoot.Value, name));
            File.WriteAllBytes(path.Value, bytes);
            return path;
        }

        private WorkspacePath WriteText(string name, string text)
        {
            var path = new WorkspacePath(Path.Combine(WorkRoot.Value, name));
            File.WriteAllText(path.Value, text);
            return path;
        }

        private WorkspacePath WriteJson(string name, object value)
        {
            var path = new WorkspacePath(Path.Combine(WorkRoot.Value, name));
            File.WriteAllText(path.Value, JsonSerializer.Serialize(value));
            return path;
        }

        private string Relative(WorkspacePath path) =>
            Path.GetRelativePath(LabRoot.Value, path.Value)
                .Replace(Path.DirectorySeparatorChar, '/');

        private static WorkspacePath FindLabRoot()
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "AGENTS.md")) &&
                    File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
                {
                    return new WorkspacePath(current.FullName);
                }
                current = current.Parent;
            }
            throw new InvalidOperationException(
                "Could not locate the repo-local NpcManagerReimplementation fixture root.");
        }
    }
}
