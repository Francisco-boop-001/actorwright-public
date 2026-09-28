using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Exceptions;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record BethesdaSkyrimNpcFinishCoreVerification(
    bool Verified,
    ImmutableDictionary<string, int> TypedForbiddenCounts,
    ImmutableDictionary<string, int> RawForbiddenCounts,
    Sha256Hash ProtectedNpcSubrecordsSha256,
    Sha256Hash SourceSidecarTreeSha256,
    Sha256Hash OutputSidecarTreeSha256,
    ImmutableArray<Diagnostic> Diagnostics);

/// <summary>
/// Reopens source and output independently. The writer is never called from
/// this verifier; typed overlay and raw-byte checks must agree on the graph.
/// </summary>
public sealed partial class BethesdaSkyrimNpcFinishCoreVerifier
{
    private readonly record struct RawRecordIdentity(
        string Signature,
        uint RawFormId,
        string GroupPath);

    private readonly record struct RawGroupIdentity(
        int Depth,
        string Label,
        string Path,
        int Type,
        uint RawLabel);

    /// <summary>
    /// Reopens the Finish output after the ordinary writer has completed and
    /// checks the external record-only dependency closure. Provider bytes are
    /// never read from or copied into the Finish package by this check.
    /// </summary>
    public static ImmutableArray<Diagnostic> VerifyExternalHeadPartOutputClosure(
        WorkspacePath outputPlugin,
        PluginName outputPluginName,
        FormId targetFormId,
        ImmutableArray<ExternalHeadPartInstallProviderObservation> providers,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        CancellationToken cancellationToken,
        ImmutableArray<PluginName> expectedMasterOrder = default,
        ImmutableArray<FormReference> expectedPnamSequence = default)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(outputPlugin.Value) ||
                Directory.Exists(outputPlugin.Value))
            {
                diagnostics.Add(new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    DiagnosticSeverity.Error,
                    "The Finish output plugin is missing for external head-part closure verification."));
                return diagnostics.ToImmutable();
            }

            SkyrimMod output = SkyrimMod.CreateFromBinary(
                new ModPath(
                    ModKey.FromNameAndExtension(outputPluginName.Value),
                    new FilePath(outputPlugin.Value)),
                SkyrimRelease.SkyrimSE);
            HashSet<string> masters = output.ModHeader.MasterReferences
                .Select(item => item.Master.ToString())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (ExternalHeadPartInstallProviderObservation provider in providers)
            {
                if (!masters.Contains(provider.ProviderPlugin.Value))
                    diagnostics.Add(new Diagnostic(
                        ExternalHeadPartDiagnosticCodes.OutputMasterMissing,
                        DiagnosticSeverity.Error,
                        $"Output plugin is missing declared external provider master '{provider.ProviderPlugin.Value}'."));
            }
            if (!expectedMasterOrder.IsDefault &&
                !output.ModHeader.MasterReferences
                    .Select(item => new PluginName(item.Master.ToString()))
                    .SequenceEqual(expectedMasterOrder))
            {
                diagnostics.Add(new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.OutputMasterMissing,
                    DiagnosticSeverity.Error,
                    "Output plugin master order differs from the selected external binding."));
            }

            FormKey target = new(
                ModKey.FromNameAndExtension(outputPluginName.Value),
                targetFormId.Value);
            Npc[] npcs = output.Npcs
                .Where(item => item.FormKey == target)
                .ToArray();
            if (npcs.Length != 1)
            {
                diagnostics.Add(new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    DiagnosticSeverity.Error,
                    $"Output plugin must contain exactly one target NPC for external PNAM closure; observed={npcs.Length}."));
                return diagnostics.ToImmutable();
            }

            HashSet<FormKey> outputHeadParts = npcs[0].HeadParts
                .Select(item => item.FormKey)
                .ToHashSet();
            ImmutableArray<FormReference> actualPnam = npcs[0].HeadParts.Select(item =>
                    new FormReference(
                        new PluginName(item.FormKey.ModKey.ToString()),
                        new FormId(item.FormKey.ID)))
                .ToImmutableArray();
            HashSet<string> externalPlugins = providers
                .Select(item => item.ProviderPlugin.Value)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            externalPlugins.UnionWith(members.Select(item => item.WinningForm.Plugin.Value));
            ImmutableArray<FormReference> actualExternalPnam = actualPnam
                .Where(item => externalPlugins.Contains(item.Plugin.Value))
                .ToImmutableArray();
            if (!expectedPnamSequence.IsDefault &&
                !actualExternalPnam.SequenceEqual(expectedPnamSequence))
            {
                diagnostics.Add(new Diagnostic(
                    ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                    DiagnosticSeverity.Error,
                    "Output PNAM sequence differs from the selected external binding."));
            }
            foreach (ExternalHeadPartRecordDependency member in members)
            {
                FormKey winning = ToFormKey(member.WinningForm);
                if (!outputHeadParts.Contains(winning))
                    diagnostics.Add(new Diagnostic(
                        ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                        DiagnosticSeverity.Error,
                        $"Output PNAM is missing declared external HDPT '{member.WinningForm}'."));
            }
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or
                ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic(
                ExternalHeadPartDiagnosticCodes.OutputReferenceMissing,
                DiagnosticSeverity.Error,
                exception.Message));
        }
        return diagnostics.ToImmutable();
    }

    public BethesdaSkyrimNpcFinishCoreVerification Verify(
        WorkspacePath sourcePlugin,
        WorkspacePath outputPlugin,
        SkyrimNpcFinishCoreProposal proposal,
        WorkspacePath copiedMaster,
        CancellationToken cancellationToken)
    {
        _ = GetType();
        ArgumentNullException.ThrowIfNull(proposal);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ImmutableDictionary<string, int> empty =
            BethesdaSkyrimNpcFinishCoreSourceReader.ForbiddenSignatures
                .ToImmutableDictionary(value => value, _ => 0, StringComparer.Ordinal);
        try
        {
            if (!File.Exists(sourcePlugin.Value) || Directory.Exists(sourcePlugin.Value))
                return Failure("finish-core-verify-source", "The source plugin is missing.", empty, empty, diagnostics);
            if (!File.Exists(outputPlugin.Value) || Directory.Exists(outputPlugin.Value))
                return Failure("finish-core-verify-output", "The output plugin is missing.", empty, empty, diagnostics);
            cancellationToken.ThrowIfCancellationRequested();
            byte[] sourceBytes = File.ReadAllBytes(sourcePlugin.Value);
            byte[] outputBytes = File.ReadAllBytes(outputPlugin.Value);
            Sha256Hash sourceHash = Hash(sourceBytes);
            if (proposal.Request?.Source.PluginSha256 is not { } expectedSource ||
                sourceHash != expectedSource)
                return Failure("finish-core-verify-source-hash", "The source hash is not the proposal binding.", empty, empty, diagnostics);
            uint targetId = proposal.Request.Actor.FormId!.Value.Value & 0x00FF_FFFFu;
            string targetIdentity = $"NPC_ {new FormId(targetId)}";
            int sourceNpcCount = BethesdaSkyrimNpcFinishCoreRaw.CountSelfRecords(
                sourceBytes, "NPC_", targetId);
            if (sourceNpcCount != 1)
            {
                AddExactlyOneDiagnostic("finish-core-verify-source-npc-count",
                    targetIdentity, sourceNpcCount, diagnostics);
                return Failure(empty, empty, diagnostics);
            }
            int outputNpcCount = BethesdaSkyrimNpcFinishCoreRaw.CountSelfRecords(
                outputBytes, "NPC_", targetId);
            if (outputNpcCount != 1)
            {
                AddExactlyOneDiagnostic("finish-core-verify-output-npc-count",
                    targetIdentity, outputNpcCount, diagnostics);
                return Failure(empty, empty, diagnostics);
            }
            bool allocationsValid = true;
            allocationsValid &= TryPreflightAllocation(
                proposal, outputBytes, "CSTY", "finish-core-verify-csty-count",
                diagnostics, out uint? cstyId);
            allocationsValid &= TryPreflightAllocation(
                proposal, outputBytes, "OTFT", "finish-core-verify-otft-count",
                diagnostics, out uint? outfitId);
            allocationsValid &= TryPreflightAllocation(
                proposal, outputBytes, "PACK", "finish-core-verify-pack-count",
                diagnostics, out uint? packageId);
            if (!allocationsValid)
                return Failure(empty, empty, diagnostics);
            BethesdaSkyrimNpcFinishCoreRaw.RawCensus sourceCensus =
                BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(sourceBytes);
            BethesdaSkyrimNpcFinishCoreRaw.RawCensus outputCensus =
                BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(outputBytes);
            SkyrimMod source = SkyrimMod.CreateFromBinary(
                new ModPath(ModKey.FromNameAndExtension(proposal.Request.Source.Plugin!.Value.Value),
                    new FilePath(sourcePlugin.Value)), SkyrimRelease.SkyrimSE);
            Npc[] sourceNpcs = source.Npcs
                .Where(row => row.FormKey.ModKey == source.ModKey && row.FormKey.ID == targetId)
                .ToArray();
            if (!RequireExactlyOne(sourceNpcs, "finish-core-verify-source-npc-count",
                    targetIdentity, diagnostics, out Npc? sourceNpc))
                return Failure(empty, empty, diagnostics);
            byte[] rebasedSourceBytes = BethesdaSkyrimNpcFinishCoreReindex.Rebase(
                sourceBytes, source, proposal.MasterOrder);
            var rebasedSourceCensus = ReferenceEquals(sourceBytes, rebasedSourceBytes)
                ? sourceCensus : BethesdaSkyrimNpcFinishCoreRaw.ReadCensus(rebasedSourceBytes);
            // Reject malformed numeric group ownership before the typed output
            // reader can throw while attaching children to their parent record.
            if (!VerifyRecordGraph(
                    outputBytes, rebasedSourceCensus, outputCensus, proposal,
                    sourceNpc!, diagnostics))
                return Failure(empty, empty, diagnostics);
            SkyrimMod output = SkyrimMod.CreateFromBinary(
                new ModPath(ModKey.FromNameAndExtension(proposal.Request.Source.Plugin!.Value.Value),
                    new FilePath(outputPlugin.Value)), SkyrimRelease.SkyrimSE);
            Npc[] outputNpcs = output.Npcs
                .Where(row => row.FormKey.ModKey == output.ModKey && row.FormKey.ID == targetId)
                .ToArray();
            if (!RequireExactlyOne(outputNpcs, "finish-core-verify-output-npc-count",
                    targetIdentity, diagnostics, out Npc? outputNpc))
                return Failure(empty, empty, diagnostics);
            VerifyRetainedFormLinks(source, output, sourceNpc!, outputNpc!, proposal,
                cstyId, outfitId, packageId, diagnostics);
            BethesdaSkyrimNpcFinishCoreRaw.RawPluginSnapshot sourceRaw =
                BethesdaSkyrimNpcFinishCoreRaw.Read(sourceBytes, targetId, sourceCensus);
            BethesdaSkyrimNpcFinishCoreRaw.RawPluginSnapshot outputRaw =
                BethesdaSkyrimNpcFinishCoreRaw.Read(outputBytes, targetId, outputCensus);
            ImmutableDictionary<string, int> rawCounts = CountRaw(outputRaw);
            ImmutableDictionary<string, int> typedCounts = CountTyped(output);
            if (sourceRaw.Target is null)
            {
                AddExactlyOneDiagnostic("finish-core-verify-source-npc-count",
                    targetIdentity, 0, diagnostics);
                return Failure(typedCounts, rawCounts, diagnostics);
            }
            if (outputRaw.Target is null)
            {
                AddExactlyOneDiagnostic("finish-core-verify-output-npc-count",
                    targetIdentity, 0, diagnostics);
                return Failure(typedCounts, rawCounts, diagnostics);
            }
            Require(output.ModKey == source.ModKey &&
                    output.ModKey.FileName.ToString().Equals(
                        proposal.Request.Source.Plugin.Value.Value, StringComparison.OrdinalIgnoreCase),
                "finish-core-verify-plugin", "Source, output, and proposal plugin identities differ.", diagnostics);
            Require((uint)output.ModHeader.Flags == proposal.SourceTes4Flags,
                "finish-core-verify-tes4-flags", "TES4 flags differ from the source proposal.", diagnostics);
            Require(output.ModHeader.Stats.NextFormID == proposal.NextFormId.Value,
                "finish-core-verify-next-form-id", "TES4 NextFormID differs from the proposal.", diagnostics);
            Require(output.ModHeader.MasterReferences.Select(x => x.Master.ToString())
                       .SequenceEqual(proposal.MasterOrder),
                "finish-core-verify-masters", "Output masters differ from the proposal order.", diagnostics);
            Require(rawCounts.Values.All(value => value == 0) &&
                    typedCounts.Values.All(value => value == 0),
                "finish-core-verify-forbidden", "The output contains forbidden world/reference records.", diagnostics);
            uint actualOutputRecordCount = checked(
                checked((uint)outputCensus.NonTes4Records.Length) +
                checked((uint)outputCensus.Groups.Length));
            Require(actualOutputRecordCount == outputRaw.Tes4.RecordCount,
                "finish-core-verify-hedr-count",
                "TES4 HEDR record count does not equal non-TES4 major records plus all GRUPs.",
                diagnostics);
            uint expectedRecordDelta = checked((uint)proposal.NewRecords.Length * 2u);
            uint expectedOutputRecordCount = checked(sourceRaw.Tes4.RecordCount + expectedRecordDelta);
            Require(expectedOutputRecordCount == outputRaw.Tes4.RecordCount,
                "finish-core-verify-hedr-count", "TES4 HEDR record count is not the exact delta.", diagnostics);
            VerifyProtectedSurface(rebasedSourceBytes, outputBytes, sourceRaw, outputRaw, proposal, diagnostics);
            bool typedCardinalityValid = VerifyTypedSurface(
                source, output, sourceNpc!, outputNpc!, proposal,
                cstyId, outfitId, packageId, copiedMaster,
                diagnostics, outputBytes, cancellationToken);
            if (!typedCardinalityValid)
                return Failure(typedCounts, rawCounts, diagnostics);

            Sha256Hash protectedHash = HashProtected(sourceBytes, sourceRaw.Target!, proposal);
            bool verified = diagnostics.All(item => item.Severity != DiagnosticSeverity.Error);
            return new BethesdaSkyrimNpcFinishCoreVerification(
                verified,
                typedCounts,
                rawCounts,
                protectedHash,
                sourceHash,
                Hash(outputBytes),
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (
            exception is IOException or InvalidDataException or ArgumentException or
                InvalidOperationException or OverflowException or RecordException)
        {
            diagnostics.Add(new Diagnostic(
                exception.Message.StartsWith("finish-core-master-reindex:", StringComparison.Ordinal)
                    ? "finish-core-master-reindex" : "finish-core-verify-exception",
                DiagnosticSeverity.Error, exception.Message));
            return new BethesdaSkyrimNpcFinishCoreVerification(
                false, empty, empty, new Sha256Hash(new string('0', 64)),
                new Sha256Hash(new string('0', 64)),
                new Sha256Hash(new string('0', 64)), diagnostics.ToImmutable());
        }
    }

    private static void VerifyProtectedSurface(
        byte[] sourceBytes,
        byte[] outputBytes,
        BethesdaSkyrimNpcFinishCoreRaw.RawPluginSnapshot source,
        BethesdaSkyrimNpcFinishCoreRaw.RawPluginSnapshot output,
        SkyrimNpcFinishCoreProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (source.Target is null || output.Target is null)
            return;
        var allowed = new HashSet<string>(["ACBS", "SNAM", "ZNAM", "DOFT", "CNTO", "PKID"], StringComparer.Ordinal);
        if (proposal.Request!.AiPolicy is not null)
            allowed.Add("AIDT");
        if (!proposal.Request.PerkPolicy.IsDefault)
        {
            allowed.Add("PRKZ");
            allowed.Add("PRKR");
        }
        ImmutableArray<BethesdaSkyrimNpcFinishCoreRaw.RawSubrecord> before =
            BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(sourceBytes, source.Target);
        ImmutableArray<BethesdaSkyrimNpcFinishCoreRaw.RawSubrecord> after =
            BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(outputBytes, output.Target);
        ImmutableArray<string> beforeProtected = before.Where(row => !allowed.Contains(row.Signature))
            .Select(row => Convert.ToHexString(row.Bytes)).ToImmutableArray();
        ImmutableArray<string> afterProtected = after.Where(row => !allowed.Contains(row.Signature))
            .Select(row => Convert.ToHexString(row.Bytes)).ToImmutableArray();
        Require(beforeProtected.SequenceEqual(afterProtected),
            "finish-core-verify-protected-subrecords",
            "Protected NPC subrecords changed or moved.", diagnostics);
        if (proposal.Request!.InventoryPolicy.Policy == SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory)
        {
            ImmutableArray<string> beforeInventory = before.Where(row => row.Signature == "CNTO")
                .Select(row => Convert.ToHexString(row.Bytes)).ToImmutableArray();
            ImmutableArray<string> afterInventory = after.Where(row => row.Signature == "CNTO")
                .Select(row => Convert.ToHexString(row.Bytes)).ToImmutableArray();
            Require(beforeInventory.SequenceEqual(afterInventory),
                "finish-core-verify-inventory", "PreserveInventory changed CNTO rows.", diagnostics);
        }
    }

    private static bool VerifyTypedSurface(
        SkyrimMod source,
        SkyrimMod output,
        Npc sourceNpc,
        Npc outputNpc,
        SkyrimNpcFinishCoreProposal proposal,
        uint? cstyId,
        uint? outfitId,
        uint? packageId,
        WorkspacePath copiedMaster,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        byte[] outputBytes,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreRequest request = proposal.Request!;
        bool cardinalityValid = true;
        uint sourceFlags = (uint)sourceNpc.Configuration.Flags;
        uint outputFlags = (uint)outputNpc.Configuration.Flags;
        Require(outputFlags == (sourceFlags | 0x00000820u),
            "finish-core-verify-acbs", "NPC ACBS flags are not source flags plus 0x820.", diagnostics);
        if (request.AiPolicy is { } aiPolicy)
        {
            const string aiDiagnosticCode = "finish-core-verify-aidt";
            const string completePolicyMessage =
                "NPC AIDT does not equal the complete reviewed AI policy.";
            if (outputNpc.AIData is not { } outputAi)
            {
                Require(false, aiDiagnosticCode, completePolicyMessage, diagnostics);
            }
            else
            {
                bool legacyFieldsMatch =
                    (int)outputAi.Aggression == (int)aiPolicy.Aggression &&
                    (int)outputAi.Confidence == (int)aiPolicy.Confidence &&
                    outputAi.EnergyLevel == aiPolicy.Energy &&
                    (int)outputAi.Responsibility == (int)aiPolicy.Morality &&
                    (int)outputAi.Assistance == (int)aiPolicy.Assistance;
                if (!legacyFieldsMatch)
                {
                    Require(false, aiDiagnosticCode, completePolicyMessage, diagnostics);
                }
                else if (request.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier)
                {
                    if (sourceNpc.AIData is not { } sourceAi)
                    {
                        Require(false, aiDiagnosticCode, completePolicyMessage, diagnostics);
                    }
                    else
                    {
                        Require(outputAi.Mood == sourceAi.Mood,
                            aiDiagnosticCode,
                            $"NPC AIDT Mood differs from source NPC: source={sourceAi.Mood}; " +
                            $"output={outputAi.Mood}.",
                            diagnostics);
                    }
                }
                else
                {
                    bool requestedMoodMatches = aiPolicy.Mood is { } expectedMood &&
                        TryToFinishCoreMood(outputAi.Mood, out SkyrimNpcFinishCoreMood actualMood) &&
                        actualMood == expectedMood;
                    Require(requestedMoodMatches, aiDiagnosticCode, completePolicyMessage, diagnostics);
                }
            }
        }
        VerifyFactions(outputNpc, diagnostics);
        VerifyRelationship(output, outputNpc, diagnostics);
        if (!request.PerkPolicy.IsDefault)
            Require((outputNpc.Perks ?? []).Select(perk =>
                    (perk.Perk.FormKey, perk.Rank)).SequenceEqual(request.PerkPolicy.Select(perk =>
                    (new FormKey(ModKey.FromNameAndExtension(perk.Form.Plugin.Value), perk.Form.FormId.Value), perk.Rank))),
                "finish-core-verify-perks", "NPC PRKR identities/ranks differ from the reviewed ordered perk policy.", diagnostics);
        if (cstyId is { } cstyAllocationId)
        {
            CombatStyle[] styles = output.CombatStyles
                .Where(row => row.FormKey.ModKey == output.ModKey && row.FormKey.ID == cstyAllocationId)
                .ToArray();
            if (RequireExactlyOne(styles, "finish-core-verify-csty-count", "CSTY",
                    diagnostics, out CombatStyle? style))
            {
                Require((request.CombatPolicy is null ? IsDefensive(style!) : VerifyCombatClone(source, sourceNpc, output, style!, request)) && outputNpc.CombatStyle.FormKeyNullable == style!.FormKey,
                    "finish-core-verify-csty", request.CombatPolicy is null
                        ? "The derived CSTY is not the closed defensive contract."
                        : $"The derived CSTY or NPC ZNAM differs from the reviewed combat {(request.CombatPolicy.Profile is { } profile ? $"profile '{profile}'" : "style clone")}.",
                    diagnostics);
            }
            else
                cardinalityValid = false;
        }
        else if (request.CombatPolicy is { } localCombatPolicy &&
                 sourceNpc.CombatStyle.FormKeyNullable is { } localCombatStyleKey &&
                 localCombatStyleKey.ModKey == source.ModKey)
        {
            CombatStyle[] styles = output.CombatStyles
                .Where(row => row.FormKey == localCombatStyleKey)
                .ToArray();
            if (RequireExactlyOne(styles, "finish-core-verify-csty-count",
                    localCombatStyleKey.ToString(), diagnostics, out CombatStyle? style))
            {
                CombatStyle expected = BethesdaSkyrimNpcFinishCoreCombatStyle.ReadTemplate(
                    source, sourceNpc, request);
                BethesdaSkyrimNpcFinishCoreCombatStyle.ApplyProfile(
                    expected, expected, localCombatPolicy);
                Require(style!.Equals(expected) &&
                        outputNpc.CombatStyle.FormKeyNullable == localCombatStyleKey,
                    "finish-core-verify-csty",
                    "The source-owned CSTY or NPC ZNAM differs from the reviewed in-place combat profile.",
                    diagnostics);
            }
            else
                cardinalityValid = false;
        }
        ImmutableArray<FormReference> outfitItems = request.OutfitRacePolicy == SkyrimNpcFinishCoreOutfitRacePolicy.Clone
            ? BethesdaSkyrimNpcFinishCoreOutfit.Verify(output, source, sourceNpc.Race.FormKey, proposal, diagnostics, outputBytes)
            : request.OutfitPolicy.ArmorItems;
        if (outfitId is { } outfitAllocationId)
        {
            Outfit[] outfits = output.Outfits
                .Where(row => row.FormKey.ModKey == output.ModKey && row.FormKey.ID == outfitAllocationId)
                .ToArray();
            if (RequireExactlyOne(outfits, "finish-core-verify-otft-count", "OTFT",
                    diagnostics, out Outfit? outfit))
            {
                Require(outputNpc.DefaultOutfit.FormKeyNullable == outfit!.FormKey &&
                        (outfit.Items ?? []).Select(row => row.FormKey).SequenceEqual(
                            outfitItems.Select(ToFormKey)),
                    "finish-core-verify-otft", "The private outfit differs from the ordered authority.", diagnostics);
            }
            else
                cardinalityValid = false;
        }
        if (packageId is { } packageAllocationId)
        {
            Package[] packages = output.Packages
                .Where(row => row.FormKey.ModKey == output.ModKey && row.FormKey.ID == packageAllocationId)
                .ToArray();
            if (RequireExactlyOne(packages, "finish-core-verify-pack-count", "PACK",
                    diagnostics, out Package? package))
            {
                Package selectedPackage = package!;
                PackageDataLocation[] locations = selectedPackage.Data.Values
                    .OfType<PackageDataLocation>()
                    .ToArray();
                bool locationValid = RequireExactlyOne(
                    locations, "finish-core-verify-pack-location-count", "PACK location",
                    diagnostics, out PackageDataLocation? location);
                var conditions = selectedPackage.Conditions.ToArray();
                bool conditionValid = RequireExactlyOne(
                    conditions, "finish-core-verify-pack-condition-count", "PACK condition",
                    diagnostics, out var condition);
                if (!locationValid || !conditionValid)
                    cardinalityValid = false;
                if (locationValid && conditionValid)
                {
                    Require(outputNpc.Packages.Select(row => row.FormKey).SequenceEqual([selectedPackage.FormKey]) &&
                            string.Equals(selectedPackage.EditorID,
                                BuildEditorId(request.Actor.EditorId!.Value.Value, "FinishCoreSandbox"),
                                StringComparison.Ordinal) &&
                            location!.Location is LocationTargetRadius
                            {
                                Radius: 512,
                                Target: LocationFallback fallback
                            } &&
                            string.Equals(fallback.Type.ToString(), "NearEditorLocation",
                                StringComparison.Ordinal) && fallback.Data == 0 &&
                            IsFinishCoreCondition(condition!) &&
                            selectedPackage.OwnerQuest.FormKeyNullable is null &&
                            selectedPackage.VirtualMachineAdapter is null &&
                            selectedPackage.ScheduleMonth == -1 &&
                            selectedPackage.ScheduleDayOfWeek == Package.DayOfWeek.Any &&
                            selectedPackage.ScheduleDate == 0 && selectedPackage.ScheduleHour == -1 &&
                            selectedPackage.ScheduleMinute == -1 &&
                            selectedPackage.ScheduleDurationInMinutes == 0,
                        "finish-core-verify-pack", "The derived sandbox PACK widened its admitted surface.", diagnostics);
                }
            }
            else
                cardinalityValid = false;
        }
        if (request.InventoryPolicy.Policy == SkyrimNpcFinishCoreInventoryPolicy.ReplaceExactInventory)
        {
            FormKey[] expected = request.InventoryPolicy.DesiredItems.Select(ParseFormKey).ToArray();
            FormKey[] actual = (outputNpc.Items ?? []).Select(row => row.Item.Item.FormKey).ToArray();
            Require(expected.SequenceEqual(actual), "finish-core-verify-inventory",
                "ReplaceExactInventory does not equal the reviewed desired list.", diagnostics);
        }
        cancellationToken.ThrowIfCancellationRequested();
        _ = copiedMaster;
        return cardinalityValid;
    }

    private static void VerifyFactions(Npc npc, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        FormKey potential = new(ModKey.FromNameAndExtension("Skyrim.esm"), 0x5C84D);
        FormKey current = new(ModKey.FromNameAndExtension("Skyrim.esm"), 0x5C84E);
        RankPlacement[] potentialRows = (npc.Factions ?? []).Where(row => row.Faction.FormKey == potential).ToArray();
        RankPlacement[] currentRows = (npc.Factions ?? []).Where(row => row.Faction.FormKey == current).ToArray();
        Require(potentialRows.Length == 1 && potentialRows[0].Rank == 0 &&
                currentRows.Length == 1 && currentRows[0].Rank == -1,
            "finish-core-verify-factions", "Follower faction rows are missing, duplicated, or mis-ranked.", diagnostics);
    }

    private static void VerifyRelationship(SkyrimMod mod, Npc npc, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        FormKey player = new(ModKey.FromNameAndExtension("Skyrim.esm"), 7);
        Relationship[] rows = mod.Relationships.Where(row => row.Parent.FormKey == npc.FormKey && row.Child.FormKey == player).ToArray();
        Require(rows.Length == 1 && rows[0].Rank == Relationship.RankType.Ally && rows[0].Flags == 0 && rows[0].AssociationType.IsNull,
            "finish-core-verify-relationship",
            "Actor-to-player relationship is not exactly one Ally row. Actual: " +
            string.Join(", ", rows.Select(row =>
                $"{row.Parent.FormKey}->{row.Child.FormKey} rank={row.Rank} flags={row.Flags} assoc-null={row.AssociationType.IsNull}")),
            diagnostics);
    }

    private static bool VerifyRecordGraph(
        byte[] outputBytes,
        BethesdaSkyrimNpcFinishCoreRaw.RawCensus source,
        BethesdaSkyrimNpcFinishCoreRaw.RawCensus output,
        SkyrimNpcFinishCoreProposal proposal,
        Npc sourceNpc,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var expectedRecords = source.Records.Select(ToRawRecordIdentity).ToList();
        var expectedGroups = source.Groups.Select(ToRawGroupIdentity).ToList();
        var nextTopLevelOrdinals = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (BethesdaSkyrimNpcFinishCoreRaw.RawGroup group in
                 source.Groups.Where(group => group.Depth == 1))
        {
            nextTopLevelOrdinals[group.Label] =
                nextTopLevelOrdinals.TryGetValue(group.Label, out int count)
                    ? checked(count + 1)
                    : 1;
        }
        foreach (string row in proposal.NewRecords)
        {
            if (!TryParseProposalRecordIdentity(row, out string signature, out uint localFormId))
            {
                Require(false, "finish-core-verify-record-closure",
                    $"Proposal record '{row}' is not a valid raw record identity.", diagnostics);
                return false;
            }
            int ordinal = nextTopLevelOrdinals.TryGetValue(signature, out int existing)
                ? existing
                : 0;
            nextTopLevelOrdinals[signature] = checked(ordinal + 1);
            string path = BethesdaSkyrimNpcFinishCoreRaw.BuildGroupPath(
                string.Empty, signature, ordinal);
            expectedGroups.Add(new RawGroupIdentity(1, signature, path, 0,
                BinaryPrimitives.ReadUInt32LittleEndian(Encoding.ASCII.GetBytes(signature))));
            expectedRecords.Add(new RawRecordIdentity(signature,
                ((uint)proposal.MasterOrder.Length << 24) | localFormId, path));
        }

        var actualRecords = output.Records.Select(ToRawRecordIdentity).ToList();
        var actualGroups = output.Groups.Select(ToRawGroupIdentity).ToList();
        ImmutableArray<RawRecordIdentity> missingRecords = GetMultiplicityDifference(
            expectedRecords, actualRecords, FormatClosureRecordIdentity);
        ImmutableArray<RawRecordIdentity> unexpectedRecords = GetMultiplicityDifference(
            actualRecords, expectedRecords, FormatClosureRecordIdentity);
        ImmutableArray<RawGroupIdentity> missingGroups = GetMultiplicityDifference(
            expectedGroups, actualGroups, FormatClosureGroupIdentity);
        ImmutableArray<RawGroupIdentity> unexpectedGroups = GetMultiplicityDifference(
            actualGroups, expectedGroups, FormatClosureGroupIdentity);
        if (missingRecords.Length != 0 || unexpectedRecords.Length != 0 ||
            missingGroups.Length != 0 || unexpectedGroups.Length != 0)
        {
            uint expectedMajorCount = checked((uint)expectedRecords.Count(
                record => record.Signature != "TES4"));
            uint expectedTopLevelGroupCount = checked((uint)expectedGroups.Count(
                group => group.Depth == 1));
            uint expectedHedrCount = checked(expectedMajorCount + (uint)expectedGroups.Count);
            uint actualMajorCount = checked((uint)output.NonTes4Records.Length);
            uint actualTopLevelGroupCount = checked((uint)output.Groups.Count(
                group => group.Depth == 1));
            uint? actualHedrCount = TryReadHedrRecordCount(outputBytes, output);
            Require(false, "finish-core-verify-record-closure",
                "Output raw record/group closure differs; " +
                $"missing-records={FormatDifference(missingRecords, FormatClosureRecordIdentity)}; " +
                $"unexpected-records={FormatDifference(unexpectedRecords, FormatClosureRecordIdentity)}; " +
                $"missing-groups={FormatDifference(missingGroups, FormatClosureGroupIdentity)}; " +
                $"unexpected-groups={FormatDifference(unexpectedGroups, FormatClosureGroupIdentity)}; " +
                $"expected-census=(major={expectedMajorCount}; top-level-groups={expectedTopLevelGroupCount}; " +
                $"hedr={expectedHedrCount}); " +
                $"actual-census=(major={actualMajorCount}; top-level-groups={actualTopLevelGroupCount}; " +
                $"hedr={FormatHedrCount(actualHedrCount)}).",
                diagnostics);
            return false;
        }

        uint target = ((uint)proposal.MasterOrder.Length << 24) |
            (proposal.Request!.Actor.FormId!.Value.Value & 0x00FF_FFFFu);
        ILookup<RawRecordIdentity, BethesdaSkyrimNpcFinishCoreRaw.RawRecord> outputByIdentity =
            output.Records
                .Where(row => row.Signature != "TES4" &&
                              !(row.Signature == "NPC_" &&
                                row.RawFormId == target))
                .ToLookup(ToRawRecordIdentity);
        HashSet<uint> reviewedLocalCombatStyles = proposal.ExistingRecordChanges
            .Where(row => row.StartsWith("CSTY ", StringComparison.Ordinal))
            .Select(row => row.Split(':', 2)[0])
            .Where(row => !proposal.NewRecords.Contains(row, StringComparer.Ordinal))
            .Select(row => TryParseProposalRecordIdentity(row, out string signature, out uint localId) &&
                           signature == "CSTY" ? localId : 0u)
            .Where(localId => localId != 0)
            .ToHashSet();
        uint? requestedLocalCombatStyle = proposal.Request!.CombatPolicy is not null &&
            sourceNpc.CombatStyle.FormKeyNullable is { } sourceStyle &&
            sourceStyle.ModKey == sourceNpc.FormKey.ModKey
                ? sourceStyle.ID
                : null;
        foreach (IGrouping<RawRecordIdentity, BethesdaSkyrimNpcFinishCoreRaw.RawRecord> group in
                 source.Records
                     .Where(row => row.Signature != "TES4" &&
                                   !(row.Signature == "NPC_" &&
                                     row.RawFormId == target))
                     .GroupBy(ToRawRecordIdentity))
        {
            BethesdaSkyrimNpcFinishCoreRaw.RawRecord[] actual =
                outputByIdentity[group.Key].ToArray();
            bool reviewedInPlaceChange = group.Key.Signature == "CSTY" &&
                requestedLocalCombatStyle == (group.Key.RawFormId & 0x00FF_FFFFu) &&
                reviewedLocalCombatStyles.Contains(group.Key.RawFormId & 0x00FF_FFFFu);
            bool bytesMatch = actual.Length == group.Count() &&
                (reviewedInPlaceChange || group.Select(row => row.Bytes).Zip(actual,
                    (expected, observed) => expected.AsSpan().SequenceEqual(observed.Bytes))
                    .All(value => value));
            Require(bytesMatch,
                "finish-core-verify-record-preservation",
                $"Source record {FormatRawRecordIdentity(group.Key)} changed.", diagnostics);
        }
        return true;
    }

    private static RawRecordIdentity ToRawRecordIdentity(
        BethesdaSkyrimNpcFinishCoreRaw.RawRecord record) =>
        new(record.Signature, record.RawFormId, record.GroupPath);

    private static RawGroupIdentity ToRawGroupIdentity(
        BethesdaSkyrimNpcFinishCoreRaw.RawGroup group) =>
        new(group.Depth, group.Label, group.Path, group.Type, group.RawLabel);

    private static bool TryParseProposalRecordIdentity(
        string value,
        out string signature,
        out uint localFormId)
    {
        signature = string.Empty;
        localFormId = 0;
        string[] parts = value.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2 || parts[0].Length != 4 ||
            !FormId.TryParse(parts[1], out FormId parsed) ||
            parsed.Value == 0 || parsed.Value > 0x00FF_FFFFu)
            return false;
        signature = parts[0];
        localFormId = parsed.Value & 0x00FF_FFFFu;
        return true;
    }

    private static ImmutableArray<T> GetMultiplicityDifference<T>(
        IEnumerable<T> left,
        IEnumerable<T> right,
        Func<T, string> formatter)
        where T : notnull
    {
        var remaining = new Dictionary<T, int>();
        foreach (T value in left)
            remaining[value] = remaining.TryGetValue(value, out int count)
                ? checked(count + 1)
                : 1;
        foreach (T value in right)
        {
            if (!remaining.TryGetValue(value, out int count))
                continue;
            if (count == 1)
                remaining.Remove(value);
            else
                remaining[value] = count - 1;
        }
        var difference = ImmutableArray.CreateBuilder<T>();
        foreach (KeyValuePair<T, int> entry in remaining.OrderBy(
                     entry => formatter(entry.Key), StringComparer.Ordinal))
        {
            for (int index = 0; index < entry.Value; index++)
                difference.Add(entry.Key);
        }
        return difference.ToImmutable();
    }

    private static string FormatRawRecordIdentity(RawRecordIdentity identity) =>
        identity.Signature + ":" + (identity.RawFormId & 0x00FF_FFFFu).ToString("X8", CultureInfo.InvariantCulture) +
        " owner=" + (identity.RawFormId >> 24).ToString("X2", CultureInfo.InvariantCulture) +
        "@" + identity.GroupPath;

    private static string FormatClosureRecordIdentity(RawRecordIdentity identity) =>
        identity.Signature + " 0x" + (identity.RawFormId & 0x00FF_FFFFu).ToString("X8", CultureInfo.InvariantCulture) +
        " owner=" + (identity.RawFormId >> 24).ToString("X2", CultureInfo.InvariantCulture) +
        " path=" + identity.GroupPath;

    private static string FormatClosureGroupIdentity(RawGroupIdentity identity) =>
        "GRUP " + identity.Label + " depth=" + identity.Depth.ToString(CultureInfo.InvariantCulture) +
        " path=" + identity.Path;

    private static string FormatDifference<T>(
        ImmutableArray<T> difference,
        Func<T, string> formatter) =>
        difference.Length == 0
            ? "none"
            : difference.Length == 1
                ? formatter(difference[0])
                : formatter(difference[0]) +
                  " (+" + (difference.Length - 1).ToString(CultureInfo.InvariantCulture) + " more)";

    private static uint? TryReadHedrRecordCount(
        byte[] bytes,
        BethesdaSkyrimNpcFinishCoreRaw.RawCensus census)
    {
        BethesdaSkyrimNpcFinishCoreRaw.RawRecord? tes4 = census.Records
            .FirstOrDefault(record => record.Signature == "TES4");
        if (tes4 is null)
            return null;
        BethesdaSkyrimNpcFinishCoreRaw.RawSubrecord? hedr =
            BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(bytes, tes4)
                .FirstOrDefault(record => record.Signature == "HEDR");
        if (hedr is null || hedr.Length < 18 ||
            BinaryPrimitives.ReadUInt16LittleEndian(hedr.Bytes.AsSpan(4, 2)) < 12)
            return null;
        return BinaryPrimitives.ReadUInt32LittleEndian(hedr.Bytes.AsSpan(10, 4));
    }

    private static string FormatHedrCount(uint? count) =>
        count?.ToString(CultureInfo.InvariantCulture) ?? "missing";

    private static ImmutableDictionary<string, int> CountRaw(
        BethesdaSkyrimNpcFinishCoreRaw.RawPluginSnapshot snapshot) =>
        BethesdaSkyrimNpcFinishCoreSourceReader.ForbiddenSignatures
            .ToImmutableDictionary(signature => signature,
                signature => snapshot.Records.Count(row => row.Signature == signature), StringComparer.Ordinal);

    private static ImmutableDictionary<string, int> CountTyped(SkyrimMod mod)
    {
        var counts = BethesdaSkyrimNpcFinishCoreSourceReader.ForbiddenSignatures
            .ToImmutableDictionary(signature => signature, _ => 0, StringComparer.Ordinal)
            .ToBuilder();
        foreach (object record in mod.EnumerateMajorRecords())
        {
            string? signature = record.GetType().Name switch
            {
                "Cell" => "CELL",
                "Worldspace" => "WRLD",
                "PlacedNpc" => "ACHR",
                "PlacedObject" => "REFR",
                "Landscape" => "LAND",
                "NavigationMesh" => "NAVM",
                "NavigationMeshInfoMap" => "NAVI",
                "Water" => "WATR",
                "LandscapeTexture" => "LTEX",
                "Location" => "LCTN",
                "Region" => "REGN",
                "Climate" => "CLMT",
                "MusicType" or "MusicTrack" => "MUSC",
                "ImageSpace" => "IMGS",
                _ => null
            };
            if (signature is not null)
                counts[signature]++;
        }
        return counts.ToImmutable();
    }

    private static Sha256Hash HashProtected(
        byte[] bytes,
        BethesdaSkyrimNpcFinishCoreRaw.RawRecord target,
        SkyrimNpcFinishCoreProposal proposal)
    {
        var allowed = new HashSet<string>(["ACBS", "SNAM", "ZNAM", "DOFT", "CNTO", "PKID"], StringComparer.Ordinal);
        string text = string.Join("\n", BethesdaSkyrimNpcFinishCoreRaw.GetSubrecords(bytes, target)
            .Where(row => !allowed.Contains(row.Signature))
            .Select(row => Convert.ToHexString(row.Bytes)));
        _ = proposal;
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
    }

    private static bool TryPreflightAllocation(
        SkyrimNpcFinishCoreProposal proposal,
        byte[] outputBytes,
        string signature,
        string countCode,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out uint? id)
    {
        id = null;
        if (!TryGetAllocation(proposal, signature, diagnostics,
                out uint allocationId, out bool cardinalityValid))
            return cardinalityValid;
        int observedCount = BethesdaSkyrimNpcFinishCoreRaw.CountSelfRecords(
            outputBytes, signature, allocationId);
        if (observedCount != 1)
        {
            AddExactlyOneDiagnostic(countCode, signature, observedCount, diagnostics);
            return false;
        }
        id = allocationId;
        return true;
    }

    private static bool TryGetAllocation(
        SkyrimNpcFinishCoreProposal proposal,
        string signature,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out uint id,
        out bool cardinalityValid)
    {
        string[] rows = proposal.NewRecords
            .Where(value => value.StartsWith(signature + " ", StringComparison.Ordinal))
            .ToArray();
        id = 0;
        cardinalityValid = true;
        if (rows.Length == 0)
        {
            return false;
        }
        if (!RequireExactlyOne(rows, "finish-core-verify-proposal-allocation-count",
                $"{signature} allocation", diagnostics, out string? row))
        {
            cardinalityValid = false;
            return false;
        }
        string allocationValue = row!.AsSpan(signature.Length + 1).ToString();
        if (!FormId.TryParse(allocationValue, out FormId parsed) ||
            parsed.Value == 0 ||
            parsed.Value > 0x00FF_FFFF ||
            !string.Equals(allocationValue, new FormId(parsed.Value).ToString(), StringComparison.Ordinal))
        {
            cardinalityValid = false;
            diagnostics.Add(new Diagnostic(
                "finish-core-verify-proposal-allocation-invalid",
                DiagnosticSeverity.Error,
                $"{signature} allocation '{allocationValue}' is not a canonical supported FormID."));
            return false;
        }
        id = parsed.Value;
        return true;
    }

    private static bool VerifyCombatClone(
        SkyrimMod source, Npc sourceNpc, SkyrimMod output, CombatStyle actual,
        SkyrimNpcFinishCoreRequest request)
    {
        CombatStyle template = BethesdaSkyrimNpcFinishCoreCombatStyle.ReadTemplate(source, sourceNpc, request);
        var comparison = new SkyrimMod(output.ModKey, SkyrimRelease.SkyrimSE);
        CombatStyle expected = comparison.CombatStyles.DuplicateInAsNewRecord(template, actual.FormKey);
        string editor = request.Actor.EditorId!.Value.Value;
        const string suffix = "_CombatStyle";
        expected.EditorID = editor[..Math.Min(editor.Length, 64 - suffix.Length)] + suffix;
        switch (request.CombatPolicy!.Profile)
        {
            case SkyrimNpcFinishCoreCombatProfile.Defensive:
                expected.OffensiveMult = template.OffensiveMult / 2f;
                expected.DefensiveMult = Math.Max(1f, template.DefensiveMult);
                break;
            case SkyrimNpcFinishCoreCombatProfile.RangedFirst:
            case SkyrimNpcFinishCoreCombatProfile.MeleeFirst:
                float priority = new[] { 1f, template.EquipmentScoreMultMelee, template.EquipmentScoreMultMagic,
                    template.EquipmentScoreMultRanged, template.EquipmentScoreMultShout,
                    template.EquipmentScoreMultStaff, template.EquipmentScoreMultUnarmed }.Max() + 1f;
                if (request.CombatPolicy.Profile == SkyrimNpcFinishCoreCombatProfile.RangedFirst)
                    expected.EquipmentScoreMultRanged = priority;
                else
                    expected.EquipmentScoreMultMelee = priority;
                break;
        }
        return actual.Equals(expected);
    }

    private static bool IsDefensive(ICombatStyleGetter style) =>
        style.OffensiveMult == 0f && style.DefensiveMult == 1f && style.GroupOffensiveMult == 0f &&
        style.EquipmentScoreMultMelee == 0f && style.EquipmentScoreMultMagic == 0f &&
        style.EquipmentScoreMultRanged == 0f && style.EquipmentScoreMultShout == 0f &&
        style.EquipmentScoreMultUnarmed == 0f && style.EquipmentScoreMultStaff == 0f &&
        style.AvoidThreatChance == 1f && style.CloseRange is { FallbackMult: 1f } &&
        style.Flight is { HoverChance: 0f, DiveBombChance: 0f, GroundAttackChance: 0f,
            PerchAttackChance: 0f, FlyingAttackChance: 0f };

    private static bool IsFinishCoreCondition(IConditionGetter condition)
    {
        GetFactionRankConditionData? data = condition.Data as GetFactionRankConditionData;
        FormKey currentFollower = new(
            ModKey.FromNameAndExtension("Skyrim.esm"), 0x0005C84E);
        return condition.CompareOperator == CompareOperator.LessThan &&
               HasZeroComparisonValue(condition) && data is
               {
                   RunOnType: Condition.RunOnType.Subject
               } && data.Faction.Link.FormKey == currentFollower;
    }

    private static bool HasZeroComparisonValue(IConditionGetter condition)
    {
        object? value = condition.GetType().GetProperty("ComparisonValue")?.GetValue(condition);
        return value is not null &&
               Convert.ToSingle(value, CultureInfo.InvariantCulture) == 0f;
    }

    private static string BuildEditorId(string editorId, string suffix)
    {
        string text = "_" + suffix;
        int length = Math.Min(editorId.Length, 64 - text.Length);
        return editorId[..length] + text;
    }

    private static bool TryToFinishCoreMood(
        Mood mood,
        out SkyrimNpcFinishCoreMood coreMood)
    {
        switch (mood)
        {
            case Mood.Neutral:
                coreMood = SkyrimNpcFinishCoreMood.Neutral;
                return true;
            case Mood.Angry:
                coreMood = SkyrimNpcFinishCoreMood.Angry;
                return true;
            case Mood.Fear:
                coreMood = SkyrimNpcFinishCoreMood.Fear;
                return true;
            case Mood.Happy:
                coreMood = SkyrimNpcFinishCoreMood.Happy;
                return true;
            case Mood.Sad:
                coreMood = SkyrimNpcFinishCoreMood.Sad;
                return true;
            case Mood.Surprised:
                coreMood = SkyrimNpcFinishCoreMood.Surprise;
                return true;
            case Mood.Puzzled:
                coreMood = SkyrimNpcFinishCoreMood.Puzzled;
                return true;
            case Mood.Disgusted:
                coreMood = SkyrimNpcFinishCoreMood.Disgusted;
                return true;
            default:
                coreMood = default;
                return false;
        }
    }

    private static FormKey ToFormKey(FormReference value) =>
        new(ModKey.FromNameAndExtension(value.Plugin.Value), value.FormId.Value);

    private static FormKey ParseFormKey(string value)
    {
        int separator = value.IndexOf('|');
        if (separator <= 0)
            throw new InvalidDataException("Malformed inventory FormReference.");
        string text = value.AsSpan(separator + 1).ToString();
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            text = text[2..];
        if (!uint.TryParse(text, System.Globalization.NumberStyles.AllowHexSpecifier,
                System.Globalization.CultureInfo.InvariantCulture, out uint id))
            throw new InvalidDataException("Malformed inventory FormReference.");
        return new FormKey(ModKey.FromNameAndExtension(value.AsSpan(0, separator)), id);
    }

    private static bool RequireExactlyOne<T>(
        T[] records,
        string code,
        string identity,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out T? record)
        where T : class
    {
        if (records.Length == 1)
        {
            record = records[0];
            return true;
        }

        AddExactlyOneDiagnostic(code, identity, records.Length, diagnostics);
        record = null;
        return false;
    }

    private static void AddExactlyOneDiagnostic(
        string code,
        string identity,
        int observedCount,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        string observed = observedCount switch
        {
            0 => "0",
            2 => "at least 2",
            _ => observedCount.ToString(CultureInfo.InvariantCulture)
        };
        diagnostics.Add(new Diagnostic(
            code,
            DiagnosticSeverity.Error,
            $"{identity} requires exactly 1 record; observed {observed} " +
            $"(expected=1; observed={observed})."));
    }

    private static void Require(
        bool condition,
        string code,
        string message,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!condition)
            diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message));
    }

    private static BethesdaSkyrimNpcFinishCoreVerification Failure(
        ImmutableDictionary<string, int> typed,
        ImmutableDictionary<string, int> raw,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            false,
            typed,
            raw,
            new Sha256Hash(new string('0', 64)),
            new Sha256Hash(new string('0', 64)),
            new Sha256Hash(new string('0', 64)),
            diagnostics.ToImmutable());

    private static BethesdaSkyrimNpcFinishCoreVerification Failure(
        string code,
        string message,
        ImmutableDictionary<string, int> typed,
        ImmutableDictionary<string, int> raw,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, message));
        return new BethesdaSkyrimNpcFinishCoreVerification(
            false, typed, raw, new Sha256Hash(new string('0', 64)),
            new Sha256Hash(new string('0', 64)),
            new Sha256Hash(new string('0', 64)), diagnostics.ToImmutable());
    }

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));
}
