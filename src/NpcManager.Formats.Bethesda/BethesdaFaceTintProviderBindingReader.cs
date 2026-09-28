using System.Collections.Immutable;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reads the FO4 RACE tint groups and their referenced CLFM records from an
/// explicit copied-plugin order. It is intentionally read-only and fails closed
/// when a referenced provider is absent or has an unsupported data variant.
/// </summary>
public sealed class BethesdaFaceTintProviderBindingReader : IFaceTintProviderBindingReader
{
    public async ValueTask<FaceTintProviderBindingResult> ReadAsync(
        FaceTintProviderBindingRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Edition != GameEdition.Fallout4)
        {
            diagnostics.Add(new Diagnostic("facetint-provider-game-unsupported", DiagnosticSeverity.Error,
                "RACE/CLFM FaceTint provider binding is supported for Fallout 4 only."));
            return Refused(diagnostics);
        }
        if (request.PluginOrder.IsDefaultOrEmpty)
        {
            diagnostics.Add(new Diagnostic("facetint-provider-plugin-order-required", DiagnosticSeverity.Error,
                "RACE/CLFM provider binding requires an explicit plugin order."));
            return Refused(diagnostics);
        }

        var records = new Dictionary<RecordKey, IMajorRecordGetter>();
        foreach (var plugin in request.PluginOrder)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = Path.Combine(request.DataRoot.Value, plugin.Value);
            if (!File.Exists(path))
            {
                diagnostics.Add(new Diagnostic("facetint-provider-plugin-missing", DiagnosticSeverity.Error,
                    $"Explicit provider plugin '{plugin.Value}' does not exist under the copied Data root."));
                continue;
            }
            try
            {
                using var mod = Fallout4Mod.CreateFromBinaryOverlay(path, Fallout4Release.Fallout4);
                foreach (var record in mod.EnumerateMajorRecords())
                    records[new RecordKey(record.FormKey.ModKey.ToString(), record.FormKey.ID)] = record;
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                diagnostics.Add(new Diagnostic("facetint-provider-plugin-read-failed", DiagnosticSeverity.Error,
                    $"Provider plugin '{plugin.Value}' could not be read: {exception.Message}"));
            }
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var npcKey = new RecordKey(request.WinningPlugin.Value, request.NpcFormId.Value);
        if (!records.TryGetValue(npcKey, out var npcRecord) || npcRecord is not INpcGetter npc)
        {
            diagnostics.Add(new Diagnostic("facetint-provider-npc-missing", DiagnosticSeverity.Error,
                $"Winning NPC {request.NpcFormId} was not found in plugin '{request.WinningPlugin.Value}'."));
            return Refused(diagnostics);
        }

        var raceKey = new RecordKey(npc.Race.FormKey.ModKey.ToString(), npc.Race.FormKey.ID);
        if (!records.TryGetValue(raceKey, out var raceRecord) || raceRecord is not IRaceGetter race)
        {
            diagnostics.Add(new Diagnostic("facetint-provider-race-missing", DiagnosticSeverity.Error,
                $"NPC {request.NpcFormId} references RACE {raceKey} which is not available in the explicit provider order."));
            return Refused(diagnostics);
        }

        var genderedHeadData = race.HeadData;
        if (genderedHeadData is null)
        {
            // A race may legitimately define no tint groups at all. Preserve a
            // resolved, empty binding so ordinary FO4 provider resolution does
            // not require synthetic tint records, while still failing closed
            // when a declared gendered head-data branch is missing below.
            return new FaceTintProviderBindingResult(
                true,
                new FaceTintRaceBinding(
                    FormIdFor(raceRecord.FormKey.ID),
                    raceRecord.FormKey.ModKey.ToString(),
                    raceRecord.EditorID,
                    request.Sex,
                    ImmutableArray<FaceTintGroupBinding>.Empty),
                diagnostics.ToImmutable());
        }
        var headData = request.Sex == NpcSex.Female ? genderedHeadData.Female : genderedHeadData.Male;
        if (headData is null)
        {
            diagnostics.Add(new Diagnostic("facetint-provider-head-data-missing", DiagnosticSeverity.Error,
                $"RACE {raceKey} does not expose {request.Sex.ToString().ToLowerInvariant()} head data."));
            return Refused(diagnostics);
        }

