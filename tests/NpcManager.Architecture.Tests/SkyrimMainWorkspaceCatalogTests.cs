using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimMainWorkspaceRawNames()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "actorwright-main-workspace-raw-names",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string actorsPath = Path.Combine(root, "RawNames.esp");
            WriteMainWorkspaceRawNameActors(actorsPath);
            PatchMainWorkspaceFullName(
                actorsPath, "CatalogRene", [0x52, 0x65, 0x6E, 0xE9, 0x00]);

            SkyrimMainWorkspacePluginReadResult read =
                new BethesdaSkyrimMainWorkspaceReader().Read(
                    new WorkspacePath(actorsPath));
            SkyrimMainWorkspacePluginRecord compressed = read.Records.Single(
                record => record.FormId == new FormId(0x802));
            Assert(
                compressed.Name == "Compressed Name",
                "A compressed NPC lost its typed-name fallback or catalog row.");
            Assert(
                read.Records.Single(record =>
                        record.FormId == new FormId(0x900))
                    .LeveledNpcEntries.Select(entry => entry.FormId)
                    .SequenceEqual(
                    [
                        new FormId(0x800),
                        new FormId(0x800),
                        new FormId(0x801)
                    ]),
                "Repeated LVLO references, including duplicates, changed source order.");
            Assert(
                read.Records.Single(record =>
                        record.FormId == new FormId(0x800))
                    .NpcMetadata?.IsInLeveledList == true &&
                read.Records.Single(record =>
                        record.FormId == new FormId(0x801))
                    .NpcMetadata?.IsInLeveledList == true,
                "Repeated LVLO references did not classify every NPC in source order.");

            IReadOnlyDictionary<uint, string> rawNames =
                BethesdaRawNpcNameReader.Read(actorsPath);
            Assert(
                rawNames.GetValueOrDefault(0x00000800u) == "Bob",
                "A four-byte nonlocalized FULL payload was mistaken for a localized string ID.");
            Assert(
                rawNames.GetValueOrDefault(0x00000801u) == "René" &&
                !rawNames[0x00000801u].Contains('\uFFFD'),
                "A nonlocalized FULL payload was not decoded as strict Windows-1252.");
            PluginInspection inspection = await new BethesdaPluginReader()
                .ReadAsync(
                    new PluginReadRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(actorsPath)),
                    CancellationToken.None);
            Assert(
                inspection.Diagnostics.Any(item =>
                    item.Code == "npc-raw-name-fallback" &&
                    item.Message.Contains("compressed", StringComparison.OrdinalIgnoreCase)),
                "Compressed raw-name fallback was not reported with a bounded diagnostic.");

            string localizedPath = Path.Combine(root, "Localized.esp");
            WriteMainWorkspaceLocalizedActor(localizedPath);
            PatchMainWorkspaceLocalizedFullId(
                localizedPath, "LocalizedActor", 0x00001234);
            WriteMainWorkspaceLocalizedStrings(
                root,
                "Localized",
                0x00001234,
                "Localized Typed Name");
            IReadOnlyDictionary<uint, string> localizedNames =
                BethesdaRawNpcNameReader.Read(localizedPath);
            Assert(
                !localizedNames.ContainsKey(0x00000800u),
                "A localized four-byte FULL string ID was reinterpreted as text.");
            NpcMetadataScanResult localizedMetadata =
                BethesdaNpcMetadataReader.Read(localizedPath);
            Assert(
                localizedMetadata.Diagnostics.Any(item =>
                    item.Code == "npc-raw-name-fallback" &&
                    item.Message.Contains("localized", StringComparison.OrdinalIgnoreCase)),
                "Localized raw-name fallback was not reported with a bounded diagnostic.");
            PluginInspection localizedInspection = await new BethesdaPluginReader()
                .ReadAsync(
                    new PluginReadRequest(
                        GameEdition.SkyrimSpecialEdition,
                        new WorkspacePath(localizedPath)),
                    CancellationToken.None);
            Assert(
                localizedInspection.Records.Single(record =>
                        record.FormId == new FormId(0x800))
                    .Name == "Localized Typed Name" &&
                localizedInspection.Diagnostics.Any(item =>
                    item.Code == "npc-raw-name-fallback" &&
                    item.Message.Contains(
                        "localized",
                        StringComparison.OrdinalIgnoreCase)),
                "The public plugin reader did not retain the localized NPC row, typed fallback name, and diagnostic.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static int ReadSkyrimMainWorkspacePlugin(string path)
    {
        try
        {
            Sha256Hash before = HashMainWorkspaceFile(path);
            SkyrimMainWorkspacePluginReadResult result =
                new BethesdaSkyrimMainWorkspaceReader().Read(
                    new WorkspacePath(path));
            Sha256Hash after = HashMainWorkspaceFile(path);
            if (before != after)
                throw new InvalidDataException(
                    "Plugin hash changed during the compatibility read.");
            Console.WriteLine(
                $"MAIN-WORKSPACE-READ plugin={result.Plugin.Value} " +
                $"masters={result.Masters.Length} " +
                $"records={result.Records.Length} " +
                $"npcs={result.Records.Count(record => record.Signature == "NPC_")} " +
                $"lvln={result.Records.Count(record => record.Signature == "LVLN")} " +
                $"sha256={before.Value}");
            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(
                $"MAIN-WORKSPACE-READ-FAILED {exception}");
            return 1;
        }
    }

    private static async Task TestSkyrimMainWorkspaceCatalog()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "sky-gui-002-catalog-tests",
            Guid.NewGuid().ToString("N"));
        string dataRoot = Path.Combine(root, "Data");
        Directory.CreateDirectory(dataRoot);
        try
        {
            string skyrimPath = Path.Combine(dataRoot, "Skyrim.esm");
            string actorsPath = Path.Combine(dataRoot, "Actors.esp");
            string patchPath = Path.Combine(dataRoot, "ActorsPatch.esp");
            WriteMainWorkspaceMaster(skyrimPath);
            WriteMainWorkspaceActors(actorsPath);
            WriteMainWorkspacePatch(patchPath);

            ReviewedGameIntake intake = CreateMainWorkspaceIntake(
                root, dataRoot, skyrimPath, actorsPath, patchPath);
            var service = new SkyrimMainWorkspaceCatalogService(
                new BethesdaSkyrimMainWorkspaceReader());
            SkyrimMainWorkspaceCatalogResult result =
                await service.LoadAsync(
                    new SkyrimMainWorkspaceCatalogRequest(intake),
                    CancellationToken.None);

            Assert(result.Accepted, "Reviewed catalog was refused.");
            Assert(result.Snapshot is not null,
                "Accepted catalog did not return a snapshot.");
            SkyrimMainWorkspaceSnapshot snapshot =
                result.Snapshot ??
                throw new InvalidOperationException(
                    "Accepted catalog did not return a snapshot.");
            Assert(snapshot.Records.Length == 4,
                "Catalog did not collapse providers to four winning records.");
            SkyrimMainWorkspaceRecord female =
                snapshot.Records.Single(
                    row => row.Identity.FormId == new FormId(0x800));
            Assert(
                female.Identity.OwnerPlugin ==
                    new PluginName("Actors.esp") &&
                female.Identity.WinningProvider ==
                    new PluginName("ActorsPatch.esp"),
                "Owner/winner identity was conflated.");
            Assert(
                female.ProviderChain.SequenceEqual(
                    [
                        new PluginName("Actors.esp"),
                        new PluginName("ActorsPatch.esp")
                    ]) &&
                female.Sex == NpcSex.Female &&
                female.ChangeState == NpcChangeState.Changed,
                "Winning NPC provenance, sex, or changed state was lost.");

            SkyrimMainWorkspaceRecord leveled =
                snapshot.Records.Single(
                    row => row.Identity.FormId == new FormId(0x900));
            Assert(
                leveled.LeveledNpcEntries.Select(item => item.FormId)
                    .SequenceEqual(
                        [new FormId(0x800), new FormId(0x801)]),
                "Winning LVLN order changed.");
            Assert(
                leveled.ProviderChain.SequenceEqual(
                    [
                        new PluginName("Actors.esp"),
                        new PluginName("ActorsPatch.esp")
                    ]),
                "Winning LVLN provider chain changed.");
            Assert(
                snapshot.Records.Single(
                        row => row.Identity.FormId == new FormId(0x901))
                    .IsEmptyLeveledList,
                "Empty LVLN was hidden instead of represented.");
            Assert(
                snapshot.Records.All(
                    row => row.RawRecordSha256.Length == 64 &&
                           row.RawRecordSha256.Any(character =>
                               character != '0')),
                "Catalog did not retain exact non-default raw-record hashes.");

            TamperLastByte(patchPath);
            SkyrimMainWorkspaceCatalogResult stale =
                await service.LoadAsync(
                    new SkyrimMainWorkspaceCatalogRequest(intake),
                    CancellationToken.None);
            Assert(
                !stale.Accepted &&
                stale.Snapshot is null &&
                stale.Diagnostics.Any(item =>
                    item.Code == "workspace-plugin-hash-changed"),
                "Catalog accepted a plugin that changed after intake review.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteMainWorkspaceMaster(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        WriteMainWorkspaceMod(
            new SkyrimMod(key, SkyrimRelease.SkyrimSE), path);
    }

    private static void WriteMainWorkspaceActors(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = skyrim });
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x800),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogFemale",
            Name = "Catalog Female",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            }
        });
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x801),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogMale",
            Name = "Catalog Male",
            Configuration = new NpcConfiguration()
        });
        mod.LeveledNpcs.Add(MainWorkspaceList(
            new FormKey(key, 0x900),
            "CatalogActors",
            [
                new FormKey(key, 0x800),
                new FormKey(key, 0x801)
            ]));
        mod.LeveledNpcs.Add(MainWorkspaceList(
            new FormKey(key, 0x901),
            "CatalogActorsEmpty",
            []));
        WriteMainWorkspaceMod(mod, path);
    }

    private static void WriteMainWorkspaceRawNameActors(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogBob",
            Name = "Bob",
            Configuration = new NpcConfiguration()
        });
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x801), SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogRene",
            Name = "Rene",
            Configuration = new NpcConfiguration()
        });
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x802), SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogCompressed",
            Name = "Compressed Name",
            Configuration = new NpcConfiguration(),
            IsCompressed = true
        });
        mod.LeveledNpcs.Add(MainWorkspaceList(
            new FormKey(key, 0x900),
            "CatalogRepeatedLvlo",
            [
                new FormKey(key, 0x800),
                new FormKey(key, 0x800),
                new FormKey(key, 0x801)
            ]));
        WriteMainWorkspaceMod(mod, path);
    }

    private static void WriteMainWorkspaceLocalizedActor(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x800), SkyrimRelease.SkyrimSE)
        {
            EditorID = "LocalizedActor",
            Name = "Id0",
            Configuration = new NpcConfiguration()
        });
        WriteMainWorkspaceMod(mod, path);
    }

    private static void PatchMainWorkspaceLocalizedFullId(
        string path,
        string editorId,
        uint stringId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        uint flags = BinaryPrimitives.ReadUInt32LittleEndian(
            bytes.AsSpan(8, 4));
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(8, 4), flags | 0x00000080);
        Span<byte> payload = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(payload, stringId);
        PatchMainWorkspaceFullName(bytes, editorId, payload);
        File.WriteAllBytes(path, bytes);
    }

    private static void WriteMainWorkspaceLocalizedStrings(
        string root,
        string pluginStem,
        uint stringId,
        string value)
    {
        string stringsRoot = Path.Combine(root, "Strings");
        Directory.CreateDirectory(stringsRoot);
        byte[] valueBytes = Encoding.UTF8.GetBytes(value + '\0');
        using var stream = File.Create(Path.Combine(
            stringsRoot,
            pluginStem + "_English.strings"));
        using var writer = new BinaryWriter(stream, Encoding.UTF8);
        writer.Write(1u);
        writer.Write(checked((uint)valueBytes.Length));
        writer.Write(stringId);
        writer.Write(0u);
        writer.Write(valueBytes);
    }

    private static void PatchMainWorkspaceFullName(
        string path,
        string editorId,
        ReadOnlySpan<byte> payload)
    {
        byte[] bytes = File.ReadAllBytes(path);
        PatchMainWorkspaceFullName(bytes, editorId, payload);
        File.WriteAllBytes(path, bytes);
    }

    private static void PatchMainWorkspaceFullName(
        byte[] bytes,
        string editorId,
        ReadOnlySpan<byte> payload)
    {
        byte[] editorBytes = Encoding.ASCII.GetBytes(editorId + '\0');
        int editorOffset = bytes.AsSpan().IndexOf(editorBytes);
        Assert(editorOffset >= 6, $"Fixture EDID '{editorId}' was not found.");
        int recordStart = editorOffset - 6 - 24;
        int recordEnd = checked(
            recordStart + 24 +
            (int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(recordStart + 4, 4)));
        int fullOffset = bytes.AsSpan(editorOffset + editorBytes.Length)
            .IndexOf("FULL"u8);
        Assert(fullOffset >= 0, $"Fixture FULL for '{editorId}' was not found.");
        fullOffset += editorOffset + editorBytes.Length;
        Assert(fullOffset + 6 + payload.Length <= recordEnd,
            $"Fixture FULL for '{editorId}' escaped its NPC record.");
        ushort size = BinaryPrimitives.ReadUInt16LittleEndian(
            bytes.AsSpan(fullOffset + 4, 2));
        Assert(size == payload.Length,
            $"Fixture FULL for '{editorId}' had unexpected size {size}.");
        payload.CopyTo(bytes.AsSpan(fullOffset + 6, payload.Length));
    }

    private static void WriteMainWorkspacePatch(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        ModKey actors = ModKey.FromNameAndExtension("Actors.esp");
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = skyrim });
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = actors });
        mod.Npcs.Add(new Npc(
            new FormKey(actors, 0x800),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "CatalogFemale",
            Name = "Catalog Female Patched",
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female
            }
        });
        mod.LeveledNpcs.Add(MainWorkspaceList(
            new FormKey(actors, 0x900),
            "CatalogActors",
            [
                new FormKey(actors, 0x800),
                new FormKey(actors, 0x801)
            ]));
        WriteMainWorkspaceMod(mod, path);
    }

    private static LeveledNpc MainWorkspaceList(
        FormKey key,
        string editorId,
        ImmutableArray<FormKey> entries)
    {
        var list = new LeveledNpc(key, SkyrimRelease.SkyrimSE)
        {
            EditorID = editorId,
            Entries = []
        };
        foreach (FormKey entry in entries)
        {
            list.Entries.Add(new LeveledNpcEntry
            {
                Data = new LeveledNpcEntryData
                {
                    Level = 1,
                    Count = 1,
                    Reference = new FormLink<INpcSpawnGetter>(entry)
                }
            });
        }
        return list;
    }

    private static void WriteMainWorkspaceMod(
        SkyrimMod mod,
        string path)
    {
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static ReviewedGameIntake CreateMainWorkspaceIntake(
        string root,
        string dataRoot,
        string skyrimPath,
        string actorsPath,
        string patchPath) =>
        new(
            GameEdition.SkyrimSpecialEdition,
            new WorkspacePath(root),
            new WorkspacePath(dataRoot),
            new WorkspacePath(Path.Combine(root, "load-order.json")),
            new WorkspacePath(Path.Combine(root, "output")),
            new Sha256Hash(new string('1', 64)),
            [
                MainWorkspaceEntry(
                    "Skyrim.esm", 0, skyrimPath, []),
                MainWorkspaceEntry(
                    "Actors.esp", 1, actorsPath,
                    [new PluginName("Skyrim.esm")]),
                MainWorkspaceEntry(
                    "ActorsPatch.esp", 2, patchPath,
                    [
                        new PluginName("Skyrim.esm"),
                        new PluginName("Actors.esp")
                    ])
            ],
            [],
            [],
            [],
            0,
            new Sha256Hash(new string('2', 64)),
            new Sha256Hash(new string('3', 64)),
            false);

    private static PluginClosureReviewEntry MainWorkspaceEntry(
        string plugin,
        int order,
        string path,
        ImmutableArray<PluginName> masters) =>
        new(
            new PluginName(plugin),
            order,
            true,
            true,
            true,
            order != 2,
            true,
            new WorkspacePath(path),
            HashMainWorkspaceFile(path),
            masters);

    private static Sha256Hash HashMainWorkspaceFile(string path)
    {
        using var stream = File.OpenRead(path);
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static void TamperLastByte(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);
        stream.Position = stream.Length - 1;
        int value = stream.ReadByte();
        stream.Position = stream.Length - 1;
        stream.WriteByte((byte)(value ^ 0x01));
        stream.Flush(flushToDisk: true);
    }
}
