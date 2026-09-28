using System.Collections.Immutable;

namespace NpcManager.Application;

public static partial class AgentCommandRegistry
{
    private static readonly ImmutableHashSet<string> CoreBodyRecordNames =
        new[]
        {
            "capabilities", "schema export", "diagnose", "gui", "workspace preflight",
            "workspace scan-generated", "profile scan", "load-order validate",
            "plugins resolve-load-order", "plugins validate", "assets index", "assets search",
            "headpart choices", "paint choices", "forms search", "body sliders resolve",
            "body sliders inspect-preset", "body sidecar inspect", "body sidecar write",
            "bodygen write", "body weight resolve", "body overlay patch", "body overlay bake",
            "body transforms apply", "body reset", "body patch", "body weight normalize",
            "body weight redistribute", "bodygen build", "records list", "records propose",
            "changes list", "changes update"
        }.ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);

    private static bool IsCoreBodyRecordCommand(string name) => CoreBodyRecordNames.Contains(name);

    private static AgentCommandContract CoreBodyRecordContract(CommandDescriptor descriptor)
    {
        AgentCommandContract draft = descriptor.Name switch
        {
            "capabilities" => CapabilitiesDraft(descriptor),
            "schema export" => SchemaExportDraft(descriptor),
            "workspace preflight" => WorkspacePreflightDraft(descriptor),
            "gui" => GuiLegacy(descriptor),
            _ => Legacy(descriptor)
        };
        ImmutableArray<AgentArtifactContract> inputs = InputArtifacts(descriptor.Name);
        ImmutableArray<AgentArtifactContract> outputs = OutputArtifacts(descriptor.Name);
        bool writes = WritesArtifact(descriptor.Name);
        return draft with
        {
            ContractStatus = AgentContractStatus.Complete,
            SupportedGames = SupportedGames(descriptor),
            Limitations = descriptor.Name == "schema export"
                ? draft.Limitations
                : descriptor.Limitations
                    .Where(item => !string.Equals(item,
                        "M1 shell: feature implementation is introduced by later milestones.",
                        StringComparison.Ordinal)).ToImmutableArray(),
            Options = LegacyOptions(descriptor.Name),
            OptionRelationships = Relationships(descriptor.Name),
            InputArtifactKinds = ArtifactKinds(inputs),
            InputArtifacts = inputs,
            OutputArtifacts = outputs,
            ResultShape = "object",
            ResultDescription = ResultDescription(descriptor.Name),
            Effects = descriptor.Name is "workspace preflight" or "schema export"
                ? draft.Effects
                : Effects(descriptor.Name, writes),
            RetryPolicy = descriptor.Name == "workspace preflight" ? draft.RetryPolicy : descriptor.Name == "gui" ? AgentRetryPolicy.NotRetryable : writes ? AgentRetryPolicy.RequiresFreshOutput : AgentRetryPolicy.SafeUnchanged,
            Determinism = descriptor.Name == "workspace preflight" ? draft.Determinism : descriptor.Name is "diagnose" or "gui"
                ? AgentDeterminism.EnvironmentDependent
                : AgentDeterminism.Deterministic,
            SupportsDryRun = descriptor.Name is "body reset" or "body patch",
            Authority = descriptor.Name is "workspace preflight" or "capabilities" or "schema export" ? draft.Authority : FamilyAuthority(descriptor.Name, writes)
        };
    }

    private static ImmutableArray<NpcManager.Domain.GameEdition> SupportedGames(CommandDescriptor descriptor) =>
        descriptor.Name switch
        {
            "headpart choices" or "paint choices" or "body sliders inspect-preset" or
                "body weight resolve" or "body overlay bake" or "body transforms apply" =>
                [NpcManager.Domain.GameEdition.SkyrimSpecialEdition],
            "body weight normalize" or "body weight redistribute" =>
                [NpcManager.Domain.GameEdition.Fallout4],
            _ => descriptor.SupportedGames
        };

    private static ImmutableArray<AgentOptionRelationshipContract> Relationships(string name)
    {
        var rows = ImmutableArray.CreateBuilder<AgentOptionRelationshipContract>();
        if (name != "workspace preflight" && LegacyCommandOptionCatalog.For(name).Any(option => option.Name == "edition"))
            rows.Add(AtLeastOne(name is "body sidecar write" or "body overlay patch" or "body overlay bake" or "body transforms apply" or "body reset" or "body weight normalize" or "body weight redistribute" ? ["game", "edition"] : ["edition", "game"]));
        ImmutableArray<AgentOptionRelationshipContract> commandRows = name switch
        {
        "gui" => [RequiresTogether("workflow-bundle", "workflow-bundle-sha256"), RequiredWhen("executable", "launch", "Desktop launch is selected when --launch is present.")],
        "workspace preflight" =>
        [
            RequiredWhen(["workspace-root", "output-root"], ["edition", "game", "plugin", "asset-index", "load-order", "loadorder"], "Basic mode is selected when no game, archive, or reviewed-intake selector is present.", absentOnly: true),
            RequiredWhen(["data-root", "output-root"], ["edition", "game"], "Game-root mode is selected by --edition or --game."),
            RequiredWhen(["plugin", "asset-index"], ["plugin", "asset-index"], "Archive-consistency mode is selected when either archive option is present.", allowSelf: true),
            RequiredWhen(["data-root", "output-root"], ["load-order", "loadorder"], "Reviewed-intake mode is selected by either load-order spelling.")
        ],
        "load-order validate" or "plugins validate" or "plugins resolve-load-order" => [AtLeastOne(["load-order", "loadorder"])],
        "assets index" or "assets search" or "records list" => [AtLeastOne(["plugin", "data-root"])],
        "forms search" => [AtLeastOne(["plugin", "data-root"]), AtLeastOne(["type", "signature"])],
        "body sidecar write" => [AtLeastOne(["sliders", "morphs"])],
        "body overlay patch" or "body transforms apply" => [AtLeastOne(["npc", "form-id"])],
        "body reset" => [AtLeastOne(["npc", "form-id"]), ForbiddenActive("apply", "dry-run")],
        "body patch" => [AtLeastOne(["form-id", "npc"]), ForbiddenActive("apply", "dry-run"), ForbiddenActiveAndPresent("clear-skin", "skin")],
        "body weight normalize" or "body weight redistribute" => [AtLeastOne(["triangle", "current"])],
        _ => []
        };
        rows.AddRange(commandRows);
        return rows.ToImmutable();
    }

    private static AgentOptionRelationshipContract AtLeastOne(ImmutableArray<string> options) =>
        new(AgentOptionRelationshipKind.AtLeastOne, options, [],
            $"At least one of {string.Join(", ", options.Select(option => $"--{option}"))} is required.");

    private static AgentOptionRelationshipContract ForbiddenActive(string first, string second) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, [first, second], [first, second],
            $"--{first} and --{second} cannot both be active.")
        {
            Trigger = new AgentOptionRelationshipTrigger(AgentPredicateCombination.All,
            [
                new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, first, AgentPredicateMatch.AnyOf, ["true", "1"], false),
                new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, second, AgentPredicateMatch.AnyOf, ["true", "1"], false)
            ])
        };

    private static AgentOptionRelationshipContract ForbiddenActiveAndPresent(string activeFlag, string presentOption) =>
        new(AgentOptionRelationshipKind.ForbiddenWhen, [activeFlag, presentOption], [activeFlag, presentOption],
            $"Active --{activeFlag} cannot be combined with --{presentOption}.")
        {
            Trigger = new AgentOptionRelationshipTrigger(AgentPredicateCombination.All,
            [
                new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, activeFlag, AgentPredicateMatch.AnyOf, ["true", "1"], false),
                new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, presentOption, AgentPredicateMatch.NoneOf, ["__absent__"], false)
            ])
        };

    private static AgentOptionRelationshipContract RequiredWhen(string option, string selector, string condition) =>
        RequiredWhen([option], [selector], condition);

    private static AgentOptionRelationshipContract RequiredWhen(
        ImmutableArray<string> options, ImmutableArray<string> selectors, string condition,
        bool absentOnly = false, bool allowSelf = false)
    {
        ImmutableArray<string> references = allowSelf
            ? selectors.Where(selector => !options.Contains(selector, StringComparer.OrdinalIgnoreCase)).ToImmutableArray()
            : selectors;
        if (allowSelf && references.IsEmpty)
            return RequiresTogether(options[0], options[1]);
        return new AgentOptionRelationshipContract(
            AgentOptionRelationshipKind.RequiredWhen, options, references, condition)
        {
            Trigger = new AgentOptionRelationshipTrigger(
                absentOnly ? AgentPredicateCombination.All : AgentPredicateCombination.Any,
                references.Select(selector => new AgentRelationshipPredicate(
                    AgentPredicateSource.OptionValue, selector,
                    absentOnly ? AgentPredicateMatch.AnyOf : AgentPredicateMatch.NoneOf,
                    ["__absent__"], absentOnly)).ToImmutableArray())
        };
    }

    private static ImmutableArray<AgentArtifactContract> InputArtifacts(string name)
    {
        ImmutableArray<LegacyCommandOption> rows = LegacyCommandOptionCatalog.For(name);
        string[] inputNames = ["file", "tri", "preset-xml", "input", "layers", "preset", "current", "baseline", "input-plugin", "assignments", "morphs", "data-root", "plugin", "load-order", "loadorder", "session", "masters", "executable", "workflow-bundle"];
        return rows.Where(row => inputNames.Contains(row.Name, StringComparer.Ordinal))
            .Select(row => new AgentArtifactContract($"{name.Replace(' ', '-')}-{row.Name}", [],
                $"Existing input selected by --{row.Name}; no grounded schema identifier is published.")
            {
                Trigger = name == "gui"
                    ? row.Name == "workflow-bundle"
                        ? AllPresent("launch", "workflow-bundle")
                        : Present("launch")
                    : null
            })
            .ToImmutableArray();
    }

    private static ImmutableArray<AgentArtifactContract> OutputArtifacts(string name) => name switch
    {
        "schema export" or "assets index" => [new AgentArtifactContract($"{name.Replace(' ', '-')}-output", [], "Optional fresh K-local output; no grounded schema identifier is published.") { Trigger = Present("output") }],
        "body reset" or "body patch" =>
        [
            new AgentArtifactContract($"{name.Replace(' ', '-')}-proposal", [], "Fresh proposal artifact when --proposal is supplied.") { Trigger = Present("proposal") },
            new AgentArtifactContract($"{name.Replace(' ', '-')}-applied-output", [], "Fresh applied output when --apply is active.") { Trigger = Truthy("apply") }
        ],
        _ when WritesArtifact(name) => [new AgentArtifactContract($"{name.Replace(' ', '-')}-output", [], "Fresh K-local output selected by the command; no grounded schema identifier is published.")],
        _ => []
    };

    private static bool WritesArtifact(string name) => name is
        "schema export" or "assets index" or "body sidecar write" or "bodygen write" or "body overlay bake" or
        "body transforms apply" or "body reset" or "body patch" or "bodygen build" or
        "records propose" or "changes update";

    private static ImmutableArray<AgentEffectContract> Effects(string name, bool writes)
    {
        var effects = ImmutableArray.CreateBuilder<AgentEffectContract>();
        if (name == "gui" || LegacyCommandOptionCatalog.For(name).Any(option => option.ValueSyntax.Contains("path", StringComparison.OrdinalIgnoreCase) || option.ValueSyntax.Contains("root", StringComparison.OrdinalIgnoreCase) || option.ValueSyntax.Contains("file", StringComparison.OrdinalIgnoreCase) || option.ValueSyntax.Contains("plugin", StringComparison.OrdinalIgnoreCase)))
            effects.Add(new AgentEffectContract(AgentEffectKind.ReadWorkspace, "When an admitted existing path is supplied.", "workspace")
            {
                Trigger = name == "gui" ? Present("launch") : null
            });
        if (name == "gui")
        {
            effects.Add(new AgentEffectContract(AgentEffectKind.InvokeAdmittedProcess, "Only when --launch is present and desktop admission succeeds.", "K-local admitted desktop executable") { Trigger = Present("launch") });
            effects.Add(new AgentEffectContract(AgentEffectKind.LaunchDesktop, "Only when --launch is present and the operating system creates the desktop process.", "Actorwright desktop process") { Trigger = Present("launch") });
        }
        if (name is "schema export" or "assets index")
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only when --output is supplied and validation succeeds.", "k-local-output") { Trigger = Present("output") });
        else if (name is "body reset" or "body patch")
        {
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only when --proposal is supplied and validation succeeds.", "proposal-output") { Trigger = Present("proposal") });
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only when --apply is active and validation succeeds.", "applied-output") { Trigger = Truthy("apply") });
        }
        else if (writes)
            effects.Add(new AgentEffectContract(AgentEffectKind.WriteNewArtifact, "Only after command validation succeeds.", "k-local-output"));
        effects.Add(JournalEffect());
        return effects.ToImmutable();
    }

    private static AgentOptionRelationshipTrigger Present(string option) => new(AgentPredicateCombination.All,
        [new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.NoneOf, ["__absent__"], false)]);

    private static AgentOptionRelationshipTrigger AllPresent(params string[] options) => new(AgentPredicateCombination.All,
        options.Select(option => new AgentRelationshipPredicate(
            AgentPredicateSource.OptionValue, option, AgentPredicateMatch.NoneOf, ["__absent__"], false)).ToImmutableArray());

    private static AgentOptionRelationshipTrigger Truthy(string option) => new(AgentPredicateCombination.All,
        [new AgentRelationshipPredicate(AgentPredicateSource.OptionValue, option, AgentPredicateMatch.AnyOf, ["true", "1"], false)]);

    private static string ResultDescription(string name) => name switch
    {
        "capabilities" => "Versioned command catalogue and ledger mappings.",
        "diagnose" => "Read-only local operating-system and process diagnosis.",
        "gui" => "Desktop availability or launch outcome and diagnostics.",
        _ => $"Typed {name} outcome and diagnostics; no grounded result schema identifier is published."
    };

    private static ImmutableArray<AgentAuthorityContract> FamilyAuthority(string name, bool writes) =>
    [
        AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "Command options and K-local paths are validated before execution."),
        AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, name is "headpart choices" or "paint choices" or "assets index" or "assets search" or "forms search" ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable, "Provider identity is established only by provider-aware discovery results."),
        AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, writes ? AgentAuthorityState.Established : AgentAuthorityState.NotApplicable, writes ? "Fresh output is deterministically materialized for admitted inputs." : "This command does not materialize a persistent artifact."),
        AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, writes ? AgentAuthorityState.Required : AgentAuthorityState.NotApplicable, writes ? "A write result does not itself establish independent verification." : "No persistent artifact is produced for independent verification."),
        AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "This family does not render an off-engine preview."),
        AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.NotApplicable, "This family does not establish human visual acceptance."),
        AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, writes ? AgentAuthorityState.Required : AgentAuthorityState.NotApplicable, writes ? "Static output does not establish game-runtime behavior." : "This command does not materialize a game artifact."),
        AuthorityContract(AgentAuthorityKind.PromotionApproval, writes ? AgentAuthorityState.Required : AgentAuthorityState.NotApplicable, writes ? "Command success never grants promotion approval." : "This command produces no promotable artifact.")
    ];
}
