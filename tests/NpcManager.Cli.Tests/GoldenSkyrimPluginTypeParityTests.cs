using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Cli.Tests;

/// <summary>
/// WI-NPC-1 (NPCM-20260907-2671): the blank create-from-jslot route must refuse
/// a non-.esp output name at preflight, before any native work, and admit a
/// typed light-from-creation option (`output.pluginType: "espfe"`).
/// </summary>
internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private const string PluginTypeDiagnostic = "npc-build-preflight-plugin-type";
    private const uint Tes4LightFlag = 0x0000_0200;
    private const uint Tes4MasterFlag = 0x0000_0001;
    private const string PackagedOutput =
        "\"output\":{\"root\":\"planned-npc-output\",\"plugin\":\"PackagedNpc.esp\"}";

    /// <summary>Focused selector: preflight refusal, loader refusal, and the light build together.</summary>
    internal static async Task RunPluginTypeParityAsync()
    {
        AssertPluginTypeSchema();
        await RunCreationProposalPluginTypeBindingAsync();
        await RunPreflightPluginTypeAsync();
        await RunPluginTypeBuildAsync();
    }

    private static Task RunCreationProposalPluginTypeBindingAsync() =>
        WithPluginTypeFixtureAsync("creation-proposal-plugin-type", async (root, _, _) =>
        {
            var template = Child(root, "Data", "ActorwrightBlankNpcProvider.esp");
            var proposalPath = Child(root, "npc-preflight", "creation-proposal.json");
            var outputPath = Child(root, "ProposalBound.esp");
            var skyrim = new PluginName("Skyrim.esm");
            var provider = new PluginName("ActorwrightBlankNpcProvider.esp");
            var templateHash = new Sha256Hash(HashFile(template));
            var face = new FormReference(provider, new FormId(0x802));
            var appearance = new FullyAuthoredSkyrimNpcAppearanceSource(
                [new OutputOwnedSkyrimNpcFaceHeadPart(new FormId(0x803), face)],
                new OutputOwnedSkyrimNpcHairColor(
                    new FormId(0x801), new SkyrimPackedRgb(0x11_22_33)),
                new OutputOwnedSkyrimNpcFaceTextureSet(
                    new FormId(0x802),
                    new SkyrimPrivateHeadTexturePaths(
                        new AssetPath("actors/character/Actorwright/Head.dds"),
                        new AssetPath("actors/character/Actorwright/Head_msn.dds"),
                        new AssetPath("actors/character/Actorwright/Head_sk.dds"),
                        new AssetPath("actors/character/Male/BlankDetailmap.dds"),
                        new AssetPath("actors/character/Actorwright/Head_s.dds"))),
                42f,
                new SkyrimFaceMorphPatch(
                    Enumerable.Repeat(0f, 18).ToImmutableArray(),
                    0f,
                    Enumerable.Repeat(uint.MaxValue, 4).ToImmutableArray()),
                new SkyrimFaceTintPatch(
                    [new SkyrimFaceTintLayer(0, 255, 255, 255, 255, 0, -1)]),
                new SkyrimQnamRgb(1f, 1f, 1f));
            var request = new NpcCreationRequest(
                GameEdition.SkyrimSpecialEdition,
                template,
                templateHash,
                new FormId(0x800),
                proposalPath,
                outputPath,
                new NpcCreationIdentity(
                    new EditorId("NPCM_ProposalBound"),
                    new NpcName("Proposal bound NPC")),
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
                    null),
                appearance,
                new SkyrimNpcCreationStats(
                    new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                    0, 0, 0, 1, 1, 100, 35, 0, 50, 50, 50, 1f, 42f, 255))
            {
                PluginAuthorities =
                [new NpcCreationPluginAuthority(provider, template, templateHash)]
            };
            INpcCreationService service = NpcCreationComposition.Create(
                new KOnlyWorkspacePolicy(root, new WorkspacePath(@"F:\ExampleGame")),
                root);

            NpcCreationProposal ordinary = await service.AnalyzeAsync(
                request, CancellationToken.None);
            Require(ordinary.IsApplicable && ordinary.ProposalHash is not null,
                "The ordinary request did not produce an applicable creation proposal: " +
                string.Join(" | ", ordinary.Diagnostics.Select(item => item.Code + ": " + item.Message)));
            using (JsonDocument document = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(proposalPath.Value)))
            {
                Require(!document.RootElement.TryGetProperty("pluginType", out _),
                    "The default ordinary plugin type changed backward-compatible proposal bytes.");
            }

            NpcCreationRequest changedToLight = request with
            {
                PluginType = BlankNpcPluginType.Espfe
            };
            NpcCreationResult mismatch = await service.ApplyAsync(
                changedToLight, ordinary, CancellationToken.None);
            Require(!mismatch.Applied && mismatch.Diagnostics.Any(item =>
                        item.Code == "npc-create-proposal-request-mismatch") &&
                    !File.Exists(outputPath.Value),
                "An ordinary analyzed proposal was reused to write a light plugin: " +
                string.Join(" | ", mismatch.Diagnostics.Select(item => item.Code + ": " + item.Message)));

            Sha256Hash ordinaryHash = ordinary.ProposalHash ??
                throw new InvalidOperationException("The applicable ordinary proposal had no hash.");
            File.Delete(proposalPath.Value);
            NpcCreationProposal light = await service.AnalyzeAsync(
                changedToLight, CancellationToken.None);
            Require(light.IsApplicable && light.ProposalHash is not null &&
                    light.ProposalHash != ordinaryHash,
                "The light request did not produce a distinct applicable proposal: " +
                string.Join(" | ", light.Diagnostics.Select(item => item.Code + ": " + item.Message)));
            using (JsonDocument document = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(proposalPath.Value)))
            {
                Require(document.RootElement.GetProperty("pluginType").GetString() == "espfe",
                    "The light proposal did not persist its byte-affecting plugin type.");
            }

            NpcCreationResult applied = await service.ApplyAsync(
                changedToLight, light, CancellationToken.None);
            Require(applied.Applied && applied.Verification is { IsValid: true },
                "A matched light request/proposal pair was refused: " +
                string.Join(" | ", applied.Diagnostics.Select(item => item.Code + ": " + item.Message)));
            var outputKey = ModKey.FromNameAndExtension(Path.GetFileName(outputPath.Value));
            using var output = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(outputPath.Value)),
                SkyrimRelease.SkyrimSE);
            Require(output.IsSmallMaster && !output.IsMaster,
                "The matched light request/proposal pair did not write a light .esp.");
        });

    private static void AssertPluginTypeSchema()
    {
        JsonElement export = ProtocolV2SchemaService.RenderInline("npc create-from-jslot");
        JsonElement request = export.GetProperty("documentSchemas").EnumerateArray()
            .Single(item => item.GetProperty("name").GetString() == "request")
            .GetProperty("jsonSchema");
        JsonElement outputReference = request.GetProperty("properties").GetProperty("output");
        string reference = outputReference.GetProperty("$ref").GetString()!;
        JsonElement output = request.GetProperty("$defs")
            .GetProperty(reference["#/$defs/".Length..]);
        JsonElement pluginType = output.GetProperty("properties").GetProperty("pluginType");
        bool hasDefault = pluginType.TryGetProperty("default", out JsonElement defaultValue);
        Require(!output.GetProperty("required").EnumerateArray()
                    .Any(item => item.GetString() == "pluginType") &&
                pluginType.GetProperty("type").GetString() == "string" &&
                hasDefault && defaultValue.GetString() == "esp" &&
                pluginType.GetProperty("enum").EnumerateArray()
                    .Select(item => item.GetString())
                    .SequenceEqual(["esp", "espfe"], StringComparer.Ordinal),
            "The create request schema did not publish optional output.pluginType with its exact default and closed values.");
    }

    internal static Task RunPreflightPluginTypeAsync() =>
        WithPluginTypeFixtureAsync("preflight-plugin-type", async (root, preset, request) =>
        {
            string presetSha256 = HashFile(preset);
            WorkspacePath eslRequest = WritePluginTypeRequest(root, request, "request-esl.json",
                "\"output\":{\"root\":\"planned-npc-output\",\"plugin\":\"Noctivixen.esl\"}");
            var eslPreflight = Child(root, "npc-preflight", "esl-preflight.json");
            ProtocolInvocation esl = await RunPreflightAsync(root, eslRequest, preset, presetSha256, eslPreflight);
            Require(esl.ExitCode != 0 && esl.Root.GetProperty("created").GetBoolean() &&
                    !esl.Root.GetProperty("readyForBuild").GetBoolean() && File.Exists(eslPreflight.Value),
                "Preflight admitted a '.esl' output name on the blank route: " + esl.Root + esl.StdErr);
            JsonElement[] gates = esl.Root.GetProperty("requiredGates").EnumerateArray().ToArray();
            Require(gates.Any(gate => gate.GetProperty("id").GetString() == "output-policy" &&
                                      !gate.GetProperty("passed").GetBoolean()),
                "The output-policy gate passed a '.esl' output name: " + esl.Root);
            Require(gates.Where(gate => gate.GetProperty("id").GetString() != "output-policy")
                    .All(gate => gate.GetProperty("passed").GetBoolean()),
                "The '.esl' fixture failed a gate other than output-policy, so the refusal is not isolated: " + esl.Root);
            JsonElement typed = esl.Root.GetProperty("diagnostics").EnumerateArray().FirstOrDefault(row =>
                row.GetProperty("code").GetString() == PluginTypeDiagnostic);
            Require(typed.ValueKind == JsonValueKind.Object &&
                    string.Equals(typed.GetProperty("severity").GetString(), "error", StringComparison.OrdinalIgnoreCase),
                $"Preflight did not emit the typed '{PluginTypeDiagnostic}' refusal: " + esl.Root);
            string message = typed.GetProperty("message").GetString() ?? "";
            Require(message.Contains("Noctivixen.esl", StringComparison.Ordinal) &&
                    message.Contains(".esp", StringComparison.Ordinal) &&
                    message.Contains("pluginType", StringComparison.Ordinal) &&
                    message.Contains("espfe", StringComparison.Ordinal),
                "The plugin-type refusal did not name the offending file and the supported route: " + message);
            using JsonDocument persisted = JsonDocument.Parse(File.ReadAllBytes(eslPreflight.Value));
            Require(!persisted.RootElement.GetProperty("readyForBuild").GetBoolean() &&
                    persisted.RootElement.GetProperty("requiredGates").EnumerateArray().Any(gate =>
                        gate.GetProperty("id").GetString() == "output-policy" &&
                        !gate.GetProperty("passed").GetBoolean()),
                "The persisted preflight artifact disagreed with the response about the output-policy gate.");
            RequireNoBuildSideEffects(root);

            WorkspacePath badType = WritePluginTypeRequest(root, request, "request-bad-type.json",
                "\"output\":{\"root\":\"planned-npc-output\",\"plugin\":\"PackagedNpc.esp\",\"pluginType\":\"esl\"}");
            ProtocolInvocation refusedType = await RunPreflightAsync(root, badType, preset, presetSha256,
                Child(root, "npc-preflight", "bad-type-preflight.json"));
            Require(refusedType.ExitCode != 0 &&
                    refusedType.Root.TryGetProperty("code", out JsonElement code) &&
                    code.GetString() == "jslot-npc-request-invalid" &&
                    refusedType.Root.GetProperty("message").GetString()!.Contains("output.pluginType", StringComparison.Ordinal),
                "The loader admitted an unsupported output.pluginType value: " + refusedType.Root + refusedType.StdErr);

            WorkspacePath espfeRequest = WritePluginTypeRequest(root, request, "request-espfe.json",
                "\"output\":{\"root\":\"planned-npc-output\",\"plugin\":\"PackagedNpc.esp\",\"pluginType\":\"espfe\"}");
            var espfePreflight = Child(root, "npc-preflight", "espfe-preflight.json");
            ProtocolInvocation espfe = await RunPreflightAsync(root, espfeRequest, preset, presetSha256, espfePreflight);
            Require(espfe.ExitCode == 0 && espfe.Root.GetProperty("readyForBuild").GetBoolean() &&
                    espfe.Root.GetProperty("requiredGates").EnumerateArray().All(gate => gate.GetProperty("passed").GetBoolean()),
                "Preflight refused the supported light route (.esp name with pluginType espfe): " + espfe.Root + espfe.StdErr);
            using JsonDocument espfeDocument = JsonDocument.Parse(File.ReadAllBytes(espfePreflight.Value));
            Require(espfeDocument.RootElement.GetProperty("plannedOutputs").EnumerateArray().Any(row =>
                    row.GetProperty("role").GetString() == "plugin" &&
                    row.GetProperty("path").GetString()!.EndsWith(Path.Combine("Data", "PackagedNpc.esp"), StringComparison.Ordinal)),
                "The espfe preflight did not keep the planned '.esp' plugin path.");
            RequireNoBuildSideEffects(root);
        });

    internal static Task RunPluginTypeBuildAsync() =>
        WithPluginTypeFixtureAsync("build-plugin-type", async (root, preset, request) =>
        {
            string presetSha256 = HashFile(preset);
            WorkspacePath eslRequest = WritePluginTypeRequest(root, request, "request-esl.json",
                "\"output\":{\"root\":\"planned-npc-output\",\"plugin\":\"Noctivixen.esl\"}");
            ProtocolInvocation esl = await RunBuildAsync(root, eslRequest, preset, presetSha256);
            string[] eslCodes = esl.Root.GetProperty("diagnostics").EnumerateArray()
                .Select(row => row.GetProperty("code").GetString() ?? "").ToArray();
            Require(esl.ExitCode != 0 && !esl.Root.GetProperty("completed").GetBoolean() &&
                    eslCodes.Contains(PluginTypeDiagnostic) &&
                    eslCodes.Contains("npc-build-preflight-refused") &&
                    !eslCodes.Contains("blank-npc-plugin-type") &&
                    !eslCodes.Any(code => code.StartsWith("jslot-companion", StringComparison.Ordinal)) &&
                    !esl.Root.GetProperty("temporaryNpcVerified").GetBoolean() &&
                    esl.Root.GetProperty("companionPreset").ValueKind == JsonValueKind.Null,
                "The '.esl' build was not refused at preflight before companion/native work: " + esl.Root + esl.StdErr);
            RequireNoBuildSideEffects(root);

            WorkspacePath espfeRequest = WritePluginTypeRequest(root, request, "request-espfe.json",
                "\"output\":{\"root\":\"planned-npc-output\",\"plugin\":\"PackagedNpc.esp\",\"pluginType\":\"espfe\"}");
            ProtocolInvocation espfe = await RunBuildAsync(root, espfeRequest, preset, presetSha256);
            Require(espfe.ExitCode == 0 && espfe.Root.GetProperty("completed").GetBoolean() &&
                    espfe.Root.GetProperty("npcFormId").GetString() == "0x00000800" &&
                    !espfe.Root.GetProperty("runtimeAuthority").GetBoolean(),
                "The espfe build did not complete as a static build: " + espfe.Root + espfe.StdErr);
            var packageRoot = Child(root, "planned-npc-output");
            var pluginPath = Child(packageRoot, "Data", "PackagedNpc.esp");
            Require(string.Equals(espfe.Root.GetProperty("plugin").GetString(), pluginPath.Value, StringComparison.OrdinalIgnoreCase) &&
                    File.Exists(pluginPath.Value) &&
                    File.Exists(Child(packageRoot, "Data", "meshes", "actors", "character", "FaceGenData", "FaceGeom", "PackagedNpc.esp", "00000800.nif").Value) &&
                    File.Exists(Child(packageRoot, "Data", "textures", "actors", "character", "FaceGenData", "FaceTint", "PackagedNpc.esp", "00000800.dds").Value) &&
                    File.Exists(Child(packageRoot, "npcmanager-package.json").Value),
                "The espfe build did not key its plugin and FaceGen files under the '.esp' name.");
            string packagedHash = HashFile(pluginPath);
            string? responseHash = espfe.Root.GetProperty("pluginSha256").GetString();
            Require(string.Equals(packagedHash, responseHash, StringComparison.OrdinalIgnoreCase),
                $"The espfe build response did not bind the written plugin hash: response {responseHash}, packaged {packagedHash}.");

            byte[] bytes = File.ReadAllBytes(pluginPath.Value);
            Require(bytes.AsSpan(0, 4).SequenceEqual("TES4"u8) && bytes.AsSpan(24, 4).SequenceEqual("HEDR"u8),
                "The espfe plugin does not start with a TES4/HEDR header.");
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
            uint nextFormId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(38, 4));
            Require((flags & Tes4LightFlag) != 0 && (flags & Tes4MasterFlag) == 0,
                $"The espfe plugin TES4 flags 0x{flags:X8} do not carry the light flag 0x200 alone.");
            Require(nextFormId is > 0x800 and <= 0x1000,
                $"The espfe plugin HEDR NextFormID 0x{nextFormId:X8} was not left in the uncompacted 0x800..0xFFF budget.");

            var key = ModKey.FromNameAndExtension("PackagedNpc.esp");
            using var readback = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(key, new FilePath(pluginPath.Value)), SkyrimRelease.SkyrimSE);
            Require(readback.IsSmallMaster && !readback.IsMaster,
                "Mutagen readback did not observe IsSmallMaster on the espfe plugin.");
            ImmutableArray<FormKey> owned = readback.EnumerateMajorRecords()
                .Select(record => record.FormKey).Where(formKey => formKey.ModKey == key).ToImmutableArray();
            Require(owned.Length > 0 && owned.All(formKey => formKey.ID is >= 0x800 and <= 0xFFF) &&
                    readback.Npcs.Single().FormKey == new FormKey(key, 0x800),
                "The espfe plugin changed its owned local IDs instead of keeping them in 0x800..0xFFF: " +
                string.Join(",", owned.Select(formKey => $"0x{formKey.ID:X}")));
        });

    private static async Task WithPluginTypeFixtureAsync(
        string prefix,
        Func<WorkspacePath, WorkspacePath, WorkspacePath, Task> body)
    {
        string repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        string catalog = Path.Combine(repositoryRoot, "tools", "release",
            "protocol-v2-workflow-probes", "catalog.json");
        string ownedRoot = Path.Combine(repositoryRoot, "artifacts", "test-work",
            $"{prefix}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var root = new WorkspacePath(ownedRoot);
            MaterializeProbeFixtures(repositoryRoot, catalog, "npc create-from-jslot", root);
            var preset = Child(root, "npc-preflight", "fixture.jslot");
            PrepareGoldenPresetFixture(preset);
            var request = Child(root, "npc-preflight", "request.json");
            CorrectNpcRequestFixture(repositoryRoot, root, request, HashFile(preset));
            await body(root, preset, request);
        }
        finally
        {
            string canonical = Path.GetFullPath(ownedRoot);
            Require(canonical.StartsWith(Path.Combine(repositoryRoot, "artifacts", "test-work") +
                        Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
                    Path.GetFileName(canonical).StartsWith(prefix + "-", StringComparison.Ordinal),
                "Plugin-type fixture cleanup refused a non-owned root.");
            RequireOrdinaryTree(canonical);
            Directory.Delete(canonical, recursive: true);
        }
    }

    private static WorkspacePath WritePluginTypeRequest(
        WorkspacePath root,
        WorkspacePath request,
        string fileName,
        string output)
    {
        string source = ReplaceExactly(File.ReadAllText(request.Value), PackagedOutput, output);
        var path = Child(root, "npc-preflight", fileName);
        File.WriteAllText(path.Value, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    private static Task<ProtocolInvocation> RunPreflightAsync(
        WorkspacePath root,
        WorkspacePath request,
        WorkspacePath preset,
        string presetSha256,
        WorkspacePath output) => RunCliAsync(root,
        "npc", "create-from-jslot", "--json",
        "--request", request.Value, "--request-sha256", HashFile(request),
        "--preset", preset.Value, "--preset-sha256", presetSha256,
        "--data-root", Child(root, "Data").Value,
        "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
        "--companion-root", Child(root, "companion").Value,
        "--preflight-output", output.Value);

    private static Task<ProtocolInvocation> RunBuildAsync(
        WorkspacePath root,
        WorkspacePath request,
        WorkspacePath preset,
        string presetSha256) => RunCliAsync(root,
        "npc", "create-from-jslot", "--json",
        "--request", request.Value, "--request-sha256", HashFile(request),
        "--preset", preset.Value, "--preset-sha256", presetSha256,
        "--data-root", Child(root, "Data").Value,
        "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
        "--companion-root", Child(root, "companion").Value);

    private static void RequireNoBuildSideEffects(WorkspacePath root) =>
        Require(!Directory.Exists(Child(root, "companion").Value) &&
                !Directory.Exists(Child(root, "planned-npc-output").Value) &&
                !Directory.Exists(Child(root, "planned-npc-output-preview").Value),
            "A refused plugin-type request crossed the build write boundary.");
}
