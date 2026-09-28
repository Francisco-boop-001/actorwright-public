using System.Collections.Immutable;
using System.Security.Cryptography;
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
    private static async Task TestSkyrimSavePluginTransformer()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        string root = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            "sky-gui-023-transform-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "Gate23Transform.esp");
            string existingOutput = Path.Combine(
                root,
                "Gate23Transform-existing.esp");
            string newOutput = Path.Combine(root, "Gate23Transform-new.esp");
            WriteGate23TransformSource(source);
            Sha256Hash sourceHash = HashGate23TransformFile(source);
            var snapshot = new SkyrimSavePackageSnapshot(
                new PluginName("Gate23Transform.esp"),
                new FormId(0x800),
                2,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                false,
                1,
                1,
                [],
                [
                    new SkyrimSavePackageNpc(
                        new FormId(0x800),
                        new EditorId("Gate23First"),
                        true),
                    new SkyrimSavePackageNpc(
                        new FormId(0x801),
                        new EditorId("Gate23Second"),
                        true)
                ],
                SkyrimSaveEncodingMode.Windows1252,
                ["Gate23ExistingActors"],
                0x900,
                0);
            SkyrimSavePackageOptions exact =
                SkyrimSavePackageRules.CreateExactOptions(snapshot);
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath("F:\\ExampleGame"));
            var transformer =
                new BethesdaSkyrimSavePluginTransformer(policy, labRoot);

            SkyrimSavePluginTransformResult existing =
                await transformer.TransformAsync(
                    new SkyrimSavePluginTransformRequest(
                        new WorkspacePath(source),
                        new WorkspacePath(existingOutput),
                        snapshot,
                        exact with
                        {
                            Scope = SkyrimSaveScope.AllChanged,
                            TargetMode = SkyrimSaveTargetMode.UpdateExisting,
                            MarkAsMaster = true,
                            EncodingMode = SkyrimSaveEncodingMode.Utf8,
                            LeveledListMode =
                                SkyrimSaveLeveledListMode.Existing,
                            LeveledListEditorId =
                                "Gate23ExistingActors",
                            NoDuplicateLeveledEntries = true
                        }),
                    CancellationToken.None);
            Assert(existing.Completed && existing.Artifact is not null &&
                   existing.Artifact.SourceSha256 == sourceHash &&
                   existing.Artifact.MarkAsMaster &&
                   !existing.Artifact.LightMaster &&
                   existing.Artifact.NpcFormIds.SequenceEqual(
                       [new FormId(0x800), new FormId(0x801)]) &&
                   existing.Artifact.LeveledNpcEditorId ==
                   "Gate23ExistingActors" &&
                   existing.Artifact.LeveledNpcEntries.SequenceEqual(
                       [new FormId(0x800), new FormId(0x801)]) &&
                   existing.Artifact.OtherRecordCount == 4 &&
                   File.Exists(existingOutput) &&
                   HashGate23TransformFile(source) == sourceHash,
                "Fresh-derived update did not preserve the complete source and append all changed NPCs to the existing LVLN.");
            Assert(
                File.ReadAllBytes(existingOutput).AsSpan().IndexOf(
                    new byte[] { 0x42, 0x72, 0xC3, 0xAD, 0x61, 0x72 }) >= 0,
                "UTF-8 transform did not serialize the non-ASCII armor name as UTF-8.");
            SkyrimSavePluginTransformArtifact existingArtifact =
                existing.Artifact ??
                throw new InvalidOperationException(
                    "The existing-LVLN transform returned no artifact.");
            var verifier = new BethesdaSkyrimSavePluginVerifier(
                policy,
                labRoot);
            SkyrimSavePluginVerifyResult verified =
                await verifier.VerifyAsync(
                    new SkyrimSavePluginVerifyRequest(
                        new WorkspacePath(existingOutput),
                        existingArtifact,
                        [
                            new SkyrimSavePluginEncodingProbe(
                                "Bríar")
                        ]),
                    CancellationToken.None);
            Assert(verified.Verified &&
                   verified.Artifact is not null &&
                   verified.Artifact.MarkAsMaster &&
                   verified.Artifact.LeveledNpcEntries.SequenceEqual(
                   [
                       new FormId(0x800),
                       new FormId(0x801)
                   ]),
                "Independent plugin verifier did not confirm flags, UTF-8 evidence, NPCs, and LVLN entries.");
            SkyrimSavePluginVerifyResult wrongFlags =
                await verifier.VerifyAsync(
                    new SkyrimSavePluginVerifyRequest(
                        new WorkspacePath(existingOutput),
                        existingArtifact with
                        {
                            MarkAsMaster = false
                        }),
                    CancellationToken.None);
            Assert(!wrongFlags.Verified &&
                   wrongFlags.Diagnostics.Any(item =>
                       item.Code ==
                       "save-plugin-flags-mismatch"),
                "Independent plugin verifier accepted planted wrong TES4 flags.");
            SkyrimSavePluginVerifyResult wrongEntries =
                await verifier.VerifyAsync(
                    new SkyrimSavePluginVerifyRequest(
                        new WorkspacePath(existingOutput),
                        existingArtifact with
                        {
                            LeveledNpcEntries =
                            [
                                new FormId(0x800)
                            ]
                        }),
                    CancellationToken.None);
            Assert(!wrongEntries.Verified &&
                   wrongEntries.Diagnostics.Any(item =>
                       item.Code ==
                       "save-plugin-lvln-mismatch"),
                "Independent plugin verifier accepted planted wrong LVLN entries.");
            SkyrimSavePluginVerifyResult wrongEncoding =
                await verifier.VerifyAsync(
                    new SkyrimSavePluginVerifyRequest(
                        new WorkspacePath(existingOutput),
                        existingArtifact with
                        {
                            EncodingMode =
                                SkyrimSaveEncodingMode.Windows1252
                        },
                        [
                            new SkyrimSavePluginEncodingProbe(
                                "Bríar")
                        ]),
                    CancellationToken.None);
            Assert(!wrongEncoding.Verified &&
                   wrongEncoding.Diagnostics.Any(item =>
                       item.Code ==
                       "save-plugin-encoding-mismatch"),
                "Independent plugin verifier accepted planted wrong encoding evidence.");
            SkyrimSavePluginVerifyResult wrongHash =
                await verifier.VerifyAsync(
                    new SkyrimSavePluginVerifyRequest(
                        new WorkspacePath(existingOutput),
                        existingArtifact with
                        {
                            OutputSha256 = new Sha256Hash(
                                new string('0', 64))
                        }),
                    CancellationToken.None);
            Assert(!wrongHash.Verified &&
                   wrongHash.Diagnostics.Any(item =>
                       item.Code ==
                       "save-plugin-hash-mismatch"),
                "Independent plugin verifier accepted a planted wrong plugin hash.");

            SkyrimSavePluginTransformResult created =
                await transformer.TransformAsync(
                    new SkyrimSavePluginTransformRequest(
                        new WorkspacePath(source),
                        new WorkspacePath(newOutput),
                        snapshot,
                        exact with
                        {
                            EncodingMode =
                                SkyrimSaveEncodingMode.Windows1252,
                            LeveledListMode =
                                SkyrimSaveLeveledListMode.New,
                            LeveledListEditorId =
                                "Gate23NewActors"
                        }),
                    CancellationToken.None);
            Assert(created.Completed && created.Artifact is not null &&
                   created.Artifact.LeveledNpcEditorId ==
                   "Gate23NewActors" &&
                   created.Artifact.LeveledNpcEntries.SequenceEqual(
                       [new FormId(0x800)]) &&
                   created.Artifact.OtherRecordCount == 4 &&
                   !created.Artifact.MarkAsMaster &&
                   !created.Artifact.LightMaster,
                "Selected-only transform did not write one new LVLN while preserving unrelated records.");
            Assert(
                File.ReadAllBytes(newOutput).AsSpan().IndexOf(
                    new byte[] { 0x42, 0x72, 0xED, 0x61, 0x72 }) >= 0,
                "Windows-1252 transform did not serialize the non-ASCII armor name as code page 1252.");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static void WriteGate23TransformSource(string path)
    {
        ModKey key = ModKey.FromNameAndExtension(Path.GetFileName(path));
        var mod = new SkyrimMod(key, SkyrimRelease.SkyrimSE);
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x800),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "Gate23First"
        });
        mod.Npcs.Add(new Npc(
            new FormKey(key, 0x801),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "Gate23Second"
        });
        var armorAddon = new ArmorAddon(
            new FormKey(key, 0x870),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "Gate23ArmorAddon",
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)0x04
            }
        };
        mod.ArmorAddons.Add(armorAddon);
        var armor = new Armor(
            new FormKey(key, 0x880),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "Gate23Armor",
            Name = "Bríar"
        };
        armor.Armature.Add(new FormLink<IArmorAddonGetter>(
            armorAddon.FormKey));
        mod.Armors.Add(armor);
        var leveledItem = new LeveledItem(
            new FormKey(key, 0x890),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "Gate23LeveledArmor",
            Entries =
            [
                new LeveledItemEntry
                {
                    Data = new LeveledItemEntryData
                    {
                        Level = 1,
                        Count = 1,
                        Reference = new FormLink<IItemGetter>(
                            armor.FormKey)
                    }
                }
            ]
        };
        mod.LeveledItems.Add(leveledItem);
        mod.Outfits.Add(new Outfit(
            new FormKey(key, 0x8A0),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "Gate23Outfit",
            Items =
            [
                new FormLink<IOutfitTargetGetter>(armor.FormKey),
                new FormLink<IOutfitTargetGetter>(
                    leveledItem.FormKey)
            ]
        });
        var list = new LeveledNpc(
            new FormKey(key, 0x900),
            SkyrimRelease.SkyrimSE)
        {
            EditorID = "Gate23ExistingActors",
            Entries = []
        };
        list.Entries.Add(new LeveledNpcEntry
        {
            Data = new LeveledNpcEntryData
            {
                Level = 1,
                Count = 1,
                Reference = new FormLink<INpcSpawnGetter>(
                    new FormKey(key, 0x800))
            }
        });
        mod.LeveledNpcs.Add(list);
        mod.WriteToBinary(new FilePath(path), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static Sha256Hash HashGate23TransformFile(string path)
    {
        using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.SequentialScan);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }
}
