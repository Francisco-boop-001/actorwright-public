using System.Security.Cryptography;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private const string NpcCreateRequestSchemaIdentifier =
        "npc.create-from-jslot.request.v1";

    private static readonly JsonSerializerOptions ProviderTestJsonOptions =
        new() { WriteIndented = true };

    private static async Task TestRaceMenuRequestLoaderCompatibility()
    {
        string rootPath = Path.Combine(Path.GetFullPath("artifacts"),
            $"request-loader-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(rootPath);
        var root = new WorkspacePath(rootPath);
        var workspace = new WorkspacePath(Path.GetFullPath("."));
        var registry = new TestProductProviderResourceRegistry();
        var loader = new RaceMenuNpcExecutionRequestFileLoader(
            workspace, registry);
        try
        {
            AssertRequestSchemaExport();

            BoundFile preset = await ProviderTestFile(root, "preset.jslot");
            BoundFile faceGeom = await ProviderTestFile(root, "face.nif");
            BoundFile faceTint = await ProviderTestFile(root, "face.dds");
            BoundFile record = await ProviderTestFile(root, "record.json");
            BoundFile routes = await ProviderTestFile(root, "routes.json");
            BoundFile standalone = await ProviderTestFile(root,
                "standalone.json");
            BoundFile providerManifest = await ProviderTestFile(root,
                "workspace-provider.json");
            BoundFile template = await ProviderTestFile(root,
                "WorkspaceTemplate.esp");
            BoundFile carrier = await ProviderTestFile(root,
                "workspace-carrier.nif");
            BoundFile tintManifest = await ProviderTestFile(root,
                "workspace-tint.json");
            BoundFile dependency = await ProviderTestFile(root,
                "workspace-dependency.json");
            BoundFile sourcePlugin = await ProviderTestFile(root,
                "Existing.esp");
            string providerRoot = Path.Combine(root.Value, "provider-data");
            Directory.CreateDirectory(providerRoot);
            BoundFile bundle = await ProviderTestJson(root,
                "source-bundle.json",
                new JsonObject
                {
                    ["schemaVersion"] = 1,
                    ["providerContext"] = new JsonObject
                    {
                        ["manifestPath"] = providerManifest.Relative,
                        ["manifestSha256"] = providerManifest.Hash.Value,
                        ["dependencyManifestPath"] = dependency.Relative,
                        ["dependencyManifestSha256"] = dependency.Hash.Value
                    },
                    ["retained"] = "non-provider-authority"
                });

            JsonObject workspaceRequest = ProviderTestRequest(
                schemaVersion: 1, bundle, preset, faceGeom, faceTint,
                record, routes, standalone,
                WorkspaceProvider(providerManifest, template, carrier,
                    tintManifest, dependency,
                    ProviderTestRelative(workspace, providerRoot)),
                existing: null);
            RaceMenuNpcExecutionRequestFileLoadResult schema1 =
                await ProviderTestLoad(loader, root, "schema1.json",
                    workspaceRequest);
            Assert(schema1.Loaded &&
                   schema1.Request?.Build.ProviderContext.ProviderResources is
                       null,
                "schemaVersion 1 workspace provider no longer loads unchanged.");

            JsonObject schema2Request =
                workspaceRequest.DeepClone().AsObject();
            schema2Request["schemaVersion"] = 2;
            schema2Request["existingNpcTarget"] = new JsonObject
            {
                ["sourcePlugin"] = sourcePlugin.Relative,
                ["sourcePluginSha256"] = sourcePlugin.Hash.Value,
                ["targetFormId"] = "0x00000800"
            };
            RaceMenuNpcExecutionRequestFileLoadResult schema2 =
                await ProviderTestLoad(loader, root, "schema2.json",
                    schema2Request);
            Assert(schema2.Loaded && schema2.Request?.Build.ExistingNpcTarget
                   is not null,
                "schemaVersion 2 workspace provider no longer loads unchanged.");

            JsonObject schema3Workspace =
                workspaceRequest.DeepClone().AsObject();
            schema3Workspace["schemaVersion"] = 3;
            RaceMenuNpcExecutionRequestFileLoadResult schema3 =
                await ProviderTestLoad(loader, root, "schema3-workspace.json",
                    schema3Workspace);
            Assert(schema3.Loaded,
                "schemaVersion 3 rejected its complete workspace arm.");

            JsonObject schema3ExistingTarget =
                schema3Workspace.DeepClone().AsObject();
            schema3ExistingTarget["existingNpcTarget"] =
                schema2Request["existingNpcTarget"]!.DeepClone();
            RaceMenuNpcExecutionRequestFileLoadResult schema3Target =
                await ProviderTestLoad(loader, root,
                    "schema3-existing-target.json", schema3ExistingTarget);
            Assert(!schema3Target.Loaded && schema3Target.Status ==
                   RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused &&
                   schema3Target.Diagnostics.Any(item => item.Code ==
                       "preset-npc-request-invalid"),
                "schemaVersion 3 silently accepted an existingNpcTarget that the closed schema rejects.");

            ProductFixtureBundleReference product = new(
                "blank-npc-v1", new Sha256Hash(new string('0', 64)));
            JsonObject productRequest =
                workspaceRequest.DeepClone().AsObject();
            productRequest["schemaVersion"] = 3;
            productRequest["providerContext"] = ProductProvider(product);
            JsonObject admittedProductRequest =
                productRequest.DeepClone().AsObject();
            admittedProductRequest["providerContext"] = ProductProvider(
                new ProductFixtureBundleReference("blank-npc-v1",
                    new Sha256Hash(new string('A', 64))));
            RaceMenuNpcExecutionRequestFileLoadResult admittedProduct =
                await ProviderTestLoad(loader, root,
                    "schema3-product-admitted.json", admittedProductRequest);
            BlankNpcProviderBindingRequest? admittedProductContext =
                admittedProduct.Request?.Build.ProviderContext;
            Assert(admittedProduct.Loaded &&
                   admittedProductContext is not null &&
                   admittedProductContext.ManifestPath == default &&
                   admittedProductContext.ProviderResources is
                       { BundleId: "blank-npc-v1" } productResources &&
                   productResources.Files.Length == 6 &&
                   productResources.Files.All(resource =>
                       resource is ApplicationProviderResourceAuthority authority &&
                       authority.BundleId == "blank-npc-v1"),
                "A valid schemaVersion 3 product-provider request no longer maps application authority files without workspace paths.");
            var emptyResourceRoot = new ApplicationResourcePath(Path.Combine(
                root.Value, "resources-without-default-provider"));
            Directory.CreateDirectory(emptyResourceRoot.Value);
            var unavailableRegistry =
                new ApplicationProviderResourceRegistry(emptyResourceRoot);
            Assert(!unavailableRegistry.TryGetDefaultBlankNpcFixture(
                    out ProductFixtureBundleReference? absentProduct) &&
                   absentProduct is null,
                "A wholly absent optional provider bundle was not reported as unavailable.");
            var missingProviderLoader =
                new RaceMenuNpcExecutionRequestFileLoader(
                    workspace, unavailableRegistry);
            RaceMenuNpcExecutionRequestFileLoadResult productLoaded =
                await ProviderTestLoad(missingProviderLoader, root,
                    "schema3-product.json", productRequest);
            Assert(!productLoaded.Loaded && productLoaded.Status ==
                       RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused &&
                       productLoaded.Diagnostics.Any(item => item.Code ==
                        "product-provider-unavailable"),
                "An explicit product-provider request without its optional bundle did not preserve the typed unavailable diagnostic.");
            var rejectedAdmissionLoader =
                new RaceMenuNpcExecutionRequestFileLoader(
                    workspace,
                    new TestProductProviderResourceRegistry(
                        refuseAdmission: true));
            RaceMenuNpcExecutionRequestFileLoadResult rejectedAdmission =
                await ProviderTestLoad(rejectedAdmissionLoader, root,
                    "schema3-product-rejected.json", productRequest);
            Assert(!rejectedAdmission.Loaded &&
                   rejectedAdmission.Status ==
                       RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused &&
                   rejectedAdmission.Diagnostics.Any(item =>
                       item.Code == "preset-npc-request-invalid" &&
                       item.Message.Contains(
                           "fixture-product-provider-rejected",
                           StringComparison.Ordinal)) &&
                   rejectedAdmission.Diagnostics.All(item => item.Code !=
                       "product-provider-unavailable"),
                "A present product-provider admission failure was mislabeled as optional absence.");
            Directory.CreateDirectory(Path.Combine(emptyResourceRoot.Value,
                "runtime", "product-fixtures", "blank-npc-v1"));
            bool partialBundleRejected = false;
            try
            {
                _ = unavailableRegistry.TryGetDefaultBlankNpcFixture(out _);
            }
            catch (InvalidDataException)
            {
                partialBundleRejected = true;
            }
            Assert(partialBundleRejected,
                "A present but partial optional bundle was silently treated as absent.");
            RaceMenuNpcExecutionRequestFileLoadResult partialProductLoaded =
                await ProviderTestLoad(missingProviderLoader, root,
                    "schema3-product-partial.json", productRequest);
            Assert(!partialProductLoaded.Loaded &&
                   partialProductLoaded.Status ==
                       RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused &&
                   partialProductLoaded.Diagnostics.Any(item => item.Code ==
                       "preset-npc-request-invalid") &&
                   partialProductLoaded.Diagnostics.All(item => item.Code !=
                       "product-provider-unavailable"),
                "A present partial product-provider bundle was mislabeled as optional absence.");
            JsonObject mixed = productRequest.DeepClone().AsObject();
            mixed["providerContext"]!["manifestPath"] =
                providerManifest.Relative;
            RaceMenuNpcExecutionRequestFileLoadResult mixedResult =
                await ProviderTestLoad(loader, root, "schema3-mixed.json",
                    mixed);
            Assert(!mixedResult.Loaded && mixedResult.Status ==
                   RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused,
                "schemaVersion 3 accepted conflicting provider arms.");

            JsonObject legacy = schema2Request.DeepClone().AsObject();
            legacy["providerContext"] = LegacyMissingProvider(
                dependency.Relative,
                ProviderTestRelative(workspace, providerRoot));
            var migratedRoot = new WorkspacePath(Path.Combine(root.Value,
                "migrated-request"));
            BoundFile legacyRequestFile = await ProviderTestJson(root,
                "legacy-missing.json", legacy);
            var migrationUnavailableResourceRoot = new ApplicationResourcePath(
                Path.Combine(root.Value,
                    "migration-resources-without-provider"));
            Directory.CreateDirectory(migrationUnavailableResourceRoot.Value);
            var migrationUnavailableLoader =
                new RaceMenuNpcExecutionRequestFileLoader(
                    workspace,
                    new ApplicationProviderResourceRegistry(
                        migrationUnavailableResourceRoot));
            RaceMenuNpcExecutionRequestFileLoadResult unavailableMigration =
                await migrationUnavailableLoader.LoadAsync(
                    new RaceMenuNpcExecutionRequestFileLoadRequest(
                        new WorkspacePath(Path.Combine(workspace.Value,
                            legacyRequestFile.Relative.Replace('/',
                                Path.DirectorySeparatorChar))),
                        legacyRequestFile.Hash), CancellationToken.None);
            Assert(unavailableMigration.Status ==
                       RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused &&
                   unavailableMigration.Diagnostics.Any(item => item.Code ==
                       "product-provider-unavailable"),
                "A recognized legacy request with no optional provider bundle lost its typed unavailable diagnostic.");

            RaceMenuNpcExecutionRequestFileLoadResult partialMigration =
                await missingProviderLoader.LoadAsync(
                    new RaceMenuNpcExecutionRequestFileLoadRequest(
                        new WorkspacePath(Path.Combine(workspace.Value,
                            legacyRequestFile.Relative.Replace('/',
                                Path.DirectorySeparatorChar))),
                        legacyRequestFile.Hash), CancellationToken.None);
            Assert(partialMigration.Status ==
                       RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused &&
                   partialMigration.Diagnostics.Any(item => item.Code ==
                       "preset-npc-request-invalid") &&
                   partialMigration.Diagnostics.All(item => item.Code !=
                       "product-provider-unavailable"),
                "A present partial provider bundle during migration was mislabeled as optional absence.");

            RaceMenuNpcExecutionRequestFileLoadResult migration =
                await loader.LoadAsync(
                    new RaceMenuNpcExecutionRequestFileLoadRequest(
                        new WorkspacePath(Path.Combine(workspace.Value,
                            legacyRequestFile.Relative.Replace('/',
                                Path.DirectorySeparatorChar))),
                        legacyRequestFile.Hash)
                    {
                        PlannedMigratedRequestRoot = migratedRoot
                    }, CancellationToken.None);
            Assert(migration.Status ==
                       RaceMenuNpcExecutionRequestFileLoadStatus
                           .MigrationRequired &&
                   !migration.Loaded && migration.Request is null &&
                   migration.MigrationReview is
                   {
                       BuildAuthority: false,
                       RuntimeAuthority: false,
                       PlannedFiles.Length: 3
                   } &&
                   migration.Diagnostics.Any(item => item.Code ==
                       "legacy-provider-fixture-bytes-unavailable"),
                "Recognized missing Gate 1 paths did not return a blocked migration review.");

            var rejectedMigrationLoader =
                new RaceMenuNpcExecutionRequestFileLoader(
                    workspace,
                    new TestProductProviderResourceRegistry(
                        refuseAdmission: true));
            RaceMenuNpcExecutionRequestFileLoadResult rejectedMigration =
                await rejectedMigrationLoader.LoadAsync(
                    new RaceMenuNpcExecutionRequestFileLoadRequest(
                        new WorkspacePath(Path.Combine(workspace.Value,
                            legacyRequestFile.Relative.Replace('/',
                                Path.DirectorySeparatorChar))),
                        legacyRequestFile.Hash), CancellationToken.None);
            Assert(rejectedMigration.Status ==
                       RaceMenuNpcExecutionRequestFileLoadStatus.ValidationRefused &&
                   rejectedMigration.Diagnostics.Any(item =>
                       item.Code == "preset-npc-request-invalid" &&
                       item.Message.Contains(
                           "fixture-product-provider-rejected",
                           StringComparison.Ordinal)) &&
                   rejectedMigration.Diagnostics.All(item => item.Code !=
                       "product-provider-unavailable"),
                "A present product-provider admission failure during migration was mislabeled as optional absence.");

            await AssertProviderMigrationAdmissionDiagnostics(
                workspace, registry, migration, root);

            var service = new ProviderMigrationService(workspace, registry);
            var reviewPath = new WorkspacePath(Path.Combine(root.Value,
                "provider-migration-review.json"));
            ProviderMigrationResult review = await service.WriteReviewAsync(
                new ProviderMigrationReviewWriteRequest(
                    migration.MigrationReview!, reviewPath),
                CancellationToken.None);
            Assert(review.Completed && File.Exists(reviewPath.Value) &&
                   !Directory.Exists(migratedRoot.Value) &&
                   review.Outputs.SequenceEqual([reviewPath]),
                "Migration review wrote build output or failed atomic readback: " +
                string.Join(" | ", review.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));

            ProviderMigrationResult accepted = await service.AcceptAsync(
                new ProviderMigrationAcceptanceRequest(
                    migration.RequestFile,
                    migration.ActualSha256!.Value,
                    reviewPath,
                    review.Review!.Sha256,
                    migratedRoot),
                CancellationToken.None);
            Assert(accepted.Completed &&
                   Directory.Exists(migratedRoot.Value),
                "Migration acceptance failed before promotion: " +
                string.Join(" | ", accepted.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));
            string[] migratedFiles = Directory.EnumerateFiles(
                    migratedRoot.Value, "*", SearchOption.AllDirectories)
                .Select(Path.GetFileName)
                .Select(name => name!)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert(migratedFiles.SequenceEqual(
                   ["migration-receipt.json", "npc-request.json",
                       "preset-bundle.json"], StringComparer.Ordinal),
                "Migration acceptance did not promote exactly the reviewed three-file plan.");

            string migratedRequestPath = Path.Combine(migratedRoot.Value,
                "npc-request.json");
            byte[] migratedBytes = await File.ReadAllBytesAsync(
                migratedRequestPath);
            using (JsonDocument migratedDocument =
                JsonDocument.Parse(migratedBytes))
            {
                Assert(!migratedDocument.RootElement.TryGetProperty(
                           "existingNpcTarget", out _) &&
                       migration.MigrationReview!.Changes.Any(item =>
                           item.Field == "existingNpcTarget" &&
                           item.After ==
                               "omitted for schemaVersion 3 product-fixture request"),
                    "schemaVersion 3 provider migration did not omit and record existingNpcTarget.");
            }
            RaceMenuNpcExecutionRequestFileLoadResult migrated =
                await loader.LoadAsync(
                    new RaceMenuNpcExecutionRequestFileLoadRequest(
                        new WorkspacePath(migratedRequestPath),
                        ProviderTestHash(migratedBytes)),
                    CancellationToken.None);
            Assert(migrated.Loaded && migrated.Request?.Build.ProviderContext
                       .ProviderResources is not null,
                "The accepted migrated request did not load through the normal schemaVersion 3 product arm.");

            ProviderMigrationResult replay = await service.AcceptAsync(
                new ProviderMigrationAcceptanceRequest(
                    migration.RequestFile,
                    migration.ActualSha256.Value,
                    reviewPath,
                    review.Review.Sha256,
                    migratedRoot),
                CancellationToken.None);
            Assert(!replay.Completed,
                "Migration acceptance overwrote an existing output root.");
        }
        finally
        {
            if (Directory.Exists(root.Value))
                Directory.Delete(root.Value, recursive: true);
        }
    }

    private static void AssertRequestSchemaExport()
    {
        JsonElement export = ProtocolV2SchemaService.RenderInline(
            "npc create-from-jslot");
        JsonElement[] documentSchemas = export.GetProperty("documentSchemas")
            .EnumerateArray().ToArray();
        JsonElement request = documentSchemas.FirstOrDefault(item =>
            item.GetProperty("schemaIdentifier").GetString() ==
                NpcCreateRequestSchemaIdentifier);
        Assert(documentSchemas.Select(item => (
                   item.GetProperty("name").GetString(),
                   item.GetProperty("direction").GetString(),
                   item.GetProperty("schemaIdentifier").GetString()))
               .SequenceEqual([
                   ("request", "input", NpcCreateRequestSchemaIdentifier),
                   ("preflight", "output", "actorwright-npc-build-preflight/1"),
                   ("face-bake-authority", "output", "skyrim-face-bake-authority/1")
               ]) && request.ValueKind != JsonValueKind.Undefined,
            "npc create-from-jslot document schema identities, directions, or order drifted.");
        JsonElement schema = request.GetProperty("jsonSchema");
        Assert(schema.GetProperty("type").GetString() == "object" &&
               !schema.GetProperty("additionalProperties").GetBoolean() &&
               schema.GetProperty("properties").GetProperty("schemaVersion")
                   .GetProperty("enum").EnumerateArray()
                   .Select(item => item.GetInt32())
                   .SequenceEqual([1, 2, 3]),
            "npc create-from-jslot request schema is not closed across versions 1/2/3.");
        JsonElement[] versionArms = schema.GetProperty("oneOf")
            .EnumerateArray().ToArray();
        Assert(versionArms.Length == 3 &&
               versionArms.Any(item => VersionRequires(item, 1, false)) &&
               versionArms.Any(item => VersionRequires(item, 2, true)) &&
               versionArms.Any(item => VersionRequires(item, 3, false)) &&
               versionArms.Where(item => IsVersion(item, 1) || IsVersion(item, 2))
                   .All(ProductFixtureExclusionRequiresNestedProperty),
            "npc create-from-jslot request schema omitted existing-target version rules.");
    }

    private static bool IsVersion(JsonElement schema, int version) =>
        schema.GetProperty("properties").GetProperty("schemaVersion")
            .GetProperty("const").GetInt32() == version;

    private static bool ProductFixtureExclusionRequiresNestedProperty(
        JsonElement schema)
    {
        if (!schema.TryGetProperty("not", out JsonElement exclusion))
            return false;

        JsonElement[] candidates = exclusion.TryGetProperty(
                "anyOf", out JsonElement anyOf)
            ? anyOf.EnumerateArray().ToArray()
            : [exclusion];
        foreach (JsonElement candidate in candidates)
        {
            if (!candidate.TryGetProperty("required",
                    out JsonElement outerRequired) ||
                !outerRequired.EnumerateArray().Any(item =>
                    item.GetString() == "providerContext") ||
                !candidate.TryGetProperty("properties",
                    out JsonElement properties) ||
                !properties.TryGetProperty("providerContext",
                    out JsonElement provider) ||
                !provider.TryGetProperty("required",
                    out JsonElement nestedRequired))
                continue;

            if (nestedRequired.EnumerateArray().Any(item =>
                    item.GetString() == "productFixtureBundle"))
                return true;
        }

        return false;
    }

    private static bool VersionRequires(
        JsonElement schema,
        int version,
        bool existingTargetRequired)
    {
        if (schema.GetProperty("properties").GetProperty("schemaVersion")
                .GetProperty("const").GetInt32() != version)
            return false;
        bool required = schema.TryGetProperty("required", out JsonElement members) &&
            members.EnumerateArray().Any(item =>
                item.GetString() == "existingNpcTarget");
        return required == existingTargetRequired;
    }

    private static JsonObject ProviderTestRequest(
        int schemaVersion,
        BoundFile bundle,
        BoundFile preset,
        BoundFile faceGeom,
        BoundFile faceTint,
        BoundFile record,
        BoundFile routes,
        BoundFile standalone,
        JsonObject provider,
        JsonObject? existing) =>
        new()
        {
            ["schemaVersion"] = schemaVersion,
            ["edition"] = "skyrimse",
            ["presetBundle"] = new JsonObject
            {
                ["manifestPath"] = bundle.Relative,
                ["manifestSha256"] = bundle.Hash.Value,
                ["presetPath"] = preset.Relative,
                ["presetSha256"] = preset.Hash.Value,
                ["faceGeomPath"] = faceGeom.Relative,
                ["faceGeomSha256"] = faceGeom.Hash.Value,
                ["faceTintPath"] = faceTint.Relative,
                ["faceTintSha256"] = faceTint.Hash.Value,
                ["recordAuthorityPath"] = record.Relative,
                ["recordAuthoritySha256"] = record.Hash.Value,
                ["runtimeRoutesPath"] = routes.Relative,
                ["runtimeRoutesSha256"] = routes.Hash.Value
            },
            ["providerContext"] = provider,
            ["standaloneAssets"] = new JsonObject
            {
                ["manifestPath"] = standalone.Relative,
                ["manifestSha256"] = standalone.Hash.Value
            },
            ["output"] = new JsonObject
            {
                ["root"] = "artifacts/provider-test-output",
                ["plugin"] = "ProviderTest.esp"
            },
            ["existingNpcTarget"] = existing,
            ["identity"] = new JsonObject
            {
                ["editorId"] = "ActorwrightProviderTest",
                ["name"] = "Actorwright Provider Test"
            },
            ["traits"] = new JsonObject
            {
                ["sex"] = "female",
                ["role"] = "static-validation",
                ["unique"] = true,
                ["essential"] = false,
                ["protected"] = false,
                ["respawns"] = false,
                ["autoCalcStats"] = true
            },
            ["references"] = new JsonObject
            {
                ["race"] = "Skyrim.esm|0x00013746",
                ["voice"] = "Skyrim.esm|0x00013ADC",
                ["class"] = "Skyrim.esm|0x00013181",
                ["combatStyle"] = "Skyrim.esm|0x0003BE1D",
                ["defaultOutfit"] = "Skyrim.esm|0x0001DC10"
            },
            ["stats"] = new JsonObject
            {
                ["levelMode"] = "fixed",
                ["level"] = 1,
                ["magickaOffset"] = 0,
                ["staminaOffset"] = 0,
                ["healthOffset"] = 0,
                ["calcMinLevel"] = 1,
                ["calcMaxLevel"] = 1,
                ["speedMultiplier"] = 100,
                ["dispositionBase"] = 35,
                ["bleedoutOverride"] = 0,
                ["baseHealth"] = 50,
                ["baseMagicka"] = 50,
                ["baseStamina"] = 50,
                ["height"] = 1,
                ["weight"] = 0,
                ["farAwayModelDistance"] = 255
            }
        };

    private static JsonObject WorkspaceProvider(
        BoundFile manifest,
        BoundFile template,
        BoundFile carrier,
        BoundFile tint,
        BoundFile dependency,
        string providerRoot) =>
        new()
        {
            ["manifestPath"] = manifest.Relative,
            ["manifestSha256"] = manifest.Hash.Value,
            ["templatePlugin"] = template.Relative,
            ["templateSha256"] = template.Hash.Value,
            ["templateNpcFormId"] = "0x00000800",
            ["faceGeomCarrier"] = carrier.Relative,
            ["faceGeomSha256"] = carrier.Hash.Value,
            ["faceTintManifest"] = tint.Relative,
            ["faceTintProviderRoot"] = providerRoot,
            ["dependencyManifest"] = dependency.Relative
        };

    private static JsonObject ProductProvider(
        ProductFixtureBundleReference product) =>
        new()
        {
            ["templateNpcFormId"] = "0x00000800",
            ["productFixtureBundle"] = new JsonObject
            {
                ["bundleId"] = product.BundleId,
                ["registryManifestSha256"] =
                    product.RegistryManifestSha256.Value
            }
        };

    private static JsonObject LegacyMissingProvider(
        string dependency,
        string providerRoot) =>
        new()
        {
            ["manifestPath"] =
                "missing/gate1-blank-provider-bundle.json",
            ["manifestSha256"] =
                "EACB7112F3D0177F1C8C1B14604B3F36F413185C52973BC6027059EAE1D7D06E",
            ["templatePlugin"] = "missing/EmiCarrierProbe.esp",
            ["templateSha256"] =
                "421C3A902A87F343D50F8791CC4B9CAFB2B71BF3B7D94F7C2F7BE2DB6535BBF0",
            ["templateNpcFormId"] = "0x00000800",
            ["faceGeomCarrier"] = "missing/00000800.NIF",
            ["faceGeomSha256"] =
                "4658408FF08F77F2C64EA8AD1837B6A449D9BD4B3958BDF21F4EECDB516EC2F9",
            ["faceTintManifest"] =
                "missing/gate1-qualified-facetint.json",
            ["faceTintProviderRoot"] = providerRoot,
            ["dependencyManifest"] = dependency
        };

    private static async Task<RaceMenuNpcExecutionRequestFileLoadResult>
        ProviderTestLoad(
            RaceMenuNpcExecutionRequestFileLoader loader,
            WorkspacePath root,
            string name,
            JsonObject value)
    {
        BoundFile file = await ProviderTestJson(root, name, value);
        return await loader.LoadAsync(
            new RaceMenuNpcExecutionRequestFileLoadRequest(
                new WorkspacePath(Path.Combine(Path.GetFullPath("."),
                    file.Relative.Replace('/', Path.DirectorySeparatorChar))),
                file.Hash), CancellationToken.None);
    }

    private static async Task<BoundFile> ProviderTestFile(
        WorkspacePath root,
        string name)
    {
        string path = Path.Combine(root.Value, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes("fixture:" + name);
        await File.WriteAllBytesAsync(path, bytes);
        return new BoundFile(ProviderTestRelative(
            new WorkspacePath(Path.GetFullPath(".")), path),
            ProviderTestHash(bytes));
    }

    private static async Task<BoundFile> ProviderTestJson(
        WorkspacePath root,
        string name,
        JsonObject value)
    {
        string path = Path.Combine(root.Value, name);
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(value,
            ProviderTestJsonOptions);
        await File.WriteAllBytesAsync(path, bytes);
        return new BoundFile(ProviderTestRelative(
            new WorkspacePath(Path.GetFullPath(".")), path),
            ProviderTestHash(bytes));
    }

    private static string ProviderTestRelative(
        WorkspacePath workspace,
        string path) =>
        Path.GetRelativePath(workspace.Value, path)
            .Replace(Path.DirectorySeparatorChar, '/');

    private static Sha256Hash ProviderTestHash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static async Task AssertProviderMigrationAdmissionDiagnostics(
        WorkspacePath workspace,
        IApplicationProviderResourceRegistry registry,
        RaceMenuNpcExecutionRequestFileLoadResult migration,
        WorkspacePath root)
    {
        ProviderMigrationReviewArtifact review = migration.MigrationReview!;
        Sha256Hash actualSha256 = migration.ActualSha256!.Value;
        var service = new ProviderMigrationService(workspace, registry);
        await AssertReviewRefusalAsync(service, review,
            new WorkspacePath(Path.Combine(Path.GetTempPath(),
                $"actorwright-migration-outside-{Guid.NewGuid():N}.json")),
            "provider-migration-target-outside-workspace");

        string occupied = Path.Combine(root.Value, "occupied-review.json");
        await File.WriteAllTextAsync(occupied, "occupied");
        await AssertReviewRefusalAsync(service, review,
            new WorkspacePath(occupied), "provider-migration-target-exists");

        ProviderMigrationService reparseService =
            CreateProviderMigrationService(workspace, registry, _ => true);
        await AssertReviewRefusalAsync(reparseService, review,
            new WorkspacePath(Path.Combine(root.Value, "reparse-review.json")),
            "provider-migration-target-reparse-ancestor");

        var reparseSourceCollision = new WorkspacePath(Path.Combine(root.Value,
            "missing-reparse-source-collision.json"));
        await AssertReviewRefusalAsync(reparseService,
            review with { SourceRequest = reparseSourceCollision },
            reparseSourceCollision,
            "provider-migration-target-reparse-ancestor");

        await AssertReviewRefusalAsync(reparseService, review,
            review.SourceRequest,
            "provider-migration-target-exists");

        ProviderMigrationService collisionService =
            CreateProviderMigrationService(workspace, registry, _ => false);
        var missingSource = new WorkspacePath(Path.Combine(root.Value,
            "missing-source-collision.json"));
        await AssertReviewRefusalAsync(collisionService,
            review with { SourceRequest = missingSource }, missingSource,
            "provider-migration-target-source-collision");
        await AssertReviewRefusalAsync(collisionService, review,
            new WorkspacePath(Path.Combine(
                review.PlannedMigratedRequestRoot.Value, "review.json")),
            "provider-migration-target-review-collision");

        var nestedRoot = new WorkspacePath(Path.Combine(
            migration.RequestFile.Value, "migrated"));
        string nestedReviewPath = Path.Combine(root.Value, "nested-review.json");
        byte[] nestedReviewBytes = JsonSerializer.SerializeToUtf8Bytes(review,
            ProviderTestJsonOptions);
        await File.WriteAllBytesAsync(nestedReviewPath, nestedReviewBytes);
        ProviderMigrationResult nestedResult = await service.AcceptAsync(
            new ProviderMigrationAcceptanceRequest(
                migration.RequestFile, actualSha256,
                new WorkspacePath(nestedReviewPath),
                ProviderTestHash(nestedReviewBytes), nestedRoot),
            CancellationToken.None);
        Assert(!nestedResult.Completed && nestedResult.Diagnostics.Any(item =>
                   item.Code == "provider-migration-target-nested"),
            "Nested provider migration target did not retain its exact diagnostic: " +
            string.Join(" | ", nestedResult.Diagnostics.Select(item =>
                item.Code + ":" + item.Message)));
    }

    private static async Task AssertReviewRefusalAsync(
        ProviderMigrationService service,
        ProviderMigrationReviewArtifact review,
        WorkspacePath output,
        string expectedCode)
    {
        ProviderMigrationResult result = await service.WriteReviewAsync(
            new ProviderMigrationReviewWriteRequest(review, output),
            CancellationToken.None);
        Assert(!result.Completed && result.Diagnostics.Length == 1 &&
               result.Diagnostics[0].Code == expectedCode,
            $"Migration admission did not report {expectedCode} first and alone: " +
            string.Join(" | ", result.Diagnostics.Select(item =>
                item.Code + ":" + item.Message)));
    }

    private static ProviderMigrationService CreateProviderMigrationService(
        WorkspacePath workspace,
        IApplicationProviderResourceRegistry registry,
        Func<string, bool> reparseAncestorProbe)
    {
        ConstructorInfo? constructor = typeof(ProviderMigrationService)
            .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null,
                [typeof(WorkspacePath),
                    typeof(IApplicationProviderResourceRegistry),
                    typeof(Func<string, bool>)], modifiers: null);
        Assert(constructor is not null,
            "Provider migration lacks the internal reparse-ancestor test constructor.");
        return (ProviderMigrationService)constructor!.Invoke(
            [workspace, registry, reparseAncestorProbe]);
    }

    private sealed class TestProductProviderResourceRegistry :
        IApplicationProviderResourceRegistry
    {
        private readonly bool refuseAdmission;

        private static readonly Sha256Hash RegistryHash = new(
            new string('A', 64));
        private static readonly Sha256Hash AssetHash = new(
            new string('B', 64));

        public TestProductProviderResourceRegistry(
            bool refuseAdmission = false) =>
            this.refuseAdmission = refuseAdmission;

        public bool TryGetDefaultBlankNpcFixture(
            out ProductFixtureBundleReference? reference)
        {
            reference = new ProductFixtureBundleReference(
                "blank-npc-v1", RegistryHash);
            return true;
        }

        public ApplicationProviderResourceAdmissionResult Admit(
            ProductFixtureBundleReference reference,
            FormId templateNpcFormId,
            GameEdition edition,
            NpcSex sex)
        {
            if (refuseAdmission)
                return new ApplicationProviderResourceAdmissionResult(
                    null,
                    [new Diagnostic(
                        "fixture-product-provider-rejected",
                        DiagnosticSeverity.Error,
                        "The present synthetic provider failed admission.")]);
            ApplicationProviderResourceAuthority File(string role) => new(
                reference.BundleId,
                role,
                new ApplicationResourcePath(Path.Combine(
                    Path.GetTempPath(), "provider-fixture-tests", role)),
                AssetHash,
                reference.RegistryManifestSha256);
            var authority = new ProviderResourceAuthoritySet(
                reference.BundleId,
                reference.RegistryManifestSha256,
                File("provider-manifest"),
                File("template-plugin"),
                File("facegeom-carrier"),
                File("facetint-manifest"),
                new ApplicationProviderResourceAuthority(
                    reference.BundleId,
                    "facetint-provider-root",
                    new ApplicationResourcePath(Path.Combine(
                        Path.GetTempPath(), "provider-fixture-tests", "Data")),
                    AssetHash,
                    reference.RegistryManifestSha256,
                    Directory: true),
                File("facetint-source"),
                File("dependency-manifest"),
                templateNpcFormId,
                edition,
                sex);
            return new ApplicationProviderResourceAdmissionResult(
                authority, []);
        }
    }

    private sealed record BoundFile(string Relative, Sha256Hash Hash);
}
