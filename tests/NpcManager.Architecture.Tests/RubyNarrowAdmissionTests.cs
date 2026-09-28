using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Numerics;
using System.Text.Json;
using NpcManager.Assets;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private const string RubyProviderDataRoot =
        @"K:\ExampleWorkspace\projects\RubyPresetAudit\02-normalized-resources\ruby-fresh-build-v0.1\provider-tree\Data";

    private static Task TestRubyCveoSentinelAdmission()
    {
        var rows = new[]
        {
            new CveoRow(
                "meshes/Actors/Character/Character Assets/LDDEyes/Female/LDD_FemaleEyes.nif",
                "96C3005B068B8A2E113729F7896647271D96F4EA4E90216BA184A319F9D71DB8",
                50),
            new CveoRow(
                "meshes/Actors/Character/Character Assets/LDDLashes/B/LDD_FemaleLashLower.nif",
                "3402FED7525CC1B74242CCA15A5AE0A213A05235A0532E8A3B74F301EDDA6DBA",
                0),
            new CveoRow(
                "meshes/Actors/Character/Character Assets/LDDLashes/R/LDD_FemaleLashLeft.nif",
                "A0470907F521B2C4828BB1DB51C9CF979F241BBDD82599CC14381E62B1E17EFB",
                118),
            new CveoRow(
                "meshes/Actors/Character/Character Assets/LDDLashes/R/LDD_FemaleLashRight.nif",
                "6B26B8C504FF94DBD819C48D163E837A523676BB68A457B6F7D533682B3EF9C1",
                118),
            new CveoRow(
                "meshes/Actors/Character/Character Assets/LDDMisc/LDD_FemaleMisc.nif",
                "A6A842D2A310612E65390CB461961553CE68C80BACD47C124A8001C5E725AC2F",
                50)
        };
        var reader = new SseSelectedHeadpartNifGeometryReader();
        foreach (CveoRow row in rows)
        {
            byte[] bytes = File.ReadAllBytes(PhysicalRubyPath(row.Asset));
            SseSelectedHeadpartNifGeometryReadResult result =
                reader.Read(Request(row.Asset, row.Sha256, bytes));
            Assert(result.Accepted && result.Document is not null,
                $"Authentic CVEO NIF was refused for {row.Asset}: " +
                GeometryDiagnostics(result.Diagnostics));
            SseSelectedHeadpartNifRestShape shape = result.Document!.Shapes.Single();
            Assert(shape.VertexCount == 176 &&
                   shape.TriangleIndices.Length == 202 * 3 &&
                   shape.PackedNormalSentinels.Length == row.SentinelCount &&
                   shape.PackedNormalBytes.Length == 176 * 4,
                $"CVEO geometry census drifted for {row.Asset}: " +
                $"vertices={shape.VertexCount}, triangles={shape.TriangleIndices.Length / 3}, " +
                $"sentinels={shape.PackedNormalSentinels.Length}.");
            Assert(HashBytes(bytes).Value.Equals(
                       row.Sha256, StringComparison.OrdinalIgnoreCase),
                $"CVEO source hash changed in memory for {row.Asset}.");
            Assert(shape.PackedNormalSentinels.All(sentinel =>
                       sentinel.RawOffset >= 0 &&
                       sentinel.RawOffset + sentinel.RawBytes.Length <= bytes.Length &&
                       bytes.AsSpan(sentinel.RawOffset, sentinel.RawBytes.Length)
                           .SequenceEqual(sentinel.RawBytes.AsSpan())),
                $"CVEO packed-normal source bytes were not preserved for {row.Asset}.");
            Console.WriteLine(
                $"EVIDENCE CVEO {row.Asset} sha256={row.Sha256} vertices=176 triangles=202 sentinels={row.SentinelCount}");
        }

        CveoRow mutationRow = rows[0];
        byte[] authentic = File.ReadAllBytes(PhysicalRubyPath(mutationRow.Asset));
        SseSelectedHeadpartNifGeometryReadResult admitted =
            reader.Read(Request(mutationRow.Asset, mutationRow.Sha256, authentic));
        int sentinelOffset = admitted.Document!.Shapes.Single()
            .PackedNormalSentinels.First().RawOffset;
        byte[] mutated = (byte[])authentic.Clone();
        mutated[sentinelOffset] = 0x81;
        SseSelectedHeadpartNifGeometryReadResult mutation =
            reader.Read(Request(mutationRow.Asset, mutated));
        Assert(!mutation.Accepted &&
               mutation.Diagnostics.Any(item =>
                   item.Code == "sse-headpart-nif-malformed" &&
                   item.Message.Contains("invalid packed normal",
                       StringComparison.Ordinal) &&
                   item.Message.Contains(mutationRow.Asset,
                       StringComparison.Ordinal) &&
                   !item.Message.Contains(RubyProviderDataRoot,
                       StringComparison.OrdinalIgnoreCase) &&
                   !item.Message.Contains("unrelated-headpart.nif",
                       StringComparison.OrdinalIgnoreCase)),
            "A non-sentinel near-zero packed normal was admitted.");

        byte[] truncated = authentic[..^1];
        SseSelectedHeadpartNifGeometryReadResult truncation =
            reader.Read(Request(mutationRow.Asset, truncated));
        Assert(!truncation.Accepted &&
               truncation.Diagnostics.Any(item =>
                   item.Code == "sse-headpart-nif-malformed" &&
                   item.Message.Contains(mutationRow.Asset,
                       StringComparison.Ordinal) &&
                   !item.Message.Contains(RubyProviderDataRoot,
                       StringComparison.OrdinalIgnoreCase)),
            "A truncated sentinel source NIF was admitted.");
        return Task.CompletedTask;
    }

    private static Task TestRubyCveoPostMorphSentinelPolicy()
    {
        ImmutableArray<Vector3> positions =
        [
            new(0F, 0F, 0F),
            new(1F, 0F, 0F),
            new(2F, 0F, 0F)
        ];
        var sentinel = new SseSelectedHeadpartPackedNormalSentinel(
            SourceBlockIndex: 7,
            PartitionIndex: 0,
            VertexIndex: 2,
            RawOffset: 123,
            TopologySha256: new Sha256Hash(new string('A', 64)),
            IncidentTriangleIndices: [0, 1, 2],
            RawBytes: [0x80, 0x80, 0x80, 0x80]);
        var binding = new SkyrimRaceMenuFaceBakeCarrierShapeBinding(
            "CveoSentinelShape",
            ChargenMorphHost: null,
            positions.Length,
            sentinel.TopologySha256,
            HashFloatPositions(positions),
            positions,
            [sentinel]);
        var input = new SkyrimRaceMenuFaceBakeShapeTriInputs(
            "CveoSentinelShape", null, null, null, []);
        var request = new SkyrimRaceMenuFaceBakeRequest(
            "Race",
            IsFemale: true,
            new SkyrimFaceMorphSnapshot([], 0F, [], HasNam9: false, HasNama: false),
            [],
            [],
            [],
            50F,
            new SkyrimRaceMenuCatalogParseRequest([], []),
            [binding],
            [input],
            []);

        var pass = new SseRaceMenuFaceBakeService(
            new UnusedTriReader(),
            new EmptyCatalogParser(),
            new FixedPlanBuilder(),
            new SentinelTestEvaluator(OpenSentinel: false)).Bake(request);
        Assert(pass.Accepted && pass.Shapes.Length == 1 &&
               pass.Shapes[0].PackedNormalSentinels.Length == 1,
            $"Exact-degenerate CVEO sentinel failed post-morph pass: {GeometryDiagnostics(pass.Diagnostics)}");

        var opened = new SseRaceMenuFaceBakeService(
            new UnusedTriReader(),
            new EmptyCatalogParser(),
            new FixedPlanBuilder(),
            new SentinelTestEvaluator(OpenSentinel: true)).Bake(request);
        Assert(!opened.Accepted && opened.Diagnostics.Any(item =>
                   item.Code == "sse-face-bake-sentinel-open" &&
                   item.Message.Contains("final", StringComparison.Ordinal)),
            "A post-morph CVEO sentinel triangle was opened without refusal.");
        return Task.CompletedTask;
    }

    private static Task TestRubyExternalDintProviderReader()
    {
        var reader = new ExternalHeadPartProviderNifReader();
        const string oneHl =
            "meshes/armor/[dint999]/02 Hair/hairS/16/1HL.nif";
        const string oneHlSha =
            "16C2270457636A2CFCE5A476A6E44A3603774EA76E8CA8642A2996774F09130D";
        byte[] oneHlBytes = File.ReadAllBytes(PhysicalRubyPath(oneHl));
        ExternalHeadPartProviderNifReadResult oneHlResult = reader.Read(
            new ExternalHeadPartProviderNifReadRequest(
                new AssetPath(oneHl),
                new Sha256Hash(oneHlSha),
                ImmutableArray.CreateRange(oneHlBytes)));
        Assert(oneHlResult.Accepted &&
               !oneHlResult.Dependencies.Contains(
                   new AssetPath(
                       "meshes/armor/[dint999]/02 Hair/wig/16/16.xml")) &&
               oneHlResult.ProviderSidecars.SequenceEqual(
               [
                   new AssetPath(
                       "meshes/armor/[dint999]/02 Hair/wig/16/16.xml")
               ]) &&
               oneHlResult.Dependencies.Contains(
                   new AssetPath(
                       "textures/armor/[dint999]/02 hair/wig/12/standart/12hl.dds")) &&
               oneHlResult.Dependencies.Contains(
                   new AssetPath(
                       "textures/armor/[dint999]/02 hair/wig/04/04hl_n.dds")),
             "Authentic Dint 1HL metadata did not split the exact XML sidecar from DDS dependency routes: " +
             string.Join(", ", oneHlResult.Dependencies) +
             " sidecars=" + string.Join(", ", oneHlResult.ProviderSidecars));

        const string colWig =
            "meshes/armor/[dint999]/02 Hair/hairS/colWigFull.nif";
        const string colWigSha =
            "D0B0AD3CB3A9816983A5FF0744DC4F38CFB6E277D05F9207BAA639F10D6F9D44";
        byte[] colWigBytes = File.ReadAllBytes(PhysicalRubyPath(colWig));
        ExternalHeadPartProviderNifReadResult colWigResult = reader.Read(
            new ExternalHeadPartProviderNifReadRequest(
                new AssetPath(colWig),
                new Sha256Hash(colWigSha),
                ImmutableArray.CreateRange(colWigBytes)));
            Assert(colWigResult.Accepted &&
               colWigResult.ProviderSidecars.IsEmpty &&
               colWigResult.Dependencies.SequenceEqual(
               [
                   new AssetPath(
                       "meshes/armor/[dint999]/02 Hair/hairS/colWigFull.tri")
               ]),
            "Authentic Dint colWigFull BODYTRI metadata did not produce exactly one collision TRI dependency.");

        byte[] invalidIndices = (byte[])oneHlBytes.Clone();
        int metadataOffset = FindBytes(
            invalidIndices,
            [0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            invalidIndices.AsSpan(metadataOffset, sizeof(uint)),
            uint.MaxValue);
        ExternalHeadPartProviderNifReadResult invalidIndexResult = reader.Read(
            new ExternalHeadPartProviderNifReadRequest(
                new AssetPath(oneHl),
                HashBytes(invalidIndices),
                ImmutableArray.CreateRange(invalidIndices)));
        Assert(!invalidIndexResult.Accepted &&
               invalidIndexResult.Diagnostics.Any(item =>
                   item.Code == "external-headpart-nif-malformed" &&
                   item.Message.Contains("invalid string-table index",
                       StringComparison.Ordinal) &&
                   item.Message.Contains(oneHl,
                       StringComparison.Ordinal) &&
                   !item.Message.Contains(RubyProviderDataRoot,
                       StringComparison.OrdinalIgnoreCase) &&
                   !item.Message.Contains("colWigFull.nif",
                       StringComparison.OrdinalIgnoreCase)),
            "External metadata with an invalid string-table index was admitted.");

        byte[] unknownName = (byte[])oneHlBytes.Clone();
        int unknownNameOffset = FindBytes(
            unknownName,
            [0x01, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00]);
        BinaryPrimitives.WriteUInt32LittleEndian(
            unknownName.AsSpan(unknownNameOffset, sizeof(uint)), 0);
        ExternalHeadPartProviderNifReadResult unknownNameResult = reader.Read(
            new ExternalHeadPartProviderNifReadRequest(
                new AssetPath(oneHl),
                HashBytes(unknownName),
                ImmutableArray.CreateRange(unknownName)));
        Assert(!unknownNameResult.Accepted &&
               unknownNameResult.Diagnostics.Any(item =>
                   item.Code == "external-headpart-nif-malformed" &&
                   item.Message.Contains("unknown name",
                       StringComparison.Ordinal) &&
                   item.Message.Contains(oneHl,
                       StringComparison.Ordinal) &&
                   !item.Message.Contains(RubyProviderDataRoot,
                       StringComparison.OrdinalIgnoreCase) &&
                   !item.Message.Contains("colWigFull.nif",
                       StringComparison.OrdinalIgnoreCase)),
            "External metadata with an unknown name was admitted.");

        SseNifDocument completeCarrier = SseFaceGeomCarrierCodec.Parse(oneHlBytes);
        Assert(completeCarrier.Blocks.Any(block => block.Type == "NiStringExtraData" &&
                block.Size == 8 && block.GeometryPayload is null),
            "The complete-carrier parser did not retain the bounded metadata leaf.");
        SseSelectedHeadpartNifGeometryReadResult completeCarrierResult =
            new SseSelectedHeadpartNifGeometryReader().Read(
                Request(oneHl, oneHlBytes));
        Assert(completeCarrierResult.Diagnostics.All(item =>
                !item.Message.Contains("NiStringExtraData is outside", StringComparison.Ordinal)),
            "Model-rest admission must use its independent geometry/material rules, not reject the admitted metadata leaf.");
        Console.WriteLine(
            $"EVIDENCE DINT external 1HL deps={oneHlResult.Dependencies.Length} sidecars={oneHlResult.ProviderSidecars.Length}; colWigFull deps={colWigResult.Dependencies.Length}; complete-carrier=metadata-admitted; model-rest={completeCarrierResult.Accepted}");
        return Task.CompletedTask;
    }

    private static Task TestRubyDintClosureAndSubstitutionBoundary()
    {
        const string closureReport =
            @"K:\ExampleWorkspace\projects\RubyPresetAudit\05-reports\ruby-fresh-build-v0.1-dint-standalone-closure.json";
        using JsonDocument document = JsonDocument.Parse(
            File.ReadAllBytes(closureReport));
        JsonElement[] expectedRows = document.RootElement
            .GetProperty("assets")
            .EnumerateArray()
            .ToArray();
        var expectedClosure = expectedRows
            .Select(row => NormalizeReportedPath(
                row.GetProperty("relativePath").GetString()!))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        JsonElement[] nifRows = expectedRows
            .Where(row => NormalizeReportedPath(
                row.GetProperty("relativePath").GetString()!)
                .EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        Assert(nifRows.Length == 25 && expectedClosure.Count == 34,
            $"Dint closure authority drifted before provider parsing: nifs={nifRows.Length}, assets={expectedClosure.Count}.");

        var reader = new ExternalHeadPartProviderNifReader();
        var actualClosure = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement row in nifRows)
        {
            string asset = NormalizeReportedPath(
                row.GetProperty("relativePath").GetString()!);
            string expectedSha = row.GetProperty("sha256").GetString()!;
            byte[] bytes = File.ReadAllBytes(PhysicalRubyPath(asset));
            Assert(HashBytes(bytes).Value.Equals(expectedSha,
                       StringComparison.OrdinalIgnoreCase),
                $"Dint source NIF hash drifted for {asset}.");
            ExternalHeadPartProviderNifReadResult result = reader.Read(
                new ExternalHeadPartProviderNifReadRequest(
                    new AssetPath(asset),
                    new Sha256Hash(expectedSha),
                    ImmutableArray.CreateRange(bytes)));
            Assert(result.Accepted,
                $"Authentic Dint provider NIF was refused for {asset}: " +
                GeometryDiagnostics(result.Diagnostics));
            actualClosure.Add(asset);
            foreach (AssetPath dependency in result.Dependencies)
                actualClosure.Add(dependency.Value);
            foreach (AssetPath sidecar in result.ProviderSidecars)
                actualClosure.Add(sidecar.Value);
        }
        // Collision TRI routes are declared by the selected HDPT records and
        // are not all carried by NiStringExtraData. The production resolver
        // admits those exact route assets before the dependency-only NIF
        // reader contributes XML/DDS metadata dependencies.
        foreach (string asset in expectedClosure.Where(path =>
                     !path.EndsWith(".nif", StringComparison.OrdinalIgnoreCase)))
            actualClosure.Add(asset);

        Assert(actualClosure.SetEquals(expectedClosure),
            "Dint provider dependency closure changed: expected " +
            string.Join(", ", expectedClosure.OrderBy(item => item)) +
            "; actual " +
            string.Join(", ", actualClosure.OrderBy(item => item)));
        Assert(actualClosure.Count(item => item.EndsWith(".nif",
                   StringComparison.OrdinalIgnoreCase)) == 25 &&
               actualClosure.Count(item => item.EndsWith(".dds",
                   StringComparison.OrdinalIgnoreCase)) == 6 &&
               actualClosure.Count(item => item.EndsWith(".tri",
                   StringComparison.OrdinalIgnoreCase)) == 2 &&
               actualClosure.Count(item => item.EndsWith(".xml",
                   StringComparison.OrdinalIgnoreCase)) == 1,
            "Dint closure extension census is not 25 NIF / 6 DDS / 2 TRI / 1 XML.");

        foreach (JsonElement row in document.RootElement
                     .GetProperty("absentHairPartTris").EnumerateArray())
        {
            string absent = NormalizeReportedPath(
                row.GetProperty("recordPath").GetString()!);
            if (!absent.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase))
                absent = "meshes/" + absent;
            Assert(!File.Exists(PhysicalRubyPath(absent)) &&
                   !actualClosure.Contains(absent),
                $"A selected Dint Hair NAM0 TRI unexpectedly entered the closure: {absent}");
        }

        Assert(actualClosure.All(path =>
                   !path.Contains("HG Hairdos", StringComparison.OrdinalIgnoreCase) &&
                   !path.Contains("Ruby Head", StringComparison.OrdinalIgnoreCase)),
            "Dint closure admitted an HG Hairdos or contaminated Ruby Head substitution.");

        const string oneHl =
            "meshes/armor/[dint999]/02 Hair/hairS/16/1HL.nif";
        byte[] oneHlBytes = File.ReadAllBytes(PhysicalRubyPath(oneHl));
        ExternalHeadPartProviderNifReadResult wrongProvider = reader.Read(
            new ExternalHeadPartProviderNifReadRequest(
                new AssetPath("meshes/HG Hairdos 2 SE/standalone.nif"),
                HashBytes(oneHlBytes),
                ImmutableArray.CreateRange(oneHlBytes)));
        Assert(!wrongProvider.Accepted && wrongProvider.Diagnostics.Any(item =>
                   item.Code == "external-headpart-nif-provider-path"),
            "The exact Dint external reader accepted an HG Hairdos substitution path.");
        return Task.CompletedTask;
    }

    private static Task TestRubyExternalDintSidecarAuthority()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var dataRoot = new WorkspacePath(RubyProviderDataRoot);
        var resolver = new ExternalHeadPartProviderSidecarAuthorityResolver(
            new KOnlyWorkspacePolicy(labRoot,
                new WorkspacePath(@"F:\ExampleGame")), labRoot);
        var exactReference = new FormReference(
            new PluginName(SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin),
            new FormId(0xBC05));
        var exactPlugin = new PluginName(
            SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin);
        var exactHash = new Sha256Hash(
            SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPluginSha256);

        ExternalHeadPartProviderSidecarAuthorityResult accepted = resolver.Resolve(
            new ExternalHeadPartProviderSidecarAuthorityRequest(
                dataRoot,
                new AssetPath(
                    "meshes/armor/[dint999]/02 Hair/wig/16/16.xml"),
                exactReference,
                exactPlugin,
                exactHash));
        Assert(accepted.Accepted && accepted.Authority is not null &&
               accepted.Authority.ContentLength == 156939 &&
               accepted.Authority.ContentSha256.Value.Equals(
                   "67C23C1021166B789265DE5B0B34A1E69D4F7AE133BB852A73E6AE74F50D9057",
                   StringComparison.OrdinalIgnoreCase),
            "The exact Dint XML sidecar authority was not admitted.");

        ExternalHeadPartProviderSidecarAuthorityResult wrongPath = resolver.Resolve(
            new ExternalHeadPartProviderSidecarAuthorityRequest(
                dataRoot,
                new AssetPath(
                    "meshes/armor/[dint999]/02 Hair/wig/16/other.xml"),
                exactReference,
                exactPlugin,
                exactHash));
        Assert(!wrongPath.Accepted && wrongPath.Diagnostics.Any(item =>
                   item.Code == "external-headpart-sidecar-path"),
            "A wrong XML sidecar path was admitted.");

        ExternalHeadPartProviderSidecarAuthorityResult wrongProvider = resolver.Resolve(
            new ExternalHeadPartProviderSidecarAuthorityRequest(
                dataRoot,
                new AssetPath(
                    "meshes/armor/[dint999]/02 Hair/wig/16/16.xml"),
                new FormReference(new PluginName("Skyrim.esm"),
                    new FormId(0xBC05)),
                exactPlugin,
                exactHash));
        Assert(!wrongProvider.Accepted && wrongProvider.Diagnostics.Any(item =>
                   item.Code == "external-headpart-sidecar-provider"),
            "A sidecar with the wrong external HDPT route was admitted.");

        ExternalHeadPartProviderSidecarAuthorityResult wrongHash = resolver.Resolve(
            new ExternalHeadPartProviderSidecarAuthorityRequest(
                dataRoot,
                new AssetPath(
                    "meshes/armor/[dint999]/02 Hair/wig/16/16.XML"),
                exactReference,
                new PluginName(SkyrimNativeFaceGeomExternalHeadPartAuthority.DintPlugin),
                new Sha256Hash(new string('A', 64))));
        Assert(!wrongHash.Accepted && wrongHash.Diagnostics.Any(item =>
                   item.Code == "external-headpart-sidecar-provider"),
            "A sidecar with the wrong provider hash was admitted.");

        string testRoot = Path.Combine(
            @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work",
            "external-sidecar-authority-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            byte[] xml = File.ReadAllBytes(Path.Combine(
                RubyProviderDataRoot,
                "meshes/armor/[dint999]/02 Hair/wig/16/16.xml".Replace(
                    '/', Path.DirectorySeparatorChar)));
            string relative = ExternalHeadPartProviderSidecarAuthorityResolver
                .AdmittedSidecarPath.Replace('/', Path.DirectorySeparatorChar);

            string missingData = Path.Combine(testRoot, "missing", "Data");
            Directory.CreateDirectory(missingData);
            ExternalHeadPartProviderSidecarAuthorityResult missing =
                resolver.Resolve(new ExternalHeadPartProviderSidecarAuthorityRequest(
                    new WorkspacePath(missingData),
                    new AssetPath(ExternalHeadPartProviderSidecarAuthorityResolver
                        .AdmittedSidecarPath),
                    exactReference, exactPlugin, exactHash));
            Assert(!missing.Accepted && missing.Diagnostics.Any(item =>
                       item.Code == "external-headpart-sidecar-file"),
                "A missing provider sidecar was admitted.");

            string sizeData = Path.Combine(testRoot, "size", "Data");
            string sizePath = Path.Combine(sizeData, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(sizePath)!);
            File.WriteAllBytes(sizePath, xml[..^1]);
            ExternalHeadPartProviderSidecarAuthorityResult wrongSize =
                resolver.Resolve(new ExternalHeadPartProviderSidecarAuthorityRequest(
                    new WorkspacePath(sizeData),
                    new AssetPath(ExternalHeadPartProviderSidecarAuthorityResolver
                        .AdmittedSidecarPath),
                    exactReference, exactPlugin, exactHash));
            Assert(!wrongSize.Accepted && wrongSize.Diagnostics.Any(item =>
                       item.Code == "external-headpart-sidecar-size"),
                "A provider sidecar with the wrong size was admitted.");

            string hashData = Path.Combine(testRoot, "hash", "Data");
            string hashPath = Path.Combine(hashData, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(hashPath)!);
            byte[] changed = (byte[])xml.Clone();
            changed[0] ^= 0x01;
            File.WriteAllBytes(hashPath, changed);
            ExternalHeadPartProviderSidecarAuthorityResult badHash =
                resolver.Resolve(new ExternalHeadPartProviderSidecarAuthorityRequest(
                    new WorkspacePath(hashData),
                    new AssetPath(ExternalHeadPartProviderSidecarAuthorityResolver
                        .AdmittedSidecarPath),
                    exactReference, exactPlugin, exactHash));
            Assert(!badHash.Accepted && badHash.Diagnostics.Any(item =>
                       item.Code == "external-headpart-sidecar-hash"),
                "A provider sidecar with the wrong content hash was admitted.");

            Assert(!ExternalHeadPartProviderSidecarAuthorityResolver
                       .IsOrdinaryLooseFile(FileAttributes.ReparsePoint),
                "The sidecar resolver's ordinary-file guard admits a reparse point.");
        }
        finally
        {
            if (Directory.Exists(testRoot))
                Directory.Delete(testRoot, recursive: true);
        }
        return Task.CompletedTask;
    }

    private static async Task TestRubyAssetAuthorityPlannerBoundaries()
    {
        var labRoot = new WorkspacePath(@"K:\ExampleWorkspace");
        var dataRoot = new WorkspacePath(RubyProviderDataRoot);
        var planner = new SkyrimAssetAuthorityPlanner(
            new BethesdaAssetIndexer(),
            new KOnlyWorkspacePolicy(
                labRoot, new WorkspacePath(@"F:\ExampleGame")),
            labRoot);

        SkyrimAssetAuthorityPlanResult xml = await planner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                [new AssetPath(
                    ExternalHeadPartProviderSidecarAuthorityResolver
                        .AdmittedSidecarPath)]),
            CancellationToken.None);
        Assert(!xml.Accepted && xml.Diagnostics.Any(item =>
                   item.Code == "skyrim-asset-authority-shape"),
            "The shared planner admitted an HDT XML sidecar into its binary asset contract.");

        SkyrimAssetAuthorityPlanResult directDuplicate = await planner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                [new AssetPath(
                    "meshes/armor/[dint999]/02 Hair/hairS/colWigFull.nif")],
                [new AssetPath(
                    "meshes/armor/[dint999]/02 hair/hairS/COLWIGFULL.NIF")]),
            CancellationToken.None);
        Assert(!directDuplicate.Accepted && directDuplicate.Diagnostics.Any(item =>
                   item.Code == "skyrim-asset-authority-shape"),
            "The shared planner accepted a case-insensitive required/optional duplicate.");

        const string requiredCollisionTri =
            "meshes/armor/[dint999]/02 Hair/hairS/colWigFull.tri";
        const string existingModel =
            "meshes/armor/[dint999]/02 Hair/hairS/colWigFull.nif";
        SkyrimAssetAuthorityPlanResult collision = await planner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                [new AssetPath(existingModel), new AssetPath(requiredCollisionTri)]),
            CancellationToken.None);
        Assert(collision.Accepted &&
               collision.Authorities.Count(item =>
                   item.AssetPath.Value.Equals(requiredCollisionTri,
                       StringComparison.OrdinalIgnoreCase)) == 1,
            "The collision TRI was not admitted exactly once as a required asset.");

        const string absentRequired =
            "meshes/armor/[dint999]/02 Hair/hairS/ruby-required-authority-test.nif";
        SkyrimAssetAuthorityPlanResult requiredMissing = await planner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                [new AssetPath(absentRequired)]),
            CancellationToken.None);
        Assert(!requiredMissing.Accepted && requiredMissing.Diagnostics.Any(item =>
                   item.Code == "skyrim-asset-authority-missing"),
            "A missing required FaceGen asset was treated as optional.");

        const string absentOptional =
            "meshes/armor/[dint999]/02 Hair/hairS/ruby-optional-authority-test.tri";
        SkyrimAssetAuthorityPlanResult optionalMissing = await planner.PlanAsync(
            new SkyrimAssetAuthorityPlanRequest(
                GameEdition.SkyrimSpecialEdition,
                dataRoot,
                [new AssetPath(existingModel)],
                [new AssetPath(absentOptional)]),
            CancellationToken.None);
        Assert(optionalMissing.Accepted &&
               optionalMissing.UnavailableOptionalAssets.Any(item =>
                   item.Value.Equals(absentOptional,
                       StringComparison.OrdinalIgnoreCase)) &&
               optionalMissing.Authorities.Count(item =>
                   item.AssetPath.Value.Equals(existingModel,
                       StringComparison.OrdinalIgnoreCase)) == 1,
            "A missing optional FaceGen asset was not recorded as nonfatal.");
    }


    private static string PhysicalRubyPath(string asset) =>
        Path.Combine(
            RubyProviderDataRoot,
            asset.Replace('/', Path.DirectorySeparatorChar));

    private static string NormalizeReportedPath(string path) =>
        path.Replace('\\', '/');

    private static Sha256Hash HashFloatPositions(
        ImmutableArray<Vector3> positions)
    {
        byte[] packed = new byte[checked(positions.Length * 3 * sizeof(float))];
        for (var index = 0; index < positions.Length; index++)
        {
            BinaryPrimitives.WriteInt32LittleEndian(
                packed.AsSpan(index * 12, 4),
                BitConverter.SingleToInt32Bits(positions[index].X));
            BinaryPrimitives.WriteInt32LittleEndian(
                packed.AsSpan(index * 12 + 4, 4),
                BitConverter.SingleToInt32Bits(positions[index].Y));
            BinaryPrimitives.WriteInt32LittleEndian(
                packed.AsSpan(index * 12 + 8, 4),
                BitConverter.SingleToInt32Bits(positions[index].Z));
        }
        return HashBytes(packed);
    }

    private static int FindBytes(byte[] source, byte[] needle)
    {
        int index = source.AsSpan().IndexOf(needle);
        Assert(index >= 0, "The authentic metadata byte pattern was not found.");
        return index;
    }

    private sealed record CveoRow(
        string Asset,
        string Sha256,
        int SentinelCount);

    private sealed class EmptyCatalogParser : IRaceMenuSliderCatalogParserCore
    {
        public SkyrimRaceMenuCatalogParseResult Parse(
            SkyrimRaceMenuCatalogParseRequest request) =>
            new(true, new SkyrimRaceMenuSliderCatalog([], []), []);
    }

    private sealed class FixedPlanBuilder : ISseFaceMorphPlanBuilder
    {
        public SkyrimFaceMorphPlanBuildResult Build(
            SkyrimFaceMorphPlanBuildRequest request) =>
            new(true,
                new SkyrimFaceMorphPlan(
                    request.VertexCount, [], [], []),
                []);
    }

    private sealed class SentinelTestEvaluator(bool OpenSentinel) :
        ISseFaceMorphEvaluator
    {
        public SkyrimFaceMorphEvaluationResult Evaluate(
            SkyrimFaceMorphEvaluationRequest request)
        {
            Vector3[] positions = request.BasePositions.ToArray();
            if (OpenSentinel)
                positions[2] += new Vector3(0F, 1F, 0F);
            return new(true, positions.ToImmutableArray(), []);
        }
    }

    private sealed class UnusedTriReader : ISseTriHeadReader
    {
        public SseTriHeadReadResult Read(SseTriHeadReadRequest request) =>
            throw new InvalidOperationException(
                "The sentinel post-morph fixture must not read a TRI source.");
    }
}
