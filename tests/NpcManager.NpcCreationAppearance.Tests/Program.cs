using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using Noggog;
using NpcManager.Application;
using NpcManager.BodyGen;
using NpcManager.Domain;
using NpcManager.FaceGen;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;
using NpcManager.Pipeline;
using NpcManager.TestInfrastructure;

namespace NpcManager.NpcCreationAppearance.Tests;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (NpcManager.TestInfrastructure.StandaloneSelectorInventory.TryList(args, typeof(Program)))
        {
            return 0;
        }

        var tests = new (string Name, Func<Task> Run)[]
        {
            ("template-carrier appearance preserves Gate 1 plugin bytes",
                TestTemplateCarrierCompatibility),
            ("fully authored appearance writes and independently reads back",
                TestAuthoredWriteReadbackAndBinding),
            ("external HDPT refuses a target race absent from RNAM FLST",
                TestExternalHeadPartRaceRestriction),
            ("male NPC refuses a female-only external HDPT",
                () => TestIncompatibleExternalHeadPartSex(
                    NpcSex.Male, HeadPart.Flag.Female, "male-female-only")),
            ("female NPC refuses a male-only external HDPT",
                () => TestIncompatibleExternalHeadPartSex(
                    NpcSex.Female, HeadPart.Flag.Male, "female-male-only")),
            ("female NPC accepts both-sex HDPTs",
                () => TestCompatibleHeadPartSexFlags(NpcSex.Female,
                    HeadPart.Flag.Male | HeadPart.Flag.Female, "female-both-sex")),
            ("male NPC accepts both-sex HDPTs",
                () => TestCompatibleHeadPartSexFlags(NpcSex.Male,
                    HeadPart.Flag.Male | HeadPart.Flag.Female, "male-both-sex")),
            ("female NPC accepts HDPTs with neither sex flag",
                () => TestCompatibleHeadPartSexFlags(NpcSex.Female, 0, "female-neither-sex")),
            ("male NPC accepts HDPTs with neither sex flag",
                () => TestCompatibleHeadPartSexFlags(NpcSex.Male, 0, "male-neither-sex")),
            ("new NPC can intentionally omit its default outfit",
                TestNoDefaultOutfitWriteReadback),
            ("output-owned naked skin binds private WNAM body hands and feet",
                TestNakedSkinBinding),
            ("output-owned naked skin ignores null retained-master links",
                TestNakedSkinNullRetainedMasterLink),
            ("output-owned naked skin can preserve mesh-embedded null TXST regions",
                TestMeshEmbeddedNakedSkinBinding),
            ("output-owned exposed outfit binds the exact female body TXST",
                TestExposedOutfitSkinBinding),
            ("exposed outfit clones exact winning override and closes retained masters",
                TestExposedOutfitWinningProviderAndMasterClosure),
            ("fully authored appearance override preserves source ownership and unrelated fields",
                TestAuthoredOverrideOwnershipAndPreservation),
            ("existing-NPC package keys FaceGen to the source owner",
                TestExistingNpcOriginKeyedPackage),
            ("follower role writes exact vanilla support semantics without leaking to other roles",
                TestFollowerRoleWriteReadbackAndIsolation),
            ("typed SSE runtime VMAD is proposal-bound and independently verified",
                TestRuntimeAppearanceWriteReadbackAndTamper),
            ("external plugin authorities are hash- and proposal-bound",
                TestExternalPluginAuthorityBinding),
            ("fully authored appearance rejects malformed native payloads",
                TestMalformedAuthoredAppearance)
        };
        var passed = 0;
        foreach (var (name, run) in tests)
        {
            try
            {
                await run();
                passed++;
                Console.WriteLine($"PASS {name}");
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"FAIL {name}: {exception.Message}");
                return 1;
            }
        }

        Console.WriteLine($"RESULT PASS {passed}/{tests.Length}");
        return 0;
    }

    private static async Task TestTemplateCarrierCompatibility()
    {
        const string expectedPluginSha256 =
            "a09d7c270e97b43b6b924fe2904fb8763a59bf5bcd22d4da8d2aef815fcd2527";
        var context = CreateContext("template-compatibility");
        try
        {
            var baseline = CreateRequest(
                context, TemplateCarrierNpcAppearanceSource.Instance);
            var request = baseline with
            {
                Output = new WorkspacePath(Path.Combine(
                    context.ScratchRoot, "NpcManagerGate1Final.esp")),
                Identity = new NpcCreationIdentity(
                    new EditorId("NPCM_Gate1Final"),
                    new NpcName("NPC Manager Gate 1 Final")),
                Stats = baseline.Stats with { Weight = 0f }
            };
            var proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(proposal.IsApplicable,
                "The explicit template-carrier proposal was refused: " +
                Format(proposal.Diagnostics));
            var result = await context.Service.ApplyAsync(
                request, proposal, CancellationToken.None);
            Assert(result.Applied && result.Verification is { IsValid: true } &&
                   result.OutputHash?.Value == expectedPluginSha256,
                "The explicit template-carrier source changed the proven Gate 1 plugin bytes: " +
                Format(result.Diagnostics));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestAuthoredWriteReadbackAndBinding()
    {
        var context = CreateContext("deterministic");
        try
        {
            var validAppearance = CreateValidAppearance(context.Template);
            var request = CreateRequest(context, validAppearance);
            var first = await context.Service.AnalyzeAsync(request, CancellationToken.None);
            Assert(first.IsApplicable && first.ProposalHash is not null,
                "The valid authored appearance did not produce an applicable proposal: " +
                Format(first.Diagnostics));
            var firstBytes = await File.ReadAllBytesAsync(request.Proposal.Value);

            using (var document = JsonDocument.Parse(firstBytes))
            {
                var appearance = document.RootElement.GetProperty("appearance");
                var authored = appearance.GetProperty("fullyAuthored");
                var hair = authored.GetProperty("hairColor");
                var privateTextureDocument = authored.GetProperty("faceTextureSet");
                var ownedHead = authored.GetProperty("orderedHeadParts")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("kind").GetString() ==
                        "output-owned-face-hdpt");
                Assert(appearance.GetProperty("kind").GetString() == "fully-authored-skyrim" &&
                       authored.GetProperty("orderedHeadParts").GetArrayLength() ==
                           validAppearance.OrderedHeadParts.Length &&
                       ownedHead.GetProperty("allocatedLocalFormId").GetString() == "0x00000803" &&
                       ownedHead.GetProperty("qualifiedExternalFaceHdpt").GetString() ==
                           "High Poly Head.esm|0x00000A06" &&
                       hair.GetProperty("kind").GetString() == "output-owned-clfm" &&
                       hair.GetProperty("allocatedLocalFormId").GetString() == "0x00000801" &&
                       hair.GetProperty("packedRgb").GetUInt32() == 0x11_22_33 &&
                       privateTextureDocument.GetProperty("kind").GetString() ==
                           "output-owned-private-head-txst" &&
                       privateTextureDocument.GetProperty("allocatedLocalFormId").GetString() ==
                           "0x00000802" &&
                       privateTextureDocument.GetProperty("paths").GetProperty("diffuse").GetString() ==
                           "actors/character/emi2/skin/femalehead.dds" &&
                       authored.GetProperty("faceMorphs").GetProperty("nam9Sliders").GetArrayLength() == 18 &&
                       authored.GetProperty("faceTints").GetProperty("layers").GetArrayLength() == 2,
                    "The deterministic proposal did not retain the complete authored appearance union.");
            }

            File.Delete(request.Proposal.Value);
            var second = await context.Service.AnalyzeAsync(request, CancellationToken.None);
            Assert(second.IsApplicable && second.ProposalHash == first.ProposalHash,
                "Repeating the same authored analysis changed the proposal hash.");
            var secondBytes = await File.ReadAllBytesAsync(request.Proposal.Value);
            Assert(firstBytes.AsSpan().SequenceEqual(secondBytes),
                "Repeating the same authored analysis changed the canonical proposal bytes.");

            var privateTexture = (OutputOwnedSkyrimNpcFaceTextureSet)validAppearance.FaceTextureSet;
            var blankSlotAppearance = validAppearance with
            {
                FaceTextureSet = new OutputOwnedSkyrimNpcFaceTextureSet(
                    privateTexture.AllocatedLocalFormId,
                    privateTexture.Paths with
                    {
                        BacklightMaskOrSpecular =
                            new AssetPath("actors/character/male/blankdetailmap.dds")
                    })
            };
            var blankSlotRequest = CreateRequest(context, blankSlotAppearance) with
            {
                Proposal = new WorkspacePath(Path.Combine(
                    context.ScratchRoot, "blank-slot7-proposal.json")),
                Output = new WorkspacePath(Path.Combine(
                    context.ScratchRoot, "blank-slot7.esp"))
            };
            var blankSlot = await context.Service.AnalyzeAsync(
                blankSlotRequest, CancellationToken.None);
            Assert(blankSlot.IsApplicable &&
                   blankSlot.Diagnostics.All(item =>
                       item.Code != "npc-create-appearance-private-txst-path-duplicate"),
                "A private TXST using the canonical blank detail map for empty height and slot 7 was refused: " +
                Format(blankSlot.Diagnostics));

            var changedAppearance = ((FullyAuthoredSkyrimNpcAppearanceSource)request.Appearance) with
            {
                Qnam = new SkyrimQnamRgb(32f / 255f, 97f / 255f, 160f / 255f)
            };
            var changedRequest = request with { Appearance = changedAppearance };
            var refused = await context.Service.ApplyAsync(
                changedRequest, second, CancellationToken.None);
            Assert(!refused.Applied &&
                   refused.Diagnostics.Any(item => item.Code == "npc-create-proposal-request-mismatch") &&
                   !File.Exists(request.Output.Value),
                "A changed authored appearance was not proposal-bound and refused before writing.");

            var applied = await context.Service.ApplyAsync(
                request, second, CancellationToken.None);
            Assert(applied.Applied && applied.Verification is
            {
                IsValid: true,
                MajorRecordCount: 4,
                NpcRecordCount: 1,
                NextFormId.Value: 0x804
            } && File.Exists(request.Output.Value),
                "The fully authored appearance did not survive typed and raw read-back: " +
                Format(applied.Diagnostics));

            TamperUniqueAssetPath(request.Output.Value, privateTexture.Paths.Diffuse);
            var hostile = await context.Service.VerifyAsync(
                request, second, CancellationToken.None);
            Assert(!hostile.IsValid &&
                   hostile.Diagnostics.Any(item => item.Code == "npc-create-raw-owned-txst-mismatch"),
                "The independent verifier accepted a hostile raw TXST path mismatch: " +
                Format(hostile.Diagnostics));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestExternalPluginAuthorityBinding()
    {
        var context = CreateContext("external-plugin-authority");
        try
        {
            FullyAuthoredSkyrimNpcAppearanceSource appearance =
                CreateValidAppearance(context.Template);
            var provider = new PluginName("High Poly Head.esm");
            string source = Path.Combine(
                Path.GetDirectoryName(context.Template.Value)!, provider.Value);
            string authorityRoot = Path.Combine(context.ScratchRoot, "reviewed-providers");
            Directory.CreateDirectory(authorityRoot);
            var copied = new WorkspacePath(Path.Combine(authorityRoot, provider.Value));
            WriteRaceRestrictedFaceHeadPartProvider(source, copied.Value);
            var authority = new NpcCreationPluginAuthority(
                provider, copied, ComputeHash(copied));
            NpcCreationRequest request = CreateRequest(context, appearance) with
            {
                PluginAuthorities = [authority]
            };

            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(proposal.IsApplicable &&
                   proposal.PluginAuthorities.SequenceEqual(request.PluginAuthorities),
                "A race-restricted external Face HDPT provider authority was not admitted and proposal-bound: " +
                Format(proposal.Diagnostics));

            NpcCreationRequest changedPath = request with
            {
                PluginAuthorities =
                [authority with { PluginPath = new WorkspacePath(source) }]
            };
            NpcCreationResult pathRefused = await context.Service.ApplyAsync(
                changedPath, proposal, CancellationToken.None);
            Assert(!pathRefused.Applied &&
                   pathRefused.Diagnostics.Any(item =>
                       item.Code == "npc-create-proposal-request-mismatch") &&
                   !File.Exists(request.Output.Value),
                "Changing an admitted plugin-authority path after analysis was not refused.");

            File.Delete(request.Proposal.Value);
            NpcCreationRequest staleHash = request with
            {
                PluginAuthorities =
                [authority with { ExpectedSha256 = new Sha256Hash(new string('A', 64)) }]
            };
            NpcCreationProposal refused = await context.Service.AnalyzeAsync(
                staleHash, CancellationToken.None);
            Assert(!refused.IsApplicable &&
                   refused.Diagnostics.Any(item =>
                       item.Code == "npc-create-plugin-authority-hash-mismatch") &&
                   !File.Exists(staleHash.Proposal.Value) &&
                   !File.Exists(staleHash.Output.Value),
                "A stale external plugin authority was not refused before proposal or output write.");
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestCompatibleHeadPartSexFlags(
        NpcSex sex,
        HeadPart.Flag sexFlags,
        string scenario)
    {
        var context = CreateContext(scenario);
        try
        {
            FullyAuthoredSkyrimNpcAppearanceSource appearance =
                CreateHeadPartCompatibilityAppearance(context.Template);
            ImmutableArray<NpcCreationPluginAuthority> authorities =
                WriteHeadPartCompatibilityProviders(
                context, appearance, sexFlags);
            NpcCreationRequest baseline = CreateRequest(context, appearance);
            NpcCreationRequest request = baseline with
            {
                Traits = baseline.Traits with { Sex = sex },
                PluginAuthorities = authorities
            };

            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(proposal.IsApplicable,
                $"A {sex} NPC refused compatible HDPT sex flags {sexFlags}: " +
                Format(proposal.Diagnostics));

            NpcCreationResult result = await context.Service.ApplyAsync(
                request, proposal, CancellationToken.None);
            Assert(result.Applied && result.Verification is { IsValid: true } &&
                   File.Exists(request.Output.Value) &&
                   File.ReadAllBytes(request.Output.Value).Length > 0,
                $"A {sex} NPC did not preserve compatible HDPTs through byte/readback verification: " +
                Format(result.Diagnostics));
            using var output = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(
                    ModKey.FromNameAndExtension(Path.GetFileName(request.Output.Value)),
                    new FilePath(request.Output.Value)),
                SkyrimRelease.SkyrimSE);
            Assert(output.Npcs.Count == 1,
                "Independent typed readback did not find the authored NPC.");
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestIncompatibleExternalHeadPartSex(
        NpcSex sex,
        HeadPart.Flag incompatibleFlag,
        string scenario)
    {
        var context = CreateContext(scenario);
        try
        {
            FullyAuthoredSkyrimNpcAppearanceSource appearance =
                CreateHeadPartCompatibilityAppearance(context.Template);
            ExternalSkyrimNpcHeadPart external = OrdinaryExternalHeadPart(appearance);
            HeadPart.Flag compatibleFlags = sex == NpcSex.Female
                ? HeadPart.Flag.Female
                : HeadPart.Flag.Male;
            ImmutableArray<NpcCreationPluginAuthority> authorities =
                WriteHeadPartCompatibilityProviders(
                context, appearance, compatibleFlags, external.Hdpt, incompatibleFlag);
            NpcCreationRequest baseline = CreateRequest(context, appearance);
            NpcCreationRequest request = baseline with
            {
                Traits = baseline.Traits with { Sex = sex },
                PluginAuthorities = authorities
            };

            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(!proposal.IsApplicable &&
                   !File.Exists(request.Proposal.Value) &&
                   !File.Exists(request.Output.Value),
                $"A {sex} NPC admitted an incompatible external {external.Type} HDPT: " +
                Format(proposal.Diagnostics));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestExternalHeadPartRaceRestriction()
    {
        var context = CreateContext("external-wrong-race");
        try
        {
            FullyAuthoredSkyrimNpcAppearanceSource appearance =
                CreateHeadPartCompatibilityAppearance(context.Template);
            ExternalSkyrimNpcHeadPart external = OrdinaryExternalHeadPart(appearance);
            ImmutableArray<NpcCreationPluginAuthority> authorities =
                WriteHeadPartCompatibilityProviders(
                context,
                appearance,
                HeadPart.Flag.Female,
                raceRestrictedHeadPart: external.Hdpt);
            NpcCreationRequest request = CreateRequest(context, appearance) with
            {
                PluginAuthorities = authorities
            };

            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(!proposal.IsApplicable &&
                   !File.Exists(request.Proposal.Value) &&
                   !File.Exists(request.Output.Value),
                $"An external {external.Type} HDPT admitted a target race absent from its RNAM FLST: " +
                Format(proposal.Diagnostics));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestNoDefaultOutfitWriteReadback()
    {
        var context = CreateContext("no-default-outfit");
        try
        {
            NpcCreationRequest baseline = CreateRequest(
                context, CreateValidAppearance(context.Template));
            NpcCreationRequest request = baseline with
            {
                References = baseline.References with
                {
                    DefaultOutfit = default
                }
            };

            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(proposal.IsApplicable,
                "An intentional no-default-outfit request was refused: " +
                Format(proposal.Diagnostics));

            NpcCreationResult result = await context.Service.ApplyAsync(
                request, proposal, CancellationToken.None);
            Assert(result.Applied &&
                   result.Verification is { IsValid: true },
                "The no-default-outfit NPC did not survive independent readback: " +
                Format(result.Diagnostics));

            ModKey outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(request.Output.Value));
            using var output = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(request.Output.Value)),
                SkyrimRelease.SkyrimSE);
            INpcGetter npc = output.Npcs.Single();
            Assert(npc.DefaultOutfit.IsNull,
                "Typed readback found a default outfit on an intentionally naked NPC.");
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestNakedSkinBinding()
    {
        var context = CreateContext("naked-skin");
        try
        {
            NakedSkinFixture skin = CreateNakedSkinFixture(context);
            FullyAuthoredSkyrimNpcAppearanceSource appearance =
                CreateValidAppearance(context.Template) with
                {
                    NakedSkinBinding =
                        new OutputOwnedSkyrimNpcNakedSkinBinding(
                            new FormId(0x807),
                            skin.SourceSkinArmor,
                            [
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Body,
                                    new FormId(0x804),
                                    skin.SourceBodyAddon,
                                    skin.BodyTextureSet,
                                    new AssetPath("Meshes/Actors/Character/Chel/body_1.nif")),
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Hands,
                                    new FormId(0x805),
                                    skin.SourceHandsAddon,
                                    skin.HandsTextureSet,
                                    new AssetPath("Meshes/Actors/Character/Chel/hands_1.nif")),
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Feet,
                                    new FormId(0x806),
                                    skin.SourceFeetAddon,
                                    skin.FeetTextureSet,
                                    new AssetPath("Meshes/Actors/Character/Chel/feet_1.nif"))
                            ])
                };

            NpcCreationRequest request = CreateRequest(context, appearance) with
            {
                References = CreateRequest(context, appearance).References with
                {
                    DefaultOutfit = null
                },
                PluginAuthorities = [skin.Authority]
            };
            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(proposal.IsApplicable,
                "The exact naked-skin binding proposal was refused: " +
                Format(proposal.Diagnostics));

            NpcCreationResult result = await context.Service.ApplyAsync(
                request, proposal, CancellationToken.None);
            Assert(result.Applied && result.Verification is
                   {
                       IsValid: true,
                       MajorRecordCount: 8,
                       NpcRecordCount: 1,
                       NextFormId.Value: 0x808
                   },
                "The private naked-skin binding did not survive typed and raw readback: " +
                Format(result.Diagnostics));

            var outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(request.Output.Value));
            using (var output = SkyrimMod.CreateFromBinaryOverlay(
                       new ModPath(outputKey, new FilePath(request.Output.Value)),
                       SkyrimRelease.SkyrimSE))
            {
                Assert(output.Npcs.Single().DefaultOutfit.IsNull &&
                       output.Armors.Single().FormKey ==
                       new FormKey(outputKey, 0x807) &&
                       output.ArmorAddons.Count == 3,
                    "Typed inspection did not find one private naked ARMO and three private ARMAs.");
                foreach ((FormKey key, string model) in new[]
                         {
                             (new FormKey(outputKey, 0x804), "meshes\\actors\\character\\chel\\body_1.nif"),
                             (new FormKey(outputKey, 0x805), "meshes\\actors\\character\\chel\\hands_1.nif"),
                             (new FormKey(outputKey, 0x806), "meshes\\actors\\character\\chel\\feet_1.nif")
                         })
                {
                    IArmorAddonGetter addon = output.ArmorAddons.Single(
                        item => item.FormKey == key);
                    string? actualModel = addon.WorldModel?.Female?.File
                        ?.ToString()
                        .Replace('/', '\\');
                    Assert(string.Equals(
                               actualModel,
                               model,
                               StringComparison.OrdinalIgnoreCase),
                        "The private naked ARMA did not retain the staged female mesh path: " +
                        (actualModel ?? "<null>"));
                }
            }

            uint outputIndex = checked((uint)proposal.Masters.Length << 24);
            var skinLink = new byte[4];
            BinaryPrimitives.WriteUInt32LittleEndian(
                skinLink, outputIndex | 0x807U);
            Assert(CountSequence(
                       await File.ReadAllBytesAsync(request.Output.Value),
                       BuildSubrecord("WNAM", skinLink)) == 1,
                "Raw inspection did not find exactly one NPC WNAM pointing at the private skin ARMO.");

            byte[] acceptedBytes = await File.ReadAllBytesAsync(request.Output.Value);
            TamperUniqueLinkSubrecord(
                request.Output.Value,
                "WNAM",
                outputIndex | 0x807U);
            NpcCreationVerificationResult hostile =
                await context.Service.VerifyAsync(
                    request,
                    proposal,
                    CancellationToken.None);
            Assert(!hostile.IsValid &&
                   hostile.Diagnostics.Any(item =>
                       item.Code == "npc-create-raw-skin-armor-mismatch"),
                "The independent verifier accepted a hostile WNAM skin link.");
            await File.WriteAllBytesAsync(request.Output.Value, acceptedBytes);
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestNakedSkinNullRetainedMasterLink()
    {
        var context = CreateContext("naked-skin-null-retained-master");
        try
        {
            NakedSkinFixture skin = CreateNakedSkinFixture(
                context,
                includeFemaleTextureSet: true,
                includeMaleTextureSet: false);
            FullyAuthoredSkyrimNpcAppearanceSource appearance =
                CreateValidAppearance(context.Template) with
                {
                    NakedSkinBinding =
                        new OutputOwnedSkyrimNpcNakedSkinBinding(
                            new FormId(0x807),
                            skin.SourceSkinArmor,
                            [
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Body,
                                    new FormId(0x804),
                                    skin.SourceBodyAddon,
                                    skin.BodyTextureSet,
                                    new AssetPath("Meshes/Actors/Character/Test/body_1.nif")),
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Hands,
                                    new FormId(0x805),
                                    skin.SourceHandsAddon,
                                    skin.HandsTextureSet,
                                    new AssetPath("Meshes/Actors/Character/Test/hands_1.nif")),
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Feet,
                                    new FormId(0x806),
                                    skin.SourceFeetAddon,
                                    skin.FeetTextureSet,
                                    new AssetPath("Meshes/Actors/Character/Test/feet_1.nif"))
                            ])
                };
            NpcCreationRequest request = CreateRequest(context, appearance) with
            {
                References = CreateRequest(context, appearance).References with
                {
                    DefaultOutfit = null
                },
                PluginAuthorities = [skin.Authority]
            };

            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request,
                CancellationToken.None);

            Assert(proposal.IsApplicable &&
                   proposal.Masters.All(item =>
                       !string.Equals(
                           item.Value,
                           "Null",
                           StringComparison.OrdinalIgnoreCase)),
                "A legitimate null ARMA FormKey was treated as a retained plugin master: " +
                Format(proposal.Diagnostics));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestMeshEmbeddedNakedSkinBinding()
    {
        var context = CreateContext("mesh-embedded-naked-skin");
        try
        {
            NakedSkinFixture skin = CreateNakedSkinFixture(
                context, includeFemaleTextureSet: false);
            FullyAuthoredSkyrimNpcAppearanceSource appearance =
                CreateValidAppearance(context.Template) with
                {
                    NakedSkinBinding =
                        new OutputOwnedSkyrimNpcNakedSkinBinding(
                            new FormId(0x807),
                            skin.SourceSkinArmor,
                            [
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Body,
                                    new FormId(0x804),
                                    skin.SourceBodyAddon,
                                    null,
                                    new AssetPath("Meshes/Actors/Character/Chel/body_1.nif")),
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Hands,
                                    new FormId(0x805),
                                    skin.SourceHandsAddon,
                                    null,
                                    new AssetPath("Meshes/Actors/Character/Chel/hands_1.nif")),
                                new OutputOwnedSkyrimNpcNakedSkinRegionBinding(
                                    SkyrimNpcSkinRegion.Feet,
                                    new FormId(0x806),
                                    skin.SourceFeetAddon,
                                    null,
                                    new AssetPath("Meshes/Actors/Character/Chel/feet_1.nif"))
                            ])
                };

            NpcCreationRequest request = CreateRequest(context, appearance) with
            {
                References = CreateRequest(context, appearance).References with
                {
                    DefaultOutfit = null
                },
                PluginAuthorities = [skin.Authority]
            };
            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(proposal.IsApplicable,
                "The mesh-embedded naked-skin proposal was refused: " +
                Format(proposal.Diagnostics));

            NpcCreationResult result = await context.Service.ApplyAsync(
                request, proposal, CancellationToken.None);
            Assert(result.Applied && result.Verification is
                   {
                       IsValid: true,
                       MajorRecordCount: 8,
                       NpcRecordCount: 1,
                       NextFormId.Value: 0x808
                   },
                "The mesh-embedded naked-skin binding did not survive readback: " +
                Format(result.Diagnostics));

            var outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(request.Output.Value));
            using var output = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(outputKey, new FilePath(request.Output.Value)),
                SkyrimRelease.SkyrimSE);
            Assert(output.ArmorAddons.Count == 3 &&
                   output.ArmorAddons.All(item =>
                       item.SkinTexture?.Female?.FormKeyNullable is null),
                "The private mesh-embedded ARMAs did not preserve null female TXST routing.");
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestExposedOutfitSkinBinding()
    {
        var context = CreateContext("exposed-outfit-skin");
        try
        {
            FullyAuthoredSkyrimNpcAppearanceSource baseAppearance =
                CreateValidAppearance(context.Template);
            string dataRoot = Path.GetDirectoryName(context.Template.Value)!;
            var skyrimKey = ModKey.FromNameAndExtension("Skyrim.esm");
            string skyrimPath = Path.Combine(dataRoot, "Skyrim.esm");
            var skyrimProvider = new WorkspacePath(skyrimPath);
            var skyrimHash = ComputeHash(skyrimProvider);
            var skyrimAuthority = new NpcCreationPluginAuthority(
                new PluginName("Skyrim.esm"),
                skyrimProvider,
                skyrimHash);
            FormKey targetTexture;
            using (var skyrim = SkyrimMod.CreateFromBinaryOverlay(
                       new ModPath(skyrimKey, new FilePath(skyrimPath)),
                       SkyrimRelease.SkyrimSE))
            {
                targetTexture = skyrim.TextureSets
                    .First(item => !item.IsDeleted)
                    .FormKey;
            }

            var appearance = baseAppearance with
            {
                ExposedOutfitSkinBinding =
                    new OutputOwnedSkyrimNpcExposedOutfitSkinBinding(
                        new FormId(0x804),
                        new FormId(0x805),
                        new FormId(0x806),
                        Bound(
                            "OTFT",
                            new FormReference(
                            new PluginName("Skyrim.esm"),
                            new FormId(0x1DC10))),
                        Bound(
                            "ARMO",
                            new FormReference(
                            new PluginName("Skyrim.esm"),
                            new FormId(0x1BE1A))),
                        Bound(
                            "ARMA",
                            new FormReference(
                            new PluginName("Skyrim.esm"),
                            new FormId(0x1BE18))),
                        Bound("TXST", ToReference(targetTexture)))
            };
            RaceMenuNpcFormBinding Bound(
                string signature,
                FormReference reference) => new(
                new RecordSignature(signature),
                reference,
                reference,
                new PluginName("Skyrim.esm"),
                skyrimProvider,
                skyrimHash,
                null);
            NpcCreationRequest request = CreateRequest(context, appearance)
                with
                {
                    PluginAuthorities = [skyrimAuthority]
                };
            NpcCreationProposal proposal = await context.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(proposal.IsApplicable,
                "The exact exposed-outfit binding proposal was refused: " +
                Format(proposal.Diagnostics));
            using (JsonDocument document = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(
                           request.Proposal.Value)))
            {
                JsonElement sourceOutfit = document.RootElement
                    .GetProperty("appearance")
                    .GetProperty("fullyAuthored")
                    .GetProperty("exposedOutfitSkinBinding")
                    .GetProperty("sourceOutfit");
                Assert(
                    sourceOutfit.GetProperty(
                            "providerPluginName")
                        .GetString() == "Skyrim.esm" &&
                    sourceOutfit.GetProperty(
                            "providerPlugin")
                        .GetString() == skyrimProvider.Value &&
                    sourceOutfit.GetProperty(
                            "providerPluginSha256")
                        .GetString() == skyrimHash.Value,
                    "The canonical proposal did not preserve the outfit record's exact winning provider path and hash.");
            }

            NpcCreationResult result = await context.Service.ApplyAsync(
                request, proposal, CancellationToken.None);
            Assert(result.Applied && result.Verification is
                   {
                       IsValid: true,
                       MajorRecordCount: 7,
                       NpcRecordCount: 1,
                       NextFormId.Value: 0x807
                   },
                "The exposed-outfit binding did not survive independent readback: " +
                Format(result.Diagnostics));

            var outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(request.Output.Value));
            using (var output = SkyrimMod.CreateFromBinaryOverlay(
                       new ModPath(
                           outputKey,
                           new FilePath(request.Output.Value)),
                       SkyrimRelease.SkyrimSE))
            {
                INpcGetter npc = output.Npcs.Single();
                IArmorAddonGetter addon =
                    output.ArmorAddons.Single();
                IArmorGetter armor = output.Armors.Single();
                IOutfitGetter outfit = output.Outfits.Single();
                Assert(npc.DefaultOutfit.FormKeyNullable ==
                           new FormKey(outputKey, 0x806) &&
                       addon.FormKey ==
                           new FormKey(outputKey, 0x804) &&
                       addon.SkinTexture?.Female?.FormKey ==
                           targetTexture &&
                       armor.FormKey ==
                           new FormKey(outputKey, 0x805) &&
                       armor.Armature.Count(item =>
                           item.FormKey ==
                           new FormKey(outputKey, 0x804)) == 1 &&
                       outfit.FormKey ==
                           new FormKey(outputKey, 0x806) &&
                       (outfit.Items ?? []).Count(item =>
                           item.FormKey ==
                           new FormKey(outputKey, 0x805)) == 1,
                    "Typed inspection did not find the private OTFT -> ARMO -> ARMA -> TXST graph.");
            }

            byte[] acceptedBytes =
                await File.ReadAllBytesAsync(request.Output.Value);
            int targetMasterIndex = proposal.Masters.IndexOf(
                new PluginName(targetTexture.ModKey.ToString()));
            Assert(targetMasterIndex >= 0,
                "The private body TXST owner is absent from the proposal masters.");
            uint rawTargetTexture =
                checked((uint)targetMasterIndex << 24) |
                targetTexture.ID;
            uint outputIndex =
                checked((uint)proposal.Masters.Length << 24);
            foreach ((string signature, uint link) in new[]
                     {
                         ("NAM1", rawTargetTexture),
                         ("MODL", outputIndex | 0x804U),
                         ("INAM", outputIndex | 0x805U)
                     })
            {
                await File.WriteAllBytesAsync(
                    request.Output.Value, acceptedBytes);
                TamperUniqueLinkSubrecord(
                    request.Output.Value,
                    signature,
                    link);
                var hostile =
                    await context.Service.VerifyAsync(
                        request,
                        proposal,
                        CancellationToken.None);
                Assert(!hostile.IsValid &&
                       hostile.Diagnostics.Any(item =>
                           item.Code is
                               "npc-create-raw-owned-outfit-skin-mismatch" or
                               "npc-create-raw-owned-outfit-carrier-drift"),
                    $"The independent verifier accepted hostile {signature} link corruption.");
            }
            await File.WriteAllBytesAsync(
                request.Output.Value, acceptedBytes);

            var invalidContext = CreateContext(
                "exposed-outfit-skin-invalid");
            try
            {
                var invalidProvider = new WorkspacePath(Path.Combine(
                    Path.GetDirectoryName(invalidContext.Template.Value)!,
                    "Skyrim.esm"));
                var invalidHash = ComputeHash(invalidProvider);
                RaceMenuNpcFormBinding Rebind(
                    RaceMenuNpcFormBinding source,
                    FormReference? reference = null) => source with
                    {
                        SourceReference =
                            reference ?? source.SourceReference,
                        Reference = reference ?? source.Reference,
                        ProviderPlugin = invalidProvider,
                        ProviderPluginSha256 = invalidHash
                    };
                var originalBinding =
                    appearance.ExposedOutfitSkinBinding!;
                var invalid = appearance with
                {
                    ExposedOutfitSkinBinding = originalBinding with
                    {
                        SourceOutfit = Rebind(
                            originalBinding.SourceOutfit),
                        SourceArmor = Rebind(
                            originalBinding.SourceArmor),
                        SourceArmorAddon = Rebind(
                            originalBinding.SourceArmorAddon,
                            new FormReference(
                                new PluginName("Skyrim.esm"),
                                new FormId(0x00FFFF))),
                        TargetFemaleSkinTextureSet = Rebind(
                            originalBinding.TargetFemaleSkinTextureSet)
                    }
                };
                NpcCreationRequest invalidRequest =
                    CreateRequest(invalidContext, invalid) with
                    {
                        PluginAuthorities =
                        [
                            new NpcCreationPluginAuthority(
                                new PluginName("Skyrim.esm"),
                                invalidProvider,
                                invalidHash)
                        ]
                    };
                NpcCreationProposal refused =
                    await invalidContext.Service.AnalyzeAsync(
                        invalidRequest,
                        CancellationToken.None);
                Assert(!refused.IsApplicable &&
                       refused.Diagnostics.Any(item =>
                           item.Code is
                               "npc-create-appearance-outfit-skin-binding-invalid" or
                               "npc-create-appearance-master-union-invalid"),
                    "A missing source ARMA was not refused before output creation.");
            }
            finally
            {
                invalidContext.Dispose();
            }
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task
        TestExposedOutfitWinningProviderAndMasterClosure()
    {
        var context = CreateContext(
            "exposed-outfit-winning-provider");
        try
        {
            string dataRoot =
                Path.GetDirectoryName(context.Template.Value)!;
            string skyrimPath = Path.Combine(dataRoot, "Skyrim.esm");
            var skyrimProvider = new WorkspacePath(skyrimPath);
            var skyrimHash = ComputeHash(skyrimProvider);
            var skyrimKey =
                ModKey.FromNameAndExtension("Skyrim.esm");
            var overrideKey =
                ModKey.FromNameAndExtension("OutfitWinner.esp");
            var overridePath = new WorkspacePath(Path.Combine(
                context.ScratchRoot, overrideKey.FileName.String));
            var overrideMod =
                new SkyrimMod(overrideKey, SkyrimRelease.SkyrimSE);
            overrideMod.ModHeader.MasterReferences.Add(
                new MasterReference { Master = skyrimKey });
            var retainedRaceKey = new FormKey(overrideKey, 0x800);
            overrideMod.Races.Add(new Race(
                retainedRaceKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "WinningProviderRetainedRace"
            });
            FormKey targetTexture;
            using (var skyrim = SkyrimMod.CreateFromBinaryOverlay(
                       new ModPath(
                           skyrimKey,
                           new FilePath(skyrimPath)),
                       SkyrimRelease.SkyrimSE))
            {
                IArmorAddonGetter sourceAddon =
                    skyrim.ArmorAddons.Single(item =>
                        item.FormKey ==
                        new FormKey(skyrimKey, 0x1BE18));
                var winningAddon = sourceAddon.DeepCopy();
                winningAddon.AdditionalRaces.Add(
                    new FormLink<IRaceGetter>(retainedRaceKey));
                overrideMod.ArmorAddons.Add(winningAddon);
                overrideMod.Armors.Add(
                    skyrim.Armors.Single(item =>
                            item.FormKey ==
                            new FormKey(skyrimKey, 0x1BE1A))
                        .DeepCopy());
                overrideMod.Outfits.Add(
                    skyrim.Outfits.Single(item =>
                            item.FormKey ==
                            new FormKey(skyrimKey, 0x1DC10))
                        .DeepCopy());
                targetTexture = skyrim.TextureSets
                    .First(item => !item.IsDeleted)
                    .FormKey;
            }
            overrideMod.WriteToBinary(
                new FilePath(overridePath.Value),
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent =
                        MastersListContentOption.NoCheck,
                    MastersListOrdering =
                        MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });
            var overrideHash = ComputeHash(overridePath);

            RaceMenuNpcFormBinding Bound(
                string signature,
                FormReference reference,
                PluginName provider,
                WorkspacePath path,
                Sha256Hash hash) => new(
                new RecordSignature(signature),
                reference,
                reference,
                provider,
                path,
                hash,
                null);
            var winner = new PluginName("OutfitWinner.esp");
            FullyAuthoredSkyrimNpcAppearanceSource appearance =
                CreateValidAppearance(context.Template) with
                {
                    ExposedOutfitSkinBinding =
                        new OutputOwnedSkyrimNpcExposedOutfitSkinBinding(
                            new FormId(0x804),
                            new FormId(0x805),
                            new FormId(0x806),
                            Bound(
                                "OTFT",
                                new FormReference(
                                    new PluginName("Skyrim.esm"),
                                    new FormId(0x1DC10)),
                                winner,
                                overridePath,
                                overrideHash),
                            Bound(
                                "ARMO",
                                new FormReference(
                                    new PluginName("Skyrim.esm"),
                                    new FormId(0x1BE1A)),
                                winner,
                                overridePath,
                                overrideHash),
                            Bound(
                                "ARMA",
                                new FormReference(
                                    new PluginName("Skyrim.esm"),
                                    new FormId(0x1BE18)),
                                winner,
                                overridePath,
                                overrideHash),
                            Bound(
                                "TXST",
                                ToReference(targetTexture),
                                new PluginName("Skyrim.esm"),
                                skyrimProvider,
                                skyrimHash))
                };
            NpcCreationRequest request =
                CreateRequest(context, appearance) with
                {
                    PluginAuthorities =
                    [
                        new NpcCreationPluginAuthority(
                            new PluginName("Skyrim.esm"),
                            skyrimProvider,
                            skyrimHash),
                        new NpcCreationPluginAuthority(
                            winner,
                            overridePath,
                            overrideHash)
                    ]
                };

            var staleBinding =
                appearance.ExposedOutfitSkinBinding!;
            NpcCreationRequest staleRequest = request with
            {
                Appearance = appearance with
                {
                    ExposedOutfitSkinBinding =
                        staleBinding with
                        {
                            SourceArmorAddon =
                                staleBinding.SourceArmorAddon with
                                {
                                    ProviderPluginSha256 =
                                        new Sha256Hash(
                                            new string('A', 64))
                                }
                        }
                }
            };
            NpcCreationProposal stale =
                await context.Service.AnalyzeAsync(
                    staleRequest,
                    CancellationToken.None);
            Assert(!stale.IsApplicable &&
                   stale.Diagnostics.Any(item =>
                       item.Code ==
                       "npc-create-appearance-master-union-invalid") &&
                   !File.Exists(request.Proposal.Value) &&
                   !File.Exists(request.Output.Value),
                "A stale winning-provider hash was accepted before proposal or output creation.");

            NpcCreationProposal proposal =
                await context.Service.AnalyzeAsync(
                    request, CancellationToken.None);
            Assert(proposal.IsApplicable &&
                   proposal.Masters.Contains(winner),
                "The winning override's retained FormKey owner was not added to the deterministic master union: " +
                Format(proposal.Diagnostics));
            NpcCreationResult result =
                await context.Service.ApplyAsync(
                    request, proposal, CancellationToken.None);
            Assert(result.Applied &&
                   result.Verification is { IsValid: true },
                "The exact winning OTFT/ARMO/ARMA provider was not cloned and independently verified: " +
                Format(result.Diagnostics));

            var outputKey = ModKey.FromNameAndExtension(
                Path.GetFileName(request.Output.Value));
            using (var output = SkyrimMod.CreateFromBinaryOverlay(
                       new ModPath(
                           outputKey,
                           new FilePath(request.Output.Value)),
                       SkyrimRelease.SkyrimSE))
            {
                IArmorAddonGetter ownedAddon =
                    output.ArmorAddons.Single();
                Assert(ownedAddon.AdditionalRaces.Any(item =>
                           item.FormKey == retainedRaceKey),
                    "A supposedly preserved winning-provider ARMA reference was not retained.");
            }

            int winnerIndex = proposal.Masters.IndexOf(winner);
            Assert(winnerIndex >= 0,
                "The winning provider is absent from the accepted proposal masters.");
            uint retainedRaceRaw =
                checked((uint)winnerIndex << 24) |
                retainedRaceKey.ID;
            TamperUniqueUInt32(
                request.Output.Value,
                retainedRaceRaw);
            var hostile =
                await context.Service.VerifyAsync(
                    request,
                    proposal,
                    CancellationToken.None);
            Assert(!hostile.IsValid &&
                   hostile.Diagnostics.Any(item =>
                       item.Code is
                           "npc-create-typed-owned-outfit-skin-mismatch" or
                           "npc-create-raw-owned-outfit-carrier-drift"),
                "The verifier accepted corruption of a supposedly preserved winning-provider ARMA field.");
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestMalformedAuthoredAppearance()
    {
        var context = CreateContext("invalid");
        try
        {
            var provider = new PluginName("AppearanceProvider.esp");
            var invalid = new FullyAuthoredSkyrimNpcAppearanceSource(
                [
                    new NpcHeadPartSelection(
                        new FormReference(provider, new FormId(0)), NpcHeadPartType.Hair),
                    new NpcHeadPartSelection(
                        new FormReference(provider, new FormId(0x1234)), NpcHeadPartType.Hair)
                ],
                new OutputOwnedSkyrimNpcHairColor(
                    new FormId(0x800), new SkyrimPackedRgb(0x01_00_00_00)),
                new FormReference(provider, new FormId(0)),
                float.NaN,
                new SkyrimFaceMorphPatch(
                    ImmutableArray.CreateRange(Enumerable.Repeat(2f, 17)),
                    float.PositiveInfinity,
                    [0, 1, 99]),
                new SkyrimFaceTintPatch(
                [
                    new SkyrimFaceTintLayer(1, 1, 2, 3, 4, 101, 0),
                    new SkyrimFaceTintLayer(1, 5, 6, 7, 8, 50, 0)
                ]),
                new SkyrimQnamRgb(-0.1f, float.NaN, 1.1f));
            var result = await context.Service.AnalyzeAsync(
                CreateRequest(context, invalid), CancellationToken.None);
            var codes = result.Diagnostics.Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
            var expected = new[]
            {
                "npc-create-appearance-headpart-type-duplicate",
                "npc-create-appearance-headpart-0-form-id",
                "npc-create-appearance-hair-color-form-id-collision",
                "npc-create-appearance-hair-color-rgb",
                "npc-create-appearance-face-texture-set-form-id",
                "npc-create-appearance-weight-invalid",
                "npc-create-appearance-nam9-count",
                "npc-create-appearance-nam9-range",
                "npc-create-appearance-nam9-trailing",
                "npc-create-appearance-nama-count",
                "npc-create-appearance-face-tint-index-duplicate",
                "npc-create-appearance-face-tint-coverage",
                "npc-create-appearance-qnam-range"
            };
            Assert(!result.IsApplicable && expected.All(codes.Contains) &&
                   !File.Exists(context.Proposal.Value) && !File.Exists(context.Output.Value),
                "Malformed authored appearance validation was incomplete: " +
                string.Join(", ", expected.Where(code => !codes.Contains(code))));

            var privateHead = CreateValidAppearance(context.Template);
            var misplacedHeadParts = privateHead.OrderedHeadParts
                .Select(item => item is OutputOwnedSkyrimNpcFaceHeadPart outputOwned
                    ? (SkyrimNpcHeadPartSource)new OutputOwnedSkyrimNpcFaceHeadPart(
                        new FormId(0x805), outputOwned.QualifiedExternalFaceHdpt)
                    : item)
                .ToImmutableArray();
            var invalidPrivateHead = privateHead with
            {
                OrderedHeadParts = misplacedHeadParts,
                FaceTextureSet = new OutputOwnedSkyrimNpcFaceTextureSet(
                    new FormId(0x804),
                    new SkyrimPrivateHeadTexturePaths(
                        new AssetPath("textures/actors/character/emi2/skin/femalehead.png"),
                        new AssetPath("textures/actors/character/emi2/skin/shared.dds"),
                        new AssetPath("textures/actors/character/emi2/skin/shared.dds"),
                        new AssetPath("textures/actors/character/male/blankdetailmap.dds"),
                        new AssetPath("textures/actors/character/emi2/skin/femalehead_s.dds")))
            };
            var privateResult = await context.Service.AnalyzeAsync(
                CreateRequest(context, invalidPrivateHead), CancellationToken.None);
            var privateCodes = privateResult.Diagnostics
                .Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
            var expectedPrivateCodes = new[]
            {
                "npc-create-appearance-private-txst-path-invalid",
                "npc-create-appearance-private-txst-path-duplicate",
                "npc-create-appearance-owned-txst-form-id-sequence",
                "npc-create-appearance-owned-hdpt-form-id-sequence"
            };
            Assert(!privateResult.IsApplicable && expectedPrivateCodes.All(privateCodes.Contains) &&
                   !File.Exists(context.Proposal.Value) && !File.Exists(context.Output.Value),
                "Malformed private TXST/HDPT validation was incomplete: " +
                string.Join(", ", expectedPrivateCodes.Where(code => !privateCodes.Contains(code))));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestAuthoredOverrideOwnershipAndPreservation()
    {
        var context = CreateContext("appearance-override");
        try
        {
            var labRoot = FindLabRoot();
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var service = new NpcAppearanceOverrideService(policy, labRoot);
            var appearance = CreateValidAppearance(context.Template);
            var request = new NpcAppearanceOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                context.Template,
                context.TemplateHash,
                new FormId(0x800),
                context.Proposal,
                context.Output,
                new FormReference(new PluginName("Skyrim.esm"), new FormId(0x0001_3746)),
                NpcSex.Female,
                appearance,
                CreateValidRuntimeAppearance());

            var proposal = await service.AnalyzeAsync(request, CancellationToken.None);
            Assert(proposal.IsApplicable && proposal.ProposalSha256 is not null &&
                   proposal.RequiredMasters.Contains(
                       new PluginName(Path.GetFileName(context.Template.Value))) &&
                   proposal.ExpectedMajorRecordSignatures.Length == 4,
                "The appearance override did not produce a complete source-mastered proposal: " +
                Format(proposal.Diagnostics));
            using (var document = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(context.Proposal.Value)))
            {
                var root = document.RootElement;
                var persistedAppearance = root.GetProperty("appearance");
                var persistedHair = persistedAppearance.GetProperty("hairColor");
                var persistedTexture = persistedAppearance.GetProperty("faceTextureSet");
                var persistedOwnedFace = persistedAppearance.GetProperty("orderedHeadParts")
                    .EnumerateArray()
                    .Single(item => item.GetProperty("kind").GetString() ==
                        "output-owned-face-hdpt");
                Assert(root.GetProperty("artifactKind").GetString() ==
                       "skyrim-npc-appearance-override-proposal" &&
                       persistedHair.GetProperty("allocatedLocalFormId").GetString() ==
                           "0x00000801" &&
                       persistedHair.GetProperty("packedRgb").GetUInt32() == 0x11_22_33 &&
                       persistedTexture.GetProperty("allocatedLocalFormId").GetString() ==
                           "0x00000802" &&
                       persistedTexture.GetProperty("paths").GetProperty("diffuse").GetString() ==
                           "actors/character/emi2/skin/femalehead.dds" &&
                       persistedOwnedFace.GetProperty("allocatedLocalFormId").GetString() ==
                           "0x00000803" &&
                       persistedAppearance.GetProperty("faceMorphs")
                           .GetProperty("nam9Sliders").GetArrayLength() == 18 &&
                       root.GetProperty("runtimeAppearance").GetProperty("overlays")
                           .GetProperty("alpha").GetArrayLength() == 1,
                    "The persisted appearance-override proposal flattened or omitted authored data.");
            }
            var reloadedRuntime = JsonSerializer.Deserialize<SkyrimNpcApplySseVmadPayload>(
                JsonSerializer.Serialize(proposal.RuntimeAppearance)) ??
                throw new InvalidDataException("The runtime proposal payload did not deserialize.");
            var reloadedProposal = proposal with { RuntimeAppearance = reloadedRuntime };
            var applied = await service.ApplyAsync(
                request,
                reloadedProposal,
                CancellationToken.None);
            Assert(applied.Applied && applied.Verification is
            {
                IsValid: true,
                SourceOwnedTargetCount: 1,
                SelfOwnedTargetCount: 0,
                AppearanceMatches: true,
                UnrelatedNpcSubrecordsPreserved: true,
                UnrelatedScriptsPreserved: true
            } && ComputeHash(context.Template) == context.TemplateHash,
                "The appearance override did not retain source ownership and unrelated state: " +
                Format(applied.Diagnostics));

            var sharedVerifier = new PluginVerifyService(policy, labRoot);
            var sharedVerification = await sharedVerifier.VerifyAsync(
                new PluginVerifyRequest(
                    GameEdition.SkyrimSpecialEdition,
                    context.Template,
                    context.Output,
                    context.Proposal),
                CancellationToken.None);
            Assert(sharedVerification.IsValid &&
                   sharedVerification.ObservedChanges.Any(change =>
                       change.Field == "AppearanceMatches" && change.After == "true") &&
                   sharedVerification.ObservedChanges.Any(change =>
                       change.Field == "SourceOwnedTargetCount" && change.After == "1") &&
                   sharedVerification.ObservedChanges.Any(change =>
                       change.Field == "SelfOwnedTargetCount" && change.After == "0"),
                "The shared plugin verifier could not read the persisted appearance proposal: " +
                Format(sharedVerification.Diagnostics));

            TamperPreservedNpcName(context.Template, context.Output);
            var hostile = await service.VerifyAsync(
                request,
                proposal,
                CancellationToken.None);
            Assert(!hostile.IsValid &&
                   hostile.Diagnostics.Any(item => item.Code ==
                       "npc-appearance-override-subrecord-drift"),
                "The appearance verifier accepted a changed preserved NPC name: " +
                Format(hostile.Diagnostics));
            var sharedHostile = await sharedVerifier.VerifyAsync(
                new PluginVerifyRequest(
                    GameEdition.SkyrimSpecialEdition,
                    context.Template,
                    context.Output,
                    context.Proposal),
                CancellationToken.None);
            Assert(!sharedHostile.IsValid && sharedHostile.Diagnostics.Any(item =>
                       item.Code == "npc-appearance-override-subrecord-drift"),
                "The shared plugin verifier accepted a changed preserved NPC name: " +
                Format(sharedHostile.Diagnostics));

            File.Delete(context.Output.Value);
            File.Delete(context.Proposal.Value);
            var malformedRuntime = request.RuntimeAppearance! with
            {
                Overlays = request.RuntimeAppearance!.Overlays with
                {
                    Alpha = [2f]
                }
            };
            var malformedAppearance = appearance with
            {
                Qnam = new SkyrimQnamRgb(float.NaN, 0.5f, 0.5f)
            };
            var refused = await service.AnalyzeAsync(
                request with
                {
                    Appearance = malformedAppearance,
                    RuntimeAppearance = malformedRuntime
                },
                CancellationToken.None);
            Assert(!refused.IsApplicable &&
                   refused.Diagnostics.Any(item => item.Code ==
                       "npc-create-appearance-qnam-range") &&
                   refused.Diagnostics.Any(item => item.Code ==
                       "npc-create-runtime-float-invalid") &&
                   !File.Exists(context.Proposal.Value) &&
                   !File.Exists(context.Output.Value),
                "The appearance override analysis accepted malformed authored or runtime data: " +
                Format(refused.Diagnostics));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static async Task TestExistingNpcOriginKeyedPackage()
    {
        var context = CreateContext("eop");
        try
        {
            var labRoot = FindLabRoot();
            var policy = new KOnlyWorkspacePolicy(
                labRoot,
                new WorkspacePath(@"F:\ExampleGame"));
            var appearance = CreateValidAppearance(context.Template);
            var dataRoot = Path.GetDirectoryName(context.Template.Value)!;
            var sourceOwner = Path.GetFileName(context.Template.Value);
            var sourceNif = new WorkspacePath(Path.Combine(
                dataRoot,
                "meshes",
                "Actors",
                "Character",
                "FaceGenData",
                "FaceGeom",
                sourceOwner,
                "00000800.NIF"));
            var sourceDds = new WorkspacePath(Path.Combine(
                dataRoot,
                "Textures",
                "Actors",
                "Character",
                "FaceGenData",
                "FaceTint",
                sourceOwner,
                "00000800.dds"));
            var decoder = new ChainedFaceTintTextureDecoder(
                new Bgra8FaceTintTextureDecoder(labRoot),
                new TexconvFaceTintTextureDecoder(
                    new WorkspacePath(Path.Combine(
                        labRoot.Value,
                        "tools",
                        "external",
                        "directxtex-texconv-2026.5.7",
                        "texconv.exe")),
                    labRoot,
                    new Sha256Hash(
                        "DCFDEC10244E02CF5037FBA089C55FB7E1326B1C8181742D77D15FA5CB5EEF06")));
            var decodedSource = await decoder.DecodeAsync(sourceDds, CancellationToken.None);
            Assert(decodedSource is
            {
                Decoded: true,
                SourceSha256: not null
            }, "The copied source FaceTint fixture did not decode.");
            var sourceDdsHash = decodedSource.SourceSha256 ??
                throw new InvalidDataException(
                    "The decoded source FaceTint omitted its hash.");
            var packageRoot = new WorkspacePath(Path.Combine(
                context.ScratchRoot,
                "origin-keyed-package"));
            var service = new ExistingNpcAppearanceBuildService(
                new NpcAppearanceOverrideService(policy, labRoot),
                new BodyGenService(policy, labRoot),
                new QualifiedFaceGeomCarrierService(policy, labRoot),
                decoder,
                new PackageVerifyService(new PackageManifestReader(policy, labRoot)),
                policy,
                labRoot);
            var buildRequest = new ExistingNpcAppearanceBuildRequest(
                GameEdition.SkyrimSpecialEdition,
                context.Template,
                context.TemplateHash,
                new FormId(0x800),
                packageRoot,
                new PluginName("AuthoredOverride.esp"),
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x0001_3746)),
                NpcSex.Female,
                appearance,
                CreateValidRuntimeAppearance(),
                new ExactNifBlankNpcFaceGeomSource(
                    sourceNif,
                    ComputeHash(sourceNif)),
                new ExactDdsBlankNpcFaceTintSource(
                    sourceDds,
                    sourceDdsHash,
                    decodedSource.Width,
                    decodedSource.Height),
                [new BodyGenMorph("CBBE", 0.75f)],
                []);
            var result = await service.ExecuteAsync(
                buildRequest,
                null,
                CancellationToken.None);

            Assert(result.Completed && result.Artifact is
            {
                SourceOwnerPlugin.Value: "EmiCarrierProbe.esp",
                SourceOwnerFormId.Value: 0x800,
                RuntimeAuthority: false
            } artifact &&
                   artifact.FaceGeom.Value.EndsWith(
                       Path.Combine("EmiCarrierProbe.esp", "00000800.nif"),
                       StringComparison.OrdinalIgnoreCase) &&
                   artifact.FaceTint.Value.EndsWith(
                       Path.Combine("EmiCarrierProbe.esp", "00000800.dds"),
                       StringComparison.OrdinalIgnoreCase) &&
                   result.AppearanceOverride?.Verification is
                   {
                       IsValid: true,
                       SourceOwnedTargetCount: 1,
                       SelfOwnedTargetCount: 0
                   } &&
                   result.BodyGen is
                   {
                       Written: true,
                       Plugin.Value: "EmiCarrierProbe.esp",
                       NpcFormId.Value: 0x800,
                       Files.Length: 2
                   } &&
                   result.PackageVerification is
                   {
                       Verified: true,
                       Artifact.NoUndeclaredFiles: true,
                       Artifact.RuntimeProof: false
                   } &&
                   ComputeHash(context.Template) == context.TemplateHash,
                "The existing-NPC package lost source ownership, origin-keyed FaceGen, or exact inventory evidence: " +
                Format(result.Diagnostics));

            var refusedRoot = new WorkspacePath(Path.Combine(
                context.ScratchRoot,
                "origin-keyed-package-refused"));
            var refused = await service.ExecuteAsync(
                buildRequest with
                {
                    OutputRoot = refusedRoot,
                    FaceGeomSource = buildRequest.FaceGeomSource with
                    {
                        ExpectedSha256 = new Sha256Hash(new string('0', 64))
                    }
                },
                null,
                CancellationToken.None);
            Assert(!refused.Completed &&
                   !Directory.Exists(refusedRoot.Value) &&
                   ComputeHash(context.Template) == context.TemplateHash,
                "A post-write FaceGeom refusal did not roll back the complete owned package root: " +
                Format(refused.Diagnostics));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static void TamperPreservedNpcName(
        WorkspacePath sourcePlugin,
        WorkspacePath outputPlugin)
    {
        var sourceKey = ModKey.FromNameAndExtension(Path.GetFileName(sourcePlugin.Value));
        using var source = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(sourceKey, new FilePath(sourcePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        var name = source.Npcs.Single(item =>
            item.FormKey == new FormKey(sourceKey, 0x800)).Name?.String ??
            throw new InvalidDataException("The source NPC has no preserved display name.");
        var marker = Encoding.UTF8.GetBytes(name + "\0");
        var bytes = File.ReadAllBytes(outputPlugin.Value);
        var matches = FindSequenceOffsets(bytes, marker);
        Assert(matches.Count == 1,
            "Could not isolate the preserved NPC name for hostile verification.");
        bytes[matches[0]] = bytes[matches[0]] == (byte)'X' ? (byte)'Y' : (byte)'X';
        File.WriteAllBytes(outputPlugin.Value, bytes);
    }

    private static async Task TestFollowerRoleWriteReadbackAndIsolation()
    {
        var followerContext = CreateContext("follower-role");
        try
        {
            var request = CreateRequest(
                followerContext, CreateValidAppearance(followerContext.Template));
            request = request with
            {
                Traits = request.Traits with { Role = NpcCreationRole.Follower }
            };
            var proposal = await followerContext.Service.AnalyzeAsync(
                request, CancellationToken.None);
            Assert(proposal.IsApplicable,
                "The follower role did not produce an applicable proposal: " +
                Format(proposal.Diagnostics));
            var applied = await followerContext.Service.ApplyAsync(
                request, proposal, CancellationToken.None);
            Assert(applied.Applied && applied.Verification is
            {
                IsValid: true,
                MajorRecordCount: 5,
                NpcRecordCount: 1,
                NextFormId.Value: 0x805
            },
                "The follower role did not survive typed and raw read-back: " +
                Format(applied.Diagnostics));
            AssertTypedFollowerRole(request.Output.Value, request.Appearance, expected: true);
            AssertRawFollowerRole(request.Output.Value, expected: true);

            TamperFollowerRelationshipRank(request.Output.Value);
            var hostile = await followerContext.Service.VerifyAsync(
                request, proposal, CancellationToken.None);
            Assert(!hostile.IsValid &&
                   hostile.Diagnostics.Any(item => item.Code ==
                       "npc-create-typed-role-relationship-mismatch") &&
                   hostile.Diagnostics.Any(item => item.Code ==
                       "npc-create-raw-role-relationship-mismatch"),
                "The typed and raw readers did not both reject a changed follower relationship rank: " +
                Format(hostile.Diagnostics));
        }
        finally
        {
            followerContext.Dispose();
        }

        foreach (var role in Enum.GetValues<NpcCreationRole>()
                     .Where(item => item != NpcCreationRole.Follower))
        {
            var context = CreateContext($"non-follower-role-{role}");
            try
            {
                var request = CreateRequest(
                    context, TemplateCarrierNpcAppearanceSource.Instance);
                request = request with
                {
                    Traits = request.Traits with { Role = role }
                };
                var proposal = await context.Service.AnalyzeAsync(
                    request, CancellationToken.None);
                Assert(proposal.IsApplicable,
                    $"The {role} role did not produce an applicable proposal: " +
                    Format(proposal.Diagnostics));
                var applied = await context.Service.ApplyAsync(
                    request, proposal, CancellationToken.None);
                Assert(applied.Applied && applied.Verification is
                {
                    IsValid: true,
                    MajorRecordCount: 1,
                    NpcRecordCount: 1,
                    NextFormId.Value: 0x801
                },
                    $"The {role} role gained a follower record surface: " +
                    Format(applied.Diagnostics));
                AssertTypedFollowerRole(request.Output.Value, request.Appearance, expected: false);
                AssertRawFollowerRole(request.Output.Value, expected: false);
            }
            finally
            {
                context.Dispose();
            }
        }
    }

    private static async Task TestRuntimeAppearanceWriteReadbackAndTamper()
    {
        var context = CreateContext("runtime-vmad");
        try
        {
            var runtime = CreateValidRuntimeAppearance();
            var invalidOverlay = runtime.Overlays with
            {
                Nodes = Enumerable.Repeat("Body [Body]", 129)
                    .Select((value, index) => index == 0 ? value + "\0" : value)
                    .ToImmutableArray(),
                Alpha = [float.NaN]
            };
            var invalidRuntime = runtime with
            {
                SchemaVersion = 0,
                Overlays = invalidOverlay
            };
            var invalidRequest = CreateRequest(context, CreateValidAppearance(context.Template)) with
            {
                RuntimeAppearance = invalidRuntime
            };
            var invalid = await context.Service.AnalyzeAsync(
                invalidRequest, CancellationToken.None);
            var invalidCodes = invalid.Diagnostics
                .Select(item => item.Code).ToHashSet(StringComparer.Ordinal);
            var expectedInvalidCodes = new[]
            {
                "npc-create-runtime-schema-version",
                "npc-create-runtime-overlay-array-count",
                "npc-create-runtime-overlay-array-length-mismatch",
                "npc-create-runtime-overlay-string-invalid",
                "npc-create-runtime-float-invalid"
            };
            Assert(!invalid.IsApplicable && expectedInvalidCodes.All(invalidCodes.Contains) &&
                   !File.Exists(context.Proposal.Value) && !File.Exists(context.Output.Value),
                "Malformed runtime VMAD validation was incomplete: " +
                string.Join(", ", expectedInvalidCodes.Where(code => !invalidCodes.Contains(code))));

            var request = invalidRequest with { RuntimeAppearance = runtime };
            var proposal = await context.Service.AnalyzeAsync(request, CancellationToken.None);
            Assert(proposal.IsApplicable && proposal.RuntimeAppearance is not null,
                "The valid runtime VMAD payload did not produce an applicable proposal: " +
                Format(proposal.Diagnostics));
            using (var document = JsonDocument.Parse(
                       await File.ReadAllBytesAsync(request.Proposal.Value)))
            {
                var serialized = document.RootElement.GetProperty("runtimeAppearance");
                Assert(serialized.GetProperty("schemaVersion").GetInt32() == runtime.SchemaVersion &&
                       serialized.GetProperty("overlays").GetProperty("nodes").GetArrayLength() == 1 &&
                       serialized.GetProperty("nodeTransforms").GetProperty("rotationM8")
                           .GetArrayLength() == 1,
                    "The persisted creation proposal omitted or flattened the typed runtime payload.");
            }

            var changed = request with
            {
                RuntimeAppearance = runtime with { SchemaVersion = runtime.SchemaVersion + 1 }
            };
            var refused = await context.Service.ApplyAsync(
                changed, proposal, CancellationToken.None);
            Assert(!refused.Applied &&
                   refused.Diagnostics.Any(item => item.Code ==
                       "npc-create-proposal-request-mismatch") &&
                   !File.Exists(request.Output.Value),
                "A changed runtime payload was not proposal-bound and refused before writing.");

            var applied = await context.Service.ApplyAsync(
                request, proposal, CancellationToken.None);
            Assert(applied.Applied && applied.Verification is { IsValid: true } &&
                   File.Exists(request.Output.Value),
                "The typed runtime payload did not survive the single creation transaction: " +
                Format(applied.Diagnostics));

            TamperUniqueAscii(request.Output.Value, "OvlNode");
            var hostile = await context.Service.VerifyAsync(
                request, proposal, CancellationToken.None);
            Assert(!hostile.IsValid &&
                   hostile.Diagnostics.Any(item => item.Code ==
                       "npc-create-typed-runtime-vmad-mismatch") &&
                   hostile.Diagnostics.Any(item => item.Code ==
                       "npc-create-raw-runtime-vmad-mismatch"),
                "The typed and raw readers did not both reject the altered VMAD property set: " +
                Format(hostile.Diagnostics));
        }
        finally
        {
            context.Dispose();
        }
    }

    private static SkyrimNpcApplySseVmadPayload CreateValidRuntimeAppearance() => new(
        true,
        0x1234_567,
        new SkyrimNpcApplySseOverlayArrays(
            ["Body [Body]"],
            ["textures/actors/character/overlays/body.dds"],
            ["textures/actors/character/overlays/body_n.dds"],
            [true],
            [unchecked((int)0xFF00_0000u)],
            [true],
            [25.5f],
            [true],
            [unchecked((int)0xFF11_2233u)],
            [true],
            [0.75f]),
        new SkyrimNpcApplySseSkinArrays(
            [unchecked((int)0x8000_0000u)],
            ["textures/actors/character/skin/body.dds"],
            ["textures/actors/character/skin/body_msn.dds"],
            [true],
            [unchecked((int)0xFF44_5566u)]),
        new SkyrimNpcApplySseNodeArrays(
            ["NPC Head [Head]"],
            [true],
            [0.95f],
            [true],
            [1.25f],
            [-2.5f],
            [0.125f],
            [true],
            [1f],
            [0f],
            [0f],
            [0f],
            [1f],
            [0f],
            [0f],
            [0f],
            [1f],
            [1]));

    private static FullyAuthoredSkyrimNpcAppearanceSource CreateValidAppearance(
        WorkspacePath templatePlugin)
    {
        var templateModKey = ModKey.FromNameAndExtension(Path.GetFileName(templatePlugin.Value));
        using var template = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(templateModKey, new FilePath(templatePlugin.Value)),
            SkyrimRelease.SkyrimSE);
        var npc = template.Npcs.Single(item => item.FormKey == new FormKey(templateModKey, 0x800));
        var dataRoot = Path.GetDirectoryName(templatePlugin.Value)!;
        var headParts = npc.HeadParts
            .Select(item => ToHeadPartSelection(dataRoot, item.FormKey))
            .ToImmutableArray();
        var orderedSources = headParts
            .Select(item => (SkyrimNpcHeadPartSource)new ExternalSkyrimNpcHeadPart(item))
            .ToArray();
        var faceIndex = Array.FindIndex(
            orderedSources, item => item.Type == NpcHeadPartType.Face);
        if (faceIndex < 0 || orderedSources[faceIndex] is not ExternalSkyrimNpcHeadPart face)
            throw new InvalidDataException("The qualified template has no external Face HDPT authority.");
        orderedSources[faceIndex] = new OutputOwnedSkyrimNpcFaceHeadPart(
            new FormId(0x803), face.Hdpt);

        return new FullyAuthoredSkyrimNpcAppearanceSource(
            orderedSources.ToImmutableArray(),
            new OutputOwnedSkyrimNpcHairColor(
                new FormId(0x801), new SkyrimPackedRgb(0x11_22_33)),
            new OutputOwnedSkyrimNpcFaceTextureSet(
                new FormId(0x802),
                new SkyrimPrivateHeadTexturePaths(
                    new AssetPath("actors/character/emi2/skin/femalehead.dds"),
                    new AssetPath("actors/character/emi2/skin/femalehead_msn.dds"),
                    new AssetPath("actors/character/emi2/skin/femalehead_sk.dds"),
                    new AssetPath("actors/character/male/blankdetailmap.dds"),
                    new AssetPath("actors/character/emi2/skin/femalehead_s.dds"))),
            42f,
            new SkyrimFaceMorphPatch(
                Enumerable.Range(0, 18).Select(index => (index - 9) / 20f).ToImmutableArray(),
                -0.625f,
                [1, 2, uint.MaxValue, 24]),
            new SkyrimFaceTintPatch(
            [
                new SkyrimFaceTintLayer(16, 70, 80, 90, 255, 37, -1),
                new SkyrimFaceTintLayer(24, 12, 34, 56, 200, 82, 3)
            ]),
            new SkyrimQnamRgb(32f / 255f, 96f / 255f, 160f / 255f));
    }

    private static FullyAuthoredSkyrimNpcAppearanceSource
        CreateHeadPartCompatibilityAppearance(WorkspacePath templatePlugin)
    {
        FullyAuthoredSkyrimNpcAppearanceSource appearance =
            CreateValidAppearance(templatePlugin);
        OutputOwnedSkyrimNpcFaceHeadPart face = appearance.OrderedHeadParts
            .OfType<OutputOwnedSkyrimNpcFaceHeadPart>().Single();
        PluginName provider = face.QualifiedExternalFaceHdpt.Plugin;
        string providerPath = Path.Combine(
            Path.GetDirectoryName(templatePlugin.Value)!, provider.Value);
        ModKey providerKey = ModKey.FromNameAndExtension(provider.Value);
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(providerKey, new FilePath(providerPath)), SkyrimRelease.SkyrimSE);
        IHeadPartGetter ordinary = overlay.HeadParts.First(item =>
            item.Type is HeadPart.TypeEnum.Hair or HeadPart.TypeEnum.Eyebrows);
        var external = new ExternalSkyrimNpcHeadPart(
            new FormReference(provider, new FormId(ordinary.FormKey.ID)),
            ParseHeadPartType(ordinary.Type));
        return appearance with
        {
            OrderedHeadParts = [face, external]
        };
    }

    private static void WriteRaceRestrictedFaceHeadPartProvider(
        string sourcePath,
        string destinationPath)
    {
        var modKey = ModKey.FromNameAndExtension(Path.GetFileName(sourcePath));
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, new FilePath(sourcePath)), SkyrimRelease.SkyrimSE);
        var mod = (SkyrimMod)overlay.DeepCopy();
        var raceListKey = new FormKey(modKey, 0x00EF10);
        if (!mod.FormLists.Any(item => item.FormKey == raceListKey))
        {
            mod.FormLists.Add(new FormList(raceListKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "NPCM_TestRaceRestrictedFaceHeadPart",
                Items =
                [
                    new FormLink<IRaceGetter>(new FormKey(
                        ModKey.FromNameAndExtension("Skyrim.esm"), 0x0001_3746))
                ]
            });
        }
        var face = mod.HeadParts.Single(item => item.FormKey.ID == 0x00000A06);
        face.Flags = HeadPart.Flag.Playable;
        face.ValidRaces = new FormLinkNullable<IFormListGetter>(raceListKey);
        mod.WriteToBinary(new FilePath(destinationPath), new BinaryWriteParameters
        {
            ModKey = ModKeyOption.NoCheck,
            MastersListContent = MastersListContentOption.NoCheck,
            MastersListOrdering = MastersListOrderingOption.NoCheck,
            NextFormID = NextFormIDOption.NoCheck
        });
    }

    private static ExternalSkyrimNpcHeadPart OrdinaryExternalHeadPart(
        FullyAuthoredSkyrimNpcAppearanceSource appearance) =>
        appearance.OrderedHeadParts
            .OfType<ExternalSkyrimNpcHeadPart>()
            .First(item => item.Type is NpcHeadPartType.Hair or NpcHeadPartType.Eyebrows);

    private static ImmutableArray<NpcCreationPluginAuthority>
        WriteHeadPartCompatibilityProviders(
        TestContext context,
        FullyAuthoredSkyrimNpcAppearanceSource appearance,
        HeadPart.Flag commonSexFlags,
        FormReference? sexOverrideHeadPart = null,
        HeadPart.Flag sexOverrideFlags = 0,
        FormReference? raceRestrictedHeadPart = null)
    {
        FormReference[] references = appearance.OrderedHeadParts.Select(item => item switch
        {
            ExternalSkyrimNpcHeadPart external => external.Hdpt,
            OutputOwnedSkyrimNpcFaceHeadPart outputOwned =>
                outputOwned.QualifiedExternalFaceHdpt,
            _ => throw new InvalidDataException("Unsupported authored HDPT source in fixture.")
        }).ToArray();
        var authorities = ImmutableArray.CreateBuilder<NpcCreationPluginAuthority>();
        const HeadPart.Flag sexMask = HeadPart.Flag.Male | HeadPart.Flag.Female;
        foreach (IGrouping<PluginName, FormReference> providerGroup in
                 references.GroupBy(item => item.Plugin))
        {
            PluginName provider = providerGroup.Key;
            string sourcePath = Path.Combine(
                Path.GetDirectoryName(context.Template.Value)!, provider.Value);
            var providerKey = ModKey.FromNameAndExtension(provider.Value);
            string authorityRoot = Path.Combine(
                context.ScratchRoot,
                "reviewed-" + Path.GetFileNameWithoutExtension(provider.Value));
            Directory.CreateDirectory(authorityRoot);
            var destination = new WorkspacePath(Path.Combine(
                authorityRoot, provider.Value));
            using var overlay = SkyrimMod.CreateFromBinaryOverlay(
                new ModPath(providerKey, new FilePath(sourcePath)), SkyrimRelease.SkyrimSE);
            var mod = (SkyrimMod)overlay.DeepCopy();
            foreach (FormReference reference in providerGroup)
            {
                HeadPart headPart = mod.HeadParts.Single(item =>
                    item.FormKey == new FormKey(providerKey, reference.FormId.Value));
                HeadPart.Flag selected = sexOverrideHeadPart is { } overrideReference &&
                                         overrideReference == reference
                    ? sexOverrideFlags
                    : commonSexFlags;
                headPart.Flags = (headPart.Flags & ~sexMask) | HeadPart.Flag.Playable | selected;
                headPart.ValidRaces = new FormLinkNullable<IFormListGetter>();
            }

            if (raceRestrictedHeadPart is { } restrictedReference &&
                restrictedReference.Plugin == provider)
            {
                uint raceListId = mod.FormLists.Select(item => item.FormKey.ID)
                    .DefaultIfEmpty(0x00EF0FU).Max() + 1;
                var raceListKey = new FormKey(providerKey, raceListId);
                var wrongRace = new FormKey(
                    ModKey.FromNameAndExtension("Skyrim.esm"), 0x0001_3747);
                mod.FormLists.Add(new FormList(raceListKey, SkyrimRelease.SkyrimSE)
                {
                    EditorID = "NPCM_TestWrongValidRaces",
                    Items = [new FormLink<IRaceGetter>(wrongRace)]
                });
                mod.HeadParts.Single(item =>
                        item.FormKey == new FormKey(providerKey, restrictedReference.FormId.Value))
                    .ValidRaces = new FormLinkNullable<IFormListGetter>(raceListKey);
            }

            mod.WriteToBinary(new FilePath(destination.Value), new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
            authorities.Add(new NpcCreationPluginAuthority(
                provider, destination, ComputeHash(destination)));
        }
        return authorities.ToImmutable();
    }

    private static NakedSkinFixture CreateNakedSkinFixture(
        TestContext context,
        bool includeFemaleTextureSet = true,
        bool? includeMaleTextureSet = null)
    {
        var provider = new PluginName("NakedSkinProvider.esp");
        var providerKey = ModKey.FromNameAndExtension(provider.Value);
        var providerPath = new WorkspacePath(Path.Combine(
            context.ScratchRoot, provider.Value));
        var raceKey = new FormKey(providerKey, 0x900);
        var skinKey = new FormKey(providerKey, 0x901);
        var bodyAddonKey = new FormKey(providerKey, 0x902);
        var handsAddonKey = new FormKey(providerKey, 0x903);
        var feetAddonKey = new FormKey(providerKey, 0x904);
        var bodyTextureKey = new FormKey(providerKey, 0x905);
        var handsTextureKey = new FormKey(providerKey, 0x906);
        var feetTextureKey = new FormKey(providerKey, 0x907);

        var mod = new SkyrimMod(providerKey, SkyrimRelease.SkyrimSE);
        mod.Races.Add(new Race(raceKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "NakedSkinProviderRace",
            Skin = new FormLinkNullable<IArmorGetter>(skinKey)
        });
        var skin = new Armor(skinKey, SkyrimRelease.SkyrimSE)
        {
            EditorID = "NakedSkinProviderSkin",
            Race = new FormLinkNullable<IRaceGetter>(raceKey),
            BodyTemplate = new BodyTemplate
            {
                FirstPersonFlags = (BipedObjectFlag)(0x04 | 0x08 | 0x80)
            }
        };
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(bodyAddonKey));
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(handsAddonKey));
        skin.Armature.Add(new FormLink<IArmorAddonGetter>(feetAddonKey));
        mod.Armors.Add(skin);
        mod.ArmorAddons.Add(BuildNakedSkinAddon(
            bodyAddonKey,
            raceKey,
            (includeMaleTextureSet ?? includeFemaleTextureSet) ? bodyTextureKey : null,
            includeFemaleTextureSet ? bodyTextureKey : null,
            0x04,
            "NakedSkinBody"));
        mod.ArmorAddons.Add(BuildNakedSkinAddon(
            handsAddonKey,
            raceKey,
            (includeMaleTextureSet ?? includeFemaleTextureSet) ? handsTextureKey : null,
            includeFemaleTextureSet ? handsTextureKey : null,
            0x08,
            "NakedSkinHands"));
        mod.ArmorAddons.Add(BuildNakedSkinAddon(
            feetAddonKey,
            raceKey,
            (includeMaleTextureSet ?? includeFemaleTextureSet) ? feetTextureKey : null,
            includeFemaleTextureSet ? feetTextureKey : null,
            0x80,
            "NakedSkinFeet"));
        mod.TextureSets.Add(BuildNakedSkinTextureSet(
            bodyTextureKey, "naked/body"));
        mod.TextureSets.Add(BuildNakedSkinTextureSet(
            handsTextureKey, "naked/hands"));
        mod.TextureSets.Add(BuildNakedSkinTextureSet(
            feetTextureKey, "naked/feet"));
        mod.WriteToBinary(new FilePath(providerPath.Value),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent = MastersListContentOption.NoCheck,
                MastersListOrdering = MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });
        var hash = ComputeHash(providerPath);
        var authority = new NpcCreationPluginAuthority(
            provider,
            providerPath,
            hash);

        RaceMenuNpcFormBinding Bound(
            string signature,
            FormKey key) => new(
            new RecordSignature(signature),
            ToReference(key),
            ToReference(key),
            provider,
            providerPath,
            hash,
            null);

        return new NakedSkinFixture(
            authority,
            Bound("ARMO", skinKey),
            Bound("ARMA", bodyAddonKey),
            Bound("ARMA", handsAddonKey),
            Bound("ARMA", feetAddonKey),
            Bound("TXST", bodyTextureKey),
            Bound("TXST", handsTextureKey),
            Bound("TXST", feetTextureKey));
    }

    private static ArmorAddon BuildNakedSkinAddon(
        FormKey addon,
        FormKey race,
        FormKey? maleTextureSet,
        FormKey? femaleTextureSet,
        uint slotMask,
        string editorId) => new(addon, SkyrimRelease.SkyrimSE)
    {
        EditorID = editorId,
        BodyTemplate = new BodyTemplate
        {
            FirstPersonFlags = (BipedObjectFlag)slotMask
        },
        Race = new FormLinkNullable<IRaceGetter>(race),
        SkinTexture = new GenderedItem<
            IFormLinkNullableGetter<ITextureSetGetter>>(
            maleTextureSet is { } maleKey
                ? new FormLinkNullable<ITextureSetGetter>(maleKey)
                : new FormLinkNullable<ITextureSetGetter>(),
            femaleTextureSet is { } femaleKey
                ? new FormLinkNullable<ITextureSetGetter>(femaleKey)
                : new FormLinkNullable<ITextureSetGetter>())
    };

    private static TextureSet BuildNakedSkinTextureSet(
        FormKey key,
        string stem) => new(key, SkyrimRelease.SkyrimSE)
    {
        EditorID = Path.GetFileName(stem) + "Skin",
        Diffuse = stem + "_d.dds",
        NormalOrGloss = stem + "_n.dds",
        GlowOrDetailMap = stem + "_sk.dds",
        BacklightMaskOrSpecular = stem + "_s.dds"
    };

    private static NpcHeadPartSelection ToHeadPartSelection(string dataRoot, FormKey key)
    {
        var providerPath = Path.Combine(dataRoot, key.ModKey.ToString());
        using var provider = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(key.ModKey, new FilePath(providerPath)), SkyrimRelease.SkyrimSE);
        var headPart = provider.HeadParts.Single(item => item.FormKey == key);
        return new NpcHeadPartSelection(ToReference(key), ParseHeadPartType(headPart.Type));
    }

    private static NpcHeadPartType ParseHeadPartType<TEnum>(TEnum? type)
        where TEnum : struct, Enum
    {
        if (type is null) throw new InvalidDataException("A qualified HDPT record has no type.");
        var text = type.Value.ToString();
        if (Enum.TryParse<NpcHeadPartType>(text, true, out var parsed)) return parsed;
        return text switch
        {
            "Scars" => NpcHeadPartType.Scar,
            "FacialHair" => NpcHeadPartType.FacialHair,
            _ when int.TryParse(text, out var number) && number is >= 0 and <= 9 =>
                (NpcHeadPartType)number,
            _ => throw new InvalidDataException($"Unsupported qualified HDPT type '{text}'.")
        };
    }

    private static FormReference ToReference(FormKey key) => new(
        new PluginName(key.ModKey.ToString()), new FormId(key.ID));

    private static void TamperUniqueAssetPath(string pluginPath, AssetPath path)
    {
        var marker = Encoding.Latin1.GetBytes(path.Value);
        var bytes = File.ReadAllBytes(pluginPath);
        var matches = new List<int>();
        for (var index = 0; index <= bytes.Length - marker.Length; index++)
        {
            if (bytes.AsSpan(index, marker.Length).SequenceEqual(marker)) matches.Add(index);
        }
        Assert(matches.Count == 1,
            "Could not isolate the authored private texture path for hostile verification.");
        bytes[matches[0] + marker.Length - 5] ^= 0x01;
        File.WriteAllBytes(pluginPath, bytes);
    }

    private static void TamperUniqueAscii(string pluginPath, string value)
    {
        var marker = Encoding.ASCII.GetBytes(value);
        var bytes = File.ReadAllBytes(pluginPath);
        var matches = new List<int>();
        for (var index = 0; index <= bytes.Length - marker.Length; index++)
        {
            if (bytes.AsSpan(index, marker.Length).SequenceEqual(marker)) matches.Add(index);
        }
        Assert(matches.Count == 1,
            "Could not isolate the runtime VMAD property name for hostile verification.");
        bytes[matches[0]] ^= 0x01;
        File.WriteAllBytes(pluginPath, bytes);
    }

    private static void TamperUniqueLinkSubrecord(
        string pluginPath,
        string signature,
        uint value)
    {
        var bytes = File.ReadAllBytes(pluginPath);
        byte[] marker = Encoding.ASCII.GetBytes(signature);
        var matches = new List<int>();
        foreach (int offset in FindSequenceOffsets(bytes, marker))
        {
            if (offset + 6 > bytes.Length) continue;
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(offset + 4, 2));
            if (size == 0 || size % 4 != 0 ||
                offset + 6 + size > bytes.Length)
                continue;
            for (int dataOffset = offset + 6;
                 dataOffset < offset + 6 + size;
                 dataOffset += 4)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(
                        bytes.AsSpan(dataOffset, 4)) == value)
                    matches.Add(dataOffset);
            }
        }
        Assert(matches.Count == 1,
            $"Could not isolate one raw {signature} link 0x{value:X8} for hostile verification.");
        bytes[matches[0]] ^= 0x01;
        File.WriteAllBytes(pluginPath, bytes);
    }

    private static void TamperUniqueUInt32(
        string pluginPath,
        uint value)
    {
        var marker = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(marker, value);
        var bytes = File.ReadAllBytes(pluginPath);
        List<int> matches = FindSequenceOffsets(bytes, marker);
        Assert(matches.Count == 1,
            "Could not isolate one retained FormID for hostile verification.");
        bytes[matches[0]] ^= 0x01;
        File.WriteAllBytes(pluginPath, bytes);
    }

    private static void AssertTypedFollowerRole(
        string pluginPath,
        NpcCreationAppearanceSource appearance,
        bool expected)
    {
        var modKey = ModKey.FromNameAndExtension(Path.GetFileName(pluginPath));
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, new FilePath(pluginPath)), SkyrimRelease.SkyrimSE);
        var npc = overlay.Npcs.Single(item => item.FormKey == new FormKey(modKey, 0x800));
        var skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var potentialFollower = new FormKey(
            skyrim, BethesdaNpcCreationAdapter.PotentialFollowerFactionLocalFormId);
        var currentFollower = new FormKey(
            skyrim, BethesdaNpcCreationAdapter.CurrentFollowerFactionLocalFormId);
        var followerFactions = npc.Factions
            .Where(item => item.Faction.FormKey == potentialFollower ||
                           item.Faction.FormKey == currentFollower)
            .Select(item => (item.Faction.FormKey, Rank: (int)item.Rank))
            .ToArray();

        if (!expected)
        {
            Assert(followerFactions.Length == 0 && overlay.Relationships.Count == 0,
                "A non-follower role gained vanilla follower factions or a relationship.");
            return;
        }

        Assert(followerFactions.SequenceEqual(new[]
               {
                   (potentialFollower, BethesdaNpcCreationAdapter.PotentialFollowerFactionRank),
                   (currentFollower, BethesdaNpcCreationAdapter.CurrentFollowerFactionRank)
               }) && npc.Factions.Count == 2,
            "Typed read-back did not expose exactly the two vanilla follower faction memberships.");
        var relationship = overlay.Relationships.Single();
        Assert(relationship.FormKey == new FormKey(
                   modKey,
                   BethesdaNpcCreationAdapter.FollowerRelationshipLocalFormIdFor(appearance)) &&
               relationship.FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
               relationship.Parent.FormKey == npc.FormKey &&
               relationship.Child.FormKey == new FormKey(
                   skyrim, BethesdaNpcCreationAdapter.PlayerLocalFormId) &&
               relationship.Rank == Relationship.RankType.Ally &&
               relationship.Unknown == 0 && relationship.Flags == 0 &&
               relationship.AssociationType.FormKey == FormKey.Null,
            "Typed read-back did not expose the exact output-owned NPC-to-Player Ally relationship.");
        Assert(npc.Factions.All(item => item.Faction.FormKey !=
                   new FormKey(skyrim, 0x0001_9809)),
            "Follower role silently added PotentialMarriageFaction, which is a separate capability.");
    }

    private static void AssertRawFollowerRole(string pluginPath, bool expected)
    {
        var rawIds = ReadRawRoleIds(pluginPath);
        var bytes = File.ReadAllBytes(pluginPath);
        var potentialFollower = BuildFactionSubrecord(
            rawIds.PotentialFollowerFaction, BethesdaNpcCreationAdapter.PotentialFollowerFactionRank);
        var currentFollower = BuildFactionSubrecord(
            rawIds.CurrentFollowerFaction, BethesdaNpcCreationAdapter.CurrentFollowerFactionRank);
        var expectedCount = expected ? 1 : 0;
        Assert(CountSequence(bytes, potentialFollower) == expectedCount &&
               CountSequence(bytes, currentFollower) == expectedCount,
            "Raw SNAM bytes do not contain the exact role-appropriate follower faction pair.");
        if (!expected) return;

        var relationshipData = BuildRelationshipDataSubrecord(
            rawIds.OutputNpc, rawIds.Player, (short)Relationship.RankType.Ally);
        Assert(CountSequence(bytes, relationshipData) == 1,
            "Raw RELA DATA bytes do not contain the exact output-NPC-to-Player Ally relationship.");
    }

    private static void TamperFollowerRelationshipRank(string pluginPath)
    {
        var ids = ReadRawRoleIds(pluginPath);
        var marker = BuildRelationshipDataSubrecord(
            ids.OutputNpc, ids.Player, (short)Relationship.RankType.Ally);
        var bytes = File.ReadAllBytes(pluginPath);
        var matches = FindSequenceOffsets(bytes, marker);
        Assert(matches.Count == 1,
            "Could not isolate the follower RELA DATA for hostile verification.");
        bytes[matches[0] + 6 + 8] = (byte)Relationship.RankType.Confidant;
        File.WriteAllBytes(pluginPath, bytes);
    }

    private static RawRoleIds ReadRawRoleIds(string pluginPath)
    {
        var modKey = ModKey.FromNameAndExtension(Path.GetFileName(pluginPath));
        using var overlay = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, new FilePath(pluginPath)), SkyrimRelease.SkyrimSE);
        var masters = overlay.ModHeader.MasterReferences
            .Select(item => item.Master)
            .ToArray();
        var skyrim = ModKey.FromNameAndExtension("Skyrim.esm");
        var skyrimIndex = Array.FindIndex(masters, item => item == skyrim);
        Assert(skyrimIndex >= 0, "The role test output has no Skyrim.esm master.");
        return new RawRoleIds(
            checked((uint)(skyrimIndex << 24)) |
                BethesdaNpcCreationAdapter.PotentialFollowerFactionLocalFormId,
            checked((uint)(skyrimIndex << 24)) |
                BethesdaNpcCreationAdapter.CurrentFollowerFactionLocalFormId,
            checked((uint)(masters.Length << 24)) |
                BethesdaNpcCreationAdapter.AllocatedLocalFormId,
            checked((uint)(skyrimIndex << 24)) |
                BethesdaNpcCreationAdapter.PlayerLocalFormId);
    }

    private static byte[] BuildFactionSubrecord(uint faction, int rank)
    {
        var data = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(data, faction);
        data[4] = unchecked((byte)(sbyte)rank);
        return BuildSubrecord("SNAM", data);
    }

    private static byte[] BuildRelationshipDataSubrecord(uint parent, uint child, short rank)
    {
        var data = new byte[16];
        BinaryPrimitives.WriteUInt32LittleEndian(data, parent);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(4), child);
        BinaryPrimitives.WriteInt16LittleEndian(data.AsSpan(8), rank);
        return BuildSubrecord("DATA", data);
    }

    private static byte[] BuildSubrecord(string signature, byte[] data)
    {
        var bytes = new byte[6 + data.Length];
        Encoding.ASCII.GetBytes(signature).CopyTo(bytes, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(4), checked((ushort)data.Length));
        data.CopyTo(bytes, 6);
        return bytes;
    }

    private static int CountSequence(byte[] bytes, byte[] marker) =>
        FindSequenceOffsets(bytes, marker).Count;

    private static List<int> FindSequenceOffsets(byte[] bytes, byte[] marker)
    {
        var matches = new List<int>();
        for (var index = 0; index <= bytes.Length - marker.Length; index++)
        {
            if (bytes.AsSpan(index, marker.Length).SequenceEqual(marker)) matches.Add(index);
        }
        return matches;
    }

    private static NpcCreationRequest CreateRequest(
        TestContext context,
        NpcCreationAppearanceSource appearance)
    {
        var skyrim = new PluginName("Skyrim.esm");
        return new NpcCreationRequest(
            GameEdition.SkyrimSpecialEdition,
            context.Template,
            context.TemplateHash,
            new FormId(0x800),
            context.Proposal,
            context.Output,
            new NpcCreationIdentity(
                new EditorId("NPCM_AuthoredContract"),
                new NpcName("Authored contract NPC")),
            new SkyrimNpcCreationTraits(
                NpcSex.Female,
                NpcCreationRole.StaticValidation,
                true,
                false,
                false,
                false,
                true),
            new SkyrimNpcCreationReferences(
                new FormReference(skyrim, new FormId(0x0001_3746)),
                new FormReference(skyrim, new FormId(0x0001_3ADC)),
                new FormReference(skyrim, new FormId(0x0001_3181)),
                new FormReference(skyrim, new FormId(0x0003_BE1D)),
                new FormReference(skyrim, new FormId(0x0001_DC10))),
            appearance,
            new SkyrimNpcCreationStats(
                new NpcLevelValue(NpcLevelMode.Fixed, 1m),
                0,
                0,
                0,
                1,
                1,
                100,
                35,
                0,
                50,
                50,
                50,
                1f,
                42f,
                255));
    }

    private static TestContext CreateContext(string scenario)
    {
        var labRoot = FindLabRoot();
        var projectRoot = Path.Combine(
            labRoot.Value, "projects", "NpcManagerReimplementation");
        var testRoot = Path.Combine(
            projectRoot,
            "tests",
            "NpcManager.NpcCreationAppearance.Tests",
            $".scratch-{scenario}-{Environment.ProcessId}");
        Directory.CreateDirectory(testRoot);
        var template = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects",
            "Emi2FreshBuild",
            "03-builds",
            "feasibility-probes",
            "ck-carrier-root",
            "Data",
            "EmiCarrierProbe.esp"));
        var policy = new KOnlyWorkspacePolicy(
            labRoot, new WorkspacePath(@"F:\ExampleGame"));
        return new TestContext(
            NpcCreationComposition.Create(policy, labRoot),
            template,
            ComputeHash(template),
            new WorkspacePath(Path.Combine(testRoot, "proposal.json")),
            new WorkspacePath(Path.Combine(testRoot, "AuthoredContract.esp")),
            testRoot);
    }

    private static WorkspacePath FindLabRoot() =>
        new(TestAuthorityWorkspace.ResolveLabRoot(
            AppContext.BaseDirectory));

    private static Sha256Hash ComputeHash(WorkspacePath path)
    {
        using var stream = File.OpenRead(path.Value);
        return new Sha256Hash(Convert.ToHexString(SHA256.HashData(stream)));
    }

    private static string Format(IEnumerable<Diagnostic> diagnostics) =>
        string.Join(" | ", diagnostics.Select(item => $"{item.Code}: {item.Message}"));

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    private sealed record TestContext(
        INpcCreationService Service,
        WorkspacePath Template,
        Sha256Hash TemplateHash,
        WorkspacePath Proposal,
        WorkspacePath Output,
        string ScratchRoot) : IDisposable
    {
        public void Dispose()
        {
            if (Directory.Exists(ScratchRoot)) Directory.Delete(ScratchRoot, recursive: true);
        }
    }

    private sealed record RawRoleIds(
        uint PotentialFollowerFaction,
        uint CurrentFollowerFaction,
        uint OutputNpc,
        uint Player);

    private sealed record NakedSkinFixture(
        NpcCreationPluginAuthority Authority,
        RaceMenuNpcFormBinding SourceSkinArmor,
        RaceMenuNpcFormBinding SourceBodyAddon,
        RaceMenuNpcFormBinding SourceHandsAddon,
        RaceMenuNpcFormBinding SourceFeetAddon,
        RaceMenuNpcFormBinding BodyTextureSet,
        RaceMenuNpcFormBinding HandsTextureSet,
        RaceMenuNpcFormBinding FeetTextureSet);
}
