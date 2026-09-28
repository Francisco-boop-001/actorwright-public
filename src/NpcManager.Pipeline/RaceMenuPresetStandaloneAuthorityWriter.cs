using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Produces schema-6/7 direct-CharGen or schema-8 Manager-native standalone
/// authority from independently reopened inputs. It writes one new manifest
/// and never changes the active request.
/// </summary>
public sealed class RaceMenuPresetStandaloneAuthorityWriter(
    ISkyrimFaceMorphSnapshotService faceMorphSnapshotService,
    ISkyrimAssetAuthorityPlanner assetAuthorityPlanner,
    IFaceTintTextureDecoder faceTintDecoder,
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot)
    : IRaceMenuPresetStandaloneAuthorityWriter
{
    private const int MaximumManifestBytes = 1 * 1024 * 1024;

    public async ValueTask<RaceMenuPresetStandaloneAuthorityWriteResult> WriteAsync(
        RaceMenuPresetStandaloneAuthorityWriteRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimNpcRuntimeAppearancePayload runtime;
        try
        {
            runtime = RaceMenuNpcRuntimeAppearanceMapper.Map(
                request.Plan, request.OverlayDecisions, applyBodyMorphs: true);
        }
        catch (InvalidDataException exception)
        {
            diagnostics.Add(Error("racemenu-standalone-runtime", exception.Message));
            return Refused(diagnostics);
        }
        if (!runtime.CanEmit)
        {
            diagnostics.Add(Error("racemenu-standalone-runtime-blocked",
                "The selected preset contains a runtime row that the pinned Skyrim NPC script cannot represent."));
            return Refused(diagnostics);
        }
        diagnostics.AddRange(runtime.Dispositions.Where(item => item.Surface == SkyrimNpcRuntimeAppearanceSurface.Overlay &&
                item.Kind == SkyrimNpcRuntimeDispositionKind.UserOmitted)
            .Select(item => new Diagnostic("racemenu-overlay-user-omitted", DiagnosticSeverity.Info, item.Reason)));

        RaceMenuNpcNam9TrailingAuthority nam9Authority = request.Nam9Authority;
        SkyrimFaceMorphSnapshotResult nam9 = await faceMorphSnapshotService.ReadAsync(
            new SkyrimFaceMorphSnapshotRequest(
                GameEdition.SkyrimSpecialEdition,
                nam9Authority.Plugin,
                nam9Authority.ExpectedPluginSha256,
                nam9Authority.NpcFormId),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(nam9.Diagnostics);
        if (!nam9.Resolved || nam9.Snapshot is not { HasNam9: true } snapshot)
        {
            diagnostics.Add(Error("racemenu-standalone-nam9",
                "The exact copied template did not expose its engine-owned NAM9 payload."));
            return Refused(diagnostics);
        }
        if (nam9.PluginSha256 != nam9Authority.ExpectedPluginSha256)
        {
            diagnostics.Add(Error("racemenu-standalone-nam9-hash",
                $"The reopened NAM9 plugin hash {nam9.PluginSha256} does not match {nam9Authority.ExpectedPluginSha256}."));
            return Refused(diagnostics);
        }
        if (!float.IsFinite(snapshot.Nam9Trailing))
        {
            diagnostics.Add(Error("racemenu-standalone-nam9-trailing",
                "The exact copied template NAM9 trailing value is not finite."));
            return Refused(diagnostics);
        }
        if (snapshot.Nam9Trailing != nam9Authority.ExpectedTrailingValue)
        {
            diagnostics.Add(Error("racemenu-standalone-nam9-value",
                "The reopened NAM9 trailing value disagrees with the reviewed current request."));
            return Refused(diagnostics);
        }

        RaceMenuNpcPresetBundle bundle = request.Plan.Request.PresetBundle;
        FaceTintTextureDecodeResult faceTint = await faceTintDecoder.DecodeAsync(
            bundle.CharGenFaceTint, cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(faceTint.Diagnostics);
        if (!faceTint.Decoded || faceTint.SourceSha256 !=
            bundle.ExpectedCharGenFaceTintSha256 || faceTint.Width <= 0 ||
            faceTint.Height <= 0)
        {
            diagnostics.Add(Error("racemenu-standalone-facetint",
                "The selected CharGen FaceTint did not reopen with its exact hash and positive dimensions."));
            return Refused(diagnostics);
        }

        SkyrimPrivateHeadTexturePaths headTextures = request.PrivateHeadTextures ?? request.RecordDraft.HeadTextureAuthority.Paths;
        ImmutableArray<AssetPath> requiredTextures;
        ImmutableArray<BlankNpcTransitivePackageAsset> packageAssets;
        try
        {
            requiredTextures = RequiredTextures(headTextures, runtime);
            var privatePaths = HeadTextures(headTextures).Select(path => path.Value).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
            packageAssets = request.PrivateHeadTextures is null ? [] : request.RetainedPackageAssets
                .Where(item => privatePaths.Contains(item.Destination.Value))
                .OrderBy(item => item.Destination.Value, StringComparer.OrdinalIgnoreCase).ToImmutableArray();
            if (packageAssets.Select(item => item.Destination.Value).Distinct(StringComparer.OrdinalIgnoreCase).Count() != packageAssets.Length)
                throw new InvalidDataException("More than one copied authority supplies the same private texture destination.");
            if (!request.ExternalHeadPartDependencies.IsDefaultOrEmpty && !packageAssets.IsEmpty)
                throw new InvalidDataException("SchemaVersion 8 forbids packageAssets; use its existing verified external texture authority route.");
            foreach (var asset in packageAssets)
            {
                _ = Relative(asset.Source);
                diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, asset.Source));
                if (request.RetainedExternalTextures.Any(item => item.DataRelativePath.Value.Equals(asset.Destination.Value, StringComparison.OrdinalIgnoreCase) &&
                        item.ExpectedMemberSha256 != asset.ExpectedSha256))
                    throw new InvalidDataException($"Copied and external authorities disagree for private texture '{asset.Destination.Value}'.");
            }
            if (HasErrors(diagnostics))
            {
                diagnostics.Add(PrivateTextureRefusal("A copied private texture source is outside the admitted workspace policy."));
                return Refused(diagnostics);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            diagnostics.Add(request.PrivateHeadTextures is null
                ? Error("racemenu-standalone-texture-path", exception.Message) : PrivateTextureRefusal(exception.Message));
            return Refused(diagnostics);
        }
        var copiedPaths = packageAssets.Select(item => item.Destination.Value).ToImmutableHashSet(StringComparer.OrdinalIgnoreCase);
        var retainedByPath = request.RetainedExternalTextures.ToDictionary(
            item => item.DataRelativePath.Value, StringComparer.OrdinalIgnoreCase);
        ImmutableArray<AssetPath> unresolved = requiredTextures
            .Where(item => !copiedPaths.Contains(item.Value) && !retainedByPath.ContainsKey(item.Value))
            .ToImmutableArray();
        ImmutableArray<SkyrimAssetAuthority> planned = [];
        if (!unresolved.IsDefaultOrEmpty)
        {
            SkyrimAssetAuthorityPlanResult texturePlan =
                await assetAuthorityPlanner.PlanAsync(
                    new SkyrimAssetAuthorityPlanRequest(
                        GameEdition.SkyrimSpecialEdition,
                        request.RecordDraft.Target.DataRoot,
                        unresolved),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(texturePlan.Diagnostics);
            if (!texturePlan.Accepted || texturePlan.Authorities.Length !=
                unresolved.Length || HasErrors(diagnostics))
            {
                if (request.PrivateHeadTextures is not null)
                    diagnostics.Add(PrivateTextureRefusal("Required texture authority could not be resolved: " + string.Join(", ", unresolved.Select(item => item.Value))));
                return Refused(diagnostics);
            }
            planned = texturePlan.Authorities;
        }

        ImmutableArray<RaceMenuNpcExternalTextureAuthority> external = requiredTextures
            .Where(path => !copiedPaths.Contains(path.Value))
            .Select(path => retainedByPath.TryGetValue(path.Value, out var retained)
                ? retained
                : ToExternalAuthority(planned.Single(item =>
                    string.Equals(item.AssetPath.Value, path.Value,
                        StringComparison.OrdinalIgnoreCase))))
                .OrderBy(item => item.DataRelativePath.Value,
                    StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray();
        string assetSetId = BuildAssetSetId(request, external, packageAssets);
        bool schema8 = !request.ExternalHeadPartDependencies.IsDefaultOrEmpty;
        int schemaVersion = schema8
            ? 8
            : request.BodySlidePresetAuthority is not null &&
              request.BodyMeshAuthority is not null
                ? 7
                : 6;
        byte[] bytes = Serialize(
            assetSetId, request, snapshot.Nam9Trailing,
            faceTint.Width, faceTint.Height, external, packageAssets);
        if (bytes.Length is <= 0 or > MaximumManifestBytes)
        {
            diagnostics.Add(Error("racemenu-standalone-size",
                $"Generated standalone authority must be 1-{MaximumManifestBytes} bytes."));
            return Refused(diagnostics);
        }

        try
        {
            Sha256Hash hash = await WriteAndReopenAsync(
                request.Destination, bytes, assetSetId, schemaVersion,
                request.ExternalHeadPartDependencies,
                request.ExternalHeadPartExclusionAttestations,
                cancellationToken)
                .ConfigureAwait(false);
            return new RaceMenuPresetStandaloneAuthorityWriteResult(
                true,
                new RaceMenuPresetStandaloneAuthorityArtifact(
                    assetSetId, request.Destination, hash,
                    faceTint.Width, faceTint.Height, external,
                    RuntimeAuthority: false,
                    request.BodySlidePresetAuthority,
                    request.BodyMeshAuthority,
                    request.ExternalCharGenExportAuthority)
                {
                    ExternalHeadPartDependencies =
                        request.ExternalHeadPartDependencies,
                    ExternalHeadPartExclusionAttestations =
                        request.ExternalHeadPartExclusionAttestations
                },
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException)
        {
            TryDeleteOwnedOutput(request.Destination);
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           InvalidDataException or
                                           ArgumentException or
                                           NotSupportedException or
                                           JsonException)
        {
            TryDeleteOwnedOutput(request.Destination);
            diagnostics.Add(Error("racemenu-standalone-write", exception.Message));
            return Refused(diagnostics);
        }
    }

    private void ValidateRequest(
        RaceMenuPresetStandaloneAuthorityWriteRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        RaceMenuNpcAppearancePlan plan = request.Plan;
        RaceMenuPresetRecordAuthorityDraft draft = request.RecordDraft;
        bool hasDescriptors = !request.ExternalHeadPartDependencies.IsDefaultOrEmpty;
        bool hasAttestations = !request.ExternalHeadPartExclusionAttestations.IsDefaultOrEmpty;
        if (hasDescriptors != hasAttestations ||
            (hasDescriptors && request.ExternalHeadPartDependencies.Length == 0) ||
            (hasAttestations && request.ExternalHeadPartExclusionAttestations.Length == 0) ||
            (hasDescriptors && request.ExternalHeadPartDependencies.Length !=
                request.ExternalHeadPartExclusionAttestations.Length))
        {
            diagnostics.Add(Error(
                "racemenu-standalone-external-authority-pair",
                "Schema-8 descriptor and FaceGeom exclusion-attestation arrays must both be non-empty and have equal length."));
        }
        if (hasDescriptors)
        {
            var descriptorIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ExternalHeadPartDependencyDescriptor descriptor in
                     request.ExternalHeadPartDependencies)
            {
                try
                {
                    _ = ExternalHeadPartDependencyDescriptorCodec
                        .SerializeDescriptor(descriptor);
                    if (!descriptorIds.Add(descriptor.DescriptorId.Value))
                        diagnostics.Add(Error(
                            "racemenu-standalone-external-descriptor-duplicate",
                            $"Schema-8 descriptor '{descriptor.DescriptorId}' occurs more than once."));
                }
                catch (Exception exception) when (exception is InvalidDataException or
                                                   ArgumentException or
                                                   FormatException)
                {
                    diagnostics.Add(Error(
                        "racemenu-standalone-external-descriptor",
                        exception.Message));
                }
            }

            var attestationIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (ExternalHeadPartFaceGeomExclusionAttestation attestation in
                     request.ExternalHeadPartExclusionAttestations)
            {
                try
                {
                    _ = ExternalHeadPartDependencyDescriptorCodec
                        .SerializeAttestation(attestation);
                    if (!attestationIds.Add(attestation.DescriptorId.Value))
                        diagnostics.Add(Error(
                            "racemenu-standalone-external-attestation-duplicate",
                            $"Schema-8 attestation for descriptor '{attestation.DescriptorId}' occurs more than once."));
                    if (!descriptorIds.Contains(attestation.DescriptorId.Value))
                        diagnostics.Add(Error(
                            "racemenu-standalone-external-attestation-orphan",
                            $"Schema-8 attestation '{attestation.DescriptorId}' has no matching descriptor."));
                }
                catch (Exception exception) when (exception is InvalidDataException or
                                                   ArgumentException or
                                                   FormatException)
                {
                    diagnostics.Add(Error(
                        "racemenu-standalone-external-attestation",
                        exception.Message));
                }
            }
            foreach (string descriptorId in descriptorIds)
            {
                if (!attestationIds.Contains(descriptorId))
                    diagnostics.Add(Error(
                        "racemenu-standalone-external-attestation-missing",
                        $"Schema-8 descriptor '{descriptorId}' has no matching FaceGeom exclusion attestation."));
            }
            if (request.ExternalCharGenExportAuthority is not null)
                diagnostics.Add(Error(
                    "racemenu-standalone-external-chargen-schema8",
                    "SchemaVersion 8 forbids external CharGen export authority."));
        }
        if ((request.BodySlidePresetAuthority is null) !=
            (request.BodyMeshAuthority is null))
        {
            diagnostics.Add(Error("racemenu-standalone-body-authority",
                "BodySlide preset and body mesh authorities must be supplied together for schemaVersion 7."));
        }
        if (request.ExternalCharGenExportAuthority is { } external &&
            (request.BodySlidePresetAuthority is null ||
             request.BodyMeshAuthority is null ||
             external.PresetSha256 != plan.Preset.SourceHash ||
             external.FaceGeomSha256 !=
                plan.Request.PresetBundle.ExpectedCharGenFaceGeomSha256 ||
             external.Race != plan.Request.References.Race ||
             external.Sex != plan.Request.Traits.Sex ||
             !external.UserConfirmedVisualMatch ||
             external.RuntimeAuthority))
        {
            diagnostics.Add(Error(
                "racemenu-standalone-external-chargen-authority",
                "An external CharGen export may be retained only in schemaVersion 7 when it matches the exact selected preset, FaceGeom, race, sex, user confirmation, and non-runtime limits."));
        }
        if (!plan.IsReady || plan.RuntimeAuthority || draft.RuntimeAuthority ||
            plan.Request.Edition != GameEdition.SkyrimSpecialEdition ||
            plan.Preset.SourceHash != draft.Preset.SourceHash ||
            plan.Preset.SourceHash != plan.Request.PresetBundle.ExpectedPresetSha256 ||
            plan.Request.References.Race != draft.Target.Race ||
            plan.Request.Traits.Sex != draft.Target.Sex)
        {
            diagnostics.Add(Error("racemenu-standalone-plan",
                "Standalone authority requires one ready non-runtime plan, record draft, race, sex, and preset identity."));
        }
        if (request.OverlayDecisions is { } decisions &&
            decisions.PresetSha256 != plan.Preset.SourceHash)
        {
            diagnostics.Add(Error("racemenu-standalone-overlay-decisions",
                "Retained overlay decisions belong to a different preset hash."));
        }
        if (request.RetainedExternalTextures.IsDefault ||
            request.RetainedExternalTextures.Select(item => item.DataRelativePath.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.RetainedExternalTextures.Length)
        {
            diagnostics.Add(Error("racemenu-standalone-retained-textures",
                "Retained external texture authorities must be an initialized distinct array."));
        }
        if (request.RetainedPackageAssets.IsDefault)
            diagnostics.Add(PrivateTextureRefusal("Retained package assets must be an initialized array."));
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(
            labRoot, draft.Target.DataRoot));
        string? parent = Path.GetDirectoryName(request.Destination.Value);
        if (!request.Destination.IsUnder(labRoot) ||
            !string.Equals(Path.GetExtension(request.Destination.Value), ".json",
                StringComparison.OrdinalIgnoreCase) ||
            parent is null || !Directory.Exists(parent) ||
            File.Exists(request.Destination.Value) ||
            Directory.Exists(request.Destination.Value))
        {
            diagnostics.Add(Error("racemenu-standalone-destination",
                "Destination must be one absent .json file under an existing K-local transaction directory."));
            return;
        }
        try
        {
            if (HasReparsePath(parent))
                diagnostics.Add(Error("racemenu-standalone-destination-reparse",
                    "Standalone authority destination traverses a reparse point."));
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-standalone-destination-inspection",
                exception.Message));
        }
    }

    private static ImmutableArray<AssetPath> RequiredTextures(
        SkyrimPrivateHeadTexturePaths head,
        SkyrimNpcRuntimeAppearancePayload runtime)
    {
        var values = HeadTextures(head);
        foreach (SkyrimNpcRuntimeOverlay overlay in runtime.Overlays)
        {
            AddRuntime(values, overlay.Diffuse);
            AddRuntime(values, overlay.Normal);
        }
        foreach (SkyrimNpcRuntimeSkinOverride skin in runtime.SkinOverrides)
        {
            AddRuntime(values, skin.Diffuse);
            AddRuntime(values, skin.Normal);
        }
        return values.DistinctBy(item => item.Value,
                StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
    }

    private static List<AssetPath> HeadTextures(SkyrimPrivateHeadTexturePaths head)
    {
        var values = new List<AssetPath>
        {
            DataTexture(head.Diffuse.Value),
            DataTexture(head.NormalOrGloss.Value),
            DataTexture(head.GlowOrDetailMap.Value),
            DataTexture(head.Height.Value),
            DataTexture(head.BacklightMaskOrSpecular.Value)
        };
        AddOptional(values, head.EnvironmentMaskOrSubsurfaceTint);
        AddOptional(values, head.Environment);
        AddOptional(values, head.Multilayer);
        return values;
    }

    private static void AddOptional(List<AssetPath> values, AssetPath? path)
    {
        if (path is not null) values.Add(DataTexture(path.Value.Value));
    }

    private static void AddRuntime(List<AssetPath> values, string path)
    {
        if (!string.IsNullOrWhiteSpace(path)) values.Add(DataTexture(path));
    }

    private static AssetPath DataTexture(string path)
    {
        var normalized = new AssetPath(path);
        var dataPath = normalized.Value.StartsWith("Textures/",
            StringComparison.OrdinalIgnoreCase)
            ? normalized
            : new AssetPath($"Textures/{normalized.Value}");
        if (!dataPath.Value.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Texture path '{path}' is not a DDS.", nameof(path));
        return dataPath;
    }

    private static RaceMenuNpcExternalTextureAuthority ToExternalAuthority(
        SkyrimAssetAuthority authority) =>
        new(authority.AssetPath,
            authority.ProviderKind == AssetProviderKind.Loose
                ? "loose"
                : authority.ProviderId,
            authority.ProviderPath,
            authority.ProviderSha256,
            authority.ContentSha256);

    private byte[] Serialize(
        string assetSetId,
        RaceMenuPresetStandaloneAuthorityWriteRequest request,
        float nam9Trailing,
        int width,
        int height,
        ImmutableArray<RaceMenuNpcExternalTextureAuthority> external,
        ImmutableArray<BlankNpcTransitivePackageAsset> packageAssets)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer,
                   new JsonWriterOptions { Indented = true, SkipValidation = false }))
        {
            bool schema8 = !request.ExternalHeadPartDependencies.IsDefaultOrEmpty;
            bool schema7 = !schema8 &&
                           request.BodySlidePresetAuthority is not null &&
                           request.BodyMeshAuthority is not null;
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", schema8 ? 8 : schema7 ? 7 : 6);
            writer.WriteString("assetSetId", assetSetId);
            writer.WriteString("edition", "skyrimse");
            WriteNam9(writer, request.Nam9Authority, nam9Trailing);
            writer.WriteStartObject("faceTint");
            writer.WriteNumber("width", width);
            writer.WriteNumber("height", height);
            writer.WriteEndObject();
            WriteHeadTextures(writer, request.PrivateHeadTextures ?? request.RecordDraft.HeadTextureAuthority.Paths);
            writer.WriteStartArray("packageAssets");
            foreach (var asset in packageAssets)
            {
                writer.WriteStartObject();
                writer.WriteString("sourcePath", Relative(asset.Source));
                writer.WriteString("sha256", asset.ExpectedSha256.Value);
                writer.WriteString("destination", asset.Destination.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (request.OverlayDecisions is { } decisions)
            {
                writer.WriteStartObject("overlayDecisions");
                writer.WriteString("manifestPath", Relative(decisions.ManifestPath));
                writer.WriteString("manifestSha256", decisions.ManifestSha256.Value);
                writer.WriteEndObject();
            }
            else
            {
                writer.WriteNull("overlayDecisions");
            }
            writer.WriteStartArray("externalTextureAuthorities");
            foreach (RaceMenuNpcExternalTextureAuthority row in external)
            {
                writer.WriteStartObject();
                writer.WriteString("dataRelativePath", row.DataRelativePath.Value);
                writer.WriteString("provider", row.Provider);
                writer.WriteString("sourcePath", Relative(row.Source));
                writer.WriteString("sourceSha256", row.ExpectedSourceSha256.Value);
                writer.WriteString("memberSha256", row.ExpectedMemberSha256.Value);
                writer.WriteEndObject();
            }
            writer.WriteEndArray();
            if (schema8)
            {
                writer.WriteStartArray("externalHeadPartDependencies");
                foreach (ExternalHeadPartDependencyDescriptor descriptor in
                         request.ExternalHeadPartDependencies)
                    writer.WriteRawValue(
                        Encoding.UTF8.GetString(
                            ExternalHeadPartDependencyDescriptorCodec
                                .SerializeDescriptor(descriptor)),
                        skipInputValidation: true);
                writer.WriteEndArray();
                writer.WriteStartArray(
                    "externalHeadPartFaceGeomExclusionAttestations");
                foreach (ExternalHeadPartFaceGeomExclusionAttestation attestation in
                         request.ExternalHeadPartExclusionAttestations)
                    writer.WriteRawValue(
                        Encoding.UTF8.GetString(
                            ExternalHeadPartDependencyDescriptorCodec
                                .SerializeAttestation(attestation)),
                        skipInputValidation: true);
                writer.WriteEndArray();
            }
            if (schema8 || schema7)
            {
                if (request.BodySlidePresetAuthority is { } bodySlide)
                    WriteBodySlideReference(
                        writer,
                        "bodySlidePresetAuthority",
                        bodySlide);
                else
                    writer.WriteNull("bodySlidePresetAuthority");
                if (request.BodyMeshAuthority is { } bodyMesh)
                    WriteBodyMeshReference(
                        writer,
                        "bodyMeshAuthority",
                        bodyMesh);
                else
                    writer.WriteNull("bodyMeshAuthority");
                if (schema7 && request.ExternalCharGenExportAuthority is { } charGenExport)
                    WriteExternalCharGenExportReference(writer, charGenExport);
            }
            writer.WriteNull("finalOutputAuthority");
            writer.WriteEndObject();
        }
        return buffer.WrittenSpan.ToArray();
    }

    private void WriteBodySlideReference(
        Utf8JsonWriter writer,
        string name,
        RaceMenuNpcBodySlidePresetAuthority authority)
    {
        writer.WriteStartObject(name);
        writer.WriteString("manifestPath", Relative(authority.ManifestPath));
        writer.WriteString("manifestSha256", authority.ManifestSha256.Value);
        writer.WriteEndObject();
    }

    private void WriteBodyMeshReference(
        Utf8JsonWriter writer,
        string name,
        RaceMenuNpcBodyMeshAuthority authority)
    {
        writer.WriteStartObject(name);
        writer.WriteString("manifestPath", Relative(authority.ManifestPath));
        writer.WriteString("manifestSha256", authority.ManifestSha256.Value);
        writer.WriteEndObject();
    }

    private void WriteExternalCharGenExportReference(
        Utf8JsonWriter writer,
        RaceMenuNpcExternalCharGenExportAuthority authority)
    {
        writer.WriteStartObject("externalCharGenExportAuthority");
        writer.WriteString("manifestPath", Relative(authority.ManifestPath));
        writer.WriteString("manifestSha256", authority.ManifestSha256.Value);
        writer.WriteEndObject();
    }

    private void WriteNam9(
        Utf8JsonWriter writer,
        RaceMenuNpcNam9TrailingAuthority authority,
        float trailing)
    {
        writer.WriteStartObject("nam9Authority");
        writer.WriteString("pluginPath", Relative(authority.Plugin));
        writer.WriteString("pluginSha256", authority.ExpectedPluginSha256.Value);
        writer.WriteString("npcFormId", $"0x{authority.NpcFormId.Value:X8}");
        writer.WriteNumber("trailingValue", trailing);
        writer.WriteEndObject();
    }

    private static void WriteHeadTextures(
        Utf8JsonWriter writer,
        SkyrimPrivateHeadTexturePaths paths)
    {
        writer.WriteStartObject("privateHeadTextures");
        writer.WriteString("diffuse", paths.Diffuse.Value);
        writer.WriteString("normalOrGloss", paths.NormalOrGloss.Value);
        writer.WriteString("glowOrDetailMap", paths.GlowOrDetailMap.Value);
        writer.WriteString("height", paths.Height.Value);
        writer.WriteString("backlightMaskOrSpecular",
            paths.BacklightMaskOrSpecular.Value);
        WriteNullable(writer, "environmentMaskOrSubsurfaceTint",
            paths.EnvironmentMaskOrSubsurfaceTint);
        WriteNullable(writer, "environment", paths.Environment);
        WriteNullable(writer, "multilayer", paths.Multilayer);
        writer.WriteEndObject();
    }

    private static void WriteNullable(
        Utf8JsonWriter writer,
        string name,
        AssetPath? path)
    {
        if (path is null) writer.WriteNull(name);
        else writer.WriteString(name, path.Value.Value);
    }

    private static async ValueTask<Sha256Hash> WriteAndReopenAsync(
        WorkspacePath destination,
        byte[] bytes,
        string assetSetId,
        int expectedSchema,
        ImmutableArray<ExternalHeadPartDependencyDescriptor> expectedDescriptors,
        ImmutableArray<ExternalHeadPartFaceGeomExclusionAttestation>
            expectedAttestations,
        CancellationToken cancellationToken)
    {
        await using (var stream = new FileStream(destination.Value,
                         FileMode.CreateNew, FileAccess.Write, FileShare.None,
                         64 * 1024,
                         FileOptions.Asynchronous | FileOptions.WriteThrough))
        {
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        byte[] reopened = await File.ReadAllBytesAsync(
            destination.Value, cancellationToken).ConfigureAwait(false);
        if (!reopened.AsSpan().SequenceEqual(bytes))
            throw new InvalidDataException(
                "Standalone authority did not reopen byte-for-byte.");
        using JsonDocument document = JsonDocument.Parse(reopened);
        if (document.RootElement.GetProperty("schemaVersion").GetInt32() != expectedSchema ||
            !string.Equals(document.RootElement.GetProperty("assetSetId").GetString(),
                assetSetId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "Standalone authority lost its schema or identity on readback.");
        if (expectedSchema == 8)
        {
            JsonElement descriptorRows = document.RootElement.GetProperty(
                "externalHeadPartDependencies");
            JsonElement attestationRows = document.RootElement.GetProperty(
                "externalHeadPartFaceGeomExclusionAttestations");
            if (descriptorRows.ValueKind != JsonValueKind.Array ||
                attestationRows.ValueKind != JsonValueKind.Array ||
                descriptorRows.GetArrayLength() != expectedDescriptors.Length ||
                attestationRows.GetArrayLength() != expectedAttestations.Length)
                throw new InvalidDataException(
                    "Schema-8 standalone authority lost its descriptor or attestation array on readback.");
            for (var index = 0; index < expectedDescriptors.Length; index++)
            {
                ExternalHeadPartDependencyDescriptor descriptor =
                    ExternalHeadPartDependencyDescriptorCodec.ParseDescriptor(
                        Encoding.UTF8.GetBytes(
                            descriptorRows[index].GetRawText()));
                ExternalHeadPartFaceGeomExclusionAttestation attestation =
                    ExternalHeadPartDependencyDescriptorCodec.ParseAttestation(
                        Encoding.UTF8.GetBytes(
                            attestationRows[index].GetRawText()));
                if (!CanonicalDescriptorEquals(
                        descriptor, expectedDescriptors[index]) ||
                    !CanonicalAttestationEquals(
                        attestation, expectedAttestations[index]) ||
                    attestation.DescriptorId != descriptor.DescriptorId)
                    throw new InvalidDataException(
                        "Schema-8 standalone authority changed descriptor or attestation semantics on readback.");
            }
        }
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(reopened)));
    }

    private static bool CanonicalDescriptorEquals(
        ExternalHeadPartDependencyDescriptor left,
        ExternalHeadPartDependencyDescriptor right) =>
        ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(left)
            .AsSpan().SequenceEqual(
                ExternalHeadPartDependencyDescriptorCodec.SerializeDescriptor(right));

    private static bool CanonicalAttestationEquals(
        ExternalHeadPartFaceGeomExclusionAttestation left,
        ExternalHeadPartFaceGeomExclusionAttestation right) =>
        ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(left)
            .AsSpan().SequenceEqual(
                ExternalHeadPartDependencyDescriptorCodec.SerializeAttestation(right));

    private static string BuildAssetSetId(
        RaceMenuPresetStandaloneAuthorityWriteRequest request,
        ImmutableArray<RaceMenuNpcExternalTextureAuthority> external,
        ImmutableArray<BlankNpcTransitivePackageAsset> packageAssets)
    {
        string identity = string.Join('|',
            request.Plan.Request.PresetBundle.ExpectedPresetSha256.Value,
            request.Plan.Request.PresetBundle.ExpectedCharGenFaceGeomSha256.Value,
            request.Plan.Request.PresetBundle.ExpectedCharGenFaceTintSha256.Value,
            request.Plan.RecordAuthoritySha256.Value,
            string.Join(';', external.Select(item =>
                $"{item.DataRelativePath.Value}:{item.ExpectedMemberSha256.Value}")));
        if (request.PrivateHeadTextures is { } head)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject(); WriteHeadTextures(writer, head); writer.WriteEndObject();
            }
            identity += "|" + Encoding.UTF8.GetString(buffer.WrittenSpan) + "|" + string.Join(';', packageAssets.Select(item =>
                $"{item.Destination.Value}:{item.ExpectedSha256.Value}"));
        }
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "selection-assets-" + hash[..24].ToLowerInvariant();
    }

    private string Relative(WorkspacePath path)
    {
        if (!path.IsUnder(labRoot) || path == labRoot)
            throw new InvalidDataException(
                "Standalone authority path escaped the K-local lab root.");
        return new AssetPath(Path.GetRelativePath(labRoot.Value, path.Value)
            .Replace(Path.DirectorySeparatorChar, '/')).Value;
    }

    private bool HasReparsePath(string path)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            if (string.Equals(current, labRoot.Value,
                    StringComparison.OrdinalIgnoreCase)) return false;
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) || string.Equals(parent, current,
                    StringComparison.OrdinalIgnoreCase)) return true;
            current = parent;
        }
    }

    private void TryDeleteOwnedOutput(WorkspacePath path)
    {
        try
        {
            if (path.IsUnder(labRoot) && File.Exists(path.Value) &&
                !File.GetAttributes(path.Value).HasFlag(FileAttributes.ReparsePoint))
                File.Delete(path.Value);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            // The owning transaction can quarantine an incomplete candidate.
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Diagnostic PrivateTextureRefusal(string message) =>
        Error("racemenu-private-head-textures-refused", "standaloneAssets.privateHeadTextures: " + message);

    private static RaceMenuPresetStandaloneAuthorityWriteResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
