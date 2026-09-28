using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Architecture.Tests;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;

namespace NpcManager.Assets.Tests;

internal static partial class Program
{
    private const string OneByteHash =
        "559aead08264d5795d3909718cdd05abd49572e84fe55590eef31a88a08fdffd";
    private const string EmiAuthorityHash =
        "dfec412d1b1dcda6667c66e85fe3a18f1a446adc109bf74af59b5377a549c1f5";
    private static readonly WorkspacePath TestLabRoot = new(@"K:\ExampleWorkspace");

    private static async Task TestStrictFaceBakeAuthority()
    {
        using TestAuthorityDirectory temp = TestAuthorityDirectory.Create();
        string manifestText = SyntheticManifest();
        WorkspacePath manifest = temp.WriteManifest("valid.json", manifestText);
        RecordingContentResolver resolver = new(reverseOutput: true);
        SkyrimFaceBakeAuthorityLoader loader = AuthorityLoader(resolver);

        SkyrimFaceBakeAuthorityLoadResult result = await loader.LoadAsync(
            Request(manifest, Hash(manifestText)), CancellationToken.None);
        Assert(result.Loaded && result.Authority is not null,
            "Valid strict authority was refused: " + Format(result.Diagnostics));
        SkyrimFaceBakeAuthority authority = result.Authority!;
        Assert(resolver.CallCount == 1 && resolver.LastRequest is not null &&
               resolver.LastRequest.Authorities.Length == 3,
            "Authority loader did not delegate its complete declared asset set exactly once.");
        Assert(authority.Assets.Select(item => item.Id).SequenceEqual(
                ["cfg", "model", "tri"], StringComparer.Ordinal) &&
               authority.Assets.All(item => item.Content.SequenceEqual([(byte)'A'])),
            "Resolver sorting changed manifest asset order or exact content bytes.");
        Assert(authority.LoadedPlugins is [{ Value: "Synthetic.esm" }] &&
               authority.RecordPluginAuthorities.Length == 1 &&
               authority.CatalogConfigs.Length == 1 &&
               authority.CarrierShapes.Length == 1 &&
               authority.RecordOnlyMappedHeadParts.IsEmpty &&
               authority.ShapeTriInputs is [{ ChargenMorphTri: null, MeshMorphTri.Id: "tri" }] &&
               authority.OptionalUnavailableExtensions.Length == 1 &&
               authority.ManifestSha256 == Hash(manifestText),
            "Typed authority lost plugin, selected-headpart, role, optional-path, or manifest bindings.");
    }

