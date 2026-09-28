using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

// Transport-only response records live outside the command router so the
// router remains responsible for dispatch and orchestration, not schema shape.
internal sealed record VersionResponse(string Product, string Version, string SourceLine, string Protocol, string TargetFramework, string HostRuntime);
internal sealed record CapabilitiesResponse(string Product, string Version, string SourceLine, string Protocol,
    ImmutableArray<CommandDescriptor> Commands, ImmutableArray<CapabilityLedgerMapping> LedgerMappings);
internal sealed record CapabilityLedgerMapping(string Command, ImmutableArray<string> LedgerIds);
internal sealed record DiagnosisResponse(string Version, string Platform, bool Is64Bit, string Status);
internal sealed record PreflightResponse(bool IsAllowed, string WorkspaceRoot, string OutputRoot, ImmutableArray<Diagnostic> Diagnostics);
internal sealed record GameRootPreflightResponse(string SchemaVersion, string Edition, bool IsAllowed,
    string WorkspaceRoot, string DataRoot, string OutputRoot, ImmutableArray<Diagnostic> Diagnostics);
internal sealed record ReviewedGameIntakeResponse(
    string SchemaVersion,
    string Edition,
    bool IsAccepted,
    string WorkspaceRoot,
    string DataRoot,
    string LoadOrderPath,
    string OutputRoot,
    string? LoadOrderHash,
    string? AssetIndexFingerprint,
    string? IntakeFingerprint,
    ImmutableArray<ReviewedPluginResponse> Plugins,
    int BodySidecarCount,
    int GeneratedPluginCount,
    int GeneratedSidecarCount,
    ImmutableArray<ReviewedBodySidecarResponse>
        BodySidecars,
    ImmutableArray<ReviewedGeneratedPluginResponse>
        GeneratedPlugins,
    ImmutableArray<ReviewedGeneratedSidecarResponse>
        GeneratedSidecars,
    int AssetProviderCount,
    bool RuntimeAuthority,
    ImmutableArray<Diagnostic> Diagnostics)
{
    public static ReviewedGameIntakeResponse From(
        ReviewedGameIntakeRequest request,
        ReviewedGameIntakeResult result)
    {
        var intake = result.Intake;
        return new ReviewedGameIntakeResponse(
            "2", request.Edition.ToWireName(), result.IsAccepted,
            request.WorkspaceRoot.Value, request.DataRoot.Value,
            request.LoadOrderPath.Value, request.OutputRoot.Value,
            intake?.LoadOrderHash.Value, intake?.AssetIndexFingerprint.Value,
            intake?.IntakeFingerprint.Value,
            (intake?.Plugins ?? []).Select(ReviewedPluginResponse.From).ToImmutableArray(),
            intake?.BodySidecars.Length ?? 0,
            intake?.GeneratedPlugins.Length ?? 0,
            intake?.GeneratedSidecars.Length ?? 0,
            (intake?.BodySidecars ?? [])
                .Select(
                    ReviewedBodySidecarResponse.From)
                .ToImmutableArray(),
            (intake?.GeneratedPlugins ?? [])
                .Select(
                    ReviewedGeneratedPluginResponse.From)
                .ToImmutableArray(),
            (intake?.GeneratedSidecars ?? [])
                .Select(
                    ReviewedGeneratedSidecarResponse.From)
                .ToImmutableArray(),
            intake?.AssetProviderCount ?? 0,
            intake?.RuntimeAuthority ?? false,
            result.Diagnostics);
    }
}

internal sealed record ReviewedBodySidecarResponse(
    string Plugin,
    string Path,
    string SourceHash,
    string CanonicalHash,
    int NpcCount)
{
    public static ReviewedBodySidecarResponse From(
        ReviewedBodySidecar value) =>
        new(
            value.Plugin.Value,
            value.Path.Value,
            value.SourceHash.Value,
            value.CanonicalHash.Value,
            value.NpcCount);
}

