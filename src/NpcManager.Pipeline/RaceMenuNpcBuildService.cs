using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Composes the admitted RaceMenu plan into the proven fresh-NPC transaction.
/// All preset and authority decisions happen before the final output root is
/// created; the service never performs a second plugin mutation.
/// </summary>
public sealed partial class RaceMenuNpcBuildService(
    IRaceMenuNpcAppearancePlanService planService,
    ISkyrimFaceMorphSnapshotService faceMorphSnapshotService,
    IBodyGenService bodyGenService,
    IBlankNpcBuildService blankNpcBuildService,
    IAssetIndexer assetIndexer,
    IWorkspacePolicy policy,
    WorkspacePath labRoot,
    IRaceMenuNpcFaceGeomBuildService? faceGeomBuildService = null,
    IRaceMenuNpcFaceTextureBuildService? faceTextureBuildService = null,
    IQualifiedFaceGeomCarrierService? qualifiedFaceGeomCarrierService = null,
    IFaceTintTextureDecoder? exactFaceTintEvidenceDecoder = null,
    IExistingNpcAppearanceBuildService? existingNpcBuildService = null,
    IRaceMenuDirectCharGenFaceGeomBuildService? directCharGenFaceGeomBuildService = null,
    IRaceMenuNpcWholeSkinAuthorityReader? wholeSkinAuthorityReader = null)
    : RaceMenuNpcStandaloneAuthorityReader(assetIndexer, policy, labRoot),
      IRaceMenuNpcBuildService
{
    public async ValueTask<RaceMenuNpcExecutionResult> ExecuteAsync(
        RaceMenuNpcExecutionRequest request,
        IProgress<BlankNpcBuildProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        bool outputBindingProbe = RaceMenuJslotProbeToken.IsValid(
            request.JslotOutputBindingProbe);
        if (RaceMenuJslotProbeToken.IsInvalid(request.JslotOutputBindingProbe))
        {
            diagnostics.Add(Error(
                "racemenu-build-unknown-probe-token",
                "The build service received an unknown JSlot probe token."));
            return Refused(null, null, null, null, null, null, diagnostics);
        }

        var assets = await ReadStandaloneAssetsAsync(
            request.AssetAuthority, diagnostics, cancellationToken);
        if (assets is null || HasErrors(diagnostics))
            return Refused(null, assets, null, null, null, null, diagnostics);
        if (outputBindingProbe && assets.SchemaVersion != 8)
        {
            diagnostics.Add(Error(
                "racemenu-build-schema8-probe-route",
                "The internal JSlot output-binding probe is valid only for schema-8 external evidence."));
            return Refused(null, assets, null, null, null, null, diagnostics);
        }

        var planResult = await planService.AnalyzeAsync(request.Build, cancellationToken);
        diagnostics.AddRange(planResult.Diagnostics);
        if (!planResult.Accepted || planResult.Plan is not { IsReady: true } plan ||
            HasErrors(diagnostics))
            return Refused(planResult.Plan, assets, null, null, null, null, diagnostics);
        if (assets.SchemaVersion == 4 &&
            plan.TintDispositions.Any(item =>
                item.Kind == RaceMenuNpcTintDispositionKind.MappedRecord) &&
            plan.TintDispositions.Any(item =>
                item.Kind == RaceMenuNpcTintDispositionKind.Baked))
        {
            diagnostics.Add(Error("racemenu-build-schema4-mixed-tints",
                "SchemaVersion 4 cannot safely package mixed mapped and baked tints; use schemaVersion 5 product face-texture composition."));
            return Refused(plan, assets, null, null, null, null, diagnostics);
        }
        if (assets.SchemaVersion == 8 &&
            request.Build.ExistingNpcTarget is not null)
        {
            diagnostics.Add(Error("racemenu-build-schema8-new-npc-only",
                "SchemaVersion 8 is limited to the new-follower route; ExistingNpcTarget is not supported."));
            return Refused(plan, assets, null, null, null, null, diagnostics);
        }
        SkyrimNpcWholeSkinAuthority? wholeSkin = null;
        if (request.Build.ExistingNpcTarget is null || assets.BodyMeshAuthority is not null)
        {
            if (request.Build.WholeSkinAuthority is null)
            {
                diagnostics.Add(Error("racemenu-build-whole-skin-authority-required",
                    "A new preset-derived NPC or existing-target body-mesh replacement requires explicit hash-bound whole-skin authority; the JSlot does not own player race, WNAM, body, hand, or foot skin state."));
                return Refused(plan, assets, null, null, null, null, diagnostics);
            }
            if (wholeSkinAuthorityReader is null)
            {
                diagnostics.Add(Error("racemenu-build-whole-skin-service-unavailable",
                    "A new preset-derived NPC requires the whole-skin authority verifier before any output transaction starts."));
                return Refused(plan, assets, null, null, null, null, diagnostics);
            }
            RaceMenuNpcWholeSkinAuthorityReadResult skinResult =
                await wholeSkinAuthorityReader.ReadAsync(
                    new RaceMenuNpcWholeSkinAuthorityReadRequest(
                        request.Build.WholeSkinAuthority,
                        request.Build.References.Race,
                        request.Build.Traits.Sex,
                        request.Build.References.DefaultOutfit)
                    {
                        BodyMeshAuthority = assets.BodyMeshAuthority
                    },
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(skinResult.Diagnostics);
            if (!skinResult.Accepted || skinResult.Snapshot is null ||
                HasErrors(diagnostics))
                return Refused(plan, assets, null, null, null, null, diagnostics);
            wholeSkin = skinResult.Snapshot;
        }
        if (!ValidateStandaloneAuthorityMode(assets, diagnostics) ||
            !ValidateFinalOutputAuthorityBinding(request.Build, assets, diagnostics) ||
            !ValidateExternalCharGenExportBinding(
                request.Build, assets, diagnostics))
            return Refused(plan, assets, null, null, null, null, diagnostics);
        if (!ValidateSchemaDependencies(assets, diagnostics))
            return Refused(plan, assets, null, null, null, null, diagnostics);
        if (!ValidateSchema8AuthorityBinding(request, assets, diagnostics) ||
            !await VerifySchema8ManagerCarrierAsync(
                request, assets, diagnostics, cancellationToken))
            return Refused(plan, assets, null, null, null, null, diagnostics);
        var authorityResult = await faceMorphSnapshotService.ReadAsync(
            new SkyrimFaceMorphSnapshotRequest(
                GameEdition.SkyrimSpecialEdition,
                assets.Nam9Authority.Plugin,
                assets.Nam9Authority.ExpectedPluginSha256,
                assets.Nam9Authority.NpcFormId),
            cancellationToken);
        diagnostics.AddRange(authorityResult.Diagnostics);
        if (!authorityResult.Resolved || authorityResult.Snapshot is not { HasNam9: true } snapshot ||
            snapshot.Nam9Trailing != assets.Nam9Authority.ExpectedTrailingValue)
        {
            diagnostics.Add(Error("racemenu-build-nam9-authority",
                "The hash-bound NPC did not contain the exact declared engine-owned NAM9 trailing value."));
            return Refused(plan, assets, null, null, null, null, diagnostics);
        }

        FullyAuthoredSkyrimNpcAppearanceSource appearance;
        ImmutableArray<NpcCreationPluginAuthority>
            creationPluginAuthorities;
        SkyrimNpcRuntimeAppearancePayload runtime;
        SkyrimNpcApplySseVmadPayload vmad;
        try
        {
            appearance = RaceMenuNpcCreationAppearanceMapper.Map(
                plan,
                snapshot.Nam9Trailing,
                assets.PrivateHeadTextures,
                preserveQualifiedFaceEditorId:
                    assets.ExternalCharGenExportAuthority is not null);
            appearance = AttachPrivateNakedSkinBinding(
                appearance, wholeSkin, assets.BodyMeshAuthority);
            appearance = AttachExposedOutfitSkinBinding(
                appearance, wholeSkin);
            creationPluginAuthorities =
                MergeCreationPluginAuthorities(
                    plan.PluginAuthorities,
                    appearance);
            runtime = RaceMenuNpcRuntimeAppearanceMapper.Map(
                plan, assets.OverlayDecisions,
                request.ApplyBodySlide && assets.BodyMeshAuthority is null);
            vmad = SkyrimNpcApplySseVmadAdapter.ToVmadPayload(runtime);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("racemenu-build-translation-refused", exception.Message));
            return Refused(plan, assets, null, null, null, null, diagnostics);
        }

        var packageAssets = BuildEvidenceAssets(request, plan, assets, diagnostics).ToBuilder();
        if (!ValidateStandaloneAssetCoverage(assets, runtime, packageAssets, diagnostics) ||
            HasErrors(diagnostics))
            return Refused(plan, assets, runtime, vmad, null, null, diagnostics);

        WorkspacePath? stagingRoot = null;
        WorkspacePath providerCarrier = request.Build.ProviderContext.FaceGeomCarrier;
        Sha256Hash providerCarrierSha256 =
            request.Build.ProviderContext.ExpectedFaceGeomCarrierSha256;
        BodyGenBuildResult? bodyGen = null;
        BlankNpcBuildResult? build = null;
        ExistingNpcAppearanceBuildResult? existingNpcBuild = null;
        RaceMenuNpcFaceGeomBuildArtifact? generatedFaceGeom = null;
        RaceMenuDirectCharGenFaceGeomBuildResult? directFaceGeom = null;
        WorkspacePath? schema8ManagerCarrier = null;
        RaceMenuNpcFaceTextureBuildArtifact? generatedFaceTextures = null;
        try
        {
            stagingRoot = CreateStagingRoot(request.Build.OutputRoot, diagnostics);
            if (stagingRoot is not null)
            {
                if (request.Build.ProviderContext.ProviderResources is { } productResources)
                {
                    WorkspacePath? materializedCarrier =
                        await MaterializeProductProviderCarrierAsync(
                            productResources, stagingRoot.Value, diagnostics,
                            cancellationToken);
                    if (materializedCarrier is not null)
                    {
                        providerCarrier = materializedCarrier.Value;
                        providerCarrierSha256 =
                            productResources.FaceGeomCarrier.ExpectedSha256;
                    }
                }
                try
                {
                    packageAssets.Add(await SkyrimApplySseProductAsset.MaterializeAsync(
                        stagingRoot.Value, cancellationToken));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception) when (exception is IOException or
                                                   UnauthorizedAccessException or
                                                   InvalidDataException or
                                                   ArgumentException or
                                                   OverflowException)
                {
                    diagnostics.Add(Error("racemenu-build-product-runtime-asset",
                        exception.Message));
                }
            }

            if (!HasErrors(diagnostics) &&
                request.Build.ExistingNpcTarget is null &&
                request.ApplyBodySlide &&
                assets.BodyMeshAuthority is null &&
                plan.Preset.Appearance.BodyMorphs.Count > 0)
            {
                var stagingData = new WorkspacePath(Path.Combine(stagingRoot!.Value.Value, "Data"));
                Directory.CreateDirectory(stagingData.Value);
                var morphs = plan.Preset.Appearance.BodyMorphs
                    .OrderBy(item => item.Key, StringComparer.Ordinal)
                    .Select(item => new BodyGenMorph(item.Key, item.Value))
                    .ToImmutableArray();
                bodyGen = await bodyGenService.BuildTypedAsync(
                    new BodyGenTypedBuildRequest(
                        GameEdition.SkyrimSpecialEdition,
                        request.Build.OutputPlugin,
                        new FormId(0x0000_0800),
                        Path.GetFileNameWithoutExtension(request.Build.OutputPlugin.Value),
                        morphs,
                        stagingData)
                    {
                        EditorId = request.Build.Identity.EditorId
                    },
                    cancellationToken);
                diagnostics.AddRange(bodyGen.Diagnostics);
                if (bodyGen.Written && !bodyGen.Files.IsDefaultOrEmpty)
                {
                    packageAssets.AddRange(bodyGen.Files.Select(file =>
                        new BlankNpcTransitivePackageAsset(
                            file.AbsolutePath, file.Sha256, file.RelativePath)));
                }
                else if (!HasErrors(diagnostics))
                {
                    diagnostics.Add(Error("racemenu-build-bodygen-incomplete",
                        "BodyGen did not produce the required typed sidecar files."));
                }
            }

            if (!HasErrors(diagnostics) && assets.SchemaVersion is 4 or 5)
            {
                if (faceGeomBuildService is null || assets.FaceBakeAuthority is not { } faceBake)
                {
                    diagnostics.Add(Error("racemenu-build-facegeom-service-unavailable",
                        $"SchemaVersion {assets.SchemaVersion} requires the concrete product FaceGeom build service and an exact face-bake authority."));
                }
                else
                {
                    var outputNif = new WorkspacePath(Path.Combine(
                        stagingRoot!.Value.Value, "product-facegeom.nif"));
                    var faceGeomResult = await faceGeomBuildService.BuildAsync(
                        new RaceMenuNpcFaceGeomBuildRequest(
                            plan,
                            snapshot,
                            plan.Preset.Appearance.OrderedCustomMorphs,
                            LaboratoryRoot,
                            faceBake.ManifestPath,
                            faceBake.ExpectedManifestSha256,
                            providerCarrier,
                            providerCarrierSha256,
                            request.Build.PresetBundle.CharGenFaceGeom,
                            request.Build.PresetBundle.ExpectedCharGenFaceGeomSha256,
                            stagingRoot.Value,
                            outputNif),
                        cancellationToken);
                    diagnostics.AddRange(faceGeomResult.Diagnostics);
                    if (!faceGeomResult.Written || !faceGeomResult.Verified ||
                        faceGeomResult.Artifact is null)
                    {
                        if (!HasErrors(diagnostics))
                            diagnostics.Add(Error("racemenu-build-facegeom-incomplete",
                                "The product FaceGeom bake did not return a written, independently verified artifact."));
                    }
                    else if (await AddFaceGeomEvidenceAssetsAsync(
                                 faceBake,
                                 faceGeomResult.Artifact,
                                 stagingRoot.Value,
                                 packageAssets,
                                 diagnostics,
                                 cancellationToken))
                    {
                        generatedFaceGeom = faceGeomResult.Artifact;
                    }
                }
            }

            if (!HasErrors(diagnostics) && assets.SchemaVersion is 6 or 7)
            {
                if (directCharGenFaceGeomBuildService is null)
                {
                    diagnostics.Add(Error("racemenu-build-direct-facegeom-service-unavailable",
                        $"SchemaVersion {assets.SchemaVersion} requires the direct CharGen-to-qualified-carrier composition service."));
                }
                else
                {
                    RaceMenuDirectFaceGeomCarrierSelection carrierSelection;
                    try
                    {
                        carrierSelection = RaceMenuDirectFaceGeomCarrierSelector.Select(
                            request.Build.PresetBundle.CharGenFaceGeom,
                            request.Build.PresetBundle.ExpectedCharGenFaceGeomSha256,
                            providerCarrier,
                            providerCarrierSha256,
                            request.ManagerOwnedFaceGeomCarrier,
                            assets.ExternalCharGenExportAuthority);
                    }
                    catch (InvalidDataException exception)
                    {
                        diagnostics.Add(Error(
                            "racemenu-build-direct-carrier-binding",
                            exception.Message));
                        carrierSelection = new RaceMenuDirectFaceGeomCarrierSelection(
                            providerCarrier,
                            providerCarrierSha256,
                            RequiresOwnedCopy: false);
                    }

                    WorkspacePath? selectedCarrier = HasErrors(diagnostics)
                        ? null
                        : await MaterializeDirectCarrierAsync(
                            carrierSelection,
                            stagingRoot!.Value,
                            diagnostics,
                            cancellationToken);
                    if (selectedCarrier is null || HasErrors(diagnostics))
                    {
                        diagnostics.Add(Error(
                            "racemenu-build-direct-carrier-unavailable",
                            "The selected direct FaceGeom carrier could not be materialized as a distinct hash-bound staging input."));
                    }
                    else
                    {
                        var outputNif = new WorkspacePath(Path.Combine(
                            stagingRoot!.Value.Value, "direct-chargen-facegeom.nif"));
                        directFaceGeom = await directCharGenFaceGeomBuildService.BuildAsync(
                            new RaceMenuDirectCharGenFaceGeomBuildRequest(
                                request.Build.PresetBundle.CharGenFaceGeom,
                                request.Build.PresetBundle.ExpectedCharGenFaceGeomSha256,
                                selectedCarrier.Value,
                                carrierSelection.SourceSha256,
                                outputNif)
                            {
                                QualificationProfile =
                                    carrierSelection.QualificationProfile
                            },
                            cancellationToken);
                        diagnostics.AddRange(directFaceGeom.Diagnostics);
                        if (!directFaceGeom.Written || !directFaceGeom.Verified ||
                            directFaceGeom.Artifact is null ||
                            directFaceGeom.IndependentVerification is not { Verified: true })
                        {
                            if (!HasErrors(diagnostics))
                                diagnostics.Add(Error("racemenu-build-direct-facegeom-incomplete",
                                    "Direct CharGen composition did not return a written, independently verified complete carrier."));
                        }
                        else
                        {
                            await AddDirectFaceGeomEvidenceAssetAsync(
                                directFaceGeom,
                                request.Build.PresetBundle.ExpectedCharGenFaceGeomSha256,
                                carrierSelection.SourceSha256,
                                stagingRoot.Value,
                                packageAssets,
                                diagnostics,
                                cancellationToken);
                        }
                    }
                }
            }

            if (!HasErrors(diagnostics) && assets.SchemaVersion == 8)
            {
                if (request.ManagerOwnedFaceGeomCarrier is not { } managerCarrier ||
                    stagingRoot is null)
                {
                    diagnostics.Add(Error(
                        "racemenu-build-schema8-manager-carrier-unavailable",
                        "SchemaVersion 8 requires a staging root and the validated Manager-owned FaceGeom carrier."));
                }
                else
                {
                    schema8ManagerCarrier =
                        await MaterializeSchema8ManagerCarrierAsync(
                            managerCarrier,
                            stagingRoot.Value,
                            diagnostics,
                            cancellationToken);
                }
            }

            if (!HasErrors(diagnostics) && assets.SchemaVersion == 5)
            {
                if (faceTextureBuildService is null ||
                    assets.FaceTextureBakeAuthority is not { } faceTextureBake)
                {
                    diagnostics.Add(Error("racemenu-build-face-texture-service-unavailable",
                        "SchemaVersion 5 requires the product face-texture composition service and exact authority."));
                }
                else
                {
                    var faceTintOutput = new WorkspacePath(Path.Combine(
                        stagingRoot!.Value.Value, "product-facetint.dds"));
                    var privateDiffuseOutput = new WorkspacePath(Path.Combine(
                        stagingRoot.Value.Value, "product-private-diffuse.dds"));
                    var evidenceOutput = new WorkspacePath(Path.Combine(
                        stagingRoot.Value.Value, "face-texture-composition.json"));
                    var privateDiffuseDestination = new AssetPath(
                        ToPackageTexturePath(assets.PrivateHeadTextures.Diffuse.Value));
                    var faceTextureResult = await faceTextureBuildService.BuildAsync(
                        new RaceMenuNpcFaceTextureBuildRequest(
                            plan,
                            LaboratoryRoot,
                            faceTextureBake,
                            assets.FaceTintWidth,
                            assets.FaceTintHeight,
                            privateDiffuseDestination,
                            stagingRoot.Value,
                            faceTintOutput,
                            privateDiffuseOutput,
                            evidenceOutput),
                        cancellationToken);
                    diagnostics.AddRange(faceTextureResult.Diagnostics);
                    if (!faceTextureResult.Written || !faceTextureResult.Verified ||
                        faceTextureResult.Artifact is null)
                    {
                        if (!HasErrors(diagnostics))
                            diagnostics.Add(Error("racemenu-build-face-texture-incomplete",
                                "Product face-texture composition did not return a written, independently verified artifact."));
                    }
                    else if (await AddFaceTextureEvidenceAssetsAsync(
                                 faceTextureBake,
                                 privateDiffuseDestination,
                                 faceTextureResult.Artifact,
                                 stagingRoot.Value,
                                 packageAssets,
                                 diagnostics,
                                 cancellationToken))
                    {
                        generatedFaceTextures = faceTextureResult.Artifact;
                    }
                }
            }

            if (!HasErrors(diagnostics))
                DestinationsAreUnique(packageAssets, diagnostics);

            if (!HasErrors(diagnostics))
            {
                var provider = request.Build.ProviderContext;
                BlankNpcFaceGeomSource? faceGeomSource = assets.SchemaVersion switch
                {
                    3 when assets.FinalOutputAuthority is { } final =>
                        new ExactNifBlankNpcFaceGeomSource(
                            final.FaceGeom, final.FaceGeomSha256),
                    4 when generatedFaceGeom is not null =>
                        new ExactNifBlankNpcFaceGeomSource(
                            generatedFaceGeom.OutputNif,
                            generatedFaceGeom.OutputNifSha256),
                    5 when generatedFaceGeom is not null =>
                        new ExactNifBlankNpcFaceGeomSource(
                            generatedFaceGeom.OutputNif,
                            generatedFaceGeom.OutputNifSha256),
                    6 or 7 when directFaceGeom?.Artifact is { } direct =>
                        new ExactNifBlankNpcFaceGeomSource(
                            direct.Proposal.OutputNif,
                            direct.OutputSha256)
                        {
                            QualificationProfile =
                                direct.Proposal.QualificationProfile
                        },
                    8 when schema8ManagerCarrier is not null &&
                        request.ManagerOwnedFaceGeomCarrier is { } managerCarrier =>
                        new ExactNifBlankNpcFaceGeomSource(
                            schema8ManagerCarrier.Value,
                            managerCarrier.FaceGeomSha256)
                        {
                            QualificationProfile =
                                QualifiedFaceGeomCarrierProfile.ManagerAssembledComplete
                        },
                    _ => null
                };
                if (faceGeomSource is null)
                {
                    diagnostics.Add(Error("racemenu-build-facegeom-source-missing",
                        "No verified FaceGeom source exists for the declared standalone-asset schema."));
                    return Refused(plan, assets, runtime, vmad, bodyGen, build, diagnostics);
                }
                BlankNpcFaceTintSource? faceTintSource = assets.SchemaVersion switch
                {
                    3 when assets.FinalOutputAuthority is { } finalTint =>
                        new ExactDdsBlankNpcFaceTintSource(
                            finalTint.FaceTint,
                            finalTint.FaceTintSha256,
                            finalTint.FaceTintWidth,
                            finalTint.FaceTintHeight),
                    4 => new ExactDdsBlankNpcFaceTintSource(
                        request.Build.PresetBundle.CharGenFaceTint,
                        request.Build.PresetBundle.ExpectedCharGenFaceTintSha256,
                        assets.FaceTintWidth,
                        assets.FaceTintHeight),
                    5 when generatedFaceTextures is not null =>
                        new ExactDdsBlankNpcFaceTintSource(
                            generatedFaceTextures.ConventionalFaceTint,
                            generatedFaceTextures.ConventionalFaceTintSha256,
                            generatedFaceTextures.Width,
                            generatedFaceTextures.Height),
                    6 or 7 => new ExactDdsBlankNpcFaceTintSource(
                        request.Build.PresetBundle.CharGenFaceTint,
                        request.Build.PresetBundle.ExpectedCharGenFaceTintSha256,
                        assets.FaceTintWidth,
                        assets.FaceTintHeight),
                    8 => new ExactDdsBlankNpcFaceTintSource(
                        request.Build.PresetBundle.CharGenFaceTint,
                        request.Build.PresetBundle.ExpectedCharGenFaceTintSha256,
                        assets.FaceTintWidth,
                        assets.FaceTintHeight),
                    _ => null
                };
                if (faceTintSource is null)
                {
                    diagnostics.Add(Error("racemenu-build-facetint-source-missing",
                        "No verified FaceTint source exists for the declared standalone-asset schema."));
                    return Refused(plan, assets, runtime, vmad, bodyGen, build, diagnostics);
                }
                if (assets.FinalOutputAuthority is not null)
                    diagnostics.Add(new Diagnostic(
                        "racemenu-build-final-output-oracle",
                        DiagnosticSeverity.Info,
                        "The package uses the exact admitted final FaceGeom/FaceTint oracle pair; this proves the bounded output transaction, not the general preset bake."));
                if (request.Build.ExistingNpcTarget is { } existingTarget)
                {
                    if (existingNpcBuildService is null)
                    {
                        diagnostics.Add(Error("racemenu-build-existing-service-unavailable",
                            "The existing-NPC target requires the source-owned appearance package service."));
                    }
                    else if (faceGeomSource is not ExactNifBlankNpcFaceGeomSource exactFaceGeom ||
                             faceTintSource is not ExactDdsBlankNpcFaceTintSource exactFaceTint)
                    {
                        diagnostics.Add(Error("racemenu-build-existing-facegen-source",
                            "The existing-NPC package requires exact verified FaceGeom and FaceTint sources."));
                    }
                    else
                    {
                        var bodyMorphs = request.ApplyBodySlide &&
                                         assets.BodyMeshAuthority is null
                            ? plan.Preset.Appearance.BodyMorphs
                                .OrderBy(item => item.Key, StringComparer.Ordinal)
                                .Select(item => new BodyGenMorph(item.Key, item.Value))
                                .ToImmutableArray()
                            : ImmutableArray<BodyGenMorph>.Empty;
                        existingNpcBuild = await existingNpcBuildService.ExecuteAsync(
                            new ExistingNpcAppearanceBuildRequest(
                                request.Build.Edition,
                                existingTarget.SourcePlugin,
                                existingTarget.ExpectedSourcePluginSha256,
                                existingTarget.TargetFormId,
                                request.Build.OutputRoot,
                                request.Build.OutputPlugin,
                                request.Build.References.Race,
                                request.Build.Traits.Sex,
                                appearance,
                                vmad,
                                exactFaceGeom,
                                exactFaceTint,
                                bodyMorphs,
                                packageAssets.ToImmutable()) { PluginAuthorities = creationPluginAuthorities },
                            progress,
                            cancellationToken);
                        diagnostics.AddRange(existingNpcBuild.Diagnostics);
                        bodyGen = existingNpcBuild.BodyGen;
                    }
                }
                else
                {
                    var blankRequest = new BlankNpcBuildRequest(
                        request.Build.Edition,
                        provider.ManifestPath,
                        provider.ExpectedManifestSha256,
                        provider.TemplatePlugin,
                        provider.ExpectedTemplatePluginSha256,
                        provider.TemplateNpcFormId,
                        provider.FaceGeomCarrier,
                        provider.ExpectedFaceGeomCarrierSha256,
                        provider.FaceTintManifest,
                        provider.FaceTintProviderRoot,
                        provider.DependencyManifest,
                        request.Build.OutputRoot,
                        request.Build.OutputPlugin,
                        request.Build.Identity,
                        request.Build.Traits,
                        request.Build.References,
                        appearance,
                        request.Build.Stats)
                    {
                        PluginType = request.Build.PluginType,
                        FaceGeomSource = faceGeomSource,
                        FaceTintSource = faceTintSource,
                        RuntimeAppearance = vmad,
                        ExpectedOutputMasters = plan.RequiredOutputMasters,
                        PluginAuthorities =
                            creationPluginAuthorities,
                        TransitivePackageAssets = packageAssets.ToImmutable(),
                        ProviderResources = provider.ProviderResources
                    };
                    build = await blankNpcBuildService.ExecuteAsync(
                        blankRequest, progress, cancellationToken);
                    diagnostics.AddRange(build.Diagnostics);
                }
            }
        }
        finally
        {
            if (stagingRoot is not null)
                CleanupOwnedStagingRoot(stagingRoot.Value, diagnostics);
        }

        var durablePostChecksPassed = false;
        if (build is { Completed: true, Artifact: not null })
        {
            try
            {
                if (bodyGen is { Written: true })
                {
                    bodyGen = await RebindAndVerifyRetainedBodyGenAsync(
                        bodyGen,
                        build.Artifact.OutputRoot,
                        diagnostics,
                        cancellationToken);
                }

                if (qualifiedFaceGeomCarrierService is null)
                {
                    diagnostics.Add(Error("racemenu-build-durable-facegeom-service-unavailable",
                        "A completed preset-derived build requires the durable FaceGeom evidence verifier."));
                }
                else
                {
                    var durableFaceGeom = await qualifiedFaceGeomCarrierService.VerifyEvidenceFileAsync(
                        build.Artifact.OutputRoot,
                        new AssetPath("evidence/facegeom-carrier-materialization.json"),
                        cancellationToken);
                    diagnostics.AddRange(durableFaceGeom.Diagnostics);
                    var materialization = build.FaceGeom?.Artifact;
                    if (!durableFaceGeom.Verified ||
                        durableFaceGeom.OutputNif is null ||
                        durableFaceGeom.OutputSha256 is null ||
                        durableFaceGeom.ReconstructedSourceSha256 is null ||
                        materialization is null ||
                        !string.Equals(durableFaceGeom.OutputNif.Value.Value,
                            build.Artifact.FaceGeom.Value, StringComparison.OrdinalIgnoreCase) ||
                        durableFaceGeom.OutputSha256 != build.Artifact.FaceGeomSha256 ||
                        durableFaceGeom.OutputSha256 != materialization.OutputSha256 ||
                        durableFaceGeom.ReconstructedSourceSha256 != materialization.SourceSha256 ||
                        !durableFaceGeom.ChangedBlocks.SequenceEqual(materialization.ChangedBlocks))
                    {
                        diagnostics.Add(Error("racemenu-build-durable-facegeom-mismatch",
                            "The packaged FaceGeom evidence did not reconstruct the completed build's exact source, output, and changed-block surface after staging cleanup."));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "racemenu-build-durable-facegeom-verified",
                            DiagnosticSeverity.Info,
                            "The packaged FaceGeom evidence independently reconstructed the exact source and output after owned staging cleanup."));
                    }
                }

                if (exactFaceTintEvidenceDecoder is null)
                {
                    diagnostics.Add(Error("racemenu-build-durable-facetint-service-unavailable",
                        "A completed preset-derived build requires the durable exact FaceTint evidence decoder."));
                }
                else
                {
                    var durableFaceTint = await BlankNpcBuildService.VerifyExactFaceTintEvidenceFileAsync(
                        build.Artifact.OutputRoot,
                        new AssetPath("evidence/facetint-exact-source.json"),
                        WorkspacePolicy,
                        LaboratoryRoot,
                        exactFaceTintEvidenceDecoder,
                        cancellationToken);
                    diagnostics.AddRange(durableFaceTint.Diagnostics);
                    if (!durableFaceTint.Verified || durableFaceTint.OutputDds is null ||
                        durableFaceTint.SourceSha256 is null || durableFaceTint.OutputSha256 is null ||
                        !string.Equals(durableFaceTint.OutputDds.Value.Value,
                            build.Artifact.FaceTint.Value, StringComparison.OrdinalIgnoreCase) ||
                        durableFaceTint.SourceSha256 != build.Artifact.FaceTintSha256 ||
                        durableFaceTint.OutputSha256 != build.Artifact.FaceTintSha256)
                    {
                        diagnostics.Add(Error("racemenu-build-durable-facetint-mismatch",
                            "The package-relative FaceTint evidence did not rehash and decode the completed build after staging cleanup."));
                    }
                    else
                    {
                        diagnostics.Add(new Diagnostic(
                            "racemenu-build-durable-facetint-verified",
                            DiagnosticSeverity.Info,
                            "The package-relative FaceTint evidence rehashed and decoded the retained exact source after owned staging cleanup."));
                    }
                }

                durablePostChecksPassed = !HasErrors(diagnostics);
            }
            finally
            {
                if (!durablePostChecksPassed)
                    QuarantineFailedCompletedOutputRoot(
                        request.Build.OutputRoot, build.Artifact.OutputRoot, diagnostics);
            }
        }

        var completed = (build is { Completed: true, Artifact: not null } ||
                         existingNpcBuild is { Completed: true, Artifact: not null }) &&
                        !HasErrors(diagnostics);
        if (outputBindingProbe)
        {
            if (!completed || build?.Artifact is not { } probeArtifact)
            {
                diagnostics.Add(Error(
                    "racemenu-build-output-binding-probe",
                    "The private JSlot output-binding probe did not produce a completed private build artifact."));
                return Refused(null, null, null, null, null, null, diagnostics);
            }

            return new RaceMenuNpcExecutionResult(
                Completed: false,
                Plan: null,
                Assets: null,
                RuntimeAppearance: null,
                RuntimeVmad: null,
                BodyGen: null,
                Build: null,
                Diagnostics: diagnostics.ToImmutable())
            {
                ExternalInstallOutputPluginProbe =
                    new RaceMenuNpcExternalInstallOutputPluginProbeArtifact(
                        request.Build.OutputPlugin,
                        probeArtifact.Plugin,
                        probeArtifact.PluginSha256,
                        probeArtifact.AllocatedFormId)
            };
        }

        if (completed)
        {
            diagnostics.Add(new Diagnostic("racemenu-build-static-complete",
                DiagnosticSeverity.Info,
                "The preset-derived package was written and independently reopened; Skyrim runtime and visual authority remain separate."));
        }
        return new RaceMenuNpcExecutionResult(
            completed, plan, assets, runtime, vmad, bodyGen, build,
            diagnostics.ToImmutable())
        {
            ExistingNpcBuild = existingNpcBuild,
            WholeSkinAuthority = wholeSkin
        };
    }

    private static FullyAuthoredSkyrimNpcAppearanceSource
        AttachPrivateNakedSkinBinding(
            FullyAuthoredSkyrimNpcAppearanceSource appearance,
            SkyrimNpcWholeSkinAuthority? wholeSkin,
            RaceMenuNpcBodyMeshAuthority? bodyMeshAuthority)
    {
        if (bodyMeshAuthority is null) return appearance;
        if (wholeSkin is null)
        {
            throw new InvalidDataException(
                "External BodySlide mesh authority requires accepted whole-skin authority so private WNAM skin textures stay closed.");
        }

        var meshByRole = bodyMeshAuthority.Meshes.ToDictionary(
            item => item.Role);
        AssetPath Mesh(RaceMenuNpcBodyMeshRole role) =>
            meshByRole.TryGetValue(role, out var mesh)
                ? mesh.Destination
                : throw new InvalidDataException(
                    $"External BodySlide mesh authority is missing {role.ToWireName()}.");
        uint next = NextOwnedAppearanceFormIdAfterHead(appearance);
        var regions = wholeSkin.Regions
            .Select(region =>
            {
                RaceMenuNpcBodyMeshRole role = region.Region switch
                {
                    SkyrimNpcSkinRegion.Body => RaceMenuNpcBodyMeshRole.Body1,
                    SkyrimNpcSkinRegion.Hands => RaceMenuNpcBodyMeshRole.Hands1,
                    SkyrimNpcSkinRegion.Feet => RaceMenuNpcBodyMeshRole.Feet1,
                    _ => throw new InvalidDataException(
                        "Whole-skin authority contains an unsupported region.")
                };
                var result =
                    new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                        region.Region,
                        new FormId(next),
                        region.ArmorAddon,
                        region.TextureSet,
                        Mesh(role));
                next++;
                return result;
            })
            .ToImmutableArray();
        return appearance with
        {
            NakedSkinBinding =
                new OutputOwnedSkyrimNpcNakedSkinBinding(
                    new FormId(next),
                    wholeSkin.SkinArmor,
                    regions)
        };
    }

    private static FullyAuthoredSkyrimNpcAppearanceSource
        AttachExposedOutfitSkinBinding(
            FullyAuthoredSkyrimNpcAppearanceSource appearance,
            SkyrimNpcWholeSkinAuthority? wholeSkin)
    {
        if (wholeSkin?.ExposedOutfitSkinBinding is not { } binding)
            return appearance;

        uint next = NextOwnedAppearanceFormIdAfterHead(appearance);
        if (appearance.NakedSkinBinding is { } skinBinding)
            next = checked(next + (uint)skinBinding.Regions.Length + 1);
        return appearance with
        {
            ExposedOutfitSkinBinding =
                new OutputOwnedSkyrimNpcExposedOutfitSkinBinding(
                    new FormId(next),
                    new FormId(checked(next + 1)),
                    new FormId(checked(next + 2)),
                    binding.Outfit,
                    binding.Armor,
                    binding.ArmorAddon,
                    binding.TargetFemaleSkinTextureSet)
        };
    }

    private static uint NextOwnedAppearanceFormIdAfterHead(
        FullyAuthoredSkyrimNpcAppearanceSource appearance)
    {
        uint next = 0x0000_0801;
        if (appearance.HairColor is OutputOwnedSkyrimNpcHairColor)
            next++;
        if (appearance.FaceTextureSet is OutputOwnedSkyrimNpcFaceTextureSet)
            next = checked(next + 2);
        return next;
    }

    private static ImmutableArray<NpcCreationPluginAuthority>
        MergeCreationPluginAuthorities(
            ImmutableArray<NpcCreationPluginAuthority> planAuthorities,
            FullyAuthoredSkyrimNpcAppearanceSource appearance)
    {
        var result = ImmutableArray.CreateBuilder<
            NpcCreationPluginAuthority>();
        var byPlugin = new Dictionary<
            string,
            NpcCreationPluginAuthority>(
            StringComparer.OrdinalIgnoreCase);

        void Add(NpcCreationPluginAuthority authority)
        {
            if (byPlugin.TryGetValue(
                    authority.Plugin.Value,
                    out NpcCreationPluginAuthority? current))
            {
                if (current != authority)
                {
                    throw new InvalidDataException(
                        $"Creation provider '{authority.Plugin}' has conflicting reviewed path or hash authorities.");
                }
                return;
            }
            byPlugin.Add(authority.Plugin.Value, authority);
            result.Add(authority);
        }

        foreach (NpcCreationPluginAuthority authority in
                 planAuthorities.IsDefault
                     ? []
                     : planAuthorities)
            Add(authority);
        if (appearance.NakedSkinBinding is { } skinBinding)
        {
            Add(new NpcCreationPluginAuthority(
                skinBinding.SourceSkinArmor.ProviderPluginName,
                skinBinding.SourceSkinArmor.ProviderPlugin,
                skinBinding.SourceSkinArmor.ProviderPluginSha256));
            foreach (var source in skinBinding.Regions.SelectMany(region =>
                         region.TargetFemaleSkinTextureSet is null
                             ? new[] { region.SourceArmorAddon }
                             : new[]
                             {
                                 region.SourceArmorAddon,
                                 region.TargetFemaleSkinTextureSet
                             }))
            {
                Add(new NpcCreationPluginAuthority(
                    source.ProviderPluginName,
                    source.ProviderPlugin,
                    source.ProviderPluginSha256));
            }
        }
        if (appearance.ExposedOutfitSkinBinding is { } binding)
        {
            foreach (RaceMenuNpcFormBinding source in new[]
                     {
                         binding.SourceOutfit,
                         binding.SourceArmor,
                         binding.SourceArmorAddon,
                         binding.TargetFemaleSkinTextureSet
                     })
            {
                Add(new NpcCreationPluginAuthority(
                    source.ProviderPluginName,
                    source.ProviderPlugin,
                    source.ProviderPluginSha256));
            }
        }
        return result.ToImmutable();
    }

    private static RaceMenuNpcExecutionResult Refused(
        RaceMenuNpcAppearancePlan? plan,
        RaceMenuNpcStandaloneAssets? assets,
        SkyrimNpcRuntimeAppearancePayload? runtime,
        SkyrimNpcApplySseVmadPayload? vmad,
        BodyGenBuildResult? bodyGen,
        BlankNpcBuildResult? build,
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, plan, assets, runtime, vmad, bodyGen, build,
            diagnostics.ToImmutable());

    private bool ValidateSchemaDependencies(
        RaceMenuNpcStandaloneAssets assets,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (assets.SchemaVersion is 4 or 5 &&
            (faceGeomBuildService is null || assets.FaceBakeAuthority is null))
        {
            diagnostics.Add(Error("racemenu-build-facegeom-service-unavailable",
                $"SchemaVersion {assets.SchemaVersion} requires the concrete product FaceGeom build service and an exact face-bake authority."));
        }
        if (assets.SchemaVersion == 5 &&
            (faceTextureBuildService is null || assets.FaceTextureBakeAuthority is null))
        {
            diagnostics.Add(Error("racemenu-build-face-texture-service-unavailable",
                "SchemaVersion 5 requires the product face-texture composition service and exact authority."));
        }
        if (assets.SchemaVersion is 6 or 7 &&
            directCharGenFaceGeomBuildService is null)
        {
            diagnostics.Add(Error("racemenu-build-direct-facegeom-service-unavailable",
                $"SchemaVersion {assets.SchemaVersion} requires the direct CharGen-to-qualified-carrier composition service."));
        }
        if (assets.SchemaVersion == 7 &&
            (assets.BodySlidePresetAuthority is null ||
             assets.BodyMeshAuthority is null))
        {
            diagnostics.Add(Error("racemenu-build-schema7-body-authority",
                "SchemaVersion 7 requires exact BodySlide preset and staged body mesh authorities."));
        }
        if (qualifiedFaceGeomCarrierService is null)
        {
            diagnostics.Add(Error("racemenu-build-durable-facegeom-service-unavailable",
                "Preset-derived builds require the durable FaceGeom evidence verifier before any output transaction starts."));
        }
        if (exactFaceTintEvidenceDecoder is null)
        {
            diagnostics.Add(Error("racemenu-build-durable-facetint-service-unavailable",
                "Preset-derived builds require the durable exact FaceTint evidence decoder before any output transaction starts."));
        }
        return !HasErrors(diagnostics);
    }
}
