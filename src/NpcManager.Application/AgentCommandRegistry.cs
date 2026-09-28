using System.Collections.Immutable;
using NpcManager.Domain;

namespace NpcManager.Application;

public static class AgentProtocolSchemaIds
{
    public const string CapabilitiesResult =
        "urn:actorwright:protocol-v2:capabilities-result:v1";
    public const string VersionResult =
        "urn:actorwright:protocol-v2:version-result:v1";
    public const string SchemaExportResult =
        "urn:actorwright:protocol-v2:schema-export-result:v1";
    public const string ScopedHelpResult =
        "urn:actorwright:protocol-v2:scoped-help-result:v1";
    public const string WorkspacePreflightResult =
        "urn:actorwright:protocol-v2:workspace-preflight-result:v1";
    public const string PresetInspectResult =
        "urn:actorwright:protocol-v2:preset-inspect-result:v1";
    public const string NpcCreatePreflightResult =
        "urn:actorwright:protocol-v2:npc-create-preflight-result:v1";
    public const string NpcCreateFromJslotBuildResult =
        "urn:actorwright:protocol-v2:npc-create-from-jslot-build-result:v1";
    public const string NpcVisualPreviewResult =
        "urn:actorwright:protocol-v2:npc-visual-preview-result:v1";
    public const string ReviewReceiptResult =
        "urn:actorwright:protocol-v2:review-receipt-result:v1";
    public const string FinishVerifyResult =
        "urn:actorwright:protocol-v2:finish-verify-result:v1";
    public const string FinishAnalyzeResult = "urn:actorwright:protocol-v2:finish-analyze-result:v1";
    public const string FinishApplyResult = "urn:actorwright:protocol-v2:finish-apply-result:v1";
}

public static partial class AgentCommandRegistry
{
    private const string JournalCondition =
        "When the operation journal is available.";
    private const string JournalScope =
        "<labRoot>/.actorwright/operations";

    private static readonly ImmutableArray<AgentArtifactContract>
        FinishApplyInputArtifacts =
        [
            new(
                WorkflowArtifactKinds.NpcFinishCoreRequest,
                [
                    SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier,
                    SkyrimNpcFinishCoreRequest.SchemaIdentifier,
                    SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier,
                    SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier
                ],
                "Exact hash-bound Finish Core request."),
            new(
                WorkflowArtifactKinds.NpcFinishCoreProposal,
                [
                    SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier,
                    SkyrimNpcFinishCoreProposal.SchemaIdentifier,
                    SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier,
                    SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier
                ],
                "Exact hash-bound Finish Core proposal.")
        ];

    public static ImmutableArray<AgentCommandContract> All { get; } =
        CommandCatalog.All.Select(Create).ToImmutableArray();

    public static AgentCommandContract GetRequired(string name) =>
        All.Single(item => string.Equals(
            item.Name,
            name,
            StringComparison.OrdinalIgnoreCase));

    public static AgentCommandContract GetLegacyDiscoveryRequired(string name)
    {
        CommandDescriptor descriptor = CommandCatalog.All.Single(item =>
            string.Equals(item.Name, name, StringComparison.OrdinalIgnoreCase));
        AgentCommandContract contract = name == "preset inspect"
            ? ObjectPresetContract(descriptor)
            : name == "npc finish verify"
            ? FinishVerifyV1(descriptor)
            : name == "npc finish analyze"
            ? FinishAnalyze(descriptor)
            : name == "npc finish apply"
            ? FinishApplyV1(descriptor)
            : name == "npc assembly preflight"
            ? ActorAssemblyPreflightLegacy(descriptor)
            : PreviewPluginOptionCatalog.Contains(name)
            ? PreviewPluginContract(descriptor)
            : NpcCreationOptionCatalog.Contains(name)
            ? NpcCreationContract(descriptor)
            : GetRequired(name);
        if (contract.Effects.IsEmpty)
            return contract;
        return contract with
        {
            Effects = contract.Effects.Select(effect => effect with
            {
                AllowedResultScopes = AllowedResultScopes(
                    contract.Name,
                    effect.Kind)
            }).ToImmutableArray()
        };
    }

    public static ImmutableArray<string> Validate() => Validate(All);

    public static ImmutableArray<string> Validate(
        ImmutableArray<AgentCommandContract> contracts)
    {
        var errors = ImmutableArray.CreateBuilder<string>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var contract in contracts)
        {
            if (!names.Add(contract.Name))
                errors.Add($"duplicate command name: {contract.Name}");
        }

        var namesAndAliases = new HashSet<string>(
            names,
            StringComparer.OrdinalIgnoreCase);
        foreach (var contract in contracts)
        {
            foreach (var alias in contract.Aliases)
            {
                if (string.IsNullOrWhiteSpace(alias) ||
                    !namesAndAliases.Add(alias))
                    errors.Add($"duplicate command alias: {alias}");
            }

            if (!contract.Effects.IsDefaultOrEmpty)
                ValidateEffectScopes(contract, errors);
            if (contract.Readiness == ProtocolReadiness.V2)
            {
                ValidateV2(contract, errors);
            }
            if (RequiresCompleteAuthority(contract))
                ValidateAuthority(contract, errors);
            ValidateContractMetadata(contract, names, errors);
        }

        ValidateCanonicalCommandGraphs(contracts, names, errors);

