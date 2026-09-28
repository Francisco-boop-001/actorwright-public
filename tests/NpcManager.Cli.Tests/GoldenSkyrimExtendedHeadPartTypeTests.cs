using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Domain;

namespace NpcManager.Cli.Tests;

internal static partial class GoldenSkyrimWorkflowResumptionTests
{
    private const uint ExtendedHeadPartFormId = 0x830;
    private const uint ExtendedHeadPartPnam = 71; // UBE eye wetness (Rachel NPCM-20260906-6742 R2)
    private const string ExtendedHeadPartModel = "Actors/Character/Actorwright/Wetness.nif";

    /// <summary>
    /// WI-NPC-3: a schema record-authority binding whose headPartType is the raw
    /// PNAM value of a mod-defined HDPT type (71) must be admitted, reported with
    /// an info diagnostic, and preflight must walk the part's model like any
    /// other mapped record instead of refusing the whole request.
    /// </summary>
    internal static async Task RunExtendedHeadPartTypePreflightAsync()
    {
        string repositoryRoot = Path.GetFullPath(Directory.GetCurrentDirectory());
        string ownedRoot = Path.Combine(repositoryRoot, "artifacts", "test-work",
            $"extended-hdpt-{Guid.NewGuid():N}");
        Directory.CreateDirectory(ownedRoot);
        try
        {
            var root = new WorkspacePath(ownedRoot);
            MaterializeProbeFixtures(repositoryRoot, Path.Combine(repositoryRoot, "tools", "release",
                "protocol-v2-workflow-probes", "catalog.json"), "npc create-from-jslot", root);
            var preset = Child(root, "npc-preflight", "fixture.jslot");
            PrepareGoldenPresetFixture(preset);
            var request = Child(root, "npc-preflight", "request.json");
            CorrectNpcRequestFixture(repositoryRoot, root, request, HashFile(preset));

            ProtocolInvocation baseline = await RunPreflight(root, request, preset, "baseline.json");
            Require(baseline.Root.GetProperty("readyForBuild").GetBoolean(),
                "The golden fixture must be ready before the extended head part is added: " +
                baseline.Root.GetProperty("diagnostics").GetRawText());

            AddExtendedHeadPart(root, preset);
            ProtocolInvocation extended = await RunPreflight(root, request, preset, "extended.json");
            string diagnostics = extended.Root.GetProperty("diagnostics").GetRawText();
            Require(!extended.Root.GetProperty("diagnostics").EnumerateArray().Any(row =>
                    row.GetProperty("code").GetString() == "racemenu-plan-record-authority-invalid"),
                "A raw PNAM 71 headPartType binding was refused as an invalid record authority: " + diagnostics);
            Require(extended.Root.GetProperty("diagnostics").EnumerateArray().Any(row =>
                    row.GetProperty("code").GetString() == "racemenu-plan-record-authority-extended-head-part-type" &&
                    row.GetProperty("severity").GetString() == "info" &&
                    row.GetProperty("message").GetString()!.Contains("71", StringComparison.Ordinal) &&
                    row.GetProperty("message").GetString()!.Contains(
                        $"0x{ExtendedHeadPartFormId:X8}", StringComparison.OrdinalIgnoreCase)),
                "The extended HDPT type was not reported with its raw PNAM value and record: " + diagnostics);
            Require(extended.Root.GetProperty("readyForBuild").GetBoolean(),
                "Preflight is not ready with an admitted extended HDPT type: " + diagnostics);
            using JsonDocument preflight = JsonDocument.Parse(await File.ReadAllBytesAsync(
                extended.Root.GetProperty("path").GetString()!));
            JsonElement headParts = preflight.RootElement.GetProperty("headParts");
            Require(headParts.EnumerateArray().Any(row =>
                    row.GetProperty("sourceType").GetString() == ExtendedHeadPartPnam.ToString() &&
                    row.GetProperty("effectiveType").GetString() == ExtendedHeadPartPnam.ToString() &&
                    row.GetProperty("disposition").GetString() == "MappedRecord" &&
                    row.GetProperty("provider").GetString() == "ActorwrightBlankNpcProvider.esp"),
                "The extended part is not a record-mapped preflight head-part row: " +
                headParts.GetRawText());
            Require(extended.Root.GetProperty("dependencyClosure").EnumerateArray().Any(row =>
                    row.GetProperty("kind").GetString() == "mesh" &&
                    string.Equals(row.GetProperty("path").GetString(), "meshes/" + ExtendedHeadPartModel,
                        StringComparison.OrdinalIgnoreCase) &&
                    row.GetProperty("status").GetString() == "present"),
                "Preflight did not walk the extended part's model into the dependency closure: " +
                extended.Root.GetProperty("dependencyClosure").GetRawText());

            SetExtendedHeadPartDeclaredType(root, "0");
            ProtocolInvocation numericMismatch = await RunPreflight(root, request, preset, "numeric-mismatch.json");
            Require(!numericMismatch.Root.GetProperty("readyForBuild").GetBoolean() &&
                    numericMismatch.Root.GetProperty("diagnostics").EnumerateArray().Any(row =>
                        row.GetProperty("code").GetString() == "racemenu-plan-headpart-provider-type-mismatch" &&
                        row.GetProperty("message").GetString()!.Contains("PNAM 71", StringComparison.Ordinal) &&
                        row.GetProperty("message").GetString()!.Contains("PNAM 0", StringComparison.Ordinal)),
                "A declared numeric PNAM 0 did not refuse the actual PNAM 71 provider exactly: " +
                numericMismatch.Root.GetProperty("diagnostics").GetRawText());

            SetExtendedHeadPartDeclaredType(root, "misc");
            ProtocolInvocation namedCompatibility = await RunPreflight(root, request, preset, "named-compatibility.json");
            Require(namedCompatibility.Root.GetProperty("readyForBuild").GetBoolean() &&
                    !namedCompatibility.Root.GetProperty("diagnostics").EnumerateArray().Any(row =>
                        row.GetProperty("code").GetString() == "racemenu-plan-headpart-provider-type-mismatch"),
                "The existing wire-name Misc projection no longer accepts a mod-defined provider type: " +
                namedCompatibility.Root.GetProperty("diagnostics").GetRawText());
        }
        finally
        {
            Directory.Delete(ownedRoot, recursive: true);
        }
    }

