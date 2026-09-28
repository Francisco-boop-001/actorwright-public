using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Drawing;
using System.Reflection;
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
using NpcManager.TestInfrastructure;

namespace NpcManager.Architecture.Tests;

internal static partial class Program
{
    private static async Task TestSkyrimFollowerFinishCoreBinary()
    {
        await using var fixture =
            await SkyrimFollowerFinishCoreFixture.CreateAsync();
        var writer = new BethesdaSkyrimFollowerFinishCoreWriter();
        var verifier = new BethesdaSkyrimFollowerFinishCoreVerifier();

        SkyrimMod output = writer.Write(
            fixture.Source,
            fixture.Proposal,
            fixture.Authority);
        string outputPath = Path.Combine(fixture.Root, "core-output.esp");
        WriteFollowerFinishCorePlugin(output, outputPath);

        Assert(output.IsSmallMaster,
            "TES4 ESL flag was not set.");
        Assert(output.Colors.Single().Color ==
               Color.FromArgb(255, 214, 190, 131),
            "CLFM did not become the proposal RGB.");
        Assert(output.Npcs.Single().Packages.Single().FormKey.ID == 0x805,
            "NPC PKID does not point to the proposal PACK allocation.");

        Package package = output.Packages.Single();
        PackageDataLocation location = package.Data.Values
            .OfType<PackageDataLocation>()
            .Single();
        var locationTarget = location.Location.Target as LocationTarget;
        var condition = package.Conditions.Single() as ConditionFloat;
        var conditionData =
            condition?.Data as GetFactionRankConditionData;
        Assert(location.Location.Radius == 768 &&
               locationTarget?.Link.FormKey.ID == 0x806,
            "Sandbox radius or local marker target differs from the proposal.");
        Assert(condition is not null &&
               condition.CompareOperator == CompareOperator.LessThan &&
               condition.ComparisonValue == 0f &&
               conditionData is not null &&
               conditionData.Faction.Link.FormKey ==
               new FormKey(
                   ModKey.FromNameAndExtension("Skyrim.esm"),
                   0x5C84E),
            "The package does not contain the one proposal-bound CurrentFollowerFaction < 0 condition.");
        Assert(package.VirtualMachineAdapter is null &&
               package.OwnerQuest.FormKeyNullable is null &&
               IsCanonicalFollowerFinishEvent(package.OnBegin) &&
               IsCanonicalFollowerFinishEvent(package.OnChange) &&
               IsCanonicalFollowerFinishEvent(package.OnEnd),
            "The admitted package gained VMAD, owner, or noncanonical event content.");
        Assert(package.ScheduleMonth ==
               fixture.Template.ScheduleMonth &&
               package.ScheduleDayOfWeek ==
               fixture.Template.ScheduleDayOfWeek &&
               package.ScheduleDate ==
               fixture.Template.ScheduleDate &&
               package.ScheduleHour ==
               fixture.Template.ScheduleHour &&
               package.ScheduleMinute ==
               fixture.Template.ScheduleMinute &&
               package.ScheduleDurationInMinutes ==
               fixture.Template
                   .ScheduleDurationInMinutes,
            "The continuous admitted schedule was not preserved.");

        BethesdaSkyrimFollowerFinishCoreVerification verified =
            verifier.Verify(
                new WorkspacePath(fixture.SourcePath),
                new WorkspacePath(outputPath),
                fixture.Proposal,
                fixture.Authority);
        Assert(verified.Verified &&
               verified.Diagnostics.All(diagnostic =>
                   diagnostic.Severity != DiagnosticSeverity.Error),
            "The core verifier rejected the admitted closed binary delta: " +
            string.Join(
                " | ",
                verified.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));

        Assert(
            typeof(BethesdaSkyrimFollowerFinishSandboxAuthority)
                .GetConstructors(BindingFlags.Public |
                                 BindingFlags.Instance)
                .Length == 0 &&
            typeof(BethesdaSkyrimFollowerFinishSandboxAuthority)
                .GetProperties(BindingFlags.Public |
                               BindingFlags.Instance)
                .All(property =>
                    property.SetMethod is null &&
                    property.PropertyType != typeof(Package)),
            "Sandbox authority exposes caller construction, mutation, or a mutable Package.");
        MethodInfo[] publicAdmissionMethods =
            typeof(BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission)
                .GetMethods(BindingFlags.Public |
                            BindingFlags.Instance |
                            BindingFlags.DeclaredOnly)
                .Where(method => method.Name == "Admit")
                .ToArray();
        Assert(
            publicAdmissionMethods.Length == 1 &&
            publicAdmissionMethods[0].GetParameters()
                .Select(parameter => parameter.ParameterType)
                .SequenceEqual(
                    new[] { typeof(WorkspacePath), typeof(Sha256Hash) }),
            "Production admission exposes caller selection of template identity or raw digest.");
        AssertAdmissionRefusal(
            fixture,
            new Sha256Hash(new string('B', 64)),
            fixture.TemplatePath,
            "follower-finish-core-template-file-hash");

        string arbitraryDirectory = Path.Combine(
            fixture.Root,
            "arbitrary-template");
        Directory.CreateDirectory(arbitraryDirectory);
        string arbitraryPath = Path.Combine(
            arbitraryDirectory,
            "Skyrim.esm");
        WriteFollowerFinishCorePlugin(
            SkyrimFollowerFinishCoreFixture.CreateTemplateMaster(
                editorId: "CallerSelectedTemplate"),
            arbitraryPath);
        AssertAdmissionRefusal(
            fixture,
            SkyrimFollowerFinishCoreFixture
                .HashFollowerFinishCoreFile(arbitraryPath),
            arbitraryPath,
            "follower-finish-core-template-editor-id");

        string travelDirectory = Path.Combine(
            fixture.Root,
            "travel-template");
        Directory.CreateDirectory(travelDirectory);
        string travelPath = Path.Combine(
            travelDirectory,
            "Skyrim.esm");
        SkyrimMod travelMaster =
            SkyrimFollowerFinishCoreFixture.CreateTemplateMaster(
                procedureType: "Travel");
        string travelProcedure = travelMaster.Packages.Single()
            .ProcedureTree.Single()
            .ProcedureType ??
            throw new InvalidDataException(
                "The Travel hostile lost its procedure type.");
        SkyrimFollowerFinishRequest matchingTravelRequest =
            fixture.Request with
            {
                Sandbox = fixture.Request.Sandbox with
                {
                    Procedure = travelProcedure
                }
            };
        Assert(
            matchingTravelRequest.Sandbox.Procedure == "Travel" &&
            travelMaster.Packages.Single()
                .Data.Values.OfType<PackageDataLocation>()
                .Count() == 1,
            "The Travel hostile does not match its continuous one-location request.");
        WriteFollowerFinishCorePlugin(
            travelMaster,
            travelPath);
        AssertAdmissionRefusal(
            fixture,
            SkyrimFollowerFinishCoreFixture
                .HashFollowerFinishCoreFile(travelPath),
            travelPath,
            "follower-finish-core-template-procedure");

        SkyrimMod substitutedMaster =
            SkyrimFollowerFinishCoreFixture.CreateTemplateMaster(
            scheduleHour: 7);
        WriteFollowerFinishCorePlugin(
            substitutedMaster,
            fixture.TemplatePath);
        SkyrimMod frozenOutput = writer.Write(
            fixture.Source,
            fixture.Proposal,
            fixture.Authority);
        Assert(frozenOutput.Packages.Single().ScheduleHour ==
               fixture.Template.ScheduleHour,
            "Post-admission copied-master substitution changed the frozen authority.");

        AssertCoreRefusal(
            () =>
            {
                SkyrimMod hostile = fixture.CopySource();
                hostile.Colors.Single().Color =
                    Color.FromArgb(255, 1, 2, 3);
                writer.Write(
                    hostile,
                    fixture.Proposal,
                    fixture.Authority);
            },
            "follower-finish-core-old-rgb");
        AssertCoreRefusal(
            () =>
            {
                SkyrimMod hostile = fixture.CopySource();
                hostile.Npcs.Single().HairColor.SetTo(
                    new FormKey(hostile.ModKey, 0x802));
                writer.Write(
                    hostile,
                    fixture.Proposal,
                    fixture.Authority);
            },
            "follower-finish-core-hclf");
        AssertCoreRefusal(
            () => writer.Write(
                fixture.Source,
                WithFollowerFinishRequest(
                    fixture.Proposal,
                    fixture.Request with
                    {
                        NpcFormId = new FormId(0x809)
                    }),
                fixture.Authority),
            "follower-finish-core-npc-form-id");
        AssertCoreRefusal(
            () => writer.Write(
                fixture.Source,
                WithFollowerFinishRequest(
                    fixture.Proposal,
                    fixture.Request with
                    {
                        OccupiedLocalFormIds =
                        [
                            new FormId(0x800),
                            new FormId(0x801),
                            new FormId(0x802),
                            new FormId(0x803),
                            new FormId(0x809)
                        ]
                    }),
                fixture.Authority),
            "follower-finish-core-occupied-ids");
        AssertCoreRefusal(
            () =>
            {
                SkyrimMod hostile = fixture.CopySource();
                hostile.Npcs.Single().Factions[0].Rank = 2;
                writer.Write(
                    hostile,
                    fixture.Proposal,
                    fixture.Authority);
            },
            "follower-finish-core-factions");
        AssertCoreRefusal(
            () =>
            {
                SkyrimMod hostile = fixture.CopySource();
                hostile.Relationships.Single().Rank =
                    Relationship.RankType.Friend;
                writer.Write(
                    hostile,
                    fixture.Proposal,
                    fixture.Authority);
            },
            "follower-finish-core-relationship-rank");
        AssertCoreRefusal(
            () =>
            {
                SkyrimMod hostile = fixture.CopySource();
                hostile.Npcs.Single().DefaultOutfit.SetTo(
                    new FormKey(
                        ModKey.FromNameAndExtension("Skyrim.esm"),
                        0x10));
                writer.Write(
                    hostile,
                    fixture.Proposal,
                    fixture.Authority);
            },
            "follower-finish-core-outfit");
        AssertCoreRefusal(
            () => writer.Write(
                fixture.Source,
                WithSandbox(
                    fixture,
                    fixture.Request.Sandbox with
                    {
                        Procedure = "hostile-procedure"
                    }),
                fixture.Authority),
            "follower-finish-core-procedure");
        AssertCoreRefusal(
            () =>
            {
                SkyrimMod hostile = fixture.CopySource();
                hostile.IsSmallMaster = true;
                writer.Write(
                    hostile,
                    fixture.Proposal,
                    fixture.Authority);
            },
            "follower-finish-core-source-light");
        AssertCoreRefusal(
            () =>
            {
                SkyrimMod hostile = fixture.CopySource();
                hostile.ModHeader.Stats.NextFormID = 0x806;
                writer.Write(
                    hostile,
                    fixture.Proposal,
                    fixture.Authority);
            },
            "follower-finish-core-next-form-id");

        string noncontinuousDirectory = Path.Combine(
            fixture.Root,
            "noncontinuous-template");
        Directory.CreateDirectory(noncontinuousDirectory);
        string noncontinuousPath = Path.Combine(
            noncontinuousDirectory,
            "Skyrim.esm");
        SkyrimFollowerFinishCoreFixture
            .WriteCanonicalFollowerFinishTemplateVariant(
                noncontinuousPath,
                scheduleHour: 1);
        AssertAdmissionRefusal(
            fixture,
            SkyrimFollowerFinishCoreFixture
                .HashFollowerFinishCoreFile(noncontinuousPath),
            noncontinuousPath,
            "follower-finish-core-template-schedule");

        RunFollowerFinishVerifierHostiles(
            fixture,
            verifier,
            output);
    }

