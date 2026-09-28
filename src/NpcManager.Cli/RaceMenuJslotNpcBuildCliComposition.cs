using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Pipeline;

namespace NpcManager.Cli;

/// <summary>
/// One CLI-owned composition boundary for the JSlot transaction.  All
/// external-SMP services are supplied by the caller so the native bake,
/// selection transaction, precheck, and protocol bridge share the same
/// route/discovery/physics/exclusion and strict-verifier instances.
/// </summary>
internal sealed class RaceMenuJslotNpcBuildCliComposition
{
    private RaceMenuJslotNpcBuildCliComposition(
        IRaceMenuJslotNpcBuildService buildService,
        RaceMenuJslotNpcBuildCommandExecutionBridge commandBridge,
        IRaceMenuNpcStandaloneAuthorityReader standaloneAuthorityReader,
        INpcBuildPreflightService preflightService,
        IRaceMenuNpcExecutionRequestFileLoader requestLoader)
    {
        BuildService = buildService ??
            throw new ArgumentNullException(nameof(buildService));
        CommandBridge = commandBridge ??
            throw new ArgumentNullException(nameof(commandBridge));
        StandaloneAuthorityReader = standaloneAuthorityReader ??
            throw new ArgumentNullException(nameof(standaloneAuthorityReader));
        PreflightService = preflightService ??
            throw new ArgumentNullException(nameof(preflightService));
        RequestLoader = requestLoader ??
            throw new ArgumentNullException(nameof(requestLoader));
    }

    public IRaceMenuJslotNpcBuildService BuildService { get; }

    public RaceMenuJslotNpcBuildCommandExecutionBridge CommandBridge { get; }

    public IRaceMenuNpcStandaloneAuthorityReader StandaloneAuthorityReader { get; }

    public INpcBuildPreflightService PreflightService { get; }

    public IRaceMenuNpcExecutionRequestFileLoader RequestLoader { get; }

    public static RaceMenuJslotNpcBuildCliComposition Create(
        IPresetService presetService,
        ISkyrimFaceRecordPluginAuthorityLoader pluginAuthorityLoader,
        IRaceMenuJslotCompanionBuildService companionBuildService,
        IRaceMenuPresetSelectionTransactionService selectionTransactionService,
        IRaceMenuNpcBuildService npcBuildService,
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot,
        IRaceMenuNpcExecutionRequestFileLoader requestLoader,
        INpcBuildPreflightService preflightService,
        IRaceMenuJslotExternalHeadPartPrecheckService externalHeadPartPrecheck,
        IRaceMenuJslotOutputPluginBindingReader outputPluginBindingReader,
        IExternalHeadPartInstallVerifier externalHeadPartInstallVerifier,
        IRaceMenuNpcStandaloneAuthorityReader standaloneAuthorityReader)
    {
        ArgumentNullException.ThrowIfNull(presetService);
        ArgumentNullException.ThrowIfNull(pluginAuthorityLoader);
        ArgumentNullException.ThrowIfNull(companionBuildService);
        ArgumentNullException.ThrowIfNull(selectionTransactionService);
        ArgumentNullException.ThrowIfNull(npcBuildService);
        ArgumentNullException.ThrowIfNull(workspacePolicy);
        ArgumentNullException.ThrowIfNull(requestLoader);
        ArgumentNullException.ThrowIfNull(preflightService);
        ArgumentNullException.ThrowIfNull(externalHeadPartPrecheck);
        ArgumentNullException.ThrowIfNull(outputPluginBindingReader);
        ArgumentNullException.ThrowIfNull(externalHeadPartInstallVerifier);
        ArgumentNullException.ThrowIfNull(standaloneAuthorityReader);

        var buildService = new RaceMenuJslotNpcBuildService(
            presetService,
            pluginAuthorityLoader,
            companionBuildService,
            selectionTransactionService,
            npcBuildService,
            workspacePolicy,
            labRoot,
            preflightService,
            externalHeadPartPrecheck,
            outputPluginBindingReader,
            externalHeadPartInstallVerifier);
        return new RaceMenuJslotNpcBuildCliComposition(
            buildService,
            new RaceMenuJslotNpcBuildCommandExecutionBridge(buildService),
            standaloneAuthorityReader,
            preflightService,
            requestLoader);
    }
}