        return errors.ToImmutable();
    }

    private static bool RequiresCompleteAuthority(
        AgentCommandContract contract) =>
        contract.Readiness == ProtocolReadiness.V2 ||
        contract.ContractStatus == AgentContractStatus.Complete ||
        (contract.Name is "capabilities" or "version" or "schema export" &&
         HasRichMetadata(contract));

    private static bool HasRichMetadata(AgentCommandContract contract) =>
        contract.Options.Length > 0 ||
        contract.InputArtifactKinds.Length > 0 ||
        contract.ResultSchemaIds.Length > 0 ||
        contract.Effects.Length > 0 ||
        contract.Authority.Length > 0 ||
        contract.Transitions.Length > 0;

    private static void ValidateContractMetadata(
        AgentCommandContract contract,
        HashSet<string> commandNames,
        ImmutableArray<string>.Builder errors)
    {
        if (contract.ContractStatus == AgentContractStatus.Complete &&
            (string.IsNullOrWhiteSpace(contract.ResultShape) ||
             string.IsNullOrWhiteSpace(contract.ResultDescription) ||
             contract.ResultShape == "unspecified"))
            errors.Add($"complete contract missing result description: {contract.Name}");

        var optionNames = contract.Options.Select(item => item.CliName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dependencyGraph = optionNames.ToDictionary(
            item => item,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);

        foreach (AgentOptionContract option in contract.Options)
        {
            if (contract.ContractStatus == AgentContractStatus.Complete &&
                (string.IsNullOrWhiteSpace(option.ValueSyntax) ||
                 string.IsNullOrWhiteSpace(option.Description)))
                errors.Add(
                    $"complete option missing discovery metadata: {contract.Name} --{option.CliName}");
            if (option.AliasFor is not null)
            {
                if (!optionNames.Contains(option.AliasFor) ||
                    string.Equals(option.CliName, option.AliasFor,
                        StringComparison.OrdinalIgnoreCase))
                    errors.Add(
                        $"unknown option alias target: {contract.Name} --{option.CliName} -> --{option.AliasFor}");
                else
                    dependencyGraph[option.CliName].Add(option.AliasFor);
            }
            foreach (string conflict in option.ConflictsWith)
            {
                if (!optionNames.Contains(conflict) ||
                    string.Equals(option.CliName, conflict,
                        StringComparison.OrdinalIgnoreCase))
                    errors.Add(
                        $"unknown option conflict: {contract.Name} --{option.CliName} conflicts with --{conflict}");
            }
        }

        foreach (AgentOptionRelationshipContract relationship in
                 contract.OptionRelationships)
        {
            if (relationship.Options.IsDefaultOrEmpty ||
                relationship.Options.Any(item => !optionNames.Contains(item)) ||
                relationship.ReferencedOptions.Any(item =>
                    !optionNames.Contains(item)) ||
                string.IsNullOrWhiteSpace(relationship.Condition))
            {
                errors.Add(
                    $"unknown option relationship reference: {contract.Name} {relationship.Kind}");
                continue;
            }

            if (relationship.Kind is AgentOptionRelationshipKind.RequiresTogether or AgentOptionRelationshipKind.AtLeastOne)
            {
                if (relationship.Options.Length < 2 ||
                    relationship.Options.Distinct(StringComparer.OrdinalIgnoreCase)
                        .Count() != relationship.Options.Length ||
                    !relationship.ReferencedOptions.IsDefaultOrEmpty ||
                    relationship.Trigger is not null)
                    errors.Add(
                        $"invalid unconditional relationship: {contract.Name} {relationship.Kind}");
                continue;
            }

            if (relationship.Trigger is not { } trigger ||
                trigger.Predicates.IsDefaultOrEmpty ||
                !Enum.IsDefined(trigger.Operator))
            {
                errors.Add(
                    $"missing conditional trigger: {contract.Name} {relationship.Kind}");
                continue;
            }

            ImmutableArray<string> predicateOptionReferences =
                trigger.Predicates
                    .Where(item => item.Source == AgentPredicateSource.OptionValue)
                    .Select(item => item.Subject)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToImmutableArray();
            if (!relationship.ReferencedOptions.SequenceEqual(
                    predicateOptionReferences,
                    StringComparer.OrdinalIgnoreCase))
                errors.Add(
                    $"predicate option reference mismatch: {contract.Name} {relationship.Kind}");

            foreach (AgentRelationshipPredicate predicate in trigger.Predicates)
            {
                if (!Enum.IsDefined(predicate.Source) ||
                    !Enum.IsDefined(predicate.Match) ||
                    string.IsNullOrWhiteSpace(predicate.Subject) ||
                    predicate.Values.IsDefaultOrEmpty ||
                    predicate.Values.Any(value => InvalidPredicateValue(
                        predicate.Source, value)))
                {
                    errors.Add(
                        $"invalid conditional predicate: {contract.Name} {relationship.Kind}");
                    continue;
                }

                if (predicate.Source == AgentPredicateSource.OptionValue)
                {
                    if (!optionNames.Contains(predicate.Subject))
                    {
                        errors.Add(
                            $"unknown option predicate subject: {contract.Name} --{predicate.Subject}");
                        continue;
                    }
                    foreach (string option in relationship.Options)
                    {
                        if (relationship.Kind != AgentOptionRelationshipKind.ForbiddenWhen && string.Equals(option, predicate.Subject,
                                StringComparison.OrdinalIgnoreCase))
                            errors.Add(
                                $"self-referential option relationship: {contract.Name} --{option}");
                        else if (relationship.Kind != AgentOptionRelationshipKind.ForbiddenWhen)
                            dependencyGraph[option].Add(predicate.Subject);
                    }
                    continue;
                }

                AgentArtifactContract? artifact = contract.InputArtifacts
                    .FirstOrDefault(item => string.Equals(
                        item.Kind,
                        predicate.Subject,
                        StringComparison.OrdinalIgnoreCase));
                if (artifact is null ||
                    predicate.Values.Any(value => !artifact.SchemaIds.Contains(
                        value,
                        StringComparer.Ordinal)) ||
                    predicate.MatchesWhenAbsent)
                    errors.Add(
                        $"unknown input artifact schema predicate: {contract.Name} {predicate.Subject}");
            }
        }

        if (HasCycle(dependencyGraph))
            errors.Add($"cyclic option relationship: {contract.Name}");

        if (contract.ContractStatus == AgentContractStatus.Complete &&
            !contract.InputArtifactKinds.SequenceEqual(
                contract.InputArtifacts.Select(item => item.Kind),
                StringComparer.Ordinal))
            errors.Add(
                $"input artifact kind projection mismatch: {contract.Name}");

        foreach (AgentArtifactContract artifact in contract.InputArtifacts
                     .AddRange(contract.OutputArtifacts))
        {
            if (string.IsNullOrWhiteSpace(artifact.Kind) ||
                string.IsNullOrWhiteSpace(artifact.Description) ||
                artifact.SchemaIds.Any(string.IsNullOrWhiteSpace))
                errors.Add($"invalid artifact metadata: {contract.Name}");
            ValidateProjectionTrigger(contract, artifact.Trigger, optionNames,
                "artifact", errors);
        }

        foreach (AgentEffectContract effect in contract.Effects)
            ValidateProjectionTrigger(contract, effect.Trigger, optionNames,
                "effect", errors);

        if (contract.CanonicalCommand is { } canonical &&
            !commandNames.Contains(canonical))
            errors.Add(
                $"unknown canonical command: {contract.Name} -> {canonical}");
    }

    private static void ValidateProjectionTrigger(
        AgentCommandContract contract,
        AgentOptionRelationshipTrigger? trigger,
        HashSet<string> optionNames,
        string projection,
        ImmutableArray<string>.Builder errors)
    {
        if (trigger is null) return;
        if (!Enum.IsDefined(trigger.Operator) || trigger.Predicates.IsDefaultOrEmpty)
        {
            errors.Add($"invalid {projection} trigger: {contract.Name}");
            return;
        }
        foreach (AgentRelationshipPredicate predicate in trigger.Predicates)
        {
            if (predicate.Source != AgentPredicateSource.OptionValue ||
                !Enum.IsDefined(predicate.Match) ||
                string.IsNullOrWhiteSpace(predicate.Subject) ||
                predicate.Values.IsDefaultOrEmpty ||
                predicate.Values.Any(value => InvalidPredicateValue(
                    predicate.Source, value)))
            {
                errors.Add($"invalid {projection} trigger predicate: {contract.Name}");
                continue;
            }
            if (!optionNames.Contains(predicate.Subject))
                errors.Add($"unknown {projection} trigger option subject: {contract.Name} --{predicate.Subject}");
        }
    }

    private static bool InvalidPredicateValue(
        AgentPredicateSource source,
        string? value) => value is null ||
        value.Length > 0 && string.IsNullOrWhiteSpace(value) ||
        value.Length == 0 && source != AgentPredicateSource.OptionValue;

    private static void ValidateCanonicalCommandGraphs(
        ImmutableArray<AgentCommandContract> contracts,
        HashSet<string> commandNames,
        ImmutableArray<string>.Builder errors)
    {
        var graph = commandNames.ToDictionary(
            item => item,
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach (AgentCommandContract contract in contracts)
        {
            if (contract.CanonicalCommand is not { } canonical ||
                !commandNames.Contains(canonical))
                continue;
            if (string.Equals(contract.Name, canonical,
                    StringComparison.OrdinalIgnoreCase))
                errors.Add(
                    $"self-referential canonical command: {contract.Name}");
            else
                graph[contract.Name].Add(canonical);
        }
        if (HasCycle(graph))
            errors.Add("cyclic canonical command graph");
    }

    private static bool HasCycle(
        Dictionary<string, HashSet<string>> graph)
    {
        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        bool Visit(string node)
        {
            if (!visiting.Add(node)) return true;
            if (visited.Contains(node))
            {
                visiting.Remove(node);
                return false;
            }
            foreach (string next in graph[node])
                if (Visit(next)) return true;
            visiting.Remove(node);
            visited.Add(node);
            return false;
        }
        return graph.Keys.Any(Visit);
    }

    private static void ValidateAuthority(
        AgentCommandContract contract,
        ImmutableArray<string>.Builder errors)
    {
        foreach (var kind in Enum.GetValues<AgentAuthorityKind>())
        {
            var count = contract.Authority.Count(item => item.Kind == kind);
            if (count == 0)
                errors.Add($"missing authority kind: {contract.Name} {kind}");
            else if (count > 1)
                errors.Add($"duplicate authority kind: {contract.Name} {kind}");
        }
    }

    private static AgentCommandContract Create(CommandDescriptor descriptor)
    {
        AgentCommandContract contract = IsOmittedFaceRuntimeCommand(descriptor.Name)
            ? OmittedFaceRuntimeContract(descriptor)
            : IsOmittedNpcCommand(descriptor.Name)
            ? OmittedNpcContract(descriptor)
            : IsObjectPresetCommand(descriptor.Name) &&
                                        descriptor.Name != "preset inspect"
            ? ObjectPresetContract(descriptor)
            : IsP09RecordCommand(descriptor.Name)
            ? P09RecordContract(descriptor)
            : IsPackageRuntimeCommand(descriptor.Name)
            ? PackageRuntimeContract(descriptor)
            : IsPreviewPluginCommand(descriptor.Name) &&
                                        descriptor.Name != "preview npc"
            ? PreviewPluginContract(descriptor)
            : IsNpcCreationCommand(descriptor.Name)
            ? NpcCreationContract(descriptor)
            : IsFaceGenMaterializationCommand(descriptor.Name)
            ? FaceGenMaterializationContract(descriptor)
            : IsPatchReadonlyFaceGenCommand(descriptor.Name)
            ? PatchReadonlyFaceGenContract(descriptor)
            : IsReferenceHairFinishCommand(descriptor.Name)
            ? ReferenceHairFinishContract(descriptor)
            : IsCoreBodyRecordCommand(descriptor.Name)
                ? CoreBodyRecordContract(descriptor)
            : descriptor.Name switch
        {
            "capabilities" => CapabilitiesDraft(descriptor),
            "version" => VersionDraft(descriptor),
            "schema export" => SchemaExportDraft(descriptor),
            "workspace preflight" => WorkspacePreflightDraft(descriptor),
            "preset inspect" => PresetInspectDraft(descriptor),
            "npc assembly preflight" =>
                ActorAssemblyPreflightDraft(descriptor),
            "npc create-from-jslot" =>
                CreateFromJslotPreflightDraft(descriptor),
            "runtime smoke verify" => RuntimeSmokeVerifyLegacy(descriptor),
            "preview npc" => PreviewDraft(descriptor),
            "npc finish verify" => FinishVerifyDraft(descriptor),
            "npc finish analyze" or "npc finish apply" => FinishCoreV2(descriptor),
            "npc voice discover" or "npc voice import" or "npc voice synthesize" or
            "npc dialogue analyze" or "npc dialogue apply" or "npc dialogue verify" =>
                VoiceDialogueContract(descriptor),
            "gui" => GuiLegacy(descriptor),
            "npc patch" => GoldenLegacy(descriptor,
                "The scalar patch path has no closed golden workflow artifact transition."),
            "package verify" => GoldenLegacy(descriptor,
                "The generic package manifest dialect is not the Finish Core workflow manifest."),
            "package archive" => GoldenLegacy(descriptor,
                "The generic package archive path is not a reviewed golden workflow transition."),
            _ => Legacy(descriptor)
        };
        if (contract.Effects.IsEmpty)
            return contract;
        return contract with
        {
            Effects = contract.Effects.Select(effect => effect with
                {
                    AllowedResultScopes = AllowedResultScopes(
                        contract.Name,
                        effect.Kind)
                })
                .ToImmutableArray()
        };
    }

    private static ImmutableArray<ApplicationEffectScope> AllowedResultScopes(
        string commandName,
        AgentEffectKind kind) =>
        kind switch
        {
            AgentEffectKind.ReadWorkspace
                when commandName == "workspace preflight" =>
                [
                    ApplicationEffectScope.Workspace,
                    ApplicationEffectScope.ReviewedWorkspace
                ],
            AgentEffectKind.ReadWorkspace =>
                [ApplicationEffectScope.Workspace],
            AgentEffectKind.WriteNewArtifact =>
                [ApplicationEffectScope.KLocalOutput],
            AgentEffectKind.DeployToCopiedData =>
                [ApplicationEffectScope.KLocalOutput],
            AgentEffectKind.AppendLocalOperationJournal =>
                [ApplicationEffectScope.WorkspaceLocalJournal],
            AgentEffectKind.InvokeAdmittedProcess or AgentEffectKind.LaunchDesktop =>
                [ApplicationEffectScope.Workspace],
            _ => []
        };

    private static void ValidateEffectScopes(
        AgentCommandContract contract,
        ImmutableArray<string>.Builder errors)
    {
        foreach (AgentEffectContract effect in contract.Effects)
        {
            if (effect.AllowedResultScopes.IsDefaultOrEmpty)
            {
                errors.Add(
                    $"missing allowed result scopes: {contract.Name} {effect.Kind}");
                continue;
            }

            var scopes = new HashSet<ApplicationEffectScope>();
            foreach (ApplicationEffectScope scope in effect.AllowedResultScopes)
            {
                if (!Enum.IsDefined(scope))
                    errors.Add(
                        $"undefined allowed result scope: {contract.Name} {effect.Kind}");
                else if (!scopes.Add(scope))
                    errors.Add(
                        $"duplicate allowed result scope: {contract.Name} {effect.Kind} {scope}");
                else if (!ApplicationEffectVocabulary.IsAdmittedPair(
                             effect.Kind, scope))
                    errors.Add(
                        $"illegal allowed result scope: {contract.Name} {effect.Kind} {scope}");
            }
        }
    }

    private static AgentCommandContract FinishVerifyDraft(
        CommandDescriptor descriptor) =>
        new(
            descriptor.Name,
            [],
            descriptor.SchemaVersion,
            ProtocolReadiness.V2,
            [GameEdition.SkyrimSpecialEdition],
            descriptor.Description,
            descriptor.Limitations.Add(
                "Writes one fresh external static-verification artifact; runtime, visual, and promotion authority remain required."),
            [
                Option("manifest", AgentValueKind.Path, true),
                Option("manifest-sha256", AgentValueKind.Sha256, true),
                Option("verification-output", AgentValueKind.Path, true),
                Option("workflow-bundle", AgentValueKind.Path, true),
                Option("workflow-bundle-sha256", AgentValueKind.Sha256, true),
                Option("workflow-output", AgentValueKind.Path, true),
                .. LegacyOptions("npc finish verify").Where(option => !option.Required)
            ],
            [WorkflowArtifactKinds.NpcFinishCoreManifest],
            [AgentProtocolSchemaIds.FinishVerifyResult],
            [
                new AgentEffectContract(
                    AgentEffectKind.ReadWorkspace,
                    "When the exact manifest, package, archive, and static evidence are verified.",
                    "workspace"),
                new AgentEffectContract(
                    AgentEffectKind.WriteNewArtifact,
                    "Only after independent static verification succeeds.",
                    "k-local-output"),
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput,
            AgentDeterminism.Deterministic,
            false,
            [
                AuthorityContract(AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Established,
                    "The exact manifest and archive bytes are admitted."),
                AuthorityContract(AgentAuthorityKind.SourceProviderIdentity,
                    AgentAuthorityState.Established,
                    "The manifest binds the source, plugin, and runtime identity."),
                AuthorityContract(AgentAuthorityKind.DeterministicMaterialization,
                    AgentAuthorityState.Established,
                    "The Finish package and archive remain hash-bound."),
                AuthorityContract(AgentAuthorityKind.IndependentStaticVerification,
                    AgentAuthorityState.Established,
                    "Static verification is persisted and independently reopened."),
                AuthorityContract(AgentAuthorityKind.OffEnginePreview,
                    AgentAuthorityState.NotApplicable,
                    "Finish Verify does not render a preview."),
                AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance,
                    AgentAuthorityState.Required,
                    "Static verification does not establish human visual acceptance."),
                AuthorityContract(AgentAuthorityKind.GameRuntimeVerification,
                    AgentAuthorityState.Required,
                    "Game-runtime verification remains required."),
                AuthorityContract(AgentAuthorityKind.PromotionApproval,
                    AgentAuthorityState.Required,
                    "Static verification never grants promotion approval.")
            ],
            [
                new AgentTransitionContract(
                    AgentWorkflowPhase.Verify,
                    AgentWorkflowPhase.RuntimeAcceptance,
                    [
                        WorkflowArtifactKinds.NpcFinishCoreVerification,
                        WorkflowArtifactKinds.PackageArchive
                    ]),
                new AgentTransitionContract(AgentWorkflowPhase.Verify, AgentWorkflowPhase.ReviewRequired,
                    [WorkflowArtifactKinds.NpcFinishCoreRequest, WorkflowArtifactKinds.NpcFinishCoreProposal,
                     WorkflowArtifactKinds.NpcFinishCoreVerification, WorkflowArtifactKinds.PackageArchive])
            ])
        {
            ContractStatus = AgentContractStatus.Complete,
            OptionRelationships = [RequiresTogether("data-root", "plugins")],
            InputArtifactKinds = [WorkflowArtifactKinds.NpcFinishCoreManifest, "workflow-bundle"],
            InputArtifacts =
            [
                .. FinishVerifyV1(descriptor).InputArtifacts,
                new("workflow-bundle", [AgentWorkflowSchemas.BundleV1],
                    "Exact hash-bound predecessor workflow containing the Finish manifest.")
            ],
            OutputArtifacts =
            [
                new(WorkflowArtifactKinds.NpcFinishCoreVerification,
                    [SkyrimNpcFinishCoreVerification.SchemaIdentifier,
                     SkyrimNpcFinishCoreVerification.ExternalSchemaIdentifier],
                    "Fresh canonical independent static-verification evidence, reopened before publication."),
                new("workflow-bundle", [AgentWorkflowSchemas.BundleV1],
                    "Fresh verified workflow successor retaining the manifest and archive bindings.")
            ],
            ResultShape = "object",
            ResultDescription =
                "Static verification outcome, persisted evidence and workflow bindings, external install observations when supplied, diagnostics, and separate human, runtime, and promotion requirements."
        };

    private static AgentCommandContract RuntimeSmokeVerifyLegacy(
        CommandDescriptor descriptor) => Legacy(descriptor) with
        {
            Options = [
                Option("edition", AgentValueKind.Enum, true, ["skyrimse"]),
                Option("runtime-report", AgentValueKind.Path, true),
                Option("package-acceptance", AgentValueKind.Path, true)
            ]
        };

    private static AgentCommandContract GuiLegacy(
        CommandDescriptor descriptor) => Legacy(descriptor) with
        {
            Options =
            [
                Option("launch", AgentValueKind.Boolean, false),
                Option("executable", AgentValueKind.Path, false),
                Option("workflow-bundle", AgentValueKind.Path, false),
                Option("workflow-bundle-sha256", AgentValueKind.Sha256, false)
            ]
        };

    private static AgentCommandContract PresetInspectDraft(
        CommandDescriptor descriptor) =>
        new(
            descriptor.Name,
            [],
            descriptor.SchemaVersion,
            ProtocolReadiness.V2,
            [GameEdition.SkyrimSpecialEdition],
            descriptor.Description,
            descriptor.Limitations.Add(
                "Protocol v2 admits one exact hash-bound RaceMenu JSlot and requires a fresh inspection receipt output."),
            [
                Option("format", AgentValueKind.Enum, true,
                    ["racemenu-jslot"]),
                Option("edition", AgentValueKind.Enum, true, ["skyrimse"]),
                Option("input", AgentValueKind.Path, true),
                Option("input-sha256", AgentValueKind.Sha256, true),
                Option("inspection-output", AgentValueKind.Path, true),
                Option("workflow-bundle", AgentValueKind.Path, true),
                Option("workflow-bundle-sha256", AgentValueKind.Sha256, true),
                Option("workflow-output", AgentValueKind.Path, true)
            ],
            [],
            [AgentProtocolSchemaIds.PresetInspectResult],
            [
                new AgentEffectContract(
                    AgentEffectKind.ReadWorkspace,
                    "When the exact JSlot source is admitted and parsed.",
                    "The exact --input file beneath the configured workspace."),
                new AgentEffectContract(
                    AgentEffectKind.WriteNewArtifact,
                    "After exact JSlot parsing reaches a typed inspection document.",
                    "The fresh exact --inspection-output receipt."),
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput,
            AgentDeterminism.Deterministic,
            false,
            [
                new AgentAuthorityContract(
                    AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Established,
                    "Exact ordinary K-local JSlot bytes and their uppercase digest are admitted."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.SourceProviderIdentity,
                    AgentAuthorityState.Required,
                    "Preset inspection does not establish plugin or asset provider identity."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.DeterministicMaterialization,
                    AgentAuthorityState.Established,
                    "The strict preset-inspection receipt is canonically materialized."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.IndependentStaticVerification,
                    AgentAuthorityState.Established,
                    "The receipt is read back, strictly parsed, and pinned and reloaded."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.OffEnginePreview,
                    AgentAuthorityState.NotApplicable,
                    "Preset inspection does not render a preview."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.HumanVisualAcceptance,
                    AgentAuthorityState.NotApplicable,
                    "Preset inspection does not request visual acceptance."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.GameRuntimeVerification,
                    AgentAuthorityState.Required,
                    "Game-runtime verification remains required."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.PromotionApproval,
                    AgentAuthorityState.NotApplicable,
                    "Preset inspection performs no promotion.")
            ],
            [
                new AgentTransitionContract(
                    AgentWorkflowPhase.Analyze,
                    AgentWorkflowPhase.Analyze,
                    [WorkflowArtifactKinds.RaceMenuJslot])
            ])
        {
            ContractStatus = AgentContractStatus.Complete,
            InputArtifactKinds =
            [
                WorkflowArtifactKinds.RaceMenuJslot,
                "workflow-bundle"
            ],
            InputArtifacts =
            [
                new AgentArtifactContract(
                    WorkflowArtifactKinds.RaceMenuJslot,
                    ["application/json"],
                    "Exact hash-bound RaceMenu JSlot admitted as JSON bytes."),
                new AgentArtifactContract(
                    "workflow-bundle",
                    [AgentWorkflowSchemas.BundleV1],
                    "Exact hash-bound predecessor workflow bundle.")
            ],
            OutputArtifacts =
            [
                new AgentArtifactContract(
                    PresetInspectionSchemas.ArtifactKind,
                    [PresetInspectionSchemas.CurrentDocument],
                    "Fresh canonical preset-inspection receipt, reopened and pinned to the admitted JSlot bytes."),
                new AgentArtifactContract(
                    "workflow-bundle",
                    [AgentWorkflowSchemas.BundleV1],
                    "Fresh independently verified workflow successor.")
            ],
            ResultShape = "object",
            ResultDescription =
                "Strict inspection outcome, canonical receipt path and digest, typed RaceMenu appearance evidence, diagnostics, workflow transition, and no runtime or visual-acceptance claim."
        };

    private static AgentCommandContract WorkspacePreflightDraft(
        CommandDescriptor descriptor) =>
        new(
            descriptor.Name,
            [],
            descriptor.SchemaVersion,
            ProtocolReadiness.V2,
            [GameEdition.SkyrimSpecialEdition],
            descriptor.Description,
            descriptor.Limitations.Add(
                "Protocol v2 requires a fresh --intake-output and persists only reviewed-intake schema 2."),
            [
                Option("game", AgentValueKind.Enum, true, ["skyrimse"]),
                Option("workspace-root", AgentValueKind.Path, true),
                Option("data-root", AgentValueKind.Path, true),
                Option("output-root", AgentValueKind.Path, true),
                Option("load-order", AgentValueKind.Path, true),
                Option("selected", AgentValueKind.String, false),
                Option("intake-output", AgentValueKind.Path, true),
                Option("npc-editor-id", AgentValueKind.Identifier, true),
                Option("workflow-output", AgentValueKind.Path, true)
            ],
            [],
            [AgentProtocolSchemaIds.WorkspacePreflightResult],
            [
                new AgentEffectContract(
                    AgentEffectKind.ReadWorkspace,
                    "When reviewed copied-workspace inputs are inspected.",
                    "The exact --workspace-root, --data-root, and bound input files."),
                new AgentEffectContract(
                    AgentEffectKind.WriteNewArtifact,
                    "Only after reviewed intake acceptance.",
                    "The fresh exact --intake-output file."),
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput,
            AgentDeterminism.Deterministic,
            false,
            [
                new AgentAuthorityContract(
                    AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Established,
                    "Exact K-local copied-workspace inputs are admitted."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.SourceProviderIdentity,
                    AgentAuthorityState.Established,
                    "The plugin closure and provider inventory are hash-bound."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.DeterministicMaterialization,
                    AgentAuthorityState.Established,
                    "Reviewed-intake schema 2 is canonically materialized."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.IndependentStaticVerification,
                    AgentAuthorityState.Established,
                    "Promoted bytes are read back and strictly parsed."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.OffEnginePreview,
                    AgentAuthorityState.NotApplicable,
                    "Workspace preflight does not render a preview."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.HumanVisualAcceptance,
                    AgentAuthorityState.NotApplicable,
                    "Workspace preflight does not request visual acceptance."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.GameRuntimeVerification,
                    AgentAuthorityState.Required,
                    "Game-runtime verification remains required."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.PromotionApproval,
                    AgentAuthorityState.NotApplicable,
                    "Workspace preflight performs no promotion.")
            ],
            [
                new AgentTransitionContract(
                    AgentWorkflowPhase.Discover,
                    AgentWorkflowPhase.Analyze,
                    [WorkflowArtifactKinds.ReviewedWorkspaceIntake])
            ]);

    private static AgentCommandContract ActorAssemblyPreflightLegacy(
        CommandDescriptor descriptor) =>
        Legacy(descriptor) with
        {
            ContractStatus = AgentContractStatus.Complete,
            Options =
            [
                Option("contract", AgentValueKind.Path, true),
                Option("contract-sha256", AgentValueKind.Sha256, true)
            ],
            InputArtifactKinds = ["actor-assembly-preflight-contract"],
            InputArtifacts =
            [
                new AgentArtifactContract(
                    "actor-assembly-preflight-contract",
                    [ActorAssemblyPreflightSchemas.ContractSchema],
                    "Exact hash-bound Actor Assembly contract admitted by the existing V1 handler.")
            ],
            ResultSchemaIds =
            [
                ActorAssemblyPreflightSchemas.ResultSchema,
                ActorAssemblyPreflightSchemas.ErrorSchema
            ],
            Effects =
            [
                new AgentEffectContract(AgentEffectKind.ReadWorkspace,
                    "When the exact contract and referenced package evidence are statically inspected.",
                    "workspace")
            ],
            RetryPolicy = AgentRetryPolicy.SafeUnchanged,
            Determinism = AgentDeterminism.Deterministic,
            Authority =
            [
                AuthorityContract(AgentAuthorityKind.InputAdmission, AgentAuthorityState.Established, "The exact contract path and physical digest are admitted."),
                AuthorityContract(AgentAuthorityKind.SourceProviderIdentity, AgentAuthorityState.Established, "The contract binds package and actor source identity for static inspection."),
                AuthorityContract(AgentAuthorityKind.DeterministicMaterialization, AgentAuthorityState.NotApplicable, "V1 assembly preflight is read-only and writes no result artifact."),
                AuthorityContract(AgentAuthorityKind.IndependentStaticVerification, AgentAuthorityState.Established, "Typed and raw record evidence is cross-checked without mutation."),
                AuthorityContract(AgentAuthorityKind.OffEnginePreview, AgentAuthorityState.NotApplicable, "Assembly preflight renders no preview."),
                AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance, AgentAuthorityState.Required, "Static evidence does not establish human visual acceptance."),
                AuthorityContract(AgentAuthorityKind.GameRuntimeVerification, AgentAuthorityState.Required, "Static evidence does not establish Skyrim runtime behavior."),
                AuthorityContract(AgentAuthorityKind.PromotionApproval, AgentAuthorityState.Required, "Preflight never grants promotion approval.")
            ],
            ResultShape = "object",
            ResultDescription =
                "Either the typed Actor Assembly result or refusal artifact with admitted state, static checks, evidence, diagnostics, no-write state, and runtimeAuthority false."
        };

    private static AgentCommandContract ActorAssemblyPreflightDraft(
        CommandDescriptor descriptor) =>
        new(
            descriptor.Name,
            [],
            descriptor.SchemaVersion,
            ProtocolReadiness.V2,
            descriptor.SupportedGames,
            descriptor.Description,
            descriptor.Limitations.Add(
                "Protocol v2 persists one exact static Actor Assembly preflight result; it does not create a workflow successor or establish visual, runtime, or promotion authority."),
            [
                Option("contract", AgentValueKind.Path, true),
                Option("contract-sha256", AgentValueKind.Sha256, true),
                Option("output", AgentValueKind.Path, true)
            ],
            [],
            [ActorAssemblyPreflightSchemas.ProtocolResultSchema],
            [
                new AgentEffectContract(
                    AgentEffectKind.ReadWorkspace,
                    "When the exact contract file and uppercase physical SHA-256 are admitted before service execution.",
                    "The exact K-local --contract file."),
                new AgentEffectContract(
                    AgentEffectKind.WriteNewArtifact,
                    "Only after a typed preflight outcome is canonically persisted and independently reopened.",
                    "The fresh exact K-local --output file."),
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput,
            AgentDeterminism.Deterministic,
            false,
            [
                new AgentAuthorityContract(
                    AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Established,
                    "The exact Actor Assembly contract and uppercase physical digest are admitted."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.SourceProviderIdentity,
                    AgentAuthorityState.Established,
                    "The preflight service evaluates the hash-bound package and actor identity evidence."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.DeterministicMaterialization,
                    AgentAuthorityState.Established,
                    "The typed Actor Assembly preflight result is canonically materialized."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.IndependentStaticVerification,
                    AgentAuthorityState.Established,
                    "The promoted result is retained-read, pinned-reopened, and strictly validated."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.OffEnginePreview,
                    AgentAuthorityState.NotApplicable,
                    "Actor Assembly preflight does not render an off-engine preview."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.HumanVisualAcceptance,
                    AgentAuthorityState.Required,
                    "Static preflight evidence does not establish human visual acceptance."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.GameRuntimeVerification,
                    AgentAuthorityState.Required,
                    "Static preflight evidence does not establish Skyrim runtime behavior."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.PromotionApproval,
                    AgentAuthorityState.Required,
                    "Static preflight does not grant promotion approval.")
            ],
            [])
        {
            ContractStatus = AgentContractStatus.Complete,
            InputArtifactKinds = ["actor-assembly-preflight-contract"],
            InputArtifacts =
            [
                new AgentArtifactContract(
                    "actor-assembly-preflight-contract",
                    [ActorAssemblyPreflightSchemas.ContractSchema],
                    "Exact hash-bound Actor Assembly contract.")
            ],
            OutputArtifacts =
            [
                new AgentArtifactContract(
                    ActorAssemblyPreflightSchemas.ArtifactKind,
                    [ActorAssemblyPreflightSchemas.ResultSchema],
                    "Fresh canonical typed result persisted at --output; refusal envelopes emit no artifact.")
            ],
            ResultShape = "object",
            ResultDescription =
                "Protocol envelope result containing the persisted typed Actor Assembly outcome or refusal, exact physical bindings, static checks, evidence, diagnostics, no-write state, and runtimeAuthority false."
        };

    private static AgentCommandContract CreateFromJslotPreflightDraft(
        CommandDescriptor descriptor) =>
        new(
            descriptor.Name,
            [],
            descriptor.SchemaVersion,
            ProtocolReadiness.V2,
            [GameEdition.SkyrimSpecialEdition],
            descriptor.Description,
            descriptor.Limitations
                .Add("Protocol v2 admits either a fresh preflight output or the complete reviewed-preflight build pair.")
                .Add("Provider-migration modes remain protocol v1 only."),
            [
                Option("request", AgentValueKind.Path, true),
                Option("request-sha256", AgentValueKind.Sha256, true),
                Option("preset", AgentValueKind.Path, true),
                Option("preset-sha256", AgentValueKind.Sha256, true),
                Option("data-root", AgentValueKind.Path, true),
                Option("plugins", AgentValueKind.String, true),
                Option("companion-root", AgentValueKind.Path, true),
                Option("preflight-output", AgentValueKind.Path, false) with
                {
                    ConflictsWith =
                        ["reviewed-preflight", "reviewed-preflight-sha256"]
                },
                Option("face-bake-authority-output", AgentValueKind.Path, false) with
                {
                    ConflictsWith = ["reviewed-preflight", "reviewed-preflight-sha256"]
                },
                Option("reviewed-preflight", AgentValueKind.Path, false) with
                {
                    ConflictsWith = ["preflight-output"]
                },
                Option("reviewed-preflight-sha256", AgentValueKind.Sha256,
                    false) with
                {
                    ConflictsWith = ["preflight-output"]
                },
                Option("workflow-bundle", AgentValueKind.Path, true),
                Option("workflow-bundle-sha256", AgentValueKind.Sha256, true),
                Option("workflow-output", AgentValueKind.Path, true)
            ],
            [],
            [
                AgentProtocolSchemaIds.NpcCreatePreflightResult,
                AgentProtocolSchemaIds.NpcCreateFromJslotBuildResult
            ],
            [
                new AgentEffectContract(
                    AgentEffectKind.ReadWorkspace,
                    "When the exact request, preset, copied Data, plugin order, companion inputs, reviewed preflight, and workflow are admitted for the selected mode.",
                    "The exact K-local preflight or reviewed-build inputs."),
                new AgentEffectContract(
                    AgentEffectKind.WriteNewArtifact,
                    "Only after every selected-mode gate passes and any produced package is independently reopened.",
                    "The fresh preflight, package, and/or workflow output for the selected mode."),
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput,
            AgentDeterminism.Deterministic,
            false,
            [
                new AgentAuthorityContract(
                    AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Established,
                    "The exact request, JSlot, copied Data, plugin order, and companion inputs are admitted."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.SourceProviderIdentity,
                    AgentAuthorityState.Established,
                    "The provider and final dependency closure are hash-bound."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.DeterministicMaterialization,
                    AgentAuthorityState.Established,
                    "The canonical NPC build-preflight artifact is materialized."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.IndependentStaticVerification,
                    AgentAuthorityState.Established,
                    "The promoted artifact is retained-read and independently reopened."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.OffEnginePreview,
                    AgentAuthorityState.NotApplicable,
                    "NPC build preflight does not render a preview."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.HumanVisualAcceptance,
                    AgentAuthorityState.NotApplicable,
                    "NPC preflight/static build does not request visual acceptance."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.GameRuntimeVerification,
                    AgentAuthorityState.Required,
                    "Game-runtime verification remains required."),
                new AgentAuthorityContract(
                    AgentAuthorityKind.PromotionApproval,
                    AgentAuthorityState.NotApplicable,
                    "NPC preflight/static build performs no promotion.")
            ],
            [
                new AgentTransitionContract(
                    AgentWorkflowPhase.Analyze,
                    AgentWorkflowPhase.Apply,
                    [WorkflowArtifactKinds.NpcBuildPreflight]),
                new AgentTransitionContract(
                    AgentWorkflowPhase.Apply,
                    AgentWorkflowPhase.Verify,
                    [WorkflowArtifactKinds.NpcPackageManifest])
            ])
        {
            ContractStatus = AgentContractStatus.Complete,
            OptionRelationships =
            [
                new AgentOptionRelationshipContract(
                    AgentOptionRelationshipKind.AtLeastOne,
                    ["preflight-output", "reviewed-preflight"], [],
                    "Select exactly one strict mode: fresh preflight output or reviewed-preflight build."),
                RequiresTogether("reviewed-preflight", "reviewed-preflight-sha256"),
                RequiredWhen(["preflight-output"], ["face-bake-authority-output"],
                    "Face-bake authority emission requires preflight authoring mode and subsequent explicit binding.",
                    Present("face-bake-authority-output"))
            ],
            InputArtifactKinds =
            [
                "npc-create-request",
                WorkflowArtifactKinds.RaceMenuJslot,
                WorkflowArtifactKinds.NpcBuildPreflight,
                "workflow-bundle"
            ],
            InputArtifacts =
            [
                new AgentArtifactContract(
                    "npc-create-request",
                    [RaceMenuNpcExecutionRequestSchemas.Request],
                    "Exact reviewed NPC execution request."),
                new AgentArtifactContract(
                    WorkflowArtifactKinds.RaceMenuJslot,
                    ["application/json"],
                    "Exact hash-bound RaceMenu JSlot preset."),
                new AgentArtifactContract(
                    WorkflowArtifactKinds.NpcBuildPreflight,
                    [NpcBuildPreflightSchemas.Artifact],
                    "Exact reviewed preflight admitted only in reviewed build mode.")
                    { Trigger = Present("reviewed-preflight") },
                new AgentArtifactContract(
                    "workflow-bundle",
                    [AgentWorkflowSchemas.BundleV1],
                    "Exact hash-bound predecessor workflow bundle.")
            ],
            OutputArtifacts =
            [
                new AgentArtifactContract(WorkflowArtifactKinds.NpcBuildPreflight,
                    [NpcBuildPreflightSchemas.Artifact],
                    "Fresh read-only preflight artifact in --preflight-output mode.")
                    { Trigger = Present("preflight-output") },
                new AgentArtifactContract("skyrim-face-bake-authority", ["skyrim-face-bake-authority/1"],
                    "Fresh canonical authority from exact model/carrier bytes; explicitly bind it and rerun preflight before build.")
                    { Trigger = Present("face-bake-authority-output") },
                new AgentArtifactContract(WorkflowArtifactKinds.NpcPackageManifest,
                    ["application/json"],
                    "Fresh independently reopened static NPC package in reviewed build mode.")
                    { Trigger = Present("reviewed-preflight") },
                new AgentArtifactContract(
                    "workflow-bundle",
                    [AgentWorkflowSchemas.BundleV1],
                    "Fresh independently verified workflow successor.")
            ],
            ResultShape = "object",
            ResultDescription =
                "Mode-specific protocol result: either typed preflight evidence or a statically verified package, plus workflow persistence, effects, diagnostics, and authority without runtime or visual claims."
        };

    private static AgentCommandContract PreviewDraft(
        CommandDescriptor descriptor) => new(
        descriptor.Name,
        [],
        descriptor.SchemaVersion,
        ProtocolReadiness.V2,
        [GameEdition.SkyrimSpecialEdition],
        descriptor.Description,
        descriptor.Limitations.Add(
            "Produces off-engine evidence only; human visual acceptance, Skyrim runtime verification, and promotion approval remain required."),
        [
            Option("intake", AgentValueKind.Path, true),
            Option("plugin", AgentValueKind.String, true),
            Option("form", AgentValueKind.Identifier, true),
            Option("package-manifest", AgentValueKind.Path, true),
            Option("expected-package-sha256", AgentValueKind.Sha256, true),
            Option("output-root", AgentValueKind.Path, true),
            Option("workflow-bundle", AgentValueKind.Path, true),
            Option("workflow-bundle-sha256", AgentValueKind.Sha256, true),
            Option("workflow-output", AgentValueKind.Path, true)
        ],
        [WorkflowArtifactKinds.ReviewedWorkspaceIntake,
         WorkflowArtifactKinds.NpcPackageManifest],
        [AgentProtocolSchemaIds.NpcVisualPreviewResult],
        [
            new AgentEffectContract(
                AgentEffectKind.ReadWorkspace,
                "When the exact package, reviewed intake, NPC identity, and workflow are admitted.",
                "workspace"),
            new AgentEffectContract(
                AgentEffectKind.InvokeAdmittedProcess,
                "When the admitted renderer composes the six deterministic off-engine views.",
                "workspace"),
            new AgentEffectContract(
                AgentEffectKind.WriteNewArtifact,
                "Only after every preview output is independently reopened.",
                "k-local-output"),
            JournalEffect()
        ],
        AgentRetryPolicy.RequiresFreshOutput,
        AgentDeterminism.Deterministic,
        false,
        [
            AuthorityContract(AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established,
                "The package, intake, workflow, and NPC identity are admitted."),
            AuthorityContract(AgentAuthorityKind.SourceProviderIdentity,
                AgentAuthorityState.Established,
                "The rendered source graph remains bound to the exact package."),
            AuthorityContract(AgentAuthorityKind.DeterministicMaterialization,
                AgentAuthorityState.Established,
                "The hash-bound static NPC package remains materialized."),
            AuthorityContract(AgentAuthorityKind.IndependentStaticVerification,
                AgentAuthorityState.Established,
                "The package and preview outputs are independently reopened."),
            AuthorityContract(AgentAuthorityKind.OffEnginePreview,
                AgentAuthorityState.Established,
                "Six deterministic off-engine views and evidence are persisted."),
            AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance,
                AgentAuthorityState.Required,
                "The agent may inspect evidence but cannot attest human acceptance."),
            AuthorityContract(AgentAuthorityKind.GameRuntimeVerification,
                AgentAuthorityState.Required,
                "Runtime testing remains user-operated."),
            AuthorityContract(AgentAuthorityKind.PromotionApproval,
                AgentAuthorityState.Required,
                "Preview production never grants promotion approval.")
        ],
        [
            new AgentTransitionContract(
                AgentWorkflowPhase.Verify,
                AgentWorkflowPhase.Review,
                [
                    WorkflowArtifactKinds.NpcPackageManifest,
                    WorkflowArtifactKinds.NpcPreviewManifest
                ])
        ])
    {
        ContractStatus = AgentContractStatus.Complete,
        InputArtifactKinds =
        [
            WorkflowArtifactKinds.ReviewedWorkspaceIntake,
            WorkflowArtifactKinds.NpcPackageManifest,
            "workflow-bundle"
        ],
        InputArtifacts =
        [
            new AgentArtifactContract(WorkflowArtifactKinds.ReviewedWorkspaceIntake,
                ["npcmanager-reviewed-game-intake/2"],
                "Exact codec-validated reviewed Skyrim intake."),
            new AgentArtifactContract(WorkflowArtifactKinds.NpcPackageManifest,
                ["application/json"],
                "Exact hash-bound NPC package overlay."),
            new AgentArtifactContract(
                "workflow-bundle",
                [AgentWorkflowSchemas.BundleV1],
                "Exact hash-bound predecessor workflow bundle.")
        ],
        OutputArtifacts =
        [
            new AgentArtifactContract(WorkflowArtifactKinds.NpcPreviewManifest,
                [NpcVisualPreviewPersistenceContract.BundleSchema],
                "Fresh independently reopened six-view off-engine preview bundle."),
            new AgentArtifactContract(
                "workflow-bundle",
                [AgentWorkflowSchemas.BundleV1],
                "Fresh independently verified workflow successor.")
        ],
        ResultShape = "object",
        ResultDescription =
            "Protocol result with composed state, persisted bundle versions, source route, off-engine label, contact sheet, six view paths and hashes, diagnostics, effects, and authority; runtime and human visual acceptance remain required."
    };

    private static AgentOptionContract Option(
        string name,
        AgentValueKind kind,
        bool required,
        ImmutableArray<string> allowedValues = default) =>
        new(
            name,
            name,
            kind,
            required,
            allowedValues.IsDefault ? [] : allowedValues,
            [],
            false)
        {
            ValueSyntax = OptionValueSyntax(name, kind,
                allowedValues.IsDefault ? [] : allowedValues),
            Description = OptionDescription(name)
        };

    private static string OptionValueSyntax(
        string name,
        AgentValueKind kind,
        ImmutableArray<string> allowedValues) =>
        !allowedValues.IsDefaultOrEmpty ? string.Join('|', allowedValues) :
        name switch
        {
            "plugins" => "<plugin-name,...>",
            "form" => "<nonzero-hexadecimal-form-id>",
            "request-sha256" or "preset-sha256" or "input-sha256" or
                "contract-sha256" or "expected-package-sha256" or
                "reviewed-preflight-sha256" or "workflow-bundle-sha256" =>
                "<uppercase-64-hex-sha256>",
            "plugin" => "<plugin-name>",
            _ => kind switch
            {
                AgentValueKind.Path => "<K-local-path>",
                AgentValueKind.Sha256 => "<64-hex-sha256>",
                AgentValueKind.Identifier => "<identifier>",
                AgentValueKind.String => "<value>",
                _ => $"<{kind.ToString().ToLowerInvariant()}>"
            }
        };

    private static string OptionDescription(string name) => name switch
    {
        "format" => "Exact preset format; strict inspection accepts only racemenu-jslot.",
        "edition" => "Exact game edition admitted by the strict command.",
        "request" => "Exact reviewed NPC execution request path.",
        "request-sha256" => "Uppercase physical SHA-256 of the exact request bytes.",
        "preset" => "Exact RaceMenu JSlot preset path.",
        "preset-sha256" => "Uppercase physical SHA-256 of the exact JSlot bytes.",
        "data-root" => "Existing copied Skyrim Data root used only for admitted static inspection.",
        "plugins" => "Nonempty distinct comma-separated plugin order.",
        "companion-root" => "Existing K-local companion evidence root.",
        "preflight-output" => "Fresh output selecting read-only preflight mode; conflicts with the reviewed-preflight pair.",
        "face-bake-authority-output" => "Fresh canonical face-bake authority output, only with preflight mode; explicitly bind the returned path/hash and rerun ordinary preflight before build.",
        "reviewed-preflight" => "Exact reviewed preflight artifact selecting static build mode.",
        "reviewed-preflight-sha256" => "Uppercase physical SHA-256 required with --reviewed-preflight.",
        "input" => "Exact ordinary K-local RaceMenu JSlot input.",
        "input-sha256" => "Uppercase physical SHA-256 of the exact input bytes.",
        "inspection-output" => "Fresh canonical preset-inspection receipt output.",
        "intake" => "Exact codec-validated reviewed Skyrim intake.",
        "plugin" => "Selected NPC owner plugin name.",
        "form" => "Selected non-null NPC FormID.",
        "package-manifest" => "Exact NPC package manifest required by strict preview.",
        "expected-package-sha256" => "Uppercase physical SHA-256 binding the package manifest.",
        "output-root" => "Fresh K-local six-view preview output root.",
        "contract" => "Exact K-local Actor Assembly contract path.",
        "contract-sha256" => "Physical SHA-256 binding the exact Actor Assembly contract bytes.",
        "output" => "Fresh K-local persisted result output.",
        "workflow-bundle" => "Exact resumable workflow bundle input.",
        "workflow-bundle-sha256" => "Uppercase physical SHA-256 binding the workflow bundle.",
        "workflow-output" => "Fresh successor workflow bundle output.",
        _ => $"Typed value for --{name}."
    };

    private static AgentCommandContract CapabilitiesDraft(
        CommandDescriptor descriptor) =>
        KernelDiscoveryDraft(
            descriptor,
            "No command-specific input is accepted.");

    private static AgentCommandContract VersionDraft(
        CommandDescriptor descriptor) =>
        KernelDiscoveryDraft(
            descriptor,
            "No command-specific input is accepted.") with
        {
            ContractStatus = AgentContractStatus.Complete,
            ResultShape = "object",
            ResultDescription =
                "Deterministic product, build, target-framework, and supported-protocol discovery."
        };

    private static AgentCommandContract KernelDiscoveryDraft(
        CommandDescriptor descriptor,
        string inputAdmissionReason) =>
        new(
            descriptor.Name,
            [],
            descriptor.SchemaVersion,
            ProtocolReadiness.V2,
            descriptor.SupportedGames,
            descriptor.Description,
            [],
            [],
            [],
            [descriptor.Name switch
            {
                "capabilities" => AgentProtocolSchemaIds.CapabilitiesResult,
                "version" => AgentProtocolSchemaIds.VersionResult,
                _ => throw new InvalidOperationException(
                    $"Unknown discovery command '{descriptor.Name}'.")
            }],
            [JournalEffect()],
            AgentRetryPolicy.SafeUnchanged,
            AgentDeterminism.Deterministic,
            false,
            CompleteAuthority(
                inputAdmissionReason,
                AgentAuthorityState.NotApplicable,
                "This discovery command does not materialize an artifact."),
            []);

    private static AgentCommandContract SchemaExportDraft(
        CommandDescriptor descriptor) =>
        new(
            descriptor.Name,
            [],
            descriptor.SchemaVersion,
            ProtocolReadiness.V2,
            descriptor.SupportedGames,
            descriptor.Description,
            [
                "Protocol v2 schema export refuses to overwrite an existing output file."
            ],
            [
                new AgentOptionContract(
                    "command",
                    "command",
                    AgentValueKind.String,
                    false,
                    [],
                    [],
                    false),
                new AgentOptionContract(
                    "output",
                    "output",
                    AgentValueKind.Path,
                    false,
                    [],
                    [],
                    false)
            ],
            [],
            [AgentProtocolSchemaIds.SchemaExportResult],
            [
                new AgentEffectContract(
                    AgentEffectKind.WriteNewArtifact,
                    "When --output is supplied.",
                    "The admitted K-local --output path."),
                JournalEffect()
            ],
            AgentRetryPolicy.RequiresFreshOutput,
            AgentDeterminism.Deterministic,
            false,
            CompleteAuthority(
                "Command options are admitted before schema projection or output.",
                AgentAuthorityState.Established,
                "The schema result is deterministically materialized in the response."),
            []);

    private static AgentCommandContract Legacy(
        CommandDescriptor descriptor) =>
        new(
            descriptor.Name,
            [],
            descriptor.SchemaVersion,
            ProtocolReadiness.Legacy,
            descriptor.SupportedGames,
            descriptor.Description,
            descriptor.Limitations,
            [],
            [],
            [],
            [],
            AgentRetryPolicy.NotRetryable,
            AgentDeterminism.EnvironmentDependent,
            false,
            [],
            []);

    private static AgentCommandContract FinishApplyLegacy(
        CommandDescriptor descriptor) => GoldenLegacy(
            descriptor,
            "A protocol-v2 review receipt is optional; absent human acceptance remains an outstanding workflow requirement.") with
        {
            ContractStatus = AgentContractStatus.Complete,
            Options = LegacyOptions("npc finish apply"),
            OptionRelationships =
            [
                RequiresTogether("data-root", "plugins"),
                new AgentOptionRelationshipContract(
                    AgentOptionRelationshipKind.RequiredWhen,
                    ["data-root", "plugins"],
                    ["validate-all"],
                    "Required for an external-schema request when --validate-all is not true or 1; validation mode may omit both.")
                {
                    Trigger = new AgentOptionRelationshipTrigger(
                        AgentPredicateCombination.All,
                        [
                            new AgentRelationshipPredicate(
                                AgentPredicateSource.OptionValue,
                                "validate-all",
                                AgentPredicateMatch.NoneOf,
                                ["true", "1"],
                                true),
                            new AgentRelationshipPredicate(
                                AgentPredicateSource.InputArtifactSchema,
                                WorkflowArtifactKinds.NpcFinishCoreRequest,
                                AgentPredicateMatch.AnyOf,
                                [SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier],
                                false)
                        ])
                }
            ],
            InputArtifactKinds = ArtifactKinds(FinishApplyInputArtifacts),
            InputArtifacts = FinishApplyInputArtifacts,
            OutputArtifacts =
            [
                new AgentArtifactContract(
                    WorkflowArtifactKinds.NpcFinishCoreManifest,
                    [
                        SkyrimNpcFinishCoreManifest.SchemaIdentifier,
                        SkyrimNpcFinishCoreManifest.ExternalSchemaIdentifier
                    ],
                    "Fresh applied package manifest when mutation succeeds.")
            ],
            ResultShape = "object",
            ResultDescription =
                "Apply status, fresh output/archive paths, and diagnostics; no grounded result schema identifier is published.",
            Effects =
            [
                new AgentEffectContract(
                    AgentEffectKind.ReadWorkspace,
                    "When admitting the exact request, proposal, and optional install context.",
                    "workspace"),
                new AgentEffectContract(
                    AgentEffectKind.WriteNewArtifact,
                    "Only after all request, proposal, authority, and optional install-context checks pass.",
                    "k-local-output"),
                JournalEffect()
            ],
            RetryPolicy = AgentRetryPolicy.RequiresFreshOutput,
            Determinism = AgentDeterminism.PinnedInputsAndTools,
            SupportsDryRun = false,
            Authority =
            [
                AuthorityContract(AgentAuthorityKind.InputAdmission,
                    AgentAuthorityState.Established,
                    "Exact hash-bound request and proposal bytes are admitted."),
                AuthorityContract(AgentAuthorityKind.SourceProviderIdentity,
                    AgentAuthorityState.Established,
                    "The request and proposal bind source/provider identity."),
                AuthorityContract(AgentAuthorityKind.DeterministicMaterialization,
                    AgentAuthorityState.Established,
                    "The applied package remains bound to reviewed inputs and fresh outputs."),
                AuthorityContract(AgentAuthorityKind.IndependentStaticVerification,
                    AgentAuthorityState.Required,
                    "Apply output still requires the independent Finish Verify step."),
                AuthorityContract(AgentAuthorityKind.OffEnginePreview,
                    AgentAuthorityState.NotApplicable,
                    "Finish Apply does not render an off-engine preview."),
                AuthorityContract(AgentAuthorityKind.HumanVisualAcceptance,
                    AgentAuthorityState.Required,
                    "Static apply output does not establish human visual acceptance."),
                AuthorityContract(AgentAuthorityKind.GameRuntimeVerification,
                    AgentAuthorityState.Required,
                    "Static apply output does not establish game-runtime behavior."),
                AuthorityContract(AgentAuthorityKind.PromotionApproval,
                    AgentAuthorityState.Required,
                    "Finish Apply never grants promotion approval.")
            ]
        };

    private static ImmutableArray<AgentOptionContract> LegacyOptions(
        string commandName) => LegacyCommandOptionCatalog.For(commandName)
        .Select(option => new AgentOptionContract(
            option.Name,
            option.Name,
            LegacyValueKind(commandName, option),
            option.Required,
            option.AcceptedValues,
            PreviewPluginOptionCatalog.Contains(commandName)
                ? PreviewPluginOptionCatalog.ConflictsFor(commandName, option.Name)
                : LegacyConflicts(commandName, option.Name),
            false)
        {
            ValueSyntax = option.ValueSyntax,
            Description = option.Description,
            AliasFor = LegacyAliasFor(commandName, option.Name)
        }).ToImmutableArray();

    private static AgentValueKind LegacyValueKind(string commandName, LegacyCommandOption option) =>
        OmittedFaceRuntimeOptionCatalog.Contains(commandName)
            ? OmittedFaceRuntimeOptionCatalog.ValueKindFor(commandName, option.Name)
            : OmittedNpcOptionCatalog.Contains(commandName)
            ? OmittedNpcOptionCatalog.ValueKindFor(commandName, option)
            : ObjectPresetOptionCatalog.Contains(commandName)
            ? ObjectPresetOptionCatalog.ValueKindFor(option)
            : P09RecordOptionCatalog.Contains(commandName)
            ? P09RecordOptionCatalog.ValueKindFor(commandName, option)
            : PackageRuntimeOptionCatalog.Contains(commandName)
            ? PackageRuntimeOptionCatalog.ValueKindFor(option)
            : PreviewPluginOptionCatalog.Contains(commandName)
            ? PreviewPluginOptionCatalog.ValueKindFor(commandName, option)
            : NpcCreationOptionCatalog.Contains(commandName)
            ? NpcCreationOptionCatalog.ValueKindFor(option)
            : FaceGenMaterializationOptionCatalog.IsFamily(commandName)
            ? FaceGenMaterializationOptionCatalog.ValueKindFor(option)
            : ReferenceHairFinishOptionCatalog.Contains(commandName)
            ? ReferenceHairFinishOptionCatalog.ValueKindFor(option)
            : PatchReadonlyFaceGenOptionCatalog.IsFamily(commandName)
            ? PatchReadonlyFaceGenOptionCatalog.ValueKindFor(commandName, option)
            : CoreBodyRecordOptionCatalog.Contains(commandName)
            ? CoreBodyRecordOptionCatalog.ValueKindFor(commandName, option)
            : LegacyValueKind(option);

    private static AgentValueKind LegacyValueKind(LegacyCommandOption option) =>
        option.Name switch
        {
            "validate-all" or "play" or "render-headwear" or "allow-null" or
                "changed-only" or "dry-run" or "apply" or "clear-skin" =>
                AgentValueKind.Boolean,
            "edition" or "game" => AgentValueKind.Enum,
            "frame" or "fps" or "width" or "height" =>
                AgentValueKind.Integer,
            "request-sha256" or "proposal-sha256" or "expected-sha256" or
                "input-sha" or "input-sha256" or "workflow-bundle-sha256" => AgentValueKind.Sha256,
            "manifest" or "output" or "asset-root" or "image-output" or
                "data-root" or "request" or "proposal" => AgentValueKind.Path,
            "outfit" => AgentValueKind.ArtifactReference,
            "variant" or "camera" or "lighting" or "animation" =>
                AgentValueKind.Identifier,
            _ => AgentValueKind.String
        };

    private static string? LegacyAliasFor(string commandName, string optionName) => (commandName, optionName) switch
    {
        _ when OmittedFaceRuntimeOptionCatalog.Contains(commandName) =>
            OmittedFaceRuntimeOptionCatalog.AliasFor(commandName, optionName),
        _ when OmittedNpcOptionCatalog.Contains(commandName) =>
            OmittedNpcOptionCatalog.AliasFor(commandName, optionName),
        _ when ObjectPresetOptionCatalog.Contains(commandName) =>
            ObjectPresetOptionCatalog.AliasFor(optionName),
        _ when P09RecordOptionCatalog.Contains(commandName) =>
            P09RecordOptionCatalog.AliasFor(optionName),
        _ when PackageRuntimeOptionCatalog.Contains(commandName) =>
            PackageRuntimeOptionCatalog.AliasFor(commandName, optionName),
        _ when PreviewPluginOptionCatalog.Contains(commandName) =>
            PreviewPluginOptionCatalog.AliasFor(commandName, optionName),
        ("npc create-from-jslot", "provider-migration") => "reviewed-provider-migration",
        ("npc create-from-jslot", "provider-migration-sha256") => "reviewed-provider-migration-sha256",
        ("facegen options", "options") => "input",
        ("facegen bake-all", "batch") => "manifests",
        ("facegen build-plugin", "target") => "manifest",
        ("facegen build-plugin", "target-plugin") => "plugin",
        ("body sidecar write" or "body overlay patch" or "body overlay bake" or
            "body transforms apply" or "body reset" or "body weight normalize" or
            "body weight redistribute", "edition") => "game",
        ("body sidecar write" or "body overlay patch" or "body overlay bake" or
            "body transforms apply" or "body reset" or "body weight normalize" or
            "body weight redistribute", "game") => null,
        ("npc face-patch", "game") => null,
        (_, "game") => "edition",
        (_, "loadorder") => "load-order",
        ("assets index", "category") => "filter",
        ("assets search" or "forms search", "search") => "query",
        ("headpart choices" or "paint choices", "query") => "search",
        ("forms search", "signature") => "type",
        ("body overlay patch" or "body transforms apply" or "body reset", "form-id") => "npc",
        ("body patch", "npc") => "form-id",
        ("npc patch", "npc") => "form-id",
        ("npc patch", "input-sha") => "input-sha256",
        ("npc patch", "expected-sha256") => "input-sha256",
        ("body patch", "input-sha") => "input-sha256",
        ("body patch", "expected-sha256") => "input-sha256",
        ("npc face-patch", "edition") => "game",
        ("npc face-patch", "npc") => "form-id",
        ("npc edit-package", "expected-sha256") => "input-sha256",
        ("body sidecar write", "morphs") => "sliders",
        ("body transforms apply", "skins") => "skin-overrides",
        ("body weight normalize" or "body weight redistribute", "current") => "triangle",
        _ => null
    };

    private static ImmutableArray<string> LegacyConflicts(
        string commandName,
        string optionName) => OmittedNpcOptionCatalog.Contains(commandName)
            ? OmittedNpcOptionCatalog.ConflictsFor(commandName, optionName)
            : optionName switch
        {
            "frame" => ["time"],
            "time" => ["frame"],
            "weight" when commandName is "body patch" or "npc patch" => ["weight-triangle"],
            "weight-triangle" when commandName is "body patch" or "npc patch" => ["weight"],
            "level" when commandName is "body patch" or "npc patch" or "npc edit-package" => ["level-mult"],
            "level-mult" when commandName is "body patch" or "npc patch" or "npc edit-package" => ["level"],
            _ => []
        };

    private static AgentOptionRelationshipContract RequiresTogether(
        string first,
        string second) => new(
            AgentOptionRelationshipKind.RequiresTogether,
            [first, second],
            [],
            $"--{first} and --{second} must be supplied together.");

    private static ImmutableArray<string> ArtifactKinds(
        ImmutableArray<AgentArtifactContract> artifacts) =>
        artifacts.Select(item => item.Kind).ToImmutableArray();

    private static AgentCommandContract GoldenLegacy(
        CommandDescriptor descriptor,
        string limitation) => Legacy(descriptor) with
        {
            Limitations = descriptor.Limitations.Add(limitation)
        };

    private static AgentEffectContract JournalEffect() =>
        new(
            AgentEffectKind.AppendLocalOperationJournal,
            JournalCondition,
            JournalScope);

    private static AgentAuthorityContract AuthorityContract(
        AgentAuthorityKind kind,
        AgentAuthorityState state,
        string reason) => new(kind, state, reason);

    private static ImmutableArray<AgentAuthorityContract> CompleteAuthority(
        string inputAdmissionReason,
        AgentAuthorityState materializationState,
        string materializationReason) =>
        [
            new(
                AgentAuthorityKind.InputAdmission,
                AgentAuthorityState.Established,
                inputAdmissionReason),
            new(
                AgentAuthorityKind.SourceProviderIdentity,
                AgentAuthorityState.NotApplicable,
                "This command does not consume source-provider artifacts."),
            new(
                AgentAuthorityKind.DeterministicMaterialization,
                materializationState,
                materializationReason),
            new(
                AgentAuthorityKind.IndependentStaticVerification,
                AgentAuthorityState.NotApplicable,
                "This command does not verify a produced game artifact."),
            new(
                AgentAuthorityKind.OffEnginePreview,
                AgentAuthorityState.NotApplicable,
                "This command does not produce an off-engine preview."),
            new(
                AgentAuthorityKind.HumanVisualAcceptance,
                AgentAuthorityState.NotApplicable,
                "This command does not request visual acceptance."),
            new(
                AgentAuthorityKind.GameRuntimeVerification,
                AgentAuthorityState.NotApplicable,
                "This command does not make a game-runtime claim."),
            new(
                AgentAuthorityKind.PromotionApproval,
                AgentAuthorityState.NotApplicable,
                "This command does not promote an artifact.")
        ];

    private static void ValidateV2(
        AgentCommandContract contract,
        ImmutableArray<string>.Builder errors)
    {
        if (string.IsNullOrWhiteSpace(contract.CommandSchemaVersion))
            errors.Add($"v2 command missing schema version: {contract.Name}");
        if (contract.SupportedGames.Length == 0)
            errors.Add($"v2 command missing supported games: {contract.Name}");
        if (string.IsNullOrWhiteSpace(contract.Purpose))
            errors.Add($"v2 command missing purpose: {contract.Name}");
        if (contract.ResultSchemaIds.Length == 0 ||
            contract.ResultSchemaIds.Any(string.IsNullOrWhiteSpace))
            errors.Add($"v2 command missing result schema: {contract.Name}");
        if (contract.Effects.Length == 0 ||
            contract.Effects.Any(item =>
                string.IsNullOrWhiteSpace(item.Condition) ||
                string.IsNullOrWhiteSpace(item.Scope)))
            errors.Add($"v2 command missing effect declaration: {contract.Name}");
        if (contract.Authority.Length == 0 ||
            contract.Authority.Any(item =>
                string.IsNullOrWhiteSpace(item.Reason)))
            errors.Add($"v2 command missing authority declaration: {contract.Name}");

        var optionNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        var jsonNames = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var option in contract.Options)
        {
            if (string.IsNullOrWhiteSpace(option.CliName) ||
                !optionNames.Add(option.CliName))
                errors.Add(
                    $"duplicate option name: {contract.Name} --{option.CliName}");
            if (string.IsNullOrWhiteSpace(option.JsonName) ||
                !jsonNames.Add(option.JsonName))
                errors.Add(
                    $"duplicate option json name: {contract.Name} {option.JsonName}");
            if (option.ValueKind is AgentValueKind.Enum or AgentValueKind.EnumList &&
                option.AllowedValues.Length == 0)
                errors.Add(
                    $"v2 enum option missing allowed values: {contract.Name} --{option.CliName}");
        }

        foreach (var option in contract.Options)
        {
            foreach (var conflict in option.ConflictsWith)
            {
                if (!optionNames.Contains(conflict) ||
                    string.Equals(
                        option.CliName,
                        conflict,
                        StringComparison.OrdinalIgnoreCase))
                    errors.Add(
                        $"unknown option conflict: {contract.Name} --{option.CliName} conflicts with --{conflict}");
            }
        }

        foreach (var transition in contract.Transitions)
        {
            if (!IsLegalWorkflowTransition(transition.From, transition.To) ||
                transition.RequiredBindings.IsDefaultOrEmpty ||
                transition.RequiredBindings.Any(string.IsNullOrWhiteSpace))
                errors.Add(
                    $"illegal transition: {contract.Name} {transition.From}->{transition.To}");
        }

        if (contract.SupportsDryRun &&
            !contract.Options.Any(item =>
                item.ValueKind == AgentValueKind.Boolean &&
                string.Equals(
                    item.CliName,
                    "dry-run",
                    StringComparison.OrdinalIgnoreCase)))
            errors.Add($"dry-run option missing: {contract.Name}");
    }

    private static bool IsLegalWorkflowTransition(
        AgentWorkflowPhase from,
        AgentWorkflowPhase to) =>
        // Workflow phases are states, not ordinal ranks. Review can return an
        // accepted artifact to analysis before the next apply/verify cycle.
        (from, to) is
            (AgentWorkflowPhase.Discover, AgentWorkflowPhase.Preflight) or
            (AgentWorkflowPhase.Discover, AgentWorkflowPhase.Analyze) or
            (AgentWorkflowPhase.Preflight, AgentWorkflowPhase.Analyze) or
            (AgentWorkflowPhase.Analyze, AgentWorkflowPhase.Analyze) or
            (AgentWorkflowPhase.Analyze, AgentWorkflowPhase.Propose) or
            (AgentWorkflowPhase.Analyze, AgentWorkflowPhase.Apply) or
            (AgentWorkflowPhase.Analyze, AgentWorkflowPhase.ReviewRequired) or
            (AgentWorkflowPhase.Apply, AgentWorkflowPhase.ReviewRequired) or
            (AgentWorkflowPhase.Verify, AgentWorkflowPhase.ReviewRequired) or
            (AgentWorkflowPhase.Propose, AgentWorkflowPhase.Review) or
            (AgentWorkflowPhase.Review, AgentWorkflowPhase.Analyze) or
            (AgentWorkflowPhase.Review, AgentWorkflowPhase.Apply) or
            (AgentWorkflowPhase.Apply, AgentWorkflowPhase.Verify) or
            (AgentWorkflowPhase.Verify, AgentWorkflowPhase.Review) or
            (AgentWorkflowPhase.Verify, AgentWorkflowPhase.Package) or
            (AgentWorkflowPhase.Verify, AgentWorkflowPhase.RuntimeAcceptance) or
            (AgentWorkflowPhase.Package, AgentWorkflowPhase.Deploy) or
            (AgentWorkflowPhase.Package, AgentWorkflowPhase.RuntimeAcceptance) or
            (AgentWorkflowPhase.Deploy, AgentWorkflowPhase.RuntimeAcceptance);
}
