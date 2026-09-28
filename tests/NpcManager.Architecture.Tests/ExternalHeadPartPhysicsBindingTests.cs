using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using NpcManager.Application;
using NpcManager.Domain;
using NpcManager.Infrastructure;

namespace NpcManager.Architecture.Tests;

internal sealed class Preview254ExternalSmpPhysicsScenario :
    IPreview254ExternalSmpArchitectureScenario
{
    public string Selector => "--test-external-headpart-physics-binding";

    public async ValueTask RunAsync(CancellationToken cancellationToken)
    {
        await ExternalHeadPartPhysicsBindingTests.RunAsync(cancellationToken);
    }
}

internal static class ExternalHeadPartPhysicsBindingTests
{
    private static readonly PluginName ProviderPlugin =
        new("OrchidAdornment.esp");
    private static readonly ExternalHeadPartProviderIdentity Provider =
        new(ProviderPlugin, Hash("provider-plugin"), 256,
            ExternalHeadPartRedistributionMode.ExternalProviderRequired);
    private static readonly WorkspacePath LabRoot =
        new(FindRepositoryRoot());
    private static readonly WorkspacePath ProtectedRoot =
        new(@"F:\ExampleGame");

    public static async Task RunAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var policy = new KOnlyWorkspacePolicy(LabRoot, ProtectedRoot);
        var resolver = new ExternalHeadPartPhysicsBindingResolver(policy, LabRoot);
        var factory = new Preview254ExternalSmpPhysicsFixtureFactory(LabRoot);

        await AssertFixtureRejectsPreplantedReparseAsync(cancellationToken);
        AssertGenericProviderNifPathHardening();
        AssertLegacyTextureDependencyCardinality();
        await AssertParentReparseBetweenReadsAsync(cancellationToken);

