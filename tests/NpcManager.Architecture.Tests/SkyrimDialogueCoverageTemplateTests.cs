using System.Collections.Immutable;
using System.Globalization;
using System.Text.RegularExpressions;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Architecture.Tests;

/// <summary>
/// Behavior tests for the built-in <c>follower-core-v1</c> dialogue coverage
/// template. Static product proof only: no plugin is written, no game runtime
/// claim is made.
/// </summary>
internal static class SkyrimDialogueCoverageTemplateTests
{
    private const string TemplateName = "follower-core-v1";

    private static readonly ImmutableArray<string> RequiredCategories = ImmutableArray.Create(
        "greeting-generic", "greeting-interior", "greeting-exterior-day", "greeting-exterior-night",
        "greeting-rain", "greeting-snow", "greeting-sneaking", "greeting-weapon-drawn",
        "greeting-low-health", "greeting-relationship", "goodbye",
        "recruit", "recruit-refused-full", "follow", "wait", "dismiss", "trade",
        "combat-attack", "combat-hit", "combat-flee", "combat-bleedout", "combat-ally-killed", "combat-taunt",
        "detection-normal-to-combat", "detection-combat-to-normal", "detection-lost-to-normal",
        "idle-dungeon", "idle-city", "idle-inn", "idle-temple", "idle-wilderness", "idle-snow", "idle-rain", "idle-night",
        "quest-main", "quest-civil-war",
        "crime-steal", "crime-assault", "crime-murder", "crime-trespass", "crime-pickpocket");

