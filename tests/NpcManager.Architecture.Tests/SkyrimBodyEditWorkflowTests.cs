using System.Buffers.Binary;
using System.Collections.Immutable;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimBodyEditProjection()
    {
        SkyrimBodyEditorDocument baselineDocument = BodyBaseline();
        var baseline = new SkyrimBodyEditWriterBaseline(baselineDocument);

        SkyrimBodyEditProjectionResult weight = SkyrimBodyEditProjector.Project(
            baseline,
            SkyrimBodyEditorDocumentRules.SetWeight(baselineDocument, 60F));
        Assert(weight.Accepted && weight.WeightPatch is
        {
            SkyrimValue: 60F,
            Thin: null,
            Muscular: null,
            Fat: null
        } && weight.Diagnostics.All(item =>
            item.Severity != DiagnosticSeverity.Error),
            "A weight-only body document did not project to one exact Skyrim NAM7 patch.");

        SkyrimBodyEditProjectionResult noChange = SkyrimBodyEditProjector.Project(
            baseline,
            baselineDocument);
        Assert(!noChange.Accepted && noChange.WeightPatch is null &&
               noChange.Diagnostics.Any(item => item.Code == "body-edit-no-change"),
            "A no-op body document was admitted as a writer transaction.");

        AssertBodyProjectionRefused(
            baseline,
            SkyrimBodyEditorDocumentRules.SetBodySlideValue(
                baselineDocument, "Waist", 0.75F),
            "body-edit-bodyslide-sidecar-only");
        AssertBodyProjectionRefused(
            baseline,
            baselineDocument with
            {
                NodeTransforms = baselineDocument.NodeTransforms.RemoveAt(0)
            },
            "body-edit-transform-sidecar-only");
        AssertBodyProjectionRefused(
            baseline,
            baselineDocument with
            {
                SkinOverrides = baselineDocument.SkinOverrides.Clear()
            },
            "body-edit-skin-sidecar-only");
        AssertBodyProjectionRefused(
            baseline,
            baselineDocument with
            {
                BodyOverlays = baselineDocument.BodyOverlays.RemoveAt(0)
            },
            "body-edit-overlay-sidecar-only");

        return Task.CompletedTask;
    }

    private static async Task TestSkyrimBodyEditSourceLoader()
    {
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        var dataRoot = new WorkspacePath(Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "fixtures",
            "skyrim-production"));
        PluginName sourcePlugin = new("FaceEditFixtureSSE.esp");
        WorkspacePath sourcePath = new(Path.Combine(dataRoot.Value, sourcePlugin.Value));
        SkyrimBodyEditSourceSnapshot source =
            new BethesdaSkyrimBodyEditSourceReader().Read(
                sourcePath,
                new FormId(0x800));
        Assert(source.SourceEditorId == new EditorId("FaceEditFixtureNpcSSE") &&
               source.Weight == 55F,
            "The body source reader did not preserve exact EditorID and NAM7 authority.");

        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            dataRoot,
            new WorkspacePath(Path.Combine(dataRoot.Value, "load-order.json")),
            new WorkspacePath(Path.Combine(dataRoot.Value, "future-output")),
            new Sha256Hash(new string('A', 64)),
            [
                Entry(sourcePlugin, 0, true)
            ],
            [],
            [],
            [],
            0,
            new Sha256Hash(new string('B', 64)),
            new Sha256Hash(new string('C', 64)),
            false);
        var policy = new KOnlyWorkspacePolicy(
            labRoot,
            new WorkspacePath("F:\\ExampleGame"));
        var loader = new SkyrimBodyEditLoadService(
            new BethesdaSkyrimRaceMenuPaintChoiceService(policy, labRoot),
            new BethesdaSkyrimBodyEditSourceReader());

        SkyrimBodyEditLoadResult loaded = await loader.LoadAsync(
            new SkyrimBodyEditLoadRequest(
                intake,
                sourcePlugin,
                new FormId(0x800)),
            CancellationToken.None);
        Assert(loaded.Accepted && loaded.State is { } state &&
               state.SourcePluginPath == sourcePath &&
               state.SourcePluginSha256 == HashFile(sourcePath.Value) &&
               state.SourceEditorId == source.SourceEditorId &&
               state.Document.Weight == 55F &&
               state.Document.BodySlide.IsEmpty &&
               state.Document.NodeTransforms.IsEmpty &&
               state.Document.SkinOverrides.IsEmpty &&
               state.Document.BodyOverlays.IsEmpty &&
               state.Catalogs.Paints.Keys.Order()
                   .SequenceEqual(new[]
                   {
                       SkyrimRaceMenuPaintCategory.Body,
                       SkyrimRaceMenuPaintCategory.Hands,
                       SkyrimRaceMenuPaintCategory.Feet
                   }.Order()) &&
               state.Catalogs.Paints.Values.All(item => item.Accepted) &&
               state.Catalogs.OverlaySlotLimits.Values.All(value => value == 16),
            "The real body loader did not create one honest hash-bound baseline: " +
            string.Join(" | ", loaded.Diagnostics.Select(item =>
                $"{item.Code}:{item.Message}")));

        string transactionRoot = Path.Combine(
            labRoot.Value,
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"body-edit-source-reference-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transactionRoot);
        try
        {
            var proposalPath = new WorkspacePath(Path.Combine(
                transactionRoot,
                "body-edit-proposal.json"));
            var outputPath = new WorkspacePath(Path.Combine(
                transactionRoot,
                "FaceEditFixtureSSE-BodyEdited.esp"));
            var request = new NpcOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                sourcePath,
                HashFile(sourcePath.Value),
                new FormId(0x800),
                proposalPath,
                outputPath,
                new NpcOverridePatch(
                    null,
                    null,
                    null,
                    Weight: new NpcWeightPatch(60F, null, null, null)));
            var overrideService = new NpcOverrideService(policy, labRoot);
            NpcOverrideProposal proposal = await overrideService.AnalyzeAsync(
                request,
                CancellationToken.None);
            Assert(proposal.IsApplicable &&
                   proposal.Changes.SequenceEqual(
                   [new MutationChange("SkyrimWeight", "55", "60")]) &&
                   File.Exists(proposalPath.Value) &&
                   !File.Exists(outputPath.Value),
                "The real body proposal was not exact or wrote the plugin early: " +
                string.Join(" | ", proposal.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));

            NpcOverrideResult applied = await overrideService.ApplyAsync(
                request,
                proposal,
                CancellationToken.None);
            NpcOverrideVerificationResult verified = await overrideService.VerifyAsync(
                request,
                proposal,
                CancellationToken.None);
            Assert(applied.Applied &&
                   applied.OutputSha256 == HashFile(outputPath.Value) &&
                   verified.IsValid &&
                   verified.MajorRecordCount == 1 &&
                   verified.NpcRecordCount == 1 &&
                   verified.SourceOwnedTargetCount == 1 &&
                   verified.SelfOwnedTargetCount == 0 &&
                   verified.ObservedChanges.SequenceEqual(proposal.Changes),
                "The real body writer did not produce one explicitly reopened NAM7 change: " +
                string.Join(" | ", applied.Diagnostics.Concat(verified.Diagnostics)
                    .Select(item => $"{item.Code}:{item.Message}")));
            AssertRawBodyWeightOnly(sourcePath.Value, outputPath.Value);
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
            {
                Directory.Delete(transactionRoot, recursive: true);
            }
        }

        SkyrimBodyEditLoadResult stale = await loader.LoadAsync(
            new SkyrimBodyEditLoadRequest(
                intake with
                {
                    Plugins = intake.Plugins.Select(item =>
                            item.Plugin == sourcePlugin
                                ? item with
                                {
                                    SourceHash = new Sha256Hash(new string('D', 64))
                                }
                                : item)
                        .ToImmutableArray()
                },
                sourcePlugin,
                new FormId(0x800)),
            CancellationToken.None);
        Assert(!stale.Accepted && stale.State is null &&
               stale.Diagnostics.Any(item =>
                   item.Code == "body-edit-source-changed" &&
                   item.Severity == DiagnosticSeverity.Error),
            "The body loader admitted a source that changed after review.");

        PluginClosureReviewEntry Entry(
            PluginName plugin,
            int order,
            bool requested)
        {
            WorkspacePath path = new(Path.Combine(dataRoot.Value, plugin.Value));
            return new PluginClosureReviewEntry(
                plugin,
                order,
                true,
                true,
                true,
                false,
                requested,
                path,
                HashFile(path.Value),
                []);
        }
    }

    private static void AssertRawBodyWeightOnly(
        string sourcePath,
        string outputPath)
    {
        ImmutableArray<(string Signature, string Data)> source =
            ReadRawBodyNpcSubrecords(sourcePath);
        ImmutableArray<(string Signature, string Data)> output =
            ReadRawBodyNpcSubrecords(outputPath);
        byte[] sourceWeight = Convert.FromBase64String(source.Single(item =>
            item.Signature == "NAM7").Data);
        byte[] outputWeight = Convert.FromBase64String(output.Single(item =>
            item.Signature == "NAM7").Data);
        Assert(sourceWeight.Length == 4 && outputWeight.Length == 4 &&
               BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                   sourceWeight)) == 55F &&
               BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(
                   outputWeight)) == 60F,
            "The raw NAM7 payload did not preserve exact 55 -> 60 float values.");
        Assert(source.Where(item => item.Signature != "NAM7")
                   .SequenceEqual(output.Where(item => item.Signature != "NAM7")),
            "A raw target NPC subrecord outside NAM7 changed in the body transaction.");
    }

    private static ImmutableArray<(string Signature, string Data)>
        ReadRawBodyNpcSubrecords(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        (int Start, int End)? payload = FindRawNpcPayload(
            bytes,
            0,
            bytes.Length,
            0x0000_0800u);
        Assert(payload is not null,
            $"The target NPC was absent from raw plugin {path}.");
        var subrecords = ImmutableArray.CreateBuilder<(string, string)>();
        int position = payload!.Value.Start;
        while (position < payload.Value.End)
        {
            string signature = System.Text.Encoding.ASCII.GetString(
                bytes,
                position,
                4);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            position += 6;
            subrecords.Add((
                signature,
                Convert.ToBase64String(bytes.AsSpan(position, size))));
            position += size;
        }
        return subrecords.ToImmutable();
    }

    private static void AssertBodyProjectionRefused(
        SkyrimBodyEditWriterBaseline baseline,
        SkyrimBodyEditorDocument changed,
        string code)
    {
        SkyrimBodyEditProjectionResult result = SkyrimBodyEditProjector.Project(
            baseline,
            changed);
        Assert(!result.Accepted && result.WeightPatch is null &&
               result.Diagnostics.Any(item =>
                   item.Code == code &&
                   item.Severity == DiagnosticSeverity.Error),
            $"The body projector silently discarded unsupported carrier {code}.");
    }
}