    private static async Task TestFaceBakeAuthorityRefusals()
    {
        using TestAuthorityDirectory temp = TestAuthorityDirectory.Create();
        RecordingContentResolver resolver = new(reverseOutput: false);
        SkyrimFaceBakeAuthorityLoader loader = AuthorityLoader(resolver);
        string valid = SyntheticManifest();

        await AssertManifestRefused(loader, temp, resolver, "unknown.json",
            valid.Insert(valid.IndexOf('{') + 1, "\n  \"unknown\": true,"),
            "face-bake-authority-json-invalid");
        await AssertManifestRefused(loader, temp, resolver, "duplicate-property.json",
            valid.Insert(valid.IndexOf('{') + 1, "\n  \"schemaVersion\": 1,"),
            "face-bake-authority-json-property-duplicate");
        await AssertManifestRefused(loader, temp, resolver, "order-drift.json",
            valid.Replace("{ \"order\": 0, \"plugin\": \"Synthetic.esm\" }",
                "{ \"order\": 1, \"plugin\": \"Synthetic.esm\" }",
                StringComparison.Ordinal),
            "face-bake-authority-order-drift");
        await AssertManifestRefused(loader, temp, resolver, "noncanonical-path.json",
            valid.Replace("tmp/authority-fixture/catalog.ini",
                @"tmp\\authority-fixture\\catalog.ini", StringComparison.Ordinal),
            "face-bake-authority-asset-invalid");
        await AssertManifestRefused(loader, temp, resolver, "duplicate-id.json",
            valid.Replace("\"id\": \"model\"", "\"id\": \"cfg\"",
                StringComparison.Ordinal),
            "face-bake-authority-asset-id-invalid");
        await AssertManifestRefused(loader, temp, resolver, "record-only-order.json",
            WithRecordOnlyRows(valid,
                "{ \"order\": 1, \"headPart\": \"Synthetic.esm|0x00000002\" }"),
            "face-bake-authority-order-drift");
        await AssertManifestRefused(loader, temp, resolver, "record-only-duplicate.json",
            WithRecordOnlyRows(valid,
                "{ \"order\": 0, \"headPart\": \"Synthetic.esm|0x00000002\" },\n" +
                "    { \"order\": 1, \"headPart\": \"Synthetic.esm|0x00000002\" }"),
            "face-bake-authority-record-only-headpart-duplicate");
        await AssertManifestRefused(loader, temp, resolver, "record-only-provider.json",
            WithRecordOnlyRows(valid,
                "{ \"order\": 0, \"headPart\": \"Other.esm|0x00000002\" }"),
            "face-bake-authority-record-only-headpart-invalid");
        await AssertManifestRefused(loader, temp, resolver, "record-only-overlap.json",
            WithRecordOnlyRows(valid,
                "{ \"order\": 0, \"headPart\": \"Synthetic.esm|0x00000001\" }"),
            "face-bake-authority-record-only-headpart-carrier-overlap");

        WorkspacePath hashManifest = temp.WriteManifest("hash-drift.json", valid);
        int callsBeforeHash = resolver.CallCount;
        SkyrimFaceBakeAuthorityLoadResult hashDrift = await loader.LoadAsync(
            Request(hashManifest, new Sha256Hash(new string('0', 64))),
            CancellationToken.None);
        Assert(!hashDrift.Loaded && HasCode(hashDrift.Diagnostics,
                   "face-bake-authority-manifest-hash-mismatch") &&
               resolver.CallCount == callsBeforeHash,
            "Manifest hash drift reached content materialization.");

        string oversizedText = new(' ', 1024 * 1024 + 1);
        WorkspacePath oversized = temp.WriteManifest("oversized.json", oversizedText);
        int callsBeforeOversized = resolver.CallCount;
        SkyrimFaceBakeAuthorityLoadResult oversizedResult = await loader.LoadAsync(
            Request(oversized, Hash(oversizedText)), CancellationToken.None);
        Assert(!oversizedResult.Loaded && HasCode(oversizedResult.Diagnostics,
                   "face-bake-authority-manifest-size-invalid") &&
               resolver.CallCount == callsBeforeOversized,
            "Oversized authority manifest reached content resolution.");

        WorkspacePath ordinary = temp.WriteManifest("ordinary.json", valid);
        int callsBeforeAds = resolver.CallCount;
        SkyrimFaceBakeAuthorityLoadResult ads = await loader.LoadAsync(
            Request(new WorkspacePath(ordinary.Value + ":authority"), Hash(valid)),
            CancellationToken.None);
        Assert(!ads.Loaded && HasCode(ads.Diagnostics,
                   "face-bake-authority-manifest-path-refused") &&
               resolver.CallCount == callsBeforeAds,
            "Alternate-data-stream manifest path reached content resolution.");

        string targetDirectory = Path.Combine(temp.Path, "reparse-target");
        Directory.CreateDirectory(targetDirectory);
        string targetManifest = Path.Combine(targetDirectory, "authority.json");
        await File.WriteAllTextAsync(targetManifest, valid);
        string linkDirectory = Path.Combine(temp.Path, "reparse-link");
        if (PhysicalReparseFixture.TryCreateDirectoryLink(
                linkDirectory, targetDirectory, temp.Path))
        {
            try
            {
                int callsBeforeReparse = resolver.CallCount;
                SkyrimFaceBakeAuthorityLoadResult reparse = await loader.LoadAsync(
                    Request(new WorkspacePath(Path.Combine(linkDirectory, "authority.json")),
                        Hash(valid)), CancellationToken.None);
                Assert(!reparse.Loaded && HasCode(reparse.Diagnostics,
                           "face-bake-authority-manifest-reparse-refused") &&
                       resolver.CallCount == callsBeforeReparse,
                    "Reparse-point manifest path reached content resolution.");
            }
            finally
            {
                if (Directory.Exists(linkDirectory))
                    Directory.Delete(linkDirectory);
            }
        }
        else
        {
            MethodInfo? predicate = typeof(SkyrimFaceBakeAuthorityLoader).GetMethod(
                "IsReparsePoint", BindingFlags.NonPublic | BindingFlags.Static);
            Assert(predicate is not null &&
                   predicate.Invoke(null, [FileAttributes.ReparsePoint]) is true &&
                   predicate.Invoke(null, [FileAttributes.Normal]) is false,
                "Manifest reparse predicate did not fail closed on ReparsePoint attributes.");
        }
    }

