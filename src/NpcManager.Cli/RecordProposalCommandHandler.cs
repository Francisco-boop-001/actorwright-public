using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed class RecordProposalCommandHandler(IRecordProposalService service, TextWriter output, TextWriter error)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase, Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) } };

    public async ValueTask<CommandExitCode> RunAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var editionValue = command.Options.GetValueOrDefault("edition") ?? command.Options.GetValueOrDefault("game");
        if (editionValue is null || !GameEditionExtensions.TryParseWireName(editionValue, out var edition)) return Usage(command.Json, "records propose requires a supported --edition|--game.");
        if (!command.Options.TryGetValue("type", out var signatureText) || !TryParseSignature(signatureText, out var signature)) return Usage(command.Json, "--type must be a four-character record signature.");
        if (!command.Options.TryGetValue("mode", out var modeText) || !TryParseMode(modeText, out var mode)) return Usage(command.Json, "--mode must be new, template, or override.");
        if (!command.Options.TryGetValue("form-id", out var formText) || !FormId.TryParse(formText, out var formId) || formId.Value == 0) return Usage(command.Json, "--form-id must be a nonzero FormID.");
        if (!command.Options.TryGetValue("editor-id", out var editorText) || !TryParseEditorId(editorText, out var editorId)) return Usage(command.Json, "--editor-id must be a valid EditorID.");
        if (!command.Options.TryGetValue("output", out var outputPath) || string.IsNullOrWhiteSpace(outputPath)) return Usage(command.Json, "--output is required.");
        FormId? source = null;
        if (command.Options.TryGetValue("source", out var sourceText))
        {
            if (!FormId.TryParse(sourceText, out var parsedSource) || parsedSource.Value == 0) return Usage(command.Json, "--source must be a nonzero FormID.");
            source = parsedSource;
        }
        var masters = ImmutableArray<PluginName>.Empty;
        if (command.Options.TryGetValue("masters", out var mastersText))
        {
            try
            {
                if (mastersText.StartsWith('@'))
                {
                    var mastersPath = new WorkspacePath(mastersText[1..]);
                    if (!mastersPath.IsUnder(ActorwrightWorkspace.ResolveRoot())) return Usage(command.Json, "@masters files must remain under the configured workspace root.");
                    if (!File.Exists(mastersPath.Value)) return Usage(command.Json, "The @masters file does not exist.");
                    if ((File.GetAttributes(mastersPath.Value) & FileAttributes.ReparsePoint) != 0) return Usage(command.Json, "The @masters file may not be a reparse point.");
                    mastersText = File.ReadAllText(mastersPath.Value);
                }
                using var document = JsonDocument.Parse(mastersText);
                if (document.RootElement.ValueKind != JsonValueKind.Array) return Usage(command.Json, "--masters must be a JSON array of plugin names.");
                var builder = ImmutableArray.CreateBuilder<PluginName>();
                foreach (var value in document.RootElement.EnumerateArray())
                {
                    if (value.ValueKind != JsonValueKind.String || !TryParsePluginName(value.GetString()!, out var plugin)) return Usage(command.Json, "--masters contains an invalid plugin name.");
                    builder.Add(plugin);
                }
                masters = builder.ToImmutable();
            }
            catch (JsonException exception) { return Usage(command.Json, exception.Message); }
            catch (IOException exception) { return Usage(command.Json, exception.Message); }
            catch (UnauthorizedAccessException exception) { return Usage(command.Json, exception.Message); }
        }
        try
        {
            HeadPartComposition? part = null;
            var composeHeadPart = command.Options.Keys.Any(key => key is "model" or "tri-race" or "tri-chargen" or "tri-dialogue" or
                "valid-races" or "extra-parts" or "flags" or "part-type" or "clone-from" or "retarget-valid-races");
            if (signature.Value == "HDPT" && composeHeadPart)
            {
                if (edition != GameEdition.SkyrimSpecialEdition || mode != RecordProposalMode.New)
                    return Usage(command.Json, "HDPT composition supports Skyrim SE new output-owned records.");
                string? clone = command.Options.GetValueOrDefault("clone-from");
                if (clone is not null && !FormReference.TryParse(clone, out _)) return Usage(command.Json, "--clone-from requires Plugin|FormID.");
                if (clone is not null && command.Options.Keys.Any(key => key is "model" or "tri-race" or "tri-chargen" or "tri-dialogue" or "flags" or "part-type" or "extra-parts" or "name"))
                    return Usage(command.Json, "HDPT cloning preserves model, morphs, flags, type, extras and name; use only --editor-id and ValidRaces retargeting.");
                var type = NpcHeadPartType.Misc;
                if (clone is null && (!command.Options.TryGetValue("part-type", out var typeText) || !NpcHeadPartTypeExtensions.TryParseWireName(typeText, out type)))
                    return Usage(command.Json, "HDPT requires --part-type.");
                string? valid = command.Options.GetValueOrDefault("retarget-valid-races") ?? command.Options.GetValueOrDefault("valid-races");
                if (valid is not null && !FormReference.TryParse(valid, out _)) return Usage(command.Json, "ValidRaces requires Plugin|FormID identifying a FLST.");
                byte flags = 0;
                foreach (var flag in (command.Options.GetValueOrDefault("flags") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    flags |= flag.ToLowerInvariant() switch { "playable" => (byte)1, "male" => (byte)2, "female" => (byte)4, "extra" => (byte)8, "solid-tint" => (byte)16, "use-texture-lighting" => (byte)32, _ => throw new ArgumentException("Unknown HDPT flag: " + flag) };
                var extra = (command.Options.GetValueOrDefault("extra-parts") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToImmutableArray();
                if (extra.Any(item => !FormReference.TryParse(item, out _))) return Usage(command.Json, "Extra parts require comma-separated Plugin|FormID references.");
                string? Asset(string key, string extension)
                {
                    if (!command.Options.TryGetValue(key, out var text))
                    {
                        if (clone is not null) return null;
                        throw new ArgumentException("HDPT requires --" + key + ".");
                    }
                    var asset = new AssetPath(text);
                    if (!asset.Value.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Invalid --" + key + " extension.");
                    return asset.Value;
                }
                part = new HeadPartComposition(Asset("model", ".nif"), Asset("tri-race", ".tri"), Asset("tri-chargen", ".tri"), Asset("tri-dialogue", ".tri"), valid, extra, flags, type, clone);
            }
            var result = await service.ProposeAsync(new RecordProposalRequest(edition, signature, mode, formId, source, editorId, command.Options.GetValueOrDefault("name"), masters, new WorkspacePath(outputPath), part), cancellationToken);
            if (command.Json) output.WriteLine(JsonSerializer.Serialize(result, JsonOptions)); else output.WriteLine(result.Written ? "records propose: proposal written" : "records propose: refused");
            return result.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? DiagnosticExitCodeClassifier.Classify(result.Diagnostics) : CommandExitCode.Success;
        }
        catch (ArgumentException exception) { return Usage(command.Json, exception.Message); }
    }

    private static bool TryParseSignature(string value, out RecordSignature signature) { try { signature = new RecordSignature(value); return true; } catch (ArgumentException) { signature = new RecordSignature("NPC_"); return false; } }
    private static bool TryParseEditorId(string value, out EditorId editorId) { try { editorId = new EditorId(value); return true; } catch (ArgumentException) { editorId = new EditorId("Invalid"); return false; } }
    private static bool TryParsePluginName(string value, out PluginName plugin) { try { plugin = new PluginName(value); return true; } catch (ArgumentException) { plugin = new PluginName("Invalid.esp"); return false; } }
    private static bool TryParseMode(string value, out RecordProposalMode mode)
    {
        switch (value)
        {
            case "new": mode = RecordProposalMode.New; return true;
            case "template": mode = RecordProposalMode.Template; return true;
            case "override": mode = RecordProposalMode.Override; return true;
            default: mode = RecordProposalMode.New; return false;
        }
    }
    private CommandExitCode Usage(bool json, string message) { error.WriteLine(json ? JsonSerializer.Serialize(new ErrorResponse("usage-error", message), JsonOptions) : $"ERROR usage-error: {message}"); return CommandExitCode.UsageError; }
    private sealed record ErrorResponse(string Code, string Message);
}
