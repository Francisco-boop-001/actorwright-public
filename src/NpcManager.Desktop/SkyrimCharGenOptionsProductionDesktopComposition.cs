using System.IO;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Desktop;

internal static class SkyrimCharGenOptionsProductionDesktopComposition
{
    internal static SkyrimCharGenOptionsProductionWorkspaceViewModel Create(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
    {
        var options = new FaceGenOptionsService(policy, labRoot);
        var assetIndexer = new BatchScopedAssetIndexer(
            new BethesdaAssetIndexer());
        var contentResolver = new SkyrimAssetContentResolver(policy, labRoot);
        var pluginAuthority =
            new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
        var recordResolver =
            new BethesdaSkyrimNativeFaceTintRecordResolver(policy, labRoot);
        var nativePipeline = new SkyrimNativeFaceTintPipelineService(
            pluginAuthority,
            recordResolver,
            new SkyrimNativeFaceTintAuthorityPlanner(
                assetIndexer, policy, labRoot),
            new SkyrimNativeFaceTintMaterializationService(
                recordResolver,
                contentResolver,
                new SkyrimNativeFaceTintBuildService(
                    policy,
                    labRoot,
                    new InProcessDdsTextureDecoder(labRoot))));
        var optionsBoundBake = new SkyrimCharGenFaceTintBakeService(
            options,
            nativePipeline,
            policy,
            labRoot);
        var production = new SkyrimCharGenOptionsProductionService(
            options,
            options,
            optionsBoundBake,
            policy,
            labRoot);
        var viewModel = new SkyrimCharGenOptionsProductionWorkspaceViewModel(
            options,
            production);

        string projectRoot = Path.Combine(
            labRoot.Value, ".actorwright");
        string sourceOptions = Path.Combine(
            projectRoot,
            "tools",
            "fixtures",
            "sky-gui-022",
            "skyrim-options.json");
        if (File.Exists(sourceOptions))
        {
            viewModel.SourceOptions = sourceOptions;
            using var stream = File.OpenRead(sourceOptions);
            viewModel.SourceOptionsSha256 = Convert.ToHexString(
                SHA256.HashData(stream));
        }

        string fixtureRoot = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            "sky-gui-022-native-facetint-fixture-20260723-3");
        viewModel.DataRoot = Path.Combine(fixtureRoot, "Data");
        viewModel.PluginOrder = "NativeTintFixture.esp";
        viewModel.NpcReference = "NativeTintFixture.esp|0x00000802";
        viewModel.ExpectedRace = "NativeTintFixture.esp|0x00000801";
        viewModel.ProposalPath = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            "sky-gui-022-desktop-options-proposal.json");
        viewModel.OutputRoot = Path.Combine(
            projectRoot,
            "03-builds",
            "work",
            "sky-gui-022-desktop-options-output");
        return viewModel;
    }
}
