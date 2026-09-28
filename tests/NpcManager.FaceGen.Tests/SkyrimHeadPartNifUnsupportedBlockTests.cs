using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.FaceGen.Tests;

internal static partial class Program
{
    /// <summary>
    /// WI-NPC-6 (Noctivixen r7): a selected head-part NIF whose only problem is a
    /// block outside the admitted parser must be refused as an unsupported block
    /// naming the block type and index, not reported as a malformed file.
    /// </summary>
    private static void TestHeadPartNifUnsupportedBlock()
    {
        var reader = new SseSelectedHeadpartNifGeometryReader();
        byte[] plain = WriteTopologyModel(141);
        var control = reader.Read(new SseSelectedHeadpartNifGeometryReadRequest(
            new AssetPath("meshes/fixture/plain.nif"), TopologyHash(plain), [.. plain]));
        Assert(control.Accepted && control.Document?.Shapes.Length == 1,
            "The unsupported-block control model must still be admitted: " + Format(control.Diagnostics));

        foreach ((string type, byte[] payload) in new[]
                 {
                     // NiStringExtraData with a payload the admitted two-index layout does not cover.
                     ("NiStringExtraData", new byte[12]),
                     // A type the complete-carrier parser never admits.
                     ("BSXFlags", new byte[8])
                 })
        {
            byte[] bytes = WriteTopologyModel(141, extraBlock: (type, payload));
            const int extraIndex = 9;
            var result = reader.Read(new SseSelectedHeadpartNifGeometryReadRequest(
                new AssetPath($"meshes/fixture/extra-{type}.nif"), TopologyHash(bytes), [.. bytes]));
            Assert(!result.Accepted && result.Document is null,
                $"A NIF carrying an unsupported {type} block must remain refused: " + Format(result.Diagnostics));
            Assert(result.Diagnostics.Any(item =>
                    item.Code == "sse-headpart-nif-unsupported-block" &&
                    item.Severity == DiagnosticSeverity.Error &&
                    item.Message.Contains(type, StringComparison.Ordinal) &&
                    item.Message.Contains($"block {extraIndex}", StringComparison.OrdinalIgnoreCase)),
                $"The unsupported {type} block was not classified with its type and index: " + Format(result.Diagnostics));
            Assert(!result.Diagnostics.Any(item => item.Code == "sse-headpart-nif-malformed"),
                $"An unsupported {type} block must not be reported as a malformed NIF: " + Format(result.Diagnostics));
        }

        // Refusal semantics for genuinely malformed files are unchanged.
        byte[] truncated = plain[..^1];
        var malformed = reader.Read(new SseSelectedHeadpartNifGeometryReadRequest(
            new AssetPath("meshes/fixture/truncated.nif"), TopologyHash(truncated), [.. truncated]));
        Assert(!malformed.Accepted && malformed.Diagnostics.Any(item => item.Code == "sse-headpart-nif-malformed") &&
               !malformed.Diagnostics.Any(item => item.Code == "sse-headpart-nif-unsupported-block"),
            "A truncated NIF must keep its malformed classification: " + Format(malformed.Diagnostics));
    }
}
