using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda.Plugins;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;
using Sse = Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reads one copied plugin's NPC VMAD script identity and property names.
/// This is a no-write structural check; it does not execute Papyrus or prove
/// live load-order, deployment, or in-game behavior.
/// </summary>
public sealed class BethesdaRuntimeScriptVmadInspectService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IRuntimeScriptVmadInspectService
{
    public async ValueTask<RuntimeScriptVmadInspectResult> InspectAsync(
        RuntimeScriptVmadInspectRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        Validate(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        try
        {
            await using var stream = new FileStream(request.Plugin.Value, FileMode.Open, FileAccess.Read,
                FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken));
            var script = request.Edition == GameEdition.Fallout4
                ? ReadFallout4(request.Plugin.Value, request.NpcFormId, request.ScriptName)
                : ReadSkyrim(request.Plugin.Value, request.NpcFormId, request.ScriptName);
            var artifact = new RuntimeScriptVmadInspectArtifact(
                "1", "runtime-script-vmad-inspection", request.Edition.ToWireName(),
                Path.GetFileName(request.Plugin.Value), hash, request.NpcFormId.ToString(),
                request.ScriptName, script.ToImmutableArray(), script.Length, true, false);
            return new RuntimeScriptVmadInspectResult(true, artifact, diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or KeyNotFoundException)
        {
            diagnostics.Add(new Diagnostic("runtime-script-vmad-inspect-failed", DiagnosticSeverity.Error,
                exception.Message));
            return Refused(diagnostics);
        }
    }

    private void Validate(RuntimeScriptVmadInspectRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.Plugin));
        if (!File.Exists(request.Plugin.Value))
            diagnostics.Add(new Diagnostic("runtime-script-vmad-plugin-missing", DiagnosticSeverity.Error,
                "The copied plugin does not exist."));
        else if (File.GetAttributes(request.Plugin.Value).HasFlag(FileAttributes.ReparsePoint))
            diagnostics.Add(new Diagnostic("runtime-script-vmad-plugin-reparse", DiagnosticSeverity.Error,
                "The copied plugin may not be a reparse point."));
        if (!request.Plugin.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase) &&
            !request.Plugin.Value.EndsWith(".esm", StringComparison.OrdinalIgnoreCase) &&
            !request.Plugin.Value.EndsWith(".esl", StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(new Diagnostic("runtime-script-vmad-plugin-extension", DiagnosticSeverity.Error,
                "The copied plugin must use .esp, .esm, or .esl."));
        if (request.NpcFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("runtime-script-vmad-form-id", DiagnosticSeverity.Error,
                "The NPC FormID must be a nonzero plugin-local 24-bit value."));
        var expected = request.Edition == GameEdition.Fallout4 ? "NPCM_Manolov_ApplyFO4" : "NPCM_Manolov_ApplySSE";
        if (!string.Equals(request.ScriptName, expected, StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("runtime-script-vmad-script", DiagnosticSeverity.Error,
                "The script name does not match the selected game."));
    }

    private static string[] ReadFallout4(string path, FormId formId, string scriptName)
    {
        var modKey = ToModKey(path);
        using var mod = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(path)), Fo4.Fallout4Release.Fallout4);
        var npc = mod.Npcs[new FormKey(modKey, formId.Value)];
        var script = npc.VirtualMachineAdapter?.Scripts.SingleOrDefault(item => item.Name == scriptName)
            ?? throw new InvalidDataException($"NPC {formId} does not contain VMAD script '{scriptName}'.");
        return script.Properties.Select(item => item.Name).ToArray();
    }

    private static string[] ReadSkyrim(string path, FormId formId, string scriptName)
    {
        var modKey = ToModKey(path);
        using var mod = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(modKey, new FilePath(path)), Sse.SkyrimRelease.SkyrimSE);
        var npc = mod.Npcs[new FormKey(modKey, formId.Value)];
        var script = npc.VirtualMachineAdapter?.Scripts.SingleOrDefault(item => item.Name == scriptName)
            ?? throw new InvalidDataException($"NPC {formId} does not contain VMAD script '{scriptName}'.");
        return script.Properties.Select(item => item.Name).ToArray();
    }

    private static ModKey ToModKey(string path) => new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static RuntimeScriptVmadInspectResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
