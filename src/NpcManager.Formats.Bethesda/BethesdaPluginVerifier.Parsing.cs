using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaPluginVerifier
{
    private static ParsedNpc Parse(
        byte[] bytes,
        FormId targetFormId,
        PluginName recordOwner,
        PluginName filePlugin,
        CancellationToken cancellationToken)
    {
        var result = new List<ParsedNpc>();
        var masters = ReadTes4Masters(bytes);
        var rawTargetFormId = ResolveRawTargetFormId(
            masters,
            targetFormId,
            recordOwner,
            filePlugin);
        var recordOwnerIsDeclaredMaster = masters.Any(master => string.Equals(
            master, recordOwner.Value, StringComparison.OrdinalIgnoreCase));
        var selfReferencePlugin = !recordOwnerIsDeclaredMaster &&
                                  !string.Equals(recordOwner.Value, filePlugin.Value,
                                      StringComparison.OrdinalIgnoreCase)
            ? recordOwner
            : filePlugin;
        var referencePlugins = masters
            .Select(master => new PluginName(master))
            .Append(selfReferencePlugin)
            .ToImmutableArray();
        ParseRange(
            bytes,
            0,
            bytes.Length,
            rawTargetFormId,
            referencePlugins,
            result,
            cancellationToken);
        return result.Count switch
        {
            1 => result[0],
            0 => throw new InvalidDataException($"NPC record {targetFormId} was not found."),
            _ => throw new InvalidDataException($"NPC record {targetFormId} occurred more than once.")
        };
    }

    private static uint ResolveRawTargetFormId(
        List<string> masters,
        FormId targetFormId,
        PluginName recordOwner,
        PluginName filePlugin)
    {
        if (targetFormId.Value > 0x00FF_FFFF)
            throw new InvalidDataException("NPC verification requires a plugin-local 24-bit FormID.");

        var ownerIndex = string.Equals(
            recordOwner.Value,
            filePlugin.Value,
            StringComparison.OrdinalIgnoreCase)
            ? masters.Count
            : masters.FindIndex(master => string.Equals(
                master,
                recordOwner.Value,
                StringComparison.OrdinalIgnoreCase));

        // The legacy full-copy mutation route changes the file's ModKey and
        // therefore owns its copied records. True override artifacts instead
        // name the source plugin in MAST and resolve through that index.
        if (ownerIndex < 0) ownerIndex = masters.Count;
        if (ownerIndex > byte.MaxValue)
            throw new InvalidDataException("The plugin master count exceeds the ordinary FormID range.");
        return checked((uint)(ownerIndex << 24)) | targetFormId.Value;
    }

    private static List<string> ReadTes4Masters(byte[] bytes)
    {
        if (bytes.Length < 24 || Ascii(bytes, 0, 4) != "TES4")
            throw new InvalidDataException("The plugin does not begin with a TES4 header record.");
        var payloadLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4, 4));
        if (payloadLength > int.MaxValue)
            throw new InvalidDataException("The TES4 header payload exceeds the supported range.");
        var end = checked(24 + (int)payloadLength);
        if (end > bytes.Length)
            throw new InvalidDataException("The TES4 header extends beyond the plugin boundary.");

        var masters = new List<string>();
        var position = 24;
        while (position + 6 <= end)
        {
            var signature = Ascii(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2));
            var subrecordEnd = checked(position + 6 + size);
            if (subrecordEnd > end)
                throw new InvalidDataException($"TES4 subrecord {signature} extends beyond the header boundary.");
            if (signature == "MAST")
            {
                var name = Encoding.UTF8.GetString(bytes, position + 6, size).TrimEnd('\0');
                if (string.IsNullOrWhiteSpace(name))
                    throw new InvalidDataException("The TES4 header contains an empty MAST entry.");
                masters.Add(name);
            }
            position = subrecordEnd;
        }
        if (position != end)
            throw new InvalidDataException("The TES4 header has trailing bytes outside a complete subrecord.");
        return masters;
    }

    private static void ParseRange(byte[] bytes, int start, int end, uint targetFormId,
        ImmutableArray<PluginName> referencePlugins,
        List<ParsedNpc> result, CancellationToken cancellationToken)
    {
        var position = start;
        while (position + 8 <= end)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var signature = Ascii(bytes, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 4, 4));
            if (size > int.MaxValue) throw new InvalidDataException("Record size exceeds the supported range.");
            var recordEnd = checked(position + (signature == "GRUP" ? (int)size : 24 + (int)size));
            if (recordEnd > end) throw new InvalidDataException("Record extends beyond the plugin boundary.");
            if (signature == "GRUP")
            {
                ParseRange(bytes, position + 24, recordEnd, targetFormId,
                    referencePlugins, result, cancellationToken);
            }
            else if (signature == "NPC_" && position + 16 <= recordEnd &&
                     BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(position + 12, 4)) == targetFormId)
            {
                result.Add(ParseNpcPayload(bytes, position + 24, recordEnd, referencePlugins));
            }
            position = recordEnd;
        }
        if (position != end) throw new InvalidDataException("Trailing bytes do not form a complete record header.");
    }

    private static ParsedNpc ParseNpcPayload(
        byte[] bytes,
        int start,
        int end,
        ImmutableArray<PluginName> referencePlugins)
    {
        var fields = new Dictionary<string, List<byte[]>>(StringComparer.Ordinal);
        var position = start;
        uint? extendedSize = null;
        while (position + 6 <= end)
        {
            var signature = Ascii(bytes, position, 4);
            var shortSize = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(position + 4, 2));
            var payload = checked(position + 6);
            if (signature == "XXXX")
            {
                if (shortSize != 4 || end - payload < 4 || extendedSize is not null)
                    throw new InvalidDataException("NPC payload contains a malformed XXXX size marker.");
                extendedSize = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(payload, 4));
                position = checked(payload + 4);
                continue;
            }
            var size = extendedSize ?? shortSize;
            if (extendedSize is not null && shortSize != 0)
                throw new InvalidDataException("NPC extended subrecord has a nonzero short size.");
            extendedSize = null;
            if (size > int.MaxValue)
                throw new InvalidDataException("NPC subrecord size exceeds the supported range.");
            var fieldEnd = checked(payload + (int)size);
            if (fieldEnd > end) throw new InvalidDataException($"NPC subrecord {signature} extends beyond the record boundary.");
            if (!fields.TryGetValue(signature, out var values)) fields[signature] = values = [];
            values.Add(bytes.AsSpan(payload, (int)size).ToArray());
            position = fieldEnd;
        }
        if (extendedSize is not null)
            throw new InvalidDataException("NPC payload ends after an XXXX size marker.");
        if (position != end) throw new InvalidDataException("NPC payload has trailing bytes.");
        return new ParsedNpc(referencePlugins,
            fields.ToDictionary(pair => pair.Key, pair => Combine(pair.Value), StringComparer.Ordinal));
    }

    private static byte[] Combine(IEnumerable<byte[]> values)
    {
        using var stream = new MemoryStream();
        foreach (var value in values) stream.Write(value);
        return stream.ToArray();
    }

    private static string Ascii(byte[] bytes, int offset, int count)
    {
        if (offset < 0 || offset + count > bytes.Length) throw new InvalidDataException("Record header is truncated.");
        return Encoding.ASCII.GetString(bytes, offset, count);
    }

    private static string RawField(string field) => field switch
    {
        "EditorID" => "EDID",
        "Name" => "FULL",
        "ShortName" => "SHRT",
        "Race" => "RNAM",
        "Voice" => "VTCK",
        "Class" => "CNAM",
        "CombatStyle" => "ZNAM",
        "Packages" => "PKID",
        "DefaultPackageList" => "DPLT",
        _ when field.StartsWith("AIDT:", StringComparison.Ordinal) => "AIDT",
        "Sex" => "ACBS",
        "SkyrimWeight" => "NAM7",
        "Thin" or "Muscular" or "Fat" => "MWGT",
        "Level" or "XpValueOffset" or "MagickaOffset" or "StaminaOffset" or "HealthOffset" or
        "CalcMinLevel" or "CalcMaxLevel" or "SpeedMultiplier" or "DispositionBase" or "BleedoutOverride" => "ACBS",
        _ when field.StartsWith("Flag:", StringComparison.Ordinal) => "ACBS",
        "Height" => "NAM6",
        "Keywords" => "KWDA",
        "AttachParentSlots" => "APPR",
        "Factions" => "SNAM",
        "Inventory" => "CNTO",
        "DefaultOutfit" => "DOFT",
        "SleepingOutfit" => "SOFT",
        "Skin" => "WNAM",
        "BodyMorphRegions" => "MRSV",
        "Perks" => "PRKR",
        "ActorEffects" => "SPLO",
        "Properties" => "PRPS",
        "PlayerHealth" or "PlayerMagicka" or "PlayerStamina" => "DNAM",
        "FarAwayModelDistance" or "GearedUpWeapons" => "DNAM",
        _ when field.StartsWith("Skill", StringComparison.Ordinal) => "DNAM",
        _ => field
    };

    internal static bool TryGetRawSignatures(
        string field,
        out ImmutableArray<string> signatures)
    {
        signatures = field switch
        {
            "EditorID" => ["EDID"],
            "Name" => ["FULL"],
            "ShortName" => ["SHRT"],
            "Race" => ["RNAM"],
            "Voice" => ["VTCK"],
            "Class" => ["CNAM"],
            "CombatStyle" => ["ZNAM"],
            _ when field.StartsWith("AIDT:", StringComparison.Ordinal) => ["AIDT"],
            "Sex" => ["ACBS"],
            "SkyrimWeight" => ["NAM7"],
            "Thin" or "Muscular" or "Fat" => ["MWGT"],
            "Level" or "XpValueOffset" or "MagickaOffset" or "StaminaOffset" or "HealthOffset" or
            "CalcMinLevel" or "CalcMaxLevel" or "SpeedMultiplier" or "DispositionBase" or "BleedoutOverride" => ["ACBS"],
            "Height" => ["NAM6"],
            "Keywords" => ["KSIZ", "KWDA"],
            "AttachParentSlots" => ["APPR"],
            "Factions" => ["SNAM"],
            "Inventory" => ["COCT", "CNTO"],
            "DefaultOutfit" => ["DOFT"],
            "SleepingOutfit" => ["SOFT"],
            "Skin" => ["WNAM"],
            "BodyMorphRegions" => ["MRSV"],
            "Perks" => ["PRKZ", "PRKR"],
            "ActorEffects" => ["SPCT", "SPLO"],
            "Properties" => ["PRPS"],
            "PlayerHealth" or "PlayerMagicka" or "PlayerStamina" => ["DNAM"],
            "FarAwayModelDistance" or "GearedUpWeapons" => ["DNAM"],
            _ when field.StartsWith("Flag:", StringComparison.Ordinal) => ["ACBS"],
            _ when field.StartsWith("Skill", StringComparison.Ordinal) => ["DNAM"],
            _ => []
        };
        return signatures.Length > 0;
    }

    private static void VerifySexBitPreservation(ParsedNpc source, ParsedNpc output,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (!source.Subrecords.TryGetValue("ACBS", out var sourceAcbs) ||
            !output.Subrecords.TryGetValue("ACBS", out var outputAcbs) ||
            sourceAcbs.Length < 4 || outputAcbs.Length < 4 || sourceAcbs.Length != outputAcbs.Length)
        {
            diagnostics.Add(new Diagnostic("sex-flag-shape", DiagnosticSeverity.Error,
                "Sex mutation requires matching ACBS flag payloads in source and output."));
            return;
        }

        var sourceFlags = BinaryPrimitives.ReadUInt32LittleEndian(sourceAcbs.AsSpan(0, 4));
        var outputFlags = BinaryPrimitives.ReadUInt32LittleEndian(outputAcbs.AsSpan(0, 4));
        if ((sourceFlags & ~1u) != (outputFlags & ~1u))
            diagnostics.Add(new Diagnostic("sex-flag-drift", DiagnosticSeverity.Error,
                "Sex mutation changed an ACBS flag other than Female."));
        if (!sourceAcbs.AsSpan(4).SequenceEqual(outputAcbs.AsSpan(4)))
            diagnostics.Add(new Diagnostic("sex-acbs-drift", DiagnosticSeverity.Error,
                "Sex mutation changed ACBS bytes outside the Female flag."));
    }
}
