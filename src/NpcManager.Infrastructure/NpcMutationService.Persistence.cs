using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class NpcMutationService
{
    private static NpcMutationProposal FailedProposal(NpcMutationRequest request, ImmutableArray<Diagnostic> diagnostics) =>
        new(request.Edition, request.InputPlugin, request.OutputPlugin, request.TargetFormId,
            new Sha256Hash(new string('0', 64)), ImmutableArray<MutationChange>.Empty, PreservedFields, diagnostics);

    private static Sha256Hash ComputeHash(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant());
    }

    private static void WriteProposal(WorkspacePath path, NpcMutationProposal proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            var document = new ProposalDocument(proposal.WholeSkin is null ? 1 : 2, proposal.Edition.ToWireName(), proposal.InputPlugin.Value,
                proposal.OutputPlugin.Value, proposal.TargetFormId.ToString(), proposal.InputHash.Value,
                proposal.Changes, proposal.PreservedFields)
            {
                Aidt = proposal.Aidt,
                WholeSkin = proposal.WholeSkin is { } skin ? JsonSerializer.SerializeToElement(new
                {
                    request = JsonSerializer.Deserialize<JsonElement>(skin.Patch.DocumentJson),
                    requestSha256 = skin.Patch.DocumentSha256.Value,
                    firstAllocatedLocalFormId = skin.FirstAllocatedLocalFormId.ToString(),
                    allocatedRecordCount = skin.AllocatedRecordCount,
                    changedNpcSubrecords = skin.ChangedNpcSubrecords,
                    faceGenRebuilt = false
                }) : null
            };
            var json = JsonSerializer.Serialize(document, ProposalJsonOptions);
            var temporary = path.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            try
            {
                var bytes = new UTF8Encoding(false).GetBytes(json);
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    stream.Write(bytes);
                    stream.Flush(flushToDisk: true);
                }
                File.Move(temporary, path.Value, overwrite: false);
            }
            finally
            {
                TryDelete(temporary);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("proposal-write-failed", DiagnosticSeverity.Error, exception.Message));
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static void FlushOutput(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read, 4096, FileOptions.WriteThrough);
        stream.Flush(flushToDisk: true);
    }

    private sealed record ProposalDocument(int SchemaVersion, string Edition, string InputPlugin, string OutputPlugin,
        string TargetFormId, string InputSha256, ImmutableArray<MutationChange> Changes, ImmutableArray<string> PreservedFields)
    {
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public JsonElement? WholeSkin { get; init; }
        [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
        public NpcAidtPatch? Aidt { get; init; }
    }
}
