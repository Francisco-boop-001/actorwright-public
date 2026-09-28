using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;

namespace NpcManager.Formats.Bethesda;

/// <summary>Detached identities from exact copied master and source bytes.</summary>
public sealed class BethesdaSkyrimVanillaDialogueAuthority
{
    private readonly Dictionary<FormKey, (string? EditorId, Type Type)> records = [];
    public Npc Npc { get; }
    public ImmutableArray<string> Masters { get; }
    public bool LightPlugin { get; }
    public FormKey NpcKey => Npc.FormKey;

    public BethesdaSkyrimVanillaDialogueAuthority(SkyrimDialogueManifest manifest, byte[] source,
        IReadOnlyDictionary<string, byte[]> copiedMasters)
    {
        using var stream = new MemoryStream(source, writable: false);
        using var mod = SkyrimMod.CreateFromBinaryOverlay(stream, SkyrimRelease.SkyrimSE, ModKey.FromNameAndExtension(manifest.Npc.Plugin.Value));
        Masters = mod.ModHeader.MasterReferences.Select(x => x.Master.FileName.String).ToImmutableArray();
        LightPlugin = mod.IsSmallMaster;
        var admitted = new HashSet<ModKey>();
        foreach (string name in Masters)
            if (!copiedMasters.ContainsKey(name)) Fail(SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved, "Copied master missing: " + name);
        foreach (string name in copiedMasters.Keys)
        {
            if (!copiedMasters.TryGetValue(name, out byte[]? bytes)) Fail(SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved, "Copied master missing: " + name);
            ModKey key = ModKey.FromNameAndExtension(name);
            using var masterStream = new MemoryStream(bytes!, writable: false);
            using var master = SkyrimMod.CreateFromBinaryOverlay(masterStream, SkyrimRelease.SkyrimSE, key);
            foreach (var dependency in master.ModHeader.MasterReferences)
                if (!admitted.Contains(dependency.Master))
                    Fail(SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved, $"Missing or out-of-order copied master edge {name} -> {dependency.Master.FileName.String}.");
            if (!admitted.Add(key)) Fail(SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved, "Duplicate copied master: " + name);
            Add(master);
        }
        Add(mod);
        FormKey npcKey = new(mod.ModKey, manifest.Npc.FormId.Value);
        INpcGetter[] matches = mod.Npcs.Where(x => x.FormKey == npcKey && !x.IsDeleted).ToArray();
        if (matches.Length != 1 || manifest.Npc.EditorId is { } editor && matches[0].EditorID != editor.Value)
            Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "The source must contain exactly the identified live NPC.");
        Npc = (Npc)matches[0].DeepCopy();
        if (Npc.Configuration.Flags.HasFlag(NpcConfiguration.Flag.Female) != manifest.Npc.Female)
            Fail(SkyrimNpcDialogueDiagnosticCodes.ManifestInvalid, "NPC sex does not match the dialogue identity.");
    }

    private void Add(ISkyrimModGetter mod)
    {
        foreach (var record in mod.EnumerateMajorRecords())
        {
            if (record.IsDeleted) records.Remove(record.FormKey);
            else records[record.FormKey] = (record.EditorID, record.GetType());
        }
    }

    public bool Contains(FormKey key) => records.ContainsKey(key);
    public FormKey Resolve<T>(string? parameter) where T : class, ISkyrimMajorRecordGetter
    {
        if (parameter == "self") return RequireType<T>(NpcKey);
        if (parameter == "player") return RequireType<T>(new(ModKey.FromNameAndExtension("Skyrim.esm"), typeof(IPlacedGetter).IsAssignableFrom(typeof(T)) ? 0x14u : 7u));
        var matches = records.Where(x => string.Equals(x.Value.EditorId, parameter, StringComparison.OrdinalIgnoreCase) && typeof(T).IsAssignableFrom(x.Value.Type)).Select(x => x.Key).ToArray();
        if (string.IsNullOrWhiteSpace(parameter) || matches.Length != 1)
            Fail(SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved, $"EditorID '{parameter}' needs exactly one live {typeof(T).Name}; found {matches.Length}.");
        return matches[0];
    }
    private FormKey RequireType<T>(FormKey key)
    {
        if (!records.TryGetValue(key, out var record) || !typeof(T).IsAssignableFrom(record.Type))
            Fail(SkyrimNpcDialogueDiagnosticCodes.ConditionUnresolved, "Unresolved typed form: " + key);
        return key;
    }

    public ConditionFloat Condition(SkyrimDialogueCondition row)
    {
        if (row.SecondParameter is not null || !float.IsFinite(row.Value))
            Fail(SkyrimNpcDialogueDiagnosticCodes.ConditionUnknown, "Unsupported second parameter or nonfinite comparison.");
        ConditionData data = row.Function switch
        {
            "GetIsID" => IsId(row.Parameter), "GetInFaction" => InFaction(row.Parameter),
            "GetFactionRank" => FactionRank(row.Parameter), "GetGlobalValue" => Global(row.Parameter),
            "GetStage" => Stage(row.Parameter), "GetInCurrentLoc" => Location(row.Parameter),
            "GetLocationCleared" => Cleared(row.Parameter), "GetIsCurrentWeather" => Weather(row.Parameter),
            "GetIsRace" => Race(row.Parameter), "HasKeyword" => Keyword(row.Parameter),
            "LocationHasKeyword" => LocationKeyword(row.Parameter), "GetRelationshipRank" => Relationship(row.Parameter),
            "GetActorValue" => new GetActorValueConditionData { ActorValue = ParseActorValue(row.Parameter) },
            "IsInInterior" when row.Parameter is null => new IsInInteriorConditionData(),
            "GetCurrentTime" when row.Parameter is null => new GetCurrentTimeConditionData(),
            "GetRandomPercent" when row.Parameter is null => new GetRandomPercentConditionData(),
            "IsSneaking" when row.Parameter is null => new IsSneakingConditionData(),
            "IsWeaponOut" when row.Parameter is null => new IsWeaponOutConditionData(),
            "IsInCombat" when row.Parameter is null => new IsInCombatConditionData(),
            _ => throw new InvalidDataException($"{SkyrimNpcDialogueDiagnosticCodes.ConditionUnknown}: Unsupported condition '{row.Function}' or parameter.")
        };
        data.RunOnType = row.RunOn switch
        {
            "subject" => Mutagen.Bethesda.Skyrim.Condition.RunOnType.Subject,
            "target" => Mutagen.Bethesda.Skyrim.Condition.RunOnType.Target,
            "player" => Mutagen.Bethesda.Skyrim.Condition.RunOnType.Reference,
            _ => throw new InvalidDataException($"{SkyrimNpcDialogueDiagnosticCodes.ConditionUnknown}: Unsupported runOn.")
        };
        if (row.RunOn == "player") data.Reference.SetTo(Resolve<IPlacedNpcGetter>("player"));
        return new ConditionFloat
        {
            Data = data, ComparisonValue = row.Value, Flags = row.Or ? Mutagen.Bethesda.Skyrim.Condition.Flag.OR : 0,
            CompareOperator = row.Operator switch
            {
                "==" => CompareOperator.EqualTo, "!=" => CompareOperator.NotEqualTo,
                ">" => CompareOperator.GreaterThan, ">=" => CompareOperator.GreaterThanOrEqualTo,
                "<" => CompareOperator.LessThan, "<=" => CompareOperator.LessThanOrEqualTo,
                _ => throw new InvalidDataException($"{SkyrimNpcDialogueDiagnosticCodes.ConditionUnknown}: Unsupported comparison.")
            }
        };
    }
    private static ActorValue ParseActorValue(string? parameter) => Enum.TryParse(parameter, false, out ActorValue value) && Enum.IsDefined(value)
        ? value : throw new InvalidDataException($"{SkyrimNpcDialogueDiagnosticCodes.ConditionUnknown}: Unknown actor value '{parameter}'.");
    private GetIsIDConditionData IsId(string? p) { var d = new GetIsIDConditionData(); d.Object.Link.SetTo(Resolve<IReferenceableObjectGetter>(p)); return d; }
    private GetInFactionConditionData InFaction(string? p) { var d = new GetInFactionConditionData(); d.Faction.Link.SetTo(Resolve<IFactionGetter>(p)); return d; }
    private GetFactionRankConditionData FactionRank(string? p) { var d = new GetFactionRankConditionData(); d.Faction.Link.SetTo(Resolve<IFactionGetter>(p)); return d; }
    private GetGlobalValueConditionData Global(string? p) { var d = new GetGlobalValueConditionData(); d.Global.Link.SetTo(Resolve<IGlobalGetter>(p)); return d; }
    private GetStageConditionData Stage(string? p) { var d = new GetStageConditionData(); d.Quest.Link.SetTo(Resolve<IQuestGetter>(p)); return d; }
    private GetInCurrentLocConditionData Location(string? p) { var d = new GetInCurrentLocConditionData(); d.Location.Link.SetTo(Resolve<ILocationGetter>(p)); return d; }
    private GetLocationClearedConditionData Cleared(string? p) { var d = new GetLocationClearedConditionData(); d.Location.Link.SetTo(Resolve<ILocationGetter>(p)); return d; }
    private GetIsCurrentWeatherConditionData Weather(string? p) { var d = new GetIsCurrentWeatherConditionData(); d.Weather.Link.SetTo(Resolve<IWeatherGetter>(p)); return d; }
    private GetIsRaceConditionData Race(string? p) { var d = new GetIsRaceConditionData(); d.Race.Link.SetTo(Resolve<IRaceGetter>(p)); return d; }
    private HasKeywordConditionData Keyword(string? p) { var d = new HasKeywordConditionData(); d.Keyword.Link.SetTo(Resolve<IKeywordGetter>(p)); return d; }
    private LocationHasKeywordConditionData LocationKeyword(string? p) { var d = new LocationHasKeywordConditionData(); d.Keyword.Link.SetTo(Resolve<IKeywordGetter>(p)); return d; }
    private GetRelationshipRankConditionData Relationship(string? p) { var d = new GetRelationshipRankConditionData(); d.TargetNpc.Link.SetTo(Resolve<IPlacedNpcGetter>(p)); return d; }
    internal static void Fail(string code, string message) => throw new InvalidDataException(code + ": " + message);
}
