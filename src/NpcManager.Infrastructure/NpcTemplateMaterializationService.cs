using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed class NpcTemplateMaterializationService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : INpcTemplateMaterializationService
{
    private static readonly ImmutableArray<string> PreservedFields = ["ACBS", "SNAM", "SPLO", "KWDA"];
    private static readonly ImmutableHashSet<NpcTemplateCategory> SupportedCategories =
        [NpcTemplateCategory.Stats, NpcTemplateCategory.Factions, NpcTemplateCategory.SpellList, NpcTemplateCategory.Keywords];
    private static readonly JsonSerializerOptions ProposalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public ValueTask<NpcTemplateMaterializationProposal> AnalyzeAsync(
        NpcTemplateMaterializationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateRequest(request, requireExpectedHash: false, allowExistingProposal: false).ToBuilder();
        var inputHash = File.Exists(request.InputPlugin.Value) ? ComputeHash(request.InputPlugin.Value) : ZeroHash;
        if (request.ExpectedInputHash is { } expected && expected != inputHash)
            diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error,
                $"Input hash {inputHash} does not match the expected hash {expected}."));

        var evidence = ImmutableArray.CreateBuilder<NpcTemplateCategoryEvidence>();
        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        var sourceSnapshots = new Dictionary<NpcTemplateCategory, BethesdaNpcSnapshot>();
        if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
        {
            try
            {
                var targetTemplate = BethesdaNpcTemplateAdapter.Read(request.Edition, request.InputPlugin, request.TargetFormId);
                var categories = request.Categories ?? targetTemplate.InheritedCategories;
                foreach (var category in categories.OrderBy(item => (int)item))
                {
                    var inherited = targetTemplate.InheritedCategories.Contains(category);
                    targetTemplate.DirectSources.TryGetValue(category, out var directSource);
                    if (!inherited)
                    {
                        evidence.Add(new NpcTemplateCategoryEvidence(category, false, directSource, null, [], SupportedCategories.Contains(category)));
                        continue;
                    }

                    var resolved = ResolveSource(request, category, targetTemplate, diagnostics);
                    var supported = SupportedCategories.Contains(category);
                    var fields = InheritedFields(category);
                    evidence.Add(new NpcTemplateCategoryEvidence(category, true, directSource,
                        resolved.Source, fields, supported));
                    if (!supported)
                    {
                        diagnostics.Add(new Diagnostic("template-category-unsupported", DiagnosticSeverity.Error,
                            $"Template category '{category.ToWireName()}' is inherited but its complete typed field surface is not implemented; no output will be written."));
                        continue;
                    }

                    if (resolved.Source is null || resolved.Snapshot is null) continue;
                    sourceSnapshots[category] = resolved.Snapshot;
                    changes.Add(new MutationChange($"Template:{category.ToWireName()}",
                        directSource?.ToString() ?? "none", resolved.Source.ToString()));
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("template-read-failed", DiagnosticSeverity.Error,
                    $"NPC template data could not be read: {exception.Message}"));
            }
        }

        if (evidence.Count == 0 && !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            diagnostics.Add(new Diagnostic("template-no-categories", DiagnosticSeverity.Error,
                "The NPC has no inherited categories to materialize."));
        if (changes.Count == 0 && !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            diagnostics.Add(new Diagnostic("template-no-supported-categories", DiagnosticSeverity.Error,
                "No inherited, fully typed template category was selected."));

        var proposal = new NpcTemplateMaterializationProposal(request.Edition, request.InputPlugin, request.OutputPlugin,
            request.TargetFormId, inputHash, evidence.ToImmutable(), changes.ToImmutable(), PreservedFields,
            diagnostics.ToImmutable());
        if (request.ProposalPath is { } proposalPath && !diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            WriteProposal(proposalPath, proposal, diagnostics);
        return ValueTask.FromResult(proposal with { Diagnostics = diagnostics.ToImmutable() });
    }

    public async ValueTask<NpcTemplateMaterializationResult> ApplyAsync(
        NpcTemplateMaterializationRequest request,
        NpcTemplateMaterializationProposal proposal,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = proposal.Diagnostics.ToBuilder();
        diagnostics.AddRange(ValidateRequest(request, requireExpectedHash: true, allowExistingProposal: true));
        if (request.DryRun) diagnostics.Add(new Diagnostic("dry-run", DiagnosticSeverity.Info,
            "Dry-run requested; no plugin was written."));
        if (File.Exists(request.InputPlugin.Value) && proposal.InputHash != ComputeHash(request.InputPlugin.Value))
            diagnostics.Add(new Diagnostic("input-hash-mismatch", DiagnosticSeverity.Error,
                "The input changed after template analysis."));
        if (request.DryRun || diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return new NpcTemplateMaterializationResult(false, proposal, null, diagnostics.ToImmutable());

        var sourceSnapshots = new Dictionary<NpcTemplateCategory, BethesdaNpcSnapshot>();
        try
        {
            foreach (var category in proposal.Categories.Where(item => item.Inherited && item.Supported))
            {
                if (category.ResolvedSource is null) throw new InvalidDataException($"Category {category.Category} has no resolved source.");
                sourceSnapshots[category.Category] = BethesdaNpcMutationAdapter.Read(request.Edition, request.InputPlugin,
                    category.ResolvedSource.Value.FormId);
            }
            var mutation = BuildMutationRequest(request, sourceSnapshots);
            var temporary = request.OutputPlugin.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            var promoted = false;
            try
            {
                BethesdaNpcTemplateAdapter.Write(request, sourceSnapshots.Keys.ToImmutableHashSet(), mutation, new WorkspacePath(temporary));
                if (!File.Exists(temporary)) throw new IOException("The template adapter did not produce an output file.");
                File.Move(temporary, request.OutputPlugin.Value, overwrite: false);
                promoted = true;
                VerifyWritten(request, proposal, sourceSnapshots, diagnostics);
                if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
                {
                    TryDelete(request.OutputPlugin.Value);
                    return new NpcTemplateMaterializationResult(false, proposal, null, diagnostics.ToImmutable());
                }
                return new NpcTemplateMaterializationResult(true, proposal, ComputeHash(request.OutputPlugin.Value), diagnostics.ToImmutable());
            }
            finally
            {
                if (!promoted) TryDelete(temporary);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("template-write-failed", DiagnosticSeverity.Error, exception.Message));
            return new NpcTemplateMaterializationResult(false, proposal, null, diagnostics.ToImmutable());
        }
    }

    private static NpcMutationRequest BuildMutationRequest(NpcTemplateMaterializationRequest request,
        Dictionary<NpcTemplateCategory, BethesdaNpcSnapshot> sources)
    {
        sources.TryGetValue(NpcTemplateCategory.Stats, out var statsSource);
        sources.TryGetValue(NpcTemplateCategory.Factions, out var factionsSource);
        sources.TryGetValue(NpcTemplateCategory.SpellList, out var spellSource);
        sources.TryGetValue(NpcTemplateCategory.Keywords, out var keywordSource);
        return new NpcMutationRequest(request.Edition, request.InputPlugin, request.OutputPlugin, request.TargetFormId,
            null, null, null, null, true, null,
            Stats: statsSource?.Stats is { } stats ? ToStatsPatch(stats) : null,
            KeywordPatch: keywordSource?.Keywords is { } keywords ? new NpcKeywordPatch(
                new NpcKeywordListPatch(keywords.Keywords, [], []), null) : null,
            FactionPatch: factionsSource?.Factions is { } factions ? new NpcFactionPatch(factions.Factions, [], [], []) : null,
            ActorEffectPatch: spellSource?.ActorEffects is { } spells ? new NpcActorEffectPatch(spells.ActorEffects, [], []) : null);
    }

    private static NpcStatsPatch ToStatsPatch(NpcStatsSnapshot stats) => new(stats.Level, stats.XpValueOffset,
        stats.MagickaOffset, stats.StaminaOffset, stats.HealthOffset, stats.CalcMinLevel, stats.CalcMaxLevel,
        stats.SpeedMultiplier, stats.DispositionBase, stats.BleedoutOverride, stats.Height,
        stats.PlayerSkills is { } skills
            ? new NpcPlayerSkillsPatch(skills.Health, skills.Magicka, skills.Stamina,
                skills.Values, skills.Offsets, skills.FarAwayModelDistance,
                skills.GearedUpWeapons)
            : null,
        null);

    private static (FormReference? Source, BethesdaNpcSnapshot? Snapshot) ResolveSource(
        NpcTemplateMaterializationRequest request,
        NpcTemplateCategory category,
        BethesdaNpcTemplateSnapshot target,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var pluginName = Path.GetFileName(request.InputPlugin.Value);
        var seen = new HashSet<FormId> { request.TargetFormId };
        var current = target;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!current.InheritedCategories.Contains(category))
            {
                var sourceId = seen.Last();
                try { return (new FormReference(new PluginName(pluginName), sourceId), BethesdaNpcMutationAdapter.Read(request.Edition, request.InputPlugin, sourceId)); }
                catch (Exception exception) { diagnostics.Add(new Diagnostic("template-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return (null, null); }
            }
            if (!current.DirectSources.TryGetValue(category, out var next) || next is null)
            {
                diagnostics.Add(new Diagnostic("template-source-missing", DiagnosticSeverity.Error,
                    $"Category '{category.ToWireName()}' has a template flag but no source link."));
                return (null, null);
            }
            if (!string.Equals(next.Value.Plugin.Value, pluginName, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(new Diagnostic("template-external-source", DiagnosticSeverity.Error,
                    $"Category '{category.ToWireName()}' resolves to external plugin {next.Value.Plugin}; explicit master resolution is required."));
                return (null, null);
            }
            if (!seen.Add(next.Value.FormId))
            {
                diagnostics.Add(new Diagnostic("template-cycle", DiagnosticSeverity.Error,
                    $"Category '{category.ToWireName()}' contains a template cycle."));
                return (null, null);
            }
            try
            {
                current = BethesdaNpcTemplateAdapter.Read(request.Edition, request.InputPlugin, next.Value.FormId);
            }
            catch (Exception exception)
            {
                diagnostics.Add(new Diagnostic("template-source-read-failed", DiagnosticSeverity.Error, exception.Message));
                return (null, null);
            }
        }
        diagnostics.Add(new Diagnostic("template-depth-exceeded", DiagnosticSeverity.Error,
            "Template resolution exceeded the 32-record safety bound."));
        return (null, null);
    }

    private static ImmutableArray<string> InheritedFields(NpcTemplateCategory category) => category switch
    {
        NpcTemplateCategory.Stats => ["ACBS", "DNAM"],
        NpcTemplateCategory.Factions => ["SNAM"],
        NpcTemplateCategory.SpellList => ["SPLO"],
        NpcTemplateCategory.Keywords => ["KWDA"],
        NpcTemplateCategory.Traits => ["RNAM", "WNAM", "NAM7", "MWGT", "HEAD", "FaceGen", "OBTS"],
        NpcTemplateCategory.AiData => ["AIDT"],
        NpcTemplateCategory.AiPackages => ["PKID"],
        NpcTemplateCategory.ModelAnimation => ["MODL", "NIF"],
        NpcTemplateCategory.BaseData => ["FULL", "CNAM", "VTCK", "DNAM"],
        NpcTemplateCategory.Inventory => ["CNTO", "COED"],
        NpcTemplateCategory.Script => ["VMAD"],
        NpcTemplateCategory.DefaultPackageList => ["DPLT"],
        NpcTemplateCategory.AttackData => ["ATKD"],
        _ => []
    };

    private ImmutableArray<Diagnostic> ValidateRequest(NpcTemplateMaterializationRequest request, bool requireExpectedHash,
        bool allowExistingProposal)
    {
        var outputParent = Path.GetDirectoryName(request.OutputPlugin.Value);
        var diagnostics = (outputParent is null
            ? ImmutableArray.Create(new Diagnostic("output-parent-invalid", DiagnosticSeverity.Error, "Output path has no parent directory."))
            : policy.Evaluate(labRoot, new WorkspacePath(outputParent))).ToBuilder();
        if (!request.InputPlugin.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("input-outside-lab", DiagnosticSeverity.Error, "Input plugins must remain under the K-only lab root."));
        if (!File.Exists(request.InputPlugin.Value)) diagnostics.Add(new Diagnostic("input-plugin-missing", DiagnosticSeverity.Error, "The input plugin does not exist."));
        if (outputParent is not null && !Directory.Exists(outputParent)) diagnostics.Add(new Diagnostic("output-parent-missing", DiagnosticSeverity.Error, "The output directory must already exist."));
        if (File.Exists(request.OutputPlugin.Value)) diagnostics.Add(new Diagnostic("output-exists", DiagnosticSeverity.Error, "The output path already exists; materialization never overwrites."));
        if (string.Equals(request.InputPlugin.Value, request.OutputPlugin.Value, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("input-output-same", DiagnosticSeverity.Error, "Input and output paths must differ."));
        if (requireExpectedHash && request.ExpectedInputHash is null) diagnostics.Add(new Diagnostic("expected-hash-required", DiagnosticSeverity.Error, "Apply requires --expected-sha256."));
        if (!IsPluginPath(request.InputPlugin.Value) || !IsPluginPath(request.OutputPlugin.Value)) diagnostics.Add(new Diagnostic("plugin-extension-invalid", DiagnosticSeverity.Error, "Input and output must use .esp, .esm, or .esl."));
        if (request.Categories is { Count: 0 }) diagnostics.Add(new Diagnostic("template-category-empty", DiagnosticSeverity.Error, "At least one template category must be selected."));
        if (request.ProposalPath is { } proposal)
        {
            if (!proposal.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("proposal-outside-lab", DiagnosticSeverity.Error, "Proposal paths must remain under the K-only lab root."));
            if (File.Exists(proposal.Value) && !allowExistingProposal) diagnostics.Add(new Diagnostic("proposal-exists", DiagnosticSeverity.Error, "Proposal paths are non-overwriting."));
            var proposalParent = Path.GetDirectoryName(proposal.Value);
            if (proposalParent is null || !Directory.Exists(proposalParent)) diagnostics.Add(new Diagnostic("proposal-parent-missing", DiagnosticSeverity.Error, "The proposal directory must already exist."));
            if (proposalParent is not null) AddReparseDiagnostic(diagnostics, proposalParent, "proposal-parent");
        }
        AddReparseDiagnostic(diagnostics, request.InputPlugin.Value, "input-plugin");
        if (outputParent is not null) AddReparseDiagnostic(diagnostics, outputParent, "output-parent");
        return diagnostics.ToImmutable();
    }

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
            catch (IOException exception) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, $"The {role} could not be inspected: {exception.Message}")); return; }
            catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("path-inspection-failed", DiagnosticSeverity.Error, $"The {role} could not be inspected: {exception.Message}")); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static void VerifyWritten(NpcTemplateMaterializationRequest request,
        NpcTemplateMaterializationProposal proposal,
        IReadOnlyDictionary<NpcTemplateCategory, BethesdaNpcSnapshot> sources,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var after = BethesdaNpcTemplateAdapter.Read(request.Edition, request.OutputPlugin, request.TargetFormId);
        foreach (var category in sources.Keys)
            if (after.InheritedCategories.Contains(category)) diagnostics.Add(new Diagnostic("template-flag-not-cleared", DiagnosticSeverity.Error, $"Template flag for {category.ToWireName()} remained set after writing."));
        var snapshot = BethesdaNpcMutationAdapter.Read(request.Edition, request.OutputPlugin, request.TargetFormId);
        foreach (var (category, source) in sources)
        {
            if (category == NpcTemplateCategory.Factions && !EquivalentFactions(snapshot.Factions?.Factions ?? [], source.Factions?.Factions ?? [], request)) diagnostics.Add(new Diagnostic("template-factions-mismatch", DiagnosticSeverity.Error, $"Materialized factions differ from the resolved source. actual={string.Join(',', snapshot.Factions?.Factions ?? [])}; expected={string.Join(',', source.Factions?.Factions ?? [])}"));
            if (category == NpcTemplateCategory.SpellList && !EquivalentReferences(snapshot.ActorEffects?.ActorEffects ?? [], source.ActorEffects?.ActorEffects ?? [], request)) diagnostics.Add(new Diagnostic("template-spell-list-mismatch", DiagnosticSeverity.Error, $"Materialized spell list differs from the resolved source. actual={string.Join(',', snapshot.ActorEffects?.ActorEffects ?? [])}; expected={string.Join(',', source.ActorEffects?.ActorEffects ?? [])}"));
            if (category == NpcTemplateCategory.Keywords && !EquivalentReferences(snapshot.Keywords?.Keywords ?? ImmutableArray<FormReference>.Empty, source.Keywords?.Keywords ?? ImmutableArray<FormReference>.Empty, request)) diagnostics.Add(new Diagnostic("template-keywords-mismatch", DiagnosticSeverity.Error, $"Materialized keywords differ from the resolved source. actual={string.Join(',', snapshot.Keywords?.Keywords ?? [])}; expected={string.Join(',', source.Keywords?.Keywords ?? [])}"));
            if (category == NpcTemplateCategory.Stats && (snapshot.Stats?.Level != source.Stats?.Level || snapshot.Stats?.CalcMinLevel != source.Stats?.CalcMinLevel || snapshot.Stats?.CalcMaxLevel != source.Stats?.CalcMaxLevel)) diagnostics.Add(new Diagnostic("template-stats-mismatch", DiagnosticSeverity.Error, "Materialized stats differ from the resolved source."));
        }
    }

    private static void WriteProposal(WorkspacePath path, NpcTemplateMaterializationProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var json = JsonSerializer.Serialize(proposal, ProposalJsonOptions);
            var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path.Value, overwrite: false);
            }
            finally { TryDelete(temporary); }
        }
        catch (Exception exception) { diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); }
    }

    private static bool EquivalentReferences(ImmutableArray<FormReference> actual, ImmutableArray<FormReference> expected,
        NpcTemplateMaterializationRequest request)
    {
        if (actual.Length != expected.Length) return false;
        for (var index = 0; index < actual.Length; index++)
            if (!EquivalentReference(actual[index], expected[index], request)) return false;
        return true;
    }

    private static bool EquivalentFactions(ImmutableArray<NpcFactionEntry> actual, ImmutableArray<NpcFactionEntry> expected,
        NpcTemplateMaterializationRequest request)
    {
        if (actual.Length != expected.Length) return false;
        for (var index = 0; index < actual.Length; index++)
            if (actual[index].Rank != expected[index].Rank || !EquivalentReference(actual[index].Faction, expected[index].Faction, request)) return false;
        return true;
    }

    private static bool EquivalentReference(FormReference actual, FormReference expected, NpcTemplateMaterializationRequest request) =>
        actual.FormId == expected.FormId &&
        (string.Equals(actual.Plugin.Value, expected.Plugin.Value, StringComparison.OrdinalIgnoreCase) ||
         (string.Equals(expected.Plugin.Value, Path.GetFileName(request.InputPlugin.Value), StringComparison.OrdinalIgnoreCase) &&
          string.Equals(actual.Plugin.Value, Path.GetFileName(request.OutputPlugin.Value), StringComparison.OrdinalIgnoreCase)));

    private static Sha256Hash ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static readonly Sha256Hash ZeroHash = new(new string('0', 64));
    private static bool IsPluginPath(string path) => Path.GetExtension(path) is ".esp" or ".esm" or ".esl";
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}
