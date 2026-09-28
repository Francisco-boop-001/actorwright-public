using System.Collections.Immutable;
using System.Buffers;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Assets;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Mutagen.Bethesda.Skyrim.Assets;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpFinishServiceScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-npc-finish-core-external-smp";

    public ValueTask RunAsync(CancellationToken cancellationToken) =>
        SkyrimNpcFinishCoreExternalSmpTests.RunAsync(cancellationToken);
}

internal static class SkyrimNpcFinishCoreExternalSmpTests
{
    internal static async Task RunQuestAliasPlacementAsync()
    {
        await using Program.FinishCoreBinaryFixture fixture = await Program.FinishCoreBinaryFixture.CreateAsync();
        ExternalFixture external = await ExternalFixture.CreateAsync(fixture, CancellationToken.None);
        var root = new WorkspacePath(fixture.Root);
        var service = new SkyrimNpcFinishCoreService((_, _) => ValueTask.FromResult(external.SourceResult), root,
            new BethesdaExternalHeadPartInstallVerifier());
        Sha256Hash requestHash = SkyrimNpcFinishCoreDocumentCodec.HashRequest(external.Request, root);
        var proposal = await service.AnalyzeWithInstallContextAsync(external.Request, requestHash,
            new WorkspacePath(Path.Combine(fixture.Root, "quest-finish-proposal.json")), external.CreateContext(), CancellationToken.None);
        Require(proposal.Proposed && proposal.Proposal?.Status == SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
            "Quest v2 fixture could not admit real external Finish authority: " + Format(proposal.Diagnostics));
        var applied = await service.ApplyWithInstallContextAsync(external.Request, requestHash, proposal.Proposal!,
            proposal.ProposalSha256!.Value, external.CreateContext(), CancellationToken.None);
        Require(applied.Applied && applied.Manifest?.Schema == SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier &&
            applied.Manifest.ExternalHeadParts is not null, "Quest v2 fixture did not produce a real external manifest: " + Format(applied.Diagnostics));
        string manifestPath = Path.Combine(applied.OutputRoot!.Value.Value, "NPCManager", "Evidence", "finish-core-manifest.json");
        var verified = await service.VerifyAsync(new WorkspacePath(manifestPath), HashFile(manifestPath), CancellationToken.None);
        Require(verified.Verified, "Quest v2 fixture failed independent Finish verification: " + Format(verified.Diagnostics));
        await SkyrimQuestAliasPlacementTests.AssertExternalManifestAsync(fixture.Root, manifestPath, applied.Manifest!,
            [Path.Combine(fixture.Root, "external-data", "Skyrim.esm"), Path.Combine(fixture.Root, "external-data", external.ProviderPlugin.Value),
                Path.Combine(applied.OutputRoot.Value.Value, "Data", applied.Manifest!.Plugin!.Value.Value)]);
    }

    public static async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await using Program.FinishCoreBinaryFixture fixture =
            await Program.FinishCoreBinaryFixture.CreateAsync();
        ExternalFixture external = await ExternalFixture.CreateAsync(
            fixture,
            cancellationToken);
        SkyrimMod fixtureOutput = SkyrimMod.CreateFromBinary(
            new ModPath(
                ModKey.FromNameAndExtension(external.OutputPluginName.Value),
                new FilePath(external.OutputPlugin.Value)),
            SkyrimRelease.SkyrimSE);
        Require(fixtureOutput.Npcs.Count == 1 &&
                fixtureOutput.Npcs.Single().FormKey.ID ==
                    external.Request.Actor.FormId!.Value.Value,
            "The authentic external SMP fixture output must contain exactly one requested NPC row.");

