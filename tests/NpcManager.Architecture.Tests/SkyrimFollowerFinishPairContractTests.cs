using System.Collections.Immutable;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimFollowerFinishPairContracts()
    {
        SkyrimFollowerFinishPairAllocation allocation =
            SkyrimFollowerFinishPairAllocation.Default;
        Assert(
            allocation.NewRecordIds
                .Select(value => value.Value)
                .SequenceEqual(
                Enumerable.Range(0x805, 7)
                    .Select(value => checked((uint)value))),
            "Pair allocation is not the closed 0x805..0x80B sequence.");
        Assert(
            allocation.NextFormId.Value == 0x80C,
            "Pair allocation NextFormID drifted.");
        Assert(
            allocation.NewRecordIds.Distinct().Count() == 7,
            "Pair allocation contains duplicate local IDs.");
        SkyrimFollowerFinishPairCompanionAllocation companionAllocation =
            SkyrimFollowerFinishPairCompanionAllocation.Default;
        Assert(
            companionAllocation.NewRecordIds
                .Select(value => value.Value)
                .SequenceEqual(
                    Enumerable.Range(0x808, 3)
                        .Select(value => checked((uint)value))) &&
            companionAllocation.NextFormId.Value == 0x80B,
            "Companion finish allocation is not the closed 0x808..0x80A sequence.");
        Assert(
            SkyrimFollowerFinishPairRequest.LegacySchemaVersionValue == 2 &&
            SkyrimFollowerFinishPairRequest.SchemaVersionValue == 3 &&
            SkyrimFollowerFinishPairRequest.OperationName ==
            "skyrim-paired-follower-finish",
            "Pair request discriminator drifted.");

        PropertyInfo property =
            typeof(SkyrimFollowerFinishPairAllocation)
                .GetProperty(
                    nameof(
                        SkyrimFollowerFinishPairAllocation
                            .NewRecordIds)) ??
            throw new InvalidOperationException(
                "Pair allocation computed inventory is missing.");
        Assert(
            property.GetCustomAttribute<JsonIgnoreAttribute>() is not null,
            "Computed pair allocation inventory must not widen strict JSON.");
        PropertyInfo companionProperty =
            typeof(SkyrimFollowerFinishPairCompanionAllocation)
                .GetProperty(
                    nameof(
                        SkyrimFollowerFinishPairCompanionAllocation
                            .NewRecordIds)) ??
            throw new InvalidOperationException(
                "Companion allocation computed inventory is missing.");
        Assert(
            companionProperty.GetCustomAttribute<JsonIgnoreAttribute>()
                is not null,
            "Computed companion allocation inventory must not widen strict JSON.");
        Assert(
            typeof(BethesdaSkyrimFollowerFinishPairService)
                .GetConstructors()
                .Single()
                .GetParameters()
                .Any(parameter =>
                    parameter.ParameterType ==
                    typeof(
                        ISkyrimFollowerFinishPairFaceGeomService)),
            "The paired writer does not require the fail-closed FaceGeom hair-tint service.");

        SyntheticFaceGeomHairRegionsDocument faceGeomFixture =
            SyntheticFaceGeomHairRegionsFixture.Load(
                ActorwrightWorkspace.ResolveRoot().Value);
        byte[] faceGeom = faceGeomFixture.Bytes;
        var hairFinish = new SkyrimFollowerFinishPairHairFinish(
            new FormId(0x801),
            0xD6BE83,
            0x393728,
            ["SyntheticMainHair", "SyntheticHairline", "SyntheticLashes"],
            [34, 34, 34],
            [78, 76, 70]);
        var faceGeomService =
            new SkyrimFollowerFinishPairFaceGeomService();
        SkyrimFollowerFinishPairFaceGeomRewrite rewrite =
            faceGeomService.Rewrite(
                faceGeom.ToImmutableArray(),
                hairFinish);
        Assert(
            rewrite.Bytes.Length == faceGeom.Length &&
            rewrite.ChangedByteOffsets.Length == 27 &&
            Convert.ToHexString(SHA256.HashData(
                rewrite.Bytes.AsSpan())) ==
            "4811BEC71350B41790E597881529DF438173409F23A822DC7CB5F8766BFAF9FE",
            "The synthetic FaceGeom hair-tint rewrite drifted.");
        faceGeomService.Verify(
            faceGeom.ToImmutableArray(),
            rewrite.Bytes,
            hairFinish,
            rewrite.ChangedByteOffsets);

        string[] commands =
        [
            "npc follower-finish pair-analyze",
            "npc follower-finish pair-apply",
            "npc follower-finish pair-verify"
        ];
        Assert(
            commands.All(name =>
                CommandCatalog.All.Any(command =>
                    command.Name == name)),
            "The paired transaction is not exposed by all three CLI stages.");
        return Task.CompletedTask;
    }

    private static Task TestSkyrimFollowerFinishPairArchiveLayout()
    {
        WorkspacePath workspaceRoot = ActorwrightWorkspace.ResolveRoot();
        string root = ActorwrightWorkspace.WorkRoot(
            workspaceRoot,
            "paired-archive-layout-" + Guid.NewGuid().ToString("N")).Value;
        string archivePath = root + ".zip";
        Directory.CreateDirectory(Path.Combine(root, "Data", "meshes"));
        Directory.CreateDirectory(Path.Combine(root, "evidence"));
        try
        {
            File.WriteAllBytes(
                Path.Combine(root, "Data", "PairCompanion.esp"),
                [0x43, 0x4F, 0x4D, 0x50]);
            File.WriteAllBytes(
                Path.Combine(root, "Data", "PairSubject.esp"),
                [0x53, 0x55, 0x42, 0x4A]);
            File.WriteAllBytes(
                Path.Combine(root, "Data", "meshes", "face.nif"),
                [0x4E, 0x49, 0x46]);
            File.WriteAllText(
                Path.Combine(
                    root,
                    "README-NPCMANAGER-RUNTIME-TEST.txt"),
                "runtime handoff");
            File.WriteAllText(
                Path.Combine(root, "evidence", "proposal.json"),
                "{}");
            File.WriteAllText(
                Path.Combine(root, "npcmanager-paired-package.json"),
                "{}");

            MethodInfo method =
                typeof(BethesdaSkyrimFollowerFinishPairService)
                    .GetMethod(
                        "CreateDeterministicArchive",
                        BindingFlags.NonPublic |
                        BindingFlags.Static) ??
                throw new InvalidOperationException(
                    "The paired archive writer is missing.");
            method.Invoke(null, [root, archivePath]);

            using ZipArchive archive = ZipFile.OpenRead(archivePath);
            string[] names = archive.Entries
                .Select(entry => entry.FullName)
                .Order(StringComparer.Ordinal)
                .ToArray();
            Assert(
                names.SequenceEqual(
                [
                    "PairCompanion.esp",
                    "PairSubject.esp",
                    "README-NPCMANAGER-RUNTIME-TEST.txt",
                    "meshes/face.nif"
                ]),
                "The paired archive is not a direct-install projection of Data plus the runtime README.");
            Assert(
                names.Any(name =>
                    Path.GetExtension(name).Equals(
                        ".esp",
                        StringComparison.OrdinalIgnoreCase) &&
                    !name.Contains('/')),
                "The paired archive has no root-level plugin for mod-manager recognition.");
        }
        finally
        {
            if (File.Exists(archivePath))
                File.Delete(archivePath);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }

        return Task.CompletedTask;
    }
}
