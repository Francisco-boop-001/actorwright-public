using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.TestInfrastructure;

namespace NpcManager.Gate1.Tests;

internal static partial class Program
{
    private const string Selector_EmitProductProviderFixture = "--emit-product-provider-fixture";

    private const string ProviderManifestSha256 =
        "EACB7112F3D0177F1C8C1B14604B3F36F413185C52973BC6027059EAE1D7D06E";
    private const string TemplatePluginSha256 =
        "421C3A902A87F343D50F8791CC4B9CAFB2B71BF3B7D94F7C2F7BE2DB6535BBF0";
    private const string FaceGeomCarrierSha256 =
        "4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9";
    private const string TexconvSha256 =
        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06";
    private const string ExactFaceTintSha256 =
        "18C5D0996929C3AAC0AC18F18E794E643341F2BE4D9914CAA4A990BA1DD76C46";
    private const string EmptySha256 =
        "0000000000000000000000000000000000000000000000000000000000000000";

    public static async Task<int> Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        if (args is [Selector_EmitProductProviderFixture, var outputRoot])
        {
            await EmitProductProviderFixtureAsync(outputRoot);
            Console.WriteLine("PASS product provider fixture emitted");
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("Gate 1 provider binding qualifies and fails closed before output",
                TestProviderBindingQualificationAndRefusal),
            ("Gate 1 invalid Skyrim outfit rolls back the complete output root",
                TestInvalidOutfitRollback),
            ("Gate 1 late undeclared file is detected and preserved during safe rollback",
                TestLateUndeclaredFileSafeRollback),
            ("Exact FaceTint and transitive assets complete through the one build transaction",
                TestExactFaceTintAndTransitiveAssetJourney),
            ("Exact compressed FaceTint evidence preserves a synthetic DXT5 source",
                TestExactCompressedFaceTintEvidence),
            ("Exact and transitive asset preflight refuses drift and destination collisions before output",
                TestAssetPreflightRefusal)
        };
        var passed = 0;
        foreach (var (name, run) in tests)
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

    private static async Task TestProviderBindingQualificationAndRefusal()
    {
        var context = CreateContext();
        var qualified = await context.ProviderService.QualifyAsync(
            context.ProviderRequest, CancellationToken.None);
        Assert(qualified.Qualified && qualified.Artifact is not null,
            "The real Gate 1 provider did not qualify: " + Format(qualified.Diagnostics));
        var artifact = qualified.Artifact ??
            throw new InvalidOperationException("The qualified provider artifact is missing.");
        Assert(artifact.ProviderId == "gate1-emi-qualified-female-v1" &&
               artifact.ExpectedShapeNames.Length == 7 &&
               artifact.TemplateMasters.Length == 5 &&
               artifact.HeadPartCount == 3 &&
               artifact.LooseAssetCount == 9 &&
               artifact.ArchiveCount == 1,
            "The qualified provider identity or dependency inventory drifted.");

        var outputRoot = CreateFreshOutputRoot(context, "provider-binding-mismatch");
        AssertAbsent(outputRoot, "Provider-mismatch output root existed before the test.");
        var request = CreateBuildRequest(
            context,
            outputRoot,
            new PluginName("NpcManagerGate1ProviderMismatch.esp"),
            new EditorId("NPCM_Gate1ProviderMismatch"),
            new FormId(0x0001_DC10)) with
        {
            ExpectedFaceGeomCarrierSha256 = new Sha256Hash(EmptySha256)
        };

        var refused = await context.BuildService.ExecuteAsync(
            request, null, CancellationToken.None);
        Assert(!refused.Completed && refused.Artifact is null,
            "The mismatched provider binding unexpectedly completed a package.");
        Assert(refused.Diagnostics.Any(item => item.Code == "blank-provider-component-mismatch"),
            "The mismatched FaceGeom binding was not refused by the provider boundary: " +
            Format(refused.Diagnostics));
        Assert(refused.NpcCreation is null && refused.FaceGeom is null && refused.FaceTint is null,
            "A downstream writer ran after provider binding refusal.");
        AssertAbsent(outputRoot,
            "Provider binding refusal created an output root before admission completed.");
    }

