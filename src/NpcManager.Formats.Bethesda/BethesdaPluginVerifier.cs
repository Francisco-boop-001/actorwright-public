using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;
/// <summary>Minimal independent TES4 record reader used only for post-write evidence.</summary>
public static partial class BethesdaPluginVerifier
{
    public static PluginVerificationResult Verify(PluginVerificationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!File.Exists(request.SourcePlugin.Value))
            diagnostics.Add(new Diagnostic("source-plugin-missing", DiagnosticSeverity.Error, "The source plugin does not exist."));
        if (!File.Exists(request.OutputPlugin.Value))
            diagnostics.Add(new Diagnostic("output-plugin-missing", DiagnosticSeverity.Error, "The output plugin does not exist."));
        if (diagnostics.Count > 0) return new PluginVerificationResult(false, diagnostics.ToImmutable(), ImmutableArray<MutationChange>.Empty);

        try
        {
            var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
            var outputPlugin = new PluginName(Path.GetFileName(request.OutputPlugin.Value));
            var source = Parse(
                File.ReadAllBytes(request.SourcePlugin.Value),
                request.TargetFormId,
                sourcePlugin,
                sourcePlugin,
                cancellationToken);
            var output = Parse(
                File.ReadAllBytes(request.OutputPlugin.Value),
                request.TargetFormId,
                sourcePlugin,
                outputPlugin,
                cancellationToken);
            foreach (var expected in request.ExpectedChanges)
            {
                if (expected.Field == "WholeSkin") continue;
                var sourceValue = source.ReadValue(request.Edition, expected.Field);
                var outputValue = output.ReadValue(request.Edition, expected.Field);
                var expectedAfter = expected.Field is "Keywords" or "AttachParentSlots"
                    ? ResolveListExpectation(sourceValue, expected.After)
                    : expected.Field == "Factions"
                        ? ResolveFactionExpectation(sourceValue, expected.After)
                    : expected.Field == "Inventory"
                        ? ResolveInventoryExpectation(sourceValue, expected.After)
                    : expected.Field == "Perks"
                        ? ResolvePerkExpectation(sourceValue, expected.After)
                    : expected.Field == "Properties"
                        ? ResolvePropertyExpectation(sourceValue, expected.After)
                    : expected.Field == "BodyMorphRegions"
                        ? ResolveBodyMorphExpectation(sourceValue, expected.After)
                    : expected.After;
                if (expected.Before is not null && !string.Equals(sourceValue, expected.Before, StringComparison.Ordinal))
                    diagnostics.Add(new Diagnostic("source-field-mismatch", DiagnosticSeverity.Error,
                        $"Source field {expected.Field} was '{sourceValue}', expected '{expected.Before}'."));
                if (expectedAfter is not null && !string.Equals(outputValue, expectedAfter, StringComparison.Ordinal))
                    diagnostics.Add(new Diagnostic("output-field-mismatch", DiagnosticSeverity.Error,
                        $"Output field {expected.Field} was '{outputValue}', expected '{expectedAfter}'."));
                if (expected.Field == "Sex")
                    VerifySexBitPreservation(source, output, diagnostics);
            }

            VerifyAidtPreservation(request, source, output, diagnostics);
            var changedFields = request.ExpectedChanges.Select(change => RawField(change.Field)).ToHashSet(StringComparer.Ordinal);
            if (request.WholeSkin is { } wholeSkin)
                changedFields.UnionWith(wholeSkin.PreservesHead ? ["WNAM"] : ["WNAM", "FTST", "PNAM"]);
            foreach (var field in request.PreservedFields.Where(field => !changedFields.Contains(field)))
            {
                var sourceHasField = source.Subrecords.TryGetValue(field, out var sourceBytes);
                var outputHasField = output.Subrecords.TryGetValue(field, out var outputBytes);
                if (!sourceHasField && !outputHasField) continue;
                if (!sourceHasField || !outputHasField)
                {
                    diagnostics.Add(new Diagnostic("preserved-field-missing", DiagnosticSeverity.Error,
                        $"Preserved field {field} was not present in both source and output NPC records."));
                    continue;
                }
                if (!sourceBytes.AsSpan().SequenceEqual(outputBytes))
                    diagnostics.Add(new Diagnostic("preserved-field-drift", DiagnosticSeverity.Error,
                        $"Preserved field {field} changed outside the requested mutation."));
            }

            VerifyWholeSkin(request, diagnostics);
            var observed = request.ExpectedChanges.Select(change => change.Field == "WholeSkin" ? change : change with
            {
                Before = source.ReadValue(request.Edition, change.Field),
                After = output.ReadValue(request.Edition, change.Field)
            }).ToImmutableArray();
            return new PluginVerificationResult(diagnostics.Count == 0, diagnostics.ToImmutable(), observed);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            diagnostics.Add(new Diagnostic("plugin-parse-failed", DiagnosticSeverity.Error, $"Plugin verification failed: {exception.Message}"));
            return new PluginVerificationResult(false, diagnostics.ToImmutable(), ImmutableArray<MutationChange>.Empty);
        }
    }
}
