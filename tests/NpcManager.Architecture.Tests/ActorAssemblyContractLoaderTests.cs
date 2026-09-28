using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestActorAssemblyContractLoader()
    {
        var root = Path.Combine(Path.GetTempPath(), "npcmanager-actor-assembly-contract");
        Directory.CreateDirectory(root);
        var contractPath = Path.Combine(root, "contract.json");
        var manifestPath = Path.Combine(root, "manifest.json");
        var payload = $$"""
        {
          "schemaVersion": 1,
          "operation": "npc-assembly-preflight",
          "edition": "skyrimse",
          "packageManifest": { "path": "{{manifestPath.Replace("\\", "\\\\")}}", "sha256": "{{new string('0', 64)}}" },
          "baseNpc": { "plugin": "AnnaFieldMedicFollower.esp", "formId": "0x00000800" },
          "placement": { "mode": "persistentReference", "placedReferenceFormId": "0x00000807" },
          "bodyMorph": { "owner": "obody", "evidence": { "status": "available", "path": "{{manifestPath.Replace("\\", "\\\\")}}", "sha256": "{{new string('0', 64)}}" } },
          "outfitScope": { "status": "unavailable", "reason": "not yet packaged" }
        }
        """;
        await File.WriteAllTextAsync(contractPath, payload, new UTF8Encoding(false));
        var hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(contractPath))));
        var loader = new ActorAssemblyPreflightDocumentLoader(new WorkspacePath(root));
        var loaded = await loader.LoadAsync(
            new ActorAssemblyPreflightRequest(new WorkspacePath(contractPath), hash), CancellationToken.None);
        Assert(loaded.Document is { SchemaVersion: 1,
            Operation: "npc-assembly-preflight",
            Edition: GameEdition.SkyrimSpecialEdition },
            $"The valid contract was not admitted: {loaded.Disposition}; {string.Join(" | ", loaded.Diagnostics.Select(item => item.Code + ":" + item.Message))}");
        var document = loaded.Document ?? throw new InvalidOperationException("The valid contract was not admitted.");
        Assert(document.BaseNpc.FormId.Value == 0x800 &&
               document.Placement.PlacedReferenceFormId?.Value == 0x807,
            "Base and placed identities were collapsed.");
        Assert(loaded.ActualSha256?.Value == hash.Value,
            "The contract hash was not retained.");
    }
}
