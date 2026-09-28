using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Produces a typed, hash-bound VMAD proposal.  This deliberately stops before binary VMAD mutation:
/// the proposal is the safe seam where a later record writer can be admitted without guessing at a
/// fragile Bethesda subrecord format.
/// </summary>
public sealed class RuntimeScriptProposalService(IWorkspacePolicy policy, WorkspacePath labRoot) : IRuntimeScriptProposalService
{
    private const string EmitterIdentity =
        "actorwright://provenance/fo4-npc-manager/NpcApplyScriptEmitter.vb";
    private const string EmitterResourceName =
        "Actorwright.Provenance.Fo4NpcManager.NpcApplyScriptEmitter.vb";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private static readonly ImmutableDictionary<string, RuntimeScriptPropertyType> Fo4Properties =
        new Dictionary<string, RuntimeScriptPropertyType>(StringComparer.Ordinal)
        {
            ["IsFemale"] = RuntimeScriptPropertyType.BoolValue,
            ["SchemaVersion"] = RuntimeScriptPropertyType.IntValue,
            ["OvlTemplate"] = RuntimeScriptPropertyType.StringArray,
            ["OvlPriority"] = RuntimeScriptPropertyType.IntArray,
            ["OvlRed"] = RuntimeScriptPropertyType.FloatArray,
            ["OvlGreen"] = RuntimeScriptPropertyType.FloatArray,
            ["OvlBlue"] = RuntimeScriptPropertyType.FloatArray,
            ["OvlAlpha"] = RuntimeScriptPropertyType.FloatArray,
            ["OvlOffsetU"] = RuntimeScriptPropertyType.FloatArray,
            ["OvlOffsetV"] = RuntimeScriptPropertyType.FloatArray,
            ["OvlScaleU"] = RuntimeScriptPropertyType.FloatArray,
            ["OvlScaleV"] = RuntimeScriptPropertyType.FloatArray,
            ["SkinTemplate"] = RuntimeScriptPropertyType.StringValue
        }.ToImmutableDictionary(StringComparer.Ordinal);
    private static readonly ImmutableDictionary<string, RuntimeScriptPropertyType> SseProperties =
        new Dictionary<string, RuntimeScriptPropertyType>(StringComparer.Ordinal)
        {
            ["IsFemale"] = RuntimeScriptPropertyType.BoolValue,
            ["SchemaVersion"] = RuntimeScriptPropertyType.IntValue,
            ["OvlNode"] = RuntimeScriptPropertyType.StringArray,
            ["OvlDiffuse"] = RuntimeScriptPropertyType.StringArray,
            ["OvlNormal"] = RuntimeScriptPropertyType.StringArray,
            ["OvlHasTint"] = RuntimeScriptPropertyType.BoolArray,
            ["OvlTint"] = RuntimeScriptPropertyType.IntArray,
            ["OvlHasAlpha"] = RuntimeScriptPropertyType.BoolArray,
            ["OvlAlpha"] = RuntimeScriptPropertyType.FloatArray,
            ["SkinSlot"] = RuntimeScriptPropertyType.IntArray,
            ["SkinDiffuse"] = RuntimeScriptPropertyType.StringArray,
            ["SkinNormal"] = RuntimeScriptPropertyType.StringArray,
            ["SkinHasTint"] = RuntimeScriptPropertyType.BoolArray,
            ["SkinTint"] = RuntimeScriptPropertyType.IntArray,
            ["NodeName"] = RuntimeScriptPropertyType.StringArray,
            ["NodeHasScale"] = RuntimeScriptPropertyType.BoolArray,
            ["NodeScale"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeHasPos"] = RuntimeScriptPropertyType.BoolArray,
            ["NodePosX"] = RuntimeScriptPropertyType.FloatArray,
            ["NodePosY"] = RuntimeScriptPropertyType.FloatArray,
            ["NodePosZ"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeHasRot"] = RuntimeScriptPropertyType.BoolArray,
            ["NodeRotM0"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeRotM1"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeRotM2"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeRotM3"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeRotM4"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeRotM5"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeRotM6"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeRotM7"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeRotM8"] = RuntimeScriptPropertyType.FloatArray,
            ["NodeScaleMode"] = RuntimeScriptPropertyType.IntArray
        }.ToImmutableDictionary(StringComparer.Ordinal);

    public async ValueTask<RuntimeScriptProposalResult> ProposeAsync(RuntimeScriptProposalRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var expected = ExpectedProperties(request.Edition);
        Validate(request, expected, diagnostics);
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(diagnostics);

        Sha256Hash? inputHash = null;
        if (request.SourcePlugin is { } sourcePlugin)
        {
            if (!sourcePlugin.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("runtime-script-source-outside-lab", DiagnosticSeverity.Error, "The source plugin must remain under the K-only lab root."));
            if (!sourcePlugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) && !sourcePlugin.Value.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) && !sourcePlugin.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-source-extension", DiagnosticSeverity.Error, "The source plugin must use an .esp, .esm, or .esl extension."));
            if (!File.Exists(sourcePlugin.Value)) diagnostics.Add(new Diagnostic("runtime-script-source-missing", DiagnosticSeverity.Error, "The source plugin does not exist."));
            else if ((File.GetAttributes(sourcePlugin.Value) & FileAttributes.ReparsePoint) != 0) diagnostics.Add(new Diagnostic("runtime-script-source-reparse", DiagnosticSeverity.Error, "The source plugin may not be a reparse point."));
            if (!diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) inputHash = await HashAsync(sourcePlugin.Value, cancellationToken);
        }
        if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) return Refused(diagnostics);

        byte[] emitterBytes = ReadEmitterSource();
        var emitterHash = new Sha256Hash(Convert.ToHexString(
            SHA256.HashData(emitterBytes)));
        var artifact = new RuntimeScriptProposalArtifact("1", "npc-apply-script-vmad-proposal",
            request.Edition.ToWireName(), request.NpcFormId.ToString(), request.ScriptName,
            request.Properties.OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new RuntimeScriptPropertyArtifact(item.Name, WireType(item.Type), item.Value.Clone())).ToImmutableArray(),
            request.ObjectReferences.OrderBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => new RuntimeScriptObjectReferenceArtifact(item.Name, item.Reference.ToString())).ToImmutableArray(),
            request.Fragments.OrderBy(item => item.Index).Select(item => new RuntimeScriptFragmentArtifact(item.Index, item.StartInstruction, item.EndInstruction)).ToImmutableArray(),
            EmitterIdentity, emitterHash.Value, false, true, request.SourcePlugin?.Value, inputHash?.Value);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, request.Output.Value, overwrite: false);
            return new RuntimeScriptProposalResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (IOException exception) { diagnostics.Add(new Diagnostic("runtime-script-proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); }
        catch (UnauthorizedAccessException exception) { diagnostics.Add(new Diagnostic("runtime-script-proposal-write-denied", DiagnosticSeverity.Error, exception.Message)); }
        finally { TryDelete(temporary); }
        return Refused(diagnostics, artifact);
    }

    private static byte[] ReadEmitterSource()
    {
        Assembly assembly = typeof(RuntimeScriptProposalService).Assembly;
        using Stream stream = assembly.GetManifestResourceStream(EmitterResourceName) ??
            throw new InvalidOperationException(
                $"Embedded emitter provenance is missing: {EmitterResourceName}");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void Validate(RuntimeScriptProposalRequest request, ImmutableDictionary<string, RuntimeScriptPropertyType> expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var script = request.Edition == GameEdition.Fallout4 ? "NPCM_Manolov_ApplyFO4" : "NPCM_Manolov_ApplySSE";
        if (!string.Equals(request.ScriptName, script, StringComparison.Ordinal)) diagnostics.Add(new Diagnostic("runtime-script-game-mismatch", DiagnosticSeverity.Error, "Script name does not match the selected game."));
        if (request.NpcFormId.Value == 0 || request.NpcFormId.Value > 0x00FF_FFFF) diagnostics.Add(new Diagnostic("runtime-script-form-id-invalid", DiagnosticSeverity.Error, "NPC FormID must be a nonzero plugin-local 24-bit value."));
        if (request.Properties.Length != expected.Count) diagnostics.Add(new Diagnostic("runtime-script-property-set", DiagnosticSeverity.Error, "The proposal must contain every property emitted by the pinned script emitter exactly once."));
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in request.Properties)
        {
            if (!seen.Add(property.Name)) { diagnostics.Add(new Diagnostic("runtime-script-property-duplicate", DiagnosticSeverity.Error, $"Property '{property.Name}' is duplicated.")); continue; }
            if (!expected.TryGetValue(property.Name, out var wanted)) { diagnostics.Add(new Diagnostic("runtime-script-property-unknown", DiagnosticSeverity.Error, $"Property '{property.Name}' is not declared by the pinned script.")); continue; }
            if (wanted != property.Type) diagnostics.Add(new Diagnostic("runtime-script-property-type", DiagnosticSeverity.Error, $"Property '{property.Name}' has type {WireType(property.Type)}; expected {WireType(wanted)}."));
            ValidateValue(property, diagnostics);
        }
        foreach (var name in expected.Keys.Where(name => !seen.Contains(name))) diagnostics.Add(new Diagnostic("runtime-script-property-missing", DiagnosticSeverity.Error, $"Property '{name}' is missing."));
        ValidateParallelArrays(request, expected, diagnostics);
        var refNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in request.ObjectReferences)
        {
            if (!refNames.Add(reference.Name) || string.IsNullOrWhiteSpace(reference.Name)) diagnostics.Add(new Diagnostic("runtime-script-object-reference-name", DiagnosticSeverity.Error, "Object-reference names must be non-empty and unique."));
        }
        var lastEnd = -1;
        foreach (var fragment in request.Fragments.OrderBy(item => item.Index))
        {
            if (fragment.Index < 0 || fragment.StartInstruction < 0 || fragment.EndInstruction <= fragment.StartInstruction || fragment.StartInstruction < lastEnd)
                diagnostics.Add(new Diagnostic("runtime-script-fragment-boundary", DiagnosticSeverity.Error, "Fragments must be non-overlapping positive instruction ranges."));
            lastEnd = Math.Max(lastEnd, fragment.EndInstruction);
        }
        if (!request.Output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("runtime-script-output-outside-lab", DiagnosticSeverity.Error, "Output must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".runtime-script-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("runtime-script-output-extension", DiagnosticSeverity.Error, "Runtime-script proposals require the .runtime-script-proposal.json extension."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("runtime-script-output-exists", DiagnosticSeverity.Error, "Runtime-script proposals never overwrite existing artifacts."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("runtime-script-output-parent-missing", DiagnosticSeverity.Error, "The proposal output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
    }

    private static void ValidateValue(RuntimeScriptInputProperty property, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var value = property.Value;
        var array = property.Type is RuntimeScriptPropertyType.BoolArray or RuntimeScriptPropertyType.IntArray or RuntimeScriptPropertyType.FloatArray or RuntimeScriptPropertyType.StringArray;
        if (array)
        {
            if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() == 0) { diagnostics.Add(new Diagnostic("runtime-script-array-invalid", DiagnosticSeverity.Error, $"Array property '{property.Name}' must be non-empty.")); return; }
            foreach (var item in value.EnumerateArray()) ValidateScalar(property.Name, property.Type, item, diagnostics);
            return;
        }
        ValidateScalar(property.Name, property.Type, value, diagnostics);
    }

    private static void ValidateScalar(string name, RuntimeScriptPropertyType type, JsonElement value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var valid = type switch
        {
            RuntimeScriptPropertyType.BoolValue or RuntimeScriptPropertyType.BoolArray => value.ValueKind is JsonValueKind.True or JsonValueKind.False,
            RuntimeScriptPropertyType.IntValue or RuntimeScriptPropertyType.IntArray => value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out _),
            RuntimeScriptPropertyType.FloatValue or RuntimeScriptPropertyType.FloatArray => value.ValueKind == JsonValueKind.Number && value.TryGetSingle(out var f) && float.IsFinite(f),
            RuntimeScriptPropertyType.StringValue or RuntimeScriptPropertyType.StringArray => value.ValueKind == JsonValueKind.String && value.GetString() is { Length: <= 32768 } s && !s.Contains('\0'),
            _ => false
        };
        if (!valid) diagnostics.Add(new Diagnostic("runtime-script-property-value", DiagnosticSeverity.Error, $"Property '{name}' contains a value incompatible with {WireType(type)}."));
    }

    private static void ValidateParallelArrays(RuntimeScriptProposalRequest request, ImmutableDictionary<string, RuntimeScriptPropertyType> expected, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var values = request.Properties.GroupBy(item => item.Name, StringComparer.Ordinal).ToDictionary(group => group.Key, group => group.First().Value, StringComparer.Ordinal);
        var groups = request.Edition == GameEdition.Fallout4
            ? new[] { new[] { "OvlTemplate", "OvlPriority", "OvlRed", "OvlGreen", "OvlBlue", "OvlAlpha", "OvlOffsetU", "OvlOffsetV", "OvlScaleU", "OvlScaleV" } }
            : new[] { new[] { "OvlNode", "OvlDiffuse", "OvlNormal", "OvlHasTint", "OvlTint", "OvlHasAlpha", "OvlAlpha" }, new[] { "SkinSlot", "SkinDiffuse", "SkinNormal", "SkinHasTint", "SkinTint" }, new[] { "NodeName", "NodeHasScale", "NodeScale", "NodeHasPos", "NodePosX", "NodePosY", "NodePosZ", "NodeHasRot", "NodeRotM0", "NodeRotM1", "NodeRotM2", "NodeRotM3", "NodeRotM4", "NodeRotM5", "NodeRotM6", "NodeRotM7", "NodeRotM8", "NodeScaleMode" } };
        foreach (var group in groups)
        {
            var lengths = group.Where(values.ContainsKey).Select(name => values[name]).Where(value => value.ValueKind == JsonValueKind.Array).Select(value => value.GetArrayLength()).Distinct().ToArray();
            if (lengths.Length > 1) diagnostics.Add(new Diagnostic("runtime-script-parallel-arrays", DiagnosticSeverity.Error, $"Parallel arrays are not the same length: {string.Join(", ", group)}."));
        }
    }

    private static ImmutableDictionary<string, RuntimeScriptPropertyType> ExpectedProperties(GameEdition edition) => edition == GameEdition.Fallout4 ? Fo4Properties : SseProperties;
    private static string WireType(RuntimeScriptPropertyType type) => type switch { RuntimeScriptPropertyType.BoolValue => "bool", RuntimeScriptPropertyType.IntValue => "int", RuntimeScriptPropertyType.FloatValue => "float", RuntimeScriptPropertyType.StringValue => "string", RuntimeScriptPropertyType.BoolArray => "bool[]", RuntimeScriptPropertyType.IntArray => "int[]", RuntimeScriptPropertyType.FloatArray => "float[]", RuntimeScriptPropertyType.StringArray => "string[]", _ => "unknown" };
    private static async ValueTask<Sha256Hash> HashAsync(string path, CancellationToken cancellationToken) { await using var stream = File.OpenRead(path); return new Sha256Hash(Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken))); }
    private static RuntimeScriptProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics, RuntimeScriptProposalArtifact? artifact = null) => new(false, artifact, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
}

