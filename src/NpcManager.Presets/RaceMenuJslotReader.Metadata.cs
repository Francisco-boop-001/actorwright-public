using System.Collections.Immutable;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

internal static partial class RaceMenuJslotCodec
{
    private const int MaximumMetadataEntries = 256;

    private static ImmutableArray<RaceMenuFaceTexture> ReadFaceTextures(
        JsonElement root,
        ImmutableArray<PresetUnknownField>.Builder unknowns,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out bool present)
    {
        present = PresetJsonSupport.TryGet(root, "faceTextures", out var element);
        if (!present) return [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumMetadataEntries)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-face-textures-shape", DiagnosticSeverity.Error,
                $"RaceMenu 'faceTextures' must be an array of at most {MaximumMetadataEntries} entries."));
            return [];
        }

        var result = ImmutableArray.CreateBuilder<RaceMenuFaceTexture>();
        var seen = new HashSet<int>();
        foreach (var (item, position) in element.EnumerateArray().Select((value, index) => (value, index)))
        {
            var path = $"$.faceTextures[{position}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-face-texture-shape", DiagnosticSeverity.Error,
                    $"'{path}' must be an object."));
                continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index", "texture" },
                path, unknowns, diagnostics);
            if (!PresetJsonSupport.TryGet(item, "index", out var indexElement) ||
                !PresetJsonSupport.TryReadInt(indexElement, $"{path}.index", out var index, diagnostics) ||
                index is < 0 or > 255 || !seen.Add(index) ||
                !PresetJsonSupport.TryGet(item, "texture", out var textureElement) ||
                textureElement.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-face-texture-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires a unique byte index and a safe texture path."));
                continue;
            }
            try
            {
                var texture = new AssetPath(textureElement.GetString() ?? string.Empty).Value;
                if (texture.Length > 260) throw new ArgumentException("Texture path exceeds 260 characters.");
                result.Add(new RaceMenuFaceTexture(index, texture));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-face-texture-path", DiagnosticSeverity.Error,
                    $"'{path}.texture' is invalid: {exception.Message}"));
            }
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<PluginName> ReadModNames(
        JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out bool present)
    {
        present = PresetJsonSupport.TryGet(root, "modNames", out var element);
        if (!present) return [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumMetadataEntries)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-mod-names-shape", DiagnosticSeverity.Error,
                $"RaceMenu 'modNames' must be an array of at most {MaximumMetadataEntries} plugin names."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<PluginName>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, position) in element.EnumerateArray().Select((value, index) => (value, index)))
        {
            if (item.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-mod-name-shape", DiagnosticSeverity.Error,
                    $"'$.modNames[{position}]' must be a plugin-name string."));
                continue;
            }
            try
            {
                var plugin = new PluginName(item.GetString() ?? string.Empty);
                if (!seen.Add(plugin.Value))
                    throw new ArgumentException("Plugin name is duplicated.");
                result.Add(plugin);
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-mod-name-invalid", DiagnosticSeverity.Error,
                    $"'$.modNames[{position}]' is invalid: {exception.Message}"));
            }
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<RaceMenuModEntry> ReadMods(
        JsonElement root,
        ImmutableArray<PresetUnknownField>.Builder unknowns,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out bool present)
    {
        present = PresetJsonSupport.TryGet(root, "mods", out var element);
        if (!present) return [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaximumMetadataEntries)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-mods-shape", DiagnosticSeverity.Error,
                $"RaceMenu 'mods' must be an array of at most {MaximumMetadataEntries} entries."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<RaceMenuModEntry>();
        var seenIndexes = new HashSet<byte>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (item, position) in element.EnumerateArray().Select((value, index) => (value, index)))
        {
            var path = $"$.mods[{position}]";
            if (item.ValueKind != JsonValueKind.Object)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-mod-shape", DiagnosticSeverity.Error,
                    $"'{path}' must be an object."));
                continue;
            }
            PresetJsonSupport.AddUnknownFields(item,
                new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "index", "name" },
                path, unknowns, diagnostics);
            if (!PresetJsonSupport.TryGet(item, "index", out var indexElement) ||
                !PresetJsonSupport.TryReadInt(indexElement, $"{path}.index", out var index, diagnostics) ||
                index is < 0 or > byte.MaxValue || !seenIndexes.Add((byte)index) ||
                !PresetJsonSupport.TryGet(item, "name", out var nameElement) ||
                nameElement.ValueKind != JsonValueKind.String)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-mod-invalid", DiagnosticSeverity.Error,
                    $"'{path}' requires a unique byte index and plugin name."));
                continue;
            }
            try
            {
                var plugin = new PluginName(nameElement.GetString() ?? string.Empty);
                if (!seenNames.Add(plugin.Value)) throw new ArgumentException("Plugin name is duplicated.");
                result.Add(new RaceMenuModEntry((byte)index, plugin));
            }
            catch (ArgumentException exception)
            {
                diagnostics.Add(new Diagnostic("preset-racemenu-mod-name-invalid", DiagnosticSeverity.Error,
                    $"'{path}.name' is invalid: {exception.Message}"));
            }
        }
        return result.ToImmutable();
    }

    private static RaceMenuVersion? ReadVersion(
        JsonElement root,
        ImmutableArray<PresetUnknownField>.Builder unknowns,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        out bool present)
    {
        present = PresetJsonSupport.TryGet(root, "version", out var element);
        if (!present) return null;
        if (element.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("preset-racemenu-version-shape", DiagnosticSeverity.Error,
                "RaceMenu 'version' must be an object."));
            return null;
        }
        PresetJsonSupport.AddUnknownFields(element,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "formatVersion", "runtimeVersion", "signature", "skseVersion" },
            "$.version", unknowns, diagnostics);
        if (!ReadUInt(element, "formatVersion", diagnostics, out var format) ||
            !ReadUInt(element, "runtimeVersion", diagnostics, out var runtime) ||
            !ReadUInt(element, "signature", diagnostics, out var signature) ||
            !ReadUInt(element, "skseVersion", diagnostics, out var skse))
            return null;
        return new RaceMenuVersion(format, runtime, signature, skse);
    }

    private static bool ReadUInt(JsonElement element, string property,
        ImmutableArray<Diagnostic>.Builder diagnostics, out uint value)
    {
        if (PresetJsonSupport.TryGet(element, property, out var item))
            return PresetJsonSupport.TryReadUInt32(item, $"$.version.{property}", out value, diagnostics);

        diagnostics.Add(new Diagnostic("preset-racemenu-version-field-missing", DiagnosticSeverity.Error,
            $"'$.version.{property}' is required."));
        value = default;
        return false;
    }
}
