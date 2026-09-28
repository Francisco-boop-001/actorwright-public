using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestActorAssemblyEvidenceLoader()
    {
        var root = Path.Combine(Path.GetTempPath(), "npcmanager-actor-assembly-evidence");
        Directory.CreateDirectory(root);
        var preset = Path.Combine(root, "Anna.xml");
        var assignment = Path.Combine(root, "assignment.json");
        var evidence = Path.Combine(root, "obody.json");
        var hash = new string('A', 64);
        var json = $$"""
        {
          "schemaVersion": 1,
          "artifactKind": "actor-assembly-obody-evidence",
          "edition": "skyrimse",
          "baseNpc": { "plugin": "AnnaFieldMedicFollower.esp", "formId": "0x00000800" },
          "preset": { "path": "{{preset.Replace("\\", "\\\\")}}", "sha256": "{{hash}}" },
          "assignments": [ { "path": "{{assignment.Replace("\\", "\\\\")}}", "sha256": "{{hash}}" } ],
          "appliedOutputs": [ { "outputId": "anna-main-outfit", "appliedChannels": [ "Breasts" ] } ]
        }
        """;
        await File.WriteAllTextAsync(evidence, json, new UTF8Encoding(false));
        var boundHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(evidence))));
        var loader = new ActorAssemblyPreflightDocumentLoader(new WorkspacePath(root));
        var result = await loader.LoadOwnerEvidenceAsync(new ActorAssemblyBoundFile(new WorkspacePath(evidence), boundHash), CancellationToken.None);
        Assert(result.Document is ActorAssemblyOBodyEvidence obody && obody.AppliedOutputs.Length == 1,
            "The closed OBody owner document was not admitted.");
    }
}
