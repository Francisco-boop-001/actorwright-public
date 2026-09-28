using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Formats.Bethesda;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static Task TestSkyrimFaceEditProjection()
    {
        PluginName plugin = new("P04SSE.esp");
        var race = new FormReference(plugin, new FormId(0x801));
        var face = new NpcHeadPartSelection(
            new FormReference(plugin, new FormId(0x811)),
            NpcHeadPartType.Face);
        var hair = new NpcHeadPartSelection(
            new FormReference(plugin, new FormId(0x813)),
            NpcHeadPartType.Hair);
        var hairColor = new FormReference(plugin, new FormId(0x802));
        var headTexture = new FormReference(plugin, new FormId(0x803));
        var morphs = new SkyrimFaceMorphPatch(
            Enumerable.Repeat(0F, SkyrimFaceEditorDocumentRules.NativeSliderCount)
                .ToImmutableArray(),
            0.125F,
            Enumerable.Repeat(SkyrimFaceEditorDocumentRules.NamaUnset,
                    SkyrimFaceEditorDocumentRules.NativeFamilyCount)
                .ToImmutableArray());
        var tint = new SkyrimFaceTintLayer(1, 10, 20, 30, 255, 75, 2);
        var document = new SkyrimFaceEditorDocument(
            new SkyrimFaceEditorParts(
                [face, hair],
                OptionalFormReference.Set(hairColor),
                headTexture,
                false),
            morphs,
            [],
            [new SkyrimFaceEditorTintLayer(tint, true, null, null)],
            [],
            []);
        var appearance = new FullyAuthoredSkyrimNpcAppearanceSource(
            [new ExternalSkyrimNpcHeadPart(face), new ExternalSkyrimNpcHeadPart(hair)],
            new ExternalSkyrimNpcHairColor(hairColor),
            new ExternalSkyrimNpcFaceTextureSet(headTexture),
            0.55F,
            morphs,
            new SkyrimFaceTintPatch([tint]),
            new SkyrimQnamRgb(0.1F, 0.2F, 0.3F));
        var baseline = new SkyrimFaceEditWriterBaseline(document, appearance, null);
        SkyrimFaceEditorDocument changed = SkyrimFaceEditorDocumentRules.SetNativeSlider(
            document, 0, 0.25F);

        SkyrimFaceEditProjectionResult projected = SkyrimFaceEditProjector.Project(
            baseline, changed);

        Assert(projected.Accepted && projected.Appearance is { } authored &&
               authored.FaceMorphs.Nam9Sliders[0] == 0.25F &&
               authored.Weight == appearance.Weight &&
               authored.Qnam == appearance.Qnam &&
               authored.OrderedHeadParts.SequenceEqual(appearance.OrderedHeadParts) &&
               authored.HairColor == appearance.HairColor &&
               authored.FaceTextureSet == appearance.FaceTextureSet &&
               authored.FaceTints == appearance.FaceTints &&
               projected.RuntimeAppearance is null,
            "A native-morph edit did not project into one complete preserved appearance.");

        SkyrimFaceEditorTintLayer masked = document.Tints[0] with
        {
            MaskOverride = new AssetPath("actors/character/character assets/tintmasks/accepted.dds")
        };
        SkyrimFaceEditProjectionResult refusedMask = SkyrimFaceEditProjector.Project(
            baseline,
            document with { Tints = [masked] });
        Assert(!refusedMask.Accepted && refusedMask.Appearance is null &&
               refusedMask.Diagnostics.Any(item =>
                   item.Code == "face-edit-mask-sidecar-only" &&
                   item.Severity == DiagnosticSeverity.Error),
            "A changed RaceMenu mask override was silently flattened into the plugin writer.");

        SkyrimFaceEditProjectionResult refusedClear = SkyrimFaceEditProjector.Project(
            baseline,
            document with
            {
                Parts = document.Parts with
                {
                    HairColor = OptionalFormReference.Clear()
                }
            });
        Assert(!refusedClear.Accepted && refusedClear.Appearance is null &&
               refusedClear.Diagnostics.Any(item =>
                   item.Code == "face-edit-hair-clear-unsupported"),
            "An explicit HCLF clear was silently converted to a fully-authored hair color.");
        return Task.CompletedTask;
    }

    private static async Task TestSkyrimFaceEditSourceReader()
    {
        var plugin = new PluginName("P04SSE.esp");
        var path = new WorkspacePath(Path.Combine(
            "K:\\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "fixtures",
            "skyrim-production",
            plugin.Value));

        SkyrimFaceEditSourceSnapshot source =
            new BethesdaSkyrimFaceEditSourceReader().Read(path, new FormId(0x800));

        Console.WriteLine(
            $"EVIDENCE FACE-EDIT-SOURCE editor={source.SourceEditorId} sex={source.Sex} " +
            $"race={source.Race} pnam={source.OrderedHeadParts.Length} " +
            $"hclf={source.HairColor} ftst={source.HeadTexture?.ToString() ?? "absent"} " +
            $"weight={source.Weight?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "absent"} " +
            $"nam9={source.FaceMorphs.HasNam9} nama={source.FaceMorphs.HasNama} " +
            $"tints={source.FaceTints.Layers.Length} qnam={(source.Qnam is null ? "absent" : "present")} " +
            $"scripts={source.ScriptNames.Length}");

        Assert(source.SourceEditorId.Value.Length > 0 &&
               source.Sex == NpcSex.Female &&
               source.Race == new FormReference(plugin, new FormId(0x801)) &&
               source.OrderedHeadParts.Length == 9 &&
               source.HairColor == new FormReference(plugin, new FormId(0x802)) &&
               source.HeadTexture is null &&
               source.FaceMorphs.Nam9Sliders.Length ==
               SkyrimFaceEditorDocumentRules.NativeSliderCount &&
               source.FaceMorphs.NamaValues.Length ==
               SkyrimFaceEditorDocumentRules.NativeFamilyCount &&
               !source.FaceMorphs.HasNam9 &&
               !source.FaceMorphs.HasNama &&
               source.FaceTints.Layers.IsEmpty &&
               source.Weight == 0F &&
               source.Qnam is null &&
               !source.ScriptNames.Contains(
                   SkyrimNpcApplySseContract.ScriptName,
                   StringComparer.OrdinalIgnoreCase),
            "The copied P04 NPC did not preserve its exact source appearance, including absent FTST.");

        WorkspacePath dataRoot = new(Path.GetDirectoryName(path.Value)!);
        WorkspacePath labRoot = new("K:\\ExampleWorkspace");
        Sha256Hash pluginHash = HashFile(path.Value);
        var intake = new ReviewedGameIntake(
            GameEdition.SkyrimSpecialEdition,
            labRoot,
            dataRoot,
            new WorkspacePath(Path.Combine(dataRoot.Value, "load-order.json")),
            new WorkspacePath(Path.Combine(dataRoot.Value, "future-output")),
            new Sha256Hash(new string('A', 64)),
            [
                new PluginClosureReviewEntry(
                    plugin,
                    0,
                    true,
                    true,
                    true,
                    false,
                    true,
                    path,
                    pluginHash,
                    [])
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
        var authorityLoader = new SkyrimFaceRecordPluginAuthorityLoader(policy, labRoot);
        var loader = new SkyrimFaceEditLoadService(
            new SkyrimHeadPartEditLoadService(
                new BethesdaSkyrimHeadPartChoiceService(authorityLoader)),
            new FormChoiceService(new BethesdaPluginReader(), policy, labRoot),
            new BethesdaSkyrimRaceMenuPaintChoiceService(policy, labRoot),
            new BethesdaSkyrimFaceEditSourceReader());
        SkyrimFaceEditLoadResult refused = await loader.LoadAsync(
                new SkyrimFaceEditLoadRequest(intake, plugin, new FormId(0x800)),
                CancellationToken.None);
        Assert(!refused.Accepted && refused.State is null &&
               refused.Diagnostics.Any(item =>
                   item.Code == "face-edit-source-ftst-required" &&
                   item.Severity == DiagnosticSeverity.Error),
            "The aggregate face-edit loader admitted a source without complete FTST writer authority.");

        var completeSource = source with
        {
            HeadTexture = new FormReference(plugin, new FormId(0x804)),
            Weight = 0.55F,
            FaceMorphs = new SkyrimFaceMorphSnapshot(
                Enumerable.Repeat(0F, SkyrimFaceEditorDocumentRules.NativeSliderCount)
                    .ToImmutableArray(),
                0.125F,
                Enumerable.Repeat(SkyrimFaceEditorDocumentRules.NamaUnset,
                        SkyrimFaceEditorDocumentRules.NativeFamilyCount)
                    .ToImmutableArray(),
                true,
                true),
            FaceTints = new SkyrimFaceTintPatch(
                [new SkyrimFaceTintLayer(1, 10, 20, 30, 255, 75, 2)]),
            Qnam = new SkyrimQnamRgb(0.1F, 0.2F, 0.3F)
        };
        var acceptedLoader = new SkyrimFaceEditLoadService(
            new StubFaceHeadPartLoader(completeSource, path, pluginHash),
            new StubFaceFormChoiceService(completeSource.Race, "P04RaceSSE"),
            new StubFacePaintChoiceService(),
            new StubFaceEditSourceReader(completeSource));
        SkyrimFaceEditLoadResult accepted = await acceptedLoader.LoadAsync(
                new SkyrimFaceEditLoadRequest(intake, plugin, new FormId(0x800)),
                CancellationToken.None);
        Assert(accepted.Accepted && accepted.State is { } state &&
               state.SourcePluginPath == path &&
               state.SourcePluginSha256 == pluginHash &&
               state.Document.NativeMorphs.Nam9Trailing == 0.125F &&
               state.Document.Tints.Length == 1 &&
               state.WriterBaseline.Appearance.Weight == 0.55F &&
               state.WriterBaseline.Appearance.Qnam ==
               new SkyrimQnamRgb(0.1F, 0.2F, 0.3F) &&
               state.RaceEditorId == "P04RaceSSE" &&
               state.Catalogs.Paints.Count ==
               Enum.GetValues<SkyrimRaceMenuPaintCategory>().Length,
            "A complete typed source was not reconstructed into one immutable face-edit baseline.");

        var fixturePlugin = new PluginName("FaceEditFixtureSSE.esp");
        var fixturePath = new WorkspacePath(Path.Combine(
            "K:\\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "tests",
            "fixtures",
            "skyrim-production",
            fixturePlugin.Value));
        SkyrimFaceEditSourceSnapshot fixtureSource =
            new BethesdaSkyrimFaceEditSourceReader().Read(
                fixturePath,
                new FormId(0x800));
        Assert(fixtureSource.SourceEditorId ==
                   new EditorId("FaceEditFixtureNpcSSE") &&
               fixtureSource.Sex == NpcSex.Female &&
               fixtureSource.OrderedHeadParts.Length == 9 &&
               fixtureSource.HeadTexture ==
                   new FormReference(fixturePlugin, new FormId(0x804)) &&
               fixtureSource.Weight == 55F &&
               fixtureSource.FaceMorphs.HasNam9 &&
               fixtureSource.FaceMorphs.HasNama &&
               fixtureSource.FaceMorphs.Nam9Trailing == 0.125F &&
               fixtureSource.FaceTints.Layers.SequenceEqual(
                   [new SkyrimFaceTintLayer(1, 10, 20, 30, 255, 75, 2)]) &&
               fixtureSource.Qnam is { } fixtureQnam &&
               Math.Abs(fixtureQnam.Red - 32F / 255F) < 0.000001F &&
               Math.Abs(fixtureQnam.Green - 96F / 255F) < 0.000001F &&
               Math.Abs(fixtureQnam.Blue - 160F / 255F) < 0.000001F &&
               fixtureSource.ScriptNames.IsEmpty,
            "The purpose-built complete face-edit fixture did not preserve its exact semantic source fields.");

        WorkspacePath fixtureDataRoot = new(Path.GetDirectoryName(fixturePath.Value)!);
        Sha256Hash fixtureHash = HashFile(fixturePath.Value);
        var fixtureIntake = intake with
        {
            DataRoot = fixtureDataRoot,
            LoadOrderPath = new WorkspacePath(Path.Combine(
                fixtureDataRoot.Value,
                "load-order.json")),
            OutputRoot = new WorkspacePath(Path.Combine(
                fixtureDataRoot.Value,
                "future-output")),
            Plugins =
            [
                new PluginClosureReviewEntry(
                    fixturePlugin,
                    0,
                    true,
                    true,
                    true,
                    false,
                    true,
                    fixturePath,
                    fixtureHash,
                    [])
            ]
        };
        var fixtureLoader = new SkyrimFaceEditLoadService(
            new SkyrimHeadPartEditLoadService(
                new BethesdaSkyrimHeadPartChoiceService(authorityLoader)),
            new FormChoiceService(new BethesdaPluginReader(), policy, labRoot),
            new BethesdaSkyrimRaceMenuPaintChoiceService(policy, labRoot),
            new BethesdaSkyrimFaceEditSourceReader());
        SkyrimFaceEditLoadResult fixtureLoaded = await fixtureLoader.LoadAsync(
                new SkyrimFaceEditLoadRequest(
                    fixtureIntake,
                    fixturePlugin,
                    new FormId(0x800)),
                CancellationToken.None);
        SkyrimFaceEditLoadedState fixtureState = fixtureLoaded.State ??
            throw new InvalidDataException(
                "The real aggregate face-edit loader omitted its accepted state.");
        Assert(fixtureLoaded.Accepted &&
               fixtureState.SourcePluginSha256 == fixtureHash &&
               fixtureState.RaceEditorId == "FaceEditFixtureRaceSSE" &&
               fixtureState.Document.Parts.OrderedHeadParts.Length == 9 &&
               fixtureState.Document.Tints.Length == 1 &&
               fixtureState.Catalogs.HeadParts[NpcHeadPartType.Hair]
                   .Candidates.Length == 2 &&
               fixtureState.Catalogs.TypedForms.Candidates.Any(item =>
                   item.Signature == new RecordSignature("TXST") &&
                   item.FormId == new FormId(0x804)),
            "The real aggregate face-edit loader did not accept the complete copied fixture: " +
            string.Join(" | ", fixtureLoaded.Diagnostics.Select(item =>
                $"{item.Code}:{item.Message}")));

        var transactionRoot = Path.Combine(
            "K:\\ExampleWorkspace",
            "projects",
            "NpcManagerReimplementation",
            "03-builds",
            "work",
            $"face-edit-source-reference-regression-{Guid.NewGuid():N}");
        Directory.CreateDirectory(transactionRoot);
        try
        {
            var proposalPath = new WorkspacePath(Path.Combine(
                transactionRoot, "face-edit-proposal.json"));
            var outputPath = new WorkspacePath(Path.Combine(
                transactionRoot, "FaceEditFixtureSSE-FaceEdited.esp"));
            SkyrimFaceMorphPatch changedMorphs =
                fixtureState.WriterBaseline.Appearance.FaceMorphs with
                {
                    Nam9Sliders = fixtureState.WriterBaseline.Appearance.FaceMorphs
                        .Nam9Sliders.SetItem(0, 0.25F)
                };
            var request = new NpcAppearanceOverrideRequest(
                GameEdition.SkyrimSpecialEdition,
                fixturePath,
                fixtureHash,
                new FormId(0x800),
                proposalPath,
                outputPath,
                fixtureSource.Race,
                fixtureSource.Sex,
                fixtureState.WriterBaseline.Appearance with
                {
                    FaceMorphs = changedMorphs
                },
                null);
            var overrideService = new NpcAppearanceOverrideService(policy, labRoot);
            NpcAppearanceOverrideProposal proposal = await overrideService.AnalyzeAsync(
                request,
                CancellationToken.None);
            NpcAppearanceOverrideResult applied = await overrideService.ApplyAsync(
                request,
                proposal,
                CancellationToken.None);
            Assert(applied.Applied && applied.Verification is { IsValid: true },
                "The complete fixture appearance override did not write before raw reference inspection: " +
                string.Join(" | ", applied.Diagnostics.Select(item =>
                    $"{item.Code}:{item.Message}")));
            AssertRawFaceReferencesUseSourceMaster(outputPath.Value);
        }
        finally
        {
            if (Directory.Exists(transactionRoot))
                Directory.Delete(transactionRoot, recursive: true);
        }
    }

    private static void AssertRawFaceReferencesUseSourceMaster(string outputPath)
    {
        byte[] bytes = File.ReadAllBytes(outputPath);
        (int Start, int End)? payload = FindRawNpcPayload(
            bytes,
            0,
            bytes.Length,
            0x0000_0800u);
        Assert(payload is not null,
            "The source-owned NPC override was absent from the raw output.");

        var references = new Dictionary<string, List<uint>>(StringComparer.Ordinal);
        int position = payload!.Value.Start;
        while (position < payload.Value.End)
        {
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            int size = BinaryPrimitives.ReadUInt16LittleEndian(
                bytes.AsSpan(position + 4, 2));
            position += 6;
            if (size == 4 && signature is "RNAM" or "PNAM" or "HCLF" or "FTST")
            {
                if (!references.TryGetValue(signature, out List<uint>? values))
                    references[signature] = values = [];
                values.Add(BinaryPrimitives.ReadUInt32LittleEndian(
                    bytes.AsSpan(position, 4)));
            }
            position += size;
        }

        Assert(references.TryGetValue("RNAM", out List<uint>? race) &&
               race.SequenceEqual([0x0000_0801u]) &&
               references.TryGetValue("HCLF", out List<uint>? hairColor) &&
               hairColor.SequenceEqual([0x0000_0802u]) &&
               references.TryGetValue("FTST", out List<uint>? textureSet) &&
               textureSet.SequenceEqual([0x0000_0804u]) &&
               references.TryGetValue("PNAM", out List<uint>? headParts) &&
               headParts.SequenceEqual(Enumerable.Range(0x811, 9)
                   .Select(item => (uint)item)),
            "RNAM/PNAM/HCLF/FTST were not encoded against master index 0: " +
            string.Join(", ", references.Select(pair =>
                $"{pair.Key}=[{string.Join(";", pair.Value.Select(value => $"0x{value:X8}"))}]")));
    }

    private static (int Start, int End)? FindRawNpcPayload(
        byte[] bytes,
        int start,
        int end,
        uint targetFormId)
    {
        int position = start;
        while (position + 8 <= end)
        {
            string signature = Encoding.ASCII.GetString(bytes, position, 4);
            int dataSize = checked((int)BinaryPrimitives.ReadUInt32LittleEndian(
                bytes.AsSpan(position + 4, 4)));
            int recordEnd = signature == "GRUP"
                ? checked(position + dataSize)
                : checked(position + 24 + dataSize);
            if (recordEnd > end)
                throw new InvalidDataException(
                    "A raw face-regression record exceeds its parent boundary.");
            if (signature == "GRUP")
            {
                (int Start, int End)? nested = FindRawNpcPayload(
                    bytes,
                    position + 24,
                    recordEnd,
                    targetFormId);
                if (nested is not null) return nested;
            }
            else if (signature == "NPC_" &&
                     BinaryPrimitives.ReadUInt32LittleEndian(
                         bytes.AsSpan(position + 12, 4)) == targetFormId)
            {
                return (position + 24, recordEnd);
            }
            position = recordEnd;
        }
        return null;
    }

    private sealed class StubFaceEditSourceReader(
        SkyrimFaceEditSourceSnapshot source) : ISkyrimFaceEditSourceReader
    {
        public SkyrimFaceEditSourceSnapshot Read(
            WorkspacePath pluginPath,
            FormId targetFormId) => source;
    }

    private sealed class StubFaceHeadPartLoader(
        SkyrimFaceEditSourceSnapshot source,
        WorkspacePath sourcePluginPath,
        Sha256Hash sourcePluginSha256) : ISkyrimHeadPartEditLoadService
    {
        public ValueTask<SkyrimHeadPartEditLoadResult> LoadAsync(
            SkyrimHeadPartEditLoadRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var snapshot = new NpcFaceSnapshot(
                source.Sex,
                source.Race,
                source.OrderedHeadParts.Select((item, index) =>
                        new NpcHeadPartSelection(
                            item,
                            (NpcHeadPartType)(index + 1)))
                    .ToImmutableArray(),
                source.HairColor);
            return ValueTask.FromResult(new SkyrimHeadPartEditLoadResult(
                true,
                sourcePluginPath,
                sourcePluginSha256,
                snapshot,
                ImmutableDictionary<NpcHeadPartType, SkyrimHeadPartChoiceResult>.Empty,
                []));
        }
    }

    private sealed class StubFaceFormChoiceService(
        FormReference race,
        string raceEditorId) : IFormChoiceService
    {
        public ValueTask<FormChoiceSearchResult> SearchAsync(
            FormChoiceSearchRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var provenance = new FormChoiceProvenance(
                FormChoiceProvenanceKind.Base,
                race.Plugin,
                [race.Plugin]);
            return ValueTask.FromResult(new FormChoiceSearchResult(
                GameEdition.SkyrimSpecialEdition,
                true,
                [new FormChoiceCandidate(
                    race.Plugin,
                    race.FormId,
                    new RecordSignature("RACE"),
                    raceEditorId,
                    null,
                    false,
                    provenance)],
                []));
        }
    }

    private sealed class StubFacePaintChoiceService : ISkyrimRaceMenuPaintChoiceService
    {
        public ValueTask<SkyrimRaceMenuPaintChoiceResult> SearchAsync(
            SkyrimRaceMenuPaintChoiceRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new SkyrimRaceMenuPaintChoiceResult(
                true,
                [],
                null,
                []));
        }
    }
}