    private static async Task TestRealEmi2FaceBakeAuthority()
    {
        WorkspacePath manifest = new(Path.Combine(FindProjectRoot().Value,
            "tests", "NpcManager.Assets.Tests", "Fixtures",
            "gate2-emi2-face-bake-authority.json"));
        SkyrimFaceBakeAuthorityLoader loader = AuthorityLoader(Service());
        ImmutableArray<FormReference> selected =
        [
            Reference("High Poly Head.esm", 0x00000A06),
            Reference("Improved Eyes Skyrim.esp", 0x00002889),
            Reference("Koralina's Eyebrows.esp", 0x00000801),
            Reference("KS Hairdo's.esp", 0x000A9555)
        ];
        SkyrimFaceBakeAuthorityLoadResult result = await loader.LoadAsync(
            new SkyrimFaceBakeAuthorityLoadRequest(TestLabRoot, manifest,
                new Sha256Hash(EmiAuthorityHash)), CancellationToken.None);
        Assert(result.Loaded && result.Authority is not null,
            "Real Emi2 authority was refused: " + Format(result.Diagnostics));
        SkyrimFaceBakeAuthority authority = result.Authority!;

        Assert(authority.LoadedPlugins.Select(item => item.Value).SequenceEqual(
        [
            "Expressive Facegen Morphs.esl",
            "High Poly Head.esm",
            "RaceMenu.esp"
        ], StringComparer.Ordinal) && authority.RecordPluginAuthorities.Length == 5,
            "Real loaded-plugin order or record-plugin authority count drifted.");
        Assert(authority.Assets.Length == 57 && authority.CatalogConfigs.Length == 8 &&
               authority.ShapeTriInputs.Length == 7 && authority.CarrierShapes.Length == 7 &&
               authority.RecordOnlyMappedHeadParts.SequenceEqual(
                   [Reference("Skyrim.esm", 0x000EC1B2)]) &&
               authority.OptionalUnavailableExtensions is
               [{ Value: "meshes/actors/character/facegenmorphs/morphs/nuska/mouth/mouthhumanfchargen.tri" }],
            "Real authority closure counts or optional-unavailable path drifted.");
        Assert(selected.All(headPart => authority.CarrierShapes.Any(item =>
                   item.HeadPart == headPart)),
            "Authority does not expose every real preset-selected root for the facade intersection.");
        Assert(authority.Assets.All(item => item.ContentSha256 == Hash(item.Content.AsSpan()) &&
                                                item.Content.Length == item.ContentLength),
            "Materialized real content is not bound to every declared length and SHA-256.");

        const string raceMenuArchiveHash =
            "fcc46f42731d2b7c3782b96cff40930ffcf689482ca8e9a5568cf2b76e9a7603";
        SkyrimFaceBakeAuthorityAsset[] archiveAssets = authority.Assets
            .Where(item => item.ProviderKind == SkyrimFaceBakeAuthorityProviderKind.Bsa).ToArray();
        Assert(archiveAssets.Length == 16 && archiveAssets.All(item =>
                   item.ProviderId == "RaceMenu" &&
                   item.ProviderSha256 == new Sha256Hash(raceMenuArchiveHash)),
            "Real RaceMenu BSA provider closure or archive hash drifted.");
        Assert(authority.CatalogConfigs.Any(item =>
                   item.Asset.AssetPath == new AssetPath(
                       "meshes/actors/character/facegenmorphs/RaceMenu.esp/sliders/beast.ini") &&
                   item.Asset.ContentLength == 9501 &&
                   item.Asset.ContentSha256 == new Sha256Hash(
                       "30be21628658aa9f206812e564720b2b7a304edc46383e6799d28176b3b70f3e")),
            "RaceMenu races.ini beast slider dependency is absent or not hash-bound.");
        SkyrimRaceMenuCatalogParseResult catalog = new RaceMenuSliderCatalogParserCore().Parse(
            new SkyrimRaceMenuCatalogParseRequest(authority.LoadedPlugins,
                authority.CatalogConfigs.Select(item => new SkyrimRaceMenuCatalogAsset(
                    item.Asset.AssetPath, item.Asset.ContentSha256, item.Asset.Content))
                    .ToImmutableArray()));
        Assert(catalog.Accepted && catalog.Catalog is not null,
            "Real authority catalog closure was refused: " + Format(catalog.Diagnostics));
        AssertShapeExtensionsFollowCatalog(authority, catalog.Catalog!,
            "MJBFemaleEyesHumanGreen04", "/EFM/Female/EyesFemale.tri");
        AssertShapeExtensionsFollowCatalog(authority, catalog.Catalog!,
            "FemaleMouthHumanoidDefault", "/EFM/Female/MouthHumanF.tri");

        Dictionary<string, SkyrimFaceBakeCarrierShapeAuthority> carriers = authority.CarrierShapes
            .ToDictionary(item => item.CarrierShapeName, StringComparer.Ordinal);
        AssertCarrier(carriers, "00KLH_FemaleHeadNord", "High Poly Head.esm", 0x00000A06,
            "meshes/KL/High Poly Head/FemaleHead.nif", "FemaleHead_KLH",
            "3d48dbb1637cb6105fb8bfedd86053cd60da2d21bd4bda22ff47dfde7b9e4773",
            "e70076f7e1a7d0e6297ef1bda2a8cda3e3ac9c30a9bfd2a115d7baf3184f82d6");
        AssertCarrier(carriers, "FemaleMouthHumanoidDefault", "Skyrim.esm", 0x0005150F,
            "meshes/Actors/Character/Character Assets/Mouth/MouthHumanF.nif", "MouthHumanF",
            "b1ef0ea833a50557e412cf4c99fe16ebe6dfba640f4c0189b4f4e01c9a347775",
            "36fddb9e35d575e37b8d4f99217256a543513a827530c22a73c1d6ab2527d2f2");
        AssertCarrier(carriers, "0LassiHL", "KS Hairdo's.esp", 0x000A9554,
            "meshes/KS Hairdo's/LassiHL.nif", "s4studio_mesh_3",
            "374432f2fe324292283ea1658d2cc006525735e53a8e6864c38e7f20c63d841a",
            "07f4e98b1266abfd44e35e0f43f430f105dcf5c1eda674326dd858c9f3c5385c");
        Assert(authority.Assets.Where(item => item.AssetPath.Value.EndsWith(".nif",
                   StringComparison.OrdinalIgnoreCase)).All(item =>
                   !item.AssetPath.Value.Contains("facegeom", StringComparison.OrdinalIgnoreCase)),
            "Real loader admitted a finished FaceGeom NIF as model authority.");

        SkyrimFaceBakeShapeTriAuthority lassi = authority.ShapeTriInputs.Single(item =>
            item.CarrierShapeName == "0Lassi");
        SkyrimFaceBakeShapeTriAuthority lassiHl = authority.ShapeTriInputs.Single(item =>
            item.CarrierShapeName == "0LassiHL");
        Assert(lassi.ChargenMorphTri is null && lassi.ExtendedMorphTris.IsEmpty &&
               lassi.MeshMorphTri?.Id == "tri-hair-lassi" &&
               lassiHl.MeshMorphTri?.Id == "tri-hair-lassi",
            "Lassi's shared zero-morph mesh TRI was invented as a chargen/extended route.");
    }

