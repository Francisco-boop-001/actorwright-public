using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Composition boundary shared by the CLI and desktop surfaces.</summary>
public static class NpcCreationComposition
{
    public static INpcCreationService Create(IWorkspacePolicy policy, WorkspacePath labRoot) =>
        new NpcCreationService(policy, labRoot);
}
