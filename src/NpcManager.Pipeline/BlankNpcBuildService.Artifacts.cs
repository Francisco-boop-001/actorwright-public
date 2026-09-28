using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Pipeline;

public sealed partial class BlankNpcBuildService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static async ValueTask<Sha256Hash> WriteRuntimeKitAsync(
        BlankNpcBuildRequest request,
        FormId formId,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            schemaVersion = "1",
            artifactKind = "skyrim-runtime-validation-kit",
            plugin = request.OutputPlugin.Value,
            localFormId = formId.ToString(),
            editorId = request.Identity.EditorId.Value,
            runtimeAuthority = false,
            commands = new[]
            {
                $"help \"{request.Identity.EditorId.Value}\" 4 NPC_",
                $"player.placeatme <resolved-load-order-prefix>{formId.Value & 0x00FF_FFFFu:X6} 1"
            },
            checks = new[]
            {
                "Confirm the active plugin provider and the package plugin hash.",
                "Confirm active FaceGeom and FaceTint provider hashes.",
                "Capture face, neck, body, hands, eyes, and outfit in normal and alternate lighting.",
                "Keep a known-good control NPC in the same frame and lighting."
            }
        };
        return await WriteJsonAtomicallyAsync(destination, payload, cancellationToken);
    }

    private static async ValueTask<Sha256Hash> WriteRuntimeDiagnosticBatchAsync(
        BlankNpcBuildRequest request,
        FormId formId,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        var localFormId = formId.Value & 0x00FF_FFFFu;
        var batch = string.Join("\r\n",
        [
            "; Generated Skyrim runtime diagnostic batch.",
            $"; Plugin: {request.OutputPlugin.Value}",
            $"; NPC EditorID: {request.Identity.EditorId.Value}",
            $"; Local FormID: {localFormId:X6}",
            ";",
            "; THROWAWAY SAVE ONLY: setnpcweight changes the live actor.",
            "; Before running this batch:",
            $";   1. In the console, run: help \"{request.Identity.EditorId.Value}\" 4 NPC_",
            $";   2. Resolve the plugin load-order prefix, then run: player.placeatme <prefix>{localFormId:X6} 1",
            ";   3. Click the spawned NPC so its reference is selected.",
            $";   4. Run: bat {Path.GetFileNameWithoutExtension(destination.Value)}",
            "; Keep a known-good control NPC in the same frame and lighting for screenshots.",
            ";",
            "; Block 1: selected-actor identity evidence.",
            "getavinfo aggression",
            "; Screenshot the console header and selected actor.",
            ";",
            "; Block 2: face rebuild route probe.",
            "setnpcweight 100",
            "; Screenshot face, neck, body, hands, eyes, and outfit.",
            ";",
            "; Block 3: expression and TRI-morph binding probe.",
            "mfg expression 7 100",
            "; Screenshot the expression state.",
            "mfg reset",
            "; Screenshot the neutral state.",
            ";",
            "; Block 4: stale-render probe.",
            "disable",
            "enable",
            "; Screenshot the re-enabled actor in normal and alternate lighting.",
            ""
        ]);
        var bytes = Encoding.UTF8.GetBytes(batch);
        var hash = new Sha256Hash(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)));
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, destination.Value, overwrite: false);
            return hash;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static ValueTask<Sha256Hash> WriteExactFaceTintEvidenceAsync(
        ExactDdsBlankNpcFaceTintSource source,
        WorkspacePath packageRoot,
        WorkspacePath output,
        Sha256Hash outputHash,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        if (outputHash != source.ExpectedSha256)
            throw new InvalidDataException(
                "Durable exact FaceTint evidence requires a byte-exact output hash.");
        var retainedByteLength = new FileInfo(output.Value).Length;
        return WriteJsonAtomicallyAsync(
            destination,
            new ExactFaceTintMaterializationArtifact(
                "1",
                "exact-dds-facetint-materialization-v2",
                source.ExpectedSha256.Value,
                retainedByteLength,
                source.Width,
                source.Height,
                Relative(packageRoot, output).Value,
                outputHash.Value,
                retainedByteLength,
                true,
                false),
            cancellationToken);
    }

    private static (int Width, int Height) FaceTintDimensions(
        BlankNpcFaceTintSource source) =>
        source switch
        {
            GeneratedBlankNpcFaceTintSource => (1024, 1024),
            ExactDdsBlankNpcFaceTintSource exact => (exact.Width, exact.Height),
            _ => (0, 0)
        };

    private static async ValueTask<Sha256Hash> WriteJsonAtomicallyAsync<T>(
        WorkspacePath destination,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        var hash = new Sha256Hash(Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(bytes)));
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, destination.Value, overwrite: false);
            return hash;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void CopyAtomically(
        WorkspacePath source,
        WorkspacePath destination,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var temporary = destination.Value + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source.Value, temporary, overwrite: false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination.Value, overwrite: false);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private sealed record ExactFaceTintMaterializationArtifact(
        [property: JsonRequired] string SchemaVersion,
        [property: JsonRequired] string ArtifactKind,
        [property: JsonRequired] string SourceSha256,
        [property: JsonRequired] long SourceByteLength,
        [property: JsonRequired] int Width,
        [property: JsonRequired] int Height,
        [property: JsonRequired] string OutputDds,
        [property: JsonRequired] string OutputSha256,
        [property: JsonRequired] long OutputByteLength,
        [property: JsonRequired] bool ByteExact,
        [property: JsonRequired] bool RuntimeAuthority);
}
