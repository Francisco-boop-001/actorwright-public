using System.Collections.Immutable;
using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>
/// Loads the face-relevant subset of load-order BodySlide sidecars before a
/// native batch writes anything. Entries retain the upstream last-loaded-wins
/// merge rule while all files remain typed, hash-bound, and K-local.
/// </summary>
public sealed class SkyrimFaceGenSidecarOverlayLoader(
    IBodySidecarInspectionService inspectionService)
    : ISkyrimFaceGenSidecarOverlayLoader
{
    public async ValueTask<SkyrimFaceGenSidecarOverlayLoadResult> LoadAsync(
        SkyrimFaceGenSidecarOverlayLoadRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        ValidateRequest(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Dictionary<string, FaceGenBakeTarget> targets = request.Targets
            .ToDictionary(TargetKey, StringComparer.OrdinalIgnoreCase);
        var overlays = new Dictionary<string, PendingOverlay>(
            StringComparer.OrdinalIgnoreCase);
        var authorities = ImmutableArray.CreateBuilder<SkyrimFaceGenSidecarAuthority>();
        var stems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (PluginName plugin in request.PluginOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string stem = Path.GetFileNameWithoutExtension(plugin.Value);
            if (!stems.Add(stem))
            {
                diagnostics.Add(Error("facegen-sidecar-stem-collision",
                    $"Load order plugins collide on sidecar stem '{stem}'."));
                continue;
            }

            var path = new WorkspacePath(Path.Combine(
                request.DataRoot.Value, stem + ".bssliders"));
            if (!File.Exists(path.Value)) continue;

            BodySidecarInspectResult inspected = await inspectionService.InspectAsync(
                new BodySidecarInspectRequest(request.Edition, path),
                cancellationToken).ConfigureAwait(false);
            AddDistinct(diagnostics, inspected.Diagnostics);
            if (!inspected.IsValid || inspected.Document is null)
            {
                diagnostics.Add(Error("facegen-sidecar-invalid",
                    $"Sidecar '{path.Value}' is not a valid typed schema-11-or-earlier document."));
                continue;
            }
            if (!string.Equals(inspected.Document.Plugin.Value, plugin.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("facegen-sidecar-plugin-drift",
                    $"Sidecar '{path.Value}' declares '{inspected.Document.Plugin.Value}', not loaded plugin '{plugin.Value}'."));
                continue;
            }

            var authority = new SkyrimFaceGenSidecarAuthority(
                plugin, path, inspected.Document.SourceSha256);
            authorities.Add(authority);
            var documentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (BodySidecarNpcSummary entry in inspected.Document.Npcs)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryNormalizeIdentifier(entry.Identifier,
                        out string? key, out string? error))
                {
                    diagnostics.Add(Error("facegen-sidecar-identifier",
                        $"Sidecar '{path.Value}' contains invalid NPC identity '{entry.Identifier}': {error}"));
                    continue;
                }
                if (!documentKeys.Add(key))
                {
                    diagnostics.Add(Error("facegen-sidecar-identity-duplicate",
                        $"Sidecar '{path.Value}' repeats NPC identity '{entry.Identifier}'."));
                    continue;
                }
                if (!targets.TryGetValue(key, out FaceGenBakeTarget? target))
                {
                    continue;
                }

                ImmutableArray<SkyrimNativeFaceTintMaskOverride> tintOverrides =
                    ConvertTintOverrides(entry.SseTintTextures, path, diagnostics);
                if (HasErrors(diagnostics)) continue;
                if (!HasFaceState(entry, tintOverrides)) continue;

                if (!overlays.TryGetValue(key, out PendingOverlay? pending))
                {
                    pending = new PendingOverlay(target.OriginatingPlugin,
                        target.FormId);
                    overlays.Add(key, pending);
                }
                pending.Merge(entry, tintOverrides, authority);
            }
        }

        if (HasErrors(diagnostics)) return Refused(diagnostics);
        ImmutableArray<SkyrimFaceGenSidecarOverlay> ordered = request.Targets
            .Select(TargetKey)
            .Where(overlays.ContainsKey)
            .Select(key => overlays[key].ToImmutable())
            .ToImmutableArray();
        diagnostics.Add(new Diagnostic("facegen-sidecar-overlays-loaded",
            DiagnosticSeverity.Info,
            $"Loaded {authorities.Count} BodySlide sidecar file(s) and resolved {ordered.Length} face overlay(s) before the batch write boundary."));
        return new SkyrimFaceGenSidecarOverlayLoadResult(
            true, ordered, authorities.ToImmutable(), diagnostics.ToImmutable());
    }

    private static ImmutableArray<SkyrimNativeFaceTintMaskOverride> ConvertTintOverrides(
        ImmutableArray<BodySidecarTintTexture> rows,
        WorkspacePath path,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (rows.IsDefaultOrEmpty) return [];
        var seen = new HashSet<ushort>();
        var result = ImmutableArray.CreateBuilder<SkyrimNativeFaceTintMaskOverride>(rows.Length);
        foreach (BodySidecarTintTexture row in rows)
        {
            if (!row.Texture.Value.StartsWith("textures/",
                    StringComparison.OrdinalIgnoreCase) ||
                !row.Texture.Value.EndsWith(".dds",
                    StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(Error("facegen-sidecar-tint-path",
                    $"Sidecar '{path.Value}' tint override index {row.Index} must use a textures/*.dds asset path."));
                continue;
            }
            if (row.Index is < 0 or > ushort.MaxValue)
            {
                diagnostics.Add(Error("facegen-sidecar-tint-index",
                    $"Sidecar '{path.Value}' tint override index {row.Index} is outside the Skyrim ushort layer range."));
                continue;
            }
            ushort index = checked((ushort)row.Index);
            if (!seen.Add(index))
            {
                diagnostics.Add(Error("facegen-sidecar-tint-duplicate",
                    $"Sidecar '{path.Value}' repeats tint override index {index}."));
                continue;
            }
            result.Add(new SkyrimNativeFaceTintMaskOverride(index, row.Texture));
        }
        return result.ToImmutable();
    }

    private static bool HasFaceState(
        BodySidecarNpcSummary entry,
        ImmutableArray<SkyrimNativeFaceTintMaskOverride> tintOverrides) =>
        !Normalize(entry.SseCustomMorphs).IsEmpty ||
        !Normalize(entry.SseSculpt).IsEmpty ||
        !Normalize(entry.SseSculptParts).IsEmpty ||
        !tintOverrides.IsEmpty;

    private static string TargetKey(FaceGenBakeTarget target) =>
        $"{target.OriginatingPlugin.Value}|{target.FormId.Value & 0x00FF_FFFF:X6}";

    private static bool TryNormalizeIdentifier(
        string value,
        out string key,
        out string error)
    {
        key = string.Empty;
        error = string.Empty;
        int separator = value.IndexOf('|');
        if (separator <= 0 || separator != value.LastIndexOf('|'))
        {
            error = "expected Plugin.esp|HEX6";
            return false;
        }
        PluginName plugin;
        try { plugin = new PluginName(value[..separator]); }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
        string local = value[(separator + 1)..];
        if (local.Length != 6 || !uint.TryParse(local,
                NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture,
                out uint formId) || formId > 0x00FF_FFFF)
        {
            error = "local FormID must be exactly six hexadecimal digits";
            return false;
        }
        key = $"{plugin.Value}|{formId:X6}";
        return true;
    }

    private static void ValidateRequest(
        SkyrimFaceGenSidecarOverlayLoadRequest request,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("facegen-sidecar-edition",
                "FaceGen sidecar overlays support Skyrim Special Edition only."));
        if (!Directory.Exists(request.DataRoot.Value))
            diagnostics.Add(Error("facegen-sidecar-data-root",
                "The copied Data root must already exist."));
        if (request.PluginOrder.IsDefaultOrEmpty ||
            request.PluginOrder.Select(item => item.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            request.PluginOrder.Length)
            diagnostics.Add(Error("facegen-sidecar-plugin-order",
                "PluginOrder must be an explicit distinct ascending array."));
        if (request.Targets.IsDefaultOrEmpty || request.Targets.Any(item =>
                item is null || item.FormId.Value == 0 ||
                !request.PluginOrder.Any(plugin => string.Equals(plugin.Value,
                    item.OriginatingPlugin.Value, StringComparison.OrdinalIgnoreCase))))
            diagnostics.Add(Error("facegen-sidecar-targets",
                "Targets must be explicit discovered NPC identities inside PluginOrder."));
        else if (request.Targets.Select(TargetKey)
                     .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                 request.Targets.Length)
            diagnostics.Add(Error("facegen-sidecar-target-duplicate",
                "Targets may not repeat an originating NPC identity."));
    }

    private static ImmutableArray<T> Normalize<T>(ImmutableArray<T> values) =>
        values.IsDefault ? [] : values;

    private static void AddDistinct(
        ImmutableArray<Diagnostic>.Builder target,
        IEnumerable<Diagnostic> source)
    {
        foreach (Diagnostic diagnostic in source)
            if (!target.Any(existing => existing.Code == diagnostic.Code &&
                                        existing.Severity == diagnostic.Severity &&
                                        existing.Message == diagnostic.Message))
                target.Add(diagnostic);
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private static SkyrimFaceGenSidecarOverlayLoadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, [], [], diagnostics.ToImmutable());

    private sealed class PendingOverlay(PluginName plugin, FormId formId)
    {
        private ImmutableArray<SkyrimRaceMenuCustomMorphValue> _customMorphs = [];
        private ImmutableArray<RaceMenuSculptVertex> _legacyHeadSculpt = [];
        private ImmutableArray<BodySidecarSculptPart> _sculptParts = [];
        private ImmutableArray<SkyrimNativeFaceTintMaskOverride> _tintOverrides = [];
        private readonly List<SkyrimFaceGenSidecarAuthority> _sources = [];

        public void Merge(
            BodySidecarNpcSummary entry,
            ImmutableArray<SkyrimNativeFaceTintMaskOverride> tintOverrides,
            SkyrimFaceGenSidecarAuthority source)
        {
            ImmutableArray<SkyrimRaceMenuCustomMorphValue> custom = Normalize(entry.SseCustomMorphs);
            ImmutableArray<RaceMenuSculptVertex> legacy = Normalize(entry.SseSculpt);
            ImmutableArray<BodySidecarSculptPart> parts = Normalize(entry.SseSculptParts);
            if (!custom.IsEmpty) _customMorphs = custom;
            if (!legacy.IsEmpty) _legacyHeadSculpt = legacy;
            if (!parts.IsEmpty) _sculptParts = parts;
            if (!tintOverrides.IsEmpty) _tintOverrides = tintOverrides;
            if (!_sources.Contains(source)) _sources.Add(source);
        }

        public SkyrimFaceGenSidecarOverlay ToImmutable() =>
            new(plugin, formId, _customMorphs, _legacyHeadSculpt,
                _sculptParts, _tintOverrides, _sources.ToImmutableArray());
    }
}
