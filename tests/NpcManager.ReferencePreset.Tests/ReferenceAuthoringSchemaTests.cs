using System.Collections.Immutable;
using System.Numerics;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.ReferencePreset.Tests;

internal static class ReferenceAuthoringSchemaTests
{
    private static readonly string[] Commands = ["preset design-propose", "preset create-from-reference", "npc create-from-reference"];
    private static readonly string[] Names = ["intake", "inference-proposal", "reviewed-design", "resource-snapshot", "authoring-proposal", "verified-preset", "verified-npc-handoff"];

    public static async Task RunAsync()
    {
        var definitions = Commands.SelectMany(command => CommandDocumentSchemaCatalog.For(command)).ToArray();
        string directory = Path.Combine(Environment.CurrentDirectory, "artifacts", "task11", "schemas");
        Directory.CreateDirectory(directory);
        for (int kind = 0; kind < Names.Length; kind++)
        {
            string id = "reference-authoring." + Names[kind] + ".v1";
            var matches = definitions.Where(definition => definition.SchemaIdentifier == id).ToArray();
            Require(matches.Length > 0, "Published reference session schema missing: " + id);
            JsonElement schema = matches[0].JsonSchema;
            Require(schema.GetProperty("$id").GetString() == id && schema.GetProperty("type").GetString() == "object" &&
                    !schema.GetProperty("additionalProperties").GetBoolean(), "Reference envelope must be closed: " + id);
            Require(!schema.GetProperty("properties").TryGetProperty("schema", out _) &&
                    schema.GetProperty("properties").GetProperty("kind").ValueKind == JsonValueKind.Object,
                "Published schema invented a schema field or erased numeric kind: " + id);
            File.WriteAllText(Path.Combine(directory, Names[kind] + ".json"), schema.GetRawText());
            CheckReferences(schema, schema);
        }
        var design = AgentCommandRegistry.All.Single(row => row.Name == "preset design-propose");
        Require(design.InputArtifacts.Single(row => row.Kind == "reference-preset-intake").SchemaIds
            .SequenceEqual(["reference-authoring.intake.v1"]), "Reference input artifact schemaIds remain unbound.");
        Require(definitions.Any(row => row.SchemaIdentifier == "reference-authoring.intake.v1" && row.Direction == "input"),
            "Intake schema has no actual input direction.");
        var root = new WorkspacePath(Path.Combine(directory, "run-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root.Value);
        var service = new ReferencePresetSessionService(new KOnlyWorkspacePolicy(root, new WorkspacePath(@"F:\ExampleGame")), root);
        ReferencePresetIntake intake = ReferencePresetRulesTests.ValidIntake();
        (_, ReferencePresetResourceSnapshot snapshot, ReferencePresetAuthoringProposal authoring, _) = ReferencePresetWriterTests.Fixture();
        Sha256Hash hash = new(new string('a', 64));
        snapshot = snapshot with
        {
            ProtectedNeckRingVertexIndices = [0, 1],
            RenderShapes = [new("synthetic.nif", "head", hash, hash, hash, [])
            {
                RestPositions = [Vector3.Zero, Vector3.UnitX, Vector3.UnitY],
                TriangleIndices = [0, 1, 2],
                TextureCoordinates = [Vector2.Zero, Vector2.UnitX, Vector2.UnitY],
                Normals = [Vector3.UnitZ, Vector3.UnitZ, Vector3.UnitZ],
                TriangleMaterialOrdinals = [0]
            }],
            MorphChannels = snapshot.MorphChannels.Select(channel => channel with
                { Deltas = [new SseTriHeadVertexDelta(0, new Vector3(0.25f, 0.5f, 0.75f))] }).ToImmutableArray()
        };
        var preset = new VerifiedReferencePreset(1, ReferencePresetAuthorityKind.VerifiedPreset, intake.ProjectId,
            intake.Race, intake.Sex, intake.Weight, hash, new WorkspacePath(Path.Combine(root.Value, "result.jslot")), hash, hash, []);
        ReferencePresetSessionDocument[] documents =
        [
            new(ReferencePresetSessionDocumentKind.Intake, Intake: intake),
            new(ReferencePresetSessionDocumentKind.InferenceProposal, InferenceProposal: ReferencePresetRulesTests.ValidInferenceProposal()),
            new(ReferencePresetSessionDocumentKind.ReviewedDesign, ReviewedDesign: ReferencePresetRulesTests.ValidReviewedDesign()),
            new(ReferencePresetSessionDocumentKind.ResourceSnapshot, ResourceSnapshot: snapshot),
            new(ReferencePresetSessionDocumentKind.AuthoringProposal, AuthoringProposal: authoring),
            new(ReferencePresetSessionDocumentKind.VerifiedPreset, VerifiedPreset: preset),
            new(ReferencePresetSessionDocumentKind.VerifiedNpcHandoff, VerifiedNpcHandoff: new(1,
                ReferencePresetAuthorityKind.VerifiedNpcHandoff, intake.ProjectId, hash, preset.PresetPath, hash,
                intake.Race, intake.Sex, intake.Weight, []))
        ];
        for (int kind = 0; kind < documents.Length; kind++)
        {
            var path = new WorkspacePath(Path.Combine(root.Value, Names[kind] + ".json"));
            var written = await service.WriteAsync(new(path, documents[kind]), CancellationToken.None);
            Require(written.Written && written.ContentSha256 is not null, "Actual typed schema fixture failed persistence: " + Names[kind]);
            var reopened = await service.ReadAsync(new(path, written.ContentSha256!.Value, documents[kind].Kind), CancellationToken.None);
            Require(reopened.Document?.Kind == documents[kind].Kind, "Actual session failed round-trip: " + Names[kind]);
        }
        File.WriteAllText(Path.Combine(directory, "fixture-root.txt"), root.Value);
    }

    private static void CheckReferences(JsonElement root, JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        {
            if (node.TryGetProperty("$ref", out JsonElement reference))
            {
                string pointer = reference.GetString()!;
                Require(pointer.StartsWith("#/", StringComparison.Ordinal), "Unexpected non-local schema reference.");
                JsonElement target = root;
                foreach (string segment in pointer[2..].Split('/'))
                {
                    string key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    target = target.ValueKind == JsonValueKind.Array ? target[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)] : target.GetProperty(key);
                }
            }
            foreach (JsonProperty property in node.EnumerateObject()) CheckReferences(root, property.Value);
        }
        else if (node.ValueKind == JsonValueKind.Array)
            foreach (JsonElement item in node.EnumerateArray()) CheckReferences(root, item);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
