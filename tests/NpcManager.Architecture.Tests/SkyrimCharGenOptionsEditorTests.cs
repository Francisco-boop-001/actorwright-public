using System.Text.Json;
using System.Text.Json.Serialization;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static readonly JsonSerializerOptions CharGenEditorJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters =
        {
            new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)
        }
    };

    private static async Task TestSkyrimCharGenOptionsEditor()
    {
        CharGenOptions opening = CharGenOptionsDefaults.For(
            GameEdition.SkyrimSpecialEdition);
        SkyrimCharGenOptionsEditorResult accepted =
            SkyrimCharGenOptionsEditorRules.Save(opening);
        Assert(accepted.Accepted && accepted.Options == opening,
            "The complete default Skyrim CharGen document was refused.");
        Assert(!SkyrimCharGenOptionsEditorRules.Save(
                CharGenOptionsDefaults.For(GameEdition.Fallout4)).Accepted,
            "The Skyrim CharGen editor accepted a Fallout 4 document.");
        Assert(!SkyrimCharGenOptionsEditorRules.Cancel().Accepted,
            "CharGen Cancel returned an accepted document.");

        CharGenOptions uniform = SkyrimCharGenOptionsEditorRules.NormalizeTextureMode(
            opening with
            {
                PerLayerResolution = false,
                DiffuseResolution = FaceGenChannelResolution.R2048,
                NormalResolution = FaceGenChannelResolution.R512,
                SpecularResolution = FaceGenChannelResolution.R8192,
                DiffuseCompression = FaceGenDiffuseCompression.Bc7,
                NormalCompression = FaceGenNormalSpecularCompression.Bc3,
                SpecularCompression = FaceGenNormalSpecularCompression.Uncompressed
            });
        Assert(uniform.NormalResolution == FaceGenChannelResolution.R2048 &&
               uniform.SpecularResolution == FaceGenChannelResolution.R2048 &&
               uniform.NormalCompression ==
                   FaceGenNormalSpecularCompression.Uncompressed &&
               uniform.SpecularCompression ==
                   FaceGenNormalSpecularCompression.Bc5 &&
               SkyrimCharGenOptionsEditorRules.Save(uniform).Accepted,
            "Uniform mode did not apply the Skyrim game-aware derivation.");

        CharGenOptions perLayer =
            SkyrimCharGenOptionsEditorRules.NormalizeTextureMode(
                uniform with
                {
                    PerLayerResolution = true,
                    NormalResolution = FaceGenChannelResolution.R4096,
                    NormalCompression = FaceGenNormalSpecularCompression.Bc7,
                    SpecularResolution = FaceGenChannelResolution.R2048,
                    SpecularCompression = FaceGenNormalSpecularCompression.Bc3
                });
        Assert(perLayer.NormalResolution == FaceGenChannelResolution.R4096 &&
               perLayer.NormalCompression ==
                   FaceGenNormalSpecularCompression.Bc7 &&
               perLayer.SpecularResolution ==
                   FaceGenChannelResolution.Inherit &&
               perLayer.SpecularCompression ==
                   FaceGenNormalSpecularCompression.Bc5,
            "Per-layer mode did not preserve Normal and neutralize hidden Skyrim Specular values.");

        CharGenOptions edited = perLayer with
        {
            GenerateTga = true,
            BakeSseRaceMenuOverlays = false,
            Convention = perLayer.Convention with
            {
                SeedDiffuseG22 = !perLayer.Convention.SeedDiffuseG22
            },
            TintSort = perLayer.TintSort with
            {
                SkinTonePlacement = FaceTintSkinTonePlacement.LastOfAll
            }
        };
        CharGenOptions textureReset =
            SkyrimCharGenOptionsEditorRules.ResetTexture(edited);
        Assert(!textureReset.GenerateTga &&
               textureReset.BakeSseRaceMenuOverlays ==
                   edited.BakeSseRaceMenuOverlays &&
               textureReset.Convention == edited.Convention &&
               textureReset.TintSort == edited.TintSort,
            "Texture reset leaked into fixes, conventions, or ordering.");
        CharGenOptions conventionReset =
            SkyrimCharGenOptionsEditorRules.ResetConvention(edited);
        Assert(conventionReset.Convention.Diffuse ==
                   opening.Convention.Diffuse &&
               conventionReset.Convention.NormalSpecular ==
                   opening.Convention.NormalSpecular &&
               conventionReset.Convention.Swap == opening.Convention.Swap &&
               conventionReset.Convention.DiffuseWorkingSpaceByBlend ==
                   opening.Convention.DiffuseWorkingSpaceByBlend &&
               conventionReset.Convention.SeedConstant.SequenceEqual(
                   opening.Convention.SeedConstant) &&
               conventionReset.GenerateTga == edited.GenerateTga &&
               conventionReset.TintSort == edited.TintSort,
            "Convention reset leaked into texture or ordering state.");
        CharGenOptions sortReset =
            SkyrimCharGenOptionsEditorRules.ResetSort(edited);
        Assert(sortReset.TintSort.TintRules.SequenceEqual(
                   opening.TintSort.TintRules) &&
               sortReset.TintSort.SwapRules.SequenceEqual(
                   opening.TintSort.SwapRules) &&
               sortReset.TintSort.SkinTonePlacement ==
                   opening.TintSort.SkinTonePlacement &&
               sortReset.Convention == edited.Convention &&
               sortReset.GenerateTga == edited.GenerateTga,
            "Sort reset leaked into texture or convention state.");

        SkyrimCharGenOptionsEditorResult added =
            SkyrimCharGenOptionsEditorRules.AddSortRule(
                opening,
                SkyrimCharGenSortList.Tint,
                new FaceTintSortRule(
                    (int)FaceTintSseSortKey.Coverage,
                    true));
        Assert(added.Accepted &&
               added.Options?.TintSort.TintRules.Length == 2 &&
               added.Options.TintSort.TintRules[1] ==
                   new FaceTintSortRule(
                       (int)FaceTintSseSortKey.Coverage,
                   true),
            "A legal Skyrim tint sort rule was not appended exactly.");
        CharGenOptions withRule = added.Options ??
            throw new InvalidOperationException(
                "The accepted sort transition returned no document.");
        Assert(!SkyrimCharGenOptionsEditorRules.AddSortRule(
                withRule,
                SkyrimCharGenSortList.Tint,
                withRule.TintSort.TintRules[0]).Accepted,
            "A duplicate Skyrim tint sort key was accepted.");
        SkyrimCharGenOptionsEditorResult moved =
            SkyrimCharGenOptionsEditorRules.MoveSortRule(
                withRule,
                SkyrimCharGenSortList.Tint,
                1,
                0);
        Assert(moved.Accepted &&
               moved.Options?.TintSort.TintRules[0].Key ==
                   (int)FaceTintSseSortKey.Coverage,
            "The ordered tint rule did not move exactly.");
        CharGenOptions movedOptions = moved.Options ??
            throw new InvalidOperationException(
                "The moved sort transition returned no document.");
        SkyrimCharGenOptionsEditorResult removed =
            SkyrimCharGenOptionsEditorRules.RemoveSortRule(
                movedOptions,
                SkyrimCharGenSortList.Tint,
                0);
        Assert(removed.Accepted && removed.Options is { } removedOptions &&
               removedOptions.TintSort.TintRules.SequenceEqual(
                   opening.TintSort.TintRules),
            "Removing the moved tint rule did not restore the exact opening list.");

        Assert(!SkyrimCharGenOptionsEditorRules.Save(opening with
        {
            ApplyMouthVanillaFix = true
        }).Accepted,
            "A Fallout-only fix was accepted in a Skyrim options document.");
        Assert(!SkyrimCharGenOptionsEditorRules.Save(opening with
        {
            Convention = opening.Convention with
            {
                Diffuse = opening.Convention.Diffuse with
                {
                    MaskChannel = FaceTintMaskChannel.A
                }
            }
        }).Accepted,
            "A non-red Skyrim FaceTint mask channel was accepted.");

        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "chargen-editor-writer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string inputPath = Path.Combine(root, "accepted-options.json");
            string json = JsonSerializer.Serialize(
                    uniform,
                    CharGenEditorJsonOptions)
                .Replace("skyrimSpecialEdition", "skyrimse",
                    StringComparison.Ordinal);
            await File.WriteAllTextAsync(inputPath, json);
            WorkspacePath labRoot = new("K:\\ExampleWorkspace");
            var service = new FaceGenOptionsService(
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                labRoot);
            FaceGenOptionsResult canonical = await service.ValidateAsync(
                new FaceGenOptionsRequest(
                    GameEdition.SkyrimSpecialEdition,
                    new WorkspacePath(inputPath),
                    null,
                    null,
                    false),
                CancellationToken.None);
            Assert(canonical.IsValid && canonical.Options is not null &&
                   canonical.Options.DiffuseResolution ==
                       FaceGenChannelResolution.R2048,
                "The canonical hash-bound options service refused the accepted editor document.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task TestSkyrimCharGenOptionsProduction()
    {
        string root = Path.Combine(
            "K:\\ExampleWorkspace\\projects\\NpcManagerReimplementation\\03-builds\\work",
            "chargen-options-production-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            WorkspacePath labRoot = new("K:\\ExampleWorkspace");
            var service = new FaceGenOptionsService(
                new KOnlyWorkspacePolicy(
                    labRoot,
                    new WorkspacePath("F:\\ExampleGame")),
                labRoot);
            CharGenOptions supported =
                SkyrimCharGenOptionsEditorRules.NormalizeTextureMode(
                    CharGenOptionsDefaults.For(
                        GameEdition.SkyrimSpecialEdition) with
                    {
                        DiffuseResolution = FaceGenChannelResolution.R512,
                        NormalResolution = FaceGenChannelResolution.R512,
                        SpecularResolution = FaceGenChannelResolution.R512,
                        BakeSseRaceMenuOverlays = false
                    });
            WorkspacePath output = new(Path.Combine(root, "accepted-options.json"));
            FaceGenOptionsDocumentWriteResult written = await service.WriteAsync(
                new FaceGenOptionsDocumentWriteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    supported,
                    output),
                CancellationToken.None);
            Assert(written.Written && written.ReadbackVerified &&
                   written.OutputSha256 is not null && File.Exists(output.Value),
                "A supported accepted options document was not written and reopened canonically.");

            FaceGenOptionsResult reopened = await service.ValidateAsync(
                new FaceGenOptionsRequest(
                    GameEdition.SkyrimSpecialEdition,
                    output,
                    null,
                    written.OutputSha256,
                    false),
                CancellationToken.None);
            Assert(reopened.IsValid && reopened.Options is not null &&
                   reopened.Options.DiffuseResolution ==
                       FaceGenChannelResolution.R512 &&
                   !reopened.Options.BakeSseRaceMenuOverlays,
                "The canonical accepted options readback lost native-bake settings.");

            FaceGenOptionsDocumentWriteResult existing = await service.WriteAsync(
                new FaceGenOptionsDocumentWriteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    supported,
                    output),
                CancellationToken.None);
            Assert(!existing.Written,
                "The direct typed options writer overwrote an existing artifact.");
            FaceGenOptionsDocumentWriteResult protectedWrite = await service.WriteAsync(
                new FaceGenOptionsDocumentWriteRequest(
                    GameEdition.SkyrimSpecialEdition,
                    supported,
                    new WorkspacePath("F:\\ExampleGame\\chargen-options.json")),
                CancellationToken.None);
            Assert(!protectedWrite.Written,
                "The direct typed options writer accepted the protected live root.");

            Assert(!SkyrimCharGenNativeFaceTintConsumptionRules.Validate(supported)
                       .Any(item => item.Severity == DiagnosticSeverity.Error),
                "The exact 512/BC3/default-convention native FaceTint contract was refused.");
            Assert(SkyrimCharGenNativeFaceTintConsumptionRules.Validate(
                    SkyrimCharGenOptionsEditorRules.NormalizeTextureMode(
                        supported with
                        {
                            DiffuseCompression = FaceGenDiffuseCompression.Bc7
                        }))
                .Any(item => item.Code ==
                             "skyrim-chargen-native-facetint-compression" &&
                             item.Severity == DiagnosticSeverity.Error),
                "An unsupported native FaceTint compression did not fail before output.");
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
