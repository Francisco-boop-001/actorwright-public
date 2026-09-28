using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;
using Sse = Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

/// <summary>Materializes a source-bound apply-script proposal into one copied NPC plugin.</summary>
public sealed class BethesdaRuntimeScriptBinaryWriteService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IRuntimeScriptBinaryWriteService
{
    private const long MaximumProposalBytes = 1_048_576;
    private const string ReservedPrefix = "NPCM_Manolov_";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async ValueTask<RuntimeScriptBinaryWriteResult> WriteAsync(RuntimeScriptBinaryWriteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        RuntimeScriptProposalArtifact? proposal = null;
        if (!HasErrors(diagnostics))
        {
            try
            {
                var info = new FileInfo(request.Proposal.Value);
                if (info.Length <= 0 || info.Length > MaximumProposalBytes) throw new InvalidDataException("The runtime-script proposal is empty or too large.");
                await using var stream = new FileStream(request.Proposal.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                proposal = await JsonSerializer.DeserializeAsync<RuntimeScriptProposalArtifact>(stream, JsonOptions, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            { diagnostics.Add(new Diagnostic("runtime-script-binary-proposal-read-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (proposal is null && !HasErrors(diagnostics)) diagnostics.Add(new Diagnostic("runtime-script-binary-proposal-empty", DiagnosticSeverity.Error, "The runtime-script proposal must contain a JSON object."));
        if (proposal is null || HasErrors(diagnostics)) return Refused(request, null, diagnostics.ToImmutable());

        var targetFormId = default(FormId);
        var validTarget = !string.IsNullOrWhiteSpace(proposal.NpcFormId) && FormId.TryParse(proposal.NpcFormId, out targetFormId) && targetFormId.Value is > 0 and <= 0x00FF_FFFF;
        if (!validTarget)
            diagnostics.Add(new Diagnostic("runtime-script-binary-form-id", DiagnosticSeverity.Error, "The proposal NPC FormID must be a nonzero plugin-local 24-bit value."));
        var expectedScript = request.Edition == GameEdition.Fallout4 ? "NPCM_Manolov_ApplyFO4" : "NPCM_Manolov_ApplySSE";
        if (!string.Equals(proposal.Edition, request.Edition.ToWireName(), StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-binary-edition", DiagnosticSeverity.Error, "The proposal edition does not match the requested game."));
        if (!string.Equals(proposal.ScriptName, expectedScript, StringComparison.Ordinal)) diagnostics.Add(new Diagnostic("runtime-script-binary-script", DiagnosticSeverity.Error, "The proposal script does not match the requested game."));
        if (!string.Equals(proposal.ArtifactKind, "npc-apply-script-vmad-proposal", StringComparison.Ordinal) || proposal.SchemaVersion != "1") diagnostics.Add(new Diagnostic("runtime-script-binary-schema", DiagnosticSeverity.Error, "The runtime-script proposal schema is unsupported."));
        if (!proposal.ObjectReferences.IsDefaultOrEmpty) diagnostics.Add(new Diagnostic("runtime-script-binary-object-references", DiagnosticSeverity.Error, "NPC VMAD materialization refuses unbound object-reference rows."));
        if (!proposal.Fragments.IsDefaultOrEmpty) diagnostics.Add(new Diagnostic("runtime-script-binary-fragments", DiagnosticSeverity.Error, "NPC VMAD materialization refuses fragmented script data."));
        if (proposal.SourcePlugin is null || proposal.InputSha256 is null) diagnostics.Add(new Diagnostic("runtime-script-binary-source-binding", DiagnosticSeverity.Error, "The proposal must carry a source plugin and input hash for binary materialization."));
        else
        {
            var requestedSource = Path.GetFullPath(request.SourcePlugin.Value);
            var boundSource = Path.GetFullPath(proposal.SourcePlugin);
            if (!string.Equals(requestedSource, boundSource, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-binary-source", DiagnosticSeverity.Error, "The proposal source plugin does not match the requested source."));
            var sourceHash = new Sha256Hash(await HashAsync(request.SourcePlugin.Value, cancellationToken));
            if (!string.Equals(sourceHash.Value, proposal.InputSha256, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-binary-input-hash", DiagnosticSeverity.Error, "The proposal source hash does not match the current source plugin."));
        }
        ValidateProperties(proposal, request.Edition, diagnostics);
        if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());

        var temporaryDirectory = Path.Combine(Path.GetDirectoryName(request.Output.Value)!, ".npcm-vmad-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporaryDirectory);
        var temporary = Path.Combine(temporaryDirectory, Path.GetFileName(request.Output.Value));
        try
        {
            if (request.Edition == GameEdition.Fallout4) WriteFallout4(request.SourcePlugin.Value, temporary, targetFormId, proposal);
            else WriteSkyrim(request.SourcePlugin.Value, temporary, targetFormId, proposal);
            VerifyOutput(request.Edition, temporary, targetFormId, proposal);
            File.Move(temporary, request.Output.Value, overwrite: false);
            var hash = new Sha256Hash(await HashAsync(request.Output.Value, cancellationToken));
            return new RuntimeScriptBinaryWriteResult(true, request.Output, targetFormId, hash, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        { diagnostics.Add(new Diagnostic("runtime-script-binary-write-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(request, targetFormId, diagnostics.ToImmutable()); }
        finally { TryDelete(temporary); TryDeleteDirectory(temporaryDirectory); }
    }

    private ImmutableArray<Diagnostic> ValidatePaths(RuntimeScriptBinaryWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourcePlugin));
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.Proposal));
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("runtime-script-binary-output-outside-lab", DiagnosticSeverity.Error, "Output must remain under K."));
        if (!request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-binary-output-extension", DiagnosticSeverity.Error, "The VMAD writer emits ordinary .esp plugins only."));
        if (!request.SourcePlugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) && !request.SourcePlugin.Value.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) && !request.SourcePlugin.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-binary-source-extension", DiagnosticSeverity.Error, "Source must use .esp, .esm, or .esl."));
        if (!request.Proposal.Value.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-binary-proposal-extension", DiagnosticSeverity.Error, "Proposal must use .json."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("runtime-script-binary-output-exists", DiagnosticSeverity.Error, "Binary writes never overwrite an existing output."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("runtime-script-binary-output-parent", DiagnosticSeverity.Error, "The output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("runtime-script-binary-source-missing", DiagnosticSeverity.Error, "The source plugin does not exist."));
        if (!File.Exists(request.Proposal.Value)) diagnostics.Add(new Diagnostic("runtime-script-binary-proposal-missing", DiagnosticSeverity.Error, "The runtime-script proposal does not exist."));
        return diagnostics.ToImmutable();
    }

    private static void ValidateProperties(RuntimeScriptProposalArtifact proposal, GameEdition edition, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var expected = ExpectedProperties(edition);
        if (proposal.Properties.IsDefaultOrEmpty || proposal.Properties.Length != expected.Count) diagnostics.Add(new Diagnostic("runtime-script-binary-property-set", DiagnosticSeverity.Error, "The proposal must contain the complete pinned property set."));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in proposal.Properties)
        {
            if (property is null || string.IsNullOrWhiteSpace(property.Name))
            {
                diagnostics.Add(new Diagnostic("runtime-script-binary-property", DiagnosticSeverity.Error, "Every VMAD property must have a non-empty name."));
                continue;
            }
            if (!seen.Add(property.Name) || !expected.TryGetValue(property.Name, out var type) || !string.Equals(property.Type, type, StringComparison.Ordinal)) diagnostics.Add(new Diagnostic("runtime-script-binary-property", DiagnosticSeverity.Error, $"Property '{property.Name}' is duplicated, unknown, or has the wrong type."));
            if (!ValidValue(property.Value, property.Type)) diagnostics.Add(new Diagnostic("runtime-script-binary-property-value", DiagnosticSeverity.Error, $"Property '{property.Name}' has an invalid value."));
        }
        foreach (var name in expected.Keys.Where(name => !seen.Contains(name))) diagnostics.Add(new Diagnostic("runtime-script-binary-property-missing", DiagnosticSeverity.Error, $"Property '{name}' is missing."));
    }

    private static bool ValidValue(JsonElement value, string? type)
    {
        if (type is null) return false;
        var array = type.EndsWith("[]", StringComparison.Ordinal);
        if (array && value.ValueKind != JsonValueKind.Array) return false;
        if (!array) return type switch { "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False, "int" => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _), "float" => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var f) && float.IsFinite(f), "string" => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 32768 } s && !s.Contains('\0'), _ => false };
        return value.GetArrayLength() > 0 && value.EnumerateArray().All(item => ValidValue(item, type[..^2]));
    }

    private static ImmutableDictionary<string, string> ExpectedProperties(GameEdition edition) =>
        (edition == GameEdition.Fallout4
            ? new Dictionary<string, string> { ["IsFemale"] = "bool", ["SchemaVersion"] = "int", ["OvlTemplate"] = "string[]", ["OvlPriority"] = "int[]", ["OvlRed"] = "float[]", ["OvlGreen"] = "float[]", ["OvlBlue"] = "float[]", ["OvlAlpha"] = "float[]", ["OvlOffsetU"] = "float[]", ["OvlOffsetV"] = "float[]", ["OvlScaleU"] = "float[]", ["OvlScaleV"] = "float[]", ["SkinTemplate"] = "string" }
            : new Dictionary<string, string> { ["IsFemale"] = "bool", ["SchemaVersion"] = "int", ["OvlNode"] = "string[]", ["OvlDiffuse"] = "string[]", ["OvlNormal"] = "string[]", ["OvlHasTint"] = "bool[]", ["OvlTint"] = "int[]", ["OvlHasAlpha"] = "bool[]", ["OvlAlpha"] = "float[]", ["SkinSlot"] = "int[]", ["SkinDiffuse"] = "string[]", ["SkinNormal"] = "string[]", ["SkinHasTint"] = "bool[]", ["SkinTint"] = "int[]", ["NodeName"] = "string[]", ["NodeHasScale"] = "bool[]", ["NodeScale"] = "float[]", ["NodeHasPos"] = "bool[]", ["NodePosX"] = "float[]", ["NodePosY"] = "float[]", ["NodePosZ"] = "float[]", ["NodeHasRot"] = "bool[]", ["NodeRotM0"] = "float[]", ["NodeRotM1"] = "float[]", ["NodeRotM2"] = "float[]", ["NodeRotM3"] = "float[]", ["NodeRotM4"] = "float[]", ["NodeRotM5"] = "float[]", ["NodeRotM6"] = "float[]", ["NodeRotM7"] = "float[]", ["NodeRotM8"] = "float[]", ["NodeScaleMode"] = "int[]" })
            .ToImmutableDictionary(StringComparer.Ordinal);

    private static void WriteFallout4(string source, string destination, FormId target, RuntimeScriptProposalArtifact proposal)
    {
        var sourceKey = ToModKey(source);
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(sourceKey, new FilePath(source)), Fo4.Fallout4Release.Fallout4);
        var mod = (Fo4.Fallout4Mod)overlay.DeepCopy();
        var npc = mod.Npcs[new FormKey(sourceKey, target.Value)];
        npc.VirtualMachineAdapter = BuildFo4Adapter(npc.VirtualMachineAdapter, proposal);
        WriteMod(mod, destination);
    }

    private static void WriteSkyrim(string source, string destination, FormId target, RuntimeScriptProposalArtifact proposal)
    {
        var sourceKey = ToModKey(source);
        using var overlay = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(sourceKey, new FilePath(source)), Sse.SkyrimRelease.SkyrimSE);
        var mod = (Sse.SkyrimMod)overlay.DeepCopy();
        var npc = mod.Npcs[new FormKey(sourceKey, target.Value)];
        npc.VirtualMachineAdapter = BuildSseAdapter(npc.VirtualMachineAdapter, proposal);
        WriteMod(mod, destination);
    }

    private static Fo4.VirtualMachineAdapter BuildFo4Adapter(Fo4.VirtualMachineAdapter? existing, RuntimeScriptProposalArtifact proposal)
    {
        var adapter = existing ?? new Fo4.VirtualMachineAdapter { Version = 6, ObjectFormat = 2 };
        var kept = adapter.Scripts.Where(script => !script.Name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        adapter.Scripts.Clear(); foreach (var script in kept) adapter.Scripts.Add(script);
        adapter.Scripts.Add(BuildFo4Script(proposal)); return adapter;
    }

    private static Sse.VirtualMachineAdapter BuildSseAdapter(Sse.VirtualMachineAdapter? existing, RuntimeScriptProposalArtifact proposal)
    {
        var adapter = existing ?? new Sse.VirtualMachineAdapter { Version = 5, ObjectFormat = 2 };
        var kept = adapter.Scripts.Where(script => !script.Name.StartsWith(ReservedPrefix, StringComparison.OrdinalIgnoreCase)).ToArray();
        adapter.Scripts.Clear(); foreach (var script in kept) adapter.Scripts.Add(script);
        adapter.Scripts.Add(BuildSseScript(proposal)); return adapter;
    }

    private static Fo4.ScriptEntry BuildFo4Script(RuntimeScriptProposalArtifact proposal)
    {
        var script = new Fo4.ScriptEntry { Name = proposal.ScriptName };
        foreach (var property in proposal.Properties) script.Properties.Add(BuildFo4Property(property));
        return script;
    }

    private static Sse.ScriptEntry BuildSseScript(RuntimeScriptProposalArtifact proposal)
    {
        var script = new Sse.ScriptEntry { Name = proposal.ScriptName };
        foreach (var property in proposal.Properties) script.Properties.Add(BuildSseProperty(property));
        return script;
    }

    private static Fo4.ScriptProperty BuildFo4Property(RuntimeScriptPropertyArtifact property) => property.Type switch
    {
        "bool" => new Fo4.ScriptBoolProperty { Name = property.Name, Data = property.Value.GetBoolean(), Flags = (Fo4.ScriptProperty.Flag)1 },
        "int" => new Fo4.ScriptIntProperty { Name = property.Name, Data = property.Value.GetInt32(), Flags = (Fo4.ScriptProperty.Flag)1 },
        "float" => new Fo4.ScriptFloatProperty { Name = property.Name, Data = property.Value.GetSingle(), Flags = (Fo4.ScriptProperty.Flag)1 },
        "string" => new Fo4.ScriptStringProperty { Name = property.Name, Data = property.Value.GetString()!, Flags = (Fo4.ScriptProperty.Flag)1 },
        "bool[]" => new Fo4.ScriptBoolListProperty { Name = property.Name, Data = ToExtended(property.Value.EnumerateArray().Select(item => item.GetBoolean())), Flags = (Fo4.ScriptProperty.Flag)1 },
        "int[]" => new Fo4.ScriptIntListProperty { Name = property.Name, Data = ToExtended(property.Value.EnumerateArray().Select(item => item.GetInt32())), Flags = (Fo4.ScriptProperty.Flag)1 },
        "float[]" => new Fo4.ScriptFloatListProperty { Name = property.Name, Data = ToExtended(property.Value.EnumerateArray().Select(item => item.GetSingle())), Flags = (Fo4.ScriptProperty.Flag)1 },
        "string[]" => new Fo4.ScriptStringListProperty { Name = property.Name, Data = ToExtended(property.Value.EnumerateArray().Select(item => item.GetString()!)), Flags = (Fo4.ScriptProperty.Flag)1 },
        _ => throw new InvalidDataException($"Unsupported FO4 VMAD property type '{property.Type}'.")
    };

    private static Sse.ScriptProperty BuildSseProperty(RuntimeScriptPropertyArtifact property) => property.Type switch
    {
        "bool" => new Sse.ScriptBoolProperty { Name = property.Name, Data = property.Value.GetBoolean(), Flags = (Sse.ScriptProperty.Flag)1 },
        "int" => new Sse.ScriptIntProperty { Name = property.Name, Data = property.Value.GetInt32(), Flags = (Sse.ScriptProperty.Flag)1 },
        "float" => new Sse.ScriptFloatProperty { Name = property.Name, Data = property.Value.GetSingle(), Flags = (Sse.ScriptProperty.Flag)1 },
        "string" => new Sse.ScriptStringProperty { Name = property.Name, Data = property.Value.GetString()!, Flags = (Sse.ScriptProperty.Flag)1 },
        "bool[]" => new Sse.ScriptBoolListProperty { Name = property.Name, Data = ToExtended(property.Value.EnumerateArray().Select(item => item.GetBoolean())), Flags = (Sse.ScriptProperty.Flag)1 },
        "int[]" => new Sse.ScriptIntListProperty { Name = property.Name, Data = ToExtended(property.Value.EnumerateArray().Select(item => item.GetInt32())), Flags = (Sse.ScriptProperty.Flag)1 },
        "float[]" => new Sse.ScriptFloatListProperty { Name = property.Name, Data = ToExtended(property.Value.EnumerateArray().Select(item => item.GetSingle())), Flags = (Sse.ScriptProperty.Flag)1 },
        "string[]" => new Sse.ScriptStringListProperty { Name = property.Name, Data = ToExtended(property.Value.EnumerateArray().Select(item => item.GetString()!)), Flags = (Sse.ScriptProperty.Flag)1 },
        _ => throw new InvalidDataException($"Unsupported SSE VMAD property type '{property.Type}'.")
    };

    private static void VerifyOutput(GameEdition edition, string path, FormId target, RuntimeScriptProposalArtifact proposal)
    {
        if (edition == GameEdition.Fallout4)
        {
            var modKey = ToModKey(path); using var mod = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(path)), Fo4.Fallout4Release.Fallout4);
            var npc = mod.Npcs[new FormKey(modKey, target.Value)]; var script = npc.VirtualMachineAdapter?.Scripts.SingleOrDefault(item => item.Name == proposal.ScriptName);
            if (script is null || script.Properties.Count != proposal.Properties.Length) throw new InvalidDataException("The written Fallout 4 VMAD did not round-trip the requested script.");
        }
        else
        {
            var modKey = ToModKey(path); using var mod = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(path)), Sse.SkyrimRelease.SkyrimSE);
            var npc = mod.Npcs[new FormKey(modKey, target.Value)]; var script = npc.VirtualMachineAdapter?.Scripts.SingleOrDefault(item => item.Name == proposal.ScriptName);
            if (script is null || script.Properties.Count != proposal.Properties.Length) throw new InvalidDataException("The written Skyrim VMAD did not round-trip the requested script.");
        }
    }

    private static void WriteMod(IModGetter mod, string destination) => mod.WriteToBinary(new FilePath(destination), new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck, MastersListOrdering = MastersListOrderingOption.NoCheck });
    private static async ValueTask<string> HashAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
    }
    private static ExtendedList<T> ToExtended<T>(IEnumerable<T> values)
    {
        var result = new ExtendedList<T>();
        foreach (var value in values) result.Add(value);
        return result;
    }
    private static ModKey ToModKey(string path) => new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static RuntimeScriptBinaryWriteResult Refused(RuntimeScriptBinaryWriteRequest request, FormId? target, ImmutableArray<Diagnostic> diagnostics) => new(false, request.Output, target, null, diagnostics);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
    private static void TryDeleteDirectory(string path) { try { if (Directory.Exists(path)) Directory.Delete(path, true); } catch { } }
}
