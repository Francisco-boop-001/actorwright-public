using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimArmorAddonProductionWorkflow()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "armor-addon-production-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            string sourcePath = Path.Combine(dataRoot, "Source.esp");
            string providerPath = Path.Combine(dataRoot, "Provider.esp");
            WriteArmorSource(sourcePath);
            WriteArmorAddonReferenceProvider(providerPath);
            ReviewedGameIntake intake = CreateArmorReviewedIntake(
                root, dataRoot, sourcePath, providerPath);
            SkyrimArmorProductionSource source =
                new BethesdaSkyrimArmorProductionReader().Read(
                    new SkyrimArmorProductionReadRequest(
                        intake,
                        new PluginName("Source.esp"),
                        new FormId(0xA00),
                        new FormId(0xB00))).Source ??
                throw new InvalidDataException(
                    "The ARMA production test could not reconstruct its reviewed source.");
            SkyrimArmorAddonEditorDocument opening = source.ArmorAddonCatalog
                .Single(item => item.Candidate.Reference == new FormReference(
                    new PluginName("Source.esp"), new FormId(0x907)) &&
                    !item.Candidate.IsDeleted && !item.Candidate.IsStale)
                .EditableDocument ?? throw new InvalidDataException(
                    "The source-owned ARMA did not expose an editable document.");
            SkyrimArmorAddonEditorDocument edited = opening with
            {
                Intent = SkyrimArmorAddonEditorIntent.OverrideExisting,
                MaleModel = "armor\\gate020_override_m.nif",
                FemaleModel = null,
                MaleFirstPersonModel = "armor\\gate020_override_1st_m.nif",
                FemaleFirstPersonModel = null,
                SlotMask = 0x00000040,
                MaleSkinTexture = null,
                FemaleSkinTexture = null,
                MaleSkinTextureSwapList = null,
                FemaleSkinTextureSwapList = null,
                FootstepSet = null,
                ArtObject = null,
                MalePriority = 17,
                FemalePriority = 29,
                MaleWeightSliderEnabled = true,
                FemaleWeightSliderEnabled = false,
                DetectionSound = 7,
                WeaponAdjust = 12.5
            };
            WorkspacePath proposalPath = new(Path.Combine(
                root, "Gate020.armor-addon-proposal.json"));
            WorkspacePath outputPath = new(Path.Combine(
                root, "Gate020Override.esp"));
            SkyrimArmorAddonProposalAdapterResult adapted =
                SkyrimArmorAddonEditorRules.ToProposal(edited, proposalPath);
            Assert(adapted.Accepted && adapted.Proposal is not null,
                "The complete Gate-020 document did not adapt to a production proposal.");
            var request = new SkyrimArmorAddonProductionRequest(
                adapted.Proposal!,
                HashArmorProductionFile(sourcePath),
                outputPath);
            var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var transaction = new SkyrimArmorAddonProductionTransactionService(
                new ArmorAddonProposalService(
                    new BethesdaPluginReader(), policy, labRoot),
                new BethesdaArmorAddonBinaryWriteService(policy, labRoot),
                new BethesdaSkyrimArmorAddonProductionOutputReader(),
                policy,
                labRoot);

            SkyrimArmorAddonProductionProposal proposal =
                await transaction.AnalyzeAsync(request, CancellationToken.None);
            Assert(proposal.IsApplicable &&
                   proposal.Artifact is
                   {
                       CompleteDocument: true,
                       Mode: ArmorAddonProposalMode.Override,
                       EditorId: "SourceAddon"
                   } &&
                   File.Exists(proposalPath.Value) &&
                   !File.Exists(outputPath.Value),
                "ARMA Review was not proposal-only or lost the complete document: " +
                string.Join("; ", proposal.Diagnostics.Select(item => item.Message)));

            byte[] reviewedBytes = await File.ReadAllBytesAsync(proposalPath.Value);
            await File.AppendAllTextAsync(proposalPath.Value, " ");
            SkyrimArmorAddonProductionResult staleProposal =
                await transaction.ApplyAsync(
                    request, proposal, CancellationToken.None);
            Assert(!staleProposal.Applied && !File.Exists(outputPath.Value) &&
                   staleProposal.Diagnostics.Any(item =>
                       item.Code == "armor-addon-production-proposal-hash"),
                "ARMA Apply accepted changed proposal bytes.");
            await File.WriteAllBytesAsync(proposalPath.Value, reviewedBytes);

            SkyrimArmorAddonProductionResult rebound =
                await transaction.ApplyAsync(
                    request with
                    {
                        OutputPlugin = new WorkspacePath(Path.Combine(
                            root, "Changed.esp"))
                    },
                    proposal,
                    CancellationToken.None);
            Assert(!rebound.Applied && !File.Exists(Path.Combine(root, "Changed.esp")) &&
                   rebound.Diagnostics.Any(item =>
                       item.Code == "armor-addon-production-request-binding"),
                "ARMA Apply accepted an output not bound to Review.");

            SkyrimArmorAddonProductionResult applied =
                await transaction.ApplyAsync(
                    request, proposal, CancellationToken.None);
            Assert(applied.Applied && applied.Verification is
            {
                IsValid: true,
                ArmorAddonRecordCount: 1,
                OtherRecordCount: 0,
                OwnerMatches: true,
                EditorIdMatches: true,
                DocumentMatches: true,
                MasterSetMatches: true
            },
                "The production ARMA failed immediate independent readback: " +
                string.Join("; ", applied.Diagnostics.Select(item => item.Message)));

            SkyrimArmorAddonProductionVerification verified =
                await transaction.VerifyAsync(
                    request, proposal, CancellationToken.None);
            Assert(verified.IsValid &&
                   verified.ProviderPlugin == new PluginName("Gate020Override.esp") &&
                   verified.OwnerPlugin == new PluginName("Source.esp") &&
                   verified.TargetFormId == new FormId(0x907) &&
                   verified.OutputSha256 is not null,
                "The explicit second ARMA reopen lost owner/provider identity.");

            SkyrimArmorAddonEditorDocument newDocument = edited with
            {
                Intent = SkyrimArmorAddonEditorIntent.NewFromTemplate,
                Mode = ArmorAddonProposalMode.New,
                EditorId = new EditorId("npcm_ARMA_Gate020New"),
                TargetFormId = new FormId(0xC00),
                TargetPlugin = new PluginName("ExpectedNewAddon.esp"),
                SeedFromSource = true
            };
            WorkspacePath newProposalPath = new(Path.Combine(
                root, "New.armor-addon-proposal.json"));
            SkyrimArmorAddonProposalRequestForTest newRequest =
                AdaptArmorAddonProductionForTest(newDocument, newProposalPath);
            SkyrimArmorAddonProductionProposal wrongNewOutput =
                await transaction.AnalyzeAsync(
                    new SkyrimArmorAddonProductionRequest(
                        newRequest.Proposal,
                        HashArmorProductionFile(sourcePath),
                        new WorkspacePath(Path.Combine(root, "WrongName.esp"))),
                    CancellationToken.None);
            Assert(!wrongNewOutput.IsApplicable &&
                   wrongNewOutput.Diagnostics.Any(item =>
                       item.Code == "armor-addon-production-target-plugin"),
                "A new ARMA output filename was allowed to disagree with its qualified target plugin.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private sealed record SkyrimArmorAddonProposalRequestForTest(
        ArmorAddonProposalRequest Proposal);

    private static SkyrimArmorAddonProposalRequestForTest
        AdaptArmorAddonProductionForTest(
            SkyrimArmorAddonEditorDocument document,
            WorkspacePath outputProposal)
    {
        SkyrimArmorAddonProposalAdapterResult adapted =
            SkyrimArmorAddonEditorRules.ToProposal(document, outputProposal);
        return new SkyrimArmorAddonProposalRequestForTest(
            adapted.Proposal ?? throw new InvalidDataException(
                "The new Gate-020 document failed proposal adaptation."));
    }
}
