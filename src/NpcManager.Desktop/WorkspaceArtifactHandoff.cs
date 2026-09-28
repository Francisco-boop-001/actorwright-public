using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

/// <summary>
/// One identity-preserving request from the integrated workbench to a
/// production child transaction.
/// </summary>
public sealed record SkyrimWorkspaceNavigationRequest(
    SkyrimMainWorkspaceRoute Route,
    SkyrimMainWorkspaceIdentity Identity,
    WorkspacePath SourcePlugin,
    SkyrimMainWorkspaceArtifactHandoff? Artifact);

/// <summary>
/// Implemented by completed child transactions that can return an
/// independently verified artifact to the integrated workbench.
/// </summary>
public interface ISkyrimMainWorkspaceArtifactOwner
{
    event EventHandler<SkyrimMainWorkspaceArtifactHandoff>? ArtifactCommitted;

    void BindWorkspaceIdentity(SkyrimMainWorkspaceIdentity identity);
}

internal sealed class SkyrimMainWorkspaceArtifactEmitter
{
    private SkyrimMainWorkspaceIdentity? identity;

    public SkyrimMainWorkspaceIdentity? BoundIdentity => identity;

    public event EventHandler<SkyrimMainWorkspaceArtifactHandoff>?
        ArtifactCommitted;

    public void Bind(SkyrimMainWorkspaceIdentity value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!string.Equals(
                value.Signature,
                "NPC_",
                StringComparison.Ordinal))
            throw new ArgumentException(
                "Only an NPC_ identity can be bound to an NPC child transaction.",
                nameof(value));
        identity = value;
    }

    public void Commit(
        object sender,
        string kind,
        WorkspacePath path,
        Sha256Hash sha256,
        FormId actualFormId,
        WorkspacePath? proposalPath = null,
        Sha256Hash? proposalSha256 = null)
    {
        if (identity is null)
            return;
        if (identity.FormId != actualFormId)
            throw new InvalidOperationException(
                "The completed child artifact belongs to a different FormID than the bound workbench identity.");
        ArtifactCommitted?.Invoke(
            sender,
            new SkyrimMainWorkspaceArtifactHandoff(
                identity,
                kind,
                path,
                sha256,
                proposalPath,
                proposalSha256,
                false));
    }
}
