using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Presets;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestActorAssemblyTriInspection()
    {
        var root = Path.Combine(Path.GetTempPath(), "npcmanager-actor-assembly-tri");
        Directory.CreateDirectory(root);
        var triPath = Path.Combine(root, "bad.tri");
        await File.WriteAllBytesAsync(triPath, new byte[] { 0x42, 0x41, 0x44 });
        var service = new BodySlideTriInspectionService(new KOnlyWorkspacePolicy(
            new WorkspacePath(root), new WorkspacePath(root + "-protected")), new WorkspacePath(root));
        var result = await service.InspectAsync(
            new BodySlideTriInspectionRequest(GameEdition.SkyrimSpecialEdition, new WorkspacePath(triPath)),
            CancellationToken.None);
        Assert(!result.IsValid && result.Diagnostics.Any(item => item.Code == "body-tri-invalid"),
            "Non-PIRT TRI input was not rejected by the focused inspector.");
    }
}
