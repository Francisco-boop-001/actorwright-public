using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Desktop;

internal sealed record ExistingNpcEditDesktopContext(
    IExistingNpcEditService Service,
    IFormChoiceService FormChoices,
    ExistingNpcEditRequest InitialRequest);

/// <summary>Small desktop composition root for the shared existing-NPC edit transaction.</summary>
internal static class ExistingNpcEditDesktopComposition
{
    public static ExistingNpcEditDesktopContext Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var packageVerifier = new PackageVerifyService(
            new PackageManifestReader(policy, labRoot));
        var service = new ExistingNpcEditService(
            new NpcOverrideService(policy, labRoot),
            packageVerifier,
            policy,
            labRoot);
        var formChoices = new FormChoiceService(
            new BethesdaPluginReader(), policy, labRoot);
        return new ExistingNpcEditDesktopContext(
            service, formChoices, CreateInitialRequest(labRoot));
    }

    private static ExistingNpcEditRequest CreateInitialRequest(WorkspacePath labRoot)
    {
        var project = Path.Combine(labRoot.Value, ".actorwright");
        var input = Path.Combine(project, "01-source-copies", "gate3-fixtures", "identity",
            "Data", "M3ArchetypeSSE.esp");
        var outputParent = Path.Combine(project, "03-builds", "work",
            "gate3-existing-npc-gui-product");
        Directory.CreateDirectory(outputParent);
        var expectedInputSha256 = File.Exists(input)
            ? HashFile(input)
            : new Sha256Hash(new string('0', 64));
        return new ExistingNpcEditRequest(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(input),
            expectedInputSha256,
            new FormId(0x00000800),
            FreshChild(outputParent),
            new PluginName("M3ArchetypeSSE-Gate3GuiGameplayEdit.esp"),
            null,
            null,
            null);
    }

    private static WorkspacePath FreshChild(string parent)
    {
        var stamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);
        return new WorkspacePath(Path.Combine(parent, "gui-run-" + stamp));
    }

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }
}
