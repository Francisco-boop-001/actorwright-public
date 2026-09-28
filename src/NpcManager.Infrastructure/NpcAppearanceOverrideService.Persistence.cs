using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

public sealed partial class NpcAppearanceOverrideService
{
    private static readonly JsonSerializerOptions ProposalJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private static async ValueTask<Sha256Hash> WriteProposalAsync(
        NpcAppearanceOverrideProposal proposal,
        CancellationToken cancellationToken)
    {
        var destination = proposal.ProposalPath.Value;
        var temporary = Path.Combine(
            Path.GetDirectoryName(destination)!,
            $".{Path.GetFileName(destination)}.tmp-{Guid.NewGuid():N}");
        try
        {
            var persistable = proposal with { ProposalSha256 = null };
            var bytes = Encoding.UTF8.GetBytes(
                JsonSerializer.Serialize(
                    CreateProposalDocument(persistable),
                    ProposalJsonOptions) + "\n");
            await using (var stream = new FileStream(
                             temporary,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             64 * 1024,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(bytes, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: false);
            return HashFile(destination);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static ProposalDocument CreateProposalDocument(
        NpcAppearanceOverrideProposal proposal) => new(
        proposal.SchemaVersion.ToString(System.Globalization.CultureInfo.InvariantCulture),
        "skyrim-npc-appearance-override-proposal",
        proposal.Edition.ToWireName(),
        proposal.SourcePlugin.Value,
        proposal.SourceSha256.Value,
        proposal.SourcePluginName.Value,
        proposal.TargetFormId.ToString(),
        proposal.SourceEditorId.Value,
        proposal.ProposalPath.Value,
        proposal.OutputPlugin.Value,
        proposal.OutputPluginName.Value,
        proposal.Race.ToString(),
        proposal.Sex,
        NpcAppearanceProposalJson.Create(proposal.Appearance),
        proposal.RuntimeAppearance,
        CreateOutfitPatchDocument(proposal.OutfitPatch),
        proposal.IsCharGenFacePreset,
        proposal.RequiredMasters.Select(item => item.Value).ToImmutableArray(),
        proposal.ExpectedMajorRecordSignatures
            .Select(item => item.Value)
            .ToImmutableArray(),
        proposal.ChangedNpcSubrecords);

    private static OutfitPatchDocument? CreateOutfitPatchDocument(
        NpcOutfitPatch? patch) => patch is null
        ? null
        : new OutfitPatchDocument(
            patch.DefaultOutfit.IsSpecified,
            patch.DefaultOutfit.Value?.ToString(),
            patch.SleepingOutfit.IsSpecified,
            patch.SleepingOutfit.Value?.ToString());

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void FlushFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite,
            FileShare.None, 4096, FileOptions.WriteThrough);
        if (stream.Length <= 0)
            throw new IOException("The appearance override writer produced an empty plugin.");
        stream.Flush(flushToDisk: true);
    }

    private static void AddReparseDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string path,
        string role)
    {
        try
        {
            FileSystemInfo? current = new FileInfo(path);
            while (current is not null)
            {
                if (current.Exists && current.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    diagnostics.Add(Error("npc-appearance-override-reparse",
                        $"The {role} traverses reparse point '{current.FullName}'."));
                    return;
                }
                current = current switch
                {
                    FileInfo file => file.Directory,
                    DirectoryInfo directory => directory.Parent,
                    _ => null
                };
            }
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error("npc-appearance-override-path-inspection",
                $"The {role} path could not be inspected safely: {exception.Message}"));
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Best-effort cleanup; the primary diagnostic remains authoritative.
        }
    }

    private sealed record ProposalDocument(
        string SchemaVersion,
        string ArtifactKind,
        string Edition,
        string SourcePlugin,
        string SourceSha256,
        string SourcePluginName,
        string TargetFormId,
        string SourceEditorId,
        string ProposalPath,
        string OutputPlugin,
        string OutputPluginName,
        string Race,
        NpcSex Sex,
        NpcAppearanceProposalJson.FullyAuthoredDocument Appearance,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        SkyrimNpcApplySseVmadPayload? RuntimeAppearance,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        OutfitPatchDocument? OutfitPatch,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        bool? IsCharGenFacePreset,
        ImmutableArray<string> RequiredMasters,
        ImmutableArray<string> ExpectedMajorRecordSignatures,
        ImmutableArray<string> ChangedNpcSubrecords);

    private sealed record OutfitPatchDocument(
        bool DefaultOutfitSpecified,
        string? DefaultOutfit,
        bool SleepingOutfitSpecified,
        string? SleepingOutfit);
}
