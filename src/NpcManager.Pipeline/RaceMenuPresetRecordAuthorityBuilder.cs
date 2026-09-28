using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

/// <summary>
/// Derives every schema-2 record-authority decision from reopened copied
/// records. The builder is read-only; serialization is a separate transaction.
/// </summary>
public sealed class RaceMenuPresetRecordAuthorityBuilder(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader,
    ISkyrimMajorRecordBindingReader majorRecordBindingReader,
    ISkyrimRaceTintAuthorityReader raceTintAuthorityReader,
    ISkyrimFaceTextureSetAuthorityReader faceTextureSetAuthorityReader,
    IRaceMenuPresetTintAuthorityMapper tintAuthorityMapper,
    ISkyrimFaceTextureSetMatchResolver? faceTextureSetMatchResolver = null)
    : IRaceMenuPresetRecordAuthorityBuilder
{
    private static readonly RecordSignature RaceSignature = new("RACE");
    private static readonly RecordSignature HeadPartSignature = new("HDPT");
    private static readonly RecordSignature HeadTextureSignature = new("TXST");
    private static readonly FormId OutputOwnedHairColorFormId = new(0x0000_0801);

    public async ValueTask<RaceMenuPresetRecordAuthorityBuildResult> BuildAsync(
        PresetDocument preset,
        RaceMenuPresetTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateInputs(preset, target, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        SkyrimFaceRecordPluginAuthorityResult reopened =
            await authorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    target.DataRoot,
                    target.PluginOrder.Select(item => item.Plugin).ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(reopened.Diagnostics);
        if (!reopened.Accepted ||
            !MatchesReviewedAuthorities(reopened.Authorities, target.PluginOrder))
        {
            if (reopened.Accepted)
            {
                diagnostics.Add(Error("racemenu-record-authority-stale",
                    "The copied plugin order no longer matches the reviewed paths and SHA-256 values."));
            }
            return Refused(diagnostics);
        }

        var normalizedSelections = ImmutableArray.CreateBuilder<NormalizedHeadPart>(
            preset.Appearance.HeadParts.Length);
        foreach (PresetHeadPart source in preset.Appearance.HeadParts)
        {
            if (source.Identifier.Plugin is not { } plugin ||
                source.Identifier.FormId is not { } formId)
            {
                diagnostics.Add(Error("racemenu-record-headpart-reference",
                    $"Head part '{source.Identifier.Raw}' has no portable plugin-local reference."));
                continue;
            }
            SkyrimFaceRecordPluginAuthority? provider = reopened.Authorities
                .SingleOrDefault(item => string.Equals(item.Plugin.Value,
                    plugin.Value, StringComparison.OrdinalIgnoreCase));
            if (provider is null)
            {
                diagnostics.Add(Error("racemenu-record-headpart-provider",
                    $"Head part '{source.Identifier.Raw}' has no reviewed source plugin."));
                continue;
            }
            FormReference? normalized = await NormalizeSourceReferenceAsync(
                new FormReference(plugin, formId), provider, diagnostics, cancellationToken)
                .ConfigureAwait(false);
            if (normalized is not null)
                normalizedSelections.Add(new NormalizedHeadPart(source, normalized.Value));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        string? rawHeadTexture = preset.Appearance.RaceMenu?.HeadTexture;
        FormReference? normalizedTexture = null;
        FormReference? headTextureSource = null;
        SkyrimFaceTextureSetAuthority? matchedTextureAuthority = null;
        if (!string.IsNullOrWhiteSpace(rawHeadTexture))
        {
            PresetIdentifier headTextureId = PresetIdentifier.Parse(rawHeadTexture);
            if (headTextureId.Plugin is not { } texturePlugin ||
                headTextureId.FormId is not { } textureFormId)
            {
                diagnostics.Add(Error("racemenu-record-head-texture-reference",
                    "The RaceMenu headTexture value is not one portable TXST reference."));
                return Refused(diagnostics);
            }
            SkyrimFaceRecordPluginAuthority? textureSourceProvider = reopened.Authorities
                .SingleOrDefault(item => string.Equals(item.Plugin.Value,
                    texturePlugin.Value, StringComparison.OrdinalIgnoreCase));
            if (textureSourceProvider is null)
            {
                diagnostics.Add(Error("racemenu-record-head-texture-provider",
                    $"Head texture '{headTextureId.Raw}' has no reviewed source plugin."));
                return Refused(diagnostics);
            }
            headTextureSource = new FormReference(texturePlugin, textureFormId);
            normalizedTexture = await NormalizeSourceReferenceAsync(
                headTextureSource.Value,
                textureSourceProvider,
                diagnostics,
                cancellationToken).ConfigureAwait(false);
            if (normalizedTexture is not null &&
                preset.Appearance.RaceMenu is
                { FaceTextures.IsDefaultOrEmpty: false } explicitDirect &&
                faceTextureSetMatchResolver is not null)
            {
                SkyrimFaceTextureSetMatchResult matched =
                    await faceTextureSetMatchResolver.ResolveAsync(
                        new SkyrimFaceTextureSetMatchRequest(
                            GameEdition.SkyrimSpecialEdition,
                            target.DataRoot,
                            explicitDirect.FaceTextures,
                            reopened.Authorities,
                            normalizedTexture),
                        cancellationToken).ConfigureAwait(false);
                diagnostics.AddRange(matched.Diagnostics);
                if (matched.Accepted && matched.Authority is { } authority)
                {
                    matchedTextureAuthority = authority;
                    diagnostics.Add(new Diagnostic(
                        "racemenu-record-head-texture-direct",
                        DiagnosticSeverity.Info,
                        $"Explicit TXST {authority.TextureSet} supplies typed identity while the direct JSlot faceTextures own the output texture paths."));
                }
            }
        }
        else if (preset.Appearance.RaceMenu is
        { FaceTextures.IsDefaultOrEmpty: false } raceMenu &&
                 faceTextureSetMatchResolver is not null)
        {
            SkyrimFaceTextureSetMatchResult matched =
                await faceTextureSetMatchResolver.ResolveAsync(
                    new SkyrimFaceTextureSetMatchRequest(
                        GameEdition.SkyrimSpecialEdition,
                        target.DataRoot,
                        raceMenu.FaceTextures,
                        reopened.Authorities),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(matched.Diagnostics);
            if (matched.Accepted && matched.Authority is { } authority)
            {
                normalizedTexture = authority.TextureSet;
                headTextureSource = authority.TextureSet;
                matchedTextureAuthority = authority;
                diagnostics.Add(new Diagnostic(
                    "racemenu-record-head-texture-direct",
                    DiagnosticSeverity.Info,
                    $"Direct JSlot faceTextures selected typed TXST carrier {authority.TextureSet}; the JSlot owns the output texture paths."));
            }
        }
        else
        {
            diagnostics.Add(Error("racemenu-record-head-texture-reference",
                "The RaceMenu preset has neither a portable headTexture TXST reference nor direct faceTextures that can be resolved by the product."));
        }
        if (normalizedTexture is null || headTextureSource is null ||
            HasErrors(diagnostics))
        {
            if (preset.Appearance.RaceMenu is { FaceTextures.IsDefaultOrEmpty: false } &&
                diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error &&
                    item.Code.StartsWith("skyrim-face-texture-match-", StringComparison.Ordinal)))
                diagnostics.Add(Error("racemenu-private-head-textures-refused",
                    "faceTextures: private paths require a matching copied TXST carrier for direct import. Use a valid explicit headTexture with standaloneAssets.privateHeadTextures for private output paths."));
            return Refused(diagnostics);
        }

        var bindingSelections = ImmutableArray.CreateBuilder<SkyrimMajorRecordBindingSelection>(
            normalizedSelections.Count + 2);
        bindingSelections.Add(new SkyrimMajorRecordBindingSelection(
            target.Race, RaceSignature));
        bindingSelections.AddRange(normalizedSelections.Select(item =>
            new SkyrimMajorRecordBindingSelection(item.Reference, HeadPartSignature)));
        bindingSelections.Add(new SkyrimMajorRecordBindingSelection(
            normalizedTexture.Value, HeadTextureSignature));
        SkyrimMajorRecordBindingResult bound = await majorRecordBindingReader.ReadAsync(
            new SkyrimMajorRecordBindingRequest(
                GameEdition.SkyrimSpecialEdition,
                target.DataRoot,
                bindingSelections.ToImmutable(),
                reopened.Authorities),
            cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(bound.Diagnostics);
        if (!bound.Accepted) return Refused(diagnostics);

        SkyrimMajorRecordBinding raceRecord = bound.Bindings.Single(item =>
            SameReference(item.Reference, target.Race));
        RaceMenuNpcFormBinding? raceBinding = ToBinding(
            RaceSignature,
            target.Race,
            target.Race,
            raceRecord.Provider,
            null);
        var headParts = ImmutableArray.CreateBuilder<RaceMenuPresetHeadPartAuthority>(
            normalizedSelections.Count);
        foreach (NormalizedHeadPart selected in normalizedSelections)
        {
            SkyrimMajorRecordBinding route = bound.Bindings.Single(item =>
                SameReference(item.Reference, selected.Reference));
            RaceMenuNpcFormBinding? binding = ToBinding(
                HeadPartSignature,
                SourceReference(selected.Source.Identifier),
                route.Reference,
                route.Provider,
                route.HeadPartType);
            if (binding is not null)
                headParts.Add(new RaceMenuPresetHeadPartAuthority(selected.Source, binding));
        }

        SkyrimFaceTextureSetAuthority textureAuthority;
        if (matchedTextureAuthority is not null)
        {
            textureAuthority = matchedTextureAuthority;
        }
        else
        {
            diagnostics.Add(new Diagnostic(
                "racemenu-record-head-texture-explicit",
                DiagnosticSeverity.Info,
                "Reading the required texture paths from the JSlot's explicit headTexture TXST."));
            SkyrimFaceTextureSetAuthorityResult textureResult =
                await faceTextureSetAuthorityReader.ReadAsync(
                    new SkyrimFaceTextureSetAuthorityRequest(
                        GameEdition.SkyrimSpecialEdition,
                        target.DataRoot,
                        normalizedTexture.Value,
                        reopened.Authorities),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(textureResult.Diagnostics);
            if (!textureResult.Accepted || textureResult.Authority is null)
                return Refused(diagnostics);
            textureAuthority = textureResult.Authority;
        }
        SkyrimMajorRecordBinding textureRecord = bound.Bindings.Single(item =>
            SameReference(item.Reference, normalizedTexture.Value));
        if (textureRecord.Provider != textureAuthority.Provider)
        {
            diagnostics.Add(Error("racemenu-record-head-texture-provider-drift",
                "The TXST identity and texture-path readers selected different winning providers."));
            return Refused(diagnostics);
        }
        RaceMenuNpcFormBinding? textureBinding = ToBinding(
            HeadTextureSignature,
            headTextureSource.Value,
            textureRecord.Reference,
            textureRecord.Provider,
            null);

        SkyrimRaceTintAuthorityResult tintAuthority =
            await raceTintAuthorityReader.ReadAsync(
                new SkyrimRaceTintAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    target.DataRoot,
                    target.Race,
                    target.Sex,
                    reopened.Authorities),
                cancellationToken).ConfigureAwait(false);
        diagnostics.AddRange(tintAuthority.Diagnostics);
        if (!tintAuthority.Accepted || tintAuthority.Authority is null)
            return Refused(diagnostics);
        RaceMenuPresetTintAuthorityPlanResult tintPlan = tintAuthorityMapper.Map(
            preset, tintAuthority.Authority);
        diagnostics.AddRange(tintPlan.Diagnostics);
        if (!tintPlan.Accepted || tintPlan.Plan is null)
            return Refused(diagnostics);

        uint? packedHairColor = preset.Appearance.HairColor?.PackedRgb;
        if (packedHairColor is null || packedHairColor.Value > 0x00FF_FFFF)
        {
            diagnostics.Add(Error("racemenu-record-hair-color",
                "A new preset-derived NPC requires one packed 24-bit RaceMenu hair color."));
        }
        if (headParts.Count != preset.Appearance.HeadParts.Length ||
            headParts.Count(item => item.Binding.HeadPartType == NpcHeadPartType.Face) != 1)
        {
            diagnostics.Add(Error("racemenu-record-headpart-coverage",
                "Record authority requires every source head part and exactly one typed Face HDPT."));
        }
        if (raceBinding is null || textureBinding is null || packedHairColor is null ||
            HasErrors(diagnostics))
            return Refused(diagnostics);

        string authorityId = BuildAuthorityId(preset, target);
        return new RaceMenuPresetRecordAuthorityBuildResult(
            true,
            new RaceMenuPresetRecordAuthorityDraft(
                authorityId,
                preset,
                target,
                raceBinding,
                headParts.ToImmutable(),
                textureBinding,
                textureAuthority,
                packedHairColor.GetValueOrDefault(),
                OutputOwnedHairColorFormId,
                tintPlan.Plan,
                RuntimeAuthority: false),
            diagnostics.ToImmutable());
    }

    private static RaceMenuNpcFormBinding? ToBinding(
        RecordSignature signature,
        FormReference source,
        FormReference providerReference,
        SkyrimFaceRecordProvider provider,
        NpcHeadPartType? headPartType)
    {
        return new RaceMenuNpcFormBinding(
            signature,
            source,
            providerReference,
            provider.Plugin,
            provider.Path,
            provider.Sha256,
            headPartType);
    }

    private static async ValueTask<FormReference?> NormalizeSourceReferenceAsync(
        FormReference source,
        SkyrimFaceRecordPluginAuthority provider,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(
                provider.Path.Value,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var header = new byte[12];
            await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
            if (!header.AsSpan(0, 4).SequenceEqual("TES4"u8))
                throw new InvalidDataException("The provider does not begin with a TES4 record.");
            bool isLightMaster =
                (BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4)) & 0x0000_0200U) != 0;
            uint localId = source.FormId.Value;
            if (isLightMaster)
                localId &= 0x0000_0FFF;
            if (localId == 0)
                throw new InvalidDataException("The normalized provider-local FormID is zero.");
            return new FormReference(source.Plugin, new FormId(localId));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           InvalidDataException or ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("racemenu-record-source-normalization",
                $"Source reference {source} could not be normalized from '{provider.Path.Value}': {exception.Message}"));
            return null;
        }
    }

    private static void ValidateInputs(
        PresetDocument preset,
        RaceMenuPresetTarget target,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (preset.Format != PresetFormat.RaceMenuJslot ||
            preset.Edition != GameEdition.SkyrimSpecialEdition ||
            !preset.IsValid || preset.Appearance.RaceMenu is null)
            diagnostics.Add(Error("racemenu-record-preset",
                "Record authority requires one valid typed Skyrim SE RaceMenu preset."));
        if (string.IsNullOrWhiteSpace(target.AuthorityId) ||
            target.PluginOrder.IsDefaultOrEmpty ||
            !Enum.IsDefined(target.Sex) ||
            string.IsNullOrWhiteSpace(target.Race.Plugin.Value) ||
            target.Race.FormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("racemenu-record-target",
                "Record authority requires one complete reviewed race, sex, and plugin-order target."));
    }

    private static bool MatchesReviewedAuthorities(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> current,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> reviewed)
    {
        if (current.Length != reviewed.Length) return false;
        for (var index = 0; index < current.Length; index++)
        {
            if (!string.Equals(current[index].Plugin.Value,
                    reviewed[index].Plugin.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current[index].Path.Value,
                    reviewed[index].Path.Value, StringComparison.OrdinalIgnoreCase) ||
                current[index].ExpectedSha256 != reviewed[index].ExpectedSha256)
                return false;
        }
        return true;
    }

    private static FormReference SourceReference(PresetIdentifier identifier) =>
        new(identifier.Plugin ?? throw new InvalidDataException("Preset source plugin is absent."),
            identifier.FormId ?? throw new InvalidDataException("Preset source FormID is absent."));

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(left.Plugin.Value,
            right.Plugin.Value, StringComparison.OrdinalIgnoreCase);

    private static string BuildAuthorityId(
        PresetDocument preset,
        RaceMenuPresetTarget target)
    {
        string identity = $"{preset.SourceHash.Value}|{target.AuthorityId}|{target.Race}|{target.Sex}";
        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return "selection-record-" + hash[..24].ToLowerInvariant();
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuPresetRecordAuthorityBuildResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private sealed record NormalizedHeadPart(
        PresetHeadPart Source,
        FormReference Reference);
}
