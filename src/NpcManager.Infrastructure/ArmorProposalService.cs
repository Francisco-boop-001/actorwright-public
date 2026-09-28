using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Infrastructure;

/// <summary>Writes a typed, hash-bound ARMO proposal without binary plugin mutation.</summary>
public sealed class ArmorProposalService(
    IPluginReader pluginReader,
    IWorkspacePolicy policy,
    WorkspacePath labRoot) : IArmorProposalService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ArmorProposalResult> ProposeAsync(ArmorProposalRequest request,
        CancellationToken cancellationToken)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        var parent = Path.GetDirectoryName(request.SourcePlugin.Value);
        if (parent is null) diagnostics.Add(new Diagnostic("armor-proposal-source-parent", DiagnosticSeverity.Error,
            "The source plugin must have a parent Data directory."));
        else diagnostics.AddRange(policy.EvaluateReadRoot(labRoot, request.SourcePlugin));
        diagnostics.AddRange(ValidateDestination(request.OutputProposal));
        if (!File.Exists(request.SourcePlugin.Value)) diagnostics.Add(new Diagnostic("armor-proposal-source-missing",
            DiagnosticSeverity.Error, "The source plugin does not exist."));
        else
        {
            try
            {
                if ((File.GetAttributes(request.SourcePlugin.Value) & FileAttributes.ReparsePoint) != 0)
                    diagnostics.Add(new Diagnostic("armor-proposal-source-reparse", DiagnosticSeverity.Error,
                        "The source plugin may not be a reparse point."));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { diagnostics.Add(new Diagnostic("armor-proposal-source-attributes-failed", DiagnosticSeverity.Error, exception.Message)); }
        }
        if (request.SourceFormId.Value == 0) diagnostics.Add(new Diagnostic("armor-proposal-source-form-invalid",
            DiagnosticSeverity.Error, "The source ARMO FormID may not be null."));
        if (request.Mode is not (ArmorProposalMode.New or ArmorProposalMode.Override))
            diagnostics.Add(new Diagnostic("armor-proposal-mode-invalid", DiagnosticSeverity.Error,
                "Armor proposal mode must be new or override."));
        if (request.Mode == ArmorProposalMode.New && request.EditorId is null)
            diagnostics.Add(new Diagnostic("armor-proposal-editor-id-required", DiagnosticSeverity.Error,
                "A new armor proposal requires an explicit EditorID."));
        if (request.Mode == ArmorProposalMode.Override && request.EditorId is not null)
            diagnostics.Add(new Diagnostic("armor-proposal-override-editor-id", DiagnosticSeverity.Error,
                "Override proposals inherit the source EditorID and may not supply a replacement."));
        if (request.Mode == ArmorProposalMode.New && (request.TargetFormId is null || request.TargetFormId.Value.Value == 0 || request.TargetFormId.Value.Value > 0x00FF_FFFF))
            diagnostics.Add(new Diagnostic("armor-proposal-new-target-invalid", DiagnosticSeverity.Error,
                "New armor proposals require a nonzero plugin-local 24-bit target FormID."));
        if (request.Mode == ArmorProposalMode.Override && request.TargetFormId is not null && request.TargetFormId != request.SourceFormId)
            diagnostics.Add(new Diagnostic("armor-proposal-override-target-invalid", DiagnosticSeverity.Error,
                "Override armor proposals must target the source FormID."));
        ValidatePatch(request, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics);

        Sha256Hash inputHash;
        try { inputHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(request.SourcePlugin.Value, cancellationToken)))); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { diagnostics.Add(new Diagnostic("armor-proposal-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics); }

        PluginInspection inspection;
        try { inspection = await pluginReader.ReadAsync(new PluginReadRequest(request.Edition, request.SourcePlugin), cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception)
        { diagnostics.Add(new Diagnostic("armor-proposal-source-read-failed", DiagnosticSeverity.Error, exception.Message)); return Refused(diagnostics, inputHash); }
        var matches = inspection.Records.Where(record => record.FormId == request.SourceFormId &&
            string.Equals(record.Signature, "ARMO", StringComparison.Ordinal)).ToArray();
        if (matches.Length == 0) diagnostics.Add(new Diagnostic("armor-proposal-source-not-found", DiagnosticSeverity.Error,
            $"ARMO source record {request.SourceFormId} was not found in the source plugin."));
        if (matches.Length > 1) diagnostics.Add(new Diagnostic("armor-proposal-source-duplicate", DiagnosticSeverity.Error,
            $"ARMO source record {request.SourceFormId} is duplicated in the source plugin."));
        if (request.Mode == ArmorProposalMode.Override && matches.Length == 1 && string.IsNullOrWhiteSpace(matches[0].EditorId))
            diagnostics.Add(new Diagnostic("armor-proposal-source-editor-id-missing", DiagnosticSeverity.Error,
                "Override proposals require the source ARMO EditorID."));
        var sourcePlugin = new PluginName(Path.GetFileName(request.SourcePlugin.Value));
        var allowed = inspection.Masters.Append(sourcePlugin).ToImmutableHashSet();
        ValidateReferences(request, allowed, diagnostics);
        if (HasErrors(diagnostics)) return Refused(diagnostics, inputHash);

        var editorId = request.Mode == ArmorProposalMode.Override ? matches[0].EditorId! : request.EditorId!.Value.Value;
        var patchBytes = JsonSerializer.SerializeToUtf8Bytes(request, JsonOptions);
        var changed = ChangedFields(request);
        var artifact = new ArmorProposalArtifact("1", "armor-record-proposal", request.Edition.ToWireName(), request.Mode,
            request.SourcePlugin.Value, request.SourceFormId.ToString(), editorId, inputHash.Value,
            Convert.ToHexString(SHA256.HashData(patchBytes)), request.Name, request.SlotMask, request.Race?.ToString(),
            request.MaleWorldModel, request.FemaleWorldModel, request.Value, request.Weight, request.Health,
            request.ArmorRating, request.Keywords?.Select(item => item.ToString()).ToImmutableArray() ?? [],
            request.ArmorAddons?.Select(item => new ArmorAddonArtifact(item.Index, item.Addon.ToString())).ToImmutableArray() ?? [],
            changed, inspection.Masters.Select(item => item.Value).ToImmutableArray(), true,
            request.TargetFormId?.ToString(), request.Description, request.NonPlayable,
            request.Enchantment?.ToString(), request.PickupSound?.ToString(),
            request.DropSound?.ToString(), request.EquipmentType?.ToString(),
            request.AlternateBlockMaterial?.ToString(), request.TemplateArmor?.ToString(),
            request.ObjectBounds, request.CompleteDocument);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(artifact, JsonOptions);
        var temporary = request.OutputProposal.Value + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        try
        {
            await File.WriteAllBytesAsync(temporary, bytes, cancellationToken);
            File.Move(temporary, request.OutputProposal.Value, overwrite: false);
            return new ArmorProposalResult(true, artifact,
                new Sha256Hash(Convert.ToHexString(SHA256.HashData(bytes))), diagnostics.ToImmutable());
        }
        catch (OperationCanceledException) { TryDelete(temporary); throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { TryDelete(temporary); diagnostics.Add(new Diagnostic("armor-proposal-write-failed", DiagnosticSeverity.Error, exception.Message)); return new ArmorProposalResult(false, artifact, null, diagnostics.ToImmutable()); }
    }

    private static void ValidatePatch(ArmorProposalRequest request, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Name is { Length: > 4096 }) diagnostics.Add(new Diagnostic("armor-proposal-name-too-long", DiagnosticSeverity.Error,
            "Armor names may not exceed 4096 characters."));
        if (request.Description is { Length: > 4096 } ||
            request.Description?.Any(character => char.IsControl(character) && character is not ('\r' or '\n' or '\t')) == true)
            diagnostics.Add(new Diagnostic("armor-proposal-description-invalid", DiagnosticSeverity.Error,
                "Armor descriptions may not exceed 4096 characters or contain unsupported controls."));
        ValidateAssetPath(request.MaleWorldModel, "male-world-model", diagnostics);
        ValidateAssetPath(request.FemaleWorldModel, "female-world-model", diagnostics);
        if (request.Value is { } value && (request.Edition == GameEdition.SkyrimSpecialEdition
                ? value is < 0 or > uint.MaxValue
                : value is < int.MinValue or > int.MaxValue))
            diagnostics.Add(new Diagnostic("armor-proposal-value-range", DiagnosticSeverity.Error,
                "Armor value is outside the selected game's serialized range."));
        if (request.Weight is { } weight && (!double.IsFinite(weight) || weight < 0 || weight > 1_000_000))
            diagnostics.Add(new Diagnostic("armor-proposal-weight-range", DiagnosticSeverity.Error,
                "Armor weight must be finite and within 0..1000000."));
        if (request.ArmorRating is { } rating && (!double.IsFinite(rating) || rating < 0 || rating > 65535))
            diagnostics.Add(new Diagnostic("armor-proposal-rating-range", DiagnosticSeverity.Error,
                "Armor rating must be finite and within 0..65535."));
        if (request.Edition == GameEdition.SkyrimSpecialEdition && request.Health is not null)
            diagnostics.Add(new Diagnostic("armor-proposal-health-unsupported", DiagnosticSeverity.Error,
                "Health is a Fallout 4-only ARMO field in this proposal contract."));
        if (request.CompleteDocument && request.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(new Diagnostic("armor-proposal-complete-edition", DiagnosticSeverity.Error,
                "Complete armor documents are currently supported for Skyrim Special Edition only."));
        if (request.Edition == GameEdition.Fallout4 &&
            (request.Description is not null || request.NonPlayable is not null ||
             request.Enchantment is not null || request.PickupSound is not null ||
             request.DropSound is not null || request.EquipmentType is not null ||
             request.AlternateBlockMaterial is not null || request.TemplateArmor is not null ||
             request.ObjectBounds is not null))
            diagnostics.Add(new Diagnostic("armor-proposal-skyrim-fields-on-fallout", DiagnosticSeverity.Error,
                "The extended Armor editor fields are bounded to Skyrim Special Edition."));
        if (request.CompleteDocument &&
            (request.Name is null || request.SlotMask is null || request.Race is null ||
             request.Value is null || request.Weight is null || request.ArmorRating is null ||
             request.Keywords is null || request.ArmorAddons is null ||
             request.Description is null || request.NonPlayable is null || request.ObjectBounds is null))
            diagnostics.Add(new Diagnostic("armor-proposal-complete-fields", DiagnosticSeverity.Error,
                "A complete Skyrim armor document must initialize every required scalar and collection field."));
        if (request.Edition == GameEdition.Fallout4 && request.ArmorRating is { } falloutRating &&
            falloutRating != Math.Truncate(falloutRating))
            diagnostics.Add(new Diagnostic("armor-proposal-rating-integral", DiagnosticSeverity.Error,
                "Fallout 4 armor rating must be an integer."));
        if (request.Keywords is { } keywords && (keywords.IsDefault || keywords.Length > 255 ||
            keywords.Any(item => item.FormId.Value == 0) ||
            keywords.Select(ReferenceKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() != keywords.Length)) diagnostics.Add(new Diagnostic("armor-proposal-keywords-invalid",
                DiagnosticSeverity.Error, "Armor keywords must be non-null, unique FormReferences."));
        if (request.ArmorAddons is { } addons && (addons.IsDefault || addons.Length > 255 ||
            addons.Any(item => item.Addon.FormId.Value == 0))) diagnostics.Add(new Diagnostic("armor-proposal-addons-invalid",
                DiagnosticSeverity.Error, "Armor addons must be initialized, bounded, non-null FormReferences."));
        if (request.Edition == GameEdition.SkyrimSpecialEdition && request.CompleteDocument &&
            request.ArmorAddons is { } skyrimAddons && skyrimAddons.Any(item => item.Index != 0))
            diagnostics.Add(new Diagnostic("armor-proposal-skyrim-addon-index", DiagnosticSeverity.Error,
                "Skyrim ARMO armatures have no per-row addon index; complete documents must use zero."));
        if (request.ObjectBounds is { } bounds &&
            (bounds.MinimumX > bounds.MaximumX || bounds.MinimumY > bounds.MaximumY ||
             bounds.MinimumZ > bounds.MaximumZ))
            diagnostics.Add(new Diagnostic("armor-proposal-bounds-invalid", DiagnosticSeverity.Error,
                "Each object-bounds minimum must not exceed its corresponding maximum."));
        if (ChangedFields(request).IsDefaultOrEmpty) diagnostics.Add(new Diagnostic("armor-proposal-empty-patch", DiagnosticSeverity.Error,
            "The armor proposal must contain at least one supported field change."));
    }

    private static void ValidateAssetPath(string? value, string field, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            var path = new AssetPath(value);
            if (!path.Value.EndsWith(".nif", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("World models must use the .nif extension.");
        }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic($"armor-proposal-{field}-invalid", DiagnosticSeverity.Error, exception.Message)); }
    }

    private static void ValidateReferences(ArmorProposalRequest request, ImmutableHashSet<PluginName> allowed,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var references = new List<FormReference>();
        if (request.Race is { } race) references.Add(race);
        if (request.Enchantment is { } enchantment) references.Add(enchantment);
        if (request.PickupSound is { } pickup) references.Add(pickup);
        if (request.DropSound is { } drop) references.Add(drop);
        if (request.EquipmentType is { } equipmentType) references.Add(equipmentType);
        if (request.AlternateBlockMaterial is { } blockMaterial) references.Add(blockMaterial);
        if (request.TemplateArmor is { } templateArmor) references.Add(templateArmor);
        if (request.Keywords is { } keywords) references.AddRange(keywords);
        if (request.ArmorAddons is { } addons) references.AddRange(addons.Select(item => item.Addon));
        foreach (var reference in references)
        {
            if (reference.FormId.Value == 0) diagnostics.Add(new Diagnostic("armor-proposal-reference-null", DiagnosticSeverity.Error,
                $"Armor reference {reference} uses the null FormID."));
            if (!allowed.Contains(reference.Plugin)) diagnostics.Add(new Diagnostic("armor-proposal-master-unresolved", DiagnosticSeverity.Error,
                $"Armor reference {reference} is not provided by the source plugin or its masters."));
        }
    }

    private static ImmutableArray<string> ChangedFields(ArmorProposalRequest request)
    {
        var fields = ImmutableArray.CreateBuilder<string>();
        if (request.Name is not null) fields.Add("name");
        if (request.SlotMask is not null) fields.Add("slotMask");
        if (request.Race is not null) fields.Add("race");
        if (request.MaleWorldModel is not null) fields.Add("maleWorldModel");
        if (request.FemaleWorldModel is not null) fields.Add("femaleWorldModel");
        if (request.Value is not null) fields.Add("value");
        if (request.Weight is not null) fields.Add("weight");
        if (request.Health is not null) fields.Add("health");
        if (request.ArmorRating is not null) fields.Add("armorRating");
        if (request.Keywords is not null) fields.Add("keywords");
        if (request.ArmorAddons is not null) fields.Add("armorAddons");
        if (request.Description is not null) fields.Add("description");
        if (request.NonPlayable is not null) fields.Add("nonPlayable");
        if (request.Enchantment is not null) fields.Add("enchantment");
        if (request.PickupSound is not null) fields.Add("pickupSound");
        if (request.DropSound is not null) fields.Add("dropSound");
        if (request.EquipmentType is not null) fields.Add("equipmentType");
        if (request.AlternateBlockMaterial is not null) fields.Add("alternateBlockMaterial");
        if (request.TemplateArmor is not null) fields.Add("templateArmor");
        if (request.ObjectBounds is not null) fields.Add("objectBounds");
        if (request.CompleteDocument)
        {
            string[] completeFields =
            [
                "name", "slotMask", "race", "maleWorldModel", "femaleWorldModel",
                "value", "weight", "armorRating", "keywords", "armorAddons",
                "description", "nonPlayable", "enchantment", "pickupSound",
                "dropSound", "equipmentType", "alternateBlockMaterial",
                "templateArmor", "objectBounds"
            ];
            foreach (string field in completeFields)
                if (!fields.Contains(field, StringComparer.Ordinal)) fields.Add(field);
        }
        return fields.ToImmutable();
    }

    private static string ReferenceKey(FormReference reference) =>
        $"{reference.Plugin.Value}|{reference.FormId.Value:X6}";

    private ImmutableArray<Diagnostic> ValidateDestination(WorkspacePath output)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!output.IsUnder(labRoot)) diagnostics.Add(new Diagnostic("armor-proposal-output-outside-lab", DiagnosticSeverity.Error,
            "Armor proposals must remain under the K-only lab root."));
        if (!output.Value.EndsWith(".armor-proposal.json", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("armor-proposal-output-extension", DiagnosticSeverity.Error,
            "Armor proposals must use the .armor-proposal.json extension."));
        if (File.Exists(output.Value)) diagnostics.Add(new Diagnostic("armor-proposal-output-exists", DiagnosticSeverity.Error,
            "Armor proposals never overwrite an existing artifact."));
        var parent = Path.GetDirectoryName(output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("armor-proposal-output-parent-missing", DiagnosticSeverity.Error,
            "The armor proposal output directory must already exist."));
        else diagnostics.AddRange(policy.Evaluate(labRoot, new WorkspacePath(parent)));
        return diagnostics.ToImmutable();
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ArmorProposalResult Refused(ImmutableArray<Diagnostic>.Builder diagnostics, Sha256Hash? hash = null) => new(false, null, null, diagnostics.ToImmutable());
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }
}
