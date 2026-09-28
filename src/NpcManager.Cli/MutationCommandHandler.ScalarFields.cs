using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Cli;

internal sealed partial class MutationCommandHandler
{
    private static bool TryBuildPatchRequest(ParsedCommand command, out NpcMutationRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryGetEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, "input-plugin", out var input, out errorMessage) ||
            !TryPath(command, "output", out var output, out errorMessage) ||
            !TryFormId(command, out var formId, out errorMessage)) return false;

        EditorId? editorId = null;
        if (command.Options.TryGetValue("editor-id", out var editorIdValue))
        {
            try { editorId = new EditorId(editorIdValue); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        NpcName? name = null;
        if (command.Options.TryGetValue("name", out var nameValue))
        {
            try { name = new NpcName(nameValue); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        if (!TrySex(command, out var sex, out errorMessage)) return false;
        if (!TryArchetype(command, out var archetype, out errorMessage)) return false;
        if (!TryStats(command, edition, out var stats, out errorMessage)) return false;
        if (!TryKeywordPatch(command, edition, out var keywordPatch, out errorMessage)) return false;
        if (!TryFactionPatch(command, out var factionPatch, out errorMessage)) return false;
        if (!TryInventoryPatch(command, out var inventoryPatch, out errorMessage)) return false;
        if (!TryOutfitPatch(command, out var outfitPatch, out errorMessage)) return false;
        if (!TryPerkPatch(command, out var perkPatch, out errorMessage)) return false;
        if (!TryActorEffectPatch(command, out var actorEffectPatch, out errorMessage)) return false;
        if (!TryPropertyPatch(command, out var propertyPatch, out errorMessage)) return false;
        if (!TrySkinPatch(command, out var skin, out errorMessage)) return false;
        if (!TryBodyMorphPatch(command, out var bodyMorphs, out errorMessage)) return false;
        if (!TryWeight(command, out var weight, out errorMessage)) return false;
        if (!TryWholeSkin(command, out var wholeSkin, out errorMessage)) return false;
        if (!TryAidt(command, edition, out var aidt, out errorMessage)) return false;
        Sha256Hash? expected = null;
        string[] suppliedHashes = ["input-sha", "input-sha256", "expected-sha256"];
        string[] hashValues = suppliedHashes
            .Where(command.Options.ContainsKey)
            .Select(name => command.Options[name])
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (hashValues.Length > 1)
        {
            errorMessage = "--input-sha, --input-sha256, and --expected-sha256 must agree when supplied together.";
            return false;
        }
        if (hashValues is [string hashValue])
        {
            try { expected = new Sha256Hash(hashValue); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        WorkspacePath? proposalPath = null;
        if (command.Options.TryGetValue("proposal", out var proposalValue))
        {
            try { proposalPath = new WorkspacePath(proposalValue); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        request = new NpcMutationRequest(edition, input, output, formId, editorId, name, weight, expected, true, proposalPath, sex, archetype, stats, keywordPatch, factionPatch, inventoryPatch, outfitPatch, perkPatch, actorEffectPatch, propertyPatch, skin, bodyMorphs) { WholeSkin = wholeSkin, Aidt = aidt };
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryBuildVerificationRequest(ParsedCommand command, out PluginVerificationRequest request, out string errorMessage)
    {
        request = default!;
        if (!TryGetEdition(command, out var edition, out errorMessage) ||
            !TryPath(command, "source-plugin", out var source, out errorMessage) ||
            !TryPath(command, "output-plugin", out var output, out errorMessage) ||
            !TryFormId(command, out var formId, out errorMessage)) return false;
        var changes = ImmutableArray.CreateBuilder<MutationChange>();
        if (!TryAidt(command, edition, out var aidt, out errorMessage)) return false;
        if (aidt is not null) changes.AddRange(aidt.ToExpectations());
        if (command.Options.TryGetValue("editor-id", out var editorId))
        {
            try { _ = new EditorId(editorId); changes.Add(new MutationChange("EditorID", null, editorId)); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        if (command.Options.TryGetValue("name", out var name))
        {
            try { _ = new NpcName(name); changes.Add(new MutationChange("Name", null, name)); }
            catch (ArgumentException exception) { errorMessage = exception.Message; return false; }
        }
        if (!TrySex(command, out var sex, out errorMessage)) return false;
        if (sex is { } expectedSex) changes.Add(new MutationChange("Sex", null, expectedSex.ToString().ToLowerInvariant()));
        if (!TryArchetype(command, out var archetype, out errorMessage)) return false;
        if (archetype is { } requestedArchetype)
        {
            AddReferenceExpectation(changes, "Race", requestedArchetype.Race);
            AddReferenceExpectation(changes, "Voice", requestedArchetype.Voice);
            AddReferenceExpectation(changes, "Class", requestedArchetype.Class);
            AddReferenceExpectation(changes, "CombatStyle", requestedArchetype.CombatStyle);
        }
        if (!TryStats(command, edition, out var stats, out errorMessage)) return false;
        AddStatsExpectations(changes, stats);
        if (!TryKeywordPatch(command, edition, out var keywordPatch, out errorMessage)) return false;
        AddKeywordExpectations(changes, keywordPatch);
        if (!TryFactionPatch(command, out var factionPatch, out errorMessage)) return false;
        AddFactionExpectations(changes, factionPatch);
        if (!TryInventoryPatch(command, out var inventoryPatch, out errorMessage)) return false;
        AddInventoryExpectations(changes, inventoryPatch);
        if (!TryOutfitPatch(command, out var outfitPatch, out errorMessage)) return false;
        AddOutfitExpectations(changes, outfitPatch);
        if (!TryPerkPatch(command, out var perkPatch, out errorMessage)) return false;
        AddPerkExpectations(changes, perkPatch);
        if (!TryActorEffectPatch(command, out var actorEffectPatch, out errorMessage)) return false;
        AddActorEffectExpectations(changes, actorEffectPatch);
        if (!TryPropertyPatch(command, out var propertyPatch, out errorMessage)) return false;
        AddPropertyExpectations(changes, propertyPatch);
        if (!TrySkinPatch(command, out var skin, out errorMessage)) return false;
        if (skin is { PresetSkinId: not null })
        {
            errorMessage = "plugin verify cannot assert an unverified LooksMenu preset-skin template; use the typed provider route when available.";
            return false;
        }
        if (skin is { IsEmpty: false } && edition != GameEdition.Fallout4)
        {
            errorMessage = "Skyrim SE plugin verification cannot assert Fallout 4 WNAM skin routing.";
            return false;
        }
        AddSkinExpectation(changes, skin);
        if (!TryBodyMorphPatch(command, out var bodyMorphs, out errorMessage)) return false;
        if (bodyMorphs is { IsEmpty: false } && edition != GameEdition.Fallout4)
        {
            errorMessage = "Skyrim SE plugin verification cannot assert Fallout 4 MRSV body-region morphs.";
            return false;
        }
        AddBodyMorphExpectation(changes, bodyMorphs);
        if (!TryWeight(command, out var weight, out errorMessage)) return false;
        if (weight?.SkyrimValue is { } scalar) changes.Add(new MutationChange("SkyrimWeight", null, scalar.ToString("R", CultureInfo.InvariantCulture)));
        if (weight?.Thin is { } thin) changes.Add(new MutationChange("Thin", null, thin.ToString("R", CultureInfo.InvariantCulture)));
        if (weight?.Muscular is { } muscular) changes.Add(new MutationChange("Muscular", null, muscular.ToString("R", CultureInfo.InvariantCulture)));
        if (weight?.Fat is { } fat) changes.Add(new MutationChange("Fat", null, fat.ToString("R", CultureInfo.InvariantCulture)));
        if (!TryWholeSkin(command, out var wholeSkin, out errorMessage)) return false;
        if (changes.Count == 0 && wholeSkin is null) { errorMessage = "plugin verify requires at least one expected identity, archetype, body morph, skin, weight, stats, keyword, faction, inventory, outfit, perk, actor-effect, or property value."; return false; }
        request = new PluginVerificationRequest(edition, source, output, formId, changes.ToImmutable(), ["OBND", "ACBS", "RNAM", "VTCK", "AIDT", "CNAM", "ZNAM", "DATA", "NAM5", "NAM6", "NAM7", "MWGT", "MRSV", "KWDA", "APPR", "SNAM", "CNTO", "DOFT", "SOFT", "WNAM", "PRKR", "SPLO", "PRPS"]);
        request = request with { WholeSkin = wholeSkin };
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryBodyMorphPatch(ParsedCommand command, out NpcBodyMorphPatch? patch, out string errorMessage)
    {
        patch = null;
        if (!command.Options.TryGetValue("regions", out var value))
        {
            errorMessage = string.Empty;
            return true;
        }
        var text = value;
        if (text.StartsWith('@'))
        {
            try
            {
                var path = new WorkspacePath(text[1..]);
                WorkspacePath root = ActorwrightWorkspace.ResolveRoot();
                if (!path.IsUnder(root) || !File.Exists(path.Value) || HasReparsePath(path.Value))
                    throw new FormatException("A regions file must exist under the K-only workspace root and may not traverse a reparse point.");
                text = File.ReadAllText(path.Value);
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or IOException or UnauthorizedAccessException)
            {
                errorMessage = exception.Message;
                return false;
            }
        }
        if (text.Length > 16_384)
        {
            errorMessage = "--regions JSON is too large.";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                errorMessage = "--regions must be a JSON object keyed by head, upperTorso, arms, lowerTorso, or legs.";
                return false;
            }
            var builder = ImmutableDictionary.CreateBuilder<Fallout4BodyRegion, float>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!Fallout4BodyRegionCatalog.TryParseWireName(property.Name, out var region))
                {
                    errorMessage = $"--regions contains unknown Fallout 4 body region '{property.Name}'.";
                    return false;
                }
                if (!builder.TryAdd(region, property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetSingle(out var parsed) ? parsed : float.NaN))
                {
                    errorMessage = $"--regions contains duplicate body region '{property.Name}'.";
                    return false;
                }
            }
            if (builder.Count == 0)
            {
                errorMessage = "--regions must contain at least one body region value.";
                return false;
            }
            patch = new NpcBodyMorphPatch(builder.ToImmutable());
            errorMessage = string.Empty;
            return true;
        }
        catch (JsonException exception)
        {
            errorMessage = $"--regions is not valid JSON: {exception.Message}";
            return false;
        }
    }

    private static bool HasReparsePath(string path)
    {
        var current = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(current))
        {
            if ((File.Exists(current) || Directory.Exists(current)) &&
                File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) return true;
            var parent = Directory.GetParent(current)?.FullName;
            if (string.Equals(parent, current, StringComparison.OrdinalIgnoreCase)) break;
            current = parent ?? string.Empty;
        }
        return false;
    }

    private static bool TrySkinPatch(ParsedCommand command, out NpcSkinPatch? skin, out string errorMessage)
    {
        skin = null;
        var hasSkin = command.Options.TryGetValue("skin", out var skinValue);
        var clearSkin = command.Options.TryGetValue("clear-skin", out var clearValue) && IsTrue(clearValue);
        var hasPreset = command.Options.TryGetValue("preset-skin", out var presetValue);
        if (hasSkin && clearSkin)
        {
            errorMessage = "Use either --skin or --clear-skin, not both.";
            return false;
        }
        OptionalFormReference reference = default;
        if (hasSkin)
        {
            if (!FormReference.TryParse(skinValue!, out var parsed))
            {
                errorMessage = "--skin uses a Plugin.esp|0xXXXXXXXX ARMO reference.";
                return false;
            }
            reference = OptionalFormReference.Set(parsed);
        }
        else if (clearSkin)
        {
            reference = OptionalFormReference.Clear();
        }
        if (hasPreset && string.IsNullOrWhiteSpace(presetValue))
        {
            errorMessage = "--preset-skin requires a non-empty LooksMenu template identifier.";
            return false;
        }
        if (reference.IsSpecified || hasPreset)
            skin = new NpcSkinPatch(reference, hasPreset ? presetValue : null);
        errorMessage = string.Empty;
        return true;
    }

    private static void AddSkinExpectation(ImmutableArray<MutationChange>.Builder changes, NpcSkinPatch? skin)
    {
        if (skin is null || !skin.Fallout4Skin.IsSpecified) return;
        changes.Add(new MutationChange("Skin", null, skin.Fallout4Skin.Value?.ToString() ?? "none"));
    }

    private static void AddBodyMorphExpectation(ImmutableArray<MutationChange>.Builder changes, NpcBodyMorphPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return;
        var values = string.Join(",", Fallout4BodyRegionCatalog.Ordered.Where(patch.Values.ContainsKey)
            .Select(region => $"{region.ToWireName()}={patch.Values[region].ToString("R", CultureInfo.InvariantCulture)}"));
        changes.Add(new MutationChange("BodyMorphRegions", null, $"patch:{values}"));
    }

    private static bool TryWeight(ParsedCommand command, out NpcWeightPatch? weight, out string errorMessage)
    {
        weight = null;
        var scalar = command.Options.TryGetValue("weight", out var scalarValue);
        var triangle = command.Options.TryGetValue("weight-triangle", out var triangleValue);
        if (scalar && triangle) { errorMessage = "Use either --weight or --weight-triangle, not both."; return false; }
        if (scalar)
        {
            if (!float.TryParse(scalarValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) { errorMessage = "--weight must be an invariant-culture number."; return false; }
            weight = new NpcWeightPatch(value, null, null, null);
            errorMessage = string.Empty;
            return true;
        }
        if (triangle)
        {
            float? thin = null, muscular = null, fat = null;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var part in triangleValue!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var pieces = part.Split('=', 2, StringSplitOptions.TrimEntries);
                if (pieces.Length != 2 || !float.TryParse(pieces[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) { errorMessage = "--weight-triangle uses thin|muscular|fat=<number> entries."; return false; }
                if (!seen.Add(pieces[0])) { errorMessage = $"--weight-triangle repeats the '{pieces[0]}' channel."; return false; }
                switch (pieces[0].ToLowerInvariant())
                {
                    case "thin": thin = value; break;
                    case "muscular": muscular = value; break;
                    case "fat": fat = value; break;
                    default: errorMessage = $"Unknown weight-triangle channel '{pieces[0]}'."; return false;
                }
            }
            if (thin is null || muscular is null || fat is null) { errorMessage = "--weight-triangle requires thin, muscular, and fat channels."; return false; }
            if (!WeightTriangleMath.TryNormalize(thin.Value, muscular.Value, fat.Value, out var normalized, out errorMessage)) return false;
            weight = new NpcWeightPatch(null, normalized.Thin, normalized.Muscular, normalized.Fat);
        }
        errorMessage = string.Empty;
        return true;
    }

    private static bool TrySex(ParsedCommand command, out NpcSex? sex, out string errorMessage)
    {
        sex = null;
        if (!command.Options.TryGetValue("sex", out var value))
        {
            errorMessage = string.Empty;
            return true;
        }

        if (string.Equals(value, "male", StringComparison.OrdinalIgnoreCase)) sex = NpcSex.Male;
        else if (string.Equals(value, "female", StringComparison.OrdinalIgnoreCase)) sex = NpcSex.Female;
        else
        {
            errorMessage = "--sex must be male or female.";
            return false;
        }
        errorMessage = string.Empty;
        return true;
    }

    internal static bool TryStats(ParsedCommand command, GameEdition edition, out NpcStatsPatch? stats, out string errorMessage)
    {
        stats = null;
        if (!TryDecimalLevel(command, "level", NpcLevelMode.Fixed, out var fixedLevel, out errorMessage) ||
            !TryDecimalLevel(command, "level-mult", NpcLevelMode.Multiplier, out var multiplier, out errorMessage)) return false;
        if (fixedLevel is not null && multiplier is not null) { errorMessage = "Use either --level or --level-mult, not both."; return false; }
        if (!TryInt16(command, "xp-offset", out var xp, out errorMessage) ||
            !TryInt16(command, "magicka-offset", out var magicka, out errorMessage) ||
            !TryInt16(command, "stamina-offset", out var stamina, out errorMessage) ||
            !TryInt16(command, "health-offset", out var health, out errorMessage) ||
            !TryUInt16(command, "calc-min", out var calcMin, out errorMessage) ||
            !TryUInt16(command, "calc-max", out var calcMax, out errorMessage) ||
            !TryInt16(command, "speed-multiplier", out var speed, out errorMessage) ||
            !TryInt16(command, "disposition", out var disposition, out errorMessage) ||
            !TryInt16(command, "bleedout", out var bleedout, out errorMessage) ||
            !TryFloat(command, "height", out var height, out errorMessage) ||
            !TryPlayerSkills(command, out var playerSkills, out errorMessage) ||
            !TryFlags(command, edition, out var flags, out errorMessage)) return false;
        if (fixedLevel is null && multiplier is null && xp is null && magicka is null && stamina is null && health is null &&
            calcMin is null && calcMax is null && speed is null && disposition is null && bleedout is null && height is null &&
            playerSkills is null && flags is null) return true;
        stats = new NpcStatsPatch(fixedLevel ?? multiplier, xp, magicka, stamina, health, calcMin, calcMax, speed, disposition,
            bleedout, height, playerSkills, flags);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryDecimalLevel(ParsedCommand command, string option, NpcLevelMode mode, out NpcLevelValue? level, out string errorMessage)
    {
        level = null;
        if (!command.Options.TryGetValue(option, out var value)) { errorMessage = string.Empty; return true; }
        if (!decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed))
        { errorMessage = $"--{option} must be an invariant-culture number."; return false; }
        level = new NpcLevelValue(mode, parsed);
        errorMessage = string.Empty;
        return true;
    }

    private static bool TryInt16(ParsedCommand command, string option, out short? value, out string errorMessage)
    {
        value = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        if (!short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) { errorMessage = $"--{option} must be a signed 16-bit integer."; return false; }
        value = parsed; errorMessage = string.Empty; return true;
    }

    private static bool TryUInt16(ParsedCommand command, string option, out ushort? value, out string errorMessage)
    {
        value = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        if (!ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) { errorMessage = $"--{option} must be an unsigned 16-bit integer."; return false; }
        value = parsed; errorMessage = string.Empty; return true;
    }

    private static bool TryFloat(ParsedCommand command, string option, out float? value, out string errorMessage)
    {
        value = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)) { errorMessage = $"--{option} must be an invariant-culture number."; return false; }
        value = parsed; errorMessage = string.Empty; return true;
    }

    private static bool TryPlayerSkills(ParsedCommand command, out NpcPlayerSkillsPatch? skills, out string errorMessage)
    {
        skills = null;
        if (!TryUInt16(command, "player-health", out var health, out errorMessage) ||
            !TryUInt16(command, "player-magicka", out var magicka, out errorMessage) ||
            !TryUInt16(command, "player-stamina", out var stamina, out errorMessage) ||
            !TrySkillMap(command, "skill-values", out var values, out errorMessage) ||
            !TrySkillMap(command, "skill-offsets", out var offsets, out errorMessage) ||
            !TryFloat(command, "far-model-distance", out var farAwayModelDistance, out errorMessage) ||
            !TryByte(command, "geared-weapons", out var gearedUpWeapons, out errorMessage)) return false;
        if (health is null && magicka is null && stamina is null && values is null && offsets is null &&
            farAwayModelDistance is null && gearedUpWeapons is null) return true;
        skills = new NpcPlayerSkillsPatch(health, magicka, stamina, values, offsets,
            farAwayModelDistance, gearedUpWeapons);
        errorMessage = string.Empty; return true;
    }

    private static bool TryByte(ParsedCommand command, string option, out byte? value, out string errorMessage)
    {
        value = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        if (!byte.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) { errorMessage = $"--{option} must be a byte from 0 through 255."; return false; }
        value = parsed; errorMessage = string.Empty; return true;
    }

    private static bool TrySkillMap(ParsedCommand command, string option, out ImmutableDictionary<NpcSkill, byte>? map, out string errorMessage)
    {
        map = null;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableDictionary.CreateBuilder<NpcSkill, byte>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pieces = item.Split('=', 2, StringSplitOptions.TrimEntries);
            if (pieces.Length != 2 || !NpcSkillExtensions.TryParseWireName(pieces[0], out var skill) ||
                !byte.TryParse(pieces[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var value))
            { errorMessage = $"--{option} uses skill=value entries with known skill names and byte values."; return false; }
            if (!builder.TryAdd(skill, value)) { errorMessage = $"--{option} contains duplicate skill {pieces[0]}."; return false; }
        }
        if (builder.Count == 0) { errorMessage = $"--{option} must contain at least one skill=value entry."; return false; }
        map = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static bool TryFlags(ParsedCommand command, GameEdition edition, out NpcFlagPatch? flags, out string errorMessage)
    {
        flags = null;
        if (!TryFlagList(command, edition, "set-flag", out var set, out errorMessage) ||
            !TryFlagList(command, edition, "clear-flag", out var clear, out errorMessage)) return false;
        if (set.IsDefaultOrEmpty && clear.IsDefaultOrEmpty) return true;
        if (set.Intersect(clear).Any()) { errorMessage = "A flag cannot be both set and cleared."; return false; }
        flags = new NpcFlagPatch(set, clear); errorMessage = string.Empty; return true;
    }

    private static bool TryFlagList(ParsedCommand command, GameEdition edition, string option, out ImmutableArray<NpcFlag> flags, out string errorMessage)
    {
        flags = ImmutableArray<NpcFlag>.Empty;
        if (!command.Options.TryGetValue(option, out var text)) { errorMessage = string.Empty; return true; }
        var builder = ImmutableArray.CreateBuilder<NpcFlag>();
        foreach (var item in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var normalized = item.Equals("pc-level-mult", StringComparison.OrdinalIgnoreCase)
                ? (edition == GameEdition.Fallout4 ? nameof(NpcFlag.Fallout4CalcForEachTemplate) : nameof(NpcFlag.SkyrimUseTemplate)) : item;
            if (!NpcFlagExtensions.TryParseWireName(normalized, out var flag) || !NpcFlagExtensions.TryGetMask(edition, flag, out _))
            { errorMessage = $"Unknown or edition-incompatible flag '{item}'."; return false; }
            if (builder.Contains(flag)) { errorMessage = $"--{option} contains duplicate flag '{item}'."; return false; }
            builder.Add(flag);
        }
        flags = builder.ToImmutable(); errorMessage = string.Empty; return true;
    }

    private static void AddStatsExpectations(ImmutableArray<MutationChange>.Builder changes, NpcStatsPatch? stats)
    {
        if (stats is null) return;
        if (stats.Level is { } level) changes.Add(new MutationChange("Level", null, level.ToString()));
        AddExpected(changes, "XpValueOffset", stats.XpValueOffset);
        AddExpected(changes, "MagickaOffset", stats.MagickaOffset);
        AddExpected(changes, "StaminaOffset", stats.StaminaOffset);
        AddExpected(changes, "HealthOffset", stats.HealthOffset);
        AddExpected(changes, "CalcMinLevel", stats.CalcMinLevel);
        AddExpected(changes, "CalcMaxLevel", stats.CalcMaxLevel);
        AddExpected(changes, "SpeedMultiplier", stats.SpeedMultiplier);
        AddExpected(changes, "DispositionBase", stats.DispositionBase);
        AddExpected(changes, "BleedoutOverride", stats.BleedoutOverride);
        AddExpected(changes, "Height", stats.Height);
        if (stats.PlayerSkills is { } skills)
        {
            AddExpected(changes, "PlayerHealth", skills.Health); AddExpected(changes, "PlayerMagicka", skills.Magicka); AddExpected(changes, "PlayerStamina", skills.Stamina);
            if (skills.Values is not null) foreach (var (skill, value) in skills.Values) AddExpected(changes, $"SkillValue:{skill.ToWireName()}", (byte?)value);
            if (skills.Offsets is not null) foreach (var (skill, value) in skills.Offsets) AddExpected(changes, $"SkillOffset:{skill.ToWireName()}", (byte?)value);
        }
        if (stats.Flags is { } flags)
        {
            foreach (var flag in flags.Set) changes.Add(new MutationChange($"Flag:{flag.ToWireName()}", null, "true"));
            foreach (var flag in flags.Clear) changes.Add(new MutationChange($"Flag:{flag.ToWireName()}", null, "false"));
        }
    }

    private static void AddExpected<T>(ImmutableArray<MutationChange>.Builder changes, string field, T? value) where T : struct
    {
        if (value is { } present) changes.Add(new MutationChange(field, null, Convert.ToString(present, CultureInfo.InvariantCulture)));
    }

}
