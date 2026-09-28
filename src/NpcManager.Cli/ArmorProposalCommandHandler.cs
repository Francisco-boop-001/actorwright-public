using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class ArmorProposalCommandHandler(IArmorProposalService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!TryBuildRequest(command, out var request, out var message)) return Usage(command.Json, message);
        var result = await service.ProposeAsync(request, cancellationToken);
        output.WriteLine(command.Json ? JsonSerializer.Serialize(ArmorResponse.From(result), JsonOptions) : result.Written ? "armor propose: PASS" : "armor propose: REFUSED");
        if (!result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return CommandExitCode.Success;
        return DiagnosticExitCodeClassifier.Classify(result.Diagnostics);
    }

    private static bool TryBuildRequest(ParsedCommand command, out ArmorProposalRequest request, out string message)
    {
        request = default!;
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition))
        { message = "armor propose requires --edition fallout4|skyrimse."; return false; }
        if (!command.Options.TryGetValue("plugin", out var plugin) || !command.Options.TryGetValue("source", out var source) ||
            !command.Options.TryGetValue("patch", out var patch) || !command.Options.TryGetValue("output", out var output))
        { message = "armor propose requires --plugin, --source, --patch, and --output."; return false; }
        if (!FormId.TryParse(source, out var formId) || formId.Value == 0)
        { message = "--source must be a non-null hexadecimal FormID."; return false; }
        try
        {
            var patchJson = ReadPatchText(patch);
            using var document = JsonDocument.Parse(patchJson, new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 12
            });
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException("--patch must be a JSON object.");
            var properties = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!properties.Add(property.Name))
                    throw new FormatException($"--patch contains duplicate property '{property.Name}'.");
            }
            request = ParsePatch(document.RootElement, edition, new WorkspacePath(plugin), formId, new WorkspacePath(output));
            message = string.Empty;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException or JsonException or IOException or UnauthorizedAccessException)
        { message = exception.Message; return false; }
    }

    private static string ReadPatchText(string value)
    {
        if (!value.StartsWith('@')) return value;
        var path = new WorkspacePath(value[1..]);
        WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
        if (!path.IsUnder(root) || !File.Exists(path.Value) || (File.GetAttributes(path.Value) & FileAttributes.ReparsePoint) != 0)
            throw new FormatException("An armor patch file must exist under the K-only workspace root and may not be a reparse point.");
        if (new FileInfo(path.Value).Length > 65_536) throw new FormatException("Armor patch JSON may not exceed 65536 bytes.");
        return File.ReadAllText(path.Value);
    }

    private static ArmorProposalRequest ParsePatch(JsonElement root, GameEdition edition, WorkspacePath plugin,
        FormId formId, WorkspacePath output)
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal)
        { "mode", "editorId", "targetFormId", "name", "slotMask", "race", "maleWorldModel", "femaleWorldModel", "value", "weight", "health", "armorRating", "keywords", "armorAddons", "description", "nonPlayable", "enchantment", "pickupSound", "dropSound", "equipmentType", "alternateBlockMaterial", "templateArmor", "objectBounds", "completeDocument" };
        foreach (var property in root.EnumerateObject()) if (!allowed.Contains(property.Name))
            throw new FormatException($"--patch contains unsupported property '{property.Name}'.");
        var modeText = RequiredString(root, "mode");
        if (!Enum.TryParse<ArmorProposalMode>(modeText, true, out var mode)) throw new FormatException("Patch mode must be new or override.");
        EditorId? editorId = null;
        if (OptionalString(root, "editorId") is { } editor) editorId = new EditorId(editor);
        var race = OptionalFormReference(root, "race");
        var keywords = OptionalReferences(root, "keywords");
        var addons = OptionalAddons(root);
        FormId? targetFormId = null;
        if (root.TryGetProperty("targetFormId", out var targetValue))
        {
            if (targetValue.ValueKind != JsonValueKind.String || !FormId.TryParse(targetValue.GetString() ?? string.Empty, out var parsedTarget))
                throw new FormatException("Patch property 'targetFormId' must be a hexadecimal FormID string.");
            targetFormId = parsedTarget;
        }
        return new ArmorProposalRequest(edition, plugin, formId, mode, editorId,
            OptionalString(root, "name"), OptionalUInt32(root, "slotMask"), race,
            OptionalString(root, "maleWorldModel"), OptionalString(root, "femaleWorldModel"), OptionalInt64(root, "value"),
            OptionalDouble(root, "weight"), OptionalUInt32(root, "health"), OptionalDouble(root, "armorRating"),
            keywords, addons, output, targetFormId,
            OptionalString(root, "description"), OptionalBoolean(root, "nonPlayable"),
            OptionalFormReference(root, "enchantment"), OptionalFormReference(root, "pickupSound"),
            OptionalFormReference(root, "dropSound"), OptionalFormReference(root, "equipmentType"),
            OptionalFormReference(root, "alternateBlockMaterial"), OptionalFormReference(root, "templateArmor"),
            OptionalBounds(root), OptionalBoolean(root, "completeDocument") ?? false);
    }

    private static string RequiredString(JsonElement root, string name) => OptionalString(root, name) ?? throw new FormatException($"Patch requires string property '{name}'.");
    private static string? OptionalString(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) throw new FormatException($"Patch property '{name}' must be a string.");
        return value.GetString();
    }
    private static uint? OptionalUInt32(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetUInt32(out var parsed)) throw new FormatException($"Patch property '{name}' must be an unsigned 32-bit integer.");
        return parsed;
    }
    private static long? OptionalInt64(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetInt64(out var parsed)) throw new FormatException($"Patch property '{name}' must be a signed 64-bit integer.");
        return parsed;
    }
    private static bool? OptionalBoolean(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new FormatException($"Patch property '{name}' must be a Boolean.");
        return value.GetBoolean();
    }
    private static double? OptionalDouble(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value)) return null;
        if (!value.TryGetDouble(out var parsed) || !double.IsFinite(parsed)) throw new FormatException($"Patch property '{name}' must be a finite number.");
        return parsed;
    }
    private static FormReference? OptionalFormReference(JsonElement root, string name)
    {
        var value = OptionalString(root, name);
        if (value is null) return null;
        if (!FormReference.TryParse(value, out var reference)) throw new FormatException($"Patch property '{name}' must be a Plugin|FormID reference.");
        return reference;
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
    private static ImmutableArray<ArmorAddonProposal>? OptionalAddons(JsonElement root)
    {
        if (!root.TryGetProperty("armorAddons", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Array) throw new FormatException("Patch property 'armorAddons' must be an array.");
        var builder = ImmutableArray.CreateBuilder<ArmorAddonProposal>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("index", out var index) || !index.TryGetUInt16(out var parsedIndex) ||
                !item.TryGetProperty("addon", out var addon) || addon.ValueKind != JsonValueKind.String ||
                !FormReference.TryParse(addon.GetString() ?? string.Empty, out var reference))
                throw new FormatException("Each armorAddons entry requires an unsigned index and addon FormReference.");
            builder.Add(new ArmorAddonProposal(parsedIndex, reference));
        }
        return builder.ToImmutable();
    }

    private static ArmorObjectBounds? OptionalBounds(JsonElement root)
    {
        if (!root.TryGetProperty("objectBounds", out var value)) return null;
        if (value.ValueKind != JsonValueKind.Object)
            throw new FormatException("Patch property 'objectBounds' must be an object.");
        string[] allowed =
        [
            "minimumX", "minimumY", "minimumZ",
            "maximumX", "maximumY", "maximumZ"
        ];
        var properties = value.EnumerateObject().ToArray();
        if (properties.Length != allowed.Length ||
            properties.Select(item => item.Name).Distinct(StringComparer.Ordinal).Count() != properties.Length ||
            properties.Any(item => !allowed.Contains(item.Name, StringComparer.Ordinal)))
            throw new FormatException("objectBounds requires exactly minimumX, minimumY, minimumZ, maximumX, maximumY, and maximumZ.");
        return new ArmorObjectBounds(
            RequiredInt16(value, "minimumX"), RequiredInt16(value, "minimumY"),
            RequiredInt16(value, "minimumZ"), RequiredInt16(value, "maximumX"),
            RequiredInt16(value, "maximumY"), RequiredInt16(value, "maximumZ"));
    }

    private static short RequiredInt16(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || !value.TryGetInt16(out short parsed))
            throw new FormatException($"objectBounds property '{name}' must be a signed 16-bit integer.");
        return parsed;
    }

    private CommandExitCode Usage(bool json, string message)
    {
        var response = new ErrorResponse("usage-error", message);
        error.WriteLine(json ? JsonSerializer.Serialize(response, JsonOptions) : $"ERROR {response.Code}: {response.Message}");
        return CommandExitCode.UsageError;
    }

    private sealed record ErrorResponse(string Code, string Message);
    private sealed record ArmorResponse(bool Written, string? ArtifactKind, string? Edition, ArmorProposalMode? Mode,
        string? SourcePlugin, string? SourceFormId, string? EditorId, string? InputSha256, string? PatchSha256,
        ImmutableArray<string> ChangedFields, ImmutableArray<Diagnostic> Diagnostics)
    {
        public static ArmorResponse From(ArmorProposalResult result) => new(result.Written, result.Artifact?.ArtifactKind,
            result.Artifact?.Edition, result.Artifact?.Mode, result.Artifact?.SourcePlugin, result.Artifact?.SourceFormId,
            result.Artifact?.EditorId, result.Artifact?.InputSha256, result.Artifact?.PatchSha256,
            result.Artifact?.ChangedFields ?? [], result.Diagnostics);
    }
}
