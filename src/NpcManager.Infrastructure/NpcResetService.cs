using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

/// <summary>
/// Resets one typed NPC section from a same-plugin baseline snapshot. The baseline is explicit
/// because a stateless CLI cannot infer the in-memory snapshot held by the desktop editor.
/// </summary>
public sealed class NpcResetService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    INpcMutationService mutationService) : INpcResetService
{
    private static readonly ImmutableArray<string> EmptyPreservedFields = [];
    private static readonly JsonSerializerOptions ProposalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async ValueTask<NpcResetProposal> AnalyzeAsync(NpcResetRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = Validate(request, requireExpectedHash: false, allowExistingProposal: false).ToBuilder();
        var currentHash = HashOrZero(request.CurrentPlugin.Value, diagnostics, "current-plugin");
        var baselineHash = HashOrZero(request.BaselinePlugin.Value, diagnostics, "baseline-plugin");
        BethesdaNpcSnapshot? current = null;
        BethesdaNpcSnapshot? baseline = null;
        if (!HasErrors(diagnostics))
        {
            try
            {
                current = BethesdaNpcMutationAdapter.Read(request.Edition, request.CurrentPlugin, request.TargetFormId);
                baseline = BethesdaNpcMutationAdapter.Read(request.Edition, request.BaselinePlugin, request.TargetFormId);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("npc-reset-read-failed", DiagnosticSeverity.Error,
                    $"The current and baseline NPC records could not be read: {exception.Message}"));
            }
        }

        var changes = ImmutableArray<MutationChange>.Empty;
        var preserved = EmptyPreservedFields;
        if (current is not null && baseline is not null && !HasErrors(diagnostics))
        {
            if (SectionEqual(request.Section, current, baseline))
            {
                diagnostics.Add(new Diagnostic("reset-noop", DiagnosticSeverity.Info,
                    $"NPC section '{request.Section.ToWireName()}' already matches the baseline."));
            }
            else
            {
                var mutation = BuildMutationRequest(request, current, baseline, diagnostics);
                if (mutation is not null && !HasErrors(diagnostics))
                {
                    var mutationProposal = await mutationService.AnalyzeAsync(mutation, cancellationToken);
                    diagnostics.AddRange(mutationProposal.Diagnostics);
                    changes = mutationProposal.Changes;
                    preserved = mutationProposal.PreservedFields;
                }
            }
        }

