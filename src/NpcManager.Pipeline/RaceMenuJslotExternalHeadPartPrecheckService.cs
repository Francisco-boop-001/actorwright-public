using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Pipeline;

/// <summary>
/// Early, read-only closure check for external SMP head-part selections. The
/// preset is only an input selector: race, sex, Data root, and plugin hashes
/// remain the reviewed target authority, and discovery consumes the existing
/// bounded face-record route rather than walking HNAM a second time.
/// </summary>
public sealed class RaceMenuJslotExternalHeadPartPrecheckService(
    IPresetService presetService,
    ISkyrimFaceRecordPluginAuthorityLoader pluginAuthorityLoader,
    ISkyrimFaceRecordRouteResolver routeResolver,
    IExternalHeadPartDependencyDiscovery discovery) :
    IRaceMenuJslotExternalHeadPartPrecheckService
{
    public async ValueTask<RaceMenuJslotExternalHeadPartPrecheckResult> PrecheckAsync(
        RaceMenuJslotExternalHeadPartPrecheckRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        Sha256Hash actualPresetHash;
        try
        {
            actualPresetHash = await HashFileAsync(
                request.PresetPath, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or
                                           UnauthorizedAccessException or
                                           ArgumentException or
                                           NotSupportedException)
        {
            diagnostics.Add(Error(
                "external-headpart-precheck-preset-hash",
                $"The selected preset could not be reopened for SHA-256: {exception.Message}"));
            return Refused(diagnostics);
        }
        if (actualPresetHash != request.ExpectedPresetSha256)
        {
            diagnostics.Add(Error(
                "external-headpart-precheck-preset-hash",
                "The selected preset changed before external-provider precheck."));
            return Refused(diagnostics);
        }

        PresetParseResult parsed = await presetService.InspectAsync(
            new PresetParseRequest(
                PresetFormat.RaceMenuJslot,
                GameEdition.SkyrimSpecialEdition,
                request.PresetPath),
            cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, parsed.Diagnostics);
        if (parsed.Document is not
            {
                Format: PresetFormat.RaceMenuJslot,
                Edition: GameEdition.SkyrimSpecialEdition,
                IsValid: true,
                SourceHash: var parsedHash
            } preset || parsedHash != request.ExpectedPresetSha256 ||
            HasErrors(diagnostics))
        {
            diagnostics.Add(Error(
                "external-headpart-precheck-preset",
                "The selected preset did not reopen as valid Skyrim SE JSlot data with the reviewed hash."));
            return Refused(diagnostics);
        }

        SkyrimFaceRecordPluginAuthorityResult loaded =
            await pluginAuthorityLoader.LoadAsync(
                new SkyrimFaceRecordPluginAuthorityRequest(
                    GameEdition.SkyrimSpecialEdition,
                    request.Target.DataRoot,
                    request.Target.PluginOrder.Select(item => item.Plugin)
                        .ToImmutableArray()),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, loaded.Diagnostics);
        if (!loaded.Accepted ||
            !MatchesReviewedAuthorities(loaded.Authorities,
                request.Target.PluginOrder))
        {
            diagnostics.Add(Error(
                ExternalHeadPartDiagnosticCodes.RecordDrift,
                "The reviewed Data root or ordered plugin path/hash authorities changed before external-provider precheck."));
            return Refused(diagnostics);
        }

        ImmutableArray<SkyrimFaceRecordHeadPartSelection> selections =
            ParseHeadPartSelections(preset, diagnostics);
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        SkyrimFaceRecordRouteResult routed = await routeResolver.ResolveAsync(
            new SkyrimFaceRecordRouteRequest(
                GameEdition.SkyrimSpecialEdition,
                request.Target.Race,
                request.Target.Sex,
                selections,
                loaded.Authorities),
            cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, routed.Diagnostics);
        if (!routed.Accepted || routed.Route is null || HasErrors(diagnostics))
        {
            diagnostics.Add(Error(
                "external-headpart-precheck-route",
                "The selected preset did not resolve through the reviewed Skyrim face-record route."));
            return Refused(diagnostics);
        }

        ExternalHeadPartDependencyDiscoveryResult discovered =
            await discovery.DiscoverAsync(
                new ExternalHeadPartDependencyDiscoveryRequest(
                    request.Target.DataRoot,
                    request.Target.PluginOrder.Select(item => item.Plugin)
                        .ToImmutableArray(),
                    routed.Route,
                    request.Target.Sex,
                    request.ExpectedDescriptor),
                cancellationToken).ConfigureAwait(false);
        AddDistinct(diagnostics, discovered.Diagnostics);
        if (discovered.Status == ExternalHeadPartDependencyDiscoveryStatus.NotApplicable)
        {
            if (discovered.Descriptor is not null ||
                request.ExpectedDescriptor is not null)
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    "External discovery returned NotApplicable for a route with an expected descriptor."));
                return Refused(diagnostics);
            }
            if (HasErrors(diagnostics))
                return Refused(diagnostics);
            return new RaceMenuJslotExternalHeadPartPrecheckResult(
                RaceMenuJslotExternalHeadPartPrecheckStatus.NotApplicable,
                null,
                diagnostics.ToImmutable());
        }
        if (discovered.Status != ExternalHeadPartDependencyDiscoveryStatus.Accepted ||
            discovered.Descriptor is null)
        {
            if (!HasErrors(diagnostics))
                diagnostics.Add(Error(
                    "external-headpart-precheck-discovery",
                    "External head-part discovery refused the selected route."));
            return Refused(diagnostics);
        }

        try
        {
            _ = ExternalHeadPartDependencyDescriptorCodec
                .SerializeDescriptor(discovered.Descriptor);
            if (request.ExpectedDescriptor is { } expected &&
                !ExternalHeadPartDependencyDescriptorCodec
                    .SerializeDescriptor(expected).AsSpan().SequenceEqual(
                        ExternalHeadPartDependencyDescriptorCodec
                            .SerializeDescriptor(discovered.Descriptor)))
            {
                diagnostics.Add(Error(
                    ExternalHeadPartDiagnosticCodes.DescriptorLost,
                    "The independently discovered external descriptor does not match the expected receipt descriptor."));
                return Refused(diagnostics);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or
                                           ArgumentException or
                                           FormatException)
        {
            diagnostics.Add(Error(
                "external-headpart-precheck-descriptor",
                exception.Message));
            return Refused(diagnostics);
        }
        if (HasErrors(diagnostics))
            return Refused(diagnostics);

        return new RaceMenuJslotExternalHeadPartPrecheckResult(
            RaceMenuJslotExternalHeadPartPrecheckStatus.Accepted,
            new RaceMenuJslotExternalHeadPartPrecheckAcceptance(
                request.ExpectedPresetSha256,
                request.Target,
                discovered.Descriptor),
            diagnostics.ToImmutable());
    }

    private static ImmutableArray<SkyrimFaceRecordHeadPartSelection>
        ParseHeadPartSelections(
            PresetDocument preset,
            ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var selections = ImmutableArray.CreateBuilder<
            SkyrimFaceRecordHeadPartSelection>(preset.Appearance.HeadParts.Length);
        foreach (PresetHeadPart headPart in preset.Appearance.HeadParts)
        {
            if (headPart.Identifier.Plugin is not { } plugin ||
                headPart.Identifier.FormId is not { } formId)
            {
                diagnostics.Add(Error(
                    "external-headpart-precheck-headpart",
                    $"Preset head part '{headPart.Identifier.Raw}' has no portable plugin-local reference."));
                continue;
            }
            selections.Add(new SkyrimFaceRecordHeadPartSelection(
                new FormReference(plugin, formId), []));
        }
        if (selections.Count == 0)
            diagnostics.Add(Error(
                "external-headpart-precheck-headpart",
                "The selected preset contains no portable head-part selections."));
        return selections.ToImmutable();
    }

    private static bool MatchesReviewedAuthorities(
        ImmutableArray<SkyrimFaceRecordPluginAuthority> actual,
        ImmutableArray<SkyrimFaceRecordPluginAuthority> expected) =>
        actual.Length == expected.Length && actual.Zip(expected).All(pair =>
            pair.First.Plugin == pair.Second.Plugin &&
            pair.First.Path == pair.Second.Path &&
            pair.First.ExpectedSha256 == pair.Second.ExpectedSha256);

    private static void ValidateRequest(
        RaceMenuJslotExternalHeadPartPrecheckRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.PresetPath.Value.Length == 0 ||
            request.ExpectedPresetSha256.Value.Length != 64 ||
            request.Target is null ||
            string.IsNullOrWhiteSpace(request.Target.AuthorityId) ||
            request.Target.DataRoot.Value.Length == 0 ||
            request.Target.PluginOrder.IsDefaultOrEmpty ||
            request.Target.PluginOrder.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.Target.PluginOrder.Length ||
            request.Target.PluginOrder.Any(item =>
                item.Path.Value.Length == 0 ||
                item.ExpectedSha256.Value.Length != 64))
        {
            diagnostics.Add(Error(
                "external-headpart-precheck-request",
                "External JSlot precheck requires one preset, reviewed target, and complete distinct plugin authority order."));
        }
    }

    private static async ValueTask<Sha256Hash> HashFileAsync(
        WorkspacePath path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path.Value,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(
            await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false)));
    }

    private static void AddDistinct(
        ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic diagnostic in source)
        {
            if (!target.Any(existing => existing.Code == diagnostic.Code &&
                                        existing.Severity == diagnostic.Severity &&
                                        existing.Message == diagnostic.Message))
                target.Add(diagnostic);
        }
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static RaceMenuJslotExternalHeadPartPrecheckResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(RaceMenuJslotExternalHeadPartPrecheckStatus.Refused, null,
            diagnostics.ToImmutable());
}
