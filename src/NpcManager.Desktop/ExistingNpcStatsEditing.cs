using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Globalization;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Desktop;

public sealed record ExistingNpcStatsValues(
    NpcLevelValue Level,
    short MagickaOffset,
    short StaminaOffset,
    short HealthOffset,
    ushort CalcMinLevel,
    ushort CalcMaxLevel,
    short SpeedMultiplier,
    short DispositionBase,
    short BleedoutOverride,
    ushort PlayerHealth,
    ushort PlayerMagicka,
    ushort PlayerStamina,
    ImmutableDictionary<NpcSkill, byte> SkillValues,
    ImmutableDictionary<NpcSkill, byte> SkillOffsets,
    float FarAwayModelDistance,
    byte GearedUpWeapons,
    ImmutableHashSet<NpcFlag> Flags);

/// <summary>
/// Owns the committed, hash-bound Skyrim ACBS/DNAM editor state. The modal
/// receives a copy and can only commit a fully parsed value set.
/// </summary>
public sealed class ExistingNpcStatsState
{
    private ExistingNpcStatsValues? baseline;
    private ExistingNpcStatsValues? current;
    private NpcStatsPatch? initialPatch;

    public static ImmutableArray<NpcFlag> EditableFlags { get; } =
    [
        NpcFlag.Essential,
        NpcFlag.Respawn,
        NpcFlag.AutoCalcStats,
        NpcFlag.Unique,
        NpcFlag.DoesntAffectStealthMeter,
        NpcFlag.Protected,
        NpcFlag.Summonable,
        NpcFlag.DoesNotBleed,
        NpcFlag.OppositeGenderAnims,
        NpcFlag.SimpleActor,
        NpcFlag.IsGhost,
        NpcFlag.Invulnerable
    ];

    public bool IsLoaded => baseline is not null && current is not null;
    public bool HasChanges => BuildPatch() is not null;
    public string Summary => current is null
        ? "Load the selected NPC before editing its statistics."
        : $"Level {current.Level} · {current.SkillValues.Count} skills · " +
          $"{current.Flags.Count(flag => EditableFlags.Contains(flag))} enabled gameplay flags";

    public event EventHandler? Changed;

    public void InitializePatch(NpcStatsPatch? patch) => initialPatch = patch;

