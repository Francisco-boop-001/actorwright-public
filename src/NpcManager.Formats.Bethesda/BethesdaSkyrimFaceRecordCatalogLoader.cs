using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Copies only the typed HDPT/RACE/KYWD fields needed after an overlay closes.</summary>
internal static class BethesdaSkyrimFaceRecordCatalogLoader
{
    public static SkyrimFaceRecordCatalog Load(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> pluginOrder,
        CancellationToken cancellationToken,
        bool requireWinningRecordEvidence = false)
    {
        var headParts = ImmutableDictionary.CreateBuilder<SkyrimFaceRecordKey, SkyrimFaceDecodedHeadPart>();
        var races = ImmutableDictionary.CreateBuilder<SkyrimFaceRecordKey, SkyrimFaceDecodedRace>();
        var keywords = ImmutableDictionary.CreateBuilder<SkyrimFaceRecordKey, SkyrimFaceDecodedKeyword>();
        var formLists = ImmutableDictionary.CreateBuilder<SkyrimFaceRecordKey, SkyrimFaceDecodedFormList>();
        var textureSets = ImmutableDictionary.CreateBuilder<SkyrimFaceRecordKey, SkyrimFaceDecodedTextureSet>();
        var lightProviderPlugins = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var authority in pluginOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var mod = SkyrimMod.CreateFromBinaryOverlay(
                authority.Path.Value, SkyrimRelease.SkyrimSE);
            if (!string.Equals(mod.ModKey.ToString(), authority.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"Copied plugin '{authority.Path}' identifies itself as '{mod.ModKey}', not '{authority.Plugin}'.");
            }

            var provider = new SkyrimFaceRecordProvider(
                authority.Plugin, authority.Path, authority.ExpectedSha256);
            if (mod.IsSmallMaster)
            {
                lightProviderPlugins.Add(authority.Plugin.Value);
            }
            var masterKeys = mod.MasterReferences
                .Select(reference => reference.Master)
                .ToImmutableArray();
            IReadOnlyDictionary<(uint FormId, string Signature), string>? rawDigests =
                requireWinningRecordEvidence
                    ? BethesdaRawRecordDigestReader.Read(authority.Path.Value)
                    : null;
            long? pluginByteLength = requireWinningRecordEvidence
                ? new FileInfo(authority.Path.Value).Length
                : null;
            foreach (var record in mod.HeadParts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = ToReference(record.FormKey);
                Sha256Hash? winningRecordSha256 = null;
                uint rawFormId = ToRawFormId(record.FormKey, mod.ModKey, masterKeys);
                if (rawDigests is not null &&
                    rawDigests.TryGetValue((rawFormId, "HDPT"), out var rawDigest))
                {
                    winningRecordSha256 = new Sha256Hash(rawDigest);
                }
                else if (requireWinningRecordEvidence)
                {
                    throw new InvalidDataException(
                        $"Winning HDPT record digest is missing for {reference} in '{authority.Plugin}'.");
                }
                headParts[SkyrimFaceRecordKey.From(record.FormKey)] =
                    new SkyrimFaceDecodedHeadPart(
                        reference,
                        provider,
                        record.EditorID,
                        record.Name?.String,
                        record.Type is { } type ? Convert.ToInt32(type) : null,
                        record.Flags.HasFlag(HeadPart.Flag.IsExtraPart),
                        record.Flags.HasFlag(HeadPart.Flag.Male),
                        record.Flags.HasFlag(HeadPart.Flag.Female),
                        record.Model?.File?.GivenPath,
                        record.Parts.Select(part => new SkyrimFaceDecodedTriPart(
                                part.PartType is { } role ? Convert.ToInt32(role) : null,
                                part.FileName?.GivenPath))
                            .ToImmutableArray(),
                        ToReference(record.ValidRaces.FormKeyNullable),
                        ToReference(record.TextureSet.FormKeyNullable),
                        record.ExtraParts.Select(part => ToReference(part.FormKey)).ToImmutableArray(),
                        record.IsDeleted,
                        winningRecordSha256,
                        pluginByteLength);
            }

            foreach (var record in mod.Races)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = ToReference(record.FormKey);
                races[SkyrimFaceRecordKey.From(record.FormKey)] =
                    new SkyrimFaceDecodedRace(
                        reference,
                        provider,
                        record.EditorID,
                        ToReference(record.MorphRace.FormKeyNullable),
                        record.Keywords?.Select(keyword => ToReference(keyword.FormKey)).ToImmutableArray() ?? [],
                        ReadDefaultHeadParts(record.HeadData?.Male),
                        ReadDefaultHeadParts(record.HeadData?.Female),
                        record.IsDeleted);
            }

