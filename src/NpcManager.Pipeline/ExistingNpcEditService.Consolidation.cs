using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Pipeline;

public sealed partial class ExistingNpcEditService
{
    private async ValueTask<ExistingNpcEditResult> ConsolidateAsync(
        ExistingNpcEditRequest request, CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        string scratch = Path.Combine(labRoot.Value, "artifacts", "consolidation", Guid.NewGuid().ToString("N"));
        var published = new List<string>();
        bool completed = false;
        bool scratchCreated = false;
        try
        {
            var output = request.ConsolidationOutput!.Value;
            var providers = request.ConsolidationProviders.IsDefault ? ImmutableArray<WorkspacePath>.Empty : request.ConsolidationProviders;
            if (request.Edition != GameEdition.SkyrimSpecialEdition ||
                !Path.GetExtension(output.Value).Equals(".esp", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Consolidation requires Skyrim SE and a fresh .esp output.");
            if (!request.EslFlag && providers.IsEmpty)
                throw new InvalidDataException("Consolidation requires at least one provider or the ESL flag operation.");
            if (request.EditorId is not null || request.Name is not null || request.Stats is not null ||
                request.Keywords is not null || request.Factions is not null || request.Inventory is not null ||
                request.Outfits is not null || request.Perks is not null || request.ActorEffects is not null ||
                request.Names is not null || request.Archetype is not null)
                throw new InvalidDataException("Consolidation cannot be mixed with scalar/package edits.");
            if (!Directory.Exists(Path.GetDirectoryName(output.Value)))
                throw new InvalidDataException("The output parent directory must already exist.");
            ValidatePath(output, "output");
            ValidatePath(new WorkspacePath(scratch), "scratch output");
            var inputs = providers.Prepend(request.InputPlugin).ToArray();
            if (inputs.Select(path => Path.GetFileName(path.Value)).Distinct(StringComparer.OrdinalIgnoreCase).Count() != inputs.Length)
                throw new InvalidDataException("Input and provider filenames must be distinct.");
            var bindings = new List<ConsolidationInput>();
            foreach (var path in inputs)
            {
                ValidatePath(path, "copied plugin");
                byte[] bytes = await File.ReadAllBytesAsync(path.Value, cancellationToken);
                bindings.Add(new(path, bytes, HashBytes(bytes)));
            }
            if (bindings[0].Sha256 != request.ExpectedInputSha256)
                throw new InvalidDataException("The input plugin SHA-256 does not match --input-sha256.");
            Directory.CreateDirectory(scratch);
            scratchCreated = true;
            var copies = new List<WorkspacePath>();
            foreach (var binding in bindings)
            {
                var copy = new WorkspacePath(Path.Combine(scratch, Path.GetFileName(binding.Path.Value)));
                await File.WriteAllBytesAsync(copy.Value, binding.Bytes, cancellationToken);
                copies.Add(copy);
            }
            var consolidated = BethesdaPluginConsolidationService.Consolidate(copies[0],
                copies.Skip(1).ToImmutableArray(), new PluginName(Path.GetFileName(output.Value)), request.EslFlag);
            var seqIds = new List<uint>();
            foreach (var path in inputs)
            {
                var seqPath = new WorkspacePath(Path.Combine(Path.GetDirectoryName(path.Value)!,
                    "Seq", Path.GetFileNameWithoutExtension(path.Value) + ".seq"));
                ValidatePath(seqPath, "copied SEQ");
                if (!File.Exists(seqPath.Value)) continue;
                byte[] bytes = await File.ReadAllBytesAsync(seqPath.Value, cancellationToken);
                bindings.Add(new(seqPath, bytes, HashBytes(bytes)));
                var copy = copies[Array.IndexOf(inputs, path)];
                byte[] rebased = BethesdaPluginConsolidationService.RebaseSeq(bytes,
                    Path.GetFileName(path.Value), BethesdaNpcStandaloneCopyAdapter.ReadMasters(copy), consolidated);
                for (int index = 0; index < rebased.Length; index += 4)
                {
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(rebased.AsSpan(index, 4));
                    if (!seqIds.Contains(id)) seqIds.Add(id);
                }
            }
            string stageDirectory = Path.Combine(scratch, "output");
            Directory.CreateDirectory(stageDirectory);
            var staged = new WorkspacePath(Path.Combine(stageDirectory, Path.GetFileName(output.Value)));
            await File.WriteAllBytesAsync(staged.Value, consolidated.PluginBytes, cancellationToken);
            BethesdaPluginConsolidationService.Verify(staged, consolidated, request.EslFlag);
            var pluginHash = HashBytes(consolidated.PluginBytes);
            byte[] seq = new byte[checked(seqIds.Count * 4)];
            for (int index = 0; index < seqIds.Count; index++)
                BinaryPrimitives.WriteUInt32LittleEndian(seq.AsSpan(index * 4, 4), seqIds[index]);
            var evidencePath = new WorkspacePath(output.Value + ".consolidation.json");
            var outputs = new List<(WorkspacePath Path, byte[] Bytes)>();
            if (seq.Length > 0)
                outputs.Add((new WorkspacePath(Path.Combine(Path.GetDirectoryName(output.Value)!, "Seq",
                    Path.GetFileNameWithoutExtension(output.Value) + ".seq")), seq));
            outputs.Add((evidencePath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                artifactKind = "plugin-consolidation-evidence",
                inputs = bindings.Select(row => new { path = row.Path.Value, sha256 = row.Sha256.Value }),
                output = output.Value,
                outputSha256 = pluginHash.Value,
                seqSha256 = seq.Length == 0 ? null : HashBytes(seq).Value,
                masters = consolidated.Masters,
                records = consolidated.Records,
                eslFlag = request.EslFlag,
                existingIdsCompacted = false,
                provenRecordRelocation = true,
                independentReopen = true,
                runtimeAuthority = false,
                visualAuthority = false
            }, JsonOptions)));
            // Publish the plugin last. Roll back only files created by this
            // invocation if any later publication or source recheck fails.
            outputs.Add((output, consolidated.PluginBytes));
            foreach (var item in outputs)
            {
                ValidatePath(item.Path, "publication output");
                if (File.Exists(item.Path.Value) || Directory.Exists(item.Path.Value))
                    throw new IOException("A consolidation output already exists: " + item.Path.Value);
            }
            RecheckInputs();
            foreach (var item in outputs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ValidatePath(item.Path, "publication output");
                Directory.CreateDirectory(Path.GetDirectoryName(item.Path.Value)!);
                await using var stream = new FileStream(item.Path.Value, FileMode.CreateNew, FileAccess.Write,
                    FileShare.None, 64 * 1024, FileOptions.WriteThrough);
                published.Add(item.Path.Value);
                await stream.WriteAsync(item.Bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            RecheckInputs();
            BethesdaPluginConsolidationService.Verify(output, consolidated, request.EslFlag);
            completed = true;
            return new ExistingNpcEditResult(true, "CONSOLIDATED_STATIC", output, pluginHash,
                null, null, [], null, diagnostics.ToImmutable()) { ConsolidationEvidencePath = evidencePath };

            void ValidatePath(WorkspacePath path, string role)
            {
                if (!path.IsUnder(labRoot)) throw new InvalidDataException($"The {role} must remain within the active workspace.");
                diagnostics.AddRange(policy.Evaluate(labRoot, path));
                AddReparseDiagnostic(diagnostics, path.Value, role);
                if (HasErrors(diagnostics)) throw new InvalidDataException($"The {role} failed workspace path admission.");
            }

            void RecheckInputs()
            {
                foreach (var binding in bindings)
                    if (HashFile(binding.Path.Value) != binding.Sha256)
                        throw new InvalidDataException("A copied plugin or SEQ changed during consolidation: " + binding.Path.Value);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("plugin-consolidation-refused", DiagnosticSeverity.Error, exception.Message));
            return Refused(diagnostics);
        }
        finally
        {
            if (!completed)
                foreach (string path in published)
                    try { File.Delete(path); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
            if (scratchCreated) TryDeleteOwnedDirectory(scratch);
        }
    }

    private static Sha256Hash HashBytes(byte[] bytes) => new(Convert.ToHexString(SHA256.HashData(bytes)));
    private sealed record ConsolidationInput(WorkspacePath Path, byte[] Bytes, Sha256Hash Sha256);
}