    public void Load(NpcOverrideSourceSnapshot snapshot)
    {
        baseline = From(snapshot.Stats);
        current = ApplyPatch(baseline, initialPatch);
        initialPatch = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Clear(bool discardInitialPatch = false)
    {
        if (discardInitialPatch) initialPatch = null;
        if (!IsLoaded) return;
        baseline = null;
        current = null;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public ExistingNpcStatsEditorViewModel CreateEditor()
    {
        if (current is null)
            throw new InvalidOperationException("Load a hash-bound NPC snapshot before editing statistics.");
        return new ExistingNpcStatsEditorViewModel(current);
    }

    public void Commit(ExistingNpcStatsEditorViewModel editor)
    {
        ArgumentNullException.ThrowIfNull(editor);
        if (editor.AcceptedValues is not { } values)
            throw new InvalidOperationException("Only a validated statistics editor can be committed.");
        current = values;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public NpcStatsPatch? BuildPatch()
    {
        if (baseline is null || current is null) return null;
        var values = ChangedSkills(baseline.SkillValues, current.SkillValues);
        var offsets = ChangedSkills(baseline.SkillOffsets, current.SkillOffsets);
        var playerSkills = baseline.PlayerHealth == current.PlayerHealth &&
                           baseline.PlayerMagicka == current.PlayerMagicka &&
                           baseline.PlayerStamina == current.PlayerStamina &&
                           values is null && offsets is null &&
                           baseline.FarAwayModelDistance == current.FarAwayModelDistance &&
                           baseline.GearedUpWeapons == current.GearedUpWeapons
            ? null
            : new NpcPlayerSkillsPatch(
                baseline.PlayerHealth == current.PlayerHealth ? null : current.PlayerHealth,
                baseline.PlayerMagicka == current.PlayerMagicka ? null : current.PlayerMagicka,
                baseline.PlayerStamina == current.PlayerStamina ? null : current.PlayerStamina,
                values,
                offsets,
                baseline.FarAwayModelDistance == current.FarAwayModelDistance
                    ? null
                    : current.FarAwayModelDistance,
                baseline.GearedUpWeapons == current.GearedUpWeapons
                    ? null
                    : current.GearedUpWeapons);
        var setFlags = EditableFlags
            .Where(flag => !baseline.Flags.Contains(flag) && current.Flags.Contains(flag))
            .ToImmutableArray();
        var clearFlags = EditableFlags
            .Where(flag => baseline.Flags.Contains(flag) && !current.Flags.Contains(flag))
            .ToImmutableArray();
        var flags = setFlags.IsEmpty && clearFlags.IsEmpty
            ? null
            : new NpcFlagPatch(setFlags, clearFlags);
        var patch = new NpcStatsPatch(
            baseline.Level == current.Level ? null : current.Level,
            null,
            baseline.MagickaOffset == current.MagickaOffset ? null : current.MagickaOffset,
            baseline.StaminaOffset == current.StaminaOffset ? null : current.StaminaOffset,
            baseline.HealthOffset == current.HealthOffset ? null : current.HealthOffset,
            baseline.CalcMinLevel == current.CalcMinLevel ? null : current.CalcMinLevel,
            baseline.CalcMaxLevel == current.CalcMaxLevel ? null : current.CalcMaxLevel,
            baseline.SpeedMultiplier == current.SpeedMultiplier ? null : current.SpeedMultiplier,
            baseline.DispositionBase == current.DispositionBase ? null : current.DispositionBase,
            baseline.BleedoutOverride == current.BleedoutOverride ? null : current.BleedoutOverride,
            null,
            playerSkills,
            flags);
        return patch.IsEmpty ? null : patch;
    }

    private static ExistingNpcStatsValues From(NpcStatsSnapshot snapshot)
    {
        var skills = snapshot.PlayerSkills ?? new NpcPlayerSkillsSnapshot(
            0, 0, 0,
            Enum.GetValues<NpcSkill>().ToImmutableDictionary(skill => skill, _ => (byte)0),
            Enum.GetValues<NpcSkill>().ToImmutableDictionary(skill => skill, _ => (byte)0));
        return new ExistingNpcStatsValues(
            snapshot.Level,
            snapshot.MagickaOffset,
            snapshot.StaminaOffset,
            snapshot.HealthOffset,
            snapshot.CalcMinLevel,
            snapshot.CalcMaxLevel,
            snapshot.SpeedMultiplier,
            snapshot.DispositionBase,
            snapshot.BleedoutOverride,
            skills.Health,
            skills.Magicka,
            skills.Stamina,
            CompleteSkills(skills.Values),
            CompleteSkills(skills.Offsets),
            skills.FarAwayModelDistance,
            skills.GearedUpWeapons,
            snapshot.Flags);
    }

    private static ExistingNpcStatsValues ApplyPatch(
        ExistingNpcStatsValues baseline,
        NpcStatsPatch? patch)
    {
        if (patch is null || patch.IsEmpty) return baseline;
        var skills = patch.PlayerSkills;
        var values = ApplySkills(baseline.SkillValues, skills?.Values);
        var offsets = ApplySkills(baseline.SkillOffsets, skills?.Offsets);
        var flags = baseline.Flags.ToBuilder();
        if (patch.Flags is { } flagPatch)
        {
            flags.UnionWith(flagPatch.Set);
            flags.ExceptWith(flagPatch.Clear);
        }
        return baseline with
        {
            Level = patch.Level ?? baseline.Level,
            MagickaOffset = patch.MagickaOffset ?? baseline.MagickaOffset,
            StaminaOffset = patch.StaminaOffset ?? baseline.StaminaOffset,
            HealthOffset = patch.HealthOffset ?? baseline.HealthOffset,
            CalcMinLevel = patch.CalcMinLevel ?? baseline.CalcMinLevel,
            CalcMaxLevel = patch.CalcMaxLevel ?? baseline.CalcMaxLevel,
            SpeedMultiplier = patch.SpeedMultiplier ?? baseline.SpeedMultiplier,
            DispositionBase = patch.DispositionBase ?? baseline.DispositionBase,
            BleedoutOverride = patch.BleedoutOverride ?? baseline.BleedoutOverride,
            PlayerHealth = skills?.Health ?? baseline.PlayerHealth,
            PlayerMagicka = skills?.Magicka ?? baseline.PlayerMagicka,
            PlayerStamina = skills?.Stamina ?? baseline.PlayerStamina,
            SkillValues = values,
            SkillOffsets = offsets,
            FarAwayModelDistance = skills?.FarAwayModelDistance ?? baseline.FarAwayModelDistance,
            GearedUpWeapons = skills?.GearedUpWeapons ?? baseline.GearedUpWeapons,
            Flags = flags.ToImmutable()
        };
    }

    private static ImmutableDictionary<NpcSkill, byte> CompleteSkills(
        ImmutableDictionary<NpcSkill, byte> source) =>
        Enum.GetValues<NpcSkill>().ToImmutableDictionary(
            skill => skill,
            skill => source.GetValueOrDefault(skill));

    private static ImmutableDictionary<NpcSkill, byte> ApplySkills(
        ImmutableDictionary<NpcSkill, byte> source,
        ImmutableDictionary<NpcSkill, byte>? patch)
    {
        if (patch is null) return source;
        var result = source.ToBuilder();
        foreach (var (skill, value) in patch) result[skill] = value;
        return result.ToImmutable();
    }

    private static ImmutableDictionary<NpcSkill, byte>? ChangedSkills(
        ImmutableDictionary<NpcSkill, byte> baseline,
        ImmutableDictionary<NpcSkill, byte> current)
    {
        var changed = current
            .Where(pair => baseline.GetValueOrDefault(pair.Key) != pair.Value)
            .ToImmutableDictionary();
        return changed.IsEmpty ? null : changed;
    }
}

public sealed class ExistingNpcStatsEditorViewModel : NotifyViewModel
{
    public ExistingNpcStatsEditorViewModel(ExistingNpcStatsValues current)
    {
        LevelMode = current.Level.Mode;
        Level = current.Level.Value.ToString(CultureInfo.InvariantCulture);
        MagickaOffset = current.MagickaOffset.ToString(CultureInfo.InvariantCulture);
        StaminaOffset = current.StaminaOffset.ToString(CultureInfo.InvariantCulture);
        HealthOffset = current.HealthOffset.ToString(CultureInfo.InvariantCulture);
        CalcMinLevel = current.CalcMinLevel.ToString(CultureInfo.InvariantCulture);
        CalcMaxLevel = current.CalcMaxLevel.ToString(CultureInfo.InvariantCulture);
        SpeedMultiplier = current.SpeedMultiplier.ToString(CultureInfo.InvariantCulture);
        DispositionBase = current.DispositionBase.ToString(CultureInfo.InvariantCulture);
        BleedoutOverride = current.BleedoutOverride.ToString(CultureInfo.InvariantCulture);
        PlayerHealth = current.PlayerHealth.ToString(CultureInfo.InvariantCulture);
        PlayerMagicka = current.PlayerMagicka.ToString(CultureInfo.InvariantCulture);
        PlayerStamina = current.PlayerStamina.ToString(CultureInfo.InvariantCulture);
        FarAwayModelDistance = current.FarAwayModelDistance.ToString("R", CultureInfo.InvariantCulture);
        GearedUpWeapons = current.GearedUpWeapons.ToString(CultureInfo.InvariantCulture);
        Skills = new ObservableCollection<ExistingNpcSkillRowViewModel>(
            Enum.GetValues<NpcSkill>().Select(skill => new ExistingNpcSkillRowViewModel(
                skill,
                current.SkillValues.GetValueOrDefault(skill).ToString(CultureInfo.InvariantCulture),
                current.SkillOffsets.GetValueOrDefault(skill).ToString(CultureInfo.InvariantCulture))));
        Flags = new ObservableCollection<ExistingNpcFlagRowViewModel>(
            ExistingNpcStatsState.EditableFlags.Select(flag => new ExistingNpcFlagRowViewModel(
                flag, current.Flags.Contains(flag))));
    }

    public IReadOnlyList<NpcLevelMode> LevelModes { get; } = Enum.GetValues<NpcLevelMode>();
    public ObservableCollection<ExistingNpcSkillRowViewModel> Skills { get; }
    public ObservableCollection<ExistingNpcFlagRowViewModel> Flags { get; }

    private NpcLevelMode levelMode;
    public NpcLevelMode LevelMode { get => levelMode; set => Set(ref levelMode, value); }

    private string level = string.Empty;
    public string Level { get => level; set => Set(ref level, value); }

    private string magickaOffset = string.Empty;
    public string MagickaOffset { get => magickaOffset; set => Set(ref magickaOffset, value); }

    private string staminaOffset = string.Empty;
    public string StaminaOffset { get => staminaOffset; set => Set(ref staminaOffset, value); }

    private string healthOffset = string.Empty;
    public string HealthOffset { get => healthOffset; set => Set(ref healthOffset, value); }

    private string calcMinLevel = string.Empty;
    public string CalcMinLevel { get => calcMinLevel; set => Set(ref calcMinLevel, value); }

    private string calcMaxLevel = string.Empty;
    public string CalcMaxLevel { get => calcMaxLevel; set => Set(ref calcMaxLevel, value); }

    private string speedMultiplier = string.Empty;
    public string SpeedMultiplier { get => speedMultiplier; set => Set(ref speedMultiplier, value); }

    private string dispositionBase = string.Empty;
    public string DispositionBase { get => dispositionBase; set => Set(ref dispositionBase, value); }

    private string bleedoutOverride = string.Empty;
    public string BleedoutOverride { get => bleedoutOverride; set => Set(ref bleedoutOverride, value); }

    private string playerHealth = string.Empty;
    public string PlayerHealth { get => playerHealth; set => Set(ref playerHealth, value); }

    private string playerMagicka = string.Empty;
    public string PlayerMagicka { get => playerMagicka; set => Set(ref playerMagicka, value); }

    private string playerStamina = string.Empty;
    public string PlayerStamina { get => playerStamina; set => Set(ref playerStamina, value); }

    private string farAwayModelDistance = string.Empty;
    public string FarAwayModelDistance
    {
        get => farAwayModelDistance;
        set => Set(ref farAwayModelDistance, value);
    }

    private string gearedUpWeapons = string.Empty;
    public string GearedUpWeapons { get => gearedUpWeapons; set => Set(ref gearedUpWeapons, value); }

    private string validationMessage = string.Empty;
    public string ValidationMessage
    {
        get => validationMessage;
        private set => Set(ref validationMessage, value);
    }

    public ExistingNpcStatsValues? AcceptedValues { get; private set; }

    public bool TryAccept()
    {
        AcceptedValues = null;
        if (!decimal.TryParse(Level, NumberStyles.Number, CultureInfo.InvariantCulture, out var levelValue) ||
            !new NpcLevelValue(LevelMode, levelValue).TryGetRaw(out _))
            return Refuse("Level must be 0–65535 when fixed, or 0–65.535 with at most three decimals when multiplied.");
        if (!TryInt16(MagickaOffset, "Magicka offset", out var magickaOffset) ||
            !TryInt16(StaminaOffset, "Stamina offset", out var staminaOffset) ||
            !TryInt16(HealthOffset, "Health offset", out var healthOffset) ||
            !TryUInt16(CalcMinLevel, "Minimum calculated level", out var calcMin) ||
            !TryUInt16(CalcMaxLevel, "Maximum calculated level", out var calcMax) ||
            !TryInt16(SpeedMultiplier, "Speed multiplier", out var speedMultiplier) ||
            !TryInt16(DispositionBase, "Disposition", out var disposition) ||
            !TryInt16(BleedoutOverride, "Bleedout override", out var bleedout) ||
            !TryUInt16(PlayerHealth, "Base health", out var playerHealth) ||
            !TryUInt16(PlayerMagicka, "Base magicka", out var playerMagicka) ||
            !TryUInt16(PlayerStamina, "Base stamina", out var playerStamina) ||
            !byte.TryParse(GearedUpWeapons, NumberStyles.Integer, CultureInfo.InvariantCulture,
                out var gearedUpWeapons))
        {
            if (string.IsNullOrEmpty(ValidationMessage))
                ValidationMessage = "Geared weapons must be between 0 and 255.";
            return false;
        }
        if (calcMin > calcMax)
            return Refuse("Minimum calculated level cannot exceed maximum calculated level.");
        if (!float.TryParse(FarAwayModelDistance, NumberStyles.Float,
                CultureInfo.InvariantCulture, out var farAwayModelDistance) ||
            !float.IsFinite(farAwayModelDistance) ||
            farAwayModelDistance is < -1_000_000f or > 1_000_000f)
            return Refuse("Far-away model distance must be finite and between -1000000 and 1000000.");

        var values = ImmutableDictionary.CreateBuilder<NpcSkill, byte>();
        var offsets = ImmutableDictionary.CreateBuilder<NpcSkill, byte>();
        foreach (var row in Skills)
        {
            if (!byte.TryParse(row.Value, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var value) ||
                !byte.TryParse(row.Offset, NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out var offset))
                return Refuse($"{row.DisplayName} value and offset must each be between 0 and 255.");
            values[row.Skill] = value;
            offsets[row.Skill] = offset;
        }

        ValidationMessage = string.Empty;
        AcceptedValues = new ExistingNpcStatsValues(
            new NpcLevelValue(LevelMode, levelValue),
            magickaOffset,
            staminaOffset,
            healthOffset,
            calcMin,
            calcMax,
            speedMultiplier,
            disposition,
            bleedout,
            playerHealth,
            playerMagicka,
            playerStamina,
            values.ToImmutable(),
            offsets.ToImmutable(),
            farAwayModelDistance,
            gearedUpWeapons,
            Flags.Where(row => row.IsEnabled).Select(row => row.Flag).ToImmutableHashSet());
        return true;
    }

    private bool TryInt16(string text, string label, out short value)
    {
        if (short.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        ValidationMessage = $"{label} must be a signed 16-bit integer.";
        return false;
    }

    private bool TryUInt16(string text, string label, out ushort value)
    {
        if (ushort.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
            return true;
        ValidationMessage = $"{label} must be between 0 and 65535.";
        return false;
    }

    private bool Refuse(string message)
    {
        ValidationMessage = message;
        return false;
    }
}

public sealed class ExistingNpcSkillRowViewModel(
    NpcSkill skill,
    string value,
    string offset) : NotifyViewModel
{
    public NpcSkill Skill { get; } = skill;
    public string DisplayName { get; } = SplitName(skill.ToString());

    private string value = value;
    public string Value { get => value; set => Set(ref this.value, value); }

    private string offset = offset;
    public string Offset { get => offset; set => Set(ref offset, value); }

    private static string SplitName(string value) =>
        string.Concat(value.Select((character, index) =>
            index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}

public sealed class ExistingNpcFlagRowViewModel(
    NpcFlag flag,
    bool isEnabled) : NotifyViewModel
{
    public NpcFlag Flag { get; } = flag;
    public string DisplayName { get; } = flag.ToWireName().Replace('-', ' ');

    private bool isEnabled = isEnabled;
    public bool IsEnabled { get => isEnabled; set => Set(ref isEnabled, value); }
}