            foreach (var record in mod.Keywords)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = ToReference(record.FormKey);
                keywords[SkyrimFaceRecordKey.From(record.FormKey)] =
                    new SkyrimFaceDecodedKeyword(
                        reference, provider, record.EditorID, record.IsDeleted);
            }

            foreach (var record in mod.FormLists)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = ToReference(record.FormKey);
                formLists[SkyrimFaceRecordKey.From(record.FormKey)] =
                    new SkyrimFaceDecodedFormList(
                        reference,
                        provider,
                        record.Items.Select(item => ToReference(item.FormKey)).ToImmutableArray(),
                        record.IsDeleted);
            }

            foreach (var record in mod.TextureSets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var reference = ToReference(record.FormKey);
                textureSets[SkyrimFaceRecordKey.From(record.FormKey)] =
                    new SkyrimFaceDecodedTextureSet(
                        reference,
                        provider,
                        [
                            NormalizeTextureRemainder(record.Diffuse),
                            NormalizeTextureRemainder(record.NormalOrGloss),
                            NormalizeTextureRemainder(record.EnvironmentMaskOrSubsurfaceTint),
                            NormalizeTextureRemainder(record.GlowOrDetailMap),
                            NormalizeTextureRemainder(record.Height),
                            NormalizeTextureRemainder(record.Environment),
                            NormalizeTextureRemainder(record.Multilayer),
                            NormalizeTextureRemainder(record.BacklightMaskOrSpecular)
                        ],
                        record.IsDeleted);
            }
        }

        return new SkyrimFaceRecordCatalog(
            headParts.ToImmutable(), races.ToImmutable(), keywords.ToImmutable(),
            formLists.ToImmutable(), textureSets.ToImmutable(),
            requireWinningRecordEvidence, lightProviderPlugins.ToImmutable());
    }

    private static string NormalizeTextureRemainder(string? value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value != value.Trim() || value.Any(char.IsControl))
            throw new InvalidDataException("TXST texture paths may not contain outer whitespace or control characters.");
        string normalized = value.Replace('/', '\\');
        if (normalized.StartsWith("Textures\\", StringComparison.OrdinalIgnoreCase))
            normalized = normalized["Textures\\".Length..];
        if (normalized.StartsWith("Textures\\", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith('\\') ||
            Path.IsPathRooted(normalized) ||
            normalized.Split('\\').Any(segment => segment is "" or "." or ".."))
            throw new InvalidDataException(
                $"TXST texture path '{value}' is not one safe Data/Textures-relative remainder.");
        return normalized;
    }

    private static ImmutableArray<FormReference> ReadDefaultHeadParts(IHeadDataGetter? headData)
    {
        if (headData is null) return [];
        var result = ImmutableArray.CreateBuilder<FormReference>(headData.HeadParts.Count);
        foreach (var item in headData.HeadParts)
        {
            var key = item.Head.FormKeyNullable;
            if (key is { } formKey && !formKey.IsNull)
            {
                result.Add(ToReference(formKey));
            }
        }

        return result.ToImmutable();
    }

    private static FormReference ToReference(FormKey key) =>
        new(new PluginName(key.ModKey.ToString()), new FormId(key.ID));

    private static FormReference? ToReference(FormKey? key) =>
        key is { } formKey && !formKey.IsNull ? ToReference(formKey) : null;

    private static uint ToRawFormId(
        FormKey formKey,
        ModKey self,
        ImmutableArray<ModKey> masters)
    {
        var ownerIndex = formKey.ModKey == self
            ? masters.Length
            : masters.IndexOf(formKey.ModKey);
        if (ownerIndex is < 0 or > byte.MaxValue ||
            formKey.ID > 0x00FF_FFFF)
        {
            throw new InvalidDataException(
                $"Record {formKey} cannot be represented by this plugin's ordinary master table.");
        }
        return ((uint)ownerIndex << 24) | formKey.ID;
    }

}
