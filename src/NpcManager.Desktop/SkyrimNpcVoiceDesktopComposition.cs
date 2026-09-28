using System.Globalization;
using System.IO;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Desktop;

internal static class SkyrimNpcVoiceDesktopComposition
{
    public static SkyrimNpcVoiceViewModel Create(WorkspacePath workspaceRoot)
    {
        var service = new SkyrimNpcVoiceService(workspaceRoot);
        string stamp = DateTime.UtcNow.ToString(
            "yyyyMMdd-HHmmss-fff",
            CultureInfo.InvariantCulture);
        return new SkyrimNpcVoiceViewModel(
            service,
            workspaceRoot,
            new WorkspacePath(Path.Combine(
                workspaceRoot.Value,
                ".actorwright",
                "work",
                "npc-voice-sample-" + stamp)),
            service);
    }
}