        var proposal = new NpcResetProposal(request.Edition, request.CurrentPlugin, request.BaselinePlugin,
            request.OutputPlugin, request.TargetFormId, request.Section, currentHash, baselineHash,
            changes, preserved, diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !HasErrors(diagnostics))
        {
            WriteProposal(proposalPath, proposal, diagnostics);
            proposal = proposal with { Diagnostics = diagnostics.ToImmutable() };
        }
        return proposal;
    }

    public async ValueTask<NpcResetResult> ApplyAsync(NpcResetRequest request, NpcResetProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(Validate(request, requireExpectedHash: true, allowExistingProposal: true));
        if (request.DryRun)
            diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info, "Dry-run requested; no plugin was written."));
        if (!File.Exists(request.CurrentPlugin.Value) || !string.Equals(HashOrZero(request.CurrentPlugin.Value, diagnostics, "current-plugin").Value,
                proposal.CurrentHash.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error,
                "The current plugin changed after reset analysis."));
        if (!File.Exists(request.BaselinePlugin.Value) || !string.Equals(HashOrZero(request.BaselinePlugin.Value, diagnostics, "baseline-plugin").Value,
                proposal.BaselineHash.Value, StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("baseline-hash-mismatch", DiagnosticSeverity.Error,
                "The baseline plugin changed after reset analysis."));
        if (request.ExpectedCurrentHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error,
                "Apply requires --expected-sha256 to bind the reset to an observed current plugin."));
        if (proposal.Section != request.Section || proposal.TargetFormId != request.TargetFormId ||
            proposal.Edition != request.Edition ||
            !SamePath(proposal.CurrentPlugin, request.CurrentPlugin) ||
            !SamePath(proposal.BaselinePlugin, request.BaselinePlugin) ||
            !SamePath(proposal.OutputPlugin, request.OutputPlugin))
            diagnostics.Add(new Diagnostic("proposal-request-mismatch", DiagnosticSeverity.Error,
                "The reset proposal is not bound to this request."));

        if (request.DryRun || HasErrors(diagnostics))
            return new NpcResetResult(false, proposal, null, diagnostics.ToImmutable());
        if (!proposal.IsApplicable)
            return new NpcResetResult(false, proposal, null, diagnostics.ToImmutable());

        var current = BethesdaNpcMutationAdapter.Read(request.Edition, request.CurrentPlugin, request.TargetFormId);
        var baseline = BethesdaNpcMutationAdapter.Read(request.Edition, request.BaselinePlugin, request.TargetFormId);
        var mutation = BuildMutationRequest(request, current, baseline, diagnostics);
        if (mutation is null || HasErrors(diagnostics))
            return new NpcResetResult(false, proposal, null, diagnostics.ToImmutable());

        var mutationProposal = await mutationService.AnalyzeAsync(mutation, cancellationToken);
        diagnostics.AddRange(mutationProposal.Diagnostics);
        if (HasErrors(diagnostics))
            return new NpcResetResult(false, proposal, null, diagnostics.ToImmutable());
        var mutationResult = await mutationService.ApplyAsync(mutation, mutationProposal, cancellationToken);
        diagnostics.AddRange(mutationResult.Diagnostics);
        if (!mutationResult.Applied)
            return new NpcResetResult(false, proposal, null, diagnostics.ToImmutable());

        try
        {
            var output = BethesdaNpcMutationAdapter.Read(request.Edition, request.OutputPlugin, request.TargetFormId);
            if (!SectionEqual(request.Section, output, baseline))
                diagnostics.Add(new Diagnostic("reset-section-mismatch", DiagnosticSeverity.Error,
                    "The output section does not equal the requested baseline section."));
            foreach (var section in Enum.GetValues<NpcResetSection>())
            {
                if (section == request.Section) continue;
                if (!SectionEqual(section, output, current))
                    diagnostics.Add(new Diagnostic("reset-unrelated-drift", DiagnosticSeverity.Error,
                        $"The unrelated '{section.ToWireName()}' section changed during reset."));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("reset-verify-failed", DiagnosticSeverity.Error, exception.Message));
        }

        if (HasErrors(diagnostics))
        {
            TryDelete(request.OutputPlugin.Value);
            return new NpcResetResult(false, proposal, null, diagnostics.ToImmutable());
        }
        return new NpcResetResult(true, proposal, HashOrZero(request.OutputPlugin.Value, diagnostics, "output-plugin"), diagnostics.ToImmutable());
    }

    private static NpcMutationRequest? BuildMutationRequest(NpcResetRequest request, BethesdaNpcSnapshot current,
        BethesdaNpcSnapshot baseline, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        EditorId? editorId = null;
        if (baseline.EditorId is { } baselineEditorId) editorId = new EditorId(baselineEditorId);
        else RequireBaselineValue(current.EditorId, "EditorID", diagnostics);
        NpcName? name = null;
        if (baseline.Name is { } baselineName) name = new NpcName(baselineName);
        else RequireBaselineValue(current.Name, "Name", diagnostics);
        NpcWeightPatch? weight = null;
        NpcStatsPatch? stats = null;
        NpcKeywordPatch? keywords = null;
        NpcFactionPatch? factions = null;
        NpcInventoryPatch? inventory = null;
        NpcOutfitPatch? outfits = null;
        NpcPerkPatch? perks = null;
        NpcActorEffectPatch? actorEffects = null;
        NpcPropertyPatch? properties = null;
        NpcArchetypePatch? archetype = null;
        EditorId? identityEditorId = null;
        NpcName? identityName = null;
        NpcSex? identitySex = null;

        switch (request.Section)
        {
            case NpcResetSection.Identity:
                identityEditorId = editorId;
                identityName = name;
                identitySex = baseline.Sex;
                break;
            case NpcResetSection.Archetype:
                archetype = BuildArchetypePatch(current.Archetype, baseline.Archetype, diagnostics);
                break;
            case NpcResetSection.Weight:
                weight = BuildWeightPatch(request.Edition, current, baseline, diagnostics);
                break;
            case NpcResetSection.Stats:
                stats = BuildStatsPatch(request.Edition, current.Stats, baseline.Stats, diagnostics);
                break;
            case NpcResetSection.Keywords:
                keywords = new NpcKeywordPatch(
                    new NpcKeywordListPatch(baseline.Keywords?.Keywords ?? [], [], []),
                    request.Edition == GameEdition.Fallout4
                        ? new NpcKeywordListPatch(baseline.Keywords?.AttachParentSlots ?? [], [], [])
                        : null);
                break;
            case NpcResetSection.Factions:
                factions = new NpcFactionPatch(baseline.Factions?.Factions ?? [], [], [], []);
                break;
            case NpcResetSection.Inventory:
                inventory = new NpcInventoryPatch(baseline.Inventory?.Items ?? [], [], [], []);
                break;
            case NpcResetSection.Outfits:
                outfits = new NpcOutfitPatch(ToOptional(baseline.Outfits?.DefaultOutfit), ToOptional(baseline.Outfits?.SleepingOutfit));
                break;
            case NpcResetSection.Perks:
                perks = new NpcPerkPatch(baseline.Perks?.Perks ?? [], [], [], []);
                break;
            case NpcResetSection.ActorEffects:
                actorEffects = new NpcActorEffectPatch(baseline.ActorEffects?.ActorEffects ?? [], [], []);
                break;
            case NpcResetSection.Properties:
                if (request.Edition != GameEdition.Fallout4)
                    diagnostics.Add(new Diagnostic("skyrim-properties-unsupported", DiagnosticSeverity.Error,
                        "NPC PRPS properties are Fallout 4-only in the pinned upstream editor."));
                properties = new NpcPropertyPatch(baseline.Properties?.Properties ?? [], [], [], []);
                break;
            default:
                diagnostics.Add(new Diagnostic("reset-section-unsupported", DiagnosticSeverity.Error,
                    $"NPC reset section {request.Section} is unsupported."));
                break;
        }

        if (HasErrors(diagnostics)) return null;
        return new NpcMutationRequest(request.Edition, request.CurrentPlugin, request.OutputPlugin, request.TargetFormId,
            request.Section == NpcResetSection.Identity ? identityEditorId : null,
            request.Section == NpcResetSection.Identity ? identityName : null,
            weight, request.ExpectedCurrentHash, request.DryRun, null, identitySex, archetype, stats, keywords,
            factions, inventory, outfits, perks, actorEffects, properties);
    }

    private static NpcArchetypePatch? BuildArchetypePatch(NpcArchetypeReferences? current,
        NpcArchetypeReferences? baseline, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (baseline is null || baseline.Race is null)
        {
            if (current?.Race is not null)
                diagnostics.Add(new Diagnostic("reset-race-clear-unsupported", DiagnosticSeverity.Error,
                    "The typed NPC writer cannot clear a required race reference."));
            return baseline is null ? null : new NpcArchetypePatch(OptionalFormReference.Clear(), ToOptional(baseline.Voice),
                ToOptional(baseline.Class), ToOptional(baseline.CombatStyle));
        }
        return new NpcArchetypePatch(OptionalFormReference.Set(baseline.Race.Value), ToOptional(baseline.Voice),
            ToOptional(baseline.Class), ToOptional(baseline.CombatStyle));
    }

    private static NpcWeightPatch? BuildWeightPatch(GameEdition edition, BethesdaNpcSnapshot current,
        BethesdaNpcSnapshot baseline, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (edition == GameEdition.SkyrimSpecialEdition)
        {
            if (baseline.SkyrimWeight is null && current.SkyrimWeight is not null)
            {
                diagnostics.Add(new Diagnostic("reset-weight-clear-unsupported", DiagnosticSeverity.Error,
                    "The typed Skyrim writer cannot clear an authored scalar weight."));
                return null;
            }
            return new NpcWeightPatch(baseline.SkyrimWeight, null, null, null);
        }

        if ((baseline.Thin is null && current.Thin is not null) ||
            (baseline.Muscular is null && current.Muscular is not null) ||
            (baseline.Fat is null && current.Fat is not null))
        {
            diagnostics.Add(new Diagnostic("reset-weight-clear-unsupported", DiagnosticSeverity.Error,
                "The typed Fallout 4 writer cannot clear one component of an absent weight triangle."));
            return null;
        }
        return new NpcWeightPatch(null, baseline.Thin, baseline.Muscular, baseline.Fat);
    }

    private static NpcStatsPatch? BuildStatsPatch(GameEdition edition, NpcStatsSnapshot? current,
        NpcStatsSnapshot? baseline, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (current is null || baseline is null)
        {
            diagnostics.Add(new Diagnostic("reset-stats-missing", DiagnosticSeverity.Error,
                "Both current and baseline statistics must be present."));
            return null;
        }
        if (baseline.Height is null && current.Height is not null)
            diagnostics.Add(new Diagnostic("reset-height-clear-unsupported", DiagnosticSeverity.Error,
                "The typed Skyrim writer cannot clear an authored height."));
        if (baseline.PlayerSkills is null && current.PlayerSkills is not null)
            diagnostics.Add(new Diagnostic("reset-player-skills-clear-unsupported", DiagnosticSeverity.Error,
                "The typed Skyrim writer cannot clear an authored player-skills block."));
        if (baseline.PlayerSkills is not null && current.PlayerSkills is not null &&
            (!baseline.PlayerSkills.Values.Keys.ToHashSet().SetEquals(current.PlayerSkills.Values.Keys) ||
             !baseline.PlayerSkills.Offsets.Keys.ToHashSet().SetEquals(current.PlayerSkills.Offsets.Keys)))
            diagnostics.Add(new Diagnostic("reset-player-skills-shape-unsupported", DiagnosticSeverity.Error,
                "Resetting player skills requires matching skill-key shapes in current and baseline records."));
        if (HasErrors(diagnostics)) return null;

        var setFlags = baseline.Flags.Except(current.Flags).ToImmutableArray();
        var clearFlags = current.Flags.Except(baseline.Flags).ToImmutableArray();
        var skills = baseline.PlayerSkills is null ? null : new NpcPlayerSkillsPatch(
            baseline.PlayerSkills.Health, baseline.PlayerSkills.Magicka, baseline.PlayerSkills.Stamina,
            baseline.PlayerSkills.Values, baseline.PlayerSkills.Offsets,
            baseline.PlayerSkills.FarAwayModelDistance, baseline.PlayerSkills.GearedUpWeapons);
        return edition == GameEdition.Fallout4
            ? new NpcStatsPatch(baseline.Level, baseline.XpValueOffset, null, null, null, baseline.CalcMinLevel,
                baseline.CalcMaxLevel, null, baseline.DispositionBase, baseline.BleedoutOverride, null, null,
                new NpcFlagPatch(setFlags, clearFlags))
            : new NpcStatsPatch(baseline.Level, null, baseline.MagickaOffset, baseline.StaminaOffset,
                baseline.HealthOffset, baseline.CalcMinLevel, baseline.CalcMaxLevel, baseline.SpeedMultiplier,
                baseline.DispositionBase, baseline.BleedoutOverride, baseline.Height, skills,
                new NpcFlagPatch(setFlags, clearFlags));
    }

    private ImmutableArray<Diagnostic> Validate(NpcResetRequest request, bool requireExpectedHash,
        bool allowExistingProposal)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(request.CurrentPlugin.Value))
            diagnostics.Add(new Diagnostic("current-plugin-missing", DiagnosticSeverity.Error, "The current plugin does not exist."));
        if (!File.Exists(request.BaselinePlugin.Value))
            diagnostics.Add(new Diagnostic("baseline-plugin-missing", DiagnosticSeverity.Error, "The baseline plugin does not exist."));
        if (!request.CurrentPlugin.IsUnder(labRoot) || !request.BaselinePlugin.IsUnder(labRoot))
            diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error, "Current and baseline plugins must remain under the K-only lab root."));
        var outputParent = Path.GetDirectoryName(request.OutputPlugin.Value);
        if (outputParent is null || !Directory.Exists(outputParent))
            diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error, "The output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(outputParent)));
        if (!IsPluginPath(request.CurrentPlugin.Value) || !IsPluginPath(request.BaselinePlugin.Value) || !IsPluginPath(request.OutputPlugin.Value))
            diagnostics.Add(new Diagnostic("plugin-extension-invalid", DiagnosticSeverity.Error, "Current, baseline, and output paths must use .esp, .esm, or .esl extensions."));
        if (!string.Equals(Path.GetFileName(request.CurrentPlugin.Value), Path.GetFileName(request.BaselinePlugin.Value), StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(Path.GetExtension(request.CurrentPlugin.Value), Path.GetExtension(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("plugin-identity-mismatch", DiagnosticSeverity.Error,
                "Current, baseline, and output must carry the same plugin filename and extension."));
        if (SamePath(request.CurrentPlugin, request.BaselinePlugin) || SamePath(request.CurrentPlugin, request.OutputPlugin) || SamePath(request.BaselinePlugin, request.OutputPlugin))
            diagnostics.Add(new Diagnostic("reset-path-collision", DiagnosticSeverity.Error, "Current, baseline, and output must be distinct files."));
        if (File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "The reset output already exists; reset never overwrites artifacts."));
        if (requireExpectedHash && request.ExpectedCurrentHash is null)
            diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        AddReparseDiagnostic(diagnostics, request.CurrentPlugin.Value, "current-plugin");
        AddReparseDiagnostic(diagnostics, request.BaselinePlugin.Value, "baseline-plugin");
        if (outputParent is not null) AddReparseDiagnostic(diagnostics, outputParent, "output-parent");
        if (request.ProposalPath is { } proposal)
        {
            var parent = Path.GetDirectoryName(proposal.Value);
            if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error, "The proposal directory must already exist."));
            if (!proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "The proposal path must remain under the K-only lab root."));
            if (File.Exists(proposal.Value) && !allowExistingProposal) diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error, "Proposals never overwrite artifacts."));
            if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "proposal-parent");
        }
        return diagnostics.ToImmutable();
    }

    private static string? RequireBaselineValue(string? current, string field, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (current is not null)
            diagnostics.Add(new Diagnostic("reset-null-baseline-unsupported", DiagnosticSeverity.Error,
                $"The typed writer cannot clear the current {field} value to a null baseline."));
        return null;
    }

    private static OptionalFormReference ToOptional(FormReference? value) =>
        value is { } reference ? OptionalFormReference.Set(reference) : OptionalFormReference.Clear();

    private static bool SectionEqual(NpcResetSection section, BethesdaNpcSnapshot left, BethesdaNpcSnapshot right) => section switch
    {
        NpcResetSection.Identity => left.EditorId == right.EditorId && left.Name == right.Name && left.Sex == right.Sex,
        NpcResetSection.Archetype => left.Archetype == right.Archetype,
        NpcResetSection.Weight => left.SkyrimWeight == right.SkyrimWeight && left.Thin == right.Thin && left.Muscular == right.Muscular && left.Fat == right.Fat,
        NpcResetSection.Stats => StatsEqual(left.Stats, right.Stats),
        NpcResetSection.Keywords => KeywordsEqual(left.Keywords, right.Keywords),
        NpcResetSection.Factions => SequenceEqual(left.Factions?.Factions, right.Factions?.Factions),
        NpcResetSection.Inventory => SequenceEqual(left.Inventory?.Items, right.Inventory?.Items),
        NpcResetSection.Outfits => left.Outfits == right.Outfits,
        NpcResetSection.Perks => SequenceEqual(left.Perks?.Perks, right.Perks?.Perks),
        NpcResetSection.ActorEffects => SequenceEqual(left.ActorEffects?.ActorEffects, right.ActorEffects?.ActorEffects),
        NpcResetSection.Properties => SequenceEqual(left.Properties?.Properties, right.Properties?.Properties),
        _ => false
    };

    private static bool KeywordsEqual(NpcKeywordSnapshot? left, NpcKeywordSnapshot? right) =>
        left is null || right is null ? left is null && right is null :
        left.Keywords.SequenceEqual(right.Keywords) && left.AttachParentSlots.SequenceEqual(right.AttachParentSlots);

    private static bool StatsEqual(NpcStatsSnapshot? left, NpcStatsSnapshot? right)
    {
        if (left is null || right is null) return left is null && right is null;
        if (left.Level != right.Level || left.XpValueOffset != right.XpValueOffset || left.MagickaOffset != right.MagickaOffset ||
            left.StaminaOffset != right.StaminaOffset || left.HealthOffset != right.HealthOffset || left.CalcMinLevel != right.CalcMinLevel ||
            left.CalcMaxLevel != right.CalcMaxLevel || left.SpeedMultiplier != right.SpeedMultiplier || left.DispositionBase != right.DispositionBase ||
            left.BleedoutOverride != right.BleedoutOverride || left.Height != right.Height || !left.Flags.SetEquals(right.Flags)) return false;
        if (left.PlayerSkills is null || right.PlayerSkills is null) return left.PlayerSkills is null && right.PlayerSkills is null;
        return left.PlayerSkills.Health == right.PlayerSkills.Health && left.PlayerSkills.Magicka == right.PlayerSkills.Magicka &&
               left.PlayerSkills.Stamina == right.PlayerSkills.Stamina && DictionaryEqual(left.PlayerSkills.Values, right.PlayerSkills.Values) &&
               DictionaryEqual(left.PlayerSkills.Offsets, right.PlayerSkills.Offsets) &&
               left.PlayerSkills.FarAwayModelDistance == right.PlayerSkills.FarAwayModelDistance &&
               left.PlayerSkills.GearedUpWeapons == right.PlayerSkills.GearedUpWeapons;
    }

    private static bool DictionaryEqual(ImmutableDictionary<NpcSkill, byte> left, ImmutableDictionary<NpcSkill, byte> right) =>
        left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static bool SequenceEqual<T>(ImmutableArray<T>? left, ImmutableArray<T>? right) =>
        left is null || right is null ? left is null && right is null : left.Value.SequenceEqual(right.Value);

    private static Sha256Hash HashOrZero(string path, ImmutableArray<Diagnostic>.Builder diagnostics, string role)
    {
        try
        {
            if (!File.Exists(path)) return ZeroHash;
            using var stream = File.OpenRead(path);
            return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("hash-failed", DiagnosticSeverity.Error, $"The {role} hash could not be computed: {exception.Message}"));
            return ZeroHash;
        }
    }

    private static void WriteProposal(WorkspacePath path, NpcResetProposal proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(proposal, ProposalJsonOptions));
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path.Value, overwrite: false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message));
        }
        finally { TryDelete(temporary); }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static bool SamePath(WorkspacePath left, WorkspacePath right) => string.Equals(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);
    private static bool IsPluginPath(string path) => Path.GetExtension(path) is ".esp" or ".esm" or ".esl";

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(new Diagnostic("reparse-point-refused", DiagnosticSeverity.Error, $"The {role} traverses a reparse point."));
                    return;
                }
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message)); return; }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));
}
