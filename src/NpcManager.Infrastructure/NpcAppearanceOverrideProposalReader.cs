using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Reads the closed persisted appearance-override proposal emitted by
/// <see cref="NpcAppearanceOverrideService"/> for independent verification.
/// </summary>
internal static class NpcAppearanceOverrideProposalReader
{
    private const long MaximumProposalBytes = 1_048_576;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    internal static async ValueTask<NpcAppearanceOverrideProposal> ReadAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path.Value);
        if (info.Length <= 0 || info.Length > MaximumProposalBytes)
            throw new InvalidDataException(
                "Appearance proposal size is outside the accepted bounds.");

        PersistedProposalDocument? document;
        await using (var stream = new FileStream(
                         path.Value,
                         FileMode.Open,
                         FileAccess.Read,
                         FileShare.Read,
                         4096,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            document = await JsonSerializer.DeserializeAsync<PersistedProposalDocument>(
                stream,
                JsonOptions,
                cancellationToken);
        }

        if (document is null ||
            !string.Equals(document.ArtifactKind,
                "skyrim-npc-appearance-override-proposal",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "The proposal is not a Skyrim NPC appearance override.");
        }
        if (!int.TryParse(document.SchemaVersion, NumberStyles.None,
                CultureInfo.InvariantCulture, out var schemaVersion))
            throw new InvalidDataException("Appearance proposal schema is invalid.");
        if (!GameEditionExtensions.TryParseWireName(
                Required(document.Edition, "edition"), out var edition))
            throw new InvalidDataException("Appearance proposal edition is invalid.");
        if (!FormId.TryParse(Required(document.TargetFormId, "targetFormId"),
                out var targetFormId))
            throw new InvalidDataException("Appearance target FormID is invalid.");
        if (!FormReference.TryParse(Required(document.Race, "race"), out var race))
            throw new InvalidDataException("Appearance race reference is invalid.");
        if (!Enum.TryParse<NpcSex>(Required(document.Sex, "sex"), true,
                out var sex) || !Enum.IsDefined(sex))
            throw new InvalidDataException("Appearance NPC sex is invalid.");
        if (document.Appearance is null)
            throw new InvalidDataException("Appearance payload is missing.");
        if (document.RequiredMasters.IsDefault ||
            document.ExpectedMajorRecordSignatures.IsDefault ||
            document.ChangedNpcSubrecords.IsDefault)
            throw new InvalidDataException("Appearance proposal arrays are missing.");

        var proposalHash = HashFile(path.Value);
        return new NpcAppearanceOverrideProposal(
            schemaVersion,
            edition,
            new WorkspacePath(Required(document.SourcePlugin, "sourcePlugin")),
            new Sha256Hash(Required(document.SourceSha256, "sourceSha256")),
            new PluginName(Required(document.SourcePluginName, "sourcePluginName")),
            targetFormId,
            new EditorId(Required(document.SourceEditorId, "sourceEditorId")),
            new WorkspacePath(Required(document.ProposalPath, "proposalPath")),
            proposalHash,
            new WorkspacePath(Required(document.OutputPlugin, "outputPlugin")),
            new PluginName(Required(document.OutputPluginName, "outputPluginName")),
            race,
            sex,
            NpcAppearanceProposalJson.Parse(document.Appearance),
            document.RuntimeAppearance,
            document.RequiredMasters.Select(item =>
                    new PluginName(Required(item, "requiredMasters")))
                .ToImmutableArray(),
            document.ExpectedMajorRecordSignatures.Select(item =>
                    new RecordSignature(Required(item,
                        "expectedMajorRecordSignatures")))
                .ToImmutableArray(),
            document.ChangedNpcSubrecords.Select(item =>
                    Required(item, "changedNpcSubrecords"))
                .ToImmutableArray(),
            [],
            ParseOutfitPatch(document.OutfitPatch),
            document.IsCharGenFacePreset);
    }

    private static NpcOutfitPatch? ParseOutfitPatch(
        PersistedOutfitPatchDocument? document)
    {
        if (document is null) return null;
        return new NpcOutfitPatch(
            ParseOptionalReference(
                document.DefaultOutfitSpecified,
                document.DefaultOutfit,
                "outfitPatch.defaultOutfit"),
            ParseOptionalReference(
                document.SleepingOutfitSpecified,
                document.SleepingOutfit,
                "outfitPatch.sleepingOutfit"));
    }

    private static OptionalFormReference ParseOptionalReference(
        bool specified,
        string? value,
        string property)
    {
        if (!specified)
        {
            if (value is not null)
                throw new InvalidDataException(
                    $"Appearance proposal property '{property}' has a value but is not specified.");
            return default;
        }
        if (value is null) return OptionalFormReference.Clear();
        if (!FormReference.TryParse(value, out var reference))
            throw new InvalidDataException(
                $"Appearance proposal property '{property}' is invalid.");
        return OptionalFormReference.Set(reference);
    }

    private static string Required(string? value, string property) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidDataException(
                $"Appearance proposal property '{property}' is missing.")
            : value;

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.Read, 64 * 1024, FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private sealed record PersistedProposalDocument(
        string? SchemaVersion,
        string? ArtifactKind,
        string? Edition,
        string? SourcePlugin,
        string? SourceSha256,
        string? SourcePluginName,
        string? TargetFormId,
        string? SourceEditorId,
        string? ProposalPath,
        string? OutputPlugin,
        string? OutputPluginName,
        string? Race,
        string? Sex,
        NpcAppearanceProposalJson.FullyAuthoredDocument? Appearance,
        SkyrimNpcApplySseVmadPayload? RuntimeAppearance,
        PersistedOutfitPatchDocument? OutfitPatch,
        bool? IsCharGenFacePreset,
        ImmutableArray<string?> RequiredMasters,
        ImmutableArray<string?> ExpectedMajorRecordSignatures,
        ImmutableArray<string?> ChangedNpcSubrecords);

    private sealed record PersistedOutfitPatchDocument(
        bool DefaultOutfitSpecified,
        string? DefaultOutfit,
        bool SleepingOutfitSpecified,
        string? SleepingOutfit);
}
