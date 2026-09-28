using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private static readonly JsonSerializerOptions PrettyDocumentOptions = new() { WriteIndented = true };
    internal static async Task RunCreateToFinishAsync(bool appendMaster = false, bool reformatDocuments = false, bool outfitRace = false, bool verifyPackage = false, bool siblingRecovery = false, bool inheritedEvidence = false, ICompatibilityJourneySink? compatibilitySink = null)
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        string repositoryRoot = Path.GetFullPath(Environment.CurrentDirectory);
        string catalog = Path.Combine(repositoryRoot, "tools", "release", "protocol-v2-workflow-probes", "catalog.json");
        string ownedRoot = CompatibilityJourneySupport.CreateCreateToFinishRoot(
            repositoryRoot);
        Directory.CreateDirectory(ownedRoot);
        string operationId = Path.GetFileName(ownedRoot).Split('-')[^1];
        compatibilitySink ??= CompatibilityJourneyFileSink.FromEnvironment(
            ownedRoot, Path.Combine(ownedRoot, "finished"), operationId);
        var compatibilitySteps = ImmutableArray.CreateBuilder<CompatibilityStepObservation>();
        void Observe(string command, ProtocolInvocation invocation, ImmutableArray<string> schemaIds = default) =>
            compatibilitySteps.Add(new CompatibilityStepObservation(
                command,
                invocation.ExitCode,
                CompatibilityJourneySupport.ExitMeaning(invocation.ExitCode),
                schemaIds.IsDefault ? [] : schemaIds));
        try
        {
            var root = new WorkspacePath(ownedRoot);
            MaterializeProbeFixtures(repositoryRoot, catalog, "npc create-from-jslot", root);
            var preset = Child(root, "npc-preflight", "fixture.jslot");
            PrepareGoldenPresetFixture(preset);
            var createRequest = Child(root, "npc-preflight", "request.json");
            CorrectNpcRequestFixture(
                repositoryRoot, root, createRequest, HashFile(preset),
                workspaceProvider: verifyPackage);
            foreach (string command in new[] { "version", "capabilities" })
            {
                ProtocolInvocation discovery = await RunCliAsync(root, command, "--protocol", "2", "--json");
                Require(discovery.ExitCode == 0, "Exact binary discovery failed.");
                Observe(command, discovery);
            }
            foreach (string command in new[] { "npc create-from-jslot", "npc finish analyze", "npc finish apply", "npc finish verify" })
            {
                ProtocolInvocation schema = await RunCliAsync(root, "schema", "export", "--protocol", "2", "--json", "--command", command);
                Require(schema.ExitCode == 0, "Exact binary schema discovery failed for " + command);
                Observe("schema export:" + command, schema, CompatibilityJourneySupport.SchemaIds(schema.Root));
            }
            await using CompatibilityTransientSourceWatcher? transientCapture =
                compatibilitySink is null ? null :
                new CompatibilityTransientSourceWatcher(ownedRoot);
            ProtocolInvocation created = await RunCliAsync(root,
                "npc", "create-from-jslot", "--json",
                "--request", createRequest.Value, "--request-sha256", HashFile(createRequest),
                "--preset", preset.Value, "--preset-sha256", HashFile(preset),
                "--data-root", Child(root, "Data").Value,
                "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
                "--companion-root", Child(root, "companion").Value);
            if (transientCapture is not null) await transientCapture.StopAsync();
            Require(created.ExitCode == 0, "Actual V1 golden create failed: " + created.Root + created.StdErr);
            Observe("npc create-from-jslot", created);
            var packageRoot = Child(root, "planned-npc-output");
            var manifestPath = Child(packageRoot, "npcmanager-package.json");
            var pluginPath = Child(packageRoot, "Data", "PackagedNpc.esp");
            string sourceBeforeSha256 = HashFile(pluginPath);
            ImmutableArray<(string Relative, string Sha256, long Length)> seededEvidence = inheritedEvidence
                ? SeedInheritedHostEvidence(packageRoot, manifestPath) : [];
            var key = ModKey.FromNameAndExtension("PackagedNpc.esp");
            SkyrimNpcFinishCoreAdditionalMasterBinding? appended = appendMaster
                ? PrepareFinishMasterAppendFixture(root, pluginPath, manifestPath)
                : null;
            using var plugin = SkyrimMod.CreateFromBinaryOverlay(new ModPath(key, new FilePath(pluginPath.Value)), SkyrimRelease.SkyrimSE);
            INpcGetter npc = plugin.Npcs.Single();
            Require(plugin.CombatStyles.Count == 0 && npc.CombatStyle.FormKey.ModKey == ModKey.FromNameAndExtension("Skyrim.esm"),
                "Actual create output should retain a master CSTY without a local seed record.");

            var copiedMaster = Child(root, "authority", "Skyrim.esm");
            SkyrimFinishMasterFixture.AppendCanonicalPackGroupToMaster(
                Child(root, "Data", "Skyrim.esm").Value,
                copiedMaster.Value,
                root.Value);
            if (outfitRace) PrepareFinishOutfitRaceFixture(copiedMaster, npc.Race.FormKey);
            var request = new SkyrimNpcFinishCoreRequest
            {
                Schema = SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier,
                Source = new()
                {
                    PackageRoot = packageRoot, PackageManifest = manifestPath,
                    PackageManifestSha256 = new Sha256Hash(HashFile(manifestPath)),
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(packageRoot),
                    PluginPath = pluginPath, Plugin = new PluginName(key.ToString()), PluginSha256 = new Sha256Hash(HashFile(pluginPath))
                },
                Actor = new() { EditorId = new EditorId(npc.EditorID!), FormId = new FormId(npc.FormKey.ID) },
                Authorities = new()
                {
                    BodyRoute = SkyrimNpcFinishCoreBodyRoute.Cbbe3Ba,
                    Providers = [new() { Plugin = new PluginName(key.ToString()), Path = pluginPath,
                        Sha256 = new Sha256Hash(HashFile(pluginPath)), ByteLength = new FileInfo(pluginPath.Value).Length }]
                },
                AiPolicy = new() { Aggression = SkyrimNpcFinishCoreAggression.Unaggressive,
                    Confidence = SkyrimNpcFinishCoreConfidence.Brave, Energy = 50,
                    Morality = SkyrimNpcFinishCoreMorality.NoCrime,
                    Assistance = SkyrimNpcFinishCoreAssistance.HelpsFriendsAndAllies, Mood = SkyrimNpcFinishCoreMood.Neutral },
                CombatPolicy = new() { SeedLocalStyle = true, Profile = SkyrimNpcFinishCoreCombatProfile.RangedFirst },
                PerkPolicy = [new(new FormReference(new PluginName("Skyrim.esm"), new FormId(0x58F6A)), 2)],
                OutfitPolicy = new() { Policy = SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit,
                    ArmorItems = [new FormReference(new PluginName("Skyrim.esm"), new FormId(0x900))] },
                SandboxAuthority = new() { CopiedMaster = copiedMaster,
                    CopiedMasterSha256 = new Sha256Hash(HashFile(copiedMaster)),
                    Template = new FormReference(new PluginName("Skyrim.esm"), new FormId(0x1B217)),
                    TemplateEditorId = "DefaultSandboxEditorLocation512" },
                Output = new() { Root = Child(root, "finished"), Archive = Child(root, "finished.zip"), PluginFileName = key.ToString() }
            };
            if (appended is not null)
                request = request with
                {
                    Authorities = request.Authorities with { AdditionalMasters = [appended] },
                    OutfitPolicy = new() { Policy = SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit,
                        ExistingOutfit = new FormReference(appended.Plugin, new FormId(0x800)) }
                };
            if (outfitRace) request = await CheckFinishOutfitRaceRefusals(root, copiedMaster, request);
            if (siblingRecovery) await AssertCstyRecoveryAsync(root, request);
            var requestPath = Child(root, "finish-request.json");
            byte[] canonicalRequest = SkyrimNpcFinishCoreDocumentCodec.SerializeRequest(request, root);
            File.WriteAllBytes(requestPath.Value, reformatDocuments ? ReformatFinishDocument(canonicalRequest) : canonicalRequest);
            var proposalPath = Child(root, "finish-proposal.json");
            ProtocolInvocation analyzed = await RunCliAsync(root, "npc", "finish", "analyze", "--json",
                "--request", requestPath.Value, "--request-sha256", HashFile(requestPath), "--proposal", proposalPath.Value);
            Require(analyzed.ExitCode == 0 &&
                    analyzed.Root.GetProperty("schema").GetString() == SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier &&
                    analyzed.Root.GetProperty("status").GetString() == "ReadyForReviewedWrite",
                "Actual create output did not reach Finish ReadyForReviewedWrite: " + analyzed.Root + analyzed.StdErr);
            Observe("npc finish analyze", analyzed);
            if (reformatDocuments)
            {
                byte[] validProposal = File.ReadAllBytes(proposalPath.Value);
                JsonNode badProposal = JsonNode.Parse(validProposal)!;
                badProposal["proposalSha256"] = new string('9', 64);
                File.WriteAllBytes(proposalPath.Value, JsonSerializer.SerializeToUtf8Bytes(badProposal));
                ProtocolInvocation invalidProposal = await RunCliAsync(root, "npc", "finish", "apply", "--json",
                    "--request", requestPath.Value, "--request-sha256", HashFile(requestPath),
                    "--proposal", proposalPath.Value, "--proposal-sha256", HashFile(proposalPath));
                Require(invalidProposal.ExitCode != 0 &&
                        invalidProposal.Root.GetProperty("code").GetString() == "finish-core-proposal-binding" &&
                        !Directory.Exists(Child(root, "finished").Value),
                    "A raw transport hash must not override a false embedded proposal identity.");
                File.WriteAllBytes(proposalPath.Value, validProposal);
                File.WriteAllBytes(proposalPath.Value, ReformatFinishDocument(File.ReadAllBytes(proposalPath.Value)));
            }
            ProtocolInvocation applied = await RunCliAsync(root, "npc", "finish", "apply", "--json",
                "--request", requestPath.Value, "--request-sha256", reformatDocuments
                    ? SkyrimNpcFinishCoreDocumentCodec.HashRequest(request, root).Value : HashFile(requestPath),
                "--proposal", proposalPath.Value, "--proposal-sha256", reformatDocuments
                    ? HashFile(proposalPath) : analyzed.Root.GetProperty("proposalSha256").GetString()!);
            Require(applied.ExitCode == 0 && applied.Root.GetProperty("applied").GetBoolean(),
                "Actual create-to-finish apply failed: " + applied.Root + applied.StdErr);
            Observe("npc finish apply", applied);
            var finishedManifest = Child(root, "finished", "NPCManager", "Evidence", "finish-core-manifest.json");
            ProtocolInvocation verified = await RunCliAsync(root, "npc", "finish", "verify", "--json",
                "--manifest", finishedManifest.Value, "--manifest-sha256", HashFile(finishedManifest));
            Require(verified.ExitCode == 0 && verified.Root.GetProperty("verified").GetBoolean(),
                "Independent CLI verification failed for actual create-to-finish: " + verified.Root + verified.StdErr);
            Observe("npc finish verify", verified);
            if (inheritedEvidence) await AssertInheritedEvidenceRelocatedAsync(root, request, seededEvidence, finishedManifest);
            if (verifyPackage)
            {
                await AssertFinishPackagePublicationAsync(root, request);
                compatibilitySteps.Add(new CompatibilityStepObservation(
                    "package verify", 4,
                    CompatibilityJourneySupport.ExitMeaning(4), []));
            }
            if (reformatDocuments)
            {
                var prettyManifest = Child(root, "pretty-manifest.json");
                File.WriteAllBytes(prettyManifest.Value, ReformatFinishDocument(File.ReadAllBytes(finishedManifest.Value)));
                var prettyVerified = await RunCliAsync(root, "npc", "finish", "verify", "--json",
                    "--manifest", prettyManifest.Value, "--manifest-sha256", HashFile(finishedManifest));
                Require(prettyVerified.ExitCode == 0 && prettyVerified.Root.GetProperty("verified").GetBoolean(),
                    "Canonical hash did not admit a pretty manifest copy: " + prettyVerified.Root + prettyVerified.StdErr);
            }
            if (appendMaster)
                AssertFinishMasterAppendOutput(pluginPath, Child(root, "finished", "Data", "PackagedNpc.esp"));
            if (outfitRace) AssertFinishOutfitRaceOutput(Child(root, "finished", "Data", "PackagedNpc.esp"), npc.Race.FormKey);
            Require(new Sha256Hash(HashFile(pluginPath)) == request.Source.PluginSha256 &&
                SkyrimNpcFinishCoreSourcePackageReader.ComputePackageTreeSha256(packageRoot) == request.Source.PackageTreeSha256,
                "Finish changed the actual create source plugin or package tree.");
            if (compatibilitySink is not null)
            {
                string sourceAfterSha256 = HashFile(pluginPath);
                var finishedPluginPath = Child(root, "finished", "Data", "PackagedNpc.esp");
                using var finishedPlugin = SkyrimMod.CreateFromBinaryOverlay(
                    finishedPluginPath.Value, SkyrimRelease.SkyrimSE);
                INpcGetter finishedNpc = finishedPlugin.Npcs.Single();
                string[] sourceMasters = plugin.ModHeader.MasterReferences
                    .Select(masterReference => masterReference.Master.ToString())
                    .ToArray();
                string[] finalMasters = finishedPlugin.ModHeader.MasterReferences
                    .Select(masterReference => masterReference.Master.ToString())
                    .ToArray();
                var semanticInventory = Directory.EnumerateFiles(
                        ownedRoot, "*", SearchOption.AllDirectories)
                    .Where(path => !path.Contains(
                        Path.DirectorySeparatorChar + ".actorwright" +
                        Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    .Select(path => Path.GetRelativePath(ownedRoot, path).Replace('\\', '/'))
                    .Where(path => path == "finished.zip" ||
                        path.StartsWith("planned-npc-output/", StringComparison.Ordinal) ||
                        path.StartsWith("finished/", StringComparison.Ordinal))
                    .Order(StringComparer.Ordinal)
                    .Select(path => CompatibilityJourneySupport.Artifact(
                        ownedRoot, path, "inventory-member"))
                    .ToArray();
                compatibilitySink.Capture(new CompatibilityJourneyObservation(
                    "journey.create-to-package",
                    compatibilitySteps.ToImmutable(),
                    [
                        CompatibilityJourneySupport.Artifact(ownedRoot, "planned-npc-output/npcmanager-package.json", "source-package-manifest"),
                        CompatibilityJourneySupport.Artifact(ownedRoot, "planned-npc-output/Data/PackagedNpc.esp", "source-plugin"),
                        CompatibilityJourneySupport.Artifact(ownedRoot, "finished/Data/PackagedNpc.esp", "finished-plugin"),
                        CompatibilityJourneySupport.Artifact(ownedRoot, "finished/NPCManager/Evidence/finish-core-manifest.json", "finish-manifest"),
                        CompatibilityJourneySupport.Artifact(ownedRoot, "finished.zip", "finished-archive")
                    ],
                    new JsonObject
                    {
                        ["readyForReviewedWrite"] = true,
                        ["applied"] = true,
                        ["verified"] = true,
                        ["source"] = new JsonObject
                        {
                            ["plugin"] = key.ToString(),
                            ["masters"] = new JsonArray(sourceMasters.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                            ["actorFormId"] = $"0x{npc.FormKey.ID:X8}",
                            ["beforeSha256"] = sourceBeforeSha256,
                            ["afterSha256"] = sourceAfterSha256,
                            ["unchanged"] = sourceBeforeSha256 == sourceAfterSha256
                        },
                        ["genericPackageVerification"] = new JsonObject
                        {
                            ["exitCode"] = 4,
                            ["code"] = "workflow-human-review-required",
                            ["verified"] = false
                        },
                        ["final"] = new JsonObject
                        {
                            ["plugin"] = finishedPlugin.ModKey.ToString(),
                            ["masters"] = new JsonArray(finalMasters.Select(value => (JsonNode)JsonValue.Create(value)!).ToArray()),
                            ["actorFormId"] = $"0x{finishedNpc.FormKey.ID:X8}",
                            ["sha256"] = HashFile(finishedPluginPath)
                        },
                        ["packageInventoryExact"] = true,
                        ["artifactInventory"] = new JsonArray(semanticInventory.Select(item =>
                            (JsonNode)CompatibilityJourneySupport.ArtifactJson(item)).ToArray())
                    },
                    transientCapture?.Seal(
                        Child(root, "planned-npc-output", "Data", "NPCManager", "Evidence", "racemenu-bundle.json").Value,
                        Child(root, "planned-npc-output", "Data", "NPCManager", "Evidence").Value)
                        ?? []));
            }
        }
        finally { CompatibilityJourneySupport.CleanupCreateToFinishWorkspace(ownedRoot, compatibilitySink); }
    }

    /// <summary>
    /// Turns the golden create output into a create-from-jslot style host that
    /// carries its own package-root <c>NPCManager/Evidence</c> namespace: the
    /// product's own build evidence copied beside a nested synthetic member, all
    /// declared in the source package inventory.
    /// </summary>
    private static ImmutableArray<(string Relative, string Sha256, long Length)> SeedInheritedHostEvidence(
        WorkspacePath packageRoot, WorkspacePath manifestPath)
    {
        string buildEvidence = Child(packageRoot, "Data", "NPCManager", "Evidence").Value;
        string hostEvidence = Child(packageRoot, "NPCManager", "Evidence").Value;
        Require(Directory.Exists(buildEvidence) && !Directory.Exists(hostEvidence),
            "The golden create output no longer writes Data/NPCManager/Evidence or already owns a package-root evidence namespace.");
        var seeded = ImmutableArray.CreateBuilder<(string, string, long)>();
        foreach (string source in Directory.EnumerateFiles(buildEvidence, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(buildEvidence, source);
            string destination = Path.Combine(hostEvidence, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination);
        }
        string nested = Path.Combine(hostEvidence, "FaceGeom", "face-bake-authority.json");
        Directory.CreateDirectory(Path.GetDirectoryName(nested)!);
        File.WriteAllBytes(nested, JsonSerializer.SerializeToUtf8Bytes(new JsonObject
        {
            ["schema"] = "actorwright.test.inherited-host-evidence.v1",
            ["note"] = "Synthetic product-owned nested host evidence member."
        }));
        JsonNode manifest = JsonNode.Parse(File.ReadAllBytes(manifestPath.Value))!;
        JsonArray artifacts = manifest["artifacts"]!.AsArray();
        foreach (string file in Directory.EnumerateFiles(hostEvidence, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string relative = Path.GetRelativePath(packageRoot.Value, file).Replace('\\', '/');
            string sha256 = HashFile(new WorkspacePath(file));
            long length = new FileInfo(file).Length;
            seeded.Add((relative, sha256, length));
            artifacts.Add(new JsonObject
            {
                ["kind"] = "host-evidence", ["relativePath"] = relative,
                ["byteLength"] = length, ["sha256"] = sha256
            });
        }
        File.WriteAllBytes(manifestPath.Value, JsonSerializer.SerializeToUtf8Bytes(manifest, PrettyDocumentOptions));
        Require(seeded.Count >= 3 && seeded.Any(item => item.Item1 == "NPCManager/Evidence/racemenu-bundle.json") &&
                seeded.Any(item => item.Item1 == "NPCManager/Evidence/FaceGeom/face-bake-authority.json"),
            "The seeded host evidence namespace lacks the create-from-jslot members this case exists for.");
        return seeded.ToImmutable();
    }

    private static async Task AssertInheritedEvidenceRelocatedAsync(
        WorkspacePath root, SkyrimNpcFinishCoreRequest request,
        ImmutableArray<(string Relative, string Sha256, long Length)> seeded, WorkspacePath finishedManifest)
    {
        WorkspacePath finished = request.Output.Root!.Value;
        string inheritedRoot = "NPCManager/Evidence/Inherited/" + request.Source.PackageTreeSha256!.Value.Value[..8];
        foreach ((string relative, string sha256, long length) in seeded)
        {
            Require(!File.Exists(Path.Combine(finished.Value, relative.Replace('/', Path.DirectorySeparatorChar))),
                "Finish apply re-published inherited host evidence in place: " + relative);
            string relocated = inheritedRoot + "/" + relative;
            var relocatedPath = new WorkspacePath(Path.Combine(finished.Value, relocated.Replace('/', Path.DirectorySeparatorChar)));
            Require(File.Exists(relocatedPath.Value) && HashFile(relocatedPath) == sha256 &&
                    new FileInfo(relocatedPath.Value).Length == length,
                "Finish apply did not relocate inherited host evidence byte-for-byte: " + relocated);
        }
        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(finishedManifest.Value));
        JsonElement inherited = manifest.RootElement.GetProperty("evidence").GetProperty("inherited");
        var listed = inherited.EnumerateArray().Select(row => (
            Path: row.GetProperty("path").GetString()!, Source: row.GetProperty("sourcePath").GetString()!,
            Sha256: row.GetProperty("sha256").GetString()!, Length: row.GetProperty("byteLength").GetInt64())).ToArray();
        Require(listed.Length == seeded.Length && seeded.All(item => listed.Any(row =>
                    row.Source == item.Relative && row.Path == inheritedRoot + "/" + item.Relative &&
                    string.Equals(row.Sha256, item.Sha256, StringComparison.OrdinalIgnoreCase) && row.Length == item.Length)),
            "The Finish manifest does not list the relocated inherited evidence with source paths and hashes: " + inherited);
        Require(manifest.RootElement.GetProperty("evidence").GetProperty("files").GetArrayLength() == 4,
            "Inherited evidence leaked into the canonical evidence file set.");

        async Task<ProtocolInvocation> VerifyAsync() => await RunCliAsync(root, "npc", "finish", "verify", "--json",
            "--manifest", finishedManifest.Value, "--manifest-sha256", HashFile(finishedManifest));
        static string Codes(ProtocolInvocation invocation) => invocation.Root.ToString() + invocation.StdErr;
        string mutated = Path.Combine(finished.Value, (inheritedRoot + "/" + seeded[0].Relative).Replace('/', Path.DirectorySeparatorChar));
        byte[] original = File.ReadAllBytes(mutated);
        File.WriteAllBytes(mutated, original.Concat("\n"u8.ToArray()).ToArray());
        ProtocolInvocation drifted = await VerifyAsync();
        Require(drifted.ExitCode != 0 && Codes(drifted).Contains("finish-core-verify-inherited-evidence-drift", StringComparison.Ordinal),
            "Verify accepted a mutated inherited evidence member: " + Codes(drifted));
        File.WriteAllBytes(mutated, original);
        using (var sparse = new FileStream(mutated, FileMode.Open,
                   FileAccess.Write, FileShare.None))
            sparse.SetLength(64L * 1024 * 1024 + 1);
        ProtocolInvocation oversized = await VerifyAsync();
        Require(oversized.ExitCode != 0 &&
                Codes(oversized).Contains("finish-core-verify-inherited-evidence-drift",
                    StringComparison.Ordinal),
            "Verify accepted a physically oversized inherited evidence member: " +
            Codes(oversized));
        File.WriteAllBytes(mutated, original);
        string extra = Path.Combine(finished.Value, inheritedRoot.Replace('/', Path.DirectorySeparatorChar), "unlisted-inherited.txt");
        File.WriteAllText(extra, "unlisted");
        ProtocolInvocation unlisted = await VerifyAsync();
        Require(unlisted.ExitCode != 0 && Codes(unlisted).Contains("finish-core-verify-inherited-evidence-set", StringComparison.Ordinal),
            "Verify accepted an unlisted file inside the inherited evidence namespace: " + Codes(unlisted));
        File.Delete(extra);
        string canonicalExtra = Path.Combine(finished.Value, "NPCManager", "Evidence", "unexpected-extra.txt");
        File.WriteAllText(canonicalExtra, "extra");
        ProtocolInvocation canonicalUnlisted = await VerifyAsync();
        Require(canonicalUnlisted.ExitCode != 0 && Codes(canonicalUnlisted).Contains("finish-core-verify-evidence-set", StringComparison.Ordinal),
            "Verify accepted an unlisted member of the canonical evidence root: " + Codes(canonicalUnlisted));
        File.Delete(canonicalExtra);
        ProtocolInvocation restored = await VerifyAsync();
        Require(restored.ExitCode == 0 && restored.Root.GetProperty("verified").GetBoolean(),
            "Verify did not pass again on the restored inherited-evidence package: " + Codes(restored));
    }

    private static byte[] ReformatFinishDocument(byte[] bytes)
    {
        JsonNode root = JsonNode.Parse(bytes)!;
        void UpperHashes(JsonNode? node)
        {
            if (node is JsonObject obj)
                foreach (string key in obj.Select(pair => pair.Key).ToArray())
                {
                    if (obj[key] is JsonValue value && value.TryGetValue<string>(out string? text) &&
                        text.Length == 64 && text.All(Uri.IsHexDigit)) obj[key] = text.ToUpperInvariant();
                    else UpperHashes(obj[key]);
                }
            else if (node is JsonArray array) foreach (JsonNode? child in array) UpperHashes(child);
        }
        UpperHashes(root);
        return JsonSerializer.SerializeToUtf8Bytes(root, PrettyDocumentOptions);
    }
}
