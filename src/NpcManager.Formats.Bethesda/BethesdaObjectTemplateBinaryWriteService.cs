using System.Collections.Immutable;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Materializes the bounded Fallout 4 OBTS combination/include proposal into one ARMO record.
/// Mutagen does not model the OBTE/OBTF/OBTS/STOP block, so the typed ARMO is written first and
/// the exact upstream subrecord block is inserted into that new, uncompressed record. OMOD
/// property rows are accepted only through a separately hashed, explicitly combination-bound
/// proposal artifact.
/// </summary>
public sealed partial class BethesdaObjectTemplateBinaryWriteService(IWorkspacePolicy policy, WorkspacePath labRoot)
    : IObjectTemplateBinaryWriteService
{
    private const long MaximumProposalBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ObjectTemplateBinaryWriteResult> WriteAsync(ObjectTemplateBinaryWriteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(request, null, diagnostics.ToImmutable());

        ObjectTemplateProposalArtifact? proposal;
        try
        {
            var info = new FileInfo(request.Proposal.Value);
            if (info.Length <= 0 || info.Length > MaximumProposalBytes)
                throw new InvalidDataException("The object-template proposal is empty or exceeds the size limit.");
            await using var stream = new FileStream(request.Proposal.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                4096, FileOptions.SequentialScan);
            proposal = await JsonSerializer.DeserializeAsync<ObjectTemplateProposalArtifact>(stream, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic("object-template-binary-proposal-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, null, diagnostics.ToImmutable());
        }

        if (proposal is null)
        {
            diagnostics.Add(new Diagnostic("object-template-binary-proposal-empty", DiagnosticSeverity.Error, "The proposal must contain a JSON object."));
            return Refused(request, null, diagnostics.ToImmutable());
        }

        ObjectTemplatePropertyProposalArtifact? properties = null;
        if (request.Properties is { } propertyPath)
        {
            try
            {
                var info = new FileInfo(propertyPath.Value);
                if (info.Length <= 0 || info.Length > MaximumProposalBytes)
                    throw new InvalidDataException("The object-template property proposal is empty or exceeds the size limit.");
                await using var stream = new FileStream(propertyPath.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                    4096, FileOptions.SequentialScan);
                properties = await JsonSerializer.DeserializeAsync<ObjectTemplatePropertyProposalArtifact>(stream, JsonOptions, cancellationToken);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
            {
                diagnostics.Add(new Diagnostic("object-template-binary-properties-read-failed", DiagnosticSeverity.Error, exception.Message));
                return Refused(request, null, diagnostics.ToImmutable());
            }
            if (properties is null)
            {
                diagnostics.Add(new Diagnostic("object-template-binary-properties-empty", DiagnosticSeverity.Error, "The object-template property proposal must contain a JSON object."));
                return Refused(request, null, diagnostics.ToImmutable());
            }
        }

        var sourceFormId = ValidateProposal(proposal, request.Edition, diagnostics);
        ValidatePropertyArtifact(properties, proposal, request.Edition, diagnostics);
        var targetFormId = ResolveTarget(proposal, sourceFormId, diagnostics);
        var sourcePath = TryWorkspacePath(proposal.SourcePlugin, diagnostics, "source");
        var sourcePlugin = TryPluginName(proposal.SourcePlugin, diagnostics);
        if (sourcePath is null || sourcePlugin is null || targetFormId is null || HasErrors(diagnostics))
            return Refused(request, targetFormId, diagnostics.ToImmutable());

        try
        {
            var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath.Value.Value, cancellationToken))));
            if (!string.Equals(sourceHash.Value, proposal.InputSha256, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("object-template-binary-input-hash-mismatch", DiagnosticSeverity.Error, "The proposal source hash does not match the current source plugin."));
            if (properties is not null && !string.Equals(sourceHash.Value, properties.InputSha256, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("object-template-binary-properties-input-hash-mismatch", DiagnosticSeverity.Error, "The property proposal source hash does not match the current source plugin."));
            var outputModKey = ToModKey(request.Output.Value);
            if (outputModKey == sourcePlugin.Value)
                diagnostics.Add(new Diagnostic("object-template-binary-output-master-self", DiagnosticSeverity.Error, "The output plugin may not be the same plugin as the proposal source."));
            if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());

            var temporaryDirectory = Path.Combine(Path.GetDirectoryName(request.Output.Value)!, ".npcm-obts-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryDirectory);
            var temporary = Path.Combine(temporaryDirectory, Path.GetFileName(request.Output.Value));
            try
            {
                WriteFallout4(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal, properties, sourceFormId!.Value, targetFormId.Value, temporary);
                PatchArmoObjectTemplate(temporary, proposal, properties, outputModKey, targetFormId.Value, diagnostics);
                if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());
                File.Move(temporary, request.Output.Value, overwrite: false);
                var outputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.Output.Value, cancellationToken))));
                return new ObjectTemplateBinaryWriteResult(true, request.Proposal, request.Output, targetFormId, outputHash, diagnostics.ToImmutable());
            }
            finally
            {
                TryDelete(temporary);
                TryDeleteDirectory(temporaryDirectory);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException)
        {
            diagnostics.Add(new Diagnostic("object-template-binary-write-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, targetFormId, diagnostics.ToImmutable());
        }
    }
}
