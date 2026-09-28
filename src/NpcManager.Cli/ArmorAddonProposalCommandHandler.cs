using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ArmorAddonProposalCommandHandler(IArmorAddonProposalService service, IArmorAddonModelProposalService? modelService, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (command.Options.ContainsKey("models")) return await RunModelsAsync(command, cancellationToken);
        if (!TryBuildRequest(command, out var request, out var message)) return Usage(command.Json, message);
        var result = await service.ProposeAsync(request, cancellationToken);
        output.WriteLine(command.Json ? JsonSerializer.Serialize(Response.From(result), JsonOptions) : result.Written ? "armor-addon propose: PASS" : "armor-addon propose: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private async ValueTask<CommandExitCode> RunModelsAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (modelService is null) return WriteUsageError(command.Json, "armor-addon models is unavailable in this runner configuration.");
        if (!TryBuildModelRequest(command, out var request, out var message)) return Usage(command.Json, message);
        var result = await modelService.ProposeAsync(request, cancellationToken);
        output.WriteLine(command.Json ? JsonSerializer.Serialize(ModelResponse.From(result), JsonOptions) : result.Written ? "armor-addon models: PASS" : "armor-addon models: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private CommandExitCode WriteUsageError(bool json, string message) => Usage(json, message);

    private static bool TryBuildModelRequest(ParsedCommand command, out ArmorAddonModelProposalRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        { message = "armor-addon models requires --edition fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("plugin", out var plugin) || !command.Options.TryGetValue("source", out var source) ||
            !command.Options.TryGetValue("models", out var models) || !command.Options.TryGetValue("output", out var output))
        { message = "armor-addon models requires --plugin, --source, --models, and --output."; return false; }
        if (!FormId.TryParse(source, out var formId) || formId.Value == 0)
        { message = "--source must be a non-null hexadecimal FormID."; return false; }
        try
        {
            using var document = JsonDocument.Parse(ReadModelsText(models), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 8 });
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw new FormatException("--models must be a JSON array.");
            var entries = ImmutableArray.CreateBuilder<ArmorAddonModelEntryProposal>();
            foreach (var item in document.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Each armor-addon model entry must be an object.");
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var property in item.EnumerateObject())
                {
                    if (!seen.Add(property.Name)) throw new FormatException("Armor-addon model entries may not contain duplicate properties.");
                    if (property.Name is not ("index" or "addon")) throw new FormatException($"Armor-addon model entries contain unsupported property '{property.Name}'.");
                }
                if (!item.TryGetProperty("index", out var index) || !index.TryGetUInt16(out var parsedIndex) ||
                    !item.TryGetProperty("addon", out var addon) || addon.ValueKind != JsonValueKind.String ||
                    !FormReference.TryParse(addon.GetString() ?? string.Empty, out var reference))
                    throw new FormatException("Each armor-addon model entry requires an unsigned index and addon FormReference.");
                entries.Add(new ArmorAddonModelEntryProposal(parsedIndex, reference));
            }
            request = new ArmorAddonModelProposalRequest(edition, new WorkspacePath(plugin), formId, entries.ToImmutable(), new WorkspacePath(output));
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException)
        { message = exception.Message; return false; }
    }

    private static string ReadModelsText(string value)
    {
        if (!value.StartsWith('@')) return value;
        var path = new WorkspacePath(value[1..]);
        WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
        if (!path.IsUnder(root) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("An armor-addon models file must exist under the K-only workspace root and may not be a reparse point.");
        if (new FileInfo(path.Value).Length > 65_536) throw new FormatException("Armor-addon models JSON may not exceed 65536 bytes.");
        return File.ReadAllText(path.Value);
    }

    private static bool TryBuildRequest(ParsedCommand command, out ArmorAddonProposalRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        { message = "armor-addon propose requires --edition fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("plugin", out var plugin) || !command.Options.TryGetValue("source", out var source) ||
            !command.Options.TryGetValue("patch", out var patch) || !command.Options.TryGetValue("output", out var output))
        { message = "armor-addon propose requires --plugin, --source, --patch, and --output."; return false; }
        if (!FormId.TryParse(source, out var formId) || formId.Value == 0)
        { message = "--source must be a non-null hexadecimal FormID."; return false; }
        try
        {
            using var document = JsonDocument.Parse(ReadPatchText(patch), new JsonDocumentOptions
            { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow, MaxDepth = 16 });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("--patch must be a JSON object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject()) if (!seen.Add(property.Name))
                throw new FormatException($"--patch contains duplicate property '{property.Name}'.");
            var target = OptionalTargetFormId(document.RootElement);
            request = new ArmorAddonProposalRequest(edition, new WorkspacePath(plugin), formId,
                ParseMode(document.RootElement), ParsePatch(document.RootElement), new WorkspacePath(output), target);
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException)
        { message = exception.Message; return false; }
    }

    private static ArmorAddonProposalMode ParseMode(JsonElement root)
    {
        var text = RequiredString(root, "mode");
        if (!Enum.TryParse<ArmorAddonProposalMode>(text, true, out var mode)) throw new FormatException("Patch mode must be new or override.");
        return mode;
    }

    private static ArmorAddonProposalPatch ParsePatch(JsonElement root)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "mode", "editorId", "targetFormId", "slotMask", "race", "footstepSet", "malePriority", "femalePriority", "maleWeightSliderFlags", "femaleWeightSliderFlags", "detectionSound", "weaponAdjust", "maleModel", "femaleModel", "maleFirstPersonModel", "femaleFirstPersonModel", "maleModelFlags", "femaleModelFlags", "maleColorRemapIndex", "femaleColorRemapIndex", "maleSkinTexture", "femaleSkinTexture", "maleSkinTextureSwapList", "femaleSkinTextureSwapList", "maleMaterialSwap", "femaleMaterialSwap", "maleFirstPersonMaterialSwap", "femaleFirstPersonMaterialSwap", "artObject", "additionalRaces", "sculpt", "noUnderarmorScaling", "hasSculptData", "hiResFirstPersonOnly" };
        foreach (var property in root.EnumerateObject()) if (!allowed.Contains(property.Name)) throw new FormatException($"--patch contains unsupported property '{property.Name}'.");
        return new ArmorAddonProposalPatch(OptionalEditorId(root), OptionalUInt32(root, "slotMask"), OptionalReference(root, "race"), OptionalReference(root, "footstepSet"),
            OptionalByte(root, "malePriority"), OptionalByte(root, "femalePriority"), OptionalByte(root, "maleWeightSliderFlags"), OptionalByte(root, "femaleWeightSliderFlags"),
            OptionalByte(root, "detectionSound"), OptionalDouble(root, "weaponAdjust"), OptionalString(root, "maleModel"), OptionalString(root, "femaleModel"),
            OptionalString(root, "maleFirstPersonModel"), OptionalString(root, "femaleFirstPersonModel"), OptionalByte(root, "maleModelFlags"), OptionalByte(root, "femaleModelFlags"),
            OptionalDouble(root, "maleColorRemapIndex"), OptionalDouble(root, "femaleColorRemapIndex"), OptionalReference(root, "maleSkinTexture"), OptionalReference(root, "femaleSkinTexture"),
            OptionalReference(root, "maleSkinTextureSwapList"), OptionalReference(root, "femaleSkinTextureSwapList"), OptionalReference(root, "maleMaterialSwap"), OptionalReference(root, "femaleMaterialSwap"),
            OptionalReference(root, "maleFirstPersonMaterialSwap"), OptionalReference(root, "femaleFirstPersonMaterialSwap"), OptionalReference(root, "artObject"),
            OptionalReferences(root, "additionalRaces"), OptionalSculpt(root), OptionalBool(root, "noUnderarmorScaling"), OptionalBool(root, "hasSculptData"), OptionalBool(root, "hiResFirstPersonOnly"));
    }

    private static EditorId? OptionalEditorId(JsonElement root)
    {
        var value = OptionalString(root, "editorId");
        return value is null ? null : new EditorId(value);
    }

    private static FormId? OptionalTargetFormId(JsonElement root)
    {
        if (!root.TryGetProperty("targetFormId", out var value)) return null;
        if (value.ValueKind != JsonValueKind.String || !FormId.TryParse(value.GetString() ?? string.Empty, out var target))
            throw new FormatException("Patch property 'targetFormId' must be a hexadecimal FormID string.");
        return target;
    }

    private static ImmutableArray<ArmorAddonSculptProposal>? OptionalSculpt(JsonElement root)
    {
        if (!root.TryGetProperty("sculpt", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException("Patch property 'sculpt' must be an array.");
        var rows = ImmutableArray.CreateBuilder<ArmorAddonSculptProposal>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) throw new FormatException("Each sculpt entry must be an object.");
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in item.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new FormatException("Sculpt entries may not contain duplicate properties.");
                if (property.Name is not ("gender" or "bone" or "x" or "y" or "z")) throw new FormatException($"Sculpt contains unsupported property '{property.Name}'.");
            }
            var gender = RequiredByte(item, "gender");
            var bone = RequiredString(item, "bone");
            rows.Add(new ArmorAddonSculptProposal(gender, bone, RequiredDouble(item, "x"), RequiredDouble(item, "y"), RequiredDouble(item, "z")));
        }
        return rows.ToImmutable();
    }

    private static ImmutableArray<FormReference>? OptionalReferences(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException($"Patch property '{name}' must be an array.");
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || !FormReference.TryParse(item.GetString() ?? string.Empty, out var reference))
                throw new FormatException($"Patch property '{name}' contains an invalid FormReference.");
            builder.Add(reference);
        }
        return builder.ToImmutable();
    }

    private static FormReference? OptionalReference(JsonElement root, string name)
    {
        var text = OptionalString(root, name);
        if (text is null) return null;
        if (!FormReference.TryParse(text, out var reference)) throw new FormatException($"Patch property '{name}' must be a Plugin|FormID reference.");
        return reference;
    }

    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException($"Patch property '{name}' must be a string.");
        return value.GetString();
    }

    private static string RequiredString(JsonElement root, string name) => OptionalString(root, name) ?? throw new FormatException($"Patch requires string property '{name}'.");
    private static byte? OptionalByte(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? ParseByte(value, name) : null;
    private static byte RequiredByte(JsonElement root, string name) { if (!root.TryGetProperty(name, out var value)) throw new FormatException($"Patch requires byte property '{name}'."); return ParseByte(value, name); }
    private static byte ParseByte(JsonElement value, string name) { if (!value.TryGetByte(out var parsed)) throw new FormatException($"Patch property '{name}' must be an unsigned byte."); return parsed; }
    private static uint? OptionalUInt32(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.TryGetUInt32(out var parsed) ? parsed : throw new FormatException($"Patch property '{name}' must be an unsigned 32-bit integer.") : null;
    private static double? OptionalDouble(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? ParseDouble(value, name) : null;
    private static double RequiredDouble(JsonElement root, string name) { if (!root.TryGetProperty(name, out var value)) throw new FormatException($"Patch requires numeric property '{name}'."); return ParseDouble(value, name); }
    private static double ParseDouble(JsonElement value, string name) { if (!value.TryGetDouble(out var parsed) || !double.IsFinite(parsed)) throw new FormatException($"Patch property '{name}' must be a finite number."); return parsed; }
    private static bool? OptionalBool(JsonElement root, string name) => root.TryGetProperty(name, out var value) ? value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : throw new FormatException($"Patch property '{name}' must be a boolean.") : null;

    private static string ReadPatchText(string value)
    {
        if (!value.StartsWith('@')) return value;
        var path = new WorkspacePath(value[1..]);
        WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
        if (!path.IsUnder(root) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("An armor-addon patch file must exist under the K-only workspace root and may not be a reparse point.");
        if (new FileInfo(path.Value).Length > 131_072) throw new FormatException("Armor-addon patch JSON may not exceed 131072 bytes.");
        return File.ReadAllText(path.Value);
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record Response(bool Written, string? ArtifactKind, string? Edition, ArmorAddonProposalMode? Mode, string? SourcePlugin,
        string? SourceFormId, string? EditorId, string? InputSha256, string? PatchSha256, ImmutableArray<string> ChangedFields,
        ImmutableArray<Diagnostic> Diagnostics)
    {
        public static Response From(ArmorAddonProposalResult result) => new(result.Written, result.Artifact?.ArtifactKind, result.Artifact?.Edition,
            result.Artifact?.Mode, result.Artifact?.SourcePlugin, result.Artifact?.SourceFormId, result.Artifact?.EditorId,
            result.Artifact?.InputSha256, result.Artifact?.PatchSha256, result.Artifact?.ChangedFields ?? [], result.Diagnostics);
    }

    private sealed record ModelResponse(bool Written, string? ArtifactKind, string? Edition, string? SourcePlugin, string? SourceFormId,
        ImmutableArray<ArmorAddonModelEntryArtifact> Entries, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static ModelResponse From(ArmorAddonModelProposalResult result) => new(result.Written, result.Artifact?.ArtifactKind,
            result.Artifact?.Edition, result.Artifact?.SourcePlugin, result.Artifact?.SourceFormId,
            result.Artifact?.Entries ?? [], result.Diagnostics);
    }
}
