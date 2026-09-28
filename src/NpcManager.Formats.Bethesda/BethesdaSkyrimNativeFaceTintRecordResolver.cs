using System.Collections.Immutable;
using System.Security.Cryptography;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Resolves the exact Skyrim NPC/RACE/CLFM inputs used by the native FaceTint
/// compositor. It reads only an explicit hash-bound copied-plugin order.
/// </summary>
public sealed class BethesdaSkyrimNativeFaceTintRecordResolver(
    IWorkspacePolicy workspacePolicy,
    WorkspacePath labRoot) : ISkyrimNativeFaceTintRecordResolver
{
    private const int MaximumPlugins = 64;
    // Vanilla Nord female head data contains 33 rows. Keep a defensive bound
    // without rejecting ordinary engine-authored RACE tables.
    private const int MaximumRaceLayers = 256;
    private const long MaximumPluginBytes = 2L * 1024 * 1024 * 1024;

    public async ValueTask<SkyrimNativeFaceTintRecordResult> ResolveAsync(
        SkyrimNativeFaceTintRecordRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        foreach (var authority in request.PluginOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ValidateAuthorityAsync(authority, diagnostics, cancellationToken)
                .ConfigureAwait(false);
        }
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        SkyrimNativeTintCatalog catalog;
        try
        {
            catalog = BethesdaSkyrimNativeFaceTintCatalogLoader.Load(
                request.PluginOrder, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(Error("skyrim-native-tint-plugin-malformed",
                $"The copied plugin order could not be decoded: {exception.Message}"));
            return Refused(diagnostics);
        }

        if (!catalog.Npcs.TryGetValue(SkyrimNativeTintRecordKey.From(request.Npc), out var npc) ||
            npc.IsDeleted)
        {
            diagnostics.Add(Error("skyrim-native-tint-npc-missing",
                $"Winning NPC {request.Npc} is unavailable in the explicit plugin order."));
            return Refused(diagnostics);
        }
        if (npc.Sex != request.ExpectedSex)
        {
            diagnostics.Add(Error("skyrim-native-tint-sex-drift",
                $"Winning NPC {request.Npc} is {npc.Sex}, not the expected {request.ExpectedSex}."));
        }
        if (!SameReference(npc.Race, request.ExpectedRace))
        {
            diagnostics.Add(Error("skyrim-native-tint-race-drift",
                $"Winning NPC {request.Npc} references {npc.Race}, not {request.ExpectedRace}."));
        }
        if (!catalog.Races.TryGetValue(SkyrimNativeTintRecordKey.From(npc.Race), out var race) ||
            race.IsDeleted)
        {
            diagnostics.Add(Error("skyrim-native-tint-race-missing",
                $"Winning RACE {npc.Race} is unavailable in the explicit plugin order."));
            return Refused(diagnostics);
        }
        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }

        var authored = BuildAuthoredMap(npc, diagnostics);
        Dictionary<ushort, AssetPath> maskOverrides = BuildMaskOverrides(
            request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);
        var appliedOverrides = new HashSet<ushort>();
        ImmutableArray<SkyrimNativeTintRaceLayer> raceLayers = npc.Sex == NpcSex.Female
            ? race.FemaleLayers
            : race.MaleLayers;
        if (raceLayers.Length > MaximumRaceLayers)
        {
            diagnostics.Add(Error("skyrim-native-tint-layer-limit",
                $"RACE {race.Reference} exposes {raceLayers.Length} tint layers; the engine contract admits at most {MaximumRaceLayers}."));
            return Refused(diagnostics);
        }

        var raceIndexCounts = raceLayers
            .Where(item => item.Index is not null)
            .GroupBy(item => item.Index!.Value)
            .ToDictionary(group => group.Key, group => group.Count());
        var ambiguousIndexes = raceIndexCounts
            .Where(item => item.Value > 1)
            .Select(item => item.Key)
            .ToHashSet();
        var reportedAmbiguousIndexes = new HashSet<ushort>();
        var layers = ImmutableArray.CreateBuilder<SkyrimNativeFaceTintLayerRoute>(raceLayers.Length);
        for (var order = 0; order < raceLayers.Length; order++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SkyrimNativeTintRaceLayer source = raceLayers[order];
            if (source.Index is not { } index)
            {
                diagnostics.Add(Error("skyrim-native-tint-race-index",
                    $"RACE {race.Reference} tint row {order} has no TINI index."));
                continue;
            }
            if (ambiguousIndexes.Contains(index) &&
                (authored.ContainsKey(index) || maskOverrides.ContainsKey(index)))
            {
                if (reportedAmbiguousIndexes.Add(index))
                {
                    diagnostics.Add(Error("skyrim-native-tint-race-index-ambiguous",
                        $"RACE {race.Reference} reuses TINI {index} across distinct masks, so an NPC-authored layer or mask override cannot target it unambiguously."));
                }
                continue;
            }

            AssetPath maskPath;
            if (maskOverrides.TryGetValue(index, out AssetPath overridePath))
            {
                maskPath = overridePath;
                appliedOverrides.Add(index);
            }
            else try
                {
                    maskPath = ToTexturePath(source.MaskPath);
                }
                catch (ArgumentException exception)
                {
                    diagnostics.Add(Error("skyrim-native-tint-mask-path",
                        $"RACE {race.Reference} tint index {index} has an invalid TINT path: {exception.Message}"));
                    continue;
                }

            if (authored.TryGetValue(index, out var npcLayer))
            {
                if (npcLayer.Color is not { } color || !IsCoverage(npcLayer.Coverage))
                {
                    diagnostics.Add(Error("skyrim-native-tint-authored-layer",
                        $"NPC {npc.Reference} authored tint index {index} is incomplete or outside 0..1 coverage."));
                    continue;
                }
                layers.Add(new SkyrimNativeFaceTintLayerRoute(
                    order, index, source.MaskType, maskPath,
                    color.R, color.G, color.B, npcLayer.Coverage!.Value,
                    SkyrimNativeFaceTintColorSource.NpcAuthored, null, null));
                continue;
            }

            float defaultCoverage = ResolveDefaultCoverage(source, diagnostics, race.Reference, index);
            byte red = byte.MaxValue;
            byte green = byte.MaxValue;
            byte blue = byte.MaxValue;
            SkyrimFaceRecordProvider? colorProvider = null;
            if (source.DefaultColor is { } defaultColor)
            {
                if (!catalog.Colors.TryGetValue(SkyrimNativeTintRecordKey.From(defaultColor), out var color) ||
                    color.IsDeleted)
                {
                    if (defaultCoverage > 0F)
                    {
                        diagnostics.Add(Error("skyrim-native-tint-default-color-missing",
                            $"RACE {race.Reference} tint index {index} references unavailable CLFM {defaultColor}."));
                    }
                }
                else
                {
                    red = color.Color.R;
                    green = color.Color.G;
                    blue = color.Color.B;
                    colorProvider = color.Provider;
                }
            }
            layers.Add(new SkyrimNativeFaceTintLayerRoute(
                order, index, source.MaskType, maskPath,
                red, green, blue, defaultCoverage,
                SkyrimNativeFaceTintColorSource.RaceDefault,
                source.DefaultColor, colorProvider));
        }

        foreach (ushort unmatched in maskOverrides.Keys.Where(index =>
                     !appliedOverrides.Contains(index) &&
                     !ambiguousIndexes.Contains(index)))
        {
            diagnostics.Add(Error("skyrim-native-tint-mask-override-unmatched",
                $"Sidecar tint mask override index {unmatched} has no matching RACE tint layer."));
        }

        if (HasErrors(diagnostics))
        {
            return Refused(diagnostics);
        }
        if (ambiguousIndexes.Count > 0)
        {
            diagnostics.Add(Warning("skyrim-native-tint-race-index-reused",
                $"RACE {race.Reference} reuses {ambiguousIndexes.Count} TINI value(s). " +
                "Their rows were retained in race order using defaults only; no ambiguous NPC-authored value or mask override was admitted."));
        }

        return new SkyrimNativeFaceTintRecordResult(
            true,
            new SkyrimNativeFaceTintRecordRoute(
                npc.Reference, npc.Provider, race.Reference, race.Provider,
                npc.Sex, layers.ToImmutable()),
            diagnostics.ToImmutable());
    }

    private static Dictionary<ushort, SkyrimNativeTintAuthoredLayer> BuildAuthoredMap(
        SkyrimNativeTintNpc npc,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = new Dictionary<ushort, SkyrimNativeTintAuthoredLayer>();
        foreach (var layer in npc.Layers)
        {
            if (layer.Index is not { } index || !result.TryAdd(index, layer))
            {
                diagnostics.Add(Error("skyrim-native-tint-authored-index",
                    $"NPC {npc.Reference} has a missing or duplicate authored TINI index."));
            }
        }
        return result;
    }

    private static Dictionary<ushort, AssetPath> BuildMaskOverrides(
        SkyrimNativeFaceTintRecordRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var result = new Dictionary<ushort, AssetPath>();
        ImmutableArray<SkyrimNativeFaceTintMaskOverride> rows =
            request.MaskOverrides.IsDefault ? [] : request.MaskOverrides;
        foreach (SkyrimNativeFaceTintMaskOverride? row in rows)
        {
            if (row is null || string.IsNullOrWhiteSpace(row.MaskPath.Value) ||
                !row.MaskPath.Value.StartsWith("textures/",
                    StringComparison.OrdinalIgnoreCase) ||
                !row.MaskPath.Value.EndsWith(".dds",
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("skyrim-native-tint-mask-override",
                    "Sidecar tint mask overrides must be explicit textures/*.dds asset paths."));
                continue;
            }
            if (!result.TryAdd(row.Index, row.MaskPath))
            {
                diagnostics.Add(Error("skyrim-native-tint-mask-override-duplicate",
                    $"Sidecar tint mask override index {row.Index} is repeated."));
            }
        }
        return result;
    }

    private static float ResolveDefaultCoverage(
        SkyrimNativeTintRaceLayer layer,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        FormReference race,
        ushort index)
    {
        if (layer.DefaultColor is not { } defaultColor)
        {
            return 0F;
        }

        SkyrimNativeTintPreset[] matches = layer.Presets
            .Where(item => item.Color is { } reference && SameReference(reference, defaultColor))
            .ToArray();
        if (matches.Length > 1)
        {
            diagnostics.Add(Error("skyrim-native-tint-default-ambiguous",
                $"RACE {race} tint index {index} has more than one preset for default CLFM {defaultColor}."));
            return 0F;
        }
        if (matches.Length == 0)
        {
            return 0F;
        }
        if (!IsCoverage(matches[0].Coverage))
        {
            diagnostics.Add(Error("skyrim-native-tint-default-coverage",
                $"RACE {race} tint index {index} has default coverage outside 0..1."));
            return 0F;
        }
        return matches[0].Coverage!.Value;
    }

    private static AssetPath ToTexturePath(string? value)
    {
        string normalized = (value ?? string.Empty).Trim().Replace('\\', '/').TrimStart('/');
        if (!normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = "textures/" + normalized;
        }
        if (!normalized.EndsWith(".dds", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Skyrim tint masks must be DDS assets.");
        }
        return new AssetPath(normalized);
    }

    private static bool IsCoverage(float? value) =>
        value is { } coverage && float.IsFinite(coverage) && coverage is >= 0F and <= 1F;

    private static bool SameReference(FormReference left, FormReference right) =>
        left.FormId == right.FormId && string.Equals(left.Plugin.Value, right.Plugin.Value,
            StringComparison.OrdinalIgnoreCase);

    private static void ValidateRequest(
        SkyrimNativeFaceTintRecordRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
        {
            diagnostics.Add(Error("skyrim-native-tint-edition",
                "Native FaceTint record resolution supports Skyrim Special Edition only."));
        }
        if (!Enum.IsDefined(request.ExpectedSex))
        {
            diagnostics.Add(Error("skyrim-native-tint-sex", "The expected NPC sex is unsupported."));
        }
        if (!IsReference(request.Npc) || !IsReference(request.ExpectedRace))
        {
            diagnostics.Add(Error("skyrim-native-tint-reference",
                "NPC and expected RACE must be explicit nonzero plugin-local references."));
        }
        if (request.PluginOrder.IsDefaultOrEmpty || request.PluginOrder.Length > MaximumPlugins)
        {
            diagnostics.Add(Error("skyrim-native-tint-plugin-count",
                $"PluginOrder must explicitly contain between 1 and {MaximumPlugins} copied plugins."));
            return;
        }
        if (request.PluginOrder.Any(item => item is null))
        {
            diagnostics.Add(Error("skyrim-native-tint-authority",
                "PluginOrder may not contain absent provider authorities."));
            return;
        }
        if (request.PluginOrder.Select(item => item.Plugin.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(Error("skyrim-native-tint-plugin-duplicate",
                "PluginOrder may not repeat a plugin identity."));
        }
        if (request.PluginOrder.Select(item => item.Path.Value)
            .Distinct(StringComparer.OrdinalIgnoreCase).Count() != request.PluginOrder.Length)
        {
            diagnostics.Add(Error("skyrim-native-tint-path-duplicate",
                "PluginOrder may not repeat a provider path."));
        }
    }

    private async ValueTask ValidateAuthorityAsync(
        SkyrimFaceRecordPluginAuthority authority,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        CancellationToken cancellationToken)
    {
        if (authority is null || string.IsNullOrWhiteSpace(authority.Plugin.Value) ||
            string.IsNullOrWhiteSpace(authority.Path.Value) ||
            string.IsNullOrWhiteSpace(authority.ExpectedSha256.Value))
        {
            diagnostics.Add(Error("skyrim-native-tint-authority",
                "Every plugin authority must contain a plugin, path, and SHA-256."));
            return;
        }
        if (!authority.Path.IsUnder(labRoot) || authority.Path == labRoot ||
            HasAlternateDataStream(authority.Path.Value))
        {
            diagnostics.Add(Error("skyrim-native-tint-provider-outside-workspace",
                $"Provider for '{authority.Plugin}' must be an ordinary file under the K-local lab root."));
            return;
        }
        diagnostics.AddRange(workspacePolicy.EvaluateReadRoot(labRoot, authority.Path));
        if (!string.Equals(Path.GetFileName(authority.Path.Value), authority.Plugin.Value,
                StringComparison.OrdinalIgnoreCase))
        {
            diagnostics.Add(Error("skyrim-native-tint-provider-name",
                $"Provider filename for '{authority.Plugin}' does not match its plugin identity."));
            return;
        }

        try
        {
            var info = new FileInfo(authority.Path.Value);
            if (!info.Exists || info.Length <= 0 || info.Length > MaximumPluginBytes ||
                info.Attributes.HasFlag(FileAttributes.Directory) ||
                info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                diagnostics.Add(Error("skyrim-native-tint-provider-file",
                    $"Provider for '{authority.Plugin}' is not an admitted ordinary plugin file."));
                return;
            }
            if (TraversesReparsePoint(authority.Path.Value))
            {
                diagnostics.Add(Error("skyrim-native-tint-provider-reparse",
                    $"Provider for '{authority.Plugin}' traverses a reparse point."));
                return;
            }

            await using var stream = new FileStream(authority.Path.Value, FileMode.Open,
                FileAccess.Read, FileShare.Read, 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var actual = new Sha256Hash(Convert.ToHexString(
                await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)));
            if (actual != authority.ExpectedSha256)
            {
                diagnostics.Add(Error("skyrim-native-tint-provider-hash",
                    $"Provider for '{authority.Plugin}' does not match its expected SHA-256."));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                           ArgumentException or NotSupportedException)
        {
            diagnostics.Add(Error("skyrim-native-tint-provider-read",
                $"Provider for '{authority.Plugin}' could not be read: {exception.Message}"));
        }
    }

    private bool TraversesReparsePoint(string path)
    {
        var current = path;
        while (!string.Equals(current, labRoot.Value, StringComparison.OrdinalIgnoreCase))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
            {
                return true;
            }
            string? parent = Path.GetDirectoryName(current);
            if (string.IsNullOrWhiteSpace(parent) ||
                string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            current = parent;
        }
        return false;
    }

    private static bool HasAlternateDataStream(string path)
    {
        string root = Path.GetPathRoot(path) ?? string.Empty;
        return path.AsSpan(root.Length).Contains(':');
    }

    private static bool IsReference(FormReference reference) =>
        !string.IsNullOrWhiteSpace(reference.Plugin.Value) &&
        reference.FormId.Value is > 0 and <= 0x00FF_FFFF;

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static Diagnostic Warning(string code, string message) =>
        new(code, DiagnosticSeverity.Warning, message);

    private static SkyrimNativeFaceTintRecordResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());
}