internal sealed record ReviewedGeneratedPluginResponse(
    string Plugin,
    string Path,
    string Author,
    string? Sha256,
    bool ReadSucceeded,
    ImmutableArray<string> NpcFormIds)
{
    public static ReviewedGeneratedPluginResponse From(
        GeneratedPluginScanEntry value) =>
        new(
            value.Plugin.Value,
            value.Path.Value,
            value.Author,
            value.Sha256?.Value,
            value.ReadSucceeded,
            value.NpcFormIds.Select(formId =>
                    formId.ToString())
                .ToImmutableArray());
}

internal sealed record ReviewedGeneratedSidecarResponse(
    string OriginPlugin,
    string Kind,
    string Variant,
    string RelativePath,
    string Path,
    string? FormId,
    long Size,
    string Sha256)
{
    public static ReviewedGeneratedSidecarResponse From(
        GeneratedSidecarEntry value) =>
        new(
            value.OriginPlugin.Value,
            value.Kind.ToWireName(),
            value.Variant.ToWireName(),
            value.RelativePath.Value,
            value.Path.Value,
            value.FormId?.ToString(),
            value.Size,
            value.Sha256.Value);
}

internal sealed record ReviewedPluginResponse(
    string Plugin,
    int Order,
    bool Active,
    bool Requested,
    bool RequiredMaster,
    string SourceHash,
    ImmutableArray<string> Masters)
{
    public static ReviewedPluginResponse From(PluginClosureReviewEntry entry) => new(
        entry.Plugin.Value, entry.Order, entry.Enabled, entry.Requested,
        entry.RequiredMaster, entry.SourceHash?.Value ?? string.Empty,
        entry.Masters.Select(master => master.Value).ToImmutableArray());
}
internal sealed record ArchiveConsistencyResponse(string SchemaVersion, string Edition, string Plugin,
    string AssetIndex, string? DataRoot, bool IsConsistent,
    ImmutableArray<ArchiveConsistencyEntryResponse> Archives, ImmutableArray<Diagnostic> Diagnostics)
{
    public static ArchiveConsistencyResponse From(ArchiveConsistencyResult result) => new("1",
        result.Edition.ToWireName(), result.PluginPath.Value, result.AssetIndexPath.Value,
        result.DataRoot?.Value, result.IsConsistent,
        result.Archives.Select(ArchiveConsistencyEntryResponse.From).ToImmutableArray(), result.Diagnostics);
}

internal sealed record ArchiveConsistencyEntryResponse(string ArchiveName, bool PresentOnDisk, bool Indexed,
    string Status)
{
    public static ArchiveConsistencyEntryResponse From(ArchiveConsistencyEntry entry) => new(
        entry.ArchiveName, entry.PresentOnDisk, entry.Indexed,
        entry.Status switch
        {
            ArchiveConsistencyStatus.Matched => "matched",
            ArchiveConsistencyStatus.NotIndexed => "notIndexed",
            ArchiveConsistencyStatus.MissingOnDisk => "missingOnDisk",
            ArchiveConsistencyStatus.UnsupportedEdition => "unsupportedEdition",
            _ => throw new ArgumentOutOfRangeException(nameof(entry), entry.Status, "Unsupported archive status.")
        });
}

internal sealed record DesktopShellResponse(string Status, string Message);
internal sealed record DesktopLaunchResponse(string SchemaVersion, string Executable, bool Launched,
    ImmutableArray<Diagnostic> Diagnostics);
internal sealed record InventoryResponse(string Edition, ImmutableArray<string> Plugins, ImmutableArray<string> Masters,
    ImmutableArray<NpcResponse> Npcs, ImmutableArray<Diagnostic> Diagnostics)
{
    public static InventoryResponse From(GameInventory inventory) => new(
        inventory.Edition.ToWireName(),
        inventory.Plugins.Select(plugin => plugin.Value).ToImmutableArray(),
        inventory.Masters.Select(plugin => plugin.Value).ToImmutableArray(),
        inventory.Npcs.Select(NpcResponse.From).ToImmutableArray(),
        inventory.Diagnostics);
}

