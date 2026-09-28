using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class NpcMutationService(IWorkspacePolicy policy, WorkspacePath labRoot) : INpcMutationService
{
    private readonly Action<WorkspacePath>? candidateVerificationFault;

    internal NpcMutationService(IWorkspacePolicy policy, WorkspacePath labRoot,
        Action<WorkspacePath> candidateVerificationFault)
        : this(policy, labRoot) => this.candidateVerificationFault = candidateVerificationFault;

    private static readonly JsonSerializerOptions ProposalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static readonly ImmutableArray<string> PreservedFields =
        ["EDID", "FULL", "SHRT", "OBND", "ACBS", "RNAM", "VTCK", "AIDT", "CNAM", "ZNAM", "DATA", "NAM5", "NAM6", "NAM7", "MWGT", "MRSV", "KWDA", "APPR", "SNAM", "CNTO", "DOFT", "SOFT", "WNAM", "PRKR", "SPLO", "PRPS"];

    internal static void ValidateCollectionFields(
        NpcMutationRequest request,
        ImmutableHashSet<string> referencePlugins,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        ValidateKeywords(request, request.KeywordPatch, diagnostics, referencePlugins);
        ValidateFactions(request, request.FactionPatch, diagnostics, referencePlugins);
        ValidateInventory(request, request.InventoryPatch, diagnostics, referencePlugins);
        ValidateOutfits(request, request.OutfitPatch, diagnostics, referencePlugins);
        ValidatePerks(request, request.PerkPatch, diagnostics, referencePlugins);
        ValidateActorEffects(request, request.ActorEffectPatch, diagnostics, referencePlugins);
        ValidateProperties(request, request.PropertyPatch, diagnostics, referencePlugins);
    }

    public ValueTask<NpcMutationProposal> AnalyzeAsync(NpcMutationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireExpectedHash: false, allowExistingProposal: false);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return ValueTask.FromResult(FailedProposal(request, diagnostics));

        var inputHash = ComputeHash(request.InputPlugin.Value);
        if (request.ExpectedInputHash is { } expected && expected != inputHash)
            diagnostics = diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error,
                $"Input hash {inputHash} does not match the expected hash {expected}."));

        BethesdaNpcSnapshot? snapshot = null;
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            try
            {
                snapshot = BethesdaNpcMutationAdapter.Read(request.Edition, request.InputPlugin, request.TargetFormId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics = diagnostics.Add(new Diagnostic("npc-read-failed", DiagnosticSeverity.Error,
                    $"NPC {request.TargetFormId} could not be read: {exception.Message}"));
            }
        }

        ImmutableArray<MutationChange> changes;
        if (snapshot is not null) diagnostics = diagnostics.AddRange(ValidateAidtSource(request, snapshot));
        try
        {
            changes = snapshot is null ? ImmutableArray<MutationChange>.Empty : BuildChanges(request, snapshot);
        }
        catch (OverflowException)
        {
            diagnostics = diagnostics.Add(new Diagnostic("inventory-count-overflow", DiagnosticSeverity.Error,
                "Inventory count merging would exceed the signed 32-bit CNTO range."));
            changes = ImmutableArray<MutationChange>.Empty;
        }
        if (snapshot is not null && request.FactionPatch is { Replace: null } &&
            snapshot.Factions is { } factionSnapshot &&
            factionSnapshot.Factions.Select(entry => entry.Faction).Distinct().Count() != factionSnapshot.Factions.Length)
            diagnostics = diagnostics.Add(new Diagnostic("faction-source-duplicate", DiagnosticSeverity.Error,
                "The source NPC contains duplicate faction memberships; use an explicit replacement to repair the list."));
        if (snapshot is not null && request.PerkPatch is { Replace: null } &&
            snapshot.Perks is { } perkSnapshot &&
            perkSnapshot.Perks.Select(entry => entry.Perk).Distinct().Count() != perkSnapshot.Perks.Length)
            diagnostics = diagnostics.Add(new Diagnostic("perk-source-duplicate", DiagnosticSeverity.Error,
                "The source NPC contains duplicate perk entries; use an explicit replacement to repair the list."));
        if (snapshot is not null && request.ActorEffectPatch is { Replace: null } &&
            snapshot.ActorEffects is { } actorEffectSnapshot &&
            actorEffectSnapshot.ActorEffects.Distinct().Count() != actorEffectSnapshot.ActorEffects.Length)
            diagnostics = diagnostics.Add(new Diagnostic("actor-effect-source-duplicate", DiagnosticSeverity.Error,
                "The source NPC contains duplicate actor-effect references; use an explicit replacement to repair the list."));
        if (snapshot is not null && request.PropertyPatch is { Replace: null } &&
            snapshot.Properties is { } propertySnapshot &&
            propertySnapshot.Properties.Select(entry => entry.ActorValue).Distinct().Count() != propertySnapshot.Properties.Length)
            diagnostics = diagnostics.Add(new Diagnostic("property-source-duplicate", DiagnosticSeverity.Error,
                "The source NPC contains duplicate PRPS actor-value entries; use an explicit replacement to repair the list."));
        NpcWholeSkinPlan? wholeSkin = null;
        if (request.WholeSkin is not null && !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            try
            {
                wholeSkin = BethesdaNpcMutationAdapter.PlanWholeSkin(request);
                changes = changes.Add(new MutationChange("WholeSkin", null, wholeSkin.Patch.DocumentSha256.Value));
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics = diagnostics.Add(new Diagnostic("npc-whole-skin-source", DiagnosticSeverity.Error, exception.Message));
            }
        }
        if (changes.Length == 0 && !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            diagnostics = diagnostics.Add(new Diagnostic("no-changes", DiagnosticSeverity.Error, "At least one supported NPC field must change."));

        var proposalDiagnostics = diagnostics.ToBuilder();
        var proposal = new NpcMutationProposal(request.Edition, request.InputPlugin, request.OutputPlugin,
            request.TargetFormId, inputHash, changes,
            changes.Any(change => change.Field.StartsWith("AIDT:", StringComparison.Ordinal)) ? PreservedFields.Remove("AIDT") : PreservedFields,
            proposalDiagnostics.ToImmutable()) { WholeSkin = wholeSkin, Aidt = request.Aidt };
        if (request.ProposalPath is { } proposalPath && !proposalDiagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            WriteProposal(proposalPath, proposal, proposalDiagnostics);
            proposal = proposal with { Diagnostics = proposalDiagnostics.ToImmutable() };
        }
        return ValueTask.FromResult(proposal);
    }

    public async ValueTask<NpcMutationResult> ApplyAsync(NpcMutationRequest request, NpcMutationProposal proposal, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(ValidateRequest(request, requireExpectedHash: true, allowExistingProposal: true));
        if (request.WholeSkin?.DocumentSha256 != proposal.WholeSkin?.Patch.DocumentSha256)
            diagnostics.Add(new Diagnostic("proposal-request-mismatch", DiagnosticSeverity.Error, "wholeSkin proposal is not bound to this request document."));
        if (request.Aidt != proposal.Aidt)
            diagnostics.Add(new Diagnostic("proposal-request-mismatch", DiagnosticSeverity.Error, "AIDT proposal is not bound to this request intent."));
        if (request.DryRun) diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info, "Dry-run requested; no plugin was written."));
        if (File.Exists(request.InputPlugin.Value) && proposal.InputHash != ComputeHash(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error, "The input changed after proposal analysis."));
        if (!string.Equals(proposal.InputPlugin.Value, request.InputPlugin.Value, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(proposal.OutputPlugin.Value, request.OutputPlugin.Value, StringComparison.OrdinalIgnoreCase) ||
            proposal.TargetFormId != request.TargetFormId || proposal.Edition != request.Edition)
            diagnostics.Add(new Diagnostic("proposal-request-mismatch", DiagnosticSeverity.Error, "The proposal is not bound to this request."));

        if (request.DryRun || diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new NpcMutationResult(false, proposal, null, diagnostics.ToImmutable());

        var destination = request.OutputPlugin.Value;
        string outputParent = Path.GetDirectoryName(destination)!;
        string stagingDirectory = Path.Combine(outputParent, ".actorwright-npc-patch-" + Guid.NewGuid().ToString("N"));
        var temporary = Path.Combine(stagingDirectory, Path.GetFileName(destination));
        try
        {
            Directory.CreateDirectory(stagingDirectory);
            BethesdaNpcMutationAdapter.Write(request, new WorkspacePath(temporary));
            if (!File.Exists(temporary)) throw new IOException("The mutation adapter did not produce an output file.");
            FlushOutput(temporary);

            var verification = await VerifyAsync(new PluginVerificationRequest(request.Edition, request.InputPlugin,
                new WorkspacePath(temporary), request.TargetFormId, proposal.Changes, proposal.PreservedFields) { WholeSkin = request.WholeSkin }, cancellationToken);
            diagnostics.AddRange(verification.Diagnostics);
            if (!verification.IsValid)
                return new NpcMutationResult(false, proposal, null, diagnostics.ToImmutable());

            var result = new NpcMutationResult(true, proposal, ComputeHash(temporary), diagnostics.ToImmutable());
            File.Move(temporary, destination, overwrite: false);
            return result;
        }
        catch (IOException exception)
        {
            diagnostics.Add(new Diagnostic("output-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new NpcMutationResult(false, proposal, null, diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("mutation-failed", DiagnosticSeverity.Error, exception.Message));
            return new NpcMutationResult(false, proposal, null, diagnostics.ToImmutable());
        }
        finally
        {
            TryDelete(temporary);
            try { if (Directory.Exists(stagingDirectory)) Directory.Delete(stagingDirectory); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public ValueTask<PluginVerificationResult> VerifyAsync(PluginVerificationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var validation = NpcMutationPathPolicy.ValidateReadOnlyPaths(policy, labRoot,
            request.SourcePlugin, request.OutputPlugin).ToBuilder();
        ValidateWholeSkin(request.WholeSkin, request.Edition, request.SourcePlugin,
            request.TargetFormId, validation);
        if (request.WholeSkin is { PreservesHead: true } &&
            !string.Equals(Path.GetFileName(request.SourcePlugin.Value), Path.GetFileName(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase))
            validation.Add(new Diagnostic("npc-whole-skin-identity", DiagnosticSeverity.Error,
                "headPolicy preserve verification requires the same logical plugin filename."));
        var diagnostics = validation.ToImmutable();
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return ValueTask.FromResult(new PluginVerificationResult(false, diagnostics, ImmutableArray<MutationChange>.Empty));
        candidateVerificationFault?.Invoke(request.OutputPlugin);
        return ValueTask.FromResult(BethesdaPluginVerifier.Verify(request, cancellationToken));
    }

    private ImmutableArray<Diagnostic> ValidateRequest(NpcMutationRequest request, bool requireExpectedHash, bool allowExistingProposal)
    {
        var diagnostics = NpcMutationPathPolicy.ValidateReadOnlyPaths(policy, labRoot,
            request.InputPlugin, request.OutputPlugin).ToBuilder();
        if (!File.Exists(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("input-plugin-missing", DiagnosticSeverity.Error, "The explicit input plugin does not exist."));
        var outputParent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (outputParent is null || !Directory.Exists(outputParent))
            diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error, "The explicit output directory must already exist."));
        if (File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "The output path already exists; mutation never overwrites an artifact."));
        if (string.Equals(request.InputPlugin.Value, request.OutputPlugin.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("input-output-same", DiagnosticSeverity.Error, "Input and output plugins must be different files."));
        if (requireExpectedHash && request.ExpectedInputHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256 to bind the write to an observed input."));
        if (!NpcMutationPathPolicy.IsPluginPath(request.InputPlugin.Value) ||
            !NpcMutationPathPolicy.IsPluginPath(request.OutputPlugin.Value))
            diagnostics.Add(new Diagnostic("plugin-extension-invalid", DiagnosticSeverity.Error, "Input and output must use .esp, .esm, or .esl extensions."));
        if (!string.Equals(Path.GetExtension(request.InputPlugin.Value), Path.GetExtension(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("plugin-extension-mismatch", DiagnosticSeverity.Error, "Input and output plugin extensions must match."));
        if (request.EditorId is null && request.Name is null && request.Sex is null &&
            (request.Archetype is null || request.Archetype.IsEmpty) &&
            (request.Weight is null || request.Weight.IsEmpty) &&
            (request.Stats is null || request.Stats.IsEmpty) &&
            (request.KeywordPatch is null || request.KeywordPatch.IsEmpty) &&
            (request.FactionPatch is null || request.FactionPatch.IsEmpty) &&
            (request.InventoryPatch is null || request.InventoryPatch.IsEmpty) &&
            (request.OutfitPatch is null || request.OutfitPatch.IsEmpty) &&
            (request.PerkPatch is null || request.PerkPatch.IsEmpty) &&
            (request.ActorEffectPatch is null || request.ActorEffectPatch.IsEmpty) &&
            (request.PropertyPatch is null || request.PropertyPatch.IsEmpty) &&
            (request.Skin is null || request.Skin.IsEmpty) &&
            (request.BodyMorphs is null || request.BodyMorphs.IsEmpty) &&
            (request.Names is null || request.Names.IsEmpty) && request.WholeSkin is null && (request.Aidt is null || request.Aidt.IsEmpty))
            diagnostics.Add(new Diagnostic("mutation-empty", DiagnosticSeverity.Error, "No supported mutation fields were supplied."));
        ValidateWeight(request.Edition, request.Weight, diagnostics);
        ValidateAidt(request, diagnostics);
        ValidateEditableNames(request, diagnostics);
        ValidateStats(request.Edition, request.Stats, diagnostics);
        var referencePlugins = ImmutableHashSet.Create(
            StringComparer.OrdinalIgnoreCase,
            Path.GetFileName(request.InputPlugin.Value));
        if (File.Exists(request.InputPlugin.Value) &&
            (HasCollectionFields(request) || request.Archetype is { IsEmpty: false }))
        {
            try
            {
                referencePlugins = BethesdaNpcMutationAdapter.ReadReferencePluginClosure(
                    request.Edition, request.InputPlugin);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("reference-closure-read-failed",
                    DiagnosticSeverity.Error,
                    $"The source plugin master closure could not be read: {exception.Message}"));
            }
        }
        ValidateArchetype(request.Archetype, referencePlugins, diagnostics);
        ValidateCollectionFields(request, referencePlugins, diagnostics);
        ValidateSkin(request, request.Skin, diagnostics);
        ValidateBodyMorphs(request, request.BodyMorphs, diagnostics);
        ValidateWholeSkin(request.WholeSkin, request.Edition, request.InputPlugin,
            request.TargetFormId, diagnostics);
        if (request.WholeSkin is { PreservesHead: true })
        {
            if (!string.Equals(Path.GetFileName(request.InputPlugin.Value), Path.GetFileName(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("npc-whole-skin-identity", DiagnosticSeverity.Error,
                    "headPolicy preserve requires a fresh output location with the same logical plugin filename."));
            if (HasPreserveModeConflict(request))
                diagnostics.Add(new Diagnostic("npc-whole-skin-conflict", DiagnosticSeverity.Error,
                    "headPolicy preserve is a WNAM-only transaction and refuses simultaneous appearance or scalar edits."));
        }
        if (request.ProposalPath is { } proposalPath)
        {
            var proposalParent = Path.GetDirectoryName(proposalPath.Value);
            if (proposalParent is null || !Directory.Exists(proposalParent))
                diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error, "The proposal directory must already exist."));
            if (File.Exists(proposalPath.Value) && !allowExistingProposal)
                diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error, "The proposal path already exists; proposals never overwrite artifacts."));
            if (!proposalPath.IsUnder(labRoot))
                diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "The proposal path must remain under the K-only lab root."));
            if (string.Equals(proposalPath.Value, request.InputPlugin.Value, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(proposalPath.Value, request.OutputPlugin.Value, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("proposal-path-collision", DiagnosticSeverity.Error, "The proposal path must be distinct from both plugin paths."));
        }
        return diagnostics.ToImmutable();
    }

    private static bool HasCollectionFields(NpcMutationRequest request) =>
        (request.KeywordPatch is not null && !request.KeywordPatch.IsEmpty) ||
        (request.FactionPatch is not null && !request.FactionPatch.IsEmpty) ||
        (request.InventoryPatch is not null && !request.InventoryPatch.IsEmpty) ||
        (request.OutfitPatch is not null && !request.OutfitPatch.IsEmpty) ||
        (request.PerkPatch is not null && !request.PerkPatch.IsEmpty) ||
        (request.ActorEffectPatch is not null && !request.ActorEffectPatch.IsEmpty) ||
        (request.PropertyPatch is not null && !request.PropertyPatch.IsEmpty);

    private static bool HasPreserveModeConflict(NpcMutationRequest request) =>
        request.EditorId is not null || request.Name is not null || request.Sex is not null ||
        request.Names is { IsEmpty: false } || request.Archetype is { IsEmpty: false } ||
        request.Weight is { IsEmpty: false } || request.Stats is { IsEmpty: false } ||
        HasCollectionFields(request) || request.Skin is not null ||
        request.BodyMorphs is { IsEmpty: false } || request.Aidt is not null;

}
