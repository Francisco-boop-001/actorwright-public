using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class PreviewSceneCommandHandler(IPreviewSceneService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal async ValueTask<CommandExitCode> RunAsync(ParsedCommand command,
        CancellationToken cancellationToken)
    {
        if (!TryParseEdition(command, out var edition, out var editionError)) return Usage(command.Json, editionError);
        if (!command.Options.TryGetValue("manifest", out var manifestPath))
            return Usage(command.Json, "preview render requires --manifest <json>.");
        if (!command.Options.TryGetValue("output", out var outputPath))
            return Usage(command.Json, "preview render requires --output <path>.");
        if (!TryParseVisibility(command, out var visibleCategories, out var visibilityError))
            return Usage(command.Json, visibilityError);
        if (!TryParseMorphs(command, out var morphCategories, out var morphError))
            return Usage(command.Json, morphError);
        if (!TryParseVariant(command, out var outfit, out var variantId, out var variantError))
            return Usage(command.Json, variantError);
        if (!TryParsePresetId(command, "camera", out var cameraId, out var cameraError))
            return Usage(command.Json, cameraError);
        if (!TryParsePresetId(command, "lighting", out var lightingId, out var lightingError))
            return Usage(command.Json, lightingError);
        if (!TryParseAnimation(command, out var animationId, out var frame, out var timeSeconds,
                out var playbackRate, out var playing, out var animationError))
            return Usage(command.Json, animationError);
        if (!TryParseImageRender(command, out var assetRoot, out var imageOutput, out var imageWidth,
                out var imageHeight, out var imageError))
            return Usage(command.Json, imageError);
        if (!TryParseHairZap(command, out var coveredHairSlots, out var renderHeadwear, out var hairZapError))
            return Usage(command.Json, hairZapError);

        PreviewSceneResult result;
        try
        {
            result = await service.RenderAsync(new PreviewSceneRequest(edition,
                new WorkspacePath(manifestPath), new WorkspacePath(outputPath), visibleCategories, morphCategories,
                outfit, variantId, cameraId, lightingId, animationId, frame, timeSeconds, playbackRate, playing,
                assetRoot, imageOutput, imageWidth, imageHeight, coveredHairSlots, renderHeadwear), cancellationToken);
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }

        var response = new PreviewResponse(result.Written, result.Artifact?.ArtifactKind,
            result.Artifact?.NpcFormId, result.Artifact?.AssetCount, result.Artifact?.IncludedAssetCount,
            result.Artifact?.VisibleAssetCount, result.Artifact?.AppliedMorphCount,
            result.Artifact?.Variant?.Id, result.Artifact?.Variant?.Outfit, result.Artifact?.SceneSha256,
            result.OutputSha256?.Value, result.Artifact?.Camera?.Id, result.Artifact?.Camera?.Version,
            result.Artifact?.Lighting?.Id, result.Artifact?.Lighting?.Version, result.Artifact?.Animation?.Id,
            result.Artifact?.Animation?.Frame, result.Artifact?.Animation?.TimeSeconds,
            result.Artifact?.Animation?.PlaybackRate, result.Artifact?.Animation?.Playing,
            result.Artifact?.RenderedImage?.Path, result.Artifact?.RenderedImage?.Sha256,
            result.Artifact?.RenderedImage?.Width, result.Artifact?.RenderedImage?.Height,
             result.Artifact?.RenderedImage?.MeshCount,
             result.Artifact?.RenderedImage?.MorphNames.IsDefaultOrEmpty == false
                 ? result.Artifact.RenderedImage.MorphNames : null,
             result.Artifact?.RenderedImage?.MorphDeformed,
             result.Artifact?.RenderedImage?.Edition,
             result.Artifact?.HairZap?.RenderHeadwear,
             result.Artifact?.HairZap?.CoveredSlots,
             result.Artifact?.HairZap?.TopCovered,
             result.Artifact?.HairZap?.LongCovered,
             result.Artifact?.HairZap?.FaceGenHeadCovered,
             result.Artifact?.RenderedImage?.HairZapApplied,
             result.Artifact?.RenderedImage?.HairZapAffectedMeshCount,
             result.Artifact?.RenderedImage?.HairZapRemovedFaceCount,
             result.Artifact?.RenderedImage?.FaceCullApplied,
             result.Artifact?.RenderedImage?.FaceCullAffectedMeshCount,
             result.Diagnostics);
        Write(response, command.Json, result.Written ? "preview render: PASS" : "preview render: REFUSED");
        if (result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
        return CommandExitCode.Success;
    }

    private static bool TryParseMorphs(ParsedCommand command,
        out ImmutableHashSet<PreviewMorphCategory>? selected, out string message)
    {
        selected = null;
        if (!command.Options.TryGetValue("morphs", out var text))
        { message = string.Empty; return true; }
        var builder = ImmutableHashSet.CreateBuilder<PreviewMorphCategory>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(item, "all", StringComparison.OrdinalIgnoreCase))
            {
                selected = Enum.GetValues<PreviewMorphCategory>().ToImmutableHashSet();
                message = string.Empty;
                return true;
            }
            if (!PreviewMorphCategoryExtensions.TryParseWireName(item, out var category))
            { message = "preview render --morphs accepts comma-separated bone,vertex,weight,sculpt, or all."; return false; }
            builder.Add(category);
        }
        if (builder.Count == 0)
        { message = "preview render --morphs requires at least one category or all."; return false; }
        selected = builder.ToImmutable();
        message = string.Empty;
        return true;
    }

    private static bool TryParseVisibility(ParsedCommand command,
        out ImmutableHashSet<PreviewAssetCategory>? visible, out string message)
    {
        visible = null;
        if (!command.Options.TryGetValue("visible", out var text))
        { message = string.Empty; return true; }
        var builder = ImmutableHashSet.CreateBuilder<PreviewAssetCategory>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (string.Equals(item, "all", StringComparison.OrdinalIgnoreCase))
            {
                visible = Enum.GetValues<PreviewAssetCategory>().ToImmutableHashSet();
                message = string.Empty;
                return true;
            }
            if (!PreviewAssetCategoryExtensions.TryParseWireName(item, out var category))
            { message = "preview render --visible accepts comma-separated face,body,hair,outfit,accessory, or all."; return false; }
            builder.Add(category);
        }
        if (builder.Count == 0)
        { message = "preview render --visible requires at least one layer or all."; return false; }
        visible = builder.ToImmutable();
        message = string.Empty;
        return true;
    }

    private static bool TryParseHairZap(ParsedCommand command,
        out ImmutableHashSet<int>? coveredSlots, out bool renderHeadwear, out string message)
    {
        coveredSlots = null;
        renderHeadwear = false;
        var hasSlots = command.Options.TryGetValue("hair-slots", out var slotsText);
        var hasToggle = command.Options.TryGetValue("render-headwear", out var toggleText);
        if (!hasSlots && !hasToggle)
        { message = string.Empty; return true; }
        if (hasToggle && !bool.TryParse(toggleText, out renderHeadwear))
        { message = "preview render --render-headwear accepts true or false."; return false; }
        if (!hasSlots)
        {
            if (renderHeadwear)
            { message = "preview render --render-headwear true requires --hair-slots 30,31,32 (or Skyrim 31,41)."; return false; }
            message = string.Empty;
            return true;
        }
        var builder = ImmutableHashSet.CreateBuilder<int>();
        foreach (var item in (slotsText ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(item, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) || slot is < 0 or > 255)
            { message = "preview render --hair-slots accepts comma-separated integer slots from 0 to 255."; return false; }
            builder.Add(slot);
        }
        if (builder.Count == 0)
        { message = "preview render --hair-slots requires at least one slot."; return false; }
        if (!hasToggle)
        { message = "preview render --hair-slots requires --render-headwear true|false."; return false; }
        coveredSlots = builder.ToImmutable();
        message = string.Empty;
        return true;
    }

    private static bool TryParseVariant(ParsedCommand command, out FormReference? outfit,
        out string? variantId, out string message)
    {
        outfit = null;
        variantId = null;
        var hasOutfit = command.Options.TryGetValue("outfit", out var outfitText);
        var hasVariant = command.Options.TryGetValue("variant", out var variantText);
        if (!hasOutfit && !hasVariant)
        { message = string.Empty; return true; }
        if (!hasOutfit || !hasVariant)
        { message = "preview render --outfit and --variant must be supplied together."; return false; }
        if (!FormReference.TryParse(outfitText ?? string.Empty, out var parsedOutfit))
        { message = "preview render --outfit requires Plugin.esp|0xXXXXXXXX (or .esm/.esl)."; return false; }
        if (string.IsNullOrWhiteSpace(variantText) || variantText.Length > 128 || variantText != variantText.Trim() || variantText.Any(char.IsControl))
        { message = "preview render --variant requires a non-empty identifier of at most 128 characters."; return false; }
        outfit = parsedOutfit;
        variantId = variantText;
        message = string.Empty;
        return true;
    }

    private static bool TryParsePresetId(ParsedCommand command, string option, out string? id, out string message)
    {
        id = null;
        if (!command.Options.TryGetValue(option, out var text))
        { message = string.Empty; return true; }
        if (string.IsNullOrWhiteSpace(text) || text.Length > 128 || text != text.Trim() || text.Any(char.IsControl))
        {
            message = $"preview render --{option} requires a non-empty identifier of at most 128 characters.";
            return false;
        }
        id = text;
        message = string.Empty;
        return true;
    }

    private static bool TryParseAnimation(ParsedCommand command, out string? animationId, out int? frame,
        out float? timeSeconds, out float? playbackRate, out bool playing, out string message)
    {
        animationId = null;
        frame = null;
        timeSeconds = null;
        playbackRate = null;
        playing = false;
        var hasAnimation = command.Options.TryGetValue("animation", out var idText);
        var hasFrame = command.Options.TryGetValue("frame", out var frameText);
        var hasTime = command.Options.TryGetValue("time", out var timeText);
        var hasRate = command.Options.TryGetValue("fps", out var rateText);
        var hasPlay = command.Options.TryGetValue("play", out var playText);
        if (!hasAnimation)
        {
            if (hasFrame || hasTime || hasRate || hasPlay)
            { message = "preview render --frame, --time, --fps, and --play require --animation <id>."; return false; }
            message = string.Empty;
            return true;
        }
        if (string.IsNullOrWhiteSpace(idText) || idText.Length > 128 || idText != idText.Trim() || idText.Any(char.IsControl))
        { message = "preview render --animation requires a non-empty identifier of at most 128 characters."; return false; }
        if (hasFrame == hasTime)
        { message = "preview render --animation requires exactly one --frame <number> or --time <seconds>."; return false; }
        if (hasFrame && (!int.TryParse(frameText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFrame) || parsedFrame < 0))
        { message = "preview render --frame requires a non-negative integer."; return false; }
        if (hasFrame) frame = int.Parse(frameText!, NumberStyles.Integer, CultureInfo.InvariantCulture);
        if (hasTime && (!float.TryParse(timeText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedTime) ||
            !float.IsFinite(parsedTime) || parsedTime < 0))
        { message = "preview render --time requires finite non-negative seconds."; return false; }
        if (hasTime) timeSeconds = float.Parse(timeText!, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (hasRate && (!float.TryParse(rateText, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsedRate) ||
            !float.IsFinite(parsedRate) || parsedRate < 1 || parsedRate > 240))
        { message = "preview render --fps requires finite playback rate from 1 to 240."; return false; }
        if (hasRate) playbackRate = float.Parse(rateText!, NumberStyles.Float, CultureInfo.InvariantCulture);
        if (hasPlay && (!bool.TryParse(playText, out playing)))
        { message = "preview render --play accepts true or false."; return false; }
        animationId = idText;
        message = string.Empty;
        return true;
    }

    private static bool TryParseEdition(ParsedCommand command, out GameEdition edition, out string message)
    {
        edition = default;
        var value = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (value is null || !GameEditionExtensions.TryParseWireName(value, out edition))
        { message = "preview render requires --edition|--game fallout4|skyrimse."; return false; }
        message = string.Empty; return true;
    }

    private static bool TryParseImageRender(ParsedCommand command, out WorkspacePath? assetRoot,
        out WorkspacePath? imageOutput, out int imageWidth, out int imageHeight, out string message)
    {
        assetRoot = null;
        imageOutput = null;
        imageWidth = 512;
        imageHeight = 512;
        var hasRoot = command.Options.TryGetValue("asset-root", out var rootText);
        var hasOutput = command.Options.TryGetValue("image-output", out var outputText);
        var hasWidth = command.Options.TryGetValue("width", out var widthText);
        var hasHeight = command.Options.TryGetValue("height", out var heightText);
        if (hasRoot != hasOutput)
        { message = "preview render --asset-root and --image-output must be supplied together."; return false; }
        if (!hasRoot && (hasWidth || hasHeight))
        { message = "preview render --width and --height require --asset-root and --image-output."; return false; }
        if (hasWidth && (!int.TryParse(widthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out imageWidth) ||
            imageWidth is < 64 or > 2048))
        { message = "preview render --width requires an integer from 64 to 2048."; return false; }
        if (hasHeight && (!int.TryParse(heightText, NumberStyles.Integer, CultureInfo.InvariantCulture, out imageHeight) ||
            imageHeight is < 64 or > 2048))
        { message = "preview render --height requires an integer from 64 to 2048."; return false; }
        if (hasRoot)
        {
            try
            {
                assetRoot = new WorkspacePath(rootText!);
                imageOutput = new WorkspacePath(outputText!);
            }
            catch (ArgumentException exception)
            { message = exception.Message; return false; }
        }
        message = string.Empty;
        return true;
    }

    private void Write<T>(T response, bool json, string human)
    {
        if (json) output.WriteLine(JsonSerializer.Serialize(response, JsonOptions)); else output.WriteLine(human);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var diagnostics = ImmutableArray.Create(new Diagnostic("usage-error", DiagnosticSeverity.Error, message));
        if (json) error.WriteLine(JsonSerializer.Serialize(new ErrorResponse("usage-error", diagnostics), JsonOptions));
        else error.WriteLine($"ERROR usage-error: {message}");
        return CommandExitCode.UsageError;
    }

    private sealed record PreviewResponse(bool Written, string? ArtifactKind, string? NpcFormId,
        int? AssetCount, int? IncludedAssetCount, int? VisibleAssetCount, int? AppliedMorphCount,
        string? VariantId, string? Outfit, string? SceneSha256, string? OutputSha256,
        string? CameraPresetId, int? CameraPresetVersion, string? LightingPresetId, int? LightingPresetVersion,
        string? AnimationId, int? AnimationFrame, float? AnimationTimeSeconds, float? AnimationPlaybackRate,
        bool? AnimationPlaying,
        string? RenderedImagePath, string? RenderedImageSha256,
         int? RenderedImageWidth, int? RenderedImageHeight, int? RenderedImageMeshCount,
         ImmutableArray<string>? RenderedMorphNames, bool? RenderedMorphDeformed,
         string? RenderedEdition, bool? HairZapRenderHeadwear,
         ImmutableArray<int>? HairZapCoveredSlots, bool? HairZapTop, bool? HairZapLong,
         bool? FaceGenHeadCovered,
         bool? RenderedHairZapApplied, int? RenderedHairZapAffectedMeshCount,
         int? RenderedHairZapRemovedFaceCount,
         bool? FaceCullApplied, int? FaceCullAffectedMeshCount,
         ImmutableArray<Diagnostic> Diagnostics);

    private sealed record ErrorResponse(string Code, ImmutableArray<Diagnostic> Diagnostics);
}
