using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class ExternalHeadPartPhysicsBindingResolver :
    IExternalHeadPartPhysicsBindingResolver
{
    private readonly IWorkspacePolicy _workspacePolicy;
    private readonly WorkspacePath _labRoot;
    private readonly ExternalHeadPartProviderNifReader _nifReader = new();
    private readonly ExternalHeadPartPhysicsXmlReader _xmlReader;
    private readonly Action<ExternalHeadPartPhysicsXmlDocument>? _afterPhysicsXmlRead;

    public ExternalHeadPartPhysicsBindingResolver(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot)
        : this(workspacePolicy, labRoot, null)
    {
    }

    internal ExternalHeadPartPhysicsBindingResolver(
        IWorkspacePolicy workspacePolicy,
        WorkspacePath labRoot,
        Action<ExternalHeadPartPhysicsXmlDocument>? afterPhysicsXmlRead)
    {
        _workspacePolicy = workspacePolicy ??
            throw new ArgumentNullException(nameof(workspacePolicy));
        _labRoot = labRoot;
        _afterPhysicsXmlRead = afterPhysicsXmlRead;
        _xmlReader = new ExternalHeadPartPhysicsXmlReader(
            _workspacePolicy,
            _labRoot);
    }

    public async ValueTask<ExternalHeadPartPhysicsBindingResult> ResolveAsync(
        ExternalHeadPartPhysicsBindingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        try
        {
            diagnostics.AddRange(_workspacePolicy.EvaluateReadRoot(
                _labRoot,
                request.DataRoot));
            ValidateRequest(request, diagnostics);
            if (HasErrors(diagnostics))
                return Refused(diagnostics);

            var observations = ImmutableArray.CreateBuilder<
                ProviderModelObservation>(request.ProviderMembers.Length);
            foreach (ExternalHeadPartRecordDependency member in
                     request.ProviderMembers.OrderBy(item => item.RouteOrder)
                         .ThenBy(item => item.OriginForm.ToString(),
                             StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                AssetPath model = member.ModelNif!.Value;
                ExternalHeadPartPhysicsFileReadEvidence modelEvidence =
                    await ExternalHeadPartPhysicsXmlReader.ReadOrdinaryFileAsync(
                        request.DataRoot,
                        model,
                        ExternalHeadPartPhysicsXmlReader.MaximumAssetBytes,
                        cancellationToken);
                ExternalHeadPartProviderNifReadResult nif = _nifReader.Read(
                    new ExternalHeadPartProviderNifReadRequest(
                        model,
                        modelEvidence.Sha256,
                        ImmutableArray.CreateRange(modelEvidence.Bytes),
                        AllowPhysicsBinding: true));
                diagnostics.AddRange(nif.Diagnostics);
                if (!nif.Accepted || nif.ShapeNames.IsDefaultOrEmpty ||
                    nif.PhysicsObjectLocators.Any(locator =>
                        !ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
                            locator,
                            "SKSE/Plugins/hdtSkinnedMeshConfigs/",
                            ".xml",
                            out _)))
                {
                    diagnostics.Add(Error(
                        "external-headpart-physics-nif-refused",
                        $"Provider model '{model.Value}' did not expose an admitted physics shape/locator set."));
                    return Refused(diagnostics);
                }
                observations.Add(new ProviderModelObservation(
                    member.OriginForm,
                    model,
                    nif.ShapeNames,
                    nif.PhysicsObjectLocators));
            }

            bool hasDirectLocator = observations.Any(item =>
                !item.PhysicsObjectLocators.IsEmpty);
            ExternalHeadPartPhysicsBindingMode mode;
            ExternalHeadPartPhysicsMappingAuthority? mappingAuthority = null;
            var shapeXmlPaths = new Dictionary<ShapeBindingKey, AssetPath>();
            var inheritedRoots = new Dictionary<FormReference, ProviderModelObservation>();
            if (observations.Any(item => item.PhysicsObjectLocators.Length > 1))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.PhysicsAmbiguous,
                    "Each provider model must expose at most one direct HDT physics locator."));
                return Refused(diagnostics);
            }

            var chainMembers = (request.ChainMembers.IsDefault
                ? request.ProviderMembers : request.ChainMembers).ToDictionary(member => member.OriginForm);
            foreach (ProviderModelObservation strand in observations.Where(item => item.PhysicsObjectLocators.IsEmpty))
            {
                var roots = ExternalHeadPartMemberAuthority.TryGetChainRoot(strand.MemberForm, chainMembers, out var chainRoot)
                    ? observations.Where(item => item.MemberForm == chainRoot &&
                        item.PhysicsObjectLocators.Length == 1).ToArray() : [];
                if (roots.Select(item => item.PhysicsObjectLocators[0]).Distinct(StringComparer.Ordinal).Count() > 1)
                {
                    diagnostics.Add(Error(ExternalHeadPartDiagnosticCodes.PhysicsAmbiguous,
                        $"Member '{strand.MemberForm}' has distinct locator-bearing roots in its admitted HDPT chain."));
                    return Refused(diagnostics);
                }
                if (roots.Length > 0) inheritedRoots[strand.MemberForm] = roots[0];
                else if (request.PhysicsBinding == ExternalHeadPartPhysicsBindingDisposition.InheritRoot)
                {
                    diagnostics.Add(Error(ExternalHeadPartDiagnosticCodes.PhysicsMissing,
                        $"Member '{strand.MemberForm}' requested inherit-root but its admitted HDPT chain has no locator-bearing root."));
                    return Refused(diagnostics);
                }
            }

            ExternalHeadPartDefaultBbpReadResult? fallbackMapping = null;
            if (observations.Any(item => item.PhysicsObjectLocators.IsEmpty && !inheritedRoots.ContainsKey(item.MemberForm)))
            {
                ExternalHeadPartDefaultBbpReadResult mapping =
                    await _xmlReader.ReadDefaultBbpAsync(
                        request.DataRoot,
                        cancellationToken);
                diagnostics.AddRange(mapping.Diagnostics);
                if (!mapping.Accepted || mapping.Mapping is null)
                    return Refused(diagnostics);
                fallbackMapping = mapping;
            }

            Dictionary<string, AssetPath>? byShape = fallbackMapping?.Mapping?.Entries
                .ToDictionary(item => item.ShapeName, item => item.XmlPath,
                    StringComparer.Ordinal);
            foreach (ProviderModelObservation observation in observations)
            {
                AssetPath? directPath = observation.PhysicsObjectLocators.Length == 1
                    ? new AssetPath(observation.PhysicsObjectLocators[0])
                    : null;
                foreach (string shapeName in observation.ShapeNames)
                {
                    AssetPath xmlPath;
                    if (directPath is { } direct)
                        xmlPath = direct;
                    else if (inheritedRoots.TryGetValue(observation.MemberForm, out var inheritedRoot))
                        xmlPath = new AssetPath(inheritedRoot.PhysicsObjectLocators[0]);
                    else if (byShape is not null &&
                             byShape.TryGetValue(shapeName,
                                 out AssetPath fallbackPath))
                        xmlPath = fallbackPath;
                    else
                    {
                        diagnostics.Add(Error(
                            ExternalHeadPartDiagnosticCodes.PhysicsMissing,
                            $"defaultBBPs.xml does not map relevant shape '{shapeName}'."));
                        return Refused(diagnostics);
                    }

                    shapeXmlPaths[new ShapeBindingKey(
                        observation.MemberForm,
                        observation.ModelNif,
                        shapeName)] = xmlPath;
                }
            }

            if (fallbackMapping?.Mapping is { } mappingAuthorityEvidence)
                mappingAuthority = new ExternalHeadPartPhysicsMappingAuthority(
                    mappingAuthorityEvidence.Path,
                    mappingAuthorityEvidence.Sha256,
                    mappingAuthorityEvidence.ByteLength);
            mode = hasDirectLocator && fallbackMapping is null
                ? ExternalHeadPartPhysicsBindingMode.DirectNifExtraData
                : ExternalHeadPartPhysicsBindingMode.DefaultBbpMap;

            var canonicalXmlPaths = new Dictionary<string, AssetPath>(
                StringComparer.OrdinalIgnoreCase);
            foreach (AssetPath xmlPath in shapeXmlPaths.Values)
            {
                if (canonicalXmlPaths.TryGetValue(xmlPath.Value,
                        out AssetPath existing) &&
                    !string.Equals(existing.Value, xmlPath.Value,
                        StringComparison.Ordinal))
                {
                    diagnostics.Add(Error(
                        "external-headpart-physics-path-collision",
                        $"Physics XML paths '{existing.Value}' and '{xmlPath.Value}' collide case-insensitively."));
                    return Refused(diagnostics);
                }
                canonicalXmlPaths[xmlPath.Value] = xmlPath;
            }

            var documents = new Dictionary<string,
                ExternalHeadPartPhysicsXmlDocument>(
                    StringComparer.OrdinalIgnoreCase);
            foreach (AssetPath xmlPath in canonicalXmlPaths.Values.OrderBy(
                         item => item.Value,
                         StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                ExternalHeadPartPhysicsXmlReadResult xml =
                    await _xmlReader.ReadPhysicsXmlAsync(
                        request.DataRoot,
                        xmlPath,
                        cancellationToken);
                diagnostics.AddRange(xml.Diagnostics);
                if (!xml.Accepted || xml.Document is null)
                    return Refused(diagnostics);
                _afterPhysicsXmlRead?.Invoke(xml.Document);
                documents[xmlPath.Value] = xml.Document;
            }

            var shapeBindings = ImmutableArray.CreateBuilder<
                ExternalHeadPartPhysicsShapeBinding>();
            foreach (ProviderModelObservation observation in observations)
                foreach (string shapeName in observation.ShapeNames)
                {
                    AssetPath xmlPath = shapeXmlPaths[new ShapeBindingKey(
                        observation.MemberForm,
                        observation.ModelNif,
                        shapeName)];
                    ExternalHeadPartPhysicsXmlDocument document =
                        documents[xmlPath.Value];
                    inheritedRoots.TryGetValue(observation.MemberForm, out var inheritedRoot);
                    shapeBindings.Add(new(
                        observation.MemberForm,
                        observation.ModelNif,
                        shapeName,
                        document.Path,
                        document.Sha256,
                        document.ByteLength,
                        inheritedRoots.Count == 0 ? null : inheritedRoot is not null
                            ? ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot
                            : observation.PhysicsObjectLocators.IsEmpty
                                ? ExternalHeadPartPhysicsBindingOrigin.DefaultBbp
                                : ExternalHeadPartPhysicsBindingOrigin.Direct,
                        inheritedRoot is null ? null : new(inheritedRoot.MemberForm,
                            inheritedRoot.ModelNif, inheritedRoot.ShapeNames[0])));
                }

            ImmutableArray<ExternalHeadPartPhysicsShapeBinding> orderedShapes =
                shapeBindings.OrderBy(item => item.MemberForm.ToString(),
                        StringComparer.Ordinal)
                    .ThenBy(item => item.ModelNif.Value,
                        StringComparer.Ordinal)
                    .ThenBy(item => item.ShapeName,
                        StringComparer.Ordinal)
                    .ToImmutableArray();
            if (orderedShapes.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.PhysicsMissing,
                    "No relevant physics shapes were admitted from the provider models."));
                return Refused(diagnostics);
            }

            var assets = ImmutableArray.CreateBuilder<
                ExternalHeadPartAssetDependency>();
            var assetPathAuthorities = new Dictionary<string,
                (string Spelling, Sha256Hash Sha256, long ByteLength,
                    PluginName ProviderPlugin, Sha256Hash ProviderSha256)>(
                StringComparer.OrdinalIgnoreCase);
            foreach (ExternalHeadPartPhysicsXmlDocument document in documents.Values
                         .OrderBy(item => item.Path.Value,
                             StringComparer.Ordinal))
            {
                if (!AddPhysicsAsset(assets, assetPathAuthorities,
                        document.Path, document.Sha256, document.ByteLength,
                        request.Provider, diagnostics))
                    return Refused(diagnostics);
                foreach (ExternalHeadPartPhysicsColliderEvidence collider in
                         document.Colliders)
                    if (!AddPhysicsAsset(assets, assetPathAuthorities,
                            collider.Path, collider.Sha256, collider.ByteLength,
                            request.Provider, diagnostics))
                        return Refused(diagnostics);
            }

            diagnostics.Add(new Diagnostic(
                "external-headpart-physics-binding-resolved",
                DiagnosticSeverity.Info,
                $"Resolved {orderedShapes.Length} external headpart physics shape binding(s) using {mode} without copying provider model bytes."));
            return new ExternalHeadPartPhysicsBindingResult(
                true,
                new ExternalHeadPartPhysicsBinding(
                    mode,
                    orderedShapes,
                    mappingAuthority),
                assets.ToImmutable(),
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidDataException or
            IOException or UnauthorizedAccessException or ArgumentException)
        {
            diagnostics.Add(Error(
                "external-headpart-physics-refused",
                $"External headpart physics binding was refused: {exception.Message}"));
            return Refused(diagnostics);
        }
    }

    private static void ValidateRequest(
        ExternalHeadPartPhysicsBindingRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.PhysicsBinding is { } disposition && !Enum.IsDefined(disposition))
            diagnostics.Add(Error("external-headpart-physics-disposition", "The physics binding disposition is undefined."));
        if (!Enum.IsDefined(request.Provider.RedistributionMode))
            diagnostics.Add(Error(
                "external-headpart-physics-provider",
                "The external provider redistribution mode is undefined."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            !request.PluginOrder.Any(item => string.Equals(
                item.Value,
                request.Provider.Plugin.Value,
                StringComparison.OrdinalIgnoreCase)))
            diagnostics.Add(Error(
                "external-headpart-physics-provider-order",
                "The supplied plugin order does not contain the declared physics provider."));
        if (request.ProviderMembers.IsDefaultOrEmpty)
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.PhysicsMissing,
                "At least one external headpart member is required for physics binding.") );

        var seenMembers = new HashSet<string>(StringComparer.Ordinal);
        foreach (ExternalHeadPartRecordDependency member in request.ProviderMembers)
        {
            if (member.EffectiveType != NpcHeadPartType.Hair ||
                !string.Equals(member.WinningPlugin.Value,
                    request.Provider.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase) ||
                member.WinningPluginSha256 != request.Provider.PluginSha256 ||
                member.WinningPluginByteLength != request.Provider.PluginByteLength)
                diagnostics.Add(Error(
                    "external-headpart-physics-provider",
                    $"Member '{member.OriginForm}' is not bound to the declared external hair provider."));
            if (member.ModelNif is not { } model ||
                !ExternalHeadPartPhysicsPathRules.TryCreateAssetPath(
                    model.Value,
                    "meshes/",
                    ".nif",
                    out _))
                diagnostics.Add(Error(
                    "external-headpart-physics-model-path",
                    $"Member '{member.OriginForm}' does not declare a safe meshes-relative NIF model."));
            else if (!seenMembers.Add(member.OriginForm.ToString() + "\0" +
                         model.Value))
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.PhysicsAmbiguous,
                    $"Member/model tuple '{member.OriginForm}|{model.Value}' is duplicated."));
        }
    }

    private static bool AddPhysicsAsset(
        ImmutableArray<ExternalHeadPartAssetDependency>.Builder assets,
        Dictionary<string,
            (string Spelling, Sha256Hash Sha256, long ByteLength,
                PluginName ProviderPlugin, Sha256Hash ProviderSha256)> pathAuthorities,
        AssetPath path,
        Sha256Hash sha256,
        long byteLength,
        ExternalHeadPartProviderIdentity provider,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (pathAuthorities.TryGetValue(path.Value,
                out var existing))
        {
            if (!string.Equals(existing.Spelling, path.Value,
                    StringComparison.Ordinal))
            {
                diagnostics.Add(Error(
                    "external-headpart-physics-asset-path-collision",
                    $"Physics asset paths '{existing.Spelling}' and '{path.Value}' collide case-insensitively."));
                return false;
            }

            if (existing.Sha256 != sha256 ||
                existing.ByteLength != byteLength ||
                existing.ProviderPlugin != provider.Plugin ||
                existing.ProviderSha256 != provider.PluginSha256)
            {
                diagnostics.Add(Error(
                    "external-headpart-physics-asset-evidence-drift",
                    $"Repeated physics asset path '{path.Value}' carried inconsistent hash, length, or provider evidence."));
                return false;
            }
            return true;
        }

        pathAuthorities.Add(path.Value, (
            path.Value,
            sha256,
            byteLength,
            provider.Plugin,
            provider.PluginSha256));
        assets.Add(new ExternalHeadPartAssetDependency(
            path,
            sha256,
            byteLength,
            provider.Plugin,
            provider.PluginSha256,
            null));
        return true;
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static ExternalHeadPartPhysicsBindingResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, ImmutableArray<ExternalHeadPartAssetDependency>.Empty,
            diagnostics.ToImmutable());

    private sealed record ProviderModelObservation(
        FormReference MemberForm,
        AssetPath ModelNif,
        ImmutableArray<string> ShapeNames,
        ImmutableArray<string> PhysicsObjectLocators);

    private readonly record struct ShapeBindingKey(
        FormReference MemberForm,
        AssetPath ModelNif,
        string ShapeName);
}