        var groups = ImmutableArray.CreateBuilder<FaceTintGroupBinding>(headData.TintLayers.Count);
        foreach (var group in headData.TintLayers)
        {
            var sourceOptions = group.Options ?? [];
            var options = ImmutableArray.CreateBuilder<FaceTintOptionBinding>(sourceOptions.Count);
            foreach (var option in sourceOptions)
            {
                var sourceTemplateColors = option.TemplateColors ?? [];
                var templateColors = ImmutableArray.CreateBuilder<FaceTintTemplateColorBinding>(sourceTemplateColors.Count);
                foreach (var templateColor in sourceTemplateColors)
                {
                    var colorKey = new RecordKey(templateColor.Color.FormKey.ModKey.ToString(), templateColor.Color.FormKey.ID);
                    if (!records.TryGetValue(colorKey, out var colorRecord) || colorRecord is not IColorRecordGetter color)
                    {
                        diagnostics.Add(new Diagnostic("facetint-provider-clfm-missing", DiagnosticSeverity.Error,
                            $"RACE {raceKey} tint option {option.Index} references missing CLFM {colorKey}."));
                        continue;
                    }
                    var binding = ReadColor(colorKey, color, diagnostics);
                    if (binding is null) continue;
                    templateColors.Add(new FaceTintTemplateColorBinding(
                        templateColor.TemplateIndex,
                        templateColor.Alpha,
                        templateColor.BlendOperation.ToString(),
                        binding));
                }
                options.Add(new FaceTintOptionBinding(
                    option.Index,
                    option.Slot.ToString(),
                    option.Name?.String,
                    option.Default,
                    option.Textures.ToImmutableArray(),
                    templateColors.ToImmutable()));
            }
            groups.Add(new FaceTintGroupBinding(group.CategoryIndex, group.Name?.String, options.ToImmutable()));
        }
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        var bindingResult = new FaceTintRaceBinding(
            FormIdFor(raceRecord.FormKey.ID),
            raceRecord.FormKey.ModKey.ToString(),
            raceRecord.EditorID,
            request.Sex,
            groups.ToImmutable());
        return new FaceTintProviderBindingResult(true, bindingResult, diagnostics.ToImmutable());
    }

    private static FaceTintColorProviderBinding? ReadColor(
        RecordKey key, IColorRecordGetter color, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var data = color.Data;
        return data switch
        {
            IColorDataGetter rgb => new FaceTintColorProviderBinding(
                FormIdFor(key.FormId), key.Plugin, color.EditorID, FaceTintColorDataKind.Rgb,
                rgb.Color.R, rgb.Color.G, rgb.Color.B, null),
            IColorRemappingIndexGetter remapping => new FaceTintColorProviderBinding(
                FormIdFor(key.FormId), key.Plugin, color.EditorID, FaceTintColorDataKind.RemappingIndex,
                null, null, null, remapping.Index),
            _ => UnsupportedColor(key, diagnostics)
        };
    }

    private static FaceTintColorProviderBinding? UnsupportedColor(
        RecordKey key, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        diagnostics.Add(new Diagnostic("facetint-provider-clfm-data-unsupported", DiagnosticSeverity.Error,
            $"CLFM {key} uses an unsupported data variant."));
        return null;
    }

    private static string FormIdFor(uint value) => $"0x{value:X8}";
    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static FaceTintProviderBindingResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private readonly record struct RecordKey(string Plugin, uint FormId)
    {
        public override string ToString() => $"{Plugin}|0x{FormId:X8}";
    }
}
