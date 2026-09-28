using System.Collections.Immutable;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Archives;
using Noggog;
using NpcManager.Application;
using NpcManager.Assets;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Discovers the portable closure for one selected external head-part graph.
/// The service consumes the already bounded face-record route; in particular,
/// it never walks HNAM itself. Provider bytes are reopened for evidence only
/// and are never copied into a Manager-owned output.
/// </summary>
public sealed class ExternalHeadPartDependencyDiscoveryService(
    ISkyrimAssetAuthorityPlanner assetAuthorityPlanner,
    ISkyrimAssetContentResolver assetContentResolver,
    IExternalHeadPartPhysicsBindingResolver physicsBindingResolver) :
    IExternalHeadPartDependencyDiscovery
{
    private const int MaximumGraphMembers = 512;
    private const long MaximumProviderBytes = 512L * 1024 * 1024;
    private const long MaximumAssetBytes = 256L * 1024 * 1024;
    private const string GraphHashDomain =
        "Actorwright.ExternalHeadPartGraph.v1\0";

    private readonly ExternalHeadPartProviderNifReader _nifReader = new();

    public async ValueTask<ExternalHeadPartDependencyDiscoveryResult>
        DiscoverAsync(
            ExternalHeadPartDependencyDiscoveryRequest request,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        try
        {
            ValidateRequest(request, diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            SkyrimFaceHeadPartGraphRoute? root = ResolveRoot(
                request.RecordRoute, diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);
            if (root is null)
                return NotApplicable(diagnostics);

            ImmutableArray<SkyrimFaceHeadPartGraphRoute> graphMembers =
                ResolveClosedGraph(request.RecordRoute, root, diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            // The root's route-bound identity is only a provisional binding;
            // ReopenProviderAsync independently reopens the plugin before it
            // becomes descriptor authority.
            var provisionalProvider = new ExternalHeadPartProviderIdentity(
                root.WinningPlugin,
                root.WinningPluginSha256,
                root.WinningPluginByteLength,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired);
            ImmutableArray<ExternalHeadPartRecordDependency> members =
                BuildMembers(
                    request, root, graphMembers, provisionalProvider, diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            ExternalHeadPartProviderIdentity? provider =
                await ReopenProviderAsync(
                    request, root, members, diagnostics,
                    cancellationToken).ConfigureAwait(false);
            if (provider is null || HasErrors(diagnostics))
                return Refused(diagnostics);

            AssetClosure initialAssets =
                EnumerateInitialAssets(
                    request.RecordRoute, members, provider, diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            ImmutableArray<ExternalHeadPartRecordDependency> providerModelMembers =
                members.Where(item =>
                    ExternalHeadPartMemberAuthority.Classify(item, provider) ==
                        ExternalHeadPartMemberAuthorityKind.PrimaryProvider &&
                    item.ModelNif is not null)
                    .ToImmutableArray();
            if (providerModelMembers.IsEmpty)
            {
                if (!initialAssets.Paths.IsEmpty)
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.AssetMissing,
                        "A model-less external graph declared TRI or texture assets."));
                    return Refused(diagnostics);
                }
                return NotApplicable(diagnostics);
            }

            AssetResolution? resolved = await ResolveAssetsAsync(
                request.DataRoot,
                initialAssets.Paths,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (resolved is null || HasErrors(diagnostics))
                return Refused(diagnostics);

            ImmutableArray<ModelObservation> observations = ReadModels(
                providerModelMembers, resolved.ByAssetPath, diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            ImmutableArray<DeclaredAsset> vanillaDependencies =
                ReadVanillaDependencies(
                    members,
                    resolved.ByAssetPath,
                    initialAssets.Owners,
                    diagnostics);
            AssetOwner primaryOwner = new(
                provider.Plugin, provider.PluginSha256, provider.PluginByteLength);
            ImmutableArray<DeclaredAsset> providerDependencies =
                observations.SelectMany(item => item.Read.Dependencies)
                    .Select(path => new DeclaredAsset(path, primaryOwner))
                    .ToImmutableArray();
            AssetClosure discoveredAssets = MergeAssetPaths(
                initialAssets,
                providerDependencies.Concat(vanillaDependencies),
                diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);
            if (discoveredAssets.Paths.Length != initialAssets.Paths.Length)
            {
                resolved = await ResolveAssetsAsync(
                    request.DataRoot,
                    discoveredAssets.Paths,
                    diagnostics,
                    cancellationToken).ConfigureAwait(false);
                if (resolved is null || HasErrors(diagnostics))
                    return Refused(diagnostics);
                observations = ReadModels(
                    providerModelMembers, resolved.ByAssetPath, diagnostics);
                if (HasErrors(diagnostics))
                    return Refused(diagnostics);
                _ = ReadVanillaDependencies(
                    members,
                    resolved.ByAssetPath,
                    discoveredAssets.Owners,
                    diagnostics);
                if (HasErrors(diagnostics))
                    return Refused(diagnostics);
            }

            bool hasDirectLocator = observations.Any(item =>
                !item.Read.PhysicsObjectLocators.IsDefaultOrEmpty);
            var physicsDisposition = request.PhysicsBinding ?? request.ExpectedDescriptor?.PhysicsBinding;
            FallbackMapState fallbackMap = hasDirectLocator || physicsDisposition is not null ? FallbackMapState.Absent : ProbeFallbackMap(
                request.DataRoot, diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);
            if (!hasDirectLocator && fallbackMap == FallbackMapState.Absent && physicsDisposition is null)
                return NotApplicable(diagnostics);

            ExternalHeadPartPhysicsBindingResult physics =
                await physicsBindingResolver.ResolveAsync(
                    new ExternalHeadPartPhysicsBindingRequest(
                        request.DataRoot,
                        request.PluginOrder,
                        provider,
                        providerModelMembers,
                        physicsDisposition,
                        members),
                    cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, physics.Diagnostics);
            if (!physics.Accepted || physics.Binding is null || HasErrors(diagnostics))
            {
                if (physics.Diagnostics.Any(item =>
                        item.Code.Contains("asset-missing",
                            StringComparison.OrdinalIgnoreCase)))
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.AssetMissing,
                        "A physics XML, collider, or mapping asset was missing during rediscovery."));
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.PhysicsMissing,
                    "The admitted external graph did not produce a complete physics binding."));
                return Refused(diagnostics);
            }

            if (hasDirectLocator && !physics.Binding.Shapes.Any(shape =>
                    shape.Origin == ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot))
            {
                _ = ProbeFallbackMap(request.DataRoot, diagnostics);
                if (HasErrors(diagnostics)) return Refused(diagnostics);
            }

            ImmutableArray<ExternalHeadPartAssetDependency> assets =
                BuildAssetDependencies(
                    request.DataRoot,
                    provider,
                    resolved,
                    discoveredAssets.Owners,
                    physics,
                    diagnostics,
                    cancellationToken);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            // Reopen the admitted provider after physics and asset evidence
            // have been resolved.  Descriptor identity is allowed to observe
            // only this final, independently verified provider authority.
            ExternalHeadPartProviderIdentity? finalProvider =
                await ReopenProviderAsync(
                    request, root, members, diagnostics,
                    cancellationToken).ConfigureAwait(false);
            if (finalProvider is null || HasErrors(diagnostics) ||
                finalProvider != provider)
            {
                if (!HasErrors(diagnostics))
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        "The admitted provider changed before descriptor identity was computed."));
                return Refused(diagnostics);
            }
            provider = finalProvider;

            ExternalHeadPartDependencyDescriptor descriptor =
                new(
                    ExternalHeadPartSchemaIdentifiers.Descriptor,
                    default,
                    ExternalHeadPartDependencyDisposition.RecordOnlyExternal,
                    root.OriginForm,
                    root.WinningForm,
                    root.EffectiveType,
                    ComputeGraphHash(members),
                    provider,
                    members,
                    physics.Binding,
                    assets,
                    [],
                    physicsDisposition);
            descriptor = descriptor with
            {
                DescriptorId = ExternalHeadPartDependencyDescriptorCodec
                    .ComputeDescriptorId(descriptor)
            };

            if (!MatchesExpectedDescriptor(
                    descriptor, request.ExpectedDescriptor, diagnostics))
                return Refused(diagnostics);

            // Serialize once at the admission boundary. This applies the
            // Task 1 closed descriptor invariants to every discovered value.
            _ = ExternalHeadPartDependencyDescriptorCodec
                .SerializeDescriptor(descriptor);
            diagnostics.Add(new Diagnostic(
                "external-headpart-dependency-discovered",
                DiagnosticSeverity.Info,
                $"Discovered {descriptor.Members.Length} external headpart record member(s) and {descriptor.Assets.Length} reopened asset authority(ies)."));
            return new ExternalHeadPartDependencyDiscoveryResult(
                ExternalHeadPartDependencyDiscoveryStatus.Accepted,
                descriptor,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error(
                "external-headpart-discovery-refused",
                $"External headpart dependency discovery was refused: {exception.Message}"));
            return Refused(diagnostics);
        }
    }

    private static void ValidateRequest(
        ExternalHeadPartDependencyDiscoveryRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!IsKLocal(request.DataRoot))
            diagnostics.Add(Error(
                "external-headpart-discovery-workspace",
                "External headpart discovery requires a K-local Data root."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.PluginOrder.Length)
            diagnostics.Add(Error(
                "external-headpart-discovery-plugin-order",
                "PluginOrder must be a non-empty, distinct ordered list."));
        if (!Enum.IsDefined(request.Sex))
            diagnostics.Add(Error(
                "external-headpart-discovery-sex",
                "The requested sex is not an admitted Skyrim sex value."));
        if (request.RecordRoute.HeadPartGraph.IsDefaultOrEmpty ||
            request.RecordRoute.HeadPartGraph.Length > MaximumGraphMembers)
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                "The bounded face-record route does not contain an admitted graph."));
        if (request.RecordRoute.RootHeadParts.IsDefaultOrEmpty)
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RootUnsupported,
                "The bounded face-record route does not identify a selected root."));
    }

    private static SkyrimFaceHeadPartGraphRoute? ResolveRoot(
        SkyrimFaceRecordRoute route,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var graphByOrigin = route.HeadPartGraph
            .GroupBy(item => item.OriginForm)
            .ToArray();
        if (graphByOrigin.Any(group => group.Count() != 1))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                "The face-record route contains duplicate HDPT graph identities."));
            return null!;
        }

        var selected = route.RootHeadParts
            .Select(reference => route.HeadPartGraph.FirstOrDefault(item =>
                item.OriginForm == reference))
            .Where(item => item is not null)
            .Cast<SkyrimFaceHeadPartGraphRoute>()
            .Where(item => item.IsSelected)
            .ToArray();
        if (selected.Length == route.RootHeadParts.Length &&
            selected.Select(item => item.OriginForm).Distinct().Count() ==
                selected.Length &&
            selected.All(item => item.Parent is null && item.Depth == 0) &&
            selected.All(item =>
                item.DeclaredType != NpcHeadPartType.Hair &&
                item.EffectiveType != NpcHeadPartType.Hair &&
                item.DeclaredType != NpcHeadPartType.FacialHair &&
                item.EffectiveType != NpcHeadPartType.FacialHair))
            return null;
        SkyrimFaceHeadPartGraphRoute[] hair = selected
            .Where(item => item.EffectiveType == NpcHeadPartType.Hair ||
                item.DeclaredType == NpcHeadPartType.Hair)
            .ToArray();
        if (hair.Length != 1)
        {
            if (selected.Any(item => item.EffectiveType ==
                        NpcHeadPartType.FacialHair ||
                    item.DeclaredType == NpcHeadPartType.FacialHair))
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RootWigOnly,
                    "The selected headpart route identifies a wig/facial-hair selection, not a Hair HDPT root."));
            else
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RootUnsupported,
                    "The selected face-record route must contain exactly one Hair HDPT root."));
            return null!;
        }

        SkyrimFaceHeadPartGraphRoute root = hair[0];
        if (root.Parent is not null || root.Depth != 0)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                "The selected Hair root is not the depth-zero graph member."));
        }
        return root;
    }

    private static ImmutableArray<SkyrimFaceHeadPartGraphRoute> ResolveClosedGraph(
        SkyrimFaceRecordRoute route,
        SkyrimFaceHeadPartGraphRoute root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var byOrigin = route.HeadPartGraph.ToDictionary(item => item.OriginForm);
        // The route resolver has already performed the bounded HNAM walk. The
        // discovery layer scopes that projection through its supplied parent
        // evidence and only checks its selected closure; it never traverses
        // Bethesda records or discovers additional HNAM members.
        if (!byOrigin.ContainsKey(root.OriginForm))
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                $"The selected root '{root.OriginForm}' is absent from the bounded route graph."));

        // This is one in-memory projection pass over Task 2's already
        // resolved Parent/HnamEdges route. It does not reread Bethesda
        // records or walk HNAM chains a second time.
        var closure = new HashSet<FormReference> { root.OriginForm };
        SkyrimFaceHeadPartGraphRoute[] ordered = route.HeadPartGraph
            .OrderBy(item => item.RouteOrder)
            .ThenBy(item => item.OriginForm.ToString(), StringComparer.Ordinal)
            .ToArray();
        foreach (SkyrimFaceHeadPartGraphRoute candidate in ordered)
        {
            if (candidate.OriginForm != root.OriginForm &&
                candidate.Parent is FormReference parent &&
                closure.Contains(parent))
                closure.Add(candidate.OriginForm);
        }

        SkyrimFaceHeadPartGraphRoute[] members = ordered
            .Where(item => closure.Contains(item.OriginForm))
            .ToArray();
        foreach (SkyrimFaceHeadPartGraphRoute member in members)
        {
            string memberLabel = GraphMemberLabel(member, root);
            if (member.EffectiveType != NpcHeadPartType.Hair)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RootUnsupported,
                    $"{memberLabel} '{member.OriginForm}' is not an effective Hair headpart."));
            if (member.HnamEdges.Distinct().Count() != member.HnamEdges.Length)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    $"{memberLabel} '{member.OriginForm}' repeats an edge."));
            foreach (FormReference child in member.HnamEdges)
            {
                if (!byOrigin.TryGetValue(child,
                        out SkyrimFaceHeadPartGraphRoute? childMember) ||
                    childMember is null ||
                    !closure.Contains(child))
                {
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                        $"HNAM member '{child}' is absent from the bounded route graph."));
                    continue;
                }
                if (childMember.RouteOrder <= member.RouteOrder)
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.GraphCycle,
                        $"HNAM graph edge '{member.OriginForm}' -> '{child}' is not forward in the bounded route order."));
                else if (childMember.Parent != member.OriginForm)
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        $"HNAM edge/parent evidence disagrees for '{child}'."));
            }
            if (member.Parent is FormReference parent &&
                (!byOrigin.TryGetValue(parent,
                        out SkyrimFaceHeadPartGraphRoute? parentMember) ||
                 parentMember is null ||
                 !closure.Contains(parent) ||
                 !parentMember.HnamEdges.Contains(member.OriginForm)))
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    $"HNAM parent/edge evidence disagrees for '{member.OriginForm}'."));
            if (member.Parent is FormReference parentReference &&
                byOrigin.TryGetValue(parentReference,
                    out SkyrimFaceHeadPartGraphRoute? parentRoute) &&
                parentRoute is not null &&
                parentRoute.RouteOrder >= member.RouteOrder)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.GraphCycle,
                    $"HNAM parent '{parentReference}' is not before '{member.OriginForm}' in the bounded route order."));
            if (member.Parent is null && member.OriginForm != root.OriginForm)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                    $"Disconnected HNAM member '{member.OriginForm}' has no parent."));
            if (member.Parent is FormReference depthParent &&
                byOrigin.TryGetValue(depthParent,
                    out SkyrimFaceHeadPartGraphRoute? depthParentRoute) &&
                depthParentRoute is not null &&
                member.Depth != depthParentRoute.Depth + 1)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    $"{memberLabel} '{member.OriginForm}' has inconsistent depth evidence."));
        }

        if (members.Select(item => item.RouteOrder).Distinct().Count() !=
            members.Length)
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                "The selected HNAM closure contains duplicate route-order values."));

        return members.ToImmutableArray();
    }

    private static async ValueTask<ExternalHeadPartProviderIdentity?> ReopenProviderAsync(
        ExternalHeadPartDependencyDiscoveryRequest request,
        SkyrimFaceHeadPartGraphRoute root,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        PluginName providerPlugin = root.WinningPlugin;
        if (root.WinningForm != root.OriginForm ||
            !SamePlugin(root.OriginForm.Plugin, providerPlugin) ||
            !SamePlugin(root.RequiredOutputMaster, providerPlugin))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.WinningProviderOverrideUnsupported,
                $"The selected root '{root.OriginForm}' is won by an unsupported cross-plugin override."));
            return null;
        }
        if (!request.PluginOrder.Any(item => string.Equals(
                item.Value, providerPlugin.Value,
                StringComparison.OrdinalIgnoreCase)))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.ProviderMissing,
                $"The external provider '{providerPlugin}' is absent from PluginOrder."));
            return null;
        }

        SkyrimFaceRecordProvider[] admittedRows = request.RecordRoute.HeadParts
            .Where(item => SamePlugin(item.Provider.Plugin, providerPlugin))
            .Select(item => item.Provider)
            .ToArray();
        if (admittedRows.Length == 0 &&
            SamePlugin(request.RecordRoute.Race.Provider.Plugin, providerPlugin))
            admittedRows = [request.RecordRoute.Race.Provider];
        if (admittedRows.Length > 1 &&
            admittedRows.Any(item => !SameProviderAuthority(
                item, admittedRows[0])))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                $"Route rows for provider '{providerPlugin}' carry conflicting path/hash authority."));
            return null;
        }
        SkyrimFaceRecordProvider? providerEvidence =
            admittedRows.FirstOrDefault();
        if (providerEvidence is null ||
            !string.Equals(providerEvidence.Plugin.Value,
                providerPlugin.Value, StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.ProviderMissing,
                $"The bounded route does not carry a path authority for provider '{providerPlugin}'."));
            return null;
        }

        foreach (ExternalHeadPartRecordDependency member in members)
        {
            string memberLabel = GraphMemberLabel(member, root.OriginForm);
            ExternalHeadPartMemberAuthorityKind authority =
                ExternalHeadPartMemberAuthority.Classify(
                    member,
                    new ExternalHeadPartProviderIdentity(
                        providerPlugin,
                        root.WinningPluginSha256,
                        root.WinningPluginByteLength,
                        ExternalHeadPartRedistributionMode.ExternalProviderRequired));
            if (authority == ExternalHeadPartMemberAuthorityKind.Unsupported)
            {
                diagnostics.Add(Error(
                    member.WinningForm != member.OriginForm
                        ? ExternalHeadPartDiagnosticCodes.WinningProviderOverrideUnsupported
                        : ExternalHeadPartDiagnosticCodes.GraphMixedProvider,
                    $"{memberLabel} '{member.OriginForm}' does not have an admitted provider or official vanilla authority."));
                continue;
            }
            if (authority != ExternalHeadPartMemberAuthorityKind.OfficialVanilla)
                continue;

            SkyrimFaceHeadPartRecordRoute? vanillaRoute = request.RecordRoute.HeadParts
                .FirstOrDefault(item => item.Reference == member.OriginForm);
            if (vanillaRoute is null ||
                !SamePlugin(vanillaRoute.Provider.Plugin, member.OriginForm.Plugin) ||
                vanillaRoute.Provider.Sha256 != member.WinningPluginSha256)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    $"Official vanilla member '{member.OriginForm}' lacks its exact self-owned plugin authority."));
                continue;
            }
            try
            {
                FileEvidence vanillaEvidence = await ReadFileAsync(
                    request.DataRoot,
                    vanillaRoute.Provider.Path,
                    MaximumProviderBytes,
                    cancellationToken).ConfigureAwait(false);
                if (vanillaEvidence.Hash != member.WinningPluginSha256 ||
                    vanillaEvidence.Length != member.WinningPluginByteLength ||
                    vanillaEvidence.Hash != vanillaRoute.Provider.Sha256)
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        $"Official vanilla member '{member.OriginForm}' changed while its authority was reopened."));
            }
            catch (IOException exception)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.ProviderMissing,
                    $"Official vanilla member '{member.OriginForm}' could not be reopened: {exception.Message}"));
            }
        }
        if (HasErrors(diagnostics))
            return null;

        try
        {
            FileEvidence evidence = await ReadFileAsync(
                request.DataRoot,
                providerEvidence.Path,
                MaximumProviderBytes,
                cancellationToken).ConfigureAwait(false);
            if (evidence.Hash != root.WinningPluginSha256 ||
                evidence.Length != root.WinningPluginByteLength ||
                evidence.Hash != providerEvidence.Sha256)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    $"Provider plugin '{providerPlugin}' changed while its record route was being reopened."));
                return null;
            }
            return new ExternalHeadPartProviderIdentity(
                providerPlugin,
                evidence.Hash,
                evidence.Length,
                ExternalHeadPartRedistributionMode.ExternalProviderRequired);
        }
        catch (FileNotFoundException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.ProviderMissing,
                $"Provider plugin '{providerPlugin}' could not be reopened."));
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.ProviderMissing,
                $"Provider plugin '{providerPlugin}' could not be reopened."));
            return null;
        }
        catch (IOException exception)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                $"Provider plugin '{providerPlugin}' could not be reopened: {exception.Message}"));
            return null;
        }
    }

    private static ImmutableArray<ExternalHeadPartRecordDependency> BuildMembers(
        ExternalHeadPartDependencyDiscoveryRequest request,
        SkyrimFaceHeadPartGraphRoute root,
        ImmutableArray<SkyrimFaceHeadPartGraphRoute> graphMembers,
        ExternalHeadPartProviderIdentity provider,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var members = ImmutableArray.CreateBuilder<ExternalHeadPartRecordDependency>(
            graphMembers.Length);
        foreach (SkyrimFaceHeadPartGraphRoute member in graphMembers)
        {
            string memberLabel = GraphMemberLabel(member, root);
            if (string.IsNullOrWhiteSpace(member.EditorId))
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                    $"{memberLabel} '{member.OriginForm}' has no stable EditorID."));
            if (member.AppliesToSex is NpcSex sex && sex != request.Sex)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordDrift,
                    $"{memberLabel} '{member.OriginForm}' does not apply to the requested sex."));
            ValidateValidRaceList(
                member,
                memberLabel,
                request.RecordRoute.Race.Reference,
                diagnostics);
            if (member.ModelNif is AssetPath model &&
                !IsCanonicalMesh(model, ".nif"))
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetMissing,
                    $"{memberLabel} '{member.OriginForm}' has an unsafe NIF model path '{model}'."));
            var roles = new HashSet<SkyrimHdptTriRole>();
            foreach (SkyrimHdptTriRoute tri in member.TriRoutes)
            {
                if (!roles.Add(tri.Role) || !IsCanonicalMesh(tri.Path, ".tri"))
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.AssetMissing,
                        $"{memberLabel} '{member.OriginForm}' has an invalid or duplicate TRI route."));
            }
            var dependency = new ExternalHeadPartRecordDependency(
                member.OriginForm,
                member.RequiredOutputMaster,
                member.WinningForm,
                member.WinningPlugin,
                member.WinningPluginSha256,
                member.WinningPluginByteLength,
                member.WinningRecordSha256,
                member.EditorId,
                member.DeclaredType,
                member.EffectiveType,
                member.ModelNif,
                member.TriRoutes,
                member.HnamEdges,
                member.Parent,
                member.Depth,
                member.RouteOrder,
                member.AppliesToSex,
                member.ValidRace);
            ExternalHeadPartMemberAuthorityKind authority =
                ExternalHeadPartMemberAuthority.Classify(dependency, provider);
            if (authority == ExternalHeadPartMemberAuthorityKind.Unsupported)
                diagnostics.Add(Error(
                    dependency.WinningForm != dependency.OriginForm ||
                    !SamePlugin(dependency.WinningForm.Plugin, dependency.WinningPlugin)
                        ? ExternalHeadPartDiagnosticCodes.WinningProviderOverrideUnsupported
                        : ExternalHeadPartDiagnosticCodes.GraphMixedProvider,
                    $"{memberLabel} '{member.OriginForm}' does not have an admitted provider authority."));
            SkyrimFaceHeadPartRecordRoute? legacy = request.RecordRoute.HeadParts
                .FirstOrDefault(item => item.Reference == member.OriginForm);
            if (authority == ExternalHeadPartMemberAuthorityKind.OfficialVanilla &&
                legacy is not null)
            {
                if (legacy.ModelNif != dependency.ModelNif ||
                    !legacy.TriRoutes.SequenceEqual(dependency.TriRoutes))
                    diagnostics.Add(Error(
                        ExternalHeadPartDiagnosticCodes.RecordDrift,
                        $"Graph and legacy route assets disagree for '{member.OriginForm}'."));
            }
            else if (authority == ExternalHeadPartMemberAuthorityKind.OfficialVanilla &&
                (dependency.ModelNif is not null || !dependency.TriRoutes.IsEmpty))
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                    $"Official vanilla member '{member.OriginForm}' has no matching legacy asset route."));
            members.Add(dependency);
        }
        if (members.Count == 0 || members[0].OriginForm != root.OriginForm)
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                "The descriptor graph root was not preserved as the first route member."));
        if (members.Count > 0 &&
            ExternalHeadPartMemberAuthority.Classify(members[0], provider) !=
                ExternalHeadPartMemberAuthorityKind.PrimaryProvider)
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.WinningProviderOverrideUnsupported,
                "The descriptor graph root must remain owned and won by the primary external provider."));
        if (members.Any(item =>
                ExternalHeadPartMemberAuthority.Classify(item, provider) ==
                    ExternalHeadPartMemberAuthorityKind.Unsupported))
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.GraphMixedProvider,
                "The descriptor graph contains a member outside the admitted provider."));
        return members.ToImmutable();
    }

    private static void ValidateValidRaceList(
        SkyrimFaceHeadPartGraphRoute member,
        string memberLabel,
        FormReference routedRace,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (member.ValidRace is not { } validRaceListReference)
            return;

        SkyrimFaceFormListRoute? projection = member.ValidRaceList;
        if (projection is null ||
            !SameReference(projection.Reference, validRaceListReference))
        {
            string projectionDescription = projection is null
                ? "no resolved FLST projection"
                : $"projection '{projection.Reference}' does not match the pointer";
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                $"{memberLabel} '{member.OriginForm}' ValidRaces list " +
                $"'{validRaceListReference}' has {projectionDescription} for " +
                $"routed race '{routedRace}'."));
            return;
        }

        if (projection.IsDeleted)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordUnresolved,
                $"{memberLabel} '{member.OriginForm}' ValidRaces list " +
                $"'{projection.Reference}' is deleted for routed race " +
                $"'{routedRace}'."));
            return;
        }

        if (!projection.Items.Any(item => SameReference(item, routedRace)))
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                $"{memberLabel} '{member.OriginForm}' ValidRaces list " +
                $"'{projection.Reference}' does not contain routed race " +
                $"'{routedRace}'."));
    }

    private static AssetClosure EnumerateInitialAssets(
        SkyrimFaceRecordRoute route,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        ExternalHeadPartProviderIdentity provider,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var paths = ImmutableArray.CreateBuilder<AssetPath>();
        var owners = new Dictionary<string, AssetOwner>(
            StringComparer.OrdinalIgnoreCase);
        var spellings = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (ExternalHeadPartRecordDependency member in members)
        {
            ExternalHeadPartMemberAuthorityKind authority =
                ExternalHeadPartMemberAuthority.Classify(member, provider);
            if (authority == ExternalHeadPartMemberAuthorityKind.Unsupported)
                continue;
            AssetOwner owner = authority ==
                    ExternalHeadPartMemberAuthorityKind.PrimaryProvider
                ? new AssetOwner(
                    provider.Plugin,
                    provider.PluginSha256,
                    provider.PluginByteLength)
                : new AssetOwner(
                    member.OriginForm.Plugin,
                    member.WinningPluginSha256,
                    member.WinningPluginByteLength);
            if (member.ModelNif is AssetPath model)
                AddDeclaredAssetPath(
                    paths, spellings, owners, model, owner, diagnostics);
            foreach (SkyrimHdptTriRoute tri in member.TriRoutes)
                AddDeclaredAssetPath(
                    paths, spellings, owners, tri.Path, owner, diagnostics);

            SkyrimFaceHeadPartRecordRoute? legacy = route.HeadParts
                .FirstOrDefault(item => item.Reference == member.OriginForm);
            if (legacy?.TextureSet is { } textureSet)
            {
                foreach (string raw in textureSet.RawTxSlots.Where(
                             item => !string.IsNullOrWhiteSpace(item)))
                {
                    if (!TryTexturePath(raw, out AssetPath texture))
                    {
                        diagnostics.Add(Error(
                            ExternalHeadPartDiagnosticCodes.AssetMissing,
                            $"Texture-set route for '{member.OriginForm}' contains unsafe path '{raw}'."));
                        continue;
                    }
                    AddDeclaredAssetPath(
                        paths, spellings, owners, texture, owner, diagnostics);
                }
            }
        }
        return new AssetClosure(paths.ToImmutable(), owners);
    }

    private static AssetClosure MergeAssetPaths(
        AssetClosure first,
        IEnumerable<DeclaredAsset> second,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var paths = ImmutableArray.CreateBuilder<AssetPath>();
        var owners = new Dictionary<string, AssetOwner>(first.Owners,
            StringComparer.OrdinalIgnoreCase);
        var spellings = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (AssetPath path in first.Paths)
        {
            paths.Add(path);
            spellings[path.Value] = path.Value;
        }
        foreach (DeclaredAsset declaration in second)
            AddDeclaredAssetPath(
                paths, spellings, owners, declaration.Path, declaration.Owner,
                diagnostics);
        return new AssetClosure(paths.ToImmutable(), owners);
    }

    private static void AddDeclaredAssetPath(
        ImmutableArray<AssetPath>.Builder paths,
        Dictionary<string, string> spellings,
        Dictionary<string, AssetOwner> owners,
        AssetPath path,
        AssetOwner owner,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (spellings.TryGetValue(path.Value, out string? spelling))
        {
            if (!string.Equals(spelling, path.Value, StringComparison.Ordinal))
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Asset paths '{spelling}' and '{path.Value}' collide case-insensitively."));
            if (owners.TryGetValue(path.Value, out AssetOwner? existing) &&
                existing != owner)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Asset '{path}' has conflicting declared provider authorities."));
            return;
        }
        spellings.Add(path.Value, path.Value);
        owners[path.Value] = owner;
        paths.Add(path);
    }

    private static FallbackMapState ProbeFallbackMap(
        WorkspacePath dataRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var path = new AssetPath(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/defaultBBPs.xml");
        try
        {
            WorkspacePath physical = new(Path.Combine(
                dataRoot.Value,
                path.Value.Replace('/', Path.DirectorySeparatorChar)));
            EnsureSafeReadPath(dataRoot, physical);
            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(physical.Value);
            }
            catch (FileNotFoundException)
            {
                return FallbackMapState.Absent;
            }
            catch (DirectoryNotFoundException)
            {
                return FallbackMapState.Absent;
            }
            if (attributes.HasFlag(FileAttributes.Directory) ||
                attributes.HasFlag(FileAttributes.ReparsePoint) ||
                attributes.HasFlag(FileAttributes.Device))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    "The present physics fallback-map authority is not an ordinary file."));
                return FallbackMapState.Invalid;
            }
            FileEvidence evidence = ReadFile(
                dataRoot, physical, MaximumAssetBytes, diagnostics,
                CancellationToken.None);
            return evidence.Length > 0 && !HasErrors(diagnostics)
                ? FallbackMapState.Present
                : FallbackMapState.Invalid;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Physics fallback-map authority could not be inspected: {exception.Message}"));
            return FallbackMapState.Invalid;
        }
    }

    private async ValueTask<AssetResolution?> ResolveAssetsAsync(
        WorkspacePath dataRoot,
        ImmutableArray<AssetPath> requiredAssets,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (requiredAssets.IsDefaultOrEmpty)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetMissing,
                "An external graph with model-bearing members must declare assets."));
            return null;
        }

        SkyrimAssetAuthorityPlanResult plan =
            await assetAuthorityPlanner.PlanAsync(
                new SkyrimAssetAuthorityPlanRequest(
                    GameEdition.SkyrimSpecialEdition,
                    dataRoot,
                    requiredAssets),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, plan.Diagnostics);
        if (!plan.Accepted || HasErrors(diagnostics))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetMissing,
                "One or more declared external NIF/TRI/DDS authorities could not be planned."));
            return null;
        }

        SkyrimAssetContentResolutionResult content =
            await assetContentResolver.ResolveAsync(
                new SkyrimAssetContentResolutionRequest(
                    dataRoot,
                    plan.Authorities.Select(ToContentAuthority)
                        .ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, content.Diagnostics);
        if (!content.Resolved || HasErrors(diagnostics))
        {
            diagnostics.Add(Error(
                content.Diagnostics.Any(item => item.Code.Contains(
                        "missing", StringComparison.OrdinalIgnoreCase))
                    ? ExternalHeadPartDiagnosticCodes.AssetMissing
                    : ExternalHeadPartDiagnosticCodes.AssetDrift,
                "One or more external asset authorities could not be reopened."));
            return null;
        }

        var authorities = plan.Authorities.ToDictionary(
            item => item.AssetPath.Value,
            StringComparer.Ordinal);
        var contents = content.Assets.ToDictionary(
            item => item.AssetPath.Value,
            StringComparer.Ordinal);
        if (plan.Authorities.Length != requiredAssets.Length ||
            authorities.Count != requiredAssets.Length ||
            requiredAssets.Any(path => !authorities.ContainsKey(path.Value)) ||
            authorities.Keys.Any(path => !requiredAssets.Any(item =>
                string.Equals(item.Value, path, StringComparison.Ordinal))))
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                "The asset authority planner returned an extra, missing, or differently spelled asset path."));

        if (content.Assets.Length != plan.Authorities.Length ||
            contents.Count != plan.Authorities.Length ||
            plan.Authorities.Any(authority =>
                !contents.TryGetValue(authority.AssetPath.Value,
                    out ResolvedSkyrimAssetContent? resolvedContent) ||
                !MatchesContentAuthority(authority, resolvedContent)) ||
            contents.Keys.Any(path => !authorities.ContainsKey(path)))
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                "The asset content resolver returned an extra, missing, or mismatched authority identity."));
        if (HasErrors(diagnostics))
            return null;
        return new AssetResolution(plan.Authorities, contents);
    }

    private static bool MatchesContentAuthority(
        SkyrimAssetAuthority authority,
        ResolvedSkyrimAssetContent content) =>
        string.Equals(authority.ProviderId, content.ProviderId,
            StringComparison.Ordinal) &&
        authority.ProviderKind switch
        {
            AssetProviderKind.Loose => content.Kind ==
                SkyrimAssetContentProviderKind.Loose,
            AssetProviderKind.Archive => content.Kind ==
                SkyrimAssetContentProviderKind.Bsa,
            _ => false
        } &&
        string.Equals(authority.ProviderPath.Value, content.ProviderPath.Value,
            StringComparison.Ordinal) &&
        authority.ProviderSha256 == content.ProviderSha256 &&
        string.Equals(authority.AssetPath.Value, content.AssetPath.Value,
            StringComparison.Ordinal) &&
        authority.ContentLength == content.ContentLength &&
        authority.ContentSha256 == content.ContentSha256;

    private ImmutableArray<ModelObservation> ReadModels(
        ImmutableArray<ExternalHeadPartRecordDependency> modelMembers,
        IReadOnlyDictionary<string, ResolvedSkyrimAssetContent> contentByPath,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var observations = ImmutableArray.CreateBuilder<ModelObservation>(
            modelMembers.Length);
        foreach (ExternalHeadPartRecordDependency member in modelMembers)
        {
            AssetPath model = member.ModelNif!.Value;
            if (!contentByPath.TryGetValue(model.Value,
                    out ResolvedSkyrimAssetContent? content))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetMissing,
                    $"External model '{model}' was not returned by the content authority."));
                continue;
            }
            ExternalHeadPartProviderNifReadResult read = _nifReader.Read(
                new ExternalHeadPartProviderNifReadRequest(
                    model,
                    content.ContentSha256,
                    content.Content,
                    AllowPhysicsBinding: true));
            AddDistinct(diagnostics, read.Diagnostics);
            if (!read.Accepted)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"External model '{model}' did not reopen as an admitted provider NIF."));
            else
                observations.Add(new ModelObservation(member, read));
        }
        return observations.ToImmutable();
    }

    private ImmutableArray<DeclaredAsset> ReadVanillaDependencies(
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        IReadOnlyDictionary<string, ResolvedSkyrimAssetContent> contentByPath,
        IReadOnlyDictionary<string, AssetOwner> owners,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var dependencies = ImmutableArray.CreateBuilder<DeclaredAsset>();
        foreach (ExternalHeadPartRecordDependency member in members.Where(item =>
                     ExternalHeadPartMemberAuthority.IsSelfOwnedVanillaMember(item) &&
                     item.ModelNif is not null))
        {
            AssetPath model = member.ModelNif!.Value;
            if (!contentByPath.TryGetValue(model.Value,
                    out ResolvedSkyrimAssetContent? content) ||
                !owners.TryGetValue(model.Value, out AssetOwner? owner))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetMissing,
                    $"Official vanilla model '{model}' was not reopened with an owner."));
                continue;
            }
            ExternalHeadPartProviderNifReadResult read = _nifReader.Read(
                new ExternalHeadPartProviderNifReadRequest(
                    model,
                    content.ContentSha256,
                    content.Content,
                    AllowPhysicsBinding: true));
            AddDistinct(diagnostics, read.Diagnostics);
            if (!read.Accepted)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Official vanilla model '{model}' did not reopen as an admitted NIF."));
                continue;
            }
            foreach (AssetPath dependency in read.Dependencies)
                dependencies.Add(new DeclaredAsset(dependency, owner));
        }
        return dependencies.ToImmutable();
    }

    private static ImmutableArray<ExternalHeadPartAssetDependency> BuildAssetDependencies(
        WorkspacePath dataRoot,
        ExternalHeadPartProviderIdentity provider,
        AssetResolution resolved,
        IReadOnlyDictionary<string, AssetOwner> owners,
        ExternalHeadPartPhysicsBindingResult physics,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var assets = new Dictionary<string, ExternalHeadPartAssetDependency>(
            StringComparer.OrdinalIgnoreCase);
        var spellings = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (SkyrimAssetAuthority authority in resolved.Authorities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!resolved.ByAssetPath.TryGetValue(authority.AssetPath.Value,
                    out ResolvedSkyrimAssetContent? content))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetMissing,
                    $"Asset authority '{authority.AssetPath}' was not reopened."));
                continue;
            }
            if (!ValidateResolvedContent(
                    dataRoot, authority, content, diagnostics, cancellationToken))
                continue;
            ExternalHeadPartArchiveMemberAuthority? archive =
                authority.ProviderKind == AssetProviderKind.Archive
                    ? BuildArchiveAuthority(dataRoot, authority, content,
                        diagnostics, cancellationToken)
                    : null;
            if (authority.ProviderKind == AssetProviderKind.Archive &&
                archive is null)
                continue;
            if (!owners.TryGetValue(
                    content.AssetPath.Value, out AssetOwner? owner))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Resolved asset '{content.AssetPath}' has no declared owner authority."));
                continue;
            }
            AddAsset(assets, spellings,
                new ExternalHeadPartAssetDependency(
                    content.AssetPath,
                    content.ContentSha256,
                    content.ContentLength,
                    owner.Plugin,
                    owner.PluginSha256,
                    archive),
                diagnostics);
        }

        if (physics.Binding is null)
            return assets.Values.OrderBy(item => item.Path.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.Path.Value, StringComparer.Ordinal)
                .ToImmutableArray();

        foreach (ExternalHeadPartPhysicsShapeBinding shape in
                 physics.Binding.Shapes)
        {
            FileEvidence xml = ReadFile(
                dataRoot, shape.XmlPath, MaximumAssetBytes, diagnostics,
                cancellationToken);
            if (xml.Hash != shape.XmlSha256 || xml.Length != shape.XmlByteLength)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Physics XML '{shape.XmlPath}' changed after the physics resolver reopened it."));
            AddAsset(assets, spellings,
                new ExternalHeadPartAssetDependency(
                    shape.XmlPath,
                    xml.Hash,
                    xml.Length,
                    provider.Plugin,
                    provider.PluginSha256,
                    null),
                diagnostics);
        }
        foreach (ExternalHeadPartAssetDependency asset in physics.PhysicsAssets)
        {
            FileEvidence evidence = ReadFile(
                dataRoot, asset.Path, MaximumAssetBytes, diagnostics,
                cancellationToken);
            if (evidence.Hash != asset.Sha256 || evidence.Length != asset.ByteLength)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Physics asset '{asset.Path}' changed after the physics resolver reopened it."));
            AddAsset(assets, spellings,
                asset with
                {
                    Sha256 = evidence.Hash,
                    ByteLength = evidence.Length,
                    ProviderPlugin = provider.Plugin,
                    ProviderPluginSha256 = provider.PluginSha256,
                    ArchiveMember = null
                },
                diagnostics);
        }
        if (physics.Binding.MappingAuthority is { } mapping)
        {
            FileEvidence evidence = ReadFile(
                dataRoot, mapping.Path, MaximumAssetBytes, diagnostics,
                cancellationToken);
            if (evidence.Hash != mapping.Sha256 || evidence.Length != mapping.ByteLength)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Physics mapping '{mapping.Path}' changed after the resolver reopened it."));
            AddAsset(assets, spellings,
                new ExternalHeadPartAssetDependency(
                    mapping.Path,
                    evidence.Hash,
                    evidence.Length,
                    provider.Plugin,
                    provider.PluginSha256,
                    null),
                diagnostics);
        }

        return assets.Values.OrderBy(item => item.Path.Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Path.Value, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    private static bool ValidateResolvedContent(
        WorkspacePath dataRoot,
        SkyrimAssetAuthority authority,
        ResolvedSkyrimAssetContent content,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (content.ContentLength != authority.ContentLength ||
            content.Content.Length != authority.ContentLength ||
            content.ContentSha256 != authority.ContentSha256 ||
            Hash(content.Content.AsSpan()) != authority.ContentSha256)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Reopened content for '{authority.AssetPath}' disagrees with its planned hash or length."));
            return false;
        }
        try
        {
            FileEvidence provider = ReadFile(
                dataRoot,
                authority.ProviderPath,
                MaximumProviderBytes,
                diagnostics,
                cancellationToken);
            if (provider.Hash != authority.ProviderSha256)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Provider '{authority.ProviderPath}' changed after asset planning."));
                return false;
            }
            if (authority.ProviderKind == AssetProviderKind.Loose &&
                (!provider.Bytes.AsSpan().SequenceEqual(content.Content.AsSpan()) ||
                 provider.Hash != authority.ContentSha256))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Loose asset '{authority.AssetPath}' changed between authority reads."));
                return false;
            }
            return true;
        }
        catch (IOException exception)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Provider '{authority.ProviderPath}' could not be reopened: {exception.Message}"));
            return false;
        }
    }

    private static ExternalHeadPartArchiveMemberAuthority? BuildArchiveAuthority(
        WorkspacePath dataRoot,
        SkyrimAssetAuthority authority,
        ResolvedSkyrimAssetContent content,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        FileEvidence archive = ReadFile(
            dataRoot, authority.ProviderPath, MaximumProviderBytes,
            diagnostics, cancellationToken);
        if (archive.Hash != authority.ProviderSha256 ||
            archive.Length != new FileInfo(authority.ProviderPath.Value).Length)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Archive provider '{authority.ProviderPath}' changed while its member was reopened."));
            return null;
        }
        using AncestorLease? archiveAncestors = OpenAncestorLease(
            dataRoot, authority.ProviderPath);
        using SafeFileHandle archiveHandle = OpenNoFollowReadHandle(
            dataRoot, authority.ProviderPath, archiveAncestors);
        if (!GetFileInformationByHandle(
                archiveHandle, out ByHandleFileInformation archiveIdentity))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Archive provider '{authority.ProviderPath}' identity could not be pinned."));
            return null;
        }
        if (!VerifyArchiveMember(
                authority.ProviderPath,
                content.AssetPath,
                content.ContentSha256,
                content.ContentLength,
                diagnostics))
            return null;
        FileEvidence finalArchive;
        try
        {
            finalArchive = ReadPinnedFile(
                archiveHandle, authority.ProviderPath, MaximumProviderBytes);
        }
        catch (Exception exception) when (exception is IOException or
            InvalidDataException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Archive provider '{authority.ProviderPath}' could not be read from its pinned handle: {exception.Message}"));
            return null;
        }
        if (finalArchive.Hash != archive.Hash ||
            finalArchive.Length != archive.Length)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Archive provider '{authority.ProviderPath}' changed during member closure verification."));
            return null;
        }
        if (!GetFileInformationByHandle(
                archiveHandle, out ByHandleFileInformation finalIdentity) ||
            !SameHandleIdentity(archiveIdentity, finalIdentity) ||
            !IsContainedFinalPath(dataRoot.Value, GetFinalPath(archiveHandle)))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Archive provider '{authority.ProviderPath}' failed final pinned identity verification."));
            return null;
        }
        string archiveName = Path.GetFileName(authority.ProviderPath.Value);
        if (string.IsNullOrWhiteSpace(archiveName))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Archive provider for '{authority.AssetPath}' has no portable filename."));
            return null;
        }
        return new ExternalHeadPartArchiveMemberAuthority(
            new AssetPath(archiveName),
            archive.Hash,
            archive.Length,
            content.AssetPath,
            content.ContentSha256,
            content.ContentLength);
    }

    private static FileEvidence ReadPinnedFile(
        SafeFileHandle handle,
        WorkspacePath path,
        long maximumBytes)
    {
        long length = RandomAccess.GetLength(handle);
        if (length <= 0 || length > maximumBytes)
            throw new InvalidDataException(
                $"File '{path}' is outside the bounded read size.");
        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)length));
        var read = 0;
        while (read < bytes.Length)
        {
            int count = RandomAccess.Read(
                handle, bytes.AsSpan(read), read);
            if (count == 0)
                throw new IOException($"File '{path}' ended before its pinned length.");
            read += count;
        }
        return new FileEvidence(Hash(bytes), bytes.LongLength, bytes);
    }

    private static bool VerifyArchiveMember(
        WorkspacePath archivePath,
        AssetPath memberPath,
        Sha256Hash expectedHash,
        long expectedLength,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var reader = Archive.CreateReader(
                GameRelease.SkyrimSE,
                new FilePath(archivePath.Value),
                new System.IO.Abstractions.FileSystem());
            var matches = reader.Files
                .Where(entry =>
                {
                    try
                    {
                        return new AssetPath(entry.Path).Value.Equals(
                            memberPath.Value,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    catch (ArgumentException)
                    {
                        return false;
                    }
                })
                .ToArray();
            if (matches.Length != 1)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetMissing,
                    $"Archive '{archivePath}' does not contain exactly one member '{memberPath}'."));
                return false;
            }
            byte[] bytes = matches[0].GetBytes();
            if (bytes.LongLength != expectedLength ||
                Hash(bytes) != expectedHash)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Archive member '{memberPath}' disagrees with independently reopened content evidence."));
                return false;
            }
            return true;
        }
        catch (Exception exception) when (exception is IOException or
            InvalidDataException or ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Archive member '{memberPath}' could not be independently reopened: {exception.Message}"));
            return false;
        }
    }

    private static void AddAsset(
        Dictionary<string, ExternalHeadPartAssetDependency> assets,
        Dictionary<string, string> spellings,
        ExternalHeadPartAssetDependency asset,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (spellings.TryGetValue(asset.Path.Value, out string? spelling) &&
            !string.Equals(spelling, asset.Path.Value, StringComparison.Ordinal))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetDrift,
                $"Asset paths '{spelling}' and '{asset.Path.Value}' collide case-insensitively."));
            return;
        }
        spellings[asset.Path.Value] = asset.Path.Value;
        if (assets.TryGetValue(asset.Path.Value, out ExternalHeadPartAssetDependency? existing))
        {
            if (existing != asset)
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.AssetDrift,
                    $"Asset authority '{asset.Path}' was reopened with inconsistent evidence."));
            return;
        }
        assets.Add(asset.Path.Value, asset);
    }

    private static bool MatchesExpectedDescriptor(
        ExternalHeadPartDependencyDescriptor descriptor,
        ExternalHeadPartDependencyDescriptor? expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (expected is null)
            return true;
        try
        {
            byte[] actualBytes = ExternalHeadPartDependencyDescriptorCodec
                .SerializeDescriptor(descriptor);
            byte[] expectedBytes = ExternalHeadPartDependencyDescriptorCodec
                .SerializeDescriptor(expected);
            if (actualBytes.AsSpan().SequenceEqual(expectedBytes))
                return true;
        }
        catch (Exception exception) when (exception is ArgumentException or
            InvalidDataException or NotSupportedException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.DescriptorLost,
                $"The expected external descriptor was not canonical: {exception.Message}"));
            return false;
        }
        diagnostics.Add(Error(
            ExternalHeadPartDiagnosticCodes.DescriptorLost,
            "Independent external headpart rediscovery disagreed with the expected descriptor."));
        return false;
    }

    private static Sha256Hash ComputeGraphHash(
        ImmutableArray<ExternalHeadPartRecordDependency> members)
    {
        var builder = new StringBuilder();
        foreach (ExternalHeadPartRecordDependency member in members)
        {
            Append(builder, member.OriginForm);
            Append(builder, member.RequiredOutputMaster);
            Append(builder, member.WinningForm);
            Append(builder, member.WinningPlugin);
            Append(builder, member.WinningPluginSha256);
            Append(builder, member.WinningPluginByteLength);
            Append(builder, member.WinningRecordSha256);
            Append(builder, member.EditorId);
            Append(builder, member.DeclaredType);
            Append(builder, member.EffectiveType);
            Append(builder, member.ModelNif);
            foreach (SkyrimHdptTriRoute tri in member.TriRoutes)
            {
                Append(builder, tri.Role);
                Append(builder, tri.Path);
            }
            foreach (FormReference edge in member.HnamEdges)
                Append(builder, edge);
            Append(builder, member.Parent);
            Append(builder, member.Depth);
            Append(builder, member.RouteOrder);
            Append(builder, member.AppliesToSex);
            Append(builder, member.ValidRace);
            builder.Append('\n');
        }
        return Hash(Encoding.UTF8.GetBytes(GraphHashDomain + builder));
    }

    private static void Append(StringBuilder builder, object? value) =>
        builder.Append(value?.ToString() ?? "<null>").Append('\0');

    private static async ValueTask<FileEvidence> ReadFileAsync(
        WorkspacePath dataRoot,
        WorkspacePath path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        FileEvidence first = await ReadFileOnceAsync(
            dataRoot, path, maximumBytes, cancellationToken).ConfigureAwait(false);
        FileEvidence second = await ReadFileOnceAsync(
            dataRoot, path, maximumBytes, cancellationToken).ConfigureAwait(false);
        if (first.Hash != second.Hash || first.Length != second.Length ||
            !first.Bytes.AsSpan().SequenceEqual(second.Bytes.AsSpan()))
            throw new InvalidDataException(
                $"File '{path}' changed between independent evidence reads.");
        return second;
    }

    private static async ValueTask<FileEvidence> ReadFileOnceAsync(
        WorkspacePath dataRoot,
        WorkspacePath path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        // Keep every no-follow ancestor handle open while resolving and
        // reading the final file. Windows then cannot replace an intermediate
        // directory between the lexical/reparse check and CreateFile.
        using AncestorLease? ancestors = OpenAncestorLease(dataRoot, path);
        using SafeFileHandle handle = OpenNoFollowReadHandle(
            dataRoot, path, ancestors);
        await using var stream = new FileStream(
            handle,
            FileAccess.Read,
            128 * 1024,
            isAsync: true);
        if (stream.Length <= 0 || stream.Length > maximumBytes)
            throw new InvalidDataException(
                $"File '{path}' is outside the bounded read size.");
        var bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(false);
        return new FileEvidence(
            Hash(bytes),
            bytes.LongLength,
            bytes);
    }

    private static SafeFileHandle OpenNoFollowReadHandle(
        WorkspacePath dataRoot,
        WorkspacePath path,
        AncestorLease? ancestors = null)
    {
        EnsureSafeReadPath(dataRoot, path);
        if (!OperatingSystem.IsWindows())
            return File.OpenHandle(path.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, FileOptions.SequentialScan);

        SafeFileHandle handle = CreateFile(
            path.Value,
            GenericRead,
            (uint)FileShare.Read,
            IntPtr.Zero,
            OpenExisting,
            FileFlagOpenReparsePoint | FileFlagSequentialScan |
                FileFlagOverlapped,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new IOException(
                $"Could not open ordinary evidence '{path}'.",
                new Win32Exception(error));
        }

        try
        {
            if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info) ||
                (info.FileAttributes & (FileAttributeDirectory |
                    FileAttributeReparsePoint | FileAttributeDevice)) != 0)
                throw new InvalidDataException(
                    $"Evidence path '{path}' is not an ordinary file.");
            string finalPath = GetFinalPath(handle);
            if (!IsContainedFinalPath(dataRoot.Value, finalPath))
                throw new InvalidDataException(
                    $"Evidence path '{path}' resolves outside the supplied Data root.");
            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static AncestorLease? OpenAncestorLease(
        WorkspacePath dataRoot,
        WorkspacePath path)
    {
        if (!OperatingSystem.IsWindows())
            return null;
        EnsureSafeReadPath(dataRoot, path);
        string root = Path.GetFullPath(dataRoot.Value)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string? current = Path.GetDirectoryName(Path.GetFullPath(path.Value));
        var directories = new List<string>();
        while (!string.IsNullOrWhiteSpace(current))
        {
            directories.Add(current);
            if (string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
                break;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Read path '{path}' could not pin its Data ancestors.");
            current = parent;
        }
        if (directories.Count == 0 ||
            !string.Equals(directories[^1], root,
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Read path '{path}' escaped its Data ancestors.");

        var handles = new List<SafeFileHandle>(directories.Count);
        try
        {
            foreach (string directory in ((IEnumerable<string>)directories).Reverse())
            {
                SafeFileHandle handle = CreateFile(
                    directory,
                    GenericRead,
                    (uint)(FileShare.Read | FileShare.Write),
                    IntPtr.Zero,
                    OpenExisting,
                    FileFlagOpenReparsePoint | FileFlagBackupSemantics,
                    IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    throw new IOException(
                        $"Could not pin ordinary ancestor '{directory}'.",
                        new Win32Exception(error));
                }
                if (!GetFileInformationByHandle(handle,
                        out ByHandleFileInformation info) ||
                    (info.FileAttributes & (FileAttributeDirectory |
                        FileAttributeReparsePoint | FileAttributeDevice)) !=
                    FileAttributeDirectory)
                {
                    handle.Dispose();
                    throw new InvalidDataException(
                        $"Ancestor '{directory}' is not an ordinary directory.");
                }
                handles.Add(handle);
            }
            return new AncestorLease(handles);
        }
        catch
        {
            foreach (SafeFileHandle handle in handles)
                handle.Dispose();
            throw;
        }
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        char[] buffer = new char[512];
        uint length = GetFinalPathNameByHandle(
            handle, buffer, (uint)buffer.Length, 0);
        if (length == 0)
            throw new IOException(
                "Could not resolve the final path of ordinary evidence.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        while (length >= buffer.Length)
        {
            Array.Resize(ref buffer, checked(buffer.Length * 2));
            length = GetFinalPathNameByHandle(
                handle, buffer, (uint)buffer.Length, 0);
            if (length == 0)
                throw new IOException(
                    "Could not resolve the final path of ordinary evidence.",
                    new Win32Exception(Marshal.GetLastWin32Error()));
        }
        return new string(buffer, 0, checked((int)length));
    }

    private static bool IsContainedFinalPath(
        string dataRoot,
        string finalPath)
    {
        static string Normalize(string path)
        {
            if (path.StartsWith(@"\\?\UNC\",
                    StringComparison.OrdinalIgnoreCase))
                return @"\" + path[7..];
            if (path.StartsWith(@"\\?\",
                    StringComparison.OrdinalIgnoreCase))
                return path[4..];
            return path;
        }

        string root = Path.GetFullPath(Normalize(dataRoot))
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(Normalize(finalPath));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            full.StartsWith(root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static FileEvidence ReadFile(
        WorkspacePath dataRoot,
        WorkspacePath path,
        long maximumBytes,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            return ReadFileAsync(
                    dataRoot,
                    path,
                    maximumBytes,
                    cancellationToken)
                .AsTask().GetAwaiter().GetResult();
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException)
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.AssetMissing,
                $"Asset '{path}' could not be independently reopened: {exception.Message}"));
            return new FileEvidence(default, 0, []);
        }
    }

    private static FileEvidence ReadFile(
        WorkspacePath dataRoot,
        AssetPath path,
        long maximumBytes,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken) =>
        ReadFile(
            dataRoot,
            new WorkspacePath(Path.Combine(
                dataRoot.Value,
                path.Value.Replace('/', Path.DirectorySeparatorChar))),
            maximumBytes,
            diagnostics,
            cancellationToken);

    private static void EnsureSafeReadPath(
        WorkspacePath dataRoot,
        WorkspacePath path)
    {
        string root = Path.GetFullPath(dataRoot.Value)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string full = Path.GetFullPath(path.Value);
        string relative = Path.GetRelativePath(root, full);
        if (relative.Equals("..", StringComparison.Ordinal) ||
            relative.StartsWith(".." + Path.DirectorySeparatorChar,
                StringComparison.Ordinal) ||
            Path.IsPathRooted(relative) ||
            HasAlternateDataStream(full))
            throw new InvalidDataException(
                $"Read path '{path}' escapes the supplied Data root.");
        string? current = full;
        while (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) &
                    (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException(
                    $"Read path '{path}' traverses a reparse or device entry.");
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(
                    $"Read path '{path}' could not be contained below Data root.");
            current = parent;
        }
        if ((File.GetAttributes(root) &
                (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
            throw new InvalidDataException("The supplied Data root is not ordinary.");
    }

    private static bool IsKLocal(WorkspacePath path) =>
        string.Equals(Path.GetPathRoot(path.Value), @"K:\",
            StringComparison.OrdinalIgnoreCase);

    private const uint GenericRead = 0x8000_0000;
    private const uint OpenExisting = 3;
    private const uint FileFlagOpenReparsePoint = 0x0020_0000;
    private const uint FileFlagBackupSemantics = 0x0200_0000;
    private const uint FileFlagSequentialScan = 0x0800_0000;
    private const uint FileFlagOverlapped = 0x4000_0000;
    private const uint FileAttributeDirectory = 0x0000_0010;
    private const uint FileAttributeDevice = 0x0000_0040;
    private const uint FileAttributeReparsePoint = 0x0000_0400;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        [Out] char[] lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        internal uint FileAttributes;
        internal System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        internal System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        internal uint VolumeSerialNumber;
        internal uint FileSizeHigh;
        internal uint FileSizeLow;
        internal uint NumberOfLinks;
        internal uint FileIndexHigh;
        internal uint FileIndexLow;
    }

    private static bool SamePlugin(PluginName left, PluginName right) =>
        string.Equals(left.Value, right.Value,
            StringComparison.OrdinalIgnoreCase);

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId &&
        SamePlugin(left.Plugin, right.Plugin);

    private static string GraphMemberLabel(
        SkyrimFaceHeadPartGraphRoute member,
        SkyrimFaceHeadPartGraphRoute root) =>
        member.Parent is null && SameReference(member.OriginForm, root.OriginForm)
            ? "selected Hair root"
            : "HNAM member";

    private static string GraphMemberLabel(
        ExternalHeadPartRecordDependency member,
        FormReference rootForm) =>
        member.Parent is null && SameReference(member.OriginForm, rootForm)
            ? "selected Hair root"
            : "HNAM member";

    private static bool SameProviderAuthority(
        SkyrimFaceRecordProvider left,
        SkyrimFaceRecordProvider right) =>
        SamePlugin(left.Plugin, right.Plugin) &&
        string.Equals(left.Path.Value, right.Path.Value,
            StringComparison.OrdinalIgnoreCase) &&
        left.Sha256 == right.Sha256;

    private static bool SameHandleIdentity(
        ByHandleFileInformation left,
        ByHandleFileInformation right) =>
        left.VolumeSerialNumber == right.VolumeSerialNumber &&
        left.FileIndexHigh == right.FileIndexHigh &&
        left.FileIndexLow == right.FileIndexLow;

    private static bool HasAlternateDataStream(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    private static bool IsCanonicalMesh(AssetPath path, string extension) =>
        path.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase) &&
        path.Value.EndsWith(extension, StringComparison.OrdinalIgnoreCase);

    private static bool TryTexturePath(string raw, out AssetPath path)
    {
        path = default;
        string normalized = raw.Trim().Replace('\\', '/');
        if (normalized.StartsWith("Data/", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Data/".Length..];
        if (!normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase) ||
            !normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            return false;
        try
        {
            path = new AssetPath(normalized);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static SkyrimAssetContentAuthority ToContentAuthority(
        SkyrimAssetAuthority authority) =>
        new(
            authority.ProviderId,
            authority.ProviderKind switch
            {
                AssetProviderKind.Loose => SkyrimAssetContentProviderKind.Loose,
                AssetProviderKind.Archive => SkyrimAssetContentProviderKind.Bsa,
                _ => throw new InvalidDataException(
                    $"Unsupported asset provider kind '{authority.ProviderKind}'.")
            },
            authority.ProviderPath,
            authority.ProviderSha256,
            authority.AssetPath,
            authority.ContentLength,
            authority.ContentSha256);

    private static ExternalHeadPartDependencyDiscoveryResult NotApplicable(
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.Add(new Diagnostic(
            "external-headpart-not-applicable",
            DiagnosticSeverity.Info,
            "The selected Hair route contains no admitted external physics binding."));
        return new ExternalHeadPartDependencyDiscoveryResult(
            ExternalHeadPartDependencyDiscoveryStatus.NotApplicable,
            null,
            diagnostics.ToImmutable());
    }

    private static ExternalHeadPartDependencyDiscoveryResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(
            ExternalHeadPartDependencyDiscoveryStatus.Refused,
            null,
            diagnostics.ToImmutable());

    private static void AddDistinct(
        ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic diagnostic in source)
            if (!target.Any(existing => existing.Code == diagnostic.Code &&
                    existing.Severity == diagnostic.Severity &&
                    existing.Message == diagnostic.Message))
                target.Add(diagnostic);
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Sha256Hash Hash(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private sealed record ModelObservation(
        ExternalHeadPartRecordDependency Member,
        ExternalHeadPartProviderNifReadResult Read);

    private sealed record AssetResolution(
        ImmutableArray<SkyrimAssetAuthority> Authorities,
        IReadOnlyDictionary<string, ResolvedSkyrimAssetContent> ByAssetPath);

    private sealed record AssetOwner(
        PluginName Plugin,
        Sha256Hash PluginSha256,
        long PluginByteLength);

    private sealed record DeclaredAsset(AssetPath Path, AssetOwner Owner);

    private sealed record AssetClosure(
        ImmutableArray<AssetPath> Paths,
        IReadOnlyDictionary<string, AssetOwner> Owners);

    private sealed record FileEvidence(
        Sha256Hash Hash,
        long Length,
        byte[] Bytes);

    private sealed class AncestorLease(
        IReadOnlyList<SafeFileHandle> handles) : IDisposable
    {
        public void Dispose()
        {
            foreach (SafeFileHandle handle in handles)
                handle.Dispose();
        }
    }

    private enum FallbackMapState
    {
        Absent = 0,
        Present = 1,
        Invalid = 2
    }
}
