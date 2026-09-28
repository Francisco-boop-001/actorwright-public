using System.Buffers.Binary;
using System.Collections.Immutable;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimNpcFinishCoreAuthority()
    {
        await using AuthorityFixture fixture =
            await AuthorityFixture.CreateAsync();

        foreach (SkyrimNpcFinishCoreBodyRoute route in Enum.GetValues<SkyrimNpcFinishCoreBodyRoute>())
        {
            SkyrimNpcFinishCoreRequest request = fixture.Request with
            {
                Authorities = fixture.Request.Authorities with { BodyRoute = route }
            };
            SkyrimNpcFinishCoreSourceReadResult result =
                await fixture.Reader.InspectAsync(request, CancellationToken.None);
            Assert(result.Admitted,
                $"The world-clean authority package was refused for {route}: " +
                string.Join(" | ", result.Diagnostics.Select(item => item.Code + ":" + item.Message)));
            Assert(result.PackageTreeSha256 == request.Source.PackageTreeSha256 &&
                   result.PluginSha256 == request.Source.PluginSha256 &&
                   result.BaseNpc == new FormReference(
                       new PluginName("BrigitteBardotNpcManager.esp"),
                       new FormId(0x800)) &&
                   result.TargetEditorId == new EditorId("BrigitteBardotNpcManager") &&
                   result.ActorAssemblyPass && result.PlacementMode == "None" &&
                   result.TypedForbiddenCounts.Values.All(value => value == 0) &&
                   result.RawForbiddenCounts.Values.All(value => value == 0),
                $"The admitted authority snapshot lost a protected identity for {route}.");
        }

        await AssertRefusedAsync(
            fixture,
            fixture.Request with { Source = fixture.Request.Source with
            {
                PackageManifestSha256 = new Sha256Hash(new string('a', 64))
            } },
            "finish-core-source-manifest-hash");

        await AssertRefusedAsync(
            fixture,
            fixture.Request with { Actor = fixture.Request.Actor with
            {
                EditorId = new EditorId("WrongActor")
            } },
            "finish-core-source-base-npc");

        await AssertRefusedAsync(
            fixture,
            fixture.Request with { Authorities = fixture.Request.Authorities with
            {
                Providers = fixture.Request.Authorities.Providers.Add(
                    fixture.Request.Authorities.Providers[0])
            } },
            "finish-core-authority-provider-duplicate");

        await AssertRefusedAsync(
            fixture,
            fixture.Request with { Authorities = fixture.Request.Authorities with
            {
                ActorAssemblySha256 = new Sha256Hash(new string('b', 64))
            } },
            "finish-core-authority-evidence-hash");

        await AssertRefusedAsync(
            fixture,
            fixture.Request with { Authorities = fixture.Request.Authorities with
            {
                BodyOwnerSha256 = new Sha256Hash(new string('c', 64))
            } },
            "finish-core-authority-evidence-hash");

        await AssertRefusedAsync(
            fixture,
            fixture.Request with { OutfitPolicy = fixture.Request.OutfitPolicy with
            {
                ArmorItems = [
                    new FormReference(new PluginName("Skyrim.esm"), new FormId(1)),
                    new FormReference(new PluginName("Skyrim.esm"), new FormId(1))
                ]
            } },
            "finish-core-authority-outfit");

        await AssertRefusedAsync(
            fixture,
            fixture.Request with { Source = fixture.Request.Source with
            {
                PluginSha256 = new Sha256Hash(new string('d', 64))
            } },
            "finish-core-source-plugin-hash");

        await AssertRefusedAsync(
            fixture,
            fixture.Request with { Source = fixture.Request.Source with
            {
                Plugin = new PluginName("WrongSelfKey.esp")
            } },
            "finish-core-source-plugin-path");

        await fixture.SetActorAssemblyAsync(
            Encoding.UTF8.GetBytes("{\"outcome\":\"Pass\",\"placement\":{\"mode\":\"Cell\"}}\n"));
        await AssertRefusedAsync(
            fixture,
            fixture.Request,
            "finish-core-authority-actor-assembly");
        await fixture.SetActorAssemblyAsync(
            Encoding.UTF8.GetBytes("{\"outcome\":\"Pass\",\"placement\":{\"mode\":\"None\"}}\n"));

        SkyrimNpcFinishCoreRequest duplicateInventory = fixture.Request with
        {
            InventoryPolicy = fixture.Request.InventoryPolicy with
            {
                DesiredItems = ["Skyrim.esm|0x00000001", "Skyrim.esm|0x00000001"]
            }
        };
        await AssertRefusedAsync(
            fixture,
            duplicateInventory,
            "finish-core-inventory-duplicate");

        byte[] original = await File.ReadAllBytesAsync(fixture.PluginPath.Value);
        byte[] forbidden = ReplaceRecordSignature(original, 0x800, "NPC_", "ACHR");
        await File.WriteAllBytesAsync(fixture.PluginPath.Value, forbidden);
        SkyrimNpcFinishCoreRequest forbiddenRequest =
            await fixture.RefreshRequestAsync(fixture.Request);
        await AssertRefusedAsync(
            fixture,
            forbiddenRequest,
            "finish-core-source-forbidden-signature");
        await File.WriteAllBytesAsync(fixture.PluginPath.Value, original);

        byte[] compressed = SetRecordFlags(original, 0x800, 0x0004_0000u);
        await File.WriteAllBytesAsync(fixture.PluginPath.Value, compressed);
        SkyrimNpcFinishCoreRequest compressedRequest =
            await fixture.RefreshRequestAsync(fixture.Request);
        await AssertRefusedAsync(
            fixture,
            compressedRequest,
            "finish-core-source-compressed-target");
        await File.WriteAllBytesAsync(fixture.PluginPath.Value, original);

        byte[] duplicate = DuplicateRecord(original, 0x800, "NPC_");
        await File.WriteAllBytesAsync(fixture.PluginPath.Value, duplicate);
        SkyrimNpcFinishCoreRequest duplicateRequest =
            await fixture.RefreshRequestAsync(fixture.Request);
        await AssertRefusedAsync(
            fixture,
            duplicateRequest,
            "finish-core-source-duplicate-base-npc");
        await File.WriteAllBytesAsync(fixture.PluginPath.Value, original);

        AuthorityFixture driftFixture = await AuthorityFixture.CreateAsync();
        try
        {
            driftFixture.Reader = driftFixture.Reader.WithAfterInitialRead(path =>
            {
                if (path == driftFixture.PluginPath)
                    File.AppendAllText(path.Value, "drift", Encoding.UTF8);
            });
            SkyrimNpcFinishCoreSourceReadResult drift =
                await driftFixture.Reader.InspectAsync(
                    driftFixture.Request,
                    CancellationToken.None);
            Assert(!drift.Admitted && drift.Diagnostics.Any(item =>
                       item.Code == "finish-core-source-drift"),
                "Source mutation after the first read was not refused.");
        }
        finally
        {
            await driftFixture.DisposeAsync();
        }
    }

    private static async Task AssertRefusedAsync(
        AuthorityFixture fixture,
        SkyrimNpcFinishCoreRequest request,
        string diagnosticCode)
    {
        SkyrimNpcFinishCoreSourceReadResult result =
            await fixture.Reader.InspectAsync(request, CancellationToken.None);
        Assert(!result.Admitted && result.Diagnostics.Any(item =>
                   item.Code == diagnosticCode),
            $"Hostile authority case was not refused with {diagnosticCode}: " +
            string.Join(" | ", result.Diagnostics.Select(item => item.Code + ":" + item.Message)));
    }

    private sealed class AuthorityFixture : IAsyncDisposable
    {
        private AuthorityFixture(
            string root,
            WorkspacePath packageRoot,
            WorkspacePath pluginPath,
            SkyrimNpcFinishCoreRequest request,
            SkyrimNpcFinishCoreSourcePackageReader reader)
        {
            Root = root;
            PackageRoot = packageRoot;
            PluginPath = pluginPath;
            Request = request;
            Reader = reader;
        }

        public string Root { get; }

        public WorkspacePath PackageRoot { get; }

        public WorkspacePath PluginPath { get; }

        public SkyrimNpcFinishCoreRequest Request { get; private set; }

        public SkyrimNpcFinishCoreSourcePackageReader Reader { get; set; }

        public static async Task<AuthorityFixture> CreateAsync()
        {
            const string sourceZip =
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\04-packages\NpcManagerReimplementation-Brigitte-Bardot-v0.1-slot0-hardened-runtime-test-reviewed.zip";
            string root = Path.Combine(
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work\finish-core-authority-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string packageRootText = Path.Combine(root, "package");
            ZipFile.ExtractToDirectory(sourceZip, packageRootText);
            string packageRoot = Path.GetFullPath(packageRootText);

            byte[] actorAssembly = Encoding.UTF8.GetBytes(
                "{\"outcome\":\"Pass\",\"placement\":{\"mode\":\"None\"}}\n");
            byte[] bodyOwner = Encoding.UTF8.GetBytes("{\"owner\":\"baked\"}\n");
            byte[] protectedTree = Encoding.UTF8.GetBytes("{\"appearance\":\"locked\"}\n");
            await WriteAsync(packageRoot, "evidence/actor-assembly.json", actorAssembly);
            await WriteAsync(packageRoot, "evidence/body-owner.json", bodyOwner);
            await WriteAsync(packageRoot, "evidence/protected-appearance.json", protectedTree);
            await WriteAsync(packageRoot, "providers/Skyrim.esm", [1, 2, 3]);
            await WriteAsync(packageRoot, "providers/Update.esm", [4, 5, 6]);
            await WriteAsync(packageRoot, "evidence/source.json", "{}\n"u8.ToArray());

            string pluginPath = Path.Combine(packageRoot, "BrigitteBardotNpcManager.esp");
            var manifest = new JsonObject
            {
                ["schemaVersion"] = 1,
                ["edition"] = "skyrimse",
                ["presetFormat"] = "finish-core-source",
                ["sourcePreset"] = "evidence/source.json",
                ["sourcePresetSha256"] = HashFile(Path.Combine(packageRoot, "evidence/source.json")).Value,
                ["sourcePlugin"] = @"K:\source\carrier.esp",
                ["sourcePluginSha256"] = new string('2', 64),
                ["outputPlugin"] = "BrigitteBardotNpcManager.esp",
                ["targetFormId"] = "0x00000800",
                ["artifacts"] = new JsonArray()
            };
            JsonArray artifacts = (JsonArray)manifest["artifacts"]!;
            foreach (string file in Directory.EnumerateFiles(packageRoot, "*", SearchOption.AllDirectories)
                         .Where(path => !string.Equals(Path.GetFileName(path), "npcmanager-package.json", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(path => Path.GetRelativePath(packageRoot, path), StringComparer.Ordinal))
            {
                string relative = Path.GetRelativePath(packageRoot, file).Replace('\\', '/');
                byte[] bytes = await File.ReadAllBytesAsync(file);
                string kind = relative.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)
                    ? "plugin"
                    : relative.Contains("actor-assembly", StringComparison.OrdinalIgnoreCase)
                        ? "actor-assembly"
                        : relative.Contains("body-owner", StringComparison.OrdinalIgnoreCase)
                            ? "body-owner"
                            : relative.Contains("protected-appearance", StringComparison.OrdinalIgnoreCase)
                                ? "protected-appearance"
                                : relative.StartsWith("providers/", StringComparison.OrdinalIgnoreCase)
                                    ? "provider"
                                    : "asset";
                artifacts.Add(new JsonObject
                {
                    ["kind"] = kind,
                    ["relativePath"] = relative,
                    ["byteLength"] = bytes.LongLength,
                    ["sha256"] = Convert.ToHexString(SHA256.HashData(bytes))
                });
            }
            string manifestPathText = Path.Combine(packageRoot, "npcmanager-package.json");
            byte[] manifestBytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            await File.WriteAllBytesAsync(manifestPathText, manifestBytes);

            WorkspacePath workspace = new(@"K:\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(workspace, new WorkspacePath(@"F:\ExampleGame"));
            var manifestReader = new PackageManifestReader(policy, workspace);
            var verifier = new PackageVerifyService(manifestReader);
            var reader = new SkyrimNpcFinishCoreSourcePackageReader(
                workspace,
                policy,
                manifestReader,
                verifier,
                new BethesdaSkyrimNpcFinishCoreSourceReader());

            byte[] actorAssemblyHashBytes = actorAssembly;
            byte[] bodyOwnerHashBytes = bodyOwner;
            byte[] protectedTreeHashBytes = protectedTree;
            var pluginName = new PluginName("BrigitteBardotNpcManager.esp");
            var request = new SkyrimNpcFinishCoreRequest
            {
                Source = new SkyrimNpcFinishCoreSource
                {
                    PackageRoot = new WorkspacePath(packageRoot),
                    PackageManifest = new WorkspacePath(manifestPathText),
                    PackageManifestSha256 = HashFile(manifestPathText),
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(
                        new WorkspacePath(packageRoot)),
                    PluginPath = new WorkspacePath(pluginPath),
                    Plugin = pluginName,
                    PluginSha256 = HashFile(pluginPath)
                },
                Actor = new SkyrimNpcFinishCoreActor
                {
                    EditorId = new EditorId("BrigitteBardotNpcManager"),
                    FormId = new FormId(0x800)
                },
                Authorities = new SkyrimNpcFinishCoreAuthorities
                {
                    BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                    Providers = [
                        Provider(packageRoot, "Skyrim.esm", [1, 2, 3]),
                        Provider(packageRoot, "Update.esm", [4, 5, 6])
                    ],
                    ActorAssemblySha256 = HashBytes(actorAssemblyHashBytes),
                    BodyOwnerSha256 = HashBytes(bodyOwnerHashBytes),
                    ProtectedAppearanceTreeSha256 = HashBytes(protectedTreeHashBytes)
                },
                OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
                {
                    Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit,
                    ArmorItems = [
                        new FormReference(new PluginName("Skyrim.esm"), new FormId(1)),
                        new FormReference(new PluginName("Skyrim.esm"), new FormId(2))
                    ]
                },
                InventoryPolicy = new SkyrimNpcFinishCoreInventoryPolicyDocument
                {
                    Policy = SkyrimNpcFinishCoreInventoryPolicy.ReplaceExactInventory,
                    ExpectedSourceItems = ["Skyrim.esm|0x00000001"],
                    DesiredItems = ["Skyrim.esm|0x00000001"]
                },
                SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
                {
                    Template = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                    TemplateEditorId = "DefaultSandboxEditorLocation512"
                },
                Output = new SkyrimNpcFinishCoreOutput
                {
                    Root = new WorkspacePath(Path.Combine(root, "output")),
                    Archive = new WorkspacePath(Path.Combine(root, "output.zip")),
                    PluginFileName = pluginName.Value
                }
            };
            return new AuthorityFixture(root, new WorkspacePath(packageRoot), new WorkspacePath(pluginPath), request, reader);
        }

        public async Task<SkyrimNpcFinishCoreRequest> RefreshRequestAsync(
            SkyrimNpcFinishCoreRequest request)
        {
            string manifestPath = request.Source.PackageManifest!.Value.Value;
            JsonObject manifest =
                JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            JsonArray artifacts = manifest["artifacts"]!.AsArray();
            JsonObject pluginArtifact = artifacts
                .Select(item => item!.AsObject())
                .Single(item => string.Equals(
                    item["relativePath"]!.GetValue<string>(),
                    Path.GetFileName(PluginPath.Value),
                    StringComparison.OrdinalIgnoreCase));
            byte[] pluginBytes = await File.ReadAllBytesAsync(PluginPath.Value);
            pluginArtifact["byteLength"] = pluginBytes.LongLength;
            pluginArtifact["sha256"] = Convert.ToHexString(SHA256.HashData(pluginBytes));
            await File.WriteAllBytesAsync(
                manifestPath,
                JsonSerializer.SerializeToUtf8Bytes(manifest));
            Request = request with
            {
                Source = request.Source with
                {
                    PackageManifestSha256 = HashFile(manifestPath),
                    PluginSha256 = HashFile(PluginPath.Value),
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(PackageRoot)
                }
            };
            return Request;
        }

        public async Task SetActorAssemblyAsync(byte[] bytes)
        {
            string evidencePath = Path.Combine(PackageRoot.Value, "evidence", "actor-assembly.json");
            await File.WriteAllBytesAsync(evidencePath, bytes);
            string manifestPath = Request.Source.PackageManifest!.Value.Value;
            JsonObject manifest =
                JsonNode.Parse(await File.ReadAllTextAsync(manifestPath))!.AsObject();
            JsonObject artifact = manifest["artifacts"]!.AsArray()
                .Select(item => item!.AsObject())
                .Single(item => item["relativePath"]!.GetValue<string>() == "evidence/actor-assembly.json");
            artifact["byteLength"] = bytes.LongLength;
            artifact["sha256"] = Convert.ToHexString(SHA256.HashData(bytes));
            await File.WriteAllBytesAsync(manifestPath, JsonSerializer.SerializeToUtf8Bytes(manifest));
            Request = Request with
            {
                Source = Request.Source with
                {
                    PackageManifestSha256 = HashFile(manifestPath),
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(PackageRoot)
                },
                Authorities = Request.Authorities with
                {
                    ActorAssemblySha256 = HashBytes(bytes)
                }
            };
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, true);
            return ValueTask.CompletedTask;
        }

        private static async Task WriteAsync(string root, string relative, byte[] bytes)
        {
            string path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, bytes);
        }

        private static SkyrimNpcFinishCoreProviderAuthority Provider(
            string root, string plugin, byte[] bytes)
        {
            string path = Path.Combine(root, "providers", plugin);
            return new SkyrimNpcFinishCoreProviderAuthority
            {
                Plugin = new PluginName(plugin),
                Path = new WorkspacePath(path),
                Sha256 = HashBytes(bytes),
                ByteLength = bytes.LongLength
            };
        }

        private static Sha256Hash HashFile(string path) =>
            HashBytes(File.ReadAllBytes(path));

        private static Sha256Hash HashBytes(byte[] bytes) =>
            new(Convert.ToHexString(SHA256.HashData(bytes)));
    }

    private static byte[] ReplaceRecordSignature(
        byte[] source, uint formId, string from, string to)
    {
        byte[] result = source.ToArray();
        int offset = FindRecord(result, formId, from);
        Encoding.ASCII.GetBytes(to).CopyTo(result, offset);
        return result;
    }

    private static byte[] SetRecordFlags(byte[] source, uint formId, uint flags)
    {
        byte[] result = source.ToArray();
        int offset = FindRecord(result, formId, "NPC_");
        uint existing = BinaryPrimitives.ReadUInt32LittleEndian(result.AsSpan(offset + 8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(offset + 8, 4), existing | flags);
        return result;
    }

    private static byte[] DuplicateRecord(byte[] source, uint formId, string signature)
    {
        int offset = FindRecord(source, formId, signature);
        int length = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(source.AsSpan(offset + 4, 4)));
        byte[] result = new byte[source.Length + length];
        Buffer.BlockCopy(source, 0, result, 0, source.Length);
        Buffer.BlockCopy(source, offset, result, source.Length, length);
        return result;
    }

    private static int FindRecord(byte[] bytes, uint formId, string signature)
    {
        int first = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4)));
        int? found = Walk(first, bytes.Length);
        return found ?? throw new InvalidDataException($"Record {signature} {formId:X8} was not found.");

        int? Walk(int start, int end)
        {
            int position = start;
            while (position < end)
            {
                string actual = Encoding.ASCII.GetString(bytes, position, 4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
                if (actual == "GRUP")
                {
                    int groupEnd = checked(position + (int)size);
                    int? nested = Walk(position + 24, groupEnd);
                    if (nested is not null)
                        return nested;
                    position = groupEnd;
                    continue;
                }
                int recordEnd = checked(position + 24 + (int)size);
                uint id = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4));
                if (actual == signature && (id & 0x00FF_FFFFu) == formId)
                    return position;
                position = recordEnd;
            }
            return null;
        }
    }

    private static async Task TestPreview251FinishAuthority()
    {
        WorkspacePath projectRoot = ActorwrightWorkspace.ResolveRoot();
        JsonObject v2 = Preview251RequestJson();
        byte[] v2Bytes = JsonSerializer.SerializeToUtf8Bytes(v2);

        Assert(
            SkyrimNpcFinishCoreRequest.SchemaIdentifier ==
                "npc.finish-core.request.v2" &&
            SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier ==
                "npc.finish-core.request.v1" &&
            SkyrimNpcFinishCoreProposal.SchemaIdentifier ==
                "npc.finish-core.proposal.v2" &&
            SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier ==
                "npc.finish-core.proposal.v1",
            "Finish Core v2 and legacy schema identifiers are not the approved pair.");
        Assert(
            Enum.GetNames<SkyrimNpcFinishCoreMood>().SequenceEqual([
                "Neutral", "Angry", "Fear", "Happy", "Sad", "Surprise",
                "Puzzled", "Disgusted"
            ]),
            "Finish Core mood enum does not expose exactly the eight admitted values.");

        foreach (string mood in Enum.GetNames<SkyrimNpcFinishCoreMood>())
        {
            JsonObject fixture = v2.DeepClone().AsObject();
            fixture["aiPolicy"]!["mood"] = mood;
            SkyrimNpcFinishCoreRequest parsed =
                SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                    JsonSerializer.SerializeToUtf8Bytes(fixture), projectRoot);
            Assert(
                parsed.AiPolicy?.Mood?.ToString() == mood,
                $"Finish Core v2 did not strictly parse mood '{mood}'.");
        }

        JsonObject unknownMood = v2.DeepClone().AsObject();
        unknownMood["aiPolicy"]!["mood"] = "Calm";
        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                JsonSerializer.SerializeToUtf8Bytes(unknownMood), projectRoot));

        JsonObject missingAuthorities = v2.DeepClone().AsObject();
        missingAuthorities["authorities"]!.AsObject().Remove("additionalMasters");
        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(
                JsonSerializer.SerializeToUtf8Bytes(missingAuthorities), projectRoot));
        SkyrimNpcFinishCoreRequest emptyAuthorities =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(v2Bytes, projectRoot);
        Assert(
            emptyAuthorities.Authorities.AdditionalMasters.IsEmpty,
            "A v2 request with an empty additional-master authority was refused.");

        JsonObject legacy = v2.DeepClone().AsObject();
        legacy["schema"] = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier;
        legacy["authorities"]!.AsObject().Remove("additionalMasters");
        legacy["aiPolicy"]!.AsObject().Remove("mood");
        byte[] legacyCanonical = SkyrimNpcFinishCoreDocumentCodec.CanonicalizeRequest(
            JsonSerializer.SerializeToUtf8Bytes(legacy), projectRoot);
        SkyrimNpcFinishCoreRequest legacyRequest =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(legacyCanonical, projectRoot);
        byte[] legacyReplay = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
            legacyRequest, projectRoot);
        Assert(
            legacyRequest.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
            legacyReplay.SequenceEqual(legacyCanonical) &&
            !JsonNode.Parse(legacyReplay)!.AsObject()["authorities"]!
                .AsObject().ContainsKey("additionalMasters") &&
            !JsonNode.Parse(legacyReplay)!.AsObject()["aiPolicy"]!
                .AsObject().ContainsKey("mood"),
            "Legacy Finish Core request replay injected v2 members or changed canonical bytes.");
        var legacyProposal = new SkyrimNpcFinishCoreProposal
        {
            Schema = SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
            Request = legacyRequest,
            RequestSha256 = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                legacyRequest, projectRoot),
            Status = SkyrimNpcFinishCoreStatus.NoChanges
        };
        byte[] legacyProposalBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
            legacyProposal, projectRoot);
        JsonObject proposalJson = JsonNode.Parse(legacyProposalBytes)!.AsObject();
        Assert(
            proposalJson["schema"]!.GetValue<string>() ==
                SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier &&
            proposalJson["request"]!["schema"]!.GetValue<string>() ==
                SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
            !proposalJson["request"]!["authorities"]!.AsObject()
                .ContainsKey("additionalMasters") &&
            !proposalJson["request"]!["aiPolicy"]!.AsObject().ContainsKey("mood"),
            "Legacy proposal embedding changed the retained v1 request shape.");

        JsonObject mismatchedProposal = JsonNode.Parse(legacyProposalBytes)!.AsObject();
        mismatchedProposal["schema"] = SkyrimNpcFinishCoreProposal.SchemaIdentifier;
        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
                JsonSerializer.SerializeToUtf8Bytes(mismatchedProposal), projectRoot));
        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                legacyProposal with { Schema = SkyrimNpcFinishCoreProposal.SchemaIdentifier },
                projectRoot));
        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                legacyRequest with
                {
                    AiPolicy = legacyRequest.AiPolicy! with
                    {
                        Mood = SkyrimNpcFinishCoreMood.Neutral
                    }
                },
                projectRoot));
        AssertThrows<InvalidDataException>(() =>
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                legacyRequest with
                {
                    Authorities = legacyRequest.Authorities with
                    {
                        AdditionalMasters = [new SkyrimNpcFinishCoreAdditionalMasterBinding(
                            new PluginName("AddonA.esm"),
                            new WorkspacePath(Path.Combine(projectRoot.Value, "AddonA.esm")),
                            new Sha256Hash(new string('a', 64)), 1, 7)]
                    }
                },
                projectRoot));

        JsonObject uppercaseLegacy = legacy.DeepClone().AsObject();
        uppercaseLegacy["source"]!["pluginSha256"] = new string('A', 64);
        byte[] uppercaseCanonical = SkyrimNpcFinishCoreDocumentCodec.CanonicalizeRequest(
            JsonSerializer.SerializeToUtf8Bytes(uppercaseLegacy), projectRoot);
        JsonObject uppercaseCanonicalJson = JsonNode.Parse(uppercaseCanonical)!.AsObject();
        Assert(
            uppercaseCanonicalJson["source"]!["pluginSha256"]!.GetValue<string>() ==
                new string('a', 64),
            "Canonical legacy request replay did not normalize SHA-256 through Sha256Hash.");
        SkyrimNpcFinishCoreRequest uppercaseRequest =
            SkyrimNpcFinishCoreDocumentCodec.ParseRequest(uppercaseCanonical, projectRoot);
        Assert(
            SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                uppercaseRequest, projectRoot).AsSpan().SequenceEqual(uppercaseCanonical),
            "Canonical legacy request bytes were not reproduced by its versioned serializer.");

        string fixtureRoot = Path.Combine(
            projectRoot.Value, "artifacts", "finish-core-v2-authority-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixtureRoot);
        try
        {
            WorkspacePath protectedRoot = new(@"F:\ExampleGame");
            var policy = new KOnlyWorkspacePolicy(projectRoot, protectedRoot);
            var fileSystem = new FaceGeomHairRegionsPinnedFileSystem(projectRoot);
            string addonAPath = Path.Combine(fixtureRoot, "AddonA.esm");
            string addonBPath = Path.Combine(fixtureRoot, "AddonB.esm");
            byte[] addonABytes = Preview251Tes4Bytes();
            byte[] addonBBytes = Preview251Tes4Bytes("AddonA.esm");
            await File.WriteAllBytesAsync(addonAPath, addonABytes);
            await File.WriteAllBytesAsync(addonBPath, addonBBytes);
            SkyrimNpcFinishCoreAdditionalMasterBinding bindingA =
                new(new PluginName("AddonA.esm"), new WorkspacePath(addonAPath),
                    Preview251Hash(addonABytes), addonABytes.LongLength, 7);
            SkyrimNpcFinishCoreAdditionalMasterBinding bindingB =
                new(new PluginName("AddonB.esm"), new WorkspacePath(addonBPath),
                    Preview251Hash(addonBBytes), addonBBytes.LongLength, 9);
            var authorityReader = new SkyrimNpcFinishCoreAdditionalMasterAuthorityReader(
                projectRoot, policy, fileSystem);
            SkyrimNpcFinishCoreAuthorityReadResult admitted =
                await authorityReader.ReadAsync([bindingA, bindingB], CancellationToken.None);
            Assert(
                admitted.Admitted && admitted.Masters.Length == 2 &&
                admitted.Masters[0].Plugin == bindingA.Plugin &&
                admitted.Masters[0].MasterDependencies.IsEmpty &&
                admitted.Masters[1].Plugin == bindingB.Plugin &&
                admitted.Masters[1].MasterDependencies.SequenceEqual(
                    [new PluginName("AddonA.esm")]),
                "Physical additional-master authority did not derive TES4 MAST dependencies from retained bytes.");

            AssertThrows<InvalidDataException>(() =>
                BethesdaSkyrimNpcFinishCoreSourceReader.ReadMasterDependencies(
                    Preview251Tes4Subrecords((
                        "MAST", Encoding.UTF8.GetBytes("AddonA.esm\0")))));
            AssertThrows<InvalidDataException>(() =>
                BethesdaSkyrimNpcFinishCoreSourceReader.ReadMasterDependencies(
                    Preview251Tes4Subrecords(("DATA", new byte[8]))));
            AssertThrows<InvalidDataException>(() =>
                BethesdaSkyrimNpcFinishCoreSourceReader.ReadMasterDependencies(
                    Preview251Tes4Subrecords(
                        ("MAST", Encoding.UTF8.GetBytes("AddonA.esm\0trailing")),
                        ("DATA", new byte[8]))));

            SkyrimNpcFinishCoreAuthorityReadResult duplicatePlugin =
                await authorityReader.ReadAsync([
                    bindingA,
                    bindingB with { Plugin = new PluginName("AddonA.esm") }
                ], CancellationToken.None);
            Assert(!duplicatePlugin.Admitted && duplicatePlugin.Diagnostics.Any(item =>
                item.Code == "finish-core-authority-duplicate-plugin"),
                "Duplicate plugin identity was not diagnosed by physical authority.");
            SkyrimNpcFinishCoreAuthorityReadResult duplicatePath =
                await authorityReader.ReadAsync([
                    bindingA,
                    bindingB with { Plugin = new PluginName("Other.esm"), Path = bindingA.Path }
                ], CancellationToken.None);
            Assert(!duplicatePath.Admitted && duplicatePath.Diagnostics.Any(item =>
                item.Code == "finish-core-authority-duplicate-path"),
                "Duplicate physical path identity was not diagnosed.");
            SkyrimNpcFinishCoreAuthorityReadResult duplicateIndex =
                await authorityReader.ReadAsync([
                    bindingA,
                    bindingB with { LoadOrderIndex = bindingA.LoadOrderIndex }
                ], CancellationToken.None);
            Assert(!duplicateIndex.Admitted && duplicateIndex.Diagnostics.Any(item =>
                item.Code == "finish-core-authority-duplicate-index"),
                "Duplicate load-order index identity was not diagnosed.");
            SkyrimNpcFinishCoreAuthorityReadResult missingIdentity =
                await authorityReader.ReadAsync([
                    new SkyrimNpcFinishCoreAdditionalMasterBinding(
                        default, default, default, 0, -1)
                ], CancellationToken.None);
            Assert(!missingIdentity.Admitted && missingIdentity.Diagnostics.Any(item =>
                item.Code == "finish-core-authority-identity-missing"),
                "Missing plugin/path/hash/size/index identity was not diagnosed.");

            byte[] wrongHash = Preview251Tes4Bytes("Wrong.esm");
            SkyrimNpcFinishCoreAuthorityReadResult identityMismatch =
                await authorityReader.ReadAsync([
                    bindingA with { Sha256 = Preview251Hash(wrongHash) }
                ], CancellationToken.None);
            Assert(!identityMismatch.Admitted && identityMismatch.Diagnostics.Any(item =>
                item.Code == "finish-core-authority-identity-mismatch" &&
                item.Message.Contains("expected=", StringComparison.Ordinal) &&
                item.Message.Contains("observed=", StringComparison.Ordinal)),
                "Physical authority identity mismatch lost expected/observed diagnostics.");

            bool replacementBlockedByLease = false;
            var raceReader = new SkyrimNpcFinishCoreAdditionalMasterAuthorityReader(
                projectRoot, policy, fileSystem,
                afterInitialRead: path =>
                {
                    try
                    {
                        string replacement = path.Value + ".replacement";
                        File.WriteAllBytes(replacement, Preview251Tes4Bytes("Race.esm"));
                        try
                        {
                            File.Move(replacement, path.Value, overwrite: true);
                        }
                        finally
                        {
                            if (File.Exists(replacement))
                                File.Delete(replacement);
                        }
                    }
                    catch (IOException)
                    {
                        replacementBlockedByLease = true;
                    }
                    catch (UnauthorizedAccessException)
                    {
                        replacementBlockedByLease = true;
                    }
                });
            SkyrimNpcFinishCoreAuthorityReadResult revalidation =
                await raceReader.ReadAsync([bindingA], CancellationToken.None);
            Assert(revalidation.Admitted && replacementBlockedByLease &&
                   revalidation.Diagnostics.IsEmpty,
                $"The retained no-follow lease did not block replacement cleanly: " +
                $"blocked={replacementBlockedByLease}; admitted={revalidation.Admitted}; " +
                $"diagnostics={string.Join(" | ", revalidation.Diagnostics.Select(item => item.Code + ":" + item.Message))}.");

            SkyrimNpcFinishCoreVerifiedAdditionalMaster[] verified = admitted.Masters.ToArray();
            var planRequest = new SkyrimNpcFinishCoreMasterPlanRequest(
                new PluginName("Source.esp"),
                new PluginName("Output.esp"),
                [new PluginName("Skyrim.esm"), new PluginName("Gap.esm")],
                [new PluginName("AddonB.esm")],
                [verified[1], verified[0]]);
            SkyrimNpcFinishCoreMasterPlan plan =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest);
            Assert(
                plan.Admitted &&
                plan.MasterOrder.SequenceEqual([
                    new PluginName("Skyrim.esm"), new PluginName("Gap.esm"),
                    new PluginName("AddonA.esm"), new PluginName("AddonB.esm")]) &&
                plan.AppendedMasters.SequenceEqual([
                    new PluginName("AddonA.esm"), new PluginName("AddonB.esm")]) &&
                plan.SourceMasterPrefix.SequenceEqual([
                    new PluginName("Skyrim.esm"), new PluginName("Gap.esm")]) &&
                plan.MasterLoadOrderIndexes.SequenceEqual([0, 1, 7, 9]),
                "Pure master planner did not preserve source prefix and deterministic dependency order.");

            SkyrimNpcFinishCoreMasterPlan ownerOnly =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    RequiredReferenceOwners = [new PluginName("FilenameOnly.esp")]
                });
            Assert(!ownerOnly.Admitted && ownerOnly.Diagnostics.Any(item =>
                item.Code == "finish-core-master-owner-unresolved"),
                "Filename/FormID-only required owner was not refused by the planner.");
            SkyrimNpcFinishCoreMasterPlan cycle =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    AdditionalMasters = [
                        verified[0] with { MasterDependencies = [new PluginName("AddonB.esm")] },
                        verified[1]
                    ]
                });
            Assert(!cycle.Admitted && cycle.Diagnostics.Any(item =>
                item.Code == "finish-core-master-cycle"),
                "Master planner accepted a dependency cycle.");
            SkyrimNpcFinishCoreMasterPlan unresolved =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    AdditionalMasters = [verified[1] with
                    {
                        MasterDependencies = [new PluginName("Missing.esm")]
                    }]
                });
            Assert(!unresolved.Admitted && unresolved.Diagnostics.Any(item =>
                item.Code == "finish-core-master-unresolved"),
                "Master planner accepted an unresolved dependency.");
            SkyrimNpcFinishCoreMasterPlan selfDependency =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    AdditionalMasters = [
                        verified[0],
                        verified[1] with
                        {
                            MasterDependencies = [new PluginName("Output.esp")]
                        }]
                });
            Assert(!selfDependency.Admitted && selfDependency.Diagnostics.Any(item =>
                item.Code == "finish-core-master-self-dependency"),
                "Master planner accepted source/output self-dependency.");
            SkyrimNpcFinishCoreMasterPlan lowerIndex =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    AdditionalMasters = [verified[0], verified[1] with { LoadOrderIndex = 3 }]
                });
            Assert(!lowerIndex.Admitted && lowerIndex.Diagnostics.Any(item =>
                item.Code == "finish-core-master-index-order"),
                "Master planner accepted an additional master with an invalid lower-index relation.");

            SkyrimNpcFinishCoreMasterPlan sourceCollision =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    SourceMasters = [new PluginName("AddonA.esm")]
                });
            Assert(!sourceCollision.Admitted && sourceCollision.Diagnostics.Any(item =>
                       item.Message.Contains("AddonA.esm", StringComparison.Ordinal)),
                "Master planner admitted an additional master that collides with the source prefix.");
            SkyrimNpcFinishCoreMasterPlan sourceSelf =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    SourceMasters = [new PluginName("Source.esp")]
                });
            Assert(!sourceSelf.Admitted && sourceSelf.Diagnostics.Any(item =>
                       item.Message.Contains("Source.esp", StringComparison.Ordinal)),
                "Master planner admitted a source-prefix entry equal to SourcePlugin.");
            SkyrimNpcFinishCoreMasterPlan outputSelf =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    SourceMasters = [new PluginName("Output.esp")]
                });
            Assert(!outputSelf.Admitted && outputSelf.Diagnostics.Any(item =>
                       item.Message.Contains("Output.esp", StringComparison.Ordinal)),
                "Master planner admitted a source-prefix entry equal to OutputPlugin.");
            SkyrimNpcFinishCoreMasterPlan nonPositiveLength =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    AdditionalMasters = [verified[0] with { ByteLength = 0 }]
                });
            Assert(!nonPositiveLength.Admitted && nonPositiveLength.Diagnostics.Any(item =>
                       item.Message.Contains("byte length", StringComparison.OrdinalIgnoreCase)),
                "Master planner admitted an additional-master row with non-positive ByteLength.");

            var disconnectedCycleA = new SkyrimNpcFinishCoreVerifiedAdditionalMaster(
                new PluginName("DisconnectedA.esm"),
                new WorkspacePath(Path.Combine(fixtureRoot, "DisconnectedA.esm")),
                new Sha256Hash(new string('e', 64)), 1, 11,
                [new PluginName("DisconnectedB.esm")]);
            var disconnectedCycleB = new SkyrimNpcFinishCoreVerifiedAdditionalMaster(
                new PluginName("DisconnectedB.esm"),
                new WorkspacePath(Path.Combine(fixtureRoot, "DisconnectedB.esm")),
                new Sha256Hash(new string('f', 64)), 1, 12,
                [new PluginName("DisconnectedA.esm")]);
            SkyrimNpcFinishCoreMasterPlan disconnectedCycle =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    RequiredReferenceOwners = [],
                    AdditionalMasters = [
                        verified[0], verified[1], disconnectedCycleA, disconnectedCycleB
                    ]
                });
            Assert(!disconnectedCycle.Admitted && disconnectedCycle.Diagnostics.Any(item =>
                       item.Code == "finish-core-master-cycle"),
                "Master planner ignored a dependency cycle outside the reachable closure.");
            var disconnectedIndexA = disconnectedCycleA with
            {
                Plugin = new PluginName("DisconnectedIndexA.esm"),
                MasterDependencies = [new PluginName("DisconnectedIndexB.esm")],
                LoadOrderIndex = 13
            };
            var disconnectedIndexB = disconnectedCycleB with
            {
                Plugin = new PluginName("DisconnectedIndexB.esm"),
                MasterDependencies = [],
                LoadOrderIndex = 14
            };
            SkyrimNpcFinishCoreMasterPlan disconnectedIndex =
                SkyrimNpcFinishCoreMasterPlanner.Plan(planRequest with
                {
                    RequiredReferenceOwners = [],
                    AdditionalMasters = [
                        verified[0], verified[1], disconnectedIndexA, disconnectedIndexB
                    ]
                });
            Assert(!disconnectedIndex.Admitted && disconnectedIndex.Diagnostics.Any(item =>
                       item.Code == "finish-core-master-index-order"),
                "Master planner ignored dependency index ordering outside the reachable closure.");
        }
        finally
        {
            if (Directory.Exists(fixtureRoot))
                Directory.Delete(fixtureRoot, recursive: true);
        }

        await TestPreview251SourcePackageExactOneAsync(projectRoot);
    }

    private static async Task TestPreview251FinishCoreIntegration()
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        WorkspacePath projectRoot = ActorwrightWorkspace.ResolveRoot();
        await TestPreview251FinishCorePipelineIntegrationAsync(projectRoot);
    }

    private static async Task TestPreview251FinishCorePipelineIntegrationAsync(
        WorkspacePath projectRoot)
    {
        string root = Path.Combine(
            projectRoot.Value, "artifacts", "finish-core-v2-integration-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string sourceRootText = Path.Combine(root, "source-package");
            Directory.CreateDirectory(sourceRootText);
            string pluginPathText = Path.Combine(sourceRootText, "Source.esp");
            byte[] sourceBytes = Preview251SourcePluginBytes("Skyrim.esm", "Gap.esm");
            await File.WriteAllBytesAsync(pluginPathText, sourceBytes);
            string manifestPathText = Path.Combine(sourceRootText, "npcmanager-package.json");
            byte[] manifestBytes = "{}\n"u8.ToArray();
            await File.WriteAllBytesAsync(manifestPathText, manifestBytes);
            string skyrimProviderPathText = Path.Combine(sourceRootText, "Skyrim.esm");
            string gapProviderPathText = Path.Combine(sourceRootText, "Gap.esm");
            byte[] skyrimProviderBytes = Preview251Tes4Bytes();
            byte[] gapProviderBytes = Preview251Tes4Bytes();
            await File.WriteAllBytesAsync(skyrimProviderPathText, skyrimProviderBytes);
            await File.WriteAllBytesAsync(gapProviderPathText, gapProviderBytes);
            AddPreview251OutfitRecords(skyrimProviderPathText, "Skyrim.esm");
            skyrimProviderBytes = await File.ReadAllBytesAsync(skyrimProviderPathText);

            string addonAPath = Path.Combine(root, "AddonA.esm");
            string addonBPath = Path.Combine(root, "AddonB.esm");
            byte[] addonABytes = Preview251Tes4Bytes();
            byte[] addonBBytes = Preview251Tes4Bytes("AddonA.esm");
            await File.WriteAllBytesAsync(addonAPath, addonABytes);
            await File.WriteAllBytesAsync(addonBPath, addonBBytes);
            AddPreview251OutfitRecords(addonBPath, "AddonB.esm");
            addonBBytes = await File.ReadAllBytesAsync(addonBPath);
            string copiedAddonBPath = Path.Combine(sourceRootText, "AddonB.esm");
            await File.WriteAllBytesAsync(copiedAddonBPath, addonBBytes);
            string sandboxTemplatePath = Path.Combine(root, "Skyrim.esm");
            SkyrimFollowerFinishCoreFixture.WriteCanonicalFollowerFinishTemplateMaster(
                sandboxTemplatePath);
            AddPreview251OutfitRecords(sandboxTemplatePath, "Skyrim.esm");
            byte[] sandboxTemplateBytes =
                await File.ReadAllBytesAsync(sandboxTemplatePath);

            var policy = new KOnlyWorkspacePolicy(
                projectRoot, new WorkspacePath(@"F:\ExampleGame"));
            WorkspacePath sourceRoot = new(sourceRootText);
            WorkspacePath pluginPath = new(pluginPathText);
            WorkspacePath manifestPath = new(manifestPathText);
            WorkspacePath skyrimProviderPath = new(skyrimProviderPathText);
            WorkspacePath gapProviderPath = new(gapProviderPathText);
            Sha256Hash sourceHash = Preview251Hash(sourceBytes);
            Sha256Hash manifestHash = Preview251Hash(manifestBytes);
            Sha256Hash skyrimProviderHash = Preview251Hash(skyrimProviderBytes);
            Sha256Hash gapProviderHash = Preview251Hash(gapProviderBytes);
            ImmutableArray<PackageFileVerification> packageFiles = [
                new PackageFileVerification("provider", new AssetPath("AddonB.esm"), addonBBytes.LongLength,
                    addonBBytes.LongLength, Preview251Hash(addonBBytes), Preview251Hash(addonBBytes), true),
                new PackageFileVerification(
                    "plugin", new AssetPath("Source.esp"), sourceBytes.LongLength,
                    sourceBytes.LongLength, sourceHash, sourceHash, true),
                new PackageFileVerification(
                    "provider", new AssetPath("Skyrim.esm"), skyrimProviderBytes.LongLength,
                    skyrimProviderBytes.LongLength, skyrimProviderHash, skyrimProviderHash, true),
                new PackageFileVerification(
                    "provider", new AssetPath("Gap.esm"), gapProviderBytes.LongLength,
                    gapProviderBytes.LongLength, gapProviderHash, gapProviderHash, true)
            ];
            var packageArtifact = new PackageVerificationArtifact(
                "1", "npcmanager-package-verification", "skyrimSpecialEdition",
                "finish-core-source", "Source.esp", new FormId(0x800), manifestPath,
                manifestHash, packageFiles, true, true, false);

            var packageReader = new SkyrimNpcFinishCoreSourcePackageReader(
                projectRoot,
                policy,
                new PackageManifestReader(policy, projectRoot),
                new Preview251PackageVerifier(packageArtifact),
                new BethesdaSkyrimNpcFinishCoreSourceReader());
            var bindings = ImmutableArray.Create(
                new SkyrimNpcFinishCoreAdditionalMasterBinding(
                    new PluginName("AddonA.esm"), new WorkspacePath(addonAPath),
                    Preview251Hash(addonABytes), addonABytes.LongLength, 7),
                new SkyrimNpcFinishCoreAdditionalMasterBinding(
                    new PluginName("AddonB.esm"), new WorkspacePath(addonBPath),
                    Preview251Hash(addonBBytes), addonBBytes.LongLength, 9));
            WorkspacePath proposalPath = new(Path.Combine(root, "proposal.json"));
            var request = new SkyrimNpcFinishCoreRequest
            {
                Source = new SkyrimNpcFinishCoreSource
                {
                    PackageRoot = sourceRoot,
                    PackageManifest = manifestPath,
                    PackageManifestSha256 = manifestHash,
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader
                        .ComputePackageTreeSha256(sourceRoot),
                    PluginPath = pluginPath,
                    Plugin = new PluginName("Source.esp"),
                    PluginSha256 = sourceHash
                },
                Actor = new SkyrimNpcFinishCoreActor
                {
                    EditorId = new EditorId("Preview251IntegrationActor"),
                    FormId = new FormId(0x800)
                },
                Authorities = new SkyrimNpcFinishCoreAuthorities
                {
                    BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                    Providers = [
                        new SkyrimNpcFinishCoreProviderAuthority
                        {
                            Plugin = new PluginName("AddonB.esm"), Path = new WorkspacePath(copiedAddonBPath),
                            Sha256 = Preview251Hash(addonBBytes), ByteLength = addonBBytes.LongLength
                        },
                        new SkyrimNpcFinishCoreProviderAuthority
                        {
                            Plugin = new PluginName("Skyrim.esm"),
                            Path = skyrimProviderPath,
                            Sha256 = skyrimProviderHash,
                            ByteLength = skyrimProviderBytes.LongLength
                        },
                        new SkyrimNpcFinishCoreProviderAuthority
                        {
                            Plugin = new PluginName("Gap.esm"),
                            Path = gapProviderPath,
                            Sha256 = gapProviderHash,
                            ByteLength = gapProviderBytes.LongLength
                        }
                    ],
                    AdditionalMasters = bindings
                },
                FollowerPolicy = new SkyrimNpcFinishCoreFollowerPolicy
                {
                    Recruitable = false,
                    DefensiveOnly = true,
                    RelationshipRank = "Ally"
                },
                    AiPolicy = new SkyrimNpcFinishCoreAiPolicy
                {
                    Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                    Confidence = SkyrimNpcFinishCoreConfidence.Brave,
                    Energy = 50,
                    Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                    Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies,
                    Mood = SkyrimNpcFinishCoreMood.Neutral
                },
                OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
                {
                    Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit,
                    ArmorItems = [
                        new FormReference(new PluginName("AddonB.esm"), new FormId(0x800))
                    ]
                },
                InventoryPolicy = new SkyrimNpcFinishCoreInventoryPolicyDocument
                {
                    Policy = SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory
                },
                SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
                {
                    Template = new FormReference(
                        new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                    TemplateEditorId = "DefaultSandboxEditorLocation512"
                },
                Output = new SkyrimNpcFinishCoreOutput
                {
                    Root = new WorkspacePath(Path.Combine(root, "output")),
                    Archive = new WorkspacePath(Path.Combine(root, "output.zip")),
                    // Finish Core preserves the authentic source/output identity.
                    PluginFileName = "Source.esp"
                }
            };

            string invalidAdditionalMasterPath = Path.Combine(root, "Invalid.esm");
            byte[] invalidAdditionalMasterBytes = [0x01, 0x02, 0x03];
            await File.WriteAllBytesAsync(
                invalidAdditionalMasterPath, invalidAdditionalMasterBytes);
            SkyrimNpcFinishCoreAdditionalMasterBinding invalidAdditionalMaster =
                new(
                    new PluginName("Invalid.esm"),
                    new WorkspacePath(invalidAdditionalMasterPath),
                    Preview251Hash(invalidAdditionalMasterBytes),
                    invalidAdditionalMasterBytes.LongLength,
                    11);

            // This is deliberately the public five-argument composition.  It
            // must admit the fixed package verifier and refuse malformed TES4
            // authority before the source plugin reader is reached.
            byte[] invalidSourcePluginBytes = [0x01, 0x02, 0x03];
            ImmutableArray<PackageFileVerification> invalidPackageFiles =
                packageFiles.Select(file =>
                    string.Equals(file.Kind, "plugin", StringComparison.OrdinalIgnoreCase)
                        ? file with
                        {
                            ExpectedByteLength = invalidSourcePluginBytes.LongLength,
                            ActualByteLength = invalidSourcePluginBytes.LongLength,
                            ExpectedSha256 = Preview251Hash(invalidSourcePluginBytes),
                            ActualSha256 = Preview251Hash(invalidSourcePluginBytes),
                            Matches = true
                        }
                        : file).ToImmutableArray();
            await File.WriteAllBytesAsync(pluginPath.Value, invalidSourcePluginBytes);
            try
            {
                var defaultWiringReader = new SkyrimNpcFinishCoreSourcePackageReader(
                    projectRoot,
                    policy,
                    new PackageManifestReader(policy, projectRoot),
                    new Preview251PackageVerifier(
                        packageArtifact with { Files = invalidPackageFiles }),
                    new BethesdaSkyrimNpcFinishCoreSourceReader());
                SkyrimNpcFinishCoreRequest invalidAuthorityRequest = request with
                {
                    Source = request.Source with
                    {
                        PluginSha256 = Preview251Hash(invalidSourcePluginBytes),
                        PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader
                            .ComputePackageTreeSha256(sourceRoot)
                    },
                    Authorities = request.Authorities with
                    {
                        AdditionalMasters = [invalidAdditionalMaster]
                    }
                };
                SkyrimNpcFinishCoreSourceReadResult defaultWiring =
                    await defaultWiringReader.InspectAsync(
                        invalidAuthorityRequest, CancellationToken.None);
                Assert(
                    !defaultWiring.Admitted &&
                    defaultWiring.Diagnostics.Count(item =>
                        item.Code == "finish-core-authority-tes4-invalid") == 1 &&
                    !defaultWiring.Diagnostics.Any(item =>
                        item.Code == "finish-core-source-plugin-raw-invalid"),
                    "The public five-argument SourcePackageReader did not refuse malformed additional-master TES4 authority before source plugin inspection: " +
                    string.Join(" | ", defaultWiring.Diagnostics.Select(item =>
                        item.Code + ":" + item.Message)));
            }
            finally
            {
                await File.WriteAllBytesAsync(pluginPath.Value, sourceBytes);
            }

            int inspectionCount = 0;

            async ValueTask<SkyrimNpcFinishCoreSourceReadResult> InspectAsync(
                SkyrimNpcFinishCoreRequest inspectedRequest,
                CancellationToken cancellationToken)
            {
                inspectionCount++;
                return await packageReader.InspectAsync(
                    inspectedRequest, cancellationToken);
            }

            var service = new SkyrimNpcFinishCoreService(InspectAsync, projectRoot);
            Sha256Hash requestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                request, projectRoot);

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(() =>
                    packageReader.InspectAsync(request, cancellation.Token).AsTask());
                Assert(
                    !Directory.Exists(request.Output.Root!.Value.Value) &&
                    !File.Exists(request.Output.Archive!.Value.Value) &&
                    !Directory.EnumerateFileSystemEntries(
                        root, ".finish-core-transaction-*", SearchOption.TopDirectoryOnly).Any(),
                    "Cancelled source admission created Finish Core output or transaction artifacts.");
            }

            SkyrimNpcFinishCoreProposalResult analyzed = await service.AnalyzeAsync(
                request, requestSha, proposalPath, CancellationToken.None);
            Assert(
                analyzed.Proposed && analyzed.Proposal is not null &&
                analyzed.Proposal.MasterOrder.SequenceEqual([
                    "Skyrim.esm", "Gap.esm", "AddonA.esm", "AddonB.esm"
                ]) &&
                analyzed.Proposal.AppendedMasters.SequenceEqual([
                    "AddonA.esm", "AddonB.esm"
                ]) && inspectionCount == 1,
                $"Analyze did not re-admit physical additional masters and apply the shared deterministic plan: " +
                $"proposed={analyzed.Proposed}; diagnostics={string.Join(" | ", analyzed.Diagnostics.Select(item => item.Code + ":" + item.Message))}; " +
                $"masterOrder=[{string.Join(",", analyzed.Proposal?.MasterOrder.ToArray() ?? Array.Empty<string>())}]; " +
                $"appended=[{string.Join(",", analyzed.Proposal?.AppendedMasters.ToArray() ?? Array.Empty<string>())}]; " +
                $"inspections={inspectionCount}.");

            SkyrimNpcFinishCoreValidationResult applyValidation =
                await service.ValidateApplyAsync(
                    request,
                    requestSha,
                    analyzed.Proposal!,
                    analyzed.ProposalSha256!.Value,
                    CancellationToken.None);
            SkyrimNpcFinishCoreValidationPhase proposalDerivation =
                applyValidation.Phases.Single(phase => phase.Name == "proposal-derivation");
            Assert(
                proposalDerivation.State == SkyrimNpcFinishCoreValidationPhaseState.Reached &&
                !proposalDerivation.Diagnostics.Any(item =>
                    item.Code == "finish-core-apply-stale-proposal" ||
                    item.Code.StartsWith("finish-core-master-", StringComparison.Ordinal)) &&
                applyValidation.Phases.Count(phase => phase.Name == "disposable-write") == 1,
                "ValidateApply reused the old direct-owner derivation instead of the clean analyzed plan, or merged the disposable Task3 boundary: " +
                string.Join(" | ", proposalDerivation.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));

            SkyrimNpcFinishCoreRequest malformedInventoryRequest = request with
            {
                InventoryPolicy = request.InventoryPolicy with
                {
                    Policy = SkyrimNpcFinishCoreInventoryPolicy.ReplaceExactInventory,
                    DesiredItems = ["InvalidOwner|0x00000100"]
                }
            };
            SkyrimNpcFinishCoreProposalResult? malformedInventory = null;
            Exception? malformedInventoryException = null;
            try
            {
                malformedInventory = await service.AnalyzeAsync(
                    malformedInventoryRequest,
                    SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                        malformedInventoryRequest, projectRoot),
                    new WorkspacePath(Path.Combine(root, "malformed-inventory.json")),
                    CancellationToken.None);
            }
            catch (Exception exception)
            {
                malformedInventoryException = exception;
            }
            Assert(
                malformedInventoryException is null &&
                malformedInventory is not null &&
                !malformedInventory.Proposed &&
                malformedInventory.Diagnostics.Any(item =>
                    (item.Code.StartsWith("finish-core-master-", StringComparison.Ordinal) ||
                     item.Code.StartsWith("finish-core-source-", StringComparison.Ordinal)) &&
                    item.Message.Contains("InvalidOwner", StringComparison.Ordinal)),
                "Analyze escaped or failed to structure a malformed inventory owner: " +
                (malformedInventoryException is { } capturedException
                    ? capturedException.GetType().Name + ":" + capturedException.Message
                    : string.Join(" | ", malformedInventory?.Diagnostics.Select(item =>
                        item.Code + ":" + item.Message) ?? Enumerable.Empty<string>())));

            SkyrimNpcFinishCoreRequest legacyRequest = request with
            {
                Schema = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
                AiPolicy = request.AiPolicy! with { Mood = null },
                Authorities = request.Authorities with
                {
                    AdditionalMasters = ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>.Empty
                },
                OutfitPolicy = request.OutfitPolicy with
                {
                    ArmorItems = [
                        new FormReference(new PluginName("Skyrim.esm"), new FormId(0x800))
                    ]
                }
            };
            SkyrimNpcFinishCoreProposalResult legacy = await service.AnalyzeAsync(
                legacyRequest,
                SkyrimNpcFinishCoreDocumentCodec.HashRequest(legacyRequest, projectRoot),
                new WorkspacePath(Path.Combine(root, "legacy-proposal.json")),
                CancellationToken.None);
            WorkspacePath legacyProposalPath = new(Path.Combine(root, "legacy-proposal.json"));
            byte[] legacyProposalBytes = await File.ReadAllBytesAsync(legacyProposalPath.Value);
            SkyrimNpcFinishCoreProposal reopenedLegacy =
                SkyrimNpcFinishCoreDocumentCodec.ParseProposal(legacyProposalBytes, projectRoot);
            Assert(
                legacy.Proposed && legacy.Proposal is not null &&
                legacy.Proposal.Schema == SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier &&
                legacy.Proposal.Request?.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
                reopenedLegacy.Schema == SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier &&
                reopenedLegacy.Request?.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(reopenedLegacy, projectRoot)
                    .AsSpan().SequenceEqual(legacyProposalBytes) &&
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(legacyProposalBytes) ==
                    legacy.ProposalSha256 &&
                !Encoding.UTF8.GetString(legacyProposalBytes).Contains(
                    "additionalMasters", StringComparison.Ordinal) &&
                !Encoding.UTF8.GetString(legacyProposalBytes).Contains(
                    "\"mood\"", StringComparison.Ordinal),
                "Analyze did not preserve the v1 request/proposal schema pair and exact legacy replay.");

            SkyrimNpcFinishCoreRequest legacyLogicalRequest = request with
            {
                Schema = SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
                AiPolicy = request.AiPolicy! with { Mood = null },
                Authorities = request.Authorities with
                {
                    AdditionalMasters = ImmutableArray<SkyrimNpcFinishCoreAdditionalMasterBinding>.Empty
                },
                OutfitPolicy = request.OutfitPolicy with
                {
                    ArmorItems = [
                        new FormReference(new PluginName("AddonB.esm"), new FormId(0x800))
                    ]
                },
                InventoryPolicy = request.InventoryPolicy with
                {
                    DesiredItems = ["AddonB.esm|0x00000800"]
                },
                SandboxAuthority = request.SandboxAuthority with
                {
                    Template = new FormReference(
                        new PluginName("AddonB.esm"), new FormId(0x1B217))
                }
            };
            Sha256Hash legacyLogicalRequestSha =
                SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                    legacyLogicalRequest, projectRoot);
            WorkspacePath legacyLogicalProposalPath = new(
                Path.Combine(root, "legacy-logical-proposal.json"));
            SkyrimNpcFinishCoreProposalResult legacyLogical =
                await service.AnalyzeAsync(
                    legacyLogicalRequest,
                    legacyLogicalRequestSha,
                    legacyLogicalProposalPath,
                    CancellationToken.None);
            Assert(
                legacyLogical.Proposed && legacyLogical.Proposal is not null &&
                legacyLogical.Proposal.Schema == SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier &&
                legacyLogical.Proposal.Request?.Schema ==
                    SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
                legacyLogical.Proposal.MasterOrder.SequenceEqual([
                    "Skyrim.esm", "Gap.esm", "AddonB.esm"
                ]) &&
                legacyLogical.Proposal.AppendedMasters.SequenceEqual(["AddonB.esm"]),
                "Legacy v1 logical master planning did not append the first-seen AddonB.esm owner: " +
                string.Join(" | ", legacyLogical.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));
            byte[] legacyLogicalProposalBytes =
                await File.ReadAllBytesAsync(legacyLogicalProposalPath.Value);
            SkyrimNpcFinishCoreProposal reopenedLegacyLogical =
                SkyrimNpcFinishCoreDocumentCodec.ParseProposal(
                    legacyLogicalProposalBytes, projectRoot);
            string legacyLogicalText =
                Encoding.UTF8.GetString(legacyLogicalProposalBytes);
            Assert(
                reopenedLegacyLogical.Request?.Schema ==
                    SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier &&
                reopenedLegacyLogical.Request.Authorities.AdditionalMasters.IsEmpty &&
                !legacyLogicalText.Contains("additionalMasters", StringComparison.Ordinal) &&
                !legacyLogicalText.Contains("\"mood\"", StringComparison.Ordinal) &&
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        reopenedLegacyLogical, projectRoot)
                    .AsSpan().SequenceEqual(legacyLogicalProposalBytes),
                "Legacy v1 logical proposal replay injected v2 authority members or changed canonical bytes.");

            SkyrimNpcFinishCoreValidationResult legacyLogicalApplyValidation =
                await service.ValidateApplyAsync(
                    legacyLogicalRequest,
                    legacyLogicalRequestSha,
                    legacyLogical.Proposal!,
                    legacyLogical.ProposalSha256!.Value,
                    CancellationToken.None);
            SkyrimNpcFinishCoreValidationPhase legacyLogicalDerivation =
                legacyLogicalApplyValidation.Phases.Single(phase =>
                    phase.Name == "proposal-derivation");
            Assert(
                legacyLogicalDerivation.State ==
                    SkyrimNpcFinishCoreValidationPhaseState.Reached &&
                !legacyLogicalDerivation.Diagnostics.Any(item =>
                    item.Code.StartsWith("finish-core-master-", StringComparison.Ordinal)) &&
                legacyLogicalApplyValidation.Phases.Count(phase =>
                    phase.Name == "disposable-write") == 1 &&
                !Directory.Exists(legacyLogicalRequest.Output.Root!.Value.Value) &&
                !File.Exists(legacyLogicalRequest.Output.Archive!.Value.Value) &&
                !Directory.EnumerateFileSystemEntries(
                    root, ".finish-core-transaction-*").Any(),
                "ValidateApply did not accept the same v1 logical master plan before the expected later boundary: " +
                string.Join(" | ", legacyLogicalDerivation.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));

            byte[] driftedSourceBytes =
                Preview251SourcePluginBytes("Skyrim.esm", "Drift.esm");
            await File.WriteAllBytesAsync(pluginPath.Value, driftedSourceBytes);
            SkyrimNpcFinishCoreRequest driftedRequest = request with
            {
                Source = request.Source with
                {
                    PluginSha256 = Preview251Hash(driftedSourceBytes),
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader
                        .ComputePackageTreeSha256(sourceRoot)
                }
            };
            Sha256Hash driftedRequestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                driftedRequest, projectRoot);
            SkyrimNpcFinishCoreProposal stalePlanProposal = analyzed.Proposal! with
            {
                Request = driftedRequest,
                RequestSha256 = driftedRequestSha,
                ProposalSha256 = null
            };
            Sha256Hash stalePlanProposalSha =
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        stalePlanProposal, projectRoot));
            stalePlanProposal = stalePlanProposal with
            {
                ProposalSha256 = stalePlanProposalSha
            };
            SkyrimNpcFinishCoreApplyResult stalePlan = await service.ApplyAsync(
                driftedRequest, driftedRequestSha, stalePlanProposal, stalePlanProposalSha,
                CancellationToken.None);
            Assert(
                !stalePlan.Applied &&
                stalePlan.Diagnostics.Any(item =>
                    item.Code == "finish-core-apply-stale-proposal") &&
                !Directory.Exists(request.Output.Root!.Value.Value) &&
                !File.Exists(request.Output.Archive!.Value.Value) &&
                !Directory.EnumerateFileSystemEntries(
                    root, ".finish-core-transaction-*").Any(),
                "Apply accepted a physical source-master-order drift or left transaction artifacts before write.");
            await File.WriteAllBytesAsync(pluginPath.Value, sourceBytes);

            SkyrimNpcFinishCoreProposal tamperedProposal = analyzed.Proposal! with
            {
                MasterOrder = ["Skyrim.esm", "Gap.esm", "Tampered.esm"]
            };
            SkyrimNpcFinishCoreApplyResult staleHash = await service.ApplyAsync(
                request, requestSha, tamperedProposal, analyzed.ProposalSha256!.Value,
                CancellationToken.None);
            Assert(
                !staleHash.Applied &&
                staleHash.Diagnostics.Any(item =>
                    item.Code == "finish-core-apply-proposal-hash") &&
                !Directory.Exists(request.Output.Root!.Value.Value) &&
                !File.Exists(request.Output.Archive!.Value.Value) &&
                !Directory.EnumerateFileSystemEntries(
                    root, ".finish-core-transaction-*").Any(),
                "Apply accepted a proposal hash/order drift instead of refusing before write.");

            await File.WriteAllBytesAsync(
                addonBPath, Preview251Tes4Bytes("AddonA.esm", "DependencyDrift.esm"));
            SkyrimNpcFinishCoreApplyResult staleAuthority = await service.ApplyAsync(
                request, requestSha, analyzed.Proposal!, analyzed.ProposalSha256!.Value,
                CancellationToken.None);
            Assert(
                !staleAuthority.Applied &&
                staleAuthority.Diagnostics.Any(item =>
                    item.Code == "finish-core-authority-identity-mismatch") &&
                !Directory.Exists(request.Output.Root!.Value.Value) &&
                !File.Exists(request.Output.Archive!.Value.Value) &&
                !Directory.EnumerateFileSystemEntries(
                    root, ".finish-core-transaction-*").Any(),
                "Apply accepted a physically changed TES4 dependency authority before write.");

            byte[] restoredAddonB = addonBBytes;
            await File.WriteAllBytesAsync(addonBPath, restoredAddonB);

            SkyrimNpcFinishCoreProposal oldPlanApplyProposal = analyzed.Proposal! with
            {
                MasterOrder = ["Skyrim.esm", "Gap.esm", "AddonB.esm"],
                AppendedMasters = ["AddonB.esm"],
                ProposalSha256 = null
            };
            Sha256Hash oldPlanApplyProposalSha =
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        oldPlanApplyProposal, projectRoot));
            oldPlanApplyProposal = oldPlanApplyProposal with
            {
                ProposalSha256 = oldPlanApplyProposalSha
            };
            SkyrimNpcFinishCoreApplyResult oldPlanApply = await service.ApplyAsync(
                request,
                requestSha,
                oldPlanApplyProposal,
                oldPlanApplyProposalSha,
                CancellationToken.None);
            Assert(
                !oldPlanApply.Applied &&
                oldPlanApply.Diagnostics.Select(item => item.Code).SequenceEqual([
                    "finish-core-apply-stale-proposal"
                ]) &&
                !Directory.Exists(request.Output.Root!.Value.Value) &&
                !File.Exists(request.Output.Archive!.Value.Value) &&
                !Directory.EnumerateFileSystemEntries(
                    root, ".finish-core-transaction-*", SearchOption.TopDirectoryOnly).Any(),
                "Apply accepted a canonical old direct-owner plan or reached the writer/binary seam: " +
                string.Join(" | ", oldPlanApply.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));

            using (var cancellation = new CancellationTokenSource())
            {
                cancellation.Cancel();
                await AssertThrowsAsync<OperationCanceledException>(() =>
                    service.AnalyzeAsync(
                        request,
                        requestSha,
                        new WorkspacePath(Path.Combine(root, "cancel-analyze.json")),
                        cancellation.Token).AsTask());
                await AssertThrowsAsync<OperationCanceledException>(() =>
                    service.ApplyAsync(
                        request,
                        requestSha,
                        analyzed.Proposal!,
                        analyzed.ProposalSha256!.Value,
                        cancellation.Token).AsTask());
                Assert(
                    !Directory.Exists(request.Output.Root!.Value.Value) &&
                    !File.Exists(request.Output.Archive!.Value.Value) &&
                    !Directory.EnumerateFileSystemEntries(
                        root, ".finish-core-transaction-*", SearchOption.TopDirectoryOnly).Any(),
                    "Cancelled Analyze/Apply created Finish Core output or transaction artifacts.");
            }

            await TestPreview251FinishCoreVerifyPlanBoundaryAsync(
                projectRoot,
                root,
                service,
                request,
                analyzed.Proposal!,
                sandboxTemplatePath,
                sandboxTemplateBytes,
                addonBPath,
                restoredAddonB,
                sourceBytes,
                () => inspectionCount);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task TestPreview251FinishCoreVerifyPlanBoundaryAsync(
        WorkspacePath projectRoot,
        string root,
        SkyrimNpcFinishCoreService service,
        SkyrimNpcFinishCoreRequest request,
        SkyrimNpcFinishCoreProposal proposal,
        string copiedMasterPath,
        byte[] copiedMasterBytes,
        string additionalMasterPath,
        byte[] restoredAdditionalMasterBytes,
        byte[] sourceBytes,
        Func<int> inspectionCount)
    {
        string outputRootText = Path.Combine(root, "verify-output");
        string archivePathText = Path.Combine(root, "verify-output.zip");
        string evidenceRoot = Path.Combine(outputRootText, "NPCManager", "Evidence");
        Directory.CreateDirectory(evidenceRoot);
        string manifestPathText = Path.Combine(
            evidenceRoot, "finish-core-manifest.json");
        string requestEvidencePath = Path.Combine(
            evidenceRoot, "finish-core-request.json");
        string proposalEvidencePath = Path.Combine(
            evidenceRoot, "finish-core-proposal.json");
        string runtimeIdentityEvidencePath = Path.Combine(
            evidenceRoot, "runtime-identities.json");
        string diagEvidencePath = Path.Combine(
            evidenceRoot, "diag-Preview251IntegrationActor.txt");

        SkyrimNpcFinishCoreEvidenceEntry EvidenceEntry(
            string relativePath,
            string physicalPath)
        {
            byte[] bytes = File.ReadAllBytes(physicalPath);
            return new SkyrimNpcFinishCoreEvidenceEntry
            {
                Path = new AssetPath(relativePath),
                ByteLength = bytes.LongLength,
                Sha256 = Preview251Hash(bytes)
            };
        }

        SkyrimNpcFinishCoreManifestEvidence BuildManifestEvidence(
            Sha256Hash packageTreeSha256,
            Sha256Hash sourcePackageTreeSha256) =>
            new()
            {
                Files = [
                    EvidenceEntry(
                        "NPCManager/Evidence/finish-core-request.json",
                        requestEvidencePath),
                    EvidenceEntry(
                        "NPCManager/Evidence/finish-core-proposal.json",
                        proposalEvidencePath),
                    EvidenceEntry(
                        "NPCManager/Evidence/runtime-identities.json",
                        runtimeIdentityEvidencePath),
                    EvidenceEntry(
                        "NPCManager/Evidence/diag-Preview251IntegrationActor.txt",
                        diagEvidencePath)
                ],
                PackageTreeSha256 = packageTreeSha256,
                SourcePackageTreeSha256 = sourcePackageTreeSha256
            };
        try
        {
            var verifyRequest = request with
            {
                SandboxAuthority = request.SandboxAuthority with
                {
                    CopiedMaster = new WorkspacePath(copiedMasterPath),
                    CopiedMasterSha256 = Preview251Hash(copiedMasterBytes)
                },
                Output = request.Output with
                {
                    Root = new WorkspacePath(outputRootText),
                    Archive = new WorkspacePath(archivePathText)
                }
            };
            Sha256Hash verifyRequestSha =
                SkyrimNpcFinishCoreDocumentCodec.HashRequest(verifyRequest, projectRoot);
            SkyrimNpcFinishCoreProposal verifyProposal = proposal with
            {
                Request = verifyRequest,
                RequestSha256 = verifyRequestSha,
                ProposalSha256 = null
            };
            Sha256Hash verifyProposalSha =
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        verifyProposal, projectRoot));
            verifyProposal = verifyProposal with
            {
                ProposalSha256 = verifyProposalSha
            };
            await File.WriteAllBytesAsync(
                requestEvidencePath,
                SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                    verifyRequest, projectRoot));
            await File.WriteAllBytesAsync(
                proposalEvidencePath,
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    verifyProposal, projectRoot));
            await File.WriteAllTextAsync(
                Path.Combine(evidenceRoot, "finish-core-verification.json"), "{}\n");
            await File.WriteAllTextAsync(
                runtimeIdentityEvidencePath, "{}\n");
            await File.WriteAllTextAsync(
                diagEvidencePath,
                "help \"Preview251IntegrationActor\" 4\r\n");

            string outputPluginPath = Path.Combine(outputRootText, "Source.esp");
            byte[] validOutputBytes = new BethesdaSkyrimNpcFinishCoreWriter().Write(
                verifyRequest.Source.PluginPath!.Value,
                verifyProposal,
                new WorkspacePath(copiedMasterPath),
                CancellationToken.None);
            await File.WriteAllBytesAsync(outputPluginPath, validOutputBytes);
            byte[] persistedOutputBytes = await File.ReadAllBytesAsync(outputPluginPath);
            int generatedOutputNpcCount = BethesdaSkyrimNpcFinishCoreRaw.CountRecords(
                validOutputBytes, "NPC_", 0x800);
            int persistedOutputNpcCount = BethesdaSkyrimNpcFinishCoreRaw.CountRecords(
                persistedOutputBytes, "NPC_", 0x800);
            bool persistedBytesEqual =
                persistedOutputBytes.AsSpan().SequenceEqual(validOutputBytes);
            ModKey outputPluginKey = ModKey.FromNameAndExtension(
                verifyRequest.Source.Plugin!.Value.Value);
            SkyrimMod typedOutput = SkyrimMod.CreateFromBinary(
                new ModPath(outputPluginKey, new FilePath(outputPluginPath)),
                SkyrimRelease.SkyrimSE);
            Npc[] typedOutputNpcs = typedOutput.Npcs
                .Where(row => row.FormKey.ID == 0x800)
                .ToArray();
            int typedOwnerNpcCount = typedOutputNpcs.Count(row =>
                row.FormKey.ModKey == typedOutput.ModKey);
            string typedNpcOwners = string.Join(",", typedOutputNpcs.Select(row =>
                row.FormKey.ModKey.ToString()));
            Assert(
                generatedOutputNpcCount == 1 &&
                persistedBytesEqual &&
                persistedOutputNpcCount == 1 &&
                typedOwnerNpcCount == 1,
                "Finish Core output boundary diverged: " +
                $"writerNpcCount={generatedOutputNpcCount}; " +
                $"pathNpcCount={persistedOutputNpcCount}; " +
                $"typedOwnerNpcCount={typedOwnerNpcCount}; " +
                $"typedNpcOwners=[{typedNpcOwners}]; " +
                $"bytesEqual={persistedBytesEqual}; " +
                $"writerLength={validOutputBytes.Length}; " +
                $"pathLength={persistedOutputBytes.Length}; " +
                $"writerSha256={Preview251Hash(validOutputBytes).Value}; " +
                $"pathSha256={Preview251Hash(persistedOutputBytes).Value}");
            Sha256Hash verifyPackageTreeSha256 =
                new Sha256Hash(new string('0', 64));
            Sha256Hash verifySourcePackageTreeSha256 =
                verifyRequest.Source.PackageTreeSha256!.Value;
            var verifyManifest = new SkyrimNpcFinishCoreManifest
            {
                Plugin = verifyRequest.Source.Plugin,
                PluginSha256 = Preview251Hash(validOutputBytes),
                BaseNpc = new FormReference(
                    verifyRequest.Source.Plugin!.Value,
                    verifyRequest.Actor.FormId!.Value),
                RequestSha256 = verifyRequestSha,
                ProposalSha256 = verifyProposalSha,
                PlacementIncluded = false,
                RuntimeAuthority = false,
                VisualAuthority = false,
                PackageRoot = new WorkspacePath(outputRootText),
                Archive = new WorkspacePath(archivePathText),
                PackageTreeSha256 = verifyPackageTreeSha256,
                SourcePackageTreeSha256 = verifySourcePackageTreeSha256,
                RuntimeIdentity = new SkyrimNpcFinishCoreRuntimeIdentity
                {
                    BaseNpc = new FormReference(
                        verifyRequest.Source.Plugin!.Value,
                        verifyRequest.Actor.FormId!.Value),
                    PlacedReference = null,
                    PlacementIncluded = false
                },
                Evidence = BuildManifestEvidence(
                    verifyPackageTreeSha256,
                    verifySourcePackageTreeSha256)
            };
            WorkspacePath manifestPath = new(manifestPathText);
            byte[] manifestBytes = SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                verifyManifest, projectRoot);
            await File.WriteAllBytesAsync(manifestPath.Value, manifestBytes);
            Sha256Hash manifestSha = Preview251Hash(manifestBytes);

            SkyrimNpcFinishCoreProposal oldPlanVerifyProposal = verifyProposal with
            {
                MasterOrder = ["Skyrim.esm", "Gap.esm", "AddonB.esm"],
                AppendedMasters = ["AddonB.esm"],
                ProposalSha256 = null
            };
            Sha256Hash oldPlanVerifyProposalSha =
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        oldPlanVerifyProposal, projectRoot));
            oldPlanVerifyProposal = oldPlanVerifyProposal with
            {
                ProposalSha256 = oldPlanVerifyProposalSha
            };
            byte[] oldPlanVerifyProposalBytes =
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    oldPlanVerifyProposal, projectRoot);
            await File.WriteAllBytesAsync(
                proposalEvidencePath, oldPlanVerifyProposalBytes);
            SkyrimNpcFinishCoreManifest oldPlanVerifyManifest = verifyManifest with
            {
                ProposalSha256 = oldPlanVerifyProposalSha,
                Evidence = BuildManifestEvidence(
                    verifyPackageTreeSha256,
                    verifySourcePackageTreeSha256)
            };
            byte[] oldPlanVerifyManifestBytes =
                SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                    oldPlanVerifyManifest, projectRoot);
            await File.WriteAllBytesAsync(
                manifestPath.Value, oldPlanVerifyManifestBytes);
            Sha256Hash oldPlanVerifyManifestSha = Preview251Hash(oldPlanVerifyManifestBytes);
            byte[] outputBeforeOldPlan = await File.ReadAllBytesAsync(outputPluginPath);
            int inspectionsBeforeOldPlan = inspectionCount();
            SkyrimNpcFinishCoreVerificationResult oldPlanVerify =
                await service.VerifyAsync(
                    manifestPath, oldPlanVerifyManifestSha, CancellationToken.None);
            Assert(
                !oldPlanVerify.Verified &&
                oldPlanVerify.Diagnostics.Count(item =>
                    item.Code == "finish-core-verify-master-plan") == 1 &&
                !oldPlanVerify.Diagnostics.Any(item =>
                    item.Code.StartsWith("finish-core-verify-", StringComparison.Ordinal) &&
                    item.Code != "finish-core-verify-master-plan") &&
                inspectionCount() == inspectionsBeforeOldPlan + 1 &&
                (await File.ReadAllBytesAsync(outputPluginPath))
                    .AsSpan().SequenceEqual(outputBeforeOldPlan) &&
                !File.Exists(archivePathText) &&
                !Directory.EnumerateFileSystemEntries(
                    root, ".finish-core-transaction-*", SearchOption.TopDirectoryOnly).Any(),
                "Verify accepted a canonical old direct-owner plan or reached the Bethesda binary seam: " +
                string.Join(" | ", oldPlanVerify.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));

            byte[] cleanVerifyProposalBytes =
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    verifyProposal, projectRoot);
            await File.WriteAllBytesAsync(
                proposalEvidencePath, cleanVerifyProposalBytes);
            SkyrimNpcFinishCoreManifest cleanVerifyManifest = verifyManifest with
            {
                Evidence = BuildManifestEvidence(
                    verifyPackageTreeSha256,
                    verifySourcePackageTreeSha256)
            };
            byte[] cleanManifestBytes =
                SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                    cleanVerifyManifest, projectRoot);
            await File.WriteAllBytesAsync(manifestPath.Value, cleanManifestBytes);
            manifestSha = Preview251Hash(cleanManifestBytes);
            int inspectionsBeforeClean = inspectionCount();
            SkyrimNpcFinishCoreVerificationResult clean = await service.VerifyAsync(
                manifestPath, manifestSha, CancellationToken.None);
            Assert(
                !clean.Verified &&
                !clean.Diagnostics.Any(item =>
                    item.Code == "finish-core-verify-master-plan") &&
                inspectionCount() == inspectionsBeforeClean + 1 &&
                clean.Diagnostics.All(item =>
                    item.Code == "finish-core-verify-package-tree" ||
                    item.Code == "finish-core-verify-archive") &&
                clean.Diagnostics.Any(item =>
                    item.Code == "finish-core-verify-package-tree" ||
                    item.Code == "finish-core-verify-archive"),
                "Verify did not reach the post-plan binary boundary for a clean physical plan: " +
                string.Join(" | ", clean.Diagnostics.Select(item => item.Code + ":" + item.Message)));

            await File.WriteAllBytesAsync(
                additionalMasterPath,
                Preview251Tes4Bytes("AddonA.esm", "DependencyDrift.esm"));
            int inspectionsBeforeAuthorityDrift = inspectionCount();
            SkyrimNpcFinishCoreVerificationResult staleAuthority = await service.VerifyAsync(
                manifestPath, manifestSha, CancellationToken.None);
            Assert(
                !staleAuthority.Verified &&
                staleAuthority.Diagnostics.Count(item =>
                    item.Code == "finish-core-authority-identity-mismatch") == 1 &&
                !staleAuthority.Diagnostics.Any(item =>
                    item.Code.StartsWith("finish-core-verify-", StringComparison.Ordinal)) &&
                inspectionCount() == inspectionsBeforeAuthorityDrift + 1,
                "Verify accepted physical additional-master drift before the Bethesda binary seam: " +
                string.Join(" | ", staleAuthority.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));

            await File.WriteAllBytesAsync(additionalMasterPath, restoredAdditionalMasterBytes);
            byte[] driftedSourceBytes =
                Preview251SourcePluginBytes("Skyrim.esm", "Drift.esm");
            await File.WriteAllBytesAsync(request.Source.PluginPath!.Value.Value, driftedSourceBytes);
            await File.WriteAllBytesAsync(outputPluginPath, driftedSourceBytes);
            Sha256Hash driftedSourceHash = Preview251Hash(driftedSourceBytes);
            SkyrimNpcFinishCoreRequest driftedVerifyRequest = verifyRequest with
            {
                Source = verifyRequest.Source with
                {
                    PluginSha256 = driftedSourceHash,
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader
                        .ComputePackageTreeSha256(verifyRequest.Source.PackageRoot!.Value)
                }
            };
            Sha256Hash driftedVerifyRequestSha =
                SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                    driftedVerifyRequest, projectRoot);
            SkyrimNpcFinishCoreProposal driftedVerifyProposal = verifyProposal with
            {
                Request = driftedVerifyRequest,
                RequestSha256 = driftedVerifyRequestSha,
                ProposalSha256 = null
            };
            Sha256Hash driftedVerifyProposalSha =
                SkyrimNpcFinishCoreDocumentCodec.HashProposalWithoutSelf(
                    SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                        driftedVerifyProposal, projectRoot));
            driftedVerifyProposal = driftedVerifyProposal with
            {
                ProposalSha256 = driftedVerifyProposalSha
            };
            byte[] driftedVerifyRequestBytes =
                SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(
                    driftedVerifyRequest, projectRoot);
            await File.WriteAllBytesAsync(
                requestEvidencePath, driftedVerifyRequestBytes);
            byte[] driftedVerifyProposalBytes =
                SkyrimNpcFinishCoreDocumentCodec.SerializeProposal(
                    driftedVerifyProposal, projectRoot);
            await File.WriteAllBytesAsync(
                proposalEvidencePath, driftedVerifyProposalBytes);
            Sha256Hash driftedSourcePackageTreeSha256 =
                driftedVerifyRequest.Source.PackageTreeSha256!.Value;
            SkyrimNpcFinishCoreManifest driftedVerifyManifest = cleanVerifyManifest with
            {
                PluginSha256 = driftedSourceHash,
                RequestSha256 = driftedVerifyRequestSha,
                ProposalSha256 = driftedVerifyProposalSha,
                SourcePackageTreeSha256 = driftedSourcePackageTreeSha256,
                Evidence = BuildManifestEvidence(
                    verifyPackageTreeSha256,
                    driftedSourcePackageTreeSha256)
            };
            byte[] driftedManifestBytes =
                SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                    driftedVerifyManifest, projectRoot);
            await File.WriteAllBytesAsync(manifestPath.Value, driftedManifestBytes);
            Sha256Hash driftedManifestSha = Preview251Hash(driftedManifestBytes);
            byte[] outputBeforeSourcePlan =
                await File.ReadAllBytesAsync(outputPluginPath);

            SkyrimNpcFinishCoreVerificationResult staleSourcePlan =
                await service.VerifyAsync(
                    manifestPath, driftedManifestSha, CancellationToken.None);
            Assert(
                !staleSourcePlan.Verified &&
                staleSourcePlan.Diagnostics.Count(item =>
                    item.Code == "finish-core-verify-master-plan") == 1 &&
                (await File.ReadAllBytesAsync(outputPluginPath))
                    .AsSpan().SequenceEqual(outputBeforeSourcePlan) &&
                !File.Exists(archivePathText) &&
                !Directory.EnumerateFileSystemEntries(
                    root, ".finish-core-transaction-*", SearchOption.TopDirectoryOnly).Any(),
                "Verify accepted a physical source-master-order drift or wrote before the exact plan refusal: " +
                string.Join(" | ", staleSourcePlan.Diagnostics.Select(item =>
                    item.Code + ":" + item.Message)));
        }
        finally
        {
            await File.WriteAllBytesAsync(additionalMasterPath, restoredAdditionalMasterBytes);
            if (Directory.Exists(outputRootText))
                Directory.Delete(outputRootText, recursive: true);
            if (File.Exists(archivePathText))
                File.Delete(archivePathText);
        }
    }

    private static JsonObject Preview251RequestJson() => new()
    {
        ["schema"] = SkyrimNpcFinishCoreRequest.SchemaIdentifier,
        ["source"] = new JsonObject
        {
            ["packageRoot"] = null,
            ["packageManifest"] = null,
            ["packageManifestSha256"] = null,
            ["packageTreeSha256"] = null,
            ["pluginPath"] = null,
            ["plugin"] = "Source.esp",
            ["pluginSha256"] = null
        },
        ["actor"] = new JsonObject
        {
            ["editorId"] = "Preview251Actor",
            ["formId"] = "0x00000800"
        },
        ["authorities"] = new JsonObject
        {
            ["bodyRoute"] = "Cbbe3Ba",
            ["providers"] = new JsonArray(),
            ["additionalMasters"] = new JsonArray(),
            ["actorAssemblySha256"] = null,
            ["bodyOwnerSha256"] = null,
            ["protectedAppearanceTreeSha256"] = null
        },
        ["followerPolicy"] = new JsonObject
        {
            ["recruitable"] = false,
            ["defensiveOnly"] = true,
            ["potentialFollowerFaction"] = null,
            ["currentFollowerFaction"] = null,
            ["relationshipRank"] = "Ally"
        },
        ["aiPolicy"] = new JsonObject
        {
            ["aggression"] = "Unaggressive",
            ["confidence"] = "Brave",
            ["energy"] = 50,
            ["morality"] = "NoCrime",
            ["assistance"] = "HelpsFriendsAndAllies",
            ["mood"] = "Neutral"
        },
        ["outfitPolicy"] = new JsonObject
        {
            ["policy"] = "ExistingOutfit",
            ["existingOutfit"] = null,
            ["armorItems"] = new JsonArray()
        },
        ["inventoryPolicy"] = new JsonObject
        {
            ["policy"] = "PreserveInventory",
            ["expectedSourceItems"] = new JsonArray(),
            ["desiredItems"] = new JsonArray()
        },
        ["sandboxAuthority"] = new JsonObject
        {
            ["copiedMaster"] = null,
            ["copiedMasterSha256"] = null,
            ["template"] = "Skyrim.esm|0x0001B217",
            ["templateEditorId"] = "DefaultSandboxEditorLocation512",
            ["rawRecordDigest"] = null
        },
        ["output"] = new JsonObject
        {
            ["root"] = null,
            ["archive"] = null,
            ["pluginFileName"] = "Output.esp"
        }
    };

    private static Sha256Hash Preview251Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static byte[] Preview251Tes4Subrecords(
        params (string Signature, byte[] Data)[] subrecords)
    {
        using var content = new MemoryStream();
        Span<byte> length = stackalloc byte[2];
        foreach ((string signature, byte[] data) in subrecords)
        {
            if (signature.Length != 4)
                throw new ArgumentException("TES4 subrecord signatures must be four bytes.");
            content.Write(Encoding.ASCII.GetBytes(signature));
            BinaryPrimitives.WriteUInt16LittleEndian(
                length, checked((ushort)data.Length));
            content.Write(length);
            content.Write(data);
        }

        byte[] payload = content.ToArray();
        byte[] record = new byte[24 + payload.Length];
        "TES4"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt32LittleEndian(
            record.AsSpan(4, 4), checked((uint)payload.Length));
        payload.CopyTo(record.AsSpan(24));
        return record;
    }

    private static byte[] Preview251Tes4Bytes(params string[] masters)
    {
        using var content = new MemoryStream();
        Span<byte> length = stackalloc byte[2];
        foreach (string master in masters)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(master + "\0");
            content.Write("MAST"u8);
            BinaryPrimitives.WriteUInt16LittleEndian(length, checked((ushort)bytes.Length));
            content.Write(length);
            content.Write(bytes);
            content.Write("DATA"u8);
            BinaryPrimitives.WriteUInt16LittleEndian(length, 8);
            content.Write(length);
            content.Write(new byte[8]);
        }

        byte[] payload = content.ToArray();
        byte[] record = new byte[24 + payload.Length];
        "TES4"u8.CopyTo(record);
        BinaryPrimitives.WriteUInt32LittleEndian(record.AsSpan(4, 4),
            checked((uint)payload.Length));
        payload.CopyTo(record.AsSpan(24));
        return record;
    }

    private static byte[] Preview251SourcePluginBytes(params string[] masters)
    {
        string root = Path.Combine(
            ActorwrightWorkspace.ResolveRoot().Value,
            "artifacts", "finish-core-v2-source-fixtures",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string path = Path.Combine(root, "Source.esp");
        try
        {
            ModKey plugin = ModKey.FromNameAndExtension("Source.esp");
            var mod = new SkyrimMod(plugin, SkyrimRelease.SkyrimSE);
            foreach (string master in masters)
            {
                mod.ModHeader.MasterReferences.Add(new MasterReference
                {
                    Master = ModKey.FromNameAndExtension(master)
                });
            }
            mod.ModHeader.Stats.NextFormID = 0x801;
            mod.Npcs.Add(new Npc(
                new FormKey(plugin, 0x800), SkyrimRelease.SkyrimSE)
            {
                EditorID = "Preview251IntegrationActor",
                Name = "Preview.251 integration actor",
                Race = new FormLink<IRaceGetter>(new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x13746))
            });
            mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
            return File.ReadAllBytes(path);
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void AddPreview251OutfitRecords(string path, string plugin)
    {
        var mod = SkyrimMod.CreateFromBinary(new ModPath(ModKey.FromNameAndExtension(plugin), new FilePath(path)), SkyrimRelease.SkyrimSE);
        if (plugin == "Skyrim.esm") mod.Races.Add(new Race(new FormKey(mod.ModKey, 0x13746), SkyrimRelease.SkyrimSE));
        mod.Armors.Add(new Armor(new FormKey(mod.ModKey, 0x800), SkyrimRelease.SkyrimSE));
        WriteCombatFixture(mod, path);
    }

    private static async Task TestPreview251SourcePackageExactOneAsync(
        WorkspacePath projectRoot)
    {
        string root = Path.Combine(
            projectRoot.Value, "artifacts", "finish-core-v2-source-reader-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath packageRoot = new(Path.Combine(root, "package"));
            Directory.CreateDirectory(packageRoot.Value);
            WorkspacePath manifestPath = new(Path.Combine(packageRoot.Value, "manifest.json"));
            WorkspacePath pluginPath = new(Path.Combine(packageRoot.Value, "Test.esp"));
            WorkspacePath providerPath = new(Path.Combine(packageRoot.Value, "Provider.esm"));
            await File.WriteAllBytesAsync(manifestPath.Value, "{}"u8.ToArray());
            await File.WriteAllBytesAsync(pluginPath.Value, Preview251Tes4Bytes());
            await File.WriteAllBytesAsync(providerPath.Value, Preview251Tes4Bytes());
            byte[] pluginBytes = await File.ReadAllBytesAsync(pluginPath.Value);
            byte[] providerBytes = await File.ReadAllBytesAsync(providerPath.Value);
            Sha256Hash pluginSha = Preview251Hash(pluginBytes);
            Sha256Hash providerSha = Preview251Hash(providerBytes);
            Sha256Hash manifestSha = Preview251Hash(await File.ReadAllBytesAsync(manifestPath.Value));
            Sha256Hash packageTree = SkyrimNpcFinishCoreSourcePackageReader
                .ComputePackageTreeSha256(packageRoot);
            WorkspacePath workspace = projectRoot;
            var policy = new KOnlyWorkspacePolicy(
                workspace, new WorkspacePath(@"F:\ExampleGame"));
            var manifestReader = new PackageManifestReader(policy, workspace);
            var packageFiles = ImmutableArray.Create(
                new PackageFileVerification(
                            "provider", new AssetPath("Provider.esm"), providerBytes.LongLength,
                            providerBytes.LongLength, providerSha, providerSha, true));
            var artifact = new PackageVerificationArtifact(
                "1", "npcmanager-package-verification", "skyrimse",
                "finish-core-source", "Test.esp", new FormId(0x800), manifestPath,
                manifestSha, packageFiles, true, true, false);

            SkyrimNpcFinishCoreRequest RequestFor(ImmutableArray<PackageFileVerification> files) =>
                new()
                {
                    Source = new SkyrimNpcFinishCoreSource
                    {
                        PackageRoot = packageRoot,
                        PackageManifest = manifestPath,
                        PackageManifestSha256 = manifestSha,
                        PackageTreeSha256 = packageTree,
                        PluginPath = pluginPath,
                        Plugin = new PluginName("Test.esp"),
                        PluginSha256 = pluginSha
                    },
                    Actor = new SkyrimNpcFinishCoreActor
                    {
                        EditorId = new EditorId("TestActor"),
                        FormId = new FormId(0x800)
                    },
                    Authorities = new SkyrimNpcFinishCoreAuthorities
                    {
                        BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                        Providers = [new SkyrimNpcFinishCoreProviderAuthority
                        {
                            Plugin = new PluginName("Provider.esm"),
                            Path = providerPath,
                            Sha256 = providerSha,
                            ByteLength = providerBytes.LongLength
                        }]
                    },
                    OutfitPolicy = new SkyrimNpcFinishCoreOutfitPolicyDocument
                    {
                        Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit,
                        ExistingOutfit = new FormReference(
                            new PluginName("Skyrim.esm"), new FormId(0x123))
                    },
                    SandboxAuthority = new SkyrimNpcFinishCoreSandboxAuthority
                    {
                        Template = new FormReference(
                            new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                        TemplateEditorId = "DefaultSandboxEditorLocation512"
                    },
                    Output = new SkyrimNpcFinishCoreOutput
                    {
                        PluginFileName = "Test.esp"
                    }
                };

            foreach ((int observed, string expectedObserved) in new[]
                     { (0, "observed=0"), (2, "observed=at least 2") })
            {
                ImmutableArray<PackageFileVerification> files = observed == 0
                    ? packageFiles
                    : packageFiles.Add(
                        new PackageFileVerification(
                            "plugin", new AssetPath("Test.esp"), pluginBytes.LongLength,
                            pluginBytes.LongLength, pluginSha, pluginSha, true))
                        .Add(new PackageFileVerification(
                            "plugin", new AssetPath("Test.esp"), pluginBytes.LongLength,
                            pluginBytes.LongLength, pluginSha, pluginSha, true));
                var verifier = new Preview251PackageVerifier(
                    artifact with { Files = files });
                var reader = new SkyrimNpcFinishCoreSourcePackageReader(
                    workspace, policy, manifestReader, verifier,
                    new BethesdaSkyrimNpcFinishCoreSourceReader());
                SkyrimNpcFinishCoreSourceReadResult result = await reader.InspectAsync(
                    RequestFor(files), CancellationToken.None);
                Diagnostic diagnostic = result.Diagnostics.Single(item =>
                    item.Code == "finish-core-source-plugin-manifest-entry");
                Assert(
                    !result.Admitted &&
                    diagnostic.Message.Contains("identity=Test.esp", StringComparison.Ordinal) &&
                    diagnostic.Message.Contains("expected=1", StringComparison.Ordinal) &&
                    diagnostic.Message.Contains(expectedObserved, StringComparison.Ordinal),
                    $"SourcePackageReader exact-one diagnostic drifted for observed {observed}: {diagnostic.Message}");
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private sealed class Preview251PackageVerifier(
        PackageVerificationArtifact artifact) : IPackageVerifyService
    {
        public ValueTask<PackageVerifyResult> VerifyAsync(
            PackageVerifyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new PackageVerifyResult(
                true, artifact, ImmutableArray<Diagnostic>.Empty));
        }
    }
}
