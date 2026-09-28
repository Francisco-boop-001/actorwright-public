using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.FaceGen;

public sealed partial class FaceTintBuildService
{
    private static ManifestData ReadManifest(JsonElement root, FaceTintBuildRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            diagnostics.Add(new Diagnostic("facetint-build-root", DiagnosticSeverity.Error,
                "FaceTint build manifest must contain an object."));
            return ManifestData.Empty;
        }

        var schema = String(root, "schemaVersion") ?? "";
        if (schema != "1") diagnostics.Add(new Diagnostic("facetint-build-schema", DiagnosticSeverity.Error,
            "FaceTint build manifest schemaVersion must be '1'."));
        var npc = String(root, "npcFormId") ?? request.NpcFormId?.ToString();
        if (npc is null)
        {
            diagnostics.Add(new Diagnostic("facetint-build-npc-required", DiagnosticSeverity.Error,
                "FaceTint build manifests require npcFormId or the --npc option."));
            npc = "unknown";
        }
        else if (!FormId.TryParse(npc, out _))
            diagnostics.Add(new Diagnostic("facetint-build-npc-invalid", DiagnosticSeverity.Error,
                "npcFormId must be a hexadecimal FormID."));
        var width = Integer(root, "width") ?? request.Resolution ?? 0;
        var height = Integer(root, "height") ?? request.Resolution ?? width;
        if (request.Resolution is not null)
        {
            width = request.Resolution.Value;
            height = request.Resolution.Value;
        }
        if (width is < 1 or > MaxDimension || height is < 1 or > MaxDimension ||
            !SupportedResolutions.Contains(width) || !SupportedResolutions.Contains(height))
            diagnostics.Add(new Diagnostic("facetint-build-resolution", DiagnosticSeverity.Error,
                "FaceTint dimensions must be a supported square resolution: 512, 1024, 2048, 4096, or 8192."));
        if (width != height)
            diagnostics.Add(new Diagnostic("facetint-build-nonsquare", DiagnosticSeverity.Error,
                "FaceTint output must be square."));

        if (request.Format is not null && !Enum.IsDefined(request.Format.Value))
            diagnostics.Add(new Diagnostic("facetint-build-format-unsupported", DiagnosticSeverity.Error,
                "The requested FaceTint format is not supported."));
        var format = request.Format ?? ParseFormat(String(root, "format"), diagnostics);
        var mips = request.MipCount ?? Integer(root, "mipCount") ?? 1;
        if (mips != 1)
            diagnostics.Add(new Diagnostic("facetint-build-mips", DiagnosticSeverity.Error,
                "The bounded FaceTint artifact supports exactly one mip level."));
        if (request.AlphaMode is not null && !Enum.IsDefined(request.AlphaMode.Value))
            diagnostics.Add(new Diagnostic("facetint-build-alpha-unsupported", DiagnosticSeverity.Error,
                "The requested FaceTint alpha mode is not supported."));
        var alpha = request.AlphaMode ?? ParseAlpha(String(root, "alphaMode"), diagnostics);
        var baseColor = ReadColor(root, "baseColor", diagnostics, [0d, 0d, 0d, 1d], required: true);
        var layers = ReadLayers(root, diagnostics);
        var coordinates = ReadProbes(root, width, height, diagnostics);
        return new ManifestData(npc, width, height, format, mips, alpha, baseColor, layers, coordinates);
    }

    private static ImmutableArray<FaceTintBuildLayer> ReadLayers(JsonElement root,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "layers", out var element)) return [];
        if (element.ValueKind != JsonValueKind.Array || element.GetArrayLength() > MaxLayers)
        {
            diagnostics.Add(new Diagnostic("facetint-build-layers", DiagnosticSeverity.Error,
                $"layers must be an array with at most {MaxLayers} entries."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<FaceTintBuildLayer>();
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var item in element.EnumerateArray())
        {
            var path = $"$.layers[{index++}]";
            var name = String(item, "name");
            var source = String(item, "source");
            var provider = String(item, "provider");
            var blendText = String(item, "blend") ?? "over";
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || string.IsNullOrWhiteSpace(source) ||
                source.Length > 1024 || string.IsNullOrWhiteSpace(provider) || provider.Length > 255 ||
                !Enum.TryParse<FaceTintBlendMode>(blendText, true, out var blend) || !Enum.IsDefined(blend) ||
                !TryNumber(item, "opacity", out var opacity) || !Unit(opacity))
            {
                diagnostics.Add(new Diagnostic("facetint-build-layer-invalid", DiagnosticSeverity.Error,
                    $"{path} requires unique name, source, provider, blend, and unit opacity."));
                continue;
            }
            var color = ReadColor(item, "color", diagnostics, [1d, 1d, 1d, 1d], required: true);
            if (!names.Add(name)) diagnostics.Add(new Diagnostic("facetint-build-layer-duplicate",
                DiagnosticSeverity.Error, $"Layer '{name}' occurs more than once."));
            else result.Add(new FaceTintBuildLayer(name.Trim(), source.Trim(), provider.Trim(), blend, opacity, color));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<(int X, int Y)> ReadProbes(JsonElement root, int width, int height,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!TryGet(root, "probes", out var element) || element.ValueKind != JsonValueKind.Array ||
            element.GetArrayLength() == 0)
            return [(0, 0), (width - 1, 0), (0, height - 1), (width - 1, height - 1)];
        if (element.GetArrayLength() > MaxProbes)
        {
            diagnostics.Add(new Diagnostic("facetint-build-probes", DiagnosticSeverity.Error,
                $"probes may contain at most {MaxProbes} entries."));
            return [];
        }
        var result = ImmutableArray.CreateBuilder<(int, int)>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !Integer(item, "x").HasValue || !Integer(item, "y").HasValue)
            {
                diagnostics.Add(new Diagnostic("facetint-build-probe-invalid", DiagnosticSeverity.Error,
                    "Each probe requires integer x and y coordinates."));
                continue;
            }
            var x = Integer(item, "x")!.Value;
            var y = Integer(item, "y")!.Value;
            if (x < 0 || x >= width || y < 0 || y >= height)
                diagnostics.Add(new Diagnostic("facetint-build-probe-range", DiagnosticSeverity.Error,
                    "FaceTint probe coordinates must be inside the output raster."));
            else result.Add((x, y));
        }
        return result.ToImmutable();
    }

    private static ImmutableArray<double> ReadColor(JsonElement element, string name,
        ImmutableArray<Diagnostic>.Builder diagnostics, ImmutableArray<double> fallback, bool required)
    {
        if (!TryGet(element, name, out var value))
        {
            if (required) diagnostics.Add(new Diagnostic("facetint-build-color", DiagnosticSeverity.Error,
                $"{name} must contain exactly four finite values from 0 to 1."));
            return fallback;
        }
        if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 4)
        {
            diagnostics.Add(new Diagnostic("facetint-build-color", DiagnosticSeverity.Error,
                $"{name} must contain exactly four finite values from 0 to 1."));
            return fallback;
        }
        var result = ImmutableArray.CreateBuilder<double>(4);
        foreach (var channel in value.EnumerateArray())
        {
            if (!channel.TryGetDouble(out var parsed) || !Unit(parsed))
            {
                diagnostics.Add(new Diagnostic("facetint-build-color", DiagnosticSeverity.Error,
                    $"{name} must contain exactly four finite values from 0 to 1."));
                return fallback;
            }
            result.Add(parsed);
        }
        return result.ToImmutable();
    }

}
