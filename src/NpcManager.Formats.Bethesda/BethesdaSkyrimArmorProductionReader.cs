using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Reads one explicit source-owned winning Skyrim ARMO from a hash-reviewed
/// copied closure. It does not resolve assets, render, or mutate plugins.
/// </summary>
public sealed class BethesdaSkyrimArmorProductionReader :
    ISkyrimArmorProductionReader
{
    public SkyrimArmorProductionReadResult Read(
        SkyrimArmorProductionReadRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var diagnostics = ImmutableArray.CreateBuilder<Diagnostic>();
        if (request.Intake.Edition != GameEdition.SkyrimSpecialEdition)
            diagnostics.Add(Error("armor-production-edition",
                "A reviewed Skyrim SE/AE intake is required."));
        if (request.SourceFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("armor-production-source-form",
                "The source ARMO must use a nonzero plugin-local 24-bit FormID."));
        if (request.NewTargetFormId.Value is 0 or > 0x00FF_FFFF)
            diagnostics.Add(Error("armor-production-target-form",
                "The new ARMO target must use a nonzero plugin-local 24-bit FormID."));

        PluginClosureReviewEntry[] ordered = request.Intake.Plugins
            .Where(item => item.Enabled && item.Exists && item.ReadSucceeded &&
                           item.SourceHash is not null)
            .OrderBy(item => item.Order)
            .ToArray();
        if (ordered.Length == 0)
            diagnostics.Add(Error("armor-production-closure-empty",
                "The reviewed copied plugin closure is empty."));
        if (ordered.Select(item => item.Plugin.Value)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
            ordered.Length)
            diagnostics.Add(Error("armor-production-closure-duplicate",
                "The reviewed copied plugin closure contains duplicate plugin names."));

        PluginClosureReviewEntry[] sourceEntries = ordered.Where(item =>
            string.Equals(item.Plugin.Value, request.SourcePlugin.Value,
                StringComparison.OrdinalIgnoreCase)).ToArray();
        if (sourceEntries.Length != 1)
            diagnostics.Add(Error("armor-production-source-plugin",
                "The selected source plugin must resolve exactly once in the reviewed closure."));

        var armors = new Dictionary<FormKey, ArmorProviderSnapshot>();
        var armorAddons = new Dictionary<FormKey, ArmorAddonProviderSnapshot>();
        var armorAddonHistory = new List<ArmorAddonProviderSnapshot>();
        foreach (PluginClosureReviewEntry entry in ordered)
        {
            try
            {
                Sha256Hash current = HashFile(entry.Path.Value);
                if (entry.SourceHash is null || current != entry.SourceHash.Value)
                {
                    diagnostics.Add(Error("armor-production-plugin-stale",
                        $"Reviewed plugin '{entry.Plugin}' changed after intake."));
                    continue;
                }
                using var mod = SkyrimMod.CreateFromBinaryOverlay(
                    new ModPath(
                        ModKey.FromNameAndExtension(entry.Plugin.Value),
                        new FilePath(entry.Path.Value)),
                    SkyrimRelease.SkyrimSE);
                foreach (IArmorGetter armor in mod.Armors)
                    armors[armor.FormKey] = new(
                        entry.Plugin,
                        entry.Path,
                        armor.DeepCopy());
                foreach (IArmorAddonGetter addon in mod.ArmorAddons)
                {
                    var addonSnapshot = new ArmorAddonProviderSnapshot(
                        entry.Plugin,
                        entry.Path,
                        addon.FormKey,
                        addon.EditorID,
                        addon.IsDeleted,
                        addon.IsDeleted ? null : addon.DeepCopy());
                    armorAddonHistory.Add(addonSnapshot);
                    armorAddons[addon.FormKey] = addonSnapshot;
                }
            }
            catch (Exception exception) when (exception is IOException or
                                               UnauthorizedAccessException or
                                               InvalidDataException or
                                               ArgumentException)
            {
                diagnostics.Add(Error("armor-production-plugin-read",
                    $"Reviewed plugin '{entry.Plugin}' could not be read: {exception.Message}"));
            }
        }
        if (HasErrors(diagnostics) || sourceEntries.Length != 1)
            return Refused(diagnostics);

        PluginClosureReviewEntry sourceEntry = sourceEntries[0];
        FormKey sourceKey = new(
            ModKey.FromNameAndExtension(request.SourcePlugin.Value),
            request.SourceFormId.Value);
        if (!armors.TryGetValue(sourceKey, out ArmorProviderSnapshot? snapshot) ||
            snapshot.Armor.IsDeleted)
            diagnostics.Add(Error("armor-production-source-armor",
                "The selected source-owned ARMO is missing, deleted, or overridden out of the reviewed closure."));
        else if (!string.Equals(snapshot.Provider.Value,
                     request.SourcePlugin.Value,
                     StringComparison.OrdinalIgnoreCase))
            diagnostics.Add(Error("armor-production-source-provider",
                "The selected source-owned ARMO is not the winning reviewed provider."));
        if (snapshot?.Armor.EditorID is not { Length: > 0 })
            diagnostics.Add(Error("armor-production-source-editor-id",
                "The source ARMO requires an EditorID."));
        if (snapshot is not null && snapshot.Armor.Race.IsNull)
            diagnostics.Add(Error("armor-production-source-race",
                "The source ARMO requires a qualified race."));
        if (HasErrors(diagnostics) || snapshot is null)
            return Refused(diagnostics);

        ImmutableArray<EditorId> editorIds = armors.Values
            .Where(item => !item.Armor.IsDeleted &&
                           !string.IsNullOrWhiteSpace(item.Armor.EditorID))
            .Select(item => new EditorId(item.Armor.EditorID!))
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();
        ImmutableArray<SkyrimArmorAddonSlotEvidence> slotEvidence = armorAddons
            .Values
            .Where(item => !item.IsDeleted && item.Addon is not null)
            .Select(item => new SkyrimArmorAddonSlotEvidence(
                Reference(item.FormKey),
                item.Addon!.BodyTemplate is null
                    ? 0U
                    : (uint)item.Addon.BodyTemplate.FirstPersonFlags))
            .OrderBy(item => item.ArmorAddon.Plugin.Value,
                StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.ArmorAddon.FormId.Value)
            .ToImmutableArray();

        FormReference owningArmorRace = Reference(snapshot.Armor.Race.FormKey);
        ImmutableArray<SkyrimArmorAddonProductionCatalogEntry> addonCatalog =
            armorAddonHistory
                .OrderBy(item => item.FormKey.ModKey.ToString(),
                    StringComparer.OrdinalIgnoreCase)
                .ThenBy(item => item.FormKey.ID)
                .ThenBy(item => item.Provider.Value,
                    StringComparer.OrdinalIgnoreCase)
                .Select(item => ToCatalogEntry(
                    item,
                    owningArmorRace,
                    armorAddons.TryGetValue(item.FormKey, out var winner) &&
                    !ReferenceEquals(item, winner)))
                .ToImmutableArray();
        ImmutableArray<EditorId> addonEditorIds = armorAddons.Values
            .Where(item => !item.IsDeleted &&
                           !string.IsNullOrWhiteSpace(item.EditorId))
            .Select(item => new EditorId(item.EditorId!))
            .DistinctBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .OrderBy(item => item.Value, StringComparer.OrdinalIgnoreCase)
            .ToImmutableArray();

        SkyrimArmorEditorDocument template = ToDocument(
            snapshot.Armor,
            snapshot.Path,
            request.NewTargetFormId,
            SkyrimArmorEditorIntent.NewFromTemplate,
            ArmorProposalMode.New,
            new EditorId(SkyrimArmorEditorRules.EditorIdPrefix +
                         SafeSuffix(snapshot.Armor.EditorID!)));
        SkyrimArmorEditorDocument blank = template with
        {
            Intent = SkyrimArmorEditorIntent.BlankNew,
            Name = string.Empty,
            Enchantment = null,
            NonPlayable = false,
            Description = string.Empty,
            Value = 0,
            Weight = 0,
            ArmorRating = 0,
            SlotMask = 0,
            PickupSound = null,
            DropSound = null,
            EquipmentType = null,
            AlternateBlockMaterial = null,
            ObjectBounds = new ArmorObjectBounds(0, 0, 0, 0, 0, 0),
            MaleWorldModel = null,
            FemaleWorldModel = null,
            TemplateArmor = null,
            ArmorAddons = [],
            Keywords = [],
            AuthoredArmorAddons = []
        };
        SkyrimArmorEditorDocument overrideDocument = template with
        {
            Intent = SkyrimArmorEditorIntent.OverrideExisting,
            Mode = ArmorProposalMode.Override,
            EditorId = new EditorId(snapshot.Armor.EditorID!),
            TargetFormId = null
        };
        var source = new SkyrimArmorProductionSource(
            request.Intake,
            ordered.Select(item => item.Plugin).ToImmutableArray(),
            snapshot.Path,
            sourceEntry.SourceHash!.Value,
            new FormReference(request.SourcePlugin, request.SourceFormId),
            blank,
            template,
            overrideDocument,
            editorIds,
            slotEvidence,
            addonCatalog,
            addonEditorIds);
        return new(true, source, diagnostics.ToImmutable());
    }

    private static SkyrimArmorAddonProductionCatalogEntry ToCatalogEntry(
        ArmorAddonProviderSnapshot snapshot,
        FormReference owningArmorRace,
        bool stale)
    {
        if (snapshot.IsDeleted || snapshot.Addon is null)
        {
            string deletedLabel = string.IsNullOrWhiteSpace(snapshot.EditorId)
                ? snapshot.FormKey.ToString()
                : snapshot.EditorId;
            var deletedCandidate = new SkyrimArmorAddonReferenceCandidate(
                Reference(snapshot.FormKey),
                new RecordSignature("ARMA"),
                deletedLabel,
                true,
                stale,
                new SkyrimArmorAddonRaceEvidence(
                    false,
                    owningArmorRace,
                    owningArmorRace,
                    []));
            return new SkyrimArmorAddonProductionCatalogEntry(
                deletedCandidate,
                snapshot.Provider,
                null);
        }
        IArmorAddonGetter addon = snapshot.Addon;
        bool complete = !addon.Race.IsNull;
        FormReference primaryRace = complete
            ? Reference(addon.Race.FormKey)
            : owningArmorRace;
        ImmutableArray<FormReference> additionalRaces = addon.AdditionalRaces
            .Select(item => Reference(item.FormKey))
            .ToImmutableArray();
        var compatibility = new SkyrimArmorAddonRaceEvidence(
            complete,
            owningArmorRace,
            primaryRace,
            additionalRaces);
        string label = string.IsNullOrWhiteSpace(addon.EditorID)
            ? addon.FormKey.ToString()
            : addon.EditorID;
        var candidate = new SkyrimArmorAddonReferenceCandidate(
            Reference(addon.FormKey),
            new RecordSignature("ARMA"),
            label,
            addon.IsDeleted,
            stale,
            compatibility);
        bool ownerProvided = string.Equals(
            Path.GetFileName(snapshot.Path.Value),
            snapshot.FormKey.ModKey.ToString(),
            StringComparison.OrdinalIgnoreCase);
        SkyrimArmorAddonEditorDocument? editable = addon.IsDeleted || stale ||
                                                     !ownerProvided
            ? null
            : ToArmorAddonDocument(addon, snapshot.Path, label);
        return new SkyrimArmorAddonProductionCatalogEntry(
            candidate,
            snapshot.Provider,
            editable);
    }

    private static SkyrimArmorAddonEditorDocument ToArmorAddonDocument(
        IArmorAddonGetter addon,
        WorkspacePath sourcePath,
        string editorId) => new(
        GameEdition.SkyrimSpecialEdition,
        SkyrimArmorAddonEditorIntent.OverrideExisting,
        ArmorAddonProposalMode.Override,
        sourcePath,
        new FormId(addon.FormKey.ID),
        new EditorId(editorId),
        null,
        null,
        false,
        addon.WorldModel?.Male?.File,
        addon.WorldModel?.Female?.File,
        addon.FirstPersonModel?.Male?.File,
        addon.FirstPersonModel?.Female?.File,
        addon.BodyTemplate is null
            ? 0U
            : (uint)addon.BodyTemplate.FirstPersonFlags,
        OptionalReference(addon.Race.FormKey),
        addon.AdditionalRaces.Select(item => Reference(item.FormKey))
            .ToImmutableArray(),
        OptionalReference(addon.SkinTexture?.Male?.FormKey ?? FormKey.Null),
        OptionalReference(addon.SkinTexture?.Female?.FormKey ?? FormKey.Null),
        OptionalReference(addon.TextureSwapList?.Male?.FormKey ?? FormKey.Null),
        OptionalReference(addon.TextureSwapList?.Female?.FormKey ?? FormKey.Null),
        OptionalReference(addon.FootstepSound.FormKey),
        OptionalReference(addon.ArtObject.FormKey),
        addon.Priority?.Male ?? 0,
        addon.Priority?.Female ?? 0,
        addon.WeightSliderEnabled?.Male ?? false,
        addon.WeightSliderEnabled?.Female ?? false,
        addon.Priority is null ? (byte)0 : addon.DetectionSoundValue,
        addon.Priority is null ? 0D : addon.WeaponAdjust);

    private static SkyrimArmorEditorDocument ToDocument(
        IArmorGetter armor,
        WorkspacePath sourcePath,
        FormId targetFormId,
        SkyrimArmorEditorIntent intent,
        ArmorProposalMode mode,
        EditorId editorId) => new(
        GameEdition.SkyrimSpecialEdition,
        intent,
        mode,
        sourcePath,
        new FormId(armor.FormKey.ID),
        editorId,
        mode == ArmorProposalMode.New ? targetFormId : null,
        armor.Name?.String ?? string.Empty,
        Reference(armor.Race.FormKey),
        OptionalReference(armor.ObjectEffect.FormKey),
        armor.MajorFlags.HasFlag(Armor.MajorFlag.NonPlayable),
        armor.Description?.String ?? string.Empty,
        armor.Value,
        armor.Weight,
        armor.ArmorRating,
        armor.BodyTemplate is null
            ? 0U
            : (uint)armor.BodyTemplate.FirstPersonFlags,
        OptionalReference(armor.PickUpSound.FormKey),
        OptionalReference(armor.PutDownSound.FormKey),
        OptionalReference(armor.EquipmentType.FormKey),
        OptionalReference(armor.AlternateBlockMaterial.FormKey),
        armor.ObjectBounds is null
            ? new ArmorObjectBounds(0, 0, 0, 0, 0, 0)
            : new ArmorObjectBounds(
                armor.ObjectBounds.First.X,
                armor.ObjectBounds.First.Y,
                armor.ObjectBounds.First.Z,
                armor.ObjectBounds.Second.X,
                armor.ObjectBounds.Second.Y,
                armor.ObjectBounds.Second.Z),
        armor.WorldModel?.Male?.Model?.File,
        armor.WorldModel?.Female?.Model?.File,
        OptionalReference(armor.TemplateArmor.FormKey),
        armor.Armature.Select(item => Reference(item.FormKey)).ToImmutableArray(),
        (armor.Keywords ?? [])
            .Select(item => Reference(item.FormKey))
            .ToImmutableArray(),
        []);

    private static string SafeSuffix(string editorId)
    {
        string value = new(editorId.Where(character =>
            char.IsLetterOrDigit(character) || character == '_').ToArray());
        return string.IsNullOrEmpty(value) ? "NewArmor" : value;
    }

    private static FormReference Reference(FormKey key) => new(
        new PluginName(key.ModKey.ToString()),
        new FormId(key.ID));

    private static FormReference? OptionalReference(FormKey key) =>
        key.IsNull ? null : Reference(key);

    private static Sha256Hash HashFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static bool HasErrors(IEnumerable<Diagnostic> diagnostics) =>
        diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error);

    private static SkyrimArmorProductionReadResult Refused(
        ImmutableArray<Diagnostic>.Builder diagnostics) =>
        new(false, null, diagnostics.ToImmutable());

    private static Diagnostic Error(string code, string message) =>
        new(code, DiagnosticSeverity.Error, message);

    private sealed record ArmorProviderSnapshot(
        PluginName Provider,
        WorkspacePath Path,
        Armor Armor);

    private sealed record ArmorAddonProviderSnapshot(
        PluginName Provider,
        WorkspacePath Path,
        FormKey FormKey,
        string? EditorId,
        bool IsDeleted,
        ArmorAddon? Addon);
}
