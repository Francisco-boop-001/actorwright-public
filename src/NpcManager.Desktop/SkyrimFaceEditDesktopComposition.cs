using System.IO;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Rendering;

namespace NpcManager.Desktop;

internal static class SkyrimFaceEditDesktopComposition
{
    public static SkyrimFaceEditWorkspaceViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var authorityLoader = new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
        var choiceService = new BethesdaSkyrimHeadPartChoiceService(authorityLoader);
        var previewRenderer = new BlenderPreviewImageRenderer(
            new WorkspacePath(Path.Combine(
                labRoot.Value,
                "tools",
                "external",
                "blender-4.5.1-windows-x64",
                "blender-4.5.1-windows-x64",
                "blender.exe")),
            new WorkspacePath(Path.Combine(
                labRoot.Value,
                "tools",
                "external",
                "blender-4.5.1-pynifly-profile")),
            "render_preview_scene",
            policy,
            labRoot,
            new Sha256Hash(
                "B5D8EEB792FD63FDE1B06628D813B3B467BB3D54C1E5F93B5A1311CFB8DE0794"));
        var previewService = new SkyrimHeadPartPreviewService(
            previewRenderer,
            policy,
            labRoot,
            new WorkspacePath(Path.Combine(
                labRoot.Value,
                ".actorwright",
                "work",
                "face-edit-preview-cache")));
        var loader = new SkyrimFaceEditLoadService(
            new SkyrimHeadPartEditLoadService(choiceService),
            new FormChoiceService(new BethesdaPluginReader(), policy, labRoot),
            new BethesdaSkyrimRaceMenuPaintChoiceService(policy, labRoot),
            new BethesdaSkyrimFaceEditSourceReader());
        return new SkyrimFaceEditWorkspaceViewModel(
            loader,
            new NpcAppearanceOverrideService(policy, labRoot),
            previewService,
            labRoot);
    }
}
