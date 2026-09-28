using System.Collections.Immutable;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record BethesdaNpcCreationTemplateSnapshot(
    ImmutableArray<PluginName> Masters,
    int MajorRecordCount,
    int NpcRecordCount,
    bool IsMaster,
    bool IsSmallMaster,
    bool HasTemplateInheritance,
    ushort? FormVersion);

public sealed record BethesdaNpcCreationReferenceValidation(
    bool RaceExists,
    bool VoiceExists,
    bool ClassExists,
    bool CombatStyleExists,
    bool DefaultOutfitExists)
{
    public bool IsValid => RaceExists && VoiceExists && ClassExists &&
        CombatStyleExists && DefaultOutfitExists;
}

/// <summary>
/// Mutagen boundary for the one-record Skyrim creation slice. It copies only
/// the admitted NPC semantics into a fresh mod and gives that copy a new,
/// output-owned FormKey.
/// </summary>
public static partial class BethesdaNpcCreationAdapter
{
    public const uint AllocatedLocalFormId = 0x000800;
    public const uint ExpectedNextFormId = 0x000801;
    public const uint PotentialFollowerFactionLocalFormId = 0x0005C84D;
    public const uint CurrentFollowerFactionLocalFormId = 0x0005C84E;
    public const uint PlayerLocalFormId = 0x00000007;
    public const int PotentialFollowerFactionRank = 0;
    public const int CurrentFollowerFactionRank = -1;
    public const float HeaderVersion = 1.7f;
    public const ushort RecordFormVersion = 44;

    private static readonly ModKey SkyrimModKey =
        ModKey.FromNameAndExtension("Skyrim.esm");

    public static BethesdaNpcCreationTemplateSnapshot ReadTemplate(
        WorkspacePath templatePlugin,
        FormId templateNpcFormId) =>
        ReadTemplate(templatePlugin.Value, templateNpcFormId);

    public static BethesdaNpcCreationTemplateSnapshot ReadTemplate(
        ApplicationResourcePath templatePlugin,
        FormId templateNpcFormId) =>
        ReadTemplate(templatePlugin.Value, templateNpcFormId);

    private static BethesdaNpcCreationTemplateSnapshot ReadTemplate(
        string templatePlugin,
        FormId templateNpcFormId)
    {
        var sourceModKey = ModKey.FromNameAndExtension(Path.GetFileName(templatePlugin));
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(sourceModKey, new FilePath(templatePlugin)),
            SkyrimRelease.SkyrimSE);
        var source = overlay.Npcs.FirstOrDefault(
            npc => npc.FormKey == new FormKey(sourceModKey, templateNpcFormId.Value));
        if (source is null)
        {
            throw new InvalidDataException(
                $"Template record {templateNpcFormId} is not an NPC owned by {sourceModKey}.");
        }

