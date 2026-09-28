using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public sealed record QuestAliasPlacementProvider(PluginName Plugin, WorkspacePath Path, Sha256Hash Sha256);

public sealed class BethesdaSkyrimQuestAliasPlacementAuthority
{
    internal BethesdaSkyrimQuestAliasPlacementAuthority(Package package, FormReference npc, FormReference marker,
        PluginName markerProvider, ImmutableArray<PluginName> masters)
    {
        Package = package;
        Npc = npc;
        Marker = marker;
        MarkerProvider = markerProvider;
        Masters = masters;
    }

    internal Package Package { get; }
    public FormReference Npc { get; }
    public FormReference Marker { get; }
    public PluginName MarkerProvider { get; }
    public ImmutableArray<PluginName> Masters { get; }
    public FormReference SandboxPackage => new(new PluginName(Package.FormKey.ModKey.FileName.String), new FormId(Package.FormKey.ID));
}

public sealed record QuestAliasPlacementOutput(byte[] Plugin, byte[] Seq, Sha256Hash PackageRecordSha256);

/// <summary>Uses existing typed package and quest serialization; source authority is read from exact copied bytes.</summary>
public static class BethesdaSkyrimInteriorPlacementQuestWriter
{
    public static BethesdaSkyrimQuestAliasPlacementAuthority Bind(
        ImmutableArray<QuestAliasPlacementProvider> providers, FormReference coreNpc, Sha256Hash coreHash, FormReference marker)
        => Bind(providers, coreNpc, coreHash, marker, null);