    private static async Task TestInvalidOutfitRollback()
    {
        var context = CreateContext();
        var outputRoot = CreateFreshOutputRoot(context, "invalid-outfit");
        AssertAbsent(outputRoot, "Invalid-outfit output root existed before the test.");
        var request = CreateBuildRequest(
            context,
            outputRoot,
            new PluginName("NpcManagerGate1InvalidOutfit.esp"),
            new EditorId("NPCM_Gate1InvalidOutfit"),
            new FormId(0x0010_CFF6));

        var refused = await context.BuildService.ExecuteAsync(
            request, null, CancellationToken.None);
        Assert(!refused.Completed && refused.Artifact is null,
            "The non-OTFT Skyrim reference unexpectedly completed a package.");
        Assert(refused.Diagnostics.Any(item =>
                item.Code == "npc-create-default-outfit-signature-mismatch"),
            "Skyrim.esm|0x0010CFF6 was not refused as a non-OTFT record: " +
            Format(refused.Diagnostics));
        Assert(refused.NpcCreation is null && refused.FaceGeom is null &&
               refused.FaceTint is null && refused.PackageVerification is null,
            "The invalid outfit advanced beyond NPC creation analysis.");
        AssertAbsent(outputRoot,
            "The failed full BlankNpcBuildService journey left its owned output root behind.");
    }

    private static async Task TestLateUndeclaredFileSafeRollback()
    {
        const string sentinel = "foreign entry injected after package verification";
        InjectingPackageVerifyService? injectingVerifier = null;
        var context = CreateContext(inner =>
        {
            injectingVerifier = new InjectingPackageVerifyService(inner, sentinel);
            return injectingVerifier;
        });
        var outputRoot = CreateFreshOutputRoot(context, "late-undeclared-file");
        AssertAbsent(outputRoot, "Late-file output root existed before the test.");
        var request = CreateBuildRequest(
            context,
            outputRoot,
            new PluginName("NpcManagerGate1LateFile.esp"),
            new EditorId("NPCM_Gate1LateFile"),
            new FormId(0x0001_DC10));

        var refused = await context.BuildService.ExecuteAsync(
            request, null, CancellationToken.None);

        var verifier = injectingVerifier ??
            throw new InvalidOperationException("The injecting verifier was not composed.");
        var foreignPath = verifier.InjectedPath ??
            throw new InvalidOperationException(
                "The real package verifier did not reach the deterministic late-file injection hook: " +
                Format(refused.Diagnostics));
        Assert(!refused.Completed && refused.Artifact is null,
            "A package with a late undeclared file unexpectedly completed.");
        Assert(refused.PackageVerification is { Verified: true, Artifact: not null },
            "The injection did not occur after a genuine successful package verification.");
        Assert(refused.Diagnostics.Any(item => item.Code == "blank-npc-final-tree-mismatch"),
            "The final package re-enumeration did not reject the late undeclared file: " +
            Format(refused.Diagnostics));
        Assert(refused.Diagnostics.Any(item => item.Code == "blank-npc-rollback-incomplete"),
            "The retained foreign file was not reported as an explicitly incomplete rollback: " +
            Format(refused.Diagnostics));
        Assert(refused.Diagnostics.Any(item => item.Code == "blank-npc-rollback-directory-not-empty"),
            "Rollback did not explicitly report that the unowned entry blocked directory removal: " +
            Format(refused.Diagnostics));

        Assert(File.Exists(foreignPath),
            "Safe rollback wrongly deleted the unowned late file.");
        Assert(string.Equals(await File.ReadAllTextAsync(foreignPath), sentinel,
                StringComparison.Ordinal),
            "Safe rollback changed the unowned late file.");

        var verification = refused.PackageVerification?.Artifact ??
            throw new InvalidOperationException(
                "The successful underlying package verification artifact is missing.");
        foreach (var declared in verification.Files)
        {
            var declaredPath = Path.Combine(outputRoot.Value,
                declared.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar));
            Assert(!File.Exists(declaredPath),
                $"Safe rollback left the owned declared file '{declared.RelativePath.Value}'.");
        }
        Assert(!File.Exists(verification.ManifestPath.Value),
            "Safe rollback left the owned package manifest.");
        var remainingFiles = Directory.EnumerateFiles(
                outputRoot.Value, "*", SearchOption.AllDirectories)
            .Select(Path.GetFullPath)
            .ToArray();
        Assert(remainingFiles.Length == 1 &&
               string.Equals(remainingFiles[0], Path.GetFullPath(foreignPath),
                   StringComparison.OrdinalIgnoreCase),
            "Rollback retained files other than the single unowned sentinel: " +
            string.Join(", ", remainingFiles));