        // Round-5 RED: an authentic schema-3 selected manifest whose groups
        // disagree on ordered output masters must refuse before proposal or
        // transaction staging.
        string mixedSelectedPath = Path.Combine(
            external.Request.Source.PackageRoot!.Value.Value,
            RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                .Replace('/', Path.DirectorySeparatorChar));
        byte[] selectedBytesBeforeMixed =
            await File.ReadAllBytesAsync(mixedSelectedPath, cancellationToken);
        try
        {
            string mixedJson = Encoding.UTF8.GetString(selectedBytesBeforeMixed);
            int firstOutputPlugin = mixedJson.IndexOf(
                "\"outputPlugin\"", StringComparison.Ordinal);
            int secondOutputPlugin = mixedJson.IndexOf(
                "\"outputPlugin\"",
                firstOutputPlugin + "\"outputPlugin\"".Length,
                StringComparison.Ordinal);
            int mastersProperty = mixedJson.IndexOf(
                "\"masters\"", secondOutputPlugin, StringComparison.Ordinal);
            int mastersEnd = mixedJson.IndexOf(']', mastersProperty);
            int previousLineStart = mixedJson.LastIndexOf('\n', mastersEnd - 1) + 1;
            string itemIndent = new(
                mixedJson[previousLineStart..]
                    .TakeWhile(char.IsWhiteSpace)
                    .ToArray());
            mixedJson = mixedJson.Insert(
                mastersEnd,
                ",\n" + itemIndent + "\"MixedExternalMaster.esp\"");
            byte[] mixedSelectedBytes = Encoding.UTF8.GetBytes(mixedJson);
            using (JsonDocument mixedDocument = JsonDocument.Parse(mixedSelectedBytes))
            {
                var canonicalBuffer = new ArrayBufferWriter<byte>();
                using (var canonicalWriter = new Utf8JsonWriter(
                           canonicalBuffer,
                           new JsonWriterOptions { Indented = true }))
                {
                    canonicalWriter.WriteStartObject();
                    foreach (JsonProperty property in mixedDocument.RootElement.EnumerateObject())
                    {
                        canonicalWriter.WritePropertyName(property.Name);
                        if (property.Name == "externalInstallDependencies")
                        {
                            canonicalWriter.WriteStartArray();
                            foreach (JsonElement group in property.Value.EnumerateArray())
                            {
                                canonicalWriter.WriteStartObject();
                                foreach (JsonProperty groupProperty in group.EnumerateObject())
                                {
                                    canonicalWriter.WritePropertyName(groupProperty.Name);
                                    if (groupProperty.Name is "descriptor" or "attestation")
                                        canonicalWriter.WriteRawValue(
                                            groupProperty.Value.GetRawText(),
                                            skipInputValidation: true);
                                    else
                                        groupProperty.Value.WriteTo(canonicalWriter);
                                }
                                canonicalWriter.WriteEndObject();
                            }
                            canonicalWriter.WriteEndArray();
                        }
                        else
                        {
                            property.Value.WriteTo(canonicalWriter);
                        }
                    }
                    canonicalWriter.WriteEndObject();
                }
                mixedSelectedBytes = canonicalBuffer.WrittenSpan.ToArray();
            }
            using (JsonDocument mixedDocument = JsonDocument.Parse(mixedSelectedBytes))
            {
                string mixedJsonCanonical = Encoding.UTF8.GetString(mixedSelectedBytes);
                string mixedId = ComputeSchema3DependencyId(mixedDocument.RootElement);
                string idProperty = mixedDocument.RootElement.GetProperty("id").GetString()!;
                int idValue = mixedJsonCanonical.IndexOf(idProperty, StringComparison.Ordinal);
                mixedSelectedBytes = Encoding.UTF8.GetBytes(
                    mixedJsonCanonical.Remove(idValue, idProperty.Length)
                        .Insert(idValue, mixedId));
            }
            await File.WriteAllBytesAsync(
                mixedSelectedPath,
                mixedSelectedBytes,
                cancellationToken);
            Sha256Hash mixedSelectedHash = HashBytes(mixedSelectedBytes);
            Sha256Hash mixedTree = SkyrimNpcFinishCoreSourcePackageReader
                .ComputePackageTreeSha256(
                    new WorkspacePath(external.Request.Source.PackageRoot.Value.Value));
            SkyrimNpcFinishCoreRequest mixedRequest = external.Request with
            {
                Source = external.Request.Source with
                {
                    PackageTreeSha256 = mixedTree
                },
                Authorities = external.Request.Authorities with
                {
                    ExternalHeadParts = external.Request.Authorities.ExternalHeadParts! with
                    {
                        SelectedManifestSha256 = mixedSelectedHash
                    }
                },
                Output = external.Request.Output with
                {
                    Root = new WorkspacePath(Path.Combine(fixture.Root, "mixed-masters-output")),
                    Archive = new WorkspacePath(Path.Combine(fixture.Root, "mixed-masters-output.zip"))
                }
            };
            Sha256Hash mixedRequestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                mixedRequest,
                new WorkspacePath(fixture.Root));
            SkyrimNpcFinishCoreSourceReadResult mixedSource =
                external.SourceResult with
                {
                    PackageTreeSha256 = mixedTree
                };
            string mixedProposalPath = Path.Combine(
                fixture.Root, "mixed-masters-proposal.json");
            var mixedService = new SkyrimNpcFinishCoreService(
                (_, _) => ValueTask.FromResult(mixedSource),
                new WorkspacePath(fixture.Root),
                new BethesdaExternalHeadPartInstallVerifier());
            SkyrimNpcFinishCoreProposalResult mixed =
                await mixedService.AnalyzeWithInstallContextAsync(
                    mixedRequest,
                    mixedRequestSha,
                    new WorkspacePath(mixedProposalPath),
                    external.CreateContext(),
                    cancellationToken);
            Require(!mixed.Proposed && mixed.Diagnostics.Any(item =>
                        item.Code == ExternalHeadPartDiagnosticCodes.OutputMasterMissing) &&
                    !File.Exists(mixedProposalPath) &&
                    !Directory.Exists(mixedRequest.Output.Root!.Value.Value) &&
                    !File.Exists(mixedRequest.Output.Archive!.Value.Value) &&
                    !Directory.EnumerateDirectories(
                        fixture.Root,
                        ".finish-core-transaction-*",
                        SearchOption.TopDirectoryOnly).Any(),
                "Concrete schema-3 analyze accepted mixed ordered output masters or staged before refusal: " +
                Format(mixed.Diagnostics));
        }
        finally
        {
            await File.WriteAllBytesAsync(
                mixedSelectedPath,
                selectedBytesBeforeMixed,
                cancellationToken);
        }
        var verifier = new MatrixVerifier();
        var service = new SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(external.SourceResult),
            new WorkspacePath(fixture.Root),
            verifier);
        Sha256Hash requestSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
            external.Request,
            new WorkspacePath(fixture.Root));

        SkyrimNpcFinishCoreProposalResult noContext = await service.AnalyzeAsync(
            external.Request,
            requestSha,
            new WorkspacePath(Path.Combine(fixture.Root, "external-no-context.json")),
            cancellationToken);
        Require(noContext.Proposed && noContext.Proposal is not null &&
                noContext.Proposal.Status ==
                    SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired &&
                noContext.Proposal.ExternalHeadParts?.Verification
                    .CurrentInstallDependencyState ==
                    ExternalInstallDependencyState.DeclaredUnverified &&
                !noContext.Proposal.ExternalHeadParts.Verification.InstallReady &&
                !noContext.Proposal.RuntimeAuthority,
            "Schema-3 context-free analyze did not produce a truthful install-required proposal: " +
            Format(noContext.Diagnostics));

        var concreteService = new SkyrimNpcFinishCoreService(
            (_, _) => ValueTask.FromResult(external.SourceResult),
            new WorkspacePath(fixture.Root),
            new BethesdaExternalHeadPartInstallVerifier());

        ExternalHeadPartInstallVerificationContext context =
            external.CreateContext();
        SkyrimNpcFinishCoreProposalResult strict =
            await service.AnalyzeWithInstallContextAsync(
                external.Request,
                requestSha,
                new WorkspacePath(Path.Combine(fixture.Root, "external-strict.json")),
                context,
                cancellationToken);
        Require(strict.Proposed && strict.Proposal is not null &&
                strict.Proposal.Status ==
                    SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite &&
                strict.Proposal.ExternalHeadParts?.ContextFingerprint is not null &&
                strict.Proposal.ExternalHeadParts.Verification
                    .CurrentInstallDependencyState == ExternalInstallDependencyState.Verified &&
                strict.Proposal.ExternalHeadParts.Verification.InstallReady,
            "Exact reviewed context did not establish Finish Core install authority: " +
            Format(strict.Diagnostics));
        SkyrimNpcFinishCoreProposalResult concreteStrict =
            await concreteService.AnalyzeWithInstallContextAsync(
                external.Request,
                requestSha,
                new WorkspacePath(Path.Combine(fixture.Root, "external-concrete-strict.json")),
                context,
                cancellationToken);
        Require(concreteStrict.Proposed && concreteStrict.Proposal is not null &&
                concreteStrict.Proposal.ExternalHeadParts?.Verification
                    .CurrentInstallDependencyState == ExternalInstallDependencyState.Verified,
            "Concrete strict analyze did not establish the same install authority: " +
            Format(concreteStrict.Diagnostics));
        SkyrimNpcFinishCoreProposal proposal = concreteStrict.Proposal!;
        Sha256Hash proposalSha = concreteStrict.ProposalSha256!.Value;

        SkyrimNpcFinishCoreApplyResult contextFreeApply = await service.ApplyAsync(
            external.Request,
            requestSha,
            proposal,
            proposalSha,
            cancellationToken);
        Require(!contextFreeApply.Applied &&
                contextFreeApply.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.InstallContextAbsent),
            "External Finish Core apply without ephemeral install context was not refused.");

        verifier.Drift = true;
        SkyrimNpcFinishCoreApplyResult driftApply =
            await service.ApplyWithInstallContextAsync(
                external.Request,
                requestSha,
                proposal,
                proposalSha,
                context,
                cancellationToken);
        verifier.Drift = false;
        Require(!driftApply.Applied &&
                driftApply.Diagnostics.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.PrecheckUnavailable ||
                    item.Code == ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch) &&
                !Directory.Exists(external.Request.Output.Root!.Value.Value),
            "Provider drift after analyze was not refused before Finish staging.");

        string selectedManifestBeforeApply = Path.Combine(
            external.Request.Source.PackageRoot!.Value.Value,
            RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                .Replace('/', Path.DirectorySeparatorChar));
        byte[] selectedManifestBytesBeforeApply =
            File.ReadAllBytes(selectedManifestBeforeApply);
        await File.WriteAllBytesAsync(
            selectedManifestBeforeApply,
            selectedManifestBytesBeforeApply.Concat(new byte[] { 0x20 }).ToArray(),
            cancellationToken);
        try
        {
            SkyrimNpcFinishCoreApplyResult selectedToctou =
                await concreteService.ApplyWithInstallContextAsync(
                    external.Request,
                    requestSha,
                    proposal,
                    proposalSha,
                    context,
                    cancellationToken);
            Require(!selectedToctou.Applied &&
                    !Directory.Exists(external.Request.Output.Root!.Value.Value),
                "Selected-manifest TOCTOU was not refused before promotion.");
        }
        finally
        {
            await File.WriteAllBytesAsync(
                selectedManifestBeforeApply,
                selectedManifestBytesBeforeApply,
                cancellationToken);
        }

        string sourceSidecarPath = Path.Combine(
            external.Request.Source.PackageRoot!.Value.Value,
            "Data", "NPCManager", "FaceGeom", "analysis-sidecar.bin");
        await File.WriteAllBytesAsync(sourceSidecarPath, [0x53, 0x49, 0x44, 0x45],
            cancellationToken);
        try
        {
            SkyrimNpcFinishCoreApplyResult sourceTreeToctou =
                await concreteService.ApplyWithInstallContextAsync(
                    external.Request,
                    requestSha,
                    proposal,
                    proposalSha,
                    context,
                    cancellationToken);
            Require(!sourceTreeToctou.Applied &&
                    sourceTreeToctou.Diagnostics.Any(item =>
                        item.Code == "finish-core-apply-source-package-tree-toctou") &&
                    !Directory.Exists(external.Request.Output.Root!.Value.Value),
                "Source package mutation after analysis was not refused before promotion.");
        }
        finally
        {
            File.Delete(sourceSidecarPath);
        }

        SkyrimNpcFinishCoreApplyResult applied =
            await concreteService.ApplyWithInstallContextAsync(
                external.Request,
                requestSha,
                proposal,
                proposalSha,
                context,
                cancellationToken);
        Require(applied.Applied && applied.Manifest?.ExternalHeadParts is not null &&
                !applied.Manifest.RuntimeAuthority && !applied.Manifest.VisualAuthority,
            "Fresh strict re-verification did not publish the external Finish package: " +
            Format(applied.Diagnostics));
        string outputRoot = applied.OutputRoot!.Value.Value;
        string finishedPlugin = Path.Combine(
            outputRoot, "Data", external.OutputPluginName.Value);
        SkyrimMod finished = SkyrimMod.CreateFromBinary(
            new ModPath(
                ModKey.FromNameAndExtension(external.OutputPluginName.Value),
                new FilePath(finishedPlugin)),
            SkyrimRelease.SkyrimSE);
        Require(finished.Npcs.Single().AIData?.Mood == Mood.Angry,
            "The v3 writer did not apply the reviewed AI mood.");
        string selectedRelativePath =
            RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath;
        string selectedSourcePath = Path.Combine(
            external.Request.Source.PackageRoot!.Value.Value,
            selectedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        string selectedOutputPath = Path.Combine(
            outputRoot,
            selectedRelativePath.Replace('/', Path.DirectorySeparatorChar));
        byte[] selectedSourceBytes = File.ReadAllBytes(selectedSourcePath);
        Require(selectedSourceBytes.AsSpan().SequenceEqual(
                    File.ReadAllBytes(selectedOutputPath)),
            "Finish apply changed the canonical selected dependency manifest bytes.");
        string promotedBindingPath = Path.Combine(
            outputRoot,
            "Data",
            "NPCManager",
            "Evidence",
            "finish-core-promoted-output-binding.json");
        string[] expectedExternalEvidence =
        [
            "NPCManager/Evidence/finish-core-request.json",
            "NPCManager/Evidence/finish-core-proposal.json",
            "NPCManager/Evidence/runtime-identities.json",
            "NPCManager/Evidence/diag-" + external.Request.Actor.EditorId!.Value.Value + ".txt",
            "Data/NPCManager/Evidence/selected-preset-dependencies.json",
            "Data/NPCManager/Evidence/finish-core-promoted-output-binding.json"
        ];
        Require(applied.Manifest!.Evidence.Files.Select(item => item.Path.Value)
                    .SequenceEqual(expectedExternalEvidence),
            "External Finish manifest evidence was not the exact canonical six-file ordered set.");
        string[] expectedPhysicalEvidence = expectedExternalEvidence
            .Concat(
            [
                "NPCManager/Evidence/finish-core-manifest.json",
                "NPCManager/Evidence/finish-core-verification.json"
            ])
            .Order(StringComparer.Ordinal)
            .ToArray();
        string[] actualPhysicalEvidence = Directory.EnumerateFiles(
                outputRoot, "*", SearchOption.AllDirectories)
            .Select(path => Path.GetRelativePath(outputRoot, path).Replace('\\', '/'))
            .Where(path => path.StartsWith("NPCManager/Evidence/", StringComparison.Ordinal) ||
                          path.StartsWith("Data/NPCManager/Evidence/", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal)
            .ToArray();
        Require(actualPhysicalEvidence.SequenceEqual(expectedPhysicalEvidence),
            "External Finish physical evidence was not the exact eight-file closure.");
        string extraPhysicalEvidence = Path.Combine(
            outputRoot, "NPCManager", "Evidence", "unexpected-extra.txt");
        await File.WriteAllTextAsync(extraPhysicalEvidence, "extra evidence",
            cancellationToken);
        string outputManifestPath = Path.Combine(
            outputRoot, "NPCManager", "Evidence", "finish-core-manifest.json");
        SkyrimNpcFinishCoreVerificationResult extraPhysicalVerification =
            await concreteService.VerifyAsync(
                new WorkspacePath(outputManifestPath),
                HashFile(outputManifestPath),
                cancellationToken);
        Require(!extraPhysicalVerification.Verified &&
                extraPhysicalVerification.Diagnostics.Any(item =>
                    item.Code == "finish-core-verify-evidence-set"),
            "External Finish verify accepted an unlisted physical evidence file.");
        File.Delete(extraPhysicalEvidence);
        Require(File.Exists(promotedBindingPath) &&
                File.ReadAllBytes(promotedBindingPath).Length > 0,
            "Finish apply did not emit the promoted output binding artifact.");
        string outputPluginPath = Path.Combine(
            outputRoot,
            "Data",
            external.OutputPluginName.Value);
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding promotedBinding =
            SkyrimNpcFinishCorePromotedOutputBindingCodec.Parse(
                File.ReadAllBytes(promotedBindingPath));
        RaceMenuSelectedDependencyManifestReadResult selectedOutput =
            await new RaceMenuSelectedDependencyManifestReader().ReadAsync(
                new WorkspacePath(selectedOutputPath),
                HashFile(selectedOutputPath),
                new WorkspacePath(outputRoot),
                cancellationToken);
        Require(promotedBinding.SourceSelectedManifestSha256 ==
                    HashFile(selectedSourcePath) &&
                promotedBinding.OutputPluginSha256 == HashFile(outputPluginPath),
            "The promoted binding did not retain Hsrc and bind the promoted output hash.");

        // Round-4 RED: the root PNAM binding is an ordered sequence.  A
        // multiset/count comparison must reject this same-members/different-
        // order mutation even when two groups share one provider.
        FormReference secondExternalPnam = new(
            external.ProviderPlugin, new FormId(0x801));
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding secondGroup =
            promotedBinding.Groups[0] with
            {
                DescriptorId = HashBytes(Encoding.UTF8.GetBytes("second-descriptor")),
                AttestationSha256 = HashBytes(Encoding.UTF8.GetBytes("second-attestation")),
                DeclaredExternalPnam = [secondExternalPnam]
            };
        ImmutableArray<SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding>
            twoGroups = [promotedBinding.Groups[0], secondGroup];
        twoGroups = twoGroups
            .OrderBy(item => item.DescriptorId.Value, StringComparer.Ordinal)
            .ToImmutableArray();
        var twoGroupBinding = promotedBinding with
        {
            Groups = twoGroups,
            DeclaredExternalPnam = twoGroups
                .SelectMany(item => item.DeclaredExternalPnam)
                .ToImmutableArray()
        };
        RequireInvalidData(
            () => SkyrimNpcFinishCorePromotedOutputBindingCodec.Serialize(
                twoGroupBinding with
                {
                    DeclaredExternalPnam = twoGroupBinding.DeclaredExternalPnam
                        .Reverse().ToImmutableArray()
                }),
            "Promoted output binding accepted a reordered root PNAM sequence.");

        ExternalHeadPartInstallVerificationResult concreteOutput =
            await new BethesdaExternalHeadPartInstallVerifier()
                .VerifyPromotedOutputAsync(
                new ExternalHeadPartPromotedOutputVerificationRequest(
                    new WorkspacePath(outputRoot),
                    new WorkspacePath(outputPluginPath),
                    external.OutputPluginName,
                    HashFile(outputPluginPath),
                    new WorkspacePath(selectedOutputPath),
                    HashFile(selectedSourcePath),
                    new WorkspacePath(promotedBindingPath),
                    HashFile(promotedBindingPath),
                    context,
                    true,
                    TargetActorFormId: external.Request.Actor.FormId),
                cancellationToken);
        Require(concreteOutput.DescriptorClosureValid &&
                concreteOutput.Artifact.CurrentInstallDependencyState ==
                    ExternalInstallDependencyState.Verified,
            "Concrete strict verification did not bind the promoted output plugin to selected schema-3 authority: " +
            Format(concreteOutput.Diagnostics));
        async ValueTask<bool> VerifyBindingVariantAsync(
            string name,
            SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding variant)
        {
            string variantPath = Path.Combine(
                outputRoot, "Data", "NPCManager", "Evidence", name);
            try
            {
                byte[] variantBytes = SkyrimNpcFinishCorePromotedOutputBindingCodec.Serialize(
                    variant);
                await File.WriteAllBytesAsync(variantPath, variantBytes, cancellationToken);
                ExternalHeadPartInstallVerificationResult result =
                    await new BethesdaExternalHeadPartInstallVerifier()
                    .VerifyPromotedOutputAsync(
                        new ExternalHeadPartPromotedOutputVerificationRequest(
                            new WorkspacePath(outputRoot),
                            new WorkspacePath(outputPluginPath),
                            external.OutputPluginName,
                            HashFile(outputPluginPath),
                            new WorkspacePath(selectedOutputPath),
                            HashFile(selectedOutputPath),
                            new WorkspacePath(variantPath),
                            HashBytes(variantBytes),
                            context,
                            true,
                            TargetActorFormId: external.Request.Actor.FormId),
                    cancellationToken);
                return result.DescriptorClosureValid;
            }
            catch (InvalidDataException)
            {
                return false;
            }
            finally
            {
                File.Delete(variantPath);
            }
        }

        bool reorderedGroups =
            await VerifyBindingVariantAsync(
                "finish-core-promoted-output-binding-reordered.json",
                promotedBinding with
                {
                    Groups = promotedBinding.Groups.Reverse().ToImmutableArray()
                });
        Require(!reorderedGroups,
            "Concrete promoted verification accepted reordered external groups.");
        bool missingGroup =
            await VerifyBindingVariantAsync(
                "finish-core-promoted-output-binding-missing.json",
                promotedBinding with
                {
                    Groups = promotedBinding.Groups.RemoveAt(1),
                    DeclaredExternalPnam = promotedBinding.Groups[0].DeclaredExternalPnam
                });
        Require(!missingGroup,
            "Concrete promoted verification accepted a missing external group.");
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputGroupBinding extraGroup =
            promotedBinding.Groups[0] with
            {
                DescriptorId = HashBytes(Encoding.UTF8.GetBytes("extra-group-id")),
                AttestationSha256 = HashBytes(Encoding.UTF8.GetBytes("extra-group-attestation"))
            };
        bool extraGroupResult =
            await VerifyBindingVariantAsync(
                "finish-core-promoted-output-binding-extra.json",
                promotedBinding with
                {
                    Groups = promotedBinding.Groups.Add(extraGroup)
                });
        Require(!extraGroupResult,
            "Concrete promoted verification accepted an extra external group.");
        bool substitutedDescriptor =
            await VerifyBindingVariantAsync(
                "finish-core-promoted-output-binding-id.json",
                promotedBinding with
                {
                    Groups = promotedBinding.Groups.SetItem(
                        0,
                        promotedBinding.Groups[0] with
                        {
                            DescriptorId = HashBytes(
                                Encoding.UTF8.GetBytes("substituted-group-id"))
                        })
                });
        Require(!substitutedDescriptor,
            "Concrete promoted verification accepted a substituted external group ID.");
        bool substitutedGroupAttestation =
            await VerifyBindingVariantAsync(
                "finish-core-promoted-output-binding-attestation.json",
                promotedBinding with
                {
                    Groups = promotedBinding.Groups.SetItem(
                        1,
                        promotedBinding.Groups[1] with
                        {
                            AttestationSha256 = HashBytes(
                                Encoding.UTF8.GetBytes("substituted-group-attestation"))
                        })
                });
        Require(!substitutedGroupAttestation,
            "Concrete promoted verification accepted a substituted per-group attestation.");
        bool substitutedGroupPnam =
            await VerifyBindingVariantAsync(
                "finish-core-promoted-output-binding-pnam.json",
                promotedBinding with
                {
                    Groups = promotedBinding.Groups.SetItem(
                        1,
                        promotedBinding.Groups[1] with
                        {
                            DeclaredExternalPnam = [
                                new FormReference(
                                    external.ProviderPlugin,
                                    new FormId(0x8FF))]
                        })
                });
        Require(!substitutedGroupPnam,
            "Concrete promoted verification accepted a substituted per-group PNAM sequence.");
        ExternalHeadPartInstallVerificationResult missingTargetBoundary =
            await new BethesdaExternalHeadPartInstallVerifier()
                .VerifyPromotedOutputAsync(
                    new ExternalHeadPartPromotedOutputVerificationRequest(
                        new WorkspacePath(outputRoot),
                        new WorkspacePath(outputPluginPath),
                        external.OutputPluginName,
                        HashFile(outputPluginPath),
                        new WorkspacePath(selectedOutputPath),
                        HashFile(selectedSourcePath),
                        new WorkspacePath(promotedBindingPath),
                        HashFile(promotedBindingPath),
                        context,
                        true,
                        TargetActorFormId: null),
                    cancellationToken);
        Require(!missingTargetBoundary.DescriptorClosureValid &&
                missingTargetBoundary.Diagnostics.Any(item =>
                    item.Message.Contains("target actor", StringComparison.OrdinalIgnoreCase)),
            "Promoted verification did not return a bounded diagnostic for a missing target actor identity.");
        ExternalHeadPartInstallVerificationResult invalidHashBoundary =
            await new BethesdaExternalHeadPartInstallVerifier()
                .VerifyPromotedOutputAsync(
                    new ExternalHeadPartPromotedOutputVerificationRequest(
                        new WorkspacePath(outputRoot),
                        new WorkspacePath(outputPluginPath),
                        external.OutputPluginName,
                        HashFile(outputPluginPath),
                        new WorkspacePath(selectedOutputPath),
                        HashFile(selectedSourcePath),
                        new WorkspacePath(promotedBindingPath),
                        default,
                        context,
                        true,
                        TargetActorFormId: external.Request.Actor.FormId),
                    cancellationToken);
        Require(!invalidHashBoundary.DescriptorClosureValid &&
                invalidHashBoundary.Diagnostics.Any(item =>
                    item.Message.Contains("SHA", StringComparison.OrdinalIgnoreCase)),
            "Promoted verification did not return a bounded diagnostic for a default binding hash.");

        // The promoted binding is closed authority, not a projection supplied by
        // the caller.  A substituted attestation must therefore fail even when
        // the attacker recomputes the binding artifact hash.
        string substitutedBindingPath = Path.Combine(
            outputRoot, "Data", "NPCManager", "Evidence",
            "finish-core-promoted-output-binding-substituted.json");
        SkyrimNpcFinishCoreExternalHeadPartPromotedOutputBinding substitutedBinding =
            promotedBinding with
            {
                Groups = promotedBinding.Groups.SetItem(
                    0,
                    promotedBinding.Groups[0] with
                    {
                        AttestationSha256 = HashBytes(
                            Encoding.UTF8.GetBytes("substituted-attestation"))
                    })
            };
        await File.WriteAllBytesAsync(
            substitutedBindingPath,
            SkyrimNpcFinishCorePromotedOutputBindingCodec.Serialize(substitutedBinding),
            cancellationToken);
        ExternalHeadPartInstallVerificationResult substitutedOutput =
            await new BethesdaExternalHeadPartInstallVerifier()
                .VerifyPromotedOutputAsync(
                    new ExternalHeadPartPromotedOutputVerificationRequest(
                        new WorkspacePath(outputRoot),
                        new WorkspacePath(outputPluginPath),
                        external.OutputPluginName,
                        HashFile(outputPluginPath),
                        new WorkspacePath(selectedOutputPath),
                        HashFile(selectedSourcePath),
                        new WorkspacePath(substitutedBindingPath),
                        HashFile(substitutedBindingPath),
                        context,
                        true,
                        TargetActorFormId: external.Request.Actor.FormId),
                    cancellationToken);
        Require(!substitutedOutput.DescriptorClosureValid &&
                substitutedOutput.Diagnostics.Any(item =>
                    item.Message.Contains("attestation", StringComparison.OrdinalIgnoreCase)),
            "Concrete promoted verification accepted an Hbind attestation substitution.");
        File.Delete(substitutedBindingPath);
        string manifestPath = Path.Combine(
            outputRoot, "NPCManager", "Evidence", "finish-core-manifest.json");
        byte[] originalManifestBytes = File.ReadAllBytes(manifestPath);
        Sha256Hash manifestOnlySourceMutation = HashBytes(
            Encoding.UTF8.GetBytes("manifest-only-source-tree-mutation"));
        SkyrimNpcFinishCoreManifest mutatedSourceManifest = applied.Manifest! with
        {
            SourcePackageTreeSha256 = manifestOnlySourceMutation,
            Evidence = applied.Manifest.Evidence with
            {
                SourcePackageTreeSha256 = manifestOnlySourceMutation
            }
        };
        byte[] mutatedSourceManifestBytes =
            SkyrimNpcFinishCoreDocumentCodec.SerializeManifest(
                mutatedSourceManifest,
                new WorkspacePath(fixture.Root));
        await File.WriteAllBytesAsync(
            manifestPath,
            mutatedSourceManifestBytes,
            cancellationToken);
        try
        {
            SkyrimNpcFinishCoreVerificationResult mutatedSourceManifestResult =
                await concreteService.VerifyAsync(
                    new WorkspacePath(manifestPath),
                    HashBytes(mutatedSourceManifestBytes),
                    cancellationToken);
            Require(!mutatedSourceManifestResult.Verified &&
                    mutatedSourceManifestResult.Diagnostics.Any(item =>
                        item.Code == "finish-core-verify-document-binding"),
                "External later verify accepted a manifest-only Hsource mutation.");
        }
        finally
        {
            await File.WriteAllBytesAsync(
                manifestPath,
                originalManifestBytes,
                cancellationToken);
        }
        Sha256Hash manifestSha = HashFile(manifestPath);
        string unavailableSourceRoot = outputRoot + ".source-unavailable";
        Directory.Move(external.Request.Source.PackageRoot!.Value.Value,
            unavailableSourceRoot);
        var sourceUnavailableService = new SkyrimNpcFinishCoreService(
            (_, _) => throw new InvalidOperationException(
                "source package inspection must not run during later verify"),
            new WorkspacePath(fixture.Root),
            new BethesdaExternalHeadPartInstallVerifier());
        SkyrimNpcFinishCoreVerificationResult historical;
        SkyrimNpcFinishCoreVerificationResult current;
        SkyrimNpcFinishCoreVerificationResult concreteLater;
        try
        {
            historical = await sourceUnavailableService.VerifyAsync(
                new WorkspacePath(manifestPath), manifestSha, cancellationToken);
            current = await sourceUnavailableService.VerifyWithInstallContextAsync(
                new WorkspacePath(manifestPath),
                manifestSha,
                context,
                cancellationToken);
            concreteLater = await sourceUnavailableService.VerifyWithInstallContextAsync(
                new WorkspacePath(manifestPath),
                manifestSha,
                context,
                cancellationToken);
            string retainedSourceManifest = Path.Combine(outputRoot,
                "NPCManager", "finish-core-source-package-manifest.json");
            byte[] retainedSourceBytes = File.ReadAllBytes(retainedSourceManifest);
            Require(HashBytes(retainedSourceBytes) == external.Request.Source.PackageManifestSha256,
                "External Finish did not retain the exact request-bound raw source manifest.");
            try
            {
                await File.WriteAllBytesAsync(retainedSourceManifest,
                    retainedSourceBytes.Concat(new byte[] { 0x20 }).ToArray(), cancellationToken);
                SkyrimNpcFinishCoreVerificationResult alteredProvenance =
                    await sourceUnavailableService.VerifyWithInstallContextAsync(
                        new WorkspacePath(manifestPath), manifestSha, context, cancellationToken);
                Require(!alteredProvenance.Verified && alteredProvenance.Diagnostics.Any(item =>
                        item.Code == "finish-core-verify-package-tree"),
                    "Source-independent external verification accepted changed retained provenance.");
                File.Delete(retainedSourceManifest);
                SkyrimNpcFinishCoreVerificationResult missingProvenance =
                    await sourceUnavailableService.VerifyWithInstallContextAsync(
                        new WorkspacePath(manifestPath), manifestSha, context, cancellationToken);
                Require(!missingProvenance.Verified && missingProvenance.Diagnostics.Any(item =>
                        item.Code == "finish-core-verify-package-tree"),
                    "Source-independent external verification accepted missing retained provenance.");
            }
            finally
            {
                await File.WriteAllBytesAsync(retainedSourceManifest, retainedSourceBytes, cancellationToken);
            }
        }
        finally
        {
            Directory.Move(unavailableSourceRoot,
                external.Request.Source.PackageRoot!.Value.Value);
        }
        string packageRequestPath = Path.Combine(
            outputRoot, "NPCManager", "Evidence", "finish-core-request.json");
        byte[] packageRequestBytes = File.ReadAllBytes(packageRequestPath);
        await File.WriteAllBytesAsync(
            packageRequestPath,
            packageRequestBytes.Concat(new byte[] { 0x20 }).ToArray(),
            cancellationToken);
        try
        {
            SkyrimNpcFinishCoreVerificationResult mutatedRequest =
                await sourceUnavailableService.VerifyAsync(
                    new WorkspacePath(manifestPath), manifestSha, cancellationToken);
            Require(!mutatedRequest.Verified,
                "Later verify accepted a mutated embedded request document.");
        }
        finally
        {
            await File.WriteAllBytesAsync(packageRequestPath, packageRequestBytes,
                cancellationToken);
        }
        byte[] promotedBindingBytes = File.ReadAllBytes(promotedBindingPath);
        await File.WriteAllBytesAsync(
            promotedBindingPath,
            promotedBindingBytes.Concat(new byte[] { 0x20 }).ToArray(),
            cancellationToken);
        try
        {
            SkyrimNpcFinishCoreVerificationResult mutatedBinding =
                await sourceUnavailableService.VerifyAsync(
                    new WorkspacePath(manifestPath), manifestSha, cancellationToken);
            Require(!mutatedBinding.Verified,
                "Later verify accepted a mutated promoted output binding.");
        }
        finally
        {
            await File.WriteAllBytesAsync(promotedBindingPath, promotedBindingBytes,
                cancellationToken);
        }
        Require(historical.Verified && historical.Verification is not null &&
                historical.Verification.Status ==
                    SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired &&
                historical.Verification.ExternalHeadParts?.Verification
                    .CurrentInstallDependencyState ==
                    ExternalInstallDependencyState.DeclaredUnverified &&
                historical.Verification.ExternalHeadParts.Verification
                    .HistoricalSnapshotValid == true &&
                !historical.Verification.RuntimeAuthority &&
                !historical.Verification.VisualAuthority,
            "Later context-free verify incorrectly treated the historical snapshot as current: " +
            Format(historical.Diagnostics));
        Require(current.Verified && current.Verification is not null &&
                current.Verification.Status ==
                    SkyrimNpcFinishCoreStatus.StaticPassRuntimeRequired &&
                current.Verification.ExternalHeadParts?.Verification
                    .CurrentInstallDependencyState == ExternalInstallDependencyState.Verified &&
                current.Verification.ExternalHeadParts.Verification.InstallReady &&
                !current.Verification.RuntimeAuthority &&
                !current.Verification.VisualAuthority,
            "Exact later context did not re-establish current install authority: " +
            Format(current.Diagnostics));

        Require(historical.Verification!.ArchiveSha256 is null &&
                current.Verification!.ArchiveSha256 is null,
            "Finish Core external verification introduced an unauthoritative physical archive hash.");

        Require(concreteLater.Verified && concreteLater.Verification is not null &&
                concreteLater.Verification.ExternalHeadParts?.Verification
                    .CurrentInstallDependencyState == ExternalInstallDependencyState.Verified,
            "Later Finish verify did not use the promoted output plugin with the concrete strict verifier: " +
            Format(concreteLater.Diagnostics));

        var artifactStore = new SkyrimNpcFinishCoreVerificationArtifactStore(
            new KOnlyWorkspacePolicy(
                new WorkspacePath(fixture.Root),
                new WorkspacePath(@"F:\ExampleGame")),
            new WorkspacePath(fixture.Root));
        SkyrimNpcFinishCoreVerifiedManifestDocument verifiedManifest =
            artifactStore.LoadManifest(
                new WorkspacePath(manifestPath),
                manifestSha.Value.ToUpperInvariant());
        WorkspacePath independentVerification = new(Path.Combine(
            fixture.Root, "external-independent-verification.json"));
        SkyrimNpcFinishCoreVerificationArtifactDocument persisted =
            await artifactStore.WriteNewAsync(
                current.Verification!,
                verifiedManifest,
                requestSha.Value.ToUpperInvariant(),
                independentVerification,
                cancellationToken);
        Require(persisted.Artifact.SchemaOrMediaType ==
                    SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier &&
                persisted.Verification.ExternalHeadParts?.Verification
                    .HistoricalSnapshotValid == true,
            "External Finish verification artifact persistence lost v2 snapshot authority.");
        SkyrimNpcFinishCoreVerificationArtifactDocument reopenedPersisted =
            artifactStore.Load(
                independentVerification,
                persisted.Sha256,
                verifiedManifest,
                requestSha.Value.ToUpperInvariant());
        Require(reopenedPersisted.Verification.ExternalHeadParts?.Verification
                    .CurrentInstallDependencyState ==
                ExternalInstallDependencyState.Verified,
            "External Finish verification artifact did not survive strict reopen.");

        string providerName = external.ProviderPlugin.Value;
        using (ZipArchive archive = ZipFile.OpenRead(applied.Archive!.Value.Value))
        {
            Require(archive.Entries.All(entry =>
                    !string.Equals(Path.GetFileName(entry.FullName), providerName,
                        StringComparison.OrdinalIgnoreCase)),
                "The Finish archive contains external provider bytes.");
        }

        string providerInPackage = Path.Combine(
            external.Request.Source.PackageRoot!.Value.Value,
            "Data", providerName);
        string providerAssetInPackage = Path.Combine(
            external.Request.Source.PackageRoot!.Value.Value,
            "Data",
            external.Descriptor.Assets[0].Path.Value.Replace(
                '/', Path.DirectorySeparatorChar));
        string providerArchiveInPackage = Path.Combine(
            external.Request.Source.PackageRoot!.Value.Value,
            "Data", "provider-nested.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(providerAssetInPackage)!);
        await File.WriteAllBytesAsync(providerInPackage, [1, 2, 3], cancellationToken);
        await File.WriteAllBytesAsync(providerAssetInPackage, [4, 5, 6], cancellationToken);
        using (ZipArchive providerArchive = ZipFile.Open(
                   providerArchiveInPackage, ZipArchiveMode.Create))
        {
            ZipArchiveEntry nested = providerArchive.CreateEntry("nested/inner.zip");
            await using MemoryStream innerBytes = new();
            using (ZipArchive inner = new(innerBytes, ZipArchiveMode.Create, true))
            {
                ZipArchiveEntry member = inner.CreateEntry(
                    external.Descriptor.Assets[0].Path.Value.Replace('\\', '/'));
                await using Stream stream = member.Open();
                await stream.WriteAsync(new byte[] { 7, 8, 9 }, cancellationToken);
            }
            innerBytes.Position = 0;
            await innerBytes.CopyToAsync(nested.Open(), cancellationToken);
        }
        try
        {
            SkyrimNpcFinishCoreRequest contaminated = external.Request with
            {
                Source = external.Request.Source with
                {
                    PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader
                        .ComputePackageTreeSha256(new WorkspacePath(
                            external.Request.Source.PackageRoot!.Value.Value))
                },
                Output = external.Request.Output with
                {
                    Root = new WorkspacePath(Path.Combine(fixture.Root, "contaminated-output")),
                    Archive = new WorkspacePath(Path.Combine(fixture.Root, "contaminated-output.zip"))
                }
            };
            Sha256Hash contaminatedSha = SkyrimNpcFinishCoreDocumentCodec.HashRequest(
                contaminated, new WorkspacePath(fixture.Root));
            SkyrimNpcFinishCoreProposalResult contaminatedProposal =
                await service.AnalyzeWithInstallContextAsync(
                    contaminated,
                    contaminatedSha,
                    new WorkspacePath(Path.Combine(fixture.Root, "contaminated.json")),
                    context,
                    cancellationToken);
            Require(contaminatedProposal.Proposed && contaminatedProposal.Proposal is not null,
                "The provider-byte contamination setup did not reach Finish apply authorization.");
            SkyrimNpcFinishCoreApplyResult contaminatedApply =
                await service.ApplyWithInstallContextAsync(
                    contaminated,
                    contaminatedSha,
                    contaminatedProposal.Proposal!,
                    contaminatedProposal.ProposalSha256!.Value,
                    context,
                    cancellationToken);
            Require(!contaminatedApply.Applied &&
                    contaminatedApply.Diagnostics.Any(item =>
                        item.Message.Contains("provider bytes", StringComparison.OrdinalIgnoreCase)) &&
                    !Directory.Exists(contaminated.Output.Root!.Value.Value),
                "Finish did not refuse a source package containing provider bytes.");
        }
        finally
        {
            File.Delete(providerInPackage);
            File.Delete(providerAssetInPackage);
            File.Delete(providerArchiveInPackage);
        }

        ImmutableArray<Diagnostic> missingClosure =
            BethesdaSkyrimNpcFinishCoreVerifier.VerifyExternalHeadPartOutputClosure(
                external.MissingClosurePlugin,
                external.OutputPluginName,
                external.Request.Actor.FormId!.Value,
                [new ExternalHeadPartInstallProviderObservation(
                    external.ProviderPlugin,
                    external.ProviderPluginSha256,
                     null,
                     null)],
                external.Descriptors.SelectMany(item => item.Members)
                    .ToImmutableArray(),
                cancellationToken);
        Require(missingClosure.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.OutputMasterMissing ||
                    item.Code == ExternalHeadPartDiagnosticCodes.OutputReferenceMissing),
            "The output master/PNAM closure regression did not identify a missing external binding.");

        ImmutableArray<
            RaceMenuSelectedDependencyManifestExternalInstallDependency> selectedGroups =
            selectedOutput.Artifact!.ExternalInstallDependencies;
        ImmutableArray<PluginName> expectedExternalMasters =
            selectedGroups[0].OutputPlugin.Masters;
        ImmutableArray<FormReference> expectedExternalPnam = selectedGroups
            .SelectMany(item => item.OutputPlugin.PnamBindings)
            .ToImmutableArray();
        ImmutableArray<Diagnostic> exactClosure =
            BethesdaSkyrimNpcFinishCoreVerifier.VerifyExternalHeadPartOutputClosure(
                new WorkspacePath(outputPluginPath),
                external.OutputPluginName,
                external.Request.Actor.FormId!.Value,
                [new ExternalHeadPartInstallProviderObservation(
                    external.ProviderPlugin,
                    external.ProviderPluginSha256,
                    null,
                    null)],
                external.Descriptors.SelectMany(item => item.Members)
                    .ToImmutableArray(),
                cancellationToken,
                expectedExternalMasters,
                expectedExternalPnam);
        Require(!exactClosure.Any(item => item.Severity == DiagnosticSeverity.Error),
            "The promoted output did not satisfy its exact master and PNAM binding.");

        string extraPnamPath = Path.Combine(fixture.Root, "extra-pnam.esp");
        File.Copy(outputPluginPath, extraPnamPath, overwrite: true);
        SkyrimMod extraPnam = SkyrimMod.CreateFromBinary(
            new ModPath(
                ModKey.FromNameAndExtension(external.OutputPluginName.Value),
                new FilePath(extraPnamPath)),
            SkyrimRelease.SkyrimSE);
        extraPnam.Npcs.Single(item => item.FormKey.ID ==
                external.Request.Actor.FormId!.Value.Value)
            .HeadParts.Add(new FormLink<IHeadPartGetter>(
                new FormKey(ModKey.FromNameAndExtension(
                    external.ProviderPlugin.Value), 0x800)));
        Program.WriteFinishCoreBinaryPlugin(extraPnam, extraPnamPath);
        ImmutableArray<Diagnostic> extraClosure =
            BethesdaSkyrimNpcFinishCoreVerifier.VerifyExternalHeadPartOutputClosure(
                new WorkspacePath(extraPnamPath),
                external.OutputPluginName,
                external.Request.Actor.FormId!.Value,
                [new ExternalHeadPartInstallProviderObservation(
                    external.ProviderPlugin,
                    external.ProviderPluginSha256,
                    null,
                    null)],
                external.Descriptors.SelectMany(item => item.Members)
                    .ToImmutableArray(),
                cancellationToken,
                expectedExternalMasters,
                expectedExternalPnam);
        Require(extraClosure.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.OutputReferenceMissing),
            "The exact closure regression accepted an extra PNAM reference.");

        string misorderedMastersPath = Path.Combine(fixture.Root, "misordered-masters.esp");
        File.Copy(outputPluginPath, misorderedMastersPath, overwrite: true);
        SkyrimMod misorderedMasters = SkyrimMod.CreateFromBinary(
            new ModPath(
                ModKey.FromNameAndExtension(external.OutputPluginName.Value),
                new FilePath(misorderedMastersPath)),
            SkyrimRelease.SkyrimSE);
        var masterReferences = misorderedMasters.ModHeader.MasterReferences
            .ToArray();
        misorderedMasters.ModHeader.MasterReferences.Clear();
        foreach (MasterReference reference in masterReferences.Reverse())
            misorderedMasters.ModHeader.MasterReferences.Add(reference);
        Program.WriteFinishCoreBinaryPlugin(misorderedMasters, misorderedMastersPath);
        ImmutableArray<Diagnostic> misorderedClosure =
            BethesdaSkyrimNpcFinishCoreVerifier.VerifyExternalHeadPartOutputClosure(
                new WorkspacePath(misorderedMastersPath),
                external.OutputPluginName,
                external.Request.Actor.FormId!.Value,
                [new ExternalHeadPartInstallProviderObservation(
                    external.ProviderPlugin,
                    external.ProviderPluginSha256,
                    null,
                    null)],
                external.Descriptors.SelectMany(item => item.Members)
                    .ToImmutableArray(),
                cancellationToken,
                expectedExternalMasters,
                expectedExternalPnam);
        Require(misorderedClosure.Any(item =>
                    item.Code == ExternalHeadPartDiagnosticCodes.OutputMasterMissing),
            "The exact closure regression accepted misordered output masters.");
    }

    private sealed class MatrixVerifier : IExternalHeadPartInstallVerifier,
        IExternalHeadPartPromotedOutputVerifier
    {
        public bool Drift { get; set; }

        public ValueTask<ExternalHeadPartInstallVerificationResult>
            VerifyPromotedOutputAsync(
                ExternalHeadPartPromotedOutputVerificationRequest request,
                CancellationToken cancellationToken) =>
            new BethesdaExternalHeadPartInstallVerifier()
                .VerifyPromotedOutputAsync(request, cancellationToken);

        public async ValueTask<ExternalHeadPartInstallVerificationResult> VerifyAsync(
            ExternalHeadPartInstallVerificationRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExternalHeadPartInstallVerificationResult result =
                await new BethesdaExternalHeadPartInstallVerifier()
                    .VerifyAsync(request, cancellationToken);
            if (!Drift || request.Context is null)
                return result;
            ExternalHeadPartInstallVerificationArtifact artifact = result.Artifact with
            {
                HistoricalSnapshotValid = null,
                VerifiedInstallSnapshot = null,
                CurrentInstallDependencyState = ExternalInstallDependencyState.DeclaredUnverified,
                InstallReady = false,
                InstallDependencyAuthority = false
            };
            return result with
            {
                Artifact = artifact,
                Diagnostics = result.Diagnostics.Add(new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.ContextFingerprintMismatch,
                    DiagnosticSeverity.Error,
                    "The external provider fingerprint drifted after analyze.") )
            };
        }
    }

    private sealed record ExternalFixture(
        SkyrimNpcFinishCoreRequest Request,
        SkyrimNpcFinishCoreSourceReadResult SourceResult,
        ExternalHeadPartDependencyDescriptor Descriptor,
        ExternalHeadPartDependencyDescriptor Descriptor2,
        PluginName ProviderPlugin,
        Sha256Hash ProviderPluginSha256,
        long ProviderPluginByteLength,
        WorkspacePath OutputPlugin,
        WorkspacePath MissingClosurePlugin,
        PluginName OutputPluginName)
    {
        public ImmutableArray<ExternalHeadPartDependencyDescriptor> Descriptors =>
            [Descriptor, Descriptor2];

        public ExternalHeadPartInstallVerificationContext CreateContext() =>
            new(
                new WorkspacePath(Path.Combine(
                    Request.Source.PackageRoot!.Value.Value,
                    "..", "external-data")),
                [new PluginName("Skyrim.esm"), ProviderPlugin],
                new FormReference(ProviderPlugin, new FormId(0x900)));

        public static async ValueTask<ExternalFixture> CreateAsync(
            Program.FinishCoreBinaryFixture fixture,
            CancellationToken cancellationToken)
        {
            PluginName provider = new("ExternalProvider.esp");
            ModKey providerKey = ModKey.FromNameAndExtension(provider.Value);
            string missingClosurePlugin = Path.Combine(fixture.Root, "missing-closure.esp");
            File.Copy(fixture.SourcePlugin.Value, missingClosurePlugin, overwrite: true);
            SkyrimMod source = SkyrimMod.CreateFromBinary(
                new ModPath(
                    fixture.PluginKey,
                    new FilePath(fixture.SourcePlugin.Value)),
                SkyrimRelease.SkyrimSE);
            source.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = providerKey
            });
            Npc sourceActor = source.Npcs.Single(item => item.FormKey.ID ==
                Program.FinishCoreBinaryFixture.ActorFormId.Value);
            sourceActor.HeadParts.Add(new FormLink<IHeadPartGetter>(
                new FormKey(providerKey, 0x800)));
            // Keep an ordinary PNAM interleaved between the two external
            // provider references so the promoted binding must preserve the
            // ordered external subsequence rather than aggregate every PNAM.
            sourceActor.HeadParts.Add(new FormLink<IHeadPartGetter>(
                new FormKey(ModKey.FromNameAndExtension("Skyrim.esm"), 0x123)));
            sourceActor.HeadParts.Add(new FormLink<IHeadPartGetter>(
                new FormKey(providerKey, 0x801)));
            Program.WriteFinishCoreBinaryPlugin(source, fixture.SourcePlugin.Value);

            string externalDataRoot = Path.Combine(fixture.Root, "external-data");
            Directory.CreateDirectory(externalDataRoot);
            string externalMasterPath = Path.Combine(externalDataRoot, "Skyrim.esm");
            File.Copy(fixture.CopiedMaster.Value, externalMasterPath, overwrite: true);
            string providerPath = Path.Combine(externalDataRoot, provider.Value);
            string modelRelative = "meshes/actors/character/hair/external.nif";
            string modelRelative2 = "meshes/actors/character/hair/external-two.nif";
            var providerMod = new SkyrimMod(providerKey, SkyrimRelease.SkyrimSE);
            providerMod.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension("Skyrim.esm")
            });
            providerMod.HeadParts.Add(new HeadPart(
                new FormKey(providerKey, 0x800), SkyrimRelease.SkyrimSE)
            {
                EditorID = "ExternalHairRoot",
                Name = "External hair root",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.Hair,
                Model = new Model
                {
                    File = new AssetLink<SkyrimModelAssetType>(modelRelative)
                }
            });
            providerMod.HeadParts.Add(new HeadPart(
                new FormKey(providerKey, 0x801), SkyrimRelease.SkyrimSE)
            {
                EditorID = "ExternalHairSecond",
                Name = "External hair second",
                Flags = HeadPart.Flag.Male | HeadPart.Flag.Female,
                Type = HeadPart.TypeEnum.Hair,
                Model = new Model
                {
                    File = new AssetLink<SkyrimModelAssetType>(modelRelative2)
                }
            });
            providerMod.Races.Add(new Race(
                new FormKey(providerKey, 0x900), SkyrimRelease.SkyrimSE)
            {
                EditorID = "ExternalHairRace",
                HeadData = new GenderedItem<HeadData?>(new HeadData(), new HeadData())
            });
            Program.WriteFinishCoreBinaryPlugin(providerMod, providerPath);
            byte[] providerBytes = await File.ReadAllBytesAsync(
                providerPath, cancellationToken);
            Sha256Hash providerHash = HashBytes(providerBytes);
            string xmlRelative = "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/direct.xml";
            string triRelative = "meshes/actors/character/hair/external.tri";
            string modelPath = Path.Combine(
                externalDataRoot,
                modelRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(modelPath)!);
            byte[] modelBytes = Preview254ExternalSmpPhysicsFixtureFactory
                .BuildProviderNifForTests(
                    null,
                    triRelative);
            await File.WriteAllBytesAsync(modelPath, modelBytes, cancellationToken);
            string triRelative2 = "meshes/actors/character/hair/external-two.tri";
            byte[] modelBytes2 = Preview254ExternalSmpPhysicsFixtureFactory
                .BuildProviderNifForTests(
                    null,
                    triRelative2);
            string modelPath2 = Path.Combine(
                externalDataRoot,
                modelRelative2.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(modelPath2, modelBytes2, cancellationToken);
            string triPath = Path.Combine(
                externalDataRoot,
                triRelative.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(triPath, [0x54, 0x52, 0x49], cancellationToken);
            string triPath2 = Path.Combine(
                externalDataRoot,
                triRelative2.Replace('/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(triPath2, [0x54, 0x52, 0x49, 0x32], cancellationToken);
            string xmlPath = Path.Combine(
                externalDataRoot,
                xmlRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(xmlPath)!);
            byte[] xmlBytes = Encoding.UTF8.GetBytes(
                Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(
                    new AssetPath("meshes/actors/character/hair/external-collider.nif")));
            await File.WriteAllBytesAsync(xmlPath, xmlBytes, cancellationToken);
            string defaultBbpRelative =
                "SKSE/Plugins/hdtSkinnedMeshConfigs/defaultBBPs.xml";
            string defaultBbpPath = Path.Combine(
                externalDataRoot,
                defaultBbpRelative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(defaultBbpPath)!);
            byte[] defaultBbpBytes = Encoding.UTF8.GetBytes(
                $"<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"{xmlRelative}\" />" +
                $"<map shape=\"HairCollisionShape\" file=\"{xmlRelative}\" /></defaultBBPs>");
            await File.WriteAllBytesAsync(defaultBbpPath, defaultBbpBytes, cancellationToken);
            string colliderPath = Path.Combine(
                externalDataRoot,
                "meshes/actors/character/hair/external-collider.nif".Replace(
                    '/', Path.DirectorySeparatorChar));
            await File.WriteAllBytesAsync(colliderPath, [0x43, 0x4F, 0x4C, 0x4C], cancellationToken);

            string faceGeomPath = Path.Combine(
                fixture.Root,
                "source",
                "Data",
                "NPCManager",
                "FaceGeom",
                "external.nif");
            Directory.CreateDirectory(Path.GetDirectoryName(faceGeomPath)!);
            byte[] faceGeomBytes = [0x4E, 0x49, 0x46, 0x2D, 0x4F, 0x55, 0x54];
            await File.WriteAllBytesAsync(faceGeomPath, faceGeomBytes, cancellationToken);
            string faceGeomPath2 = Path.Combine(
                fixture.Root,
                "source",
                "Data",
                "NPCManager",
                "FaceGeom",
                "external-two.nif");
            byte[] faceGeomBytes2 = [0x4E, 0x49, 0x46, 0x2D, 0x54, 0x57, 0x4F];
            await File.WriteAllBytesAsync(faceGeomPath2, faceGeomBytes2, cancellationToken);
            FormReference rootForm = new(provider, new FormId(0x800));
            var routePolicy = new KOnlyWorkspacePolicy(
                new WorkspacePath(fixture.Root),
                new WorkspacePath(@"F:\ExampleGame"));
            SkyrimFaceRecordRouteResult route = await
                new BethesdaSkyrimFaceRecordRouteResolver(
                    routePolicy, new WorkspacePath(fixture.Root)).ResolveAsync(
                    new SkyrimFaceRecordRouteRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new FormReference(provider, new FormId(0x900)),
                        NpcSex.Female,
                        [new SkyrimFaceRecordHeadPartSelection(rootForm, [])],
                        [new SkyrimFaceRecordPluginAuthority(
                            provider,
                            new WorkspacePath(providerPath),
                            providerHash)]),
                    cancellationToken);
            Require(route.Accepted && route.Route is not null,
                "The concrete provider route fixture was refused: " +
                Format(route.Diagnostics));
            FormReference rootForm2 = new(provider, new FormId(0x801));
            SkyrimFaceRecordRouteResult route2 = await
                new BethesdaSkyrimFaceRecordRouteResolver(
                    routePolicy, new WorkspacePath(fixture.Root)).ResolveAsync(
                    new SkyrimFaceRecordRouteRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new FormReference(provider, new FormId(0x900)),
                        NpcSex.Female,
                        [new SkyrimFaceRecordHeadPartSelection(rootForm2, [])],
                        [new SkyrimFaceRecordPluginAuthority(
                            provider,
                            new WorkspacePath(providerPath),
                            providerHash)]),
                    cancellationToken);
            Require(route2.Accepted && route2.Route is not null,
                "The second concrete provider route fixture was refused: " +
                Format(route2.Diagnostics));
            SkyrimFaceHeadPartGraphRoute routeMember = route.Route!
                .HeadPartGraph.Single();
            ExternalHeadPartRecordDependency member = new(
                routeMember.OriginForm,
                routeMember.RequiredOutputMaster,
                routeMember.WinningForm,
                routeMember.WinningPlugin,
                routeMember.WinningPluginSha256,
                routeMember.WinningPluginByteLength,
                routeMember.WinningRecordSha256,
                routeMember.EditorId,
                routeMember.DeclaredType,
                routeMember.EffectiveType,
                routeMember.ModelNif,
                routeMember.TriRoutes,
                routeMember.HnamEdges,
                routeMember.Parent,
                routeMember.Depth,
                routeMember.RouteOrder,
                routeMember.AppliesToSex,
                routeMember.ValidRace);
            SkyrimFaceHeadPartGraphRoute routeMember2 = route2.Route!
                .HeadPartGraph.Single();
            ExternalHeadPartRecordDependency member2 = new(
                routeMember2.OriginForm,
                routeMember2.RequiredOutputMaster,
                routeMember2.WinningForm,
                routeMember2.WinningPlugin,
                routeMember2.WinningPluginSha256,
                routeMember2.WinningPluginByteLength,
                routeMember2.WinningRecordSha256,
                routeMember2.EditorId,
                routeMember2.DeclaredType,
                routeMember2.EffectiveType,
                routeMember2.ModelNif,
                routeMember2.TriRoutes,
                routeMember2.HnamEdges,
                routeMember2.Parent,
                routeMember2.Depth,
                routeMember2.RouteOrder,
                routeMember2.AppliesToSex,
                routeMember2.ValidRace);
            ExternalHeadPartDependencyDescriptor descriptorDraft =
                new(
                    ExternalHeadPartSchemaIdentifiers.Descriptor,
                    HashBytes(Encoding.UTF8.GetBytes("descriptor-placeholder")),
                    ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
                    rootForm,
                    rootForm,
                    NpcHeadPartType.Hair,
                    HashBytes(Encoding.UTF8.GetBytes("graph")),
                    new ExternalHeadPartProviderIdentity(
                        provider,
                        providerHash,
                        providerBytes.LongLength,
                        ExternalHeadPartRedistributionMode.ExternalProviderRequired),
                    [member],
                    new ExternalHeadPartPhysicsBinding(
                        ExternalHeadPartPhysicsBindingMode.DefaultBbpMap,
                        [
                            new ExternalHeadPartPhysicsShapeBinding(
                                rootForm,
                                new AssetPath(modelRelative),
                                "HairPhysicsShape",
                                new AssetPath(xmlRelative),
                                HashBytes(xmlBytes),
                                xmlBytes.LongLength),
                            new ExternalHeadPartPhysicsShapeBinding(
                                rootForm,
                                new AssetPath(modelRelative),
                                "HairCollisionShape",
                                new AssetPath(xmlRelative),
                                HashBytes(xmlBytes),
                                xmlBytes.LongLength)
                        ],
                        new ExternalHeadPartPhysicsMappingAuthority(
                            new AssetPath(defaultBbpRelative),
                            HashBytes(defaultBbpBytes),
                            defaultBbpBytes.LongLength)),
                    [new ExternalHeadPartAssetDependency(
                        new AssetPath(modelRelative),
                        HashBytes(modelBytes),
                        modelBytes.LongLength,
                        provider,
                        providerHash,
                        null)],
                    [new ExternalHeadPartRuntimePrerequisite(
                        "skse-plugin", "FSMP present", "SKSE/Plugins/hdtSMP64.dll")]);
            ExternalHeadPartDependencyDescriptor descriptor = descriptorDraft with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(descriptorDraft)
            };
            ExternalHeadPartDependencyDescriptor descriptorDraft2 =
                descriptorDraft with
                {
                    DescriptorId = HashBytes(Encoding.UTF8.GetBytes("descriptor-two-placeholder")),
                    RootSourceForm = rootForm2,
                    RootWinningForm = rootForm2,
                    GraphSha256 = HashBytes(Encoding.UTF8.GetBytes("graph-two")),
                    Members = [member2],
                    Physics = new ExternalHeadPartPhysicsBinding(
                        ExternalHeadPartPhysicsBindingMode.DefaultBbpMap,
                        [
                            new ExternalHeadPartPhysicsShapeBinding(
                                rootForm2,
                                new AssetPath(modelRelative2),
                                "HairPhysicsShape",
                                new AssetPath(xmlRelative),
                                HashBytes(xmlBytes),
                                xmlBytes.LongLength),
                            new ExternalHeadPartPhysicsShapeBinding(
                                rootForm2,
                                new AssetPath(modelRelative2),
                                "HairCollisionShape",
                                new AssetPath(xmlRelative),
                                HashBytes(xmlBytes),
                                xmlBytes.LongLength)
                        ],
                        new ExternalHeadPartPhysicsMappingAuthority(
                            new AssetPath(defaultBbpRelative),
                            HashBytes(defaultBbpBytes),
                            defaultBbpBytes.LongLength)),
                    Assets = [new ExternalHeadPartAssetDependency(
                        new AssetPath(modelRelative2),
                        HashBytes(modelBytes2),
                        modelBytes2.LongLength,
                        provider,
                        providerHash,
                        null)]
                };
            ExternalHeadPartDependencyDescriptor descriptor2 = descriptorDraft2 with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(descriptorDraft2)
            };
            Require(ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(
                        ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(
                            descriptor)).Physics.Mode ==
                    ExternalHeadPartPhysicsBindingMode.DefaultBbpMap,
                "The schema-3 fixture descriptor lost its default-BBP physics mode before writing.");
            ExternalHeadPartFaceGeomExclusionAttestation attestationDraft =
                new(
                    ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
                    HashBytes(Encoding.UTF8.GetBytes("attestation-placeholder")),
                    descriptor.DescriptorId,
                    new AssetPath("Data/NPCManager/FaceGeom/external.nif"),
                    HashBytes(faceGeomBytes),
                    faceGeomBytes.LongLength,
                    [],
                    [new ExternalHeadPartExcludedShapeEvidence(
                        new AssetPath(modelRelative), "ExternalHairRoot")],
                    [new ExternalHeadPartExcludedMetadataEvidence(
                        "physics-locator", "HDT Skinned Mesh Physics Object")],
                    "preview254-exclusion-v1");
            ExternalHeadPartFaceGeomExclusionAttestation attestation =
                attestationDraft with
                {
                    AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                        .ComputeAttestationHash(attestationDraft)
                };
            ExternalHeadPartFaceGeomExclusionAttestation attestationDraft2 =
                new(
                    ExternalHeadPartSchemaIdentifiers.FaceGeomExclusion,
                    HashBytes(Encoding.UTF8.GetBytes("attestation-two-placeholder")),
                    descriptor2.DescriptorId,
                    new AssetPath("Data/NPCManager/FaceGeom/external-two.nif"),
                    HashBytes(faceGeomBytes2),
                    faceGeomBytes2.LongLength,
                    [],
                    [new ExternalHeadPartExcludedShapeEvidence(
                        new AssetPath(modelRelative2), "ExternalHairSecond")],
                    [new ExternalHeadPartExcludedMetadataEvidence(
                        "physics-locator", "HDT Skinned Mesh Physics Object")],
                    "preview254-exclusion-v1");
            ExternalHeadPartFaceGeomExclusionAttestation attestation2 =
                attestationDraft2 with
                {
                    AttestationSha256 = ExternalHeadPartDependencyDescriptorCodec
                        .ComputeAttestationHash(attestationDraft2)
                };
            // The selected-manifest writer canonically orders the external
            // groups by descriptor ID.  Match that order in the target actor's
            // external PNAM subsequence while retaining an ordinary interleave.
            sourceActor.HeadParts.Clear();
            bool ordinaryInserted = false;
            foreach (FormReference form in new[]
                         { (descriptor.DescriptorId, Form: rootForm),
                           (descriptor2.DescriptorId, Form: rootForm2) }
                         .OrderBy(item => item.DescriptorId.Value,
                             StringComparer.Ordinal)
                         .Select(item => item.Form))
            {
                sourceActor.HeadParts.Add(new FormLink<IHeadPartGetter>(
                    new FormKey(
                        ModKey.FromNameAndExtension(form.Plugin.Value),
                        form.FormId.Value)));
                if (!ordinaryInserted)
                {
                    sourceActor.HeadParts.Add(new FormLink<IHeadPartGetter>(
                        new FormKey(
                            ModKey.FromNameAndExtension("Skyrim.esm"), 0x123)));
                    ordinaryInserted = true;
                }
            }
            Program.WriteFinishCoreBinaryPlugin(source, fixture.SourcePlugin.Value);
            Sha256Hash sourcePluginHash = HashFile(fixture.SourcePlugin.Value);
            PluginName outputPlugin = fixture.Proposal.Request!.Source.Plugin!.Value;
            var outputBinding = new RaceMenuSelectedDependencyManifestOutputPluginBinding(
                outputPlugin,
                sourcePluginHash,
                new FileInfo(fixture.SourcePlugin.Value).Length,
                [new PluginName("Skyrim.esm"), provider],
                [rootForm]);
            var outputBinding2 = outputBinding with
            {
                PnamBindings = [rootForm2]
            };
            var group = new RaceMenuSelectedDependencyManifestExternalInstallDependency(
                descriptor,
                attestation,
                outputBinding,
                new AssetPath("Data/NPCManager/FaceGeom/external.nif"),
                HashBytes(faceGeomBytes),
                faceGeomBytes.LongLength);
            var group2 = new RaceMenuSelectedDependencyManifestExternalInstallDependency(
                descriptor2,
                attestation2,
                outputBinding2,
                new AssetPath("Data/NPCManager/FaceGeom/external-two.nif"),
                HashBytes(faceGeomBytes2),
                faceGeomBytes2.LongLength);
            string selectedPath = Path.Combine(
                fixture.Root,
                "source",
                RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath
                    .Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(selectedPath)!);
            var policy = new KOnlyWorkspacePolicy(
                new WorkspacePath(fixture.Root),
                new WorkspacePath(@"F:\ExampleGame"));
            var providerAuthority = new SkyrimFaceRecordPluginAuthority(
                provider, new WorkspacePath(providerPath), providerHash);
            var binding = new RaceMenuNpcFormBinding(
                new RecordSignature("HDPT"),
                rootForm,
                rootForm,
                provider,
                new WorkspacePath(providerPath),
                providerHash,
                NpcHeadPartType.Hair);
            var binding2 = new RaceMenuNpcFormBinding(
                new RecordSignature("HDPT"),
                rootForm2,
                rootForm2,
                provider,
                new WorkspacePath(providerPath),
                providerHash,
                NpcHeadPartType.Hair);
            var orderedExternalBindings = new[]
                { (descriptor.DescriptorId, new SkyrimNpcFinishCoreExternalHeadPartBinding(
                    descriptor.DescriptorId, attestation.AttestationSha256)),
                  (descriptor2.DescriptorId, new SkyrimNpcFinishCoreExternalHeadPartBinding(
                      descriptor2.DescriptorId, attestation2.AttestationSha256)) }
                .OrderBy(item => item.DescriptorId.Value, StringComparer.Ordinal)
                .Select(item => item.Item2)
                .ToImmutableArray();
            Sha256Hash presetHash = HashBytes(Encoding.UTF8.GetBytes("external-smp-preset"));
            var preset = new PresetDocument(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                null!,
                presetHash,
                []);
            var draft = new RaceMenuPresetRecordAuthorityDraft(
                "external-smp-test",
                preset,
                new RaceMenuPresetTarget(
                    "external-smp-test",
                    new FormReference(new PluginName("Skyrim.esm"), new FormId(0x13746)),
                    NpcSex.Female,
                    new WorkspacePath(externalDataRoot),
                    [providerAuthority]),
                binding,
                [
                    new RaceMenuPresetHeadPartAuthority(
                        new PresetHeadPart(PresetIdentifier.Parse(rootForm.ToString()), 0),
                        binding),
                    new RaceMenuPresetHeadPartAuthority(
                        new PresetHeadPart(PresetIdentifier.Parse(rootForm2.ToString()), 1),
                        binding2)
                ],
                binding,
                null!,
                0,
                new FormId(0x900),
                null!,
                false);
            SkyrimAssetAuthority asset = new(
                provider.Value,
                AssetProviderKind.Loose,
                new WorkspacePath(modelPath),
                HashBytes(modelBytes),
                new AssetPath(modelRelative),
                modelBytes.LongLength,
                HashBytes(modelBytes));
            SkyrimAssetAuthority asset2 = new(
                provider.Value,
                AssetProviderKind.Loose,
                new WorkspacePath(modelPath2),
                HashBytes(modelBytes2),
                new AssetPath(modelRelative2),
                modelBytes2.LongLength,
                HashBytes(modelBytes2));
            RaceMenuSelectedDependencyManifestWriteResult selectedWrite =
                await new RaceMenuSelectedDependencyManifestWriter(
                        policy,
                        new WorkspacePath(fixture.Root))
                    .WriteAsync(
                        new RaceMenuSelectedDependencyManifestWriteRequest(
                            presetHash,
                            draft,
                             [asset, asset2],
                             [],
                             new WorkspacePath(selectedPath))
                         {
                             ExternalInstallDependencies = [group, group2]
                         },
                        cancellationToken);
            Require(selectedWrite.Written,
                "Authentic schema-3 selected manifest setup failed: " +
                Format(selectedWrite.Diagnostics));
            Sha256Hash selectedHash = HashFile(selectedPath);
            SkyrimNpcFinishCoreRequest baseRequest = fixture.Proposal.Request!;
            SkyrimNpcFinishCoreSource sourceRequest = baseRequest.Source with
            {
                PluginSha256 = sourcePluginHash,
                PackageTreeSha256 = SkyrimNpcFinishCoreSourcePackageReader
                    .ComputePackageTreeSha256(new WorkspacePath(
                        Path.Combine(fixture.Root, "source")))
            };
            SkyrimNpcFinishCoreRequest request = baseRequest with
            {
                Schema = SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
                Source = sourceRequest,
                AiPolicy = baseRequest.AiPolicy! with
                {
                    Mood = SkyrimNpcFinishCoreMood.Angry
                },
                Authorities = baseRequest.Authorities with
                {
                    ExternalHeadParts = new SkyrimNpcFinishCoreExternalHeadPartAuthority(
                        new AssetPath(RaceMenuSelectedDependencyManifestPaths.CanonicalRelativePath),
                        selectedHash,
                        orderedExternalBindings)
                }
            };
            SkyrimNpcFinishCorePluginSnapshot snapshot =
                BethesdaSkyrimNpcFinishCoreSourceReader.Inspect(
                    fixture.SourcePlugin,
                    outputPlugin,
                    Program.FinishCoreBinaryFixture.ActorFormId,
                    request.Actor.EditorId!.Value,
                    cancellationToken);
            var sourceResult = new SkyrimNpcFinishCoreSourceReadResult(
                true,
                null,
                request.Source.PackageTreeSha256!.Value,
                sourcePluginHash,
                snapshot.BaseNpc,
                snapshot.TargetEditorId,
                true,
                "None",
                snapshot.TypedForbiddenCounts,
                snapshot.RawForbiddenCounts,
                snapshot.Diagnostics)
            {
                NextFormId = new FormId(snapshot.NextFormId),
                Tes4Flags = snapshot.Tes4Flags,
                MasterOrder = snapshot.MasterOrder,
                OccupiedIds = snapshot.OccupiedIds,
                TargetConfigurationFlags = snapshot.TargetConfigurationFlags,
                FactionRanks = snapshot.FactionRanks,
                CombatStyle = snapshot.CombatStyle,
                CombatStyleMatchesDefensiveContract = snapshot.CombatStyleMatchesDefensiveContract,
                DefaultOutfit = snapshot.DefaultOutfit,
                Inventory = snapshot.Inventory,
                PackageLinks = snapshot.PackageLinks,
                Relationships = snapshot.Relationships,
                AiData = snapshot.AiData,
                SemanticSurfaceValues = []
            };
            return new ExternalFixture(
                request,
                sourceResult,
                descriptor,
                descriptor2,
                provider,
                providerHash,
                providerBytes.LongLength,
                fixture.SourcePlugin,
                new WorkspacePath(missingClosurePlugin),
                new PluginName(fixture.PluginKey.FileName.ToString()));
        }
    }

    private static string Format(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => item.Code + ":" + item.Message));

    private static string ComputeSchema3DependencyId(JsonElement root)
    {
        static string CanonicalArray(IEnumerable<string> values) =>
            string.Concat(values.Select(value => value.Length + ":" + value));

        string HeadPartIdentity(JsonElement item) =>
            $"{item.GetProperty("formKey").GetString()}:{item.GetProperty("type").GetString()}:" +
            $"{item.GetProperty("pluginEvidence").GetString()}:{item.GetProperty("pluginSha256").GetString()}";

        var dependencyValues = new List<(string Path, string Value)>();
        foreach (JsonElement item in root.GetProperty("looseAssets").EnumerateArray())
        {
            string path = item.GetProperty("gamePath").GetString()!;
            dependencyValues.Add((path,
                $"{path}:{item.GetProperty("provider").GetString()}:" +
                $"{item.GetProperty("sha256").GetString()}:{item.GetProperty("byteLength").GetInt64()}"));
        }
        foreach (JsonElement archive in root.GetProperty("archives").EnumerateArray())
        {
            string provider = archive.GetProperty("provider").GetString()!;
            string providerSha = archive.GetProperty("sha256").GetString()!;
            long providerLength = archive.GetProperty("byteLength").GetInt64();
            foreach (JsonElement member in archive.GetProperty("members").EnumerateArray())
            {
                string path = member.GetProperty("gamePath").GetString()!;
                dependencyValues.Add((path,
                    $"{path}:{provider}:{providerSha}:{providerLength}:" +
                    $"{member.GetProperty("sha256").GetString()}:{member.GetProperty("byteLength").GetInt64()}"));
            }
        }
        string dependencies = string.Join(';', dependencyValues
            .OrderBy(item => item.Path, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path, StringComparer.Ordinal)
            .Select(item => item.Value));
        string sidecars = root.TryGetProperty(
                "externalProviderSidecars", out JsonElement sidecarArray)
            ? string.Join(';', sidecarArray.EnumerateArray().Select(item =>
                $"{item.GetProperty("gamePath").GetString()}:{item.GetProperty("provider").GetString()}:" +
                $"{item.GetProperty("sha256").GetString()}:{item.GetProperty("byteLength").GetInt64()}"))
            : string.Empty;
        string externalGroups = string.Join(';', root
            .GetProperty("externalInstallDependencies")
            .EnumerateArray()
            .OrderBy(item => item.GetProperty("descriptorId").GetString(),
                StringComparer.Ordinal)
            .Select(item =>
            {
                JsonElement output = item.GetProperty("outputPlugin");
                string descriptor = Convert.ToHexString(Encoding.UTF8.GetBytes(
                    item.GetProperty("descriptor").GetRawText()));
                string attestation = Convert.ToHexString(Encoding.UTF8.GetBytes(
                    item.GetProperty("attestation").GetRawText()));
                string masters = CanonicalArray(output.GetProperty("masters")
                    .EnumerateArray().Select(master => master.GetString()!));
                string pnam = CanonicalArray(output.GetProperty("pnam")
                    .EnumerateArray().Select(reference => reference.GetString()!));
                JsonElement faceGeom = item.GetProperty("faceGeom");
                return string.Join(':', descriptor, attestation,
                    output.GetProperty("plugin").GetString(),
                    output.GetProperty("sha256").GetString(),
                    output.GetProperty("byteLength").GetInt64(),
                    masters,
                    pnam,
                    faceGeom.GetProperty("path").GetString(),
                    faceGeom.GetProperty("sha256").GetString(),
                    faceGeom.GetProperty("byteLength").GetInt64());
            }));
        string identity = string.Join('|',
            "schema3",
            root.GetProperty("presetSha256").GetString(),
            string.Join(';', root.GetProperty("headParts").EnumerateArray()
                .Select(HeadPartIdentity)),
            dependencies,
            sidecars,
            externalGroups);
        string hash = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(identity)));
        return "selected-preset-dependencies-" + hash[..24].ToLowerInvariant();
    }

    private static Sha256Hash HashBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static Sha256Hash HashFile(string path) =>
        HashBytes(File.ReadAllBytes(path));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }

    private static void RequireInvalidData(Action action, string message)
    {
        try
        {
            action();
        }
        catch (InvalidDataException)
        {
            return;
        }
        throw new InvalidOperationException(message);
    }
}
