using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Drawing;
using System.Text;
using Mutagen.Bethesda;
using Mutagen.Bethesda.Plugins;
using Mutagen.Bethesda.Plugins.Binary.Parameters;
using Mutagen.Bethesda.Plugins.Records;
using Mutagen.Bethesda.Skyrim;
using NpcManager.Application;
using NpcManager.Domain;

namespace NpcManager.Formats.Bethesda;

public static partial class BethesdaNpcCreationVerifier
{
    private static void VerifyTypedAppearance(
        ISkyrimModGetter overlay,
        INpcGetter npc,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Appearance is TemplateCarrierNpcAppearanceSource) return;
        if (request.Appearance is not FullyAuthoredSkyrimNpcAppearanceSource authored)
        {
            diagnostics.Add(Error("npc-create-typed-appearance-source-invalid",
                "Typed read-back cannot verify the unsupported appearance source."));
            return;
        }

        VerifyTypedAuthoredAppearance(
            overlay,
            npc,
            authored,
            request.Identity.EditorId,
            request.Traits.Sex,
            new WorkspacePath(Path.GetDirectoryName(request.TemplatePlugin.Value)!),
            proposal.OutputPlugin,
            diagnostics,
            request.PluginAuthorities);
    }

    internal static void VerifyTypedAuthoredAppearance(
        ISkyrimModGetter overlay,
        INpcGetter npc,
        FullyAuthoredSkyrimNpcAppearanceSource authored,
        EditorId npcEditorId,
        NpcSex npcSex,
        WorkspacePath dataRoot,
        PluginName outputPlugin,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities = default)
    {
        var outputModKey = ModKey.FromNameAndExtension(outputPlugin.Value);
        Check(npc.HeadParts.Select(item => item.FormKey)
                .SequenceEqual(authored.OrderedHeadParts.Select(item => item switch
                {
                    ExternalSkyrimNpcHeadPart external => ToFormKey(external.Hdpt),
                    OutputOwnedSkyrimNpcFaceHeadPart outputOwned =>
                        new FormKey(outputModKey, outputOwned.AllocatedLocalFormId.Value),
                    _ => FormKey.Null
                })),
            "npc-create-typed-headparts-mismatch",
            "Typed read-back did not preserve the exact authored PNAM order.", diagnostics);
        FormReference targetRace = Reference(npc.Race.FormKey);
        foreach (var externalHeadPart in authored.OrderedHeadParts
                     .OfType<ExternalSkyrimNpcHeadPart>())
        {
            VerifyTypedExternalHeadPart(
                externalHeadPart,
                npcSex,
                targetRace,
                dataRoot,
                diagnostics,
                pluginAuthorities);
        }
        var expectedFaceTexture = authored.FaceTextureSet switch
        {
            ExternalSkyrimNpcFaceTextureSet external => ToFormKey(external.Txst),
            OutputOwnedSkyrimNpcFaceTextureSet outputOwned =>
                new FormKey(outputModKey, outputOwned.AllocatedLocalFormId.Value),
            _ => FormKey.Null
        };
        Check(npc.HeadTexture.FormKeyNullable == expectedFaceTexture,
            "npc-create-typed-face-texture-mismatch",
            "Typed read-back FTST does not match the authored face texture set.", diagnostics);
        Check(SameSingle(npc.Weight, authored.Weight),
            "npc-create-typed-appearance-weight-mismatch",
            "Typed read-back weight does not match the authored appearance.", diagnostics);

        var expectedHairColor = authored.HairColor switch
        {
            ExternalSkyrimNpcHairColor external => ToFormKey(external.Clfm),
            OutputOwnedSkyrimNpcHairColor outputOwned => new FormKey(
                outputModKey,
                outputOwned.AllocatedLocalFormId.Value),
            _ => FormKey.Null
        };
        Check(npc.HairColor.FormKeyNullable == expectedHairColor,
            "npc-create-typed-hair-color-mismatch",
            "Typed read-back HCLF does not match the authored hair color.", diagnostics);

        var actualMorphs = ToNam9(npc.FaceMorph);
        var expectedMorphs = authored.FaceMorphs.Nam9Sliders.Add(authored.FaceMorphs.Nam9Trailing);
        Check(actualMorphs.Length == expectedMorphs.Length &&
              actualMorphs.Zip(expectedMorphs).All(pair => SameSingle(pair.First, pair.Second)),
            "npc-create-typed-nam9-mismatch",
            "Typed read-back NAM9 does not match all 18 sliders and the explicit trailing value.", diagnostics);
        Check(ToNama(npc.FaceParts).SequenceEqual(authored.FaceMorphs.NamaValues),
            "npc-create-typed-nama-mismatch",
            "Typed read-back NAMA does not match the four authored family values.", diagnostics);

        Check(TypedTintsMatch(npc.TintLayers, authored.FaceTints.Layers),
            "npc-create-typed-face-tints-mismatch",
            "Typed read-back tint layers do not match the authored order and values.", diagnostics);
        Check(TypedQnamMatches(npc.TextureLighting, authored.Qnam),
            "npc-create-typed-qnam-mismatch",
            "Typed read-back QNAM does not match the authored normalized color.", diagnostics);

        if (authored.HairColor is OutputOwnedSkyrimNpcHairColor owned)
        {
            var key = new FormKey(outputModKey, owned.AllocatedLocalFormId.Value);
            var color = overlay.Colors.FirstOrDefault(item => item.FormKey == key);
            Check(color is not null,
                "npc-create-typed-owned-clfm-missing",
                "Typed read-back did not find the output-owned CLFM record.", diagnostics);
            if (color is not null)
            {
                Check(color.FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
                      string.Equals(color.EditorID,
                          BethesdaNpcCreationAdapter.BuildHairColorEditorId(npcEditorId.Value),
                          StringComparison.Ordinal) &&
                      color.Playable && ColorMatchesPackedRgb(color.Color, owned.PackedRgb.Value),
                    "npc-create-typed-owned-clfm-mismatch",
                    "Typed read-back of the output-owned CLFM record does not match its authored fields.",
                diagnostics);
            }
        }

        if (authored.FaceTextureSet is OutputOwnedSkyrimNpcFaceTextureSet ownedTexture)
        {
            VerifyTypedOwnedFaceTextureSet(
                overlay, npcEditorId, outputModKey, ownedTexture, diagnostics);
        }
        foreach (var ownedHeadPart in authored.OrderedHeadParts
                     .OfType<OutputOwnedSkyrimNpcFaceHeadPart>())
        {
            VerifyTypedOwnedFaceHeadPart(
                overlay, npcEditorId, npcSex, targetRace, dataRoot, outputModKey, expectedFaceTexture,
                ownedHeadPart, diagnostics, pluginAuthorities);
        }
        if (authored.NakedSkinBinding is { } skinBinding)
        {
            VerifyTypedNakedSkinBinding(
                overlay,
                npc,
                npcEditorId,
                outputModKey,
                skinBinding,
                diagnostics,
                pluginAuthorities);
        }
        if (authored.ExposedOutfitSkinBinding is { } outfitBinding)
        {
            VerifyTypedExposedOutfitSkinBinding(
                overlay,
                npc,
                npcEditorId,
                dataRoot,
                outputModKey,
                outfitBinding,
                diagnostics,
                pluginAuthorities);
        }
    }

    private static bool HasExpectedRawSurface(
        RawPluginSnapshot raw,
        NpcCreationAppearanceSource appearance,
        NpcCreationRole role)
    {
        var expectedSignatures = new List<string> { "NPC_" };
        if (appearance is FullyAuthoredSkyrimNpcAppearanceSource authored)
        {
            if (authored.HairColor is OutputOwnedSkyrimNpcHairColor) expectedSignatures.Add("CLFM");
            if (authored.FaceTextureSet is OutputOwnedSkyrimNpcFaceTextureSet) expectedSignatures.Add("TXST");
            expectedSignatures.AddRange(authored.OrderedHeadParts
                .OfType<OutputOwnedSkyrimNpcFaceHeadPart>().Select(_ => "HDPT"));
            if (authored.NakedSkinBinding is not null)
            {
                expectedSignatures.Add("ARMA");
                expectedSignatures.Add("ARMA");
                expectedSignatures.Add("ARMA");
                expectedSignatures.Add("ARMO");
            }
            if (authored.ExposedOutfitSkinBinding is not null)
            {
                expectedSignatures.Add("ARMA");
                expectedSignatures.Add("ARMO");
                expectedSignatures.Add("OTFT");
            }
        }
        if (role == NpcCreationRole.Follower) expectedSignatures.Add("RELA");
        return raw.Records.Select(item => item.Signature)
                   .OrderBy(item => item, StringComparer.Ordinal)
                   .SequenceEqual(expectedSignatures.OrderBy(item => item, StringComparer.Ordinal)) &&
               raw.TopGroups.All(item => item.GroupType == 0) &&
               raw.TopGroups.Select(item => item.Label)
                   .OrderBy(item => item, StringComparer.Ordinal)
                   .SequenceEqual(expectedSignatures
                       .Distinct(StringComparer.Ordinal)
                       .OrderBy(item => item, StringComparer.Ordinal));
    }

    private static void VerifyRawAppearance(
        RawPluginSnapshot raw,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        if (request.Appearance is TemplateCarrierNpcAppearanceSource) return;
        if (request.Appearance is not FullyAuthoredSkyrimNpcAppearanceSource authored)
        {
            diagnostics.Add(Error("npc-create-raw-appearance-source-invalid",
                "Raw read-back cannot verify the unsupported appearance source."));
            return;
        }

        try
        {
            var npcRecord = raw.Records.Single(item => item.Signature == "NPC_");
            var actual = ReadRawAppearance(npcRecord.Body.Span);
            var expectedHeadParts = authored.OrderedHeadParts
                .Select(item => item switch
                {
                    ExternalSkyrimNpcHeadPart external => ToRawFormId(external.Hdpt, proposal),
                    OutputOwnedSkyrimNpcFaceHeadPart outputOwned =>
                        ToRawOutputFormId(outputOwned.AllocatedLocalFormId, proposal),
                    _ => 0u
                })
                .ToImmutableArray();
            Check(actual.HeadParts.SequenceEqual(expectedHeadParts),
                "npc-create-raw-headparts-mismatch",
                "Raw PNAM order does not match the authored headparts.", diagnostics);

            var expectedHairColor = authored.HairColor switch
            {
                ExternalSkyrimNpcHairColor external => ToRawFormId(external.Clfm, proposal),
                OutputOwnedSkyrimNpcHairColor outputOwned => ToRawOutputFormId(
                    outputOwned.AllocatedLocalFormId, proposal),
                _ => 0u
            };
            Check(actual.HairColor == expectedHairColor,
                "npc-create-raw-hair-color-mismatch",
                "Raw HCLF does not match the authored hair color.", diagnostics);
            var expectedFaceTexture = authored.FaceTextureSet switch
            {
                ExternalSkyrimNpcFaceTextureSet external => ToRawFormId(external.Txst, proposal),
                OutputOwnedSkyrimNpcFaceTextureSet outputOwned =>
                    ToRawOutputFormId(outputOwned.AllocatedLocalFormId, proposal),
                _ => 0u
            };
            Check(actual.FaceTextureSet == expectedFaceTexture,
                "npc-create-raw-face-texture-mismatch",
                "Raw FTST does not match the authored face texture set.", diagnostics);
            if (authored.NakedSkinBinding is { } skinBinding)
            {
                uint expectedSkinArmor = ToRawOutputFormId(
                    skinBinding.AllocatedArmorLocalFormId, proposal);
                Check(actual.SkinArmor == expectedSkinArmor,
                    "npc-create-raw-skin-armor-mismatch",
                    "Raw WNAM does not match the authored private naked skin route.",
                    diagnostics);
            }
            uint expectedOutfit = authored.ExposedOutfitSkinBinding is
                { } outfitBinding
                ? ToRawOutputFormId(
                    outfitBinding.AllocatedOutfitLocalFormId, proposal)
                : request.References.DefaultOutfit is { } outfit
                    ? ToRawFormId(outfit, proposal)
                    : 0u;
            Check(actual.DefaultOutfit == expectedOutfit,
                "npc-create-raw-default-outfit-mismatch",
                "Raw DOFT does not match the authored default outfit route.",
                diagnostics);
            Check(SameSingle(actual.Weight, authored.Weight),
                "npc-create-raw-appearance-weight-mismatch",
                "Raw NAM7 does not match the authored appearance weight.", diagnostics);

            var expectedNam9 = authored.FaceMorphs.Nam9Sliders.Add(authored.FaceMorphs.Nam9Trailing);
            Check(actual.Nam9.Length == expectedNam9.Length &&
                  actual.Nam9.Zip(expectedNam9).All(pair => SameSingle(pair.First, pair.Second)),
                "npc-create-raw-nam9-mismatch",
                "Raw NAM9 does not match all 18 sliders and the explicit trailing value.", diagnostics);
            Check(actual.Nama.SequenceEqual(authored.FaceMorphs.NamaValues),
                "npc-create-raw-nama-mismatch",
                "Raw NAMA does not match the four authored family values.", diagnostics);
            Check(actual.Tints.SequenceEqual(authored.FaceTints.Layers),
                "npc-create-raw-face-tints-mismatch",
                "Raw TINI/TINC/TINV/TIAS layers do not match the authored order and values.", diagnostics);
            Check(RawQnamMatches(actual.Qnam, authored.Qnam),
                "npc-create-raw-qnam-mismatch",
                "Raw QNAM float bits do not match the authored normalized color.", diagnostics);

            if (authored.HairColor is OutputOwnedSkyrimNpcHairColor owned)
            {
                VerifyRawOwnedHairColor(raw, request, proposal, owned, diagnostics);
            }
            if (authored.FaceTextureSet is OutputOwnedSkyrimNpcFaceTextureSet ownedTexture)
            {
                VerifyRawOwnedFaceTextureSet(raw, request, proposal, ownedTexture, diagnostics);
            }
            foreach (var ownedHeadPart in authored.OrderedHeadParts
                         .OfType<OutputOwnedSkyrimNpcFaceHeadPart>())
            {
                VerifyRawOwnedFaceHeadPart(
                    raw, request, proposal, expectedFaceTexture, ownedHeadPart, diagnostics);
            }
            if (authored.NakedSkinBinding is { } rawSkinBinding)
            {
                VerifyRawNakedSkinBinding(
                    raw,
                    request,
                    proposal,
                    rawSkinBinding,
                    diagnostics);
            }
            if (authored.ExposedOutfitSkinBinding is { } rawOutfitBinding)
            {
                VerifyRawExposedOutfitSkinBinding(
                    raw,
                    request,
                    proposal,
                    rawOutfitBinding,
                    diagnostics);
            }
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or OverflowException)
        {
            diagnostics.Add(Error("npc-create-raw-appearance-readback-failed", exception.Message));
        }
    }

    private static RawAppearance ReadRawAppearance(ReadOnlySpan<byte> payload)
    {
        var headParts = ImmutableArray.CreateBuilder<uint>();
        uint? hairColor = null;
        uint? faceTextureSet = null;
        uint? skinArmor = null;
        uint? defaultOutfit = null;
        float? weight = null;
        ImmutableArray<float>? nam9 = null;
        ImmutableArray<uint>? nama = null;
        SkyrimQnamRgb? qnam = null;
        var tints = ImmutableArray.CreateBuilder<SkyrimFaceTintLayer>();
        PendingRawTint? pendingTint = null;

        ReadSubrecords(payload, (signature, data) =>
        {
            switch (signature)
            {
                case "PNAM":
                    RequireLength(signature, data, 4);
                    headParts.Add(BinaryPrimitives.ReadUInt32LittleEndian(data));
                    break;
                case "HCLF":
                    hairColor = ReadUniqueUInt32(signature, data, hairColor);
                    break;
                case "FTST":
                    faceTextureSet = ReadUniqueUInt32(signature, data, faceTextureSet);
                    break;
                case "WNAM":
                    skinArmor = ReadUniqueUInt32(signature, data, skinArmor);
                    break;
                case "DOFT":
                    defaultOutfit = ReadUniqueUInt32(
                        signature, data, defaultOutfit);
                    break;
                case "NAM7":
                    RequireLength(signature, data, 4);
                    if (weight is not null) throw Duplicate(signature);
                    weight = ReadSingle(data);
                    break;
                case "NAM9":
                    RequireLength(signature, data, 19 * sizeof(float));
                    if (nam9 is not null) throw Duplicate(signature);
                    var nam9Builder = ImmutableArray.CreateBuilder<float>(19);
                    for (var index = 0; index < 19; index++)
                        nam9Builder.Add(ReadSingle(data[(index * sizeof(float))..]));
                    nam9 = nam9Builder.MoveToImmutable();
                    break;
                case "NAMA":
                    RequireLength(signature, data, 4 * sizeof(uint));
                    if (nama is not null) throw Duplicate(signature);
                    var namaBuilder = ImmutableArray.CreateBuilder<uint>(4);
                    for (var index = 0; index < 4; index++)
                        namaBuilder.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                            data[(index * sizeof(uint))..]));
                    nama = namaBuilder.MoveToImmutable();
                    break;
                case "QNAM":
                    RequireLength(signature, data, 3 * sizeof(float));
                    if (qnam is not null) throw Duplicate(signature);
                    qnam = new SkyrimQnamRgb(
                        ReadSingle(data),
                        ReadSingle(data[sizeof(float)..]),
                        ReadSingle(data[(2 * sizeof(float))..]));
                    break;
                case "TINI":
                    RequireLength(signature, data, 2);
                    if (pendingTint is not null)
                        throw new InvalidDataException("Raw tint order contains an unclosed TINI layer.");
                    pendingTint = new PendingRawTint(BinaryPrimitives.ReadUInt16LittleEndian(data));
                    break;
                case "TINC":
                    RequireTintState(signature, pendingTint);
                    RequireLength(signature, data, 4);
                    if (pendingTint!.Color is not null) throw Duplicate(signature);
                    pendingTint.Color = (data[0], data[1], data[2], data[3]);
                    break;
                case "TINV":
                    RequireTintState(signature, pendingTint);
                    RequireLength(signature, data, 4);
                    if (pendingTint!.Coverage is not null) throw Duplicate(signature);
                    pendingTint.Coverage = BinaryPrimitives.ReadUInt32LittleEndian(data);
                    break;
                case "TIAS":
                    RequireTintState(signature, pendingTint);
                    RequireLength(signature, data, 2);
                    if (pendingTint!.Color is null || pendingTint.Coverage is null)
                        throw new InvalidDataException("Raw TIAS closes an incomplete tint layer.");
                    tints.Add(new SkyrimFaceTintLayer(
                        pendingTint.Index,
                        pendingTint.Color.Value.Red,
                        pendingTint.Color.Value.Green,
                        pendingTint.Color.Value.Blue,
                        pendingTint.Color.Value.Alpha,
                        pendingTint.Coverage.Value,
                        BinaryPrimitives.ReadInt16LittleEndian(data)));
                    pendingTint = null;
                    break;
            }
        });

        if (pendingTint is not null) throw new InvalidDataException("Raw tint order ends with an unclosed layer.");
        if (headParts.Count == 0 || hairColor is null ||
            faceTextureSet is null || weight is null ||
            nam9 is null || nama is null || qnam is null || tints.Count == 0)
        {
            throw new InvalidDataException(
                "The authored NPC is missing PNAM, HCLF, FTST, NAM7, NAM9, NAMA, tint, or QNAM data.");
        }

        return new RawAppearance(
            headParts.ToImmutable(), hairColor.Value, faceTextureSet.Value,
            skinArmor ?? 0u, defaultOutfit ?? 0u, weight.Value,
            nam9.Value, nama.Value, tints.ToImmutable(), qnam.Value);
    }

    private static void VerifyRawOwnedHairColor(
        RawPluginSnapshot raw,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        OutputOwnedSkyrimNpcHairColor expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var expectedFormId = ToRawOutputFormId(expected.AllocatedLocalFormId, proposal);
        var candidates = raw.Records.Where(item => item.Signature == "CLFM").ToArray();
        Check(candidates.Length == 1 && candidates[0].FormId == expectedFormId,
            "npc-create-raw-owned-clfm-ownership-mismatch",
            "Raw read-back did not find exactly one output-owned CLFM at the allocated FormID.", diagnostics);
        if (candidates.Length != 1 || candidates[0].FormId != expectedFormId) return;

        string? editorId = null;
        byte[]? color = null;
        uint? flags = null;
        ReadSubrecords(candidates[0].Body.Span, (signature, data) =>
        {
            switch (signature)
            {
                case "EDID":
                    if (editorId is not null) throw Duplicate(signature);
                    if (data.IsEmpty || data[^1] != 0)
                        throw new InvalidDataException("Raw CLFM EDID is not NUL terminated.");
                    editorId = Encoding.Latin1.GetString(data[..^1]);
                    break;
                case "CNAM":
                    RequireLength(signature, data, 4);
                    if (color is not null) throw Duplicate(signature);
                    color = data.ToArray();
                    break;
                case "FNAM":
                    flags = ReadUniqueUInt32(signature, data, flags);
                    break;
            }
        });
        var rgb = expected.PackedRgb.Value;
        var expectedColor = new[]
        {
            (byte)((rgb >> 16) & 0xFFu),
            (byte)((rgb >> 8) & 0xFFu),
            (byte)(rgb & 0xFFu),
            byte.MaxValue
        };
        Check(candidates[0].FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(editorId,
                  BethesdaNpcCreationAdapter.BuildHairColorEditorId(request.Identity.EditorId.Value),
                  StringComparison.Ordinal) &&
              color is not null && color.AsSpan().SequenceEqual(expectedColor) && flags == 1,
            "npc-create-raw-owned-clfm-mismatch",
            "Raw CLFM EDID, CNAM, FNAM, or FormVersion does not match the authored record.", diagnostics);
    }

    private static void VerifyTypedOwnedFaceTextureSet(
        ISkyrimModGetter overlay,
        EditorId npcEditorId,
        ModKey outputModKey,
        OutputOwnedSkyrimNpcFaceTextureSet expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var key = new FormKey(outputModKey, expected.AllocatedLocalFormId.Value);
        var texture = overlay.TextureSets.FirstOrDefault(item => item.FormKey == key);
        Check(texture is not null,
            "npc-create-typed-owned-txst-missing",
            "Typed read-back did not find the output-owned private head TXST.", diagnostics);
        if (texture is null) return;
        var flags = TextureSet.Flag.FaceGenTextures | TextureSet.Flag.HasModelSpaceNormalMap;
        Check(texture.FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(texture.EditorID,
                  BethesdaNpcCreationAdapter.BuildPrivateHeadTextureSetEditorId(
                      npcEditorId.Value), StringComparison.Ordinal) &&
              texture.Flags == flags && !texture.IsCompressed && !texture.IsDeleted &&
              TexturePathsMatch(texture, expected.Paths),
            "npc-create-typed-owned-txst-mismatch",
            "Typed read-back of the output-owned TXST does not match its exact paths, flags, or identity.",
            diagnostics);
    }

    private static void VerifyTypedOwnedFaceHeadPart(
        ISkyrimModGetter overlay,
        EditorId npcEditorId,
        NpcSex npcSex,
        FormReference targetRace,
        WorkspacePath dataRoot,
        ModKey outputModKey,
        FormKey expectedTextureSet,
        OutputOwnedSkyrimNpcFaceHeadPart expected,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var key = new FormKey(outputModKey, expected.AllocatedLocalFormId.Value);
        var actual = overlay.HeadParts.FirstOrDefault(item => item.FormKey == key);
        Check(actual is not null,
            "npc-create-typed-owned-hdpt-missing",
            "Typed read-back did not find the output-owned private Face HDPT.", diagnostics);
        if (actual is null) return;

        var providerPath = BethesdaNpcCreationProviderResolver.Resolve(
            dataRoot, expected.QualifiedExternalFaceHdpt.Plugin, pluginAuthorities);
        var providerModKey = ModKey.FromNameAndExtension(
            expected.QualifiedExternalFaceHdpt.Plugin.Value);
        using var provider = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(providerModKey, new Noggog.FilePath(providerPath)),
            SkyrimRelease.SkyrimSE);
        var source = provider.HeadParts.FirstOrDefault(
            item => item.FormKey == ToFormKey(expected.QualifiedExternalFaceHdpt));
        Check(source is not null &&
              actual.FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  actual.EditorID,
                  expected.PreserveQualifiedEditorId
                      ? source?.EditorID
                      : BethesdaNpcCreationAdapter.BuildPrivateFaceHeadPartEditorId(
                          npcEditorId.Value),
                  StringComparison.Ordinal) &&
              actual.Type == HeadPart.TypeEnum.Face &&
              actual.TextureSet.FormKeyNullable == expectedTextureSet &&
              !actual.IsCompressed && !actual.IsDeleted &&
              BethesdaNpcCreationAdapter.HeadPartCompatible(
                  actual, npcSex, targetRace, dataRoot, pluginAuthorities) &&
              source is not null && HeadPartCarrierSemanticsMatch(actual, source),
            "npc-create-typed-owned-hdpt-mismatch",
            "Typed read-back of the output-owned Face HDPT does not match its qualified model carrier and private TXST binding.",
            diagnostics);
    }

    private static void VerifyTypedExternalHeadPart(
        ExternalSkyrimNpcHeadPart expected,
        NpcSex npcSex,
        FormReference targetRace,
        WorkspacePath dataRoot,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var providerPath = BethesdaNpcCreationProviderResolver.Resolve(
            dataRoot, expected.Hdpt.Plugin, pluginAuthorities);
        var providerModKey = ModKey.FromNameAndExtension(expected.Hdpt.Plugin.Value);
        using var provider = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(providerModKey, new Noggog.FilePath(providerPath)),
            SkyrimRelease.SkyrimSE);
        IHeadPartGetter? actual = provider.HeadParts.FirstOrDefault(item =>
            item.FormKey == ToFormKey(expected.Hdpt));
        Check(actual is not null &&
              BethesdaNpcCreationAdapter.HeadPartCompatible(
                  actual, npcSex, targetRace, dataRoot, pluginAuthorities),
            "npc-create-typed-external-hdpt-incompatible",
            "Typed read-back found an external HDPT that is incompatible with the NPC sex or target race.",
            diagnostics);
    }

    private static void VerifyTypedNakedSkinBinding(
        ISkyrimModGetter overlay,
        INpcGetter npc,
        EditorId npcEditorId,
        ModKey outputModKey,
        OutputOwnedSkyrimNpcNakedSkinBinding expected,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var armorKey = new FormKey(
            outputModKey,
            expected.AllocatedArmorLocalFormId.Value);
        var addonKeys = expected.Regions
            .Select(item => new FormKey(
                outputModKey,
                item.AllocatedArmorAddonLocalFormId.Value))
            .ToImmutableArray();
        IArmorGetter? actualArmor = overlay.Armors.FirstOrDefault(
            item => item.FormKey == armorKey);
        IArmorAddonGetter[] actualAddons = addonKeys
            .Select(key => overlay.ArmorAddons.FirstOrDefault(
                item => item.FormKey == key))
            .Where(item => item is not null)
            .Cast<IArmorAddonGetter>()
            .ToArray();
        Check(actualArmor is not null &&
              actualAddons.Length == expected.Regions.Length,
            "npc-create-typed-owned-naked-skin-missing",
            "Typed read-back did not find the private naked ARMA/ARMO graph.",
            diagnostics);
        if (actualArmor is null ||
            actualAddons.Length != expected.Regions.Length)
            return;

        using var armorProvider = OpenVerificationProvider(
            expected.SourceSkinArmor,
            pluginAuthorities);
        IArmorGetter? sourceArmor = armorProvider.Armors.FirstOrDefault(
            item => item.FormKey == ToFormKey(
                expected.SourceSkinArmor.Reference));

        var addonByKey = actualAddons.ToDictionary(
            item => item.FormKey);
        bool addonsMatch = true;
        foreach (var region in expected.Regions)
        {
            FormKey addonKey = new(
                outputModKey,
                region.AllocatedArmorAddonLocalFormId.Value);
            if (!addonByKey.TryGetValue(addonKey, out var actualAddon))
            {
                addonsMatch = false;
                continue;
            }

            using var addonProvider = OpenVerificationProvider(
                region.SourceArmorAddon,
                pluginAuthorities);
            IArmorAddonGetter? sourceAddon =
                addonProvider.ArmorAddons.FirstOrDefault(item =>
                    item.FormKey == ToFormKey(
                        region.SourceArmorAddon.Reference));
            FormKey? targetTexture =
                region.TargetFemaleSkinTextureSet is { } target
                    ? ToFormKey(target.Reference)
                    : sourceAddon?.SkinTexture?.Female?.FormKeyNullable;
            addonsMatch &=
                sourceAddon is not null &&
                actualAddon.FormVersion ==
                BethesdaNpcCreationAdapter.RecordFormVersion &&
                string.Equals(
                    actualAddon.EditorID,
                    BethesdaNpcCreationAdapter
                        .BuildPrivateNakedSkinArmorAddonEditorId(
                            npcEditorId.Value, region.Region),
                    StringComparison.Ordinal) &&
                actualAddon.SkinTexture?.Female?.FormKeyNullable ==
                targetTexture &&
                actualAddon.SkinTexture?.Male?.FormKeyNullable ==
                sourceAddon?.SkinTexture?.Male?.FormKeyNullable &&
                actualAddon.BodyTemplate?.FirstPersonFlags ==
                sourceAddon?.BodyTemplate?.FirstPersonFlags &&
                actualAddon.Race.FormKey == sourceAddon?.Race.FormKey &&
                actualAddon.AdditionalRaces.Select(item => item.FormKey)
                    .SequenceEqual(
                        sourceAddon?.AdditionalRaces.Select(
                            item => item.FormKey) ?? []) &&
                ModelAssetPathMatches(
                    actualAddon.WorldModel?.Female?.File?.ToString(),
                    region.FemaleModel.Value);
        }

        Check(sourceArmor is not null &&
              actualArmor.FormVersion ==
              BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  actualArmor.EditorID,
                  BethesdaNpcCreationAdapter
                      .BuildPrivateNakedSkinArmorEditorId(
                          npcEditorId.Value),
                  StringComparison.Ordinal) &&
              actualArmor.Armature.Select(item => item.FormKey)
                  .SequenceEqual(addonKeys) &&
              addonsMatch,
            "npc-create-typed-owned-naked-skin-mismatch",
            "Typed private naked-skin read-back changed source carrier semantics, female model paths, or the exact WNAM ARMO armature.",
            diagnostics);
    }

    private static void VerifyTypedExposedOutfitSkinBinding(
        ISkyrimModGetter overlay,
        INpcGetter npc,
        EditorId npcEditorId,
        WorkspacePath dataRoot,
        ModKey outputModKey,
        OutputOwnedSkyrimNpcExposedOutfitSkinBinding expected,
        ImmutableArray<Diagnostic>.Builder diagnostics,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        var addonKey = new FormKey(
            outputModKey,
            expected.AllocatedArmorAddonLocalFormId.Value);
        var armorKey = new FormKey(
            outputModKey,
            expected.AllocatedArmorLocalFormId.Value);
        var outfitKey = new FormKey(
            outputModKey,
            expected.AllocatedOutfitLocalFormId.Value);
        IArmorAddonGetter? actualAddon = overlay.ArmorAddons.FirstOrDefault(
            item => item.FormKey == addonKey);
        IArmorGetter? actualArmor = overlay.Armors.FirstOrDefault(
            item => item.FormKey == armorKey);
        IOutfitGetter? actualOutfit = overlay.Outfits.FirstOrDefault(
            item => item.FormKey == outfitKey);
        Check(actualAddon is not null && actualArmor is not null &&
              actualOutfit is not null &&
              npc.DefaultOutfit.FormKeyNullable == outfitKey,
            "npc-create-typed-owned-outfit-skin-missing",
            "Typed read-back did not find the private ARMA/ARMO/OTFT graph or NPC DOFT.",
            diagnostics);
        if (actualAddon is null || actualArmor is null ||
            actualOutfit is null) return;

        using var addonProvider = OpenVerificationProvider(
            expected.SourceArmorAddon,
            pluginAuthorities);
        IArmorAddonGetter? sourceAddon =
            addonProvider.ArmorAddons.FirstOrDefault(
                item => item.FormKey ==
                        ToFormKey(expected.SourceArmorAddon.Reference));
        using var armorProvider = OpenVerificationProvider(
            expected.SourceArmor,
            pluginAuthorities);
        IArmorGetter? sourceArmor = armorProvider.Armors.FirstOrDefault(
            item => item.FormKey == ToFormKey(
                expected.SourceArmor.Reference));
        using var outfitProvider = OpenVerificationProvider(
            expected.SourceOutfit,
            pluginAuthorities);
        IOutfitGetter? sourceOutfit = outfitProvider.Outfits.FirstOrDefault(
            item => item.FormKey == ToFormKey(
                expected.SourceOutfit.Reference));

        FormKey targetTexture =
            ToFormKey(expected.TargetFemaleSkinTextureSet.Reference);
        FormKey sourceAddonKey = ToFormKey(
            expected.SourceArmorAddon.Reference);
        FormKey sourceArmorKey = ToFormKey(expected.SourceArmor.Reference);
        FormKey[] expectedArmature = sourceArmor is null
            ? []
            : sourceArmor.Armature.Select(item =>
                    item.FormKey == sourceAddonKey
                        ? addonKey
                        : item.FormKey)
                .ToArray();
        FormKey[] expectedItems = sourceOutfit is null
            ? []
            : (sourceOutfit.Items ?? []).Select(item =>
                    item.FormKey == sourceArmorKey
                        ? armorKey
                        : item.FormKey)
                .ToArray();

        Check(sourceAddon is not null && sourceArmor is not null &&
              sourceOutfit is not null &&
              actualAddon.FormVersion ==
              BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  actualAddon.EditorID,
                  BethesdaNpcCreationAdapter
                      .BuildPrivateOutfitArmorAddonEditorId(
                          npcEditorId.Value),
                  StringComparison.Ordinal) &&
              actualAddon.SkinTexture?.Female?.FormKey ==
              targetTexture &&
              actualAddon.SkinTexture?.Male?.FormKeyNullable ==
              sourceAddon?.SkinTexture?.Male?.FormKeyNullable &&
              actualAddon.BodyTemplate?.FirstPersonFlags ==
              sourceAddon?.BodyTemplate?.FirstPersonFlags &&
              actualAddon.Race.FormKey ==
              sourceAddon?.Race.FormKey &&
              actualAddon.AdditionalRaces.Select(item => item.FormKey)
                  .SequenceEqual(
                      sourceAddon?.AdditionalRaces.Select(
                          item => item.FormKey) ?? []) &&
              AssetPathMatches(
                  actualAddon.WorldModel?.Female?.File?.ToString(),
                  sourceAddon?.WorldModel?.Female?.File?.ToString()) &&
              actualArmor.FormVersion ==
              BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  actualArmor.EditorID,
                  BethesdaNpcCreationAdapter
                      .BuildPrivateOutfitArmorEditorId(
                          npcEditorId.Value),
                  StringComparison.Ordinal) &&
              actualArmor.Armature.Select(item => item.FormKey)
                  .SequenceEqual(expectedArmature) &&
              actualOutfit.FormVersion ==
              BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  actualOutfit.EditorID,
                  BethesdaNpcCreationAdapter.BuildPrivateOutfitEditorId(
                      npcEditorId.Value),
                  StringComparison.Ordinal) &&
              (actualOutfit.Items ?? []).Select(item => item.FormKey)
                  .SequenceEqual(expectedItems),
            "npc-create-typed-owned-outfit-skin-mismatch",
            "Typed private outfit read-back changed source carrier semantics or lost the exact female body TXST substitution.",
            diagnostics);
    }

    private static ISkyrimModDisposableGetter OpenVerificationProvider(
        WorkspacePath dataRoot,
        PluginName plugin,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        string path = BethesdaNpcCreationProviderResolver.Resolve(
            dataRoot, plugin, pluginAuthorities);
        ModKey modKey = ModKey.FromNameAndExtension(plugin.Value);
        return SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(modKey, new Noggog.FilePath(path)),
            SkyrimRelease.SkyrimSE);
    }

    private static ISkyrimModDisposableGetter OpenVerificationProvider(
        RaceMenuNpcFormBinding binding,
        ImmutableArray<NpcCreationPluginAuthority> pluginAuthorities)
    {
        NpcCreationPluginAuthority? authority = null;
        foreach (NpcCreationPluginAuthority candidate in
                 pluginAuthorities)
        {
            if (!string.Equals(
                    candidate.Plugin.Value,
                    binding.ProviderPluginName.Value,
                    StringComparison.OrdinalIgnoreCase))
                continue;
            if (authority is not null)
                throw new InvalidDataException(
                    $"Winning provider '{binding.ProviderPluginName}' is declared more than once.");
            authority = candidate;
        }
        if (authority is null ||
            authority.PluginPath != binding.ProviderPlugin ||
            authority.ExpectedSha256 != binding.ProviderPluginSha256)
        {
            throw new InvalidDataException(
                $"The winning {binding.Signature} provider binding is not present with its exact path and hash in the verification request.");
        }
        ModKey modKey = ModKey.FromNameAndExtension(
            binding.ProviderPluginName.Value);
        return SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(
                modKey,
                new Noggog.FilePath(binding.ProviderPlugin.Value)),
            SkyrimRelease.SkyrimSE);
    }

    private static void VerifyRawExposedOutfitSkinBinding(
        RawPluginSnapshot raw,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        OutputOwnedSkyrimNpcExposedOutfitSkinBinding expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        uint addonFormId = ToRawOutputFormId(
            expected.AllocatedArmorAddonLocalFormId, proposal);
        uint armorFormId = ToRawOutputFormId(
            expected.AllocatedArmorLocalFormId, proposal);
        uint outfitFormId = ToRawOutputFormId(
            expected.AllocatedOutfitLocalFormId, proposal);
        RawMajorRecord? addon = raw.Records.SingleOrDefault(item =>
            item.Signature == "ARMA" && item.FormId == addonFormId);
        RawMajorRecord? armor = raw.Records.SingleOrDefault(item =>
            item.Signature == "ARMO" && item.FormId == armorFormId);
        RawMajorRecord? outfit = raw.Records.SingleOrDefault(item =>
            item.Signature == "OTFT" && item.FormId == outfitFormId);
        Check(addon is not null && armor is not null && outfit is not null,
            "npc-create-raw-owned-outfit-skin-missing",
            "Raw read-back did not find the output-owned ARMA/ARMO/OTFT records at their allocated FormIDs.",
            diagnostics);
        if (addon is null || armor is null || outfit is null) return;

        using var addonProvider = OpenVerificationProvider(
            expected.SourceArmorAddon,
            request.PluginAuthorities);
        IArmorAddonGetter? sourceAddon =
            addonProvider.ArmorAddons.FirstOrDefault(
                item => item.FormKey == ToFormKey(
                    expected.SourceArmorAddon.Reference));
        using var armorProvider = OpenVerificationProvider(
            expected.SourceArmor,
            request.PluginAuthorities);
        IArmorGetter? sourceArmor = armorProvider.Armors.FirstOrDefault(
            item => item.FormKey == ToFormKey(
                expected.SourceArmor.Reference));
        using var outfitProvider = OpenVerificationProvider(
            expected.SourceOutfit,
            request.PluginAuthorities);
        IOutfitGetter? sourceOutfit = outfitProvider.Outfits.FirstOrDefault(
            item => item.FormKey == ToFormKey(
                expected.SourceOutfit.Reference));
        if (sourceAddon is null || sourceArmor is null ||
            sourceOutfit is null)
        {
            diagnostics.Add(Error(
                "npc-create-raw-owned-outfit-skin-source-missing",
                "Raw verification could not reopen the exact winning ARMA, ARMO, or OTFT."));
            return;
        }

        uint sourceAddonId = ToRawFormId(
            expected.SourceArmorAddon.Reference, proposal);
        uint sourceArmorId = ToRawFormId(
            expected.SourceArmor.Reference, proposal);
        uint[] expectedArmature = sourceArmor.Armature
            .Select(item =>
            {
                uint value = ToRawFormId(
                    Reference(item.FormKey), proposal);
                return value == sourceAddonId
                    ? addonFormId
                    : value;
            })
            .ToArray();
        uint[] expectedItems = (sourceOutfit.Items ?? [])
            .Select(item =>
            {
                uint value = ToRawFormId(
                    Reference(item.FormKey), proposal);
                return value == sourceArmorId ? armorFormId : value;
            })
            .ToArray();
        uint targetTexture = ToRawFormId(
            expected.TargetFemaleSkinTextureSet.Reference, proposal);

        Check(addon.FormVersion ==
              BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  ReadRawEditorId(addon.Body.Span),
                  BethesdaNpcCreationAdapter
                      .BuildPrivateOutfitArmorAddonEditorId(
                          request.Identity.EditorId.Value),
                  StringComparison.Ordinal) &&
              ReadRawUniqueLink(addon.Body.Span, "NAM1") ==
              targetTexture &&
              armor.FormVersion ==
              BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  ReadRawEditorId(armor.Body.Span),
                  BethesdaNpcCreationAdapter
                      .BuildPrivateOutfitArmorEditorId(
                          request.Identity.EditorId.Value),
                  StringComparison.Ordinal) &&
              ReadRawLinks(armor.Body.Span, "MODL")
                  .SequenceEqual(expectedArmature) &&
              outfit.FormVersion ==
              BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  ReadRawEditorId(outfit.Body.Span),
                  BethesdaNpcCreationAdapter.BuildPrivateOutfitEditorId(
                      request.Identity.EditorId.Value),
                  StringComparison.Ordinal) &&
              ReadRawLinks(outfit.Body.Span, "INAM")
                  .SequenceEqual(expectedItems),
            "npc-create-raw-owned-outfit-skin-mismatch",
            "Raw ARMA NAM1, ARMO MODL, OTFT INAM, EDID, FormID, or FormVersion data does not match the private outfit binding.",
            diagnostics);

        VerifyCompleteOutfitCarrierBytes(
            raw,
            request,
            proposal,
            expected,
            sourceAddon,
            sourceArmor,
            sourceOutfit,
            diagnostics);
    }

    private static void VerifyCompleteOutfitCarrierBytes(
        RawPluginSnapshot actual,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        OutputOwnedSkyrimNpcExposedOutfitSkinBinding binding,
        IArmorAddonGetter sourceAddon,
        IArmorGetter sourceArmor,
        IOutfitGetter sourceOutfit,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            ModKey outputKey = ModKey.FromNameAndExtension(
                proposal.OutputPlugin.Value);
            var expectedMod = new SkyrimMod(
                outputKey,
                SkyrimRelease.SkyrimSE,
                headerVersion: BethesdaNpcCreationAdapter.HeaderVersion,
                forceUseLowerFormIDRanges: false);
            foreach (PluginName master in proposal.Masters)
            {
                expectedMod.ModHeader.MasterReferences.Add(
                    new MasterReference
                    {
                        Master = ModKey.FromNameAndExtension(
                            master.Value)
                    });
            }

            var addonKey = new FormKey(
                outputKey,
                binding.AllocatedArmorAddonLocalFormId.Value);
            var armorKey = new FormKey(
                outputKey,
                binding.AllocatedArmorLocalFormId.Value);
            var outfitKey = new FormKey(
                outputKey,
                binding.AllocatedOutfitLocalFormId.Value);
            var expectedAddon =
                expectedMod.ArmorAddons.DuplicateInAsNewRecord(
                    sourceAddon,
                    addonKey);
            expectedAddon.FormVersion =
                BethesdaNpcCreationAdapter.RecordFormVersion;
            expectedAddon.EditorID =
                BethesdaNpcCreationAdapter
                    .BuildPrivateOutfitArmorAddonEditorId(
                        request.Identity.EditorId.Value);
            expectedAddon.SkinTexture =
                new GenderedItem<
                    IFormLinkNullableGetter<ITextureSetGetter>>(
                    expectedAddon.SkinTexture?.Male ??
                    new FormLinkNullable<ITextureSetGetter>(),
                    new FormLinkNullable<ITextureSetGetter>(
                        ToFormKey(
                            binding.TargetFemaleSkinTextureSet
                                .Reference)));

            FormKey sourceAddonKey = ToFormKey(
                binding.SourceArmorAddon.Reference);
            var expectedArmor =
                expectedMod.Armors.DuplicateInAsNewRecord(
                    sourceArmor,
                    armorKey);
            expectedArmor.FormVersion =
                BethesdaNpcCreationAdapter.RecordFormVersion;
            expectedArmor.EditorID =
                BethesdaNpcCreationAdapter
                    .BuildPrivateOutfitArmorEditorId(
                        request.Identity.EditorId.Value);
            FormKey[] armature = sourceArmor.Armature
                .Select(item => item.FormKey == sourceAddonKey
                    ? addonKey
                    : item.FormKey)
                .ToArray();
            expectedArmor.Armature.Clear();
            foreach (FormKey key in armature)
            {
                expectedArmor.Armature.Add(
                    new FormLink<IArmorAddonGetter>(key));
            }

            FormKey sourceArmorKey = ToFormKey(
                binding.SourceArmor.Reference);
            var expectedOutfit =
                expectedMod.Outfits.DuplicateInAsNewRecord(
                    sourceOutfit,
                    outfitKey);
            expectedOutfit.FormVersion =
                BethesdaNpcCreationAdapter.RecordFormVersion;
            expectedOutfit.EditorID =
                BethesdaNpcCreationAdapter
                    .BuildPrivateOutfitEditorId(
                        request.Identity.EditorId.Value);
            FormKey[] items = (sourceOutfit.Items ?? [])
                .Select(item => item.FormKey == sourceArmorKey
                    ? armorKey
                    : item.FormKey)
                .ToArray();
            if (expectedOutfit.Items is null)
                throw new InvalidDataException(
                    "The normalized expected OTFT lost its item collection.");
            expectedOutfit.Items.Clear();
            foreach (FormKey key in items)
            {
                expectedOutfit.Items.Add(
                    new FormLink<IOutfitTargetGetter>(key));
            }

            using var stream = new MemoryStream();
            expectedMod.WriteToBinary(
                stream,
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent =
                        MastersListContentOption.NoCheck,
                    MastersListOrdering =
                        MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });
            RawPluginSnapshot normalized = ReadRaw(
                stream.ToArray(),
                CancellationToken.None);
            foreach ((string signature, uint formId) in new[]
                     {
                         ("ARMA", ToRawOutputFormId(
                             binding.AllocatedArmorAddonLocalFormId,
                             proposal)),
                         ("ARMO", ToRawOutputFormId(
                             binding.AllocatedArmorLocalFormId,
                             proposal)),
                         ("OTFT", ToRawOutputFormId(
                             binding.AllocatedOutfitLocalFormId,
                             proposal))
                     })
            {
                RawMajorRecord expected = normalized.Records.Single(
                    item => item.Signature == signature &&
                            item.FormId == formId);
                RawMajorRecord? authored =
                    actual.Records.SingleOrDefault(
                        item => item.Signature == signature &&
                                item.FormId == formId);
                Check(authored is not null &&
                      authored.Flags == expected.Flags &&
                      authored.VersionControl ==
                      expected.VersionControl &&
                      authored.FormVersion == expected.FormVersion &&
                      authored.Unknown == expected.Unknown &&
                      authored.Body.Span.SequenceEqual(
                          expected.Body.Span),
                    "npc-create-raw-owned-outfit-carrier-drift",
                    $"The output-owned {signature} differs from a complete normalized clone outside the explicitly allowed substitutions.",
                    diagnostics);
            }
        }
        catch (Exception exception) when (exception is
                                           InvalidDataException or
                                           IOException or
                                           ArgumentException or
                                           OverflowException)
        {
            diagnostics.Add(Error(
                "npc-create-raw-owned-outfit-carrier-compare-failed",
                exception.Message));
        }
    }

    private static string ReadRawEditorId(ReadOnlySpan<byte> payload)
    {
        string? editorId = null;
        ReadSubrecords(payload, (signature, data) =>
        {
            if (signature == "EDID")
                editorId = ReadUniqueZString(
                    signature, data, editorId);
        });
        return editorId ?? throw new InvalidDataException(
            "Raw output-owned record is missing EDID.");
    }

    private static uint ReadRawUniqueLink(
        ReadOnlySpan<byte> payload,
        string expectedSignature)
    {
        uint? value = null;
        ReadSubrecords(payload, (signature, data) =>
        {
            if (signature == expectedSignature)
                value = ReadUniqueUInt32(signature, data, value);
        });
        return value ?? throw new InvalidDataException(
            $"Raw output-owned record is missing {expectedSignature}.");
    }

    private static ImmutableArray<uint> ReadRawLinks(
        ReadOnlySpan<byte> payload,
        string expectedSignature)
    {
        var values = ImmutableArray.CreateBuilder<uint>();
        ReadSubrecords(payload, (signature, data) =>
        {
            if (signature != expectedSignature) return;
            if (data.IsEmpty || data.Length % sizeof(uint) != 0)
                throw new InvalidDataException(
                    $"Raw {signature} must contain one or more complete FormIDs.");
            for (var offset = 0; offset < data.Length; offset += sizeof(uint))
                values.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                    data[offset..]));
        });
        return values.ToImmutable();
    }

    private static FormReference Reference(FormKey key) => new(
        new PluginName(key.ModKey.ToString()),
        new FormId(key.ID));

    private static void VerifyRawNakedSkinBinding(
        RawPluginSnapshot raw,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        OutputOwnedSkyrimNpcNakedSkinBinding expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        uint armorFormId = ToRawOutputFormId(
            expected.AllocatedArmorLocalFormId, proposal);
        RawMajorRecord? armor = raw.Records.SingleOrDefault(item =>
            item.Signature == "ARMO" && item.FormId == armorFormId);
        var addonRows = expected.Regions
            .Select(region => (
                Region: region,
                FormId: ToRawOutputFormId(
                    region.AllocatedArmorAddonLocalFormId, proposal),
                Record: raw.Records.SingleOrDefault(item =>
                    item.Signature == "ARMA" &&
                    item.FormId == ToRawOutputFormId(
                        region.AllocatedArmorAddonLocalFormId, proposal))))
            .ToArray();
        Check(armor is not null &&
              addonRows.All(item => item.Record is not null),
            "npc-create-raw-owned-naked-skin-missing",
            "Raw read-back did not find the output-owned naked ARMA/ARMO records at their allocated FormIDs.",
            diagnostics);
        if (armor is null ||
            addonRows.Any(item => item.Record is null))
            return;

        using var armorProvider = OpenVerificationProvider(
            expected.SourceSkinArmor,
            request.PluginAuthorities);
        IArmorGetter? sourceArmor = armorProvider.Armors.FirstOrDefault(
            item => item.FormKey == ToFormKey(
                expected.SourceSkinArmor.Reference));
        if (sourceArmor is null)
        {
            diagnostics.Add(Error(
                "npc-create-raw-owned-naked-skin-source-missing",
                "Raw verification could not reopen the exact winning naked skin ARMO."));
            return;
        }

        var sourceAddons = ImmutableArray.CreateBuilder<IArmorAddonGetter>(
            expected.Regions.Length);
        var addonProviders = new List<ISkyrimModDisposableGetter>();
        foreach (var region in expected.Regions)
        {
            var addonProvider = OpenVerificationProvider(
                region.SourceArmorAddon,
                request.PluginAuthorities);
            addonProviders.Add(addonProvider);
            IArmorAddonGetter? sourceAddon =
                addonProvider.ArmorAddons.FirstOrDefault(item =>
                    item.FormKey == ToFormKey(
                        region.SourceArmorAddon.Reference));
            if (sourceAddon is null)
            {
                diagnostics.Add(Error(
                    "npc-create-raw-owned-naked-skin-source-missing",
                    "Raw verification could not reopen one exact winning naked skin ARMA."));
                foreach (var provider in addonProviders)
                    provider.Dispose();
                return;
            }
            sourceAddons.Add(sourceAddon);
        }

        try
        {
            bool addonsMatch = true;
            foreach (var row in addonRows)
            {
                uint? targetTexture =
                    row.Region.TargetFemaleSkinTextureSet is { } target
                        ? ToRawFormId(target.Reference, proposal)
                        : null;
                ImmutableArray<uint> actualFemaleTextures =
                    ReadRawLinks(row.Record!.Body.Span, "NAM1");
                addonsMatch &=
                    row.Record.FormVersion ==
                    BethesdaNpcCreationAdapter.RecordFormVersion &&
                    string.Equals(
                        ReadRawEditorId(row.Record.Body.Span),
                        BethesdaNpcCreationAdapter
                            .BuildPrivateNakedSkinArmorAddonEditorId(
                                request.Identity.EditorId.Value,
                                row.Region.Region),
                        StringComparison.Ordinal) &&
                    (targetTexture is null
                        ? actualFemaleTextures.Length == 0
                        : actualFemaleTextures.Length == 1 &&
                          actualFemaleTextures[0] == targetTexture.Value);
            }

            uint[] expectedArmature = addonRows
                .Select(item => item.FormId)
                .ToArray();
            Check(addonsMatch &&
                  armor.FormVersion ==
                  BethesdaNpcCreationAdapter.RecordFormVersion &&
                  string.Equals(
                      ReadRawEditorId(armor.Body.Span),
                      BethesdaNpcCreationAdapter
                          .BuildPrivateNakedSkinArmorEditorId(
                              request.Identity.EditorId.Value),
                      StringComparison.Ordinal) &&
                  ReadRawLinks(armor.Body.Span, "MODL")
                      .SequenceEqual(expectedArmature),
                "npc-create-raw-owned-naked-skin-mismatch",
                "Raw private naked-skin ARMA NAM1, ARMO MODL, EDID, FormID, or FormVersion data does not match the private WNAM binding.",
                diagnostics);

            VerifyCompleteNakedSkinCarrierBytes(
                raw,
                request,
                proposal,
                expected,
                sourceArmor,
                sourceAddons.ToImmutable(),
                diagnostics);
        }
        finally
        {
            foreach (var provider in addonProviders)
                provider.Dispose();
        }
    }

    private static void VerifyCompleteNakedSkinCarrierBytes(
        RawPluginSnapshot actual,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        OutputOwnedSkyrimNpcNakedSkinBinding binding,
        IArmorGetter sourceArmor,
        ImmutableArray<IArmorAddonGetter> sourceAddons,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        try
        {
            ModKey outputKey = ModKey.FromNameAndExtension(
                proposal.OutputPlugin.Value);
            var expectedMod = new SkyrimMod(
                outputKey,
                SkyrimRelease.SkyrimSE,
                headerVersion: BethesdaNpcCreationAdapter.HeaderVersion,
                forceUseLowerFormIDRanges: false);
            foreach (PluginName master in proposal.Masters)
            {
                expectedMod.ModHeader.MasterReferences.Add(
                    new MasterReference
                    {
                        Master = ModKey.FromNameAndExtension(
                            master.Value)
                    });
            }

            var ownedAddons = ImmutableArray.CreateBuilder<FormKey>(
                binding.Regions.Length);
            for (var index = 0; index < binding.Regions.Length; index++)
            {
                var region = binding.Regions[index];
                var addonKey = new FormKey(
                    outputKey,
                    region.AllocatedArmorAddonLocalFormId.Value);
                var expectedAddon =
                    expectedMod.ArmorAddons.DuplicateInAsNewRecord(
                        sourceAddons[index],
                        addonKey);
                expectedAddon.FormVersion =
                    BethesdaNpcCreationAdapter.RecordFormVersion;
                expectedAddon.EditorID =
                    BethesdaNpcCreationAdapter
                        .BuildPrivateNakedSkinArmorAddonEditorId(
                            request.Identity.EditorId.Value,
                            region.Region);
                expectedAddon.SkinTexture =
                    new GenderedItem<
                        IFormLinkNullableGetter<ITextureSetGetter>>(
                        expectedAddon.SkinTexture?.Male ??
                        new FormLinkNullable<ITextureSetGetter>(),
                        region.TargetFemaleSkinTextureSet is { } targetTexture
                            ? new FormLinkNullable<ITextureSetGetter>(
                                ToFormKey(targetTexture.Reference))
                            : expectedAddon.SkinTexture?.Female ??
                              new FormLinkNullable<ITextureSetGetter>());
                var femaleModel = expectedAddon.WorldModel?.Female ??
                                  new Model();
                femaleModel.File = ToSkyrimModelPath(
                    region.FemaleModel);
                expectedAddon.WorldModel = new GenderedItem<Model?>(
                    expectedAddon.WorldModel?.Male,
                    femaleModel);
                ownedAddons.Add(addonKey);
            }

            var armorKey = new FormKey(
                outputKey,
                binding.AllocatedArmorLocalFormId.Value);
            var expectedArmor =
                expectedMod.Armors.DuplicateInAsNewRecord(
                    sourceArmor,
                    armorKey);
            expectedArmor.FormVersion =
                BethesdaNpcCreationAdapter.RecordFormVersion;
            expectedArmor.EditorID =
                BethesdaNpcCreationAdapter
                    .BuildPrivateNakedSkinArmorEditorId(
                        request.Identity.EditorId.Value);
            expectedArmor.Armature.Clear();
            foreach (FormKey addon in ownedAddons)
            {
                expectedArmor.Armature.Add(
                    new FormLink<IArmorAddonGetter>(addon));
            }

            using var stream = new MemoryStream();
            expectedMod.WriteToBinary(
                stream,
                new BinaryWriteParameters
                {
                    ModKey = ModKeyOption.NoCheck,
                    MastersListContent =
                        MastersListContentOption.NoCheck,
                    MastersListOrdering =
                        MastersListOrderingOption.NoCheck,
                    NextFormID = NextFormIDOption.NoCheck
                });
            RawPluginSnapshot normalized = ReadRaw(
                stream.ToArray(),
                CancellationToken.None);
            foreach ((string signature, uint formId) in
                     binding.Regions.Select(region => (
                             "ARMA",
                             ToRawOutputFormId(
                                 region.AllocatedArmorAddonLocalFormId,
                                 proposal)))
                         .Append((
                             "ARMO",
                             ToRawOutputFormId(
                                 binding.AllocatedArmorLocalFormId,
                                 proposal))))
            {
                RawMajorRecord expected = normalized.Records.Single(
                    item => item.Signature == signature &&
                            item.FormId == formId);
                RawMajorRecord? authored =
                    actual.Records.SingleOrDefault(
                        item => item.Signature == signature &&
                                item.FormId == formId);
                Check(authored is not null &&
                      authored.Flags == expected.Flags &&
                      authored.VersionControl ==
                      expected.VersionControl &&
                      authored.FormVersion == expected.FormVersion &&
                      authored.Unknown == expected.Unknown &&
                      authored.Body.Span.SequenceEqual(
                          expected.Body.Span),
                    "npc-create-raw-owned-naked-skin-carrier-drift",
                    $"The output-owned {signature} differs from a complete normalized clone outside the explicitly allowed WNAM skin substitutions.",
                    diagnostics);
            }
        }
        catch (Exception exception) when (exception is
                                           InvalidDataException or
                                           IOException or
                                           ArgumentException or
                                           KeyNotFoundException or
                                           OverflowException)
        {
            diagnostics.Add(Error(
                "npc-create-raw-owned-naked-skin-carrier-compare-failed",
                exception.Message));
        }
    }

    private static string ToSkyrimModelPath(AssetPath dataRelativePath)
    {
        var value = dataRelativePath.Value.Replace('\\', '/');
        return value.StartsWith("Meshes/", StringComparison.OrdinalIgnoreCase)
            ? value["Meshes/".Length..]
            : value;
    }

    private static void VerifyRawOwnedFaceTextureSet(
        RawPluginSnapshot raw,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        OutputOwnedSkyrimNpcFaceTextureSet expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var expectedFormId = ToRawOutputFormId(expected.AllocatedLocalFormId, proposal);
        var candidates = raw.Records.Where(item => item.Signature == "TXST").ToArray();
        Check(candidates.Length == 1 && candidates[0].FormId == expectedFormId,
            "npc-create-raw-owned-txst-ownership-mismatch",
            "Raw read-back did not find exactly one output-owned TXST at the allocated FormID.", diagnostics);
        if (candidates.Length != 1 || candidates[0].FormId != expectedFormId) return;

        var actual = ReadRawTextureSet(candidates[0].Body.Span);
        Check(candidates[0].FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(actual.EditorId,
                  BethesdaNpcCreationAdapter.BuildPrivateHeadTextureSetEditorId(
                      request.Identity.EditorId.Value), StringComparison.Ordinal) &&
              actual.Flags == 0x0006 && RawTexturePathsMatch(actual.Paths, expected.Paths),
            "npc-create-raw-owned-txst-mismatch",
            "Raw TXST EDID, TX00-TX07 paths, DNAM flags, or FormVersion do not match the authored record.",
            diagnostics);
    }

    private static void VerifyRawOwnedFaceHeadPart(
        RawPluginSnapshot raw,
        NpcCreationRequest request,
        NpcCreationProposal proposal,
        uint expectedTextureSet,
        OutputOwnedSkyrimNpcFaceHeadPart expected,
        ImmutableArray<Diagnostic>.Builder diagnostics)
    {
        var expectedFormId = ToRawOutputFormId(expected.AllocatedLocalFormId, proposal);
        var candidates = raw.Records.Where(item => item.Signature == "HDPT").ToArray();
        Check(candidates.Length == 1 && candidates[0].FormId == expectedFormId,
            "npc-create-raw-owned-hdpt-ownership-mismatch",
            "Raw read-back did not find exactly one output-owned HDPT at the allocated FormID.", diagnostics);
        if (candidates.Length != 1 || candidates[0].FormId != expectedFormId) return;

        var providerPath = BethesdaNpcCreationProviderResolver.Resolve(
            new WorkspacePath(Path.GetDirectoryName(request.TemplatePlugin.Value)!),
            expected.QualifiedExternalFaceHdpt.Plugin,
            request.PluginAuthorities);
        var providerModKey = ModKey.FromNameAndExtension(
            expected.QualifiedExternalFaceHdpt.Plugin.Value);
        using var provider = SkyrimMod.CreateFromBinaryOverlay(
            new ModPath(providerModKey, new Noggog.FilePath(providerPath)), SkyrimRelease.SkyrimSE);
        var source = provider.HeadParts.FirstOrDefault(
            item => item.FormKey == ToFormKey(expected.QualifiedExternalFaceHdpt));
        var actual = ReadRawFaceHeadPart(candidates[0].Body.Span);
        Check(candidates[0].FormVersion == BethesdaNpcCreationAdapter.RecordFormVersion &&
              string.Equals(
                  actual.EditorId,
                  expected.PreserveQualifiedEditorId
                      ? source?.EditorID
                      : BethesdaNpcCreationAdapter.BuildPrivateFaceHeadPartEditorId(
                          request.Identity.EditorId.Value),
                  StringComparison.Ordinal) &&
              actual.Type == (uint)HeadPart.TypeEnum.Face &&
              actual.TextureSet == expectedTextureSet && source?.Model is { } sourceModel &&
              ModelAssetPathMatches(actual.ModelPath, sourceModel.File?.ToString()),
            "npc-create-raw-owned-hdpt-mismatch",
            "Raw HDPT EDID, MODL, PNAM, TNAM, or FormVersion does not match the authored record.",
            diagnostics);
    }

    private static RawTextureSet ReadRawTextureSet(ReadOnlySpan<byte> payload)
    {
        string? editorId = null;
        var paths = new string?[8];
        ushort? flags = null;
        var hasZeroBounds = false;
        ReadSubrecords(payload, (signature, data) =>
        {
            if (signature == "EDID") editorId = ReadUniqueZString(signature, data, editorId);
            else if (signature is "TX00" or "TX01" or "TX02" or "TX03" or
                     "TX04" or "TX05" or "TX06" or "TX07")
            {
                var index = signature[3] - '0';
                paths[index] = ReadUniqueZString(signature, data, paths[index]);
            }
            else if (signature == "DNAM")
            {
                RequireLength(signature, data, sizeof(ushort));
                if (flags is not null) throw Duplicate(signature);
                flags = BinaryPrimitives.ReadUInt16LittleEndian(data);
            }
            else if (signature == "OBND")
            {
                RequireLength(signature, data, 12);
                if (hasZeroBounds || data.ContainsAnyExcept((byte)0))
                    throw new InvalidDataException("Raw TXST OBND must occur once as twelve zero bytes.");
                hasZeroBounds = true;
            }
            else
            {
                throw new InvalidDataException($"Raw TXST contains unexpected {signature} data.");
            }
        });
        if (editorId is null || flags is null || !hasZeroBounds)
            throw new InvalidDataException("Raw TXST is missing EDID, zero OBND, or DNAM.");
        return new RawTextureSet(editorId, paths.ToImmutableArray(), flags.Value);
    }

    private static RawFaceHeadPart ReadRawFaceHeadPart(ReadOnlySpan<byte> payload)
    {
        string? editorId = null;
        string? modelPath = null;
        uint? type = null;
        uint? textureSet = null;
        ReadSubrecords(payload, (signature, data) =>
        {
            switch (signature)
            {
                case "EDID":
                    editorId = ReadUniqueZString(signature, data, editorId);
                    break;
                case "MODL":
                    modelPath = ReadUniqueZString(signature, data, modelPath);
                    break;
                case "PNAM":
                    type = ReadUniqueUInt32(signature, data, type);
                    break;
                case "TNAM":
                    textureSet = ReadUniqueUInt32(signature, data, textureSet);
                    break;
            }
        });
        if (editorId is null || modelPath is null || type is null || textureSet is null)
            throw new InvalidDataException("Raw HDPT is missing EDID, MODL, PNAM, or TNAM.");
        return new RawFaceHeadPart(editorId, modelPath, type.Value, textureSet.Value);
    }

    private static uint ToRawFormId(FormReference reference, NpcCreationProposal proposal)
    {
        var index = -1;
        for (var candidate = 0; candidate < proposal.Masters.Length; candidate++)
        {
            if (string.Equals(proposal.Masters[candidate].Value, reference.Plugin.Value,
                    StringComparison.OrdinalIgnoreCase))
            {
                index = candidate;
                break;
            }
        }
        if (index < 0)
            throw new InvalidDataException($"Reference provider {reference.Plugin} is absent from the proposal masters.");
        return checked((uint)(index << 24)) | reference.FormId.Value;
    }

    private static uint ToRawOutputFormId(FormId localFormId, NpcCreationProposal proposal) =>
        checked((uint)(proposal.Masters.Length << 24)) | localFormId.Value;

    private static bool TexturePathsMatch(
        ITextureSetGetter actual,
        SkyrimPrivateHeadTexturePaths expected) =>
        TypedTextureAssetPathMatches(actual.Diffuse?.ToString(), expected.Diffuse.Value) &&
        TypedTextureAssetPathMatches(actual.NormalOrGloss?.ToString(), expected.NormalOrGloss.Value) &&
        TypedTextureAssetPathMatches(actual.EnvironmentMaskOrSubsurfaceTint?.ToString(),
            expected.EnvironmentMaskOrSubsurfaceTint?.Value) &&
        TypedTextureAssetPathMatches(actual.GlowOrDetailMap?.ToString(), expected.GlowOrDetailMap.Value) &&
        TypedTextureAssetPathMatches(actual.Height?.ToString(), expected.Height.Value) &&
        TypedTextureAssetPathMatches(actual.Environment?.ToString(), expected.Environment?.Value) &&
        TypedTextureAssetPathMatches(actual.Multilayer?.ToString(), expected.Multilayer?.Value) &&
        TypedTextureAssetPathMatches(actual.BacklightMaskOrSpecular?.ToString(),
            expected.BacklightMaskOrSpecular.Value);

    private static bool RawTexturePathsMatch(
        ImmutableArray<string?> actual,
        SkyrimPrivateHeadTexturePaths expected)
    {
        var expectedPaths = new string?[]
        {
            expected.Diffuse.Value,
            expected.NormalOrGloss.Value,
            expected.EnvironmentMaskOrSubsurfaceTint?.Value,
            expected.GlowOrDetailMap.Value,
            expected.Height.Value,
            expected.Environment?.Value,
            expected.Multilayer?.Value,
            expected.BacklightMaskOrSpecular.Value
        };
        return actual.Length == expectedPaths.Length &&
               actual.Zip(expectedPaths).All(pair => AssetPathMatches(pair.First, pair.Second));
    }

    private static bool AssetPathMatches(string? actual, string? expected)
    {
        var normalizedActual = string.IsNullOrEmpty(actual)
            ? null
            : actual.Replace('\\', '/');
        var normalizedExpected = string.IsNullOrEmpty(expected)
            ? null
            : expected.Replace('\\', '/');
        return string.Equals(normalizedActual, normalizedExpected, StringComparison.OrdinalIgnoreCase);
    }

    // Mutagen exposes Skyrim texture AssetLinks as Data-relative paths rooted at
    // "Textures/", while the TX00-TX07 binary strings themselves are relative
    // to Data/Textures. RawTexturePathsMatch deliberately remains exact so an
    // accidentally serialized double-Textures path cannot pass verification.
    private static bool TypedTextureAssetPathMatches(string? actual, string? expected)
    {
        static string? Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var normalized = value.Replace('\\', '/');
            return normalized.StartsWith("textures/", StringComparison.OrdinalIgnoreCase)
                ? normalized["textures/".Length..]
                : normalized;
        }
        return string.Equals(Normalize(actual), Normalize(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool ModelAssetPathMatches(string? actual, string? expected)
    {
        static string? Normalize(string? value)
        {
            if (string.IsNullOrEmpty(value)) return null;
            var normalized = value.Replace('\\', '/');
            return normalized.StartsWith("meshes/", StringComparison.OrdinalIgnoreCase)
                ? normalized["meshes/".Length..]
                : normalized;
        }
        return string.Equals(Normalize(actual), Normalize(expected),
            StringComparison.OrdinalIgnoreCase);
    }

    private static bool HeadPartCarrierSemanticsMatch(
        IHeadPartGetter actual,
        IHeadPartGetter source) =>
        actual.Flags == source.Flags &&
        actual.ValidRaces.FormKeyNullable == source.ValidRaces.FormKeyNullable &&
        actual.Color.FormKeyNullable == source.Color.FormKeyNullable &&
        actual.ExtraParts.Select(item => item.FormKey)
            .SequenceEqual(source.ExtraParts.Select(item => item.FormKey)) &&
        actual.Parts.Select(item => (item.PartType, Path: item.FileName?.ToString()))
            .SequenceEqual(source.Parts.Select(item => (item.PartType, Path: item.FileName?.ToString()))) &&
        actual.Model is { } actualModel && source.Model is { } sourceModel &&
        AssetPathMatches(actualModel.File?.ToString(), sourceModel.File?.ToString()) &&
        (actualModel.Data is null && sourceModel.Data is null ||
         actualModel.Data is { } actualData && sourceModel.Data is { } sourceData &&
         actualData.Span.SequenceEqual(sourceData.Span)) &&
        (actualModel.AlternateTextures?.Count ?? 0) == 0 &&
        (sourceModel.AlternateTextures?.Count ?? 0) == 0;

    private static string ReadUniqueZString(
        string signature,
        ReadOnlySpan<byte> data,
        string? current)
    {
        if (current is not null) throw Duplicate(signature);
        if (data.IsEmpty || data[^1] != 0)
            throw new InvalidDataException($"Raw {signature} is not NUL terminated.");
        return Encoding.Latin1.GetString(data[..^1]);
    }

    private static ImmutableArray<float> ToNam9(INpcFaceMorphGetter? source) => source is null
        ? []
        :
        [
            source.NoseLongVsShort,
            source.NoseUpVsDown,
            source.JawUpVsDown,
            source.JawNarrowVsWide,
            source.JawForwardVsBack,
            source.CheeksUpVsDown,
            source.CheeksForwardVsBack,
            source.EyesUpVsDown,
            source.EyesInVsOut,
            source.BrowsUpVsDown,
            source.BrowsInVsOut,
            source.BrowsForwardVsBack,
            source.LipsUpVsDown,
            source.LipsInVsOut,
            source.ChinNarrowVsWide,
            source.ChinUpVsDown,
            source.ChinUnderbiteVsOverbite,
            source.EyesForwardVsBack,
            source.Unknown
        ];

    private static ImmutableArray<uint> ToNama(INpcFacePartsGetter? source) => source is null
        ? []
        : [source.Nose, source.Unknown, source.Eyes, source.Mouth];

    private static bool TypedTintsMatch(
        IReadOnlyList<ITintLayerGetter> actual,
        ImmutableArray<SkyrimFaceTintLayer> expected)
    {
        if (actual.Count != expected.Length) return false;
        for (var index = 0; index < expected.Length; index++)
        {
            var source = actual[index];
            var target = expected[index];
            if (source.Index != target.Index || source.Color is not { } color ||
                color.R != target.Red || color.G != target.Green || color.B != target.Blue ||
                color.A != target.Alpha ||
                source.InterpolationValue is not { } interpolation ||
                Math.Abs(interpolation - (target.Coverage / 100f)) > 0.000001f ||
                source.Preset != target.PresetIndex)
            {
                return false;
            }
        }
        return true;
    }

    private static bool TypedQnamMatches(Color? actual, SkyrimQnamRgb expected) =>
        actual is { } color && color.R == ToColorByte(expected.Red) &&
        color.G == ToColorByte(expected.Green) && color.B == ToColorByte(expected.Blue);

    private static bool RawQnamMatches(SkyrimQnamRgb actual, SkyrimQnamRgb expected) =>
        SameSingle(actual.Red, expected.Red) && SameSingle(actual.Green, expected.Green) &&
        SameSingle(actual.Blue, expected.Blue);

    private static bool ColorMatchesPackedRgb(Color actual, uint packedRgb) =>
        actual.R == (byte)((packedRgb >> 16) & 0xFFu) &&
        actual.G == (byte)((packedRgb >> 8) & 0xFFu) &&
        actual.B == (byte)(packedRgb & 0xFFu) && actual.A == byte.MaxValue;

    private static int ToColorByte(float normalized) =>
        Math.Clamp((int)MathF.Round(normalized * byte.MaxValue), byte.MinValue, byte.MaxValue);

    private static bool SameSingle(float left, float right) =>
        BitConverter.SingleToInt32Bits(left) == BitConverter.SingleToInt32Bits(right);

    private static float ReadSingle(ReadOnlySpan<byte> data) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(data));

    private static uint ReadUniqueUInt32(string signature, ReadOnlySpan<byte> data, uint? current)
    {
        RequireLength(signature, data, sizeof(uint));
        if (current is not null) throw Duplicate(signature);
        return BinaryPrimitives.ReadUInt32LittleEndian(data);
    }

    private static void RequireLength(string signature, ReadOnlySpan<byte> data, int expected)
    {
        if (data.Length != expected)
            throw new InvalidDataException($"Raw {signature} must contain exactly {expected} bytes.");
    }

    private static void RequireTintState(string signature, PendingRawTint? pending)
    {
        if (pending is null)
            throw new InvalidDataException($"Raw {signature} occurs without a preceding TINI.");
    }

    private static InvalidDataException Duplicate(string signature) =>
        new($"Raw {signature} occurs more than once where exactly one value is required.");

    private sealed record RawAppearance(
        ImmutableArray<uint> HeadParts,
        uint HairColor,
        uint FaceTextureSet,
        uint SkinArmor,
        uint DefaultOutfit,
        float Weight,
        ImmutableArray<float> Nam9,
        ImmutableArray<uint> Nama,
        ImmutableArray<SkyrimFaceTintLayer> Tints,
        SkyrimQnamRgb Qnam);

    private sealed record RawTextureSet(
        string EditorId,
        ImmutableArray<string?> Paths,
        ushort Flags);

    private sealed record RawFaceHeadPart(
        string EditorId,
        string ModelPath,
        uint Type,
        uint TextureSet);

    private sealed class PendingRawTint(ushort index)
    {
        public ushort Index { get; } = index;
        public (byte Red, byte Green, byte Blue, byte Alpha)? Color { get; set; }
        public uint? Coverage { get; set; }
    }
}
