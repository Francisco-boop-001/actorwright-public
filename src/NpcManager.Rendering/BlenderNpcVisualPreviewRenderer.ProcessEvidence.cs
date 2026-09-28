using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Domain;

namespace NpcManager.Rendering;

internal sealed record NpcVisualPreviewBlenderProcessEvidence(
    int SchemaVersion,
    bool RuntimeAuthority,
    bool VisualAuthority,
    string BlenderPath,
    string BlenderSha256,
    string ProfileRoot,
    string ProfileManifestPath,
    string ProfileManifestSha256,
    string RendererScriptId,
    string RendererScriptSha256,
    string ExpectedStatusPath,
    int ExitCode,
    string StandardOutput,
    bool StandardOutputTruncated,
    string StandardError,
    bool StandardErrorTruncated);

public sealed partial class BlenderNpcVisualPreviewRenderer
{
    private const int MaximumProcessEvidenceBytes = 64 * 1024;
    private static readonly JsonSerializerOptions ProcessEvidenceJsonOptions =
        new()
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            UnmappedMemberHandling =
                JsonUnmappedMemberHandling.Disallow
        };

    private async ValueTask WriteProcessEvidenceAsync(
        WorkspacePath evidencePath,
        WorkspacePath statusPath,
        NpcVisualPreviewProcessResult process,
        CancellationToken cancellationToken)
    {
        NpcVisualPreviewBlenderProcessEvidence evidence = new(
            1,
            RuntimeAuthority: false,
            VisualAuthority: false,
            blenderPath.Value,
            expectedBlenderSha256.Value,
            profileRoot.Value,
            profileManifestPath.Value,
            expectedProfileManifestSha256.Value,
            scriptId.Value,
            expectedScriptSha256.Value,
            statusPath.Value,
            process.ExitCode,
            process.StandardOutput.Text,
            process.StandardOutput.Truncated,
            process.StandardError.Text,
            process.StandardError.Truncated);
        (evidence, byte[] serialized) =
            SerializeBoundedEvidence(evidence);
        string temporary = evidencePath.Value +
            ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous |
                FileOptions.SequentialScan))
            {
                await stream.WriteAsync(
                    serialized, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            byte[] reopenedTemporary =
                await File.ReadAllBytesAsync(
                    temporary, cancellationToken);
            if (!reopenedTemporary.AsSpan().SequenceEqual(serialized))
                throw new InvalidDataException(
                    "The temporary Blender process evidence changed after flush.");

            File.Move(temporary, evidencePath.Value);
            byte[] finalBytes = await File.ReadAllBytesAsync(
                evidencePath.Value, cancellationToken);
            NpcVisualPreviewBlenderProcessEvidence? reopened =
                JsonSerializer.Deserialize<
                    NpcVisualPreviewBlenderProcessEvidence>(
                        finalBytes,
                        ProcessEvidenceJsonOptions);
            if (!finalBytes.AsSpan().SequenceEqual(serialized) ||
                reopened != evidence)
                throw new InvalidDataException(
                    "The final Blender process evidence did not reopen identically.");
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static (
        NpcVisualPreviewBlenderProcessEvidence Evidence,
        byte[] Serialized) SerializeBoundedEvidence(
            NpcVisualPreviewBlenderProcessEvidence evidence)
    {
        byte[] serialized = JsonSerializer.SerializeToUtf8Bytes(
            evidence,
            ProcessEvidenceJsonOptions);
        while (serialized.Length > MaximumProcessEvidenceBytes &&
               (evidence.StandardOutput.Length > 0 ||
                evidence.StandardError.Length > 0))
        {
            if (evidence.StandardOutput.Length >=
                    evidence.StandardError.Length &&
                evidence.StandardOutput.Length > 0)
            {
                evidence = evidence with
                {
                    StandardOutput = evidence.StandardOutput[..
                        (evidence.StandardOutput.Length / 2)],
                    StandardOutputTruncated = true
                };
            }
            else
            {
                evidence = evidence with
                {
                    StandardError = evidence.StandardError[..
                        (evidence.StandardError.Length / 2)],
                    StandardErrorTruncated = true
                };
            }
            serialized = JsonSerializer.SerializeToUtf8Bytes(
                evidence,
                ProcessEvidenceJsonOptions);
        }
        if (serialized.Length > MaximumProcessEvidenceBytes)
            throw new InvalidDataException(
                "Blender process evidence metadata exceeds its byte limit.");
        return (evidence, serialized);
    }
}
