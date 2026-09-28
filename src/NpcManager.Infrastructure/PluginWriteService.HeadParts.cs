using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;

namespace NpcManager.Infrastructure;

public sealed partial class PluginWriteService
{
    private static readonly JsonSerializerOptions HeadPartJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private async ValueTask<PluginWriteResult> WriteHeadPartAsync(PluginWriteRequest request, JsonElement document, CancellationToken token)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var createdAssets = new List<string>();
        string temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N") + ".esp";
        try
        {
            var proposal = document.Deserialize<RecordProposalArtifact>(HeadPartJsonOptions) ?? throw new InvalidDataException("Missing record proposal.");
            if (request.Edition != GameEdition.SkyrimSpecialEdition || proposal.Edition != "skyrimse" || proposal.SchemaVersion != "1" ||
                proposal.Signature != "HDPT" || proposal.Mode != RecordProposalMode.New || !proposal.NoUnrelatedRecords || proposal.HeadPart is null)
                throw new InvalidDataException("plugin write accepts Skyrim SE new output-owned HDPT record proposals.");
            _ = new EditorId(proposal.EditorId);
            if (proposal.EditorId.Length > 128 || proposal.EditorId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || proposal.HeadPart.ExtraParts.IsDefault || proposal.MasterDependencies.IsDefault)
                throw new InvalidDataException("Invalid HDPT identity or reference arrays.");
            if (Path.GetExtension(request.Output.Value).ToLowerInvariant() is not (".esp" or ".esm" or ".esl")) throw new InvalidDataException("HDPT output must be a plugin file.");
            byte[]? input = null;
            if (request.InputPlugin is { } inputPath)
            {
                Admit(inputPath, false);
                if (!string.Equals(Path.GetFileName(inputPath.Value), Path.GetFileName(request.Output.Value), StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("HDPT composition must preserve the input plugin filename.");
                input = await File.ReadAllBytesAsync(inputPath.Value, token);
                if (request.ExpectedInputHash is null || new Sha256Hash(Convert.ToHexString(SHA256.HashData(input))) != request.ExpectedInputHash)
                    throw new InvalidDataException("HDPT composition requires the exact --expected-sha256 of its input plugin.");
            }
            else if (request.ExpectedInputHash is not null) throw new InvalidDataException("--expected-sha256 requires --plugin.");
            byte[]? cloneBytes = null;
            if (proposal.HeadPart.CloneFrom is { } cloneText)
            {
                if (!FormReference.TryParse(cloneText, out var clone)) throw new InvalidDataException("Invalid clone reference.");
                if (!string.Equals(clone.Plugin.Value, Path.GetFileName(request.InputPlugin?.Value), StringComparison.OrdinalIgnoreCase))
                {
                    var clonePath = new WorkspacePath(Path.Combine(request.DataRoot?.Value ?? throw new InvalidDataException("External clone requires --data-root."), clone.Plugin.Value));
                    Admit(clonePath, false); cloneBytes = await File.ReadAllBytesAsync(clonePath.Value, token);
                }
                if (request.PrivateRoot is not null) throw new InvalidDataException("Private asset copying requires an explicit HDPT composition with all four asset paths.");
            }
            var copies = new List<(string Path, byte[] Bytes)>();
            if (request.PrivateRoot is { } privateRoot)
            {
                Admit(privateRoot, true);
                var dataRoot = request.DataRoot ?? throw new InvalidDataException("--private-root requires --data-root for exact source assets.");
                Admit(dataRoot, true);
                string Copy(string? text)
                {
                    var asset = new AssetPath(text ?? throw new InvalidDataException("Private HDPT requires four explicit asset paths."));
                    string relative = asset.Value.Replace('/', Path.DirectorySeparatorChar);
                    var source = new WorkspacePath(Path.Combine(dataRoot.Value, relative));
                    if (!File.Exists(source.Value) && !asset.Value.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)) source = new WorkspacePath(Path.Combine(dataRoot.Value, "meshes", relative));
                    Admit(source, false);
                    if (new FileInfo(source.Value).Length > 512 * 1024 * 1024) throw new InvalidDataException("HDPT asset exceeds 512 MiB.");
                    string privateRelative = "actors/character/" + proposal.EditorId + "/" + Path.GetFileName(source.Value);
                    string destination = Path.Combine(privateRoot.Value, "meshes", privateRelative.Replace('/', Path.DirectorySeparatorChar));
                    Admit(new WorkspacePath(destination), false, mustExist: false);
                    if (File.Exists(destination) || copies.Any(item => string.Equals(item.Path, destination, StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("Private asset destination exists or collides: " + destination);
                    copies.Add((destination, File.ReadAllBytes(source.Value)));
                    return privateRelative;
                }
                var part = proposal.HeadPart;
                proposal = proposal with { HeadPart = part with { Model = Copy(part.Model), TriRace = Copy(part.TriRace), TriChargen = Copy(part.TriChargen), TriDialogue = Copy(part.TriDialogue) } };
            }
            byte[] bytes = BethesdaHeadPartWriter.Compose(input, Path.GetFileName(request.Output.Value), proposal, cloneBytes);
            await File.WriteAllBytesAsync(temporary, bytes, token);
            BethesdaHeadPartWriter.Verify(temporary, proposal.EditorId, new FormId(Convert.ToUInt32(proposal.FormId.Replace("0x", "", StringComparison.OrdinalIgnoreCase), 16)).Value);
            foreach (var copy in copies)
            {
                token.ThrowIfCancellationRequested();
                Directory.CreateDirectory(Path.GetDirectoryName(copy.Path)!);
                using (var target = new FileStream(copy.Path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { createdAssets.Add(copy.Path); target.Write(copy.Bytes); }
            }
            File.Move(temporary, request.Output.Value, overwrite: false);
            return new PluginWriteResult(true, request.Proposal, request.Output, new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))),
                [new MutationChange("HDPT", null, proposal.FormId)], []);
        }
        catch (OperationCanceledException) { foreach (string path in createdAssets) File.Delete(path); throw; }
        catch (Exception exception) when (exception is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or OverflowException)
        {
            foreach (string path in createdAssets) File.Delete(path);
            diagnostics.Add(new Diagnostic("hdpt-write-refused", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, diagnostics.ToImmutable());
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }

        void Admit(WorkspacePath path, bool directory, bool mustExist = true)
        {
            if (!path.IsUnder(labRoot)) throw new InvalidDataException("HDPT paths must remain under the configured workspace root.");
            AddReparseDiagnostic(diagnostics, path.Value, "hdpt-path");
            if (diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error)) throw new InvalidDataException("HDPT path traverses a reparse point.");
            if (mustExist && !(directory ? Directory.Exists(path.Value) : File.Exists(path.Value))) throw new InvalidDataException("HDPT input is absent: " + path.Value);
        }
    }
}
