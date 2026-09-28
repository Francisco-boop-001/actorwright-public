using System.Buffers.Binary;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reconstructs the pinned loader's Skyrim race gate from one copied, hash-bound
/// plugin order. Only HDPT valid-race lists and the target RACE's gender defaults
/// participate; tint rows are deliberately preserved without gating the preset.
/// </summary>
public sealed class BethesdaRaceMenuPresetCompatibilityEvaluator(
    ISkyrimFaceRecordPluginAuthorityLoader authorityLoader)
    : IRaceMenuPresetCompatibilityEvaluator, IDisposable
{
    private const int MaximumHeadParts = 128;
    private const uint LightPluginFlag = 0x0000_0200;
    private readonly SemaphoreSlim cacheGate = new(1, 1);
    private string? cachedKey;
    private CompatibilitySnapshot? cachedSnapshot;

    public async ValueTask<RaceMenuPresetCompatibilityResult> EvaluateAsync(
        PresetDocument preset,
        RaceMenuPresetTarget target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preset);
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        if (preset.Format != PresetFormat.RaceMenuJslot ||
            preset.Edition != GameEdition.SkyrimSpecialEdition)
        {
            return Unavailable(Error("preset-compatibility-format",
                "Race compatibility requires a Skyrim SE RaceMenu .jslot document."));
        }

        CompatibilitySnapshotResult snapshotResult =
            await GetSnapshotAsync(target, cancellationToken).ConfigureAwait(false);
        if (snapshotResult.Snapshot is null)
        {
            return new RaceMenuPresetCompatibilityResult(
                RaceMenuPresetCompatibilityKind.Unavailable,
                snapshotResult.Diagnostics);
        }

        return Evaluate(preset, target, snapshotResult.Snapshot);
    }

    private async ValueTask<CompatibilitySnapshotResult> GetSnapshotAsync(
        RaceMenuPresetTarget target,
        CancellationToken cancellationToken)
    {
        string key = BuildCacheKey(target);
        await cacheGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (string.Equals(cachedKey, key, StringComparison.Ordinal) &&
                cachedSnapshot is not null)
            {
                return new CompatibilitySnapshotResult(cachedSnapshot, []);
            }

            var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
            if (string.IsNullOrWhiteSpace(target.AuthorityId))
            {
                diagnostics.Add(Error("preset-compatibility-authority-id",
                    "The reviewed compatibility authority must have a stable identifier."));
            }
            if (!Enum.IsDefined(target.Sex))
            {
                diagnostics.Add(Error("preset-compatibility-sex",
                    "The target NPC sex is unsupported."));
            }
            if (target.PluginOrder.IsDefaultOrEmpty)
            {
                diagnostics.Add(Error("preset-compatibility-plugin-order",
                    "Compatibility requires the accepted copied plugin order and hashes."));
            }
            if (HasErrors(diagnostics))
            {
                return new CompatibilitySnapshotResult(null, diagnostics.ToImmutable());
            }

            SkyrimFaceRecordPluginAuthorityResult current =
                await authorityLoader.LoadAsync(
                    new SkyrimFaceRecordPluginAuthorityRequest(
                        GameEdition.SkyrimSpecialEdition,
                        target.DataRoot,
                        target.PluginOrder.Select(item => item.Plugin).ToImmutableArray()),
                    cancellationToken).ConfigureAwait(false);
            diagnostics.AddRange(current.Diagnostics);
            if (!current.Accepted ||
                !MatchesReviewedAuthorities(current.Authorities, target.PluginOrder))
            {
                if (current.Accepted)
                {
                    diagnostics.Add(Error("preset-compatibility-authority-stale",
                        "The copied plugin order no longer matches the reviewed paths and SHA-256 values."));
                }
                return new CompatibilitySnapshotResult(null, diagnostics.ToImmutable());
            }

            SkyrimFaceRecordCatalog catalog;
            try
            {
                catalog = BethesdaSkyrimFaceRecordCatalogLoader.Load(
                    current.Authorities, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(Error("preset-compatibility-provider-malformed",
                    $"The copied provider closure could not be decoded: {exception.Message}"));
                return new CompatibilitySnapshotResult(null, diagnostics.ToImmutable());
            }

            ImmutableHashSet<string> lightPlugins = await ReadLightPluginNamesAsync(
                current.Authorities, diagnostics, cancellationToken).ConfigureAwait(false);
            if (HasErrors(diagnostics))
                return new CompatibilitySnapshotResult(null, diagnostics.ToImmutable());

            if (!catalog.Races.TryGetValue(
                    SkyrimFaceRecordKey.From(target.Race), out SkyrimFaceDecodedRace? race) ||
                race.IsDeleted)
            {
                diagnostics.Add(Error("preset-compatibility-race-missing",
                    $"Target RACE {target.Race} is unavailable in the reviewed plugin order."));
                return new CompatibilitySnapshotResult(null, diagnostics.ToImmutable());
            }

            var snapshot = new CompatibilitySnapshot(
                catalog,
                race,
                target.PluginOrder.Any(item =>
                    item.Plugin.Value.Contains("RaceCompatibility",
                        StringComparison.OrdinalIgnoreCase) ||
                    item.Plugin.Value.Contains("Race Compatibility",
                        StringComparison.OrdinalIgnoreCase)),
                lightPlugins);
            cachedKey = key;
            cachedSnapshot = snapshot;
            return new CompatibilitySnapshotResult(snapshot, diagnostics.ToImmutable());
        }
        finally
        {
            cacheGate.Release();
        }
    }

    internal static RaceMenuPresetCompatibilityResult Evaluate(
        PresetDocument preset,
        RaceMenuPresetTarget target,
        CompatibilitySnapshot snapshot)
    {
        if (preset.Appearance.HeadParts.Length > MaximumHeadParts)
        {
            return Incompatible(Warning("preset-compatibility-headpart-limit",
                $"The preset exceeds the {MaximumHeadParts}-head-part compatibility bound."));
        }

        ImmutableArray<FormReference> selectedDefaults = target.Sex == NpcSex.Female
            ? snapshot.Race.FemaleDefaultHeadParts
            : snapshot.Race.MaleDefaultHeadParts;
        bool raceHasHeadParts = snapshot.Race.MaleDefaultHeadParts.Length > 0 ||
                                snapshot.Race.FemaleDefaultHeadParts.Length > 0;

        foreach (PresetHeadPart source in preset.Appearance.HeadParts)
        {
            if (source.Identifier.Plugin is not { } plugin ||
                source.Identifier.FormId is not { } formId || formId.Value == 0)
            {
                return Incompatible(Warning("preset-compatibility-headpart-unresolved",
                    $"Head part '{source.Identifier.Raw}' has no exact plugin-local reference."));
            }

            var sourceReference = new FormReference(plugin, formId);
            FormReference reference = NormalizeSourceReference(
                sourceReference, snapshot.LightPlugins);
            if (!snapshot.Catalog.HeadParts.TryGetValue(
                    SkyrimFaceRecordKey.From(reference), out SkyrimFaceDecodedHeadPart? headPart) ||
                headPart.IsDeleted)
            {
                return Incompatible(Warning("preset-compatibility-headpart-missing",
                    $"Head part {reference} is unavailable in the reviewed plugin order."));
            }

            if (selectedDefaults.Any(item => SameReference(item, reference))) continue;
            if (headPart.ValidRaces is not { } validRacesReference)
            {
                if (raceHasHeadParts) continue;
                return Incompatible(Warning("preset-compatibility-race-nonhumanoid",
                    $"Head part {reference} has no race list and target RACE {target.Race} declares no head parts."));
            }

            if (snapshot.Catalog.FormLists.TryGetValue(
                    SkyrimFaceRecordKey.From(validRacesReference),
                    out SkyrimFaceDecodedFormList? validRaces) &&
                validRaces is not null &&
                !validRaces.IsDeleted &&
                validRaces.Items.Any(item => SameReference(item, target.Race)))
            {
                continue;
            }

            if (snapshot.HasUnmodeledRuntimeRaceCompatibility)
            {
                return Unavailable(Warning("preset-compatibility-runtime-proxy-unmodeled",
                    $"Head part {reference} is not statically valid for {target.Race}, but the reviewed order contains a RaceCompatibility provider whose runtime FLST insertions are not yet modeled."));
            }

            return Incompatible(Warning("preset-compatibility-race-mismatch",
                $"Head part {reference} is not valid for target RACE {target.Race}."));
        }

        return new RaceMenuPresetCompatibilityResult(
            RaceMenuPresetCompatibilityKind.Compatible, []);
    }

    private static async ValueTask<ImmutableHashSet<string>> ReadLightPluginNamesAsync(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> authorities,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        var lightPlugins = ImmutableHashSet.CreateBuilder<string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (SkyrimFaceRecordPluginAuthority authority in authorities)
        {
            try
            {
                await using var stream = new FileStream(
                    authority.Path.Value,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                var header = new byte[12];
                await stream.ReadExactlyAsync(header, cancellationToken).ConfigureAwait(false);
                if (!header.AsSpan(0, 4).SequenceEqual("TES4"u8))
                    throw new InvalidDataException("The provider does not begin with a TES4 record.");
                if ((BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8, 4)) &
                     LightPluginFlag) != 0)
                {
                    lightPlugins.Add(authority.Plugin.Value);
                }
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               NotSupportedException)
            {
                diagnostics.Add(Error("preset-compatibility-provider-header",
                    $"The copied provider header for '{authority.Plugin}' could not be read: {exception.Message}"));
            }
        }
        return lightPlugins.ToImmutable();
    }

    private static FormReference NormalizeSourceReference(
        FormReference source,
        ImmutableHashSet<string>? lightPlugins)
    {
        if (lightPlugins?.Contains(source.Plugin.Value) != true) return source;
        uint localId = source.FormId.Value & 0x0000_0FFF;
        return localId == 0
            ? source
            : new FormReference(source.Plugin, new FormId(localId));
    }

    private static bool MatchesReviewedAuthorities(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> current,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> reviewed)
    {
        if (current.Length != reviewed.Length) return false;
        for (var index = 0; index < current.Length; index++)
        {
            if (!string.Equals(current[index].Plugin.Value,
                    reviewed[index].Plugin.Value, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(current[index].Path.Value,
                    reviewed[index].Path.Value, StringComparison.OrdinalIgnoreCase) ||
                current[index].ExpectedSha256 != reviewed[index].ExpectedSha256)
            {
                return false;
            }
        }
        return true;
    }

    private static string BuildCacheKey(RaceMenuPresetTarget target) =>
        string.Join('\u001F',
            target.AuthorityId,
            target.Race.ToString(),
            target.Sex.ToString(),
            target.DataRoot.Value,
            string.Join('\u001E', target.PluginOrder.Select(item =>
                $"{item.Plugin.Value}|{item.Path.Value}|{item.ExpectedSha256.Value}")));

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId &&
        string.Equals(left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static RaceMenuPresetCompatibilityResult Unavailable(
        params Diagnostic[] diagnostics) =>
        new(RaceMenuPresetCompatibilityKind.Unavailable, [.. diagnostics]);

    private static RaceMenuPresetCompatibilityResult Incompatible(
        params Diagnostic[] diagnostics) =>
        new(RaceMenuPresetCompatibilityKind.Incompatible, [.. diagnostics]);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Diagnostic Warning(string code, string message) =>
        new(code, DiagnosticSeverity.Warning, message);

    public void Dispose() => cacheGate.Dispose();

    internal sealed record CompatibilitySnapshot(
        SkyrimFaceRecordCatalog Catalog,
        SkyrimFaceDecodedRace Race,
        bool HasUnmodeledRuntimeRaceCompatibility,
        ImmutableHashSet<string>? LightPlugins = null);

    private sealed record CompatibilitySnapshotResult(
        CompatibilitySnapshot? Snapshot,
        ImmutableArray<Diagnostic> Diagnostics);
}
