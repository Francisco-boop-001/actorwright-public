using System.Collections.Immutable;
using System.IO;
using NpcManager.Application;
using NpcManager.Desktop;
using NpcManager.Domain;

namespace NpcManager.Desktop.Smoke;

internal static partial class Program
{
    private static void RunSkyrimCharGenOptionsProductionWorkspaceTest() =>
        RunSkyrimCharGenOptionsProductionWorkspaceTestAsync()
            .GetAwaiter().GetResult();

    private static async Task
        RunSkyrimCharGenOptionsProductionWorkspaceTestAsync()
    {
        var opening = CharGenOptionsDefaults.For(
            GameEdition.SkyrimSpecialEdition);
        Sha256Hash sourceHash = new(new string('1', 64));
        Sha256Hash proposalHash = new(new string('2', 64));
        var optionsReader = new FakeCharGenOptionsReader(opening, sourceHash);
        var production = new FakeCharGenOptionsProductionService(proposalHash);
        using var viewModel = new SkyrimCharGenOptionsProductionWorkspaceViewModel(
            optionsReader,
            production)
        {
            SourceOptions =
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\tools\fixtures\sky-gui-022\skyrim-options.json",
            SourceOptionsSha256 = sourceHash.Value,
            DataRoot =
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work\sky-gui-022-native-facetint-fixture\Data",
            PluginOrder = "Skyrim.esm\r\nNativeTintFixture.esp",
            NpcReference = "NativeTintFixture.esp|0x00000802",
            ExpectedRace = "NativeTintFixture.esp|0x00000801",
            ExpectedSex = NpcSex.Female,
            ProposalPath =
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work\sky-gui-022-options-proposal.json",
            OutputRoot =
                @"K:\ExampleWorkspace\projects\NpcManagerReimplementation\03-builds\work\sky-gui-022-options-output"
        };

        await viewModel.LoadAsync();
        Assert(viewModel.IsLoaded && viewModel.CanOpenEditor &&
               !viewModel.HasAcceptedOptions &&
               production.ReviewCalls == 0 && production.ApplyCalls == 0,
            "CharGen source review wrote or did not admit the exact opening artifact.");

        SkyrimCharGenOptionsEditorViewModel cancelled = viewModel.CreateEditor();
        cancelled.Texture.DiffuseResolution = FaceGenChannelResolution.R512;
        cancelled.Cancel();
        Assert(!viewModel.TryCommitEditor(cancelled) &&
               !viewModel.HasAcceptedOptions,
            "A cancelled CharGen modal leaked an accepted value to production.");

        SkyrimCharGenOptionsEditorViewModel editor = viewModel.CreateEditor();
        editor.Texture.DiffuseResolution = FaceGenChannelResolution.R512;
        editor.Texture.BakeSseRaceMenuOverlays = false;
        Assert(editor.TrySave() && viewModel.TryCommitEditor(editor) &&
               viewModel.HasAcceptedOptions,
            "A supported complete CharGen document did not reach the outer transaction.");

        await viewModel.ReviewAsync();
        Assert(viewModel.IsReviewed && !viewModel.HasCompleted &&
               production.ReviewCalls == 1 && production.ApplyCalls == 0 &&
               production.LastReview is { } reviewed &&
               reviewed.AcceptedOptions.DiffuseResolution ==
               FaceGenChannelResolution.R512 &&
               reviewed.AcceptedOptions.BakeSseRaceMenuOverlays ==
               false,
            "Proposal review wrote output or lost the accepted CharGen settings.");

        SkyrimCharGenOptionsEditorViewModel changed = viewModel.CreateEditor();
        changed.Texture.GenerateTga = true;
        Assert(changed.TrySave() && viewModel.TryCommitEditor(changed) &&
               !viewModel.IsReviewed,
            "Editing an accepted CharGen document did not invalidate its proposal.");
        changed.Texture.GenerateTga = false;
        Assert(changed.TrySave() && viewModel.TryCommitEditor(changed),
            "The supported CharGen document could not be restored after invalidation.");

        await viewModel.ReviewAsync();
        await viewModel.ApplyAsync();
        Assert(viewModel.HasCompleted && production.ReviewCalls == 2 &&
               production.ApplyCalls == 1 &&
               production.LastApply?.ExpectedProposalSha256 == proposalHash &&
               viewModel.OptionsSha256 == new string('3', 64) &&
               viewModel.ReceiptSha256 == new string('4', 64),
            "The desktop production owner did not apply the exact reviewed proposal and expose readback evidence.");

        Console.WriteLine(
            "PASS Skyrim CharGen production workspace covers source review, modal rollback, proposal invalidation, and exact-hash apply.");
    }

    private sealed class FakeCharGenOptionsReader(
        CharGenOptions options,
        Sha256Hash sourceHash) : IFaceGenOptionsService
    {
        public ValueTask<FaceGenOptionsResult> ValidateAsync(
            FaceGenOptionsRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool valid = request.Edition == GameEdition.SkyrimSpecialEdition &&
                         request.ExpectedSha256 == sourceHash && !request.Apply;
            return ValueTask.FromResult(new FaceGenOptionsResult(
                valid,
                false,
                request.Edition,
                request.Input,
                null,
                sourceHash,
                null,
                valid ? options : null,
                valid
                    ? []
                    : [new Diagnostic("fixture-invalid",
                        DiagnosticSeverity.Error, "fixture refused")]));
        }
    }

    private sealed class FakeCharGenOptionsProductionService(
        Sha256Hash proposalHash) : ISkyrimCharGenOptionsProductionService
    {
        public int ReviewCalls { get; private set; }
        public int ApplyCalls { get; private set; }
        public SkyrimCharGenOptionsProductionReviewRequest? LastReview
        { get; private set; }
        public SkyrimCharGenOptionsProductionApplyRequest? LastApply
        { get; private set; }

        public ValueTask<SkyrimCharGenOptionsProductionReviewResult>
            ReviewAsync(
                SkyrimCharGenOptionsProductionReviewRequest request,
                CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ReviewCalls++;
            LastReview = request;
            var proposal = new SkyrimCharGenOptionsProductionProposal(
                "1",
                "skyrim-chargen-options-production-proposal",
                request.SourceOptions.Value,
                request.SourceOptionsSha256.Value,
                request.AcceptedOptions,
                request.DataRoot.Value,
                request.PluginOrder.Select(item => item.Value)
                    .ToImmutableArray(),
                request.Npc.ToString(),
                request.ExpectedSex.ToString().ToLowerInvariant(),
                request.ExpectedRace.ToString(),
                request.OutputRoot.Value,
                false);
            return ValueTask.FromResult(
                new SkyrimCharGenOptionsProductionReviewResult(
                    true,
                    request.OutputProposal,
                    proposalHash,
                    proposal,
                    []));
        }

        public ValueTask<SkyrimCharGenOptionsProductionApplyResult> ApplyAsync(
            SkyrimCharGenOptionsProductionApplyRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCalls++;
            LastApply = request;
            var optionsHash = new Sha256Hash(new string('3', 64));
            var receiptHash = new Sha256Hash(new string('4', 64));
            return ValueTask.FromResult(
                new SkyrimCharGenOptionsProductionApplyResult(
                    true,
                    proposalHash,
                    new FaceGenOptionsDocumentWriteResult(
                        true,
                        true,
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(Path.Combine(
                            LastReview!.OutputRoot.Value,
                            "accepted-options.json")),
                        optionsHash,
                        LastReview.AcceptedOptions,
                        []),
                    new SkyrimCharGenFaceTintBakeResult(
                        true,
                        null,
                        receiptHash,
                        []),
                    []));
        }
    }
}
