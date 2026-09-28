using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;

namespace NpcManager.Application;

/// <summary>
/// Registry of built-in dialogue coverage templates. Templates are pure
/// functions of the NPC identity and profile: they write nothing, resolve
/// nothing, and never invent biography. All wording is original Actorwright
/// text filled only with profile facts (name, home, role) and neutral phrasing
/// when a fact is absent.
/// </summary>
public static class SkyrimDialogueCoverageTemplates
{
    public static ImmutableArray<ISkyrimDialogueCoverageTemplate> All { get; } =
        ImmutableArray.Create<ISkyrimDialogueCoverageTemplate>(new FollowerCoreV1());

    public static bool TryGet(string name, [NotNullWhen(true)] out ISkyrimDialogueCoverageTemplate? template)
    {
        foreach (var candidate in All)
        {
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal))
            {
                template = candidate;
                return true;
            }
        }

        template = null;
        return false;
    }

    /// <summary>
    /// The Serana-derived follower core: greetings, goodbyes, the five vanilla
    /// <c>DialogueFollower</c> actions, combat and detection barks, conditional
    /// world idles, stage-gated main-quest and civil-war reactions, witnessed
    /// crimes, and relationship-rank greetings. Vanilla EditorIDs referenced by
    /// conditions are resolved later by <c>analyze</c> against the copied masters.
    /// </summary>
    private sealed class FollowerCoreV1 : ISkyrimDialogueCoverageTemplate
    {
        private const string Draft = "draft";
        private const float StandardPriority = 50f;
        private const float ActionPriority = 60f;
        private const float CombatPriority = 40f;
        private const int GreetingThinning = 45;
        private const int GenericGreetingThinning = 50;
        private const int LowHealthGreetingThinning = 60;
        private const int IdleThinning = 30;
        private const int QuestReactionCooldownHours = 24;
        private const byte QuestPriority = 60;

        public string Name => "follower-core-v1";

        public ImmutableArray<string> Categories { get; } = ImmutableArray.Create(
            "greeting-generic", "greeting-interior", "greeting-exterior-day", "greeting-exterior-night",
            "greeting-rain", "greeting-snow", "greeting-sneaking", "greeting-weapon-drawn",
            "greeting-low-health", "greeting-relationship", "goodbye",
            "recruit", "recruit-refused-full", "follow", "wait", "dismiss", "trade",
            "combat-attack", "combat-hit", "combat-flee", "combat-bleedout", "combat-ally-killed", "combat-taunt",
            "detection-normal-to-combat", "detection-combat-to-normal", "detection-lost-to-normal",
            "idle-dungeon", "idle-city", "idle-inn", "idle-temple", "idle-wilderness", "idle-snow", "idle-rain", "idle-night",
            "quest-main", "quest-civil-war",
            "crime-steal", "crime-assault", "crime-murder", "crime-trespass", "crime-pickpocket");

        public SkyrimDialogueManifest Create(SkyrimDialogueNpcIdentity npc, SkyrimDialogueProfile profile, string language)
        {
            ArgumentNullException.ThrowIfNull(npc);
            ArgumentNullException.ThrowIfNull(profile);
            if (string.IsNullOrWhiteSpace(language))
                throw new ArgumentException("Manifest language must be non-empty.", nameof(language));
            if (string.IsNullOrWhiteSpace(profile.Name))
                throw new ArgumentException("Profile name must be non-empty.", nameof(profile));
            if (string.IsNullOrWhiteSpace(npc.VoicePrefix))
                throw new ArgumentException("NPC voice prefix must be non-empty.", nameof(npc));

            var exclusions = profile.Exclusions.IsDefault ? ImmutableArray<string>.Empty : profile.Exclusions;
            foreach (var exclusion in exclusions)
            {
                if (!Categories.Contains(exclusion, StringComparer.Ordinal))
                    throw new ArgumentException("Unknown template category in profile exclusions: " + exclusion, nameof(profile));
                if (profile.ExclusionReasons is null || !profile.ExclusionReasons.TryGetValue(exclusion, out string? reason) || string.IsNullOrWhiteSpace(reason))
                    throw new ArgumentException("Each excluded category requires a nonblank profile.exclusionReasons entry: " + exclusion, nameof(profile));
            }

            var excluded = exclusions.ToImmutableHashSet(StringComparer.Ordinal);
            if (profile.ExclusionReasons is not null && profile.ExclusionReasons.Keys.Any(category => !excluded.Contains(category)))
                throw new ArgumentException("Exclusion reasons must refer to excluded categories.", nameof(profile));
            var all = BuildLines(new Facts(npc.VoicePrefix, profile.Name.Trim(), Trimmed(profile.Home), Trimmed(profile.Role)));
            var lines = all.Where(line => !excluded.Contains(line.Category)).ToImmutableArray();
            return new SkyrimDialogueManifest(
                SkyrimNpcDialogueSchemas.Manifest,
                npc,
                language,
                profile with { Exclusions = exclusions },
                npc.VoicePrefix + "Dialogue",
                QuestPriority,
                Name,
                lines);
        }

        private static string? Trimmed(string? value) =>
            string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        private sealed record Facts(string Prefix, string Name, string? Home, string? Role);

        private static ImmutableArray<SkyrimDialogueLine> BuildLines(Facts facts)
        {
            var lines = ImmutableArray.CreateBuilder<SkyrimDialogueLine>();
            string prefix = facts.Prefix;
            string name = facts.Name;
            string homeReminder = facts.Home is null ? "The snow is settling around us." : "Snow like this reminds me of " + facts.Home + ".";
            string dismissText = facts.Home is null
                ? "Then this is where we part. Find me if you change your mind."
                : "Then I will head back toward " + facts.Home + ". Find me there if you change your mind.";
            string wildernessHome = facts.Home is null
                ? "There is open country ahead of us."
                : "Out here, I could almost be walking back to " + facts.Home + ".";
            string watchText = facts.Role is null
                ? "I am keeping watch."
                : "A " + facts.Role + " learns to keep watch. I am keeping watch now.";
            string cityRole = facts.Role is null
                ? "Crowds. I never know what to make of them."
                : "A " + facts.Role + " in the city. Nobody knows what to make of me.";

            // Greetings (Hello subtype; Random with thinning).
            Hello("greeting-generic-name", "greeting-generic", "You can call me " + name + ". What do you need?", "Neutral", 40, GenericGreetingThinning,
                Empty, "Approach the NPC anywhere with no other greeting condition met", "Must not replace a conditioned greeting every time; thinning keeps it occasional");
            Hello("greeting-generic-mind", "greeting-generic", "Something on your mind?", "Neutral", 35, GenericGreetingThinning,
                Empty, "Approach the NPC anywhere", "Must not fire for a different NPC (quest-level GetIsID)");
            Hello("greeting-generic-watch", "greeting-generic", watchText, "Neutral", 45, GenericGreetingThinning,
                Empty, "Approach the NPC anywhere", "Text must not name a role when the profile has none");
            Hello("greeting-interior-walls", "greeting-interior", "Walls around us for once. I can breathe a little.", "Happy", 40, GreetingThinning,
                Interior, "Approach indoors", "Must not fire outdoors");
            Hello("greeting-interior-quiet", "greeting-interior", "Quiet in here. Say what you came to say.", "Neutral", 35, GreetingThinning,
                Interior, "Approach indoors", "Must not fire outdoors");
            Hello("greeting-exterior-day-light", "greeting-exterior-day", "Good light today. Whatever we do, let us do it before it fades.", "Happy", 45, GreetingThinning,
                ExteriorDay, "Approach outdoors between 06:00 and 20:00", "Must not fire indoors or at night");
            Hello("greeting-exterior-day-road", "greeting-exterior-day", "Daylight and open road. I could get used to this.", "Happy", 50, GreetingThinning,
                ExteriorDay, "Approach outdoors between 06:00 and 20:00", "Must not fire indoors or at night");
            Hello("greeting-exterior-night-late", "greeting-exterior-night", "Late to be wandering. Keep your eyes open out here.", "Neutral", 40, GreetingThinning,
                ExteriorNight, "Approach outdoors after 20:00 or before 05:00", "Must not fire indoors or in daylight");
            Hello("greeting-exterior-night-stars", "greeting-exterior-night", "The stars are out. I will keep watch while you talk.", "Neutral", 35, GreetingThinning,
                ExteriorNight, "Approach outdoors after 20:00 or before 05:00", "Must not fire indoors or in daylight");
            Hello("greeting-rain-everything", "greeting-rain", "This rain finds its way into everything. Make it quick.", "Disgust", 40, GreetingThinning,
                Rain, "Approach during overcast or storm rain", "Must not fire in clear weather or snow");
            Hello("greeting-rain-tracks", "greeting-rain", "Rain again. At least it hides our tracks.", "Neutral", 35, GreetingThinning,
                Rain, "Approach during overcast or storm rain", "Must not fire in clear weather or snow");
            Hello("greeting-snow-footing", "greeting-snow", "Snow on the road. Watch your footing.", "Neutral", 40, GreetingThinning,
                Snow, "Approach during overcast or storm snow", "Must not fire in clear weather or rain");
            Hello("greeting-snow-home", "greeting-snow", homeReminder, "Sad", 40, GreetingThinning,
                Snow, "Approach during snow with a profile home set", "Text must stay neutral when the profile has no home");
            Hello("greeting-sneaking-voice", "greeting-sneaking", "Keep your voice down. I am right behind you.", "Neutral", 40, GreetingThinning,
                Sneaking, "Approach while the player is sneaking", "Must not fire while the player stands upright");
            Hello("greeting-sneaking-talking", "greeting-sneaking", "Sneaking? Then let us not stand here talking.", "Puzzled", 40, GreetingThinning,
                Sneaking, "Approach while the player is sneaking", "Must not fire while the player stands upright");
            Hello("greeting-weapon-drawn-blade", "greeting-weapon-drawn", "Your blade is out. Should mine be?", "Surprise", 45, GreetingThinning,
                WeaponDrawn, "Approach with the player's weapon drawn", "Must not fire with the weapon sheathed");
            Hello("greeting-weapon-drawn-away", "greeting-weapon-drawn", "Put that away unless you mean to use it.", "Anger", 40, GreetingThinning,
                WeaponDrawn, "Approach with the player's weapon drawn", "Must not fire with the weapon sheathed");
            Hello("greeting-low-health-hurt", "greeting-low-health", "I am hurt. Not badly, but do not ask me to run.", "Sad", 50, LowHealthGreetingThinning,
                LowHealth, "Approach while the NPC's Health actor value is below 50", "Must not fire at full health");
            Hello("greeting-low-health-bind", "greeting-low-health", "Give me a moment to bind this. Then we go on.", "Fear", 45, LowHealthGreetingThinning,
                LowHealth, "Approach while the NPC's Health actor value is below 50", "Must not fire at full health");
            Hello("greeting-relationship-friend", "greeting-relationship", "Good to see you, friend. What are we doing today?", "Happy", 55, GreetingThinning,
                Friend, "Approach with relationship rank 3 or higher", "Must not fire at rank 0-2");
            Hello("greeting-relationship-anywhere", "greeting-relationship", "I would rather be here with you than anywhere else.", "Happy", 60, GreetingThinning,
                Friend, "Approach with relationship rank 3 or higher", "Must not fire at rank 0-2");

            // Goodbyes.
            Bark("goodbye-lead", "goodbye", "Goodbye", "Goodbye", "Lead on.", "Neutral", 35, StandardPriority, 0, Empty,
                "End a conversation", "Must not play as a greeting");
            Bark("goodbye-behind", "goodbye", "Goodbye", "Goodbye", "I will be right behind you.", "Neutral", 40, StandardPriority, 0, Empty,
                "End a conversation", "Must not play as a greeting");
            Bark("goodbye-until", "goodbye", "Goodbye", "Goodbye", "Until you need me again.", "Happy", 40, StandardPriority, 0, Empty,
                "End a conversation", "Must not play as a greeting");

            // Player-facing follower actions (Custom topics bound to the vanilla DialogueFollower quest).
            Action("recruit", "recruit", "Recruit", name + ", will you travel with me?", "Very well. Lead the way and I will keep up.", "Happy", 50,
                SkyrimDialogueAction.Recruit,
                ImmutableArray.Create(SelfId, InPotentialFollowerFaction, NotInCurrentFollowerFaction, FollowerCount(0)),
                "Ask with no current follower while the NPC is a potential follower", "Must be hidden while any follower is active or once recruited");
            Custom("recruit-refused-full", "recruit-refused-full", "RecruitFull", name + ", will you travel with me?",
                "You already have company. Send them off first, then ask me again.", "Neutral", 40,
                ImmutableArray.Create(SelfId, InPotentialFollowerFaction, NotInCurrentFollowerFaction, FollowerCount(1)),
                "Ask while another follower is active", "Must be hidden when no follower is active; must not recruit");
            Action("follow", "follow", "Follow", "Follow me again.", "About time. Let us go.", "Happy", 45,
                SkyrimDialogueAction.Follow,
                ImmutableArray.Create(SelfId, InCurrentFollowerFaction, Waiting(1)),
                "Ask while the NPC is a follower told to wait", "Must be hidden while already following");
            Action("wait", "wait", "Wait", "Wait here for me.", "I will hold this spot. Do not take too long.", "Neutral", 40,
                SkyrimDialogueAction.Wait,
                ImmutableArray.Create(SelfId, InCurrentFollowerFaction, Waiting(0)),
                "Ask while the NPC is following", "Must be hidden while already waiting");
            Action("dismiss", "dismiss", "Dismiss", "It is time we parted ways.", dismissText, "Sad", 45,
                SkyrimDialogueAction.Dismiss,
                ImmutableArray.Create(SelfId, InCurrentFollowerFaction),
                "Ask while the NPC is a current follower", "Must be hidden before recruitment");
            Action("trade", "trade", "Trade", "Let me see what you are carrying.", "Take what you need. Just leave me room to swing.", "Neutral", 40,
                SkyrimDialogueAction.Trade,
                ImmutableArray.Create(SelfId, InCurrentFollowerFaction),
                "Ask while the NPC is a current follower", "Must be hidden before recruitment");

            // Combat barks.
            Combat("combat-attack-still", "combat-attack", "Attack", "Hold still!", "Anger", 55);
            Combat("combat-attack-wrong-fight", "combat-attack", "Attack", "You picked the wrong fight!", "Anger", 60);
            Combat("combat-attack-down", "combat-attack", "Attack", "Down you go!", "Anger", 50);
            Combat("combat-hit-stung", "combat-hit", "Hit", "That one stung!", "Anger", 50);
            Combat("combat-hit-all", "combat-hit", "Hit", "Is that all you have?", "Anger", 45);
            Combat("combat-flee-fall-back", "combat-flee", "Flee", "This is lost. Fall back!", "Fear", 60);
            Combat("combat-bleedout-hold", "combat-bleedout", "Bleedout", "I cannot hold them. Go on without me.", "Fear", 65);
            Combat("combat-bleedout-moment", "combat-bleedout", "Bleedout", "Give me a moment. I will get up.", "Sad", 55);
            Combat("combat-ally-killed-pay", "combat-ally-killed", "AllyKilled", "No! You will pay for that!", "Anger", 65);
            Combat("combat-taunt-closer", "combat-taunt", "Taunt", "Come closer. I dare you.", "Anger", 50);
            Combat("combat-taunt-home", "combat-taunt", "Taunt", "You should have stayed home.", "Happy", 45);
            Combat("combat-taunt-best", "combat-taunt", "Taunt", "Is that your best?", "Disgust", 45);

            // Detection transitions.
            Bark("detection-normal-to-combat-there", "detection-normal-to-combat", "NormalToCombat", "NormalToCombat", "There! Weapons out!", "Surprise", 55, StandardPriority, 0, Empty,
                "NPC spots a hostile and enters combat", "Must not fire when combat ends");
            Bark("detection-normal-to-combat-company", "detection-normal-to-combat", "NormalToCombat", "NormalToCombat", "We have company. Get ready.", "Anger", 45, StandardPriority, 0, Empty,
                "NPC spots a hostile and enters combat", "Must not fire when combat ends");
            Bark("detection-combat-to-normal-over", "detection-combat-to-normal", "CombatToNormal", "CombatToNormal", "It is over. Catch your breath.", "Neutral", 40, StandardPriority, 0, Empty,
                "Combat ends with no remaining hostiles", "Must not fire on entering combat");
            Bark("detection-combat-to-normal-clear", "detection-combat-to-normal", "CombatToNormal", "CombatToNormal", "Clear. For now.", "Neutral", 35, StandardPriority, 0, Empty,
                "Combat ends with no remaining hostiles", "Must not fire on entering combat");
            Bark("detection-lost-to-normal-sharp", "detection-lost-to-normal", "LostToNormal", "LostToNormal", "Lost them. Stay sharp anyway.", "Puzzled", 40, StandardPriority, 0, Empty,
                "NPC loses a target it was searching for", "Must not fire when the target is killed");

            // Idle world reactions (one Idle topic, conditions select the world state).
            Idle("idle-dungeon-smell", "idle-dungeon", "Places like this always smell of old death.", "Disgust", 40, Dungeon,
                "Idle inside a location with LocTypeDungeon", "Must not fire in a city or inn");
            Idle("idle-dungeon-floor", "idle-dungeon", "Watch the floor. These ruins love a trap.", "Neutral", 45, Dungeon,
                "Idle inside a location with LocTypeDungeon", "Must not fire in a city or inn");
            Idle("idle-city-exits", "idle-city", "So many people. I keep counting the exits.", "Puzzled", 40, City,
                "Idle inside a location with LocTypeCity", "Must not fire in the wilderness");
            Idle("idle-city-role", "idle-city", cityRole, "Neutral", 40, City,
                "Idle inside a location with LocTypeCity and a profile role set", "Text must stay neutral when the profile has no role");
            Idle("idle-inn-fire", "idle-inn", "Warm fire, cheap ale. I could stay a while.", "Happy", 50, Inn,
                "Idle inside a location with LocTypeInn", "Must not fire in a temple");
            Idle("idle-inn-story", "idle-inn", "Listen to the room. Every inn has a story worth hearing.", "Neutral", 40, Inn,
                "Idle inside a location with LocTypeInn", "Must not fire in a temple");
            Idle("idle-temple-listening", "idle-temple", "Keep your voice down in here. Someone is always listening.", "Neutral", 35, Temple,
                "Idle inside a location with LocTypeTemple", "Must not fire in an inn");
            Idle("idle-temple-door", "idle-temple", "I do not pray much. I will wait by the door.", "Neutral", 40, Temple,
                "Idle inside a location with LocTypeTemple", "Must not fire in an inn");
            Idle("idle-wilderness-horizon", "idle-wilderness", "Open country. Nothing between us and the horizon.", "Happy", 45, Wilderness,
                "Idle outdoors outside any city or dungeon location", "Must not fire indoors or inside a city");
            Idle("idle-wilderness-home", "idle-wilderness", wildernessHome, "Sad", 40, Wilderness,
                "Idle outdoors outside any city or dungeon location with a profile home set", "Text must stay neutral when the profile has no home");
            Idle("idle-snow-boots", "idle-snow", "Snow again. My boots are not made for this.", "Disgust", 40, ExteriorSnow,
                "Idle outdoors during snow weather", "Must not fire indoors or in rain");
            Idle("idle-snow-bite", "idle-snow", "Cold enough to bite. Keep moving.", "Neutral", 40, ExteriorSnow,
                "Idle outdoors during snow weather", "Must not fire indoors or in rain");
            Idle("idle-rain-pass", "idle-rain", "The rain will pass. It always does.", "Neutral", 35, ExteriorRain,
                "Idle outdoors during rain weather", "Must not fire indoors or in snow");
            Idle("idle-rain-temper", "idle-rain", "Wet cloak, wet boots, wet temper.", "Disgust", 45, ExteriorRain,
                "Idle outdoors during rain weather", "Must not fire indoors or in snow");
            Idle("idle-night-interesting", "idle-night", "Night is when the road gets interesting.", "Neutral", 40, ExteriorNight,
                "Idle outdoors after 20:00 or before 05:00", "Must not fire indoors or in daylight");
            Idle("idle-night-hunt", "idle-night", "Stay close. Things hunt at night.", "Fear", 40, ExteriorNight,
                "Idle outdoors after 20:00 or before 05:00", "Must not fire indoors or in daylight");

            // Main-quest reactions gated on GetStage so nothing fires before progression.
            // Stage values are the first journal stage of each vanilla quest (or the finishing
            // stage where the line reacts to completion); see coverage-matrix.md for the citations.
            Quest("quest-main-mq101", "quest-main", "MQ101", 900, "So you walked out of Helgen. Not many did.", "Sad", 45);
            Quest("quest-main-mq103", "quest-main", "MQ103", 50, "Bleak Falls Barrow. Everyone in the valley says to stay away. Naturally, we are going in.", "Puzzled", 45);
            Quest("quest-main-mq104", "quest-main", "MQ104", 10, "A dragon near the city. I never thought I would say those words.", "Fear", 50);
            Quest("quest-main-mq105", "quest-main", "MQ105", 10, "The Greybeards called for you. When the mountain speaks, people listen.", "Surprise", 45);
            Quest("quest-main-mq105-ustengrav", "quest-main", "MQ105Ustengrav", 10, "Chasing an old horn into a barrow. I hope it is worth the digging.", "Neutral", 40);
            Quest("quest-main-mq201", "quest-main", "MQ201", 30, "Sneaking into an embassy. Say what you like, life with you is never dull.", "Happy", 45);
            Quest("quest-main-mq301", "quest-main", "MQ301", 10, "Hunting a dragon on purpose. I want that written down somewhere.", "Surprise", 50);
            Quest("quest-main-mq303", "quest-main", "MQ303", 10, "The end of the world, and here we are walking toward it.", "Fear", 55);

            // Civil-war reactions: the two enlistment quests, gated on the oath stage.
            Quest("quest-civil-war-cw01a", "quest-civil-war", "CW01A", 200, "Sworn to the Legion, then. Solitude will expect a great deal of you.", "Neutral", 45);
            Quest("quest-civil-war-cw01b", "quest-civil-war", "CW01B", 200, "A Stormcloak now. Windhelm will expect a great deal of you.", "Neutral", 45);

            // Witnessed crimes.
            Bark("crime-steal-yours", "crime-steal", "Steal", "Steal", "That was not yours to take.", "Disgust", 45, StandardPriority, 0, Empty,
                "NPC witnesses the player stealing", "Must not fire when the player takes owned-by-player items");
            Bark("crime-assault-necessary", "crime-assault", "Assault", "Assault", "Was that necessary? Really?", "Anger", 50, StandardPriority, 0, Empty,
                "NPC witnesses the player assaulting a non-hostile", "Must not fire in sanctioned combat");
            Bark("crime-murder-sign", "crime-murder", "Murder", "Murder", "You killed them. I did not sign on for that.", "Anger", 65, StandardPriority, 0, Empty,
                "NPC witnesses the player murdering a non-hostile", "Must not fire when a hostile dies");
            Bark("crime-trespass-notice", "crime-trespass", "Trespass", "Trespass", "We are not supposed to be in here. Let us go before someone notices.", "Fear", 40, StandardPriority, 0, Empty,
                "NPC witnesses the player trespassing", "Must not fire in public spaces");
            Bark("crime-pickpocket-fingers", "crime-pickpocket", "Pickpocket", "PickpocketNC", "Light fingers. I saw that, and so might someone else.", "Puzzled", 45, StandardPriority, 0, Empty,
                "NPC witnesses the player pickpocketing", "Must not fire on ordinary trades");

            return lines.ToImmutable();

            void Hello(string id, string category, string text, string emotion, uint emotionValue, int randomPercent,
                ImmutableArray<SkyrimDialogueCondition> conditions, string positive, string negative) =>
                Bark(id, category, "Hello", "Hello", text, emotion, emotionValue, StandardPriority, randomPercent, conditions, positive, negative);

            void Combat(string id, string category, string subtype, string text, string emotion, uint emotionValue) =>
                Bark(id, category, subtype, subtype, text, emotion, emotionValue, CombatPriority, 0, Empty,
                    "NPC is in combat and the " + subtype + " bark fires", "Must not fire outside combat");

            void Idle(string id, string category, string text, string emotion, uint emotionValue,
                ImmutableArray<SkyrimDialogueCondition> conditions, string positive, string negative) =>
                Bark(id, category, "Idle", "Idle", text, emotion, emotionValue, StandardPriority, IdleThinning, conditions, positive, negative);

            void Quest(string id, string category, string quest, float stage, string text, string emotion, uint emotionValue) =>
                lines.Add(new SkyrimDialogueLine(
                    id, category, prefix + "QuestIdle", "Idle", text, emotion, emotionValue,
                    ImmutableArray.Create(Condition("GetStage", quest, ">=", stage)),
                    StandardPriority, SkyrimDialogueRepeatPolicy.Once, 0, QuestReactionCooldownHours, SkyrimDialogueAction.None, null, Draft,
                    "Idle after " + quest + " reaches stage " + stage.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    "Must not fire while " + quest + " is below that stage; plays once"));

            void Bark(string id, string category, string topicSuffix, string subtype, string text, string emotion, uint emotionValue,
                float priority, int randomPercent, ImmutableArray<SkyrimDialogueCondition> conditions, string positive, string negative) =>
                lines.Add(new SkyrimDialogueLine(
                    id, category, prefix + topicSuffix, subtype, text, emotion, emotionValue, conditions,
                    priority, SkyrimDialogueRepeatPolicy.Random, randomPercent, 0, SkyrimDialogueAction.None, null, Draft, positive, negative));

            void Action(string id, string category, string topicSuffix, string prompt, string text, string emotion, uint emotionValue,
                SkyrimDialogueAction action, ImmutableArray<SkyrimDialogueCondition> conditions, string positive, string negative) =>
                lines.Add(new SkyrimDialogueLine(
                    id, category, prefix + topicSuffix, "Custom", text, emotion, emotionValue, conditions,
                    ActionPriority, SkyrimDialogueRepeatPolicy.Always, 0, 0, action, prompt, Draft, positive, negative));

            void Custom(string id, string category, string topicSuffix, string prompt, string text, string emotion, uint emotionValue,
                ImmutableArray<SkyrimDialogueCondition> conditions, string positive, string negative) =>
                lines.Add(new SkyrimDialogueLine(
                    id, category, prefix + topicSuffix, "Custom", text, emotion, emotionValue, conditions,
                    StandardPriority, SkyrimDialogueRepeatPolicy.Always, 0, 0, SkyrimDialogueAction.None, prompt, Draft, positive, negative));
        }

        private static readonly ImmutableArray<SkyrimDialogueCondition> Empty = ImmutableArray<SkyrimDialogueCondition>.Empty;

        private static SkyrimDialogueCondition Condition(string function, string? parameter, string op, float value, string runOn = "subject", bool or = false) =>
            new(function, parameter, null, op, value, or, runOn);

        private static readonly SkyrimDialogueCondition SelfId = Condition("GetIsID", "self", "==", 1);
        private static readonly SkyrimDialogueCondition InPotentialFollowerFaction = Condition("GetInFaction", "PotentialFollowerFaction", "==", 1);
        private static readonly SkyrimDialogueCondition InCurrentFollowerFaction = Condition("GetInFaction", "CurrentFollowerFaction", "==", 1);
        private static readonly SkyrimDialogueCondition NotInCurrentFollowerFaction = Condition("GetInFaction", "CurrentFollowerFaction", "==", 0);
        private static SkyrimDialogueCondition FollowerCount(float value) => Condition("GetGlobalValue", "PlayerFollowerCount", "==", value);
        private static SkyrimDialogueCondition Waiting(float value) => Condition("GetActorValue", "WaitingForPlayer", "==", value);

        private static readonly SkyrimDialogueCondition IsInside = Condition("IsInInterior", null, "==", 1);
        private static readonly SkyrimDialogueCondition IsOutside = Condition("IsInInterior", null, "==", 0);
        private static readonly ImmutableArray<SkyrimDialogueCondition> Interior = ImmutableArray.Create(IsInside);
        private static readonly ImmutableArray<SkyrimDialogueCondition> ExteriorDay = ImmutableArray.Create(
            IsOutside, Condition("GetCurrentTime", null, ">=", 6), Condition("GetCurrentTime", null, "<", 20));
        private static readonly ImmutableArray<SkyrimDialogueCondition> ExteriorNight = ImmutableArray.Create(
            IsOutside, Condition("GetCurrentTime", null, ">=", 20, or: true), Condition("GetCurrentTime", null, "<", 5));
        private static readonly ImmutableArray<SkyrimDialogueCondition> Rain = ImmutableArray.Create(
            Condition("GetIsCurrentWeather", "SkyrimOvercastRain", "==", 1, or: true),
            Condition("GetIsCurrentWeather", "SkyrimStormRain", "==", 1));
        private static readonly ImmutableArray<SkyrimDialogueCondition> Snow = ImmutableArray.Create(
            Condition("GetIsCurrentWeather", "SkyrimOvercastSnow", "==", 1, or: true),
            Condition("GetIsCurrentWeather", "SkyrimStormSnow", "==", 1));
        private static readonly ImmutableArray<SkyrimDialogueCondition> ExteriorRain = Rain.Insert(0, IsOutside);
        private static readonly ImmutableArray<SkyrimDialogueCondition> ExteriorSnow = Snow.Insert(0, IsOutside);
        private static readonly ImmutableArray<SkyrimDialogueCondition> Sneaking = ImmutableArray.Create(Condition("IsSneaking", null, "==", 1, "player"));
        private static readonly ImmutableArray<SkyrimDialogueCondition> WeaponDrawn = ImmutableArray.Create(Condition("IsWeaponOut", null, "==", 1, "player"));
        private static readonly ImmutableArray<SkyrimDialogueCondition> LowHealth = ImmutableArray.Create(Condition("GetActorValue", "Health", "<", 50));
        private static readonly ImmutableArray<SkyrimDialogueCondition> Friend = ImmutableArray.Create(Condition("GetRelationshipRank", "player", ">=", 3));
        private static readonly ImmutableArray<SkyrimDialogueCondition> Dungeon = ImmutableArray.Create(Condition("LocationHasKeyword", "LocTypeDungeon", "==", 1));
        private static readonly ImmutableArray<SkyrimDialogueCondition> City = ImmutableArray.Create(Condition("LocationHasKeyword", "LocTypeCity", "==", 1));
        private static readonly ImmutableArray<SkyrimDialogueCondition> Inn = ImmutableArray.Create(Condition("LocationHasKeyword", "LocTypeInn", "==", 1));
        private static readonly ImmutableArray<SkyrimDialogueCondition> Temple = ImmutableArray.Create(Condition("LocationHasKeyword", "LocTypeTemple", "==", 1));
        private static readonly ImmutableArray<SkyrimDialogueCondition> Wilderness = ImmutableArray.Create(
            IsOutside,
            Condition("LocationHasKeyword", "LocTypeCity", "==", 0),
            Condition("LocationHasKeyword", "LocTypeDungeon", "==", 0));
    }
}