internal sealed record NpcResponse(string Plugin, string OwnerPlugin, string FormId, string? EditorId, string? Name, string Signature,
    bool IsDeleted, NpcSex? Sex, string? RaceFormId, float? SkyrimWeight, float? Fallout4ThinWeight,
    float? Fallout4MuscularWeight, float? Fallout4FatWeight, bool HasTemplate, string? TemplateFormId,
    bool IsTemplateSource, bool IsPlaced, bool IsInLeveledList, bool IsCharGenFacePreset,
    string? ProvenanceKind, ImmutableArray<string> OverrideChain, string ChangeState,
    ImmutableArray<string> Categories)
{
    public static NpcResponse From(NpcRecordSummary npc)
    {
        var metadata = npc.Metadata;
        return new NpcResponse(npc.Plugin.Value,
            (npc.OwnerPlugin ?? npc.Provenance?.SourcePlugin ?? npc.Plugin).Value,
            npc.FormId.ToString(), npc.EditorId, npc.Name,
            npc.Signature, npc.IsDeleted, metadata?.Sex, metadata?.RaceFormId?.ToString(), metadata?.SkyrimWeight,
            metadata?.Fallout4ThinWeight, metadata?.Fallout4MuscularWeight, metadata?.Fallout4FatWeight,
            metadata?.HasTemplate ?? false, metadata?.TemplateFormId?.ToString(), metadata?.IsTemplateSource ?? false,
            metadata?.IsPlaced ?? false, metadata?.IsInLeveledList ?? false, metadata?.IsCharGenFacePreset ?? false,
            npc.Provenance?.Kind.ToString(), npc.Provenance?.OverrideChain.Select(plugin => plugin.Value).ToImmutableArray() ?? [],
            npc.ChangeState.ToString(), npc.Categories.Select(category => category.ToString()).ToImmutableArray());
    }
}

internal sealed record NpcInspectResponse(string SchemaVersion, NpcResponse? Npc,
    ImmutableArray<Diagnostic> Diagnostics, ImmutableArray<string> UnsupportedFields,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ImmutableArray<string>? InheritedDefaults = null);

internal sealed record AssetResponse(string Edition, ImmutableArray<AssetProviderResponse> Providers, ImmutableArray<Diagnostic> Diagnostics)
{
    public static AssetResponse From(AssetIndex assets) => new(
        assets.Edition.ToWireName(),
        assets.Providers.Select(AssetProviderResponse.From).ToImmutableArray(),
        assets.Diagnostics);
}

internal sealed record AssetIndexExportResponse(string SchemaVersion, string Edition, string DataRoot,
    string Output, bool Written, ImmutableArray<AssetIndexEntryResponse> Entries, ImmutableArray<Diagnostic> Diagnostics)
{
    public static AssetIndexExportResponse From(AssetIndexExportResult result)
    {
        var artifact = result.Artifact;
        return new AssetIndexExportResponse("1", artifact?.Edition.ToWireName() ?? string.Empty,
            artifact?.DataRoot.Value ?? string.Empty, result.Output.Value, result.Written,
            artifact?.Entries.Select(AssetIndexEntryResponse.From).ToImmutableArray() ?? [], result.Diagnostics);
    }
}

internal sealed record AssetIndexEntryResponse(string Path, AssetProviderResponse Winner,
    ImmutableArray<AssetProviderResponse> Providers)
{
    public static AssetIndexEntryResponse From(AssetIndexEntry entry) => new(entry.Path.Value,
        AssetProviderResponse.From(entry.Winner), entry.Providers.Select(AssetProviderResponse.From).ToImmutableArray());
}

internal sealed record AssetProviderResponse(string Path, string Kind, string Source, long Size, string Sha256)
{
    public static AssetProviderResponse From(AssetProvider provider) => new(provider.Path.Value,
        provider.Kind.ToString().ToLowerInvariant(), provider.Source, provider.Size, provider.Sha256);
}

internal sealed record ErrorResponse(string Code, string Message);
