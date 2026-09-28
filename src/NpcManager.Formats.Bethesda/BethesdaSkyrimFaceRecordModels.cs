using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

internal readonly record struct SkyrimFaceRecordKey(string Plugin, uint FormId)
{
    public static SkyrimFaceRecordKey From(FormReference reference) =>
        new(reference.Plugin.Value?.ToUpperInvariant() ?? string.Empty, reference.FormId.Value);

    public static SkyrimFaceRecordKey From(Mutagen.Bethesda.Plugins.FormKey key) =>
        new(key.ModKey.ToString().ToUpperInvariant(), key.ID);

    public override string ToString() => $"{Plugin}|0x{FormId:X8}";
}

internal sealed record SkyrimFaceDecodedTriPart(int? Role, string? Path);

internal sealed record SkyrimFaceDecodedHeadPart(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    string? EditorId,
    string? Name,
    int? DeclaredType,
    bool IsExtra,
    bool SupportsMale,
    bool SupportsFemale,
    string? ModelPath,
    ImmutableArray<SkyrimFaceDecodedTriPart> TriParts,
    FormReference? ValidRaces,
    FormReference? TextureSet,
    ImmutableArray<FormReference> ExtraParts,
    bool IsDeleted,
    Sha256Hash? WinningRecordSha256 = null,
    long? WinningPluginByteLength = null);

internal sealed record SkyrimFaceDecodedRace(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    string? EditorId,
    FormReference? MorphRace,
    ImmutableArray<FormReference> Keywords,
    ImmutableArray<FormReference> MaleDefaultHeadParts,
    ImmutableArray<FormReference> FemaleDefaultHeadParts,
    bool IsDeleted);

internal sealed record SkyrimFaceDecodedKeyword(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    string? EditorId,
    bool IsDeleted);

internal sealed record SkyrimFaceDecodedFormList(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    ImmutableArray<FormReference> Items,
    bool IsDeleted);

internal sealed record SkyrimFaceDecodedTextureSet(
    FormReference Reference,
    SkyrimFaceRecordProvider Provider,
    ImmutableArray<string> RawTxSlots,
    bool IsDeleted);

internal sealed record SkyrimFaceRecordCatalog(
    ImmutableDictionary<SkyrimFaceRecordKey, SkyrimFaceDecodedHeadPart> HeadParts,
    ImmutableDictionary<SkyrimFaceRecordKey, SkyrimFaceDecodedRace> Races,
    ImmutableDictionary<SkyrimFaceRecordKey, SkyrimFaceDecodedKeyword> Keywords,
    ImmutableDictionary<SkyrimFaceRecordKey, SkyrimFaceDecodedFormList> FormLists,
    ImmutableDictionary<SkyrimFaceRecordKey, SkyrimFaceDecodedTextureSet>? TextureSets = null,
    bool RequireWinningRecordEvidence = false,
    ImmutableHashSet<string>? LightProviderPlugins = null);
