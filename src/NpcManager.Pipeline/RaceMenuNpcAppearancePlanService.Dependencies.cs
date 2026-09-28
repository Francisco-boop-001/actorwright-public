using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class RaceMenuNpcAppearancePlanService
{
    private static readonly RecordSignature RaceSignature = new("RACE");
    private static readonly RecordSignature HeadPartSignature = new("HDPT");
    private static readonly RecordSignature HeadTextureSignature = new("TXST");
    private static readonly RecordSignature HairColorSignature = new("CLFM");

    private async ValueTask<RecordAuthorityBinding?> ReadRecordAuthorityAsync(
        RaceMenuNpcRecordAuthority authority,
        RaceMenuNpcBuildRequest request,
        PresetAppearance appearance,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var (document, manifestHash) = await ReadJsonAsync(
            authority.ManifestPath,
            authority.ExpectedManifestSha256,
            "record-authority-manifest",
            diagnostics,
            cancellationToken);
        if (document is null || manifestHash is null) return null;

        using (document)
        {
            try
            {
                var parsed = ParseRecordAuthority(
                    document.RootElement,
                    manifestHash.Value,
                    request,
                    appearance);
                foreach (var binding in parsed.FormBindings.Where(item => item.HasExtendedHeadPartType))
                {
                    diagnostics.Add(new Diagnostic(
                        "racemenu-plan-record-authority-extended-head-part-type",
                        DiagnosticSeverity.Info,
                        $"Record authority binds HDPT {binding.Reference.Plugin.Value}|0x{binding.Reference.FormId.Value:X8} " +
                        $"with mod-defined PNAM type {binding.HeadPartRawType}; it is routed as a " +
                        $"{binding.HeadPartType?.ToWireName()} part by its model and flags."));
                }
                await VerifyFormProvidersAsync(parsed, diagnostics, cancellationToken);
                return parsed;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception) when (exception is InvalidDataException or ArgumentException or
                                               IOException or UnauthorizedAccessException)
            {
                diagnostics.Add(Error("racemenu-plan-record-authority-invalid", exception.Message));
                return null;
            }
        }
    }

    private RecordAuthorityBinding ParseRecordAuthority(
        JsonElement root,
        Sha256Hash manifestHash,
        RaceMenuNpcBuildRequest request,
        PresetAppearance appearance)
    {
        RequireShape(root, "RaceMenu record authority", "schemaVersion", "authorityId", "edition",
            "race", "sex", "formBindings", "headPartDispositions", "hairColorAuthority",
            "tintMappings", "qnam");
        var schemaVersion = RequiredInt(root, "schemaVersion");
        if (schemaVersion is not (1 or 2))
            throw new InvalidDataException("RaceMenu record authority schemaVersion must be 1 or 2.");
        var authorityId = RequiredString(root, "authorityId");
        if (authorityId.Length > 128)
            throw new InvalidDataException("RaceMenu record authorityId may not exceed 128 characters.");
        if (!GameEditionExtensions.TryParseWireName(RequiredString(root, "edition"), out var edition) ||
            edition != request.Edition || edition != GameEdition.SkyrimSpecialEdition)
        {
            throw new InvalidDataException("RaceMenu record authority edition does not match the Skyrim SE request.");
        }

        var sex = ParseSex(RequiredString(root, "sex"));
        if (sex != request.Traits.Sex)
            throw new InvalidDataException("RaceMenu record authority sex does not match the explicit build request.");

        var race = ReadFormBinding(
            root.GetProperty("race"), [RaceSignature], "race binding", schemaVersion);
        if (!SameReference(race.Reference, request.References.Race))
            throw new InvalidDataException("RaceMenu record authority race does not match the explicit build request.");

        ValidateProvenanceMetadata(appearance);

        var formsElement = root.GetProperty("formBindings");
        if (formsElement.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RaceMenu record authority formBindings must be an array.");
        var forms = ImmutableArray.CreateBuilder<RaceMenuNpcFormBinding>();
        foreach (var item in formsElement.EnumerateArray())
        {
            var binding = ReadFormBinding(item,
                [HeadPartSignature, HeadTextureSignature, HairColorSignature],
                "form binding", schemaVersion);
            if (forms.Any(existing =>
                    SameReference(existing.SourceReference, binding.SourceReference) ||
                    SameReference(existing.Reference, binding.Reference)))
            {
                throw new InvalidDataException(
                    $"Record authority source/provider binding '{binding.SourceReference}' -> " +
                    $"'{binding.Reference}' is duplicated.");
            }
            forms.Add(binding);
        }

        var headPartDispositions = ReadHeadPartDispositions(
            root.GetProperty("headPartDispositions"), appearance, forms.ToImmutable(),
            request.PresetBundle.ExpectedCharGenFaceGeomSha256);
        var hairAuthority = ReadHairColorAuthority(
            root.GetProperty("hairColorAuthority"), appearance, manifestHash);
        var tintDispositions = ReadTintMappings(
            root.GetProperty("tintMappings"), appearance,
            request.PresetBundle.ExpectedCharGenFaceGeomSha256,
            request.PresetBundle.ExpectedCharGenFaceTintSha256);
        var tintLayers = tintDispositions
            .Where(disposition => disposition.Kind == RaceMenuNpcTintDispositionKind.MappedRecord)
            .Select(disposition => disposition.MappedLayer ??
                throw new InvalidDataException("Mapped tint disposition lost its typed layer."))
            .ToImmutableArray();
        var qnam = ReadQnam(root.GetProperty("qnam"), tintLayers, manifestHash);
        return new RecordAuthorityBinding(authorityId, manifestHash, race, forms.ToImmutable(),
            headPartDispositions, hairAuthority, tintDispositions, tintLayers, qnam);
    }

    private static void ValidateProvenanceMetadata(PresetAppearance appearance)
    {
        var raceMenu = appearance.RaceMenu;
        if (raceMenu is null) return;

        if (!raceMenu.Mods.IsDefaultOrEmpty && raceMenu.ModNames.IsDefaultOrEmpty)
        {
            throw new InvalidDataException(
                "RaceMenu indexed mods metadata requires the companion modNames dependency list.");
        }

        var declared = raceMenu.ModNames.Select(plugin => plugin.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var explained = raceMenu.Mods.Select(entry => entry.Name.Value)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in raceMenu.Mods)
        {
            if (!declared.Contains(entry.Name.Value))
            {
                throw new InvalidDataException(
                    $"RaceMenu indexed mod '{entry.Name.Value}' is absent from the modNames dependency list.");
            }
        }
        foreach (var headPart in appearance.HeadParts)
        {
            if (headPart.Identifier.Plugin is { } plugin) explained.Add(plugin.Value);
        }
        if (!string.IsNullOrWhiteSpace(raceMenu.HeadTexture))
        {
            var identifier = PresetIdentifier.Parse(raceMenu.HeadTexture);
            if (identifier.Plugin is { } plugin) explained.Add(plugin.Value);
        }
        var unexplained = declared.FirstOrDefault(plugin => !explained.Contains(plugin));
        if (unexplained is not null)
        {
            throw new InvalidDataException(
                $"RaceMenu modNames dependency '{unexplained}' is not explained by indexed mods or a portable appearance identifier.");
        }
    }

    private static ImmutableArray<RaceMenuNpcHeadPartDisposition> ReadHeadPartDispositions(
        JsonElement element,
        PresetAppearance appearance,
        ImmutableArray<RaceMenuNpcFormBinding> bindings,
        Sha256Hash faceGeomHash)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RaceMenu headPartDispositions must be an array.");
        var dispositions = ImmutableArray.CreateBuilder<RaceMenuNpcHeadPartDisposition>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each head-part disposition must be an object.");
            var rawSource = RequiredString(item, "sourceFormKey");
            if (!FormReference.TryParse(rawSource, out var parsedSource) ||
                parsedSource.FormId.Value is 0 or > 0x00FF_FFFF)
            {
                throw new InvalidDataException(
                    $"Head-part disposition sourceFormKey '{rawSource}' is invalid.");
            }
            var sourceReference = parsedSource;
            if (!seen.Add(BindingKey(sourceReference)))
                throw new InvalidDataException(
                    $"Head-part disposition for '{sourceReference}' is duplicated.");

            var source = appearance.HeadParts.SingleOrDefault(headPart =>
                headPart.Identifier.Plugin is { } plugin &&
                headPart.Identifier.FormId is { } formId &&
                formId == sourceReference.FormId &&
                string.Equals(plugin.Value, sourceReference.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase)) ??
                throw new InvalidDataException(
                    $"Head-part disposition references absent source '{sourceReference}'.");
            var disposition = RequiredString(item, "disposition");
            if (string.Equals(disposition, "mapped-record", StringComparison.Ordinal))
            {
                RequireShape(item, "mapped head-part disposition",
                    "sourceFormKey", "disposition");
                var matches = bindings.Where(binding =>
                    binding.Signature == HeadPartSignature &&
                    SameReference(binding.SourceReference, sourceReference)).ToArray();
                if (matches.Length != 1)
                    throw new InvalidDataException(
                        $"Mapped head-part '{sourceReference}' requires exactly one HDPT binding.");
                dispositions.Add(new RaceMenuNpcHeadPartDisposition(source,
                    RaceMenuNpcHeadPartDispositionKind.MappedRecord,
                    new RaceMenuResolvedHeadPart(source, matches[0]), null));
            }
            else if (string.Equals(disposition, "baked", StringComparison.Ordinal))
            {
                RequireShape(item, "baked head-part disposition",
                    "sourceFormKey", "disposition");
                if (bindings.Any(binding => binding.Signature == HeadPartSignature &&
                                            SameReference(binding.SourceReference, sourceReference)))
                {
                    throw new InvalidDataException(
                        $"Baked head-part '{sourceReference}' may not also declare an HDPT binding.");
                }
                dispositions.Add(new RaceMenuNpcHeadPartDisposition(source,
                    RaceMenuNpcHeadPartDispositionKind.Baked, null, faceGeomHash));
            }
            else
            {
                throw new InvalidDataException(
                    $"Head-part '{sourceReference}' has unsupported disposition '{disposition}'.");
            }
        }

        if (dispositions.Count != appearance.HeadParts.Length ||
            appearance.HeadParts.Any(headPart => headPart.Identifier.Plugin is not { } plugin ||
                headPart.Identifier.FormId is not { } formId ||
                !seen.Contains(BindingKey(new FormReference(plugin, formId)))))
        {
            throw new InvalidDataException(
                "Every .jslot head-part row must have exactly one mapped or baked disposition.");
        }
        var admitted = dispositions.ToImmutable();
        return appearance.HeadParts
            .Select(source => admitted.Single(disposition => disposition.Source == source))
            .ToImmutableArray();
    }

    private RaceMenuNpcFormBinding ReadFormBinding(
        JsonElement element,
        ImmutableArray<RecordSignature> allowedSignatures,
        string role,
        int schemaVersion)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException($"The {role} must be an object.");
        var signature = new RecordSignature(RequiredString(element, "signature"));
        if (!allowedSignatures.Contains(signature))
            throw new InvalidDataException(
                $"{role} signature '{signature}' is unknown; expected {string.Join(", ", allowedSignatures)}.");
        if (signature == HeadPartSignature)
        {
            if (schemaVersion == 2)
                RequireShape(element, role, "signature", "sourceFormKey", "providerFormKey",
                    "providerPluginName", "providerPluginPath", "providerPluginSha256", "headPartType");
            else
                RequireShape(element, role, "signature", "sourceFormKey", "providerFormKey",
                    "providerPluginPath", "providerPluginSha256", "headPartType");
        }
        else
        {
            if (schemaVersion == 2)
                RequireShape(element, role, "signature", "sourceFormKey", "providerFormKey",
                    "providerPluginName", "providerPluginPath", "providerPluginSha256");
            else
                RequireShape(element, role, "signature", "sourceFormKey", "providerFormKey",
                    "providerPluginPath", "providerPluginSha256");
        }

        NpcHeadPartType? headPartType = null;
        uint? headPartRawType = null;
        if (signature == HeadPartSignature)
        {
            var rawType = RequiredString(element, "headPartType");
            if (uint.TryParse(rawType, NumberStyles.None, CultureInfo.InvariantCulture, out var pnam))
            {
                // Numeric values carry the HDPT PNAM verbatim so mod-defined
                // types beyond the closed 0..9 buckets are admitted and routed
                // by model instead of refused (Rachel R2: headPartType "71").
                headPartRawType = pnam;
                headPartType = NpcHeadPartTypeExtensions.FromPnam(pnam);
            }
            else if (NpcHeadPartTypeExtensions.TryParseWireName(rawType, out var parsedType))
            {
                headPartType = parsedType;
            }
            else
            {
                throw new InvalidDataException(
                    $"{role} headPartType '{rawType}' is not a supported Skyrim HDPT type.");
            }
        }

        var rawSource = RequiredString(element, "sourceFormKey");
        if (!FormReference.TryParse(rawSource, out var parsedSource) ||
            parsedSource.FormId.Value is 0 or > 0x00FF_FFFF)
        {
            throw new InvalidDataException(
                $"{role} sourceFormKey '{rawSource}' is not a portable RaceMenu reference.");
        }
        var rawProvider = RequiredString(element, "providerFormKey");
        if (!FormReference.TryParse(rawProvider, out var parsedProvider) ||
            parsedProvider.FormId.Value is 0 or > 0x00FF_FFFF)
        {
            throw new InvalidDataException(
                $"{role} providerFormKey '{rawProvider}' is not a nonzero plugin-local reference.");
        }
        if (!string.Equals(parsedSource.Plugin.Value, parsedProvider.Plugin.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{role} source and provider FormKeys must retain the same originating plugin.");
        }

        var providerPluginName = schemaVersion == 2
            ? new PluginName(RequiredString(element, "providerPluginName"))
            : parsedProvider.Plugin;
        var providerPlugin = ResolveRelative(RequiredString(element, "providerPluginPath"),
            $"{role} provider plugin");
        if (!string.Equals(Path.GetFileName(providerPlugin.Value), providerPluginName.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"{role} provider filename does not match winning plugin '{providerPluginName.Value}'.");
        }
        return new RaceMenuNpcFormBinding(signature,
            parsedSource,
            parsedProvider,
            providerPluginName,
            providerPlugin, new Sha256Hash(RequiredString(element, "providerPluginSha256")),
            headPartType)
        {
            HeadPartRawType = headPartRawType
        };
    }

    private static HairColorAuthorityBinding? ReadHairColorAuthority(
        JsonElement element,
        PresetAppearance appearance,
        Sha256Hash authorityHash)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (appearance.HairColor is not null)
                throw new InvalidDataException(
                    "RaceMenu hair color requires an explicit closed authority route.");
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("RaceMenu hairColorAuthority must be an object or null.");
        var kind = RequiredString(element, "kind");
        if (string.Equals(kind, "external-clfm", StringComparison.Ordinal))
        {
            RequireShape(element, "external hair-color authority", "kind", "packedRgb",
                "providerFormKey");
        }
        else if (string.Equals(kind, "output-owned-clfm", StringComparison.Ordinal))
        {
            RequireShape(element, "output-owned hair-color authority", "kind", "packedRgb",
                "allocatedLocalFormId");
        }
        else
        {
            throw new InvalidDataException(
                $"RaceMenu hairColorAuthority kind '{kind}' is unsupported.");
        }

        if (!element.GetProperty("packedRgb").TryGetUInt32(out var packedRgb) ||
            packedRgb > 0x00FF_FFFF)
        {
            throw new InvalidDataException("Hair-color authority packedRgb must be from 0 through 0xFFFFFF.");
        }
        if (appearance.HairColor?.PackedRgb is not { } source || source != packedRgb)
            throw new InvalidDataException(
                "Hair-color authority does not match the packed RGB value in the .jslot.");

        if (string.Equals(kind, "external-clfm", StringComparison.Ordinal))
        {
            var raw = RequiredString(element, "providerFormKey");
            if (!FormReference.TryParse(raw, out var reference) ||
                reference.FormId.Value is 0 or > 0x00FF_FFFF)
            {
                throw new InvalidDataException(
                    "External hair-color providerFormKey is not plugin-local.");
            }
            return new ExternalHairColorAuthorityBinding(packedRgb, reference);
        }

        var rawLocalFormId = RequiredString(element, "allocatedLocalFormId");
        if (!FormId.TryParse(rawLocalFormId, out var allocatedLocalFormId) ||
            allocatedLocalFormId.Value != 0x0000_0801)
        {
            throw new InvalidDataException(
                "Output-owned hair-color authority must allocate deterministic local FormID 0x00000801.");
        }
        return new OutputOwnedHairColorAuthorityBinding(
            packedRgb, allocatedLocalFormId, authorityHash);
    }

    private static ImmutableArray<RaceMenuNpcTintDisposition> ReadTintMappings(
        JsonElement element,
        PresetAppearance appearance,
        Sha256Hash faceGeomHash,
        Sha256Hash faceTintHash)
    {
        if (element.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("RaceMenu tintMappings must be an array.");
        if (appearance.Tints.Any(tint => tint.Index is < 0 or > ushort.MaxValue))
        {
            throw new InvalidDataException(
                "RaceMenu tint indexes must be within the TINI source range.");
        }
        if (appearance.Tints.GroupBy(tint => tint.Index).Any(group => group.Count() != 1))
            throw new InvalidDataException("RaceMenu .jslot tint indexes must be unique.");

        var dispositions = ImmutableArray.CreateBuilder<RaceMenuNpcTintDisposition>();
        var dispositionSourceIndexes = new HashSet<int>();
        var mappedTiniIndexes = new HashSet<ushort>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Each tint disposition must be an object.");
            var sourceIndex = RequiredInt(item, "jslotIndex");
            if (sourceIndex is < 0 or > ushort.MaxValue ||
                !dispositionSourceIndexes.Add(sourceIndex))
                throw new InvalidDataException(
                    $"Tint disposition for .jslot index {sourceIndex} is invalid or duplicated.");
            var source = appearance.Tints.SingleOrDefault(tint => tint.Index == sourceIndex) ??
                throw new InvalidDataException(
                    $"Tint disposition references absent .jslot tint index {sourceIndex}.");
            var sourceAlpha = (byte)(source.Color >> 24);
            var disposition = RequiredString(item, "disposition");
            if (string.Equals(disposition, "mapped-record", StringComparison.Ordinal))
            {
                RequireShape(item, "mapped tint disposition", "jslotIndex", "disposition",
                    "tiniIndex", "tiasPresetIndex", "skinTint");
                if (sourceAlpha == 0)
                    throw new InvalidDataException(
                        $"Alpha-zero tint index {sourceIndex} must be inactive, not mapped.");
                var tini = RequiredInt(item, "tiniIndex");
                var tias = RequiredInt(item, "tiasPresetIndex");
                var skinTint = RequiredBool(item, "skinTint");
                if (tini is < 0 or > ushort.MaxValue || tias is < short.MinValue or > short.MaxValue)
                    throw new InvalidDataException("Tint mapping values exceed the TINI or TIAS wire ranges.");
                if (!mappedTiniIndexes.Add((ushort)tini))
                    throw new InvalidDataException($"Tint mapping TINI index {tini} is duplicated.");

                var coverage = checked((uint)Math.Round(
                    sourceAlpha * 100D / byte.MaxValue, MidpointRounding.AwayFromZero));
                var red = (byte)((source.Color >> 16) & 0xFF);
                var green = (byte)((source.Color >> 8) & 0xFF);
                var blue = (byte)(source.Color & 0xFF);
                var layer = new RaceMenuNpcTintLayerBinding(source,
                    new SkyrimFaceTintLayer((ushort)tini, red, green, blue, Alpha: 0,
                        coverage, (short)tias), skinTint);
                dispositions.Add(new RaceMenuNpcTintDisposition(source,
                    RaceMenuNpcTintDispositionKind.MappedRecord, layer, null, null));
            }
            else if (string.Equals(disposition, "baked", StringComparison.Ordinal))
            {
                RequireShape(item, "baked tint disposition", "jslotIndex", "disposition");
                if (sourceAlpha == 0)
                    throw new InvalidDataException(
                        $"Alpha-zero tint index {sourceIndex} must be inactive, not baked.");
                dispositions.Add(new RaceMenuNpcTintDisposition(source,
                    RaceMenuNpcTintDispositionKind.Baked, null, faceGeomHash, faceTintHash));
            }
            else if (string.Equals(disposition, "inactive", StringComparison.Ordinal))
            {
                RequireShape(item, "inactive tint disposition", "jslotIndex", "disposition");
                if (sourceAlpha != 0)
                    throw new InvalidDataException(
                        $"Tint index {sourceIndex} may be inactive only when source alpha is zero.");
                dispositions.Add(new RaceMenuNpcTintDisposition(source,
                    RaceMenuNpcTintDispositionKind.Inactive, null, null, null));
            }
            else
            {
                throw new InvalidDataException(
                    $"Tint index {sourceIndex} has unsupported disposition '{disposition}'.");
            }
        }
        if (dispositionSourceIndexes.Count != appearance.Tints.Length ||
            appearance.Tints.Any(tint => !dispositionSourceIndexes.Contains(tint.Index)))
        {
            throw new InvalidDataException(
                "Every .jslot tint entry must have exactly one mapped, baked, or inactive disposition.");
        }
        var admitted = dispositions.ToImmutable();
        return appearance.Tints
            .Select(source => admitted.Single(disposition => disposition.Source == source))
            .ToImmutableArray();
    }

    private static RaceMenuNpcQnamDerivation? ReadQnam(
        JsonElement element,
        ImmutableArray<RaceMenuNpcTintLayerBinding> tintLayers,
        Sha256Hash authorityHash)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (!tintLayers.IsDefaultOrEmpty)
                throw new InvalidDataException("Tinted NPCs require an explicit QNAM derivation source.");
            return null;
        }
        RequireShape(element, "QNAM derivation", "source", "jslotIndex", "red", "green", "blue");
        if (!string.Equals(RequiredString(element, "source"), "mapped-skin-tint",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("QNAM source must be 'mapped-skin-tint'.");
        }
        var sourceIndex = RequiredInt(element, "jslotIndex");
        var skinLayers = tintLayers.Where(layer => layer.IsSkinTint).ToArray();
        if (skinLayers.Length != 1 || skinLayers[0].Source.Index != sourceIndex)
            throw new InvalidDataException(
                "QNAM requires exactly one mapped skin tint and must identify that .jslot index.");

        var red = RequiredUnitDouble(element, "red");
        var green = RequiredUnitDouble(element, "green");
        var blue = RequiredUnitDouble(element, "blue");
        var source = skinLayers[0].Source.Color;
        var expectedRed = ((source >> 16) & 0xFF) / 255D;
        var expectedGreen = ((source >> 8) & 0xFF) / 255D;
        var expectedBlue = (source & 0xFF) / 255D;
        const double tolerance = 0.000001D;
        if (Math.Abs(red - expectedRed) > tolerance || Math.Abs(green - expectedGreen) > tolerance ||
            Math.Abs(blue - expectedBlue) > tolerance)
        {
            throw new InvalidDataException(
                "QNAM channels must equal the mapped skin tint RGB channels divided by 255.");
        }
        return new RaceMenuNpcQnamDerivation(RaceMenuNpcQnamSourceKind.MappedSkinTint,
            sourceIndex, (float)red, (float)green, (float)blue, authorityHash);
    }

    private async ValueTask VerifyFormProvidersAsync(
        RecordAuthorityBinding authority,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var bindings = authority.FormBindings.Add(authority.RaceBinding);
        foreach (var group in bindings.GroupBy(binding => binding.ProviderPlugin.Value,
                     StringComparer.OrdinalIgnoreCase))
        {
            var hashes = group.Select(binding => binding.ProviderPluginSha256).Distinct().ToArray();
            if (hashes.Length != 1)
                throw new InvalidDataException(
                    $"Provider plugin '{group.Key}' is bound with conflicting hashes.");
            var providerNames = group.Select(binding => binding.ProviderPluginName)
                .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            if (providerNames.Length != 1)
                throw new InvalidDataException(
                    $"Provider path '{group.Key}' is bound with conflicting plugin names.");
            var providerName = providerNames[0];
            var path = group.First().ProviderPlugin;
            if (!await VerifyBoundFileAsync(path, hashes[0], MaximumPluginBytes,
                    BundleFileKind.FormProviderPlugin, diagnostics, cancellationToken))
            {
                continue;
            }

            try
            {
                var providerIndex = await ReadProviderRecordsAsync(
                    path, providerName, cancellationToken);
                foreach (var binding in group)
                {
                    if (!ValidateSourceProviderMapping(binding, providerIndex,
                            diagnostics))
                    {
                        continue;
                    }
                    if (!providerIndex.Records.TryGetValue(
                            ProviderRecordKey(binding.Reference), out var matches) ||
                        matches.Length != 1)
                    {
                        diagnostics.Add(Error("racemenu-plan-form-provider-reference-ambiguous",
                            $"Copied provider '{path.Value}' contains " +
                            $"{(matches.IsDefault ? 0 : matches.Length)} records for originating FormKey " +
                            $"{binding.Reference.FormId}; expected exactly one."));
                        continue;
                    }
                    if (!string.Equals(matches[0].Signature, binding.Signature.Value,
                            StringComparison.Ordinal))
                    {
                        diagnostics.Add(Error("racemenu-plan-form-provider-signature-mismatch",
                            $"Copied provider record {binding.Reference} is {matches[0].Signature}, " +
                            $"not declared {binding.Signature}."));
                        continue;
                    }
                    if (binding.Signature == HeadPartSignature &&
                        (matches[0].HeadPartType != binding.HeadPartType ||
                         binding.HeadPartRawType is not null &&
                         matches[0].HeadPartRawType != binding.HeadPartRawType))
                    {
                        diagnostics.Add(Error("racemenu-plan-headpart-provider-type-mismatch",
                            $"Copied HDPT record {binding.Reference} has type " +
                            $"'{matches[0].HeadPartType?.ToWireName() ?? "missing"}' " +
                            $"(PNAM {matches[0].HeadPartRawType?.ToString() ?? "missing"}), not declared " +
                            $"'{binding.HeadPartType?.ToWireName() ?? "missing"}' " +
                            $"(PNAM {binding.HeadPartRawType?.ToString() ?? "by name"})."));
                    }
                    if (binding.Signature == HairColorSignature &&
                        authority.HairColorAuthority is ExternalHairColorAuthorityBinding mapping &&
                        SameReference(mapping.ProviderReference, binding.Reference) &&
                        matches[0].PackedRgb != mapping.PackedRgb)
                    {
                        diagnostics.Add(Error("racemenu-plan-haircolor-provider-value-mismatch",
                            $"Copied CLFM record {binding.Reference} contains packed RGB " +
                            $"0x{matches[0].PackedRgb:X6}, not declared 0x{mapping.PackedRgb:X6}."));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                               InvalidDataException or ArgumentException)
            {
                diagnostics.Add(Error("racemenu-plan-form-provider-read-failed",
                    $"Copied provider '{path.Value}' could not be inspected: {exception.Message}"));
            }
        }
    }

    private static bool ValidateSourceProviderMapping(
        RaceMenuNpcFormBinding binding,
        ProviderRecordIndex providerIndex,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var source = binding.SourceReference;
        var provider = binding.Reference;
        var providerOwnsFormKey = string.Equals(
            provider.Plugin.Value,
            providerIndex.ProviderPluginName.Value,
            StringComparison.OrdinalIgnoreCase);
        if (providerOwnsFormKey && providerIndex.IsLightMaster &&
            provider.FormId.Value > 0x0000_0FFF)
        {
            diagnostics.Add(Error("racemenu-plan-form-provider-light-id-invalid",
                $"Light provider record {provider} exceeds the 12-bit local FormID range."));
            return false;
        }
        if (SameReference(source, provider)) return true;
        if (!providerOwnsFormKey || !providerIndex.IsLightMaster ||
            source.FormId.Value <= 0x0000_0FFF ||
            (source.FormId.Value & 0x0000_0FFF) != provider.FormId.Value)
        {
            diagnostics.Add(Error("racemenu-plan-form-provider-local-id-mismatch",
                $"RaceMenu source {source} does not normalize to copied provider record {provider}."));
            return false;
        }
        return true;
    }

    private static async ValueTask<ProviderRecordIndex>
        ReadProviderRecordsAsync(
            WorkspacePath plugin,
            PluginName providerPluginName,
            CancellationToken cancellationToken)
    {
        await using var stream = OpenReadLease(plugin.Value);
        var header = new byte[24];
        await stream.ReadExactlyAsync(header, cancellationToken);
        if (!header.AsSpan(0, 4).SequenceEqual("TES4"u8))
            throw new InvalidDataException("Copied provider does not begin with a TES4 record.");
        var tes4Flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
        var isLightMaster = (tes4Flags & 0x0000_0200U) != 0;
        var tes4Size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
        if (tes4Size > 16 * 1_048_576 || 24L + tes4Size > stream.Length)
            throw new InvalidDataException("Copied provider TES4 record size is invalid.");
        var tes4Body = new byte[checked((int)tes4Size)];
        await stream.ReadExactlyAsync(tes4Body, cancellationToken);
        var masters = ReadMasterNames(tes4Body);
        if (masters.Length > byte.MaxValue)
            throw new InvalidDataException("Copied provider declares too many masters.");
        var ownerIndex = (byte)masters.Length;

        var result = new Dictionary<string, ImmutableArray<OwnedRecord>.Builder>(
            StringComparer.OrdinalIgnoreCase);
        var ends = new Stack<long>();
        var position = 24L + tes4Size;
        var end = stream.Length;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (position == end)
            {
                if (ends.Count == 0) break;
                end = ends.Pop();
                continue;
            }
            if (position < 0 || position + 24 > end)
                throw new InvalidDataException("Copied provider record tree is truncated.");

            stream.Position = position;
            await stream.ReadExactlyAsync(header, cancellationToken);
            var signature = Encoding.ASCII.GetString(header, 0, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4, 4));
            if (string.Equals(signature, "GRUP", StringComparison.Ordinal))
            {
                if (size < 24 || position + size > end)
                    throw new InvalidDataException("Copied provider GRUP size exceeds its parent.");
                ends.Push(end);
                end = position + size;
                position += 24;
                continue;
            }

            var recordEnd = position + 24L + size;
            if (recordEnd > end)
                throw new InvalidDataException("Copied provider record size exceeds its parent.");
            var rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12, 4));
            var originIndex = rawFormId >> 24;
            if (originIndex > ownerIndex)
            {
                // Preserve the prior fail-closed authority surface: records
                // outside this file's declared master/owner namespace are not
                // candidates for a portable FormKey binding.
                position = recordEnd;
                continue;
            }
            var originPlugin = originIndex == ownerIndex
                ? providerPluginName
                : masters[checked((int)originIndex)];
            var localId = rawFormId & 0x00FF_FFFF;
            var key = ProviderRecordKey(new FormReference(originPlugin, new FormId(localId)));
            if (!result.TryGetValue(key, out var records))
            {
                records = ImmutableArray.CreateBuilder<OwnedRecord>();
                result.Add(key, records);
            }
            uint? packedRgb = null;
            NpcHeadPartType? headPartType = null;
            uint? headPartRawType = null;
            if (string.Equals(signature, "CLFM", StringComparison.Ordinal) ||
                string.Equals(signature, "HDPT", StringComparison.Ordinal))
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4));
                if ((flags & 0x0004_0000U) != 0)
                    throw new InvalidDataException(
                        $"Compressed {signature} records are not accepted as appearance authority.");
                var body = new byte[checked((int)size)];
                stream.Position = position + 24;
                await stream.ReadExactlyAsync(body, cancellationToken);
                if (string.Equals(signature, "CLFM", StringComparison.Ordinal))
                {
                    packedRgb = ReadClfmPackedRgb(body);
                }
                else
                {
                    headPartRawType = ReadHdptType(body);
                    headPartType = NpcHeadPartTypeExtensions.FromPnam(headPartRawType.Value);
                }
            }
            records.Add(new OwnedRecord(signature, packedRgb, headPartType, headPartRawType));
            position = recordEnd;
        }
        return new ProviderRecordIndex(providerPluginName, isLightMaster,
            result.ToImmutableDictionary(pair => pair.Key,
                pair => pair.Value.ToImmutable(), StringComparer.OrdinalIgnoreCase));
    }

    private static uint ReadClfmPackedRgb(ReadOnlySpan<byte> body)
    {
        var offset = 0;
        uint? extendedSize = null;
        uint? packedRgb = null;
        while (offset < body.Length)
        {
            if (offset + 6 > body.Length)
                throw new InvalidDataException("CLFM subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(body.Slice(offset, 4));
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset + 4, 2));
            var size = extendedSize ?? shortSize;
            if (offset + 6L + size > body.Length)
                throw new InvalidDataException("CLFM subrecord exceeds its record.");
            if (string.Equals(signature, "XXXX", StringComparison.Ordinal))
            {
                if (shortSize != 4)
                    throw new InvalidDataException("CLFM XXXX subrecord must contain four bytes.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset + 6, 4));
            }
            else
            {
                if (string.Equals(signature, "CNAM", StringComparison.Ordinal))
                {
                    if (size != 4 || packedRgb is not null)
                        throw new InvalidDataException(
                            "CLFM authority requires exactly one four-byte CNAM subrecord.");
                    var color = body.Slice(offset + 6, 4);
                    packedRgb = ((uint)color[0] << 16) | ((uint)color[1] << 8) | color[2];
                }
                extendedSize = null;
            }
            offset += checked(6 + (int)size);
        }
        if (extendedSize is not null)
            throw new InvalidDataException("CLFM record ends with a dangling XXXX subrecord.");
        return packedRgb ?? throw new InvalidDataException(
            "CLFM authority record does not contain a CNAM color.");
    }

    /// <summary>Reads the raw HDPT PNAM value; callers project it onto the closed enum.</summary>
    private static uint ReadHdptType(ReadOnlySpan<byte> body)
    {
        var offset = 0;
        uint? extendedSize = null;
        uint? headPartType = null;
        while (offset < body.Length)
        {
            if (offset + 6 > body.Length)
                throw new InvalidDataException("HDPT subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(body.Slice(offset, 4));
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(offset + 4, 2));
            var size = extendedSize ?? shortSize;
            if (offset + 6L + size > body.Length)
                throw new InvalidDataException("HDPT subrecord exceeds its record.");
            if (string.Equals(signature, "XXXX", StringComparison.Ordinal))
            {
                if (shortSize != 4)
                    throw new InvalidDataException("HDPT XXXX subrecord must contain four bytes.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset + 6, 4));
            }
            else
            {
                if (string.Equals(signature, "PNAM", StringComparison.Ordinal))
                {
                    if (size != 4 || headPartType is not null)
                        throw new InvalidDataException(
                            "HDPT authority requires exactly one four-byte PNAM subrecord.");
                    var rawType = BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(offset + 6, 4));
                    headPartType = rawType;
                }
                extendedSize = null;
            }
            offset += checked(6 + (int)size);
        }
        if (extendedSize is not null)
            throw new InvalidDataException("HDPT record ends with a dangling XXXX subrecord.");
        return headPartType ?? throw new InvalidDataException(
            "HDPT authority record does not contain a PNAM type.");
    }

    private static ImmutableArray<PluginName> ReadMasterNames(ReadOnlySpan<byte> tes4Body)
    {
        var offset = 0;
        var masters = ImmutableArray.CreateBuilder<PluginName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        uint? extendedSize = null;
        while (offset < tes4Body.Length)
        {
            if (offset + 6 > tes4Body.Length)
                throw new InvalidDataException("TES4 subrecord header is truncated.");
            var signature = Encoding.ASCII.GetString(tes4Body.Slice(offset, 4));
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(tes4Body.Slice(offset + 4, 2));
            var size = extendedSize ?? shortSize;
            if (offset + 6L + size > tes4Body.Length)
                throw new InvalidDataException("TES4 subrecord exceeds its record.");
            if (string.Equals(signature, "XXXX", StringComparison.Ordinal))
            {
                if (shortSize != 4)
                    throw new InvalidDataException("TES4 XXXX subrecord must contain four bytes.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(tes4Body.Slice(offset + 6, 4));
            }
            else
            {
                if (string.Equals(signature, "MAST", StringComparison.Ordinal))
                {
                    var value = tes4Body.Slice(offset + 6, checked((int)size));
                    if (value.Length < 2 || value[^1] != 0 ||
                        value[..^1].IndexOf((byte)0) >= 0)
                    {
                        throw new InvalidDataException(
                            "TES4 MAST must be one nonempty null-terminated plugin filename.");
                    }
                    var master = new PluginName(Encoding.Latin1.GetString(value[..^1]));
                    if (!seen.Add(master.Value))
                        throw new InvalidDataException(
                            $"TES4 declares duplicate master '{master.Value}'.");
                    masters.Add(master);
                }
                extendedSize = null;
            }
            offset += checked(6 + (int)size);
        }
        if (extendedSize is not null)
            throw new InvalidDataException("TES4 record ends with a dangling XXXX subrecord.");
        return masters.ToImmutable();
    }

    private static ResolvedReferences ResolvePortableReferences(
        PresetAppearance appearance,
        RecordAuthorityBinding authority,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var headParts = ImmutableArray.CreateBuilder<RaceMenuResolvedHeadPart>();
        foreach (var disposition in authority.HeadPartDispositions)
        {
            if (disposition.Kind != RaceMenuNpcHeadPartDispositionKind.MappedRecord) continue;
            if (disposition.MappedHeadPart is not { } mapped ||
                mapped.Binding.Signature != HeadPartSignature)
            {
                diagnostics.Add(Error("racemenu-plan-headpart-disposition-invalid",
                    "A mapped head-part disposition lost its typed HDPT binding."));
                continue;
            }
            headParts.Add(mapped);
            used.Add(BindingKey(mapped.Binding.SourceReference));
        }

        RaceMenuNpcFormBinding? headTexture = null;
        if (!string.IsNullOrWhiteSpace(appearance.RaceMenu?.HeadTexture))
        {
            headTexture = ResolveIdentifier(PresetIdentifier.Parse(appearance.RaceMenu.HeadTexture),
                "headtexture", HeadTextureSignature, authority.FormBindings, used, diagnostics);
        }
        else if (appearance.RaceMenu is { FaceTextures.IsDefaultOrEmpty: false })
        {
            RaceMenuNpcFormBinding[] derived = authority.FormBindings
                .Where(item => item.Signature == HeadTextureSignature &&
                               !used.Contains(BindingKey(item.SourceReference)))
                .ToArray();
            if (derived.Length == 1)
            {
                headTexture = derived[0];
                used.Add(BindingKey(headTexture.SourceReference));
            }
            else
            {
                diagnostics.Add(Error("racemenu-plan-headtexture-derived",
                    $"Direct RaceMenu faceTextures require exactly one provider-read TXST carrier binding; found {derived.Length}."));
            }
        }

        RaceMenuNpcHairColorAuthority? hairColor = null;
        if (appearance.HairColor?.PackedRgb is { } packed)
        {
            switch (authority.HairColorAuthority)
            {
                case ExternalHairColorAuthorityBinding external when external.PackedRgb == packed:
                    {
                        var binding = ResolveProviderReference(
                            external.ProviderReference, "haircolor", HairColorSignature,
                            authority.FormBindings, used, diagnostics);
                        if (binding is not null)
                            hairColor = new RaceMenuNpcExternalHairColorAuthority(packed, binding);
                        break;
                    }
                case OutputOwnedHairColorAuthorityBinding outputOwned when outputOwned.PackedRgb == packed:
                    hairColor = new RaceMenuNpcOutputOwnedHairColorAuthority(
                        packed, outputOwned.AllocatedLocalFormId, outputOwned.AuthoritySha256);
                    break;
                default:
                    diagnostics.Add(Error("racemenu-plan-haircolor-authority-missing",
                        "Packed RaceMenu hair color has no exact external or output-owned CLFM authority."));
                    break;
            }
        }
        else if (appearance.HairColor is not null)
        {
            diagnostics.Add(Error("racemenu-plan-haircolor-source-unsupported",
                "This bounded authored route requires a packed RaceMenu hair color."));
        }
        else if (authority.HairColorAuthority is not null)
        {
            diagnostics.Add(Error("racemenu-plan-haircolor-authority-unexpected",
                "The authority declares a hair-color route for an absent .jslot field."));
        }

        foreach (var binding in authority.FormBindings)
        {
            if (!used.Contains(BindingKey(binding.SourceReference)))
            {
                diagnostics.Add(Error("racemenu-plan-form-binding-unknown",
                    $"Record authority binding '{binding.Signature} {binding.SourceReference}' -> " +
                    $"'{binding.Reference}' is not used by the .jslot."));
            }
        }
        return new ResolvedReferences(headParts.ToImmutable(), headTexture, hairColor);
    }

    private static RaceMenuNpcFormBinding? ResolveIdentifier(
        PresetIdentifier identifier,
        string role,
        RecordSignature expectedSignature,
        ImmutableArray<RaceMenuNpcFormBinding> bindings,
        HashSet<string> used,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (identifier.Plugin is not { } plugin || identifier.FormId is not { } formId)
        {
            diagnostics.Add(Error($"racemenu-plan-{role}-identifier-unresolved",
                $"RaceMenu {role} identifier '{identifier.Raw}' is not plugin-qualified."));
            return null;
        }
        return ResolveReference(new FormReference(plugin, formId), role, expectedSignature,
            bindings, used, diagnostics);
    }

    private static RaceMenuNpcFormBinding? ResolveReference(
        FormReference reference,
        string role,
        RecordSignature expectedSignature,
        ImmutableArray<RaceMenuNpcFormBinding> bindings,
        HashSet<string> used,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference.FormId.Value is 0 or > 0x00FF_FFFF)
        {
            diagnostics.Add(Error($"racemenu-plan-{role}-load-order-unresolved",
                $"RaceMenu {role} identifier '{reference}' contains load-order bits."));
            return null;
        }
        var matches = bindings.Where(binding =>
            SameReference(binding.SourceReference, reference)).ToArray();
        if (matches.Length != 1)
        {
            diagnostics.Add(Error($"racemenu-plan-{role}-binding-missing",
                $"RaceMenu {role} '{reference}' requires exactly one signature-bearing provider binding."));
            return null;
        }
        var binding = matches[0];
        if (binding.Signature != expectedSignature)
        {
            diagnostics.Add(Error($"racemenu-plan-{role}-signature-mismatch",
                $"RaceMenu {role} '{reference}' requires {expectedSignature}, not {binding.Signature}."));
            return null;
        }
        used.Add(BindingKey(binding.SourceReference));
        return binding;
    }

    private static RaceMenuNpcFormBinding? ResolveProviderReference(
        FormReference reference,
        string role,
        RecordSignature expectedSignature,
        ImmutableArray<RaceMenuNpcFormBinding> bindings,
        HashSet<string> used,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (reference.FormId.Value is 0 or > 0x00FF_FFFF)
        {
            diagnostics.Add(Error($"racemenu-plan-{role}-provider-reference-invalid",
                $"RaceMenu {role} provider reference '{reference}' is not plugin-local."));
            return null;
        }
        var matches = bindings.Where(binding => SameReference(binding.Reference, reference)).ToArray();
        if (matches.Length != 1 || matches[0].Signature != expectedSignature)
        {
            diagnostics.Add(Error($"racemenu-plan-{role}-binding-missing",
                $"RaceMenu {role} provider '{reference}' requires exactly one {expectedSignature} binding."));
            return null;
        }
        used.Add(BindingKey(matches[0].SourceReference));
        return matches[0];
    }

    private static NpcSex ParseSex(string value) => value switch
    {
        "male" => NpcSex.Male,
        "female" => NpcSex.Female,
        _ => throw new InvalidDataException("RaceMenu record authority sex must be 'male' or 'female'.")
    };

    private static bool RequiredBool(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"'{property}' must be a boolean.");
        return value.GetBoolean();
    }

    private static double RequiredUnitDouble(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var result) ||
            !double.IsFinite(result) || result is < 0D or > 1D)
        {
            throw new InvalidDataException($"'{property}' must be a finite number from 0 through 1.");
        }
        return result;
    }

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId &&
        string.Equals(left.Plugin.Value, right.Plugin.Value, StringComparison.OrdinalIgnoreCase);

    private static string BindingKey(FormReference reference) =>
        $"{reference.Plugin.Value}|{reference.FormId.Value:X6}";

    private static string ProviderRecordKey(FormReference reference) =>
        $"{reference.Plugin.Value}|{reference.FormId.Value:X6}";

    private abstract record HairColorAuthorityBinding(uint PackedRgb);

    private sealed record ExternalHairColorAuthorityBinding(
        uint PackedRgb,
        FormReference ProviderReference) : HairColorAuthorityBinding(PackedRgb);

    private sealed record OutputOwnedHairColorAuthorityBinding(
        uint PackedRgb,
        FormId AllocatedLocalFormId,
        Sha256Hash AuthoritySha256) : HairColorAuthorityBinding(PackedRgb);

    private sealed record OwnedRecord(
        string Signature,
        uint? PackedRgb,
        NpcHeadPartType? HeadPartType,
        uint? HeadPartRawType);

    private sealed record ProviderRecordIndex(
        PluginName ProviderPluginName,
        bool IsLightMaster,
        ImmutableDictionary<string, ImmutableArray<OwnedRecord>> Records);

    private sealed record RecordAuthorityBinding(
        string AuthorityId,
        Sha256Hash ManifestSha256,
        RaceMenuNpcFormBinding RaceBinding,
        ImmutableArray<RaceMenuNpcFormBinding> FormBindings,
        ImmutableArray<RaceMenuNpcHeadPartDisposition> HeadPartDispositions,
        HairColorAuthorityBinding? HairColorAuthority,
        ImmutableArray<RaceMenuNpcTintDisposition> TintDispositions,
        ImmutableArray<RaceMenuNpcTintLayerBinding> TintLayers,
        RaceMenuNpcQnamDerivation? Qnam);

    private sealed record ResolvedReferences(
        ImmutableArray<RaceMenuResolvedHeadPart> HeadParts,
        RaceMenuNpcFormBinding? HeadTexture,
        RaceMenuNpcHairColorAuthority? HairColor);
}