    private static async Task<ProtocolInvocation> RunPreflight(
        WorkspacePath root, WorkspacePath request, WorkspacePath preset, string outputName) =>
        await RunCliAsync(root,
            "npc", "create-from-jslot", "--json",
            "--request", request.Value, "--request-sha256", HashFile(request),
            "--preset", preset.Value, "--preset-sha256", HashFile(preset),
            "--data-root", Child(root, "Data").Value,
            "--plugins", "Skyrim.esm,ActorwrightBlankNpcProvider.esp",
            "--companion-root", Child(root, "companion").Value,
            "--preflight-output", Child(root, "npc-preflight", outputName).Value);

    private static void AddExtendedHeadPart(WorkspacePath root, WorkspacePath preset)
    {
        var providerPath = Child(root, "Data", "ActorwrightBlankNpcProvider.esp");
        var provider = SkyrimMod.CreateFromBinary(providerPath.Value, SkyrimRelease.SkyrimSE);
        var part = provider.HeadParts.AddNew(new FormKey(provider.ModKey, ExtendedHeadPartFormId));
        part.EditorID = "ActorwrightExtendedEyeWetness";
        part.Type = (HeadPart.TypeEnum)ExtendedHeadPartPnam;
        part.Flags = HeadPart.Flag.Playable | HeadPart.Flag.Female;
        part.Model = new Model { File = ExtendedHeadPartModel };
        provider.Npcs.Single().HeadParts.Add(part.FormKey);
        provider.WriteToBinary(providerPath.Value);
        var model = Child(root, "Data", "meshes", ExtendedHeadPartModel);
        Directory.CreateDirectory(Path.GetDirectoryName(model.Value)!);
        File.WriteAllBytes(model.Value, WriteHdptTopologyModel(3, "textures/Actors/Character/Actorwright/Head.dds",
            positions: [new Vector3(-0.5F, 0F, 0F), new Vector3(0.5F, 0F, 0F), new Vector3(0F, 0F, 0.5F)],
            shapeName: "Wetness"));

        string form = $"ActorwrightBlankNpcProvider.esp|0x{ExtendedHeadPartFormId:X8}";
        JsonNode presetDocument = JsonNode.Parse(File.ReadAllBytes(preset.Value))!;
        presetDocument["headParts"]!.AsArray().Add(new JsonObject
        {
            ["formIdentifier"] = form, ["type"] = (int)ExtendedHeadPartPnam
        });
        File.WriteAllBytes(preset.Value, JsonSerializer.SerializeToUtf8Bytes(presetDocument));

        var authorityPath = Child(root, "npc-preflight", "record-authority.json");
        JsonObject authority = JsonNode.Parse(File.ReadAllBytes(authorityPath.Value))!.AsObject();
        string providerHash = HashFile(providerPath);
        authority["formBindings"]!.AsArray().Add(new JsonObject
        {
            ["signature"] = "HDPT", ["sourceFormKey"] = form, ["providerFormKey"] = form,
            ["providerPluginName"] = "ActorwrightBlankNpcProvider.esp",
            ["providerPluginPath"] = "Data/ActorwrightBlankNpcProvider.esp",
            ["providerPluginSha256"] = providerHash,
            ["headPartType"] = ExtendedHeadPartPnam.ToString()
        });
        foreach (JsonNode? binding in authority["formBindings"]!.AsArray())
        {
            if (binding!["providerPluginName"]!.GetValue<string>() == "ActorwrightBlankNpcProvider.esp")
                binding["providerPluginSha256"] = providerHash;
        }
        authority["headPartDispositions"]!.AsArray().Add(new JsonObject
        {
            ["sourceFormKey"] = form, ["disposition"] = "mapped-record"
        });
        File.WriteAllBytes(authorityPath.Value, JsonSerializer.SerializeToUtf8Bytes(authority));
        RebindNpcPreflightFixtureHashes(root);
    }

    private static void SetExtendedHeadPartDeclaredType(WorkspacePath root, string value)
    {
        string form = $"ActorwrightBlankNpcProvider.esp|0x{ExtendedHeadPartFormId:X8}";
        var authorityPath = Child(root, "npc-preflight", "record-authority.json");
        JsonObject authority = JsonNode.Parse(File.ReadAllBytes(authorityPath.Value))!.AsObject();
        JsonNode binding = authority["formBindings"]!.AsArray().Single(item =>
            item!["sourceFormKey"]!.GetValue<string>() == form)!;
        binding["headPartType"] = value;
        File.WriteAllBytes(authorityPath.Value, JsonSerializer.SerializeToUtf8Bytes(authority));
        RebindNpcPreflightFixtureHashes(root);
    }
}