        string scratch = Path.Combine(
            LabRoot.Value,
            "artifacts",
            "external-smp-physics-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            await AssertRootSpellingsAndChainInheritanceAsync(resolver, factory, scratch, cancellationToken);
            FormReference memberForm = new(ProviderPlugin, new FormId(0x800));
            AssetPath model = new(
                "meshes/actors/character/character assets/hair/direct.nif");
            ExternalHeadPartRecordDependency member = CreateMember(
                memberForm, model, 0);

            Preview254ExternalSmpPhysicsFixture directFixture =
                await factory.CreateAsync(
                    new WorkspacePath(Path.Combine(scratch, "direct")),
                    memberForm,
                    model,
                    Preview254ExternalSmpPhysicsFixtureMode.DirectLocator,
                    cancellationToken);
            ExternalHeadPartPhysicsBindingResult direct =
                await ResolveAsync(resolver, directFixture, [member],
                    cancellationToken);
            Require(direct.Accepted && direct.Binding is not null,
                "Direct NIF physics binding was refused: " +
                string.Join("; ", direct.Diagnostics.Select(item =>
                    item.Code + "=" + item.Message)));
            ExternalHeadPartPhysicsBinding directBinding = direct.Binding!;
            Require(directBinding.Mode ==
                    ExternalHeadPartPhysicsBindingMode.DirectNifExtraData &&
                directBinding.MappingAuthority is null &&
                directBinding.Shapes.Length == 2,
                "Direct NIF binding did not preserve mode, mapping separation, or shape count.");
            Require(directBinding.Shapes.All(shape =>
                    shape.XmlPath == directFixture.PhysicsXml),
                "Direct NIF binding did not use the exact locator XML path.");
            AssertPhysicsAssets(direct, directFixture);
            AssertDeterministicBindingOrder(direct, directFixture);
            AssertProviderNifMetadata(directFixture, cancellationToken);

            ExternalHeadPartRecordDependency mismatchedMember = member with
            {
                WinningPluginByteLength = Provider.PluginByteLength + 1
            };
            ExternalHeadPartPhysicsBindingResult mismatchedProvider =
                await ResolveAsync(resolver, directFixture, [mismatchedMember],
                    cancellationToken);
            Require(!mismatchedProvider.Accepted &&
                    mismatchedProvider.Diagnostics.Any(item =>
                        item.Code == "external-headpart-physics-provider"),
                "A member/provider byte-length mismatch was admitted.");

            byte[] originalPhysicsBytes = File.ReadAllBytes(
                Physical(directFixture.DataRoot, directFixture.PhysicsXml));
            byte[] driftedPhysicsBytes = CreateSameLengthMutation(
                originalPhysicsBytes);
            try
            {
                var driftReader = new ExternalHeadPartPhysicsXmlReader(
                    policy,
                    LabRoot,
                    physicalPath =>
                    {
                        if (string.Equals(
                                physicalPath,
                                Physical(directFixture.DataRoot,
                                    directFixture.PhysicsXml),
                                StringComparison.OrdinalIgnoreCase))
                            File.WriteAllBytes(physicalPath, driftedPhysicsBytes);
                    });
                ExternalHeadPartPhysicsXmlReadResult drift =
                    await driftReader.ReadPhysicsXmlAsync(
                        directFixture.DataRoot,
                        directFixture.PhysicsXml,
                        cancellationToken);
                Require(!drift.Accepted && drift.Diagnostics.Any(item =>
                        item.Code == "external-headpart-physics-asset-drift"),
                    "A same-length post-read physics XML replacement was admitted.");
            }
            finally
            {
                File.WriteAllBytes(
                    Physical(directFixture.DataRoot, directFixture.PhysicsXml),
                    originalPhysicsBytes);
            }

            Preview254ExternalSmpPhysicsFixture directSecondFixture =
                await factory.CreateAsync(
                    directFixture.ScratchRoot,
                    new FormReference(ProviderPlugin, new FormId(0x802)),
                    new AssetPath(
                        "meshes/actors/character/character assets/hair/direct-second.nif"),
                    Preview254ExternalSmpPhysicsFixtureMode.DirectLocator,
                    cancellationToken);
            ExternalHeadPartRecordDependency directSecondMember = CreateMember(
                directSecondFixture.MemberForm,
                directSecondFixture.ModelNif,
                1);
            await AssertPerModelLocatorPrecedenceAsync(
                resolver,
                directFixture,
                member,
                directSecondFixture,
                directSecondMember,
                cancellationToken);
            ExternalHeadPartPhysicsBindingResult directSameShape =
                await ResolveAsync(resolver, directFixture,
                    [member, directSecondMember], cancellationToken);
            Require(directSameShape.Accepted &&
                    directSameShape.Binding is not null &&
                    directSameShape.Binding.Shapes.Count(shape =>
                        shape.ShapeName == "HairPhysicsShape") == 2,
                "Direct binding conflated same-named shapes on distinct tuples.");

            Preview254ExternalSmpPhysicsFixture precedenceFixture =
                await factory.CreateAsync(
                    new WorkspacePath(Path.Combine(scratch, "precedence")),
                    memberForm,
                    model,
                    Preview254ExternalSmpPhysicsFixtureMode.DirectAndDefaultBbp,
                    cancellationToken);
            ExternalHeadPartPhysicsBindingResult precedence =
                await ResolveAsync(resolver, precedenceFixture, [member],
                    cancellationToken);
            Require(precedence.Accepted && precedence.Binding is not null &&
                    precedence.Binding.Mode ==
                        ExternalHeadPartPhysicsBindingMode.DirectNifExtraData &&
                    precedence.Binding.MappingAuthority is null &&
                    precedence.Binding.Shapes.All(shape =>
                        shape.XmlPath == precedenceFixture.PhysicsXml),
                "Direct NIF binding did not take precedence over default-BBP mapping.");

            Preview254ExternalSmpPhysicsFixture defaultFixture =
                await factory.CreateAsync(
                    new WorkspacePath(Path.Combine(scratch, "default")),
                    memberForm,
                    model,
                    Preview254ExternalSmpPhysicsFixtureMode.DefaultBbp,
                    cancellationToken);
            ExternalHeadPartPhysicsBindingResult fallback =
                await ResolveAsync(resolver, defaultFixture, [member],
                    cancellationToken);
            Require(fallback.Accepted && fallback.Binding is not null &&
                    fallback.Binding.Mode ==
                        ExternalHeadPartPhysicsBindingMode.DefaultBbpMap &&
                    fallback.Binding.MappingAuthority is not null &&
                    fallback.Binding.MappingAuthority.Path ==
                        defaultFixture.DefaultBbpXml &&
                    fallback.Binding.Shapes.Length == 2 &&
                    fallback.Binding.Shapes.All(shape =>
                        shape.XmlPath == defaultFixture.PhysicsXml),
                "Exact default-BBP mapping did not produce a separate binding authority.");
            AssertPhysicsAssets(fallback, defaultFixture);
            AssertDeterministicBindingOrder(fallback, defaultFixture);
            Require(!fallback.PhysicsAssets.Any(asset =>
                    asset.Path == defaultFixture.DefaultBbpXml),
                "The default-BBP mapping file was reclassified as a package asset.");

            await AssertRefusedAfterCrossXmlColliderCollisionAsync(
                resolver,
                defaultFixture,
                member,
                cancellationToken);
            await AssertExactDuplicateEvidenceConsistencyAsync(
                policy,
                defaultFixture,
                member,
                cancellationToken);

            Preview254ExternalSmpPhysicsFixture secondFixture =
                await factory.CreateAsync(
                    defaultFixture.ScratchRoot,
                    new FormReference(ProviderPlugin, new FormId(0x801)),
                    new AssetPath(
                        "meshes/actors/character/character assets/hair/second.nif"),
                    Preview254ExternalSmpPhysicsFixtureMode.DefaultBbp,
                    cancellationToken);
            ExternalHeadPartRecordDependency secondMember = CreateMember(
                secondFixture.MemberForm, secondFixture.ModelNif, 1);
            ExternalHeadPartPhysicsBindingResult sameShape =
                await ResolveAsync(resolver, defaultFixture,
                    [member, secondMember], cancellationToken);
            Require(sameShape.Accepted && sameShape.Binding is not null &&
                    sameShape.Binding.Shapes.Count(shape =>
                        shape.ShapeName == "HairPhysicsShape") == 2 &&
                    sameShape.Binding.Shapes.Select(shape =>
                            (shape.MemberForm, shape.ModelNif, shape.ShapeName))
                        .Distinct().Count() == sameShape.Binding.Shapes.Length,
                "Same-named shapes on distinct member/model tuples were conflated.");

            Preview254ExternalSmpPhysicsFixture ambiguousFixture =
                await factory.CreateAsync(
                    new WorkspacePath(Path.Combine(scratch, "ambiguous")),
                    memberForm,
                    model,
                    Preview254ExternalSmpPhysicsFixtureMode.AmbiguousDefaultBbp,
                    cancellationToken);
            await AssertRefusedAsync(resolver, ambiguousFixture, [member],
                "ambiguous default-BBP mapping", cancellationToken);

            await AssertRefusedAfterMappingMutationAsync(
                resolver,
                defaultFixture,
                [member],
                "<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"missing.xml\" /></defaultBBPs>",
                "missing mapped physics XML",
                cancellationToken);
            await AssertRefusedAfterPhysicsXmlMutationAsync(
                resolver,
                defaultFixture,
                [member],
                "<!DOCTYPE hdtSmp [<!ENTITY xxe SYSTEM \"file:///secret\">]><hdtSmp>&xxe;</hdtSmp>",
                "DTD/entity physics XML",
                cancellationToken);
            await AssertRefusedAfterMappingMutationAsync(
                resolver,
                defaultFixture,
                [member],
                "<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"C:/escape.xml\" /></defaultBBPs>",
                "rooted default-BBP mapping path",
                cancellationToken);
            await AssertRefusedAfterMappingMutationAsync(
                resolver,
                defaultFixture,
                [member],
                "<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"../escape.xml\" /></defaultBBPs>",
                "traversal default-BBP mapping path",
                cancellationToken);
            await AssertRefusedAfterPhysicsXmlMutationAsync(
                resolver,
                defaultFixture,
                [member],
                "<hdtSmp>" + new string('x', 2 * 1024 * 1024) + "</hdtSmp>",
                "oversized physics XML",
                cancellationToken);
            await AssertRefusedAfterPhysicsXmlMutationAsync(
                resolver,
                defaultFixture,
                [member],
                DeepXml(96),
                "deep physics XML",
                cancellationToken);
            await AssertRefusedAfterPhysicsXmlMutationWithDiagnosticAsync(
                resolver,
                defaultFixture,
                [member],
                "<hdtSmp><bones><bone name=\"NPC Head [Head]\" /></bones>" +
                "<colliders><collider path=\"meshes/missing-collider.nif\" /></colliders></hdtSmp>",
                "missing collider asset",
                "external-headpart-physics-asset-missing",
                cancellationToken);
            await AssertRefusedAfterPhysicsXmlMutationWithDiagnosticTextAsync(
                resolver,
                defaultFixture,
                [member],
                "<hdtSmp><bones><bone name=\"NPC Head [Head]\" /></bones>" +
                "<colliders><collider path=\"meshes/nul.nif\" /></colliders></hdtSmp>",
                "lower-case device collider path",
                "external-headpart-physics-xml-refused",
                "unsafe",
                cancellationToken);

            AssertScratchCleanupRejectsDescendantReparse();

            var sidecarResolver = new ExternalHeadPartProviderSidecarAuthorityResolver(
                policy, LabRoot);
            ExternalHeadPartProviderSidecarAuthorityResult sidecar =
                sidecarResolver.Resolve(
                    new ExternalHeadPartProviderSidecarAuthorityRequest(
                        defaultFixture.DataRoot,
                        defaultFixture.DefaultBbpXml,
                        memberForm,
                        Provider.Plugin,
                        Provider.PluginSha256));
            Require(!sidecar.Accepted && sidecar.Diagnostics.Any(item =>
                    item.Code == "external-headpart-sidecar-path"),
                "A default-BBP mapping file was admitted by the legacy sidecar resolver.");
        }
        finally
        {
            DeleteScratch(scratch);
        }
    }

    private static async Task AssertRootSpellingsAndChainInheritanceAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixtureFactory factory,
        string scratch,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        FormReference rootForm = new(ProviderPlugin, new FormId(0x900));
        AssetPath rootModel = new("meshes/hair/task7-root.nif");
        var root = CreateMember(rootForm, rootModel, 0);
        var mappingFixture = await factory.CreateAsync(
            new WorkspacePath(Path.Combine(scratch, "task7-map")), rootForm, rootModel,
            Preview254ExternalSmpPhysicsFixtureMode.DefaultBbp, cancellationToken);
        foreach (string spelling in new[] { "defaultBBPs", "default-bbps", "DEFAULTBBPS", "DEFAULT-BBPS" })
        {
            File.WriteAllText(Physical(mappingFixture.DataRoot, mappingFixture.DefaultBbpXml),
                Preview254ExternalSmpPhysicsFixtureFactory.BuildDefaultBbpXml(mappingFixture.PhysicsXml)
                    .Replace("defaultBBPs", spelling, StringComparison.Ordinal));
            var mapped = await ResolveAsync(resolver, mappingFixture, [root], cancellationToken);
            Check(mapped.Accepted && mapped.Binding?.MappingAuthority is not null,
                "Root spelling " + spelling, mapped);
        }

        var fixture = await factory.CreateAsync(
            new WorkspacePath(Path.Combine(scratch, "task7-chain")), rootForm, rootModel,
            Preview254ExternalSmpPhysicsFixtureMode.DirectLocator, cancellationToken);
        var members = ImmutableArray.CreateBuilder<ExternalHeadPartRecordDependency>();
        for (uint index = 1; index <= 3; index++)
        {
            var form = new FormReference(ProviderPlugin, new FormId(0x900 + index));
            var model = new AssetPath($"meshes/hair/task7-strand-{index}.nif");
            Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(fixture.DataRoot, model,
                Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(null, fixture.Tri.Value), LabRoot);
            members.Add(CreateMember(form, model, (int)index) with { Parent = rootForm, Depth = 1 });
        }
        root = root with { HnamEdges = members.Select(member => member.OriginForm).ToImmutableArray() };
        members.Insert(0, root);
        var inherited = await ResolveAsync(resolver, fixture, members.ToImmutable(), cancellationToken);
        Check(inherited.Accepted && inherited.Binding?.Shapes.Length == 8 &&
              inherited.Binding.MappingAuthority is null &&
              inherited.Binding.Shapes.All(shape => shape.XmlPath == fixture.PhysicsXml),
            "One locator root plus three strands without defaultBBPs", inherited);
        Check(inherited.Binding is { } inheritedBinding && inheritedBinding.Shapes.All(shape =>
                shape.MemberForm == rootForm
                    ? shape.Origin == ExternalHeadPartPhysicsBindingOrigin.Direct && shape.InheritedFromRoot is null
                    : shape.Origin == ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot &&
                      shape.InheritedFromRoot == new ExternalHeadPartPhysicsBindingRoot(rootForm, rootModel, "HairPhysicsShape")),
            "Inheritance must bind each strand to the exact direct root member/model/shape", inherited);
        var explicitInheritance = await resolver.ResolveAsync(new(fixture.DataRoot, [ProviderPlugin], Provider,
            members.ToImmutable(), ExternalHeadPartPhysicsBindingDisposition.InheritRoot), cancellationToken);
        Check(explicitInheritance.Accepted && explicitInheritance.Binding?.Shapes.SequenceEqual(inherited.Binding?.Shapes ?? []) == true,
            "Explicit inherit-root must preserve direct roots and inherited strand evidence", explicitInheritance);

        var intermediaryForm = new FormReference(ProviderPlugin, new FormId(0x910));
        var modelLess = CreateMember(intermediaryForm, rootModel, 1) with
        { ModelNif = null, Parent = rootForm, Depth = 1, HnamEdges = root.HnamEdges };
        var modelsThroughIntermediary = members.Select(member => member.OriginForm == rootForm
            ? member with { HnamEdges = [intermediaryForm] }
            : member with { Parent = intermediaryForm, Depth = 2, RouteOrder = member.RouteOrder + 1 }).ToImmutableArray();
        var fullChain = ImmutableArray.Create(modelsThroughIntermediary[0], modelLess)
            .AddRange(modelsThroughIntermediary.Skip(1));
        var mediated = await resolver.ResolveAsync(new(fixture.DataRoot, [ProviderPlugin], Provider,
            modelsThroughIntermediary, ChainMembers: fullChain), cancellationToken);
        Check(mediated.Accepted, "Admitted model-less intermediate must preserve chain inheritance", mediated);

        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(fixture.DataRoot, fixture.DefaultBbpXml,
            Encoding.UTF8.GetBytes("<malformed>"), LabRoot);
        var precedence = await ResolveAsync(resolver, fixture, members.ToImmutable(), cancellationToken);
        Check(precedence.Accepted && precedence.Binding?.MappingAuthority is null,
            "Inherited locator must precede an irrelevant malformed defaultBBPs", precedence);

        File.Delete(Physical(fixture.DataRoot, fixture.DefaultBbpXml));
        var disconnected = await ResolveAsync(resolver, fixture,
            [root with { HnamEdges = [] }, members[1] with { Parent = null, Depth = 0 }], cancellationToken);
        Check(!disconnected.Accepted, "Unrelated locator-less root must remain unbound", disconnected);
        fixture.RestoreDefaultBbp();
        var forcedUnbound = await resolver.ResolveAsync(new(fixture.DataRoot, [ProviderPlugin], Provider,
            [members[1] with { Parent = null, Depth = 0 }], ExternalHeadPartPhysicsBindingDisposition.InheritRoot), cancellationToken);
        Check(!forcedUnbound.Accepted && forcedUnbound.Diagnostics.Any(diagnostic =>
            diagnostic.Code == ExternalHeadPartDiagnosticCodes.PhysicsMissing),
            "Explicit inherit-root must not silently use an available default map", forcedUnbound);
        File.Delete(Physical(fixture.DataRoot, fixture.DefaultBbpXml));

        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(fixture.DataRoot, rootModel,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(null, fixture.Tri.Value), LabRoot);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(fixture.DataRoot, members[1].ModelNif!.Value,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(fixture.PhysicsXml.Value, fixture.Tri.Value), LabRoot);
        var childLocatorOnly = await resolver.ResolveAsync(new(fixture.DataRoot, [ProviderPlugin], Provider,
            members.ToImmutable(), ExternalHeadPartPhysicsBindingDisposition.InheritRoot), cancellationToken);
        Check(!childLocatorOnly.Accepted && childLocatorOnly.Diagnostics.Any(diagnostic =>
                diagnostic.Code == ExternalHeadPartDiagnosticCodes.PhysicsMissing),
            "A child locator must not impersonate the locator-less chain root", childLocatorOnly);

        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(fixture.DataRoot, rootModel,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(fixture.PhysicsXml.Value, fixture.Tri.Value), LabRoot);
        var alternateXml = new AssetPath("SKSE/Plugins/hdtSkinnedMeshConfigs/hair/task7-alternate.xml");
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(fixture.DataRoot, alternateXml,
            Encoding.UTF8.GetBytes(Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(fixture.ColliderNif)), LabRoot);
        Preview254ExternalSmpPhysicsFixtureFactory.WriteOrdinaryFixtureFile(fixture.DataRoot, members[1].ModelNif!.Value,
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(alternateXml.Value, fixture.Tri.Value), LabRoot);
        var distinctDirect = await ResolveAsync(resolver, fixture, members.ToImmutable(), cancellationToken);
        FormReference directChild = members[1].OriginForm;
        Check(distinctDirect.Accepted && distinctDirect.Binding is { } distinctBinding &&
              distinctBinding.Shapes.Where(shape => shape.MemberForm == rootForm).All(shape =>
                  shape.Origin == ExternalHeadPartPhysicsBindingOrigin.Direct &&
                  shape.XmlPath == fixture.PhysicsXml && shape.InheritedFromRoot is null) &&
              distinctBinding.Shapes.Where(shape => shape.MemberForm == directChild).All(shape =>
                  shape.Origin == ExternalHeadPartPhysicsBindingOrigin.Direct &&
                  shape.XmlPath == alternateXml && shape.InheritedFromRoot is null) &&
              distinctBinding.Shapes.Where(shape => shape.MemberForm != rootForm && shape.MemberForm != directChild).All(shape =>
                  shape.Origin == ExternalHeadPartPhysicsBindingOrigin.InheritedFromRoot &&
                  shape.XmlPath == fixture.PhysicsXml &&
                  shape.InheritedFromRoot == new ExternalHeadPartPhysicsBindingRoot(rootForm, rootModel, "HairPhysicsShape")),
            "A direct child locator must bind only that child while locator-less siblings inherit the real chain root", distinctDirect);
        Require(failures.Count == 0, string.Join(Environment.NewLine, failures));

        void Check(bool accepted, string label, ExternalHeadPartPhysicsBindingResult result)
        {
            if (!accepted) failures.Add(label + ": " + string.Join("; ", result.Diagnostics.Select(diagnostic =>
                diagnostic.Code + "=" + diagnostic.Message)));
        }
    }

    private static async Task<ExternalHeadPartPhysicsBindingResult> ResolveAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixture fixture,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        CancellationToken cancellationToken) =>
        await resolver.ResolveAsync(
            new ExternalHeadPartPhysicsBindingRequest(
                fixture.DataRoot,
                [Provider.Plugin],
                Provider,
                ProviderMembers: members),
            cancellationToken);

    private static async Task AssertRefusedAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixture fixture,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        string label,
        CancellationToken cancellationToken)
    {
        ExternalHeadPartPhysicsBindingResult result =
            await ResolveAsync(resolver, fixture, members, cancellationToken);
        Require(!result.Accepted && result.Binding is null,
            $"Expected {label} to be refused: " +
            string.Join("; ", result.Diagnostics.Select(item => item.Message)));
    }

    private static async Task AssertRefusedAfterMappingMutationAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixture fixture,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        string xml,
        string label,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(
            Physical(fixture.DataRoot, fixture.DefaultBbpXml),
            xml,
            new UTF8Encoding(false));
        await AssertRefusedAsync(resolver, fixture, members, label,
            cancellationToken);
        fixture.RestoreDefaultBbp();
    }

    private static async Task AssertRefusedAfterPhysicsXmlMutationAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixture fixture,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        string xml,
        string label,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(
            Physical(fixture.DataRoot, fixture.PhysicsXml),
            xml,
            new UTF8Encoding(false));
        await AssertRefusedAsync(resolver, fixture, members, label,
            cancellationToken);
        fixture.RestorePhysicsXml();
    }

    private static async Task AssertRefusedAfterPhysicsXmlMutationWithDiagnosticAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixture fixture,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        string xml,
        string label,
        string diagnosticCode,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(
            Physical(fixture.DataRoot, fixture.PhysicsXml),
            xml,
            new UTF8Encoding(false));
        try
        {
            ExternalHeadPartPhysicsBindingResult result =
                await ResolveAsync(resolver, fixture, members, cancellationToken);
            Require(!result.Accepted && result.Binding is null &&
                    result.Diagnostics.Any(item => item.Code == diagnosticCode),
                $"Expected {label} to produce {diagnosticCode}: " +
                string.Join("; ", result.Diagnostics.Select(item =>
                    item.Code + "=" + item.Message)));
        }
        finally
        {
            fixture.RestorePhysicsXml();
        }
    }

    private static async Task AssertRefusedAfterPhysicsXmlMutationWithDiagnosticTextAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixture fixture,
        ImmutableArray<ExternalHeadPartRecordDependency> members,
        string xml,
        string label,
        string diagnosticCode,
        string diagnosticText,
        CancellationToken cancellationToken)
    {
        File.WriteAllText(
            Physical(fixture.DataRoot, fixture.PhysicsXml),
            xml,
            new UTF8Encoding(false));
        try
        {
            ExternalHeadPartPhysicsBindingResult result =
                await ResolveAsync(resolver, fixture, members, cancellationToken);
            Require(!result.Accepted && result.Binding is null &&
                    result.Diagnostics.Any(item =>
                        item.Code == diagnosticCode &&
                        item.Message.Contains(diagnosticText,
                            StringComparison.OrdinalIgnoreCase)),
                $"Expected {label} to produce {diagnosticCode}/{diagnosticText}: " +
                string.Join("; ", result.Diagnostics.Select(item =>
                    item.Code + "=" + item.Message)));
        }
        finally
        {
            fixture.RestorePhysicsXml();
        }
    }

    private static async Task AssertPerModelLocatorPrecedenceAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixture fixture,
        ExternalHeadPartRecordDependency firstMember,
        Preview254ExternalSmpPhysicsFixture secondFixture,
        ExternalHeadPartRecordDependency secondMember,
        CancellationToken cancellationToken)
    {
        AssetPath secondXml = new(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/second-model.xml");
        AssetPath fallbackXml = new(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/mixed-fallback.xml");
        string secondModelPhysical = Physical(fixture.DataRoot,
            secondFixture.ModelNif);
        string secondXmlPhysical = Physical(fixture.DataRoot, secondXml);
        string fallbackXmlPhysical = Physical(fixture.DataRoot, fallbackXml);
        string mappingPhysical = Physical(fixture.DataRoot,
            secondFixture.DefaultBbpXml);
        byte[] originalModel = File.ReadAllBytes(secondModelPhysical);
        bool hadMapping = File.Exists(mappingPhysical);
        string? originalMapping = hadMapping
            ? File.ReadAllText(mappingPhysical)
            : null;
        try
        {
            File.WriteAllBytes(secondModelPhysical,
                Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                    secondXml.Value, fixture.Tri.Value));
            File.WriteAllText(secondXmlPhysical,
                Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(
                    fixture.ColliderNif),
                new UTF8Encoding(false));
            ExternalHeadPartPhysicsBindingResult distinct =
                await ResolveAsync(resolver, fixture,
                    [firstMember, secondMember], cancellationToken);
            Require(distinct.Accepted && distinct.Binding is not null &&
                    distinct.Binding.Shapes.Any(shape =>
                        shape.MemberForm == firstMember.OriginForm &&
                        shape.XmlPath == fixture.PhysicsXml) &&
                    distinct.Binding.Shapes.Any(shape =>
                        shape.MemberForm == secondMember.OriginForm &&
                        shape.XmlPath == secondXml),
                "Per-model direct physics locators were not preserved independently.");

            File.WriteAllBytes(secondModelPhysical,
                Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                    null, fixture.Tri.Value));
            File.WriteAllText(fallbackXmlPhysical,
                Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(
                    fixture.ColliderNif),
                new UTF8Encoding(false));
            File.WriteAllText(mappingPhysical,
                "<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"" +
                fallbackXml.Value + "\" /><map shape=\"HairCollisionShape\" file=\"" +
                fallbackXml.Value + "\" /></defaultBBPs>",
                new UTF8Encoding(false));
            ExternalHeadPartPhysicsBindingResult mixed =
                await ResolveAsync(resolver, fixture,
                    [firstMember, secondMember], cancellationToken);
            Require(mixed.Accepted && mixed.Binding is not null &&
                    mixed.Binding.Mode == ExternalHeadPartPhysicsBindingMode.DefaultBbpMap &&
                    mixed.Binding.MappingAuthority is not null &&
                    mixed.Binding.Shapes.Any(shape =>
                        shape.MemberForm == firstMember.OriginForm &&
                        shape.XmlPath == fixture.PhysicsXml) &&
                    mixed.Binding.Shapes.Any(shape =>
                        shape.MemberForm == secondMember.OriginForm &&
                        shape.XmlPath == fallbackXml),
                "Per-model direct precedence did not fall back only for missing-direct shapes.");
        }
        finally
        {
            File.WriteAllBytes(secondModelPhysical, originalModel);
            if (hadMapping)
                File.WriteAllText(mappingPhysical, originalMapping!,
                    new UTF8Encoding(false));
            else if (File.Exists(mappingPhysical))
                File.Delete(mappingPhysical);
            if (File.Exists(secondXmlPhysical))
                File.Delete(secondXmlPhysical);
            if (File.Exists(fallbackXmlPhysical))
                File.Delete(fallbackXmlPhysical);
        }
    }

    private static async Task AssertExactDuplicateEvidenceConsistencyAsync(
        IWorkspacePolicy policy,
        Preview254ExternalSmpPhysicsFixture fixture,
        ExternalHeadPartRecordDependency member,
        CancellationToken cancellationToken)
    {
        AssetPath alternateXml = new(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/evidence-alt.xml");
        string alternatePhysical = Physical(fixture.DataRoot, alternateXml);
        string mappingPhysical = Physical(fixture.DataRoot,
            fixture.DefaultBbpXml);
        string colliderPhysical = Physical(fixture.DataRoot,
            fixture.ColliderNif);
        string originalMapping = File.ReadAllText(mappingPhysical);
        byte[] originalCollider = File.ReadAllBytes(colliderPhysical);
        bool mutated = false;
        try
        {
            File.WriteAllText(alternatePhysical,
                Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(
                    fixture.ColliderNif),
                new UTF8Encoding(false));
            File.WriteAllText(mappingPhysical,
                "<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"" +
                fixture.PhysicsXml.Value + "\" /><map shape=\"HairCollisionShape\" file=\"" +
                alternateXml.Value + "\" /></defaultBBPs>",
                new UTF8Encoding(false));
            var resolver = new ExternalHeadPartPhysicsBindingResolver(
                policy,
                LabRoot,
                document =>
                {
                    if (!mutated)
                    {
                        mutated = true;
                        File.WriteAllBytes(colliderPhysical,
                            Encoding.UTF8.GetBytes("changed-collider"));
                    }
                });
            ExternalHeadPartPhysicsBindingResult result =
                await ResolveAsync(resolver, fixture, [member], cancellationToken);
            Require(!result.Accepted && result.Binding is null &&
                    result.Diagnostics.Any(item =>
                        item.Code == "external-headpart-physics-asset-evidence-drift"),
                "Exact duplicate collider paths with inconsistent evidence were admitted.");
        }
        finally
        {
            File.WriteAllText(mappingPhysical, originalMapping,
                new UTF8Encoding(false));
            File.WriteAllBytes(colliderPhysical, originalCollider);
            if (File.Exists(alternatePhysical))
                File.Delete(alternatePhysical);
        }
    }

    private static void AssertGenericProviderNifPathHardening()
    {
        ExternalHeadPartProviderNifReader reader =
            new();
        AssetPath source = new(
            "meshes/actors/character/character assets/hair/generic.nif");
        foreach ((string first, string? second, string label) in new[]
        {
            ("meshes/foo.tri", "meshes/FOO.tri", "case-colliding BODYTRI"),
            ("meshes/nul.tri", null, "lower-case device BODYTRI")
        })
        {
            byte[] bytes =
                Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                    null, first, second);
            ExternalHeadPartProviderNifReadResult result = reader.Read(
                new ExternalHeadPartProviderNifReadRequest(
                    source,
                    HashBytes(bytes),
                    ImmutableArray.CreateRange(bytes),
                    AllowPhysicsBinding: true));
            Require(!result.Accepted,
                $"Generic provider NIF admitted {label}.");
        }
    }

    private static void AssertLegacyTextureDependencyCardinality()
    {
        ExternalHeadPartProviderNifReader reader = new();
        AssetPath source = new(
            "meshes/armor/[dint999]/02 Hair/hairS/legacy.nif");
        byte[] bytes =
            Preview254ExternalSmpPhysicsFixtureFactory.BuildProviderNifForTests(
                null,
                "meshes/legacy/legacy.tri",
                null,
                [
                    "textures/legacy/repeated.dds",
                    "textures/legacy/repeated.dds",
                    "textures/legacy/third.dds"
                ]);
        ExternalHeadPartProviderNifReadResult result = reader.Read(
            new ExternalHeadPartProviderNifReadRequest(
                source,
                HashBytes(bytes),
                ImmutableArray.CreateRange(bytes)));
        Require(result.Accepted && result.Dependencies.SequenceEqual([
                    new AssetPath("textures/legacy/repeated.dds"),
                    new AssetPath("textures/legacy/repeated.dds"),
                    new AssetPath("textures/legacy/third.dds"),
                    new AssetPath("meshes/legacy/legacy.tri")
                ]),
            "Legacy Dint texture dependency cardinality/order changed: " +
            string.Join(",", result.Dependencies.Select(item => item.Value)));
    }

    private static async Task AssertParentReparseBetweenReadsAsync(
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(Path.GetTempPath(),
            "actorwright-external-smp-read-boundary-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(Path.GetTempPath(),
            "actorwright-external-smp-read-outside-" + Guid.NewGuid().ToString("N"));
        string data = Path.Combine(root, "Data");
        string relativeXml =
            "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/read-boundary.xml";
        string relativeCollider = "meshes/read-boundary.nif";
        Directory.CreateDirectory(Path.Combine(data, "SKSE",
            "Plugins/hdtSkinnedMeshConfigs/hair"));
        Directory.CreateDirectory(Path.Combine(data, "meshes"));
        File.WriteAllText(Path.Combine(data,
                relativeXml.Replace('/', Path.DirectorySeparatorChar)),
            Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(
                new AssetPath(relativeCollider)), new UTF8Encoding(false));
        File.WriteAllBytes(Path.Combine(data,
                relativeCollider.Replace('/', Path.DirectorySeparatorChar)),
            Encoding.UTF8.GetBytes("collider"));
        string parent = Path.Combine(data, "SKSE");
        string outsideTree = Path.Combine(outside, "SKSE");
        try
        {
            var reader = new ExternalHeadPartPhysicsXmlReader(
                new PermissiveWorkspacePolicy(), new WorkspacePath(root),
                physicalPath =>
                {
                    if (!physicalPath.EndsWith("read-boundary.xml",
                            StringComparison.OrdinalIgnoreCase))
                        return;
                    Directory.CreateDirectory(outside);
                    Directory.Move(parent, outsideTree);
                    Directory.CreateSymbolicLink(parent, outsideTree);
                });
            ExternalHeadPartPhysicsXmlReadResult result =
                await reader.ReadPhysicsXmlAsync(new WorkspacePath(data),
                    new AssetPath(relativeXml), cancellationToken);
            Require(!result.Accepted,
                "A parent reparse replacement between reads was admitted.");
        }
        finally
        {
            if (Directory.Exists(parent))
                Directory.Delete(parent);
            if (Directory.Exists(outsideTree))
                Directory.Delete(outsideTree, recursive: true);
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    private sealed class PermissiveWorkspacePolicy : IWorkspacePolicy
    {
        public ImmutableArray<Diagnostic> Evaluate(
            WorkspacePath workspaceRoot, WorkspacePath outputRoot) => [];

        public ImmutableArray<Diagnostic> EvaluateReadRoot(
            WorkspacePath workspaceRoot, WorkspacePath readRoot) => [];
    }

    private static async Task AssertRefusedAfterCrossXmlColliderCollisionAsync(
        ExternalHeadPartPhysicsBindingResolver resolver,
        Preview254ExternalSmpPhysicsFixture fixture,
        ExternalHeadPartRecordDependency member,
        CancellationToken cancellationToken)
    {
        AssetPath alternateXml = new(
            "SKSE/Plugins/hdtSkinnedMeshConfigs/hair/case-collision.xml");
        AssetPath firstCollider = new(
            "meshes/actors/character/character assets/hair/case-collider.nif");
        AssetPath secondCollider = new(
            "meshes/actors/character/character assets/hair/CASE-COLLIDER.nif");
        string physicsPhysical = Physical(fixture.DataRoot, fixture.PhysicsXml);
        string mappingPhysical = Physical(fixture.DataRoot, fixture.DefaultBbpXml);
        string alternatePhysical = Physical(fixture.DataRoot, alternateXml);
        string firstColliderPhysical = Physical(fixture.DataRoot, firstCollider);
        string secondColliderPhysical = Physical(fixture.DataRoot, secondCollider);
        string originalPhysics = File.ReadAllText(physicsPhysical);
        string originalMapping = File.ReadAllText(mappingPhysical);
        try
        {
            File.WriteAllText(
                physicsPhysical,
                Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(
                    firstCollider),
                new UTF8Encoding(false));
            File.WriteAllText(
                alternatePhysical,
                Preview254ExternalSmpPhysicsFixtureFactory.BuildPhysicsXml(
                    secondCollider),
                new UTF8Encoding(false));
            File.WriteAllBytes(firstColliderPhysical,
                Encoding.UTF8.GetBytes("case-collider"));
            File.WriteAllBytes(secondColliderPhysical,
                Encoding.UTF8.GetBytes("case-collider"));
            File.WriteAllText(
                mappingPhysical,
                "<defaultBBPs><map shape=\"HairPhysicsShape\" file=\"" +
                fixture.PhysicsXml.Value + "\" /><map shape=\"HairCollisionShape\" file=\"" +
                alternateXml.Value + "\" /></defaultBBPs>",
                new UTF8Encoding(false));

            ExternalHeadPartPhysicsBindingResult result =
                await ResolveAsync(resolver, fixture, [member], cancellationToken);
            Require(!result.Accepted && result.Binding is null &&
                    result.Diagnostics.Any(item =>
                        item.Code == "external-headpart-physics-asset-path-collision"),
                "Cross-XML collider path spellings were silently deduplicated.");
        }
        finally
        {
            File.WriteAllText(physicsPhysical, originalPhysics,
                new UTF8Encoding(false));
            File.WriteAllText(mappingPhysical, originalMapping,
                new UTF8Encoding(false));
            if (File.Exists(alternatePhysical))
                File.Delete(alternatePhysical);
            if (File.Exists(firstColliderPhysical))
                File.Delete(firstColliderPhysical);
            if (File.Exists(secondColliderPhysical))
                File.Delete(secondColliderPhysical);
        }
    }

    private static async Task AssertFixtureRejectsPreplantedReparseAsync(
        CancellationToken cancellationToken)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "actorwright-external-smp-fixture-" + Guid.NewGuid().ToString("N"));
        string outside = Path.Combine(
            Path.GetTempPath(),
            "actorwright-external-smp-outside-" + Guid.NewGuid().ToString("N"));
        var labRoot = new WorkspacePath(root);
        var factory = new Preview254ExternalSmpPhysicsFixtureFactory(labRoot);
        FormReference memberForm = new(ProviderPlugin, new FormId(0x8F0));
        AssetPath model = new(
            "meshes/actors/character/character assets/hair/preflight.nif");
        try
        {
            foreach (bool dataRootLink in new[] { true, false })
            {
                Directory.CreateDirectory(root);
                string scratch = Path.Combine(root, "scratch-" +
                    (dataRootLink ? "data" : "nested"));
                Directory.CreateDirectory(scratch);
                Directory.CreateDirectory(outside);
                string data = Path.Combine(scratch, "Data");
                string planted = dataRootLink
                    ? data
                    : Path.Combine(data, "meshes");
                if (!dataRootLink)
                    Directory.CreateDirectory(data);
                Directory.CreateSymbolicLink(planted, outside);
                try
                {
                    bool refused = false;
                    try
                    {
                        await factory.CreateAsync(
                            new WorkspacePath(scratch),
                            memberForm,
                            model,
                            Preview254ExternalSmpPhysicsFixtureMode.DirectLocator,
                            cancellationToken);
                    }
                    catch (Exception exception) when (exception is InvalidOperationException or
                        IOException or UnauthorizedAccessException)
                    {
                        refused = exception.Message.Contains(planted,
                            StringComparison.OrdinalIgnoreCase);
                    }
                    Require(refused && Directory.Exists(planted),
                        "A pre-planted fixture reparse path was not refused before writes.");
                    Require(!Directory.EnumerateFileSystemEntries(outside).Any(),
                        "Fixture writes escaped through a pre-planted reparse path.");
                }
                finally
                {
                    if (File.Exists(planted) || Directory.Exists(planted))
                        Directory.Delete(planted);
                    if (Directory.Exists(data))
                        Directory.Delete(data, recursive: true);
                    if (Directory.Exists(scratch))
                        Directory.Delete(scratch, recursive: true);
                    if (Directory.Exists(outside))
                        Directory.Delete(outside, recursive: true);
                }
            }

            string targetScratch = Path.Combine(root, "scratch-target");
            Directory.CreateDirectory(targetScratch);
            Directory.CreateDirectory(outside);
            string targetData = Path.Combine(targetScratch, "Data");
            string targetPath = Path.Combine(targetData,
                model.Value.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            Directory.CreateSymbolicLink(targetPath, outside);
            try
            {
                bool refused = false;
                try
                {
                    await factory.CreateAsync(
                        new WorkspacePath(targetScratch),
                        memberForm,
                        model,
                        Preview254ExternalSmpPhysicsFixtureMode.DirectLocator,
                        cancellationToken);
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                    IOException or UnauthorizedAccessException)
                {
                    refused = exception.Message.Contains(targetPath,
                        StringComparison.OrdinalIgnoreCase);
                }
                Require(refused && Directory.Exists(targetPath),
                    "A pre-planted fixture target reparse was not refused before writes.");
                Require(!Directory.EnumerateFileSystemEntries(outside).Any(),
                    "Fixture target writing escaped through a reparse target.");
            }
            finally
            {
                Directory.Delete(targetPath);
                if (Directory.Exists(targetScratch))
                    Directory.Delete(targetScratch, recursive: true);
                if (Directory.Exists(outside))
                    Directory.Delete(outside, recursive: true);
            }

            string restoreScratch = Path.Combine(root, "scratch-restore");
            Preview254ExternalSmpPhysicsFixture restoreFixture =
                await factory.CreateAsync(
                    new WorkspacePath(restoreScratch),
                    memberForm,
                    model,
                    Preview254ExternalSmpPhysicsFixtureMode.DirectLocator,
                    cancellationToken);
            string restoreTarget = Physical(restoreFixture.DataRoot,
                restoreFixture.PhysicsXml);
            Directory.CreateDirectory(outside);
            File.Delete(restoreTarget);
            Directory.CreateSymbolicLink(restoreTarget, outside);
            try
            {
                bool refused = false;
                try
                {
                    restoreFixture.RestorePhysicsXml();
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                    IOException or UnauthorizedAccessException)
                {
                    refused = exception.Message.Contains(restoreTarget,
                        StringComparison.OrdinalIgnoreCase);
                }
                Require(refused && Directory.Exists(restoreTarget) &&
                        !Directory.EnumerateFileSystemEntries(outside).Any(),
                    "Physics XML restore did not refuse a reparse target safely.");
            }
            finally
            {
                Directory.Delete(restoreTarget);
                if (Directory.Exists(restoreScratch))
                    Directory.Delete(restoreScratch, recursive: true);
                if (Directory.Exists(outside))
                    Directory.Delete(outside, recursive: true);
            }

            string parentRestoreScratch = Path.Combine(root,
                "scratch-restore-parent");
            Preview254ExternalSmpPhysicsFixture parentRestoreFixture =
                await factory.CreateAsync(
                    new WorkspacePath(parentRestoreScratch),
                    memberForm,
                    model,
                    Preview254ExternalSmpPhysicsFixtureMode.DirectLocator,
                    cancellationToken);
            string movedScratch = Path.Combine(outside, "moved-scratch");
            Directory.CreateDirectory(outside);
            string movedPhysics = Path.Combine(movedScratch, "Data",
                parentRestoreFixture.PhysicsXml.Value.Replace('/',
                    Path.DirectorySeparatorChar));
            string originalMovedPhysics = File.ReadAllText(
                Path.Combine(parentRestoreScratch, "Data",
                    parentRestoreFixture.PhysicsXml.Value.Replace('/',
                        Path.DirectorySeparatorChar)));
            Directory.Move(parentRestoreScratch, movedScratch);
            Directory.CreateSymbolicLink(parentRestoreScratch, movedScratch);
            try
            {
                bool refused = false;
                try
                {
                    parentRestoreFixture.RestorePhysicsXml();
                }
                catch (Exception exception) when (exception is InvalidOperationException or
                    IOException or UnauthorizedAccessException)
                {
                    refused = exception.Message.Contains(parentRestoreScratch,
                        StringComparison.OrdinalIgnoreCase);
                }
                Require(refused && File.ReadAllText(movedPhysics) ==
                        originalMovedPhysics,
                    "Fixture restore did not revalidate the scratch ancestry.");
            }
            finally
            {
                Directory.Delete(parentRestoreScratch);
                Directory.Delete(movedScratch, recursive: true);
                if (Directory.Exists(outside))
                    Directory.Delete(outside, recursive: true);
            }
        }
        finally
        {
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
            if (Directory.Exists(outside))
                Directory.Delete(outside, recursive: true);
        }
    }

    private static void AssertPhysicsAssets(
        ExternalHeadPartPhysicsBindingResult result,
        Preview254ExternalSmpPhysicsFixture fixture)
    {
        AssetPath[] expectedPaths = fixture.ReopenedAssets
            .Where(asset => asset.Path == fixture.PhysicsXml ||
                            asset.Path == fixture.ColliderNif)
            .Select(asset => asset.Path)
            .ToArray();
        Require(result.PhysicsAssets.Select(asset => asset.Path)
                    .SequenceEqual(expectedPaths),
            "Physics asset evidence was not emitted in the exact reopened order.");
        foreach (ExternalHeadPartAssetDependency asset in result.PhysicsAssets)
        {
            ExternalHeadPartAssetDependency expected = fixture.ReopenedAssets
                .Single(candidate => candidate.Path == asset.Path);
            Require(asset.Sha256 == expected.Sha256 &&
                    asset.ByteLength == expected.ByteLength,
                $"Physics asset authority drifted for '{asset.Path.Value}'.");
        }
        Require(!result.PhysicsAssets.Any(asset =>
                    asset.Path == fixture.ModelNif ||
                    asset.Path == fixture.Tri ||
                    asset.Path == fixture.DiffuseTexture),
            "Physics asset evidence was not separate from provider/model assets.");
    }

    private static void AssertDeterministicBindingOrder(
        ExternalHeadPartPhysicsBindingResult result,
        Preview254ExternalSmpPhysicsFixture fixture)
    {
        ExternalHeadPartPhysicsBinding binding = result.Binding ??
            throw new InvalidOperationException(
                "Expected a physics binding for order checks.");
        ExternalHeadPartPhysicsShapeBinding[] expectedShapes = binding.Shapes
            .OrderBy(shape => shape.MemberForm.ToString(), StringComparer.Ordinal)
            .ThenBy(shape => shape.ModelNif.Value, StringComparer.Ordinal)
            .ThenBy(shape => shape.ShapeName, StringComparer.Ordinal)
            .ToArray();
        Require(binding.Shapes.SequenceEqual(expectedShapes),
            "Physics shape bindings were not emitted in deterministic order.");

        if (binding.MappingAuthority is { } mapping)
        {
            ExternalHeadPartAssetDependency expected = fixture.ReopenedAssets
                .Single(asset => asset.Path == fixture.DefaultBbpXml);
            Require(mapping.Path == expected.Path &&
                    mapping.Sha256 == expected.Sha256 &&
                    mapping.ByteLength == expected.ByteLength,
                "Default-BBP mapping authority did not match reopened evidence.");
        }
    }

    private static byte[] CreateSameLengthMutation(byte[] bytes)
    {
        byte[] mutated = bytes.ToArray();
        int index = Array.IndexOf(mutated, (byte)'N');
        Require(index >= 0, "Fixture physics XML did not contain the mutation anchor.");
        mutated[index] = (byte)'X';
        return mutated;
    }

    private static void AssertProviderNifMetadata(
        Preview254ExternalSmpPhysicsFixture fixture,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        byte[] bytes = File.ReadAllBytes(Physical(fixture.DataRoot, fixture.ModelNif));
        ExternalHeadPartProviderNifReadResult result =
            new ExternalHeadPartProviderNifReader().Read(
                new ExternalHeadPartProviderNifReadRequest(
                    fixture.ModelNif,
                    HashBytes(bytes),
                    ImmutableArray.CreateRange(bytes),
                    AllowPhysicsBinding: true));
        Require(result.Accepted &&
                result.ProviderSidecars.IsEmpty &&
                result.ShapeNames.SequenceEqual(["HairPhysicsShape", "HairCollisionShape"]) &&
                result.PhysicsObjectLocators.SequenceEqual([fixture.PhysicsXml.Value]),
            "The provider NIF reader did not expose exact shape/locator metadata.");
    }

    private static ExternalHeadPartRecordDependency CreateMember(
        FormReference form,
        AssetPath model,
        int routeOrder) =>
        new(
            form,
            Provider.Plugin,
            form,
            Provider.Plugin,
            Provider.PluginSha256,
            Provider.PluginByteLength,
            Hash("record-" + form.FormId.Value),
            "ActorwrightHair",
            NpcHeadPartType.Hair,
            NpcHeadPartType.Hair,
            model,
            [],
            [],
            null,
            0,
            routeOrder,
            null,
            null);

    private static string Physical(WorkspacePath dataRoot, AssetPath path) =>
        Path.Combine(dataRoot.Value,
            path.Value.Replace('/', Path.DirectorySeparatorChar));

    private static string DeepXml(int depth)
    {
        var builder = new StringBuilder("<hdtSmp>");
        for (var index = 0; index < depth; index++)
            builder.Append("<nested>");
        for (var index = 0; index < depth; index++)
            builder.Append("</nested>");
        return builder.Append("</hdtSmp>").ToString();
    }

    private static void DeleteScratch(string path)
    {
        string prefix = Path.Combine(
            LabRoot.Value,
            "artifacts",
            "external-smp-physics-test-");
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !Directory.Exists(path))
            return;
        EnsureOrdinaryScratchTree(path);
        Directory.Delete(path, recursive: true);
    }

    private static void EnsureOrdinaryScratchTree(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string current = pending.Pop();
            FileAttributes attributes = File.GetAttributes(current);
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidOperationException(
                    $"The fixture scratch path '{root}' contains a reparse or device entry '{current}'.");
            if (!Directory.Exists(current))
                continue;
            foreach (string entry in Directory.EnumerateFileSystemEntries(current))
            {
                FileAttributes entryAttributes = File.GetAttributes(entry);
                if ((entryAttributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    throw new InvalidOperationException(
                        $"The fixture scratch path '{root}' contains a reparse or device entry '{entry}'.");
                if ((entryAttributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static void AssertScratchCleanupRejectsDescendantReparse()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "actorwright-external-smp-cleanup-" + Guid.NewGuid().ToString("N"));
        string target = Path.Combine(root, "cleanup-target");
        string link = Path.Combine(root, "cleanup-link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        try
        {
            Directory.CreateSymbolicLink(link, target);
            bool refused = false;
            try
            {
                EnsureOrdinaryScratchTree(root);
            }
            catch (InvalidOperationException exception)
            {
                refused = exception.Message.Contains(root,
                    StringComparison.OrdinalIgnoreCase);
            }
            Require(refused && Directory.Exists(root),
                "Scratch cleanup did not retain the exact root after descendant reparse refusal.");
        }
        finally
        {
            if (File.Exists(link) || Directory.Exists(link))
                Directory.Delete(link);
            if (Directory.Exists(target))
                Directory.Delete(target);
            if (Directory.Exists(root))
                Directory.Delete(root);
        }
    }

    private static Sha256Hash Hash(string value) =>
        HashBytes(Encoding.UTF8.GetBytes(value));

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? current = new(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "Actorwright.sln")))
                return current.FullName;
        }
        throw new InvalidOperationException("Could not locate Actorwright.sln from the test output directory.");
    }

    private static Sha256Hash HashBytes(ReadOnlySpan<byte> bytes) =>
        new(Convert.ToHexString(SHA256.HashData(bytes)));

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