    private static SkyrimFaceBakeAuthorityLoader AuthorityLoader(
        ISkyrimAssetContentResolver resolver) =>
        new(new KBoundTestPolicy(), TestLabRoot, resolver);

    private static void AssertShapeExtensionsFollowCatalog(
        SkyrimFaceBakeAuthority authority,
        SkyrimRaceMenuSliderCatalog catalog,
        string carrierShapeName,
        string identifyingExtensionSuffix)
    {
        SkyrimFaceBakeShapeTriAuthority shape = authority.ShapeTriInputs.Single(item =>
            item.CarrierShapeName == carrierShapeName);
        SkyrimRaceMenuMorphExtension catalogEntry = catalog.MorphExtensions.Single(item =>
            item.ExtendedTriPaths.Any(path => path.Value.EndsWith(
                identifyingExtensionSuffix, StringComparison.OrdinalIgnoreCase)));
        HashSet<string> materializedPaths = authority.Assets
            .Select(item => item.AssetPath.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string[] expectedAvailableOrder = catalogEntry.ExtendedTriPaths
            .Select(item => item.Value)
            .Where(materializedPaths.Contains)
            .ToArray();
        string[] actualOrder = shape.ExtendedMorphTris
            .Select(item => item.AssetPath.Value)
            .ToArray();

        Assert(actualOrder.SequenceEqual(expectedAvailableOrder, StringComparer.OrdinalIgnoreCase),
            $"Real {carrierShapeName} extension authority is not the ordered available subset of the parsed catalog.");
    }

    private static SkyrimFaceBakeAuthorityLoadRequest Request(
        WorkspacePath manifest,
        Sha256Hash hash) =>
        new(TestLabRoot, manifest, hash);

    private static FormReference Reference(string plugin, uint formId) =>
        new(new PluginName(plugin), new FormId(formId));

    private static Sha256Hash Hash(string value) => Hash(System.Text.Encoding.UTF8.GetBytes(value));

    private static Sha256Hash Hash(ReadOnlySpan<byte> value) =>
        new(Convert.ToHexString(SHA256.HashData(value)));

    private static string Format(ImmutableArray<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}:{item.Message}"));

    private static bool HasCode(ImmutableArray<Diagnostic> diagnostics, string code) =>
        diagnostics.Any(item => item.Code == code);

    private static void AssertCarrier(
        Dictionary<string, SkyrimFaceBakeCarrierShapeAuthority> carriers,
        string carrierName,
        string plugin,
        uint formId,
        string modelPath,
        string modelShape,
        string positionHash,
        string topologyHash)
    {
        Assert(carriers.TryGetValue(carrierName, out SkyrimFaceBakeCarrierShapeAuthority? carrier) &&
               carrier.HeadPart == Reference(plugin, formId) &&
               carrier.ModelNif.AssetPath == new AssetPath(modelPath) &&
               carrier.ModelShapeName == modelShape &&
               carrier.ExpectedModelPositionSha256 == new Sha256Hash(positionHash) &&
               carrier.ExpectedModelTopologySha256 == new Sha256Hash(topologyHash) &&
               carrier.ExpectedCarrierTopologySha256 == new Sha256Hash(topologyHash),
            $"Carrier mapping '{carrierName}' drifted from its exact HDPT/model/topology authority.");
    }

    private static async Task AssertManifestRefused(
        SkyrimFaceBakeAuthorityLoader loader,
        TestAuthorityDirectory temp,
        RecordingContentResolver resolver,
        string name,
        string text,
        string expectedCode)
    {
        WorkspacePath manifest = temp.WriteManifest(name, text);
        int callsBefore = resolver.CallCount;
        SkyrimFaceBakeAuthorityLoadResult result = await loader.LoadAsync(
            Request(manifest, Hash(text)), CancellationToken.None);
        Assert(!result.Loaded && HasCode(result.Diagnostics, expectedCode) &&
               resolver.CallCount == callsBefore,
            $"Refusal '{expectedCode}' did not fail before content resolution: {Format(result.Diagnostics)}");
    }

    private static string WithRecordOnlyRows(string manifest, string rows) =>
        manifest.Replace("\"recordOnlyMappedHeadParts\": []",
            $"\"recordOnlyMappedHeadParts\": [\n    {rows}\n  ]",
            StringComparison.Ordinal);

    private static string SyntheticManifest() => $$"""
        {
          "schemaVersion": 2,
          "authorityId": "synthetic-face-bake-authority-v1",
          "loadedPlugins": [
            { "order": 0, "plugin": "Synthetic.esm" }
          ],
          "recordPlugins": [
            {
              "order": 0,
              "plugin": "Synthetic.esm",
              "path": "projects/NpcManagerReimplementation/tmp/authority-fixture/Synthetic.esm",
              "sha256": "{{OneByteHash}}"
            }
          ],
          "assets": [
            {
              "order": 0,
              "id": "cfg",
              "providerId": "synthetic loose",
              "providerKind": "loose",
              "providerPath": "projects/NpcManagerReimplementation/tmp/authority-fixture/catalog.ini",
              "providerSha256": "{{OneByteHash}}",
              "assetPath": "meshes/actors/character/facegenmorphs/Synthetic.esm/morphs.ini",
              "contentLength": 1,
              "contentSha256": "{{OneByteHash}}"
            },
            {
              "order": 1,
              "id": "model",
              "providerId": "synthetic loose",
              "providerKind": "loose",
              "providerPath": "projects/NpcManagerReimplementation/tmp/authority-fixture/model.nif",
              "providerSha256": "{{OneByteHash}}",
              "assetPath": "meshes/synthetic/model.nif",
              "contentLength": 1,
              "contentSha256": "{{OneByteHash}}"
            },
            {
              "order": 2,
              "id": "tri",
              "providerId": "synthetic loose",
              "providerKind": "loose",
              "providerPath": "projects/NpcManagerReimplementation/tmp/authority-fixture/morph.tri",
              "providerSha256": "{{OneByteHash}}",
              "assetPath": "meshes/synthetic/morph.tri",
              "contentLength": 1,
              "contentSha256": "{{OneByteHash}}"
            }
          ],
          "catalogConfigs": [
            { "order": 0, "plugin": "Synthetic.esm", "assetId": "cfg" }
          ],
          "shapeTriInputs": [
            {
              "order": 0,
              "carrierShapeName": "SyntheticCarrier",
              "raceAssetId": null,
              "chargenAssetId": null,
              "meshAssetId": "tri",
              "extendedAssetIds": []
            }
          ],
          "carrierShapes": [
            {
              "order": 0,
              "headPart": "Synthetic.esm|0x00000001",
              "modelAssetId": "model",
              "modelShapeName": "SyntheticModelShape",
              "carrierShapeName": "SyntheticCarrier",
              "expectedModelPositionSha256": "2222222222222222222222222222222222222222222222222222222222222222",
              "expectedModelTopologySha256": "1111111111111111111111111111111111111111111111111111111111111111",
              "expectedCarrierTopologySha256": "1111111111111111111111111111111111111111111111111111111111111111"
            }
          ],
          "recordOnlyMappedHeadParts": [],
          "optionalUnavailableExtensions": [
            {
              "order": 0,
              "assetPath": "meshes/actors/character/facegenmorphs/morphs/synthetic/missing.tri"
            }
          ]
        }
        """;

    private sealed class RecordingContentResolver(bool reverseOutput) : ISkyrimAssetContentResolver
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
            IEnumerable<SkyrimAssetContentAuthority> declared = reverseOutput
                ? request.Authorities.Reverse()
                : request.Authorities;
            ImmutableArray<ResolvedSkyrimAssetContent> assets = declared.Select(item =>
                new ResolvedSkyrimAssetContent(item.ProviderId, item.Kind, item.ProviderPath,
                    item.ProviderSha256, item.AssetPath, item.ContentLength, item.ContentSha256,
                    [(byte)'A'])).ToImmutableArray();
            return ValueTask.FromResult(new SkyrimAssetContentResolutionResult(true, assets, []));
        }
    }

    private sealed class TestAuthorityDirectory(string path) : IDisposable
    {
        public string Path { get; } = path;

        public static TestAuthorityDirectory Create()
        {
            string path = System.IO.Path.Combine(FindProjectRoot().Value, "tmp",
                "face-bake-authority-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return new TestAuthorityDirectory(path);
        }

        public WorkspacePath WriteManifest(string name, string content)
        {
            string target = System.IO.Path.Combine(Path, name);
            File.WriteAllText(target, content);
            return new WorkspacePath(target);
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}