        return new BethesdaNpcCreationTemplateSnapshot(
            overlay.ModHeader.MasterReferences
                .Select(item => new PluginName(item.Master.ToString()))
                .ToImmutableArray(),
            overlay.EnumerateMajorRecords().Count(),
            overlay.Npcs.Count,
            overlay.IsMaster,
            overlay.IsSmallMaster,
            source.Configuration.TemplateFlags != 0 || !source.Template.IsNull,
            source.FormVersion);
    }

    public static void Write(
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        WorkspacePath destination)
    {
        var sourceModKey = ModKey.FromNameAndExtension(Path.GetFileName(request.TemplatePlugin.Value));
        var outputModKey = ModKey.FromNameAndExtension(proposal.OutputPlugin.Value);
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(sourceModKey, new FilePath(request.TemplatePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        var source = overlay.Npcs.FirstOrDefault(
            npc => npc.FormKey == new FormKey(sourceModKey, request.TemplateNpcFormId.Value))
            ?? throw new InvalidDataException(
                $"Template record {request.TemplateNpcFormId} is not an NPC owned by {sourceModKey}.");

        var templateMasters = overlay.ModHeader.MasterReferences
            .Select(item => new PluginName(item.Master.ToString()))
            .ToImmutableArray();
        var expectedMasters = BuildMasterList(
            templateMasters,
            request.Appearance,
            proposal.OutputPlugin,
            request.PluginAuthorities);
        if (!expectedMasters.SequenceEqual(proposal.Masters))
        {
            throw new InvalidDataException(
                "The proposal master list does not match the deterministic creation master union.");
        }

        var dataRoot = new WorkspacePath(Path.GetDirectoryName(request.TemplatePlugin.Value)!);
        EnsureAppearanceReferences(
            dataRoot,
            request.Appearance,
            request.Traits.Sex,
            request.References.Race,
            request.PluginAuthorities);

        var mod = new SkyrimMod(
            outputModKey,
            SkyrimRelease.SkyrimSE,
            headerVersion: HeaderVersion,
            forceUseLowerFormIDRanges: false);
        // Flag-only light output: the TES4 header bit changes, the explicit
        // 0x800-based FormIDs and NextFormID below do not.
        mod.IsSmallMaster = request.PluginType == BlankNpcPluginType.Espfe;
        ((IMod)mod).NextFormID = ExpectedNextFormIdFor(
            request.Appearance, request.Traits.Role);
        foreach (var master in proposal.Masters)
        {
            mod.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(master.Value)
            });
        }

        var targetKey = new FormKey(outputModKey, AllocatedLocalFormId);
        var npc = mod.Npcs.DuplicateInAsNewRecord(source, targetKey);
        Apply(npc, request, mod, outputModKey);
        if (npc.FormVersion != RecordFormVersion)
        {
            throw new InvalidDataException(
                $"The admitted template NPC uses FormVersion {npc.FormVersion}; expected {RecordFormVersion}.");
        }

        mod.WriteToBinary(new FilePath(destination.Value), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        PatchExactQnam(destination, proposal, request.Appearance);
        PatchPrivateSkinWnam(destination, proposal, request.Appearance);
    }

    public static BethesdaNpcCreationReferenceValidation ValidateReferenceTypes(
        WorkspacePath dataRoot,
        SkyrimNpcCreationReferences references,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities = default)
    {
        var raceExists = false;
        var voiceExists = false;
        var classExists = false;
        var combatStyleExists = false;
        var defaultOutfitExists = references.DefaultOutfit is null;
        IEnumerable<FormReference> all = new[]
        {
            references.Race,
            references.Voice,
            references.Class,
            references.CombatStyle
        };
        if (references.DefaultOutfit is { } selectedOutfit)
            all = all.Append(selectedOutfit);
        foreach (var plugin in all.Select(item => item.Plugin).Distinct())
        {
            var providerPath = BethesdaNpcCreationProviderResolver.Resolve(
                dataRoot, plugin, pluginAuthorities);
            var providerModKey = ModKey.FromNameAndExtension(plugin.Value);
            using var provider = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(providerModKey, new FilePath(providerPath)),
                SkyrimRelease.SkyrimSE);
            if (references.Race.Plugin == plugin)
                raceExists = provider.Races.Any(item => item.FormKey == ToFormKey(references.Race));
            if (references.Voice.Plugin == plugin)
                voiceExists = provider.VoiceTypes.Any(item => item.FormKey == ToFormKey(references.Voice));
            if (references.Class.Plugin == plugin)
                classExists = provider.Classes.Any(item => item.FormKey == ToFormKey(references.Class));
            if (references.CombatStyle.Plugin == plugin)
                combatStyleExists = provider.CombatStyles.Any(item => item.FormKey == ToFormKey(references.CombatStyle));
            if (references.DefaultOutfit is { } outfit &&
                outfit.Plugin == plugin)
                defaultOutfitExists = provider.Outfits.Any(
                    item => item.FormKey == ToFormKey(outfit));
        }
        return new BethesdaNpcCreationReferenceValidation(
            raceExists,
            voiceExists,
            classExists,
            combatStyleExists,
            defaultOutfitExists);
    }

    private static void Apply(
        Npc npc,
        NpcCreationRequest request,
        SkyrimMod mod,
        ModKey outputModKey)
    {
        npc.EditorID = request.Identity.EditorId.Value;
        npc.Name = request.Identity.Name.Value;
        npc.Race = new FormLink<IRaceGetter>(ToFormKey(request.References.Race));
        npc.Voice = new FormLinkNullable<IVoiceTypeGetter>(ToFormKey(request.References.Voice));
        npc.Class = new FormLink<IClassGetter>(ToFormKey(request.References.Class));
        npc.CombatStyle = new FormLinkNullable<ICombatStyleGetter>(ToFormKey(request.References.CombatStyle));
        npc.DefaultOutfit = request.References.DefaultOutfit is { } outfit
            ? new FormLinkNullable<IOutfitGetter>(ToFormKey(outfit))
            : new FormLinkNullable<IOutfitGetter>();

        npc.Configuration.TemplateFlags = 0;
        npc.Template.Clear();
        var flags = npc.Configuration.Flags;
        flags = SetFlag(flags, NpcConfiguration.Flag.Female, request.Traits.Sex == NpcSex.Female);
        flags = SetFlag(flags, NpcConfiguration.Flag.Unique, request.Traits.IsUnique);
        flags = SetFlag(flags, NpcConfiguration.Flag.Essential, request.Traits.IsEssential);
        flags = SetFlag(flags, NpcConfiguration.Flag.Protected, request.Traits.IsProtected);
        flags = SetFlag(flags, NpcConfiguration.Flag.Respawn, request.Traits.Respawns);
        flags = SetFlag(flags, NpcConfiguration.Flag.AutoCalcStats, request.Traits.AutoCalcStats);
        flags = SetFlag(flags, (NpcConfiguration.Flag)0x80u,
            request.Stats.Level.Mode == NpcLevelMode.Multiplier);
        npc.Configuration.Flags = flags;

        npc.Configuration.Level = request.Stats.Level.Mode == NpcLevelMode.Multiplier
            ? new PcLevelMult { LevelMult = (float)request.Stats.Level.Value }
            : new NpcLevel { Level = checked((short)request.Stats.Level.Value) };
        npc.Configuration.MagickaOffset = request.Stats.MagickaOffset;
        npc.Configuration.StaminaOffset = request.Stats.StaminaOffset;
        npc.Configuration.HealthOffset = request.Stats.HealthOffset;
        npc.Configuration.CalcMinLevel = checked((short)request.Stats.CalcMinLevel);
        npc.Configuration.CalcMaxLevel = checked((short)request.Stats.CalcMaxLevel);
        npc.Configuration.SpeedMultiplier = request.Stats.SpeedMultiplier;
        npc.Configuration.DispositionBase = request.Stats.DispositionBase;
        npc.Configuration.BleedoutOverride = request.Stats.BleedoutOverride;

        npc.PlayerSkills ??= new PlayerSkills();
        npc.PlayerSkills.Health = request.Stats.BaseHealth;
        npc.PlayerSkills.Magicka = request.Stats.BaseMagicka;
        npc.PlayerSkills.Stamina = request.Stats.BaseStamina;
        npc.Height = request.Stats.Height;
        npc.Weight = request.Stats.Weight;
        npc.NAM5 = request.Stats.FarAwayModelDistance;

        ApplyAppearance(npc, mod, outputModKey, request);
        ApplyRuntimeAppearance(npc, request.RuntimeAppearance);
        ApplyRole(npc, mod, outputModKey, request);
    }

    public static uint FollowerRelationshipLocalFormIdFor(
        NpcCreationAppearanceSource appearance) => ExpectedNextFormIdFor(appearance);

    public static uint ExpectedNextFormIdFor(
        NpcCreationAppearanceSource appearance,
        NpcCreationRole role) => role == NpcCreationRole.Follower
        ? checked(FollowerRelationshipLocalFormIdFor(appearance) + 1)
        : ExpectedNextFormIdFor(appearance);

    public static int ExpectedMajorRecordCountFor(
        NpcCreationAppearanceSource appearance,
        NpcCreationRole role) => ExpectedMajorRecordCountFor(appearance) +
        (role == NpcCreationRole.Follower ? 1 : 0);

    public static int ExpectedTopGroupCountFor(
        NpcCreationAppearanceSource appearance,
        NpcCreationRole role) => ExpectedTopGroupCountFor(appearance) +
        (role == NpcCreationRole.Follower ? 1 : 0);

    private static void ApplyRole(
        Npc npc,
        SkyrimMod mod,
        ModKey outputModKey,
        NpcCreationRequest request)
    {
        var potentialFollowerFaction = new FormKey(
            SkyrimModKey, PotentialFollowerFactionLocalFormId);
        var currentFollowerFaction = new FormKey(
            SkyrimModKey, CurrentFollowerFactionLocalFormId);
        var retainedFactions = npc.Factions
            .Where(item => item.Faction.FormKey != potentialFollowerFaction &&
                           item.Faction.FormKey != currentFollowerFaction)
            .Select(item => (item.Faction.FormKey, item.Rank))
            .ToArray();

        npc.Factions.Clear();
        foreach (var (faction, rank) in retainedFactions)
        {
            npc.Factions.Add(new RankPlacement
            {
                Faction = new FormLink<IFactionGetter>(faction),
                Rank = rank
            });
        }

        if (request.Traits.Role != NpcCreationRole.Follower) return;

        npc.Factions.Add(new RankPlacement
        {
            Faction = new FormLink<IFactionGetter>(potentialFollowerFaction),
            Rank = PotentialFollowerFactionRank
        });
        npc.Factions.Add(new RankPlacement
        {
            Faction = new FormLink<IFactionGetter>(currentFollowerFaction),
            Rank = CurrentFollowerFactionRank
        });

        var relationship = new Relationship(
            new FormKey(outputModKey, FollowerRelationshipLocalFormIdFor(request.Appearance)),
            SkyrimRelease.SkyrimSE)
        {
            FormVersion = RecordFormVersion,
            EditorID = BuildFollowerRelationshipEditorId(request.Identity.EditorId.Value),
            Parent = new FormLink<INpcGetter>(npc.FormKey),
            Child = new FormLink<INpcGetter>(new FormKey(SkyrimModKey, PlayerLocalFormId)),
            Rank = Relationship.RankType.Ally,
            Unknown = 0,
            Flags = 0,
            AssociationType = new FormLink<IAssociationTypeGetter>(FormKey.Null)
        };
        mod.Relationships.Add(relationship);
    }

    internal static string BuildFollowerRelationshipEditorId(string npcEditorId)
    {
        const string suffix = "_PlayerAllyRELA";
        var prefixLength = Math.Min(npcEditorId.Length, 64 - suffix.Length);
        return npcEditorId[..prefixLength] + suffix;
    }

    private static NpcConfiguration.Flag SetFlag(
        NpcConfiguration.Flag current,
        NpcConfiguration.Flag flag,
        bool enabled) => enabled ? current | flag : current & ~flag;

    private static FormKey ToFormKey(FormReference reference) => new(
        ModKey.FromNameAndExtension(reference.Plugin.Value),
        reference.FormId.Value);
}
