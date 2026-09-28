using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Xml;
using System.Xml.Linq;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Presets;

/// <summary>
/// Read-only parser for BodySlide SliderPresets XML. Values remain in
/// BodySlide-native percent space; this service does not generate meshes.
/// </summary>
public sealed class BodySlideSliderPresetInspectionService(
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IBodySlideSliderPresetInspectionService
{
    private const long MaximumXmlBytes = 4 * 1024 * 1024;

    public async ValueTask<BodySlideSliderPresetInspectionResult> InspectAsync(
        BodySlideSliderPresetInspectionRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidateInput(request).ToBuilder();
        if (HasErrors(diagnostics))
            return Empty(request, diagnostics.ToImmutable());

        byte[] bytes;
        Sha256Hash hash;
        try
        {
            bytes = await File.ReadAllBytesAsync(
                request.PresetXml.Value, cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength is <= 0 or > MaximumXmlBytes)
            {
                diagnostics.Add(Error("bodyslide-preset-size-limit",
                    $"BodySlide SliderPreset XML must be 1-{MaximumXmlBytes} bytes."));
                return Empty(request, diagnostics.ToImmutable());
            }
            hash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes)));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException)
        {
            diagnostics.Add(Error("bodyslide-preset-read-failed",
                exception.Message));
            return Empty(request, diagnostics.ToImmutable());
        }

        try
        {
            using var stream = new MemoryStream(bytes, writable: false);
            using var reader = XmlReader.Create(stream, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = MaximumXmlBytes,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true
            });
            var document = XDocument.Load(reader, LoadOptions.None);
            BodySlideSliderPresetDocument parsed = ParseDocument(document, hash);
            return new BodySlideSliderPresetInspectionResult(
                request.Edition,
                request.PresetXml,
                parsed.SourceHash,
                parsed.PresetName,
                parsed.SliderSet,
                parsed.Groups,
                parsed.Sliders,
                diagnostics.ToImmutable());
        }
        catch (Exception exception) when (exception is XmlException or
                                           InvalidDataException or
                                           FormatException or
                                           OverflowException)
        {
            diagnostics.Add(Error("bodyslide-preset-xml-invalid",
                exception.Message));
            return Empty(request, diagnostics.ToImmutable(), hash);
        }
    }

    private ImmutableArray<Diagnostic> ValidateInput(
        BodySlideSliderPresetInspectionRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("bodyslide-preset-edition",
                "BodySlide SliderPreset XML inspection supports Skyrim SE only."));
        }
        if (!request.PresetXml.IsUnder(labRoot))
        {
            diagnostics.Add(Error("bodyslide-preset-outside-lab",
                "BodySlide SliderPreset XML input must remain under the K-only lab root."));
        }
        if (!string.Equals(Path.GetExtension(request.PresetXml.Value), ".xml",
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("bodyslide-preset-extension",
                "BodySlide SliderPreset input must use the .xml extension."));
        }
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.PresetXml));
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return diagnostics.ToImmutable();
        if (!File.Exists(request.PresetXml.Value))
        {
            diagnostics.Add(Error("bodyslide-preset-missing",
                "BodySlide SliderPreset XML input does not exist."));
            return diagnostics.ToImmutable();
        }

        try
        {
            var info = new FileInfo(request.PresetXml.Value);
            if (info.Length is <= 0 or > MaximumXmlBytes)
            {
                diagnostics.Add(Error("bodyslide-preset-size-limit",
                    $"BodySlide SliderPreset XML must be 1-{MaximumXmlBytes} bytes."));
            }
            if (File.GetAttributes(request.PresetXml.Value)
                .HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("bodyslide-preset-reparse-refused",
                    "BodySlide SliderPreset XML input must not be a reparse point."));
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("bodyslide-preset-path-inspection",
                exception.Message));
        }
        return diagnostics.ToImmutable();
    }

    private static BodySlideSliderPresetDocument ParseDocument(
        XDocument document,
        Sha256Hash hash)
    {
        XElement root = document.Root ??
            throw new InvalidDataException("BodySlide XML has no root element.");
        if (root.Name.LocalName != "SliderPresets")
            throw new InvalidDataException(
                "BodySlide XML root must be SliderPresets.");
        RequireNoUnknownAttributes(root, "SliderPresets");
        XElement[] presets = root.Elements()
            .Where(item => item.Name.LocalName == "Preset")
            .ToArray();
        if (presets.Length != 1 || root.Elements()
                .Any(item => item.Name.LocalName != "Preset"))
        {
            throw new InvalidDataException(
                "BodySlide XML must contain exactly one SliderPresets/Preset element and no unknown child elements.");
        }

        XElement preset = presets[0];
        RequireAttributes(preset, "Preset", "name", "set");
        string presetName = RequiredBoundedText(
            preset.Attribute("name")?.Value, "Preset name");
        string sliderSet = RequiredBoundedText(
            preset.Attribute("set")?.Value, "Preset set");

        var groups = ImmutableArray.CreateBuilder<string>();
        var sliders = ImmutableArray.CreateBuilder<BodySlideSliderPresetRow>();
        var seenSliders = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (XElement child in preset.Elements())
        {
            if (child.Name.LocalName == "Group")
            {
                RequireAttributes(child, "Group", "name");
                groups.Add(RequiredBoundedText(
                    child.Attribute("name")?.Value, "Group name"));
                continue;
            }
            if (child.Name.LocalName != "SetSlider")
            {
                throw new InvalidDataException(
                    $"Unsupported BodySlide Preset child element '{child.Name.LocalName}'.");
            }
            RequireAttributes(child, "SetSlider", "name", "size", "value");
            string name = RequiredBoundedText(
                child.Attribute("name")?.Value, "SetSlider name");
            string size = RequiredBoundedText(
                child.Attribute("size")?.Value, "SetSlider size");
            if (size is not ("small" or "big"))
            {
                throw new InvalidDataException(
                    "SetSlider size must be 'small' or 'big'.");
            }
            if (!float.TryParse(child.Attribute("value")?.Value,
                    NumberStyles.Float,
                    CultureInfo.InvariantCulture,
                    out var value) ||
                !float.IsFinite(value))
            {
                throw new InvalidDataException(
                    "SetSlider value must be a finite BodySlide-native percent value.");
            }
            if (!seenSliders.Add(name + "\u001F" + size))
            {
                throw new InvalidDataException(
                    $"SetSlider '{name}'/'{size}' occurs more than once.");
            }
            sliders.Add(new BodySlideSliderPresetRow(name, size, value));
        }
        return new BodySlideSliderPresetDocument(
            hash,
            presetName,
            sliderSet,
            groups.ToImmutable(),
            sliders.ToImmutable());
    }

    private static void RequireNoUnknownAttributes(XElement element, string role)
    {
        if (element.HasAttributes)
            throw new InvalidDataException(
                $"{role} must not contain attributes.");
    }

    private static void RequireAttributes(
        XElement element,
        string role,
        params string[] names)
    {
        string[] actual = element.Attributes().Select(item => item.Name.LocalName)
            .ToArray();
        if (actual.Length != names.Length ||
            names.Any(name => !actual.Contains(name, StringComparer.Ordinal)))
        {
            throw new InvalidDataException(
                $"{role} has missing or unknown attributes.");
        }
    }

    private static string RequiredBoundedText(string? value, string role)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('\0') ||
            value.Length > 256)
        {
            throw new InvalidDataException(
                $"{role} must be non-empty and at most 256 characters.");
        }
        return value;
    }

    private static BodySlideSliderPresetInspectionResult Empty(
        BodySlideSliderPresetInspectionRequest request,
        ImmutableArray<Diagnostic> diagnostics,
        Sha256Hash? sourceHash = null) =>
        new(request.Edition, request.PresetXml, sourceHash, null, null,
            ImmutableArray<string>.Empty,
            ImmutableArray<BodySlideSliderPresetRow>.Empty,
            diagnostics);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);
}
