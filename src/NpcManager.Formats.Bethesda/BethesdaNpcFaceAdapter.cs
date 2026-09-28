using System.Collections.Immutable;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>Typed PNAM/HCLF access kept separate from the broader NPC mutation surface.</summary>
public static class BethesdaNpcFaceAdapter
{
    public static NpcFaceSnapshot Read(GameEdition edition, WorkspacePath pluginPath, FormId formId)
    {
        var path = pluginPath.Value;
        var modKey = ToModKey(path);
        return edition switch
        {
            GameEdition.SkyrimSpecialEdition => ReadSkyrim(path, modKey, formId),
            GameEdition.Fallout4 => ReadFallout4(path, modKey, formId),
            _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unsupported game edition.")
        };
    }

    public static ImmutableArray<NpcHeadPartProvider> ReadProviders(GameEdition edition, WorkspacePath pluginPath,
        ImmutableArray<NpcHeadPartSelection> selections)
    {
        var path = pluginPath.Value;
        var modKey = ToModKey(path);
        return edition switch
        {
            GameEdition.SkyrimSpecialEdition => ReadSkyrimProviders(path, modKey, selections),
            GameEdition.Fallout4 => ReadFallout4Providers(path, modKey, selections),
            _ => throw new ArgumentOutOfRangeException(nameof(edition), edition, "Unsupported game edition.")
        };
    }

    public static void Write(NpcFacePatchRequest request, WorkspacePath destination)
    {
        BethesdaNpcFaceBinaryWriter.Write(request, destination.Value);
    }

    private static NpcFaceSnapshot ReadSkyrim(string path, ModKey modKey, FormId formId)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs[new FormKey(modKey, formId.Value)];
        return new NpcFaceSnapshot(
            npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) ? NpcSex.Female : NpcSex.Male,
            ToReference(npc.Race.FormKey),
            ReadSelections(npc.HeadParts, key => ReadSkyrimType(mod, key)),
            ToReference(npc.HairColor.FormKeyNullable));
    }

    private static NpcFaceSnapshot ReadFallout4(string path, ModKey modKey, FormId formId)
    {
        using var mod = Fallout4Mod.CreateFromBinaryOverlay(path, Fallout4Release.Fallout4);
        var npc = mod.Npcs[new FormKey(modKey, formId.Value)];
        return new NpcFaceSnapshot(
            npc.Flags.HasFlag(Mutagen.Bethesda.Fallout4.Npc.Flag.Female) ? NpcSex.Female : NpcSex.Male,
            ToReference(npc.Race.FormKey),
            ReadSelections(npc.HeadParts, key => ReadFallout4Type(mod, key)),
            ToReference(npc.HairColor.FormKeyNullable));
    }

    private static ImmutableArray<NpcHeadPartProvider> ReadSkyrimProviders(string path, ModKey modKey,
        ImmutableArray<NpcHeadPartSelection> selections)
    {
        using var mod = SkyrimMod.CreateFromBinaryOverlay(path, SkyrimRelease.SkyrimSE);
        return selections.Select(selection =>
        {
            var part = mod.HeadParts[new FormKey(modKey, selection.Reference.FormId.Value)];
            var parsed = ParseType(part.Type);
            return new NpcHeadPartProvider(selection.Reference, parsed.Type,
                HasFlag(part.Flags, "Extra"), part.Model?.File?.ToString(),
                ReadValidRaces(mod, part.ValidRaces.FormKeyNullable),
                part.ExtraParts.Select(link => ToReference(link.FormKey)).OfType<FormReference>().ToImmutableArray(),
                part.Flags.ToString() ?? string.Empty) { RawPnamType = parsed.Raw };
        }).ToImmutableArray();
    }

    private static ImmutableArray<NpcHeadPartProvider> ReadFallout4Providers(string path, ModKey modKey,
        ImmutableArray<NpcHeadPartSelection> selections)
    {
        using var mod = Fallout4Mod.CreateFromBinaryOverlay(path, Fallout4Release.Fallout4);
        return selections.Select(selection =>
        {
            var part = mod.HeadParts[new FormKey(modKey, selection.Reference.FormId.Value)];
            var parsed = ParseType(part.Type);
            return new NpcHeadPartProvider(selection.Reference, parsed.Type,
                HasFlag(part.Flags, "Extra"), part.Model?.File?.ToString(),
                ReadValidRaces(mod, part.ValidRaces.FormKeyNullable),
                part.ExtraParts.Select(link => ToReference(link.FormKey)).OfType<FormReference>().ToImmutableArray(),
                part.Flags.ToString() ?? string.Empty) { RawPnamType = parsed.Raw };
        }).ToImmutableArray();
    }

    private static ImmutableArray<NpcHeadPartSelection> ReadSelections<T>(
        IEnumerable<IFormLinkGetter<T>> links,
        Func<FormKey, (NpcHeadPartType Type, uint Raw)?> readType)
        where T : class, IMajorRecordGetter
    {
        return links.Select(link =>
        {
            var parsed = readType(link.FormKey);
            return new NpcHeadPartSelection(
                ToReference(link.FormKey) ?? throw new InvalidDataException("NPC PNAM contains a null head-part link."),
                parsed?.Type ?? NpcHeadPartType.Misc)
            {
                RawPnamType = parsed?.Raw
            };
        }).ToImmutableArray();
    }

    private static (NpcHeadPartType Type, uint Raw)? ReadSkyrimType(ISkyrimModGetter mod, FormKey key)
    {
        try { return ParseType(mod.HeadParts[key].Type); }
        catch (KeyNotFoundException) { return null; }
    }

    private static (NpcHeadPartType Type, uint Raw)? ReadFallout4Type(IFallout4ModGetter mod, FormKey key)
    {
        try { return ParseType(mod.HeadParts[key].Type); }
        catch (KeyNotFoundException) { return null; }
    }

    private static ImmutableArray<FormReference> ReadValidRaces(ISkyrimModGetter mod, FormKey? formKey)
    {
        if (formKey is not { } key || key.IsNull) return [];
        try { return mod.FormLists[key].Items.Select(item => ToReference(item.FormKey)).OfType<FormReference>().ToImmutableArray(); }
        catch (KeyNotFoundException) { return []; }
    }

    private static ImmutableArray<FormReference> ReadValidRaces(IFallout4ModGetter mod, FormKey? formKey)
    {
        if (formKey is not { } key || key.IsNull) return [];
        try { return mod.FormLists[key].Items.Select(item => ToReference(item.FormKey)).OfType<FormReference>().ToImmutableArray(); }
        catch (KeyNotFoundException) { return []; }
    }

    internal static (NpcHeadPartType Type, uint Raw) ParseType<TEnum>(TEnum? value)
        where TEnum : struct, Enum
    {
        if (!value.HasValue) throw new InvalidDataException("Head-part type is missing.");
        long signed = Convert.ToInt64(value.Value);
        if (signed < int.MinValue || signed > uint.MaxValue)
            throw new InvalidDataException($"Unsupported head-part type '{signed}'.");
        uint raw = unchecked((uint)signed);
        return (NpcHeadPartTypeExtensions.FromPnam(raw), raw);
    }

    private static bool HasFlag<TEnum>(TEnum flags, string value)
        where TEnum : struct, Enum =>
        flags.ToString().Contains(value, StringComparison.OrdinalIgnoreCase);

    private static ModKey ToModKey(string path) =>
        new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);

    private static FormReference? ToReference(FormKey? formKey) =>
        formKey is { } key && !key.IsNull
            ? new FormReference(new PluginName(key.ModKey.ToString()), new FormId(key.ID))
            : null;

}
