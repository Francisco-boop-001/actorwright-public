using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaPluginVerifier
{
    private sealed record ParsedNpc(
        ImmutableArray<PluginName> ReferencePlugins,
        IReadOnlyDictionary<string, byte[]> Subrecords)
    {
        private FormReference ResolveReference(uint rawFormId)
        {
            var pluginIndex = checked((int)(rawFormId >> 24));
            if (pluginIndex < 0 || pluginIndex >= ReferencePlugins.Length)
                throw new InvalidDataException(
                    $"FormID 0x{rawFormId:X8} uses undeclared master index {pluginIndex}.");
            var localFormId = rawFormId & 0x00FF_FFFF;
            if (localFormId == 0)
                throw new InvalidDataException("A non-null reference contains local FormID zero.");
            return new FormReference(ReferencePlugins[pluginIndex], new FormId(localFormId));
        }

        private string ResolveNullableReference(uint rawFormId) =>
            rawFormId == 0 ? "none" : ResolveReference(rawFormId).ToString();

        public string? ReadValue(GameEdition edition, string field)
        {
            var rawField = RawField(field);
            if (!Subrecords.TryGetValue(rawField, out var bytes))
                return field is "Name" or "ShortName" ? string.Empty :
                    field is "Race" or "Voice" or "Class" or "CombatStyle" or "DefaultPackageList" ? "none" :
                    field == "Packages" ? string.Empty :
                    field is "Keywords" or "AttachParentSlots" or "Factions" or "Inventory" or "Perks" or "ActorEffects" or "Properties" ? string.Empty :
                    field is "DefaultOutfit" or "SleepingOutfit" or "Skin" ? "none" :
                    field == "BodyMorphRegions" ? Fallout4BodyRegionCatalog.FormatValues(Fallout4BodyMorphValues.Zero.ToDictionary()) : null;
            if (field is "EditorID" or "Name" or "ShortName")
                return Encoding.UTF8.GetString(bytes).TrimEnd('\0');
            if (field.StartsWith("AIDT:", StringComparison.Ordinal))
            {
                if (edition != GameEdition.SkyrimSpecialEdition || bytes.Length != 20)
                    throw new InvalidDataException("Skyrim AIDT must contain exactly 20 bytes.");
                return bytes[AidtOffset(field)].ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (field is "Packages" or "DefaultPackageList")
            {
                if (bytes.Length % 4 != 0 || field == "DefaultPackageList" && bytes.Length != 4)
                    throw new InvalidDataException($"{field} is not a valid FormID sequence.");
                return string.Join(",", Enumerable.Range(0, bytes.Length / 4)
                    .Select(index => ResolveNullableReference(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 4, 4)))));
            }
            if (field == "Sex" && bytes.Length >= 4)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                return (flags & 1) != 0 ? "female" : "male";
            }
            if (field is "Race" or "Voice" or "Class" or "CombatStyle" && bytes.Length == 0)
                return "none";
            if (field is "Race" or "Voice" or "Class" or "CombatStyle" && bytes.Length >= 4)
            {
                var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                return ResolveNullableReference(formId);
            }
            if (field is "DefaultOutfit" or "SleepingOutfit" && bytes.Length >= 4)
            {
                var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                return ResolveNullableReference(formId);
            }
            if (field == "Skin" && bytes.Length >= 4)
            {
                var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                return ResolveNullableReference(formId);
            }
            if (field == "BodyMorphRegions")
            {
                if (edition != GameEdition.Fallout4) throw new InvalidDataException("MRSV is unsupported for Skyrim SE NPC records.");
                if (bytes.Length != 20) throw new InvalidDataException("MRSV must contain five little-endian single-precision values.");
                var values = new Fallout4BodyMorphValues(
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4))),
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(4, 4))),
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(8, 4))),
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(12, 4))),
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(16, 4))));
                return Fallout4BodyRegionCatalog.FormatValues(values.ToDictionary());
            }
            if (field == "Perks")
            {
                var entrySize = edition == GameEdition.Fallout4 ? 5 : 8;
                if (bytes.Length % entrySize != 0) throw new InvalidDataException($"PRKR is not {entrySize}-byte entry aligned.");
                return string.Join(",", Enumerable.Range(0, bytes.Length / entrySize).Select(index =>
                    $"{ResolveReference(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * entrySize, 4)))}={bytes[index * entrySize + 4]}"));
            }
            if (field == "ActorEffects")
            {
                if (bytes.Length % 4 != 0) throw new InvalidDataException("SPLO is not 4-byte FormID aligned.");
                return string.Join(",", Enumerable.Range(0, bytes.Length / 4).Select(index =>
                    ResolveReference(BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(index * 4, 4))).ToString()));
            }
            if (field == "Properties")
            {
                if (edition != GameEdition.Fallout4) throw new InvalidDataException("PRPS is unsupported for Skyrim SE NPC records.");
                if (bytes.Length % 8 != 0) throw new InvalidDataException("PRPS is not 8-byte entry aligned.");
                return string.Join(",", Enumerable.Range(0, bytes.Length / 8).Select(index =>
                    $"{ResolveReference(BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index * 8, 4)))}=" +
                    BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index * 8 + 4, 4))).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
            }
            if (field is "Keywords" or "AttachParentSlots")
            {
                if (bytes.Length % 4 != 0) return null;
                var values = new List<string>();
                for (var index = 0; index < bytes.Length; index += 4)
                    values.Add(ResolveReference(BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(index, 4))).ToString());
                return string.Join(",", values);
            }
            if (field == "Factions")
            {
                var entrySize = edition == GameEdition.Fallout4 ? 5 : 8;
                if (bytes.Length % entrySize != 0) return null;
                var values = new List<string>();
                for (var index = 0; index < bytes.Length; index += entrySize)
                {
                    var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4));
                    var rank = unchecked((sbyte)bytes[index + 4]);
                    values.Add($"{ResolveReference(formId)}={rank.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                return string.Join(",", values);
            }
            if (field == "Inventory")
            {
                if (bytes.Length % 8 != 0) return null;
                var values = new List<string>();
                for (var index = 0; index < bytes.Length; index += 8)
                {
                    var formId = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(index, 4));
                    var count = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(index + 4, 4));
                    values.Add($"{ResolveReference(formId)}={count.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                return string.Join(",", values);
            }
            if (field == "SkyrimWeight" && bytes.Length >= 4)
                return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)))
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (field is "Thin" or "Muscular" or "Fat" && bytes.Length >= 12)
            {
                var offset = field switch { "Thin" => 0, "Muscular" => 4, _ => 8 };
                return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset, 4)))
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (field == "Level" && bytes.Length >= 10)
            {
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                var offset = edition == GameEdition.SkyrimSpecialEdition ? 8 : 6;
                var raw = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2));
                var multiplier = (flags & 0x80) != 0;
                return new NpcLevelValue(multiplier ? NpcLevelMode.Multiplier : NpcLevelMode.Fixed,
                    multiplier ? raw / 1000m : raw).ToString();
            }
            if (field.StartsWith("Flag:", StringComparison.Ordinal))
            {
                var flagName = field[5..];
                if (flagName.Equals("pc-level-mult", StringComparison.OrdinalIgnoreCase))
                    flagName = edition == GameEdition.Fallout4 ? nameof(NpcFlag.Fallout4CalcForEachTemplate) : nameof(NpcFlag.SkyrimUseTemplate);
                if (!NpcFlagExtensions.TryParseWireName(flagName, out var flag) || !NpcFlagExtensions.TryGetMask(edition, flag, out var mask)) return null;
                var flags = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(0, 4));
                return ((flags & mask) != 0).ToString().ToLowerInvariant();
            }
            if (field.StartsWith("SkillValue:", StringComparison.Ordinal) || field.StartsWith("SkillOffset:", StringComparison.Ordinal))
            {
                if (edition != GameEdition.SkyrimSpecialEdition || !Subrecords.TryGetValue("DNAM", out var dnam)) return null;
                var isOffset = field.StartsWith("SkillOffset:", StringComparison.Ordinal);
                if (!NpcSkillExtensions.TryParseWireName(field[(isOffset ? 12 : 11)..], out var skill)) return null;
                var offset = (isOffset ? 18 : 0) + (int)skill;
                return dnam.Length > offset ? dnam[offset].ToString(System.Globalization.CultureInfo.InvariantCulture) : null;
            }
            if (field is "XpValueOffset" or "MagickaOffset" or "StaminaOffset" or "HealthOffset" or "CalcMinLevel" or "CalcMaxLevel" or "SpeedMultiplier" or "DispositionBase" or "BleedoutOverride")
            {
                var offset = field switch
                {
                    "XpValueOffset" => 4,
                    "MagickaOffset" => 4,
                    "StaminaOffset" => 6,
                    "CalcMinLevel" => edition == GameEdition.SkyrimSpecialEdition ? 10 : 8,
                    "CalcMaxLevel" => edition == GameEdition.SkyrimSpecialEdition ? 12 : 10,
                    "SpeedMultiplier" => 14,
                    "DispositionBase" => edition == GameEdition.SkyrimSpecialEdition ? 16 : 12,
                    "HealthOffset" => 20,
                    "BleedoutOverride" => edition == GameEdition.SkyrimSpecialEdition ? 22 : 16,
                    _ => -1
                };
                if (offset < 0 || bytes.Length < offset + 2) return null;
                var signed = field is "XpValueOffset" or "MagickaOffset" or "StaminaOffset" or "HealthOffset" or "SpeedMultiplier" or "DispositionBase" or "BleedoutOverride";
                var numeric = signed ? BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(offset, 2))
                    : unchecked((short)BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)));
                return signed ? numeric.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : unchecked((ushort)numeric).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (field == "Height" && edition == GameEdition.SkyrimSpecialEdition && bytes.Length >= 4)
                return BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(0, 4)))
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            if (field is "PlayerHealth" or "PlayerMagicka" or "PlayerStamina" && edition == GameEdition.SkyrimSpecialEdition && bytes.Length >= 42)
            {
                var offset = field switch { "PlayerHealth" => 36, "PlayerMagicka" => 38, _ => 40 };
                return BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (field == "FarAwayModelDistance" && edition == GameEdition.SkyrimSpecialEdition && bytes.Length >= 48)
            {
                return BitConverter.Int32BitsToSingle(
                        BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(44, 4)))
                    .ToString("R", System.Globalization.CultureInfo.InvariantCulture);
            }
            if (field == "GearedUpWeapons" && edition == GameEdition.SkyrimSpecialEdition && bytes.Length >= 49)
                return bytes[48].ToString(System.Globalization.CultureInfo.InvariantCulture);
            return Convert.ToHexString(bytes).ToLowerInvariant();
        }
    }
}
