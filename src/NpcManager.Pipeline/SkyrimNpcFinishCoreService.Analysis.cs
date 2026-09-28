using System.Collections.Immutable;
using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

public sealed partial class SkyrimNpcFinishCoreService
{
    private sealed record ExternalFinishCoreState(
        ExternalHeadPartInstallVerificationResult Result,
        SkyrimNpcFinishCoreExternalHeadPartProposalAuthority ProposalAuthority,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> Descriptors,
        ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation> Attestations,
        RaceMenuSelectedDependencyManifestArtifact SelectedManifest);

    private static readonly ImmutableArray<string> ForbiddenSignatures =
    [
        "CELL", "WRLD", "ACHR", "REFR", "LAND", "NAVM", "NAVI",
        "WATR", "LTEX", "LCTN", "REGN", "CLMT", "MUSC", "IMGS"
    ];
    private static readonly FormReference PotentialFollowerFaction =
        new(new PluginName("Skyrim.esm"), new FormId(0x5C84D));
    private static readonly FormReference CurrentFollowerFaction =
        new(new PluginName("Skyrim.esm"), new FormId(0x5C84E));

    private static ImmutableArray<Diagnostic> ValidateProposalPath(
        WorkspacePath proposalPath,
        WorkspacePath projectRoot)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!proposalPath.IsUnder(projectRoot))
            diagnostics.Add(new Diagnostic(
                "finish-core-proposal-outside-project",
                DiagnosticSeverity.Error,
                "The proposal must remain under the K-local project root."));
        string? parent = Path.GetDirectoryName(proposalPath.Value);
        if (parent is null || !Directory.Exists(parent))
            diagnostics.Add(new Diagnostic(
                "finish-core-proposal-parent-missing",
                DiagnosticSeverity.Error,
                "The proposal parent directory must already exist."));
        return diagnostics.ToImmutable();
    }

    private static ImmutableArray<Diagnostic> ValidateSourceAndRequest(
        SkyrimNpcFinishCoreRequest request,
        SkyrimNpcFinishCoreSourceReadResult source)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!Enum.IsDefined(request.OutfitRacePolicy))
            diagnostics.Add(Error("finish-core-outfit-race-policy", "outfitRacePolicy must be refuse or clone."));
        if ((request.CombatPolicy is not null || !request.PerkPolicy.IsDefault) &&
            request.Schema is not SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier and
                not SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier)
            diagnostics.Add(Error("finish-core-policy-schema", "Combat and perk policy require a v3 or v4 request."));
        if (request.CombatPolicy?.Profile is { } profile && !Enum.IsDefined(profile))
            diagnostics.Add(Error("finish-core-combat-profile", "Combat profile must be defensive, rangedFirst, or meleeFirst."));
        if (!request.PerkPolicy.IsDefault && (request.PerkPolicy.Any(perk => perk.Rank == 0 || perk.Form.FormId.Value == 0) ||
            request.PerkPolicy.Select(perk => perk.Form.ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PerkPolicy.Length))
            diagnostics.Add(Error("finish-core-perk-policy", "Perk policy requires unique non-null FormRefs and ranks in [1, 255]."));
        if (source.BaseNpc != new FormReference(
                request.Source.Plugin!.Value,
                request.Actor.FormId!.Value) ||
            source.TargetEditorId != request.Actor.EditorId!.Value)
            diagnostics.Add(Error(
                "finish-core-source-identity",
                "The source snapshot identity differs from the requested actor."));
        if (!source.ActorAssemblyPass || source.PlacementMode != "None")
            diagnostics.Add(Error(
                "finish-core-source-actor-assembly",
                "Actor Assembly must be Pass with placement.mode=None."));
        if (!request.FollowerPolicy.DefensiveOnly && request.CombatPolicy is null)
            diagnostics.Add(Error(
                "finish-core-policy-defensive-only",
                "Finish Core requires followerPolicy.defensiveOnly=true and authors only the closed zero-offense defensive combat style."));
        SkyrimNpcFinishCoreAssistance effectiveAssistance =
            request.AiPolicy?.Assistance ?? source.AiData?.Assistance ??
            SkyrimNpcFinishCoreAssistance.HelpsNobody;
        if (request.FollowerPolicy.Recruitable &&
            effectiveAssistance == SkyrimNpcFinishCoreAssistance.HelpsNobody)
            diagnostics.Add(Error(
                "finish-core-policy-assistance",
                "A recruitable follower may not retain AIDT assistance=HelpsNobody; supply an explicit aiPolicy that assists allies or friends and allies."));
        if (source.TypedForbiddenCounts.Any(row => row.Value != 0) ||
            source.RawForbiddenCounts.Any(row => row.Value != 0))
            diagnostics.Add(Error(
                "finish-core-source-forbidden-signature",
                "Finish Core proposals require zero total forbidden world/reference signatures."));

        string[] masterNames = source.MasterOrder.ToArray();
        if (masterNames.Distinct(StringComparer.OrdinalIgnoreCase).Count() != masterNames.Length)
            diagnostics.Add(Error(
                "finish-core-master-duplicate",
                "The source master order contains case-insensitive duplicate identities."));
        if (request.OutfitPolicy.ArmorItems
                .Select(item => item.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.OutfitPolicy.ArmorItems.Length)
            diagnostics.Add(Error(
                "finish-core-authority-outfit",
                "Private outfit armor identities must be unique and ordered."));
        if (request.InventoryPolicy.ExpectedSourceItems
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.InventoryPolicy.ExpectedSourceItems.Length ||
            request.InventoryPolicy.DesiredItems
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.InventoryPolicy.DesiredItems.Length)
            diagnostics.Add(Error(
                "finish-core-inventory-duplicate",
                "Inventory identities must be unique within each reviewed list."));

        FormReference actor = new(
            request.Source.Plugin!.Value, request.Actor.FormId!.Value);
        FormReference player = new(
            new PluginName("Skyrim.esm"), new FormId(0x00000007));
        ValidateFaction(source.FactionRanks, PotentialFollowerFaction, 0,
            "PotentialFollowerFaction", diagnostics);
        ValidateFaction(source.FactionRanks, CurrentFollowerFaction, -1,
            "CurrentFollowerFaction", diagnostics);
        SkyrimNpcFinishCoreRelationshipSnapshot[] actorRelationships =
            source.Relationships
                .Where(relationship => relationship.Parent == actor &&
                                      relationship.Child == player)
                .ToArray();
        if (actorRelationships.Length > 1 ||
            actorRelationships.Any(relationship =>
                !string.Equals(relationship.Rank, "Ally", StringComparison.Ordinal) ||
                relationship.Flags != 0 || relationship.AssociationType is not null))
            diagnostics.Add(Error(
                "finish-core-relationship-conflict",
                "The source contains a duplicate or non-Ally actor-to-player relationship."));
        if (request.InventoryPolicy.Policy ==
                SkyrimNpcFinishCoreInventoryPolicy.ReplaceExactInventory &&
            source.Inventory.Length > 0 &&
            !InventoryMatches(source.Inventory, request.InventoryPolicy.ExpectedSourceItems))
            diagnostics.Add(Error(
                "finish-core-inventory-source-mismatch",
                "The complete reviewed source inventory does not match the request binding."));
        if (source.PackageLinks.Length > 0 &&
            !source.SemanticSurfaceValues.Contains("FINISH_CORE_COMPLETE",
                StringComparer.Ordinal))
            diagnostics.Add(Error(
                "finish-core-package-conflict",
                "The source NPC already has a package surface that is not an admitted Finish Core package."));

        foreach (string marker in source.SemanticSurfaceValues)
        {
            if (marker is "FINISH_CORE_COMPLETE")
                continue;
            if (marker.Contains("CONFLICT", StringComparison.OrdinalIgnoreCase) ||
                marker.StartsWith("PACKAGE ", StringComparison.OrdinalIgnoreCase) &&
                !source.SemanticSurfaceValues.Contains("FINISH_CORE_COMPLETE",
                    StringComparer.Ordinal))
                diagnostics.Add(Error(
                    "finish-core-source-conflict",
                    $"The source semantic surface is not safely replaceable: {marker}."));
        }
        return diagnostics.ToImmutable();
    }

    /// <summary>
    /// Builds the closed physical master plan from source admission evidence.
    /// Apply and Verify reuse this helper when their integration slices are
    /// enabled; it never reads or mutates workspace state.
    /// </summary>
    private static SkyrimNpcFinishCoreMasterPlan BuildMasterPlan(
        SkyrimNpcFinishCoreRequest request,
        SkyrimNpcFinishCoreSourceReadResult source) =>
        SkyrimNpcFinishCoreMasterPlanner.Plan(request,
            source.MasterOrder.Select(value => new PluginName(value)).ToImmutableArray(),
            source.VerifiedAdditionalMasters);

    private static void ValidateFaction(
        ImmutableArray<NpcFactionEntry> factions,
        FormReference expected,
        sbyte rank,
        string name,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        NpcFactionEntry[] rows = factions
            .Where(entry => entry.Faction == expected)
            .ToArray();
        if (rows.Length > 1 || rows.Any(entry => entry.Rank != rank))
            diagnostics.Add(Error(
                "finish-core-follower-rank-conflict",
                $"The source contains a duplicate or incorrectly ranked {name} row."));
    }

    private static bool InventoryMatches(
        ImmutableArray<SkyrimNpcFinishCoreInventoryEntry> actual,
        ImmutableArray<string> expected)
    {
        if (actual.Length != expected.Length)
            return false;
        return actual.Zip(expected)
            .All(pair => string.Equals(
                pair.First.Item.ToString(), pair.Second,
                StringComparison.OrdinalIgnoreCase));
    }

    private static bool AiPolicyMatches(
        SkyrimNpcFinishCoreRequest request,
        SkyrimNpcFinishCoreAiPolicy? actual)
    {
        if (request.AiPolicy is not { } expected)
            return true;
        if (actual is not { } current)
            return false;
        if (expected.Aggression != current.Aggression ||
            expected.Confidence != current.Confidence ||
            expected.Energy != current.Energy ||
            expected.Morality != current.Morality ||
            expected.Assistance != current.Assistance)
            return false;
        return request.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier ||
            expected.Mood is null ||
            expected.Mood == current.Mood;
    }

    private static SkyrimNpcFinishCoreProposal DeriveProposal(
        SkyrimNpcFinishCoreRequest request,
        Sha256Hash requestSha256,
        SkyrimNpcFinishCoreSourceReadResult source,
        SkyrimNpcFinishCoreMasterPlan? masterPlan = null,
        SkyrimNpcFinishCoreExternalHeadPartProposalAuthority? externalAuthority = null)
    {
        FormReference actor = new(
            request.Source.Plugin!.Value, request.Actor.FormId!.Value);
        FormReference player = new(
            new PluginName("Skyrim.esm"), new FormId(0x00000007));
        bool potentialFaction = HasFaction(source.FactionRanks,
            PotentialFollowerFaction, 0);
        bool currentFaction = HasFaction(source.FactionRanks,
            CurrentFollowerFaction, -1);
        bool relationship = source.Relationships.Any(row =>
            row.Parent == actor && row.Child == player &&
            string.Equals(row.Rank, "Ally", StringComparison.Ordinal) &&
            row.Flags == 0 && row.AssociationType is null);
        bool combatStyle = request.CombatPolicy is null && source.CombatStyleMatchesDefensiveContract;
        bool perks = request.PerkPolicy.IsDefault || request.PerkPolicy.SequenceEqual(source.Perks);
        bool outfitSatisfied = request.OutfitPolicy.Policy ==
            SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit &&
            request.OutfitPolicy.ExistingOutfit == source.DefaultOutfit && source.OutfitArmaturesToClone.IsEmpty;
        bool inventory = request.InventoryPolicy.Policy ==
            SkyrimNpcFinishCoreInventoryPolicy.PreserveInventory ||
            InventoryMatches(source.Inventory, request.InventoryPolicy.DesiredItems);
        bool package = source.SemanticSurfaceValues.Contains(
            "FINISH_CORE_COMPLETE", StringComparer.Ordinal);
        bool aiData = AiPolicyMatches(request, source.AiData);
        if ((source.AlreadySatisfied && request.CombatPolicy is null && perks && source.OutfitArmaturesToClone.IsEmpty) ||
            (potentialFaction && currentFaction && relationship && combatStyle &&
             outfitSatisfied && inventory && package && aiData && perks))
            return new SkyrimNpcFinishCoreProposal
            {
                Schema = ProposalSchemaFor(request),
                RequestSha256 = requestSha256,
                Request = request,
                Status = ExternalProposalStatus(externalAuthority) ??
                    SkyrimNpcFinishCoreStatus.NoChanges,
                NextFormId = source.NextFormId,
                SourceTes4Flags = source.Tes4Flags,
                AppendedMasters = masterPlan is null
                    ? ImmutableArray<string>.Empty
                    : masterPlan.AppendedMasters.Select(value => value.Value)
                        .ToImmutableArray(),
                MasterOrder = masterPlan is null
                    ? source.MasterOrder
                    : masterPlan.MasterOrder.Select(value => value.Value)
                        .ToImmutableArray(),
                RuntimeAuthority = false,
                ExternalHeadParts = externalAuthority
            };

        uint maximum = request.Source.Plugin!.Value.Value.EndsWith(
            ".esl", StringComparison.OrdinalIgnoreCase)
            ? 0xFFFu
            : 0xFFFFFFu;
        HashSet<uint> occupied = source.OccupiedIds
            .Select(ParseOccupiedId)
            .Where(value => value is not null)
            .Select(value => value!.Value)
            .ToHashSet();
        uint next = source.NextFormId.Value;
        FormId? rela = relationship ? null : Allocate(ref next, occupied, maximum);
        bool reuseLocalCombatStyle = request.CombatPolicy is not null &&
            source.CombatStyle is { } existingCombatStyle &&
            existingCombatStyle.Plugin == request.Source.Plugin;
        FormId? csty = combatStyle || reuseLocalCombatStyle
            ? null
            : Allocate(ref next, occupied, maximum);
        FormId? otft = request.OutfitPolicy.Policy ==
            SkyrimNpcFinishCoreOutfitPolicy.PrivateOutfit || !source.OutfitArmaturesToClone.IsEmpty
            ? Allocate(ref next, occupied, maximum)
            : null;
        FormId? pack = package ? null : Allocate(ref next, occupied, maximum);

        var newRecords = ImmutableArray.CreateBuilder<string>();
        if (rela is { } relationshipId)
            newRecords.Add($"RELA {relationshipId}");
        if (csty is { } combatStyleId)
            newRecords.Add($"CSTY {combatStyleId}");
        if (otft is { } privateOutfit)
            newRecords.Add($"OTFT {privateOutfit}");
        if (pack is { } packageId)
            newRecords.Add($"PACK {packageId}");
        foreach (FormReference armature in source.OutfitArmaturesToClone)
            newRecords.Add($"ARMA {Allocate(ref next, occupied, maximum)}");
        foreach (FormReference armor in source.OutfitArmorsToClone)
            newRecords.Add($"ARMO {Allocate(ref next, occupied, maximum)}");

        var changes = ImmutableArray.CreateBuilder<string>();
        const uint acbsMask = 0x00000820u;
        uint acbsBefore = source.TargetConfigurationFlags;
        if ((acbsBefore & acbsMask) != acbsMask)
        {
            uint acbsAfter = acbsBefore | acbsMask;
            changes.Add(
                $"NPC_ {request.Actor.FormId!.Value}: ACBS before=0x{acbsBefore:X8}; " +
                $"mask=0x{acbsMask:X8}; added=0x{acbsMask:X8}; " +
                $"after=0x{acbsAfter:X8}");
        }
        if (!potentialFaction)
            changes.Add($"NPC_ {request.Actor.FormId!.Value}: append PotentialFollowerFaction {PotentialFollowerFaction} rank 0");
        if (!currentFaction)
            changes.Add($"NPC_ {request.Actor.FormId!.Value}: append CurrentFollowerFaction {CurrentFollowerFaction} rank -1");
        if (!relationship)
            changes.Add($"NPC_ {request.Actor.FormId!.Value}: add ZNAM Ally relationship to player base");
        if (request.AiPolicy is { } && !AiPolicyMatches(request, source.AiData))
        {
            string mood = request.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier
                ? string.Empty
                : ", and mood";
            changes.Add(
                $"NPC_ {request.Actor.FormId!.Value}: set exact AIDT aggression, " +
                $"confidence, energy, morality, assistance{mood}");
        }
        if (csty is { } newStyle)
            changes.Add(request.CombatPolicy is { } combat
                ? $"CSTY {newStyle}: deep clone {source.CombatStyle}; profile={combat.Profile?.ToString() ?? "preserve"}; repoint NPC ZNAM"
                : $"CSTY {newStyle}: defensive-only multipliers and zero attack chances");
        else if (reuseLocalCombatStyle)
            changes.Add($"CSTY {source.CombatStyle!.Value.FormId}: apply reviewed profile={request.CombatPolicy!.Profile?.ToString() ?? "preserve"} in place");
        if (!perks)
            changes.Add($"NPC_ {request.Actor.FormId!.Value}: replace PRKR with exact reviewed perk identities and ranks");
        if (otft is { } outfit)
            changes.Add($"OTFT {outfit}: exact ordered reviewed armor list");
        foreach (FormReference armature in source.OutfitArmaturesToClone)
            changes.Add($"ARMA {armature}: output-owned clone appends actor race; clone owning ARMO and retarget private OTFT");
        if (request.InventoryPolicy.Policy ==
            SkyrimNpcFinishCoreInventoryPolicy.ReplaceExactInventory)
        {
            if (!InventoryMatches(source.Inventory, request.InventoryPolicy.DesiredItems))
            changes.Add($"NPC_ {request.Actor.FormId!.Value}: replace CNTO with exact reviewed inventory");
        }
        if (pack is { } newPackage)
            changes.Add($"PACK {newPackage}: clone sandbox template with NearEditorLocation radius 512 and one faction condition");

        ImmutableArray<string> appendedMasters = masterPlan is null
            ? source.MasterOrder
                .Concat(RequiredMasters(request))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Skip(source.MasterOrder.Length)
                .ToImmutableArray()
            : masterPlan.AppendedMasters
                .Select(value => value.Value)
                .ToImmutableArray();
        ImmutableArray<string> masterOrder = masterPlan is null
            ? source.MasterOrder.Concat(appendedMasters).ToImmutableArray()
            : masterPlan.MasterOrder.Select(value => value.Value)
                .ToImmutableArray();
        ImmutableArray<string> forbidden =
            ForbiddenSignatures
                .Select(signature => $"{signature}=0")
                .ToImmutableArray();
        return new SkyrimNpcFinishCoreProposal
        {
            Schema = ProposalSchemaFor(request),
            RequestSha256 = requestSha256,
            Request = request,
            Status = ExternalProposalStatus(externalAuthority) ??
                SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite,
            ExistingRecordChanges = changes.ToImmutable(),
            NewRecords = newRecords.ToImmutable(),
            AppendedMasters = appendedMasters,
            ForbiddenRecordCounts = forbidden,
            NextFormId = new FormId(next),
            SourceTes4Flags = source.Tes4Flags,
            MasterOrder = masterOrder,
            PackageFiles = externalAuthority is null
                ? ImmutableArray.Create(
                    "Data/" + request.Output.PluginFileName,
                    "NPCManager/Evidence/finish-core-request.json",
                    "NPCManager/Evidence/finish-core-proposal.json",
                    "NPCManager/Evidence/finish-core-manifest.json")
                : ImmutableArray.Create(
                    "Data/" + request.Output.PluginFileName,
                    "NPCManager/Evidence/finish-core-request.json",
                    "NPCManager/Evidence/finish-core-proposal.json",
                    "NPCManager/Evidence/finish-core-manifest.json",
                    SkyrimNpcFinishCoreDocumentCodec.CanonicalSelectedManifestPath),
            RuntimeAuthority = false,
            ExternalHeadParts = externalAuthority
        };
    }

    private static string ProposalSchemaFor(SkyrimNpcFinishCoreRequest request) =>
        request.Schema == SkyrimNpcFinishCoreRequest.ExternalSchemaIdentifier
            ? SkyrimNpcFinishCoreProposal.ExternalSchemaIdentifier
            : request.Schema == SkyrimNpcFinishCoreRequest.PolicySchemaIdentifier
            ? SkyrimNpcFinishCoreProposal.PolicySchemaIdentifier
            : request.Schema == SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier
            ? SkyrimNpcFinishCoreProposal.LegacySchemaIdentifier
            : SkyrimNpcFinishCoreProposal.SchemaIdentifier;

    private static SkyrimNpcFinishCoreStatus? ExternalProposalStatus(
        SkyrimNpcFinishCoreExternalHeadPartProposalAuthority? authority) =>
        authority is null
            ? null
            : authority.Verification.CurrentInstallDependencyState ==
                ExternalInstallDependencyState.Verified &&
              authority.Verification.InstallReady &&
              authority.Verification.InstallDependencyAuthority
                ? SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite
                : authority.Verification.CurrentInstallDependencyState ==
                    ExternalInstallDependencyState.DeclaredUnverified
                    ? SkyrimNpcFinishCoreStatus.StaticPassInstallDependencyRequired
                    : SkyrimNpcFinishCoreStatus.Refused;

    private async ValueTask<SkyrimNpcFinishCoreProposalResult>
        AnalyzeExternalAsync(
            SkyrimNpcFinishCoreRequest request,
            Sha256Hash requestSha256,
            WorkspacePath proposalPath,
            ExternalHeadPartInstallVerificationContext? installContext,
            bool requireCurrentAuthority,
            CancellationToken cancellationToken)
    {
        ImmutableArray<Diagnostic> pathDiagnostics = ValidateProposalPath(
            proposalPath,
            projectRoot);
        if (HasErrors(pathDiagnostics))
            return new(false, null, null, null, pathDiagnostics);
        if (File.Exists(proposalPath.Value) || Directory.Exists(proposalPath.Value))
            return Refused("finish-core-proposal-exists",
                "Analyze refuses to overwrite an existing proposal path.");

        if (request.Authorities.ExternalHeadParts is null)
            return Refused(
                "external-headpart-authority-missing",
                "An external Finish Core request must carry distinct external head-part authority.");

        SkyrimNpcFinishCoreSourceReadResult source = await inspectSource(
            request,
            cancellationToken);
        if (!source.Admitted)
            return new(false, null, null, null, source.Diagnostics);

        ImmutableArray<Diagnostic> validation = ValidateSourceAndRequest(
            request,
            source);
        if (HasErrors(validation))
            return new(false, null, null, null, validation);

        SkyrimNpcFinishCoreMasterPlan masterPlan = BuildMasterPlan(request, source);
        if (!masterPlan.Admitted)
            return new(false, null, null, null, masterPlan.Diagnostics);

        var installDiagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ExternalFinishCoreState? state = await VerifyExternalInstallAsync(
            request,
            installContext,
            requireCurrentAuthority,
            installDiagnostics,
            cancellationToken);
        if (state is null || HasErrors(installDiagnostics))
            return new(false, null, null, null, installDiagnostics.ToImmutable());

        SkyrimNpcFinishCoreProposal proposal;
        try
        {
            proposal = DeriveProposal(
                request,
                requestSha256,
                source,
                masterPlan,
                state.ProposalAuthority);
        }
        catch (SkyrimNpcFinishCoreIdOverflowException)
        {
            return Refused(
                "finish-core-id-overflow",
                "The source FormID space cannot allocate the required Finish Core records.");
        }
        return await WriteProposalAsync(
            proposal,
            proposalPath,
            projectRoot,
            cancellationToken);
    }

    private async ValueTask<ExternalFinishCoreState?> VerifyExternalInstallAsync(
        SkyrimNpcFinishCoreRequest request,
        ExternalHeadPartInstallVerificationContext? installContext,
        bool requireCurrentAuthority,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken,
        WorkspacePath? packageRootOverride = null,
        WorkspacePath? pluginPathOverride = null,
        PluginName? pluginOverride = null,
        Sha256Hash? pluginShaOverride = null,
        AssetPath? selectedManifestPathOverride = null,
        Sha256Hash? selectedManifestShaOverride = null)
    {
        SkyrimNpcFinishCoreExternalHeadPartAuthority? authority =
            request.Authorities.ExternalHeadParts;
        if (authority is null)
        {
            diagnostics.Add(Error(
                "external-headpart-authority-missing",
                "External Finish Core requires selected schema-3 dependency authority."));
            return null;
        }
        if (externalInstallVerifier is null)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "External Finish Core install verification is unavailable; no provider bytes may be trusted."));
            return null;
        }
        if ((packageRootOverride ?? request.Source.PackageRoot) is not { } packageRoot ||
            (pluginPathOverride ?? request.Source.PluginPath) is not { } pluginPath ||
            (pluginOverride ?? request.Source.Plugin) is not { } plugin ||
            (pluginShaOverride ?? request.Source.PluginSha256) is not { } pluginSha)
        {
            diagnostics.Add(Error(
                "external-headpart-source-binding",
                "External Finish Core requires a package root, source plugin, output identity, and source hash."));
            return null;
        }

        WorkspacePath selectedManifestPath;
        try
        {
            string relative = (selectedManifestPathOverride ??
                authority.SelectedManifestPath).Value.Replace(
                '/', Path.DirectorySeparatorChar);
            selectedManifestPath = new(Path.GetFullPath(
                Path.Combine(packageRoot.Value, relative)));
        }
        catch (Exception exception) when (
            exception is ArgumentException or IOException)
        {
            diagnostics.Add(Error(
                "external-headpart-selected-manifest-path",
                exception.Message));
            return null;
        }
        if (!selectedManifestPath.IsUnder(packageRoot) ||
            HasReparsePoint(selectedManifestPath.Value))
        {
            diagnostics.Add(Error(
                "external-headpart-selected-manifest-path",
                "The selected schema-3 dependency manifest must remain an ordinary package-relative file."));
            return null;
        }

        ExternalHeadPartInstallVerificationResult result;
        try
        {
            result = await externalInstallVerifier.VerifyAsync(
                new ExternalHeadPartInstallVerificationRequest(
                    packageRoot,
                    pluginPath,
                    plugin,
                    pluginSha,
                    selectedManifestPath,
                    selectedManifestShaOverride ?? authority.SelectedManifestSha256,
                    installContext,
                    requireCurrentAuthority,
                    request.Actor.FormId),
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or
                InvalidDataException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                exception.Message));
            return null;
        }

        diagnostics.AddRange(result.Diagnostics);
        ExternalHeadPartInstallVerificationArtifact artifact = result.Artifact;
        ImmutableArray<Sha256Hash> expectedIds = authority.Bindings
            .Select(binding => binding.DescriptorId)
            .ToImmutableArray();
        if (!result.DescriptorClosureValid || !artifact.PackageIntegrity ||
            !artifact.DescriptorClosureValid ||
            !artifact.DescriptorIds.SequenceEqual(expectedIds))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                "The selected schema-3 dependency closure is not exactly the request-bound descriptor set."));
            return null;
        }

        if (installContext is null)
        {
            if (requireCurrentAuthority ||
                artifact.CurrentInstallDependencyState !=
                    ExternalInstallDependencyState.DeclaredUnverified ||
                artifact.InstallReady || artifact.InstallDependencyAuthority ||
                artifact.VerifiedInstallSnapshot is not null)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.InstallContextAbsent,
                    "Context-free Finish Core operations cannot claim current external install authority."));
                return null;
            }
        }
        else if (requireCurrentAuthority &&
                 (artifact.CurrentInstallDependencyState !=
                      ExternalInstallDependencyState.Verified ||
                  !artifact.InstallReady || !artifact.InstallDependencyAuthority ||
                  artifact.VerifiedInstallSnapshot is null))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "Strict Finish Core operations require a fresh verified external install snapshot."));
            return null;
        }

        ExternalHeadPartInstallContextFingerprint? fingerprint =
            artifact.VerifiedInstallSnapshot?.ContextFingerprint;
        if (installContext is not null && fingerprint is null)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PrecheckUnavailable,
                "Strict external install verification did not return a normalized context fingerprint."));
            return null;
        }

        RaceMenuSelectedDependencyManifestReadResult selected =
            await new RaceMenuSelectedDependencyManifestReader().ReadAsync(
                selectedManifestPath,
                selectedManifestShaOverride ?? authority.SelectedManifestSha256,
                packageRoot,
                cancellationToken);
        diagnostics.AddRange(selected.Diagnostics);
        if (selected.Artifact is null ||
            selected.Artifact.SchemaVersion != 3 ||
            selected.Artifact.ExternalInstallDependencies.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                "The canonical selected dependency manifest did not reopen as schema 3 with external groups."));
            return null;
        }
        var selectedGroups = selected.Artifact.ExternalInstallDependencies;
        if (!selectedGroups.Select(group => group.Descriptor.DescriptorId)
                .SequenceEqual(expectedIds) ||
            !selectedGroups.Select(group => group.Attestation.AttestationSha256)
                .SequenceEqual(authority.Bindings.Select(binding =>
                    binding.FaceGeomExclusionAttestationSha256)))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                "The selected dependency manifest groups do not match Finish Core request authority bindings."));
            return null;
        }

        return new ExternalFinishCoreState(
            result,
            new SkyrimNpcFinishCoreExternalHeadPartProposalAuthority(
                authority,
                artifact with
                {
                    HistoricalSnapshotValid = null,
                    VerifiedInstallSnapshot = null
                },
                fingerprint),
            selectedGroups.Select(group => group.Descriptor).ToImmutableArray(),
            selectedGroups.Select(group => group.Attestation).ToImmutableArray(),
            selected.Artifact);
    }

    private static IEnumerable<string> RequiredMasters(SkyrimNpcFinishCoreRequest request) =>
        SkyrimNpcFinishCoreMasterPlanner.RequiredMasterNames(request);

    private static bool HasFaction(
        ImmutableArray<NpcFactionEntry> factions,
        FormReference faction,
        sbyte rank) => factions.Count(entry =>
        entry.Faction == faction && entry.Rank == rank) == 1;

    private static FormId Allocate(
        ref uint next,
        HashSet<uint> occupied,
        uint maximum)
    {
        while (next <= maximum && occupied.Contains(next))
            next++;
        if (next > maximum)
            throw new SkyrimNpcFinishCoreIdOverflowException();
        uint allocated = next++;
        occupied.Add(allocated);
        return new FormId(allocated);
    }

    private static uint? ParseOccupiedId(string value)
    {
        int marker = value.LastIndexOf("0x", StringComparison.OrdinalIgnoreCase);
        if (marker < 0 || !uint.TryParse(
                value[(marker + 2)..],
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint parsed))
            return null;
        return parsed;
    }

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed class SkyrimNpcFinishCoreIdOverflowException : Exception;
}