    internal static BethesdaSkyrimQuestAliasPlacementAuthority Bind(
        ImmutableArray<QuestAliasPlacementProvider> providers, FormReference coreNpc, Sha256Hash coreHash,
        FormReference marker, Action<WorkspacePath>? providerHashVerified)
    {
        var admitted = new HashSet<ModKey>();
        Package? sandbox = null;
        PluginName? markerProvider = null;
        PlacedObject? winningMarker = null;
        ImmutableArray<PluginName> masters = [];
        FormKey npcKey = Key(coreNpc);
        FormKey markerKey = Key(marker);
        foreach (QuestAliasPlacementProvider provider in providers)
        {
            using var providerStream = new FileStream(
                provider.Path.Value, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 1024 * 1024, FileOptions.SequentialScan);
            if (new Sha256Hash(Convert.ToHexString(SHA256.HashData(providerStream))) != provider.Sha256)
                throw new InvalidDataException($"Quest-alias copied provider changed: {provider.Plugin}.");
            providerHashVerified?.Invoke(provider.Path);
            providerStream.Position = 0;
            ModKey key = ModKey.FromNameAndExtension(provider.Plugin.Value);
            using var mod = SkyrimMod.CreateFromBinaryOverlay(providerStream, SkyrimRelease.SkyrimSE, key);
            if (mod.ModKey != key)
                throw new InvalidDataException($"Quest-alias copied provider changed: {provider.Plugin}.");
            ModKey[] fileMasters = mod.ModHeader.MasterReferences.Select(x => x.Master).ToArray();
            if (fileMasters.Distinct().Count() != fileMasters.Length || fileMasters.Any(master => !admitted.Contains(master)))
                throw new InvalidDataException($"Quest-alias provider {provider.Plugin} requires its complete preceding master closure.");
            int npcCount = mod.Npcs.Count(x => x.FormKey == npcKey);
            if (key == npcKey.ModKey)
            {
                if (provider.Sha256 != coreHash || npcCount != 1)
                    throw new InvalidDataException("Quest-alias requires the exact Finish Core plugin hash and one bound NPC.");
                INpcGetter npc = mod.Npcs.Single(x => x.FormKey == npcKey);
                if (npc.IsDeleted || npc.Packages.Count != 1 || npc.Packages[0].FormKey.ModKey != key)
                    throw new InvalidDataException("Quest-alias requires one core-owned package linked by the bound NPC.");
                FormKey packageKey = npc.Packages[0].FormKey;
                if (mod.Packages.Count(x => x.FormKey == packageKey) != 1)
                    throw new InvalidDataException("Quest-alias requires exactly one bound core PACK.");
                sandbox = (Package)mod.Packages.Single(x => x.FormKey == packageKey).DeepCopy();
                ValidateSandbox(sandbox);
                masters = fileMasters.Select(x => new PluginName(x.FileName.String)).Append(provider.Plugin).ToImmutableArray();
            }
            else if (npcCount != 0 || sandbox is not null && mod.Packages.Any(x => x.FormKey == sandbox.FormKey))
                throw new InvalidDataException("Quest-alias refuses a later override of the bound core NPC or sandbox PACK.");

            IPlacedObjectGetter[] markerRecords = mod.EnumerateMajorRecords<IPlacedObjectGetter>()
                .Where(x => x.FormKey == markerKey).Take(2).ToArray();
            int markerCount = markerRecords.Length;
            if (markerCount > 1)
                throw new InvalidDataException("Quest-alias marker has duplicate records in one provider.");
            if (markerCount == 1)
            {
                // DeepCopy detaches the winning reference from the disposed overlay snapshot.
                winningMarker = (PlacedObject)markerRecords[0].DeepCopy();
                markerProvider = provider.Plugin;
            }
            admitted.Add(key);
        }
        if (sandbox is null || winningMarker is null || markerProvider is null || winningMarker.IsDeleted ||
            (winningMarker.MajorRecordFlagsRaw & (int)PlacedObject.DefaultMajorFlag.Persistent) == 0 ||
            (winningMarker.MajorRecordFlagsRaw & (int)PlacedObject.DefaultMajorFlag.InitiallyDisabled) != 0)
            throw new InvalidDataException("Quest-alias requires the exact core and a winning live, enabled persistent REFR marker.");
        masters = providers.Select(provider => provider.Plugin)
            .Where(plugin => masters.Contains(plugin) || plugin == marker.Plugin || plugin == markerProvider.Value).ToImmutableArray();
        if (masters.Length > 253 || sandbox.EnumerateFormLinks().Any(link => !link.FormKey.IsNull &&
            !masters.Any(master => master.Value.Equals(link.FormKey.ModKey.FileName.String, StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("Quest-alias package references exceed the bound master closure.");
        return new(sandbox, coreNpc, marker, markerProvider.Value, masters);
    }

    public static QuestAliasPlacementOutput Write(BethesdaSkyrimQuestAliasPlacementAuthority authority, PluginName patch, uint radius)
    {
        ArgumentNullException.ThrowIfNull(authority);
        if (radius == 0) throw new InvalidDataException("Quest-alias radius must be positive.");
        ModKey patchKey = ModKey.FromNameAndExtension(patch.Value);
        var output = new SkyrimMod(patchKey, SkyrimRelease.SkyrimSE) { IsSmallMaster = true };
        foreach (PluginName master in authority.Masters)
            output.ModHeader.MasterReferences.Add(new MasterReference { Master = ModKey.FromNameAndExtension(master.Value) });
        output.ModHeader.Stats.NextFormID = 0x801;
        Package package = (Package)authority.Package.DeepCopy();
        package.IsCompressed = false;
        package.Data.Values.OfType<PackageDataLocation>().Single().Location = new LocationTargetRadius
        {
            Target = new LocationTarget { Link = new FormLink<IPlacedGetter>(Key(authority.Marker)) }, Radius = radius
        };
        output.Packages.Add(package);
        var quest = new Quest(new FormKey(patchKey, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "ActorwrightInteriorPlacement",
            Flags = Quest.Flag.StartGameEnabled | Quest.Flag.RunOnce,
            NextAliasID = 2
        };
        quest.Aliases.Add(new QuestAlias
        {
            ID = 0, Type = QuestAlias.TypeEnum.Reference, Name = "PlacementMarker",
            ForcedReference = new FormLinkNullable<IPlacedGetter>(Key(authority.Marker))
        });
        quest.Aliases.Add(new QuestAlias
        {
            ID = 1, Type = QuestAlias.TypeEnum.Reference, Name = "PlacedNpc",
            CreateReferenceToObject = new CreateReferenceToObject
            {
                Object = new FormLink<ISkyrimMajorRecordGetter>(Key(authority.Npc)),
                AliasID = 0, Create = CreateReferenceToObject.CreateEnum.At
            }
        });
        output.Quests.Add(quest);
        using var stream = new MemoryStream();
        output.WriteToBinary(stream, new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck, MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck, NextFormID = NextFormIDOption.NoCheck
        });
        byte[] plugin = stream.ToArray();
        byte[] seq = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(seq, ((uint)authority.Masters.Length << 24) | 0x800u);
        Sha256Hash packageHash = BethesdaRawPluginInventory.Read(plugin).Single(row => row.Signature == "PACK").Sha256;
        return new(plugin, seq, packageHash);
    }

    private static void ValidateSandbox(Package package)
    {
        bool sandboxProcedure = package.ProcedureTree.Count == 0
            ? package.PackageTemplate.FormKey == Key(BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission.CanonicalSandboxProcedureTemplateForm)
            : package.ProcedureTree.Count == 1 && package.PackageTemplate.FormKey.IsNull && package.ProcedureTree[0].ProcedureType == "Sandbox";
        if (package.IsDeleted || package.VirtualMachineAdapter is not null || package.OwnerQuest.FormKeyNullable is not null ||
            !sandboxProcedure ||
            package.Data.Values.OfType<PackageDataLocation>().Count() != 1 ||
            package.Data.Values.OfType<PackageDataLocation>().Single().Location?.Target is not LocationFallback
                { Type: LocationTargetRadius.LocationType.NearEditorLocation, Data: 0 })
            throw new InvalidDataException("Quest-alias requires the core's script-free single-location editor Sandbox PACK.");
    }

    private static FormKey Key(FormReference value) => new(ModKey.FromNameAndExtension(value.Plugin.Value), value.FormId.Value);
}
