using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Mutagen.Bethesda.Fallout4;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using Fo4 = Mutagen.Bethesda.Fallout4;
using Sse = Mutagen.Bethesda.Skyrim;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Materializes one hash-bound ARMO proposal into a new ordinary plugin.
/// Overrides begin from a deep copy of the source record, so unspecified fields
/// remain intact; new records use only explicitly approved proposal fields.
/// </summary>
public sealed class BethesdaArmorBinaryWriteService : IArmorBinaryWriteService
{
    private readonly IWorkspacePolicy _policy;
    private readonly WorkspacePath _labRoot;
    private readonly IArmorBinaryFileOperations _fileOperations;

    public BethesdaArmorBinaryWriteService(
        IWorkspacePolicy policy,
        WorkspacePath labRoot)
        : this(policy, labRoot, new ArmorBinaryFileOperations())
    {
    }

    internal BethesdaArmorBinaryWriteService(
        IWorkspacePolicy policy,
        WorkspacePath labRoot,
        IArmorBinaryFileOperations fileOperations)
    {
        _policy = policy;
        _labRoot = labRoot;
        _fileOperations = fileOperations;
    }

    private const long MaximumProposalBytes = 1_048_576;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public async ValueTask<ArmorBinaryWriteResult> WriteAsync(ArmorBinaryWriteRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var diagnostics = ValidatePaths(request).ToBuilder();
        if (HasErrors(diagnostics)) return Refused(request, null, diagnostics.ToImmutable());
        ArmorProposalArtifact? proposal;
        try
        {
            var info = new FileInfo(request.Proposal.Value);
            if (info.Length <= 0 || info.Length > MaximumProposalBytes)
                throw new InvalidDataException("The armor proposal is empty or exceeds the size limit.");
            await using var stream = new FileStream(request.Proposal.Value, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
            proposal = await JsonSerializer.DeserializeAsync<ArmorProposalArtifact>(stream, JsonOptions, cancellationToken);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException or InvalidDataException)
        {
            diagnostics.Add(new Diagnostic("armor-binary-proposal-read-failed", DiagnosticSeverity.Error, exception.Message));
            return Refused(request, null, diagnostics.ToImmutable());
        }
        if (proposal is null)
        {
            diagnostics.Add(new Diagnostic("armor-binary-proposal-empty", DiagnosticSeverity.Error, "The armor proposal must contain a JSON object."));
            return Refused(request, null, diagnostics.ToImmutable());
        }
        if (!string.Equals(proposal.ArtifactKind, "armor-record-proposal", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("armor-binary-proposal-kind", DiagnosticSeverity.Error, "The proposal artifact kind must be armor-record-proposal."));
        ValidateProposalArtifact(proposal, request.Edition, diagnostics);
        if (!GameEditionExtensions.TryParseWireName(proposal.Edition, out var edition) || edition != request.Edition)
            diagnostics.Add(new Diagnostic("armor-binary-proposal-edition", DiagnosticSeverity.Error, "The proposal edition does not match the write request."));
        if (!Enum.IsDefined(proposal.Mode))
            diagnostics.Add(new Diagnostic("armor-binary-proposal-mode", DiagnosticSeverity.Error, "The proposal mode is invalid."));
        if (!FormId.TryParse(proposal.SourceFormId, out var sourceFormId) || sourceFormId.Value == 0)
            diagnostics.Add(new Diagnostic("armor-binary-source-form", DiagnosticSeverity.Error, "The proposal source FormID is invalid."));
        if (!string.IsNullOrWhiteSpace(proposal.TargetFormId) && (!FormId.TryParse(proposal.TargetFormId, out var parsedTarget) || parsedTarget.Value == 0 || parsedTarget.Value > 0x00FF_FFFF))
            diagnostics.Add(new Diagnostic("armor-binary-target-form", DiagnosticSeverity.Error, "The proposal target FormID is invalid."));
        if (string.IsNullOrWhiteSpace(proposal.EditorId))
            diagnostics.Add(new Diagnostic("armor-binary-editor-id", DiagnosticSeverity.Error, "The proposal EditorID is required."));
        else
        {
            try { _ = new EditorId(proposal.EditorId); }
            catch (ArgumentException) { diagnostics.Add(new Diagnostic("armor-binary-editor-id", DiagnosticSeverity.Error, "The proposal EditorID is invalid.")); }
        }
        var sourcePath = TryWorkspacePath(proposal.SourcePlugin, diagnostics, "source");
        if (sourcePath is null || !File.Exists(sourcePath.Value.Value))
            diagnostics.Add(new Diagnostic("armor-binary-source-missing", DiagnosticSeverity.Error, "The proposal source plugin does not exist under the K-only lab root."));
        else
        {
            AddReparseDiagnostic(diagnostics, sourcePath.Value.Value, "source");
            if (!string.Equals(Path.GetFileName(sourcePath.Value.Value), Path.GetFileName(proposal.SourcePlugin), StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("armor-binary-source-name-mismatch", DiagnosticSeverity.Error, "The proposal source plugin filename must match the source path filename."));
        }
        var sourcePlugin = TryPluginName(proposal.SourcePlugin, diagnostics);
        var targetFormId = ResolveTarget(proposal, sourceFormId, diagnostics);
        var references = ParseReferences(proposal, diagnostics);
        if (sourcePath is null || sourcePlugin is null || targetFormId is null || HasErrors(diagnostics))
            return Refused(request, targetFormId, diagnostics.ToImmutable());

        string? temporary = null;
        bool outputCommitted = false;
        try
        {
            var sourceHash = new Sha256Hash(Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(sourcePath.Value.Value, cancellationToken))));
            if (!string.Equals(sourceHash.Value, proposal.InputSha256, StringComparison.OrdinalIgnoreCase))
                diagnostics.Add(new Diagnostic("armor-binary-input-hash-mismatch", DiagnosticSeverity.Error, "The proposal source hash does not match the current source plugin."));
            var outputModKey = ToModKey(request.Output.Value);
            if (outputModKey == sourcePlugin.Value)
                diagnostics.Add(new Diagnostic("armor-binary-output-master-self", DiagnosticSeverity.Error, "The output plugin may not be the same plugin as the proposal source."));
            if (HasErrors(diagnostics)) return Refused(request, targetFormId, diagnostics.ToImmutable());
            temporary = request.Output.Value + ".tmp-" + Guid.NewGuid().ToString("N");
            switch (request.Edition)
            {
                case GameEdition.Fallout4:
                    WriteFallout4(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, references, temporary);
                    VerifyFallout4(temporary, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, references, diagnostics);
                    break;
                case GameEdition.SkyrimSpecialEdition:
                    WriteSkyrim(sourcePath.Value.Value, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, references, temporary);
                    VerifySkyrim(temporary, sourcePlugin.Value, outputModKey, proposal, sourceFormId, targetFormId.Value, references, diagnostics);
                    break;
                default:
                    diagnostics.Add(new Diagnostic("armor-binary-edition-unsupported", DiagnosticSeverity.Error, "The requested game edition is unsupported."));
                    break;
            }
            if (HasErrors(diagnostics))
            {
                AddTemporaryCleanupDiagnostic(diagnostics, temporary);
                return Refused(request, targetFormId, diagnostics.ToImmutable());
            }

            _fileOperations.CommitNoOverwrite(temporary, request.Output.Value);
            outputCommitted = true;
            Sha256Hash outputHash = await _fileOperations.HashAsync(
                request.Output.Value,
                cancellationToken).ConfigureAwait(false);
            return new ArmorBinaryWriteResult(
                true,
                request.Proposal,
                request.Output,
                targetFormId,
                outputHash,
                diagnostics.ToImmutable());
        }
        catch (OperationCanceledException exception)
        {
            AttachCancellationCleanupFailures(
                exception,
                request.Output.Value,
                temporary,
                outputCommitted);
            throw;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException or ArgumentException or InvalidOperationException or KeyNotFoundException)
        {
            diagnostics.Add(new Diagnostic("armor-binary-write-failed", DiagnosticSeverity.Error, exception.Message));
            AddFailureCleanupDiagnostics(
                diagnostics,
                request.Output.Value,
                temporary,
                outputCommitted);
            return Refused(request, targetFormId, diagnostics.ToImmutable());
        }
    }

    private static FormId? ResolveTarget(ArmorProposalArtifact proposal, FormId source, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (proposal.Mode == ArmorProposalMode.Override)
        {
            if (proposal.TargetFormId is not null && (!FormId.TryParse(proposal.TargetFormId, out var target) || target != source))
                diagnostics.Add(new Diagnostic("armor-binary-override-target", DiagnosticSeverity.Error, "Override proposals must target the source FormID."));
            return source;
        }
        if (!FormId.TryParse(proposal.TargetFormId ?? string.Empty, out var newTarget) || newTarget.Value == 0 || newTarget.Value > 0x00FF_FFFF)
            diagnostics.Add(new Diagnostic("armor-binary-new-target", DiagnosticSeverity.Error, "New armor proposals require a nonzero plugin-local 24-bit target FormID."));
        return newTarget.Value == 0 ? null : newTarget;
    }

    private static void ValidateProposalArtifact(
        ArmorProposalArtifact proposal,
        GameEdition edition,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!string.Equals(proposal.SchemaVersion, "1", StringComparison.Ordinal))
            diagnostics.Add(new Diagnostic("armor-binary-schema", DiagnosticSeverity.Error,
                "The armor proposal schema version is unsupported."));
        if (proposal.ChangedFields.IsDefault ||
            proposal.ChangedFields.Any(string.IsNullOrWhiteSpace) ||
            proposal.ChangedFields.Distinct(StringComparer.Ordinal).Count() != proposal.ChangedFields.Length)
            diagnostics.Add(new Diagnostic("armor-binary-changed-fields", DiagnosticSeverity.Error,
                "Changed fields must be an initialized, unique list."));
        if (proposal.Keywords.IsDefault || proposal.ArmorAddons.IsDefault)
            diagnostics.Add(new Diagnostic("armor-binary-collections", DiagnosticSeverity.Error,
                "Proposal keyword and armor-addon collections must be initialized."));
        if (proposal.Value is { } value && (edition == GameEdition.SkyrimSpecialEdition
                ? value is < 0 or > uint.MaxValue
                : value is < int.MinValue or > int.MaxValue))
            diagnostics.Add(new Diagnostic("armor-binary-value-range", DiagnosticSeverity.Error,
                "Armor value is outside the selected game's serialized range."));
        if (proposal.Weight is { } weight && (!double.IsFinite(weight) || weight is < 0 or > 1_000_000))
            diagnostics.Add(new Diagnostic("armor-binary-weight-range", DiagnosticSeverity.Error,
                "Armor weight is outside the supported range."));
        if (proposal.ArmorRating is { } rating && (!double.IsFinite(rating) || rating is < 0 or > 65_535))
            diagnostics.Add(new Diagnostic("armor-binary-rating-range", DiagnosticSeverity.Error,
                "Armor rating is outside the supported range."));
        if (proposal.ObjectBounds is { } bounds &&
            (bounds.MinimumX > bounds.MaximumX || bounds.MinimumY > bounds.MaximumY ||
             bounds.MinimumZ > bounds.MaximumZ))
            diagnostics.Add(new Diagnostic("armor-binary-bounds", DiagnosticSeverity.Error,
                "Object-bounds minima may not exceed maxima."));
        if (!proposal.CompleteDocument) return;

        string[] requiredFields =
        [
            "name", "slotMask", "race", "maleWorldModel", "femaleWorldModel",
            "value", "weight", "armorRating", "keywords", "armorAddons",
            "description", "nonPlayable", "enchantment", "pickupSound",
            "dropSound", "equipmentType", "alternateBlockMaterial",
            "templateArmor", "objectBounds"
        ];
        if (edition != GameEdition.SkyrimSpecialEdition ||
            proposal.Name is null || proposal.SlotMask is null || proposal.Race is null ||
            proposal.Value is null || proposal.Weight is null || proposal.ArmorRating is null ||
            proposal.Description is null || proposal.NonPlayable is null ||
            proposal.ObjectBounds is null ||
            requiredFields.Any(field => !proposal.ChangedFields.Contains(field, StringComparer.Ordinal)))
            diagnostics.Add(new Diagnostic("armor-binary-complete-document", DiagnosticSeverity.Error,
                "A complete Skyrim armor proposal must initialize and mark every required field."));
        if (proposal.ArmorAddons.Any(item => item.Index != 0))
            diagnostics.Add(new Diagnostic("armor-binary-skyrim-addon-index", DiagnosticSeverity.Error,
                "Skyrim complete armor documents may not invent per-row addon indexes."));
    }