    private static void AssertAdmissionRefusal(
        SkyrimFollowerFinishCoreFixture fixture,
        Sha256Hash expectedFileHash,
        string path,
        string expectedCode)
    {
        try
        {
            fixture.Admission.Admit(
                new WorkspacePath(path),
                expectedFileHash);
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains(
                expectedCode,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected authority admission refusal '{expectedCode}'.");
    }

    private static void RunFollowerFinishVerifierHostiles(
        SkyrimFollowerFinishCoreFixture fixture,
        BethesdaSkyrimFollowerFinishCoreVerifier verifier,
        SkyrimMod valid)
    {
        var cases = new (
            string Name,
            string Code,
            Action<SkyrimMod> Mutate)[]
        {
            (
                "TES4 ESL flag",
                "follower-finish-core-verify-esl",
                mod => mod.IsSmallMaster = false),
            (
                "TES4 NextFormID",
                "follower-finish-core-verify-next-form-id",
                mod => mod.ModHeader.Stats.NextFormID = 0x809),
            (
                "CLFM RGB",
                "follower-finish-core-verify-color",
                mod => mod.Colors.Single().Color =
                    Color.FromArgb(255, 1, 2, 3)),
            (
                "NPC PKID",
                "follower-finish-core-verify-pkid",
                mod => mod.Npcs.Single().Packages.Clear()),
            (
                "duplicate NPC PKID",
                "follower-finish-core-verify-pkid",
                mod => mod.Npcs.Single().Packages.Add(
                    mod.Npcs.Single().Packages[0])),
            (
                "PACK procedure",
                "follower-finish-core-verify-procedure",
                mod => mod.Packages.Single()
                    .PackageTemplate.SetTo(
                        new FormKey(mod.ModKey, 0x805))),
            (
                "PACK schedule",
                "follower-finish-core-verify-schedule",
                mod => mod.Packages.Single().ScheduleHour = 7),
            (
                "PACK radius",
                "follower-finish-core-verify-location",
                mod => FollowerFinishLocation(mod).Location.Radius++),
            (
                "PACK marker",
                "follower-finish-core-verify-location",
                mod => ((LocationTarget)FollowerFinishLocation(mod)
                        .Location.Target)
                    .Link.SetTo(new FormKey(mod.ModKey, 0x807))),
            (
                "PACK condition",
                "follower-finish-core-verify-condition",
                mod => ((ConditionFloat)mod.Packages.Single()
                    .Conditions.Single()).ComparisonValue = 1f),
            (
                "PACK VMAD",
                "follower-finish-core-verify-package-shape",
                mod => mod.Packages.Single()
                    .VirtualMachineAdapter = new PackageAdapter()),
            (
                "PACK owner",
                "follower-finish-core-verify-package-shape",
                mod => mod.Packages.Single().OwnerQuest.SetTo(
                    new FormKey(
                        ModKey.FromNameAndExtension("Skyrim.esm"),
                        0x123))),
            (
                "PACK fragment",
                "follower-finish-core-verify-package-shape",
                mod => mod.Packages.Single().OnBegin =
                    new PackageEvent()),
            (
                "duplicate PACK condition",
                "follower-finish-core-verify-condition-count",
                mod => mod.Packages.Single().Conditions.Add(
                    mod.Packages.Single().Conditions[0].DeepCopy())),
            (
                "duplicate PACK location",
                "follower-finish-core-verify-location-count",
                mod => mod.Packages.Single().Data[1] =
                    mod.Packages.Single().Data[0].DeepCopy()),
            (
                "TXST bytes",
                "follower-finish-core-verify-preservation",
                mod => mod.TextureSets.Single().Diffuse =
                    "textures/hostile.dds"),
            (
                "HDPT bytes",
                "follower-finish-core-verify-preservation",
                mod => mod.HeadParts.Single().EditorID =
                    "HostileHeadPart"),
            (
                "RELA bytes",
                "follower-finish-core-verify-preservation",
                mod => mod.Relationships.Single().EditorID =
                    "HostileRelationship")
        };

        foreach ((string name, string code, Action<SkyrimMod> mutate)
                 in cases)
        {
            SkyrimMod hostile = (SkyrimMod)valid.DeepCopy();
            mutate(hostile);
            string path = Path.Combine(
                fixture.Root,
                "verifier-" +
                name.Replace(' ', '-').ToLowerInvariant() +
                ".esp");
            WriteFollowerFinishCorePlugin(hostile, path);
            AssertVerifierRefusal(
                verifier,
                fixture,
                path,
                code,
                name);
        }

        string headerPath = Path.Combine(
            fixture.Root,
            "verifier-record-header.esp");
        WriteFollowerFinishCorePlugin(
            (SkyrimMod)valid.DeepCopy(),
            headerPath);
        TamperFollowerFinishRecordHeader(
            headerPath,
            "TXST",
            0x802);
        AssertVerifierRefusal(
            verifier,
            fixture,
            headerPath,
            "follower-finish-core-verify-record-header-drift",
            "record header");
    }

    private static PackageDataLocation FollowerFinishLocation(
        SkyrimMod mod) =>
        mod.Packages.Single().Data.Values
            .OfType<PackageDataLocation>()
            .Single();

    private static bool IsCanonicalFollowerFinishEvent(
        PackageEvent? packageEvent) =>
        packageEvent is not null &&
        packageEvent.Topics.Count == 1 &&
        packageEvent.Topics[0] is TopicReference topic &&
        topic.Reference.FormKey.IsNull &&
        packageEvent.Idle.IsNull &&
        packageEvent.SCHR is null &&
        packageEvent.SCDA is null &&
        packageEvent.SCTX is null &&
        packageEvent.QNAM is null &&
        packageEvent.TNAM is null;

    private static void AssertVerifierRefusal(
        BethesdaSkyrimFollowerFinishCoreVerifier verifier,
        SkyrimFollowerFinishCoreFixture fixture,
        string outputPath,
        string expectedCode,
        string name)
    {
        BethesdaSkyrimFollowerFinishCoreVerification result =
            verifier.Verify(
                new WorkspacePath(fixture.SourcePath),
                new WorkspacePath(outputPath),
                fixture.Proposal,
                fixture.Authority);
        Assert(
            !result.Verified &&
            result.Diagnostics.Any(diagnostic =>
                diagnostic.Code == expectedCode &&
                diagnostic.Severity == DiagnosticSeverity.Error),
            $"Verifier accepted hostile {name}, or omitted {expectedCode}: " +
            string.Join(
                " | ",
                result.Diagnostics.Select(diagnostic =>
                    $"{diagnostic.Code}: {diagnostic.Message}")));
    }

    private static void TamperFollowerFinishRecordHeader(
        string path,
        string signature,
        uint localFormId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        int record = FindFollowerFinishRawRecord(
            bytes,
            0,
            bytes.Length,
            signature,
            localFormId);
        bytes[record + 16] ^= 0x01;
        File.WriteAllBytes(path, bytes);
    }

    private static Sha256Hash HashFollowerFinishCoreRawRecord(
        string path,
        string signature,
        uint localFormId)
    {
        byte[] bytes = File.ReadAllBytes(path);
        (int offset, int length, string groupPath) =
            FindFollowerFinishRawRecordInfo(
                bytes,
                0,
                bytes.Length,
                string.Empty,
                signature,
                localFormId);
        byte[] prefix = Encoding.UTF8.GetBytes(groupPath + "\n");
        byte[] input = new byte[prefix.Length + length];
        prefix.CopyTo(input, 0);
        bytes.AsSpan(offset, length).CopyTo(
            input.AsSpan(prefix.Length));
        return new Sha256Hash(
            Convert.ToHexString(SHA256.HashData(input)));
    }

    private static int FindFollowerFinishRawRecord(
        byte[] bytes,
        int start,
        int end,
        string signature,
        uint localFormId) =>
        FindFollowerFinishRawRecordInfo(
            bytes,
            start,
            end,
            string.Empty,
            signature,
            localFormId).Offset;

    private static (
        int Offset,
        int Length,
        string GroupPath) FindFollowerFinishRawRecordInfo(
        byte[] bytes,
        int start,
        int end,
        string groupPath,
        string wantedSignature,
        uint localFormId)
    {
        RawPluginRecordInfo? match = SkyrimFinishMasterFixture.FindRawRecordInfo(
            bytes, start, end, groupPath, wantedSignature, localFormId,
            out _);
        return match is { } record
            ? (record.Offset, record.Length, record.GroupPath)
            : throw new InvalidDataException(
                $"Raw {wantedSignature} 0x{localFormId:X8} is missing.");
    }

    private static SkyrimFollowerFinishProposal WithSandbox(
        SkyrimFollowerFinishCoreFixture fixture,
        SkyrimFollowerFinishSandbox sandbox) =>
        WithFollowerFinishRequest(
            fixture.Proposal,
            fixture.Request with { Sandbox = sandbox });

    private static SkyrimFollowerFinishProposal WithFollowerFinishRequest(
        SkyrimFollowerFinishProposal proposal,
        SkyrimFollowerFinishRequest request) =>
        proposal with { Request = request };

    private static void AssertCoreRefusal(
        Action action,
        string expectedCode)
    {
        try
        {
            action();
        }
        catch (InvalidDataException exception) when (
            exception.Message.Contains(
                expectedCode,
                StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Expected core refusal '{expectedCode}'.");
    }

    private static void WriteFollowerFinishCorePlugin(
        SkyrimMod mod,
        string path) =>
        mod.WriteToBinary(
            new FilePath(path),
            new BinaryWriteParameters
            {
                ModKey = ModKeyOption.NoCheck,
                MastersListContent =
                    MastersListContentOption.NoCheck,
                MastersListOrdering =
                    MastersListOrderingOption.NoCheck,
                NextFormID = NextFormIDOption.NoCheck
            });

    private sealed class SkyrimFollowerFinishCoreFixture :
        IAsyncDisposable
    {
        private SkyrimFollowerFinishCoreFixture(
            string root,
            string sourcePath,
            string templatePath,
            SkyrimMod source,
            Package template,
            SkyrimFollowerFinishRequest request,
            SkyrimFollowerFinishProposal proposal,
            BethesdaSkyrimFollowerFinishSandboxAuthority authority,
            BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission
                admission,
            Sha256Hash templateFileHash)
        {
            Root = root;
            SourcePath = sourcePath;
            TemplatePath = templatePath;
            Source = source;
            Template = template;
            Request = request;
            Proposal = proposal;
            Authority = authority;
            Admission = admission;
            TemplateFileHash = templateFileHash;
        }

        public string Root { get; }

        public string SourcePath { get; }

        public string TemplatePath { get; }

        public SkyrimMod Source { get; }

        public Package Template { get; }

        public SkyrimFollowerFinishRequest Request { get; }

        public SkyrimFollowerFinishProposal Proposal { get; }

        public BethesdaSkyrimFollowerFinishSandboxAuthority Authority
        {
            get;
        }

        public BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission
            Admission { get; }

        public Sha256Hash TemplateFileHash { get; }

        public SkyrimMod CopySource() =>
            (SkyrimMod)Source.DeepCopy();

        public static async Task<SkyrimFollowerFinishCoreFixture>
            CreateAsync()
        {
            SkyrimFinishMasterFixture.EnsureAvailable();
            string root = Path.Combine(
                Environment.CurrentDirectory,
                "artifacts",
                "test-work",
                "follower-finish-core-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            string sourcePath = Path.Combine(root, "CoreFixture.esp");
            SkyrimMod source = CreateSource();
            WriteFollowerFinishCorePlugin(source, sourcePath);
            Sha256Hash sourceHash = HashFollowerFinishCoreFile(
                sourcePath);
            string templatePath = Path.Combine(root, "Skyrim.esm");
            WriteCanonicalFollowerFinishTemplateMaster(templatePath);
            Package template;
            using (var canonicalOverlay =
                   SkyrimMod.CreateFromBinaryOverlay(
                       new ModPath(
                           ModKey.FromNameAndExtension("Skyrim.esm"),
                           new FilePath(templatePath)),
                       SkyrimRelease.SkyrimSE))
            {
                IPackageGetter canonicalGetter =
                    canonicalOverlay.Packages.Single(package =>
                        package.FormKey.ID == 0x1B217);
                template = canonicalGetter.DeepCopy();
            }
            Sha256Hash templateFileHash =
                HashFollowerFinishCoreFile(templatePath);
            Sha256Hash templateRawDigest =
                HashFollowerFinishCoreRawRecord(
                    templatePath,
                    "PACK",
                    0x1B217);
            Assert(
                templateRawDigest ==
                new Sha256Hash(
                    "fba3cca0eff98528da3985962ff9058ee" +
                    "7662ea944c250f3ffb90a0490d52685"),
                "The supplied canonical sandbox PACK digest drifted.");

            var plugin = new PluginName("CoreFixture.esp");
            var sandbox = new SkyrimFollowerFinishSandbox(
                "Sandbox",
                768,
                "continuous",
                new FormReference(plugin, new FormId(0x806)),
                "GetFactionRank(Skyrim.esm|0x0005C84E) < 0");
            var request = new SkyrimFollowerFinishRequest(
                1,
                SkyrimFollowerFinishRequest.OperationName,
                new SkyrimFollowerFinishSourceAuthority(
                    new WorkspacePath(Path.Combine(root, "source.zip")),
                    4,
                    new Sha256Hash(new string('1', 64)),
                    new WorkspacePath(Path.Combine(
                        root,
                        "npcmanager-package.json")),
                    new Sha256Hash(new string('2', 64)),
                    plugin,
                    sourceHash,
                    new Sha256Hash(new string('3', 64)),
                    new Sha256Hash(new string('4', 64))),
                new EditorId("CoreFixtureNpc"),
                new FormId(0x800),
                [
                    new FormId(0x800),
                    new FormId(0x801),
                    new FormId(0x802),
                    new FormId(0x803),
                    new FormId(0x804)
                ],
                new FormReference(
                    new PluginName("Skyrim.esm"),
                    new FormId(0x13746)),
                "synthetic-body-route",
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
                new FormId(0x804),
                "Ally",
                1,
                new SkyrimFollowerFinishHairChange(
                    new FormId(0x801),
                    new SkyrimPackedRgb(0x94876A),
                    new SkyrimPackedRgb(0xD6BE83)),
                true,
                false,
                sandbox,
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
                    new SkyrimExteriorTransform(
                        1, 2, 3, 0, 0, 90),
                    new SkyrimExteriorTransform(
                        4, 5, 6, 0, 0, 0)),
                new SkyrimFollowerFinishAllocation(
                    new FormId(0x805),
                    new FormId(0x806),
                    new FormId(0x807),
                    new FormId(0x808)),
                [
                    "PACK 0x00000805",
                    "REFR 0x00000806",
                    "ACHR 0x00000807"
                ],
                [
                    "TES4: set ESL flag and mechanical header metadata",
                    "CLFM 0x00000801: 0x94876A -> 0xD6BE83",
                    "NPC_ 0x00000800: add PKID 0x00000805"
                ],
                [
                    new AssetPath("Data/CoreFixture.esp"),
                    new AssetPath(
                        "Data/meshes/actors/character/facegendata/" +
                        "facegeom/CoreFixture.esp/00000800.nif"),
                    new AssetPath(
                        "Data/textures/actors/character/facegendata/" +
                        "facetint/CoreFixture.esp/00000800.dds"),
                    new AssetPath("npcmanager-package.json")
                ],
                new WorkspacePath(Path.Combine(root, "candidate")),
                new WorkspacePath(Path.Combine(
                    root,
                    "candidate.zip")),
                "A synthetic follower-finish core test.");
            SkyrimFollowerFinishPluginSnapshot snapshot =
                await new BethesdaSkyrimFollowerFinishSourceReader()
                    .InspectAsync(
                        request,
                        new WorkspacePath(sourcePath),
                        CancellationToken.None);
            Assert(snapshot.Valid,
                "Synthetic follower source was not admitted: " +
                string.Join(
                    " | ",
                    snapshot.Diagnostics.Select(diagnostic =>
                        $"{diagnostic.Code}: {diagnostic.Message}")));
            var proposal = new SkyrimFollowerFinishProposal(
                1,
                SkyrimFollowerFinishRequest.OperationName,
                new Sha256Hash(new string('5', 64)),
                request,
                snapshot,
                request.AllowedExistingRecordChanges,
                request.AllowedNewRecords,
                request.Allocation.NextFormId,
                ["TES4", "CLFM", "NPC_", "PACK"],
                request.AllowedPackageFiles,
                false);

            var admission =
                new BethesdaSkyrimFollowerFinishSandboxAuthorityAdmission(
                    new KOnlyWorkspacePolicy(
                        new WorkspacePath(Path.GetPathRoot(root)!),
                        new WorkspacePath(Path.Combine(
                            Path.GetDirectoryName(root)!,
                            "protected-" + Guid.NewGuid().ToString("N")))),
                    new WorkspacePath(root));
            BethesdaSkyrimFollowerFinishSandboxAuthority authority =
                admission.Admit(
                    new WorkspacePath(templatePath),
                    templateFileHash);
            return new SkyrimFollowerFinishCoreFixture(
                root,
                sourcePath,
                templatePath,
                source,
                template,
                request,
                proposal,
                authority,
                admission,
                templateFileHash);
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static SkyrimMod CreateSource()
        {
            ModKey plugin =
                ModKey.FromNameAndExtension("CoreFixture.esp");
            ModKey skyrim =
                ModKey.FromNameAndExtension("Skyrim.esm");
            var mod = new SkyrimMod(
                plugin,
                SkyrimRelease.SkyrimSE);
            mod.ModHeader.MasterReferences.Add(
                new MasterReference { Master = skyrim });
            mod.ModHeader.Stats.NextFormID = 0x805;

            var npcKey = new FormKey(plugin, 0x800);
            var colorKey = new FormKey(plugin, 0x801);
            var textureKey = new FormKey(plugin, 0x802);
            var headPartKey = new FormKey(plugin, 0x803);
            var npc = new Npc(npcKey, SkyrimRelease.SkyrimSE)
            {
                EditorID = "CoreFixtureNpc",
                Name = "Unrelated bytes must survive",
                Race = new FormLink<IRaceGetter>(
                    new FormKey(skyrim, 0x13746)),
                HairColor =
                    new FormLinkNullable<IColorRecordGetter>(
                        colorKey),
                Configuration = new NpcConfiguration
                {
                    Flags = NpcConfiguration.Flag.Female,
                    HealthOffset = 23
                },
                Weight = 61.5f
            };
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
            npc.HeadParts.Add(new FormLink<IHeadPartGetter>(
                headPartKey));
            mod.Npcs.Add(npc);
            mod.Colors.Add(new ColorRecord(
                colorKey,
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "CoreFixtureHairColor",
                Color = Color.FromArgb(255, 148, 135, 106),
                Playable = true
            });
            mod.TextureSets.Add(new TextureSet(
                textureKey,
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "CoreFixtureFaceTexture",
                Diffuse =
                    "textures/core-fixture-face-diffuse.dds"
            });
            mod.HeadParts.Add(new HeadPart(
                headPartKey,
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "CoreFixtureFace",
                Name = "Core Fixture Face",
                Type = HeadPart.TypeEnum.Face,
                TextureSet =
                    new FormLinkNullable<ITextureSetGetter>(
                        textureKey)
            });
            mod.Relationships.Add(new Relationship(
                new FormKey(plugin, 0x804),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = "CoreFixtureRelationship",
                Parent = new FormLink<INpcGetter>(npcKey),
                Child = new FormLink<INpcGetter>(
                    new FormKey(skyrim, 0x7)),
                Rank = Relationship.RankType.Ally,
                AssociationType =
                    new FormLink<IAssociationTypeGetter>(
                        FormKey.Null)
            });
            return mod;
        }

        internal static SkyrimMod CreateTemplateMaster(
            sbyte scheduleHour = -1,
            string modName = "Skyrim.esm",
            string editorId =
                "DefaultSandboxEditorLocation512",
            string procedureType = "Sandbox")
        {
            ModKey skyrim =
                ModKey.FromNameAndExtension(modName);
            var mod = new SkyrimMod(
                skyrim,
                SkyrimRelease.SkyrimSE);
            var template = new Package(
                new FormKey(skyrim, 0x1B217),
                SkyrimRelease.SkyrimSE)
            {
                EditorID = editorId,
                ScheduleMonth = -1,
                ScheduleDayOfWeek = Package.DayOfWeek.Any,
                ScheduleDate = 0,
                ScheduleHour = scheduleHour,
                ScheduleMinute = -1,
                ScheduleDurationInMinutes = 0
            };
            template.ProcedureTree.Add(new PackageBranch
            {
                BranchType = "Procedure",
                ProcedureType = procedureType,
                DataInputIndices = [0]
            });
            template.Data[0] = new PackageDataLocation
            {
                Location = new LocationTargetRadius
                {
                    Target = new LocationTarget
                    {
                        Link = new FormLink<IPlacedGetter>(
                            new FormKey(skyrim, 0x1BDF3))
                    },
                    Radius = 256
                }
            };
            mod.Packages.Add(template);
            mod.ModHeader.Stats.NextFormID = 0x1B218;
            return mod;
        }

        internal static void WriteCanonicalFollowerFinishTemplateMaster(
            string path)
        {
            WriteCanonicalFollowerFinishTemplateMaster(
                path,
                SkyrimFinishMasterFixture.ReadCanonicalPackRecord());
        }

        internal static void WriteCanonicalFollowerFinishTemplateMaster(
            string path,
            byte[] canonicalRecord)
        {
            WriteFollowerFinishCorePlugin(
                CreateTemplateMaster(),
                path);
            byte[] pluginBytes = File.ReadAllBytes(path);
            RawPluginRecordInfo record =
                SkyrimFinishMasterFixture.FindRawRecordInfo(
                    pluginBytes,
                    0,
                    pluginBytes.Length,
                    string.Empty,
                    "PACK",
                    0x1B217,
                    out _) ?? throw new InvalidDataException(
                        "Synthetic template envelope has no PACK 0x1B217 record.");
            int groupStart = record.Offset - 24;
            if (record.GroupPath != "PACK:00000000" ||
                groupStart < 0 ||
                Encoding.ASCII.GetString(pluginBytes, groupStart, 4) != "GRUP" ||
                Encoding.ASCII.GetString(pluginBytes, groupStart + 8, 4) != "PACK" ||
                BinaryPrimitives.ReadUInt32LittleEndian(
                    pluginBytes.AsSpan(groupStart + 4, 4)) !=
                    checked((uint)(24 + record.Length)))
                throw new InvalidDataException(
                    "Synthetic template envelope has no top-level PACK group.");
            byte[] rewritten = new byte[
                pluginBytes.Length -
                record.Length +
                canonicalRecord.Length];
            pluginBytes.AsSpan(0, record.Offset).CopyTo(rewritten);
            canonicalRecord.CopyTo(rewritten, record.Offset);
            pluginBytes.AsSpan(record.Offset + record.Length)
                .CopyTo(rewritten.AsSpan(
                    record.Offset + canonicalRecord.Length));
            BinaryPrimitives.WriteUInt32LittleEndian(
                rewritten.AsSpan(groupStart + 4, 4),
                checked((uint)(24 + canonicalRecord.Length)));
            File.WriteAllBytes(path, rewritten);
        }

        internal static void
            WriteCanonicalFollowerFinishTemplateVariant(
                string path,
                sbyte scheduleHour)
        {
            string canonicalPath = path + ".canonical";
            WriteCanonicalFollowerFinishTemplateMaster(
                canonicalPath);
            Package template;
            using (var canonicalOverlay =
                   SkyrimMod.CreateFromBinaryOverlay(
                       new ModPath(
                           ModKey.FromNameAndExtension("Skyrim.esm"),
                           new FilePath(canonicalPath)),
                       SkyrimRelease.SkyrimSE))
            {
                template = canonicalOverlay.Packages.Single(
                        package => package.FormKey.ID == 0x1B217)
                    .DeepCopy();
            }

            template.ScheduleHour = scheduleHour;
            var variant = new SkyrimMod(
                ModKey.FromNameAndExtension("Skyrim.esm"),
                SkyrimRelease.SkyrimSE);
            variant.Packages.Add(template);
            variant.ModHeader.Stats.NextFormID = 0x1B218;
            WriteFollowerFinishCorePlugin(
                variant,
                path);
        }

        internal static Sha256Hash HashFollowerFinishCoreFile(
            string path)
        {
            using var stream = File.OpenRead(path);
            return new Sha256Hash(
                Convert.ToHexString(SHA256.HashData(stream)));
        }
    }
}
