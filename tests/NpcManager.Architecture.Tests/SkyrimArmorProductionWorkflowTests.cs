using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimArmorProductionWorkflow()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "armor-production-" + Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            string sourcePath = Path.Combine(dataRoot, "Source.esp");
            string providerPath = Path.Combine(dataRoot, "Provider.esp");
            WriteArmorSource(sourcePath);
            WriteArmorAddonReferenceProvider(providerPath);
            ReviewedGameIntake intake = CreateArmorReviewedIntake(
                root,
                dataRoot,
                sourcePath,
                providerPath);

            var reader = new BethesdaSkyrimArmorProductionReader();
            SkyrimArmorProductionReadResult result = reader.Read(
                new SkyrimArmorProductionReadRequest(
                    intake,
                    new PluginName("Source.esp"),
                    new FormId(0xA00),
                    new FormId(0xB00)));
            SkyrimArmorProductionSource source = result.Source ??
                throw new InvalidDataException(
                    "The production reader omitted its accepted source: " +
                    string.Join("; ", result.Diagnostics.Select(item => item.Message)));

            ImmutableArray<SkyrimArmorAddonSlotEvidence> expectedSlotEvidence =
            [
                new SkyrimArmorAddonSlotEvidence(
                    new FormReference(
                        new PluginName("Source.esp"),
                        new FormId(0x907)),
                    0x04),
                new SkyrimArmorAddonSlotEvidence(
                    new FormReference(
                        new PluginName("Source.esp"),
                        new FormId(0x908)),
                    0x20),
                new SkyrimArmorAddonSlotEvidence(
                    new FormReference(
                        new PluginName("Source.esp"),
                        new FormId(0x90A)),
                    0x10)
            ];
            ImmutableArray<string> expectedEditorIds =
                ["SourceArmor", "TemplateArmor"];
            Assert(result.Accepted &&
                   source.SourcePluginPath == new WorkspacePath(sourcePath) &&
                   source.SourcePluginSha256 == HashArmorProductionFile(sourcePath) &&
                   source.ExistingEditorIds.Select(item => item.Value)
                       .Order(StringComparer.OrdinalIgnoreCase)
                       .SequenceEqual(expectedEditorIds
                           .Order(StringComparer.OrdinalIgnoreCase)) &&
                   source.ArmorAddonSlotEvidence.SequenceEqual(expectedSlotEvidence),
                "The production reader lost reviewed source identity, EditorIDs, or ARMA evidence: " +
                $"accepted={result.Accepted}; source={source.SourcePluginPath}; " +
                $"armorIds={string.Join(',', source.ExistingEditorIds)}; " +
                $"slots={string.Join(',', source.ArmorAddonSlotEvidence.Select(item => $"{item.ArmorAddon}:{item.SlotMask:X8}"))}.");

            SkyrimArmorAddonProductionCatalogEntry primaryAddon =
                source.ArmorAddonCatalog.Single(item =>
                    item.Candidate.Reference == new FormReference(
                        new PluginName("Source.esp"), new FormId(0x907)) &&
                    !item.Candidate.IsDeleted && !item.Candidate.IsStale);
            Assert(primaryAddon.Provider == new PluginName("Source.esp") &&
                   primaryAddon.Candidate.Compatibility.Complete &&
                   primaryAddon.Candidate.Compatibility.PrimaryRace ==
                       new FormReference(
                           new PluginName("Source.esp"), new FormId(0x900)) &&
                   primaryAddon.EditableDocument is { } editable &&
                   editable.SourceFormId == new FormId(0x907) &&
                   editable.Mode == ArmorAddonProposalMode.Override &&
                   source.ExistingArmorAddonEditorIds.Contains(
                       new EditorId("SourceAddon")),
                "The production reader did not expose a reviewed compatible ARMA candidate and exact Gate-020 source document.");
            SkyrimArmorAddonProductionCatalogEntry additionalAddon =
                source.ArmorAddonCatalog.Single(item =>
                    item.Candidate.Reference == new FormReference(
                        new PluginName("Source.esp"), new FormId(0x908)) &&
                    !item.Candidate.IsDeleted && !item.Candidate.IsStale);
            SkyrimArmorAddonProductionCatalogEntry incompatibleAddon =
                source.ArmorAddonCatalog.Single(item =>
                    item.Candidate.Reference == new FormReference(
                        new PluginName("Source.esp"), new FormId(0x90A)) &&
                    !item.Candidate.IsDeleted && !item.Candidate.IsStale);
            ImmutableArray<string> expectedAddonEditorIds =
                ["ProviderSecondAddon", "ReplacementAddon", "SourceAddon"];
            Assert(source.ArmorAddonCatalog.Length == 6 &&
                   additionalAddon.Provider == new PluginName("Provider.esp") &&
                   additionalAddon.Candidate.Compatibility.PrimaryRace ==
                       new FormReference(
                           new PluginName("Source.esp"), new FormId(0x90B)) &&
                   additionalAddon.Candidate.Compatibility.AdditionalRaces
                       .Contains(new FormReference(
                           new PluginName("Source.esp"), new FormId(0x900))) &&
                   incompatibleAddon.Candidate.Compatibility.PrimaryRace ==
                       new FormReference(
                           new PluginName("Source.esp"), new FormId(0x90B)) &&
                   source.ArmorAddonCatalog.Count(item =>
                       item.Candidate.IsStale) == 2 &&
                   source.ArmorAddonCatalog.Count(item =>
                       item.Candidate.IsDeleted) == 1 &&
                   source.ExistingArmorAddonEditorIds.Select(item => item.Value)
                       .Order(StringComparer.OrdinalIgnoreCase)
                       .SequenceEqual(expectedAddonEditorIds),
                "The production catalog lost additional-race compatibility, exclusions, winning providers, or ARMA EditorIDs.");

            SkyrimArmorEditorDocument template = source.NewFromTemplate;
            Assert(template.Intent == SkyrimArmorEditorIntent.NewFromTemplate &&
                   template.Mode == ArmorProposalMode.New &&
                   template.SourceFormId == new FormId(0xA00) &&
                   template.TargetFormId == new FormId(0xB00) &&
                   template.EditorId == new EditorId("npcm_ARMO_SourceArmor") &&
                   template.Name == "Source armor" &&
                   template.Description == "Source description" &&
                   template.Value == 10 && template.Weight == 2 &&
                   template.ArmorRating == 5 && template.SlotMask == 0x04 &&
                   template.Race == new FormReference(
                       new PluginName("Source.esp"), new FormId(0x900)) &&
                   template.Enchantment == new FormReference(
                       new PluginName("Source.esp"), new FormId(0x901)) &&
                   template.ArmorAddons.SequenceEqual(
                       [new FormReference(new PluginName("Source.esp"),
                           new FormId(0x907))]) &&
                   template.Keywords.SequenceEqual(
                       [new FormReference(new PluginName("Source.esp"),
                           new FormId(0x906))]) &&
                   template.MaleWorldModel == "armor\\old_m.nif" &&
                   template.FemaleWorldModel == "armor\\old_f.nif" &&
                   template.ObjectBounds == new ArmorObjectBounds(-1, -1, -1, 1, 1, 1),
                "The production template document lost supported ARMO state.");

            Assert(source.BlankNew.Intent == SkyrimArmorEditorIntent.BlankNew &&
                   source.BlankNew.Mode == ArmorProposalMode.New &&
                   source.BlankNew.Race == template.Race &&
                   source.BlankNew.ArmorAddons.IsEmpty &&
                   source.BlankNew.Keywords.IsEmpty &&
                   source.BlankNew.Value == 0 &&
                   source.BlankNew.Weight == 0 &&
                   source.BlankNew.ArmorRating == 0 &&
                   source.BlankNew.SlotMask == 0 &&
                   source.BlankNew.TemplateArmor is null,
                "Blank intent imported template payload beyond required source/race identity.");

            Assert(source.OverrideExisting.Intent ==
                       SkyrimArmorEditorIntent.OverrideExisting &&
                   source.OverrideExisting.Mode == ArmorProposalMode.Override &&
                   source.OverrideExisting.EditorId == new EditorId("SourceArmor") &&
                   source.OverrideExisting.TargetFormId is null,
                "Override intent did not retain exact source identity.");

            var labRoot = new WorkspacePath("K:\\ExampleWorkspace");
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            WorkspacePath proposalPath = new(Path.Combine(
                root,
                "GeneratedArmor.armor-proposal.json"));
            WorkspacePath outputPath = new(Path.Combine(root, "GeneratedArmor.esp"));
            var request = new SkyrimArmorProductionRequest(
                source,
                template,
                proposalPath,
                outputPath);
            var transaction = new SkyrimArmorProductionTransactionService(
                new ArmorProposalService(
                    new BethesdaPluginReader(), policy, labRoot),
                new BethesdaArmorBinaryWriteService(policy, labRoot),
                new BethesdaSkyrimArmorProductionOutputReader(),
                policy,
                labRoot);

            SkyrimArmorProductionProposal proposal = await transaction.AnalyzeAsync(
                request,
                CancellationToken.None);
            Assert(proposal.IsApplicable &&
                   proposal.Artifact is
                   {
                       CompleteDocument: true,
                       Mode: ArmorProposalMode.New,
                       EditorId: "npcm_ARMO_SourceArmor",
                       TargetFormId: "0x00000B00"
                   } &&
                   File.Exists(proposalPath.Value) &&
                   !File.Exists(outputPath.Value),
                "Armor Review was not proposal-only or lost the complete document: " +
                string.Join("; ", proposal.Diagnostics.Select(item => item.Message)));

            byte[] reviewedBytes = await File.ReadAllBytesAsync(proposalPath.Value);
            await File.AppendAllTextAsync(proposalPath.Value, " ");
            SkyrimArmorProductionResult staleProposal = await transaction.ApplyAsync(
                request,
                proposal,
                CancellationToken.None);
            Assert(!staleProposal.Applied && !File.Exists(outputPath.Value) &&
                   staleProposal.Diagnostics.Any(item =>
                       item.Code == "armor-production-proposal-hash"),
                "Armor Apply accepted changed proposal bytes.");
            await File.WriteAllBytesAsync(proposalPath.Value, reviewedBytes);

            SkyrimArmorProductionResult rebound = await transaction.ApplyAsync(
                request with
                {
                    Document = template with
                    {
                        TargetFormId = new FormId(0xB01)
                    }
                },
                proposal,
                CancellationToken.None);
            Assert(!rebound.Applied && !File.Exists(outputPath.Value) &&
                   rebound.Diagnostics.Any(item =>
                       item.Code == "armor-production-request-binding"),
                "Armor Apply accepted a document not bound to Review.");

            SkyrimArmorProductionResult applied = await transaction.ApplyAsync(
                request,
                proposal,
                CancellationToken.None);
            Assert(applied.Applied && applied.Verification is
            {
                IsValid: true,
                ArmorRecordCount: 1,
                OtherRecordCount: 0,
                OwnerMatches: true,
                EditorIdMatches: true,
                DocumentMatches: true,
                MasterSetMatches: true
            },
                "The production ARMO failed its immediate readback: " +
                string.Join("; ", applied.Diagnostics.Select(item => item.Message)));

            SkyrimArmorProductionVerification verified =
                await transaction.VerifyAsync(
                    request,
                    proposal,
                    CancellationToken.None);
            Assert(verified.IsValid &&
                   verified.OwnerPlugin == new PluginName("GeneratedArmor.esp") &&
                   verified.TargetFormId == new FormId(0xB00) &&
                   verified.OutputSha256 is not null,
                "The explicit second ARMO reopen lost exact new-record identity.");

            WorkspacePath nestedAddonProposalPath = new(Path.Combine(
                root, "NestedAddon.armor-addon-proposal.json"));
            SkyrimArmorAddonEditorDocument nestedAddonDocument =
                primaryAddon.EditableDocument! with
                {
                    Intent = SkyrimArmorAddonEditorIntent.OverrideExisting,
                    MaleModel = "armor\\nested_gate020_m.nif",
                    FemaleModel = null,
                    SlotMask = 0x40,
                    MalePriority = 11,
                    FemalePriority = 23,
                    DetectionSound = 9,
                    WeaponAdjust = -8.25
                };
            SkyrimArmorAddonReferenceTransition nestedRow =
                SkyrimArmorAddonEditorRules.ToReferenceRow(
                    nestedAddonDocument,
                    template.Race!,
                    nestedAddonProposalPath);
            Assert(nestedRow.Accepted && nestedRow.Row is not null &&
                   nestedRow.Row.Reference == new FormReference(
                       new PluginName("Source.esp"), new FormId(0x907)),
                "The Gate-020 override did not retain its stable source-owned parent reference.");
            SkyrimArmorEditorDocument nestedArmorDocument = template with
            {
                AuthoredArmorAddons = [nestedRow.Row!]
            };
            WorkspacePath nestedArmorProposalPath = new(Path.Combine(
                root, "NestedArmor.armor-proposal.json"));
            WorkspacePath nestedArmorOutputPath = new(Path.Combine(
                root, "NestedArmor.esp"));
            WorkspacePath nestedAddonOutputPath = new(Path.Combine(
                root, "NestedAddonOverride.esp"));
            var nestedAddonRequest = new SkyrimArmorAddonProductionRequest(
                nestedRow.Row!.AuthoredProposal!,
                source.SourcePluginSha256,
                nestedAddonOutputPath);
            var nestedRequest = new SkyrimArmorProductionRequest(
                source,
                nestedArmorDocument,
                nestedArmorProposalPath,
                nestedArmorOutputPath,
                [nestedAddonRequest]);
            var nestedAddonTransaction =
                new SkyrimArmorAddonProductionTransactionService(
                    new ArmorAddonProposalService(
                        new BethesdaPluginReader(), policy, labRoot),
                    new BethesdaArmorAddonBinaryWriteService(policy, labRoot),
                    new BethesdaSkyrimArmorAddonProductionOutputReader(),
                    policy,
                    labRoot);
            var nestedTransaction = new SkyrimArmorProductionTransactionService(
                new ArmorProposalService(
                    new BethesdaPluginReader(), policy, labRoot),
                new BethesdaArmorBinaryWriteService(policy, labRoot),
                new BethesdaSkyrimArmorProductionOutputReader(),
                policy,
                labRoot,
                nestedAddonTransaction);
            SkyrimArmorProductionProposal nestedProposal =
                await nestedTransaction.AnalyzeAsync(
                    nestedRequest,
                    CancellationToken.None);
            Assert(nestedProposal.IsApplicable &&
                   nestedProposal.ArmorAddonProposals.Length == 1 &&
                   nestedProposal.ArmorAddonProposals[0].IsApplicable &&
                   File.Exists(nestedArmorProposalPath.Value) &&
                   File.Exists(nestedAddonProposalPath.Value) &&
                   !File.Exists(nestedArmorOutputPath.Value) &&
                   !File.Exists(nestedAddonOutputPath.Value),
                "Combined Review did not remain proposal-only for child ARMA and parent ARMO: " +
                string.Join("; ", nestedProposal.Diagnostics.Select(item => item.Message)));
            var rebuiltNestedRequest = nestedRequest with
            {
                ArmorAddonOutputs = [nestedAddonRequest with { }]
            };
            SkyrimArmorProductionResult nestedApplied =
                await nestedTransaction.ApplyAsync(
                    rebuiltNestedRequest,
                    nestedProposal,
                    CancellationToken.None);
            Assert(nestedApplied.Applied &&
                   nestedApplied.Verification is
                   {
                       IsValid: true,
                       ArmorAddonVerifications.Length: 1
                   } nestedVerification &&
                   nestedVerification.ArmorAddonVerifications[0] is
                   {
                       IsValid: true,
                       OwnerPlugin.Value: "Source.esp",
                       ProviderPlugin.Value: "NestedAddonOverride.esp",
                       TargetFormId.Value: 0x907,
                       DocumentMatches: true
                   } &&
                   File.Exists(nestedArmorOutputPath.Value) &&
                   File.Exists(nestedAddonOutputPath.Value),
                "Combined Apply did not write and independently reopen child ARMA before parent ARMO: " +
                string.Join("; ", nestedApplied.Diagnostics.Select(item => item.Message)));
            SkyrimArmorProductionVerification nestedVerified =
                await nestedTransaction.VerifyAsync(
                    rebuiltNestedRequest,
                    nestedProposal,
                    CancellationToken.None);
            Assert(nestedVerified.IsValid &&
                   nestedVerified.ArmorAddonVerifications is
                       [{ IsValid: true, OutputSha256: not null }],
                "Combined explicit verification did not reopen both child and parent outputs.");

            WorkspacePath rollbackAddonProposalPath = new(Path.Combine(
                root, "RollbackAddon.armor-addon-proposal.json"));
            SkyrimArmorAddonReferenceTransition rollbackRow =
                SkyrimArmorAddonEditorRules.ToReferenceRow(
                    nestedAddonDocument,
                    template.Race!,
                    rollbackAddonProposalPath);
            Assert(rollbackRow.Accepted && rollbackRow.Row is not null,
                "The rollback regression could not create its exact nested ARMA row.");
            WorkspacePath rollbackArmorProposalPath = new(Path.Combine(
                root, "RollbackArmor.armor-proposal.json"));
            WorkspacePath rollbackArmorOutputPath = new(Path.Combine(
                root, "RollbackArmor.esp"));
            WorkspacePath rollbackAddonOutputPath = new(Path.Combine(
                root, "RollbackAddonOverride.esp"));
            var rollbackAddonRequest = new SkyrimArmorAddonProductionRequest(
                rollbackRow.Row!.AuthoredProposal!,
                source.SourcePluginSha256,
                rollbackAddonOutputPath);
            var rollbackRequest = new SkyrimArmorProductionRequest(
                source,
                template with { AuthoredArmorAddons = [rollbackRow.Row!] },
                rollbackArmorProposalPath,
                rollbackArmorOutputPath,
                [rollbackAddonRequest]);
            var rollbackTransaction = new SkyrimArmorProductionTransactionService(
                new ArmorProposalService(
                    new BethesdaPluginReader(), policy, labRoot),
                new RefusingArmorBinaryWriteService(),
                new BethesdaSkyrimArmorProductionOutputReader(),
                policy,
                labRoot,
                nestedAddonTransaction);
            SkyrimArmorProductionProposal rollbackProposal =
                await rollbackTransaction.AnalyzeAsync(
                    rollbackRequest,
                    CancellationToken.None);
            Assert(rollbackProposal.IsApplicable &&
                   !File.Exists(rollbackAddonOutputPath.Value),
                "The rollback regression Review was not proposal-only.");
            SkyrimArmorProductionResult rolledBack =
                await rollbackTransaction.ApplyAsync(
                    rollbackRequest,
                    rollbackProposal,
                    CancellationToken.None);
            Assert(!rolledBack.Applied &&
                   !File.Exists(rollbackArmorOutputPath.Value) &&
                   !File.Exists(rollbackAddonOutputPath.Value) &&
                   rolledBack.Diagnostics.Any(item =>
                       item.Code == "armor-production-nested-rollback"),
                "A parent ARMO write failure left its already verified child ARMA output behind: " +
                string.Join("; ", rolledBack.Diagnostics.Select(item => item.Message)));

            await File.AppendAllTextAsync(sourcePath, "stale");
            SkyrimArmorProductionProposal staleSource =
                await transaction.AnalyzeAsync(
                    request with
                    {
                        OutputProposal = new WorkspacePath(Path.Combine(
                            root,
                            "Stale.armor-proposal.json")),
                        OutputPlugin = new WorkspacePath(Path.Combine(
                            root,
                            "Stale.esp"))
                    },
                    CancellationToken.None);
            Assert(!staleSource.IsApplicable &&
                   staleSource.Diagnostics.Any(item =>
                       item.Code == "armor-production-source-stale"),
                "Armor Review accepted source bytes changed after intake.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static ReviewedGameIntake CreateArmorReviewedIntake(
        string root,
        string dataRoot,
        string sourcePath,
        string providerPath)
    {
        PluginName plugin = new("Source.esp");
        return new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(root),
            new WorkspacePath(dataRoot),
            new WorkspacePath(Path.Combine(root, "load-order.json")),
            new WorkspacePath(Path.Combine(root, "future-output")),
            new Sha256Hash(new string('4', 64)),
            [
                new PluginClosureReviewEntry(
                    plugin,
                    0,
                    true,
                    true,
                    true,
                    true,
                    true,
                    new WorkspacePath(sourcePath),
                    HashArmorProductionFile(sourcePath),
                    []),
                new PluginClosureReviewEntry(
                    new PluginName("Provider.esp"),
                    1,
                    true,
                    true,
                    false,
                    false,
                    true,
                    new WorkspacePath(providerPath),
                    HashArmorProductionFile(providerPath),
                    [plugin])
            ],
            [],
            [],
            [],
            0,
            new Sha256Hash(new string('5', 64)),
            new Sha256Hash(new string('6', 64)),
            false);
    }

    private static Sha256Hash HashArmorProductionFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed class RefusingArmorBinaryWriteService :
        IArmorBinaryWriteService
    {
        public ValueTask<ArmorBinaryWriteResult> WriteAsync(
            ArmorBinaryWriteRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ArmorBinaryWriteResult(
                false,
                request.Proposal,
                request.Output,
                null,
                null,
                [new Diagnostic(
                    "test-parent-write-refused",
                    DiagnosticSeverity.Error,
                    "The parent writer intentionally refused this rollback regression.")]));
        }
    }
}
