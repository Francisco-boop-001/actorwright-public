using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Diagnostics.CodeAnalysis;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

/// <summary>
/// Builds the reviewed Finish Core semantics in a Mutagen scratch plugin and
/// then delegates the final byte-preserving splice to the independent raw
/// writer. The scratch file is always private and deleted before return.
/// </summary>
public sealed class BethesdaSkyrimNpcFinishCoreWriter
{
    private static readonly FormKey PlayerKey = new(
        ModKey.FromNameAndExtension("Skyrim.esm"), 0x00000007);
    private static readonly FormKey PotentialFollowerFactionKey = new(
        ModKey.FromNameAndExtension("Skyrim.esm"), 0x0005C84D);
    private static readonly FormKey CurrentFollowerFactionKey = new(
        ModKey.FromNameAndExtension("Skyrim.esm"), 0x0005C84E);

    public byte[] Write(
        WorkspacePath sourcePlugin,
        SkyrimNpcFinishCoreProposal proposal,
        WorkspacePath copiedMaster,
        CancellationToken cancellationToken)
    {
        _ = GetType();
        ArgumentNullException.ThrowIfNull(proposal);
        cancellationToken.ThrowIfCancellationRequested();
        if (proposal.Status != SkyrimNpcFinishCoreStatus.ReadyForReviewedWrite)
            Refuse("finish-core-writer-proposal", "Only a hash-bound ReadyForReviewedWrite proposal may be written.");
        SkyrimNpcFinishCoreRequest request = proposal.Request ??
            Refuse<SkyrimNpcFinishCoreRequest>(
                "finish-core-writer-proposal", "The proposal request is missing.");
        Mood? requestedMood = null;
        if (request.Schema != SkyrimNpcFinishCoreRequest.LegacySchemaIdentifier)
        {
            SkyrimNpcFinishCoreMood mood = request.AiPolicy?.Mood ??
                Refuse<SkyrimNpcFinishCoreMood>(
                    "finish-core-writer-mood",
                    "Current v2 Finish Core requests require an aiPolicy mood.");
            requestedMood = ToMutagenMood(mood);
        }
        PluginName plugin = request.Source.Plugin ??
            Refuse<PluginName>("finish-core-writer-proposal", "The source plugin is missing.");
        Sha256Hash expectedHash = request.Source.PluginSha256 ??
            Refuse<Sha256Hash>("finish-core-writer-proposal", "The source plugin hash is missing.");
        Sha256Hash copiedMasterHash = request.SandboxAuthority.CopiedMasterSha256 ??
            Refuse<Sha256Hash>(
                "finish-core-writer-master-hash",
                "The copied-master hash authority is missing.");
        WorkspacePath requestCopiedMaster = request.SandboxAuthority.CopiedMaster ??
            Refuse<WorkspacePath>(
                "finish-core-writer-master-path",
                "The copied-master path authority is missing.");
        if (!string.Equals(
                Path.GetFullPath(copiedMaster.Value),
                Path.GetFullPath(requestCopiedMaster.Value),
                StringComparison.OrdinalIgnoreCase))
            Refuse(
                "finish-core-writer-master-path",
                "The copied-master path differs from the proposal authority.");
        if (!File.Exists(sourcePlugin.Value) || Directory.Exists(sourcePlugin.Value))
            Refuse("finish-core-writer-source", "The source plugin is not an ordinary file.");
        if (!string.Equals(Path.GetFileName(sourcePlugin.Value), plugin.Value,
                StringComparison.OrdinalIgnoreCase))
            Refuse("finish-core-writer-plugin", "The source path does not retain the requested plugin self-key.");

        byte[] sourceBytes = File.ReadAllBytes(sourcePlugin.Value);
        if (Hash(sourceBytes) != expectedHash)
            Refuse("finish-core-writer-source-hash", "The source bytes differ from the proposal authority.");
        BethesdaSkyrimFollowerFinishSandboxAuthority sandboxAuthority =
            BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission.AdmitBound(
                copiedMaster,
                copiedMasterHash,
                "finish-core-writer-pack-template-count");
        _ = BethesdaSkyrimNpcFinishCoreSandboxGate.Inspect(sandboxAuthority);
        cancellationToken.ThrowIfCancellationRequested();

        BethesdaSkyrimNpcFinishCoreRaw.RawPluginSnapshot source =
            BethesdaSkyrimNpcFinishCoreRaw.Read(sourceBytes, request.Actor.FormId!.Value.Value);
        if (source.Tes4.Flags != proposal.SourceTes4Flags)
            Refuse("finish-core-writer-tes4", "The source TES4 header differs from the admitted proposal.");
        if (source.Target is null)
            Refuse("finish-core-writer-target", "The source target NPC record is missing.");

        string scratchRoot = Path.Combine(
            Path.GetDirectoryName(sourcePlugin.Value)!,
            ".finish-core-scratch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratchRoot);
        try
        {
            string scratchPath = Path.Combine(scratchRoot, plugin.Value);
            byte[] scratchBytes = BuildScratch(
                sourcePlugin, scratchPath, sandboxAuthority, proposal, requestedMood,
                sourceBytes, out byte[] rebasedSourceBytes, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return BethesdaSkyrimNpcFinishCoreRaw.Splice(
                rebasedSourceBytes, scratchBytes, proposal, source, cancellationToken);
        }
        finally
        {
            if (Directory.Exists(scratchRoot))
                Directory.Delete(scratchRoot, true);
        }
    }

    public void WriteToPath(
        WorkspacePath sourcePlugin,
        WorkspacePath outputPlugin,
        SkyrimNpcFinishCoreProposal proposal,
        WorkspacePath copiedMaster,
        CancellationToken cancellationToken)
    {
        if (File.Exists(outputPlugin.Value) || Directory.Exists(outputPlugin.Value))
            Refuse("finish-core-writer-output-exists", "The output plugin must be a new path.");
        string? parent = Path.GetDirectoryName(outputPlugin.Value);
        if (parent is null || !Directory.Exists(parent))
            Refuse("finish-core-writer-output-parent", "The output plugin parent must already exist.");
        byte[] bytes = Write(sourcePlugin, proposal, copiedMaster, cancellationToken);
        using FileStream stream = new(
            outputPlugin.Value, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        stream.Write(bytes);
    }

    private static byte[] BuildScratch(
        WorkspacePath sourcePlugin,
        string scratchPath,
        BethesdaSkyrimFollowerFinishSandboxAuthority sandboxAuthority,
        SkyrimNpcFinishCoreProposal proposal,
        Mood? requestedMood,
        byte[] sourceBytes,
        out byte[] rebasedSourceBytes,
        CancellationToken cancellationToken)
    {
        SkyrimNpcFinishCoreRequest request = proposal.Request!;
        ModKey pluginKey = ModKey.FromNameAndExtension(request.Source.Plugin!.Value.Value);
        SkyrimMod source = SkyrimMod.CreateFromBinary(
            new ModPath(pluginKey, new FilePath(sourcePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        rebasedSourceBytes = BethesdaSkyrimNpcFinishCoreReindex.Rebase(
            sourceBytes, source, proposal.MasterOrder);
        SkyrimMod mutable = (SkyrimMod)source.DeepCopy();
        mutable.ModHeader.MasterReferences.Clear();
        foreach (string master in proposal.MasterOrder)
            mutable.ModHeader.MasterReferences.Add(new MasterReference
            {
                Master = ModKey.FromNameAndExtension(master)
            });
        mutable.ModHeader.Stats.NextFormID = proposal.NextFormId.Value;
        Npc npc = RequireExactlyOne(
            mutable.Npcs.Where(record =>
                record.FormKey.ModKey == mutable.ModKey &&
                record.FormKey.ID == request.Actor.FormId!.Value.Value),
            "finish-core-writer-npc-count",
            $"NPC_ 0x{request.Actor.FormId!.Value.Value:X8}");

        AddFollowerFactionIfNeeded(npc, PotentialFollowerFactionKey, 0);
        AddFollowerFactionIfNeeded(npc, CurrentFollowerFactionKey, -1);
        npc.Configuration.Flags = (NpcConfiguration.Flag)(
            (uint)npc.Configuration.Flags | 0x00000820u);
        if (request.AiPolicy is { } aiPolicy)
        {
            npc.AIData ??= new AIData();
            npc.AIData.Aggression = (Aggression)(int)aiPolicy.Aggression;
            npc.AIData.Confidence = (Confidence)(int)aiPolicy.Confidence;
            npc.AIData.EnergyLevel = aiPolicy.Energy;
            npc.AIData.Responsibility = (Responsibility)(int)aiPolicy.Morality;
            npc.AIData.Assistance = (Assistance)(int)aiPolicy.Assistance;
            if (requestedMood is { } mood)
                npc.AIData.Mood = mood;
        }

        if (request.OutfitPolicy.Policy == SkyrimNpcFinishCoreOutfitPolicy.ExistingOutfit &&
            request.OutfitPolicy.ExistingOutfit is { } existingOutfit)
            npc.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(ToFormKey(existingOutfit));

        if (request.InventoryPolicy.Policy ==
                SkyrimNpcFinishCoreInventoryPolicy.ReplaceExactInventory &&
            !InventoryMatches(npc, request.InventoryPolicy.DesiredItems))
        {
            npc.Items ??= [];
            npc.Items.Clear();
            foreach (string item in request.InventoryPolicy.DesiredItems)
                npc.Items.Add(new ContainerEntry
                {
                    Item = new ContainerItem
                    {
                        Item = new FormLink<IItemGetter>(ParseFormKey(item)),
                        Count = 1
                    }
                });
        }

        if (!request.PerkPolicy.IsDefault)
        {
            npc.Perks ??= [];
            npc.Perks.Clear();
            foreach (SkyrimNpcFinishCorePerk perk in request.PerkPolicy)
                npc.Perks.Add(new PerkPlacement { Perk = new FormLink<IPerkGetter>(ToFormKey(perk.Form)), Rank = perk.Rank });
        }

        if (request.CombatPolicy is { } localCombatPolicy &&
            npc.CombatStyle.FormKeyNullable is { } localCombatStyleKey &&
            localCombatStyleKey.ModKey == mutable.ModKey)
        {
            CombatStyle template = BethesdaSkyrimNpcFinishCoreCombatStyle.ReadTemplate(
                source,
                RequireExactlyOne(source.Npcs.Where(row => row.FormKey == npc.FormKey),
                    "finish-core-writer-npc-count", npc.FormKey.ToString()),
                request);
            CombatStyle localStyle = RequireExactlyOne(
                mutable.CombatStyles.Where(row => row.FormKey == localCombatStyleKey),
                "finish-core-writer-csty-count", localCombatStyleKey.ToString());
            BethesdaSkyrimNpcFinishCoreCombatStyle.ApplyProfile(
                localStyle, template, localCombatPolicy);
        }

        ImmutableArray<FormReference> outfitItems = request.OutfitRacePolicy == SkyrimNpcFinishCoreOutfitRacePolicy.Clone
            ? BethesdaSkyrimNpcFinishCoreOutfit.Compose(mutable, source, npc.Race.FormKey, proposal)
            : request.OutfitPolicy.ArmorItems;
        foreach (string record in proposal.NewRecords)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string[] parts = record.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            uint id = ParseId(parts[1]);
            switch (parts[0])
            {
                case "RELA":
                    AddRelationship(mutable, npc, pluginKey, id);
                    break;
                case "CSTY":
                    if (request.CombatPolicy is { } combat)
                    {
                        CombatStyle template = BethesdaSkyrimNpcFinishCoreCombatStyle.ReadTemplate(source,
                            RequireExactlyOne(source.Npcs.Where(row => row.FormKey == npc.FormKey),
                                "finish-core-writer-npc-count", npc.FormKey.ToString()), request);
                        CombatStyle clone = mutable.CombatStyles.DuplicateInAsNewRecord(template, new FormKey(pluginKey, id));
                        clone.EditorID = BuildEditorId(request.Actor.EditorId!.Value.Value, "CombatStyle");
                        BethesdaSkyrimNpcFinishCoreCombatStyle.ApplyProfile(clone, template, combat);
                        npc.CombatStyle.SetTo(clone.FormKey);
                    }
                    else
                        AddCombatStyle(mutable, npc, pluginKey, id, request.Actor.EditorId!.Value.Value);
                    break;
                case "OTFT":
                    AddOutfit(mutable, npc, pluginKey, id, outfitItems);
                    break;
                case "ARMA":
                case "ARMO":
                    break;
                case "PACK":
                    AddSandboxPackage(
                        mutable, npc, pluginKey, id, sandboxAuthority,
                        request.Actor.EditorId!.Value.Value);
                    break;
                default:
                    Refuse("finish-core-writer-record", $"Unsupported proposal record '{record}'.");
                    break;
            }
        }

        mutable.WriteToBinary(new FilePath(scratchPath), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
        return File.ReadAllBytes(scratchPath);
    }

    private static void AddFollowerFactionIfNeeded(Npc npc, FormKey faction, sbyte rank)
    {
        RankPlacement[] rows = (npc.Factions ?? [])
            .Where(row => row.Faction.FormKey == faction)
            .ToArray();
        if (rows.Length > 1 || rows.Any(row => row.Rank != rank))
            Refuse("finish-core-writer-faction", "The source follower faction rows are ambiguous.");
        if (rows.Length == 0)
        {
            if (npc.Factions is null)
                Refuse("finish-core-writer-faction", "The NPC faction list is unavailable.");
            npc.Factions.Add(new RankPlacement
            {
                Faction = new FormLink<IFactionGetter>(faction),
                Rank = rank
            });
        }
    }

    private static void AddRelationship(
        SkyrimMod mod,
        Npc npc,
        ModKey pluginKey,
        uint id)
    {
        Relationship[] existing = mod.Relationships.Where(row =>
            row.Parent.FormKey == npc.FormKey && row.Child.FormKey == PlayerKey).ToArray();
        if (existing.Length > 1 || existing.Any(row =>
                row.Rank != Relationship.RankType.Ally || row.Flags != 0 ||
                row.AssociationType.FormKeyNullable is not null))
            Refuse("finish-core-writer-relationship", "The source actor-to-player relationship is ambiguous.");
        if (existing.Length == 1)
            return;
        mod.Relationships.Add(new Relationship(new FormKey(pluginKey, id), SkyrimRelease.SkyrimSE)
        {
            EditorID = BuildEditorId(npc.EditorID ?? "Npc", "PlayerAllyRELA"),
            Parent = new FormLink<INpcGetter>(npc.FormKey),
            Child = new FormLink<INpcGetter>(PlayerKey),
            Rank = Relationship.RankType.Ally,
            Unknown = 0,
            Flags = 0,
            AssociationType = new FormLink<IAssociationTypeGetter>(FormKey.Null)
        });
    }

    private static void AddCombatStyle(
        SkyrimMod mod,
        Npc npc,
        ModKey pluginKey,
        uint id,
        string editorId)
    {
        var style = new CombatStyle(new FormKey(pluginKey, id), SkyrimRelease.SkyrimSE)
        {
            EditorID = BuildEditorId(editorId, "DefensiveCombatStyle"),
            OffensiveMult = 0f,
            DefensiveMult = 1f,
            GroupOffensiveMult = 0f,
            EquipmentScoreMultMelee = 0f,
            EquipmentScoreMultMagic = 0f,
            EquipmentScoreMultRanged = 0f,
            EquipmentScoreMultShout = 0f,
            EquipmentScoreMultUnarmed = 0f,
            EquipmentScoreMultStaff = 0f,
            AvoidThreatChance = 1f,
            Melee = new CombatStyleMelee
            {
                AttackStaggeredMult = 0f,
                PowerAttackStaggeredMult = 0f,
                PowerAttackBlockingMult = 0f,
                BashMult = 0f,
                BashRecoilMult = 0f,
                BashAttackMult = 0f,
                BashPowerAttackMult = 0f,
                SpecialAttackMult = 0f
            },
            CloseRange = new CombatStyleCloseRange
            {
                CircleMult = 0f,
                FallbackMult = 1f,
                FlankDistance = 0f,
                StalkTime = 0f
            },
            Flight = new CombatStyleFlight
            {
                HoverChance = 0f,
                DiveBombChance = 0f,
                GroundAttackChance = 0f,
                HoverTime = 0f,
                GroundAttackTime = 0f,
                PerchAttackChance = 0f,
                PerchAttackTime = 0f,
                FlyingAttackChance = 0f
            }
        };
        mod.CombatStyles.Add(style);
        npc.CombatStyle = new FormLinkNullable<ICombatStyleGetter>(style.FormKey);
    }

    private static void AddOutfit(
        SkyrimMod mod,
        Npc npc,
        ModKey pluginKey,
        uint id,
        ImmutableArray<FormReference> armorItems)
    {
        var outfit = new Outfit(new FormKey(pluginKey, id), SkyrimRelease.SkyrimSE)
        {
            EditorID = BuildEditorId(npc.EditorID ?? "Npc", "PrivateOutfit")
        };
        outfit.Items ??= [];
        foreach (FormReference item in armorItems)
            outfit.Items.Add(new FormLink<IOutfitTargetGetter>(ToFormKey(item)));
        mod.Outfits.Add(outfit);
        npc.DefaultOutfit = new FormLinkNullable<IOutfitGetter>(outfit.FormKey);
    }

    private static void AddSandboxPackage(
        SkyrimMod mod,
        Npc npc,
        ModKey pluginKey,
        uint id,
        BethesdaSkyrimFollowerFinishSandboxAuthority sandboxAuthority,
        string editorId)
    {
        Package package = mod.Packages.DuplicateInAsNewRecord(
            sandboxAuthority.CreateTemplateCopy(), new FormKey(pluginKey, id));
        package.EditorID = BuildEditorId(editorId, "FinishCoreSandbox");
        package.Conditions.Clear();
        var conditionData = new GetFactionRankConditionData
        {
            RunOnType = Condition.RunOnType.Subject
        };
        conditionData.Faction.Link.SetTo(CurrentFollowerFactionKey);
        package.Conditions.Add(new ConditionFloat
        {
            CompareOperator = CompareOperator.LessThan,
            ComparisonValue = 0f,
            Data = conditionData
        });
        npc.Packages.Clear();
        npc.Packages.Add(new FormLink<IPackageGetter>(package.FormKey));
    }

    private static bool InventoryMatches(Npc npc, ImmutableArray<string> desired)
    {
        FormKey[] actual = (npc.Items ?? []).Select(row => row.Item.Item.FormKey).ToArray();
        FormKey[] expected = desired.Select(ParseFormKey).ToArray();
        return actual.SequenceEqual(expected);
    }

    private static FormKey ParseFormKey(string value)
    {
        int separator = value.IndexOf('|');
        ReadOnlySpan<char> idSpan = separator > 0
            ? value.AsSpan(separator + 1)
            : ReadOnlySpan<char>.Empty;
        if (idSpan.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            idSpan = idSpan.Slice(2);
        if (separator <= 0 || !uint.TryParse(
                idSpan,
                NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture,
                out uint id))
            return Refuse<FormKey>(
                "finish-core-writer-form-id", $"Invalid FormReference '{value}'.");
        return new FormKey(
            ModKey.FromNameAndExtension(value.AsSpan(0, separator)), id);
    }

    private static uint ParseId(string value)
    {
        string text = value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? value.AsSpan(2).ToString() : value;
        if (!uint.TryParse(text.AsSpan(), NumberStyles.AllowHexSpecifier,
                CultureInfo.InvariantCulture, out uint id))
            return Refuse<uint>(
                "finish-core-writer-form-id", $"Invalid proposal allocation '{value}'.");
        return id;
    }

    private static FormKey ToFormKey(FormReference reference) =>
        new(ModKey.FromNameAndExtension(reference.Plugin.Value), reference.FormId.Value);

    private static string BuildEditorId(string editorId, string suffix)
    {
        string text = "_" + suffix;
        int length = Math.Min(editorId.Length, 64 - text.Length);
        return editorId[..length] + text;
    }

    private static T RequireExactlyOne<T>(
        IEnumerable<T> source,
        string code,
        string identity)
    {
        T[] matches = source.Take(2).ToArray();
        if (matches.Length == 1)
            return matches[0];
        string observed = matches.Length == 2
            ? "at least 2"
            : matches.Length.ToString(CultureInfo.InvariantCulture);
        return Refuse<T>(
            code,
            $"{identity} requires exactly 1 record; observed {observed} " +
            $"(expected=1; observed={observed}).");
    }

    private static Mood ToMutagenMood(SkyrimNpcFinishCoreMood mood) =>
        mood switch
        {
            SkyrimNpcFinishCoreMood.Neutral => Mood.Neutral,
            SkyrimNpcFinishCoreMood.Angry => Mood.Angry,
            SkyrimNpcFinishCoreMood.Fear => Mood.Fear,
            SkyrimNpcFinishCoreMood.Happy => Mood.Happy,
            SkyrimNpcFinishCoreMood.Sad => Mood.Sad,
            SkyrimNpcFinishCoreMood.Surprise => Mood.Surprised,
            SkyrimNpcFinishCoreMood.Puzzled => Mood.Puzzled,
            SkyrimNpcFinishCoreMood.Disgusted => Mood.Disgusted,
            _ => Refuse<Mood>(
                "finish-core-writer-mood",
                $"Unsupported Finish Core mood '{mood}' ({(int)mood}).")
        };

    private static Sha256Hash Hash(byte[] bytes) =>
        new(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)));

    [DoesNotReturn]
    private static void Refuse(string code, string message) =>
        throw new InvalidDataException($"{code}: {message}");

    [DoesNotReturn]
    private static T Refuse<T>(string code, string message) =>
        throw new InvalidDataException($"{code}: {message}");
}
