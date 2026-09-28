namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpFirstUseArchitectureScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-jslot-external-smp-first-use";

    public ValueTask RunAsync(CancellationToken cancellationToken) =>
        Preview254ExternalSmpJslotSelectionScenario.RunFirstUseAsync(
            cancellationToken);
}
