using NpcManager.Application;

namespace NpcManager.Rendering;

public interface IReferencePresetCpuRenderer
{
    ReferencePresetCpuRenderResult Render(
        ReferencePresetCpuRenderRequest request);
}
