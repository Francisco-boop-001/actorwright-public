using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Drawing;
using System.Security.Cryptography;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private sealed record FollowerRoleIds(
        uint Npc, uint Color, uint Texture, uint Face, uint Relationship,
        uint Body, uint Hands, uint Feet, uint Skin, uint Frontier)
    {
        public static FollowerRoleIds Original(bool privateGraph) =>
            new(0x800, 0x801, 0x802, 0x803, privateGraph ? 0x808u : 0x804u,
                0x804, 0x805, 0x806, 0x807, privateGraph ? 0x809u : 0x805u);

        public static FollowerRoleIds Shuffled { get; } =
            new(0x890, 0x812, 0x8B0, 0x821, 0x845, 0x803, 0x860, 0x8D0, 0x875, 0x900);

        public ImmutableArray<FormId> Occupied(bool privateGraph) =>
            (privateGraph
                ? new[] { Npc, Color, Texture, Face, Relationship, Body, Hands, Feet, Skin }
                : new[] { Npc, Color, Texture, Face, Relationship })
            .Select(id => new FormId(id)).ToImmutableArray();
    }

    private static async Task TestSkyrimFollowerFinishPrivateBody()
    {
        SkyrimFinishMasterFixture.EnsureAvailable();
        string root = Path.GetFullPath(Path.Combine(
            "artifacts",
            "follower-finish-private-body",
            Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);

        try
        {
            var reader = new BethesdaSkyrimFollowerFinishSourceReader();
            string legacyPath = Path.Combine(
                root,
                "LegacyFollower.esp");
            WriteSyntheticFollowerPlugin(
                legacyPath,
                includePrivateSkin: false,
                nextFormId: 0x805);
            SkyrimFollowerFinishRequest legacyRequest =
                CreateRequest(root, legacyPath, privateGraph: false);
            SkyrimFollowerFinishPluginSnapshot legacySnapshot =
                await reader.InspectAsync(
                    legacyRequest,
                    new WorkspacePath(legacyPath),
                    CancellationToken.None);
            Assert(
                legacySnapshot.Valid &&
                legacySnapshot.RecordInventory.SequenceEqual(
                [
                    "NPC_ 0x00000800",
                    "CLFM 0x00000801",
                    "TXST 0x00000802",
                    "HDPT 0x00000803",
                    "RELA 0x00000804"
                ]),
                "The legal five-record follower source was not admitted: " +
                Diagnostics(legacySnapshot));

            string privatePath = Path.Combine(
                root,
                "PrivateFollower.esp");
            WriteSyntheticFollowerPlugin(
                privatePath,
                includePrivateSkin: true,
                nextFormId: 0x809);
            SkyrimFollowerFinishRequest privateRequest =
                CreateRequest(root, privatePath, privateGraph: true);
            SkyrimFollowerFinishPluginSnapshot privateSnapshot =
                await reader.InspectAsync(
                    privateRequest,
                    new WorkspacePath(privatePath),
                    CancellationToken.None);
            Assert(
                privateSnapshot.Valid &&
                privateSnapshot.RecordInventory.SequenceEqual(
                [
                    "NPC_ 0x00000800",
                    "CLFM 0x00000801",
                    "TXST 0x00000802",
                    "HDPT 0x00000803",
                    "ARMA 0x00000804",
                    "ARMA 0x00000805",
                    "ARMA 0x00000806",
                    "ARMO 0x00000807",
                    "RELA 0x00000808"
                ]),
                "The exact nine-record private-body source was not admitted: " +
                Diagnostics(privateSnapshot));
            Assert(
                privateSnapshot.Diagnostics.Any(diagnostic =>
                    diagnostic.Code == "follower-finish-source-plugin-valid"),
                "The admitted private-body source omitted its validity diagnostic.");

            SkyrimFollowerFinishProposal privateProposal =
                CreateProposal(privateRequest, privateSnapshot);
            string templatePath = Path.Combine(root, "Skyrim.esm");
            SkyrimFollowerFinishCoreFixture
                .WriteCanonicalFollowerFinishTemplateMaster(templatePath);
            Sha256Hash templateHash = SkyrimFollowerFinishCoreFixture
                .HashFollowerFinishCoreFile(templatePath);
            var sandboxAdmission =
                new BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission(
                    new KOnlyWorkspacePolicy(
                        new WorkspacePath(root),
                        new WorkspacePath(
                            Path.Combine(root, "protected-live"))),
                    new WorkspacePath(root));
            BethesdaSkyrimFollowerFinishSandboxAuthority sandboxAuthority =
                sandboxAdmission.Admit(
                    new WorkspacePath(templatePath),
                    templateHash);
            await VerifyRoleLayoutsAsync(root, templatePath, sandboxAuthority);
            SkyrimMod sourceForCore =
                LoadSyntheticFollowerPlugin(privatePath);
            SkyrimMod core = new BethesdaSkyrimFollowerFinishCoreWriter().Write(
                sourceForCore,
                privateProposal,
                sandboxAuthority);
            string privateCorePath = Path.Combine(
                root,
                "private-core.esp");
            WriteSyntheticFollowerPlugin(core, privateCorePath);
            BethesdaSkyrimFollowerFinishCoreVerification coreVerification =
                new BethesdaSkyrimFollowerFinishCoreVerifier().Verify(
                    new WorkspacePath(privatePath),
                    new WorkspacePath(privateCorePath),
                    privateProposal,
                    sandboxAuthority);
            Assert(
                coreVerification.Verified,
                "The normal private-body core writer/verifier path refused the exact nine-record source: " +
                string.Join(
                    " | ",
                    coreVerification.Diagnostics.Select(diagnostic =>
                        $"{diagnostic.Code}: {diagnostic.Message}")));
            var markerAuthority =
                new BethesdaSkyrimFollowerFinishMarkerAuthority(
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x3B)),
                new Sha256Hash(new string('A', 64)));
            SkyrimMod placed =
                new BethesdaSkyrimFollowerFinishPlacementWriter().Write(
                    core,
                    privateProposal,
                    new P2Int(-42, 1),
                    markerAuthority);
            Assert(
                placed.ModHeader.Stats.NextFormID ==
                    privateRequest.Allocation.NextFormId.Value &&
                placed.Packages.Count == 1 &&
                placed.Packages.Single().FormKey.ID ==
                    privateRequest.Allocation.Package.Value &&
                placed.EnumerateMajorRecords().Any(record =>
                    record.FormKey.ID ==
                        privateRequest.Allocation.Anchor.Value &&
                    record is PlacedObject) &&
                placed.EnumerateMajorRecords().Any(record =>
                    record.FormKey.ID ==
                        privateRequest.Allocation.Actor.Value &&
                    record is PlacedNpc),
                "Placement did not honor the request-driven 0x809..0x80C allocation.");

            AssertThrows<ArgumentException>(() =>
                _ = new SkyrimFollowerFinishAllocation(
                    new FormId(0x809),
                    new FormId(0x80B),
                    new FormId(0x80A),
                    new FormId(0x80C)));
            AssertThrows<ArgumentOutOfRangeException>(() =>
                _ = new SkyrimFollowerFinishAllocation(
                    new FormId(0xFFE),
                    new FormId(0xFFF),
                    new FormId(0x1000),
                    new FormId(0x1001)));
            AssertThrows<ArgumentException>(() =>
                _ = CreateRequest(
                    root,
                    privatePath,
                    privateGraph: true,
                    occupiedOverride: [
                        new FormId(0x800),
                        new FormId(0x801),
                        new FormId(0x802),
                        new FormId(0x803),
                        new FormId(0x804),
                        new FormId(0x805),
                        new FormId(0x806),
                        new FormId(0x807),
                        new FormId(0x808),
                        new FormId(0x809)
                    ]));

            SkyrimFollowerFinishRequest occupiedMismatch =
                privateRequest with
                {
                    OccupiedLocalFormIds = [
                        new FormId(0x800),
                        new FormId(0x801),
                        new FormId(0x802),
                        new FormId(0x803),
                        new FormId(0x804),
                        new FormId(0x805),
                        new FormId(0x806),
                        new FormId(0x807)
                    ]
                };
            await AssertInvalidAsync(
                reader,
                occupiedMismatch,
                privatePath,
                "follower-finish-source-plugin-owned-form-id-set");

            SkyrimFollowerFinishRequest wrongNpcIdentity =
                privateRequest with { NpcFormId = new FormId(0x801) };
            await AssertInvalidAsync(
                reader,
                wrongNpcIdentity,
                privatePath,
                "follower-finish-source-plugin-npc");

            string malformedArmorPath = Path.Combine(
                root,
                "malformed-armor.esp");
            SkyrimMod malformedArmor =
                LoadSyntheticFollowerPlugin(privatePath);
            malformedArmor.Armors.Single(armor =>
                    armor.FormKey.ID == 0x807)
                .Armature.RemoveAt(2);
            WriteSyntheticFollowerPlugin(malformedArmor, malformedArmorPath);
            await AssertInvalidAsync(
                reader,
                privateRequest with
                {
                    Source = privateRequest.Source with
                    {
                        PluginSha256 = HashPrivateBodyFile(malformedArmorPath)
                    }
                },
                malformedArmorPath,
                "follower-finish-source-plugin-private-skin");

            string malformedHdptPath = Path.Combine(
                root,
                "malformed-hdpt.esp");
            SkyrimMod malformedHdpt =
                LoadSyntheticFollowerPlugin(privatePath);
            malformedHdpt.HeadParts.Single(headPart =>
                    headPart.FormKey.ID == 0x803)
                .Type = HeadPart.TypeEnum.Hair;
            WriteSyntheticFollowerPlugin(malformedHdpt, malformedHdptPath);
            await AssertInvalidAsync(
                reader,
                privateRequest with
                {
                    Source = privateRequest.Source with
                    {
                        PluginSha256 = HashPrivateBodyFile(malformedHdptPath)
                    }
                },
                malformedHdptPath,
                "follower-finish-source-plugin-face-hdpt");

            string malformedRelationshipPath = Path.Combine(
                root,
                "malformed-relationship.esp");
            SkyrimMod malformedRelationship =
                LoadSyntheticFollowerPlugin(privatePath);
            malformedRelationship.Relationships.Single(relationship =>
                    relationship.FormKey.ID == 0x808)
                .Child = new FormLink<INpcGetter>(
                    new FormKey(
                        ModKey.FromNameAndExtension("Skyrim.esm"),
                        0x14));
            WriteSyntheticFollowerPlugin(
                malformedRelationship,
                malformedRelationshipPath);
            await AssertInvalidAsync(
                reader,
                privateRequest with
                {
                    Source = privateRequest.Source with
                    {
                        PluginSha256 = HashPrivateBodyFile(malformedRelationshipPath)
                    }
                },
                malformedRelationshipPath,
                "follower-finish-source-plugin-relationship-endpoints");

            string unknownPath = Path.Combine(
                root,
                "unknown-record.esp");
            SkyrimMod unknownRecord =
                LoadSyntheticFollowerPlugin(privatePath);
            AddSyntheticExtraArmorAddon(unknownRecord);
            WriteSyntheticFollowerPlugin(
                unknownRecord,
                unknownPath);
            await AssertInvalidAsync(
                reader,
                privateRequest with
                {
                    Source = privateRequest.Source with
                    {
                        Plugin = new PluginName("unknown-record.esp"),
                        PluginSha256 = HashPrivateBodyFile(unknownPath)
                    }
                },
                unknownPath,
                "follower-finish-source-plugin-inventory",
                "Actual inventory:",
                "ARMA 0x00000809",
                "roles");
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }

    private static async Task VerifyRoleLayoutsAsync(
        string root,
        string templatePath,
        BethesdaSkyrimFollowerFinishSandboxAuthority authority)
    {
        var reader = new BethesdaSkyrimFollowerFinishSourceReader();
        foreach (bool privateGraph in new[] { false, true })
        {
            FollowerRoleIds ids = FollowerRoleIds.Shuffled;
            string folder = Path.Combine(root, privateGraph ? "shuffled-private" : "shuffled-base");
            Directory.CreateDirectory(folder);
            string sourcePath = Path.Combine(folder, privateGraph ? "PrivateFollower.esp" : "LegacyFollower.esp");
            WriteSyntheticFollowerPluginAndAppendWnam(
                BuildSyntheticFollowerPlugin(privateGraph, ids.Frontier, ids), sourcePath, privateGraph);
            SkyrimFollowerFinishRequest request = CreateRequest(folder, sourcePath, privateGraph, roles: ids);
            if (!privateGraph)
                request = request with { ExpectedBodyRoute = "synthetic-body-route" };
            var snapshot = await reader.InspectAsync(request, new WorkspacePath(sourcePath), CancellationToken.None);
            Assert(snapshot.Valid, $"Shuffled {(privateGraph ? "private" : "base")} role graph was refused: {Diagnostics(snapshot)}");
            var proposal = CreateProposal(request, snapshot);
            var core = new BethesdaSkyrimFollowerFinishCoreWriter().Write(
                LoadSyntheticFollowerPlugin(sourcePath), proposal, authority);
            string corePath = Path.Combine(folder, "core.esp");
            WriteSyntheticFollowerPlugin(core, corePath);
            var verifier = new BethesdaSkyrimFollowerFinishCoreVerifier();
            var verified = verifier.Verify(new WorkspacePath(sourcePath), new WorkspacePath(corePath), proposal, authority);
            Assert(verified.Verified, "Shuffled role core failed independent verification: " +
                string.Join(" | ", verified.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            foreach (var forged in new[]
            {
                proposal with { Request = request with { ExpectedBodyRoute = privateGraph ? "inherited-cotr" : "private-naked-skin" } },
                proposal with { SourceSnapshot = snapshot with { RecordInventory = snapshot.RecordInventory.RemoveAt(0) } },
                proposal with { Request = request with { Source = request.Source with { PluginSha256 = new Sha256Hash(new string('A', 64)) } } }
            })
            {
                var refused = verifier.Verify(new WorkspacePath(sourcePath), new WorkspacePath(corePath), forged, authority);
                Assert(!refused.Verified && refused.Diagnostics.Any(d => d.Code is
                    "follower-finish-core-verify-raw-inventory" or "follower-finish-core-verify-source-hash"),
                    "Core verifier trusted a forged source snapshot, route, or source hash.");
            }

            var placed = new BethesdaSkyrimFollowerFinishPlacementWriter().Write(core, proposal, new P2Int(-42, 1),
                new BethesdaSkyrimFollowerFinishMarkerAuthority(request.Placement.MarkerBase,
                    new Sha256Hash(new string('A', 64))));
            string placedPath = Path.Combine(folder, "placed.esp");
            WriteSyntheticFollowerPlugin(placed, placedPath);
            SkyrimStructuralWorldspaceRecordSanitizer.RemoveRequiredPartialMasterRecord(
                placedPath, request.Placement.Worldspace.Plugin.Value, request.Placement.Worldspace.FormId.Value);
            SkyrimStructuralWorldspaceRecordSanitizer.MinimizeRequiredExteriorCellRecord(
                placedPath, request.Placement.Worldspace.Plugin.Value, request.Placement.Worldspace.FormId.Value,
                request.Placement.Cell.FormId.Value, -42, 1);
            var world = new BethesdaSkyrimFollowerFinishWorldVerifier().Verify(
                new WorkspacePath(placedPath), proposal, new P2Int(-42, 1));
            Assert(world.Verified, "Shuffled role placement failed independent readback: " +
                string.Join(" | ", world.Diagnostics.Select(d => d.Code + ": " + d.Message)));
            Assert(placed.ModHeader.Stats.NextFormID == 0x903 &&
                placed.Packages.Single().FormKey.ID == 0x900 &&
                placed.EnumerateMajorRecords().OfType<PlacedNpc>().Single().Base.FormKey.ID == 0x890,
                "Shuffled allocation or actor base did not follow the declared identities.");
            Assert(HashPrivateBodyFile(sourcePath) == request.Source.PluginSha256,
                "The source changed during core/placement writes.");

            await VerifyRolePluginServiceAsync(root, folder, sourcePath, templatePath, request, ids);
            await VerifyRoleRefusalsAsync(folder, sourcePath, request, ids, authority);
            if (privateGraph)
            {
                byte[] clean = File.ReadAllBytes(corePath);
                foreach (string target in new[] { "ARMA", "WNAM", "owner" })
                {
                    byte[] tampered = clean.ToArray();
                    int record = FindPrivateBodyRawRecord(tampered, target == "WNAM" ? "NPC_" : "ARMA",
                        target == "WNAM" ? ids.Npc : ids.Body);
                    if (target == "owner")
                        tampered[record + 15] = 0;
                    else if (target == "WNAM")
                        ReplaceFollowerSubrecordFormId(tampered, record, "WNAM", 0x0100_0860);
                    else
                        tampered[record + 30] ^= 1;
                    File.WriteAllBytes(corePath, tampered);
                    var refused = verifier.Verify(new WorkspacePath(sourcePath), new WorkspacePath(corePath), proposal, authority);
                    Assert(!refused.Verified, $"Core verifier accepted {target} tampering.");
                }
                File.WriteAllBytes(corePath, clean);
            }
        }
    }

    private static async Task VerifyRolePluginServiceAsync(
        string root, string folder, string sourcePath, string templatePath,
        SkyrimFollowerFinishRequest request, FollowerRoleIds ids)
    {
        var service = new BethesdaSkyrimFollowerFinishPluginService(
            new KOnlyWorkspacePolicy(new WorkspacePath(root),
                new WorkspacePath(Path.Combine(root, "protected-live"))), new WorkspacePath(root));
        var source = new WorkspacePath(sourcePath);
        var snapshot = await service.InspectAsync(request, source, CancellationToken.None);
        Assert(snapshot.Valid, "Plugin service did not admit the shifted source: " + Diagnostics(snapshot));
        string providerPath = WriteFollowerFinishPlacementProvider(folder, templatePath,
            "service-provider", includeCanonicalMarker: true, mutateCanonicalMarker: false);
        string evidencePath = WriteFollowerFinishPlacementEvidence(folder, request,
            "product-local-synthetic-coordinate-audit", "Skyrim.esm|0x00ABCDEF");
        var proposal = BindFollowerFinishPlacementAuthorities(CreateProposal(request, snapshot),
            evidencePath, providerPath);
        request = proposal.Request;
        var output = new WorkspacePath(Path.Combine(folder, "service-output", request.Source.Plugin.Value));
        var written = await service.WriteAsync(request, proposal, source, output, CancellationToken.None);
        Assert(written.Written && written.OutputPlugin == output && written.OutputSha256 == HashPrivateBodyFile(output.Value),
            "Actual plugin service publication failed: " + string.Join(" | ", written.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert(written.Diagnostics.Any(d => d.Code == "follower-finish-plugin-provider-snapshot-lock"),
            "Plugin service did not revalidate the hash-bound canonical provider under its snapshot lock.");
        var verified = await service.VerifyAsync(request, proposal, source, output, CancellationToken.None);
        Assert(verified.Verified && !verified.RuntimeAuthority && verified.OutputSha256 == written.OutputSha256,
            "Actual plugin service independent verification failed: " + string.Join(" | ", verified.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        using (var reopened = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(ModKey.FromNameAndExtension(request.Source.Plugin.Value), new FilePath(output.Value)), SkyrimRelease.SkyrimSE))
        {
            Assert(reopened.ModHeader.Stats.NextFormID == 0x903 &&
                reopened.Packages.Single().FormKey.ID == 0x900 &&
                reopened.Npcs.Single().FormKey.ID == ids.Npc,
                "Published service output lost the shifted NPC or allocation identity.");
        }
        var collision = await service.WriteAsync(request, proposal, source, output, CancellationToken.None);
        Assert(!collision.Written && collision.Diagnostics.Any(d => d.Code == "follower-finish-plugin-output-exists") &&
            HashPrivateBodyFile(output.Value) == written.OutputSha256,
            "Plugin service did not preserve existing output on publication collision.");

        byte[] candidate = File.ReadAllBytes(output.Value);
        int target = FindPrivateBodyRawRecord(candidate,
            request.ExpectedBodyRoute == "private-naked-skin" ? "ARMA" : "TXST",
            request.ExpectedBodyRoute == "private-naked-skin" ? ids.Body : ids.Texture);
        candidate[target + 30] ^= 1;
        File.WriteAllBytes(output.Value, candidate);
        var tampered = await service.VerifyAsync(request, proposal, source, output, CancellationToken.None);
        Assert(!tampered.Verified && tampered.Diagnostics.Any(d => d.Code == "follower-finish-core-verify-preservation"),
            "Plugin service accepted tampering of a preserved source role.");

        File.AppendAllText(providerPath, "tampered-provider");
        var refusedOutput = new WorkspacePath(Path.Combine(folder, "bad-authority-output", request.Source.Plugin.Value));
        var refused = await service.WriteAsync(request, proposal, source, refusedOutput, CancellationToken.None);
        Assert(!refused.Written && !File.Exists(refusedOutput.Value) &&
            refused.Diagnostics.Any(d => d.Code == "follower-finish-plugin-write-failed" &&
                d.Message.Contains("provider snapshot length", StringComparison.OrdinalIgnoreCase)),
            "Plugin service accepted changed authority bytes or published output after refusal.");
        Assert(HashPrivateBodyFile(sourcePath) == request.Source.PluginSha256,
            "Plugin service modified the immutable source.");
    }

    private static async Task VerifyRoleRefusalsAsync(
        string folder, string sourcePath, SkyrimFollowerFinishRequest request,
        FollowerRoleIds ids, BethesdaSkyrimFollowerFinishSandboxAuthority authority)
    {
        var reader = new BethesdaSkyrimFollowerFinishSourceReader();
        var cases = new List<(string Name, Action<SkyrimMod> Mutate, string Code)>
        {
            ("missing-color", mod => mod.Colors.Clear(), "color"),
            ("duplicate-npc", mod => mod.Npcs.DuplicateInAsNewRecord((INpcGetter)mod.Npcs.Single(), new FormKey(mod.ModKey, 0x8E0)), "inventory"),
            ("duplicate-color", mod => mod.Colors.DuplicateInAsNewRecord((IColorRecordGetter)mod.Colors.Single(), new FormKey(mod.ModKey, 0x8E0)), "inventory"),
            ("duplicate-relationship", mod => mod.Relationships.DuplicateInAsNewRecord((IRelationshipGetter)mod.Relationships.Single(), new FormKey(mod.ModKey, 0x8E0)), "inventory"),
            ("duplicate-face", mod => mod.HeadParts.DuplicateInAsNewRecord((IHeadPartGetter)mod.HeadParts.Single(), new FormKey(mod.ModKey, 0x8E0)), "face-hdpt"),
            ("extra-hair", mod => mod.HeadParts.DuplicateInAsNewRecord((IHeadPartGetter)mod.HeadParts.Single(), new FormKey(mod.ModKey, 0x8E0)).Type = HeadPart.TypeEnum.Hair, "inventory"),
            ("bad-hclf", mod => mod.Npcs.Single().HairColor.SetTo(new FormKey(mod.ModKey, ids.Texture)), "hclf"),
            ("bad-head-texture", mod => mod.Npcs.Single().HeadTexture.SetTo(new FormKey(mod.ModKey, ids.Face)), "face-hdpt"),
            ("bad-face-texture", mod => mod.HeadParts.Single().TextureSet.SetTo(new FormKey(mod.ModKey, ids.Face)), "face-hdpt"),
            ("unresolved-headpart", mod => mod.Npcs.Single().HeadParts.Add(new FormLink<IHeadPartGetter>(new FormKey(mod.ModKey, 0x8E0))), "face-hdpt"),
            ("bad-rela-parent", mod => mod.Relationships.Single().Parent.SetTo(new FormKey(mod.ModKey, ids.Color)), "relationship-endpoints"),
            ("bad-rela-child", mod => mod.Relationships.Single().Child.SetTo(new FormKey(mod.ModKey, ids.Npc)), "relationship-endpoints"),
            ("extra-undeclared", mod => mod.TextureSets.DuplicateInAsNewRecord((ITextureSetGetter)mod.TextureSets.Single(), new FormKey(mod.ModKey, 0x8E0)), "inventory")
        };
        if (request.ExpectedBodyRoute == "private-naked-skin")
        {
            cases.Add(("duplicate-body-slot", mod => mod.ArmorAddons.Single(a => a.FormKey.ID == ids.Hands).BodyTemplate!.FirstPersonFlags = BipedObjectFlag.Body, "private-skin"));
            cases.Add(("duplicate-armature-link", mod => mod.Armors.Single().Armature[1] = mod.Armors.Single().Armature[0], "private-skin"));
            cases.Add(("missing-skin", mod => mod.Armors.Clear(), "private-skin"));
            cases.Add(("missing-wnam", mod => mod.Npcs.Single().WornArmor.Clear(), "private-skin"));
        }
        else
            cases.Add(("self-wnam-without-route", mod => mod.Npcs.Single().WornArmor.SetTo(new FormKey(mod.ModKey, ids.Texture)), "private-skin"));
        foreach (var (name, mutate, code) in cases)
        {
            string hostileFolder = Path.Combine(folder, name);
            Directory.CreateDirectory(hostileFolder);
            string path = Path.Combine(hostileFolder, request.Source.Plugin.Value);
            var mod = LoadSyntheticFollowerPlugin(sourcePath);
            mutate(mod);
            WriteSyntheticFollowerPlugin(mod, path);
            var hostile = request with { Source = request.Source with { PluginSha256 = HashPrivateBodyFile(path) } };
            await AssertInvalidAsync(reader, hostile, path, "follower-finish-source-plugin-" + code);
            if (name.StartsWith("duplicate-", StringComparison.Ordinal) || name.StartsWith("extra-", StringComparison.Ordinal))
            {
                hostile = hostile with { OccupiedLocalFormIds = mod.EnumerateMajorRecords()
                    .Select(r => new FormId(r.FormKey.ID)).ToImmutableArray() };
                await AssertInvalidAsync(reader, hostile, path, "follower-finish-source-plugin-" + code);
            }
        }
        foreach (var occupied in new[]
        {
            request.OccupiedLocalFormIds.RemoveAt(0),
            request.OccupiedLocalFormIds.Add(new FormId(0x8E0)),
            request.OccupiedLocalFormIds.Add(request.OccupiedLocalFormIds[0])
        })
            await AssertInvalidAsync(reader, request with { OccupiedLocalFormIds = occupied }, sourcePath,
                "follower-finish-source-plugin-owned-form-id-set");

        foreach (uint invalidOccupied in new uint[] { 0x900, 0x903, 0x950 })
            AssertThrows<ArgumentException>(() => CreateRequest(folder, sourcePath,
                request.ExpectedBodyRoute == "private-naked-skin",
                request.OccupiedLocalFormIds.Add(new FormId(invalidOccupied)), ids));

        var snapshot = await reader.InspectAsync(request, new WorkspacePath(sourcePath), CancellationToken.None);
        foreach (var allocation in new[]
        {
            request.Allocation with { Anchor = new FormId(0x902) },
            request.Allocation with { NextFormId = new FormId(0x1000) },
            new SkyrimFollowerFinishAllocation(new FormId(0x870), new FormId(0x871), new FormId(0x872), new FormId(0x873)),
            new SkyrimFollowerFinishAllocation(new FormId(0x910), new FormId(0x911), new FormId(0x912), new FormId(0x913))
        })
        {
            var invalidRequest = request with { Allocation = allocation };
            await AssertInvalidAsync(reader, invalidRequest, sourcePath,
                allocation.Package.Value != 0x900 ? "follower-finish-source-plugin-next-form-id" : "follower-finish-source-plugin-allocation");
            var forged = CreateProposal(invalidRequest, snapshot with { NextFormId = allocation.Package });
            AssertThrows<InvalidDataException>(() => new BethesdaSkyrimFollowerFinishCoreWriter().Write(
                LoadSyntheticFollowerPlugin(sourcePath), forged, authority));
        }

        // A mutable request can move the frontier behind existing roles while
        // preserving the source/snapshot header agreement. Recheck at the writer.
        var lowAllocation = new SkyrimFollowerFinishAllocation(new FormId(0x870), new FormId(0x871),
            new FormId(0x872), new FormId(0x873));
        var lowRequest = request with { Allocation = lowAllocation };
        var lowSource = LoadSyntheticFollowerPlugin(sourcePath);
        lowSource.ModHeader.Stats.NextFormID = 0x870;
        try
        {
            new BethesdaSkyrimFollowerFinishCoreWriter().Write(lowSource,
                CreateProposal(lowRequest, snapshot with { NextFormId = lowAllocation.Package }), authority);
            Assert(false, "Writer accepted occupied IDs above a matching allocation frontier.");
        }
        catch (InvalidDataException exception)
        {
            Assert(exception.Message.StartsWith("follower-finish-core-allocation:", StringComparison.Ordinal),
                "Writer refused the mutable frontier for an unrelated reason: " + exception.Message);
        }

        await AssertInvalidAsync(reader, request with { ExpectedBodyRoute =
                request.ExpectedBodyRoute == "private-naked-skin" ? "inherited-cotr" : "private-naked-skin" },
            sourcePath, "follower-finish-source-plugin-private-skin");
        if (request.ExpectedBodyRoute == "private-naked-skin")
        {
            foreach (uint wnam in new uint[] { 0, 0x0100_0860, 0x0000_0875 })
            {
                byte[] bytes = File.ReadAllBytes(sourcePath);
                ReplaceFollowerSubrecordFormId(bytes, FindPrivateBodyRawRecord(bytes, "NPC_", ids.Npc), "WNAM", wnam);
                string path = Path.Combine(folder, $"wnam-{wnam:X8}.esp");
                File.WriteAllBytes(path, bytes);
                await AssertInvalidAsync(reader, request, path, "follower-finish-source-plugin-private-skin");
            }
        }
    }

    private static void ReplaceFollowerSubrecordFormId(byte[] bytes, int record, string signature, uint value)
    {
        int end = record + 24 + checked((int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(record + 4, 4)));
        for (int field = record + 24; field < end;)
        {
            ushort length = BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(field + 4, 2));
            if (Encoding.ASCII.GetString(bytes, field, 4) == signature)
            {
                Assert(length == 4, "Expected a four-byte form link.");
                BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(field + 6, 4), value);
                return;
            }
            field += 6 + length;
        }
        throw new InvalidDataException("Synthetic fixture lacks " + signature);
    }

    private static async Task AssertInvalidAsync(
        BethesdaSkyrimFollowerFinishSourceReader reader,
        SkyrimFollowerFinishRequest request,
        string pluginPath,
        string expectedCode,
        params string[] expectedMessageParts)
    {
        SkyrimFollowerFinishPluginSnapshot snapshot =
            await reader.InspectAsync(
                request,
                new WorkspacePath(pluginPath),
                CancellationToken.None);
        Diagnostic? matchingDiagnostic = snapshot.Diagnostics
            .FirstOrDefault(diagnostic =>
                diagnostic.Code == expectedCode &&
                diagnostic.Severity == DiagnosticSeverity.Error);
        Assert(
            !snapshot.Valid && matchingDiagnostic is not null,
            $"Hostile source '{Path.GetFileName(pluginPath)}' was not refused with " +
            $"{expectedCode}: {Diagnostics(snapshot)}");
        if (expectedMessageParts.Length != 0)
            Assert(
                expectedMessageParts.All(part =>
                    matchingDiagnostic!.Message.Contains(
                        part,
                        StringComparison.Ordinal)),
                $"Hostile source '{Path.GetFileName(pluginPath)}' omitted one or more required inventory-message parts: " +
                Diagnostics(snapshot));
    }

    private static SkyrimFollowerFinishRequest CreateRequest(
        string root,
        string pluginPath,
        bool privateGraph,
        ImmutableArray<FormId>? occupiedOverride = null,
        FollowerRoleIds? roles = null)
    {
        roles ??= FollowerRoleIds.Original(privateGraph);
        string pluginName = Path.GetFileName(pluginPath);
        var plugin = new PluginName(pluginName);
        SkyrimFollowerFinishAllocation allocation = new(
            new FormId(roles.Frontier), new FormId(roles.Frontier + 1),
            new FormId(roles.Frontier + 2), new FormId(roles.Frontier + 3));
        ImmutableArray<FormId> occupied = occupiedOverride ??
            roles.Occupied(privateGraph);
        return new SkyrimFollowerFinishRequest(
            1,
            SkyrimFollowerFinishRequest.OperationName,
            new SkyrimFollowerFinishSourceAuthority(
                new WorkspacePath(Path.Combine(root, pluginName + ".zip")),
                1,
                new Sha256Hash(new string('1', 64)),
                new WorkspacePath(Path.Combine(root, pluginName + ".manifest.json")),
                new Sha256Hash(new string('2', 64)),
                plugin,
                HashPrivateBodyFile(pluginPath),
                new Sha256Hash(new string('3', 64)),
                new Sha256Hash(new string('4', 64))),
            new EditorId("SyntheticFollower"),
            new FormId(roles.Npc),
            occupied,
            new FormReference(
                new PluginName("Skyrim.esm"),
                new FormId(0x13746)),
            privateGraph ? "private-naked-skin" : "inherited-cotr",
            true,
            [
                new NpcFactionEntry(
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x5C84D)),
                    0),
                new NpcFactionEntry(
                    new FormReference(
                        new PluginName("Skyrim.esm"),
                        new FormId(0x5C84E)),
                    -1)
            ],
            new FormId(roles.Relationship),
            "Ally",
            1,
            new SkyrimFollowerFinishHairChange(
                new FormId(roles.Color),
                new SkyrimPackedRgb(0x94876A),
                new SkyrimPackedRgb(0xD6BE83)),
            true,
            false,
            new SkyrimFollowerFinishSandbox(
                "Sandbox",
                768,
                "continuous",
                new FormReference(plugin, allocation.Anchor),
                "GetFactionRank(Skyrim.esm|0x0005C84E) < 0"),
            new SkyrimFollowerFinishPlacement(
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x3C)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0xA16A)),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x3B)),
                new SkyrimExteriorTransform(1, 2, 3, 0, 0, 90),
                new SkyrimExteriorTransform(4, 5, 6, 0, 0, 0)),
            allocation,
            [
                $"PACK {allocation.Package}",
                $"REFR {allocation.Anchor}",
                $"ACHR {allocation.Actor}"
            ],
            [
                "TES4: set ESL flag and mechanical header metadata",
                $"CLFM {new FormId(roles.Color)}: 0x94876A -> 0xD6BE83",
                $"NPC_ {new FormId(roles.Npc)}: add PKID {allocation.Package}"
            ],
            [new AssetPath("Data/SyntheticFollower.esp")],
            new WorkspacePath(Path.Combine(root, pluginName + ".candidate")),
            new WorkspacePath(Path.Combine(root, pluginName + ".candidate.zip")),
            "A self-contained synthetic follower finish source.");
    }

    private static SkyrimFollowerFinishProposal CreateProposal(
        SkyrimFollowerFinishRequest request,
        SkyrimFollowerFinishPluginSnapshot snapshot) =>
        new(
            1,
            SkyrimFollowerFinishRequest.OperationName,
            new Sha256Hash(new string('5', 64)),
            request,
            snapshot,
            request.AllowedExistingRecordChanges,
            request.AllowedNewRecords,
            request.Allocation.NextFormId,
            ["TES4", "NPC_", "CLFM", "TXST", "HDPT", "ARMA", "ARMO", "RELA"],
            request.AllowedPackageFiles,
            false);

    private static SkyrimMod CreatePlacementCore(
        SkyrimFollowerFinishRequest request,
        string path)
    {
        ModKey plugin = ModKey.FromNameAndExtension(
            Path.GetFileName(path));
        var core = new SkyrimMod(plugin, SkyrimRelease.SkyrimSE)
        {
            IsSmallMaster = true
        };
        core.ModHeader.MasterReferences.Add(
            new MasterReference
            {
                Master = ModKey.FromNameAndExtension("Skyrim.esm")
            });
        core.ModHeader.Stats.NextFormID =
            request.Allocation.NextFormId.Value;
        core.Packages.Add(
            new Package(
                new FormKey(plugin, request.Allocation.Package.Value),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "SyntheticFollowerSandbox"
            });
        WriteSyntheticFollowerPlugin(core, path);
        return core;
    }

    private static void WriteSyntheticFollowerPlugin(
        string path,
        bool includePrivateSkin,
        uint nextFormId)
    {
        WriteSyntheticFollowerPluginAndAppendWnam(
            BuildSyntheticFollowerPlugin(includePrivateSkin, nextFormId),
            path,
            includePrivateSkin);
    }

    private static SkyrimMod BuildSyntheticFollowerPlugin(
        bool includePrivateSkin,
        uint nextFormId,
        FollowerRoleIds? roles = null)
    {
        roles ??= FollowerRoleIds.Original(includePrivateSkin);
        ModKey plugin = ModKey.FromNameAndExtension(
            includePrivateSkin
                ? "PrivateFollower.esp"
                : "LegacyFollower.esp");
        ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var mod = new SkyrimMod(plugin, SkyrimRelease.SkyrimSE);
        mod.ModHeader.MasterReferences.Add(
            new MasterReference { Master = skyrim });
        mod.ModHeader.Stats.NextFormID = nextFormId;

        FormKey npcKey = new(plugin, roles.Npc);
        FormKey colorKey = new(plugin, roles.Color);
        FormKey textureKey = new(plugin, roles.Texture);
        FormKey headPartKey = new(plugin, roles.Face);
        FormKey relationshipKey = new(plugin, roles.Relationship);
        var npc = new Npc(npcKey, SkyrimRelease.SkyrimSE)
        {
            FormVersion = 44,
            EditorID = "SyntheticFollower",
            Race = new FormLink<IRaceGetter>(new FormKey(skyrim, 0x13746)),
            HairColor = new FormLinkNullable<IColorRecordGetter>(colorKey),
            HeadTexture = new FormLinkNullable<ITextureSetGetter>(textureKey),
            Configuration = new NpcConfiguration
            {
                Flags = NpcConfiguration.Flag.Female,
                HealthOffset = 10
            },
            Weight = 50
        };
        npc.HeadParts.Add(
            new FormLink<IHeadPartGetter>(headPartKey));
        npc.Factions.Add(new RankPlacement
        {
            Faction = new FormLink<IFactionGetter>(
                new FormKey(skyrim, 0x5C84D)),
            Rank = 0
        });
        npc.Factions.Add(new RankPlacement
        {
            Faction = new FormLink<IFactionGetter>(
                new FormKey(skyrim, 0x5C84E)),
            Rank = -1
        });
        mod.Npcs.Add(npc);
        mod.Colors.Add(
            new ColorRecord(colorKey, SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "SyntheticFollowerHair",
                Color = Color.FromArgb(255, 148, 135, 106),
                Playable = true
            });
        mod.TextureSets.Add(
            new TextureSet(textureKey, SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "SyntheticFollowerFaceTextures",
                Diffuse = "textures/synthetic-follower.dds"
            });
        mod.HeadParts.Add(
            new HeadPart(headPartKey, SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "SyntheticFollowerFace",
                Type = HeadPart.TypeEnum.Face,
                TextureSet = new FormLinkNullable<ITextureSetGetter>(
                    textureKey)
            });

        if (includePrivateSkin)
        {
            AddSyntheticSkinRecords(mod, roles);
        }

        mod.Relationships.Add(
            new Relationship(relationshipKey, SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "SyntheticFollowerPlayerAlly",
                Parent = new FormLink<INpcGetter>(npcKey),
                Child = new FormLink<INpcGetter>(
                    new FormKey(skyrim, 0x7)),
                Rank = Relationship.RankType.Ally,
                Unknown = 0,
                Flags = 0,
                AssociationType = new FormLink<IAssociationTypeGetter>(
                    FormKey.Null)
            });
        return mod;
    }

    private static void AddSyntheticSkinRecords(SkyrimMod mod, FollowerRoleIds roles)
    {
        ModKey plugin = mod.ModKey;
        ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        (uint Id, BipedObjectFlag Slot, string Name)[] regions =
        [
            (roles.Body, BipedObjectFlag.Body, "Body"),
            (roles.Hands, BipedObjectFlag.Hands, "Hands"),
            (roles.Feet, BipedObjectFlag.Feet, "Feet")
        ];
        foreach ((uint id, BipedObjectFlag slot, string name) in regions)
        {
            var addon = new ArmorAddon(
                new FormKey(plugin, id),
                SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "SyntheticFollower" + name + "SkinAddon",
                Race = new FormLinkNullable<IRaceGetter>(
                    new FormKey(skyrim, 0x13746)),
                BodyTemplate = new BodyTemplate
                {
                    FirstPersonFlags = slot
                },
                WorldModel = new GenderedItem<Model?>(
                    null,
                    new Model
                    {
                        File = "actors/character/synthetic-" +
                               name.ToLowerInvariant() + ".nif"
                    })
            };
            mod.ArmorAddons.Add(addon);
        }
        var armor = new Armor(
            new FormKey(plugin, roles.Skin),
            SkyrimRelease.SkyrimSE)
        {
            FormVersion = 44,
            EditorID = "SyntheticFollowerPrivateSkin",
            Race = new FormLinkNullable<IRaceGetter>(
                new FormKey(skyrim, 0x13746))
        };
        foreach ((uint id, _, _) in regions)
            armor.Armature.Add(
                new FormLink<IArmorAddonGetter>(
                    new FormKey(plugin, id)));
        mod.Armors.Add(armor);
    }

    private static void AddSyntheticExtraArmorAddon(SkyrimMod mod)
    {
        ModKey plugin = mod.ModKey;
        ModKey skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        mod.ArmorAddons.Add(
            new ArmorAddon(
                new FormKey(plugin, 0x809),
                SkyrimRelease.SkyrimSE)
            {
                FormVersion = 44,
                EditorID = "SyntheticFollowerExtraSkinAddon",
                Race = new FormLinkNullable<IRaceGetter>(
                    new FormKey(skyrim, 0x13746)),
                BodyTemplate = new BodyTemplate
                {
                    FirstPersonFlags = BipedObjectFlag.Body
                }
            });
    }

    private static SkyrimMod LoadSyntheticFollowerPlugin(string path)
    {
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                ModKey.FromNameAndExtension(Path.GetFileName(path)),
                new FilePath(path)),
            SkyrimRelease.SkyrimSE);
        return (SkyrimMod)overlay.DeepCopy();
    }

    private static void WriteSyntheticFollowerPlugin(
        SkyrimMod mod,
        string path) =>
        WriteSyntheticFollowerPluginAndAppendWnam(
            mod,
            path,
            appendPrivateBodyWnam: false);

    private static void WriteSyntheticFollowerPluginAndAppendWnam(
        SkyrimMod mod,
        string path,
        bool appendPrivateBodyWnam)
    {
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
        if (appendPrivateBodyWnam)
            AppendPrivateBodyWnam(path, mod.Armors.Single().FormKey.ID,
                mod.Npcs.First().FormKey.ID);
    }

    private static void AppendPrivateBodyWnam(
        string path,
        uint skinLocalFormId,
        uint npcLocalFormId = 0x800)
    {
        byte[] source = File.ReadAllBytes(path);
        (int record, List<int> groups) =
            FindPrivateBodyRawRecordWithGroups(
                source,
                "NPC_",
                npcLocalFormId);
        int bodyLength = checked((int)
            BinaryPrimitives.ReadUInt32LittleEndian(
                source.AsSpan(record + 4, 4)));
        int insertAt = FindPrivateBodyWnamInsertOffset(
            source,
            record + 24,
            checked(record + 24 + bodyLength));
        byte[] subrecord = new byte[10];
        Encoding.ASCII.GetBytes("WNAM").CopyTo(subrecord, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(
            subrecord.AsSpan(4, 2),
            4);
        BinaryPrimitives.WriteUInt32LittleEndian(
            subrecord.AsSpan(6, 4),
            0x0100_0000u | skinLocalFormId);
        byte[] output = new byte[source.Length + subrecord.Length];
        source.AsSpan(0, insertAt).CopyTo(output);
        subrecord.CopyTo(output, insertAt);
        source.AsSpan(insertAt).CopyTo(
            output.AsSpan(insertAt + subrecord.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(
            output.AsSpan(record + 4, 4),
            checked((uint)(bodyLength + subrecord.Length)));
        foreach (int group in groups)
        {
            uint groupLength = BinaryPrimitives.ReadUInt32LittleEndian(
                output.AsSpan(group + 4, 4));
            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(group + 4, 4),
                checked(groupLength + (uint)subrecord.Length));
        }
        File.WriteAllBytes(path, output);
    }

    private static int FindPrivateBodyWnamInsertOffset(
        byte[] source,
        int start,
        int end)
    {
        int position = start;
        int insertAt = -1;
        while (position < end)
        {
            if (position + 6 > end)
                throw new InvalidDataException(
                    "The synthetic NPC subrecord header is truncated.");
            string signature = Encoding.ASCII.GetString(
                source,
                position,
                4);
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(
                source.AsSpan(position + 4, 2));
            int fieldEnd = checked(position + 6 + size);
            if (fieldEnd > end)
                throw new InvalidDataException(
                    "The synthetic NPC subrecord exceeds its record.");
            if (signature == "RNAM")
            {
                if (size != 4 || insertAt >= 0)
                    throw new InvalidDataException(
                        "The synthetic NPC must contain one four-byte RNAM.");
                insertAt = fieldEnd;
            }
            position = fieldEnd;
        }
        if (insertAt < 0)
            throw new InvalidDataException(
                "The synthetic NPC is missing RNAM for deterministic WNAM insertion.");
        return insertAt;
    }

    private static void ReplaceRawRecordSignature(
        byte[] bytes,
        string originalSignature,
        uint localFormId,
        string replacementSignature)
    {
        int offset = FindPrivateBodyRawRecord(
            bytes,
            originalSignature,
            localFormId);
        Encoding.ASCII.GetBytes(replacementSignature)
            .CopyTo(bytes, offset);
    }

    private static int FindPrivateBodyRawRecord(
        byte[] bytes,
        string signature,
        uint localFormId)
    {
        return FindPrivateBodyRawRecordWithGroups(
                bytes,
                signature,
                localFormId)
            .Offset;
    }

    private static (int Offset, List<int> Groups)
        FindPrivateBodyRawRecordWithGroups(
            byte[] bytes,
            string signature,
            uint localFormId)
    {
        if (Encoding.ASCII.GetString(bytes, 0, 4) != "TES4")
            throw new InvalidDataException(
                "Synthetic plugin did not begin with TES4.");
        int start = checked(
            24 + (int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(4, 4)));
        (int Offset, List<int> Groups)? found =
            Walk(start, bytes.Length, []);
        return found ??
            throw new InvalidDataException(
                $"Synthetic plugin omitted {signature} 0x{localFormId:X}.");

        (int Offset, List<int> Groups)? Walk(
            int position,
            int end,
            List<int> groups)
        {
            while (position < end)
            {
                string current = Encoding.ASCII.GetString(
                    bytes,
                    position,
                    4);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 4, 4));
                if (current == "GRUP")
                {
                    int groupEnd = checked(position + (int)size);
                    var childGroups = new List<int>(groups)
                    {
                        position
                    };
                    (int Offset, List<int> Groups)? nested =
                        Walk(position + 24, groupEnd, childGroups);
                    if (nested is not null)
                        return nested;
                    position = groupEnd;
                    continue;
                }
                uint rawFormId = BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position + 12, 4));
                if (current == signature &&
                    (rawFormId & 0x00FF_FFFFu) == localFormId)
                    return (position, groups);
                position = checked(position + 24 + (int)size);
            }
            return null;
        }
    }

    private static string Diagnostics(
        SkyrimFollowerFinishPluginSnapshot snapshot) =>
        string.Join(
            " | ",
            snapshot.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Code}: {diagnostic.Message}"));

    private static Sha256Hash HashPrivateBodyFile(string path) =>
        new(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
}
