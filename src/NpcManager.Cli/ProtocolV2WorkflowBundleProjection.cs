using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Infrastructure;

namespace NpcManager.Cli;

internal static class ProtocolV2WorkflowBundleProjection
{
    public static ProtocolArtifact Artifact(
        AgentWorkflowBundleDocument document,
        string producerCommand,
        string requestDigest) => new(
        "workflow-bundle",
        AgentWorkflowSchemas.BundleV1,
        document.Path.Value,
        document.Size,
        document.Sha256,
        producerCommand,
        requestDigest,
        document.Bundle.Artifacts
            .Select(artifact => artifact.Sha256)
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .ToImmutableArray(),
        "independentlyVerified");

    public static ImmutableArray<ProtocolNextAction> NextActions(
        AgentWorkflowBundleTransition transition)
    {
        ImmutableArray<ProtocolNextAction>.Builder projected =
            ImmutableArray.CreateBuilder<ProtocolNextAction>();
        foreach (ProtocolNextAction action in transition.Evaluation.NextActions)
        {
            bool needsPath = action.MissingPrerequisites.Contains(
                "--workflow-bundle", StringComparer.Ordinal);
            bool needsHash = action.MissingPrerequisites.Contains(
                "--workflow-bundle-sha256", StringComparer.Ordinal);
            if (needsPath != needsHash)
                throw new InvalidOperationException(
                    "Workflow next actions must require the bundle path and hash together.");

            ImmutableArray<ProtocolNextActionBinding> bindings =
                action.RequiredBindings;
            ImmutableArray<string> missing = action.MissingPrerequisites;
            if (needsPath)
            {
                bindings = bindings
                    .Add(new ProtocolNextActionBinding(
                        "--workflow-bundle",
                        transition.Document.Path.Value,
                        transition.Document.Sha256))
                    .Add(new ProtocolNextActionBinding(
                        "--workflow-bundle-sha256",
                        transition.Document.Sha256,
                        transition.Document.Sha256));
                missing = missing
                    .Where(value => value is not
                        "--workflow-bundle" and not
                        "--workflow-bundle-sha256")
                    .ToImmutableArray();
            }
            projected.Add(action with
            {
                RequiredBindings = bindings,
                MissingPrerequisites = missing
            });
        }
        return projected.ToImmutable();
    }
}