        File.Delete(foreignPath);
        Directory.Delete(outputRoot.Value, recursive: true);
        AssertAbsent(outputRoot, "The regression harness did not clean its own sentinel package root.");
    }

    private static async Task TestExactFaceTintAndTransitiveAssetJourney()
    {
        var context = CreateContext();
        var outputRoot = CreateFreshOutputRoot(context, "exact-facetint-assets");
        var outputPlugin = new PluginName("NpcManagerGate1ExactAssets.esp");
        var exactFaceTint = AcceptedExactFaceTint(context);
        var relocatedRoot = CreateFreshOutputRoot(context,
            "exact-facetint-assets-relocated");
        var request = CreateBuildRequest(
            context,
            outputRoot,
            outputPlugin,
            new EditorId("NPCM_Gate1ExactAssets"),
            new FormId(0x0001_DC10)) with
        {
            FaceTintSource = exactFaceTint,
            TransitivePackageAssets =
            [
                new BlankNpcTransitivePackageAsset(
                    context.ProviderRequest.ManifestPath,
                    new Sha256Hash(ProviderManifestSha256),
                    new AssetPath("SKSE/Plugins/NpcManagerGate1/provider-bundle.json"))
            ]
        };

        try
        {
            var result = await context.BuildService.ExecuteAsync(
                request, null, CancellationToken.None);
            Assert(result.Completed && result.Artifact is not null,
                "The exact-asset journey did not complete: " + Format(result.Diagnostics));
            var walkingProduct = result.Artifact ??
                throw new InvalidOperationException("The completed exact-asset result has no artifact.");
            Assert(result.FaceTint is null,
                "The exact DDS branch fabricated a generated FaceTintBuildResult.");
            Assert(result.FaceTintReadback is
            {
                Decoded: true,
                Width: 2048,
                Height: 2048,
                SourceSha256: not null
            } && result.FaceTintReadback.SourceSha256 == new Sha256Hash(ExactFaceTintSha256),
                "The copied exact DDS did not independently decode and hash as the accepted 2048x2048 fixture.");
            Assert(walkingProduct.FaceTintSha256 == new Sha256Hash(ExactFaceTintSha256),
                "The final walking-product FaceTint hash drifted from the admitted exact source.");

            var verification = result.PackageVerification?.Artifact ??
                throw new InvalidOperationException("The exact-asset package verification is missing.");
            Assert(verification.Files.Any(item =>
                       item.Kind == "facetint-exact-source" && item.Matches) &&
                   verification.Files.Any(item =>
                       item.Kind == "transitive-package-asset-0000" && item.Matches) &&
                   verification.Files.Any(item =>
                       item.Kind == "runtime-diagnostic-batch" && item.Matches),
                "The exact FaceTint evidence, transitive asset, or runtime diagnostic batch " +
                "is absent from the verified package inventory.");
            var diagnosticBatch = verification.Files.Single(item =>
                item.Kind == "runtime-diagnostic-batch");
            Assert(diagnosticBatch.RelativePath.Value.Equals(
                       "Data/diag-npcm_gate1exactassets.txt", StringComparison.OrdinalIgnoreCase),
                "The runtime diagnostic batch is not installed at the Data root under a " +
                "deterministic NPC-specific filename.");
            var diagnosticBatchText = await File.ReadAllTextAsync(Path.Combine(
                outputRoot.Value,
                diagnosticBatch.RelativePath.Value.Replace('/', Path.DirectorySeparatorChar)));
            Assert(diagnosticBatchText.Contains("help \"NPCM_Gate1ExactAssets\" 4 NPC_",
                       StringComparison.Ordinal) &&
                   diagnosticBatchText.Contains("player.placeatme <prefix>000800 1",
                       StringComparison.Ordinal) &&
                   diagnosticBatchText.Contains("getavinfo aggression", StringComparison.Ordinal) &&
                   diagnosticBatchText.Contains("setnpcweight 100", StringComparison.Ordinal) &&
                   diagnosticBatchText.Contains("mfg expression 7 100", StringComparison.Ordinal),
                "The runtime diagnostic batch omitted target resolution or required visual probes.");
            var transitive = verification.Files.Single(item =>
                item.Kind == "transitive-package-asset-0000");
            Assert(transitive.RelativePath.Value.Equals(
                    "Data/SKSE/Plugins/NpcManagerGate1/provider-bundle.json",
                    StringComparison.OrdinalIgnoreCase) &&
                   transitive.ActualSha256 == new Sha256Hash(ProviderManifestSha256) &&
                   transitive.Matches,
                "The transitive package asset destination or independent hash readback is wrong.");

            var evidencePath = Path.Combine(outputRoot.Value, "evidence",
                "facetint-exact-source.json");
            var evidenceJson = await File.ReadAllTextAsync(evidencePath);
            Assert(!evidenceJson.Contains(exactFaceTint.SourceDds.Value,
                       StringComparison.OrdinalIgnoreCase) &&
                   !evidenceJson.Contains(".racemenu-stage-",
                       StringComparison.OrdinalIgnoreCase),
                "Durable FaceTint evidence retained an operational source path.");

            Directory.Move(outputRoot.Value, relocatedRoot.Value);
            var policy = new KOnlyWorkspacePolicy(context.LabRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var relocated = await BlankNpcBuildService.VerifyExactFaceTintEvidenceFileAsync(
                relocatedRoot,
                new AssetPath("evidence/facetint-exact-source.json"),
                policy,
                context.LabRoot,
                new Bgra8FaceTintTextureDecoder(context.LabRoot),
                CancellationToken.None);
            Assert(relocated is
            {
                Verified: true,
                OutputDds: not null,
                SourceSha256: not null,
                OutputSha256: not null,
                Width: 2048,
                Height: 2048
            } &&
                relocated.SourceSha256 == new Sha256Hash(ExactFaceTintSha256) &&
                relocated.OutputSha256 == relocated.SourceSha256 &&
                relocated.OutputDds.Value.IsUnder(relocatedRoot),
                "Package-relative FaceTint evidence did not survive relocation: " +
                Format(relocated.Diagnostics));

            var relocatedEvidence = Path.Combine(relocatedRoot.Value, "evidence",
                "facetint-exact-source.json");
            int closingBrace = evidenceJson.LastIndexOf('}');
            Assert(closingBrace >= 0, "Durable FaceTint evidence has no object terminator.");
            var unknownFieldJson = string.Concat(evidenceJson.AsSpan(0, closingBrace),
                ",\n  \"unknownField\": true\n}");
            await File.WriteAllTextAsync(relocatedEvidence, unknownFieldJson);
            var unknownField = await BlankNpcBuildService.VerifyExactFaceTintEvidenceFileAsync(
                relocatedRoot,
                new AssetPath("evidence/facetint-exact-source.json"),
                policy,
                context.LabRoot,
                new Bgra8FaceTintTextureDecoder(context.LabRoot),
                CancellationToken.None);
            Assert(!unknownField.Verified && unknownField.Diagnostics.Any(item =>
                    item.Code == "blank-npc-exact-facetint-evidence-invalid"),
                "Unknown durable FaceTint evidence fields did not fail closed.");

            var missingRuntimeAuthority = JsonNode.Parse(evidenceJson)?.AsObject() ??
                throw new InvalidOperationException(
                    "Durable FaceTint evidence did not parse as a JSON object.");
            Assert(missingRuntimeAuthority.Remove("runtimeAuthority"),
                "Durable FaceTint evidence omitted the runtimeAuthority field before the test mutation.");
            await File.WriteAllTextAsync(relocatedEvidence,
                missingRuntimeAuthority.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            var missingRequiredField = await BlankNpcBuildService
                .VerifyExactFaceTintEvidenceFileAsync(
                    relocatedRoot,
                    new AssetPath("evidence/facetint-exact-source.json"),
                    policy,
                    context.LabRoot,
                    new Bgra8FaceTintTextureDecoder(context.LabRoot),
                    CancellationToken.None);
            Assert(!missingRequiredField.Verified &&
                   missingRequiredField.Diagnostics.Any(item =>
                       item.Code == "blank-npc-exact-facetint-evidence-invalid"),
                "Missing durable FaceTint evidence fields did not fail closed.");

            var malformedDimensions = JsonNode.Parse(evidenceJson)?.AsObject() ??
                throw new InvalidOperationException(
                    "Durable FaceTint evidence did not parse as a JSON object.");
            malformedDimensions["width"] = 2047;
            await File.WriteAllTextAsync(relocatedEvidence,
                malformedDimensions.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true
                }));
            var malformedLength = await BlankNpcBuildService
                .VerifyExactFaceTintEvidenceFileAsync(
                    relocatedRoot,
                    new AssetPath("evidence/facetint-exact-source.json"),
                    policy,
                    context.LabRoot,
                    new Bgra8FaceTintTextureDecoder(context.LabRoot),
                    CancellationToken.None);
            Assert(!malformedLength.Verified &&
                   malformedLength.Diagnostics.Any(item =>
                       item.Code == "blank-npc-exact-facetint-evidence-readback"),
                "A declared DDS layout with inconsistent decoded dimensions was not rejected.");
        }
        finally
        {
            DeleteTestOutput(outputRoot);
            DeleteTestOutput(relocatedRoot);
        }
    }

    private static async Task TestAssetPreflightRefusal()
    {
        var context = CreateContext();
        var exactFaceTint = AcceptedExactFaceTint(context);

        var hashOutput = CreateFreshOutputRoot(context, "exact-facetint-hash-mismatch");
        var hashRequest = CreateBuildRequest(
            context,
            hashOutput,
            new PluginName("NpcManagerGate1ExactHashMismatch.esp"),
            new EditorId("NPCM_Gate1ExactHashMismatch"),
            new FormId(0x0001_DC10)) with
        {
            FaceTintSource = exactFaceTint with { ExpectedSha256 = new Sha256Hash(EmptySha256) }
        };
        var hashRefusal = await context.BuildService.ExecuteAsync(
            hashRequest, null, CancellationToken.None);
        Assert(!hashRefusal.Completed &&
               hashRefusal.Diagnostics.Any(item => item.Code == "blank-npc-bound-asset-hash"),
            "The drifted exact FaceTint hash was not refused during preflight: " +
            Format(hashRefusal.Diagnostics));
        AssertAbsent(hashOutput, "Exact FaceTint hash refusal created an output root.");

        var duplicateOutput = CreateFreshOutputRoot(context, "transitive-destination-duplicate");
        var duplicateRequest = CreateBuildRequest(
            context,
            duplicateOutput,
            new PluginName("NpcManagerGate1DuplicateAssets.esp"),
            new EditorId("NPCM_Gate1DuplicateAssets"),
            new FormId(0x0001_DC10)) with
        {
            TransitivePackageAssets =
            [
                new BlankNpcTransitivePackageAsset(
                    context.ProviderRequest.ManifestPath,
                    new Sha256Hash(ProviderManifestSha256),
                    new AssetPath("textures/NpcManagerGate1/shared.dds")),
                new BlankNpcTransitivePackageAsset(
                    context.ProviderRequest.ManifestPath,
                    new Sha256Hash(ProviderManifestSha256),
                    new AssetPath("Textures/npcmanagergate1/SHARED.dds"))
            ]
        };
        var duplicateRefusal = await context.BuildService.ExecuteAsync(
            duplicateRequest, null, CancellationToken.None);
        Assert(!duplicateRefusal.Completed && duplicateRefusal.Diagnostics.Any(item =>
                item.Code == "blank-npc-transitive-destination-collision"),
            "Case-insensitive duplicate transitive destinations were not refused: " +
            Format(duplicateRefusal.Diagnostics));
        AssertAbsent(duplicateOutput, "Duplicate destination refusal created an output root.");

        var reservedOutput = CreateFreshOutputRoot(context, "transitive-destination-reserved");
        var reservedPlugin = new PluginName("NpcManagerGate1ReservedAsset.esp");
        var reservedRequest = CreateBuildRequest(
            context,
            reservedOutput,
            reservedPlugin,
            new EditorId("NPCM_Gate1ReservedAsset"),
            new FormId(0x0001_DC10)) with
        {
            TransitivePackageAssets =
            [
                new BlankNpcTransitivePackageAsset(
                    context.ProviderRequest.ManifestPath,
                    new Sha256Hash(ProviderManifestSha256),
                    new AssetPath(
                        $"textures/actors/character/FaceGenData/FaceTint/{reservedPlugin.Value}/00000800.dds"))
            ]
        };
        var reservedRefusal = await context.BuildService.ExecuteAsync(
            reservedRequest, null, CancellationToken.None);
        Assert(!reservedRefusal.Completed && reservedRefusal.Diagnostics.Any(item =>
                item.Code == "blank-npc-transitive-destination-collision"),
            "A transitive asset was allowed to collide with the canonical FaceTint destination: " +
            Format(reservedRefusal.Diagnostics));
        AssertAbsent(reservedOutput, "Reserved destination refusal created an output root.");
    }

    private static async Task TestExactCompressedFaceTintEvidence()
    {
        var context = CreateContext();
        var outputRoot = CreateFreshOutputRoot(context, "exact-facetint-dxt5");
        var source = new WorkspacePath(Path.Combine(
            ActorwrightWorkspace.ResolveRoot().Value,
            "tests",
            "fixtures",
            "skyrim-production",
            "synthetic-dxt5-facetint.dds"));
        var sourceHash = new Sha256Hash(
            "70592CDC13D11A910EC7EAC90743B6D0774A3E9959FB19730961B44FD76A18D9");
        var request = CreateBuildRequest(
            context,
            outputRoot,
            new PluginName("NpcManagerGate1Dxt5.esp"),
            new EditorId("NPCM_Gate1Dxt5"),
            new FormId(0x0001_DC10)) with
        {
            FaceTintSource = new ExactDdsBlankNpcFaceTintSource(
                source, sourceHash, 512, 512)
        };

        try
        {
            var result = await context.BuildService.ExecuteAsync(
                request, null, CancellationToken.None);
            Assert(result.Completed && result.Artifact is not null &&
                   result.Artifact.FaceTintSha256 == sourceHash,
                "The byte-exact synthetic DXT5 FaceTint package was refused: " +
                Format(result.Diagnostics));

            var evidence = await BlankNpcBuildService.VerifyExactFaceTintEvidenceFileAsync(
                outputRoot,
                new AssetPath("evidence/facetint-exact-source.json"),
                new KOnlyWorkspacePolicy(
                    context.LabRoot, new WorkspacePath(@"F:\ExampleGame")),
                context.LabRoot,
                new InProcessDdsTextureDecoder(context.LabRoot),
                CancellationToken.None);
            Assert(evidence is
            {
                Verified: true,
                SourceByteLength: 349680,
                OutputByteLength: 349680,
                Width: 512,
                Height: 512
            } &&
                   evidence.SourceSha256 == sourceHash &&
                   evidence.OutputSha256 == sourceHash,
                "The retained compressed DDS evidence did not rehash and decode: " +
                Format(evidence.Diagnostics));
        }
        finally
        {
            DeleteTestOutput(outputRoot);
        }
    }

    private static ExactDdsBlankNpcFaceTintSource AcceptedExactFaceTint(
        Gate1Context context) =>
        new(
            new WorkspacePath(Path.Combine(
                context.LabRoot.Value,
                "projects",
                "Emi2FreshBuild",
                "03-builds",
                "v0.3-static-candidate",
                "package",
                "textures",
                "actors",
                "character",
                "FaceGenData",
                "FaceTint",
                "EmiRedDossier.esp",
                "00000800.dds")),
            new Sha256Hash(ExactFaceTintSha256),
            2048,
            2048);

    private static void DeleteTestOutput(WorkspacePath outputRoot)
    {
        if (!Directory.Exists(outputRoot.Value)) return;
        var labRoot = FindLabRoot();
        var testRoot = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "NpcManager.Gate1.Tests"));
        if (!outputRoot.IsUnder(testRoot) ||
            !Path.GetFileName(outputRoot.Value).StartsWith(".output-", StringComparison.Ordinal))
            throw new InvalidOperationException(
                $"Refused to clean non-test output '{outputRoot.Value}'.");
        Directory.Delete(outputRoot.Value, recursive: true);
    }

    private static Gate1Context CreateContext(
        Func<IPackageVerifyService, IPackageVerifyService>? decoratePackageVerifier = null)
    {
        var labRoot = FindLabRoot();
        var projectRoot = Path.Combine(
            labRoot.Value, "projects", "NpcManagerReimplementation");
        var carrierData = Path.Combine(
            labRoot.Value,
            "projects",
            "Emi2FreshBuild",
            "03-builds",
            "feasibility-probes",
            "ck-carrier-root",
            "Data");
        var providerManifest = new WorkspacePath(Path.Combine(
            projectRoot,
            "01-source-copies",
            "m5-fixtures",
            "gate1-blank-provider-bundle.json"));
        var templatePlugin = new WorkspacePath(Path.Combine(carrierData, "EmiCarrierProbe.esp"));
        var faceGeomCarrier = new WorkspacePath(Path.Combine(
            carrierData,
            "meshes",
            "Actors",
            "Character",
            "FaceGenData",
            "FaceGeom",
            "EmiCarrierProbe.esp",
            "00000800.NIF"));
        var faceTintManifest = new WorkspacePath(Path.Combine(
            projectRoot,
            "01-source-copies",
            "m5-fixtures",
            "gate1-qualified-facetint.json"));
        var dependencyManifest = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects",
            "Emi2FreshBuild",
            "02-normalized-resources",
            "dependency-manifest.json"));
        var protectedRoot = new WorkspacePath(@"F:\ExampleGame");
        var policy = new KOnlyWorkspacePolicy(labRoot, protectedRoot);
        var providerService = new BlankNpcProviderService(policy, labRoot);
        var providerRequest = new BlankNpcProviderBindingRequest(
            providerManifest,
            new Sha256Hash(ProviderManifestSha256),
            GameEdition.SkyrimSpecialEdition,
            NpcSex.Female,
            templatePlugin,
            new Sha256Hash(TemplatePluginSha256),
            new FormId(0x0000_0800),
            faceGeomCarrier,
            new Sha256Hash(FaceGeomCarrierSha256),
            faceTintManifest,
            new WorkspacePath(carrierData),
            dependencyManifest);

        var texconv = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "tools",
            "external",
            "directxtex-texconv-2026.5.7",
            "texconv.exe"));
        var encoder = new TexconvFaceTintTextureEncoder(
            texconv, labRoot, new Sha256Hash(TexconvSha256));
        var decoder = new TexconvFaceTintTextureDecoder(
            texconv, labRoot, new Sha256Hash(TexconvSha256));
        var faceTint = new FaceTintBuildService(policy, labRoot, encoder, decoder);
        IPackageVerifyService packageVerifier = new PackageVerifyService(
            new PackageManifestReader(policy, labRoot));
        packageVerifier = decoratePackageVerifier?.Invoke(packageVerifier) ?? packageVerifier;
        var buildService = new BlankNpcBuildService(
            NpcCreationComposition.Create(policy, labRoot),
            providerService,
            new QualifiedFaceGeomCarrierService(policy, labRoot),
            faceTint,
            decoder,
            packageVerifier,
            policy,
            labRoot);
        return new Gate1Context(
            labRoot,
            projectRoot,
            providerRequest,
            providerService,
            buildService);
    }

    private static BlankNpcBuildRequest CreateBuildRequest(
        Gate1Context context,
        WorkspacePath outputRoot,
        PluginName outputPlugin,
        EditorId editorId,
        FormId defaultOutfit)
    {
        var provider = context.ProviderRequest;
        var skyrim = new PluginName("Skyrim.esm");
        return new BlankNpcBuildRequest(
            provider.Edition,
            provider.ManifestPath,
            provider.ExpectedManifestSha256,
            provider.TemplatePlugin,
            provider.ExpectedTemplatePluginSha256,
            provider.TemplateNpcFormId,
            provider.FaceGeomCarrier,
            provider.ExpectedFaceGeomCarrierSha256,
            provider.FaceTintManifest,
            provider.FaceTintProviderRoot,
            provider.DependencyManifest,
            outputRoot,
            outputPlugin,
            new NpcCreationIdentity(editorId, new NpcName("Gate 1 regression NPC")),
            new SkyrimNpcCreationTraits(
                NpcSex.Female,
                NpcCreationRole.StaticValidation,
                true,
                false,
                false,
                false,
                true),
            new SkyrimNpcCreationReferences(
                new FormReference(skyrim, new FormId(0x0001_3746)),
                new FormReference(skyrim, new FormId(0x0001_3ADC)),
                new FormReference(skyrim, new FormId(0x0001_3181)),
                new FormReference(skyrim, new FormId(0x0003_BE1D)),
                new FormReference(skyrim, defaultOutfit)),
            TemplateCarrierNpcAppearanceSource.Instance,
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0,
                0,
                0,
                1,
                1,
                100,
                35,
                0,
                50,
                50,
                50,
                1f,
                0f,
                255));
    }

    private static WorkspacePath CreateFreshOutputRoot(
        Gate1Context context,
        string scenario)
    {
        var suffix = Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        return new WorkspacePath(Path.Combine(
            context.ProjectRoot,
            "tests",
            "NpcManager.Gate1.Tests",
            $".output-{scenario}-{suffix}"));
    }

    private static WorkspacePath FindLabRoot() =>
        new(TestAuthorityWorkspace.ResolveLabRoot(
            AppContext.BaseDirectory));

    private static void AssertAbsent(WorkspacePath path, string message) =>
        Assert(!Directory.Exists(path.Value) && !File.Exists(path.Value),
            $"{message} Path: {path.Value}");

    private static string Format(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}: {item.Message}"));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record Gate1Context(
        WorkspacePath LabRoot,
        string ProjectRoot,
        BlankNpcProviderBindingRequest ProviderRequest,
        IBlankNpcProviderService ProviderService,
        IBlankNpcBuildService BuildService);

    private sealed class InjectingPackageVerifyService(
        IPackageVerifyService inner,
        string sentinel) : IPackageVerifyService
    {
        public string? InjectedPath { get; private set; }

        public async ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken)
        {
            var result = await inner.VerifyAsync(request, cancellationToken);
            if (!result.Verified || result.Artifact is null) return result;

            var packageRoot = Path.GetDirectoryName(request.ManifestPath.Value) ??
                throw new InvalidOperationException("The verified package manifest has no parent directory.");
            var foreignPath = Path.Combine(packageRoot, "foreign.txt");
            await using (var stream = new FileStream(
                             foreignPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            await using (var writer = new StreamWriter(stream))
            {
                await writer.WriteAsync(sentinel);
                await writer.FlushAsync(cancellationToken);
            }
            InjectedPath = foreignPath;
            return result;
        }
    }
}
