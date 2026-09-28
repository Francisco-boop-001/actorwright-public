using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Two-pass, no-overwrite transaction for a genuine source-owned Skyrim NPC
/// override. Mutation analysis is shared with the typed scalar core; binary
/// authorship and ownership verification are deliberately separate.
/// </summary>
public sealed class NpcOverrideService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : INpcOverrideService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ValueTask<NpcOverrideSourceInspection> InspectSourceAsync(
        GameEdition edition,
        WorkspacePath sourcePlugin,
        Sha256Hash expectedSourceSha256,
        FormId targetFormId,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("npc-override-edition-unsupported",
                "The true override transaction supports Skyrim SE/AE only."));
        if (!sourcePlugin.IsUnder(labRoot))
            diagnostics.Add(Error("npc-override-source-outside-lab",
                "The source plugin must remain under the K-only workspace."));
        if (!File.Exists(sourcePlugin.Value))
            diagnostics.Add(Error("npc-override-source-missing", "The source plugin does not exist."));
        else
            AddReparseDiagnostic(diagnostics, sourcePlugin.Value, "source plugin");

        Sha256Hash? sourceHash = null;
        NpcOverrideSourceSnapshot? sourceSnapshot = null;
        if (!HasErrors(diagnostics))
        {
            try
            {
                sourceHash = HashFile(sourcePlugin.Value);
                if (sourceHash != expectedSourceSha256)
                    diagnostics.Add(Error("npc-override-source-hash-mismatch",
                        $"Source hash {sourceHash} does not match the bound hash {expectedSourceSha256}."));
                if (!HasErrors(diagnostics))
                {
                    var snapshot = BethesdaNpcMutationAdapter.Read(edition, sourcePlugin, targetFormId);
                    if (snapshot.Sex is not { } sex || snapshot.SkyrimWeight is not { } weight ||
                        snapshot.Archetype is not { } archetype || snapshot.Stats is not { } stats ||
                        snapshot.Keywords is not { } keywords || snapshot.Factions is not { } factions ||
                        snapshot.Inventory is not { } inventory || snapshot.Outfits is not { } outfits ||
                        snapshot.Perks is not { } perks || snapshot.ActorEffects is not { } actorEffects)
                    {
                        diagnostics.Add(Error("npc-override-source-incomplete",
                            "The selected Skyrim NPC did not expose the complete editable source snapshot."));
                    }
                    else
                    {
                        sourceSnapshot = new NpcOverrideSourceSnapshot(
                            snapshot.EditorId is null ? null : new EditorId(snapshot.EditorId),
                            snapshot.Name is null ? null : new NpcName(snapshot.Name),
                            sex,
                            weight,
                            archetype,
                            stats,
                            keywords,
                            factions,
                            inventory,
                            outfits,
                            perks,
                            actorEffects,
                            snapshot.ShortName);
                    }
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(Error("npc-override-source-read-failed", exception.Message));
            }
        }

        return ValueTask.FromResult(new NpcOverrideSourceInspection(
            !HasErrors(diagnostics) && sourceSnapshot is not null,
            sourcePlugin,
            sourceHash,
            targetFormId,
            sourceSnapshot,
            diagnostics.ToImmutable()));
    }

    public async ValueTask<NpcOverrideProposal> AnalyzeAsync(
        NpcOverrideRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(
            request,
            outputMustExist: false,
            proposalMustExist: false).ToBuilder();
        var sourceHash = File.Exists(request.SourcePlugin.Value)
            ? HashFile(request.SourcePlugin.Value)
            : request.ExpectedSourceSha256;
        if (File.Exists(request.SourcePlugin.Value) && sourceHash != request.ExpectedSourceSha256)
            diagnostics.Add(Error("npc-override-source-hash-mismatch",
                $"Source hash {sourceHash} does not match the bound hash {request.ExpectedSourceSha256}."));

        var sourceName = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var outputName = new PluginName(Path.GetFileName(request.OutputPlugin.Value));
        ImmutableArray<PluginName> requiredMasters = [];
        ImmutableArray<MutationChange> changes = [];
        if (!HasErrors(diagnostics))
        {
            try
            {
                if (request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy)
                    BethesdaNpcStandaloneCopyAdapter.ValidateSource(
                        request.SourcePlugin,
                        request.TargetFormId);
                requiredMasters = request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy
                    ? BethesdaNpcStandaloneCopyAdapter.ReadMasters(request.SourcePlugin)
                    : BethesdaNpcOverrideAdapter.ReadRequiredMasters(request.SourcePlugin);
                var snapshot = BethesdaNpcMutationAdapter.Read(
                    request.Edition,
                    request.SourcePlugin,
                    request.TargetFormId);
                changes = NpcMutationService.BuildChanges(
                    ToMutationRequest(request),
                    snapshot);
                if (changes.IsEmpty)
                    diagnostics.Add(Error("npc-override-no-changes",
                        "At least one supported field must differ from the selected source NPC."));
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(FromWriterException(exception));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(Error("npc-override-source-read-failed", exception.Message));
            }
        }

        var proposal = new NpcOverrideProposal(
            1,
            request.Edition,
            request.SourcePlugin,
            sourceHash,
            sourceName,
            request.TargetFormId,
            request.ProposalPath,
            null,
            request.OutputPlugin,
            outputName,
            request.Patch,
            requiredMasters,
            changes,
            NpcMutationService.PreservedFields,
            diagnostics.ToImmutable())
        {
            OutputKind = request.OutputKind
        };
        if (request.ProposalPath is not null && proposal.IsApplicable)
        {
            try
            {
                var hash = await WriteProposalAsync(proposal, request.ProposalPath.Value,
                    cancellationToken);
                proposal = proposal with { ProposalSha256 = hash };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(Error("npc-override-proposal-write-failed", exception.Message));
                proposal = proposal with { Diagnostics = diagnostics.ToImmutable() };
            }
        }
        return proposal;
    }

    public async ValueTask<NpcOverrideResult> ApplyAsync(
        NpcOverrideRequest request,
        NpcOverrideProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(ValidateRequest(
            request,
            outputMustExist: false,
            proposalMustExist: true));
        ValidateProposalBinding(request, proposal, diagnostics);
        if (!HasErrors(diagnostics))
        {
            var fresh = await AnalyzeAsync(request with { ProposalPath = null }, cancellationToken);
            diagnostics.AddRange(fresh.Diagnostics);
            if (fresh.OutputKind != proposal.OutputKind ||
                !fresh.Changes.SequenceEqual(proposal.Changes) ||
                !fresh.RequiredMasters.SequenceEqual(proposal.RequiredMasters) ||
                !fresh.PreservedFields.SequenceEqual(proposal.PreservedFields))
            {
                diagnostics.Add(Error("npc-override-proposal-stale",
                    "Fresh analysis no longer matches the persisted proposal."));
            }
        }
        if (HasErrors(diagnostics))
            return new NpcOverrideResult(false, proposal, null, null, diagnostics.ToImmutable());

        var destination = request.OutputPlugin.Value;
        var parent = Path.GetDirectoryName(destination)!;
        var temporary = Path.Combine(parent,
            $".{Path.GetFileNameWithoutExtension(destination)}.tmp-{Guid.NewGuid():N}.esp");
        var promoted = false;
        try
        {
            if (request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy)
                BethesdaNpcStandaloneCopyAdapter.Write(
                    request,
                    new WorkspacePath(temporary),
                    proposal.Changes);
            else
                BethesdaNpcOverrideAdapter.Write(request, new WorkspacePath(temporary));
            FlushFile(temporary);
            var temporaryVerification = VerifyArtifact(
                request,
                proposal,
                new WorkspacePath(temporary),
                cancellationToken);
            diagnostics.AddRange(temporaryVerification.Diagnostics);
            if (!temporaryVerification.IsValid || HasErrors(diagnostics))
                return new NpcOverrideResult(false, proposal, null,
                    temporaryVerification, diagnostics.ToImmutable());

            File.Move(temporary, destination, overwrite: false);
            promoted = true;
            var finalVerification = VerifyArtifact(
                request,
                proposal,
                request.OutputPlugin,
                cancellationToken);
            diagnostics.AddRange(finalVerification.Diagnostics);
            if (!finalVerification.IsValid || HasErrors(diagnostics))
            {
                TryDelete(destination);
                promoted = false;
                return new NpcOverrideResult(false, proposal, null,
                    finalVerification, diagnostics.ToImmutable());
            }
            return new NpcOverrideResult(
                true,
                proposal,
                finalVerification.OutputSha256,
                finalVerification,
                diagnostics.ToImmutable());
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(FromWriterException(exception));
            if (promoted) TryDelete(destination);
            return new NpcOverrideResult(false, proposal, null, null, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("npc-override-write-failed", exception.Message));
            if (promoted) TryDelete(destination);
            return new NpcOverrideResult(false, proposal, null, null, diagnostics.ToImmutable());
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public ValueTask<NpcOverrideVerificationResult> VerifyAsync(
        NpcOverrideRequest request,
        NpcOverrideProposal proposal,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(VerifyArtifact(
            request,
            proposal,
            request.OutputPlugin,
            cancellationToken));

    private static NpcOverrideVerificationResult VerifyArtifact(
        NpcOverrideRequest request,
        NpcOverrideProposal proposal,
        WorkspacePath artifact,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(request.SourcePlugin.Value))
            diagnostics.Add(Error("npc-override-source-missing", "The source plugin does not exist."));
        if (!File.Exists(artifact.Value))
            diagnostics.Add(Error("npc-override-output-missing", "The override plugin does not exist."));
        if (HasErrors(diagnostics))
            return RefusedVerification(request, proposal, artifact, diagnostics);

        Sha256Hash? sourceHash = null;
        Sha256Hash? outputHash = null;
        ImmutableArray<PluginName> observedMasters = [];
        var majorCount = 0;
        var npcCount = 0;
        var sourceOwnedCount = 0;
        var selfOwnedCount = 0;
        ImmutableArray<MutationChange> observedChanges = [];
        var independentPreservation = false;
        try
        {
            sourceHash = HashFile(request.SourcePlugin.Value);
            outputHash = HashFile(artifact.Value);
            if (sourceHash != request.ExpectedSourceSha256 || sourceHash != proposal.SourceSha256)
                diagnostics.Add(Error("npc-override-source-hash-mismatch",
                    "The source plugin changed after proposal analysis."));

            if (request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy)
            {
                var inventory = BethesdaNpcStandaloneCopyAdapter.Verify(
                    request.SourcePlugin,
                    artifact,
                    request.TargetFormId,
                    proposal.Changes);
                diagnostics.AddRange(inventory.Diagnostics);
                observedMasters = BethesdaNpcStandaloneCopyAdapter.ReadMasters(artifact);
                majorCount = inventory.Output.Length;
                npcCount = inventory.Output.Count(item => item.Signature == "NPC_");
                var sourceTarget = inventory.Source.SingleOrDefault(item =>
                    item.Signature == "NPC_" &&
                    (item.RawFormId & 0x00FF_FFFFu) == (request.TargetFormId.Value & 0x00FF_FFFFu));
                var outputTarget = inventory.Output.SingleOrDefault(item =>
                    item.Signature == "NPC_" &&
                    (item.RawFormId & 0x00FF_FFFFu) == (request.TargetFormId.Value & 0x00FF_FFFFu));
                if (sourceTarget is null || outputTarget is null)
                {
                    diagnostics.Add(Error("npc-standalone-target-missing",
                        "The standalone output must retain exactly one target NPC."));
                }
                else if (sourceTarget.Sha256 == outputTarget.Sha256)
                {
                    diagnostics.Add(Error("npc-standalone-target-unchanged",
                        "The standalone output did not apply the requested NPC mutation."));
                }
                if (!observedMasters.SequenceEqual(proposal.RequiredMasters))
                    diagnostics.Add(Error("npc-standalone-master-drift",
                        "The standalone output TES4 master list changed from the source list."));
                var fields = VerifyStandaloneMutation(
                    request,
                    proposal,
                    artifact,
                    cancellationToken);
                diagnostics.AddRange(fields.Diagnostics);
                var rawFields = BethesdaPluginVerifier.Verify(
                    new PluginVerificationRequest(
                        request.Edition,
                        request.SourcePlugin,
                        artifact,
                        request.TargetFormId,
                        proposal.Changes,
                        proposal.PreservedFields),
                    cancellationToken);
                diagnostics.AddRange(rawFields.Diagnostics);
                if (!rawFields.IsValid)
                    diagnostics.Add(Error("npc-standalone-field-readback",
                        "The independent raw target field reader rejected the standalone output."));
                var referenceOwnership = BethesdaNpcStandaloneCopyAdapter
                    .VerifyTargetReferenceOwnership(
                        request.SourcePlugin,
                        artifact,
                        request.TargetFormId,
                        proposal.Changes);
                diagnostics.AddRange(referenceOwnership);
                sourceOwnedCount = 0;
                selfOwnedCount = outputTarget is null ? 0 : 1;
                observedChanges = rawFields.ObservedChanges;
                independentPreservation = inventory.Verified &&
                    sourceTarget is not null && outputTarget is not null &&
                    sourceTarget.Sha256 != outputTarget.Sha256 &&
                    fields.IsValid && rawFields.IsValid && referenceOwnership.Length == 0;
            }
            else
            {
                var ownership = BethesdaNpcOverrideAdapter.InspectOwnership(
                    artifact,
                    proposal.OutputPluginName,
                    proposal.SourcePluginName,
                    request.TargetFormId);
                observedMasters = ownership.TypedMasters;
                majorCount = ownership.MajorRecordCount;
                npcCount = ownership.NpcRecordCount;
                sourceOwnedCount = ownership.SourceOwnedTargetCount;
                selfOwnedCount = ownership.SelfOwnedTargetCount;
                if (!ownership.TypedMasters.SequenceEqual(proposal.RequiredMasters) ||
                    !ownership.RawMasters.SequenceEqual(proposal.RequiredMasters))
                    diagnostics.Add(Error("npc-override-master-drift",
                        "Typed and raw TES4 master lists must exactly match the proposal."));
                if (majorCount != 1 || npcCount != 1 || sourceOwnedCount != 1 || selfOwnedCount != 0)
                    diagnostics.Add(Error("npc-override-record-surface",
                        "The output must contain exactly one source-owned target NPC override and no self-owned duplicate or unrelated major record."));

                var fields = BethesdaPluginVerifier.Verify(
                    new PluginVerificationRequest(
                        request.Edition,
                        request.SourcePlugin,
                        artifact,
                        request.TargetFormId,
                        proposal.Changes,
                        proposal.PreservedFields),
                    cancellationToken);
                diagnostics.AddRange(fields.Diagnostics);
                observedChanges = fields.ObservedChanges;
                if (!fields.IsValid)
                    diagnostics.Add(Error("npc-override-field-readback",
                        "The independent raw field reader rejected the override."));
            }
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(FromWriterException(exception));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("npc-override-verify-failed", exception.Message));
        }

        return new NpcOverrideVerificationResult(
            !HasErrors(diagnostics),
            request.SourcePlugin,
            sourceHash,
            artifact,
            outputHash,
            proposal.SourcePluginName,
            request.TargetFormId,
            proposal.RequiredMasters,
            observedMasters,
            majorCount,
            npcCount,
            sourceOwnedCount,
            selfOwnedCount,
            observedChanges,
            diagnostics.ToImmutable())
        {
            OutputKind = request.OutputKind,
            IndependentPreservation = independentPreservation
        };
    }

    private static StandaloneMutationVerification VerifyStandaloneMutation(
        NpcOverrideRequest request,
        NpcOverrideProposal proposal,
        WorkspacePath artifact,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var observed = ImmutableArray.CreateBuilder<MutationChange>();
        try
        {
            // These are fresh, independent reads of source and output. The
            // standalone inventory only proves byte preservation; this pass
            // proves that every proposed field was actually materialized in
            // the output target and that the output-owned FormLinks resolve.
            var sourceSnapshot = BethesdaNpcMutationAdapter.Read(
                request.Edition,
                request.SourcePlugin,
                request.TargetFormId);
            var outputSnapshot = BethesdaNpcMutationAdapter.Read(
                request.Edition,
                artifact,
                request.TargetFormId);
            var expected = proposal.Changes;
            var sourceChanges = NpcMutationService.BuildChanges(
                ToMutationRequest(request),
                sourceSnapshot);
            if (!sourceChanges.SequenceEqual(expected))
                diagnostics.Add(Error("npc-standalone-proposal-drift",
                    "The persisted standalone proposal no longer matches the source mutation surface."));
            foreach (var expectedChange in expected)
            {
                if (!IsStandaloneTypedField(expectedChange.Field))
                    continue;
                var actualValue = ReadStandaloneField(
                    outputSnapshot,
                    expectedChange.Field);
                if (actualValue is null ||
                    !string.Equals(
                        NormalizeStandaloneValue(
                            expectedChange.After,
                            proposal.SourcePluginName,
                            proposal.OutputPluginName),
                        actualValue,
                        StringComparison.Ordinal))
                {
                    diagnostics.Add(Error("npc-standalone-target-field-drift",
                        $"Standalone target field '{expectedChange.Field}' did not contain the requested value."));
                    continue;
                }
                observed.Add(expectedChange with { After = actualValue });
            }

            if (observed.Count != expected.Count(item =>
                    IsStandaloneTypedField(item.Field)))
                diagnostics.Add(Error("npc-standalone-target-field-drift",
                    "Standalone target scalar/reference evidence did not cover every requested scalar/reference field."));
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(FromWriterException(exception));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("npc-standalone-target-readback-failed", exception.Message));
        }

        return new StandaloneMutationVerification(
            !HasErrors(diagnostics),
            observed.ToImmutable(),
            diagnostics.ToImmutable());
    }

    private static bool IsStandaloneTypedField(string field) => field is
        "EditorID" or "Name" or "ShortName" or "Sex" or "Race" or "Voice" or
        "Class" or "CombatStyle" or "SkyrimWeight" or "Thin" or "Muscular" or
        "Fat";

    private static string? ReadStandaloneField(
        BethesdaNpcSnapshot snapshot,
        string field) => field switch
        {
            "EditorID" => snapshot.EditorId,
            "Name" => snapshot.Name,
            "ShortName" => snapshot.ShortName,
            "Sex" => snapshot.Sex?.ToString().ToLowerInvariant(),
            "Race" => FormatStandaloneReference(snapshot.Archetype?.Race),
            "Voice" => FormatStandaloneReference(snapshot.Archetype?.Voice),
            "Class" => FormatStandaloneReference(snapshot.Archetype?.Class),
            "CombatStyle" => FormatStandaloneReference(snapshot.Archetype?.CombatStyle),
            "SkyrimWeight" => FormatStandaloneFloat(snapshot.SkyrimWeight),
            "Thin" => FormatStandaloneFloat(snapshot.Thin),
            "Muscular" => FormatStandaloneFloat(snapshot.Muscular),
            "Fat" => FormatStandaloneFloat(snapshot.Fat),
            _ => null
        };

    private static string FormatStandaloneReference(FormReference? value) =>
        value?.ToString() ?? "none";

    private static string? FormatStandaloneFloat(float? value) =>
        value?.ToString("R", CultureInfo.InvariantCulture);

    private static string? NormalizeStandaloneValue(
        string? value,
        PluginName sourcePlugin,
        PluginName outputPlugin)
    {
        if (value is null) return null;
        return value
            .Replace(sourcePlugin.Value + "|", outputPlugin.Value + "|",
                StringComparison.OrdinalIgnoreCase)
            .Replace(sourcePlugin.Value + ":", outputPlugin.Value + ":",
                StringComparison.OrdinalIgnoreCase);
    }

    private sealed record StandaloneMutationVerification(
        bool IsValid,
        ImmutableArray<MutationChange> ObservedChanges,
        ImmutableArray<Diagnostic> Diagnostics);

    private ImmutableArray<Diagnostic> ValidateRequest(
        NpcOverrideRequest request,
        bool outputMustExist,
        bool proposalMustExist)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("npc-override-edition-unsupported",
                "The true override transaction supports Skyrim SE/AE only."));
        if (request.OutputKind is not ExistingNpcEditOutputKind.SourceMasteredOverride and
            not ExistingNpcEditOutputKind.StandaloneCopy)
            diagnostics.Add(Error("npc-override-output-kind-unsupported",
                "The NPC override output kind is unsupported."));
        if (!request.SourcePlugin.IsUnder(labRoot))
            diagnostics.Add(Error("npc-override-source-outside-lab",
                "The source plugin must remain under the K-only workspace."));
        if (!request.OutputPlugin.IsUnder(labRoot))
            diagnostics.Add(Error("npc-override-output-outside-lab",
                "The output plugin must remain under the K-only workspace."));
        if (!File.Exists(request.SourcePlugin.Value))
            diagnostics.Add(Error("npc-override-source-missing", "The source plugin does not exist."));
        if (request.Patch.IsEmpty)
            diagnostics.Add(Error("npc-override-empty", "At least one supported field must be requested."));
        NpcMutationService.ValidateWeight(
            request.Edition,
            request.Patch.Weight,
            diagnostics);
        NpcMutationService.ValidateStats(request.Edition, request.Patch.Stats, diagnostics);
        if (File.Exists(request.SourcePlugin.Value))
        {
            try
            {
                if (request.OutputKind == ExistingNpcEditOutputKind.StandaloneCopy)
                    BethesdaNpcStandaloneCopyAdapter.ValidateSource(
                        request.SourcePlugin,
                        request.TargetFormId);
                if (!HasErrors(diagnostics))
                {
                    var referenceClosure = BethesdaNpcMutationAdapter.ReadReferencePluginClosure(
                        request.Edition, request.SourcePlugin);
                    NpcMutationService.ValidateArchetype(
                        request.Patch.Archetype,
                        referenceClosure,
                        diagnostics);
                    diagnostics.AddRange(
                        BethesdaNpcMutationAdapter.ValidateSkyrimArchetypeTargets(
                            request.SourcePlugin,
                            request.Patch.Archetype));
                    NpcMutationService.ValidateCollectionFields(
                        ToMutationRequest(request),
                        referenceClosure,
                        diagnostics);
                }
            }
            catch (InvalidDataException exception)
            {
                diagnostics.Add(FromWriterException(exception));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(Error("npc-override-reference-closure-read-failed",
                    $"The source plugin master closure could not be read: {exception.Message}"));
            }
        }

        var sourceExtension = Path.GetExtension(request.SourcePlugin.Value);
        if (!string.Equals(sourceExtension, ".esp", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sourceExtension, ".esm", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(sourceExtension, ".esl", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("npc-override-source-extension",
                "The source plugin must use .esp, .esm, or .esl."));
        if (!request.OutputPlugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("npc-override-output-extension",
                "The bounded override writer emits one ordinary .esp plugin."));
        if (string.Equals(Path.GetFileName(request.SourcePlugin.Value),
                Path.GetFileName(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("npc-override-name-collision",
                "The override plugin must have a different filename from its source plugin."));

        var outputParent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (outputParent is null || !Directory.Exists(outputParent))
            diagnostics.Add(Error("npc-override-output-parent-missing",
                "The output plugin parent directory must already exist."));
        else
        {
            diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(outputParent)));
            AddReparseDiagnostic(diagnostics, outputParent, "output parent");
        }
        AddReparseDiagnostic(diagnostics, request.SourcePlugin.Value, "source plugin");
        if (outputMustExist && !File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("npc-override-output-missing", "The override plugin does not exist."));
        if (!outputMustExist && File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(Error("npc-override-output-exists",
                "The output plugin already exists; override writes never overwrite."));

        if (request.ProposalPath is { } proposalPath)
        {
            if (!proposalPath.IsUnder(labRoot))
                diagnostics.Add(Error("npc-override-proposal-outside-lab",
                    "The proposal must remain under the K-only workspace."));
            var proposalParent = Path.GetDirectoryName(proposalPath.Value);
            if (proposalParent is null || !Directory.Exists(proposalParent))
                diagnostics.Add(Error("npc-override-proposal-parent-missing",
                    "The proposal parent directory must already exist."));
            else AddReparseDiagnostic(diagnostics, proposalParent, "proposal parent");
            if (proposalMustExist && !File.Exists(proposalPath.Value))
                diagnostics.Add(Error("npc-override-proposal-missing",
                    "Apply requires the persisted proposal file."));
            if (!proposalMustExist && File.Exists(proposalPath.Value))
                diagnostics.Add(Error("npc-override-proposal-exists",
                    "Proposal analysis never overwrites an existing file."));
        }
        else if (proposalMustExist)
        {
            diagnostics.Add(Error("npc-override-proposal-required",
                "Apply requires a persisted proposal path."));
        }
        return diagnostics.ToImmutable();
    }

    private static void ValidateProposalBinding(
        NpcOverrideRequest request,
        NpcOverrideProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!proposal.IsApplicable || proposal.SchemaVersion != 1)
            diagnostics.Add(Error("npc-override-proposal-inapplicable",
                "The supplied proposal is not an applicable schema-1 override proposal."));
        if (proposal.Edition != request.Edition ||
            proposal.OutputKind != request.OutputKind ||
            proposal.SourcePlugin != request.SourcePlugin ||
            proposal.SourceSha256 != request.ExpectedSourceSha256 ||
            proposal.TargetFormId != request.TargetFormId ||
            proposal.ProposalPath != request.ProposalPath ||
            proposal.OutputPlugin != request.OutputPlugin ||
            proposal.SourcePluginName != new PluginName(Path.GetFileName(request.SourcePlugin.Value)) ||
            proposal.OutputPluginName != new PluginName(Path.GetFileName(request.OutputPlugin.Value)) ||
            !NpcOverridePatchEquality.Equals(proposal.Patch, request.Patch))
        {
            diagnostics.Add(Error("npc-override-proposal-request-mismatch",
                "The proposal is not bound to this exact source, target, output, and proposal path."));
        }
        if (request.ProposalPath is { } path)
        {
            Sha256Hash? hash = File.Exists(path.Value) ? HashFile(path.Value) : null;
            if (proposal.ProposalSha256 is null || hash != proposal.ProposalSha256)
                diagnostics.Add(Error("npc-override-proposal-hash-mismatch",
                    "The persisted proposal bytes do not match the analyzed proposal hash."));
        }
    }

    private static NpcMutationRequest ToMutationRequest(NpcOverrideRequest request) =>
        new(
            request.Edition,
            request.SourcePlugin,
            request.OutputPlugin,
            request.TargetFormId,
            request.Patch.EditorId,
            request.Patch.Name,
            request.Patch.Weight,
            request.ExpectedSourceSha256,
            false,
            null,
            Archetype: request.Patch.Archetype,
            Stats: request.Patch.Stats,
            KeywordPatch: request.Patch.Keywords,
            FactionPatch: request.Patch.Factions,
            InventoryPatch: request.Patch.Inventory,
            OutfitPatch: request.Patch.Outfits,
            PerkPatch: request.Patch.Perks,
            ActorEffectPatch: request.Patch.ActorEffects,
            Names: request.Patch.Names);

    private static Diagnostic FromWriterException(InvalidDataException exception)
    {
        var message = exception.Message;
        var separator = message.IndexOf(':');
        if (separator > 0)
        {
            var code = message[..separator];
            if (code.All(character => char.IsLetterOrDigit(character) || character == '-' || character == '_'))
                return Error(code, message[(separator + 1)..].Trim());
        }
        return Error("npc-override-write-invalid-data", message);
    }

    private static async ValueTask<Sha256Hash> WriteProposalAsync(
        NpcOverrideProposal proposal,
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = new UTF8Encoding(false).GetBytes(
                JsonSerializer.Serialize(proposal, JsonOptions));
            await using (var stream = new FileStream(temporary, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None, 16 * 1024,
                             FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path.Value, overwrite: false);
            return HashFile(path.Value);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static NpcOverrideVerificationResult RefusedVerification(
        NpcOverrideRequest request,
        NpcOverrideProposal proposal,
        WorkspacePath artifact,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            false,
            request.SourcePlugin,
            null,
            artifact,
            null,
            proposal.SourcePluginName,
            request.TargetFormId,
            proposal.RequiredMasters,
            [],
            0,
            0,
            0,
            0,
            [],
            diagnostics.ToImmutable())
        {
            OutputKind = request.OutputKind,
            IndependentPreservation = false
        };

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.WriteThrough);
        if (stream.Length <= 0) throw new IOException("The override writer produced an empty plugin.");
        stream.Flush(flushToDisk: true);
    }

    private static void AddReparseDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) &&
                    File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("npc-override-reparse-refused",
                        $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("npc-override-path-inspection-failed", exception.Message));
                return;
            }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) return;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
