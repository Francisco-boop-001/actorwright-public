using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Drawing;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;
using NpcManager.Pipeline;
using NpcManager.Rendering;

namespace NpcManager.Cli.Tests;

internal static class Preview258ConsumerDefectTests
{
    public static async Task RunAsync()
    {
        var workspace = new WorkspacePath(Path.Combine(
            AppContext.BaseDirectory,
            "preview258-consumer-defects-" + Guid.NewGuid().ToString("N")));
        var dataRoot = Path.Combine(workspace.Value, "Data");
        var input = new WorkspacePath(Path.Combine(dataRoot, "Input.esp"));
        var outputRoot = new WorkspacePath(Path.Combine(workspace.Value, "standalone-package"));
        Directory.CreateDirectory(dataRoot);
        WriteFixture(input, includeFactions: true);
        InsertInterleavedUnknownTargetSubrecord(input.Value);
        InsertDiscontiguousFactionAtoms(input.Value);
        AppendExtendedKeywordSubrecord(input.Value);
        InsertKeywordCountCompanion(input.Value);
        var sourceExtendedKeywords = ReadTargetSubrecord(input.Value, "KWDA");
        var sourceOwnerIndex = ReadInventory(input.Value)
            .Single(row => row.Signature == "NPC_" &&
                           (row.RawFormId & 0x00FF_FFFFu) == 0x800)
            .RawFormId >> 24;
        Assert(sourceExtendedKeywords.Length == 65_536 &&
               Enumerable.Range(0, sourceExtendedKeywords.Length / 4).All(index =>
                       BinaryPrimitives.ReadUInt32LittleEndian(
                       sourceExtendedKeywords.AsSpan(index * 4, 4)) ==
                   ((sourceOwnerIndex << 24) | 0x0000_0805u)),
            "fixture did not emit the expected large extended KWDA payload");
        WriteSourceSidecars(dataRoot);
        var sourceMasteredDataRoot = Path.Combine(workspace.Value,
            "source-mastered-data");
        Directory.CreateDirectory(sourceMasteredDataRoot);
        var sourceMasteredInput = new WorkspacePath(Path.Combine(
            sourceMasteredDataRoot, "Input.esp"));
        WriteFixture(sourceMasteredInput);
        WriteSourceSidecars(sourceMasteredDataRoot);
        var sourceMasteredHash = Hash(sourceMasteredInput.Value);

        var policy = new KOnlyWorkspacePolicy(
            workspace,
            new WorkspacePath("F:\\ExampleGame"));
        var overrideService = new NpcOverrideService(policy, workspace);
        var service = new ExistingNpcEditService(
            overrideService,
            new PackageVerifyService(new PackageManifestReader(policy, workspace)),
            policy,
            workspace);
        try
        {
            RunCompressedMorphReadbackRegression(workspace);
            var sourceInventory = ReadInventory(input.Value);
            var sourceHash = Hash(input.Value);
            var (runner, output, error) = Program.CreateRunner(
                existingNpcEditService: service);
            var resultCode = await runner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", input.Value, "--input-sha256", sourceHash,
                "--npc", "0x00000800", "--output-root", outputRoot.Value,
                "--plugin", "Output.esp", "--output-kind", "standalone-copy",
                "--editor-id", "StandaloneCopiedNpc", "--json"
            ]), CancellationToken.None);

            Assert(resultCode == CommandExitCode.Success,
                "standalone copy was refused: " + output + error);
            var outputPlugin = Path.Combine(outputRoot.Value, "Data", "Output.esp");
            var outputInventory = ReadInventory(outputPlugin);
            Assert(outputInventory.Select(row =>
                       (row.GroupPath, row.Ordinal, row.Signature, row.RawFormId))
                   .SequenceEqual(sourceInventory.Select(row =>
                       (row.GroupPath, row.Ordinal, row.Signature, row.RawFormId))),
                "standalone copy changed ordered record inventory");
            Assert(outputInventory.Where(row => row.Signature != "NPC_")
                       .Select(row => row.Sha256)
                       .SequenceEqual(sourceInventory.Where(row => row.Signature != "NPC_")
                           .Select(row => row.Sha256)),
                "standalone copy changed a non-target record");
            Assert(!BethesdaNpcStandaloneCopyAdapter.ReadMasters(
                           new WorkspacePath(outputPlugin))
                       .Contains(new PluginName("Input.esp")),
                "standalone copy appended the input plugin as a master");
            var sourceAtoms = ReadTargetRawAtoms(input.Value);
            var outputAtoms = ReadTargetRawAtoms(outputPlugin);
            var sourceUnauthorizedAtoms = sourceAtoms
                .Where(atom => atom.Signature != "EDID")
                .ToArray();
            var outputUnauthorizedAtoms = outputAtoms
                .Where(atom => atom.Signature != "EDID")
                .ToArray();
            Assert(sourceUnauthorizedAtoms.Length > 0 &&
                   sourceUnauthorizedAtoms.Any(atom => atom.Signature == "ZZZZ"),
                "fixture did not contain the interleaved unknown target atom");
            Assert(sourceUnauthorizedAtoms.Length == outputUnauthorizedAtoms.Length &&
                   sourceUnauthorizedAtoms.Zip(outputUnauthorizedAtoms)
                       .All(pair => pair.First.Signature == pair.Second.Signature &&
                                    pair.First.Bytes.SequenceEqual(pair.Second.Bytes)),
                "standalone copy changed unauthorized target atom bytes or order");
            var tamperedRawOutput = Path.Combine(workspace.Value,
                "tampered-raw-target.esp");
            File.Copy(outputPlugin, tamperedRawOutput);
            MutateTargetUnknownAtom(tamperedRawOutput);
            var tamperedVerification = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                new WorkspacePath(tamperedRawOutput),
                new FormId(0x00000800),
                [new MutationChange("EditorID", "StandaloneSourceNpc",
                    "StandaloneCopiedNpc")]);
            Assert(!tamperedVerification.Verified &&
                   tamperedVerification.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-target-subrecord-drift"),
                "standalone raw verifier accepted a tampered unknown target atom");
            var movedUnknownOutput = Path.Combine(workspace.Value,
                "moved-unknown-target.esp");
            File.Copy(outputPlugin, movedUnknownOutput);
            MoveTargetUnknownAtomBeforeEditorId(movedUnknownOutput);
            var movedUnknownVerification = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                new WorkspacePath(movedUnknownOutput),
                new FormId(0x00000800),
                [new MutationChange("EditorID", "StandaloneSourceNpc",
                    "StandaloneCopiedNpc")]);
            Assert(!movedUnknownVerification.Verified &&
                   movedUnknownVerification.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-target-subrecord-drift"),
                "standalone raw verifier accepted an unauthorized atom moved across EDID");
            var legacyVerification = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                new WorkspacePath(outputPlugin),
                new FormId(0x00000800));
            Assert(!legacyVerification.Verified &&
                   legacyVerification.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-target-subrecord-drift"),
                "legacy three-argument standalone Verify bypassed target raw preservation");

            var identityOutput = new WorkspacePath(Path.Combine(
                workspace.Value, "identity-gap-standalone.esp"));
            var identityRequest = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                input,
                new Sha256Hash(sourceHash),
                new FormId(0x00000800),
                new WorkspacePath(Path.Combine(workspace.Value,
                    "identity-gap-proposal.json")),
                identityOutput,
                new NpcOverridePatch(
                    new EditorId("IdentityGapNpc"),
                    new NpcName("Identity Gap NPC"),
                    null),
                ExistingNpcEditOutputKind.StandaloneCopy);
            var identityProposal = await overrideService.AnalyzeAsync(
                identityRequest, CancellationToken.None);
            Assert(identityProposal.IsApplicable,
                "identity-gap proposal was refused: " +
                string.Join(" | ", identityProposal.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            var identityApply = await overrideService.ApplyAsync(
                identityRequest, identityProposal, CancellationToken.None);
            Assert(identityApply.Applied,
                "identity-gap standalone apply was refused: " +
                string.Join(" | ", identityApply.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            SwapTargetAuthorizedAtomsAcrossUnknown(identityOutput.Value);
            var identityVerification = await overrideService.VerifyAsync(
                identityRequest, identityProposal, CancellationToken.None);
            Assert(!identityVerification.IsValid &&
                   identityVerification.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-target-subrecord-drift"),
                "standalone verification accepted an EDID/FULL identity swap across an unknown atom");
            AssertStandalonePrivateReferencesOwned(outputPlugin);
            var extendedKeywords = ReadTargetSubrecord(outputPlugin, "KWDA");
            var outputOwnerIndex = outputInventory
                .Single(row => row.Signature == "NPC_" &&
                               (row.RawFormId & 0x00FF_FFFFu) == 0x800)
                .RawFormId >> 24;
            Assert(extendedKeywords.Length == 65_536 &&
                   Enumerable.Range(0, extendedKeywords.Length / 4).All(index =>
                       BinaryPrimitives.ReadUInt32LittleEndian(
                           extendedKeywords.AsSpan(index * 4, 4)) ==
                       ((outputOwnerIndex << 24) | 0x0000_0805u)),
                "standalone copy did not preserve the extended KWDA payload and its private links");

            Assert(File.Exists(Path.Combine(outputRoot.Value, "Data", "meshes",
                           "actors", "character", "FaceGenData", "FaceGeom",
                           "Output.esp", "00000800.nif")) &&
                   File.Exists(Path.Combine(outputRoot.Value, "Data", "textures",
                           "actors", "character", "FaceGenData", "FaceTint",
                           "Output.esp", "00000800.dds")) &&
                   File.Exists(Path.Combine(outputRoot.Value, "Data", "textures",
                           "actors", "character", "FaceGenData", "FaceDiffuse",
                           "Output.esp", "00000800.dds")) &&
                   File.Exists(Path.Combine(outputRoot.Value, "Data", "textures",
                           "actors", "character", "FaceGenData", "FaceNormal",
                           "Output.esp", "00000800.dds")),
                "standalone copy did not relocate the target FaceGen sidecars");
            using var manifest = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(outputRoot.Value, "npcmanager-package.json")));
            var artifacts = manifest.RootElement.GetProperty("artifacts")
                .EnumerateArray()
                .Select(item => item.GetProperty("relativePath").GetString())
                .ToArray();
            Assert(artifacts.Contains("Data/meshes/actors/character/FaceGenData/FaceGeom/Output.esp/00000800.nif") &&
                   artifacts.Contains("Data/textures/actors/character/FaceGenData/FaceTint/Output.esp/00000800.dds") &&
                   artifacts.Contains("Data/textures/actors/character/FaceGenData/FaceDiffuse/Output.esp/00000800.dds") &&
                   artifacts.Contains("Data/textures/actors/character/FaceGenData/FaceNormal/Output.esp/00000800.dds"),
                "standalone copy did not declare relocated sidecars in the manifest");
            using var proposalEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(outputRoot.Value, "evidence", "npc-edit-proposal.json")));
            Assert(proposalEvidence.RootElement.GetProperty("outputKind").GetString() == "standalone-copy" &&
                   proposalEvidence.RootElement.GetProperty("sourceDependencyPlugin").ValueKind == JsonValueKind.Null,
                "standalone package falsely declared a source dependency");
            using var verificationEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(outputRoot.Value, "evidence", "npc-edit-verification.json")));
            Assert(verificationEvidence.RootElement.GetProperty("sourceInventory").GetArrayLength() ==
                       sourceInventory.Length &&
                   verificationEvidence.RootElement.GetProperty("outputInventory").GetArrayLength() ==
                       sourceInventory.Length &&
                   verificationEvidence.RootElement.GetProperty("sourceGroups").GetArrayLength() > 0 &&
                   verificationEvidence.RootElement.GetProperty("outputGroups").GetArrayLength() ==
                       verificationEvidence.RootElement.GetProperty("sourceGroups").GetArrayLength() &&
                   verificationEvidence.RootElement.GetProperty("independentPreservation").GetBoolean(),
                "standalone package did not persist independent inventory evidence");

            var outputBeforeNoOverwrite = File.ReadAllBytes(outputPlugin);
            var (repeatRunner, repeatOutput, repeatError) = Program.CreateRunner(
                existingNpcEditService: service);
            var repeatCode = await repeatRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", input.Value, "--input-sha256", sourceHash,
                "--npc", "0x00000800", "--output-root", outputRoot.Value,
                "--plugin", "Output.esp", "--output-kind", "standalone-copy",
                "--editor-id", "StandaloneCopiedNpc", "--json"
            ]), CancellationToken.None);
            Assert(repeatCode == CommandExitCode.ValidationFailure &&
                   repeatError.ToString().Length == 0 &&
                   repeatOutput.ToString().Contains("existing-npc-edit-output-exists",
                       StringComparison.Ordinal) &&
                   File.ReadAllBytes(outputPlugin).SequenceEqual(outputBeforeNoOverwrite),
                "standalone package overwrote an existing output root");

            var directRoot = Path.Combine(workspace.Value, "direct-standalone");
            Directory.CreateDirectory(directRoot);
            var directOutput = new WorkspacePath(Path.Combine(directRoot, "Direct.esp"));
            var directProposal = new WorkspacePath(Path.Combine(
                directRoot, "direct-proposal.json"));
            var directRequest = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                input,
                new Sha256Hash(sourceHash),
                new FormId(0x00000800),
                directProposal,
                directOutput,
                new NpcOverridePatch(
                    new EditorId("DirectStandaloneNpc"),
                    null,
                    null),
                ExistingNpcEditOutputKind.StandaloneCopy);
            var directAnalysis = await overrideService.AnalyzeAsync(
                directRequest, CancellationToken.None);
            Assert(directAnalysis.IsApplicable,
                "direct standalone verification fixture analysis was refused");
            var directApply = await overrideService.ApplyAsync(
                directRequest, directAnalysis, CancellationToken.None);
            Assert(directApply.Applied && directApply.Verification?.IndependentPreservation == true,
                "direct standalone verification fixture write was refused");
            var directOutputBytes = File.ReadAllBytes(directOutput.Value);
            try
            {
                MutateTargetUnknownAtom(directOutput.Value);
                var serviceRawTamper = await overrideService.VerifyAsync(
                    directRequest, directAnalysis, CancellationToken.None);
                Assert(!serviceRawTamper.IsValid &&
                       !serviceRawTamper.IndependentPreservation &&
                       serviceRawTamper.Diagnostics.Any(item =>
                           item.Code == "npc-standalone-target-subrecord-drift"),
                    "service verification accepted a raw unknown-atom tamper or retained independent preservation");
            }
            finally
            {
                File.WriteAllBytes(directOutput.Value, directOutputBytes);
            }
            MutateTargetFormLink(directOutput.Value, "HCLF", 0x0200_0801u);
            var privateLinkRejected = await overrideService.VerifyAsync(
                directRequest, directAnalysis, CancellationToken.None);
            Assert(!privateLinkRejected.IsValid && privateLinkRejected.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-reference-ownership"),
                "standalone verification accepted an unrequested private target FormLink");
            MutateTargetFormLink(directOutput.Value, "HCLF", 0x0100_0801u);
            MutateTargetEditorId(directOutput.Value, "BrokenStandaloneNpc");
            var fieldRejected = await overrideService.VerifyAsync(
                directRequest, directAnalysis, CancellationToken.None);
            Assert(!fieldRejected.IsValid && fieldRejected.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-target-field-drift"),
                "standalone verification accepted a changed requested target field");
            MutateTargetEditorId(directOutput.Value, "DirectStandaloneNpc");
            AppendTargetFormLinkSubrecord(directOutput.Value, "HCLF",
                0x0100_0801u);
            var appendedPrivateLinkRejected = await overrideService.VerifyAsync(
                directRequest, directAnalysis, CancellationToken.None);
            Assert(!appendedPrivateLinkRejected.IsValid &&
                   appendedPrivateLinkRejected.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-reference-ownership"),
                "standalone verification accepted an appended output-owned private target FormLink");

            var factionReference = new FormReference(
                new PluginName("Input.esp"), new FormId(0x00000806));
            var secondFactionReference = new FormReference(
                new PluginName("Input.esp"), new FormId(0x00000807));
            var factionPatch = new NpcFactionPatch(
                [new NpcFactionEntry(factionReference, 3),
                 new NpcFactionEntry(secondFactionReference, 4)],
                [], [], []);
            var factionOutput = new WorkspacePath(Path.Combine(
                workspace.Value, "faction-standalone.esp"));
            var factionRequest = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                input,
                new Sha256Hash(sourceHash),
                new FormId(0x00000800),
                new WorkspacePath(Path.Combine(workspace.Value,
                    "faction-proposal.json")),
                factionOutput,
                new NpcOverridePatch(null, null, null, null, factionPatch),
                ExistingNpcEditOutputKind.StandaloneCopy);
            var factionProposal = await overrideService.AnalyzeAsync(
                factionRequest, CancellationToken.None);
            Assert(factionProposal.IsApplicable,
                "standalone discontiguous faction mutation was refused: " +
                string.Join(" | ", factionProposal.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            try
            {
                BethesdaNpcStandaloneCopyAdapter.Write(
                    factionRequest,
                    factionOutput,
                    factionProposal.Changes);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "standalone discontiguous faction mutation was refused: " +
                    string.Join(" | ", factionProposal.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")), exception);
            }
            var factionVerification = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                factionOutput,
                new FormId(0x00000800),
                factionProposal.Changes);
            Assert(factionVerification.Verified,
                "standalone discontiguous faction mutation failed raw verification: " +
                string.Join(" | ", factionVerification.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            var sourceFactionSkeleton = ReadTargetRawAtoms(input.Value)
                .Where(atom => atom.Signature is "SNAM" or "YZZZ")
                .Select(atom => atom.Signature)
                .ToArray();
            var outputFactionSkeleton = ReadTargetRawAtoms(factionOutput.Value)
                .Where(atom => atom.Signature is "SNAM" or "YZZZ")
                .Select(atom => atom.Signature)
                .ToArray();
            Assert(sourceFactionSkeleton.SequenceEqual(outputFactionSkeleton),
                "standalone collection mutation collapsed a discontiguous authorized region across an unauthorized atom");
            var factionOwnerIndex = ReadInventory(factionOutput.Value)
                .Single(row => row.Signature == "NPC_" &&
                               (row.RawFormId & 0x00FF_FFFFu) == 0x800)
                .RawFormId >> 24;
            var factionEntries = ReadFactionRawEntries(factionOutput.Value);
            Assert(factionEntries.SequenceEqual([
                    ((factionOwnerIndex << 24) | 0x0000_0806u, 3u),
                    ((factionOwnerIndex << 24) | 0x0000_0807u, 4u)]),
                "standalone faction mutation did not retain FormID/rank 3/4 on the respective sides of YZZZ");
            var sourceYzz = ReadTargetRawAtoms(input.Value)
                .Single(atom => atom.Signature == "YZZZ").Bytes;
            var outputYzz = ReadTargetRawAtoms(factionOutput.Value)
                .Single(atom => atom.Signature == "YZZZ").Bytes;
            Assert(sourceYzz.SequenceEqual(outputYzz),
                "standalone faction mutation changed the interleaved YZZZ payload");

            var keywordReference = new FormReference(
                new PluginName("Input.esp"), new FormId(0x00000805));
            var keywordRequest = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                input,
                new Sha256Hash(sourceHash),
                new FormId(0x00000800),
                new WorkspacePath(Path.Combine(workspace.Value,
                    "keyword-proposal.json")),
                new WorkspacePath(Path.Combine(workspace.Value,
                    "keyword-standalone.esp")),
                new NpcOverridePatch(
                    null,
                    null,
                    null,
                    new NpcKeywordPatch(
                        new NpcKeywordListPatch([keywordReference], [], []))),
                ExistingNpcEditOutputKind.StandaloneCopy);
            var keywordProposal = await overrideService.AnalyzeAsync(
                keywordRequest, CancellationToken.None);
            Assert(keywordProposal.IsApplicable,
                "standalone keyword collection mutation was refused: " +
                string.Join(" | ", keywordProposal.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            try
            {
                BethesdaNpcStandaloneCopyAdapter.Write(
                    keywordRequest,
                    keywordRequest.OutputPlugin,
                    keywordProposal.Changes);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException(
                    "standalone keyword collection mutation was refused: " +
                    string.Join(" | ", keywordProposal.Diagnostics.Select(item =>
                        $"{item.Code}: {item.Message}")), exception);
            }
            var keywordVerification = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                keywordRequest.OutputPlugin,
                new FormId(0x00000800),
                keywordProposal.Changes);
            Assert(keywordVerification.Verified,
                "standalone keyword collection mutation failed raw verification: " +
                string.Join(" | ", keywordVerification.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            var keywordOutput = keywordRequest.OutputPlugin.Value;
            var keywordCount = ReadTargetSubrecord(keywordOutput, "KSIZ");
            Assert(keywordCount.Length == 4 &&
                   BinaryPrimitives.ReadUInt32LittleEndian(keywordCount) == 1,
                "standalone keyword mutation did not retain a valid KSIZ companion");
            var missingKeywordCountOutput = Path.Combine(workspace.Value,
                "missing-keyword-count.esp");
            File.Copy(keywordOutput, missingKeywordCountOutput);
            MutateTargetAtomSignature(missingKeywordCountOutput, "KSIZ", "QIZZ");
            var missingKeywordCountRejected = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                new WorkspacePath(missingKeywordCountOutput),
                new FormId(0x00000800),
                keywordProposal.Changes);
            Assert(!missingKeywordCountRejected.Verified &&
                   missingKeywordCountRejected.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-target-count-drift"),
                "standalone verifier accepted nonempty KWDA without a KSIZ companion");
            MutateTargetCountCompanion(keywordOutput, "KSIZ", 2);
            var keywordCountRejected = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                keywordRequest.OutputPlugin,
                new FormId(0x00000800),
                keywordProposal.Changes);
            Assert(!keywordCountRejected.Verified &&
                   keywordCountRejected.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-target-count-drift"),
                "standalone verifier accepted a tampered KSIZ count companion");

            var shortNameOutput = new WorkspacePath(Path.Combine(
                workspace.Value, "short-name-standalone.esp"));
            var shortNameRequest = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                input,
                new Sha256Hash(sourceHash),
                new FormId(0x00000800),
                new WorkspacePath(Path.Combine(workspace.Value,
                    "short-name-proposal.json")),
                shortNameOutput,
                new NpcOverridePatch(
                    null,
                    null,
                    null,
                    Names: new NpcEditableNamesPatch(
                        default,
                        OptionalNpcText.Set("New short name"))),
                ExistingNpcEditOutputKind.StandaloneCopy);
            var shortNameProposal = await overrideService.AnalyzeAsync(
                shortNameRequest, CancellationToken.None);
            Assert(shortNameProposal.IsApplicable,
                "absent-to-present ShortName proposal was refused: " +
                string.Join(" | ", shortNameProposal.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            var shortNameApply = await overrideService.ApplyAsync(
                shortNameRequest, shortNameProposal, CancellationToken.None);
            Assert(shortNameApply.Applied,
                "absent-to-present ShortName standalone apply was refused: " +
                string.Join(" | ", shortNameApply.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));

            var clearNameOutput = new WorkspacePath(Path.Combine(
                workspace.Value, "clear-name-standalone.esp"));
            var clearNameRequest = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                input,
                new Sha256Hash(sourceHash),
                new FormId(0x00000800),
                new WorkspacePath(Path.Combine(workspace.Value,
                    "clear-name-proposal.json")),
                clearNameOutput,
                new NpcOverridePatch(
                    null,
                    null,
                    null,
                    Names: new NpcEditableNamesPatch(
                        OptionalNpcText.Clear(),
                        default)),
                ExistingNpcEditOutputKind.StandaloneCopy);
            var clearNameProposal = await overrideService.AnalyzeAsync(
                clearNameRequest, CancellationToken.None);
            Assert(clearNameProposal.IsApplicable,
                "present-to-absent Name proposal was refused: " +
                string.Join(" | ", clearNameProposal.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            BethesdaNpcStandaloneCopyAdapter.Write(
                clearNameRequest,
                clearNameOutput,
                clearNameProposal.Changes);
            var clearNameVerification = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                clearNameOutput,
                new FormId(0x00000800),
                clearNameProposal.Changes);
            Assert(clearNameVerification.Verified &&
                   ReadTargetRawAtoms(clearNameOutput.Value)
                       .All(atom => atom.Signature != "FULL"),
                "present-to-absent Name standalone mutation was not preserved across the raw gap model");

            var collectionGapRoot = Path.Combine(workspace.Value,
                "collection-gap-data");
            Directory.CreateDirectory(collectionGapRoot);
            var collectionGapInput = new WorkspacePath(Path.Combine(
                collectionGapRoot, "Input.esp"));
            File.Copy(input.Value, collectionGapInput.Value);
            SplitKeywordDataWithUnknown(collectionGapInput.Value);
            var collectionGapSourceHash = Hash(collectionGapInput.Value);
            var collectionGapOutput = new WorkspacePath(Path.Combine(
                workspace.Value, "collection-gap-standalone.esp"));
            var collectionGapKeywordRequest = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                collectionGapInput,
                new Sha256Hash(collectionGapSourceHash),
                new FormId(0x00000800),
                new WorkspacePath(Path.Combine(workspace.Value,
                    "collection-gap-proposal.json")),
                collectionGapOutput,
                new NpcOverridePatch(
                    null,
                    null,
                    null,
                    new NpcKeywordPatch(
                        new NpcKeywordListPatch([
                            new FormReference(new PluginName("Input.esp"),
                                new FormId(0x00000805)),
                            new FormReference(new PluginName("Input.esp"),
                                new FormId(0x00000808))
                        ], [], []))),
                ExistingNpcEditOutputKind.StandaloneCopy);
            var collectionGapProposal = await overrideService.AnalyzeAsync(
                collectionGapKeywordRequest, CancellationToken.None);
            Assert(collectionGapProposal.IsApplicable,
                "equal-total collection proposal was refused: " +
                string.Join(" | ", collectionGapProposal.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            BethesdaNpcStandaloneCopyAdapter.Write(
                collectionGapKeywordRequest,
                collectionGapOutput,
                collectionGapProposal.Changes);
            var collectionGapVerification =
                BethesdaNpcStandaloneCopyAdapter.Verify(
                    collectionGapInput,
                    collectionGapOutput,
                    new FormId(0x00000800),
                    collectionGapProposal.Changes);
            Assert(collectionGapVerification.Verified,
                "equal-total collection mutation did not preserve both KWDA gaps: " +
                string.Join(" | ", collectionGapVerification.Diagnostics.Select(item =>
                    $"{item.Code}: {item.Message}")));
            var expectedCollectionGapSignatures = new[]
                { "KSIZ", "KWDA", "YKKW", "KWDA" };
            var sourceCollectionGapAtoms = ReadTargetRawAtoms(
                    collectionGapInput.Value)
                .Where(atom => expectedCollectionGapSignatures.Contains(
                    atom.Signature, StringComparer.Ordinal))
                .Select(atom => atom.Signature)
                .ToArray();
            var outputCollectionGapAtoms = ReadTargetRawAtoms(
                    collectionGapOutput.Value)
                .Where(atom => expectedCollectionGapSignatures.Contains(
                    atom.Signature, StringComparer.Ordinal))
                .Select(atom => atom.Signature)
                .ToArray();
            Assert(sourceCollectionGapAtoms.SequenceEqual(
                       expectedCollectionGapSignatures) &&
                   outputCollectionGapAtoms.SequenceEqual(
                       expectedCollectionGapSignatures),
                "equal-total collection mutation changed the KSIZ/KWDA/YKKW/KWDA gap shape");
            var sourceCollectionGapUnknown = ReadTargetRawAtoms(
                    collectionGapInput.Value)
                .Single(atom => atom.Signature == "YKKW").Bytes;
            var outputCollectionGapUnknown = ReadTargetRawAtoms(
                    collectionGapOutput.Value)
                .Single(atom => atom.Signature == "YKKW").Bytes;
            Assert(sourceCollectionGapUnknown.SequenceEqual(
                       outputCollectionGapUnknown),
                "equal-total collection mutation changed the YKKW bytes");
            var collectionGapOwnerIndex = ReadInventory(
                    collectionGapOutput.Value)
                .Single(row => row.Signature == "NPC_" &&
                               (row.RawFormId & 0x00FF_FFFFu) == 0x800)
                .RawFormId >> 24;
            Assert(ReadTargetRawFormIdEntries(collectionGapOutput.Value,
                       "KWDA").SequenceEqual([
                           (collectionGapOwnerIndex << 24) | 0x0000_0805u,
                           (collectionGapOwnerIndex << 24) | 0x0000_0808u]),
                "equal-total collection mutation did not apply the requested KWDA entries");

            var groupMutated = Path.Combine(workspace.Value, "group-mutated.esp");
            File.Copy(outputPlugin, groupMutated);
            MutateFirstGroupHeader(groupMutated);
            var groupRejected = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                new WorkspacePath(groupMutated),
                new FormId(0x00000800));
            Assert(!groupRejected.Verified && groupRejected.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-group-drift"),
                "independent standalone preservation accepted changed group-header bytes");

            var reparseRoot = Path.Combine(Path.GetTempPath(),
                "npc-preview258-sidecar-" + Guid.NewGuid().ToString("N"));
            string? sidecarJunction = null;
            try
            {
                var reparseData = Path.Combine(reparseRoot, "Data");
                var reparseInput = new WorkspacePath(Path.Combine(reparseData,
                    "ReparseInput.esp"));
                Directory.CreateDirectory(reparseData);
                File.Copy(input.Value, reparseInput.Value);
                var outsideFaceTint = Path.Combine(reparseRoot, "outside-face-tint");
                Directory.CreateDirectory(outsideFaceTint);
                File.WriteAllBytes(Path.Combine(outsideFaceTint, "00000800.dds"),
                    [0x21, 0x22, 0x23, 0x24]);
                var sidecarParent = Path.Combine(reparseData, "textures", "actors",
                    "character", "FaceGenData", "FaceTint");
                Directory.CreateDirectory(sidecarParent);
                sidecarJunction = Path.Combine(sidecarParent, "ReparseInput.esp");
                CreateJunction(sidecarJunction, outsideFaceTint);
                var sidecarDestination = new WorkspacePath(Path.Combine(
                    workspace.Value, "reparse-sidecar-output"));
                Directory.CreateDirectory(sidecarDestination.Value);
                var sidecarRejected = false;
                try
                {
                    _ = BethesdaNpcStandaloneCopyAdapter.CopyLooseTargetSidecars(
                        reparseInput,
                        sidecarDestination,
                        new PluginName("ReparseOutput.esp"),
                        new FormId(0x00000800));
                }
                catch (InvalidDataException exception)
                {
                    sidecarRejected = exception.Message.StartsWith(
                        "npc-standalone-sidecar-reparse:",
                        StringComparison.Ordinal);
                }
                Assert(sidecarRejected,
                    "sidecar discovery followed a reparse-point ancestor");
            }
            finally
            {
                if (sidecarJunction is not null) RemoveJunction(sidecarJunction);
                DeleteDirectory(reparseRoot);
            }

            var sourceMasteredRoot = new WorkspacePath(Path.Combine(
                workspace.Value, "source-mastered-package"));
            var (sourceMasteredRunner, sourceMasteredOutput, sourceMasteredError) =
                Program.CreateRunner(existingNpcEditService: service);
            var sourceMasteredCode = await sourceMasteredRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", sourceMasteredInput.Value, "--input-sha256",
                sourceMasteredHash,
                "--npc", "0x00000800", "--output-root", sourceMasteredRoot.Value,
                "--plugin", "SourceMastered.esp", "--output-kind",
                "source-mastered-override", "--editor-id", "SourceMasteredNpc", "--json"
            ]), CancellationToken.None);
            Assert(sourceMasteredCode == CommandExitCode.Success &&
                   sourceMasteredError.ToString().Length == 0 &&
                   sourceMasteredOutput.ToString().Contains("STATIC_PASS_RUNTIME_REQUIRED",
                       StringComparison.Ordinal),
                "source-mastered output kind was not preserved");
            using var sourceProposalEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(sourceMasteredRoot.Value, "evidence", "npc-edit-proposal.json")));
            var sourceProposal = sourceProposalEvidence.RootElement;
            Assert(sourceProposal.GetProperty("outputKind").GetString() == "source-mastered-override" &&
                   sourceProposal.GetProperty("sourceDependencyPlugin").GetString() == "Input.esp" &&
                   string.Equals(sourceProposal.GetProperty("sourceDependencySha256").GetString(),
                       sourceMasteredHash, StringComparison.OrdinalIgnoreCase),
                "source-mastered package omitted the exact source dependency");
            using var sourceRuntimeEvidence = JsonDocument.Parse(await File.ReadAllTextAsync(
                Path.Combine(sourceMasteredRoot.Value, "evidence", "runtime-test-instructions.json")));
            var sourceInstructions = sourceRuntimeEvidence.RootElement.GetProperty("instructions").GetString()!;
            Assert(sourceInstructions.Contains("Input.esp", StringComparison.Ordinal) &&
                   sourceInstructions.Contains("SourceMastered.esp", StringComparison.Ordinal) &&
                   sourceInstructions.Contains("not independently standalone", StringComparison.Ordinal),
                "source-mastered runtime instructions were not dependency-honest");

            MutateFirstNonTargetRecord(outputPlugin);
            var rejected = BethesdaNpcStandaloneCopyAdapter.Verify(
                input,
                new WorkspacePath(outputPlugin),
                new FormId(0x00000800));
            Assert(!rejected.Verified && rejected.Diagnostics.Any(item =>
                       item.Code == "npc-standalone-inventory-drift"),
                "independent standalone preservation did not reject inventory drift");

            var missingKindRoot = new WorkspacePath(Path.Combine(
                workspace.Value, "missing-output-kind"));
            var (missingKindRunner, _, missingKindError) = Program.CreateRunner(
                existingNpcEditService: service);
            var missingKindCode = await missingKindRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", input.Value, "--input-sha256", sourceHash,
                "--npc", "0x00000800", "--output-root", missingKindRoot.Value,
                "--plugin", "MissingKind.esp", "--editor-id", "MissingKindNpc",
                "--json"
            ]), CancellationToken.None);
            Assert(missingKindCode == CommandExitCode.UsageError &&
                   missingKindError.ToString().Contains("--output-kind", StringComparison.Ordinal),
                "omitting --output-kind was not refused as a usage error");

            var unknownKindRoot = new WorkspacePath(Path.Combine(
                workspace.Value, "unknown-output-kind"));
            var (unknownKindRunner, _, unknownKindError) = Program.CreateRunner(
                existingNpcEditService: service);
            var unknownKindCode = await unknownKindRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", input.Value, "--input-sha256", sourceHash,
                "--npc", "0x00000800", "--output-root", unknownKindRoot.Value,
                "--plugin", "UnknownKind.esp", "--output-kind", "unknown",
                "--editor-id", "UnknownKindNpc", "--json"
            ]), CancellationToken.None);
            Assert(unknownKindCode == CommandExitCode.UsageError &&
                   unknownKindError.ToString().Contains("source-mastered-override",
                       StringComparison.Ordinal),
                "unknown --output-kind was not refused as a usage error");

            var localizedInput = new WorkspacePath(Path.Combine(dataRoot, "Localized.esp"));
            File.Copy(input.Value, localizedInput.Value);
            PatchTes4Flags(localizedInput.Value, 0x80u);
            var localizedRoot = new WorkspacePath(Path.Combine(
                workspace.Value, "localized-output"));
            var (localizedRunner, localizedOutput, localizedError) = Program.CreateRunner(
                existingNpcEditService: service);
            var localizedCode = await localizedRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", localizedInput.Value, "--input-sha256", Hash(localizedInput.Value),
                "--npc", "0x00000800", "--output-root", localizedRoot.Value,
                "--plugin", "Localized.esp", "--output-kind", "standalone-copy",
                "--editor-id", "LocalizedNpc", "--json"
            ]), CancellationToken.None);
            Assert(localizedCode == CommandExitCode.ValidationFailure &&
                   localizedError.ToString().Length == 0 &&
                   localizedOutput.ToString().Contains("npc-standalone-localized-source",
                       StringComparison.Ordinal),
                "localized standalone source was not refused with its typed diagnostic");

            var archive = Path.Combine(dataRoot, "Input - Textures.bsa");
            File.WriteAllBytes(archive, [0x42, 0x53, 0x41]);
            var archiveRoot = new WorkspacePath(Path.Combine(
                workspace.Value, "archive-output"));
            var (archiveRunner, archiveOutput, archiveError) = Program.CreateRunner(
                existingNpcEditService: service);
            var archiveCode = await archiveRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", input.Value, "--input-sha256", sourceHash,
                "--npc", "0x00000800", "--output-root", archiveRoot.Value,
                "--plugin", "Archive.esp", "--output-kind", "standalone-copy",
                "--editor-id", "ArchiveNpc", "--json"
            ]), CancellationToken.None);
            Assert(archiveCode == CommandExitCode.ValidationFailure &&
                   archiveError.ToString().Length == 0 &&
                   archiveOutput.ToString().Contains("npc-standalone-archive-unsupported",
                       StringComparison.Ordinal),
                "matching plugin-keyed BSA was not refused with its typed diagnostic");

            var compressedInput = new WorkspacePath(Path.Combine(dataRoot, "Compressed.esp"));
            File.Copy(input.Value, compressedInput.Value);
            PatchTargetFlags(compressedInput.Value, 0x0004_0000u);
            var compressedRoot = new WorkspacePath(Path.Combine(
                workspace.Value, "compressed-output"));
            var (compressedRunner, compressedOutput, compressedError) = Program.CreateRunner(
                existingNpcEditService: service);
            var compressedCode = await compressedRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", compressedInput.Value, "--input-sha256", Hash(compressedInput.Value),
                "--npc", "0x00000800", "--output-root", compressedRoot.Value,
                "--plugin", "Compressed.esp", "--output-kind", "standalone-copy",
                "--editor-id", "CompressedNpc", "--json"
            ]), CancellationToken.None);
            Assert(compressedCode == CommandExitCode.ValidationFailure &&
                   compressedError.ToString().Length == 0 &&
                   compressedOutput.ToString().Contains("npc-standalone-target-compressed",
                       StringComparison.Ordinal),
                "compressed standalone target was not refused with its typed diagnostic");

            var malformedInput = new WorkspacePath(Path.Combine(dataRoot, "Malformed.esp"));
            File.Copy(input.Value, malformedInput.Value);
            PatchTargetBounds(malformedInput.Value);
            var malformedRoot = new WorkspacePath(Path.Combine(
                workspace.Value, "malformed-output"));
            var (malformedRunner, malformedOutput, malformedError) = Program.CreateRunner(
                existingNpcEditService: service);
            var malformedCode = await malformedRunner.RunAsync(CommandLine.Parse([
                "npc", "edit-package", "--edition", "skyrimse",
                "--input-plugin", malformedInput.Value, "--input-sha256", Hash(malformedInput.Value),
                "--npc", "0x00000800", "--output-root", malformedRoot.Value,
                "--plugin", "Malformed.esp", "--output-kind", "standalone-copy",
                "--editor-id", "MalformedNpc", "--json"
            ]), CancellationToken.None);
            Assert(malformedCode == CommandExitCode.ValidationFailure &&
                   malformedError.ToString().Length == 0 &&
                   malformedOutput.ToString().Contains("npc-standalone-", StringComparison.Ordinal) &&
                   !malformedOutput.ToString().Contains("npc-override-source-read-failed",
                       StringComparison.Ordinal),
                "malformed standalone bounds were not refused with a typed diagnostic");

            await RunTask2TransportRedAsync(workspace);
            await RunTask3SelfAttestedGeometryRedAsync(workspace);
            await RunTask4ScopedDiscoveryRedAsync(workspace);
        }
        finally
        {
            DeleteDirectory(workspace.Value);
        }
    }

    private static async Task RunTask4ScopedDiscoveryRedAsync(
        WorkspacePath workspace)
    {
        var cases = new[]
        {
            (Name: "npc edit-package", Words: new[] { "npc", "edit-package" },
                Options: new[]
                {
                    "consolidate", "esl-flag", "output",
                    "edition", "game", "input-plugin", "input-sha256",
                    "expected-sha256", "npc", "output-root", "plugin", "editor-id",
                    "name", "short-name", "race", "voice", "class", "combat-style",
                    "level", "level-mult", "magicka-offset", "stamina-offset",
                    "health-offset", "calc-min", "calc-max", "speed-multiplier",
                    "disposition", "bleedout", "player-health", "player-magicka",
                    "player-stamina", "skill-values", "skill-offsets", "far-model-distance",
                    "geared-weapons", "set-flag", "clear-flag", "keywords", "add-keyword",
                    "remove-keyword", "factions", "add-faction", "update-faction",
                    "remove-faction", "inventory", "add-inventory", "update-inventory",
                    "remove-inventory", "default-outfit", "sleep-outfit", "perks", "add-perk",
                    "update-perk", "remove-perk", "actor-effects", "add-actor-effect",
                    "remove-actor-effect", "output-kind"
                }),
            (Name: "facegen build-geom-nif", Words: new[] { "facegen", "build-geom-nif" },
                Options: new[]
                {
                    "edition", "game", "asset-root", "source", "output", "mode", "morphs",
                    "source-sha256", "transport-profile", "carrier", "carrier-sha256", "shape"
                })
        };

        var required = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal)
        {
            ["npc edit-package"] =
                ["input-plugin"],
            ["facegen build-geom-nif"] = ["asset-root", "source", "output"]
        };

        var exactSyntax = new Dictionary<(string Command, string Option), string>
        {
            [("npc edit-package", "edition")] = "skyrimse",
            [("npc edit-package", "game")] = "skyrimse",
            [("npc edit-package", "input-sha256")] = "<64-hex-sha256>",
            [("npc edit-package", "expected-sha256")] = "<64-hex-sha256>",
            [("facegen build-geom-nif", "edition")] = "fallout4|skyrimse",
            [("facegen build-geom-nif", "game")] = "fallout4|skyrimse",
            [("facegen build-geom-nif", "source-sha256")] = "<64-hex-sha256>",
            [("facegen build-geom-nif", "transport-profile")] =
                "complete-carrier|geometry-into-carrier"
        };
        var expectedSkyrimFlags = new[]
        {
            "female", "essential", "ischargenfacepreset", "respawn", "autocalcstats",
            "unique", "doesntaffectstealthmeter", "skyrimusetemplate", "protected",
            "summonable", "doesnotbleed", "bleedoutoverride", "oppositegenderanims",
            "simpleactor", "skyrimloopedscript", "skyrimloopedaudio", "isghost",
            "invulnerable", "pc-level-mult"
        };
        var expectedDescriptions = new Dictionary<(string Command, string Option), string>
        {
            [("npc edit-package", "edition")] =
                "One of --edition or --game is required; canonical --edition wins when both are supplied.",
            [("npc edit-package", "game")] =
                "One of --edition or --game is required; canonical --edition wins when both are supplied.",
            [("npc edit-package", "input-sha256")] =
                "One of --input-sha256 or --expected-sha256 is required; canonical --input-sha256 wins when both are supplied.",
            [("npc edit-package", "expected-sha256")] =
                "One of --input-sha256 or --expected-sha256 is required; canonical --input-sha256 wins when both are supplied.",
            [("npc edit-package", "set-flag")] =
                "Comma-separated edition-compatible flags to set; pc-level-mult is accepted as an alias.",
            [("npc edit-package", "clear-flag")] =
                "Comma-separated edition-compatible flags to clear; pc-level-mult is accepted as an alias.",
            [("facegen build-geom-nif", "edition")] =
                "One of --edition or --game is required; canonical --edition wins when both are supplied.",
            [("facegen build-geom-nif", "game")] =
                "Alias for --edition used only when canonical --edition is absent.",
            [("facegen build-geom-nif", "mode")] =
                "Bake is the default; transport is Skyrim-only and uses hash-bound carrier rules.",
            [("facegen build-geom-nif", "morphs")] =
                "Required in bake mode; transport permits omission or only all-zero morph values.",
            [("facegen build-geom-nif", "source-sha256")] =
                "Required in transport mode to bind the source NIF.",
            [("facegen build-geom-nif", "transport-profile")] =
                "Required in transport mode.",
            [("facegen build-geom-nif", "carrier")] =
                "Required by geometry-into-carrier and forbidden by complete-carrier.",
            [("facegen build-geom-nif", "carrier-sha256")] =
                "Required by geometry-into-carrier and forbidden by complete-carrier.",
            [("facegen build-geom-nif", "shape")] =
                "Required by geometry-into-carrier and forbidden by complete-carrier."
        };

        foreach (var testCase in cases)
        {
            var (runner, output, error) = Program.CreateRunner();
            var helpArgs = testCase.Words.Append("--help").ToArray();
            var helpExit = await runner.RunAsync(
                CommandLine.Parse(helpArgs), CancellationToken.None);
            Assert(helpExit == CommandExitCode.Success &&
                   error.ToString().Length == 0 &&
                   output.ToString().Contains(testCase.Name, StringComparison.Ordinal),
                $"Task 4 RED: scoped help did not identify {testCase.Name}: " +
                output + error);
            Assert(!output.ToString().Contains(
                       "actorwright — typed Actorwright CLI",
                       StringComparison.Ordinal),
                $"Task 4 RED: {testCase.Name} still rendered the global command list.");
            Assert(output.ToString().Contains(
                       "Global options: --help, --json.",
                       StringComparison.Ordinal),
                $"Task 4 RED: {testCase.Name} help omitted the global option line.");
            foreach (var option in testCase.Options)
                Assert(output.ToString().Contains(
                           $"--{option}", StringComparison.Ordinal),
                    $"Task 4 RED: {testCase.Name} help omitted --{option}.");
            if (testCase.Name == "npc edit-package")
                Assert(output.ToString().Contains(
                           "source-mastered-override|standalone-copy",
                           StringComparison.Ordinal),
                    "Task 4 RED: edit-package help omitted output-kind values.");
            if (testCase.Name == "facegen build-geom-nif")
                Assert(output.ToString().Contains(
                           "complete-carrier|geometry-into-carrier",
                           StringComparison.Ordinal),
                    "Task 4 RED: build-geom-nif help omitted transport-profile values.");

            var schemaPath = new WorkspacePath(Path.Combine(
                workspace.Value, "task4-schema", testCase.Name.Replace(' ', '-')) + ".json");
            Directory.CreateDirectory(Path.GetDirectoryName(schemaPath.Value)!);
            var schemaPolicy = new KOnlyWorkspacePolicy(
                workspace, new WorkspacePath("F:\\ExampleGame"));
            var (schemaRunner, schemaOutput, schemaError) = Program.CreateRunner(
                schemaExportService: new SchemaExportService(schemaPolicy, workspace));
            var schemaExit = await schemaRunner.RunAsync(CommandLine.Parse([
                "schema", "export", "--command", testCase.Name,
                "--output", schemaPath.Value, "--json"
            ]), CancellationToken.None);
            Assert(schemaExit == CommandExitCode.Success &&
                   schemaError.ToString().Length == 0 &&
                   File.Exists(schemaPath.Value),
                $"Task 4 RED: schema export failed for {testCase.Name}: " +
                schemaOutput + schemaError);
            using var schema = JsonDocument.Parse(
                await File.ReadAllTextAsync(schemaPath.Value));
            var command = schema.RootElement.GetProperty("commands")
                .EnumerateArray().Single();
            Assert(command.TryGetProperty("options", out var options) &&
                   options.ValueKind == JsonValueKind.Array,
                $"Task 4 RED: schema export omitted option rows for {testCase.Name}.");
            var rows = options.EnumerateArray().ToArray();
            Assert(rows.Select(item => item.GetProperty("name").GetString())
                       .SequenceEqual(testCase.Options, StringComparer.Ordinal),
                $"Task 4 RED: schema option names drifted for {testCase.Name}.");
            var catalogRows = LegacyCommandOptionCatalog.For(testCase.Name);
            Assert(catalogRows.Select(item => item.Name)
                       .SequenceEqual(testCase.Options, StringComparer.Ordinal),
                $"Task 4 RED: catalogue option names drifted for {testCase.Name}.");
            for (var index = 0; index < rows.Length; index++)
            {
                var row = rows[index];
                var catalogOption = catalogRows[index];
                var option = row.GetProperty("name").GetString()!;
                Assert(option == catalogOption.Name &&
                       row.GetProperty("required").GetBoolean() == catalogOption.Required &&
                       catalogOption.Required == required[testCase.Name].Contains(option),
                    $"Task 4 RED: schema required flag drifted for --{option}.");
                var valueSyntax = row.GetProperty("valueSyntax").GetString();
                var description = row.GetProperty("description").GetString();
                Assert(valueSyntax == catalogOption.ValueSyntax &&
                       description == catalogOption.Description &&
                       valueSyntax is { Length: > 0 } &&
                       description is { Length: > 0 } &&
                       output.ToString().Contains(
                           $"--{option} {valueSyntax}", StringComparison.Ordinal) &&
                       output.ToString().Contains(description, StringComparison.Ordinal),
                    $"Task 4 RED: schema metadata is incomplete for --{option}.");
                if (exactSyntax.TryGetValue((testCase.Name, option), out var syntax))
                    Assert(valueSyntax == syntax,
                        $"Task 4 RED: value syntax drifted for {testCase.Name} --{option}.");
                if (expectedDescriptions.TryGetValue((testCase.Name, option),
                        out var expectedDescription))
                    Assert(description == expectedDescription,
                        $"Task 4 RED: description drifted for {testCase.Name} --{option}.");
                Assert(row.GetProperty("acceptedValues").EnumerateArray()
                           .Select(item => item.GetString())
                           .SequenceEqual(catalogOption.AcceptedValues,
                               StringComparer.Ordinal),
                    $"Task 4 RED: accepted values drifted for --{option}.");
                if (option is "set-flag" or "clear-flag")
                    Assert(catalogOption.AcceptedValues.SequenceEqual(
                               expectedSkyrimFlags, StringComparer.Ordinal),
                        $"Task 4 RED: Skyrim accepted flag values drifted for --{option}.");
            }
            var outputKind = rows.SingleOrDefault(item =>
                item.GetProperty("name").GetString() == "output-kind");
            if (outputKind.ValueKind != JsonValueKind.Undefined)
                Assert(outputKind.GetProperty("acceptedValues").EnumerateArray()
                           .Select(item => item.GetString())
                           .SequenceEqual(["source-mastered-override", "standalone-copy"],
                               StringComparer.Ordinal),
                    "Task 4 RED: output-kind accepted values were not exported.");
            var mode = rows.SingleOrDefault(item =>
                item.GetProperty("name").GetString() == "mode");
            if (mode.ValueKind != JsonValueKind.Undefined)
                Assert(mode.GetProperty("acceptedValues").EnumerateArray()
                           .Select(item => item.GetString())
                           .SequenceEqual(["bake", "transport"], StringComparer.Ordinal),
                    "Task 4 RED: mode accepted values were not exported.");
            var profile = rows.SingleOrDefault(item =>
                item.GetProperty("name").GetString() == "transport-profile");
            if (profile.ValueKind != JsonValueKind.Undefined)
                Assert(profile.GetProperty("acceptedValues").EnumerateArray()
                           .Select(item => item.GetString())
                           .SequenceEqual(["complete-carrier", "geometry-into-carrier"],
                               StringComparer.Ordinal),
                    "Task 4 RED: transport-profile accepted values were not exported.");
        }

        var unknownFaceGeomService = new RecordingFaceGeomBinaryBuildService();
        var (unknownRunner, unknownOutput, unknownError) = Program.CreateRunner(
            faceGeomBinaryBuildService: unknownFaceGeomService);
        var unknownExit = await unknownRunner.RunAsync(CommandLine.Parse([
            "facegen", "build-geom-nif", "--edition", "skyrimse",
            "--asset-root", workspace.Value, "--source", "missing.nif",
            "--output", Path.Combine(workspace.Value, "unknown-output.nif"),
            "--mode", "bake", "--morphs", "[]", "--unknown-option", "value", "--json"
        ]), CancellationToken.None);
        Assert(unknownExit == CommandExitCode.UsageError &&
               unknownFaceGeomService.Calls == 0 &&
               unknownError.ToString().Contains(
                   "facegen build-geom-nif does not support --unknown-option",
                   StringComparison.Ordinal),
            "Task 4 RED: unknown FaceGeom option was not refused before parsing/service dispatch: " +
            unknownOutput + unknownError);

        var transportHash = new string('0', 64);
        var transportCases = new[]
        {
            (Name: "missing source hash", Expected: "requires --source-sha256 and --transport-profile",
                Args: new[] { "--edition", "skyrimse", "--mode", "transport",
                    "--transport-profile", "complete-carrier" }),
            (Name: "missing transport profile", Expected: "requires --source-sha256 and --transport-profile",
                Args: new[] { "--edition", "skyrimse", "--mode", "transport",
                    "--source-sha256", transportHash }),
            (Name: "geometry carrier tuple", Expected: "geometry-into-carrier requires --carrier, --carrier-sha256, and --shape",
                Args: new[] { "--edition", "skyrimse", "--mode", "transport",
                    "--source-sha256", transportHash, "--transport-profile", "geometry-into-carrier" }),
            (Name: "complete carrier tuple", Expected: "complete-carrier forbids --carrier, --carrier-sha256, and --shape",
                Args: new[] { "--edition", "skyrimse", "--mode", "transport",
                    "--source-sha256", transportHash, "--transport-profile", "complete-carrier",
                    "--carrier", "carrier.nif", "--carrier-sha256", transportHash, "--shape", "Head" }),
            (Name: "transport morphs", Expected: "permits --morphs only when every value is zero",
                Args: new[] { "--edition", "skyrimse", "--mode", "transport",
                    "--source-sha256", transportHash, "--transport-profile", "complete-carrier",
                    "--morphs", "[{\"name\":\"JawOpen\",\"value\":0.1}]" }),
            (Name: "transport edition", Expected: "transport supports only --edition skyrimse",
                Args: new[] { "--edition", "fallout4", "--mode", "transport",
                    "--source-sha256", transportHash, "--transport-profile", "complete-carrier" }),
            (Name: "bake transport options", Expected: "transport-only options require --mode transport",
                Args: new[] { "--edition", "skyrimse", "--mode", "bake", "--morphs", "[]",
                    "--source-sha256", transportHash })
        };
        foreach (var transportCase in transportCases)
        {
            var transportService = new RecordingFaceGeomBinaryBuildService();
            var (transportRunner, transportOutput, transportError) = Program.CreateRunner(
                faceGeomBinaryBuildService: transportService);
            var transportArgs = new[]
            {
                "facegen", "build-geom-nif", "--asset-root", workspace.Value,
                "--source", "missing.nif", "--output",
                Path.Combine(workspace.Value, $"{transportCase.Name.Replace(' ', '-')}.nif")
            }.Concat(transportCase.Args).Append("--json").ToArray();
            var transportExit = await transportRunner.RunAsync(
                CommandLine.Parse(transportArgs), CancellationToken.None);
            Assert(transportExit == CommandExitCode.UsageError &&
                   transportService.Calls == 0 &&
                   transportError.ToString().Contains(transportCase.Expected,
                       StringComparison.Ordinal),
                $"Task 4 RED: {transportCase.Name} transport invariant was not enforced: " +
                transportOutput + transportError);
        }

        var capabilities = await ProtocolV2TestHost.RunAsync([
            "capabilities", "--protocol", "2", "--json"
        ]);
        using var capabilitiesDocument = JsonDocument.Parse(capabilities.StandardOutput);
        var capabilityRows = capabilitiesDocument.RootElement
            .GetProperty("result").GetProperty("commands");
        foreach (var command in cases)
        {
            var readiness = capabilityRows.EnumerateArray().Single(item =>
                item.GetProperty("name").GetString() == command.Name)
                .GetProperty("readiness").GetString();
            Assert(readiness == "legacy",
                $"Task 4 RED: protocol-2 discovery promoted {command.Name} to {readiness}.");
        }
    }

    private sealed class RecordingFaceGeomBinaryBuildService :
        IFaceGeomBinaryBuildService
    {
        public int Calls { get; private set; }

        public ValueTask<FaceGeomBinaryBuildResult> BuildAsync(
            FaceGeomBinaryBuildRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return ValueTask.FromResult(new FaceGeomBinaryBuildResult(
                false, null, null, []));
        }
    }

    private static async Task RunTask2TransportRedAsync(WorkspacePath workspace)
    {
        var source = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "source.nif"));
        var carrier = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "carrier.nif"));
        var outputDirectory = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "outputs"));
        Directory.CreateDirectory(outputDirectory.Value);
        string fixture = SyntheticFaceGeomHairRegionsFixture.Load(
            ActorwrightWorkspace.ResolveRoot().Value).Path;
        File.Copy(fixture, source.Value);
        File.Copy(fixture, carrier.Value);

        var policy = new KOnlyWorkspacePolicy(workspace,
            new WorkspacePath("F:\\ExampleGame"));
        var reader = new BethesdaNifGeometryReadbackService();
        var exporter = new BlenderFaceGeomNifExporter(
            new WorkspacePath(Path.Combine(workspace.Value, "missing-blender.exe")),
            new WorkspacePath(Path.Combine(workspace.Value, "missing-profile")),
            "export_facegeom_nif", policy, workspace,
            new Sha256Hash(new string('A', 64)), reader);
        var initialRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, source), CancellationToken.None);
        Assert(initialRead.Accepted && initialRead.Document is { Shapes.Length: > 0 },
            "Task 2 RED: checked-in finished-head fixture could not be independently read back: " +
            string.Join(", ", initialRead.Diagnostics.Select(item => item.Code)));

        var falloutFixture = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "fallout4-trishape.nif"));
        byte[] falloutFixtureBytes = BuildFallout4TriShapeFixture();
        File.WriteAllBytes(falloutFixture.Value, falloutFixtureBytes);
        var falloutRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.Fallout4, falloutFixture), CancellationToken.None);
        Assert(falloutRead.Accepted && falloutRead.Document is { Shapes.Length: 1 } &&
               falloutRead.Document.Shapes[0].BlockType == "BSTriShape" &&
               falloutRead.Document.Shapes[0].VertexCount == 3,
            "Task 2 RED: admitted Fallout 4 BSTriShape layout could not be read back: " +
            string.Join(", ", falloutRead.Diagnostics.Select(item => item.Code)));

        var staticSseFixture = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "skyrimse-static-trishape.nif"));
        File.WriteAllBytes(staticSseFixture.Value, BuildSkyrimStaticTriShapeFixture());
        var staticSseRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, staticSseFixture), CancellationToken.None);
        Assert(staticSseRead.Accepted && staticSseRead.Document is { Shapes.Length: 1 } &&
               staticSseRead.Document.Shapes[0].BlockType == "BSTriShape" &&
               staticSseRead.Document.Shapes[0].VertexCount == 3,
            "Task 2 RED: static Skyrim SE BSTriShape readback was not admitted: " +
            string.Join(", ", staticSseRead.Diagnostics.Select(item => item.Code)));

        var malformedFixture = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "malformed.nif"));
        File.WriteAllBytes(malformedFixture.Value, [0x42, 0x53, 0x44]);
        var malformedRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.Fallout4, malformedFixture), CancellationToken.None);
        Assert(!malformedRead.Accepted && malformedRead.Diagnostics.Any(item =>
                item.Code == "nif-geometry-readback-malformed"),
            "Task 2 RED: malformed NIF was not refused with a typed readback diagnostic");

        var ambiguousBytes = falloutFixtureBytes.ToArray();
        int descriptorOffset = checked((int)falloutRead.Document!.Shapes[0].VertexPayloadOffset -
            sizeof(ulong));
        BinaryPrimitives.WriteUInt64LittleEndian(ambiguousBytes.AsSpan(descriptorOffset),
            (0x401UL << 44) | 8UL);
        var ambiguousFixture = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "ambiguous-descriptor.nif"));
        File.WriteAllBytes(ambiguousFixture.Value, ambiguousBytes);
        var ambiguousRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.Fallout4, ambiguousFixture), CancellationToken.None);
        Assert(!ambiguousRead.Accepted && ambiguousRead.Diagnostics.Any(item =>
                item.Code == "nif-geometry-readback-malformed"),
            "Task 2 RED: inconsistent Bethesda vertex descriptor was not refused");

        var unknownFixture = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "unknown-block.nif"));
        File.WriteAllBytes(unknownFixture.Value, BuildFallout4TriShapeFixture(
            includeUnknownBlock: true));
        var unknownRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.Fallout4, unknownFixture), CancellationToken.None);
        Assert(!unknownRead.Accepted && unknownRead.Diagnostics.Any(item =>
                item.Code == "nif-geometry-readback-malformed"),
            "Task 2 RED: unknown Fallout 4 block type was silently admitted");

        var router = new FaceGeomBinaryBuildRouter(exporter, reader, policy, workspace);

        var overlapFixture = new WorkspacePath(Path.Combine(workspace.Value,
            "task2-transport", "overlapping-static-shapes.nif"));
        byte[] overlapBytes = BuildOverlappingSkyrimStaticTriShapeFixture(
            out int overlapBlockSizeTableOffset, out int overlapFirstShapeBlockSize);
        File.WriteAllBytes(overlapFixture.Value, overlapBytes);
        var admittedOverlapRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, overlapFixture), CancellationToken.None);
        Assert(admittedOverlapRead.Accepted && admittedOverlapRead.Document is
                   { Shapes.Length: 2 },
            "Task 2 RED: the two-shape overlap baseline was not admitted before mutation: " +
            string.Join(", ", admittedOverlapRead.Diagnostics.Select(item => item.Code)));
        SseNifDocument admittedOverlapDocument = SseFaceGeomCarrierCodec.Parse(overlapBytes);
        SseNifBlock firstOverlapShape = admittedOverlapDocument.Blocks.Single(item =>
            item.Type == "BSTriShape" && item.Index == 1);
        NifGeometryShapeReadback firstOverlapReadback = admittedOverlapRead.Document!.Shapes
            .Single(item => item.BlockIndex == firstOverlapShape.Index);
        const int overlapDelta = 10;
        int firstPayloadEnd = checked((int)firstOverlapReadback.VertexPayloadOffset +
            firstOverlapReadback.VertexPayloadLength);
        int shortenedFirstShapeEnd = checked(firstOverlapShape.Offset +
            overlapFirstShapeBlockSize - overlapDelta);
        Assert(shortenedFirstShapeEnd > firstOverlapReadback.VertexPayloadOffset &&
               shortenedFirstShapeEnd < firstPayloadEnd,
            "Task 2 RED: overlap fixture did not move the next admitted shape into the first vertex payload");
        BinaryPrimitives.WriteUInt32LittleEndian(
            overlapBytes.AsSpan(overlapBlockSizeTableOffset + firstOverlapShape.Index * sizeof(uint)),
            checked((uint)(overlapFirstShapeBlockSize - overlapDelta)));
        File.WriteAllBytes(overlapFixture.Value, overlapBytes);
        var overlapRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, overlapFixture), CancellationToken.None);
        Assert(!overlapRead.Accepted && overlapRead.Diagnostics.Any(item =>
                item.Code == "nif-geometry-readback-malformed"),
            "Task 2 RED: overlapping admitted vertex payload layout was accepted by readback: " +
            string.Join(", ", overlapRead.Diagnostics.Select(item => item.Code)));
        var overlapOutput = new WorkspacePath(Path.Combine(outputDirectory.Value,
            "overlapping-static-shapes-output.nif"));
        var overlapTransport = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition, workspace, overlapFixture, overlapOutput, [],
            FaceGeomBinaryOperation.Transport, FaceGeomTransportProfile.CompleteCarrier,
            new Sha256Hash(Hash(overlapFixture.Value))), CancellationToken.None);
        Assert(!overlapTransport.Written && overlapTransport.Diagnostics.Any(item =>
                item.Code == "nif-geometry-readback-malformed") &&
               !File.Exists(overlapOutput.Value),
            "Task 2 RED: overlapping geometry layout was promoted or left output bytes: " +
            string.Join(", ", overlapTransport.Diagnostics.Select(item => item.Code)));

        var sourceHash = new Sha256Hash(Hash(source.Value));
        var completeOutput = new WorkspacePath(Path.Combine(outputDirectory.Value,
            "complete.nif"));
        var empty = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            workspace,
            source,
            completeOutput,
            [], FaceGeomBinaryOperation.Transport, FaceGeomTransportProfile.CompleteCarrier,
            sourceHash), CancellationToken.None);
        Assert(empty.Written && empty.Artifact is { ImportMode: "nif-transport-complete-carrier",
                RuntimeAuthority: false, TriFiles.IsDefaultOrEmpty: true },
            "Task 2 RED: empty finished-head transport was refused or lacked complete-carrier evidence: " +
            string.Join(", ", empty.Diagnostics.Select(item => item.Code)));
        Assert(File.ReadAllBytes(source.Value).AsSpan().SequenceEqual(
                File.ReadAllBytes(completeOutput.Value)),
            "Task 2 RED: complete-carrier transport did not preserve source bytes exactly");

        var (cliRunner, cliOutputText, cliErrorText) = Program.CreateRunner(
            faceGeomBinaryBuildService: router);
        var cliOutput = new WorkspacePath(Path.Combine(outputDirectory.Value, "cli-complete.nif"));
        var cliExit = await cliRunner.RunAsync(CommandLine.Parse([
            "facegen", "build-geom-nif", "--edition", "skyrimse",
            "--asset-root", workspace.Value, "--source", source.Value,
            "--source-sha256", sourceHash.Value, "--output", cliOutput.Value,
            "--mode", "transport", "--transport-profile", "complete-carrier", "--json"
        ]), CancellationToken.None);
        Assert(cliExit == CommandExitCode.Success && File.Exists(cliOutput.Value) &&
               File.ReadAllBytes(cliOutput.Value).AsSpan().SequenceEqual(
                   File.ReadAllBytes(source.Value)),
            "Task 2 RED: real CLI transport option parsing did not route complete-carrier: " +
            cliOutputText + cliErrorText);

        var (conflictRunner, _, conflictError) = Program.CreateRunner(
            faceGeomBinaryBuildService: router);
        var conflictOutput = new WorkspacePath(Path.Combine(outputDirectory.Value, "bake-conflict.nif"));
        var conflictExit = await conflictRunner.RunAsync(CommandLine.Parse([
            "facegen", "build-geom-nif", "--edition", "skyrimse",
            "--asset-root", workspace.Value, "--source", source.Value,
            "--source-sha256", sourceHash.Value, "--output", conflictOutput.Value,
            "--mode", "bake", "--morphs", "[{\"name\":\"JawOpen\",\"value\":1}]",
            "--transport-profile", "complete-carrier", "--carrier", carrier.Value,
            "--carrier-sha256", new Sha256Hash(Hash(carrier.Value)).Value,
            "--shape", "Head", "--json"
        ]), CancellationToken.None);
        Assert(conflictExit == CommandExitCode.UsageError &&
               conflictError.ToString().Contains("transport-only", StringComparison.OrdinalIgnoreCase) &&
               !File.Exists(conflictOutput.Value),
            "Task 2 RED: Bake silently accepted transport-only CLI options");

        var zeroOutput = new WorkspacePath(Path.Combine(outputDirectory.Value, "zero.nif"));
        var zero = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition,
            workspace,
            source,
            zeroOutput,
            [new FaceGeomBinaryMorph("JawOpen", 0f)], FaceGeomBinaryOperation.Transport,
            FaceGeomTransportProfile.CompleteCarrier, sourceHash), CancellationToken.None);
        Assert(zero.Written,
            "Task 2 RED: all-zero finished-head transport was refused: " +
            string.Join(", ", zero.Diagnostics.Select(item => item.Code)));

        var overwrite = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition, workspace, source, completeOutput, [],
            FaceGeomBinaryOperation.Transport, FaceGeomTransportProfile.CompleteCarrier,
            sourceHash), CancellationToken.None);
        Assert(!overwrite.Written && overwrite.Diagnostics.Any(item =>
                item.Code == "facegeom-binary-output-exists"),
            "Task 2 RED: complete-carrier transport overwrote an existing output");

        byte[] sourceBytes = File.ReadAllBytes(source.Value);
        NifGeometryShapeReadback sourceShape = initialRead.Document!.Shapes[0];
        int sourcePayloadOffset = checked((int)sourceShape.VertexPayloadOffset);
        sourceBytes[sourcePayloadOffset] ^= 0x01;
        File.WriteAllBytes(source.Value, sourceBytes);
        var mutatedSourceHash = new Sha256Hash(Hash(source.Value));
        var mutatedRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, source, mutatedSourceHash), CancellationToken.None);
        var carrierRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, carrier,
            new Sha256Hash(Hash(carrier.Value))), CancellationToken.None);
        Assert(mutatedRead.Accepted && carrierRead.Accepted &&
               mutatedRead.Document is not null && carrierRead.Document is not null,
            "Task 2 RED: mutated source or carrier failed independent readback");
        var mutatedSourceShape = mutatedRead.Document!.Shapes.Single(shape =>
            shape.Name == sourceShape.Name);
        var carrierShape = carrierRead.Document!.Shapes.Single(shape =>
            shape.Name == sourceShape.Name);
        var geometryOutput = new WorkspacePath(Path.Combine(outputDirectory.Value,
            "geometry.nif"));
        var geometry = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition, workspace, source, geometryOutput, [],
            FaceGeomBinaryOperation.Transport, FaceGeomTransportProfile.GeometryIntoCarrier,
            mutatedSourceHash, carrier, new Sha256Hash(Hash(carrier.Value)), sourceShape.Name),
            CancellationToken.None);
        Assert(geometry.Written && geometry.Artifact is { ImportMode: "nif-transport-vertex-array",
                RuntimeAuthority: false, CarrierVertexSha256: not null },
            "Task 2 RED: geometry-into-carrier transport was refused or lacked carrier evidence: " +
            string.Join(", ", geometry.Diagnostics.Select(item => item.Code)));
        var outputRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, geometryOutput,
            new Sha256Hash(Hash(geometryOutput.Value))), CancellationToken.None);
        Assert(outputRead.Accepted && outputRead.Document is not null,
            "Task 2 RED: geometry-into-carrier output failed independent readback: " +
            string.Join(", ", outputRead.Diagnostics.Select(item => item.Code)));
        var outputShape = outputRead.Document!.Shapes.Single(shape =>
            shape.Name == sourceShape.Name);
        var carrierBytes = File.ReadAllBytes(carrier.Value);
        var outputBytes = File.ReadAllBytes(geometryOutput.Value);
        int carrierPayloadOffset = checked((int)carrierShape.VertexPayloadOffset);
        int carrierPayloadEnd = checked(carrierPayloadOffset + carrierShape.VertexPayloadLength);
        Assert(outputShape.VertexPayloadSha256 == mutatedSourceShape.VertexPayloadSha256,
            "Task 2 RED: geometry-into-carrier output did not expose changed source geometry");
        for (int index = 0; index < outputBytes.Length; index++)
            if (index < carrierPayloadOffset || index >= carrierPayloadEnd)
                Assert(outputBytes[index] == carrierBytes[index],
                    "Task 2 RED: geometry-into-carrier changed bytes outside the selected payload");

        var mutatingReader = new MutatingReadbackService(reader,
            File.ReadAllBytes(carrier.Value));
        var mutatingRouter = new FaceGeomBinaryBuildRouter(exporter,
            mutatingReader, policy, workspace);
        var exactTempOutput = new WorkspacePath(Path.Combine(outputDirectory.Value,
            "geometry-exact-temp.nif"));
        var exactTemp = await mutatingRouter.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition, workspace, source, exactTempOutput, [],
            FaceGeomBinaryOperation.Transport, FaceGeomTransportProfile.GeometryIntoCarrier,
            mutatedSourceHash, carrier, new Sha256Hash(Hash(carrier.Value)), sourceShape.Name),
            CancellationToken.None);
        Assert(!exactTemp.Written && mutatingReader.Mutated &&
               mutatingReader.OutsideCarrierByteDiffers && exactTemp.Diagnostics.Any(item =>
                item.Code == "facegeom-binary-transport-authorized-range") &&
               !File.Exists(exactTempOutput.Value),
            "Task 2 RED: geometry proof trusted pre-write bytes after temp mutation; " +
            $"mutated={mutatingReader.Mutated}; diagnostics=" +
            string.Join(",", exactTemp.Diagnostics.Select(item => item.Code)) +
            $"; mutationOffset={mutatingReader.MutationOffset}; carrierOffset={carrierShape.VertexPayloadOffset}; " +
            $"carrierLength={carrierShape.VertexPayloadLength}; outsideDiffers={mutatingReader.OutsideCarrierByteDiffers}");

        byte[] topologyCarrierBytes = File.ReadAllBytes(carrier.Value);
        SseNifDocument topologyCarrierDocument = SseFaceGeomCarrierCodec.Parse(topologyCarrierBytes);
        SseNifBlock topologyShapeBlock = topologyCarrierDocument.Blocks.Single(item =>
            item.Index == carrierShape.BlockIndex);
        int skinIndex = topologyShapeBlock.References.First(reference =>
            reference.Kind == "skin").Target;
        int topologyIndex = topologyCarrierDocument.Blocks[skinIndex].References
            .First(reference => reference.Kind == "skinpartition").Target;
        SseNifBlock topologyBlock = topologyCarrierDocument.Blocks[topologyIndex];
        topologyCarrierBytes[topologyBlock.Offset] ^= 0x01;
        var topologyCarrier = new WorkspacePath(Path.Combine(outputDirectory.Value,
            "topology-mismatch-carrier.nif"));
        File.WriteAllBytes(topologyCarrier.Value, topologyCarrierBytes);
        var topologyRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, topologyCarrier,
            new Sha256Hash(Hash(topologyCarrier.Value))), CancellationToken.None);
        Assert(topologyRead.Accepted,
            "Task 2 RED: topology-mismatch carrier could not be independently read back");
        var topologyOutput = new WorkspacePath(Path.Combine(outputDirectory.Value,
            "topology-mismatch.nif"));
        var topologyMismatch = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition, workspace, source, topologyOutput, [],
            FaceGeomBinaryOperation.Transport, FaceGeomTransportProfile.GeometryIntoCarrier,
            mutatedSourceHash, topologyCarrier,
            new Sha256Hash(Hash(topologyCarrier.Value)), sourceShape.Name), CancellationToken.None);
        Assert(!topologyMismatch.Written && topologyMismatch.Diagnostics.Any(item =>
                item.Code == "facegeom-binary-transport-geometry-mismatch") &&
               !File.Exists(topologyOutput.Value),
            "Task 2 RED: topology mismatch was accepted or left output bytes");

        var hashMismatchOutput = new WorkspacePath(Path.Combine(outputDirectory.Value,
            "hash-mismatch.nif"));
        var hashMismatch = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition, workspace, source, hashMismatchOutput, [],
            FaceGeomBinaryOperation.Transport, FaceGeomTransportProfile.CompleteCarrier,
            new Sha256Hash(new string('0', 64))), CancellationToken.None);
        Assert(!hashMismatch.Written && hashMismatch.Diagnostics.Any(item =>
                item.Code == "facegeom-binary-transport-source-hash-mismatch") &&
               !File.Exists(hashMismatchOutput.Value),
            "Task 2 RED: source hash mismatch was not refused without output");

        var nonZero = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition, workspace, source,
            new WorkspacePath(Path.Combine(outputDirectory.Value, "nonzero.nif")),
            [new FaceGeomBinaryMorph("JawOpen", 1f)], FaceGeomBinaryOperation.Transport,
            FaceGeomTransportProfile.CompleteCarrier, mutatedSourceHash),
            CancellationToken.None);
        Assert(!nonZero.Written && nonZero.Diagnostics.Any(item =>
                item.Code == "facegeom-binary-transport-morph-nonzero"),
            "Task 2 RED: non-zero transport morph was accepted");
        Assert(!File.Exists(Path.Combine(outputDirectory.Value, "nonzero.nif")),
            "Task 2 RED: non-zero transport refusal left an output file");

        var fallout = await router.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.Fallout4, workspace, source,
            new WorkspacePath(Path.Combine(outputDirectory.Value, "fallout4.nif")), [],
            FaceGeomBinaryOperation.Transport, FaceGeomTransportProfile.CompleteCarrier,
            mutatedSourceHash), CancellationToken.None);
        Assert(!fallout.Written && fallout.Diagnostics.Any(item =>
                item.Code == "facegeom-binary-transport-edition"),
            "Task 2 RED: Fallout 4 transport was not refused with a typed edition diagnostic");
        Assert(!File.Exists(Path.Combine(outputDirectory.Value, "fallout4.nif")),
            "Task 2 RED: Fallout 4 transport refusal left an output file");
    }

    private static async Task RunTask3SelfAttestedGeometryRedAsync(
        WorkspacePath workspace)
    {
        var root = new WorkspacePath(Path.Combine(workspace.Value, "task3-writer"));
        Directory.CreateDirectory(root.Value);
        var profile = new WorkspacePath(Path.Combine(root.Value, "profile"));
        Directory.CreateDirectory(profile.Value);
        var executable = new WorkspacePath(Path.Combine(root.Value, "writer.exe"));
        File.WriteAllBytes(executable.Value, [0x21, 0x22, 0x23]);
        var executableHash = new Sha256Hash(Hash(executable.Value));
        var source = new WorkspacePath(Path.Combine(root.Value, "source.nif"));
        var tri = new WorkspacePath(Path.Combine(root.Value, "source.tri"));
        var output = new WorkspacePath(Path.Combine(root.Value, "facegeom-output.nif"));
        var previewOutput = new WorkspacePath(Path.Combine(root.Value, "preview-output.nif"));
        var scene = new WorkspacePath(Path.Combine(root.Value, "scene.json"));
        byte[] sourceBytes = BuildSkyrimStaticTriShapeFixture();
        File.WriteAllBytes(source.Value, sourceBytes);
        File.WriteAllBytes(tri.Value, [0x31, 0x32, 0x33, 0x34]);

        var reader = new BethesdaNifGeometryReadbackService();
        var sourceRead = await reader.ReadAsync(new NifGeometryReadbackRequest(
            GameEdition.SkyrimSpecialEdition, source), CancellationToken.None);
        Assert(sourceRead.Accepted && sourceRead.Document is { Shapes.Length: 1 },
            "Task 3 RED: controlled source fixture was not independently readable: " +
            string.Join(", ", sourceRead.Diagnostics.Select(item => item.Code)));

        byte[] forgedOutputBytes = sourceBytes.ToArray();
        int outputVertexOffset = checked((int)sourceRead.Document!.Shapes[0].VertexPayloadOffset);
        forgedOutputBytes[outputVertexOffset] ^= 0x01;
        var forgedOutputFixture = new WorkspacePath(Path.Combine(root.Value,
            "forged-output-fixture.nif"));
        File.WriteAllBytes(forgedOutputFixture.Value, forgedOutputBytes);

        var policy = new KOnlyWorkspacePolicy(workspace,
            new WorkspacePath("F:\\ExampleGame"));
        var faceWriter = (string requestPath, string statusPath,
            CancellationToken cancellationToken) => WriteForgedFaceGeomStatusAsync(
                requestPath, statusPath, forgedOutputFixture, sourceRead.Document!,
                cancellationToken);
        var faceExporter = new BlenderFaceGeomNifExporter(
            executable, profile, "export_facegeom_nif", policy, workspace,
            executableHash, reader, faceWriter);
        var faceResult = await faceExporter.BuildAsync(new FaceGeomBinaryBuildRequest(
            GameEdition.SkyrimSpecialEdition, workspace, source, output,
            [new FaceGeomBinaryMorph("JawOpen", 1f)]), CancellationToken.None);
        Assert(!faceResult.Written && faceResult.Diagnostics.Any(item =>
                item.Code == "facegeom-binary-output-geometry-mismatch") &&
               !File.Exists(output.Value),
            "Task 3 RED: forged FaceGeom geometry status was accepted or left output bytes: " +
            string.Join(", ", faceResult.Diagnostics.Select(item => item.Code)));

        await File.WriteAllTextAsync(scene.Value, JsonSerializer.Serialize(new
        {
            schemaVersion = "1",
            artifactKind = "preview-scene-semantic-build",
            edition = "skyrimse",
            npcFormId = "0x00000800",
            inputManifestSha256 = new string('1', 64),
            sceneSha256 = new string('2', 64),
            assetCount = 1,
            includedAssetCount = 1,
            visibleAssetCount = 1,
            categoryCounts = new[] { new { category = "face", count = 1 } },
            assets = new[] { new
            {
                category = "face",
                path = "source.nif",
                provider = "fixture",
                sha256 = Hash(source.Value),
                visible = true,
                included = true
            } },
            appliedMorphCount = 1,
            morphs = new[] { new
            {
                category = "vertex",
                name = "JawOpen",
                value = 1f,
                applied = true,
                included = true
            } }
        }));

        var previewWriter = (string requestPath, string statusPath,
            CancellationToken cancellationToken) => WriteForgedPreviewStatusAsync(
                requestPath, statusPath, forgedOutputFixture, sourceRead.Document!,
                cancellationToken);
        var previewExporter = new BlenderPreviewNifExporter(
            executable, profile, "export_preview_nif", policy, workspace,
            executableHash, reader, previewWriter);
        var previewResult = await previewExporter.ExportAsync(
            new PreviewNifBinaryExportRequest(
                GameEdition.SkyrimSpecialEdition, scene, root, previewOutput),
            CancellationToken.None);
            Assert(!previewResult.Written && previewResult.Diagnostics.Any(item =>
                item.Code == "preview-nif-binary-output-geometry-mismatch") &&
               !File.Exists(previewOutput.Value),
            "Task 3 RED: forged preview geometry status was accepted or left output bytes: " +
            string.Join(", ", previewResult.Diagnostics.Select(item => item.Code)));
    }

    private static async ValueTask<ImmutableArray<Diagnostic>>
        WriteForgedFaceGeomStatusAsync(
            string requestPath,
            string statusPath,
            WorkspacePath outputFixture,
            NifGeometryReadbackDocument sourceGeometry,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = JsonDocument.Parse(
            await File.ReadAllBytesAsync(requestPath, cancellationToken));
        var root = request.RootElement.GetProperty("root").GetString()!;
        var output = request.RootElement.GetProperty("output").GetString()!;
        var source = Path.GetFullPath(Path.Combine(
            root,
            request.RootElement.GetProperty("source").GetString()!));
        var triFiles = request.RootElement.GetProperty("triFiles").EnumerateArray()
            .Select(relative =>
            {
                var path = Path.GetFullPath(Path.Combine(root, relative.GetString()!));
                return new { path, sha256 = Hash(path) };
            }).ToArray();
        File.Copy(outputFixture.Value, output, overwrite: true);
        var status = new
        {
            exported = true,
            output,
            sha256 = Hash(output),
            bytes = new FileInfo(output).Length,
            targetGame = "SKYRIMSE",
            vertexCount = sourceGeometry.Shapes.Sum(item => item.VertexCount),
            source = new { path = source, sha256 = Hash(source) },
            triFiles,
            morphs = request.RootElement.GetProperty("morphs"),
            baseVertexSha256 = sourceGeometry.AggregateGeometrySha256.Value,
            bakedVertexSha256 = new string('F', 64),
            importMode = "nif-plus-tri-bake",
            error = (string?)null
        };
        await File.WriteAllTextAsync(statusPath, JsonSerializer.Serialize(status),
            cancellationToken);
        return [];
    }

    private static async ValueTask<ImmutableArray<Diagnostic>>
        WriteForgedPreviewStatusAsync(
            string requestPath,
            string statusPath,
            WorkspacePath outputFixture,
            NifGeometryReadbackDocument sourceGeometry,
            CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var request = JsonDocument.Parse(
            await File.ReadAllBytesAsync(requestPath, cancellationToken));
        var root = request.RootElement.GetProperty("root").GetString()!;
        var output = request.RootElement.GetProperty("output").GetString()!;
        var assets = request.RootElement.GetProperty("assets").EnumerateArray()
            .Select(asset =>
            {
                var path = Path.GetFullPath(Path.Combine(
                    root, asset.GetProperty("path").GetString()!));
                return new
                {
                    path,
                    sha256 = Hash(path).ToLowerInvariant(),
                    triFiles = new[] { new { path = Path.ChangeExtension(path, ".tri"),
                        sha256 = Hash(Path.ChangeExtension(path, ".tri")).ToLowerInvariant() } }
                };
            }).ToArray();
        var triFiles = assets.SelectMany(asset => asset.triFiles).ToArray();
        File.Copy(outputFixture.Value, output, overwrite: true);
        var status = new
        {
            exported = true,
            output,
            sha256 = Hash(output),
            bytes = new FileInfo(output).Length,
            targetGame = "SKYRIMSE",
            meshCount = 1,
            vertexCount = sourceGeometry.Shapes.Sum(item => item.VertexCount),
            error = (string?)null,
            importMode = "mesh-plus-tri-bake",
            morphs = request.RootElement.GetProperty("morphs"),
            morphDeformed = true,
            baseVertexSha256 = sourceGeometry.AggregateGeometrySha256.Value,
            bakedVertexSha256 = new string('F', 64),
            triFiles,
            armatureCount = 0,
            deformationMode = "nif-mesh-evaluated",
            hairZapApplied = false,
            hairZapTop = false,
            hairZapLong = false,
            hairZapAffectedMeshCount = 0,
            hairZapRemovedFaceCount = 0,
            faceCullApplied = false,
            faceCullAffectedMeshCount = 0
        };
        await File.WriteAllTextAsync(statusPath, JsonSerializer.Serialize(status),
            cancellationToken);
        return [];
    }

    internal static byte[] BuildFallout4TriShapeFixture(bool includeUnknownBlock = false, bool halfPrecision = false)
    {
        var strings = new[] { "Root", "Head" };
        byte[] node;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(-1);
            writer.Write(new byte[56]);
            writer.Write(-1);
            writer.Write(1U);
            writer.Write(1);
            writer.Write(0U);
            writer.Flush();
            node = stream.ToArray();
        }

        ulong descriptor = halfPrecision ? (0x001UL << 44) | 2UL : (0x401UL << 44) | 4UL;
        byte[] shape;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(1U);
            writer.Write(0U);
            writer.Write(-1);
            writer.Write(new byte[56]);
            writer.Write(-1);
            writer.Write(new byte[16]);
            writer.Write(-1);
            writer.Write(-1);
            writer.Write(-1);
            writer.Write(descriptor);
            writer.Write(1U);
            writer.Write((ushort)3);
            writer.Write(halfPrecision ? 30U : 54U);
            for (int vertex = 0; vertex < 3; vertex++)
            {
                if (halfPrecision)
                {
                    writer.Write(BitConverter.HalfToUInt16Bits((Half)vertex));
                    writer.Write(BitConverter.HalfToUInt16Bits((Half)(vertex + 1)));
                    writer.Write(BitConverter.HalfToUInt16Bits((Half)(vertex + 2)));
                    writer.Write((ushort)0);
                }
                else
                {
                    writer.Write((float)vertex);
                    writer.Write((float)(vertex + 1));
                    writer.Write((float)(vertex + 2));
                    writer.Write(0U);
                }
            }
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            writer.Flush();
            shape = stream.ToArray();
        }

        using var result = new MemoryStream();
        using var output = new BinaryWriter(result, Encoding.ASCII, leaveOpen: true);
        output.Write(Encoding.ASCII.GetBytes(
            "Gamebryo File Format, Version 20.2.0.7\n"));
        output.Write(0x14020007U);
        output.Write((byte)1);
        output.Write(12U);
        output.Write(includeUnknownBlock ? 3U : 2U);
        output.Write(130U);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((ushort)(includeUnknownBlock ? 3 : 2));
        WriteSizedAscii(output, "NiNode");
        WriteSizedAscii(output, "BSTriShape");
        if (includeUnknownBlock)
            WriteSizedAscii(output, "UnknownFallout4Block");
        output.Write((ushort)0);
        output.Write((ushort)1);
        if (includeUnknownBlock)
            output.Write((ushort)2);
        output.Write((uint)node.Length);
        output.Write((uint)shape.Length);
        if (includeUnknownBlock)
            output.Write(1U);
        output.Write((uint)strings.Length);
        output.Write((uint)strings.Max(item => item.Length));
        foreach (string value in strings)
            WriteSizedAscii(output, value);
        output.Write(0U);
        output.Write(node);
        output.Write(shape);
        if (includeUnknownBlock)
            output.Write((byte)0xA5);
        output.Write(1U);
        output.Write(0);
        output.Flush();
        return result.ToArray();
    }

    private static byte[] BuildSkyrimStaticTriShapeFixture()
    {
        var strings = new[] { "Root", "Head" };
        byte[] node;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(-1);
            writer.Write(new byte[56]);
            writer.Write(-1);
            writer.Write(1U);
            writer.Write(1);
            writer.Write(0U);
            writer.Flush();
            node = stream.ToArray();
        }

        const ulong descriptor = (0x401UL << 44) | 4UL;
        byte[] shape;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(1U);
            writer.Write(0U);
            writer.Write(-1);
            writer.Write(new byte[56]);
            writer.Write(-1);
            writer.Write(new byte[16]);
            writer.Write(-1);
            writer.Write(-1);
            writer.Write(-1);
            writer.Write(descriptor);
            writer.Write((ushort)1);
            writer.Write((ushort)3);
            writer.Write(54U);
            for (int vertex = 0; vertex < 3; vertex++)
            {
                writer.Write((float)vertex);
                writer.Write((float)(vertex + 1));
                writer.Write((float)(vertex + 2));
                writer.Write(0U);
            }
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            writer.Flush();
            shape = stream.ToArray();
        }

        using var result = new MemoryStream();
        using var output = new BinaryWriter(result, Encoding.ASCII, leaveOpen: true);
        output.Write(Encoding.ASCII.GetBytes(
            "Gamebryo File Format, Version 20.2.0.7\n"));
        output.Write(0x14020007U);
        output.Write((byte)1);
        output.Write(12U);
        output.Write(2U);
        output.Write(100U);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((ushort)2);
        WriteSizedAscii(output, "NiNode");
        WriteSizedAscii(output, "BSTriShape");
        output.Write((ushort)0);
        output.Write((ushort)1);
        output.Write((uint)node.Length);
        output.Write((uint)shape.Length);
        output.Write((uint)strings.Length);
        output.Write((uint)strings.Max(item => item.Length));
        foreach (string value in strings)
            WriteSizedAscii(output, value);
        output.Write(0U);
        output.Write(node);
        output.Write(shape);
        output.Write(1U);
        output.Write(0);
        output.Flush();
        return result.ToArray();
    }

    private static byte[] BuildOverlappingSkyrimStaticTriShapeFixture(
        out int blockSizeTableOffset, out int firstShapeBlockSize)
    {
        var strings = new[] { "Root", "Head", "Mouth" };
        byte[] node;
        using (var stream = new MemoryStream())
        using (var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            writer.Write(0U);
            writer.Write(0U);
            writer.Write(-1);
            writer.Write(new byte[56]);
            writer.Write(-1);
            writer.Write(2U);
            writer.Write(1);
            writer.Write(2);
            writer.Write(0U);
            writer.Flush();
            node = stream.ToArray();
        }

        static byte[] BuildShape(int nameIndex)
        {
            const ulong descriptor = (0x401UL << 44) | 4UL;
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true);
            writer.Write((uint)nameIndex);
            writer.Write(0U);
            writer.Write(-1);
            writer.Write(new byte[56]);
            writer.Write(-1);
            writer.Write(new byte[16]);
            writer.Write(-1);
            writer.Write(-1);
            writer.Write(-1);
            writer.Write(descriptor);
            writer.Write((ushort)1);
            writer.Write((ushort)3);
            writer.Write(54U);
            for (int vertex = 0; vertex < 3; vertex++)
            {
                writer.Write((float)vertex);
                writer.Write((float)(vertex + 1));
                writer.Write((float)(vertex + 2));
                writer.Write(0U);
            }
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)2);
            writer.Flush();
            return stream.ToArray();
        }

        byte[][] shapes = [BuildShape(1), BuildShape(2)];
        using var result = new MemoryStream();
        using var output = new BinaryWriter(result, Encoding.ASCII, leaveOpen: true);
        output.Write(Encoding.ASCII.GetBytes(
            "Gamebryo File Format, Version 20.2.0.7\n"));
        output.Write(0x14020007U);
        output.Write((byte)1);
        output.Write(12U);
        output.Write(3U);
        output.Write(100U);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((byte)0);
        output.Write((ushort)2);
        WriteSizedAscii(output, "NiNode");
        WriteSizedAscii(output, "BSTriShape");
        output.Write((ushort)0);
        output.Write((ushort)1);
        output.Write((ushort)1);
        blockSizeTableOffset = checked((int)result.Position);
        output.Write((uint)node.Length);
        firstShapeBlockSize = shapes[0].Length;
        output.Write((uint)firstShapeBlockSize);
        output.Write((uint)shapes[1].Length);
        output.Write((uint)strings.Length);
        output.Write((uint)strings.Max(item => item.Length));
        foreach (string value in strings)
            WriteSizedAscii(output, value);
        output.Write(0U);
        output.Write(node);
        output.Write(shapes[0]);
        output.Write(shapes[1]);
        output.Write(1U);
        output.Write(0);
        output.Flush();
        return result.ToArray();
    }

    private sealed class MutatingReadbackService(
        INifGeometryReadbackService inner,
        byte[] carrierBytes) :
        INifGeometryReadbackService
    {
        private bool mutated;

        public bool Mutated => mutated;

        public int MutationOffset { get; private set; } = -1;

        public bool OutsideCarrierByteDiffers { get; private set; }

        public async ValueTask<NifGeometryReadbackResult> ReadAsync(
            NifGeometryReadbackRequest request,
            CancellationToken cancellationToken)
        {
            NifGeometryReadbackResult result = await inner.ReadAsync(request,
                cancellationToken);
            if (!mutated && request.NifPath.Value.Contains(
                    ".facegeom-transport-geometry-", StringComparison.OrdinalIgnoreCase))
            {
                byte[] bytes = File.ReadAllBytes(request.NifPath.Value);
                SseNifDocument document = SseFaceGeomCarrierCodec.Parse(bytes);
                SseNifBlock shape = document.Blocks.First(item =>
                    item.Type == "BSDynamicTriShape");
                int mutationOffset = shape.DynamicGeometry!.BoundsOffset;
                MutationOffset = mutationOffset;
                byte carrierByte = carrierBytes[mutationOffset];
                bytes[mutationOffset] ^= 0x01;
                OutsideCarrierByteDiffers = bytes[mutationOffset] != carrierByte;
                File.WriteAllBytes(request.NifPath.Value, bytes);
                mutated = true;
            }
            return result;
        }
    }

    private static void WriteSizedAscii(BinaryWriter writer, string value)
    {
        byte[] bytes = Encoding.ASCII.GetBytes(value);
        writer.Write((uint)bytes.Length);
        writer.Write(bytes);
    }

    private static void AssertStandalonePrivateReferencesOwned(string path)
    {
        var outputKey = ModKey.FromNameAndExtension(Path.GetFileName(path));
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(outputKey, new FilePath(path)),
            SkyrimRelease.SkyrimSE);
        var npc = overlay.Npcs[new FormKey(outputKey, 0x800)];
        Assert(npc.HeadTexture.FormKeyNullable is { ModKey: var textureOwner } &&
               textureOwner == outputKey,
            "standalone target head texture did not resolve to the output plugin");
        Assert(npc.HairColor.FormKeyNullable is { ModKey: var colorOwner } &&
               colorOwner == outputKey,
            "standalone target hair color did not resolve to the output plugin");
        Assert(npc.HeadParts.Count == 1 &&
               npc.HeadParts[0].FormKey.ModKey == outputKey,
            "standalone target head part did not resolve to the output plugin");
    }

    private static void RunCompressedMorphReadbackRegression(WorkspacePath workspace)
    {
        var root = new WorkspacePath(Path.Combine(workspace.Value, "compressed-morph"));
        Directory.CreateDirectory(root.Value);
        var ordinary = new WorkspacePath(Path.Combine(root.Value, "Ordinary.esp"));
        var compressed = new WorkspacePath(Path.Combine(root.Value, "Compressed.esp"));
        var malformed = new WorkspacePath(Path.Combine(root.Value, "Malformed.esp"));
        var oversized = new WorkspacePath(Path.Combine(root.Value, "Oversized.esp"));
        var tailed = new WorkspacePath(Path.Combine(root.Value, "Tailed.esp"));
        var nam9Bits = new[]
        {
            0x3E800000, unchecked((int)0xBF000000), 0x00000001,
            0x7F7FFFFF, unchecked((int)0x80000000), 0x3F000000,
            unchecked((int)0xBEAAAAAB), 0x7FC12345, unchecked((int)0xFFC54321),
            0x00800000, unchecked((int)0xC0000000), 0x41200000,
            0x00000000, 0x3DCCCCCD, unchecked((int)0xBF800000),
            0x3F800000, 0x40490FDB, unchecked((int)0xC0490FDB), 0x3F3504F3
        };
        var namaValues = new uint[] { 0x00000000, 0x01020304, 0xFFFFFFFF, 0x7F800001 };

        WriteFixture(ordinary);
        InsertTargetAtoms(ordinary.Value, BuildMorphSubrecords(nam9Bits, namaValues), null);
        var expected = BethesdaSkyrimFaceMorphAdapter.Read(
            GameEdition.SkyrimSpecialEdition, ordinary, new FormId(0x00000800));
        AssertMorphSnapshot(expected, nam9Bits, namaValues,
            "ordinary Skyrim morph readback");

        File.Copy(ordinary.Value, compressed.Value);
        CompressTargetRecord(compressed.Value);
        var compressedSnapshot = BethesdaSkyrimFaceMorphAdapter.Read(
            GameEdition.SkyrimSpecialEdition, compressed, new FormId(0x00000800));
        AssertMorphSnapshot(compressedSnapshot, nam9Bits, namaValues,
            "compressed Skyrim morph readback");
        AssertSnapshotsMatch(expected, compressedSnapshot);

        File.Copy(compressed.Value, malformed.Value);
        MutateCompressedDeclaredLength(malformed.Value, 1);
        var malformedRejected = false;
        try
        {
            _ = BethesdaSkyrimFaceMorphAdapter.Read(
                GameEdition.SkyrimSpecialEdition, malformed, new FormId(0x00000800));
        }
        catch (InvalidDataException)
        {
            malformedRejected = true;
        }
        Assert(malformedRejected,
            "compressed Skyrim morph readback accepted a one-byte declared-length mismatch");

        File.Copy(compressed.Value, oversized.Value);
        MutateCompressedDeclaredLength(oversized.Value, 16 * 1024 * 1024);
        var oversizedRejected = false;
        try
        {
            _ = BethesdaSkyrimFaceMorphAdapter.Read(
                GameEdition.SkyrimSpecialEdition, oversized, new FormId(0x00000800));
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("16 MiB", StringComparison.Ordinal))
        {
            oversizedRejected = true;
        }
        Assert(oversizedRejected,
            "compressed Skyrim morph readback did not refuse an oversized declaration before inflation");

        File.Copy(compressed.Value, tailed.Value);
        AppendCompressedTailByte(tailed.Value, 0xA5);
        var tailRejected = false;
        try
        {
            _ = BethesdaSkyrimFaceMorphAdapter.Read(
                GameEdition.SkyrimSpecialEdition, tailed, new FormId(0x00000800));
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains("trailing zlib tail", StringComparison.Ordinal))
        {
            tailRejected = true;
        }
        Assert(tailRejected,
            "compressed Skyrim morph readback accepted a valid zlib stream with a compressed tail");
    }

    private static byte[] BuildMorphSubrecords(int[] nam9Bits, uint[] namaValues)
    {
        Assert(nam9Bits.Length == 19, "NAM9 fixture requires exactly 19 float bit patterns");
        Assert(namaValues.Length == 4, "NAMA fixture requires exactly four values");
        var nam9 = new byte[76];
        for (var index = 0; index < nam9Bits.Length; index++)
            BinaryPrimitives.WriteInt32LittleEndian(
                nam9.AsSpan(index * 4, 4), nam9Bits[index]);
        var nama = new byte[16];
        for (var index = 0; index < namaValues.Length; index++)
            BinaryPrimitives.WriteUInt32LittleEndian(
                nama.AsSpan(index * 4, 4), namaValues[index]);
        using var output = new MemoryStream();
        WriteSubrecord(output, "NAM9", nam9);
        WriteSubrecord(output, "NAMA", nama);
        return output.ToArray();
    }

    private static void WriteSubrecord(Stream destination, string signature, byte[] data)
    {
        var header = new byte[6];
        Encoding.ASCII.GetBytes(signature).CopyTo(header.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4, 2), checked((ushort)data.Length));
        destination.Write(header);
        destination.Write(data);
    }

    private static void AssertMorphSnapshot(
        SkyrimFaceMorphSnapshot snapshot,
        int[] nam9Bits,
        uint[] namaValues,
        string context)
    {
        Assert(snapshot.HasNam9 && snapshot.HasNama,
            context + " did not report both native morph subrecords");
        Assert(snapshot.Nam9Sliders.Length == 18 && snapshot.NamaValues.Length == 4,
            context + " returned an unexpected native morph shape");
        for (var index = 0; index < 18; index++)
            Assert(BitConverter.SingleToInt32Bits(snapshot.Nam9Sliders[index]) == nam9Bits[index],
                $"{context} changed NAM9 slider bit pattern {index}");
        Assert(BitConverter.SingleToInt32Bits(snapshot.Nam9Trailing) == nam9Bits[18],
            context + " changed the engine-owned NAM9 trailing bit pattern");
        for (var index = 0; index < namaValues.Length; index++)
            Assert(snapshot.NamaValues[index] == namaValues[index],
                $"{context} changed NAMA value {index}");
    }

    private static void AssertSnapshotsMatch(
        SkyrimFaceMorphSnapshot expected,
        SkyrimFaceMorphSnapshot actual)
    {
        Assert(expected.HasNam9 == actual.HasNam9 && expected.HasNama == actual.HasNama,
            "ordinary and compressed Skyrim morph readbacks disagree on presence flags");
        Assert(expected.Nam9Sliders.Length == actual.Nam9Sliders.Length &&
               expected.Nam9Sliders.Select(BitConverter.SingleToInt32Bits)
                   .SequenceEqual(actual.Nam9Sliders.Select(BitConverter.SingleToInt32Bits)) &&
               BitConverter.SingleToInt32Bits(expected.Nam9Trailing) ==
                   BitConverter.SingleToInt32Bits(actual.Nam9Trailing) &&
               expected.NamaValues.SequenceEqual(actual.NamaValues),
            "ordinary and compressed Skyrim morph readbacks disagree");
    }

    private static void CompressTargetRecord(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var records = Rewrite(tes4End, bytes.Length, out var replaced);
        Assert(replaced, "fixture target NPC was not found for compressed morph readback");
        var output = new byte[tes4End + records.Length];
        bytes.AsSpan(0, tes4End).CopyTo(output);
        records.CopyTo(output.AsSpan(tes4End));
        File.WriteAllBytes(path, output);

        byte[] Rewrite(int start, int end, out bool didReplace)
        {
            using var stream = new MemoryStream();
            didReplace = false;
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    var children = Rewrite(position + 24, recordEnd,
                        out var childReplaced);
                    if (!childReplaced)
                        stream.Write(bytes, position, length);
                    else
                    {
                        var header = bytes.AsSpan(position, 24).ToArray();
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            header.AsSpan(4, 4), checked((uint)(24 + children.Length)));
                        stream.Write(header);
                        stream.Write(children);
                        didReplace = true;
                    }
                }
                else if (signature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                {
                    var payload = bytes.AsSpan(position + 24, checked((int)size)).ToArray();
                    using var compressed = new MemoryStream();
                    using (var zlib = new ZLibStream(
                               compressed, CompressionLevel.SmallestSize, leaveOpen: true))
                        zlib.Write(payload);
                    var encoded = new byte[checked(4 + (int)compressed.Length)];
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        encoded.AsSpan(0, 4), checked((uint)payload.Length));
                    compressed.ToArray().CopyTo(encoded.AsSpan(4));
                    var header = bytes.AsSpan(position, 24).ToArray();
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        header.AsSpan(8, 4), flags | 0x0004_0000u);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        header.AsSpan(4, 4), checked((uint)encoded.Length));
                    stream.Write(header);
                    stream.Write(encoded);
                    didReplace = true;
                }
                else
                    stream.Write(bytes, position, length);
                position = recordEnd;
            }
            return stream.ToArray();
        }
    }

    private static void MutateCompressedDeclaredLength(string path, int delta)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetRecordOffset(bytes, tes4End, bytes.Length);
        Assert(target >= 0, "compressed morph fixture target NPC was not found");
        var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(target + 8, 4));
        Assert((flags & 0x0004_0000u) != 0,
            "compressed morph fixture target NPC did not retain its compression flag");
        var size = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 4, 4)));
        Assert(size >= 4, "compressed morph fixture has no declared uncompressed length");
        var declared = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 24, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(target + 24, 4), checked((uint)((long)declared + delta)));
        File.WriteAllBytes(path, bytes);
    }

    private static void AppendCompressedTailByte(string path, byte tail)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var records = Rewrite(tes4End, bytes.Length, out var replaced);
        Assert(replaced, "compressed morph fixture target NPC was not found for tail insertion");
        var output = new byte[tes4End + records.Length];
        bytes.AsSpan(0, tes4End).CopyTo(output);
        records.CopyTo(output.AsSpan(tes4End));
        File.WriteAllBytes(path, output);

        byte[] Rewrite(int start, int end, out bool didReplace)
        {
            using var stream = new MemoryStream();
            didReplace = false;
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    var children = Rewrite(position + 24, recordEnd,
                        out var childReplaced);
                    if (!childReplaced)
                        stream.Write(bytes, position, length);
                    else
                    {
                        var header = bytes.AsSpan(position, 24).ToArray();
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            header.AsSpan(4, 4), checked((uint)(24 + children.Length)));
                        stream.Write(header);
                        stream.Write(children);
                        didReplace = true;
                    }
                }
                else if (signature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 8, 4)) & 0x0004_0000u) != 0 &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                {
                    var payload = bytes.AsSpan(position + 24, checked((int)size)).ToArray();
                    var extended = new byte[checked(payload.Length + 1)];
                    payload.CopyTo(extended, 0);
                    extended[^1] = tail;
                    var header = bytes.AsSpan(position, 24).ToArray();
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        header.AsSpan(4, 4), checked((uint)extended.Length));
                    stream.Write(header);
                    stream.Write(extended);
                    didReplace = true;
                }
                else
                    stream.Write(bytes, position, length);
                position = recordEnd;
            }
            return stream.ToArray();
        }
    }

    private sealed record RawTargetAtom(string Signature, byte[] Bytes);

    private static ImmutableArray<RawTargetAtom> ReadTargetRawAtoms(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetRecordOffset(bytes, tes4End, bytes.Length);
        Assert(target >= 0, "fixture target NPC was not found for raw atom parsing");
        var targetSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 4, 4)));
        var targetEnd = checked(target + 24 + targetSize);
        var atoms = ImmutableArray.CreateBuilder<RawTargetAtom>();
        var position = target + 24;
        while (position < targetEnd)
        {
            var atomStart = position;
            Assert(targetEnd - position >= 6,
                "target raw atom header is truncated");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            var payload = checked(position + 6);
            int atomEnd;
            string atomSignature;
            if (signature == "XXXX")
            {
                Assert(shortSize == 4 && targetEnd - payload >= 4,
                    "target XXXX atom marker is malformed");
                var extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(payload, 4));
                Assert(extendedSize <= int.MaxValue,
                    "target XXXX atom is too large for the test parser");
                var following = checked(payload + 4);
                Assert(targetEnd - following >= 6,
                    "target XXXX atom has no following subrecord");
                atomSignature = Encoding.ASCII.GetString(bytes, following, 4);
                var followingSize = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(following + 4, 2));
                Assert(followingSize == 0,
                    "target XXXX atom following subrecord has a nonzero short size");
                atomEnd = checked(following + 6 + (int)extendedSize);
            }
            else
            {
                atomSignature = signature;
                atomEnd = checked(payload + shortSize);
            }
            Assert(atomEnd <= targetEnd,
                "target raw atom exceeds the target record");
            atoms.Add(new RawTargetAtom(
                atomSignature,
                bytes.AsSpan(atomStart, atomEnd - atomStart).ToArray()));
            position = atomEnd;
        }
        Assert(position == targetEnd, "target raw atom parser ended out of bounds");
        return atoms.ToImmutable();
    }

    private static void InsertInterleavedUnknownTargetSubrecord(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var extension = new byte[18];
        "ZZZZ"u8.CopyTo(extension.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(extension.AsSpan(4, 2), 12);
        for (var index = 0; index < 12; index++)
            extension[6 + index] = checked((byte)(0xA0 + index));

        var records = Rewrite(tes4End, bytes.Length, out var replaced);
        Assert(replaced, "fixture target NPC was not found for unknown atom insertion");
        var output = new byte[tes4End + records.Length];
        bytes.AsSpan(0, tes4End).CopyTo(output);
        records.CopyTo(output.AsSpan(tes4End));
        File.WriteAllBytes(path, output);

        byte[] Rewrite(int start, int end, out bool didReplace)
        {
            using var stream = new MemoryStream();
            didReplace = false;
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    var children = Rewrite(position + 24, recordEnd,
                        out var childReplaced);
                    if (!childReplaced)
                        stream.Write(bytes, position, length);
                    else
                    {
                        var header = bytes.AsSpan(position, 24).ToArray();
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            header.AsSpan(4, 4), checked((uint)(24 + children.Length)));
                        stream.Write(header);
                        stream.Write(children);
                        didReplace = true;
                    }
                }
                else if (signature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                {
                    var header = bytes.AsSpan(position, 24).ToArray();
                    var targetSize = checked((int)size);
                    var targetStart = position + 24;
                    var targetEnd = checked(targetStart + targetSize);
                    var firstAtomEnd = ReadFirstRawAtomEnd(bytes, targetStart, targetEnd);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        header.AsSpan(4, 4), checked(size + (uint)extension.Length));
                    stream.Write(header);
                    stream.Write(bytes, targetStart, firstAtomEnd - targetStart);
                    stream.Write(extension);
                    stream.Write(bytes, firstAtomEnd, targetEnd - firstAtomEnd);
                    didReplace = true;
                }
                else
                    stream.Write(bytes, position, length);
                position = recordEnd;
            }
            return stream.ToArray();
        }
    }

    private static void InsertDiscontiguousFactionAtoms(string path)
    {
        var extension = new byte[18];
        "YZZZ"u8.CopyTo(extension.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(extension.AsSpan(4, 2), 12);
        for (var index = 0; index < 12; index++)
            extension[6 + index] = checked((byte)(0xC0 + index));
        InsertTargetAtoms(path, extension, "SNAM", occurrence: 2);
    }

    private static void InsertKeywordCountCompanion(string path)
    {
        var atom = new byte[10];
        "KSIZ"u8.CopyTo(atom.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(atom.AsSpan(4, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(atom.AsSpan(6, 4), 16_384);
        InsertTargetAtoms(path, atom, "KWDA");
    }

    private static void InsertTargetAtoms(
        string path,
        byte[] extension,
        string? beforeSignature,
        int occurrence = 1)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var records = Rewrite(tes4End, bytes.Length, out var replaced);
        Assert(replaced, "fixture target NPC was not found for atom insertion");
        var output = new byte[tes4End + records.Length];
        bytes.AsSpan(0, tes4End).CopyTo(output);
        records.CopyTo(output.AsSpan(tes4End));
        File.WriteAllBytes(path, output);

        byte[] Rewrite(int start, int end, out bool didReplace)
        {
            using var stream = new MemoryStream();
            didReplace = false;
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    var children = Rewrite(position + 24, recordEnd,
                        out var childReplaced);
                    if (!childReplaced)
                        stream.Write(bytes, position, length);
                    else
                    {
                        var header = bytes.AsSpan(position, 24).ToArray();
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            header.AsSpan(4, 4), checked((uint)(24 + children.Length)));
                        stream.Write(header);
                        stream.Write(children);
                        didReplace = true;
                    }
                }
                else if (signature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                {
                    var header = bytes.AsSpan(position, 24).ToArray();
                    var targetStart = position + 24;
                    var targetEnd = checked(targetStart + (int)size);
                    var insertion = beforeSignature is null
                        ? ReadFirstRawAtomEnd(bytes, targetStart, targetEnd)
                        : FindRawAtomStart(bytes, targetStart, targetEnd,
                            beforeSignature, occurrence);
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        header.AsSpan(4, 4), checked(size + (uint)extension.Length));
                    stream.Write(header);
                    stream.Write(bytes, targetStart, insertion - targetStart);
                    stream.Write(extension);
                    stream.Write(bytes, insertion, targetEnd - insertion);
                    didReplace = true;
                }
                else
                    stream.Write(bytes, position, length);
                position = recordEnd;
            }
            return stream.ToArray();
        }
    }

    private static int FindRawAtomStart(
        byte[] bytes,
        int start,
        int end,
        string expectedSignature,
        int occurrence)
    {
        var position = start;
        var seen = 0;
        while (position < end)
        {
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var atomEnd = ReadFirstRawAtomEnd(bytes, position, end);
            var atomSignature = signature == "XXXX"
                ? Encoding.ASCII.GetString(bytes, position + 10, 4)
                : signature;
            if (atomSignature == expectedSignature && ++seen == occurrence)
                return position;
            position = atomEnd;
        }
        throw new InvalidOperationException(
            $"target atom {expectedSignature}[{occurrence}] was not found for insertion");
    }

    private static int FindTargetRecordOffset(byte[] bytes, int start, int end)
    {
        var position = start;
        while (position < end)
        {
            Assert(end - position >= 24, "fixture record header is truncated");
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 4, 4));
            var length = signature == "GRUP"
                ? checked((int)size)
                : checked(24 + (int)size);
            var recordEnd = checked(position + length);
            if (signature == "GRUP")
            {
                var nested = FindTargetRecordOffset(bytes, position + 24, recordEnd);
                if (nested >= 0) return nested;
            }
            else if (signature == "NPC_" &&
                     (BinaryPrimitives.ReadUInt32LittleEndian(
                          bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                return position;
            position = recordEnd;
        }
        return -1;
    }

    private static int ReadFirstRawAtomEnd(byte[] bytes, int start, int end)
    {
        Assert(end - start >= 6, "fixture target has no first subrecord");
        var signature = Encoding.ASCII.GetString(bytes, start, 4);
        var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(start + 4, 2));
        if (signature != "XXXX")
            return checked(start + 6 + shortSize);
        Assert(shortSize == 4 && end - start >= 10,
            "fixture first XXXX atom marker is malformed");
        var extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(start + 6, 4));
        var following = start + 10;
        Assert(end - following >= 6 &&
               BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(following + 4, 2)) == 0,
            "fixture first XXXX atom following subrecord is malformed");
        return checked(following + 6 + (int)extendedSize);
    }

    private static void MutateTargetUnknownAtom(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetRecordOffset(bytes, tes4End, bytes.Length);
        Assert(target >= 0, "fixture target NPC was not found for unknown atom mutation");
        var targetSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 4, 4)));
        var targetEnd = checked(target + 24 + targetSize);
        var position = target + 24;
        var changed = false;
        while (position < targetEnd)
        {
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            var payload = checked(position + 6);
            if (signature == "ZZZZ")
            {
                Assert(shortSize > 0, "unknown target atom has no payload");
                bytes[payload] ^= 0xFF;
                changed = true;
                break;
            }
            position = signature == "XXXX"
                ? ReadFirstRawAtomEnd(bytes, position, targetEnd)
                : checked(payload + shortSize);
        }
        Assert(changed, "unknown target atom was not found for mutation");
        File.WriteAllBytes(path, bytes);
    }

    private static void MoveTargetUnknownAtomBeforeEditorId(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetRecordOffset(bytes, tes4End, bytes.Length);
        Assert(target >= 0, "fixture target NPC was not found for atom movement");
        var targetSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 4, 4)));
        var targetEnd = checked(target + 24 + targetSize);
        var atoms = ReadTargetRawAtoms(path).ToList();
        var unknown = atoms.SingleOrDefault(atom => atom.Signature == "ZZZZ");
        Assert(unknown is not null, "unknown target atom was not found for movement");
        atoms.Remove(unknown!);
        var editorIndex = atoms.FindIndex(atom => atom.Signature == "EDID");
        Assert(editorIndex >= 0, "target EDID was not found for atom movement");
        atoms.Insert(editorIndex, unknown!);
        var cursor = target + 24;
        foreach (var atom in atoms)
        {
            atom.Bytes.CopyTo(bytes.AsSpan(cursor));
            cursor += atom.Bytes.Length;
        }
        Assert(cursor == targetEnd, "atom movement changed the target payload length");
        File.WriteAllBytes(path, bytes);
    }

    private static void SwapTargetAuthorizedAtomsAcrossUnknown(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetRecordOffset(bytes, tes4End, bytes.Length);
        Assert(target >= 0, "fixture target NPC was not found for identity swap");
        var atoms = ReadTargetRawAtoms(path).ToList();
        var editorIndex = atoms.FindIndex(atom => atom.Signature == "EDID");
        var fullIndex = atoms.FindIndex(atom => atom.Signature == "FULL");
        Assert(editorIndex >= 0 && fullIndex >= 0,
            "target EDID/FULL atoms were not found for identity swap");
        (atoms[editorIndex], atoms[fullIndex]) =
            (atoms[fullIndex], atoms[editorIndex]);
        var targetSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 4, 4)));
        var cursor = target + 24;
        foreach (var atom in atoms)
        {
            atom.Bytes.CopyTo(bytes.AsSpan(cursor));
            cursor += atom.Bytes.Length;
        }
        Assert(cursor == target + 24 + targetSize,
            "identity atom swap changed the target payload length");
        File.WriteAllBytes(path, bytes);
    }

    private static ImmutableArray<(uint FormId, uint Rank)> ReadFactionRawEntries(
        string path)
    {
        var entries = ImmutableArray.CreateBuilder<(uint FormId, uint Rank)>();
        foreach (var atom in ReadTargetRawAtoms(path)
                     .Where(atom => atom.Signature == "SNAM"))
        {
            var payloadOffset = atom.Bytes.AsSpan(0, 4)
                                    .SequenceEqual("XXXX"u8) ? 16 : 6;
            Assert(atom.Bytes.Length >= payloadOffset &&
                   (atom.Bytes.Length - payloadOffset) % 8 == 0,
                "SNAM payload was not a sequence of 8-byte faction entries");
            for (var offset = payloadOffset;
                 offset < atom.Bytes.Length;
                 offset += 8)
            {
                entries.Add((
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        atom.Bytes.AsSpan(offset, 4)),
                    BinaryPrimitives.ReadUInt32LittleEndian(
                        atom.Bytes.AsSpan(offset + 4, 4))));
            }
        }
        return entries.ToImmutable();
    }

    private static void MutateTargetAtomSignature(
        string path,
        string expectedSignature,
        string replacementSignature)
    {
        Assert(expectedSignature.Length == 4 && replacementSignature.Length == 4,
            "raw atom signatures must be four characters");
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetRecordOffset(bytes, tes4End, bytes.Length);
        Assert(target >= 0, "fixture target NPC was not found for atom signature mutation");
        var targetSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 4, 4)));
        var targetEnd = checked(target + 24 + targetSize);
        var position = target + 24;
        var changed = false;
        while (position < targetEnd)
        {
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            if (signature == expectedSignature)
            {
                Encoding.ASCII.GetBytes(replacementSignature)
                    .CopyTo(bytes.AsSpan(position, 4));
                changed = true;
                break;
            }
            position = signature == "XXXX"
                ? ReadFirstRawAtomEnd(bytes, position, targetEnd)
                : checked(position + 6 + shortSize);
        }
        Assert(changed,
            $"target atom {expectedSignature} was not found for signature mutation");
        File.WriteAllBytes(path, bytes);
    }

    private static void SplitKeywordDataWithUnknown(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetRecordOffset(bytes, tes4End, bytes.Length);
        Assert(target >= 0, "fixture target NPC was not found for KWDA gap setup");
        var targetSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 4, 4)));
        var targetEnd = checked(target + 24 + targetSize);
        var position = target + 24;
        var changed = false;
        while (position < targetEnd)
        {
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var atomEnd = ReadFirstRawAtomEnd(bytes, position, targetEnd);
            var atomSignature = signature == "XXXX"
                ? Encoding.ASCII.GetString(bytes, position + 10, 4)
                : signature;
            if (signature == "XXXX" && atomSignature == "KWDA")
            {
                var payload = bytes.AsSpan(position + 16, 4).ToArray();
                var first = BuildExtendedRawAtom("KWDA", payload);
                var unknownPayload = new byte[65_516];
                for (var index = 0; index < unknownPayload.Length; index++)
                    unknownPayload[index] = checked((byte)(0xD0 + index % 31));
                var unknown = BuildNormalRawAtom("YKKW", unknownPayload);
                var second = BuildNormalRawAtom("KWDA", payload);
                var replacement = first.Concat(unknown).Concat(second)
                    .ToArray();
                Assert(replacement.Length == atomEnd - position,
                    "KWDA gap fixture replacement changed the target framing length");
                replacement.CopyTo(bytes.AsSpan(position));
                changed = true;
                break;
            }
            position = atomEnd;
        }
        Assert(changed, "extended KWDA atom was not found for gap setup");
        File.WriteAllBytes(path, bytes);
        MutateTargetCountCompanion(path, "KSIZ", 2);
    }

    private static byte[] BuildExtendedRawAtom(string signature, byte[] payload)
    {
        Assert(signature.Length == 4, "extended raw atom signatures must be four characters");
        var bytes = new byte[checked(16 + payload.Length)];
        "XXXX"u8.CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(6, 4), checked((uint)payload.Length));
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes.AsSpan(10, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(14, 2), 0);
        payload.CopyTo(bytes.AsSpan(16));
        return bytes;
    }

    private static byte[] BuildNormalRawAtom(string signature, byte[] payload)
    {
        Assert(signature.Length == 4 && payload.Length <= ushort.MaxValue,
            "normal raw atom shape is invalid");
        var bytes = new byte[checked(6 + payload.Length)];
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(
            bytes.AsSpan(4, 2), checked((ushort)payload.Length));
        payload.CopyTo(bytes.AsSpan(6));
        return bytes;
    }

    private static ImmutableArray<uint> ReadTargetRawFormIdEntries(
        string path,
        string expectedSignature)
    {
        var entries = ImmutableArray.CreateBuilder<uint>();
        foreach (var atom in ReadTargetRawAtoms(path)
                     .Where(atom => atom.Signature == expectedSignature))
        {
            var payloadOffset = atom.Bytes.AsSpan(0, 4)
                                    .SequenceEqual("XXXX"u8) ? 16 : 6;
            Assert(atom.Bytes.Length >= payloadOffset &&
                   (atom.Bytes.Length - payloadOffset) % 4 == 0,
                $"{expectedSignature} payload was not a sequence of FormIDs");
            for (var offset = payloadOffset;
                 offset < atom.Bytes.Length;
                 offset += 4)
                entries.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                    atom.Bytes.AsSpan(offset, 4)));
        }
        return entries.ToImmutable();
    }

    private static void MutateTargetCountCompanion(
        string path,
        string expectedSignature,
        uint value)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetRecordOffset(bytes, tes4End, bytes.Length);
        Assert(target >= 0, "fixture target NPC was not found for count mutation");
        var targetSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(target + 4, 4)));
        var targetEnd = checked(target + 24 + targetSize);
        var position = target + 24;
        var changed = false;
        while (position < targetEnd)
        {
            var signature = Encoding.ASCII.GetString(bytes, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            var payload = checked(position + 6);
            if (signature == expectedSignature)
            {
                Assert(shortSize == 4,
                    $"count companion {expectedSignature} does not have a 4-byte payload");
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(payload, 4), value);
                changed = true;
                break;
            }
            position = signature == "XXXX"
                ? ReadFirstRawAtomEnd(bytes, position, targetEnd)
                : checked(payload + shortSize);
        }
        Assert(changed, $"count companion {expectedSignature} was not found for mutation");
        File.WriteAllBytes(path, bytes);
    }

    private static byte[] ReadTargetSubrecord(string path, string expectedSignature)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        byte[]? result = null;
        Walk(tes4End, bytes.Length);
        Assert(result is not null,
            $"target subrecord {expectedSignature} was not found");
        return result!;

        void Walk(int start, int end)
        {
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    Walk(position + 24, recordEnd);
                }
                else if (signature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                {
                    ReadSubrecords(position + 24, recordEnd);
                }
                position = recordEnd;
            }
        }

        void ReadSubrecords(int start, int end)
        {
            var position = start;
            uint? extendedSize = null;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(position + 4, 2));
                var payload = checked(position + 6);
                if (signature == "XXXX")
                {
                    Assert(shortSize == 4,
                        "target XXXX marker has an invalid short size");
                    extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(payload, 4));
                    position = checked(payload + 4);
                    continue;
                }
                var size = checked((int)(extendedSize ?? shortSize));
                extendedSize = null;
                if (signature == expectedSignature &&
                    (result is null || size > result.Length))
                    result = bytes.AsSpan(payload, size).ToArray();
                position = checked(payload + size);
            }
            Assert(extendedSize is null,
                "target subrecords ended after an XXXX marker");
        }
    }

    private static void MutateTargetFormLink(string path, string expectedSignature,
        uint rawFormId)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var changed = false;
        Walk(tes4End, bytes.Length);
        Assert(changed, $"target FormLink {expectedSignature} was not found");
        File.WriteAllBytes(path, bytes);

        void Walk(int start, int end)
        {
            var position = start;
            while (position < end && !changed)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                    Walk(position + 24, recordEnd);
                else if (signature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                    PatchSubrecords(position + 24, recordEnd);
                position = recordEnd;
            }
        }

        void PatchSubrecords(int start, int end)
        {
            var position = start;
            uint? extendedSize = null;
            while (position < end && !changed)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(position + 4, 2));
                var payload = checked(position + 6);
                if (signature == "XXXX")
                {
                    Assert(shortSize == 4,
                        "target XXXX marker has an invalid short size");
                    extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(payload, 4));
                    position = checked(payload + 4);
                    continue;
                }
                var size = checked((int)(extendedSize ?? shortSize));
                extendedSize = null;
                if (signature == expectedSignature && size >= 4)
                {
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.AsSpan(payload, 4), rawFormId);
                    changed = true;
                    return;
                }
                position = checked(payload + size);
            }
        }
    }

    private static void AppendTargetFormLinkSubrecord(string path,
        string signature, uint rawFormId)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var extension = new byte[10];
        Encoding.ASCII.GetBytes(signature).CopyTo(extension.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(extension.AsSpan(4, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(extension.AsSpan(6, 4), rawFormId);

        var records = Rewrite(tes4End, bytes.Length, out var replaced);
        Assert(replaced, $"fixture target NPC was not found for appended {signature}");
        var output = new byte[tes4End + records.Length];
        bytes.AsSpan(0, tes4End).CopyTo(output);
        records.CopyTo(output.AsSpan(tes4End));
        File.WriteAllBytes(path, output);

        byte[] Rewrite(int start, int end, out bool didReplace)
        {
            using var stream = new MemoryStream();
            didReplace = false;
            var position = start;
            while (position < end)
            {
                var currentSignature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = currentSignature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (currentSignature == "GRUP")
                {
                    var children = Rewrite(position + 24, recordEnd,
                        out var childReplaced);
                    if (!childReplaced)
                        stream.Write(bytes, position, length);
                    else
                    {
                        var header = bytes.AsSpan(position, 24).ToArray();
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            header.AsSpan(4, 4), checked((uint)(24 + children.Length)));
                        stream.Write(header);
                        stream.Write(children);
                        didReplace = true;
                    }
                }
                else if (currentSignature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                {
                    var header = bytes.AsSpan(position, 24).ToArray();
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        header.AsSpan(4, 4), checked(size + (uint)extension.Length));
                    stream.Write(header);
                    stream.Write(bytes, position + 24, checked((int)size));
                    stream.Write(extension);
                    didReplace = true;
                }
                else
                    stream.Write(bytes, position, length);
                position = recordEnd;
            }
            return stream.ToArray();
        }
    }

    private static void AppendExtendedKeywordSubrecord(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var ownerIndex = ReadInventory(path)
            .Single(row => row.Signature == "NPC_" &&
                           (row.RawFormId & 0x00FF_FFFFu) == 0x800)
            .RawFormId >> 24;
        var payload = new byte[65_536];
        for (var index = 0; index < payload.Length; index += 4)
            BinaryPrimitives.WriteUInt32LittleEndian(
                payload.AsSpan(index, 4), (ownerIndex << 24) | 0x0000_0805u);
        var extension = new byte[16 + payload.Length];
        "XXXX"u8.CopyTo(extension.AsSpan(0, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(extension.AsSpan(4, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(
            extension.AsSpan(6, 4), checked((uint)payload.Length));
        "KWDA"u8.CopyTo(extension.AsSpan(10, 4));
        BinaryPrimitives.WriteUInt16LittleEndian(extension.AsSpan(14, 2), 0);
        payload.CopyTo(extension.AsSpan(16));

        var records = Rewrite(tes4End, bytes.Length, out var replaced);
        Assert(replaced, "fixture target NPC was not found for extended KWDA");
        var output = new byte[tes4End + records.Length];
        bytes.AsSpan(0, tes4End).CopyTo(output);
        records.CopyTo(output.AsSpan(tes4End));
        File.WriteAllBytes(path, output);

        byte[] Rewrite(int start, int end, out bool didReplace)
        {
            using var stream = new MemoryStream();
            didReplace = false;
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    var children = Rewrite(position + 24, recordEnd,
                        out var childReplaced);
                    if (!childReplaced)
                        stream.Write(bytes, position, length);
                    else
                    {
                        var header = bytes.AsSpan(position, 24).ToArray();
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            header.AsSpan(4, 4), checked((uint)(24 + children.Length)));
                        stream.Write(header);
                        stream.Write(children);
                        didReplace = true;
                    }
                }
                else if (signature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                {
                    var header = bytes.AsSpan(position, 24).ToArray();
                    BinaryPrimitives.WriteUInt32LittleEndian(
                        header.AsSpan(4, 4), checked(size + (uint)extension.Length));
                    stream.Write(header);
                    stream.Write(bytes, position + 24, checked((int)size));
                    stream.Write(extension);
                    didReplace = true;
                }
                else
                    stream.Write(bytes, position, length);
                position = recordEnd;
            }
            return stream.ToArray();
        }
    }

    private static void MutateTargetEditorId(string path, string value)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var changed = false;
        Walk(tes4End, bytes.Length);
        Assert(changed, "target editor ID subrecord was not found");
        File.WriteAllBytes(path, bytes);

        void Walk(int start, int end)
        {
            var position = start;
            while (position < end && !changed)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                    Walk(position + 24, recordEnd);
                else if (signature == "NPC_")
                    PatchSubrecords(position + 24, recordEnd);
                position = recordEnd;
            }
        }

        void PatchSubrecords(int start, int end)
        {
            var position = start;
            var replacement = Encoding.UTF8.GetBytes(value + "\0");
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(position + 4, 2));
                var payload = checked(position + 6);
                if (signature == "EDID")
                {
                    Assert(size == replacement.Length,
                        "target editor ID mutation helper requires equal byte length");
                    replacement.CopyTo(bytes.AsSpan(payload, size));
                    changed = true;
                    return;
                }
                position = checked(payload + size);
            }
        }
    }

    private static void MutateFirstGroupHeader(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var changed = false;
        Walk(tes4End, bytes.Length);
        Assert(changed, "fixture output has no group header to mutate");
        File.WriteAllBytes(path, bytes);

        void Walk(int start, int end)
        {
            var position = start;
            while (position < end && !changed)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    // Bytes 16-23 are non-size group-header state.
                    bytes[position + 16] ^= 0x01;
                    changed = true;
                }
                if (signature == "GRUP" && !changed)
                    Walk(position + 24, recordEnd);
                position = recordEnd;
            }
        }
    }

    private static void PatchTes4Flags(string path, uint flags)
    {
        var bytes = File.ReadAllBytes(path);
        var current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(8, 4), current | flags);
        File.WriteAllBytes(path, bytes);
    }

    private static void PatchTargetFlags(string path, uint flags)
    {
        PatchTargetHeader(path, header =>
        {
            var current = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(8, 4), current | flags);
        });
    }

    private static void PatchTargetBounds(string path)
    {
        PatchTargetHeader(path, header =>
        {
            BinaryPrimitives.WriteUInt32LittleEndian(
                header.AsSpan(4, 4), uint.MaxValue);
        });
    }

    private static void PatchTargetHeader(
        string path,
        Action<byte[]> patch)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var target = FindTargetHeader(tes4End, bytes.Length);
        Assert(target >= 0, "fixture target NPC header was not found");
        var header = bytes.AsSpan(target, 24).ToArray();
        patch(header);
        header.CopyTo(bytes.AsSpan(target, 24));
        File.WriteAllBytes(path, bytes);

        int FindTargetHeader(int start, int end)
        {
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    var nested = FindTargetHeader(position + 24, recordEnd);
                    if (nested >= 0) return nested;
                }
                else if (signature == "NPC_" &&
                         (BinaryPrimitives.ReadUInt32LittleEndian(
                              bytes.AsSpan(position + 12, 4)) & 0x00FF_FFFFu) == 0x800)
                    return position;
                position = recordEnd;
            }
            return -1;
        }
    }

    private static void CreateJunction(string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        }) ?? throw new InvalidOperationException("could not start junction creation command");
        process.WaitForExit();
        Assert(process.ExitCode == 0,
            "junction creation failed: " + process.StandardError.ReadToEnd());
    }

    private static void RemoveJunction(string link)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c rmdir \"{link}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        });
        process?.WaitForExit();
    }

    internal static void WriteFixture(WorkspacePath path, bool includeFactions = false, bool duplicateLocalFormId = true)
    {
        var outputKey = ModKey.FromNameAndExtension(Path.GetFileName(path.Value));
        var skyrimKey = ModKey.FromNameAndExtension("Skyrim.esm");
        var mod = new SkyrimMod(outputKey, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(new MasterReference { Master = skyrimKey });

        var textureSet = new TextureSet(
            new FormKey(outputKey, 0x801), SkyrimRelease.SkyrimSE)
        {
            EditorID = "PrivateFaceTextureSet",
            Diffuse = "actors/character/facegen/private_d.dds",
            NormalOrGloss = "actors/character/facegen/private_n.dds"
        };
        mod.TextureSets.Add(textureSet);

        var headPart = new HeadPart(
            new FormKey(outputKey, 0x803), SkyrimRelease.SkyrimSE)
        {
            EditorID = "PrivateFaceHeadPart",
            Type = HeadPart.TypeEnum.Face,
            Flags = HeadPart.Flag.Playable | HeadPart.Flag.Female,
            Model = new Model
            {
                File = new AssetLink<SkyrimModelAssetType>(
                    "actors/character/facegen/private.nif")
            }
        };
        mod.HeadParts.Add(headPart);

        var hairColor = new ColorRecord(
            new FormKey(outputKey, 0x802), SkyrimRelease.SkyrimSE)
        {
            EditorID = "PrivateHairColor",
            Color = Color.FromArgb(255, 0x33, 0x22, 0x11),
            Playable = true
        };
        mod.Colors.Add(hairColor);

        var privateKeyword = new Keyword(
            new FormKey(outputKey, 0x805), SkyrimRelease.SkyrimSE)
        {
            EditorID = "PrivateLargeKeyword"
        };
        mod.Keywords.Add(privateKeyword);
        var secondPrivateKeyword = new Keyword(
            new FormKey(outputKey, 0x808), SkyrimRelease.SkyrimSE)
        {
            EditorID = "PrivateSecondKeyword"
        };
        mod.Keywords.Add(secondPrivateKeyword);

        if (includeFactions)
        {
            mod.Factions.Add(new Faction(
                new FormKey(outputKey, 0x806), SkyrimRelease.SkyrimSE)
            {
                EditorID = "PrivateFactionA"
            });
            mod.Factions.Add(new Faction(
                new FormKey(outputKey, 0x807), SkyrimRelease.SkyrimSE)
            {
                EditorID = "PrivateFactionB"
            });
        }

        var npc = new Npc(
            new FormKey(outputKey, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "StandaloneSourceNpc",
            Name = "Standalone Source NPC",
            Weight = 50,
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            },
            Race = new FormLink<IRaceGetter>(
                new FormKey(skyrimKey, 0x13746)),
            Class = new FormLink<IClassGetter>(
                new FormKey(skyrimKey, 0x13181)),
            HeadTexture = new FormLinkNullable<ITextureSetGetter>(textureSet.FormKey),
            HairColor = new FormLinkNullable<IColorRecordGetter>(hairColor.FormKey)
        };
        npc.HeadParts.Add(new FormLink<IHeadPartGetter>(headPart.FormKey));
        mod.Npcs.Add(npc);
        if (includeFactions)
        {
            npc.Factions.Add(new RankPlacement
            {
                Faction = new FormLink<IFactionGetter>(
                    new FormKey(outputKey, 0x806)),
                Rank = 1
            });
            npc.Factions.Add(new RankPlacement
            {
                Faction = new FormLink<IFactionGetter>(
                    new FormKey(outputKey, 0x807)),
                Rank = 2
            });
        }

        mod.Relationships.Add(new Relationship(
            new FormKey(outputKey, 0x804), SkyrimRelease.SkyrimSE)
        {
            EditorID = "PrivateRelationship",
            Parent = new FormLink<INpcGetter>(npc.FormKey),
            Child = new FormLink<INpcGetter>(new FormKey(skyrimKey, 0x14)),
            Rank = Relationship.RankType.Ally,
            Unknown = 0,
            Flags = 0,
            AssociationType = new FormLink<IAssociationTypeGetter>(FormKey.Null)
        });

        mod.WriteToBinary(new FilePath(path.Value), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        if (duplicateLocalFormId) PatchDuplicateLocalFormId(path.Value, 0x802, 0x801);
    }

    private static void PatchDuplicateLocalFormId(string path, uint from, uint to)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4End = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        Walk(tes4End, bytes.Length);
        File.WriteAllBytes(path, bytes);

        void Walk(int start, int end)
        {
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                if (signature == "GRUP")
                {
                    Walk(position + 24, recordEnd);
                }
                else
                {
                    if (signature == "CLFM")
                        PatchUInt32(position + 12);
                    if (signature == "NPC_")
                        PatchSubrecordFormId(position + 24, recordEnd, "HCLF");
                }
                position = recordEnd;
            }
        }

        void PatchSubrecordFormId(int start, int end, string targetSignature)
        {
            var position = start;
            while (position < end)
            {
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt16LittleEndian(
                    bytes.AsSpan(position + 4, 2));
                var payload = position + 6;
                if (signature == targetSignature && size == 4)
                    PatchUInt32(payload);
                position = checked(payload + size);
            }
        }

        void PatchUInt32(int offset)
        {
            var current = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4));
            if ((current & 0x00FF_FFFFu) == from)
                BinaryPrimitives.WriteUInt32LittleEndian(
                    bytes.AsSpan(offset, 4),
                    (current & 0xFF00_0000u) | to);
        }
    }

    private static void WriteSourceSidecars(string dataRoot)
    {
        var geom = Path.Combine(dataRoot, "meshes", "actors", "character",
            "FaceGenData", "FaceGeom", "Input.esp");
        var tint = Path.Combine(dataRoot, "textures", "actors", "character",
            "FaceGenData", "FaceTint", "Input.esp");
        var diffuse = Path.Combine(dataRoot, "textures", "actors", "character",
            "FaceGenData", "FaceDiffuse", "Input.esp");
        var normal = Path.Combine(dataRoot, "textures", "actors", "character",
            "FaceGenData", "FaceNormal", "Input.esp");
        Directory.CreateDirectory(geom);
        Directory.CreateDirectory(tint);
        Directory.CreateDirectory(diffuse);
        Directory.CreateDirectory(normal);
        File.WriteAllBytes(Path.Combine(geom, "00000800.nif"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(tint, "00000800.dds"), [5, 6, 7, 8]);
        File.WriteAllBytes(Path.Combine(diffuse, "00000800.dds"), [9, 10, 11, 12]);
        File.WriteAllBytes(Path.Combine(normal, "00000800.dds"), [13, 14, 15, 16]);
    }

    private static ImmutableArray<InventoryRow> ReadInventory(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var rows = ImmutableArray.CreateBuilder<InventoryRow>();
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        var tes4Length = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        Walk(tes4Length, bytes.Length, string.Empty);
        return rows.ToImmutable();

        void Walk(int start, int end, string groupPath)
        {
            var position = start;
            while (position < end)
            {
                Assert(position + 24 <= end, "fixture record header is truncated");
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                Assert(recordEnd <= end && length >= 24,
                    "fixture record exceeds its group");
                if (signature == "GRUP")
                {
                    var label = Encoding.ASCII.GetString(bytes, position + 8, 4);
                    var type = BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4));
                    var child = string.IsNullOrEmpty(groupPath)
                        ? $"{label}:{type:X8}"
                        : $"{groupPath}/{label}:{type:X8}";
                    Walk(position + 24, recordEnd, child);
                }
                else
                {
                    var formId = BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 12, 4));
                    var flags = BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(position + 8, 4));
                    var ordinal = ordinals.GetValueOrDefault(groupPath);
                    ordinals[groupPath] = ordinal + 1;
                    rows.Add(new InventoryRow(
                        groupPath,
                        ordinal,
                        signature,
                        formId,
                        flags,
                        length,
                        Convert.ToHexString(SHA256.HashData(
                            bytes.AsSpan(position, length)))));
                }
                position = recordEnd;
            }
        }
    }

    private static void MutateFirstNonTargetRecord(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var tes4Length = checked(24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(4, 4)));
        var changed = false;
        Walk(tes4Length, bytes.Length);
        Assert(changed, "fixture output has no non-target record to mutate");
        File.WriteAllBytes(path, bytes);

        void Walk(int start, int end)
        {
            var position = start;
            while (position < end && !changed)
            {
                Assert(position + 24 <= end, "mutation record header is truncated");
                var signature = Encoding.ASCII.GetString(bytes, position, 4);
                var size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                var length = signature == "GRUP"
                    ? checked((int)size)
                    : checked(24 + (int)size);
                var recordEnd = checked(position + length);
                Assert(length >= 24 && recordEnd <= end,
                    "mutation record exceeds its group");
                if (signature == "GRUP")
                    Walk(position + 24, recordEnd);
                else if (signature != "NPC_" && length > 24)
                {
                    bytes[position + 24] ^= 0x5A;
                    changed = true;
                }
                position = recordEnd;
            }
        }
    }

    private static string Hash(string path) => Convert.ToHexString(
        SHA256.HashData(File.ReadAllBytes(path)));

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record InventoryRow(
        string GroupPath,
        int Ordinal,
        string Signature,
        uint RawFormId,
        uint Flags,
        int ByteLength,
        string Sha256);
}
