using System.Collections.Immutable;
using Mutagen.Bethesda.Plugins;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed partial class BethesdaObjectTemplateBinaryWriteService
{
    private static FormId? ValidateProposal(ObjectTemplateProposalArtifact proposal, GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!string.Equals(proposal.SchemaVersion, "1", StringComparison.Ordinal)) diagnostics.Add(new Diagnostic("object-template-binary-schema", DiagnosticSeverity.Error, "The object-template proposal schema version is unsupported."));
        if (!string.Equals(proposal.ArtifactKind, "object-template-combinations-proposal", StringComparison.Ordinal)) diagnostics.Add(new Diagnostic("object-template-binary-kind", DiagnosticSeverity.Error, "The proposal artifact kind must be object-template-combinations-proposal."));
        if (!GameEditionExtensions.TryParseWireName(proposal.Edition, out var proposalEdition) || proposalEdition != edition || edition != GameEdition.Fallout4) diagnostics.Add(new Diagnostic("object-template-binary-edition", DiagnosticSeverity.Error, "OBTS binary writing is Fallout 4-only and the proposal edition must match."));
        if (!FormId.TryParse(proposal.SourceFormId, out var sourceFormId) || sourceFormId.Value == 0) diagnostics.Add(new Diagnostic("object-template-binary-source-form", DiagnosticSeverity.Error, "The proposal source FormID is invalid."));
        if (string.IsNullOrWhiteSpace(proposal.EditorId)) diagnostics.Add(new Diagnostic("object-template-binary-editor-id", DiagnosticSeverity.Error, "The proposal EditorID is required."));
        if (proposal.Combinations.IsDefaultOrEmpty || proposal.Combinations.Length > 4096) diagnostics.Add(new Diagnostic("object-template-binary-combination-count", DiagnosticSeverity.Error, "The proposal must contain 1 to 4096 combinations."));
        for (var i = 0; i < proposal.Combinations.Length; i++)
        {
            var combination = proposal.Combinations[i];
            if (combination.Keywords.Length > byte.MaxValue) diagnostics.Add(new Diagnostic("object-template-binary-keyword-count", DiagnosticSeverity.Error, "An OBTS combination may contain at most 255 keywords because the wire count is one byte."));
            if (combination.Includes.Length > 9362) diagnostics.Add(new Diagnostic("object-template-binary-include-count", DiagnosticSeverity.Error, "An OBTS combination contains too many includes for a 16-bit subrecord payload."));
            if (combination.ParentCombinationIndex is { } parent && (parent < 0 || parent >= proposal.Combinations.Length)) diagnostics.Add(new Diagnostic("object-template-binary-parent", DiagnosticSeverity.Error, "Parent combination indexes must be non-negative and refer to an existing combination."));
            if (combination.DisplayName is { Length: > 4096 } || combination.DisplayName?.Any(char.IsControl) == true || combination.DisplayName?.Contains('\0') == true) diagnostics.Add(new Diagnostic("object-template-binary-name", DiagnosticSeverity.Error, "Combination names must be bounded and contain no control characters."));
            foreach (var reference in combination.Keywords.Concat(combination.Includes.Select(item => item.Mod)))
                if (!FormReference.TryParse(reference, out var parsedReference) || parsedReference.FormId.Value == 0) diagnostics.Add(new Diagnostic("object-template-binary-reference-invalid", DiagnosticSeverity.Error, $"OBTS reference '{reference}' is invalid or null."));
        }
        if (sourceFormId.Value == 0) return null;
        if (proposal.Mode is not (ObjectTemplateProposalMode.New or ObjectTemplateProposalMode.Override)) diagnostics.Add(new Diagnostic("object-template-binary-mode", DiagnosticSeverity.Error, "The proposal mode is invalid."));
        if (proposal.Mode == ObjectTemplateProposalMode.New && (!FormId.TryParse(proposal.TargetFormId ?? string.Empty, out var target) || target.Value == 0 || target.Value > 0x00FF_FFFF)) diagnostics.Add(new Diagnostic("object-template-binary-target-form", DiagnosticSeverity.Error, "New OBTS proposals require a nonzero plugin-local 24-bit target FormID."));
        if (proposal.Mode == ObjectTemplateProposalMode.Override && proposal.TargetFormId is not null && (!FormId.TryParse(proposal.TargetFormId, out var overrideTarget) || overrideTarget != sourceFormId)) diagnostics.Add(new Diagnostic("object-template-binary-override-target", DiagnosticSeverity.Error, "Override OBTS proposals must target their source FormID."));
        return sourceFormId;
    }

    private static FormId? ResolveTarget(ObjectTemplateProposalArtifact proposal, FormId? sourceFormId, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (sourceFormId is null) return null;
        if (proposal.Mode == ObjectTemplateProposalMode.Override) return sourceFormId;
        if (!FormId.TryParse(proposal.TargetFormId ?? string.Empty, out var target) || target.Value == 0 || target.Value > 0x00FF_FFFF) return null;
        return target;
    }

    private static void ValidatePropertyArtifact(ObjectTemplatePropertyProposalArtifact? properties,
        ObjectTemplateProposalArtifact proposal, GameEdition edition, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (properties is null) return;
        if (!string.Equals(properties.SchemaVersion, "1", StringComparison.Ordinal)) diagnostics.Add(new Diagnostic("object-template-binary-properties-schema", DiagnosticSeverity.Error, "The object-template property proposal schema version is unsupported."));
        if (!string.Equals(properties.ArtifactKind, "object-template-properties-proposal", StringComparison.Ordinal)) diagnostics.Add(new Diagnostic("object-template-binary-properties-kind", DiagnosticSeverity.Error, "The property artifact kind is unsupported."));
        if (!GameEditionExtensions.TryParseWireName(properties.Edition, out var propertyEdition) || propertyEdition != edition || edition != GameEdition.Fallout4) diagnostics.Add(new Diagnostic("object-template-binary-properties-edition", DiagnosticSeverity.Error, "Object-template property writing is Fallout 4-only and the property edition must match."));
        if (!string.Equals(properties.SourcePlugin, proposal.SourcePlugin, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("object-template-binary-properties-source", DiagnosticSeverity.Error, "The property proposal source plugin does not match the combinations proposal."));
        if (!string.Equals(properties.SourceFormId, proposal.SourceFormId, StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("object-template-binary-properties-form", DiagnosticSeverity.Error, "The property proposal source FormID does not match the combinations proposal."));
        if (proposal.Mode == ObjectTemplateProposalMode.Override && !string.Equals(properties.EditorId, proposal.EditorId, StringComparison.Ordinal)) diagnostics.Add(new Diagnostic("object-template-binary-properties-editor-id", DiagnosticSeverity.Error, "The property proposal EditorID does not match the override combinations proposal."));
        if (properties.Properties.IsDefaultOrEmpty || properties.Properties.Length > 4096) diagnostics.Add(new Diagnostic("object-template-binary-properties-count", DiagnosticSeverity.Error, "The property proposal must contain 1 to 4096 rows."));
        foreach (var property in properties.Properties)
        {
            if (property.CombinationIndex < 0 || property.CombinationIndex >= proposal.Combinations.Length) diagnostics.Add(new Diagnostic("object-template-binary-properties-combination", DiagnosticSeverity.Error, "Property combination indexes must refer to an existing combination."));
            if (!TryGetValueType(property.ValueType, out _)) diagnostics.Add(new Diagnostic("object-template-binary-properties-value-type", DiagnosticSeverity.Error, $"Unsupported OMOD value type '{property.ValueType}'."));
            var formType = property.ValueType is "FormIDInt" or "FormIDFloat";
            if (formType && (property.Value1FormId is null || !FormReference.TryParse(property.Value1FormId, out var form) || form.FormId.Value == 0)) diagnostics.Add(new Diagnostic("object-template-binary-properties-form-id", DiagnosticSeverity.Error, "FormID property rows require a non-null Value1FormId."));
            if (!formType && property.Value1FormId is not null) diagnostics.Add(new Diagnostic("object-template-binary-properties-form-id", DiagnosticSeverity.Error, "Only FormID property rows may set Value1FormId."));
            if (property.Value1Float is { } value1 && (!double.IsFinite(value1) || value1 < -float.MaxValue || value1 > float.MaxValue)) diagnostics.Add(new Diagnostic("object-template-binary-properties-finite", DiagnosticSeverity.Error, "Value1Float must be finite and representable as a single-precision value."));
            if (!double.IsFinite(property.Value2Float) || property.Value2Float < -float.MaxValue || property.Value2Float > float.MaxValue || !double.IsFinite(property.StepValue) || property.StepValue < -float.MaxValue || property.StepValue > float.MaxValue) diagnostics.Add(new Diagnostic("object-template-binary-properties-finite", DiagnosticSeverity.Error, "Value2Float and StepValue must be finite single-precision values."));
        }
    }

    private static bool TryGetValueType(string value, out byte wireValueType)
    {
        wireValueType = value switch
        {
            "IntType" => 0,
            "FloatType" => 1,
            "BoolType" => 2,
            "StringType" => 3,
            "FormIDInt" => 4,
            "EnumType" => 5,
            "FormIDFloat" => 6,
            _ => byte.MaxValue
        };
        return wireValueType != byte.MaxValue;
    }

    private static void WriteProperty(BinaryWriter writer, ObjectTemplatePropertyArtifact property,
        ImmutableArray<ModKey> masterNames, ModKey outputModKey)
    {
        if (!TryGetValueType(property.ValueType, out var valueType)) throw new InvalidDataException($"Unsupported OMOD value type '{property.ValueType}'.");
        writer.Write(valueType); writer.Write(new byte[3]);
        writer.Write(property.FunctionType); writer.Write(new byte[3]);
        writer.Write(property.PropertyIndex); writer.Write((ushort)0);
        if (valueType is 4 or 6)
        {
            if (property.Value1FormId is null || !FormReference.TryParse(property.Value1FormId, out var form)) throw new InvalidDataException("A FormID property row has no valid Value1FormId.");
            writer.Write(MapReference(form.ToString(), masterNames, outputModKey));
        }
        else
        {
            if (property.ValueType == "FloatType")
                writer.Write(BitConverter.SingleToInt32Bits((float)(property.Value1Float ?? 0)));
            else
                writer.Write(property.Value1Integer ?? 0);
        }
        if (property.ValueType is "FloatType" or "FormIDFloat")
            writer.Write(BitConverter.SingleToInt32Bits((float)property.Value2Float));
        else
            writer.Write(property.Value2Integer);
        writer.Write(BitConverter.SingleToInt32Bits((float)property.StepValue));
    }
}
