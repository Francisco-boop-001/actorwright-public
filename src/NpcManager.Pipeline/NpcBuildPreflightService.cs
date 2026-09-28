using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed class NpcBuildPreflightService(
    IRaceMenuNpcExecutionRequestFileLoader requestLoader,
    IRaceMenuNpcAppearancePlanService appearancePlanner,
    IRaceMenuNpcStandaloneAuthorityReader standaloneReader,
    ISkyrimFaceMorphSnapshotService faceMorphSnapshotService,
    IPresetService presetService,
    ISkyrimFaceRecordPluginAuthorityLoader pluginAuthorityLoader,
    Func<PreviewDependencyPreflightResult> previewPreflight,
    INpcBuildPreflightDocumentCodec documentCodec,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot,
    NpcBuildPreflightDependencyClosureService? dependencyClosureService = null,
    ISkyrimFaceBakeAuthorityDerivationService? faceBakeDerivationService = null) : INpcBuildPreflightService
{
    public async ValueTask<NpcBuildPreflightResult> CreateAsync(
        NpcBuildPreflightRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();

        RaceMenuNpcExecutionRequestFileLoadResult loaded =
            await requestLoader.LoadAsync(
                new RaceMenuNpcExecutionRequestFileLoadRequest(
                    request.SourceRequest,
                    request.SourceRequestSha256),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(loaded.Diagnostics);
        if (!loaded.Loaded || loaded.Request is null)
            return Refused(diagnostics);

        PresetParseResult preset = await presetService.InspectAsync(
            new PresetParseRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                request.Preset),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(preset.Diagnostics);
        bool presetPassed = preset.Document is { IsValid: true } document &&
                            document.SourceHash == request.ExpectedPresetSha256;

        SkyrimFaceRecordPluginAuthorityResult? plugins = null;
        bool pluginsPassed = true;
        if (request.DataRoot is { } dataRoot)
        {
            plugins = await pluginAuthorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    dataRoot,
                    request.PluginOrder),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(plugins.Diagnostics);
            pluginsPassed = plugins.Accepted &&
                            plugins.Authorities.Length ==
                            request.PluginOrder.Length;
        }

        SkyrimFaceBakeAuthorityDerivationArtifact? derivedFaceBake = null;
        if (request.FaceBakeAuthorityOutput is { } faceBakeOutput)
        {
            if (request.Output is not { } preflightOutput || preflightOutput == faceBakeOutput ||
                !ValidateOutputTarget(request, faceBakeOutput, diagnostics) ||
                !ValidateOutputTarget(request, preflightOutput, diagnostics))
            {
                diagnostics.Add(Error("face-bake-derivation-output", "Derivation requires distinct fresh authority and preflight outputs."));
                return Refused(diagnostics);
            }
            if (faceBakeDerivationService is null || request.DataRoot is null)
            {
                diagnostics.Add(Error("face-bake-derivation-unavailable", "Derivation requires the production service and copied Data/plugin order."));
                return Refused(diagnostics);
            }
            var provider = request.EffectiveRequest.Build.ProviderContext;
            ProviderResourceAuthority carrier = provider.ProviderResources?.FaceGeomCarrier ??
                new WorkspaceProviderResourceAuthority("facegeom-carrier", provider.FaceGeomCarrier, provider.ExpectedFaceGeomCarrierSha256);
            var derived = await faceBakeDerivationService.DeriveAsync(new(labRoot,
                request.Preset, request.ExpectedPresetSha256, request.DataRoot.Value, request.PluginOrder,
                request.EffectiveRequest.Build.References.Race, request.EffectiveRequest.Build.Traits.Sex,
                carrier, faceBakeOutput), cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(derived.Diagnostics);
            derivedFaceBake = derived.Artifact;
        }

        RaceMenuNpcAppearancePlanResult plan =
            await appearancePlanner.AnalyzeAsync(
                request.EffectiveRequest.Build,
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(plan.Diagnostics);
        RaceMenuNpcStandaloneAuthorityReadResult assets =
            await standaloneReader.ReadAsync(
                request.EffectiveRequest.AssetAuthority,
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(assets.Diagnostics);

        SkyrimFaceMorphSnapshotResult? morphSnapshot = null;
        if (assets.Accepted && assets.Assets is { } standaloneAssets)
        {
            morphSnapshot = await faceMorphSnapshotService.ReadAsync(
                new SkyrimFaceMorphSnapshotRequest(
                    GameEdition.SkyrimSpecialEdition,
                    standaloneAssets.Nam9Authority.Plugin,
                    standaloneAssets.Nam9Authority.ExpectedPluginSha256,
                    standaloneAssets.Nam9Authority.NpcFormId),
                cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(morphSnapshot.Diagnostics);
        }

        ImmutableArray<NpcBuildPreflightGate> required = BuildRequiredGates(
            loaded.Loaded,
            presetPassed,
            pluginsPassed,
            plan,
            assets,
            morphSnapshot,
            request,
            diagnostics);
        ImmutableArray<NpcBuildPreflightDependency>? dependencies = null;
        if (dependencyClosureService is not null &&
            request.DataRoot is not null && preset.Document is { IsValid: true } selectedPreset)
        {
            var observed = await dependencyClosureService.CollectAsync(
                request, selectedPreset, plugins?.Authorities ?? [],
                plan.Plan, assets.Assets,
                cancellationToken).ConfigureAwait(false);
            dependencies = observed.Dependencies;
            diagnostics.AddRange(observed.Diagnostics);
            required = required.Add(new NpcBuildPreflightGate(
                "dependency-closure", true,
                observed.Dependencies.All(item => item.Status == "present" ||
                    item.Disposition is "platformProvided" or "optionalUnavailable") &&
                !observed.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
                "Read-only selected and base-race assets, tint dispositions, catalogs and physics bindings resolve before build staging." +
                string.Concat(observed.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)
                    .Select(item => $" {item.Code}: {item.Message}"))));
        }
        if (request.FaceBakeAuthorityOutput is not null)
            required = required.Add(new("derived-face-bake-binding", true, false,
                "Bind the emitted authority path/SHA in standaloneAssets.faceBakeAuthority, update the execution request's standalone hash, and run ordinary preflight before build."));
        PreviewDependencyPreflightResult preview = previewPreflight();
        ImmutableArray<NpcBuildPreflightGate> optional =
            BuildPreviewGates(preview);
        bool ready = required.All(item => item.Required && item.Passed);
        bool previewReady = optional.All(item => !item.Required && item.Passed);

        var authorities = ImmutableArray.CreateBuilder<NpcBuildPreflightAuthority>();
        AddWorkspace(authorities, "execution-request", request.SourceRequest,
            request.SourceRequestSha256);
        AddWorkspace(authorities, "selected-preset", request.Preset,
            request.ExpectedPresetSha256);
        AddWorkspace(authorities, "preset-bundle",
            request.EffectiveRequest.Build.PresetBundle.ManifestPath,
            request.EffectiveRequest.Build.PresetBundle.ExpectedManifestSha256);
        AddWorkspace(authorities, "CharGen-FaceGeom",
            request.EffectiveRequest.Build.PresetBundle.CharGenFaceGeom,
            request.EffectiveRequest.Build.PresetBundle.ExpectedCharGenFaceGeomSha256);
        AddWorkspace(authorities, "CharGen-FaceTint",
            request.EffectiveRequest.Build.PresetBundle.CharGenFaceTint,
            request.EffectiveRequest.Build.PresetBundle.ExpectedCharGenFaceTintSha256);
        AddWorkspace(authorities, "record-authority",
            request.EffectiveRequest.Build.PresetBundle.RecordAuthority.ManifestPath,
            request.EffectiveRequest.Build.PresetBundle.RecordAuthority.ExpectedManifestSha256);
        AddWorkspace(authorities, "standalone-assets",
            request.EffectiveRequest.AssetAuthority.ManifestPath,
            request.EffectiveRequest.AssetAuthority.ExpectedManifestSha256);
        AddProviderAuthorities(authorities,
            request.EffectiveRequest.Build.ProviderContext,
            plan.Plan?.Provider);
        if (derivedFaceBake is { } derivedAuthority)
            AddWorkspace(authorities, "derived-face-bake-authority", derivedAuthority.Path, derivedAuthority.Sha256);
        if (plugins is not null)
            foreach (SkyrimFaceRecordPluginAuthority authority in
                     plugins.Authorities)
                AddWorkspace(authorities, "winning-plugin:" +
                    authority.Plugin.Value, authority.Path,
                    authority.ExpectedSha256);

        ImmutableArray<NpcBuildPreflightAuthority> closure =
            BuildClosure(request, plan.Plan, assets.Assets);
        ImmutableArray<NpcBuildPreflightHeadPart> headParts =
            BuildHeadParts(plan.Plan);
        ImmutableArray<NpcBuildPreflightAppearanceFact> appearance =
            BuildAppearance(plan.Plan);
        ImmutableArray<NpcBuildPreflightPlannedOutput> outputs =
            BuildOutputs(request.EffectiveRequest.Build);
        var artifact = new NpcBuildPreflightArtifact(
            NpcBuildPreflightSchemas.Artifact,
            BuildInfo.ProductName,
            BuildInfo.ProductVersion,
            BuildInfo.SourceLine,
            ExecutableHash(),
            NpcBuildPreflightSchemas.DerivationVersion,
            request.SourceRequest,
            request.SourceRequestSha256,
            request.ExpectedPresetSha256,
            request.EffectiveRequest.Build.References.Race.ToString(),
            request.EffectiveRequest.Build.Traits.Sex.ToString(),
            (plugins?.Authorities.Select(item => item.Plugin.Value) ??
             request.PluginOrder.Select(item => item.Value)).ToImmutableArray(),
            authorities.ToImmutable(),
            headParts,
            appearance,
            closure,
            required,
            optional,
            outputs,
            ready,
            previewReady,
            RuntimeAuthority: false,
            DependencyClosure: dependencies);
        NpcBuildPreflightDocument encoded = documentCodec.Encode(artifact);
        if (request.Output is { } output)
        {
            if (!ValidateOutputTarget(request, output, diagnostics))
                return Refused(diagnostics);
            try
            {
                encoded = await documentCodec.WriteNewAsync(
                    encoded, output, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or
                UnauthorizedAccessException or InvalidDataException or
                ArgumentException or NotSupportedException)
            {
                diagnostics.Add(Error("npc-build-preflight-write",
                    exception.Message));
                return Refused(diagnostics);
            }
        }
        return new NpcBuildPreflightResult(
            true,
            ready,
            encoded,
            diagnostics.ToImmutable(),
            derivedFaceBake);
    }

    public async ValueTask<NpcBuildPreflightResult> VerifyReviewedAsync(
        NpcBuildPreflightRequest request,
        NpcBuildPreflightReviewAuthority reviewed,
        CancellationToken cancellationToken)
    {
        try
        {
            NpcBuildPreflightDocument prior = await documentCodec.ReadExactAsync(
                reviewed.Path, reviewed.ExpectedSha256, cancellationToken)
                .ConfigureAwait(false);
            NpcBuildPreflightResult current = await CreateAsync(
                request with { Output = null }, cancellationToken)
                .ConfigureAwait(false);
            if (!current.Created || current.Document is null)
                return current;
            if (!prior.Utf8Json.AsSpan().SequenceEqual(
                    current.Document.Utf8Json.AsSpan()))
            {
                return new NpcBuildPreflightResult(
                    false,
                    false,
                    current.Document,
                    current.Diagnostics.Add(Error(
                        "preflight-derived-plan-stale",
                        "The complete current production-derived preflight no longer byte-matches the reviewed artifact.")));
            }
            return current;
        }
        catch (Exception exception) when (exception is IOException or
            UnauthorizedAccessException or InvalidDataException or
            ArgumentException or NotSupportedException or JsonException)
        {
            return new NpcBuildPreflightResult(
                false,
                false,
                null,
                [Error("preflight-derived-plan-stale",
                    exception.Message)]);
        }
    }

    private ImmutableArray<NpcBuildPreflightGate> BuildRequiredGates(
        bool requestPassed,
        bool presetPassed,
        bool pluginsPassed,
        RaceMenuNpcAppearancePlanResult plan,
        RaceMenuNpcStandaloneAuthorityReadResult assets,
        SkyrimFaceMorphSnapshotResult? morphSnapshot,
        NpcBuildPreflightRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        bool outputPassed = ValidateBuildRoots(request, diagnostics);
        bool appearancePassed = plan.Accepted && plan.Plan is { IsReady: true };
        bool assetsPassed = assets.Accepted && assets.Assets is not null;
        bool morphAuthorityPassed = false;
        string morphAuthorityDetail =
            "The standalone authority did not resolve a hash-bound NAM9 payload.";
        if (assetsPassed && morphSnapshot is
                { Resolved: true, Snapshot: { HasNam9: true } snapshot })
        {
            int expectedBits = BitConverter.SingleToInt32Bits(
                assets.Assets!.Nam9Authority.ExpectedTrailingValue);
            int actualBits = BitConverter.SingleToInt32Bits(
                snapshot.Nam9Trailing);
            morphAuthorityPassed = expectedBits == actualBits;
            morphAuthorityDetail = morphAuthorityPassed
                ? "The production snapshot reader reopened standalone NAM9 and matched the reviewed engine-owned trailing value by raw IEEE-754 bits."
                : $"The reopened engine-owned NAM9 trailing value differs by raw IEEE-754 bits (expected 0x{expectedBits:X8}, observed 0x{actualBits:X8}).";
        }
        else if (assetsPassed && morphSnapshot is { Resolved: true })
        {
            morphAuthorityDetail =
                "The production snapshot reader resolved the standalone authority without a NAM9 payload.";
        }
        bool geometryPassed = assetsPassed && appearancePassed &&
                              plan.Plan!.Provider.ExpectedShapeNames.Length > 0;
        bool closurePassed = assetsPassed &&
                             !assets.Assets!.PackageAssets.IsDefault &&
                             !assets.Assets.ExternalTextureAuthorities.IsDefault;
        return
        [
            new("request-authority", true, requestPassed,
                "The exact execution-request bytes load through the production loader."),
            new("preset-authority", true, presetPassed,
                "The selected JSlot parses and matches its declared SHA-256."),
            new("winning-records", true, pluginsPassed,
                request.DataRoot is null
                    ? "Winning plugin order is already bound by the prepared request."
                    : "The copied Data plugin order reopens through the production record loader."),
            new("appearance-plan", true, appearancePassed,
                "Provider appearance and final record authority resolve through the production planner."),
            new("asset-authority", true, assetsPassed,
                "The standalone NIF, TRI, DDS, and package authorities reopen through the production asset reader."),
            new("native-morph-authority", true, morphAuthorityPassed,
                morphAuthorityDetail),
            new("facegeom-codec", true, geometryPassed,
                $"Shared carrier codec and packed-normal policy {SseSelectedHeadpartPackedNormalPolicy.Version} remain required for final FaceGeom."),
            new("final-dependency-closure", true, closurePassed,
                "The final NIF/TRI/DDS closure is explicit and hash-bound."),
            new("output-policy", true, outputPassed,
                "Final and companion roots are absent, ordinary, same-volume, and disjoint.")
        ];
    }

    private bool ValidateBuildRoots(
        NpcBuildPreflightRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        WorkspacePath final = request.EffectiveRequest.Build.OutputRoot;
        var preview = new WorkspacePath(final.Value + "-preview");
        diagnostics.AddRange(workspacePolicy.Evaluate(labRoot, final));
        diagnostics.AddRange(workspacePolicy.Evaluate(labRoot, preview));
        bool valid = final.IsUnder(labRoot) && final != labRoot &&
                     !File.Exists(final.Value) && !Directory.Exists(final.Value) &&
                     preview.IsUnder(labRoot) && preview != labRoot &&
                     !File.Exists(preview.Value) &&
                     !Directory.Exists(preview.Value);
        if (request.CompanionRoot is { } companion)
        {
            diagnostics.AddRange(workspacePolicy.Evaluate(labRoot, companion));
            valid = valid && companion.IsUnder(labRoot) && companion != labRoot &&
                    companion != final && !companion.IsUnder(final) &&
                    !final.IsUnder(companion) &&
                    !File.Exists(companion.Value) &&
                    !Directory.Exists(companion.Value) &&
                    string.Equals(Path.GetPathRoot(companion.Value),
                        Path.GetPathRoot(final.Value),
                        StringComparison.OrdinalIgnoreCase);
        }
        if (!valid)
            diagnostics.Add(Error("npc-build-preflight-output-policy",
                "The planned final and companion roots are not new, disjoint, same-volume workspace paths."));
        // Blank route only: the same predicate the blank writer applies last is
        // evaluated here first, so an unsupported output kind never reaches
        // companion or native FaceGen work. Existing-NPC edits keep their kind.
        RaceMenuNpcBuildRequest build = request.EffectiveRequest.Build;
        if (build.ExistingNpcTarget is null)
        {
            ImmutableArray<Diagnostic> pluginType = BlankNpcOutputPolicy.Evaluate(
                build.OutputPlugin, build.PluginType, "npc-build-preflight-plugin-type");
            diagnostics.AddRange(pluginType);
            valid = valid && pluginType.IsEmpty;
        }
        return valid;
    }

    private bool ValidateOutputTarget(
        NpcBuildPreflightRequest request,
        WorkspacePath output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(workspacePolicy.Evaluate(labRoot, output));
        WorkspacePath final = request.EffectiveRequest.Build.OutputRoot;
        bool valid = output.IsUnder(labRoot) && output != labRoot &&
                     output.Value.EndsWith(".json", StringComparison.OrdinalIgnoreCase) &&
                     !File.Exists(output.Value) && !Directory.Exists(output.Value) &&
                     output != request.SourceRequest && output != request.Preset &&
                     !output.IsUnder(final) && !final.IsUnder(output);
        if (request.CompanionRoot is { } companion)
            valid = valid && !output.IsUnder(companion) &&
                    !companion.IsUnder(output);
        if (!valid)
            diagnostics.Add(Error("npc-build-preflight-output-target",
                "The preflight target must be a new JSON file outside source, final, and companion paths."));
        return valid;
    }

    private static ImmutableArray<NpcBuildPreflightGate> BuildPreviewGates(
        PreviewDependencyPreflightResult preview)
    {
        var gates = ImmutableArray.CreateBuilder<NpcBuildPreflightGate>();
        foreach (PreviewDependencyAuthority authority in preview.Authorities)
            gates.Add(new NpcBuildPreflightGate(
                "preview:" + Slug(authority.Role),
                false,
                true,
                $"{authority.Role} admitted at {authority.ExpectedPath}."));
        foreach (Diagnostic diagnostic in preview.Diagnostics.Where(item =>
                     item.Severity == DiagnosticSeverity.Error))
            gates.Add(new NpcBuildPreflightGate(
                "preview:" + diagnostic.Code,
                false,
                false,
                diagnostic.Message));
        if (gates.Count == 0)
            gates.Add(new NpcBuildPreflightGate(
                "preview:dependency-preflight",
                false,
                preview.Accepted,
                "Preview dependency preflight returned no component rows."));
        return gates.ToImmutable();
    }

    private static ImmutableArray<NpcBuildPreflightHeadPart> BuildHeadParts(
        RaceMenuNpcAppearancePlan? plan)
    {
        if (plan is null) return [];
        return plan.HeadPartDispositions.Select((item, index) =>
        {
            RaceMenuNpcFormBinding? binding = item.MappedHeadPart?.Binding;
            string disposition = item.Kind.ToString();
            return new NpcBuildPreflightHeadPart(
                index,
                item.Source.Type.ToString(),
                DescribeHeadPartType(binding) ?? "baked",
                disposition,
                binding?.ProviderPluginName.Value ?? "CharGen FaceGeom",
                item.Kind == RaceMenuNpcHeadPartDispositionKind.Baked
                    ? "baked into admitted final carrier"
                    : "record-mapped with provider authority",
                "production asset planner admitted",
                "hash-bound by final FaceGeom authority",
                $"shared-codec policy {SseSelectedHeadpartPackedNormalPolicy.Version}");
        }).ToImmutableArray();
    }

    /// <summary>Reports the raw PNAM for mod-defined HDPT types instead of the Misc projection.</summary>
    private static string? DescribeHeadPartType(RaceMenuNpcFormBinding? binding) =>
        binding is null
            ? null
            : binding.HasExtendedHeadPartType
                ? binding.HeadPartRawType.ToString()
                : binding.HeadPartType?.ToString();

    private static ImmutableArray<NpcBuildPreflightAppearanceFact>
        BuildAppearance(RaceMenuNpcAppearancePlan? plan)
    {
        if (plan is null) return [];
        var rows = ImmutableArray.CreateBuilder<NpcBuildPreflightAppearanceFact>();
        rows.Add(new("provider", plan.Provider.ProviderId,
            plan.Provider.ProviderId, plan.Provider.ManifestSha256.Value));
        rows.Add(new("hair-color", "provider template",
            plan.ResolvedHairColor switch
            {
                RaceMenuNpcExternalHairColorAuthority external =>
                    $"0x{external.PackedRgb:X6} via {external.Binding.Reference}",
                RaceMenuNpcOutputOwnedHairColorAuthority owned =>
                    $"0x{owned.PackedRgb:X6} output-owned",
                _ => "absent"
            },
            plan.RecordAuthoritySha256.Value));
        rows.Add(new("head-texture", "provider template",
            plan.ResolvedHeadTexture?.Reference.ToString() ?? "preserved",
            plan.RecordAuthoritySha256.Value));
        rows.Add(new("face-tints", "provider FaceTint source",
            $"{plan.ResolvedTintLayers.Length} record-mapped; " +
            $"{plan.TintDispositions.Count(item => item.Kind == RaceMenuNpcTintDispositionKind.Baked)} baked",
            plan.CharGenFaceTintSha256.Value));
        foreach (RaceMenuNpcHeadPartDisposition part in plan.HeadPartDispositions)
        {
            string type = DescribeHeadPartType(part.MappedHeadPart?.Binding) ??
                          part.Source.Type.ToString();
            if (type.Contains("Eye", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("Brow", StringComparison.OrdinalIgnoreCase) ||
                type.Contains("Hair", StringComparison.OrdinalIgnoreCase))
                rows.Add(new(type.ToLowerInvariant(), "provider template",
                    part.MappedHeadPart?.Binding.Reference.ToString() ??
                    "baked CharGen geometry",
                    part.MappedHeadPart?.Binding.ProviderPluginSha256.Value ??
                    plan.CharGenFaceGeomSha256.Value));
        }
        return rows.ToImmutable();
    }

    private static ImmutableArray<NpcBuildPreflightAuthority> BuildClosure(
        NpcBuildPreflightRequest request,
        RaceMenuNpcAppearancePlan? plan,
        RaceMenuNpcStandaloneAssets? assets)
    {
        var closure = ImmutableArray.CreateBuilder<NpcBuildPreflightAuthority>();
        AddWorkspace(closure, "final-CharGen-NIF",
            request.EffectiveRequest.Build.PresetBundle.CharGenFaceGeom,
            request.EffectiveRequest.Build.PresetBundle.ExpectedCharGenFaceGeomSha256);
        AddWorkspace(closure, "final-CharGen-DDS",
            request.EffectiveRequest.Build.PresetBundle.CharGenFaceTint,
            request.EffectiveRequest.Build.PresetBundle.ExpectedCharGenFaceTintSha256);
        if (plan is not null)
            foreach (NpcCreationPluginAuthority authority in plan.PluginAuthorities)
                AddWorkspace(closure, "record-provider:" +
                    authority.Plugin.Value, authority.PluginPath,
                    authority.ExpectedSha256);
        if (assets is not null)
        {
            foreach (BlankNpcTransitivePackageAsset asset in assets.PackageAssets)
                AddWorkspace(closure, "package:" + asset.Destination.Value,
                    asset.Source, asset.ExpectedSha256);
            foreach (RaceMenuNpcExternalTextureAuthority asset in
                     assets.ExternalTextureAuthorities)
                AddWorkspace(closure, "external-texture:" +
                    asset.DataRelativePath.Value, asset.Source,
                    asset.ExpectedSourceSha256);
            if (assets.FinalOutputAuthority is { } final)
            {
                AddWorkspace(closure, "qualified-final-NIF",
                    final.FaceGeom, final.FaceGeomSha256);
                AddWorkspace(closure, "qualified-final-DDS",
                    final.FaceTint, final.FaceTintSha256);
            }
        }
        AddProviderAuthorities(closure,
            request.EffectiveRequest.Build.ProviderContext,
            plan?.Provider);
        return closure.ToImmutable();
    }

    private static ImmutableArray<NpcBuildPreflightPlannedOutput> BuildOutputs(
        RaceMenuNpcBuildRequest build)
    {
        string root = build.OutputRoot.Value;
        string plugin = build.OutputPlugin.Value;
        string faceOwner = build.ExistingNpcTarget is { } existing
            ? Path.GetFileName(existing.SourcePlugin.Value)
            : plugin;
        string faceFormId = build.ExistingNpcTarget is { } target
            ? $"{target.TargetFormId.Value:X8}"
            : "00000800";
        string previewRoot = root + "-preview";
        var outputs = ImmutableArray.CreateBuilder<NpcBuildPreflightPlannedOutput>();
        outputs.Add(new("plugin", Path.Combine(root, "Data", plugin)));
        outputs.Add(new("facegeom", Path.Combine(root, "Data", "meshes",
            "actors", "character", "FaceGenData", "FaceGeom", faceOwner,
            faceFormId + ".nif")));
        outputs.Add(new("facetint", Path.Combine(root, "Data", "textures",
            "actors", "character", "FaceGenData", "FaceTint", faceOwner,
            faceFormId + ".dds")));
        outputs.Add(new("package-manifest",
            Path.Combine(root, "npcmanager-package.json")));
        foreach (string view in new[] { "face-front", "face-left", "face-right",
                     "face-alternate-light", "body-front", "body-back" })
            outputs.Add(new("preview:" + view,
                Path.Combine(previewRoot, view + ".png")));
        return outputs.ToImmutable();
    }

    private static void AddProviderAuthorities(
        ImmutableArray<NpcBuildPreflightAuthority>.Builder rows,
        BlankNpcProviderBindingRequest provider,
        BlankNpcProviderArtifact? qualifiedProvider)
    {
        if (provider.ProviderResources is { } resources)
        {
            foreach (ProviderResourceAuthority resource in resources.Files)
                rows.Add(new NpcBuildPreflightAuthority(
                    "provider:" + resource.Role,
                    "application",
                    resource is ApplicationProviderResourceAuthority application
                        ? $"{application.BundleId}:{application.LogicalRole}:" +
                          application.RegistryManifestSha256.Value
                        : resource.Role,
                    resource.ExpectedSha256));
        }
        else
        {
            AddWorkspace(rows, "provider:manifest", provider.ManifestPath,
                provider.ExpectedManifestSha256);
            AddWorkspace(rows, "provider:template-plugin", provider.TemplatePlugin,
                provider.ExpectedTemplatePluginSha256);
            AddWorkspace(rows, "provider:facegeom-carrier", provider.FaceGeomCarrier,
                provider.ExpectedFaceGeomCarrierSha256);
            if (qualifiedProvider is not null)
            {
                AddWorkspace(rows, "provider:facetint-manifest",
                    provider.FaceTintManifest,
                    qualifiedProvider.FaceTintManifestSha256);
                AddWorkspace(rows, "provider:dependency-manifest",
                    provider.DependencyManifest,
                    qualifiedProvider.DependencyManifestSha256);
            }
        }
    }

    private static void AddWorkspace(
        ImmutableArray<NpcBuildPreflightAuthority>.Builder rows,
        string role,
        WorkspacePath path,
        Sha256Hash sha256) => rows.Add(new NpcBuildPreflightAuthority(
            role, "workspace", path.Value, sha256));

    private static Sha256Hash? ExecutableHash()
    {
        string? path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
            !(Path.GetFileName(path).Equals("actorwright.exe",
                  StringComparison.OrdinalIgnoreCase) ||
              Path.GetFileName(path).Equals("Actorwright.Desktop.exe",
                  StringComparison.OrdinalIgnoreCase)))
            return null;
        using FileStream stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string Slug(string value) => string.Concat(value.Select(ch =>
        char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-'));

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static NpcBuildPreflightResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, false, null, diagnostics.ToImmutable());
}