    private static ImmutableArray<FormReference> ParseReferences(ArmorProposalArtifact proposal, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var references = ImmutableArray.CreateBuilder<FormReference>();
        if (proposal.Race is not null) ParseReference(proposal.Race, "race", references, diagnostics);
        if (proposal.Enchantment is not null) ParseReference(proposal.Enchantment, "enchantment", references, diagnostics);
        if (proposal.PickupSound is not null) ParseReference(proposal.PickupSound, "pickup sound", references, diagnostics);
        if (proposal.DropSound is not null) ParseReference(proposal.DropSound, "drop sound", references, diagnostics);
        if (proposal.EquipmentType is not null) ParseReference(proposal.EquipmentType, "equipment type", references, diagnostics);
        if (proposal.AlternateBlockMaterial is not null) ParseReference(proposal.AlternateBlockMaterial, "alternate block material", references, diagnostics);
        if (proposal.TemplateArmor is not null) ParseReference(proposal.TemplateArmor, "template armor", references, diagnostics);
        foreach (var value in proposal.Keywords) ParseReference(value, "keyword", references, diagnostics);
        foreach (var addon in proposal.ArmorAddons) ParseReference(addon.Addon, "armor-addon", references, diagnostics);
        return references.ToImmutable();
    }

    private static void ParseReference(string value, string role, ImmutableArray<FormReference>.Builder references, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!FormReference.TryParse(value, out var reference) || reference.FormId.Value == 0)
            diagnostics.Add(new Diagnostic("armor-binary-reference-invalid", DiagnosticSeverity.Error, $"The {role} reference '{value}' is invalid."));
        else references.Add(reference);
    }

    private static void WriteFallout4(string sourcePath, ModKey sourceModKey, ModKey outputModKey, ArmorProposalArtifact proposal,
        FormId sourceFormId, FormId targetFormId, ImmutableArray<FormReference> references, string destination)
    {
        using var overlay = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(sourceModKey, new FilePath(sourcePath)), Fo4.Fallout4Release.Fallout4);
        var source = overlay.Armors.FirstOrDefault(armor => armor.FormKey == new FormKey(sourceModKey, sourceFormId.Value));
        if (source is null) throw new InvalidDataException($"ARMO source record {proposal.SourceFormId} was not found in the source plugin.");
        var sourceMasters = overlay.ModHeader.MasterReferences.Select(master => master.Master).ToImmutableArray();
        ValidateMasters(references, sourceModKey, sourceMasters);
        var formKey = proposal.Mode == ArmorProposalMode.Override ? source.FormKey : new FormKey(outputModKey, targetFormId.Value);
        var armor = proposal.Mode == ArmorProposalMode.Override ? source.DeepCopy() : new Fo4.Armor(formKey, Fo4.Fallout4Release.Fallout4);
        ApplyFallout4(armor, proposal);
        var mod = new Fo4.Fallout4Mod(outputModKey, Fo4.Fallout4Release.Fallout4);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, references, outputModKey,
            proposal.Mode == ArmorProposalMode.Override ? sourceMasters : []);
        mod.Armors.Add(armor);
        WriteMod(mod, destination);
    }

    private static void WriteSkyrim(string sourcePath, ModKey sourceModKey, ModKey outputModKey, ArmorProposalArtifact proposal,
        FormId sourceFormId, FormId targetFormId, ImmutableArray<FormReference> references, string destination)
    {
        using var overlay = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(sourceModKey, new FilePath(sourcePath)), Sse.SkyrimRelease.SkyrimSE);
        var source = overlay.Armors.FirstOrDefault(armor => armor.FormKey == new FormKey(sourceModKey, sourceFormId.Value));
        if (source is null) throw new InvalidDataException($"ARMO source record {proposal.SourceFormId} was not found in the source plugin.");
        var sourceMasters = overlay.ModHeader.MasterReferences.Select(master => master.Master).ToImmutableArray();
        ValidateMasters(references, sourceModKey, sourceMasters);
        var formKey = proposal.Mode == ArmorProposalMode.Override ? source.FormKey : new FormKey(outputModKey, targetFormId.Value);
        var armor = proposal.Mode == ArmorProposalMode.Override ? source.DeepCopy() : new Sse.Armor(formKey, Sse.SkyrimRelease.SkyrimSE);
        ApplySkyrim(armor, proposal);
        var mod = new Sse.SkyrimMod(outputModKey, Sse.SkyrimRelease.SkyrimSE);
        AddMasters(mod.ModHeader.MasterReferences, sourceModKey, references, outputModKey,
            proposal.Mode == ArmorProposalMode.Override ? sourceMasters : []);
        mod.Armors.Add(armor);
        WriteMod(mod, destination);
    }

    private static void ApplyFallout4(Fo4.Armor armor, ArmorProposalArtifact proposal)
    {
        if (proposal.EditorId is not null) armor.EditorID = proposal.EditorId;
        if (proposal.Name is not null) armor.Name = proposal.Name;
        if (proposal.Value is { } value) armor.Value = checked((int)value);
        if (proposal.Weight is { } weight) armor.Weight = checked((float)weight);
        if (proposal.Health is { } health) armor.Health = health;
        if (proposal.ArmorRating is { } rating) armor.ArmorRating = checked((ushort)rating);
        if (proposal.SlotMask is { } slots) armor.BipedBodyTemplate = new Fo4.BipedBodyTemplate { FirstPersonFlags = (Fo4.BipedObjectFlag)slots };
        if (proposal.Race is not null && FormReference.TryParse(proposal.Race, out var race)) armor.Race = new FormLinkNullable<Fo4.IRaceGetter>(ToFormKey(race));
        if (HasChanged(proposal, "keywords")) armor.Keywords = [.. proposal.Keywords.Select(ParseFormKey).Select(key => new FormLink<Fo4.IKeywordGetter>(key))];
        if (HasChanged(proposal, "armorAddons"))
        {
            armor.Armatures.Clear();
            foreach (var addon in proposal.ArmorAddons)
                armor.Armatures.Add(new Fo4.ArmorAddonModel { AddonIndex = addon.Index, ArmorAddon = new FormLinkNullable<Fo4.IArmorAddonGetter>(ParseFormKey(addon.Addon)) });
        }
        if (proposal.MaleWorldModel is not null || proposal.FemaleWorldModel is not null)
            armor.WorldModel = new GenderedItem<Fo4.ArmorModel?>(BuildFo4Model(proposal.MaleWorldModel) ?? armor.WorldModel?.Male, BuildFo4Model(proposal.FemaleWorldModel) ?? armor.WorldModel?.Female);
    }

    private static void ApplySkyrim(Sse.Armor armor, ArmorProposalArtifact proposal)
    {
        if (proposal.EditorId is not null) armor.EditorID = proposal.EditorId;
        if (HasChanged(proposal, "name")) armor.Name = proposal.Name ?? string.Empty;
        if (HasChanged(proposal, "description")) armor.Description = proposal.Description ?? string.Empty;
        if (proposal.Value is { } value) armor.Value = checked((uint)value);
        if (proposal.Weight is { } weight) armor.Weight = checked((float)weight);
        if (proposal.ArmorRating is { } rating) armor.ArmorRating = checked((float)rating);
        if (proposal.SlotMask is { } slots) armor.BodyTemplate = new Sse.BodyTemplate { FirstPersonFlags = (Sse.BipedObjectFlag)slots };
        if (HasChanged(proposal, "nonPlayable") && proposal.NonPlayable is { } nonPlayable)
            armor.MajorFlags = nonPlayable
                ? armor.MajorFlags | Sse.Armor.MajorFlag.NonPlayable
                : armor.MajorFlags & ~Sse.Armor.MajorFlag.NonPlayable;
        if (HasChanged(proposal, "race")) armor.Race = Link<Sse.IRaceGetter>(proposal.Race);
        if (HasChanged(proposal, "enchantment")) armor.ObjectEffect = Link<Sse.IObjectEffectGetter>(proposal.Enchantment);
        if (HasChanged(proposal, "pickupSound")) armor.PickUpSound = Link<Sse.ISoundDescriptorGetter>(proposal.PickupSound);
        if (HasChanged(proposal, "dropSound")) armor.PutDownSound = Link<Sse.ISoundDescriptorGetter>(proposal.DropSound);
        if (HasChanged(proposal, "equipmentType")) armor.EquipmentType = Link<Sse.IEquipTypeGetter>(proposal.EquipmentType);
        if (HasChanged(proposal, "alternateBlockMaterial")) armor.AlternateBlockMaterial = Link<Sse.IMaterialTypeGetter>(proposal.AlternateBlockMaterial);
        if (HasChanged(proposal, "templateArmor")) armor.TemplateArmor = Link<Sse.IArmorGetter>(proposal.TemplateArmor);
        if (HasChanged(proposal, "objectBounds") && proposal.ObjectBounds is { } bounds)
            armor.ObjectBounds = new Sse.ObjectBounds
            {
                First = new P3Int16 { X = bounds.MinimumX, Y = bounds.MinimumY, Z = bounds.MinimumZ },
                Second = new P3Int16 { X = bounds.MaximumX, Y = bounds.MaximumY, Z = bounds.MaximumZ }
            };
        if (HasChanged(proposal, "keywords")) armor.Keywords = [.. proposal.Keywords.Select(ParseFormKey).Select(key => new FormLink<Sse.IKeywordGetter>(key))];
        if (HasChanged(proposal, "armorAddons"))
        {
            armor.Armature.Clear();
            foreach (var addon in proposal.ArmorAddons)
                armor.Armature.Add(new FormLink<Sse.IArmorAddonGetter>(ParseFormKey(addon.Addon)));
        }
        if (HasChanged(proposal, "maleWorldModel") || HasChanged(proposal, "femaleWorldModel"))
            armor.WorldModel = new GenderedItem<Sse.ArmorModel?>(
                ApplySseModel(proposal, "maleWorldModel", proposal.MaleWorldModel, armor.WorldModel?.Male),
                ApplySseModel(proposal, "femaleWorldModel", proposal.FemaleWorldModel, armor.WorldModel?.Female));
    }

    private static Fo4.ArmorModel? BuildFo4Model(string? path) => path is null ? null : new Fo4.ArmorModel { Model = new Fo4.Model { File = path } };
    private static Sse.ArmorModel? BuildSseModel(string? path) => path is null ? null : new Sse.ArmorModel { Model = new Sse.Model { File = path } };

    private static Sse.ArmorModel? ApplySseModel(ArmorProposalArtifact proposal, string field,
        string? path, Sse.ArmorModel? existing) =>
        !HasChanged(proposal, field)
            ? existing
            : string.IsNullOrWhiteSpace(path)
                ? null
                : BuildSseModel(path);

    private static FormLinkNullable<T> Link<T>(string? value)
        where T : class, IMajorRecordGetter =>
        value is null ? new FormLinkNullable<T>() : new FormLinkNullable<T>(ParseFormKey(value));

    private static void VerifyFallout4(string path, ModKey sourceModKey, ModKey outputModKey, ArmorProposalArtifact proposal,
        FormId sourceFormId, FormId targetFormId, ImmutableArray<FormReference> references, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Fo4.Fallout4Mod.CreateFromBinaryOverlay(new ModPath(outputModKey, new FilePath(path)), Fo4.Fallout4Release.Fallout4);
        var expectedKey = proposal.Mode == ArmorProposalMode.Override ? new FormKey(sourceModKey, sourceFormId.Value) : new FormKey(outputModKey, targetFormId.Value);
        var records = mod.Armors.ToArray();
        var armor = records.FirstOrDefault(item => item.FormKey == expectedKey);
        if (records.Length != 1 || armor is null || !string.Equals(armor.EditorID, proposal.EditorId, StringComparison.Ordinal) || !MatchesFallout4(armor, proposal))
            diagnostics.Add(new Diagnostic("armor-binary-readback-mismatch", DiagnosticSeverity.Error, "Independent FO4 ARMO read-back did not match the proposal."));
    }

    private static void VerifySkyrim(string path, ModKey sourceModKey, ModKey outputModKey, ArmorProposalArtifact proposal,
        FormId sourceFormId, FormId targetFormId, ImmutableArray<FormReference> references, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        using var mod = Sse.SkyrimMod.CreateFromBinaryOverlay(new ModPath(outputModKey, new FilePath(path)), Sse.SkyrimRelease.SkyrimSE);
        var expectedKey = proposal.Mode == ArmorProposalMode.Override ? new FormKey(sourceModKey, sourceFormId.Value) : new FormKey(outputModKey, targetFormId.Value);
        var records = mod.Armors.ToArray();
        var armor = records.FirstOrDefault(item => item.FormKey == expectedKey);
        if (records.Length != 1 || armor is null || !string.Equals(armor.EditorID, proposal.EditorId, StringComparison.Ordinal) || !MatchesSkyrim(armor, proposal))
            diagnostics.Add(new Diagnostic("armor-binary-readback-mismatch", DiagnosticSeverity.Error, "Independent Skyrim SE ARMO read-back did not match the proposal."));
    }

    private static bool MatchesFallout4(Fo4.IArmorGetter armor, ArmorProposalArtifact proposal) =>
        (proposal.Name is null || armor.Name?.String == proposal.Name) &&
        (proposal.SlotMask is null || armor.BipedBodyTemplate?.FirstPersonFlags == (Fo4.BipedObjectFlag)proposal.SlotMask) &&
        (proposal.Race is null || armor.Race.FormKey == ParseFormKey(proposal.Race)) &&
        (proposal.MaleWorldModel is null || armor.WorldModel?.Male?.Model?.File == proposal.MaleWorldModel) &&
        (proposal.FemaleWorldModel is null || armor.WorldModel?.Female?.Model?.File == proposal.FemaleWorldModel) &&
        (proposal.Value is null || armor.Value == proposal.Value) && (proposal.Health is null || armor.Health == proposal.Health) &&
        (proposal.Weight is null || Math.Abs(armor.Weight - proposal.Weight.Value) < 0.0001f) &&
        (proposal.ArmorRating is null || armor.ArmorRating == proposal.ArmorRating) &&
        (!HasChanged(proposal, "keywords") || armor.Keywords is not null && armor.Keywords.Select(item => item.FormKey).SequenceEqual(proposal.Keywords.Select(ParseFormKey))) &&
        (!HasChanged(proposal, "armorAddons") || armor.Armatures.Select(item => item.ArmorAddon.FormKey).SequenceEqual(proposal.ArmorAddons.Select(item => ParseFormKey(item.Addon))));

    private static bool MatchesSkyrim(Sse.IArmorGetter armor, ArmorProposalArtifact proposal) =>
        (!HasChanged(proposal, "name") || string.Equals(armor.Name?.String ?? string.Empty, proposal.Name ?? string.Empty, StringComparison.Ordinal)) &&
        (!HasChanged(proposal, "description") || string.Equals(armor.Description?.String ?? string.Empty, proposal.Description ?? string.Empty, StringComparison.Ordinal)) &&
        (!HasChanged(proposal, "nonPlayable") || proposal.NonPlayable is { } nonPlayable &&
            armor.MajorFlags.HasFlag(Sse.Armor.MajorFlag.NonPlayable) == nonPlayable) &&
        (proposal.SlotMask is null || armor.BodyTemplate?.FirstPersonFlags == (Sse.BipedObjectFlag)proposal.SlotMask) &&
        MatchesLink(proposal, "race", proposal.Race, armor.Race.FormKey) &&
        MatchesLink(proposal, "enchantment", proposal.Enchantment, armor.ObjectEffect.FormKey) &&
        MatchesLink(proposal, "pickupSound", proposal.PickupSound, armor.PickUpSound.FormKey) &&
        MatchesLink(proposal, "dropSound", proposal.DropSound, armor.PutDownSound.FormKey) &&
        MatchesLink(proposal, "equipmentType", proposal.EquipmentType, armor.EquipmentType.FormKey) &&
        MatchesLink(proposal, "alternateBlockMaterial", proposal.AlternateBlockMaterial, armor.AlternateBlockMaterial.FormKey) &&
        MatchesLink(proposal, "templateArmor", proposal.TemplateArmor, armor.TemplateArmor.FormKey) &&
        MatchesModel(proposal, "maleWorldModel", proposal.MaleWorldModel, armor.WorldModel?.Male?.Model?.File) &&
        MatchesModel(proposal, "femaleWorldModel", proposal.FemaleWorldModel, armor.WorldModel?.Female?.Model?.File) &&
        (proposal.Value is null || armor.Value == proposal.Value) && (proposal.Weight is null || Math.Abs(armor.Weight - proposal.Weight.Value) < 0.0001f) &&
        (proposal.ArmorRating is null || Math.Abs(armor.ArmorRating - proposal.ArmorRating.Value) < 0.0001f) &&
        (!HasChanged(proposal, "objectBounds") || proposal.ObjectBounds is { } bounds &&
            armor.ObjectBounds is { } actualBounds &&
            actualBounds.First.X == bounds.MinimumX && actualBounds.First.Y == bounds.MinimumY && actualBounds.First.Z == bounds.MinimumZ &&
            actualBounds.Second.X == bounds.MaximumX && actualBounds.Second.Y == bounds.MaximumY && actualBounds.Second.Z == bounds.MaximumZ) &&
        (!HasChanged(proposal, "keywords") || armor.Keywords is not null && armor.Keywords.Select(item => item.FormKey).SequenceEqual(proposal.Keywords.Select(ParseFormKey))) &&
        (!HasChanged(proposal, "armorAddons") || armor.Armature.Select(item => item.FormKey).SequenceEqual(proposal.ArmorAddons.Select(item => ParseFormKey(item.Addon))));

    private static bool MatchesLink(ArmorProposalArtifact proposal, string field, string? expected, FormKey actual) =>
        !HasChanged(proposal, field) || actual == (expected is null ? FormKey.Null : ParseFormKey(expected));

    private static bool MatchesModel(ArmorProposalArtifact proposal, string field, string? expected, string? actual) =>
        !HasChanged(proposal, field) || string.Equals(actual ?? string.Empty, expected ?? string.Empty, StringComparison.Ordinal);

    private static void ValidateMasters(ImmutableArray<FormReference> references, ModKey source, IEnumerable<ModKey> declaredMasters)
    {
        var allowed = declaredMasters.Append(source).ToHashSet();
        foreach (var reference in references)
        {
            var plugin = new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin);
            if (!allowed.Contains(plugin)) throw new InvalidDataException($"Reference '{reference}' is not provided by the source plugin or its declared masters.");
        }
    }

    private static void AddMasters(ExtendedList<MasterReference> masters, ModKey source,
        ImmutableArray<FormReference> references, ModKey output, IEnumerable<ModKey> sourceMasters)
    {
        foreach (var key in new[] { source }.Concat(sourceMasters).Concat(references.Select(ToFormKey).Select(key => key.ModKey))
                     .Where(key => key != output).Distinct())
            masters.Add(new MasterReference { Master = key });
    }

    private ImmutableArray<Diagnostic> ValidatePaths(ArmorBinaryWriteRequest request)
    {
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (!request.Proposal.IsUnder(_labRoot)) diagnostics.Add(new Diagnostic("armor-binary-proposal-outside-lab", DiagnosticSeverity.Error, "The proposal must remain under the K-only lab root."));
        if (!request.Output.IsUnder(_labRoot)) diagnostics.Add(new Diagnostic("armor-binary-output-outside-lab", DiagnosticSeverity.Error, "The output must remain under the K-only lab root."));
        if (!request.Output.Value.EndsWith(".esp", StringComparison.OrdinalIgnoreCase)) diagnostics.Add(new Diagnostic("armor-binary-output-extension", DiagnosticSeverity.Error, "The bounded ARMO writer emits ordinary .esp plugins only."));
        if (File.Exists(request.Output.Value)) diagnostics.Add(new Diagnostic("armor-binary-output-exists", DiagnosticSeverity.Error, "Binary armor writes never overwrite an existing plugin."));
        var parent = Path.GetDirectoryName(request.Output.Value);
        if (parent is null || !Directory.Exists(parent)) diagnostics.Add(new Diagnostic("armor-binary-output-parent", DiagnosticSeverity.Error, "The output plugin directory must already exist."));
        else diagnostics.AddRange(_policy.Evaluate(_labRoot, new WorkspacePath(parent)));
        AddReparseDiagnostic(diagnostics, request.Proposal.Value, "proposal");
        if (parent is not null) AddReparseDiagnostic(diagnostics, parent, "output-parent");
        if (!File.Exists(request.Proposal.Value)) diagnostics.Add(new Diagnostic("armor-binary-proposal-missing", DiagnosticSeverity.Error, "The armor proposal does not exist."));
        return diagnostics.ToImmutable();
    }

    private static ModKey? TryPluginName(string value, ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try { return new ModKey(Path.GetFileNameWithoutExtension(value), ModType.Plugin); }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("armor-binary-plugin-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private WorkspacePath? TryWorkspacePath(string value, ImmutableArray<Diagnostic>.Builder diagnostics, string role)
    {
        try
        {
            var path = new WorkspacePath(value);
            if (!path.IsUnder(_labRoot)) diagnostics.Add(new Diagnostic("armor-binary-source-outside-lab", DiagnosticSeverity.Error, $"The {role} path must remain under the K-only lab root."));
            return path;
        }
        catch (ArgumentException exception) { diagnostics.Add(new Diagnostic("armor-binary-source-invalid", DiagnosticSeverity.Error, exception.Message)); return null; }
    }

    private static FormKey ParseFormKey(string value) => FormReference.TryParse(value, out var reference) ? ToFormKey(reference) : throw new InvalidDataException($"Invalid armor FormReference '{value}'.");
    private static FormKey ToFormKey(FormReference reference) => new(new ModKey(Path.GetFileNameWithoutExtension(reference.Plugin.Value), ModType.Plugin), reference.FormId.Value);
    private static ModKey ToModKey(string path) => new(Path.GetFileNameWithoutExtension(path), ModType.Plugin);
    private static bool HasChanged(ArmorProposalArtifact proposal, string field) =>
        proposal.ChangedFields.Contains(field, StringComparer.Ordinal);
    private static void WriteMod(IModGetter mod, string destination) => mod.WriteToBinary(new FilePath(destination), new BinaryWriteParameters { ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck, MastersListOrdering = MastersListOrderingOption.NoCheck });

    private static void AddReparseDiagnostic(ImmutableArray<Diagnostic>.Builder diagnostics, string path, string role)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            try
            {
                if ((File.Exists(current) || Directory.Exists(current)) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                { diagnostics.Add(new Diagnostic("armor-binary-reparse", DiagnosticSeverity.Error, $"The {role} traverses a reparse point.")); return; }
            }
            catch (IOException exception) { diagnostics.Add(new Diagnostic("armor-binary-path-inspection", DiagnosticSeverity.Error, exception.Message)); return; }
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
    }

    private static bool HasErrors(ImmutableArray<Diagnostic>.Builder diagnostics) => diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);
    private static ArmorBinaryWriteResult Refused(ArmorBinaryWriteRequest request, FormId? target, ImmutableArray<Diagnostic> diagnostics) => new(false, request.Proposal, request.Output, target, null, diagnostics);

    private void AddTemporaryCleanupDiagnostic(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string temporary)
    {
        ArmorBinaryCleanupResult cleanup =
            _fileOperations.DeleteIfPresent(temporary);
        if (!cleanup.Succeeded)
            diagnostics.Add(new Diagnostic(
                "armor-binary-cleanup-failed",
                DiagnosticSeverity.Warning,
                $"Cleanup could not remove the temporary plugin '{temporary}': {cleanup.ErrorMessage}"));
    }

    private void AddFailureCleanupDiagnostics(
        ImmutableArray<Diagnostic>.Builder diagnostics,
        string output,
        string? temporary,
        bool outputCommitted)
    {
        if (outputCommitted)
        {
            ArmorBinaryCleanupResult rollback =
                _fileOperations.DeleteIfPresent(output);
            if (!rollback.Succeeded)
                diagnostics.Add(new Diagnostic(
                    "armor-binary-rollback-failed",
                    DiagnosticSeverity.Error,
                    $"Rollback could not remove the committed output '{output}': {rollback.ErrorMessage}"));
        }

        if (temporary is not null)
            AddTemporaryCleanupDiagnostic(diagnostics, temporary);
    }

    private void AttachCancellationCleanupFailures(
        OperationCanceledException exception,
        string output,
        string? temporary,
        bool outputCommitted)
    {
        if (outputCommitted)
        {
            ArmorBinaryCleanupResult rollback =
                _fileOperations.DeleteIfPresent(output);
            if (!rollback.Succeeded)
                exception.Data["armor-binary-rollback-failed"] =
                    $"Rollback could not remove the committed output '{output}': {rollback.ErrorMessage}";
        }

        if (temporary is null) return;
        ArmorBinaryCleanupResult cleanup =
            _fileOperations.DeleteIfPresent(temporary);
        if (!cleanup.Succeeded)
            exception.Data["armor-binary-cleanup-failed"] =
                $"Cleanup could not remove the temporary plugin '{temporary}': {cleanup.ErrorMessage}";
    }
}
