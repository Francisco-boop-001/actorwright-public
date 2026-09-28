using System.Globalization;
using System.IO;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

internal static class SkyrimSavePackageDesktopComposition
{
    public static SkyrimSavePackageViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var reader = new PackageManifestReader(policy, labRoot);
        var verifier = new PackageVerifyService(reader);
        var service = new SkyrimSavePackageService(
            new PackageInspectService(reader),
            verifier,
            new BethesdaSkyrimSavePluginTransformer(policy, labRoot),
            new BethesdaSkyrimSavePluginVerifier(policy, labRoot),
            new BethesdaSkyrimBsaService(policy, labRoot),
            new BethesdaPluginReader(),
            policy,
            labRoot);
        string project = Path.Combine(
            labRoot.Value,
            ".actorwright");
        string stamp = DateTime.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);
        return new SkyrimSavePackageViewModel(
            service,
            labRoot,
            new WorkspacePath(project),
            new WorkspacePath(Path.Combine(
                project,
                "03-builds",
                "work",
                "npc-package-promotion-" + stamp)));
    }
}
