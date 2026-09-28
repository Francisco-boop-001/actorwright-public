using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

internal static class SkyrimArmorEditorDocumentBuilder
{
    public static bool TryBuild(
        SkyrimArmorIdentitySectionViewModel identity,
        SkyrimArmorCoreSectionViewModel core,
        SkyrimArmorReferenceSectionViewModel referenceFields,
        SkyrimArmorBoundsSectionViewModel bounds,
        SkyrimArmorCollectionSectionViewModel collections,
        [NotNullWhen(true)] out SkyrimArmorEditorDocument? document,
        out string error)
    {
        document = null;
        error = string.Empty;
        try
        {
            var editorId = new EditorId(identity.EditorIdText.Trim());
            if (!TryUInt32(core.ValueText, out uint value))
                return Invalid("Value must be an unsigned 32-bit integer.", out error);
            if (!double.TryParse(core.WeightText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double weight))
                return Invalid("Weight must be a finite number.", out error);
            if (!double.TryParse(core.ArmorRatingText, NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double armorRating))
                return Invalid("Armor rating must be a finite number.", out error);
            if (!TryUInt32(core.SlotMaskText, out uint slotMask))
                return Invalid("BOD2 must be a decimal or 0x-prefixed 32-bit mask.", out error);
            if (!FormReference.TryParse(referenceFields.Race.Trim(), out FormReference race))
                return Invalid("Race requires a qualified Plugin|FormID reference.", out error);
            if (!TryOptionalReference(referenceFields.Enchantment, out FormReference? enchantment) ||
                !TryOptionalReference(referenceFields.PickupSound, out FormReference? pickupSound) ||
                !TryOptionalReference(referenceFields.DropSound, out FormReference? dropSound) ||
                !TryOptionalReference(referenceFields.EquipmentType, out FormReference? equipmentType) ||
                !TryOptionalReference(referenceFields.AlternateBlockMaterial, out FormReference? blockMaterial) ||
                !TryOptionalReference(referenceFields.TemplateArmor, out FormReference? templateArmor))
                return Invalid("Optional references must be empty or qualified Plugin|FormID values.", out error);
            if (!TryInt16(bounds.MinimumX, out short minimumX) ||
                !TryInt16(bounds.MinimumY, out short minimumY) ||
                !TryInt16(bounds.MinimumZ, out short minimumZ) ||
                !TryInt16(bounds.MaximumX, out short maximumX) ||
                !TryInt16(bounds.MaximumY, out short maximumY) ||
                !TryInt16(bounds.MaximumZ, out short maximumZ))
                return Invalid("Every object-bound coordinate must be a signed 16-bit integer.", out error);
            if (!TryReferences(collections.ArmorAddons, out ImmutableArray<FormReference> addons) ||
                !TryReferences(collections.Keywords, out ImmutableArray<FormReference> keywords))
                return Invalid("Every collection row must be a qualified Plugin|FormID reference.", out error);

            document = new SkyrimArmorEditorDocument(
                GameEdition.SkyrimSpecialEdition,
                identity.Intent,
                identity.Mode,
                identity.SourcePlugin,
                identity.SourceFormId,
                editorId,
                identity.TargetFormId,
                core.Name,
                race,
                enchantment,
                core.NonPlayable,
                core.Description,
                value,
                weight,
                armorRating,
                slotMask,
                pickupSound,
                dropSound,
                equipmentType,
                blockMaterial,
                new ArmorObjectBounds(minimumX, minimumY, minimumZ,
                    maximumX, maximumY, maximumZ),
                EmptyToNull(referenceFields.MaleWorldModel),
                EmptyToNull(referenceFields.FemaleWorldModel),
                templateArmor,
                addons,
                keywords,
                collections.AuthoredArmorAddons);
            return true;
        }
        catch (ArgumentException exception)
        {
            error = exception.Message;
            return false;
        }
    }

    private static bool Invalid(string message, out string error)
    {
        error = message;
        return false;
    }

    private static bool TryUInt32(string value, out uint result)
    {
        string text = value.Trim();
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(text[2..], NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out result)
            : uint.TryParse(text, NumberStyles.None,
                CultureInfo.InvariantCulture, out result);
    }

    private static bool TryInt16(string value, out short result) =>
        short.TryParse(value.Trim(), NumberStyles.Integer,
            CultureInfo.InvariantCulture, out result);

    private static bool TryOptionalReference(
        string value,
        out FormReference? reference)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            reference = null;
            return true;
        }
        if (FormReference.TryParse(value.Trim(), out FormReference parsed))
        {
            reference = parsed;
            return true;
        }
        reference = null;
        return false;
    }

    private static bool TryReferences(
        IEnumerable<string> values,
        out ImmutableArray<FormReference> references)
    {
        var builder = ImmutableArray.CreateBuilder<FormReference>();
        foreach (string value in values)
        {
            if (!FormReference.TryParse(value, out FormReference reference))
            {
                references = [];
                return false;
            }
            builder.Add(reference);
        }
        references = builder.ToImmutable();
        return true;
    }

    private static string? EmptyToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