    /// <summary>Condition function names documented on <see cref="SkyrimDialogueCondition"/>.</summary>
    private static readonly ImmutableHashSet<string> DocumentedFunctions = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "GetIsID", "IsInInterior", "GetInCurrentLoc", "GetIsCurrentWeather", "GetCurrentTime", "GetStage",
        "GetGlobalValue", "IsSneaking", "GetInFaction", "GetFactionRank", "GetRandomPercent",
        "GetRelationshipRank", "GetActorValue", "IsWeaponOut", "GetIsRace", "HasKeyword",
        "LocationHasKeyword", "IsInCombat", "GetLocationCleared");

    /// <summary>
    /// Vanilla EditorIDs (and the two actor-value names) the template may reference.
    /// Follower factions, the follower-count global, and the WaitingForPlayer actor
    /// value are cited in skyrim-dialogue.md section 2; the location keywords,
    /// weather, and quest EditorIDs are the vanilla Skyrim.esm names cited with
    /// their UESP stage tables in coverage-matrix.md.
    /// </summary>
    private static readonly ImmutableHashSet<string> AllowedVanillaParameters = ImmutableHashSet.Create(
        StringComparer.Ordinal,
        "PotentialFollowerFaction", "CurrentFollowerFaction", "DismissedFollowerFaction",
        "PlayerFollowerCount", "WaitingForPlayer", "Health",
        "LocTypeDungeon", "LocTypeCity", "LocTypeInn", "LocTypeTemple",
        "SkyrimOvercastRain", "SkyrimStormRain", "SkyrimOvercastSnow", "SkyrimStormSnow",
        "MQ101", "MQ103", "MQ104", "MQ105", "MQ105Ustengrav", "MQ201", "MQ301", "MQ303",
        "CW01A", "CW01B");

    private static readonly ImmutableHashSet<string> Emotions = ImmutableHashSet.Create(
        StringComparer.Ordinal, "Neutral", "Anger", "Disgust", "Fear", "Sad", "Happy", "Surprise", "Puzzled");

    private static readonly ImmutableHashSet<string> Operators = ImmutableHashSet.Create(
        StringComparer.Ordinal, "==", "!=", ">", ">=", "<", "<=");

    private static readonly ImmutableHashSet<string> RunOnValues = ImmutableHashSet.Create(
        StringComparer.Ordinal, "subject", "target", "player");

    private static readonly Regex KebabCase = new("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant);

    public static void Run()
    {
        var npc = new SkyrimDialogueNpcIdentity(
            new PluginName("ExampleFollower.esp"), new FormId(0x800), new EditorId("ExampleActor"), "AWExample", true);
        var profile = new SkyrimDialogueProfile(
            "Example", ImmutableArray.Create("gentle"), ImmutableArray<string>.Empty, "Riverwood", "hunter");

        TestRegistry();
        var template = SkyrimDialogueCoverageTemplates.TryGet(TemplateName, out var resolved) && resolved is not null
            ? resolved
            : throw new InvalidOperationException("Registry did not resolve " + TemplateName);
        var manifest = template.Create(npc, profile, "en");

        TestManifestShape(manifest, npc, profile, template);
        TestCategoryCoverage(manifest, template);
        TestLineInvariants(manifest);
        TestConditions(manifest);
        TestPrompts(manifest);
        TestActions(manifest);
        TestPrioritiesRepeatThinning(manifest);
        TestBudget(manifest);
        TestDeterminism(template, npc, profile);
        TestExclusions(template, npc, profile);
        TestSparseProfile(template, npc);
        Console.WriteLine(
            "PASS dialogue coverage template: " + manifest.Lines.Length.ToString(CultureInfo.InvariantCulture) +
            " lines, " + manifest.Lines.Select(l => l.Topic).Distinct(StringComparer.Ordinal).Count().ToString(CultureInfo.InvariantCulture) +
            " topics, " + template.Categories.Length.ToString(CultureInfo.InvariantCulture) + " categories");
    }

    private static void TestRegistry()
    {
        Assert(SkyrimDialogueCoverageTemplates.All.Any(t => t.Name == TemplateName), "All lists " + TemplateName);
        Assert(SkyrimDialogueCoverageTemplates.TryGet(TemplateName, out var found) && found?.Name == TemplateName,
            "TryGet resolves " + TemplateName);
        Assert(!SkyrimDialogueCoverageTemplates.TryGet("follower-core-v2", out var missing) && missing is null,
            "TryGet refuses an unknown template name");
        Assert(!SkyrimDialogueCoverageTemplates.TryGet("FOLLOWER-CORE-V1", out _), "TryGet is case-sensitive");
    }

    private static void TestManifestShape(
        SkyrimDialogueManifest manifest, SkyrimDialogueNpcIdentity npc, SkyrimDialogueProfile profile,
        ISkyrimDialogueCoverageTemplate template)
    {
        Assert(manifest.Schema == SkyrimNpcDialogueSchemas.Manifest, "manifest schema");
        Assert(manifest.Npc == npc, "manifest echoes the NPC identity");
        Assert(manifest.Profile == profile, "manifest echoes the profile");
        Assert(manifest.Language == "en", "manifest language");
        Assert(manifest.Template == template.Name, "manifest names the template");
        Assert(manifest.QuestEditorId.StartsWith(npc.VoicePrefix, StringComparison.Ordinal), "quest EditorID uses the voice prefix");
        Assert(manifest.QuestPriority > 0, "quest priority is positive");
        Assert(!manifest.Lines.IsDefaultOrEmpty, "manifest has lines");
    }

    private static void TestCategoryCoverage(SkyrimDialogueManifest manifest, ISkyrimDialogueCoverageTemplate template)
    {
        var lineCategories = manifest.Lines.Select(l => l.Category).Distinct(StringComparer.Ordinal).ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var category in RequiredCategories)
        {
            Assert(template.Categories.Contains(category), "template declares category " + category);
            Assert(lineCategories.Contains(category), "at least one line in category " + category);
        }
        Assert(template.Categories.Distinct(StringComparer.Ordinal).Count() == template.Categories.Length, "Categories has no duplicates");
        var declared = template.Categories.ToImmutableHashSet(StringComparer.Ordinal);
        foreach (var category in lineCategories)
            Assert(declared.Contains(category), "every line category is declared: " + category);
        var orderedFirstUse = manifest.Lines.Select(l => l.Category).Distinct(StringComparer.Ordinal).ToImmutableArray();
        Assert(orderedFirstUse.SequenceEqual(template.Categories), "Categories lists covered categories in template order");
    }

    private static void TestLineInvariants(SkyrimDialogueManifest manifest)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in manifest.Lines)
        {
            Assert(KebabCase.IsMatch(line.Id), "line id is kebab-case: " + line.Id);
            Assert(ids.Add(line.Id), "line id is unique: " + line.Id);
            Assert(!string.IsNullOrWhiteSpace(line.Text), "line text is non-empty: " + line.Id);
            Assert(texts.Add(line.Text), "line text is not duplicated: " + line.Id);
            Assert(Emotions.Contains(line.Emotion), "emotion is a Mutagen emotion: " + line.Id + " " + line.Emotion);
            Assert(line.EmotionValue is >= 30 and <= 70, "emotion value 30-70: " + line.Id);
            Assert(line.Status == "draft", "status is draft: " + line.Id);
            Assert(!string.IsNullOrWhiteSpace(line.PositiveTest), "positive test scenario: " + line.Id);
            Assert(!string.IsNullOrWhiteSpace(line.NegativeTest), "negative test scenario: " + line.Id);
            Assert(!string.IsNullOrWhiteSpace(line.Topic) && line.Topic.StartsWith(manifest.Npc.VoicePrefix, StringComparison.Ordinal),
                "topic EditorID uses the voice prefix: " + line.Id);
            Assert(!string.IsNullOrWhiteSpace(line.Subtype), "subtype is set: " + line.Id);
            Assert(!line.Conditions.IsDefault, "conditions array is initialized: " + line.Id);
        }
    }

    private static void TestConditions(SkyrimDialogueManifest manifest)
    {
        foreach (var line in manifest.Lines)
        {
            foreach (var condition in line.Conditions)
            {
                Assert(DocumentedFunctions.Contains(condition.Function),
                    "condition function is documented: " + line.Id + " " + condition.Function);
                Assert(Operators.Contains(condition.Operator), "condition operator: " + line.Id + " " + condition.Operator);
                Assert(RunOnValues.Contains(condition.RunOn), "condition run-on: " + line.Id + " " + condition.RunOn);
                AssertParameter(line.Id, condition.Parameter);
                AssertParameter(line.Id, condition.SecondParameter);
            }
            if (line.Conditions.Length > 0)
                Assert(!line.Conditions[^1].Or, "last condition does not dangle an OR: " + line.Id);
        }
    }

    private static void AssertParameter(string lineId, string? parameter)
    {
        if (parameter is null) return;
        bool allowed = parameter is "self" or "player" ||
            float.TryParse(parameter, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ||
            AllowedVanillaParameters.Contains(parameter);
        Assert(allowed, "condition parameter is self/player/numeric/allowed vanilla EditorID: " + lineId + " " + parameter);
    }

    private static void TestPrompts(SkyrimDialogueManifest manifest)
    {
        foreach (var line in manifest.Lines)
        {
            if (line.Subtype == "Custom")
                Assert(!string.IsNullOrWhiteSpace(line.Prompt), "Custom topic has a player prompt: " + line.Id);
            else
                Assert(line.Prompt is null, "non-Custom line has no prompt: " + line.Id);
        }
    }

    private static void TestActions(SkyrimDialogueManifest manifest)
    {
        var recruit = Single(manifest, "recruit");
        Assert(recruit.Action == SkyrimDialogueAction.Recruit && recruit.Subtype == "Custom", "recruit is a Custom Recruit action");
        AssertCondition(recruit, "GetInFaction", "PotentialFollowerFaction", "==", 1, "subject");
        AssertCondition(recruit, "GetInFaction", "CurrentFollowerFaction", "==", 0, "subject");
        AssertCondition(recruit, "GetGlobalValue", "PlayerFollowerCount", "==", 0, "subject");

        var refused = Single(manifest, "recruit-refused-full");
        Assert(refused.Action == SkyrimDialogueAction.None && refused.Subtype == "Custom", "refused recruit has no action");
        AssertCondition(refused, "GetInFaction", "PotentialFollowerFaction", "==", 1, "subject");
        AssertCondition(refused, "GetInFaction", "CurrentFollowerFaction", "==", 0, "subject");
        AssertCondition(refused, "GetGlobalValue", "PlayerFollowerCount", "==", 1, "subject");

        var follow = Single(manifest, "follow");
        Assert(follow.Action == SkyrimDialogueAction.Follow, "follow action");
        AssertCondition(follow, "GetInFaction", "CurrentFollowerFaction", "==", 1, "subject");
        AssertCondition(follow, "GetActorValue", "WaitingForPlayer", "==", 1, "subject");

        var wait = Single(manifest, "wait");
        Assert(wait.Action == SkyrimDialogueAction.Wait, "wait action");
        AssertCondition(wait, "GetInFaction", "CurrentFollowerFaction", "==", 1, "subject");
        AssertCondition(wait, "GetActorValue", "WaitingForPlayer", "==", 0, "subject");

        var dismiss = Single(manifest, "dismiss");
        Assert(dismiss.Action == SkyrimDialogueAction.Dismiss, "dismiss action");
        AssertCondition(dismiss, "GetInFaction", "CurrentFollowerFaction", "==", 1, "subject");

        var trade = Single(manifest, "trade");
        Assert(trade.Action == SkyrimDialogueAction.Trade, "trade action");
        AssertCondition(trade, "GetInFaction", "CurrentFollowerFaction", "==", 1, "subject");

        foreach (var line in manifest.Lines)
        {
            if (line.Action != SkyrimDialogueAction.None)
            {
                Assert(line.Subtype == "Custom", "action lines are Custom topics: " + line.Id);
                AssertCondition(line, "GetIsID", "self", "==", 1, "subject");
            }
        }

        var subtypeByCategory = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["combat-attack"] = "Attack", ["combat-hit"] = "Hit", ["combat-flee"] = "Flee",
            ["combat-bleedout"] = "Bleedout", ["combat-ally-killed"] = "AllyKilled", ["combat-taunt"] = "Taunt",
            ["detection-normal-to-combat"] = "NormalToCombat", ["detection-combat-to-normal"] = "CombatToNormal",
            ["detection-lost-to-normal"] = "LostToNormal", ["goodbye"] = "Goodbye",
            ["crime-steal"] = "Steal", ["crime-assault"] = "Assault", ["crime-murder"] = "Murder",
            ["crime-trespass"] = "Trespass", ["crime-pickpocket"] = "PickpocketNC",
            ["idle-dungeon"] = "Idle", ["idle-city"] = "Idle", ["idle-inn"] = "Idle", ["idle-temple"] = "Idle",
            ["idle-wilderness"] = "Idle", ["idle-snow"] = "Idle", ["idle-rain"] = "Idle", ["idle-night"] = "Idle"
        };
        foreach (var line in manifest.Lines)
        {
            if (line.Category.StartsWith("greeting-", StringComparison.Ordinal))
                Assert(line.Subtype == "Hello", "greeting lines use the Hello subtype: " + line.Id);
            else if (subtypeByCategory.TryGetValue(line.Category, out var expected))
                Assert(line.Subtype == expected, "subtype for " + line.Category + ": " + line.Id);
        }

        AssertEvery(manifest, "greeting-interior", l => Has(l, "IsInInterior", null, "==", 1));
        AssertEvery(manifest, "greeting-exterior-day", l => Has(l, "IsInInterior", null, "==", 0) && Has(l, "GetCurrentTime", null, ">=", 6));
        AssertEvery(manifest, "greeting-exterior-night", l => Has(l, "IsInInterior", null, "==", 0) && l.Conditions.Any(c => c.Function == "GetCurrentTime"));
        AssertEvery(manifest, "greeting-rain", l => l.Conditions.Any(c => c.Function == "GetIsCurrentWeather" && c.Parameter is "SkyrimOvercastRain" or "SkyrimStormRain"));
        AssertEvery(manifest, "greeting-snow", l => l.Conditions.Any(c => c.Function == "GetIsCurrentWeather" && c.Parameter is "SkyrimOvercastSnow" or "SkyrimStormSnow"));
        AssertEvery(manifest, "greeting-sneaking", l => Has(l, "IsSneaking", null, "==", 1, "player"));
        AssertEvery(manifest, "greeting-weapon-drawn", l => Has(l, "IsWeaponOut", null, "==", 1, "player"));
        AssertEvery(manifest, "greeting-low-health", l => l.Conditions.Any(c => c.Function == "GetActorValue" && c.Parameter == "Health" && c.Operator is "<" or "<="));
        AssertEvery(manifest, "greeting-relationship", l => Has(l, "GetRelationshipRank", "player", ">=", 3));
        AssertEvery(manifest, "idle-dungeon", l => Has(l, "LocationHasKeyword", "LocTypeDungeon", "==", 1));
        AssertEvery(manifest, "idle-city", l => Has(l, "LocationHasKeyword", "LocTypeCity", "==", 1));
        AssertEvery(manifest, "idle-inn", l => Has(l, "LocationHasKeyword", "LocTypeInn", "==", 1));
        AssertEvery(manifest, "idle-temple", l => Has(l, "LocationHasKeyword", "LocTypeTemple", "==", 1));
        AssertEvery(manifest, "idle-wilderness", l => Has(l, "IsInInterior", null, "==", 0));
        AssertEvery(manifest, "idle-snow", l => l.Conditions.Any(c => c.Function == "GetIsCurrentWeather" && c.Parameter is "SkyrimOvercastSnow" or "SkyrimStormSnow"));
        AssertEvery(manifest, "idle-rain", l => l.Conditions.Any(c => c.Function == "GetIsCurrentWeather" && c.Parameter is "SkyrimOvercastRain" or "SkyrimStormRain"));
        AssertEvery(manifest, "idle-night", l => l.Conditions.Any(c => c.Function == "GetCurrentTime"));

        var mainQuests = new[] { "MQ101", "MQ103", "MQ104", "MQ105", "MQ105Ustengrav", "MQ201", "MQ301", "MQ303" };
        var questLines = manifest.Lines.Where(l => l.Category == "quest-main").ToImmutableArray();
        foreach (var quest in mainQuests)
            Assert(questLines.Any(l => l.Conditions.Any(c => c.Function == "GetStage" && c.Parameter == quest && c.Operator == ">=" && c.Value > 0)),
                "main-quest reaction gated on GetStage " + quest);
        AssertEvery(manifest, "quest-main", l => l.Conditions.Any(c => c.Function == "GetStage" && c.Operator == ">=" && c.Value > 0));
        var civilWar = manifest.Lines.Where(l => l.Category == "quest-civil-war").ToImmutableArray();
        foreach (var quest in new[] { "CW01A", "CW01B" })
            Assert(civilWar.Any(l => l.Conditions.Any(c => c.Function == "GetStage" && c.Parameter == quest && c.Operator == ">=" && c.Value > 0)),
                "civil-war reaction gated on GetStage " + quest);
        AssertEvery(manifest, "quest-civil-war", l => l.Conditions.Any(c => c.Function == "GetStage" && c.Operator == ">=" && c.Value > 0));
    }

    private static void TestPrioritiesRepeatThinning(SkyrimDialogueManifest manifest)
    {
        foreach (var line in manifest.Lines)
        {
            bool action = line.Action != SkyrimDialogueAction.None;
            bool combat = line.Category.StartsWith("combat-", StringComparison.Ordinal);
            bool questReaction = line.Category.StartsWith("quest-", StringComparison.Ordinal);
            bool greeting = line.Category.StartsWith("greeting-", StringComparison.Ordinal);
            bool idle = line.Category.StartsWith("idle-", StringComparison.Ordinal);
            float expectedPriority = action ? 60f : combat ? 40f : 50f;
            Assert(line.Priority == expectedPriority, "priority for " + line.Id);
            if (action) Assert(line.Repeat == SkyrimDialogueRepeatPolicy.Always, "actions repeat Always: " + line.Id);
            if (questReaction) Assert(line.Repeat == SkyrimDialogueRepeatPolicy.Once, "quest reactions say Once: " + line.Id);
            if (greeting || idle || combat) Assert(line.Repeat == SkyrimDialogueRepeatPolicy.Random, "greetings/idles/combat are Random: " + line.Id);
            if (idle) Assert(line.RandomPercent is >= 20 and <= 40, "idle thinning 20-40: " + line.Id);
            if (greeting) Assert(line.RandomPercent is >= 35 and <= 60, "greeting thinning 35-60: " + line.Id);
            Assert(line.RandomPercent is >= 0 and <= 100, "random percent range: " + line.Id);
            if (questReaction) Assert(line.CooldownHours > 0, "quest reactions carry an advisory cooldown: " + line.Id);
            else Assert(line.CooldownHours == 0, "non-quest lines have no cooldown: " + line.Id);
        }
    }

    private static void TestBudget(SkyrimDialogueManifest manifest)
    {
        int topics = manifest.Lines.Select(l => l.Topic).Distinct(StringComparer.Ordinal).Count();
        Assert(manifest.Lines.Length <= 220, "line budget: " + manifest.Lines.Length.ToString(CultureInfo.InvariantCulture));
        Assert(topics <= 70, "topic budget: " + topics.ToString(CultureInfo.InvariantCulture));
        var subtypeByTopic = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in manifest.Lines)
        {
            if (subtypeByTopic.TryGetValue(line.Topic, out var subtype))
                Assert(subtype == line.Subtype, "one subtype per topic: " + line.Topic);
            else
                subtypeByTopic[line.Topic] = line.Subtype;
        }
    }

    private static void TestDeterminism(ISkyrimDialogueCoverageTemplate template, SkyrimDialogueNpcIdentity npc, SkyrimDialogueProfile profile)
    {
        var first = SkyrimNpcVoiceDocumentCodec.Serialize(template.Create(npc, profile, "en"));
        var second = SkyrimNpcVoiceDocumentCodec.Serialize(template.Create(npc, profile, "en"));
        Assert(first.AsSpan().SequenceEqual(second), "two creations serialize to identical bytes");
        var roundTrip = SkyrimNpcVoiceDocumentCodec.Parse<SkyrimDialogueManifest>(first);
        Assert(SkyrimNpcVoiceDocumentCodec.Serialize(roundTrip).AsSpan().SequenceEqual(first), "manifest round-trips through the codec");
    }

    private static void TestExclusions(ISkyrimDialogueCoverageTemplate template, SkyrimDialogueNpcIdentity npc, SkyrimDialogueProfile profile)
    {
        var exclusions = ImmutableArray.Create("greeting-sneaking", "quest-civil-war", "crime-pickpocket");
        var reasons = exclusions.ToImmutableDictionary(category => category, category => "Operator omitted " + category);
        var excluded = template.Create(npc, profile with { Exclusions = exclusions, ExclusionReasons = reasons }, "en");
        Assert(excluded.Profile.Exclusions.SequenceEqual(exclusions), "manifest echoes the exclusions");
        var roundTrip = SkyrimNpcVoiceDocumentCodec.Parse<SkyrimDialogueManifest>(SkyrimNpcVoiceDocumentCodec.Serialize(excluded));
        Assert(reasons.All(pair => roundTrip.Profile.ExclusionReasons?.GetValueOrDefault(pair.Key) == pair.Value), "exclusion reasons survive manifest serialization");
        foreach (var category in exclusions)
            Assert(!excluded.Lines.Any(l => l.Category == category), "excluded category has no lines: " + category);
        var baseline = template.Create(npc, profile, "en");
        int removed = baseline.Lines.Count(l => exclusions.Contains(l.Category));
        Assert(removed > 0 && excluded.Lines.Length == baseline.Lines.Length - removed, "only the excluded categories were removed");
        Assert(template.Categories.Contains("quest-civil-war"), "template Categories still declares the excluded category");

        bool refused = false;
        try { template.Create(npc, profile with { Exclusions = ImmutableArray.Create("not-a-category") }, "en"); }
        catch (ArgumentException) { refused = true; }
        Assert(refused, "unknown exclusion name is refused");
        foreach (var invalid in new[]
        {
            profile with { Exclusions = exclusions },
            profile with { Exclusions = exclusions, ExclusionReasons = reasons.SetItem(exclusions[0], " ") },
            profile with { Exclusions = exclusions, ExclusionReasons = reasons.Remove(exclusions[0]) },
            profile with { ExclusionReasons = reasons }
        })
        {
            refused = false;
            try { template.Create(npc, invalid, "en"); }
            catch (ArgumentException) { refused = true; }
            Assert(refused, "missing, blank, or orphan exclusion reasons are refused");
        }
    }

    private static void TestSparseProfile(ISkyrimDialogueCoverageTemplate template, SkyrimDialogueNpcIdentity npc)
    {
        var sparse = new SkyrimDialogueProfile("Example", ImmutableArray<string>.Empty, ImmutableArray<string>.Empty, null, null);
        var manifest = template.Create(npc, sparse, "en");
        var full = template.Create(npc, sparse with { Home = "Riverwood", Role = "hunter" }, "en");
        Assert(manifest.Lines.Length == full.Lines.Length, "sparse profile keeps every line");
        foreach (var line in manifest.Lines)
        {
            Assert(!string.IsNullOrWhiteSpace(line.Text), "sparse profile text is non-empty: " + line.Id);
            Assert(!line.Text.Contains("null", StringComparison.OrdinalIgnoreCase) && !line.Text.Contains('{') &&
                !line.Text.Contains('}') && !line.Text.Contains("  ", StringComparison.Ordinal),
                "sparse profile text has no placeholder residue: " + line.Id + " => " + line.Text);
            Assert(!line.Text.Contains("Riverwood", StringComparison.Ordinal) && !line.Text.Contains("hunter", StringComparison.Ordinal),
                "sparse profile text does not invent facts: " + line.Id);
            if (line.Id is "greeting-snow-home" or "idle-wilderness-home" or "greeting-generic-watch")
                Assert(!line.Text.Contains("home", StringComparison.OrdinalIgnoreCase) && !line.Text.Contains("I learned", StringComparison.Ordinal),
                    "missing home and role facts must not invent memories or past experience: " + line.Id);
        }
        Assert(full.Lines.Any(l => l.Text.Contains("Riverwood", StringComparison.Ordinal)), "home fact is used when present");
        Assert(full.Lines.Any(l => l.Text.Contains("hunter", StringComparison.Ordinal)), "role fact is used when present");
        Assert(full.Lines.Any(l => l.Text.Contains("Example", StringComparison.Ordinal)), "name fact is used");

        bool refused = false;
        try { template.Create(npc, sparse with { Name = " " }, "en"); }
        catch (ArgumentException) { refused = true; }
        Assert(refused, "blank profile name is refused");
    }

    private static SkyrimDialogueLine Single(SkyrimDialogueManifest manifest, string category)
    {
        var lines = manifest.Lines.Where(l => l.Category == category).ToImmutableArray();
        Assert(lines.Length == 1, "exactly one line in category " + category);
        return lines[0];
    }

    private static void AssertEvery(SkyrimDialogueManifest manifest, string category, Func<SkyrimDialogueLine, bool> predicate)
    {
        var lines = manifest.Lines.Where(l => l.Category == category).ToImmutableArray();
        Assert(lines.Length > 0, "category present: " + category);
        foreach (var line in lines)
            Assert(predicate(line), "condition shape for " + category + ": " + line.Id);
    }

    private static bool Has(SkyrimDialogueLine line, string function, string? parameter, string op, float value, string runOn = "subject") =>
        line.Conditions.Any(c => c.Function == function && c.Parameter == parameter && c.Operator == op && c.Value == value && c.RunOn == runOn);

    private static void AssertCondition(SkyrimDialogueLine line, string function, string? parameter, string op, float value, string runOn) =>
        Assert(Has(line, function, parameter, op, value, runOn),
            line.Id + " has " + function + " " + (parameter ?? "-") + " " + op + " " + value.ToString(CultureInfo.InvariantCulture) + " on " + runOn);

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("FAIL " + message);
    }
}
